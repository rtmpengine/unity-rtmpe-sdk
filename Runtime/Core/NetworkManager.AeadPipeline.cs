// RTMPE SDK — Runtime/Core/NetworkManager.AeadPipeline.cs
//
// Outbound helpers + ClearSession + EncryptAndSend + DecryptInbound (AEAD pipeline).
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Core.Aead;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Outbound helpers ───────────────────────────────────────────────────

        private void SendHandshakeInit(string apiKey)
        {
            byte[] psk = null;
            try { psk = _settings?.ApiKeyPskBytes; }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Invalid apiKeyPskHex in settings: {ex.Message}");
                // Fall through — will send without encryption (insecure dev path).
            }

            byte[] encryptedPayload;
            if (psk != null)
            {
                var localEp = _transport.LocalEndPoint;
                if (localEp == null)
                {
                    RtmpeLog.Error("[RTMPE] SendHandshakeInit: transport not yet bound, aborting.");
                    return;
                }
                encryptedPayload = ApiKeyCipher.Encrypt(psk, apiKey, localEp);
                LogDebug($"SendHandshakeInit: API key encrypted with PSK, source={localEp}");
            }
            else
            {
                // No PSK configured.
                // UNITY_EDITOR only: allow plaintext for local loopback development.
                // All other build targets (DEVELOPMENT_BUILD, release) abort — a
                // dev build can be distributed to testers who are not on a trusted
                // LAN, so the plaintext path must not ship outside the editor.
#if UNITY_EDITOR
                Debug.LogWarning("[RTMPE] apiKeyPskHex is not configured — sending API key " +
                                 "unencrypted. This path is permitted only in the Unity Editor " +
                                 "for local loopback development. Set apiKeyPskHex in " +
                                 "NetworkSettings before creating any distributable build.");
                var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
                encryptedPayload = new byte[2 + keyBytes.Length];
                encryptedPayload[0] = (byte)(keyBytes.Length & 0xFF);
                encryptedPayload[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(keyBytes, 0, encryptedPayload, 2, keyBytes.Length);
#else
                Debug.LogError("[RTMPE] SendHandshakeInit: apiKeyPskHex MUST be configured. " +
                               "Sending the API key unencrypted is not permitted outside the " +
                               "Unity Editor — it exposes the key to any network observer. " +
                               "Aborting connection. Set apiKeyPskHex in NetworkSettings.");
                return;
#endif
            }

            // Pass the previously emitted HandshakeInit ciphertext
            // for transcript channel binding.  The gateway hashes exactly
            // these bytes (`packet.payload`) and signs the resulting digest
            // into the Challenge transcript; the client must recompute the
            // same hash on Challenge receipt.
            _lastHandshakeInitCiphertext = encryptedPayload;

            // Use SendOwned — packet is a freshly built array; the caller
            // does not retain a reference after this call.
            var packet = _packetBuilder.BuildHandshakeInit(encryptedPayload);
            SendToWire(packet);
            LogDebug($"HandshakeInit sent ({packet.Length} B).");
        }

        private void SendDisconnect()
        {
            if (_packetBuilder == null) return;
            var packet = _packetBuilder.BuildDisconnect();
            // Route through EncryptAndSend so the Disconnect packet is AEAD-encrypted
            // when a session is active, matching gateway expectations.
            //
            // UDP loss models drown the single packet ~5%; threefold redundancy
            // lifts effective delivery to >99.99%.  The encryption pass runs ONCE
            // (one nonce burn) — only the resulting ciphertext bytes hit the wire
            // three times via the kernel send queue.  Sending the same ciphertext
            // is safe: the gateway's replay window accepts the first arrival and
            // discards the duplicates by their identical nonce counter, then
            // tears the session down on the first.  Without redundancy a single
            // dropped Disconnect leaves the gateway holding the session open
            // until the heartbeat timeout (~15 s) — a wasted slot the attacker
            // (or simply a flaky link) can cheaply burn.
            EncryptAndSendRedundant(packet, copies: 3);
            LogDebug("Sent Disconnect packet.");
        }

        /// <summary>
        /// Encrypt-once, send-many helper for the Disconnect packet (and any
        /// future "must-arrive" out-of-band frame).  Captures the ciphertext
        /// produced by <see cref="EncryptAndSend"/> through a temporary
        /// redirect of the wire-send hook so the same bytes can be queued
        /// multiple times without re-running AEAD or burning extra nonces.
        /// </summary>
        private void EncryptAndSendRedundant(byte[] packet, int copies)
        {
            if (copies < 1) copies = 1;
            byte[] captured = null;
            // Temporary capture of the would-be wire bytes.  EncryptAndSend
            // funnels every send through SendToWire; we shunt that single
            // call through a captured-bytes sink, then queue the captured
            // result `copies` times via the real path.
            void Capture(byte[] b) => captured = b;
            AssertWireSendOverrideMainThread("set");
            _wireSendOverride = Capture;
            try
            {
                EncryptAndSend(packet);
            }
            finally
            {
                AssertWireSendOverrideMainThread("clear");
                _wireSendOverride = null;
            }

            if (captured == null)
            {
                // Pre-session path (no AEAD): fall back to plain send.
                for (int i = 0; i < copies; i++) SendToWire(packet);
                return;
            }

            for (int i = 0; i < copies; i++)
            {
                // Each call needs its own owned array — SendOwned keeps the
                // reference internally for the queue.  Cloning is cheaper
                // than re-running AEAD.
                var copy = new byte[captured.Length];
                Buffer.BlockCopy(captured, 0, copy, 0, captured.Length);
                SendToWire(copy);
            }
        }

        /// <summary>
        /// Optional wire-send redirect used by <see cref="EncryptAndSendRedundant"/>.
        /// When non-null, <see cref="SendToWire"/> delegates to this function
        /// INSTEAD of pushing to the network thread.  Lets the redundant-send
        /// helper capture the post-encryption byte payload without splitting
        /// <see cref="EncryptAndSend"/>.
        /// <para>
        /// <b>Threading invariant — main-thread only.</b>  The field is
        /// captured for the duration of a single AEAD seal cycle inside
        /// <see cref="EncryptAndSendRedundant"/> and cleared by its
        /// <c>finally</c> block before the call returns.  The capture/clear
        /// pair is guaranteed to run on the Unity main thread because every
        /// caller of <see cref="EncryptAndSendRedundant"/> originates from a
        /// main-thread callback (<c>Disconnect</c>, <c>Update</c>-driven
        /// heartbeat).  A future regression that triggered <see cref="SendToWire"/>
        /// from a background thread WHILE the override is non-null could
        /// race the <c>finally</c> clear and leak a captured ciphertext into
        /// an unrelated send.  <see cref="AssertWireSendOverrideMainThread"/>
        /// turns that misuse into a deterministic warning at every read/write
        /// of the field.
        /// </para>
        /// <para>Volatile because future regressions could legally introduce
        /// off-thread reads on weakly-ordered platforms (ARM64 / IL2CPP);
        /// without an acquire fence on read, a background thread could see a
        /// stale non-null delegate after the main thread cleared the field
        /// in EncryptAndSendRedundant's finally block.  The field is still
        /// expected to be mutated only on the main thread — the volatile is
        /// pure defence-in-depth, not a relaxation of that invariant.</para>
        /// </summary>
        private volatile System.Action<byte[]> _wireSendOverride;

        // Debug-only assertion that callers respect the
        // <see cref="_wireSendOverride"/> main-thread invariant.  Logs a
        // redacted warning instead of throwing because the override path is
        // a soft optimisation — a stray off-thread access should be visible
        // in dev/QA without taking the SDK down in production.
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void AssertWireSendOverrideMainThread(string op)
        {
            if (RTMPE.Threading.MainThreadDispatcher.IsMainThread) return;
            // Redacted — message intentionally carries no payload bytes,
            // ciphertext, or session identifiers.  Operators only need the
            // operation name and the thread mismatch to triage.
            Debug.LogWarning(
                $"[RTMPE] _wireSendOverride.{op} accessed off the Unity main thread " +
                "(invariant violated). This is a soft assertion — investigate the " +
                "caller; the override field is not safe to mutate from a background " +
                "thread because EncryptAndSendRedundant relies on a serial " +
                "capture / clear pair around a single AEAD seal cycle.");
        }

        /// <summary>
        /// **N-1** — emit a <c>ReconnectInit</c> packet carrying the stored
        /// reconnect token.  Payload is plaintext (no PSK encryption — the
        /// token itself IS the authentication) and does NOT go through
        /// <see cref="EncryptAndSend"/> because no session key exists yet.
        /// </summary>
        /// <param name="token">
        /// The previously-stored reconnect token.  Empty / null is treated as
        /// a programming error and aborts without sending.
        /// </param>
        private void SendReconnectInit(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                RtmpeLog.Error("[RTMPE] SendReconnectInit: token is empty — aborting reconnect.");
                return;
            }
            if (_packetBuilder == null)
            {
                RtmpeLog.Error("[RTMPE] SendReconnectInit: packet builder not initialised — aborting.");
                return;
            }

            // N-8: if we have an IP migration key, compute the HMAC-SHA256 proof
            // bound to the token so the gateway can accept a reconnect from a
            // new IP address (WiFi → 4G migration).  Without a migration key we
            // fall back to the no-proof variant — the gateway then accepts the
            // reconnect only if the source IP matches the issue-time binding.
            byte[] packet;
            bool hasProof = _ipMigrationKey != null;
            try
            {
                if (hasProof)
                {
                    var proof = RTMPE.Protocol.PacketBuilder.ComputeReconnectProof(token, _ipMigrationKey);
                    packet = _packetBuilder.BuildReconnectInit(token, proof);
                }
                else
                {
                    packet = _packetBuilder.BuildReconnectInitWithoutProof(token);
                }
            }
            catch (ArgumentException ex)
            {
                RtmpeLog.Error($"[RTMPE] SendReconnectInit: token rejected ({ex.Message}); aborting.");
                return;
            }

            SendToWire(packet);
            LogDebug($"ReconnectInit sent ({packet.Length} B, proof={hasProof}).");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private IEnumerator ConnectionTimeoutRoutine()
        {
            yield return new WaitForSeconds(_settings.connectionTimeoutMs / 1_000f);

            // N-1: the timeout applies to both the fresh-Connect path
            // (Connecting) and the reconnect path (Reconnecting).  On reconnect
            // timeout we also clear the reconnect token — the gateway has
            // already consumed it (single-use), so keeping it on the client
            // would just feed stale reconnect attempts that always fail.
            if (_state == NetworkState.Connecting || _state == NetworkState.Reconnecting)
            {
                bool wasReconnecting = _state == NetworkState.Reconnecting;
                SafeRaise(OnConnectionFailed,
                    wasReconnecting ? "Reconnect timeout." : "Connection timeout.",
                    nameof(OnConnectionFailed));

                // Symmetric teardown with DisconnectWithReason: a timeout-driven
                // shutdown must leave the manager in a state from which a
                // subsequent retry can construct fresh thread + coroutine
                // instances.  Without nulling _networkThread and stopping
                // _connectCoroutine the next attempt would call Start() on a
                // terminated thread and leak the in-flight handshake coroutine.
                //
                // Disconnect the transport explicitly.  NetworkThread.Stop()
                // only calls Disconnect when its Join times out, leaving the
                // socket bound on the normal-Stop path.  EnsureNetworkThreadReady
                // (the next retry) constructs a NEW NetworkThread that will
                // call _transport.Connect() against the already-bound socket;
                // a custom transport whose Connect is non-idempotent (or
                // raises "already bound") fails the retry silently.  Force a
                // closed-socket baseline so each retry starts identically
                // regardless of which Stop path the previous attempt took.
                _networkThread?.Stop();
                _networkThread = null;
                try { _transport?.Disconnect(); }
                catch (Exception ex)
                {
                    RtmpeLog.Warn($"[NM] Transport disconnect on timeout teardown threw: {ex.Message}");
                }
                if (_connectCoroutine != null)
                {
                    StopCoroutine(_connectCoroutine);
                    _connectCoroutine = null;
                }
                _heartbeatManager?.Stop();
                // Reconnect token semantics: the gateway treats it as single-
                // use once consumed.  Clearing here matches the historical
                // behaviour and stops a bounded retry loop from spinning on a
                // token the gateway already accepted; the next loop iteration
                // observes !CanReconnect and exits cleanly with OnReconnectFailed.
                ClearSessionData(preserveReconnectToken: false);
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }

            _timeoutCoroutine = null;
        }

        private void ClearSessionData() => ClearSessionData(preserveReconnectToken: false);

        /// <summary>
        /// Tear down the current session's crypto + application state.
        /// </summary>
        /// <param name="preserveReconnectToken">
        /// **N-1** — when <see langword="true"/>, the <see cref="_reconnectToken"/>
        /// is deliberately left intact so a subsequent <c>ReconnectInit</c> can
        /// resume the session.  All other state (JWT, crypto keys, room context,
        /// spawned objects) is still wiped — the token alone is insufficient
        /// to speak the protocol until the handshake completes.
        /// <para>
        /// Default <see langword="false"/> preserves pre-N-1 semantics: a clean
        /// disconnect clears everything.
        /// </para>
        /// </param>
        private void ClearSessionData(bool preserveReconnectToken)
        {
            _jwtToken              = null;
            if (!preserveReconnectToken)
            {
                _reconnectToken    = null;
                // N-8: ip_migration_key is only useful alongside the reconnect token.
                if (_ipMigrationKey != null)
                {
                    Array.Clear(_ipMigrationKey, 0, _ipMigrationKey.Length);
                    _ipMigrationKey = null;
                }
                if (_sessionAckKey != null)
                {
                    Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
                    _sessionAckKey = null;
                }
                // Last-room snapshot is a companion to the reconnect token —
                // both lose meaning once the token is cleared.  An explicit
                // Disconnect() therefore also wipes the snapshot so a later
                // Connect(apiKey) starts without dangling rejoin state.
                _lastRoomId   = null;
                _lastRoomCode = null;
            }
            // NOTE: when preserveReconnectToken is true we intentionally leave
            // _lastRoomId / _lastRoomCode intact so Reconnect() can feed them
            // back into RoomManager.JoinRoom after SessionAck.
            _localPlayerId         = 0;
            _localPlayerStringId   = null;
            _currentRoomId         = 0;
            // Per-instance gameplay sequence is session-scoped: the gateway-
            // side ordering buffer is allocated fresh per session so a
            // non-zero starting value would only delay the receiver's
            // window-warmup without any benefit.  Outbound app sequence
            // shares the same lifetime and is reset alongside.
            System.Threading.Interlocked.Exchange(ref _outboundAppSequenceCounter, -1L);
            System.Threading.Interlocked.Exchange(
                ref _outboundGameplaySequenceCounter, 0);
            _roomManager?.ClearState();
            _spawnManager?.ClearAll();     // Destroy all spawned objects

            // Detach the static EnhancedRpcVerifier hooks installed by
            // RecreateRoomAndSpawnManagers so they cannot fire against the
            // torn-down session (and so the captured registry / session-id
            // closure is eligible for GC).
            RTMPE.Rpc.EnhancedRpcVerifier.SelfSessionIdProvider  = null;
            RTMPE.Rpc.EnhancedRpcVerifier.ObjectExistsVerifier   = null;
            RTMPE.Rpc.EnhancedRpcVerifier.IsRoomJoined           = null;
            RTMPE.Rpc.EnhancedRpcVerifier.LocalSessionIdProvider = null;
            RTMPE.Rpc.EnhancedRpcVerifier.IsRosterMemberSession  = null;
            // Revert to the static default so a subsequent session that boots
            // without RecreateRoomAndSpawnManagers (e.g. a unit-test fixture
            // poking ClearSessionData) inherits the conservative self-only
            // policy rather than a stale roster-anchored closure.
            RTMPE.Rpc.EnhancedRpcVerifier.SenderVerifier =
                RTMPE.Rpc.EnhancedRpcVerifier.DefaultSenderVerifier;
            _handshakeHandler?.Dispose();  // Zero key material before GC can observe it
            _handshakeHandler = null;
            // Reset every per-session AEAD field as a single bundled
            // operation so the all-valid-or-all-reset invariant declared
            // by SessionKeyStore is enforced from one reviewable site.
            // Disposes session keys first (zeroing material before GC can
            // observe it), then collapses the remaining state in lockstep.
            _sessionKeyStore.ResetAllForSession();

            // Drain pending payloads on session boundary so reconnect does
            // not flush stale data into the new session's sequence/nonce
            // stream.  _pendingLiveRpcs may hold up to MaxPendingLiveRpcs-
            // DuringReplay (4096) Enhanced RPC byte[] from the previous room;
            // _batchPending may hold queued variable-update payloads built
            // against the previous PacketBuilder counter.  Either set,
            // dispatched after reconnect, would either re-apply stale state
            // or violate the gateway's monotonic sequence/nonce contract.
            // RpcReplayBuffer.Clear drops the pending queue, resets the byte
            // counter, and clears the CAS guard so the new session starts idle.
            _rpcReplayBuffer.Clear();

            // Drain the static RequestIdAllocator pending map at session
            // boundary.  Without this, OnTimeout closures captured during the
            // previous session continue to live in the global static across
            // reconnect / domain reload — PurgeExpired would later fire them
            // against torn-down NetworkManager state, and a delayed forged
            // reply on a previously-allocated request_id would still
            // correlate against the old slot.  Synthetic-timeout invocation
            // is the cleanest contract: pending callers see "session ended"
            // signalled through the same hook they registered for.
            try { Rpc.RequestIdAllocator.DropPending(); }
            catch (Exception ex)
            {
                RtmpeLog.Warn($"[NM] RequestIdAllocator.DropPending threw on session boundary: {ex.Message}");
            }
            // Drain the VariableBatchManager's pending queue so a reconnect
            // does not flush stale variable updates onto the new session's
            // nonce stream.
            _variableBatchManager?.Clear();

            // Drop the cached HandshakeInit ciphertext.  It is no
            // longer secret (it is on the wire), but a stale buffer would let
            // a future Challenge be verified against an unrelated transcript.
            _lastHandshakeInitCiphertext = null;
            LastRttMs         = -1f;
        }

        // ── AEAD outbound / inbound pipeline ──────────────────────────────────

        /// <summary>
        /// Highest application-level monotonic sequence accepted on an inbound
        /// encrypted packet that carried <c>FLAG_APP_SEQUENCE</c> on the
        /// current session.  Returns <c>-1</c> when no such packet has been
        /// received yet.  The wire <c>Sequence</c> field is the AEAD nonce
        /// counter once a session is up, so the application-level sequence
        /// would otherwise be reachable only after decrypting the payload —
        /// this property exposes it post-AEAD-verification so receivers can
        /// dedup or order without first reading the encrypted body.
        ///
        /// Updates use a monotonic CAS so the observable advances strictly
        /// forward; a reordered-but-AEAD-valid frame whose sequence is below
        /// the current high-water value cannot regress this property.
        /// Consumers can therefore treat it as a monotonic clock.
        /// </summary>
        public long LastInboundApplicationSequence =>
            _sessionKeyStore.ReadLastInboundAppSequence();

        /// <summary>
        /// Encrypts <paramref name="packet"/> with ChaCha20-Poly1305 AEAD and enqueues it
        /// for transmission on the network thread.
        ///
       /// <para>If session keys are not yet established (pre-handshake, e.g.
        /// <c>HandshakeInit</c>) the packet is sent as-is — the gateway expects those
        /// to arrive in plaintext.</para>
        ///
       /// <para>When session keys are present the following transformations are applied,
        /// mirroring Rust gateway <c>encrypt_outbound()</c> in
        /// <c>modules/gateway/src/crypto/pipeline.rs</c>:</para>
        /// <list type="number">
        ///  <item>The original application <c>header.sequence</c> is saved and prepended
        ///        as a 4-byte LE prefix to the plaintext before sealing.</item>
        ///  <item>AAD = <c>[packet_type, flags]</c> where <c>flags</c> does <b>not</b>
        ///        yet include <c>FLAG_ENCRYPTED</c>.</item>
        ///  <item>A 12-byte nonce is built by <see cref="AeadNonce.Build"/>:
        ///        <c>[counter:4 LE u32][zeros:4][cryptoId:4 LE u32]</c>.  This
        ///        is the wire encoding of the gateway's
        ///        <c>[counter:8 LE u64][cryptoId:4 LE u32]</c> layout — the
        ///        SDK's outbound counter is a <see cref="uint"/>, so the high
        ///        four bytes of the LE-u64 representation are always zero
        ///        (and never written) but the byte positions remain
        ///        identical.  The outbound counter is atomically incremented
        ///        from <c>_sessionKeyStore.IncrementOutboundNonceCounter()</c>.</item>
        ///  <item><c>header.sequence</c> is overwritten with the nonce counter (lower
        ///        32 bits), <c>FLAG_ENCRYPTED</c> is set, and <c>payload_len</c> is
        ///        updated to reflect the enlarged ciphertext.</item>
        /// </list>
        /// </summary>
        private void EncryptAndSend(byte[] packet)
        {
            if (packet == null || packet.Length < PacketProtocol.HEADER_SIZE)
                return;

            // Pre-session: HandshakeInit and HandshakeResponse travel in plaintext.
            if (!_sessionKeyStore.IsReady)
            {
                SendToWire(packet);
                return;
            }

            // ── 1. Claim next nonce counter ──────────────────────────────────────
            // The store's outbound counter starts at -1L; first call returns 0,
            // matching the Rust NonceGenerator which also starts at 0.
            long rawCounter = _sessionKeyStore.IncrementOutboundNonceCounter();

            // Hard stop: counter reached 2^32 — the gateway's NonceGenerator
            // exhausts at the same threshold (SEQUENCE_EXHAUSTION_THRESHOLD).
            // Beyond this point every packet would reuse a nonce already in the
            // gateway's replay-protection window, guaranteeing rejection.
            // Disconnect immediately so the app can re-establish a fresh session.
            if (rawCounter >= OutboundNonceExhaustionThreshold)
            {
                Debug.LogError("[RTMPE] Outbound nonce counter exhausted after 2^32 packets. " +
                               "Session must be re-established with fresh session keys.");
                DisconnectWithReason(DisconnectReason.NonceExhausted);
                return;
            }

            // Advisory: warn when fewer than ~1 M nonces remain (~9.7 h @ 30 Hz).
            // Gives the application time to schedule a graceful reconnect before the
            // hard stop fires. Mirrors the gateway's is_near_exhaustion() check.
            if (rawCounter >= OutboundNonceExhaustionThreshold - OutboundNonceNearExhaustionMargin)
                Debug.LogWarning(
                    $"[RTMPE] Outbound nonce counter near exhaustion — " +
                    $"{OutboundNonceExhaustionThreshold - rawCounter:N0} packets remaining. " +
                    "Schedule a session re-establishment soon.");

            uint nonceCounter = (uint)rawCounter;

            // ── 2. Read original sequence and payload from header ────────────────
            uint origSeq = (uint)(
                  packet[PacketProtocol.OFFSET_SEQUENCE]
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 1] << 8)
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 2] << 16)
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 3] << 24));

            uint payloadLen = (uint)(
                  packet[PacketProtocol.OFFSET_PAYLOAD_LEN]
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] << 8)
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] << 16)
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] << 24));

            // Bound payload_len before any allocation.  The wire field is a
            // 32-bit LE unsigned integer; a malformed or corrupted producer
            // path could write a value that, cast to int, becomes negative
            // (causing OverflowException in `new byte[(int)payloadLen]`) or
            // demands a multi-gigabyte buffer.  Reject anything beyond the
            // protocol cap (1 MiB) and anything that does not match the
            // physical packet length so the cast is provably safe.
            int maxPayload = RTMPE.Protocol.PacketBuilder.MaxPayloadBytes;
            if (payloadLen > (uint)maxPayload
                || payloadLen > (uint)(packet.Length - PacketProtocol.HEADER_SIZE))
            {
                Debug.LogError(
                    $"[RTMPE] EncryptAndSend rejected packet: payload_len {payloadLen} " +
                    $"exceeds protocol cap ({maxPayload}) or physical packet length " +
                    $"({packet.Length - PacketProtocol.HEADER_SIZE}). Possible buffer " +
                    "corruption or malformed builder output.");
                return;
            }
            int payloadLenInt = checked((int)payloadLen);

            byte packetType = packet[PacketProtocol.OFFSET_TYPE];
            byte flags      = packet[PacketProtocol.OFFSET_FLAGS];

            // ── 3. Build payload bytes, compressing if beneficial ────────────────
            // Compression happens before AEAD sealing so the tag covers the
            // compressed form.  FLAG_COMPRESSED is set in both the plaintext
            // prefix (restored after decryption) and the AAD so the gateway
            // can verify it didn't change in transit.
            byte[] rawPayload = null;
            if (payloadLenInt > 0)
            {
                rawPayload = new byte[payloadLenInt];
                Buffer.BlockCopy(packet, PacketProtocol.HEADER_SIZE,
                                 rawPayload, 0, payloadLenInt);
            }

            byte[] effectivePayload = rawPayload ?? Array.Empty<byte>();
            if (rawPayload != null)
            {
                var candidate = Lz4Compressor.CompressIfBeneficial(rawPayload, out bool didCompress);
                if (didCompress)
                {
                    effectivePayload = candidate;
                    flags |= (byte)PacketFlags.Compressed;
                }
            }

            // ── 4. Build plaintext = [orig_seq:4 LE] || effectivePayload ────────
            int ptLen = 4 + effectivePayload.Length;
            var plaintext = new byte[ptLen];
            plaintext[0] = (byte) origSeq;
            plaintext[1] = (byte)(origSeq >>  8);
            plaintext[2] = (byte)(origSeq >> 16);
            plaintext[3] = (byte)(origSeq >> 24);
            if (effectivePayload.Length > 0)
                Buffer.BlockCopy(effectivePayload, 0, plaintext, 4, effectivePayload.Length);

            // ── 5. Build AAD = [packet_type, flags_without_encrypted] ────────────
            // flags now includes FLAG_COMPRESSED if compression was applied.
            // flags does NOT yet include FLAG_ENCRYPTED — this must match exactly
            // what the gateway sees as AAD on its decrypt_inbound() path.
            //
            // When NetworkSettings.preserveApplicationSequence is on, the AAD
            // additionally binds a 4-byte LE u32 application sequence and the
            // wire flags carry FLAG_APP_SEQUENCE.  The application sequence is
            // tamper-evident through the AEAD tag — an on-path attacker who
            // rewrites the wire bytes to a different sequence value will fail
            // Poly1305 verification on the receiver because the AAD differs
            // from what was sealed.
            uint appSeqForWire = 0u;
            bool stampAppSeq = _settings != null
                            && _settings.preserveApplicationSequence;
            if (stampAppSeq)
            {
                appSeqForWire = (uint)System.Threading.Interlocked.Increment(
                    ref _outboundAppSequenceCounter);
                flags |= (byte)PacketFlags.AppSequence;
            }
            byte[] aad = stampAppSeq
                ? new byte[]
                  {
                      packetType,
                      flags,
                      (byte) appSeqForWire,
                      (byte)(appSeqForWire >>  8),
                      (byte)(appSeqForWire >> 16),
                      (byte)(appSeqForWire >> 24),
                  }
                : new byte[] { packetType, flags };

            // ── 6. Build 12-byte nonce = [counter:8 LE][crypto_id:4 LE] ─────────
            // The SDK's outbound counter is a uint; the high four bytes of
            // the LE-u64 counter region are therefore always zero on the
            // wire.  See AeadNonce.Build for the byte-level layout.
            var nonce = AeadNonce.Build(nonceCounter, _sessionKeyStore.CryptoId);
            // Wire-format invariant: every AEAD frame is sealed with a
            // 12-byte nonce.  A regression in AeadNonce.Build would produce
            // a Poly1305 mismatch on the gateway and a hard-to-trace drop
            // storm; assert here so the bug surfaces at the offending call.
            if (nonce == null || nonce.Length != 12)
                throw new InvalidOperationException(
                    "AeadNonce.Build returned a non-12-byte nonce — wire format invariant violated.");

            // ── 7. Seal (ChaCha20-Poly1305) ──────────────────────────────────────
            var ciphertext = ChaCha20Poly1305Impl.Seal(
                _sessionKeyStore.SessionKeys.EncryptKey, nonce, plaintext, aad);
            // ciphertext.Length == ptLen + 16  (Poly1305 tag appended)

            // ── 8. Assemble the encrypted packet ────────────────────────────────
            // Wire layout (sub-headers appear in this fixed order before the
            // ciphertext, each gated by its corresponding flag bit):
            //   [header(13)]
            //   [arq_seq(4 LE)        if FLAG_RELIABLE        and EmitArqSequence]
            //   [app_seq(4 LE)        if FLAG_APP_SEQUENCE]
            //   [gameplay_seq(4 LE)   if FLAG_GAMEPLAY_ORDERED and EmitGameplaySequencePrefix]
            //   [ciphertext]
            //
            // The 4-byte app sequence is on the wire so the receiver can read
            // it without first decrypting; the AAD binds those same bytes so
            // any tampering causes Poly1305 verification to fail and the
            // packet is silently dropped.
            //
            // arq_seq and gameplay_seq are NOT bound into the AAD on the
            // gateway side (see `build_aad` in modules/gateway/src/crypto/pipeline.rs),
            // so they pass through as plaintext sub-headers; the AEAD key still
            // protects every byte of the ciphertext that follows.
            bool emitArq =
                _settings != null
                && _settings.EmitArqSequence
                && (flags & (byte)PacketFlags.Reliable) != 0;
            bool emitGameplay =
                _settings != null
                && _settings.EmitGameplaySequencePrefix
                && (flags & (byte)PacketFlags.GameplayOrdered) != 0;
            int arqWireBytes      = emitArq      ? 4 : 0;
            int appSeqWireBytes   = stampAppSeq  ? 4 : 0;
            int gameplayWireBytes = emitGameplay ? 4 : 0;
            int subHeaderBytes    = arqWireBytes + appSeqWireBytes + gameplayWireBytes;

            uint arqSeqForWire = 0u;
            if (emitArq)
            {
                // Allocate from the channel's outbound counter without
                // registering a retransmit entry — the on-the-wire emission
                // is intentionally decoupled from the retransmit table until
                // gateway-side ACK plumbing lands.  When the full ARQ loop is
                // wired, the registration will move to the caller of
                // EncryptAndSend.
                arqSeqForWire = _outboundReliableChannel.AllocateOutboundSequence();
            }
            uint gameplaySeqForWire = 0u;
            if (emitGameplay)
            {
                // Per-instance counter avoids the cross-manager interleave
                // that the previous static GameplaySequencePrefix._counter
                // exhibited when a process hosted more than one NetworkManager.
                gameplaySeqForWire = unchecked(
                    (uint)System.Threading.Interlocked.Increment(
                        ref _outboundGameplaySequenceCounter));
            }
            var result = new byte[PacketProtocol.HEADER_SIZE + subHeaderBytes + ciphertext.Length];
            // Copy header as-is first, then patch the three affected fields.
            Buffer.BlockCopy(packet, 0, result, 0, PacketProtocol.HEADER_SIZE);

            // header.sequence = nonce_counter  (gateway uses this to reconstruct nonce)
            result[PacketProtocol.OFFSET_SEQUENCE]     = (byte) nonceCounter;
            result[PacketProtocol.OFFSET_SEQUENCE + 1] = (byte)(nonceCounter >>  8);
            result[PacketProtocol.OFFSET_SEQUENCE + 2] = (byte)(nonceCounter >> 16);
            result[PacketProtocol.OFFSET_SEQUENCE + 3] = (byte)(nonceCounter >> 24);

            // header.flags |= FLAG_ENCRYPTED  (FLAG_APP_SEQUENCE was already
            // folded into `flags` above when stampAppSeq is true).
            result[PacketProtocol.OFFSET_FLAGS] = (byte)(flags | (byte)PacketFlags.Encrypted);

            // header.payload_len counts every byte after the 13-byte header,
            // i.e. every present sub-header prefix plus the ciphertext.
            uint ctLen = (uint)(subHeaderBytes + ciphertext.Length);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte) ctLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(ctLen >>  8);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = (byte)(ctLen >> 16);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = (byte)(ctLen >> 24);

            int subOffset = PacketProtocol.HEADER_SIZE;
            if (emitArq)
            {
                result[subOffset    ] = (byte) arqSeqForWire;
                result[subOffset + 1] = (byte)(arqSeqForWire >>  8);
                result[subOffset + 2] = (byte)(arqSeqForWire >> 16);
                result[subOffset + 3] = (byte)(arqSeqForWire >> 24);
                subOffset += 4;
            }
            if (stampAppSeq)
            {
                result[subOffset    ] = (byte) appSeqForWire;
                result[subOffset + 1] = (byte)(appSeqForWire >>  8);
                result[subOffset + 2] = (byte)(appSeqForWire >> 16);
                result[subOffset + 3] = (byte)(appSeqForWire >> 24);
                subOffset += 4;
            }
            if (emitGameplay)
            {
                result[subOffset    ] = (byte) gameplaySeqForWire;
                result[subOffset + 1] = (byte)(gameplaySeqForWire >>  8);
                result[subOffset + 2] = (byte)(gameplaySeqForWire >> 16);
                result[subOffset + 3] = (byte)(gameplaySeqForWire >> 24);
                subOffset += 4;
            }
            Buffer.BlockCopy(ciphertext, 0, result, subOffset, ciphertext.Length);

            SendToWire(result);
        }

        /// <summary>
        /// Decrypts an inbound packet that has <c>FLAG_ENCRYPTED</c> set.
        ///
        /// <para>Reverses the transformations applied by the gateway's
        /// <c>encrypt_outbound()</c>:</para>
        /// <list type="number">
        ///  <item>Reconstructs the 12-byte nonce from <c>header.sequence</c> (the nonce
        ///        counter placed there by the gateway) and <c>_sessionKeyStore.CryptoId</c>.</item>
        ///  <item>AAD = <c>[packet_type, flags &amp; ~FLAG_ENCRYPTED]</c>.</item>
        ///  <item>Opens (decrypts + verifies) the ciphertext with
        ///        <c>_sessionKeyStore.SessionKeys.DecryptKey</c>.</item>
        ///  <item>Recovers the original application sequence from the first 4 bytes of
        ///        the plaintext (the SEQ prefix) and writes it back to
        ///        <c>header.sequence</c>.</item>
        ///  <item>Returns a rebuilt packet: decrypted payload, cleared
        ///        <c>FLAG_ENCRYPTED</c>, corrected <c>payload_len</c>.</item>
        /// </list>
        ///
        /// <returns>
        ///  The decrypted packet, or <see langword="null"/> on MAC failure, missing
        ///  session keys, or a malformed input — the caller must drop the packet silently.
        /// </returns>
        /// </summary>
        // <paramref name="length"/> is the meaningful byte count of
        // <paramref name="data"/>.  When <paramref name="data"/> is a rented
        // buffer from <see cref="System.Buffers.ArrayPool{T}"/>, its physical
        // <c>.Length</c> may exceed the packet length and MUST NOT be used to
        // size ciphertext extraction — that would feed garbage tail bytes
        // into Poly1305 and guarantee a tag-mismatch on every packet.
        /// <summary>
        /// Decrypt the bootstrap-encrypted SessionAck payload.
        ///
        /// <para>The gateway's <c>encrypt_session_ack()</c> seals the payload
        /// (the bytes after the 13-byte fixed header) with:</para>
        /// <list type="bullet">
        ///  <item>Key: HKDF-SHA256 expansion of the ECDH PRK with info suffix <c>\x03</c>
        ///        (<see cref="_sessionAckKey"/>).</item>
        ///  <item>Nonce: twelve zero bytes — safe because the key is single-use per session.</item>
        ///  <item>AAD: two bytes — <c>[0x08, 0x02]</c>
        ///        (<see cref="PacketType.SessionAck"/>, <see cref="PacketFlags.Encrypted"/>).</item>
        /// </list>
        /// <para>The returned byte[] mirrors <see cref="DecryptInboundPacket"/>:
        /// a freshly-allocated header + plaintext-payload buffer with
        /// <see cref="PacketFlags.Encrypted"/> stripped from the flags byte and
        /// <c>payload_len</c> updated to match.</para>
        /// </summary>
        private byte[] DecryptSessionAckPacket(byte[] data, int length)
        {
            if (_sessionAckKey == null) return null;

            // Header(13) + Poly1305 tag(16) is the minimum valid frame size.
            const int TagLen = 16;
            if (data == null || length < PacketProtocol.HEADER_SIZE + TagLen)
                return null;

            int ciphertextLen = length - PacketProtocol.HEADER_SIZE;
            var ciphertext = new byte[ciphertextLen];
            Buffer.BlockCopy(data, PacketProtocol.HEADER_SIZE, ciphertext, 0, ciphertextLen);

            // Match the gateway's SESSION_ACK_AAD constant byte-for-byte.
            byte[] aad = new byte[]
            {
                (byte)PacketType.SessionAck,
                (byte)PacketFlags.Encrypted,
            };
            byte[] nonce = new byte[12]; // all zeros — fixed bootstrap nonce
            // The all-zero bootstrap nonce is safe ONLY because the AEAD key is
            // unique per session: it is HKDF-derived from a fresh ECDH shared
            // secret negotiated during the current handshake, and the key is
            // single-use (scrubbed below after a definitive verdict).  Re-using
            // a (key, nonce) pair under ChaCha20-Poly1305 is catastrophic, so a
            // DEBUG-time guard verifies the key is not the all-zero sentinel —
            // an all-zero key would indicate that derivation never ran or
            // produced no output, rather than a fresh ECDH product.  The guard
            // compiles out of release builds so the production hot path is
            // unaffected.
#if DEBUG || UNITY_EDITOR
            {
                bool keyAllZero = true;
                for (int i = 0; i < _sessionAckKey.Length; i++)
                {
                    if (_sessionAckKey[i] != 0) { keyAllZero = false; break; }
                }
                System.Diagnostics.Debug.Assert(
                    !keyAllZero,
                    "[RTMPE] SessionAck bootstrap key is all-zero; ECDH key " +
                    "derivation is broken. The all-zero nonce is only safe " +
                    "when the per-session key is unique.");
            }
#endif

            byte[] plaintext;
            bool openThrew = false;
            try
            {
                plaintext = ChaCha20Poly1305Impl.Open(_sessionAckKey, nonce, ciphertext, aad);
            }
            catch
            {
                plaintext = null;
                openThrew = true;
            }

            // Bootstrap key lifetime invariant: the SessionAck AEAD key is a
            // one-shot secret derived during the handshake.  It MUST be wiped
            // as soon as we have a definitive verdict on the bootstrap frame —
            // success OR authentication failure.  An attacker who can flood
            // forged ciphertext at the SDK socket would otherwise hold the key
            // resident in managed memory indefinitely (each forged packet only
            // returns null on auth-fail without scrubbing), broadening the
            // window for a memory-dump attacker on the same host to recover
            // it.  Failing closed here costs at most a single legitimate
            // SessionAck retransmit (the gateway-driven handshake timeout
            // already tears the connection down on bootstrap failure), and is
            // the documented bootstrap-once contract.
            Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
            _sessionAckKey = null;

            if (openThrew || plaintext == null) return null;

            // Reassemble: header (with FLAG_ENCRYPTED stripped, payload_len
            // adjusted) followed by the decrypted plaintext.  Downstream
            // dispatch then runs as if the packet had arrived in plaintext —
            // identical to the path taken when ExpectEncryptedSessionAck=false.
            var result = new byte[PacketProtocol.HEADER_SIZE + plaintext.Length];
            Buffer.BlockCopy(data, 0, result, 0, PacketProtocol.HEADER_SIZE);
            result[PacketProtocol.OFFSET_FLAGS] =
                (byte)(result[PacketProtocol.OFFSET_FLAGS] & ~(byte)PacketFlags.Encrypted);
            uint plLen = (uint)plaintext.Length;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte) plLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(plLen >>  8);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = (byte)(plLen >> 16);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = (byte)(plLen >> 24);
            if (plaintext.Length > 0)
                Buffer.BlockCopy(plaintext, 0, result, PacketProtocol.HEADER_SIZE, plaintext.Length);
            return result;
        }

        private byte[] DecryptInboundPacket(byte[] data, int length)
        {
            if (!_sessionKeyStore.IsReady) return null;

            // Minimum valid encrypted packet:
            //  header(13) + SEQ_prefix(4) + Poly1305_tag(16) = 33 bytes.
            if (data == null || length < PacketProtocol.HEADER_SIZE + 4 + 16)
                return null;

            // ── 1. Read nonce counter from header.sequence ───────────────────────
            // The gateway wrote nonce_counter here during encryption.
            uint nonceCounter = (uint)(
                  data[PacketProtocol.OFFSET_SEQUENCE]
                | (data[PacketProtocol.OFFSET_SEQUENCE + 1] << 8)
                | (data[PacketProtocol.OFFSET_SEQUENCE + 2] << 16)
                | (data[PacketProtocol.OFFSET_SEQUENCE + 3] << 24));

            byte packetType = data[PacketProtocol.OFFSET_TYPE];
            byte flags      = data[PacketProtocol.OFFSET_FLAGS];

            // ── 2. Detect optional plaintext sub-header prefixes ─────────────────
            // The wire layout post-header (matching the gateway's encrypt_outbound):
            //   [arq_seq:4 LE]      iff FLAG_RELIABLE        (0x04)
            //   [app_seq:4 LE]      iff FLAG_APP_SEQUENCE    (0x20)
            //   [gameplay_seq:4 LE] iff FLAG_GAMEPLAY_ORDERED (0x10)
            //   [AEAD ciphertext + Poly1305 tag]
            //
            // Only app_seq is bound into the AAD (matching the gateway's
            // build_aad).  arq_seq and gameplay_seq are plaintext metadata
            // ahead of the ciphertext — read for offset, ignored for value.
            bool hasArq      = (flags & (byte)PacketFlags.Reliable)        != 0;
            bool hasAppSeq   = (flags & (byte)PacketFlags.AppSequence)     != 0;
            bool hasGameplay = (flags & (byte)PacketFlags.GameplayOrdered) != 0;

            int subHeaderBytes = (hasArq      ? 4 : 0)
                               + (hasAppSeq   ? 4 : 0)
                               + (hasGameplay ? 4 : 0);

            if (length < PacketProtocol.HEADER_SIZE + subHeaderBytes + 4 + 16)
                return null; // truncated: sub-headers + SEQ prefix + tag minimum

            uint inboundAppSeq = 0u;
            if (hasAppSeq)
            {
                int o = PacketProtocol.HEADER_SIZE + (hasArq ? 4 : 0);
                inboundAppSeq = (uint)(
                      data[o]
                    | (data[o + 1] << 8)
                    | (data[o + 2] << 16)
                    | (data[o + 3] << 24));
            }

            // ── 3. Build AAD = [packet_type, flags & ~FLAG_ENCRYPTED] (+app_seq) ─
            // Stripping FLAG_ENCRYPTED reproduces the AAD the gateway used when
            // sealing.  When FLAG_APP_SEQUENCE is set the same 4-byte
            // application sequence the sender bound is appended; an attacker
            // that rewrites either the wire bytes or the flag bit changes the
            // AAD and Poly1305 verification fails on the next line.
            byte[] aad = hasAppSeq
                ? new byte[]
                  {
                      packetType,
                      (byte)(flags & ~(byte)PacketFlags.Encrypted),
                      (byte) inboundAppSeq,
                      (byte)(inboundAppSeq >>  8),
                      (byte)(inboundAppSeq >> 16),
                      (byte)(inboundAppSeq >> 24),
                  }
                : new byte[] { packetType,
                               (byte)(flags & ~(byte)PacketFlags.Encrypted) };

            // ── 4. Build 12-byte nonce ───────────────────────────────────────────
            var nonce = AeadNonce.Build(nonceCounter, _sessionKeyStore.CryptoId);
            // Same wire-format invariant as the outbound side; see EncryptAndSend.
            if (nonce == null || nonce.Length != 12)
                throw new InvalidOperationException(
                    "AeadNonce.Build returned a non-12-byte nonce — wire format invariant violated.");

            // ── 5. Extract ciphertext (skip header + every sub-header prefix) ────
            // Use the explicit <c>length</c> argument — the rented buffer's
            // physical .Length may be larger than the meaningful packet.
            int ctLen = length - PacketProtocol.HEADER_SIZE - subHeaderBytes;
            if (ctLen < 16) return null; // ciphertext must at least carry the Poly1305 tag
            var ciphertext = new byte[ctLen];
            Buffer.BlockCopy(data,
                             PacketProtocol.HEADER_SIZE + subHeaderBytes,
                             ciphertext, 0, ctLen);

            // ── 6. Open: decrypt + verify Poly1305 tag ───────────────────────────
            var plaintext = ChaCha20Poly1305Impl.Open(
                _sessionKeyStore.SessionKeys.DecryptKey, nonce, ciphertext, aad);
            if (plaintext == null) return null; // MAC failure — drop

            // plaintext = [orig_seq:4 LE] || actual_payload (possibly LZ4-compressed)
            if (plaintext.Length < 4) return null; // should never happen

            // Anti-replay admission AFTER AEAD verification.  Performing this
            // check before Open would let an attacker burn through window
            // bits with forged ciphertext that would fail Poly1305; doing it
            // after means only authenticated counters can move the window
            // head and only authenticated duplicates are rejected.
            //
            // A null window here is a state-machine integrity violation: the
            // session-key derivation path (OnChallenge) MUST allocate the
            // window before any AEAD-bearing frame can be observed.  If we
            // got this far with _sessionKeyStore.IsReady but ReplayWindow
            // == null, we have no way to enforce replay protection on this
            // packet — reject rather than silently accept.
            var window = _sessionKeyStore.ReplayWindow;
            if (window == null)
            {
                Debug.LogWarning(
                    "[RTMPE] Dropped AEAD packet: inbound replay window is not initialised. " +
                    "Session keys exist but the window allocation was missed — refusing the " +
                    "frame to preserve replay-protection invariants.");
                return null;
            }
            if (!window.Admit(nonceCounter))
            {
                Debug.LogWarning(
                    "[RTMPE] Dropped packet: replayed or out-of-window inbound counter " +
                    $"{nonceCounter} (highest accepted is within the trailing " +
                    $"{RTMPE.Crypto.Internal.ReplayWindow.WindowSize}-entry window).");
                return null;
            }

            // Update the surfaced inbound application-sequence only for accepted
            // packets; replayed-but-AEAD-valid frames must not poison the
            // observable.  AEAD authenticates the wire bytes, but a replay
            // carries an authenticated old sequence — exposing it via
            // LastInboundApplicationSequence would let a passive replay rewind
            // any consumer that uses the property as a monotonic clock.
            //
            // Monotonic high-water mark; reordered-but-AEAD-valid frames must
            // not regress the surfaced inbound sequence.  ReplayWindow.Admit
            // dedupes via its bitmap but does not enforce strict forward
            // movement, so under UDP reorder a later-arriving lower counter
            // would otherwise clobber the high-water value.  CAS keeps the
            // observable monotonic without serialising the receive path on a
            // lock.
            if (hasAppSeq)
            {
                _sessionKeyStore.AdvanceLastInboundAppSequenceMonotonic((long)inboundAppSeq);
            }

            // ── 6. Recover original application sequence from SEQ prefix ─────────
            uint origSeq = (uint)(
                  plaintext[0]
                | (plaintext[1] << 8)
                | (plaintext[2] << 16)
                | (plaintext[3] << 24));

            // ── 7. Decompress payload if FLAG_COMPRESSED was set ─────────────────
            // Compression was authenticated via AAD, so this branch is only
            // reachable for legitimately sealed compressed packets.
            bool wasCompressed = (flags & (byte)PacketFlags.Compressed) != 0;
            byte[] finalPayload;
            int    finalPayloadLen;

            if (wasCompressed && plaintext.Length > 4)
            {
                var compressed = new byte[plaintext.Length - 4];
                Buffer.BlockCopy(plaintext, 4, compressed, 0, compressed.Length);
                try
                {
                    finalPayload    = Lz4Compressor.Decompress(compressed);
                    finalPayloadLen = finalPayload.Length;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: LZ4 decompression failed — {ex.Message}");
                    return null;
                }
            }
            else
            {
                finalPayloadLen = plaintext.Length - 4;
                finalPayload    = null; // use plaintext[4..] via BlockCopy below
            }

            // ── 8. Rebuild packet with restored header ───────────────────────────
            var result = new byte[PacketProtocol.HEADER_SIZE + finalPayloadLen];
            Buffer.BlockCopy(data, 0, result, 0, PacketProtocol.HEADER_SIZE);

            // Restore original application sequence number.
            result[PacketProtocol.OFFSET_SEQUENCE]     = (byte) origSeq;
            result[PacketProtocol.OFFSET_SEQUENCE + 1] = (byte)(origSeq >>  8);
            result[PacketProtocol.OFFSET_SEQUENCE + 2] = (byte)(origSeq >> 16);
            result[PacketProtocol.OFFSET_SEQUENCE + 3] = (byte)(origSeq >> 24);

            // Clear FLAG_ENCRYPTED, FLAG_COMPRESSED and FLAG_APP_SEQUENCE —
            // downstream handlers always receive plaintext uncompressed
            // packets, and the application-sequence bit is consumed at this
            // layer (the post-decryption representation has no extra prefix).
            result[PacketProtocol.OFFSET_FLAGS] = (byte)(flags
                & ~(byte)PacketFlags.Encrypted
                & ~(byte)PacketFlags.Compressed
                & ~(byte)PacketFlags.AppSequence);

            // Update payload_len: SEQ prefix, tag, and compression overhead removed.
            uint newPayloadLen = (uint)finalPayloadLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte) newPayloadLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(newPayloadLen >>  8);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = (byte)(newPayloadLen >> 16);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = (byte)(newPayloadLen >> 24);

            if (finalPayloadLen > 0)
            {
                if (finalPayload != null)
                    Buffer.BlockCopy(finalPayload, 0, result,
                                     PacketProtocol.HEADER_SIZE, finalPayloadLen);
                else
                    Buffer.BlockCopy(plaintext, 4, result,
                                     PacketProtocol.HEADER_SIZE, finalPayloadLen);
            }

            return result;
        }

        // BuildAeadNonce was extracted to RTMPE.Core.Aead.AeadNonce.Build —
        // it is pure (no instance state), so isolating it lets the AEAD
        // wire-format invariant be unit-tested without instantiating
        // NetworkManager or any Unity context.
    }
}
