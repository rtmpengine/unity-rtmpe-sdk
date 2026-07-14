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
using RTMPE.Core.Diagnostics;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Outbound helpers ───────────────────────────────────────────────────

        private void SendHandshakeInit(string apiKey)
        {
            // Sealed-box path.  When the gateway's static X25519 public key is
            // configured, the API key is sealed to it (anonymous sealed box) and
            // no shared PSK is involved.  This path is self-contained and returns
            // here, so the PSK/pin swap-guard below — which concerns the PSK
            // field — does not run for a sealed handshake.
            byte[] sealRecipient = null;
            try { sealRecipient = _settings?.ApiKeySealServerPublicKeyBytes; }
            catch (Exception ex)
            {
                Debug.LogError(
                    "[RTMPE] SendHandshakeInit: apiKeySealServerPublicKeyHex is not a valid " +
                    $"64-char hex X25519 public key ({ex.Message}).  Aborting connection.");
                FailHandshake(
                    "apiKeySealServerPublicKeyHex is not a valid X25519 public key.",
                    DisconnectReason.ProtocolError);
                return;
            }
            if (sealRecipient != null)
            {
                byte[] sealedPayload;
                try
                {
                    sealedPayload = SealedApiKeyCipher.Seal(sealRecipient, apiKey);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        "[RTMPE] SendHandshakeInit: sealing the API key to the configured " +
                        $"static X25519 public key failed ({ex.Message}).  Aborting connection.");
                    FailHandshake(
                        "The API key could not be sealed to apiKeySealServerPublicKeyHex.",
                        DisconnectReason.ProtocolError);
                    return;
                }
                // Transcript channel binding hashes exactly these payload bytes
                // (as the PSK path below does); the gateway hashes the same
                // sealed box, so the Challenge transcript matches regardless of
                // which envelope format produced the payload.
                _lastHandshakeInitCiphertext = sealedPayload;
                var sealedPacket = _packetBuilder.BuildHandshakeInit(sealedPayload, sealedApiKey: true);
                SendToWire(sealedPacket);
                LogDebug($"HandshakeInit sent ({sealedPacket.Length} B, sealed to static X25519 key).");
                // Witness for the timeout diagnostic: the init reached the wire, so
                // a later connect timeout resolves to the NoServerReply rung (the
                // gateway never answered) rather than the earlier "init not
                // dispatched" rung. This path returns here, ahead of the shared
                // witness on the PSK branch below, so it records the dispatch
                // itself.
                _diagHandshakeInitSent = true;
                return;
            }

            // The API-key PSK and the server pin are distinct credentials; an
            // exact match means the gateway public key was placed in the PSK
            // field.  Such a HandshakeInit is sealed under the wrong key and the
            // gateway can only discard it, so refuse up front with a precise
            // reason rather than emit an undecryptable packet that dies at the
            // connection-timeout watchdog ten seconds later.
            if (_settings != null
             && ServerKeyPinning.ApiKeyPskMatchesPinnedKey(
                    _settings.apiKeyPskHex, _settings.pinnedServerPublicKeyHex))
            {
                Debug.LogError(
                    "[RTMPE] SendHandshakeInit: apiKeyPskHex equals pinnedServerPublicKeyHex — " +
                    "the API-key PSK and the gateway public key are different credentials. " +
                    "Put the operator-supplied PSK (GATEWAY_API_KEY_ENCRYPTION_KEY_HEX) in " +
                    "apiKeyPskHex; the gateway public key belongs only in pinnedServerPublicKeyHex. " +
                    "Aborting connection.");
                FailHandshake(
                    "apiKeyPskHex equals pinnedServerPublicKeyHex — the gateway public key was " +
                    "placed in the API-key PSK field.",
                    DisconnectReason.ProtocolError);
                return;
            }

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
                // H-1: the API-key blob is sealed with an EMPTY AAD.  The previous
                // design bound it to _transport.LocalEndPoint (the client's
                // locally observed address), but a NAT'd client cannot observe
                // its post-NAT source, so the gateway — which sees the post-NAT
                // address — could never reproduce the AAD and rejected every
                // real-world (NAT'd) handshake.  Channel authentication is
                // provided independently by the Ed25519 transcript signature over
                // SHA-256(HandshakeInit ciphertext), verified on Challenge receipt.
                encryptedPayload = ApiKeyCipher.Encrypt(psk, apiKey);
                LogDebug("SendHandshakeInit: API key encrypted with PSK (empty AAD).");
            }
            else
            {
                // No PSK configured.
                // UNITY_EDITOR only: allow plaintext when the target gateway lives
                // on a loopback address.  Targeting a remote gateway from the
                // editor still aborts because the API key would otherwise traverse
                // the editor host's primary network interface — typically a
                // shared developer LAN.  All non-editor build targets
                // (DEVELOPMENT_BUILD, release) abort unconditionally; a dev build
                // can be distributed to testers who are not on a trusted LAN, so
                // the plaintext path must never ship outside the editor.
#if UNITY_EDITOR
                if (!IsLoopbackGatewayEndpoint())
                {
                    Debug.LogError("[RTMPE] SendHandshakeInit: no API-key envelope is configured AND " +
                                   "the gateway endpoint is not on loopback.  Plaintext is permitted " +
                                   "in the Unity Editor only when the gateway lives on 127.0.0.0/8 " +
                                   "or ::1 — sending the API key unencrypted to a remote host would " +
                                   "expose it to every observer on the editor's network path.  " +
                                   "Aborting connection.  Set apiKeySealServerPublicKeyHex (the " +
                                   "gateway's X25519 key from the dashboard) to seal the key, or " +
                                   "apiKeyPskHex for the legacy shared-secret path, in NetworkSettings.");
                    // Surface the cause immediately instead of letting the
                    // connection-timeout watchdog report a generic failure ten
                    // seconds later — the same fail-fast contract the
                    // Strict-pinning refusal uses, so the caller's
                    // OnConnectionFailed names the misconfiguration.
                    FailHandshake(
                        "No API-key envelope configured — set apiKeySealServerPublicKeyHex or apiKeyPskHex to reach a non-loopback gateway.",
                        DisconnectReason.ProtocolError);
                    return;
                }
                Debug.LogWarning("[RTMPE] No API-key envelope is configured — sending API key " +
                                 "unencrypted over loopback.  This path is permitted only in the " +
                                 "Unity Editor for local development.  Set apiKeySealServerPublicKeyHex " +
                                 "or apiKeyPskHex in NetworkSettings before creating any distributable build.");
                var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
                encryptedPayload = new byte[2 + keyBytes.Length];
                encryptedPayload[0] = (byte)(keyBytes.Length & 0xFF);
                encryptedPayload[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(keyBytes, 0, encryptedPayload, 2, keyBytes.Length);
#else
                Debug.LogError("[RTMPE] SendHandshakeInit: an API-key envelope MUST be configured. " +
                               "Sending the API key unencrypted is not permitted outside the " +
                               "Unity Editor — it exposes the key to any network observer. " +
                               "Aborting connection. Set apiKeySealServerPublicKeyHex or " +
                               "apiKeyPskHex in NetworkSettings.");
                // Fail fast with a specific reason (as in the editor branch above)
                // so the connection-timeout watchdog never reports this
                // misconfiguration as a generic timeout.
                FailHandshake(
                    "No API-key envelope configured — set apiKeySealServerPublicKeyHex or apiKeyPskHex (required outside the Unity Editor).",
                    DisconnectReason.ProtocolError);
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
            // Witness for the timeout diagnostic: the init reached the wire, so a
            // later timeout is past the "init dispatched" rung of the ladder.
            _diagHandshakeInitSent = true;
        }

#if UNITY_EDITOR
        // IsLoopbackGatewayEndpoint reports whether the configured gateway
        // host resolves to a loopback address.  Gates the editor-only
        // plaintext handshake path: the API key may travel unencrypted only
        // when the destination is unambiguously on the local machine and
        // therefore unreachable from any external observer.
        //
        // Recognised loopback forms:
        //   - Literal IPv4 in 127.0.0.0/8
        //   - Literal IPv6 ::1
        //   - The hostname "localhost" (case-insensitive)
        //
        // Any other shape — including unresolved hostnames and 0.0.0.0 — is
        // treated as remote and refuses the plaintext path.  Caller already
        // logs the refusal; this helper returns a plain bool and avoids
        // any DNS resolution that could block the connect path.
        private bool IsLoopbackGatewayEndpoint()
        {
            string host = _settings?.serverHost;
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (System.Net.IPAddress.TryParse(host, out var addr))
            {
                if (System.Net.IPAddress.IsLoopback(addr)) return true;
            }
            return false;
        }
#endif

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

        // Guards the outbound seal's thread-affinity invariant.  The encrypt
        // path claims a nonce counter, writes the nonce and AAD into the
        // reusable per-instance _outboundNonceScratch / _outboundAadScratch
        // buffers, then seals from them.  That claim → write → seal sequence
        // is only free of (key, nonce) reuse while it runs serially: two
        // concurrent seals would interleave their scratch writes and could
        // seal distinct plaintexts under the same nonce, collapsing
        // ChaCha20-Poly1305 confidentiality.  Every production send already
        // originates on the Unity main thread, so this is an unenforced
        // invariant rather than a live defect; the assertion turns a future
        // off-thread send (a ConfigureAwait(false) continuation, a Task.Run
        // callback) into an un-missable dev/QA error instead of silent
        // keystream reuse.  LogError — not LogWarning — because the failure it
        // catches is catastrophic, matching the nonce-exhaustion register in
        // this file.  Compiled out of release players, where the invariant
        // holds by construction.
        [System.Diagnostics.Conditional("DEBUG"), System.Diagnostics.Conditional("UNITY_EDITOR"), System.Diagnostics.Conditional("DEVELOPMENT_BUILD")]
        private static void AssertEncryptPathMainThread()
        {
            if (RTMPE.Threading.MainThreadDispatcher.IsMainThread) return;
            // Redacted — carries no payload bytes, keys, nonces, or session ids.
            Debug.LogError(
                "[RTMPE] EncryptAndSendInternal invoked off the Unity main thread " +
                "(AEAD scratch-buffer invariant violated). Concurrent seals share " +
                "the per-session nonce/AAD scratch buffers and can reuse a " +
                "(key, nonce) pair, breaking ChaCha20-Poly1305 confidentiality. " +
                "Marshal the send onto the main thread (MainThreadDispatcher.Enqueue).");
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

                // Name the exact handshake rung the attempt stalled at before the
                // teardown below clears the witnesses. Scoped to the fresh-connect
                // ladder (init -> Challenge -> SessionAck); the reconnect flow
                // climbs a different ladder these witnesses do not track, so it
                // keeps the plain "Reconnect timeout." message.
                if (!wasReconnecting)
                {
                    bool bound = _transport?.LocalEndPoint != null;
                    var stage  = ConnectionDiagnostics.Classify(
                        transportBound:     bound,
                        handshakeInitSent:  _diagHandshakeInitSent,
                        challengeReceived:  _diagChallengeReceived,
                        sessionEstablished: _sessionEstablished);

                    RtmpeLog.Warning("[NM] " + ConnectionDiagnostics.Describe(
                        transportBound:     bound,
                        handshakeInitSent:  _diagHandshakeInitSent,
                        challengeReceived:  _diagChallengeReceived,
                        sessionEstablished: _sessionEstablished,
                        elapsedMs:          _settings.connectionTimeoutMs));

                    MacOsIncomingFirewallAdvisory.NotifyIfApplicable(
                        isNoServerReply: stage == ConnectionFailureStage.NoServerReply,
                        isMacOsPlatform: Application.platform == RuntimePlatform.OSXPlayer
                                      || Application.platform == RuntimePlatform.OSXEditor);

                    string host = _settings?.serverHost ?? string.Empty;
                    LoopbackHostAdvisory.NotifyIfApplicable(
                        isLoopbackHost:    LoopbackHostAdvisory.IsLoopback(host),
                        isStandaloneBuild: !Application.isEditor);
                }

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
                    RtmpeLog.Warning($"[NM] Transport disconnect on timeout teardown threw: {ex.Message}");
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
            // Central teardown for the diagnostics uplink. Its capture hook is a
            // PROCESS-STATIC Unity event (Application.logMessageReceivedThreaded),
            // so it must be unsubscribed on EVERY session-ending path or the
            // instance leaks and keeps capturing for the life of the process.
            // Stopping it here — the one chokepoint every session-ending path
            // funnels through (incl. the gateway-initiated OnServerDisconnect,
            // which never stops the heartbeat via the usual per-site pattern) —
            // covers all of them by construction. Stop() is idempotent, so the
            // SessionAck re-create and Cleanup paths that stop it explicitly are
            // unaffected. The instance is re-created on the next SessionAck.
            _diagnosticsUplink?.Stop();

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
            // Session teardown: reset the per-session object-id space so the
            // next session starts from a clean counter.
            _spawnManager?.ClearAll(resetObjectIdSpace: true);

            // Detach the static EnhancedRpcVerifier hooks installed by
            // RecreateRoomAndSpawnManagers so they cannot fire against the
            // torn-down session and the captured registry / session-id closures
            // become GC-eligible.  Shared with the Cleanup() teardown path.
            DetachRpcVerifierHooks();
            _handshakeHandler?.Dispose();  // Zero key material before GC can observe it
            _handshakeHandler = null;
            // Drop the negotiated capability bitmask alongside the rest of
            // the per-session AEAD state.  A subsequent connect re-runs
            // the handshake and re-derives the intersection from a fresh
            // SessionAck; leaving the old value behind would let a
            // reconnect that lands against a downgraded gateway keep
            // emitting ARQ retransmits as though the prior session's caps
            // were still in force.
            _negotiatedPeerCaps = RTMPE.Core.Protocol.CapabilityFlags.None;
            // The reliable channel's retransmit table parks the plaintext of
            // each unacknowledged frame and re-seals it lazily at resend time
            // under whatever session key is then current.  An entry that
            // outlived this session would be re-encrypted under the next
            // session's key and nonce stream and resent under a stale sequence —
            // a frame the gateway can only reject on its replay window or AEAD
            // tag.  Clearing the table (and the companion inbound dedup window,
            // whose sequence space also restarts per session) on the boundary
            // confines each session's reliable traffic to that session; the
            // RTO / attempt tuning is application config and is preserved.  This
            // runs unconditionally — like the key reset below — because a
            // reconnect that preserves the token still establishes a fresh
            // session whose keys the stale entries would not match.
            _outboundReliableChannel.Reset();
            // Clear the handshake-progress witnesses so the next attempt's
            // timeout diagnostic reflects that attempt alone, not a stale rung
            // left set by a prior connect.
            _diagHandshakeInitSent = false;
            _diagChallengeReceived = false;
            // Reset every per-session AEAD field as a single bundled
            // operation so the all-valid-or-all-reset invariant declared
            // by SessionKeyStore is enforced from one reviewable site.
            // Disposes session keys first (zeroing material before GC can
            // observe it), then collapses the remaining state in lockstep.
            _sessionKeyStore.ResetAllForSession();

            // Drop queued catch-up payloads on the session boundary so
            // reconnect does not flush stale data into the new session's
            // sequence/nonce stream.  The replay buffer may hold historical
            // (buffered) Enhanced RPCs decoded from the previous room's replay
            // frame plus up to MaxPendingLiveRpcsDuringReplay (4096) deferred
            // live RPC byte[]; _batchPending may hold queued variable-update
            // payloads built against the previous PacketBuilder counter.
            // Either set, dispatched after reconnect, would either re-apply
            // stale state or violate the gateway's monotonic sequence/nonce
            // contract.  RpcReplayBuffer.Clear drops both queues, resets the
            // byte counter, and lowers the ordering barrier so the new session
            // starts idle.
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
            try { RTMPE.Rpc.RequestIdAllocator.DropPending(); }
            catch (Exception ex)
            {
                RtmpeLog.Warning($"[NM] RequestIdAllocator.DropPending threw on session boundary: {ex.Message}");
            }
            // Backstop for SendEnhancedRpcAsync awaiters: the cancellation
            // path above runs through the timeout callback wired in
            // SendEnhancedRpcAsync, but a future change that re-routes
            // registration must not silently leak awaiters.  Calling
            // DrainPendingServerRpcs here is idempotent — returns zero when
            // the timeout-callback path already cleared the dictionary, and
            // surfaces a positive count when something slipped past it.
            try { _ = DrainPendingServerRpcs(); }
            catch (Exception ex)
            {
                RtmpeLog.Warning($"[NM] DrainPendingServerRpcs threw on session boundary: {ex.Message}");
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
            // Purge queued main-thread callbacks enqueued by the background
            // receive path.  Session-bound closures (e.g. OnPacketReceived
            // continuations, buffer-return actions) captured during the
            // now-defunct session must not execute against the reconstituted
            // session's state.  The dispatcher returns any rented buffers to
            // the pool during the drain — no ArrayPool accounting leak.
            RTMPE.Threading.MainThreadDispatcher.Instance?.DiscardPendingCallbacks();
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
            => EncryptAndSendInternal(packet, hasFixedArqSeq: false, fixedArqSeq: 0u);

        /// <summary>
        /// Internal AEAD send.  The caller pins the ARQ sequence emitted in the
        /// wire sub-header via <paramref name="fixedArqSeq"/>.
        ///
        /// <para>Used both by <see cref="Send"/> for a freshly registered
        /// reliable packet and by <see cref="ReliableChannel"/>-driven
        /// retransmits.  A retransmit re-emits the same `arq_seq` the entry was
        /// registered under so the receiver's cumulative-ACK clearing — which
        /// drains every retransmit entry with `seq &lt;= ackedSeq` — recognises
        /// a late ACK for the original sequence even after a resend.</para>
        ///
        /// <para>The ARQ sub-header is emitted only when
        /// <paramref name="hasFixedArqSeq"/> is `true`, i.e. the packet is
        /// registered in the retransmit table.  When it is `false` the routine
        /// sends a single best-effort transmission and clears any FLAG_RELIABLE
        /// bit from the wire frame, so an `arq_seq` appears on the wire exactly
        /// when a retransmit entry backs it.</para>
        /// </summary>
        private void EncryptAndSendInternal(byte[] packet, bool hasFixedArqSeq, uint fixedArqSeq)
        {
            if (packet == null || packet.Length < PacketProtocol.HEADER_SIZE)
                return;

            // Pre-session: HandshakeInit and HandshakeResponse travel in plaintext.
            if (!_sessionKeyStore.IsReady)
            {
                SendToWire(packet);
                return;
            }

            // The sealing section below claims a nonce counter and writes into
            // the shared per-instance scratch buffers; it is main-thread-only.
            AssertEncryptPathMainThread();

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

            // ── 5. Decide which sub-headers we will emit on the wire ────────────
            // Compute the emission decisions up-front so the AAD construction
            // below reflects the actual wire frame.  Binding emit decisions
            // BEFORE building the AAD lets us:
            //   (a) include app_seq + gameplay_seq in the AAD when and only
            //       when they appear on the wire — the gateway's build_aad
            //       branches off the same flag bits, so the two AADs MUST
            //       match byte-for-byte;
            //   (b) clear flag bits we are NOT emitting so the on-wire flags
            //       byte matches the AAD's flags byte exactly — a flag bit
            //       set without its corresponding sub-header would yield a
            //       malformed frame the gateway rejects as truncated.
            bool stampAppSeq = _settings != null
                            && _settings.preserveApplicationSequence;
            if (stampAppSeq)
            {
                flags |= (byte)PacketFlags.AppSequence;
            }
            // ARQ sub-header emission is gated on four predicates that must
            // all hold:
            //   • the local opt-in (`NetworkSettings.EmitArqSequence`);
            //   • the gateway-advertised `CapabilityFlags.ArqAck` from the
            //     SessionAck — stamping the sub-header without the gateway
            //     acknowledging it would burn 4 bytes per packet against no
            //     downstream consumer;
            //   • the caller-set FLAG_RELIABLE bit;
            //   • a caller-assigned sequence (`hasFixedArqSeq`), i.e. the
            //     packet is registered in the ReliableChannel retransmit
            //     table.  An unregistered reliable-flagged packet has no
            //     retransmit entry, so emitting an arq_seq for it would
            //     advertise a frame the SDK cannot track and draw a DataAck
            //     that clears nothing.  Restricting emission to registered
            //     packets preserves the wire invariant "an arq_seq is
            //     present exactly when a retransmit entry backs it"; an
            //     unregistered reliable packet degrades to a single
            //     best-effort transmission below.
            // Gating every predicate at this single decision site keeps the
            // AAD construction below in exact lockstep with the on-wire frame.
            bool peerSupportsArqAckForEmit =
                (_negotiatedPeerCaps & RTMPE.Core.Protocol.CapabilityFlags.ArqAck) != 0;
            bool emitArq =
                _settings != null
                && _settings.EmitArqSequence
                && peerSupportsArqAckForEmit
                && (flags & (byte)PacketFlags.Reliable) != 0
                && hasFixedArqSeq;
            bool emitGameplay =
                _settings != null
                && _settings.EmitGameplaySequencePrefix
                && (flags & (byte)PacketFlags.GameplayOrdered) != 0;

            // Synchronize the wire flags byte with what we actually emit.
            // If a caller marked FLAG_RELIABLE / FLAG_GAMEPLAY_ORDERED but
            // the corresponding setting suppresses emission, drop the bit
            // here.  Without this, the AAD's flags byte would advertise a
            // sub-header the wire frame doesn't carry — the gateway sees a
            // truncated sub-header region and silently drops every packet.
            if (!emitArq)
            {
                flags &= unchecked((byte)~(byte)PacketFlags.Reliable);
            }
            if (!emitGameplay)
            {
                flags &= unchecked((byte)~(byte)PacketFlags.GameplayOrdered);
            }

            // ── 5a. Allocate the per-packet sub-header values now so the
            //        AAD below can bind app_seq and gameplay_seq.
            uint appSeqForWire = 0u;
            if (stampAppSeq)
            {
                appSeqForWire = (uint)System.Threading.Interlocked.Increment(
                    ref _outboundAppSequenceCounter);
            }
            uint arqSeqForWire = 0u;
            if (emitArq)
            {
                // emitArq implies hasFixedArqSeq (see the gate above), so the
                // sequence is always the value ReliableChannel assigned at
                // TryRegisterOutbound time.  A Tick-driven retransmit re-enters
                // this path with the same fixedArqSeq, so the receiver observes
                // a stable arq_seq across retries — the per-frame ack that
                // clears the matching retransmit entry relies on that stability.
                arqSeqForWire = fixedArqSeq;
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

            // ── 5b. Build AAD = [packet_type, flags] [+app_seq] [+gameplay_seq] ─
            // flags now reflects the on-the-wire flags byte exactly (minus
            // FLAG_ENCRYPTED, which the gateway's decrypt_inbound() path
            // strips before reconstructing AAD).  Both sub-headers we
            // emit are bound into the AAD: the AEAD tag therefore detects
            // any tampering with them — an on-path attacker who rewrites
            // app_seq or gameplay_seq fails Poly1305 verification on the
            // receiver and the packet is silently dropped.
            //
            // arq_seq is intentionally NOT bound — a retransmit reuses
            // the same ciphertext with a different arq_seq, so binding it
            // would force a fresh AEAD seal per retry.  Both other
            // sub-headers are stable per (encrypted payload, sequence)
            // pair and binding them is essentially free.
            //
            // The AAD is written into the per-direction reusable
            // _outboundAadScratch buffer instead of a fresh byte[] every
            // packet.  SealInto reads exactly aadLen bytes from offset 0,
            // so the slack capacity past aadLen never participates in
            // the tag.
            int aadLen = 2 + (stampAppSeq ? 4 : 0) + (emitGameplay ? 4 : 0);
            byte[] aad = _outboundAadScratch;
            aad[0] = packetType;
            aad[1] = flags;
            int aadOff = 2;
            if (stampAppSeq)
            {
                aad[aadOff    ] = (byte) appSeqForWire;
                aad[aadOff + 1] = (byte)(appSeqForWire >>  8);
                aad[aadOff + 2] = (byte)(appSeqForWire >> 16);
                aad[aadOff + 3] = (byte)(appSeqForWire >> 24);
                aadOff += 4;
            }
            if (emitGameplay)
            {
                aad[aadOff    ] = (byte) gameplaySeqForWire;
                aad[aadOff + 1] = (byte)(gameplaySeqForWire >>  8);
                aad[aadOff + 2] = (byte)(gameplaySeqForWire >> 16);
                aad[aadOff + 3] = (byte)(gameplaySeqForWire >> 24);
            }

            // ── 6. Build 12-byte nonce = [counter:8 LE][crypto_id:4 LE] ─────────
            // The SDK's outbound counter is a uint; the high four bytes of
            // the LE-u64 counter region are therefore always zero on the
            // wire.  See AeadNonce.Build for the byte-level layout.
            //
            // GC Round 3 (2026-05-02): write into the cached outbound
            // scratch buffer instead of allocating a fresh 12-byte array
            // per packet.  EncryptAndSend is called serially from the
            // Unity main thread (see file-header threading note in
            // EncryptAndSendRedundant); the buffer is fully overwritten
            // before being read by the Seal call, so a stale value from
            // the previous packet is never observable.
            AeadNonce.BuildInto(nonceCounter, _sessionKeyStore.CryptoId, _outboundNonceScratch);

            // ── 7. Compute wire-frame layout up-front so we can SealInto the
            //       final destination buffer (avoids the intermediate
            //       ciphertext byte[] that the legacy Seal call allocates).
            //
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
            // app_seq AND gameplay_seq are bound into the AAD on the
            // gateway side (see `build_aad` in modules/gateway/src/crypto/
            // pipeline.rs).  arq_seq is NOT bound — retransmits reuse the
            // ciphertext with a different arq_seq.
            int arqWireBytes      = emitArq      ? 4 : 0;
            int appSeqWireBytes   = stampAppSeq  ? 4 : 0;
            int gameplayWireBytes = emitGameplay ? 4 : 0;
            int subHeaderBytes    = arqWireBytes + appSeqWireBytes + gameplayWireBytes;

            // ciphertextLen = plaintextLen + 16-byte Poly1305 tag.
            int ciphertextLen = ptLen + 16;
            var result = new byte[PacketProtocol.HEADER_SIZE + subHeaderBytes + ciphertextLen];
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
            uint ctLen = (uint)(subHeaderBytes + ciphertextLen);
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

            // ── 8. Seal directly into the final wire packet ─────────────────────
            // SealInto writes [ciphertext || tag] = ptLen + 16 bytes into
            // result[subOffset..].  This skips the intermediate ciphertext
            // byte[] allocation that the legacy `Seal()` shim materialises and
            // then BlockCopy's into result; the wire bytes are bit-for-bit
            // identical.  ChaCha20 XORs the keystream into result, then
            // Poly1305 reads the just-written ciphertext from result to
            // compute the MAC, which it writes into result[subOffset+ptLen..].
            ChaCha20Poly1305Impl.SealInto(
                _sessionKeyStore.SessionKeys.EncryptKey,
                _outboundNonceScratch,
                plaintext, 0, plaintext.Length,
                // Pass aadLen (not aad.Length) — the scratch buffer is
                // larger than the meaningful AAD; only the prefix is bound.
                aad,       0, aadLen,
                result,    subOffset);

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
            // single-use (scrubbed below once it opens the genuine SessionAck,
            // and on session teardown).  Re-using
            // a (key, nonce) pair under ChaCha20-Poly1305 is catastrophic, so
            // we verify at runtime (in every build configuration) that the key
            // is not the all-zero sentinel.  An all-zero key would indicate
            // that derivation never ran or produced no output, rather than a
            // fresh ECDH product; in that case we fail closed by treating the
            // frame as authentication-failed (return null), which lets the
            // gateway-driven handshake timeout tear the connection down via
            // the documented bootstrap-once contract.  Failing closed costs at
            // most a single legitimate retransmit and never surrenders a
            // (key, nonce) reuse opportunity.
            {
                bool keyAllZero = true;
                for (int i = 0; i < _sessionAckKey.Length; i++)
                {
                    if (_sessionAckKey[i] != 0) { keyAllZero = false; break; }
                }
                if (keyAllZero)
                {
                    // Wipe and null the buffer so the failed-bootstrap state
                    // matches the post-decrypt invariant below: the key is
                    // never resident in managed memory after a definitive
                    // verdict, success or failure.
                    Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
                    _sessionAckKey = null;
                    return null;
                }
            }

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

            if (openThrew || plaintext == null)
            {
                // Authentication failed for THIS frame, which may be a forged
                // 0x08 injected on-path.  Consuming the one-shot key here would
                // let a single spoofed packet null it and starve the genuine
                // SessionAck until the handshake timeout — a trivial connect
                // DoS.  Preserve the key so the legitimate retransmit can still
                // be opened; it is not orphaned — it is scrubbed on the
                // successful open below and on every teardown path
                // (ClearSessionData on disconnect/timeout, Cleanup on destroy),
                // so its residency stays bounded by the handshake window.
                return null;
            }

            // Bootstrap key lifetime invariant: the SessionAck AEAD key is a
            // one-shot secret derived during the handshake.  Now that it has
            // opened the genuine SessionAck, wipe it.  Together with the teardown
            // scrubs (ClearSessionData on disconnect/timeout, Cleanup on destroy)
            // and the pre-re-derivation scrub in OnChallenge, this bounds the
            // key's residency to the handshake window on every path.
            Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
            _sessionAckKey = null;

            // Reassemble: header (with FLAG_ENCRYPTED stripped, payload_len
            // adjusted) followed by the decrypted plaintext.  Downstream
            // dispatch then runs as if the packet had arrived in plaintext —
            // identical to the path taken for an unsealed SessionAck.
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

            // The header's payload_len must agree with the datagram the
            // transport delivered.  The ciphertext below is framed from
            // `length`; a header whose declared length disagrees indicates a
            // corrupt or mis-framed packet, and is refused here before any
            // sub-header offset arithmetic consumes it.
            if (!RTMPE.Protocol.PacketParser.HeaderPayloadLengthMatchesFrame(data, length))
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

            // Decode app_seq and gameplay_seq from their wire offsets so the
            // AAD construction below can bind both.  arq_seq is intentionally
            // left out of the AAD (a retransmit reuses the same ciphertext
            // with a different arq_seq); app_seq and gameplay_seq are
            // bound because they steer application-layer ordering and a
            // tampered value would otherwise slip past Poly1305.
            int subCursor = PacketProtocol.HEADER_SIZE + (hasArq ? 4 : 0);
            uint inboundAppSeq = 0u;
            if (hasAppSeq)
            {
                inboundAppSeq = (uint)(
                      data[subCursor]
                    | (data[subCursor + 1] <<  8)
                    | (data[subCursor + 2] << 16)
                    | (data[subCursor + 3] << 24));
                subCursor += 4;
            }
            uint inboundGameplaySeq = 0u;
            if (hasGameplay)
            {
                inboundGameplaySeq = (uint)(
                      data[subCursor]
                    | (data[subCursor + 1] <<  8)
                    | (data[subCursor + 2] << 16)
                    | (data[subCursor + 3] << 24));
                // subCursor is intentionally not advanced past the
                // gameplay_seq field; no callers below this point read
                // the sub-header region again — the next consumer is the
                // ciphertext which sits at PacketProtocol.HEADER_SIZE +
                // subHeaderBytes (computed independently above).
            }

            // ── 3. Build AAD = [packet_type, flags & ~FLAG_ENCRYPTED] (+app_seq) (+gameplay_seq) ─
            // Stripping FLAG_ENCRYPTED reproduces the AAD the gateway used
            // when sealing.  When FLAG_APP_SEQUENCE / FLAG_GAMEPLAY_ORDERED
            // are set the matching 4-byte sub-header values are appended in
            // that order; an attacker that rewrites either the wire bytes
            // or the flag bit changes the AAD and Poly1305 verification
            // fails on the next line.
            //
            // The AAD is written into the per-direction reusable
            // _inboundAadScratch buffer instead of a fresh byte[] every
            // packet.  OpenInto reads exactly aadLen bytes from offset 0;
            // the slack capacity past aadLen never participates in tag
            // verification.
            int aadLen = 2 + (hasAppSeq ? 4 : 0) + (hasGameplay ? 4 : 0);
            byte[] aad = _inboundAadScratch;
            aad[0] = packetType;
            aad[1] = (byte)(flags & ~(byte)PacketFlags.Encrypted);
            int aadOff = 2;
            if (hasAppSeq)
            {
                aad[aadOff    ] = (byte) inboundAppSeq;
                aad[aadOff + 1] = (byte)(inboundAppSeq >>  8);
                aad[aadOff + 2] = (byte)(inboundAppSeq >> 16);
                aad[aadOff + 3] = (byte)(inboundAppSeq >> 24);
                aadOff += 4;
            }
            if (hasGameplay)
            {
                aad[aadOff    ] = (byte) inboundGameplaySeq;
                aad[aadOff + 1] = (byte)(inboundGameplaySeq >>  8);
                aad[aadOff + 2] = (byte)(inboundGameplaySeq >> 16);
                aad[aadOff + 3] = (byte)(inboundGameplaySeq >> 24);
            }

            // ── 4. Build 12-byte nonce ───────────────────────────────────────────
            // GC Round 3 (2026-05-02): reuse the cached inbound scratch
            // buffer.  Like the outbound counterpart, DecryptInboundPacket
            // is invoked from the main-thread receive dispatch path, so
            // sequential writes-then-reads of the scratch are race-free.
            AeadNonce.BuildInto(nonceCounter, _sessionKeyStore.CryptoId, _inboundNonceScratch);

            // ── 5. Locate ciphertext in-place (skip header + every sub-header prefix) ────
            // Use the explicit <c>length</c> argument — the rented buffer's
            // physical .Length may be larger than the meaningful packet.
            //
            // GC Round 4 (2026-05-02): instead of copying the ciphertext out
            // of <c>data</c> into a freshly-allocated byte[], we point
            // OpenInto directly at the in-place ciphertext slice.  OpenInto
            // verifies the Poly1305 tag without writing into <c>data</c>
            // (it reads ciphertext + tag from the source slice and writes
            // plaintext into a separate destination buffer); the original
            // packet bytes are therefore left untouched.
            int ctOffset = PacketProtocol.HEADER_SIZE + subHeaderBytes;
            int ctLen = length - ctOffset;
            if (ctLen < 16) return null; // ciphertext must at least carry the Poly1305 tag

            // ── 6. Open: decrypt + verify Poly1305 tag ───────────────────────────
            // Allocate plaintext at exact-fit size so the caller can use
            // .Length without a separate length parameter.  OpenInto
            // returns the plaintext byte count or -1 on MAC failure.
            int plaintextLen = ctLen - 16;
            var plaintext = new byte[plaintextLen];
            int written = ChaCha20Poly1305Impl.OpenInto(
                _sessionKeyStore.SessionKeys.DecryptKey,
                _inboundNonceScratch,
                data,      ctOffset, ctLen,
                // Pass aadLen (not aad.Length) — the scratch buffer is
                // larger than the meaningful AAD; only the prefix is bound.
                aad,       0,        aadLen,
                plaintext, 0);
            if (written < 0)
            {
                // Genuine Poly1305 tag mismatch: forged or corrupted ciphertext.
                // Reported here — the only site that knows the tag itself failed —
                // and kept distinct from the valid-tag replay rejection below so a
                // benign duplicate is never mislabelled as an authentication failure.
                if (ShouldWarn(ref _lastAeadFailWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Dropped packet: AEAD authentication failed (Poly1305 tag mismatch).");
                return null;
            }

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
                // A duplicated or reordered datagram stream — the ordinary case
                // under UDP/KCP retransmission — rejects here at line rate. Gate
                // the notice to at most one line per second so a steady stream of
                // benign duplicates cannot swamp the log; the reason and a sample
                // counter in that line convey the condition without repeating it
                // per packet. The rejection itself is unconditional.
                if (ShouldWarn(ref _lastReplayWindowDropWarnTicks))
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
