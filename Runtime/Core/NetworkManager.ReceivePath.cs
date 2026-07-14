// RTMPE SDK — Runtime/Core/NetworkManager.ReceivePath.cs
//
// ProcessPacket dispatch + inbound handlers + transport error path + state machine.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Core.Rpc;
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
        // Malformed UTF-8 must reject the packet, mirroring every room/lobby
        // packet parser.  The default Encoding.UTF8 substitutes U+FFFD
        // instead, which would let a corrupted or hostile payload pass the
        // decode and surface as silently-mangled property keys and values.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ── Legacy / other handlers ────────────────────────────────────────────

        private void OnHandshakeAck(byte[] _)
        {
            // The legacy unauthenticated handshake (0x02) is incompatible with
            // the current security model, which requires ECDH key derivation via
            // Challenge/HandshakeResponse/SessionAck before reaching Connected.
            // Accepting this packet would leave _sessionKeyStore.SessionKeys null
            // and _sessionEstablished false, causing EncryptAndSend to transmit
            // in plaintext. Force-disconnect instead of permitting an insecure state.
            if (_state != NetworkState.Connecting) return;
            _networkThread?.Stop();
            ClearSessionData(preserveReconnectToken: false);
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ProtocolError);
        }

        private void OnHeartbeatAck(byte[] data)
        {
            // Gateway puts a single backpressure byte (0-255) in the
            // HeartbeatAck payload.  See modules/gateway/src/main.rs:242-247
            // and src/router/session_limiter.rs::backpressure().  Older
            // gateways (and reconnect-only test fixtures) emit an empty
            // payload — the length check makes this fully backward-compatible.
            var payload = PacketParser.ExtractPayload(data);
            if (payload.Length >= 1)
            {
                System.Threading.Volatile.Write(ref _serverBackpressure, payload[0]);
            }
            _heartbeatManager?.OnAckReceived();
        }

        private void OnHeartbeatTimeout()
        {
            Debug.LogWarning("[RTMPE] Heartbeat timeout — no acknowledged keep-alive within the liveness window. Disconnecting.");
            // Transition through Disconnecting first so listeners observing state
            // changes see the full lifecycle (Connected → Disconnecting → Disconnected),
            // consistent with the explicit Disconnect() path.
            TransitionTo(NetworkState.Disconnecting);
            _heartbeatManager?.Stop();
            _networkThread?.Stop();
            // N-1: preserve the reconnect token across the drop so apps can
            // observe OnDisconnected and call Reconnect() without the user
            // having to re-authenticate.  If the app doesn't want a reconnect
            // (e.g. explicit logout), calling Disconnect() still clears it.
            ClearSessionData(preserveReconnectToken: true);
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
        }

        /// <summary>
        /// Route room packets to the RoomManager (lifecycle 0x20–0x23,
        /// management 0x2C/0x2E/0x2F).
        /// </summary>
        private void OnRoomPacket(PacketType type, byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _roomManager.HandleRoomPacket(type, payload);
        }

        /// <summary>
        /// Routes a LobbyJoin reply (0x27) or LobbyList reply (0x29) to the
        /// LobbyManager.  LobbyLeave (0x28) has no server reply but is passed
        /// here for uniform event notification if needed.
        /// </summary>
        private void OnLobbyPacket(PacketType type, byte[] data)
        {
            if (_lobbyManager == null) return;
            if (type == PacketType.LobbyLeave) return; // fire-and-forget: no reply payload
            var payload = PacketParser.ExtractPayload(data);
            // Forward the discriminating PacketType so the LobbyManager only
            // consumes a pending JoinLobby slot when an actual LobbyJoin reply
            // arrives — a stray LobbyList (0x29) reply must not flip
            // IsInLobby.
            _lobbyManager.HandleLobbyReply(type, payload);
        }

        /// <summary>
        /// Routes a LobbyRoomListUpdate push (0x2A) to the LobbyManager.
        /// </summary>
        private void OnLobbyRoomListUpdate(byte[] data)
        {
            if (_lobbyManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _lobbyManager.HandleLobbyRoomListUpdate(payload);
        }

        /// <summary>
        /// Handle an inbound <c>RoomPropertyUpdate</c> (0x24) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the local <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnRoomPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RoomPropertyUpdate broadcast rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = StrictUtf8.GetString(payload);
                var (version, props) = PropertyJson.DecodeRoomPayload(json);
                _roomManager.ApplyRoomPropertiesBroadcast(version, props);
            }
            catch (Exception ex)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RoomPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an inbound <c>PlayerPropertyUpdate</c> (0x25) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the matching player in
        /// <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnPlayerPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"PlayerPropertyUpdate broadcast rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = StrictUtf8.GetString(payload);
                var (playerId, version, props) = PropertyJson.DecodePlayerPayload(json);
                _roomManager.ApplyPlayerPropertiesBroadcast(playerId, version, props);
            }
            catch (Exception ex)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"PlayerPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        // ── Spawn / Despawn inbound handlers ─────────────────────────

        // A staged catch-up packet was shed because the pre-room buffer stood at
        // capacity, so its oldest entry was dropped to make room. Surfaced as a
        // rate-limited warning rather than passing unseen: a lost pre-join Spawn,
        // Despawn, or RPC replay can leave a peer's object unrendered until the
        // next full snapshot. Gated to one line per second so a sustained
        // overflow reports the condition without flooding the log.
        private void WarnIfEarlyObjectEvicted(bool evicted)
        {
            if (evicted && ShouldWarn(ref _lastEarlyObjectEvictWarnTicks))
                Debug.LogWarning(
                    "[RTMPE] Early-object staging buffer reached capacity before room " +
                    "entry; the oldest catch-up packet was evicted. A pre-join Spawn, " +
                    "Despawn, or RPC replay may be lost.");
        }

        /// <summary>
        /// Handle an inbound <c>Spawn</c> (0x30) packet from the server.
        /// Parses the payload and calls <see cref="SpawnManager.CreateLocal"/>
        /// to instantiate the object on the receiving client.
        /// </summary>
        private void OnSpawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Game-data packets are applied only after a successful room join;
            // before then the room context does not yet exist.  A Spawn that
            // arrives early is the joiner's own catch-up object — the server
            // replays the live set as the session binds, which can outrun the
            // join reply that opens this gate — so hold it (a defensive copy,
            // the receive buffer may be reused) for ordered release on InRoom
            // rather than dropping it.  Nothing is applied while staged, so the
            // pre-room injection guard is preserved.
            if (_state != NetworkState.InRoom)
            {
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.Stage(EarlyPacketKind.Spawn, (byte[])data.Clone()));
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseSpawn(payload, out var spawnData))
            {
                if (IsDebugLogEnabled)
                    LogDebug("Spawn packet: malformed payload, dropped.");
                return;
            }

            // Dedup: if this object was already spawned locally (e.g. server echoed
            // our own Spawn back), skip to avoid creating a duplicate GameObject.
            if (_spawnManager.Registry.Get(spawnData.ObjectId) != null)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Spawn packet: objectId {spawnData.ObjectId} already exists, skipped (dedup).");
                return;
            }

            _spawnManager.CreateLocal(
                spawnData.PrefabId, spawnData.ObjectId,
                spawnData.OwnerPlayerId, spawnData.Position, spawnData.Rotation);
        }

        /// <summary>
        /// Handle an inbound <c>Despawn</c> (0x31) packet from the server.
        /// Parses the object ID and calls <see cref="SpawnManager.DestroyLocal"/>.
        /// </summary>
        private void OnDespawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Held until InRoom for the same reason as Spawn: a despawn that
            // races ahead of the join reply belongs to the catch-up set and must
            // be replayed in order with its spawns so it does not resurrect an
            // object the server has already removed.  See OnSpawnPacket.
            if (_state != NetworkState.InRoom)
            {
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.Stage(EarlyPacketKind.Despawn, (byte[])data.Clone()));
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseDespawn(payload, out var objectId))
            {
                LogDebug("Despawn packet: malformed payload, dropped.");
                return;
            }
            _spawnManager.DestroyLocal(objectId);
        }

        /// <summary>
        /// Release the object-lifecycle packets that were staged while the
        /// client was not yet in a room, in their original arrival order, now
        /// that the gate they were held behind has opened.  Each packet is
        /// re-dispatched through its normal handler, which now passes the
        /// InRoom check and applies it (Spawn deduplicates against objects the
        /// client already holds, so a buffered object the server later re-sent
        /// live is not double-instantiated).
        /// </summary>
        private void FlushEarlyObjectPackets()
        {
            if (_earlyObjectBuffer.Count == 0) return;

            // Replay in arrival order through the live handlers, which now pass
            // the InRoom check. Spawn deduplicates against objects the client
            // already holds (so a buffered object the server later re-sent live
            // is not double-instantiated), and a catch-up RpcReplay follows the
            // spawns it may target because order is preserved.
            EarlyObjectPacketBuffer.Dispatch(
                _earlyObjectBuffer.Drain(),
                OnSpawnPacket,
                OnDespawnPacket,
                HandleRpcBufferReplay,
                kind =>
                {
                    // A staged kind with no handler is a programming error — a new
                    // EarlyPacketKind added without a dispatch case — so surface it
                    // rather than mis-route it to one of the real handlers.
                    if (IsDebugLogEnabled)
                        LogDebug($"FlushEarlyObjectPackets: unhandled staged kind {kind}, dropped.");
                });
        }

        // ── RPC inbound handlers ─────────────────────────────────────

        /// <summary>
        /// Handle an inbound <c>Rpc</c> (0x50) request from the server.
        /// Dispatches ownership-related RPCs (200) and damage RPCs (301).
        /// </summary>
        private void OnRpcRequest(byte[] data)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RPC request rejected; not in a room (state={_state}).");
                return;
            }

            // Distinguish Enhanced RPC (27-byte header, typed params) from legacy (18-byte).
            bool isEnhanced = (data[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.EnhancedRpc) != 0;

            var payload = PacketParser.ExtractPayload(data);

            if (isEnhanced)
            {
                OnEnhancedRpcRequest(payload);
                return;
            }

            // Legacy RPC path.
            if (!RpcPacketParser.TryParseRequest(payload, out var request))
            {
                if (IsDebugLogEnabled)
                    LogDebug("RPC request: malformed payload, dropped.");
                return;
            }

            // AEAD authenticates the gateway as the relay, not the originating
            // peer.  The Enhanced RPC path already passes every inbound
            // senderId through EnhancedRpcVerifier.IsSenderAcceptable; the
            // legacy MethodId path applies the same uniform gate so a hostile
            // peer cannot stamp Ping (100) / ApplyDamage (301) /
            // TransferOwnership (200) with a spoofed senderId and have the
            // receiver dispatch as if the gateway had attested origin.
            // Per-method overrides (e.g. IsOwnershipTransferAuthorized) layer
            // on top of this gate at the matching case below.
            //
            // The settings toggle is consulted only inside the Unity Editor,
            // where loopback test rigs may legitimately deliver legacy RPCs
            // from senders outside the active roster.  All other build
            // targets enforce the gate unconditionally — the toggle cannot
            // weaken a distributed binary's security posture.
#if UNITY_EDITOR
            bool gateActive = _settings == null || _settings.requireLegacyRpcSender;
#else
            const bool gateActive = true;
#endif
            if (gateActive
                && !LegacyRpcVerifier.IsLegacyRpcAuthorized(
                       request.SenderId, request.MethodId))
            {
                Debug.LogWarning(
                    $"[RTMPE] Legacy RPC rejected: sender " +
                    $"{LogRedaction.Redact(request.SenderId)} not authorised " +
                    $"for method_id {request.MethodId}.");
                return;
            }

            // Publish the gateway-attested sender for the legacy handlers too
            // (mirrors the Enhanced path) so an IDamageable / ownership handler
            // can authorize the call via NetworkManager.CurrentRpcSenderId;
            // saved and restored so a nested or throwing handler cannot leak it.
            ulong previousRpcSenderId = CurrentRpcSenderId;
            CurrentRpcSenderId = request.SenderId;
            try
            {
                switch (request.MethodId)
                {
                    case RpcMethodId.TransferOwnership:
                        HandleOwnershipTransferRpc(request);
                        break;
                    // Server-broadcast ApplyDamage (301) → route to target HealthController.
                    case RpcMethodId.ApplyDamage:
                        HandleApplyDamageRpc(request);
                        break;
                    default:
                        if (IsDebugLogEnabled)
                            LogDebug($"RPC request: unhandled method_id {request.MethodId}.");
                        break;
                }
            }
            finally
            {
                CurrentRpcSenderId = previousRpcSenderId;
            }
        }

        /// <summary>
        /// Dispatch an inbound Enhanced RPC packet to the target <c>NetworkBehaviour</c>.
        /// Resolves the object via the spawn registry and invokes the correct
        /// <c>[RtmpeRpc]</c> method via <see cref="RTMPE.Core.NetworkBehaviour.DispatchEnhancedRpc"/>.
        /// </summary>
        private void OnEnhancedRpcRequest(byte[] payload)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Enhanced RPC rejected; not in a room (state={_state}).");
                return;
            }

            // Buffered (historical) RPCs must be processed before live RPCs
            // that arrive during the replay window; otherwise a live RPC's
            // state mutation can be overwritten by an older buffered handler
            // (re-entrant dispatch, or a future change that pumps the
            // dispatcher mid-replay, would let a live RPC interleave with
            // the replay loop and break the server-emitted ordering).
            // Queue the live RPC payload for drainage in arrival order once
            // the replay completes — RpcReplayBuffer owns the CAS guard +
            // the per-cap admission policy, see Runtime/Core/Rpc/RpcReplayBuffer.cs.
            if (_rpcReplayBuffer.IsReplayInProgress)
            {
                var enqueueResult = _rpcReplayBuffer.TryEnqueue(payload);
                // A drop here represents a lost authoritative game-state RPC
                // during replay catch-up.  The warning surface is rate-limited
                // to one emission per second per cap so a hostile peer cannot
                // turn the buffer into an unbounded log-flood primitive; the
                // cumulative count is always exposed via
                // DroppedRpcReplayBufferCount for application-level alerting.
                switch (enqueueResult)
                {
                    case RpcReplayBuffer.EnqueueResult.DroppedPayloadTooLarge:
                        if (ShouldWarn(ref _lastRpcDropPayloadWarnTicks))
                        {
                            Debug.LogWarning(
                                $"[RTMPE] Enhanced RPC: pending payload " +
                                $"{(payload != null ? payload.Length : 0)} B exceeds per-payload cap " +
                                $"{RpcReplayBuffer.MaxPayloadBytes} B; dropped. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.DroppedCumulativeTooLarge:
                        if (ShouldWarn(ref _lastRpcDropCumulativeWarnTicks))
                        {
                            Debug.LogWarning(
                                $"[RTMPE] Enhanced RPC: cumulative pending bytes would exceed " +
                                $"{RpcReplayBuffer.MaxCumulativeBytes} B; dropped. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.DroppedSlotCapReached:
                        if (ShouldWarn(ref _lastRpcDropSlotWarnTicks))
                        {
                            Debug.LogWarning(
                                "[RTMPE] Enhanced RPC: pending-during-replay queue full " +
                                $"({RpcReplayBuffer.MaxPendingDuringReplay}); dropping to bound memory. " +
                                $"Total drops this session: {_rpcReplayBuffer.DroppedCount}.");
                        }
                        return;
                    case RpcReplayBuffer.EnqueueResult.Ok:
                        return;
                }
            }

            DispatchEnhancedRpcPayload(payload);
        }

        /// <summary>
        /// Decode and dispatch a single Enhanced RPC payload.  Shared by the
        /// live-arrival path and the post-replay drain so both observe
        /// identical parsing / lookup semantics.
        /// </summary>
        private void DispatchEnhancedRpcPayload(byte[] payload)
        {
            if (!EnhancedRpcPacketParser.TryParse(payload, out var req))
            {
                LogDebug("Enhanced RPC: malformed payload, dropped.");
                return;
            }

            var nb = _spawnManager?.Registry?.Get(req.ObjectId);
            if (nb == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Enhanced RPC: no spawned object with id {req.ObjectId} — dropped.");
                return;
            }

            // The wire addresses the object, not the component: a [RtmpeRpc]
            // method may be declared on the routing anchor or on any sibling
            // NetworkBehaviour of the same GameObject.  Resolve the owning
            // component before dispatch; the anchor is kept when it owns the id,
            // when nothing owns it, or when the id is ambiguous, so the anchor's
            // existing "no [RtmpeRpc] method" diagnostic is preserved.
            NetworkBehaviour target = ResolveEnhancedRpcTarget(nb, req.MethodId);

            // Publish the gateway-attested sender id for the duration of the
            // handler so game code can authorize the call (e.g. compare against
            // LocalPlayerId).  Saved and restored around the dispatch so the
            // ambient stays correct if a handler ever synchronously triggers
            // another dispatch, and reads 0 outside any dispatch — including the
            // AllBuffered replay drain, which routes through here too.
            ulong previousRpcSenderId = CurrentRpcSenderId;
            CurrentRpcSenderId = req.SenderId;
            try
            {
                target.DispatchEnhancedRpc(req.MethodId, req.Target, req.Args);
            }
            finally
            {
                CurrentRpcSenderId = previousRpcSenderId;
            }
        }

        // Reused across inbound Enhanced RPC dispatches (which run on the main
        // thread via MainThreadDispatcher) so target resolution allocates nothing
        // per call.  Mirrors the flush-scratch pattern in NetworkManager.Lifecycle.
        private readonly List<NetworkBehaviour> _rpcTargetScratch = new List<NetworkBehaviour>(8);
        private readonly List<Type> _rpcTargetTypeScratch = new List<Type>(8);
        private HashSet<Type> _siblingRpcAdvised;

        /// <summary>
        /// Resolve the <see cref="NetworkBehaviour"/> that should receive an
        /// Enhanced RPC for <paramref name="methodId"/> on <paramref name="anchor"/>'s
        /// object.  The routing anchor keeps precedence; a uniquely-owning sibling
        /// component is selected otherwise; and the anchor is returned unchanged
        /// when it owns the method, when no component does, or when the id is
        /// ambiguous — so <see cref="NetworkBehaviour.DispatchEnhancedRpc"/> emits
        /// its existing "no [RtmpeRpc] method" warning for a genuine miss.
        ///
        /// <para><b>Not exercised by any dotnet/CI test</b> — this method lives in
        /// the Unity-only compile.  The resolution *decision* is covered by the
        /// pure <see cref="RpcRegistry.TryResolveOwningType"/> unit tests; the
        /// invariants that only manual review can guard here are: the non-allocating
        /// <c>GetComponents(List&lt;T&gt;)</c> overload (which clears the list before
        /// filling), the 1:1 index alignment between <c>_rpcTargetScratch</c> and
        /// <c>_rpcTargetTypeScratch</c>, and the Unity fake-null <c>==</c>/<c>!=</c>
        /// checks that keep a destroyed-but-not-finalised component out of dispatch.
        /// The scratch is filled and released entirely within this call, before the
        /// resolved handler runs, so a handler that re-enters the receive path
        /// cannot observe a half-built list.</para>
        /// </summary>
        private NetworkBehaviour ResolveEnhancedRpcTarget(NetworkBehaviour anchor, uint methodId)
        {
            anchor.GetComponents(_rpcTargetScratch);
            _rpcTargetTypeScratch.Clear();
            int anchorIndex = -1;
            for (int i = 0; i < _rpcTargetScratch.Count; i++)
            {
                var component = _rpcTargetScratch[i];
                // A destroyed-but-not-finalised component reads as fake-null under
                // Unity's == override; record it as a null type so the resolver
                // skips it and the index alignment with _rpcTargetScratch holds.
                _rpcTargetTypeScratch.Add(component != null ? component.GetType() : null);
                if (ReferenceEquals(component, anchor)) anchorIndex = i;
            }

            NetworkBehaviour target = anchor;
            if (RpcRegistry.TryResolveOwningType(
                    _rpcTargetTypeScratch, anchorIndex, methodId, out int idx))
            {
                var resolved = _rpcTargetScratch[idx];
                if (resolved != null && !ReferenceEquals(resolved, anchor))
                {
                    AdviseSiblingRpcOnce(resolved);
                    target = resolved;
                }
            }

            _rpcTargetScratch.Clear();
            _rpcTargetTypeScratch.Clear();
            return target;
        }

        /// <summary>
        /// Warn once per resolved sibling type that networked <em>state</em> does
        /// not follow the same rule as RPCs: an Enhanced RPC reaches a method on
        /// any component, but a <c>NetworkVariable</c> member replicates only when
        /// it lives on the object's anchor (first NetworkBehaviour).  A component
        /// carrying both an [RtmpeRpc] method and a NetworkVariable would otherwise
        /// dispatch the call yet silently never replicate the state.
        /// </summary>
        private void AdviseSiblingRpcOnce(NetworkBehaviour sibling)
        {
            Type siblingType = sibling.GetType();
            _siblingRpcAdvised ??= new HashSet<Type>();
            if (!_siblingRpcAdvised.Add(siblingType)) return;

            // "resolved to", not "dispatched to": this fires when the owning
            // component is chosen, before DispatchEnhancedRpc's audience/argument
            // gates, which may still refuse the call.
            Debug.LogWarning(
                $"[RTMPE] Enhanced RPC resolved to non-anchor component '{siblingType.Name}'. " +
                "This is supported for [RtmpeRpc] methods; note that NetworkVariable members on a " +
                "non-anchor component do NOT replicate — keep networked state on the object's first " +
                "NetworkBehaviour (the anchor).");
        }

        /// <summary>
        /// Maximum number of events accepted in a single RpcBufferReplay frame.
        /// A hostile or buggy peer can advertise <c>event_count = 0xFFFF</c>
        /// (65 535); even with the per-event truncation check, a 65 535-iteration
        /// loop on the main thread is a trivial CPU-stall primitive on slower
        /// devices.  The room service legitimately buffers at most a few hundred
        /// catch-up events, so this cap leaves ample headroom while bounding
        /// worst-case work to a fixed budget.
        /// </summary>
        internal const int MaxRpcBufferReplayEvents = 4096;

        // RPC replay state owned by RTMPE.Core.Rpc.RpcReplayBuffer.
        // The ordering barrier, the historical (buffered) and live (pending)
        // queues, the running byte counter, and the dropped-count atomic live
        // there.  The cap constants below are passthroughs so callers can
        // reference them without a direct dependency on RpcReplayBuffer.

        internal const int MaxPendingLiveRpcsDuringReplay   = RpcReplayBuffer.MaxPendingDuringReplay;
        internal const int MaxPendingLiveRpcPayloadBytes    = RpcReplayBuffer.MaxPayloadBytes;
        internal const int MaxPendingLiveRpcCumulativeBytes = RpcReplayBuffer.MaxCumulativeBytes;

        /// <summary>
        /// Wall-clock budget for a single catch-up replay drain pump, in
        /// milliseconds.  A late-join frame is already heavy (object spawns,
        /// full state sync), so a large pre-loaded RPC buffer is drained across
        /// a few frames under this budget rather than dispatched all at once —
        /// each catch-up event invokes arbitrary game code, and a peer that
        /// filled the room's buffer could otherwise stall a joining client's
        /// main thread for seconds.  At 30 Hz a frame is ~33 ms, so this slice
        /// stays well under one frame.
        /// </summary>
        internal const double ReplayDrainBudgetMillis = 4.0;

        /// <summary>
        /// Hard per-pump dispatch ceiling backing the wall-clock budget: a
        /// monotonic clock that fails to advance (or a non-positive budget)
        /// must not let one pump spin without bound.  Sized to the sum of both
        /// queue caps so a fast drain still completes in a single pump.
        /// </summary>
        internal const int MaxReplayDrainPerPump =
            MaxRpcBufferReplayEvents + RpcReplayBuffer.MaxPendingDuringReplay;

        // Monotonic clock for the drain budget and the budget precomputed in
        // Stopwatch ticks.  Stopwatch.Frequency is fixed per process, so the
        // tick budget is computed once; the delegate is cached to keep the
        // per-frame drain path allocation-free.
        private static readonly System.Func<long> ReplayDrainClock =
            System.Diagnostics.Stopwatch.GetTimestamp;
        private static readonly long ReplayDrainBudgetTicks =
            (long)(ReplayDrainBudgetMillis * System.Diagnostics.Stopwatch.Frequency / 1000.0);

        /// <summary>
        /// Handle an <c>RpcBufferReplay</c> (0x52) packet delivered immediately after joining a room.
        /// Decodes the binary replay buffer and dispatches each Enhanced RPC event as if it arrived live.
        /// </summary>
        /// <param name="payload">
        /// Binary payload: [event_count:2 LE u16][for each: [payload_len:2 LE u16][payload:N bytes]]
        /// </param>
        internal void HandleRpcBufferReplay(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                LogDebug("RpcBufferReplay: empty or truncated payload, skipped.");
                return;
            }

            // Game-data packets are valid only after a successful room join.
            // A replay frame that races ahead of the join reply belongs to this
            // joiner's catch-up stream, so — like the Spawn/Despawn catch-up —
            // stage it (a defensive copy, the receive buffer may be reused) for
            // ordered release on InRoom rather than dropping the buffered RPCs.
            // It shares the one ordered buffer so it is replayed after the spawns
            // its RPCs may target.  Nothing is dispatched while staged, so the
            // pre-room injection guard holds.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RpcBufferReplay staged until InRoom (state={_state}).");
                WarnIfEarlyObjectEvicted(
                    _earlyObjectBuffer.Stage(EarlyPacketKind.RpcReplay, (byte[])payload.Clone()));
                return;
            }

            // Raise the ordering barrier for this frame.  A second frame
            // arriving while a prior replay is still draining (it may span
            // several pumps) is dropped to avoid double-dispatching its
            // catch-up RPCs; the gateway emits exactly one replay frame per
            // join, so this only fires on a hostile/buggy retry.
            if (!_rpcReplayBuffer.TryEnterDrain())
            {
                LogDebug("RpcBufferReplay: replay already in progress, dropping concurrent frame.");
                return;
            }

            try
            {
                int offset = 0;
                ushort eventCount = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                offset += 2;

                if (eventCount > MaxRpcBufferReplayEvents)
                {
                    LogDebug(
                        $"RpcBufferReplay: event_count {eventCount} exceeds cap " +
                        $"{MaxRpcBufferReplayEvents}; rejecting frame to bound main-thread work.");
                    return;
                }

                // Decode each catch-up event and queue it in server-emitted
                // order.  Dispatch is deferred to DrainReplayQueue so the
                // historical events share one bounded, resumable drain with any
                // live RPCs that arrive during the window — both run in the
                // correct order (buffered before live) without freezing the
                // main thread when the buffer is large.  Per-event parse,
                // registry lookup, and audience checks happen at dispatch time
                // inside DispatchEnhancedRpcPayload.
                for (int i = 0; i < eventCount; i++)
                {
                    if (offset + 2 > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: truncated at event {i}/{eventCount}, stopping decode.");
                        break;
                    }
                    ushort payloadLen = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                    offset += 2;

                    if (offset + payloadLen > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: event {i} payload truncated ({payloadLen} bytes), stopping decode.");
                        break;
                    }

                    var eventPayload = new byte[payloadLen];
                    Array.Copy(payload, offset, eventPayload, 0, payloadLen);
                    offset += payloadLen;

                    _rpcReplayBuffer.EnqueueBuffered(eventPayload);
                }

                // Dispatch as much as this frame's time budget allows.
                // DrainReplayQueue lowers the ordering barrier once the queues
                // are empty; otherwise the per-frame Update continuation
                // finishes the remainder in order.
                DrainReplayQueue();
            }
            finally
            {
                // On the normal path DrainReplayQueue has already lowered the
                // barrier if everything drained.  If an exception escaped the
                // decode, any events queued so far stay enqueued and the Update
                // continuation will deliver them — release the barrier only
                // when nothing is outstanding, so a thrown decode can never
                // strand it raised forever.
                if (!_rpcReplayBuffer.HasPendingWork)
                    _rpcReplayBuffer.ExitDrain();
            }
        }

        /// <summary>
        /// Drain queued catch-up RPCs — historical (buffered) first, then live
        /// (pending) — within this frame's wall-clock budget.  Invoked from
        /// <see cref="HandleRpcBufferReplay"/> when a replay frame arrives and
        /// again each frame from <c>Update</c> while work remains, so a large
        /// catch-up buffer is delivered in order across a few frames instead of
        /// stalling the main thread in one.
        /// </summary>
        private void DrainReplayQueue()
        {
            // The cached delegate is bound in Start(); the method-group fallback
            // only materialises before Start has run (e.g. an EditMode test that
            // invokes the receive path directly) and never allocates in
            // production, where Start always precedes the first inbound packet.
            _rpcReplayBuffer.DrainBounded(
                _drainReplayDispatch ?? SafeDispatchReplayPayload,
                MaxReplayDrainPerPump,
                ReplayDrainBudgetTicks,
                ReplayDrainClock);
        }

        /// <summary>
        /// Dispatch one drained catch-up payload, isolating a throwing
        /// <c>[RtmpeRpc]</c> handler so a single bad event cannot abort the
        /// rest of the drain.  Bound once to <see cref="_drainReplayDispatch"/>
        /// so the per-frame drain pump stays allocation-free.
        /// </summary>
        private void SafeDispatchReplayPayload(byte[] payload)
        {
            try
            {
                DispatchEnhancedRpcPayload(payload);
            }
            catch (Exception ex)
            {
                LogDebug(
                    "RpcBufferReplay: drain dispatch threw: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an inbound <c>RpcResponse</c> (0x51) from the server.
        /// Routes ownership grant responses to the OwnershipManager.
        /// </summary>
        private void OnRpcResponse(byte[] data)
        {
            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RPC response rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (!RpcPacketParser.TryParseResponse(payload, out var response))
            {
                LogDebug("RPC response: malformed payload, dropped.");
                return;
            }

            // Awaiters registered via SendEnhancedRpcAsync take precedence
            // over the per-method dispatch table: a response with a known
            // request_id corresponds to a server-targeted Enhanced RPC, and
            // the awaiter consumes the structured result regardless of
            // method id.  Returns false when no awaiter is bound, in which
            // case the method-id switch below handles legacy SDK-internal
            // flows (e.g. TransferOwnership grant frames).
            if (TryCompleteServerRpc(response))
                return;

            switch (response.MethodId)
            {
                case RpcMethodId.TransferOwnership:
                    HandleOwnershipTransferResponse(response);
                    break;
                default:
                    if (IsDebugLogEnabled)
                        LogDebug($"RPC response: unhandled method_id {response.MethodId}.");
                    break;
            }
        }

        /// <summary>
        /// Process a server-broadcast TransferOwnership RPC that tells this client
        /// to apply an ownership change (server-authoritative grant).
        /// Payload: [object_id:8 LE u64][new_owner_len:2 LE u16][new_owner:N UTF-8].
        /// </summary>
        /// <remarks>
        /// Authorisation policy applied here is defence-in-depth against a
        /// peer that captures and re-emits an authentic grant frame (the
        /// AEAD tag survives replay; the anti-replay window catches the
        /// exact-counter case but a peer in the same room can also forge a
        /// fresh-counter packet through a compromised gateway).  The client
        /// rejects the grant unless one of the following holds:
        ///   • the object's current owner is empty (initial assignment), or
        ///   • the wire-supplied <c>senderId</c> equals the local player's
        ///     gateway session ID and the local player currently owns the
        ///     object (we requested this transfer ourselves), or
        ///   • the new-owner string is a recognised member of the current
        ///     room roster AND the local player is the room master client
        ///     (host-authorised reassignment).
        /// The room-membership cross-check additionally guarantees the
        /// new-owner string is not arbitrary attacker-supplied bytes.
        /// </remarks>
        // Ownership-transfer RPC logic lives in RTMPE.Core.Rpc.OwnershipTransfer.
        // The four-path authorisation predicate is reviewable in isolation;
        // these instance methods are thin passthroughs onto that class.

        private void HandleOwnershipTransferRpc(RpcRequest request)
            => RTMPE.Core.Rpc.OwnershipTransfer.HandleRpc(
                request,
                _spawnManager,
                _localPlayerId,
                _localPlayerStringId,
                IsMasterClient,
                _roomManager);

        /// <summary>
        /// Test-visible passthrough onto
        /// <see cref="RTMPE.Core.Rpc.OwnershipTransfer.IsAuthorized"/>.  Existing
        /// fixtures (Tier0SecurityTests) call this through the NetworkManager
        /// instance; preserving the signature keeps the test surface stable.
        /// </summary>
        internal bool IsOwnershipTransferAuthorized(
            ulong objectId, string newOwner, ulong senderId)
            => RTMPE.Core.Rpc.OwnershipTransfer.IsAuthorized(
                objectId, newOwner, senderId,
                _spawnManager,
                _localPlayerId,
                _localPlayerStringId,
                IsMasterClient,
                _roomManager);

        private void HandleOwnershipTransferResponse(RpcResponse response)
            => RTMPE.Core.Rpc.OwnershipTransfer.HandleResponse(response, _spawnManager);

        /// <summary>RoomManager fires OnRoomCreated → transition to InRoom.</summary>
        private void OnRoomManagerCreated(RoomInfo room)
        {
            RememberRoom(room);
            RecordRoomEvent($"Room created: {room?.RoomId ?? "?"}");
            // Size the inbound flood budget to the room's capacity: peer-to-peer
            // fan-out scales with member count, so a fixed cap would drop
            // legitimate state in a large room.
            _inboundBudget.ConfigureForRoomSize(room.MaxPlayers);

            // Enter the room context so inbound state is accepted while the host
            // seat is established by the join that always follows a create — the
            // AutoJoinAsHost round-trip by default, or the caller's explicit
            // JoinRoom in the two-step flow.  The obsolete OnJoinedRoom signal is
            // left to that join, whose handler (OnRoomManagerJoined) is its single
            // source; raising it here as well would deliver two "joined" callbacks
            // for one room entry.  The modern split is OnRoomCreated (raised by
            // RoomManager) followed by OnRoomJoined.
            if (_state == NetworkState.Connected)
                TransitionTo(NetworkState.InRoom);
        }

        /// <summary>RoomManager fires OnRoomJoined → transition to InRoom.</summary>
        private void OnRoomManagerJoined(RoomInfo room)
        {
            RememberRoom(room);
            RecordRoomEvent($"Joined room: {room?.RoomId ?? "?"}");
            // Size the inbound flood budget to the room's capacity: peer-to-peer
            // fan-out scales with member count, so a fixed cap would drop
            // legitimate state in a large room.
            _inboundBudget.ConfigureForRoomSize(room.MaxPlayers);

            // Drive the state machine when we are arriving at InRoom from a
            // not-yet-in-a-room state (Connected on first join; Reconnecting
            // → Connected → InRoom on a fresh connect).  When auto-rejoin
            // fires after a quick disconnect/reconnect the state may already
            // be InRoom by the time RoomManager.OnRoomJoined is raised; the
            // transition itself is a no-op in that case, but the public
            // OnJoinedRoom event MUST still fire so application code that
            // gates spawn / UI work on it observes the rejoin.  Firing the
            // event unconditionally on InRoom arrival makes the contract
            // independent of how the state machine got us here.
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
            }

            if (_state == NetworkState.InRoom)
            {
#pragma warning disable CS0618
                SafeRaise(OnJoinedRoom, 0UL, nameof(OnJoinedRoom));
#pragma warning restore CS0618
            }
        }

        /// <summary>RoomManager fires OnRoomLeft → transition back to Connected.</summary>
        private void OnRoomManagerLeft()
        {
            RecordRoomEvent("Left room");
            // Explicit leave = user wants out of this room; clear the
            // last-room snapshot so a subsequent Reconnect() does NOT auto-rejoin.
            _lastRoomId   = null;
            _lastRoomCode = null;
            // Peer fan-out ceases on leave; restore the pre-room flood budget.
            _inboundBudget.ResetToDefault();
            if (_state == NetworkState.InRoom)
            {
                // Room leave keeps the transport session, so preserve the
                // per-session object-id counter: a rejoin must not re-issue ids
                // the room still holds under a despawn tombstone.
                _spawnManager?.ClearAll(resetObjectIdSpace: false);
                TransitionTo(NetworkState.Connected);
#pragma warning disable CS0618
                SafeRaise(OnLeftRoom, 0UL, nameof(OnLeftRoom));
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// Remember the currently-joined room so <see cref="Reconnect"/> can
        /// auto-rejoin it after a token-preserving disconnect.  A null or
        /// empty room argument clears the snapshot (defensive — the room
        /// parsers already return empty strings rather than null IDs).
        /// </summary>
        private void RememberRoom(RoomInfo room)
        {
            if (room == null || string.IsNullOrEmpty(room.RoomId))
            {
                _lastRoomId   = null;
                _lastRoomCode = null;
                return;
            }
            _lastRoomId   = room.RoomId;
            _lastRoomCode = room.RoomCode;
        }

        private void OnServerDisconnect(byte[] data)
        {
            // Reject Disconnect packets that arrive before SessionAck has
            // promoted the session to "established".  During key derivation
            // the receive path is already accepting AEAD-decrypted frames
            // (the session keys exist), but the application-visible session
            // is not yet live; tearing down now would let a forged or
            // mistimed Disconnect interrupt an in-progress handshake and
            // strand the client in Disconnecting/Disconnected with no
            // session to recover.  Leave the in-flight handshake undisturbed.
            if (!_sessionEstablished)
            {
                Debug.LogWarning(
                    "[RTMPE] Ignoring Disconnect received before session establishment — " +
                    "handshake is still in progress; will not tear down session keys.");
                return;
            }
            // Wire format: optional 1-byte reason discriminator at payload[0].
            // Empty payload = legacy gateway → fall back to ServerRequest so
            // old gateways continue to work unchanged.
            var payload = PacketParser.ExtractPayload(data);
            DisconnectReason reason = payload.Length >= 1
                ? MapWireDisconnectReason(payload[0])
                : DisconnectReason.ServerRequest;

            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, reason);
        }

        // Wire-format byte → enum mapping for the gateway's typed Disconnect.
        // Byte values mirror the underlying enum ordinals; see
        // modules/gateway/src/packet/mod.rs::disconnect_reason for the
        // authoritative wire contract.  Unknown values fall back to
        // ServerRequest so a forward-compatible gateway that adds new
        // reason codes does not crash older SDK builds.
        private static DisconnectReason MapWireDisconnectReason(byte wire)
        {
            switch (wire)
            {
                case 0x00: return DisconnectReason.Unknown;
                case 0x02: return DisconnectReason.ServerRequest;
                case 0x05: return DisconnectReason.Kicked;
                case 0x07: return DisconnectReason.ProtocolError;
                default:   return DisconnectReason.ServerRequest;
            }
        }

        // ── Transport error path ───────────────────────────────────────────────

        private void HandleTransportError(Exception ex)
        {
            RtmpeLog.Error($"[RTMPE] Transport error: {ex.Message}");

            _dispatcher?.Enqueue(() =>
            {
                bool wasConnecting = _state == NetworkState.Connecting;

                if (_timeoutCoroutine != null)
                {
                    StopCoroutine(_timeoutCoroutine);
                    _timeoutCoroutine = null;
                }
                if (_connectCoroutine != null)
                {
                    StopCoroutine(_connectCoroutine);
                    _connectCoroutine = null;
                }

                if (wasConnecting)
                    SafeRaise(OnConnectionFailed, ex.Message, nameof(OnConnectionFailed));

                _networkThread?.Stop();
                _heartbeatManager?.Stop();
                ClearSessionData();
                TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
            });
        }

        // ── State machine ──────────────────────────────────────────────────────

        private void TransitionTo(
            NetworkState   next,
            DisconnectReason reason = DisconnectReason.Unknown)
        {
            var prev = _state;
            if (prev == next) return;

            // Clear the session-established witness BEFORE the state assignment
            // and event raise so observers (OnDisconnected callbacks) cannot
            // observe a "Disconnected with _sessionEstablished == true"
            // inconsistent snapshot.  Reconnecting is intentionally retained
            // because the existing session keys remain in use until the new
            // SessionAck either confirms the migration or replaces them.
            if (next == NetworkState.Disconnected || next == NetworkState.Disconnecting)
                _sessionEstablished = false;

            _state = next;
            LogDebug($"State: {prev} \u2192 {next}");
            SafeRaise(OnStateChanged, prev, next, nameof(OnStateChanged));

            switch (next)
            {
                case NetworkState.Connected:
                    SafeRaise(OnConnected, nameof(OnConnected));
                    break;

                case NetworkState.Disconnected when prev != NetworkState.Disconnected:
                    SafeRaise(OnDisconnected, reason, nameof(OnDisconnected));
                    break;
            }

            // Entering a room opens the game-data gate: release any lifecycle
            // packets staged ahead of the join reply, after OnStateChanged so
            // observers learn they are InRoom before the catch-up objects
            // surface.  Every other transition discards the staging buffer so a
            // stale object set can never bleed across a room boundary or an
            // abandoned join attempt.
            if (next == NetworkState.InRoom)
                FlushEarlyObjectPackets();
            else
                _earlyObjectBuffer.Clear();
        }

    }
}
