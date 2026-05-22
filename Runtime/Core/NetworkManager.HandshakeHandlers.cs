// RTMPE SDK — Runtime/Core/NetworkManager.HandshakeHandlers.cs
//
// Handshake coroutine + Challenge/SessionAck packet handlers + auto-rejoin.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Core.Protocol;
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
        // ── Handshake coroutine ────────────────────────────────────────────────

        /// <summary>
        /// Wait for the UDP transport to bind (background thread sets LocalEndPoint),
        /// then build and transmit the encrypted HandshakeInit packet.
        ///
       /// Polls LocalEndPoint each frame — the background thread calls
        /// _transport.Connect() which binds the socket and sets the endpoint
        /// within the first loop iteration (~1 ms after Start()).
        /// </summary>
        private IEnumerator HandshakeInitCoroutine(string apiKey)
        {
            const float maxWaitSecs = 2f;
            float waited = 0f;

            while (_transport.LocalEndPoint == null && waited < maxWaitSecs)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            if (_transport.LocalEndPoint == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug("Transport did not bind within 2 s — timeout coroutine will handle failure.");
                yield break;
            }

            SendHandshakeInit(apiKey);
        }

        /// <summary>
        /// **N-1** — reconnect variant of <see cref="HandshakeInitCoroutine"/>.
        /// Waits for the UDP transport to bind, then sends a single
        /// <c>ReconnectInit</c> carrying the stored reconnect token.
        /// </summary>
        /// <remarks>
        /// The server's response is a normal <see cref="PacketType.Challenge"/>,
        /// handled by the same pipeline as the full handshake.  No extra
        /// client-side state machine is required — the Reconnecting state just
        /// marks the intent for observers.
        /// </remarks>
        private IEnumerator ReconnectInitCoroutine()
        {
            const float maxWaitSecs = 2f;
            float waited = 0f;

            while (_transport.LocalEndPoint == null && waited < maxWaitSecs)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            if (_transport.LocalEndPoint == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug("ReconnectInit: transport did not bind within 2 s — timeout coroutine will handle failure.");
                yield break;
            }

            SendReconnectInit(_reconnectToken);
        }

        // ── Receive path (raised on network thread → marshalled to main thread) ─

        // Pre-resolved delegate for the per-packet main-thread dispatch.  The
        // static lambda has no captured state so the runtime emits exactly one
        // delegate instance for the entire process lifetime — no per-packet
        // closure box, no captured `this` pointer.  ProcessPacketAndReturn
        // reads `NetworkManager.Instance` to find the live receiver, which is
        // safe because the receive path is unconditionally torn down with the
        // singleton in OnDestroy.
        private static readonly Action<byte[], int> s_ProcessPacketDispatch =
            ProcessPacketAndReturn;

        // Drain handler executed on the Unity main thread.  Consumes the
        // pool-rented buffer end-to-end and unconditionally returns it to
        // the pool — try/finally guarantees the rental does not leak on
        // any exception path inside ProcessPacket or its callees.
        private static void ProcessPacketAndReturn(byte[] buffer, int length)
        {
            var instance = System.Threading.Volatile.Read(ref _instance);
            if (instance == null)
            {
                // Manager was torn down between dispatch and drain — return
                // the rented buffer so the pool does not leak.
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
                return;
            }

            try
            {
                instance.ProcessPacket(buffer, length);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        // Invoked SYNCHRONOUSLY on the network background thread for every
        // datagram.  The `rented` argument is owned by NetworkThread and is
        // returned to ArrayPool the moment this method returns, so we copy
        // the meaningful prefix into a SEPARATE pool rental that is ours to
        // hand off to the main thread.  Net effect: one pool rent + one
        // pool return per packet, zero managed-heap allocation in steady
        // state (delegate is pre-resolved, dispatcher work item carries the
        // (buffer, length) pair inline).
        private void HandlePacketReceivedRented(byte[] rented, int offset, int length)
        {
            // Telemetry — count wire-level inbound packets / bytes BEFORE
            // dispatching to the main thread so the metrics reflect the raw
            // socket-level traffic regardless of any decryption / decompression
            // applied later in ProcessPacket.  Interlocked.Add is lock-free
            // and safe to call from the network thread.
            if (length <= 0) return;
            System.Threading.Interlocked.Increment(ref _packetsIn);
            System.Threading.Interlocked.Add(ref _bytesIn, length);

            var dispatcher = _dispatcher;
            if (dispatcher == null) return;

            // Re-rent so the buffer survives the cross-thread hop.  Rent may
            // hand back an array larger than `length`; the dispatched length
            // is the authoritative byte count for ProcessPacket, never
            // `owned.Length`.
            var owned = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
            bool accepted = false;
            try
            {
                Buffer.BlockCopy(rented, offset, owned, 0, length);
                accepted = dispatcher.Enqueue(s_ProcessPacketDispatch, owned, length);
            }
            catch
            {
                // Enqueue failure (queue full + Throw policy, or OOM during
                // segment grow) must release the rental to keep the pool
                // honest — the dispatched drain will never run for this
                // buffer if we throw.
                System.Buffers.ArrayPool<byte>.Shared.Return(owned, clearArray: true);
                throw;
            }

            // Backpressure rejection (DropTail / DropHead) returns the rental
            // synchronously; ProcessPacketAndReturn never runs for a dropped
            // packet so the rental would otherwise leak.  The throttled
            // warning surfaces the drop without flooding the log under
            // sustained backpressure.
            if (!accepted)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(owned, clearArray: true);
                MaybeWarnRentedPacketDropped();
            }
        }

        // Throttle the dropped-rented-packet warning to at most one log per
        // second.  Stopwatch ticks are monotonic so a wall-clock adjustment
        // cannot freeze or open the gate; the comparison is against the last
        // emit timestamp captured in the same tick frame.
        private long _lastRentedDropWarnTicks;

        private void MaybeWarnRentedPacketDropped()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            long last = System.Threading.Interlocked.Read(ref _lastRentedDropWarnTicks);
            long oneSecond = System.Diagnostics.Stopwatch.Frequency;
            if (last != 0 && now - last < oneSecond) return;

            // CompareExchange so concurrent network-thread callers cannot
            // both pass the gate; the first writer emits, others fall through.
            if (System.Threading.Interlocked.CompareExchange(
                    ref _lastRentedDropWarnTicks, now, last) != last)
                return;

            // Redacted: the log mentions only the bounded total, never the
            // dropped packet's content or peer address.
            Debug.LogWarning(
                "[RTMPE] Inbound packet dropped by MainThreadDispatcher backpressure; " +
                "pool rental returned.  See dispatcher.DroppedRentedPacketCount for the running total.");
        }

        /// <summary>
        /// Single funnel for every wire-level outbound packet.  Increments the
        /// <c>_packetsOut</c> / <c>_bytesOut</c> telemetry counters and
        /// forwards to <see cref="NetworkThread.SendOwned"/>.  All historical
        /// call sites that previously called <c>_networkThread.SendOwned</c>
        /// directly route through this helper so the Network Debugger sees a
        /// complete picture of outbound traffic.
        ///
       /// <para>The packet must be owned by the caller (defensive copies are
        /// the caller's responsibility, matching the original SendOwned
        /// contract).</para>
        /// </summary>
        private void SendToWire(byte[] packet)
        {
            if (packet == null) return;

            // Capture hook used by EncryptAndSendRedundant: when set, the
            // post-encryption bytes are routed to the override instead of the
            // real network thread.  Telemetry counters intentionally do NOT
            // tick for the captured copy — only the actual wire copies count.
            var capture = _wireSendOverride;
            if (capture != null)
            {
                AssertWireSendOverrideMainThread(nameof(SendToWire));
                capture(packet);
                return;
            }

            if (_networkThread == null) return;

            System.Threading.Interlocked.Increment(ref _packetsOut);
            System.Threading.Interlocked.Add(ref _bytesOut, packet.Length);

            _networkThread.SendOwned(packet);
        }

        // Rate-limit malformed-packet warnings to 1Hz so a hostile flood
        // cannot drive GC pressure via the log pipeline — each interpolated
        // $"..." would otherwise allocate a fresh formatted System.String,
        // and at 1 kHz of bad packets that cost dominates the receive thread
        // and the Update-frame budget on mobile.  A separate timestamp per
        // site means a slow-drip flow of one bad-magic + one bad-version
        // still surfaces both warnings; the cap is per category, not global.
        private long _lastBadHeaderWarnTicks;
        private long _lastBadMagicWarnTicks;
        private long _lastBadVersionWarnTicks;
        private long _lastAeadFailWarnTicks;
        private long _lastMissingEncryptionWarnTicks;
        private long _lastInboundFloodWarnTicks;
        private long _lastPreSessionGameDataWarnTicks;
        private long _lastMasterClientTransferWarnTicks;

        // Inbound packet rate limiter — extracted to RTMPE.Core.Protocol.InboundBudget.
        // The state + token-bucket math live in that class; NetworkManager keeps
        // a single field reference and a thin public passthrough for the
        // observability counter.  Defaults stay defined as InboundBudget.MaxTokens
        // (burst 3000) and InboundBudget.RefillPerSec (sustained 1500 pps).  See
        // Runtime/Core/Protocol/InboundBudget.cs for the full threading contract
        // and threat-model rationale.
        private readonly InboundBudget _inboundBudget = new InboundBudget();

        // Defence-in-depth: explicit session-established flag.  Game-data
        // packet handlers (Spawn / VariableUpdate / RPC / property
        // broadcasts) require BOTH _state == InRoom AND
        // _sessionEstablished — currently equivalent because InRoom is only
        // reachable after a successful SessionAck, but the redundant gate
        // closes any future state-machine refactor that decouples the two.
        // Set in OnSessionAck; cleared in TransitionTo(Disconnected).
        //
        // The volatile modifier guarantees that a write on one thread is
        // observable on every other thread on its next read.  Writes happen
        // on the Unity main thread (post-dispatch handlers); reads happen on
        // the network-receive thread inside the pre-dispatch admission gate.
        // Without the barrier the C# memory model permits a stale-cached
        // read that admits a packet whose session has just been torn down.
        private volatile bool _sessionEstablished;

        /// <summary>
        /// Number of inbound packets dropped because the per-second token
        /// bucket was exhausted.  Surfaced for backpressure observability —
        /// any persistent non-zero rate means either a hostile gateway or a
        /// configuration mismatch (legitimate burst above the cap).
        /// </summary>
        public long DroppedInboundFloodPacketCount =>
            _inboundBudget.DroppedFloodPacketCount;

        /// <summary>
        /// Number of Enhanced RPC payloads dropped at enqueue time because
        /// the buffer-replay deferral queue hit one of its three caps
        /// (per-payload size, cumulative bytes, slot count).  Surfaced so an
        /// application can alert on a sustained non-zero rate that would
        /// otherwise only surface as quiet log lines.
        /// </summary>
        public long DroppedRpcReplayBufferCount =>
            _rpcReplayBuffer.DroppedCount;

        // Per-cap rate-limit gates for the warning emitted on each drop.
        // Stopwatch ticks are monotonic so an NTP step cannot freeze or
        // open the gates; ShouldWarn collapses concurrent emitters via CAS
        // so a flood of drops still produces at most one warning per second
        // per cap, keeping the log signal-to-noise ratio bounded.
        private long _lastRpcDropPayloadWarnTicks;
        private long _lastRpcDropCumulativeWarnTicks;
        private long _lastRpcDropSlotWarnTicks;

        // Returns true when the per-site one-second gate has elapsed and the
        // caller should emit its warning.  Stopwatch ticks are monotonic so
        // an NTP step cannot freeze or open the gate; CompareExchange races
        // resolve to a single emitter per epoch.
        private static bool ShouldWarn(ref long lastWarnTicks)
        {
            long now      = System.Diagnostics.Stopwatch.GetTimestamp();
            long prev     = System.Threading.Interlocked.Read(ref lastWarnTicks);
            long oneSec   = System.Diagnostics.Stopwatch.Frequency;
            if (prev != 0 && now - prev < oneSec) return false;
            return System.Threading.Interlocked.CompareExchange(
                ref lastWarnTicks, now, prev) == prev;
        }

        // Token-bucket inbound packet admission moved to
        // RTMPE.Core.Protocol.InboundBudget.TryConsume — see that class for
        // the threat-model rationale, capacity choices, and threading contract.

        // RequiresActiveSession was extracted to RTMPE.Core.Protocol.PacketGates —
        // the static decision table is shared with future PacketDispatcher
        // tooling and is unit-testable in isolation. The local thin
        // passthrough below preserves the existing call-site shape.
        private static bool RequiresActiveSession(PacketType type)
            => RTMPE.Core.Protocol.PacketGates.RequiresActiveSession(type);

        // Length-aware overload — `data` may be a pool-rented buffer whose
        // physical .Length exceeds the meaningful packet size.  All header
        // and payload-bound checks therefore use the explicit
        // <paramref name="length"/> parameter, never <c>data.Length</c>.
        private void ProcessPacket(byte[] data, int length)
        {
            // ── Gate 1: Length sanity (cheapest; runs first) ────────────────
            // A null buffer or sub-header length cannot pass any subsequent
            // check — reject before consuming budget so a malformed-packet
            // flood does not also wedge the rate limiter against legitimate
            // traffic. Gate ordering: length before budget before header
            // validation before AEAD — cheapest checks run first.
            if (data == null || length < PacketProtocol.HEADER_SIZE)
            {
                if (ShouldWarn(ref _lastBadHeaderWarnTicks))
                    Debug.LogWarning("[RTMPE] Dropped packet: too short to contain a valid header.");
                return;
            }

            // ── Gate 2: Inbound budget (token bucket) ───────────────────────
            // Bound CPU under hostile flood.  The token bucket runs BEFORE any
            // magic / version / AEAD work so a replay-amplification attack
            // against a valid encrypted packet at line-rate cannot saturate
            // the main thread.  Legitimate 30 Hz × 16-player traffic
            // (~480 pps) is an order of magnitude below the cap; surfaces
            // drops via <see cref="DroppedInboundFloodPacketCount"/>.
            if (!_inboundBudget.TryConsume())
            {
                _inboundBudget.RecordDrop();
                if (ShouldWarn(ref _lastInboundFloodWarnTicks))
                    Debug.LogWarning(
                        "[RTMPE] Inbound packet rate exceeded " +
                        $"{InboundBudget.RefillPerSec:F0} pps (burst {InboundBudget.MaxTokens:F0}); " +
                        "dropping. Sustained drops indicate either a hostile gateway " +
                        "flood or legitimate traffic above the cap — inspect " +
                        "DroppedInboundFloodPacketCount.");
                return;
            }

            // ── Gate 3: Header magic + version + type/flags extraction ─────
            // <see cref="PacketGates.ValidateHeader"/> is pure / side-effect
            // free and shares the same wire-format invariants the gateway
            // enforces, so it is unit-testable in isolation. The length
            // re-check inside it is redundant given Gate 1 but harmless;
            // per-failure-mode warning latches stay at this call site so
            // each failure mode has its own independent rate-limit.
            var hdrResult = PacketGates.ValidateHeader(
                data, length, out var packetType, out var wasEncrypted);
            switch (hdrResult)
            {
                case HeaderValidationResult.BadMagic:
                {
                    if (ShouldWarn(ref _lastBadMagicWarnTicks))
                    {
                        // Re-read on the cold failure path so the log line
                        // includes the observed magic value. Hot path
                        // pays nothing for the diagnostic surface.
                        var observedMagic = (ushort)(
                              data[PacketProtocol.OFFSET_MAGIC]
                            | (data[PacketProtocol.OFFSET_MAGIC + 1] << 8));
                        Debug.LogWarning(
                            $"[RTMPE] Dropped packet: bad magic 0x{observedMagic:X4} " +
                            $"(expected 0x{PacketProtocol.MAGIC:X4}).");
                    }
                    return;
                }

                case HeaderValidationResult.UnsupportedVersion:
                    if (ShouldWarn(ref _lastBadVersionWarnTicks))
                        Debug.LogWarning(
                            $"[RTMPE] Dropped packet: unsupported protocol version " +
                            $"{data[PacketProtocol.OFFSET_VERSION]} (expected {PacketProtocol.VERSION}).");
                    return;

                case HeaderValidationResult.TooShort:
                    // Pre-checked at Gate 1; keep the case here so a future
                    // ValidateHeader extension that adds new TooShort triggers
                    // (e.g. minimum-encrypted-size) is not silently dropped
                    // through the default arm.
                    if (ShouldWarn(ref _lastBadHeaderWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped packet: too short to contain a valid header.");
                    return;

                case HeaderValidationResult.MalformedFlags:
                    // The flags byte carries a bit outside the protocol's
                    // defined set — a corrupt, tampered, or version-skewed
                    // frame.  Shares the malformed-header warning latch.
                    if (ShouldWarn(ref _lastBadHeaderWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped packet: header flags byte carries a bit " +
                            "outside the protocol's defined set.");
                    return;

                case HeaderValidationResult.Ok:
                    break;
            }

            // If FLAG_ENCRYPTED is set the gateway has wrapped the payload in a
            // ChaCha20-Poly1305 AEAD envelope.  Decrypt before dispatching so every
            // handler always receives plaintext — handlers are unaware of encryption.
            //
            // Pre-handshake packets (Challenge, HandshakeAck) arrive plaintext by
            // protocol — the SDK has no key material yet.  Post-handshake packets
            // carry session-bound semantics and must be AEAD-protected; the
            // `requiresEnc` gate below enforces that.  SessionAck is the one
            // exception: it is the bootstrap envelope, and whether it is sealed
            // is a negotiated property of the handshake
            // (CapabilityFlags.EncryptedSessionAck) rather than a mandate of the
            // static gate.
            // Note: wasEncrypted + packetType were captured by ValidateHeader
            // above; both are read from the plaintext header bytes which the
            // decrypt path preserves verbatim, so subsequent state-machine
            // logic can rely on them across the decrypt boundary.
            int packetLength = length;

            // SessionAck bootstrap-encryption path.  When the handshake
            // negotiated CapabilityFlags.EncryptedSessionAck the gateway seals
            // the SessionAck payload under a one-time AEAD key derived from the
            // ECDH shared secret (HKDF info suffix \x03), with a fixed all-zero
            // 12-byte nonce and AAD = [0x08, FLAG_ENCRYPTED], and stamps
            // FLAG_ENCRYPTED on the header.  The decrypt decision is taken
            // purely from that wire bit: the SDK always advertises the
            // capability, so a sealed SessionAck means the gateway honoured it.
            // The regular session-key decrypt path cannot open this packet —
            // SessionAck is the bootstrap that delivers crypto_id, which the
            // session-AEAD nonce depends on — so it is routed through a
            // dedicated decrypt before the generic AEAD branch runs.
            //
            // `wasEncrypted` is wire truth (the FLAG_ENCRYPTED bit) and is
            // never mutated locally; `alreadyDecrypted` tracks whether the
            // SessionAck-specific path already produced plaintext so the
            // generic AEAD branch is skipped.
            bool alreadyDecrypted = false;
            if (wasEncrypted && packetType == PacketType.SessionAck)
            {
                data = DecryptSessionAckPacket(data, length);
                if (data == null)
                {
                    if (ShouldWarn(ref _lastAeadFailWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped SessionAck: bootstrap AEAD authentication " +
                            "failed — the ECDH-derived bootstrap key did not open the " +
                            "sealed envelope.");
                    return;
                }
                packetLength = data.Length;
                alreadyDecrypted = true;
            }
            if (wasEncrypted && !alreadyDecrypted)
            {
                // DecryptInboundPacket returns an exact-sized plaintext byte[]
                // (header + decrypted payload), severing the dependency on
                // the rented buffer's physical length.  Downstream handlers
                // can therefore continue to inspect data.Length safely.
                data = DecryptInboundPacket(data, length);
                if (data == null)
                {
                    if (ShouldWarn(ref _lastAeadFailWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Dropped packet: AEAD authentication failed " +
                            "(tag mismatch, replayed counter, or no session keys).");
                    return;
                }
                packetLength = data.Length;
            }
            else if (data.Length != length)
            {
                // Pre-handshake unencrypted path: handlers expect data.Length
                // == packet length.  Slice once into an exact-sized array so
                // legacy bounds checks stay correct.  This branch is cold —
                // only handshake bootstrap and a few protocol packets travel
                // unencrypted, so the per-packet allocation here is amortised
                // far below the encrypted hot path's zero-copy benefit.
                var exact = new byte[length];
                Buffer.BlockCopy(data, 0, exact, 0, length);
                data = exact;
                packetLength = length;
            }

            // packetType was captured by ValidateHeader above and is read
            // from the plaintext header byte at OFFSET_TYPE — both decrypt
            // paths above preserve that byte verbatim, so re-reading after
            // the decrypt boundary would be redundant.

            // SessionAck is excluded from RequiresEncryption — it is the
            // bootstrap envelope and its encryption is negotiated, not
            // mandated.  OnSessionAck performs the post-parse downgrade check
            // once the gateway's advertised caps are known: a gateway that
            // advertises EncryptedSessionAck but ships the envelope in the
            // clear is rejected there.
            bool requiresEnc = RequiresEncryption(packetType);
            if (!wasEncrypted && requiresEnc)
            {
                if (ShouldWarn(ref _lastMissingEncryptionWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: {packetType} arrived without " +
                        "FLAG_ENCRYPTED. This packet type carries session-bound " +
                        "state and must be AEAD-protected once the session is " +
                        "established — accepting it would let an off-path " +
                        "attacker race a forged frame against the gateway's reply.");
                return;
            }

            // Centralised pre-dispatch session gate.  Game-data packet
            // handlers each carry their own InRoom check (defence-in-depth),
            // but routing the packet THROUGH the dispatcher first costs CPU
            // and surface area; rejecting at the gate is cheaper and
            // eliminates the implicit-state assumption that "InRoom implies
            // SessionEstablished".  A future state-machine refactor that
            // decouples the two cannot leak game-data dispatch through this
            // path.
            if (RequiresActiveSession(packetType) && !_sessionEstablished)
            {
                if (ShouldWarn(ref _lastPreSessionGameDataWarnTicks))
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: {packetType} arrived before " +
                        "SessionAck completed. Game-data packets are only valid " +
                        "after the session is established; pre-session traffic " +
                        "is rejected as a state-machine integrity check.");
                return;
            }

            if (IsDebugLogEnabled)
                LogDebug($"Received {packetType} ({packetLength} B).");

            switch (packetType)
            {
                // ── ECDH 4-step handshake ────────────────────────────────
                case PacketType.Challenge:    OnChallenge(data);    break;
                case PacketType.SessionAck:   OnSessionAck(data, wasEncrypted); break;

                // ── Legacy handshake (backward compatibility) ────────────────
                case PacketType.HandshakeAck: OnHandshakeAck(data); break;

                // ── Keep-alive ───────────────────────────────────────────────
                case PacketType.HeartbeatAck: OnHeartbeatAck(data); break;

                // ── Room lifecycle (0x20–0x23) ─────────────────────
                case PacketType.RoomCreate:
                case PacketType.RoomJoin:
                case PacketType.RoomLeave:
                case PacketType.RoomList:
                    OnRoomPacket(packetType, data);
                    break;

                // ── Custom property broadcasts (0x24–0x25) ─────────
                case PacketType.RoomPropertyUpdate:
                    OnRoomPropertyUpdateBroadcast(data);
                    break;
                case PacketType.PlayerPropertyUpdate:
                    OnPlayerPropertyUpdateBroadcast(data);
                    break;

                // ── Matchmaking (0x26, 0x2B) ──────────────────────
                case PacketType.MatchmakingResponse:
                    _matchmakingManager?.HandleMatchmakingResponse(
                        PacketParser.ExtractPayload(data));
                    break;

                // ── Lobby system (0x27–0x2A) ───────────────────────
                case PacketType.LobbyJoin:
                case PacketType.LobbyList:
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyLeave:
                    // Fire-and-forget — no reply; notify listeners.
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyRoomListUpdate:
                    OnLobbyRoomListUpdate(data);
                    break;

                // ── Room management broadcasts (0x2C, 0x2E, 0x2F) ──
                case PacketType.MasterClientChanged:
                case PacketType.KickPlayer:
                case PacketType.SceneLoaded:
                    OnRoomPacket(packetType, data);
                    break;

                // MasterClientTransfer (0x2D) is a client→server request packet
                // (see MasterClientPacketBuilder).  The gateway communicates a
                // successful transfer to all peers via MasterClientChanged
                // (0x2C); a server-broadcast 0x2D would either be a relay echo
                // or a forged packet from an off-path attacker.  Drop it
                // explicitly with a rate-limited warning so a future protocol
                // change that legitimately delivers 0x2D server→client surfaces
                // here rather than silently falling through to the default arm.
                case PacketType.MasterClientTransfer:
                    if (ShouldWarn(ref _lastMasterClientTransferWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] Ignoring server-broadcast MasterClientTransfer " +
                            "(0x2D); this packet is client→server only. The authoritative " +
                            "master-client change is delivered via MasterClientChanged (0x2C).");
                    break;

                case PacketType.Disconnect:      OnServerDisconnect(data); break;
                case PacketType.Data:
                case PacketType.StateSync:        SafeRaise(OnDataReceived, data, nameof(OnDataReceived)); break;

                // DataAck carries the gateway-acknowledged ARQ sequence as a
                // 4-byte little-endian payload.  Drain the matching entry
                // from the outbound retransmit table BEFORE firing the
                // public event so subscribers observe the post-ack state.
                // A truncated payload (legacy gateway that pre-dates the
                // arq_seq wire extension) skips the ledger update but still
                // raises the event, preserving back-compat semantics.
                case PacketType.DataAck:
                    {
                        var ackPayload = PacketParser.ExtractPayload(data);
                        if (ackPayload != null && ackPayload.Length >= 4)
                        {
                            uint ackedSeq = (uint)(
                                  ackPayload[0]
                                | (ackPayload[1] << 8)
                                | (ackPayload[2] << 16)
                                | (ackPayload[3] << 24));
                            int cleared = _outboundReliableChannel.AcknowledgeUpTo(ackedSeq);
                            if (cleared > 0 && IsDebugLogEnabled)
                                LogDebug($"DataAck arq_seq={ackedSeq} cleared {cleared} retransmit entries.");
                        }
                        SafeRaise(OnDataAcknowledged, nameof(OnDataAcknowledged));
                        LogDebug("DataAck received.");
                    }
                    break;

                // ── Networked object lifecycle ─────────────────────
                case PacketType.Spawn:            OnSpawnPacket(data);   break;
                case PacketType.Despawn:          OnDespawnPacket(data); break;

                // ── RPC system ─────────────────────────────────────
                case PacketType.Rpc:              OnRpcRequest(data);    break;
                case PacketType.RpcResponse:      OnRpcResponse(data);   break;
                case PacketType.RpcBufferReplay:  HandleRpcBufferReplay(PacketParser.ExtractPayload(data)); break;

                // Receive inbound variable update packets.
                case PacketType.VariableUpdate:   HandleVariableUpdatePacket(data); break;

                default:
                    if (IsDebugLogEnabled)
                        LogDebug($"No handler for packet type 0x{(byte)packetType:X2}.");
                    break;
            }
        }

        // ── Handshake packet handlers ───────────────────────────────────────

        /// <summary>
        /// Handle an incoming <c>Challenge</c> (0x06) from the server.
        ///
       /// 1. Parse 128-byte payload: [ephemeral:32][static:32][sig:64].
        /// 2. Verify Ed25519 signature — reject on failure.
        /// 3. Derive session keys via X25519 ECDH + HKDF-SHA256.
        /// 4. Send <c>HandshakeResponse</c> containing the client public key.
        /// </summary>
        private void OnChallenge(byte[] data)
        {
            // Guard: only process Challenge while we are actively connecting
            // or reconnecting.  N-1 adds the Reconnecting state — the server
            // replies to ReconnectInit with the same Challenge packet format,
            // so the same handler runs for both flows.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;
            if (_handshakeHandler == null)         return;

            var payload = PacketParser.ExtractPayload(data);

            byte[] configuredPin = null;
            try { configuredPin = _settings?.PinnedServerPublicKeyBytes; }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Invalid pinnedServerPublicKeyHex in settings: {ex.Message}");
                return;
            }

            // Resolve the pinning decision BEFORE invoking ValidateChallenge.
            // The default (Strict + no configured pin) refuses outright: a
            // silent "trust any signed key" path would let any rogue gateway
            // with its own Ed25519 identity complete the handshake, which is
            // the substituted-key MITM vector the pin is meant to close.
            //
           // Trust-On-First-Use captures the static key after — never before —
            // ValidateChallenge succeeds, so an attacker cannot poison the pin
            // store by sending a malformed Challenge.
            var mode       = _settings != null ? _settings.EffectivePinningMode : ServerPinningMode.Strict;
            var resolution = ServerKeyPinning.PreparePin(
                mode,
                configuredPin,
                PinStore,
                _settings != null ? _settings.serverHost : "",
                _settings != null ? _settings.serverPort : 0,
                requireFirstUseProvisioned:
                    _settings != null && _settings.requireFirstUseProvisioned);

            switch (resolution.Decision)
            {
                case PinDecision.RefuseStrictNoPin:
                    Debug.LogError(
                        "[RTMPE] Server not pinned — refusing handshake.  Either set " +
                        "NetworkSettings.pinnedServerPublicKeyHex (Strict), pre-provision a " +
                        "pin via IServerKeyPinStore (TrustOnFirstUse + " +
                        "requireFirstUseProvisioned), or relax requireFirstUseProvisioned " +
                        "if first-flight TOFU capture is acceptable for this deployment.");
                    return;

                case PinDecision.ProceedUnpinned:
                    Debug.LogWarning(
                        "[RTMPE] ServerPinningMode.InsecureNoPinning is active — the SDK will " +
                        "accept any valid Ed25519 signature. This is unsafe for production: a " +
                        "rogue gateway with its own keypair will complete the handshake. " +
                        "Configure pinnedServerPublicKeyHex (Strict) or TrustOnFirstUse instead.");
                    break;
            }

            // Pass the previously emitted HandshakeInit ciphertext (or null
            // in the reconnect flow) so ValidateChallenge can reconstruct the
            // same transcript the gateway signed.  A reconnect path leaves
            // _lastHandshakeInitCiphertext == null, which the handler maps to
            // the agreed absent-sentinel (32 × 0x00); replay defence in that
            // flow is provided by the single-use reconnect token instead.
            //
            // Classify the in-flight handshake from the state machine rather
            // than from ciphertext-presence.  Anchoring on
            // NetworkState.Reconnecting (set only by StartReconnectAttempt)
            // and additionally requiring a non-empty reconnect token means a
            // fresh Connect() that races with a stale _reconnectToken left
            // over from a prior session cannot silently mis-engage the
            // reconnect transcript path during the brief window where the
            // ciphertext is also null.  Any future disagreement between
            // ciphertext-presence and the state-derived flow is caught by
            // ValidateChallenge's own defence-in-depth checks.
            HandshakeFlow handshakeFlow;
            if (_state == NetworkState.Reconnecting && !string.IsNullOrEmpty(_reconnectToken))
            {
                handshakeFlow = HandshakeFlow.Reconnect;
            }
            else
            {
                handshakeFlow = HandshakeFlow.Init;
            }

            if (!_handshakeHandler.ValidateChallenge(
                    payload,
                    _lastHandshakeInitCiphertext,
                    handshakeFlow,
                    out _,                                    // serverEphemeralPub (stored inside handler)
                    out var verifiedServerStaticPub,          // captured for TOFU persistence
                    resolution.PinToEnforce))
            {
                Debug.LogError("[RTMPE] Challenge validation failed — Ed25519 signature invalid, " +
                               "pin mismatch, or Challenge payload malformed. Possible MITM " +
                               "attack. Disconnecting.");
                // Let the timeout coroutine fire OnConnectionFailed cleanly.
                return;
            }

            // Retain the verified server identity key: the Challenge Ed25519
            // signature has now been checked against it, so it is a trusted
            // anchor.  If the gateway advertises CapabilityFlags.IdentitySignedJwt
            // in the upcoming SessionAck, OnSessionAck verifies the JWT
            // signature against this same key.
            _serverIdentityPublicKey = verifiedServerStaticPub;

            // Derive directional session keys (AEAD) + N-8 IP migration key via HKDF-SHA256.
            // Three independent expansions from a single PRK — info suffixes \x00, \x01, \x02.
            var derivedKeys = _handshakeHandler.DeriveSessionKeys(out _ipMigrationKey, out _sessionAckKey);
            if (derivedKeys == null)
            {
                Debug.LogError("[RTMPE] ECDH key derivation failed (degenerate shared secret). Disconnecting.");
                return;
            }
            _sessionKeyStore.InstallSessionKeys(derivedKeys);

            // Fresh session keys imply a fresh inbound nonce stream — reuse
            // of an old window would falsely reject the first packet of the
            // new session because its counter starts back at zero.
            //
            // The window MUST be live before the first AEAD-decrypted packet
            // is dispatched, so it is initialised here — immediately after
            // key derivation, before HandshakeResponse is sent and therefore
            // before any inbound frame can be sealed under the new keys.  The
            // receive path treats a null window as a hard reject for any
            // AEAD-bearing frame, so a missed initialisation cannot silently
            // degrade into a no-op replay check.
            if (_sessionKeyStore.ReplayWindow == null)
                _sessionKeyStore.EnsureReplayWindow();
            else
                _sessionKeyStore.ReplayWindow.Reset();

            // Persist the captured key only AFTER both transcript verification
            // and ECDH succeed.  Writing earlier would let a malformed
            // Challenge — one whose signature passes parsing but whose ECDH
            // produces a degenerate secret — poison the pin store.
            if (ServerKeyPinning.PersistFirstUse(resolution, PinStore, verifiedServerStaticPub))
            {
                LogDebug(
                    $"Captured server static key on first connect to " +
                    $"{resolution.Endpoint} (TrustOnFirstUse).");
            }

            // Send the client's X25519 ephemeral public key to the server.
            // Use SendOwned — response is a freshly allocated array that
            // will not be reused, so the extra copy inside Send() is unnecessary.
            //
            // The cap advertisement carries every optional feature the SDK
            // is willing to honour for this session — see ComputeLocalCaps.
            // A gateway that does not understand the cap field tolerates the
            // trailing bytes (`payload[..32]` semantics are unchanged), so
            // emitting the advertisement is safe against legacy gateways.
            RTMPE.Core.Protocol.CapabilityFlags clientCaps = ComputeLocalCaps();
            var response = _packetBuilder.BuildHandshakeResponse(
                _handshakeHandler.ClientPublicKey,
                RTMPE.Protocol.WireFormat.Default,
                clientCaps);
            SendToWire(response);
            LogDebug("Sent HandshakeResponse — awaiting SessionAck.");
        }

        /// <summary>
        /// The capability bitmask the SDK advertises for this session.
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck"/>
        /// is always set — the SDK can always decrypt the bootstrap envelope —
        /// and <see cref="RTMPE.Core.Protocol.CapabilityFlags.ArqAck"/> is
        /// added only when the local <c>EmitArqSequence</c> opt-in is active,
        /// keeping the on-wire promise honest (the SDK cannot consume the
        /// gateway's DataAck without the local opt-in).  The same value is
        /// sent in HandshakeResponse and re-derived in OnSessionAck for the
        /// negotiation intersection, so both call sites route through here.
        /// </summary>
        private RTMPE.Core.Protocol.CapabilityFlags ComputeLocalCaps()
        {
            var caps = RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck;
            if (_settings != null && _settings.EmitArqSequence)
                caps |= RTMPE.Core.Protocol.CapabilityFlags.ArqAck;
            return caps;
        }

        /// <summary>
        /// Handle <c>SessionAck</c> (0x08): parse crypto_id, JWT, and reconnect token,
        /// then transition to <see cref="NetworkState.Connected"/> and start heartbeat.
        /// </summary>
        /// <param name="data">The SessionAck packet bytes (already decrypted
        /// when the bootstrap envelope was sealed).</param>
        /// <param name="wasEncrypted">Wire truth: whether the SessionAck
        /// arrived carrying <see cref="PacketFlags.Encrypted"/>.  Used for the
        /// post-parse downgrade check against the gateway's advertised caps.</param>
        private void OnSessionAck(byte[] data, bool wasEncrypted)
        {
            // Guard: ignore stale ACKs that arrive after a timeout.
            // N-1: reconnect flow also terminates with SessionAck, so accept
            // both Connecting and Reconnecting states.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;

            // Remember whether this SessionAck is closing a reconnect flow —
            // we check it BEFORE the state transition below clears the context.
            bool wasReconnecting = _state == NetworkState.Reconnecting;

            var payload = PacketParser.ExtractPayload(data);

            if (!PacketParser.ParseSessionAck(payload,
                    out uint   cryptoId,
                    out string jwtToken,
                    out string reconnectToken,
                    out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps))
            {
                Debug.LogError("[RTMPE] SessionAck parse failed — malformed payload. Disconnecting.");
                return;
            }

            // Negotiate the session-effective capability set as the bitwise
            // intersection of what the SDK advertised in HandshakeResponse
            // (re-derived here via ComputeLocalCaps so the two are always
            // identical) and what the gateway returned in SessionAck.  The
            // intersection captured here gates the Send / AeadPipeline /
            // retransmit-tick paths for the rest of the session.  A legacy
            // gateway that does not understand the SessionAck tail yields
            // `gatewayCaps == None`, which makes the intersection empty and
            // falls back to the pre-capability behaviour — exactly what the
            // back-compat contract requires.
            RTMPE.Core.Protocol.CapabilityFlags localCaps = ComputeLocalCaps();
            _negotiatedPeerCaps =
                RTMPE.Core.Protocol.CapabilityFlagsWire.Negotiate(localCaps, gatewayCaps);

            // Downgrade guard: a gateway that advertised EncryptedSessionAck
            // must also have sealed the bootstrap envelope.  An advertised-
            // but-plaintext SessionAck is refused rather than continued on an
            // unprotected bootstrap.
            //
            // `gatewayCaps` is AEAD-authenticated when `wasEncrypted` is true
            // (it was parsed from the decrypted payload) and attacker-mutable
            // only when the packet arrived in the clear.  The guard is sound
            // regardless: to suppress it an attacker would need a plaintext
            // SessionAck the SDK still accepts, but a capability-aware gateway
            // only ever emits a sealed envelope, and AEAD ciphertext is
            // overwhelmingly unlikely to satisfy the length-prefix and
            // UTF-8 checks in PacketParser.ParseSessionAck — so in practice
            // no acceptable plaintext packet exists to pair with a cleared
            // capability bit.  The guard's own correctness does not rest on
            // that probability: it keys off the `wasEncrypted` wire bit, not
            // on parse success.  It is deliberately NOT widened to "reject
            // every plaintext SessionAck": that would break interoperability
            // with genuinely legacy gateways, which legitimately ship the
            // envelope in the clear and advertise no capability tail at all.
            if ((gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck) != 0
                && !wasEncrypted)
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: the gateway advertised encrypted-" +
                    "SessionAck support but delivered the bootstrap envelope in the " +
                    "clear. Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            // Validate the JWT before we trust the sub claim that becomes the
            // local session identifier.  When the gateway advertised
            // CapabilityFlags.IdentitySignedJwt the token is verified against
            // the server identity key the SDK verified on the Challenge —
            // how strong an anchor that is depends on the server-key pinning
            // mode.  Without that advertisement verification follows the
            // NetworkSettings configuration; either way the structural and
            // temporal claims are always enforced so a malformed or expired
            // token cannot install garbage as `_localPlayerId` and corrupt
            // every subsequent AEAD nonce.
            bool gatewayAssertsIdentityJwt =
                (gatewayCaps & RTMPE.Core.Protocol.CapabilityFlags.IdentitySignedJwt) != 0;

            // Honour the gateway's identity-signed-JWT assertion as a hard
            // requirement: if the identity key was not captured the SDK
            // cannot perform the promised verification, so it fails closed
            // rather than silently falling back to structural-only checks.
            // The Challenge always precedes SessionAck, so this is
            // unreachable today; the guard keeps a future handshake refactor
            // from quietly weakening token verification.
            if (gatewayAssertsIdentityJwt
                && (_serverIdentityPublicKey == null || _serverIdentityPublicKey.Length == 0))
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: the gateway advertised an " +
                    "identity-signed JWT but the server identity key was not " +
                    "available to verify it. Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            byte[] jwtVerificationKey =
                gatewayAssertsIdentityJwt ? _serverIdentityPublicKey : null;
            if (!TryValidateJwt(jwtToken,
                    expectedIssuer: _settings != null ? _settings.expectedJwtIssuer : null,
                    expectedAudience: _settings != null ? _settings.expectedJwtAudience : null,
                    jwtVerificationKey,
                    out string subject,
                    out string jwtError))
            {
                Debug.LogError(
                    $"[RTMPE] SessionAck rejected: JWT validation failed " +
                    $"({jwtError}). Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            if (!ulong.TryParse(subject, out var sessionId))
            {
                Debug.LogError(
                    "[RTMPE] SessionAck rejected: JWT sub claim is not a valid u64 session ID. " +
                    "Disconnecting.");
                DisconnectWithReason(DisconnectReason.Unknown);
                return;
            }

            _sessionKeyStore.InstallCryptoId(cryptoId);
            _jwtToken       = jwtToken;
            _reconnectToken = reconnectToken;
            _localPlayerId  = sessionId;

            // Even under verbose logging we redact session-correlation identifiers
            // so support-bundle log captures never leak a full crypto_id or session_id
            // into third-party ticketing systems (Slack, Jira, Zendesk, etc.).
            LogDebug(
                $"SessionAck received: crypto_id={LogRedaction.Redact(cryptoId)}, " +
                $"session_id={LogRedaction.Redact(_localPlayerId)}, " +
                $"jwt_len={jwtToken?.Length ?? 0}");

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }

            // Mark the session live BEFORE the state transition so any
            // observer hooked into TransitionTo (e.g. user-supplied
            // OnConnected handlers) sees a consistent snapshot.  The flag
            // is the centralised pre-dispatch gate's witness: without it
            // RequiresActiveSession-typed packets are dropped.
            _sessionEstablished = true;

            TransitionTo(NetworkState.Connected);

            // Start keep-alive heartbeat.
            // Pass _packetBuilder so heartbeat packets share the same sequence
            // counter as all other outbound packets (prevents nonce reuse under AEAD).
            //
           // Lifecycle note: the OnRttUpdated lambda below captures `this` (via
            // LastRttMs and SafeRaise on instance event fields).  This is safe
            // because Cleanup() nulls _heartbeatManager — releasing the only
            // outstanding reference to the HeartbeatManager and therefore the
            // only path that could invoke the lambda — and OnDestroy() calls
            // Cleanup() before the NetworkManager itself is finalized.  The
            // lambda is therefore guaranteed to die with the manager it was
            // wired to.  Do NOT replace these with named methods unless the
            // Cleanup path is also updated to explicitly unsubscribe — the
            // implicit "_heartbeatManager = null drops the chain" contract is
            // load-bearing.
            // Reconnect path: a previous SessionAck may have wired event
            // handlers onto an earlier HeartbeatManager.  Stop and detach the
            // old one explicitly before assigning the new instance so the
            // old multicast list cannot accumulate stale per-cycle
            // subscriptions across reconnect bursts.  Stop() is idempotent
            // and safe even when the manager has not yet started.
            if (_heartbeatManager != null)
            {
                _heartbeatManager.Stop();
                _heartbeatManager.OnHeartbeatTimeout -= OnHeartbeatTimeout;
                // OnRttUpdated was wired to a fresh closure on each session,
                // so the old delegate list is unreachable once
                // _heartbeatManager is replaced — no symmetric -= needed.
            }

            _heartbeatManager = new HeartbeatManager(_settings.heartbeatIntervalMs, _packetBuilder);
            _heartbeatManager.OnRttUpdated     += rtt => { LastRttMs = rtt; SafeRaise(OnRttUpdated, rtt, nameof(OnRttUpdated)); };
            _heartbeatManager.OnHeartbeatTimeout += OnHeartbeatTimeout;
            _heartbeatManager.Start();

            // Auto-rejoin the last room after a successful reconnect, if enabled.
            // The session is now fully established, so RoomManager.RequireConnected
            // will pass.  We intentionally do NOT clear _lastRoomId here — the
            // subsequent OnRoomJoined handler will refresh the snapshot with the
            // fresh RoomInfo returned by the server.
            if (wasReconnecting && _settings != null && _settings.autoRejoinLastRoomOnReconnect)
            {
                TryAutoRejoinLastRoom();
            }
        }

        /// <summary>
        /// Attempt to rejoin <see cref="LastRoomId"/> via
        /// <see cref="RoomManager.JoinRoom"/>.  No-op when no snapshot exists.
        /// Silent on RoomManager internal failures — the app can observe the
        /// outcome through the existing <see cref="RoomManager.OnRoomJoined"/> /
        /// <see cref="RoomManager.OnRoomError"/> events.
        /// </summary>
        private void TryAutoRejoinLastRoom()
        {
            if (string.IsNullOrEmpty(_lastRoomId))
            {
                LogDebug("Reconnect: no last room to auto-rejoin.");
                return;
            }

            if (_roomManager == null)
            {
                Debug.LogWarning("[RTMPE] Auto-rejoin: RoomManager is null (internal invariant violation).");
                return;
            }

            LogDebug($"Reconnect: auto-rejoining last room {_lastRoomId}.");
            SafeRaise(OnAutoRejoinAttempt, _lastRoomId, nameof(OnAutoRejoinAttempt));
            _roomManager.JoinRoom(_lastRoomId);
        }

    }
}
