// RTMPE SDK — Runtime/Core/NetworkManager.ReceivePath.cs
//
// ProcessPacket dispatch + inbound handlers + transport error path + state machine.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
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
            Debug.LogWarning("[RTMPE] Heartbeat timeout — 3 consecutive misses. Disconnecting.");
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
                var json = System.Text.Encoding.UTF8.GetString(payload);
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
                var json = System.Text.Encoding.UTF8.GetString(payload);
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

        /// <summary>
        /// Handle an inbound <c>Spawn</c> (0x30) packet from the server.
        /// Parses the payload and calls <see cref="SpawnManager.CreateLocal"/>
        /// to instantiate the object on the receiving client.
        /// </summary>
        private void OnSpawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Spawn packet rejected; not in a room (state={_state}).");
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

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"Despawn packet rejected; not in a room (state={_state}).");
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

            nb.DispatchEnhancedRpc(req.MethodId, req.Args);
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
        // The CAS re-entry guard, the pending-live queue, the running byte
        // counter, and the dropped-count atomic live there.
        // The cap constants below are passthroughs so callers can reference
        // them without a direct dependency on RpcReplayBuffer.

        internal const int MaxPendingLiveRpcsDuringReplay   = RpcReplayBuffer.MaxPendingDuringReplay;
        internal const int MaxPendingLiveRpcPayloadBytes    = RpcReplayBuffer.MaxPayloadBytes;
        internal const int MaxPendingLiveRpcCumulativeBytes = RpcReplayBuffer.MaxCumulativeBytes;

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

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"RpcBufferReplay rejected; not in a room (state={_state}).");
                return;
            }

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

                for (int i = 0; i < eventCount; i++)
                {
                    if (offset + 2 > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: truncated at event {i}/{eventCount}, aborting replay.");
                        return;
                    }
                    ushort payloadLen = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                    offset += 2;

                    if (offset + payloadLen > payload.Length)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: event {i} payload truncated ({payloadLen} bytes), aborting.");
                        return;
                    }

                    var eventPayload = new byte[payloadLen];
                    Array.Copy(payload, offset, eventPayload, 0, payloadLen);
                    offset += payloadLen;

                    if (!EnhancedRpcPacketParser.TryParse(eventPayload, out var request))
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: failed to parse event {i}, skipped.");
                        continue;
                    }

                    var nb = _spawnManager?.Registry?.Get(request.ObjectId);
                    if (nb == null)
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"RpcBufferReplay: no spawned object {request.ObjectId} for event {i}, skipped.");
                        continue;
                    }

                    // DispatchEnhancedRpc consults the per-type RpcRegistry; an unknown
                    // methodId or mismatched argument count is rejected inside the
                    // registry's TryInvoke gate without throwing back to this loop.
                    // We still wrap defensively so a single buggy [RtmpeRpc] handler
                    // cannot abort the rest of the replay.
                    try
                    {
                        nb.DispatchEnhancedRpc(request.MethodId, request.Args);
                    }
                    catch (Exception ex)
                    {
                        LogDebug(
                            $"RpcBufferReplay: dispatch threw for method {request.MethodId} on " +
                            $"object {request.ObjectId}: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            finally
            {
                // Drain inside the flag window so re-entrant inbound packets
                // continue to queue rather than bypass the FIFO.  If a queued
                // RPC's [RtmpeRpc] handler synchronously dispatches another
                // inbound packet, the OnEnhancedRpcRequest fast path observes
                // _replayInProgress = 1 and appends the new payload to the
                // tail of _pendingLiveRpcs — exactly the desired arrival
                // ordering.  Clearing the flag first would let the nested
                // dispatch run immediately and stomp on the remaining queued
                // items, breaking FIFO.
                try
                {
                    // Bounded outer loop.  A drained payload's [RtmpeRpc]
                    // handler may synchronously dispatch more inbound packets
                    // through OnEnhancedRpcRequest, which (with
                    // _replayInProgress still set) re-enqueues onto
                    // _pendingLiveRpcs.  We re-check the queue after the inner
                    // drain finishes so those late arrivals are not stranded
                    // until the next replay.  The outer iteration count is
                    // capped at 4 to prevent a pathological handler from
                    // turning the drain into an infinite loop.
                    const int MaxDrainIterations = 4;
                    for (int iter = 0; iter < MaxDrainIterations; iter++)
                    {
                        if (_rpcReplayBuffer.PendingCount == 0)
                            break;

                        // Snapshot the count for the inner loop so a handler
                        // that synchronously re-enqueues during dispatch
                        // cannot turn this loop into an infinite drain.  Late
                        // arrivals are picked up on the next outer iteration
                        // (and the outer iteration cap is the final ceiling).
                        int innerBudget = _rpcReplayBuffer.PendingCount;
                        for (int j = 0; j < innerBudget; j++)
                        {
                            if (!_rpcReplayBuffer.TryDequeue(out var pending)) break;
                            try
                            {
                                DispatchEnhancedRpcPayload(pending);
                            }
                            catch (Exception ex)
                            {
                                LogDebug(
                                    "RpcBufferReplay: post-replay drain dispatch threw: " +
                                    $"{ex.GetType().Name}: {ex.Message}");
                            }
                        }
                    }

                    if (_rpcReplayBuffer.PendingCount > 0)
                    {
                        LogDebug(
                            "RpcBufferReplay: drain iteration cap reached with " +
                            $"{_rpcReplayBuffer.PendingCount} payload(s) still queued; " +
                            "remaining items will be dispatched on the next live RPC.");
                    }
                }
                finally
                {
                    // The flag clear is the very last act so any payload
                    // enqueued mid-drain has already been picked up by the
                    // surrounding loop's Count check.  ExitDrain calls
                    // Interlocked.Exchange under the hood so the producer-side
                    // Volatile.Read sees the flag clear with full release
                    // semantics — the producer-side Volatile.Read sees the
                    // flag clear with full release ordering.
                    _rpcReplayBuffer.ExitDrain();
                }
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
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
#pragma warning disable CS0618 // Legacy event — users should migrate to RoomManager events
                SafeRaise(OnJoinedRoom, 0UL, nameof(OnJoinedRoom));
#pragma warning restore CS0618
            }
        }

        /// <summary>RoomManager fires OnRoomJoined → transition to InRoom.</summary>
        private void OnRoomManagerJoined(RoomInfo room)
        {
            RememberRoom(room);

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
            // Explicit leave = user wants out of this room; clear the
            // last-room snapshot so a subsequent Reconnect() does NOT auto-rejoin.
            _lastRoomId   = null;
            _lastRoomCode = null;
            if (_state == NetworkState.InRoom)
            {
                _spawnManager?.ClearAll();  // Destroy all spawned objects on room leave
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
        }

    }
}
