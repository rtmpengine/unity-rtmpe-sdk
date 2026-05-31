// RTMPE SDK — Runtime/Core/NetworkManager.GameData.cs
//
// StateSync + Variable update + RPC send paths + ApplyDamage RPC + BuildPacket.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
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
        // ── State-sync inbound handler ────────────────────────────────

        /// <summary>
        /// Route incoming <c>StateSync</c>/<c>Data</c> server broadcasts to
        /// the appropriate sync component on the matching spawned object.
        ///
       /// <para>Dispatch priority:</para>
        /// <list type="number">
        ///  <item><see cref="TransformPacketParser"/> — handles transform deltas (changed_mask bits 0x01–0x07 only).</item>
        ///  <item><see cref="PhysicsPacketParser.IsPhysics2D"/> — handles 2-D Rigidbody2D packets (bit 0x80 set).</item>
        ///  <item><see cref="PhysicsPacketParser.IsPhysics3D"/> — handles 3-D Rigidbody packets (bit 0x40 set, bit 0x80 clear).</item>
        /// </list>
        ///
       /// Subscribed to <see cref="OnDataReceived"/> in <see cref="InitialiseNetwork"/>.
        /// </summary>
        private void HandleStateSyncPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"StateSync packet rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length < 9) return;

            // ── 1a. Try quantized transform parse ─────────────────────────────
            // The 25-byte quantized layout encodes [flags|object_id|3×half|smallest3|3×half].
            // Detect it BEFORE the legacy delta parser — TryParseStateDelta would
            // otherwise mis-read object_id from bytes 0..7 (which include the
            // flags byte) and then mask-check against byte 8 (the high byte of
            // the real object_id in the quantized layout), silently dropping
            // every quantized update.
            if (payload.Length == TransformPacketParser.QUANTIZED_UPDATE_SIZE
                && (payload[0] & TransformPacketParser.FLAG_QUANTIZED) != 0)
            {
                if (TransformPacketParser.TryParseQuantizedUpdate(
                        payload, out ulong qObjectId, out TransformState qState))
                {
                    var qNb = _spawnManager.Registry.Get(qObjectId);
                    if (qNb == null) return;

                    // Receive-side interest filter — mirrors the legacy delta
                    // path.  The quantized payload always carries position, so
                    // the incoming-vs-live-transform fallback collapses to the
                    // packet position unconditionally.
                    if (!qNb.IsOwner && InterestManager.IsReceiveFilterActive)
                    {
                        var (lh1, lh2) = InterestManager.LocalPosition;
                        Vector3 objPos = qState.Position;
                        float dx  = objPos.x - lh1;
                        float dh2 = (InterestManager.LocalUsesXzPlane ? objPos.z : objPos.y) - lh2;
                        if (!InterestManager.ShouldDeliver(qObjectId, dx * dx + dh2 * dh2)) return;
                    }

                    if (qNb.IsOwner)
                    {
                        // Receive hot-path uses the cached NetworkBehaviour
                        // accessor: at 30 Hz × N peers, GetComponent<T> per
                        // packet adds up to a measurable slice of the frame
                        // budget on mobile / IL2CPP builds.
                        qNb.CachedNetworkTransform?.ApplyReconciliation(qState);
                        return;
                    }

                    var qInterp = qNb.CachedNetworkTransformInterpolator;
                    if (qInterp == null) return;
                    qInterp.AddState(qState, UnityEngine.Time.timeAsDouble);
                    return;
                }
                // Malformed quantized payload — drop without falling through to
                // the legacy parser, whose offsets do not match this layout.
                if (IsDebugLogEnabled)
                    LogDebug("StateSync: malformed quantized payload, dropped.");
                return;
            }

            // ── 1b. Physics dispatch (mutually exclusive with StateDelta) ────
            // Physics frames set discriminator bits in byte 8 (bit 0x80 for
            // 2-D, bit 0x40 for 3-D) outside the StateDelta KnownMask of 0x07.
            // Dispatching them BEFORE the StateDelta iteration removes the
            // ambiguity that would otherwise arise on the very first parse
            // attempt — the StateDelta parser would reject a physics frame on
            // the unknown-bit guard, but that rejection would also abort the
            // remainder of any concatenated batch even when the inputs were
            // legitimate.  The discriminator byte is the same token both
            // sides peek at, so the dispatch agrees with `IsPhysics2D` /
            // `IsPhysics3D` by construction.
            if (PhysicsPacketParser.IsPhysics2D(payload))
            {
                HandlePhysicsSync2DPacket(payload);
                return;
            }
            if (PhysicsPacketParser.IsPhysics3D(payload))
            {
                HandlePhysicsSyncPacket(payload);
                return;
            }

            // ── 2. StateDelta iteration (single record OR concatenated batch) ─
            // The Sync Service's `BroadcastSyncFrame` (`.delta` subject)
            // concatenates one serialised `StateDelta` per changed object into
            // a single `PacketType.StateSync` frame; one-object rooms produce
            // a single record, multi-object rooms produce N records back to
            // back.  The loop below dispatches each record to its registered
            // NetworkObject, advancing through the buffer until exhausted.  A
            // single malformed record terminates the loop so the remainder
            // of a poisoned batch cannot drift into a misaligned read of a
            // later well-formed record.
            int cursor = 0;
            int recordsApplied = 0;
            while (cursor < payload.Length)
            {
                if (!TransformPacketParser.TryParseStateDeltaAt(
                        payload, ref cursor,
                        out ulong objectId,
                        out byte changedMask,
                        out TransformState state))
                {
                    if (recordsApplied == 0 && IsDebugLogEnabled)
                        LogDebug("StateSync: no StateDelta record could be parsed from payload.");
                    return;
                }
                ApplyStateDeltaToObject(objectId, changedMask, state);
                recordsApplied++;
            }
        }

        /// <summary>
        /// Apply a single decoded <c>StateDelta</c> to its target
        /// <see cref="NetworkObjectRegistry"/> entry.  Encapsulates the
        /// per-record interest filter, blended-state construction, and
        /// owner-vs-remote dispatch so the multi-delta iteration in
        /// <see cref="HandleStateSyncPacket"/> stays linear.
        /// </summary>
        private void ApplyStateDeltaToObject(
            ulong objectId,
            byte changedMask,
            TransformState state)
        {
            var nb = _spawnManager.Registry.Get(objectId);
            if (nb == null) return;

            // ── Receive-side interest filter ──────────────────────────────
            // When an InterestManager is active with a non-zero radius,
            // discard state updates for objects outside that radius.
            // The owning client's objects are always processed regardless of
            // distance (the owner needs reconciliation data).
            // This is a secondary client-side guard; the gateway already
            // performs the primary spatial cull before sending the packet.
            //
            // Filter against the INCOMING position when the packet carries
            // one — falling back to the live transform only when the delta
            // omits a position field.  Filtering against transform.position
            // alone would lag one tick behind: a fast-moving object entering
            // the radius would be discarded for one tick before we accept it.
            if (!nb.IsOwner && InterestManager.IsReceiveFilterActive)
            {
                var (lh1, lh2) = InterestManager.LocalPosition;
                Vector3 objPos;
                if ((changedMask & TransformPacketParser.ChangedPosition) != 0)
                    objPos = state.Position;
                else
                    objPos = nb.transform.position;
                // Pick the matching horizontal axis: XZ for 3-D games
                // (default), XY for 2-D / top-down games.  Without this
                // dispatch the filter compares an XY-stored local position
                // against the unused vertical axis of the remote object
                // and silently rejects every packet for top-down games.
                float dx = objPos.x - lh1;
                float dh2 = (InterestManager.LocalUsesXzPlane ? objPos.z : objPos.y) - lh2;
                if (!InterestManager.ShouldDeliver(objectId, dx * dx + dh2 * dh2)) return;
            }

            // Build a blended state: merge only the fields present in the delta.
            // Fields absent from the delta keep zero-initialised values in `state`
            // which would clobber the current transform — fall back to the live
            // transform for those axes.
            //
            // Resolve the cached NetworkTransform once so both the
            // delta-merge below and the owner reconciliation branch
            // share a single field load.
            var cachedNetTransform = nb.CachedNetworkTransform;
            var current = cachedNetTransform != null
                ? cachedNetTransform.GetState()
                : new TransformState
                {
                    Position = nb.transform.position,
                    Rotation = nb.transform.rotation,
                    Scale    = nb.transform.localScale,
                };
            var blended = new TransformState
            {
                Position = (changedMask & TransformPacketParser.ChangedPosition) != 0
                               ? state.Position : current.Position,
                Rotation = (changedMask & TransformPacketParser.ChangedRotation) != 0
                               ? state.Rotation : current.Rotation,
                Scale    = (changedMask & TransformPacketParser.ChangedScale) != 0
                               ? state.Scale : current.Scale,
            };

            if (nb.IsOwner)
            {
                cachedNetTransform?.ApplyReconciliation(blended);
                return;
            }

            var interp = nb.CachedNetworkTransformInterpolator;
            if (interp == null) return;
            interp.AddState(blended, UnityEngine.Time.timeAsDouble);
        }

        /// <summary>
        /// Route an inbound 3-D physics-sync payload to the
        /// <see cref="NetworkRigidbody"/> component on the matching object.
        /// </summary>
        private void HandlePhysicsSyncPacket(byte[] payload)
        {
            if (!PhysicsPacketParser.TryParsePhysicsState(
                    payload, out ulong objectId, out byte changedMask, out PhysicsState state))
                return;

            var nb = _spawnManager?.Registry.Get(objectId);
            if (nb == null) return;

            // Receive-side interest filter — mirrors the transform-packet filter in
            // HandleStateSyncPacket.  Owners receive reconciliation unconditionally;
            // non-owners are dropped when the object lies outside the interest radius.
            if (!nb.IsOwner && InterestManager.IsReceiveFilterActive)
            {
                var (lh1, lh2) = InterestManager.LocalPosition;
                Vector3 objPos = (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                                 ? state.Position
                                 : nb.transform.position;
                float dx  = objPos.x - lh1;
                float dh2 = (InterestManager.LocalUsesXzPlane ? objPos.z : objPos.y) - lh2;
                if (!InterestManager.ShouldDeliver(objectId, dx * dx + dh2 * dh2)) return;
            }

            // Resolve the cached component once; both branches read it.
            var cachedNetRb = nb.CachedNetworkRigidbody;
            if (nb.IsOwner)
            {
                cachedNetRb?.ApplyReconciliation(state, changedMask);
                return;
            }

            cachedNetRb?.ApplyRemoteState(state, changedMask);
        }

        /// <summary>
        /// Route an inbound 2-D physics-sync payload to the
        /// <see cref="NetworkRigidbody2D"/> component on the matching object.
        /// </summary>
        private void HandlePhysicsSync2DPacket(byte[] payload)
        {
            if (!PhysicsPacketParser.TryParsePhysicsState2D(
                    payload, out ulong objectId, out byte changedMask, out PhysicsState2D state))
                return;

            var nb = _spawnManager?.Registry.Get(objectId);
            if (nb == null) return;

            // Receive-side interest filter — 2-D variant.
            // PhysicsState2D.Position is Vector2 (x/y world plane), which aligns with
            // InterestManager when UseXzPlane == false (standard for 2-D games).
            if (!nb.IsOwner && InterestManager.IsReceiveFilterActive)
            {
                var (lh1, lh2) = InterestManager.LocalPosition;
                float ox = (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                           ? state.Position.x : nb.transform.position.x;
                float oy = (changedMask & PhysicsPacketBuilder.ChangedPosition) != 0
                           ? state.Position.y : nb.transform.position.y;
                float dx  = ox - lh1;
                float dh2 = oy - lh2;
                if (!InterestManager.ShouldDeliver(objectId, dx * dx + dh2 * dh2)) return;
            }

            // Resolve the cached component once; both branches read it.
            var cachedNetRb2D = nb.CachedNetworkRigidbody2D;
            if (nb.IsOwner)
            {
                cachedNetRb2D?.ApplyReconciliation(state, changedMask);
                return;
            }

            cachedNetRb2D?.ApplyRemoteState(state, changedMask);
        }

        // ── Variable update inbound handler ────────────────────────────

        /// <summary>
        /// Apply an inbound <c>VariableUpdate</c> (0x41) packet from the server
        /// to the matching spawned object's NetworkVariables.
        /// Payload: <c>[object_id:8 LE][tick:4 LE][var_count:1][for each: [var_id:2 LE][value_len:2 LE][value_bytes:N]]</c>
        ///
        /// The 4-byte tick is the sender's LocalTick at flush time.  Each
        /// variable on the receiver maintains its own last-applied-tick
        /// watermark and rejects updates whose tick is not strictly greater
        /// (RFC 1982 modular comparison) so a re-ordered datagram cannot
        /// roll the value back.
        /// </summary>
        private void HandleVariableUpdatePacket(byte[] data)
        {
            if (_spawnManager == null) return;

            // Game-data packets are valid only after a successful room join;
            // rejecting earlier traffic prevents pre-room state injection.
            if (_state != NetworkState.InRoom)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"VariableUpdate packet rejected; not in a room (state={_state}).");
                return;
            }

            var payload = PacketParser.ExtractPayload(data);
            // Minimum: object_id(8) + tick(4) + var_count(1) = 13 bytes.
            if (payload == null || payload.Length < 13) return;

            // Wire protocol is little-endian.  `BitConverter.ToUInt64` is
            // platform-endian — see `HandleOwnershipTransferRpc` for the same
            // correctness rationale.  Decode explicitly LE so the behaviour
            // matches the gateway on every target architecture.
            ulong objectId =
                  (ulong)payload[0]
                | ((ulong)payload[1] << 8)
                | ((ulong)payload[2] << 16)
                | ((ulong)payload[3] << 24)
                | ((ulong)payload[4] << 32)
                | ((ulong)payload[5] << 40)
                | ((ulong)payload[6] << 48)
                | ((ulong)payload[7] << 56);
            uint packetTick =
                  (uint)payload[8]
                | ((uint)payload[9]  << 8)
                | ((uint)payload[10] << 16)
                | ((uint)payload[11] << 24);
            int varCount = payload[12];
            if (varCount == 0) return;

            var nb = _spawnManager.Registry.Get(objectId);
            if (nb == null) return;

            try
            {
                using var ms     = new System.IO.MemoryStream(payload, 13, payload.Length - 13);
                using var reader = new System.IO.BinaryReader(ms);

                // Per-packet upper bound on cumulative deserialized variable
                // bytes.  The wire format permits varCount=255 × valueLen=65535
                // (16 MiB of main-thread work per datagram); this cap ensures a
                // single hostile datagram cannot stall the dispatcher.  The cap
                // is generous relative to legitimate payloads (a typical
                // batch flushes well under 4 KiB) and matches the safe-message
                // ceiling used by the gateway.
                const int MaxVariableUpdateCumulativeBytes = 64 * 1024;
                int cumulativeBytes = 0;

                for (int i = 0; i < varCount; i++)
                {
                    // Wire format: [var_id:2 LE][value_len:2 LE][value_bytes:N]
                    // Read value_len before dispatching to ApplyVariableUpdate.
                    // If the var_id is unknown, advance the reader by value_len bytes
                    // so subsequent variables in this packet are parsed correctly.
                    if (ms.Length - ms.Position < 4) break; // need var_id(2) + value_len(2)
                    ushort varId    = reader.ReadUInt16();
                    ushort valueLen = reader.ReadUInt16();

                    if (ms.Length - ms.Position < valueLen) break; // truncated packet

                    cumulativeBytes += 4 + valueLen;
                    if (cumulativeBytes > MaxVariableUpdateCumulativeBytes)
                    {
                        if (ShouldWarn(ref _lastVariableUpdateTrailingWarnTicks))
                            Debug.LogWarning(
                                "[RTMPE] VariableUpdate: cumulative deserialized " +
                                $"bytes exceeded {MaxVariableUpdateCumulativeBytes} for objectId " +
                                $"{objectId}; rejecting packet to bound main-thread work.");
                        return;
                    }

                    long valueStart = ms.Position;
                    nb.ApplyVariableUpdate(varId, reader, valueLen, packetTick, hasPacketTick: true);

                    // Ensure the reader is positioned exactly after value_bytes,
                    // even if ApplyVariableUpdate consumed fewer or more bytes
                    // (or skipped the value entirely on a stale-tick rejection).
                    ms.Position = valueStart + valueLen;
                }

                // Strict trailing-bytes check.  A well-formed VariableUpdate
                // batch ends exactly at the last variable's value_bytes; any
                // residue indicates either a sender bug or a deliberate
                // amplification attempt.  Surface it via a rate-limited
                // warning instead of silently ignoring — silent tolerance
                // hides protocol drift across versions and lets an attacker
                // smuggle bytes through without raising any signal.
                if (ms.Position != ms.Length)
                {
                    if (ShouldWarn(ref _lastVariableUpdateTrailingWarnTicks))
                        Debug.LogWarning(
                            "[RTMPE] VariableUpdate: " +
                            $"{ms.Length - ms.Position} trailing byte(s) after " +
                            $"{varCount} declared variable(s) for objectId {objectId}; " +
                            "rejecting packet to prevent protocol-drift smuggling.");
                    return;
                }
            }
            catch (Exception ex)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"VariableUpdate: parse error for objectId {objectId}: {ex.Message}");
            }
        }

        private long _lastVariableUpdateTrailingWarnTicks;

        // ── Variable update send path ─────────────────────────────────

        /// <summary>
        /// Build and enqueue a <c>VariableUpdate</c> (0x41) packet.
        /// Called by <see cref="NetworkBehaviour.FlushDirtyVariables"/> for each
        /// owned object that has dirty NetworkVariables.
        /// </summary>
        internal void SendVariableUpdate(byte[] payload)
            => SendVariableUpdate(payload, payload?.Length ?? 0);

        /// <summary>
        /// Pooled-buffer overload of <see cref="SendVariableUpdate(byte[])"/>.
        /// Wraps <paramref name="payloadLength"/> bytes from <paramref name="payload"/>
        /// — accepts an oversized buffer (e.g. rented from <c>ArrayPool&lt;byte&gt;.Shared</c>)
        /// and uses only the leading <paramref name="payloadLength"/> bytes.
        /// </summary>
        internal void SendVariableUpdate(byte[] payload, int payloadLength)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payloadLength <= 0) return;

            var packet = _packetBuilder.Build(
                PacketType.VariableUpdate,
                PacketFlags.Reliable,
                payload, payloadLength);

            // Hand the packet to the reliable send path so it is registered
            // in the ReliableChannel retransmit table and re-emitted on RTO
            // expiry until the gateway acknowledges it.  When the ARQ wire
            // extension is not negotiated the same call degrades to a single
            // best-effort transmission, matching the historical semantics.
            Send(packet, reliable: true);
        }

        /// <summary>
        /// Build and enqueue a coalesced VariableBatchUpdate (0x44) packet.
        /// Invoked from the per-tick variable flush when
        /// <c>NetworkSettings.enableVariableBatching</c> is true; the batch
        /// payload was built by <see cref="VariableBatchBuilder.Build"/>.
        /// </summary>
        internal void SendVariableBatchUpdate(byte[] payload)
            => SendVariableBatchUpdate(payload, payload?.Length ?? 0);

        /// <summary>
        /// Pooled-buffer overload of <see cref="SendVariableBatchUpdate(byte[])"/>.
        /// </summary>
        internal void SendVariableBatchUpdate(byte[] payload, int payloadLength)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payloadLength <= 0) return;

            var packet = _packetBuilder.Build(
                PacketType.VariableBatchUpdate,
                PacketFlags.Reliable,
                payload, payloadLength);

            // Coalesced variable batches share the per-object update's
            // delivery contract: route through the reliable send path for
            // retransmit-table registration, degrading to best-effort when
            // the ARQ wire extension is not negotiated.
            Send(packet, reliable: true);
        }

        // ── Position update send path (Feature #6 — Interest Management) ───

        /// <summary>
        /// Build and enqueue a <c>PositionUpdate</c> (0x42) packet carrying the
        /// client's 2-D world position so the gateway can apply zone-based
        /// interest filtering to room-wide broadcasts.
        ///
       /// <para>Call this from <see cref="RTMPE.Rooms.InterestManager"/> at the
        /// configured update interval while in a room.  Sending outside a room is
        /// a no-op (the gateway has no room context to filter against).</para>
        ///
       /// <para>Payload layout: <c>[x: f32 LE 4 B][y: f32 LE 4 B]</c> — 8 bytes.</para>
        /// </summary>
        internal void SendPositionUpdate(float x, float y)
        {
            if (_networkThread == null || _packetBuilder == null) return;

            // Sender-side finiteness gate.  The gateway parser rejects any
            // NaN/±Inf component as a malformed transform, tearing the
            // channel down and forcing the client into a full reconnect.
            // Surfacing the misuse at the call boundary lets the caller's
            // own controller logic fail with a clear exception instead of
            // silently corrupting the session.  Matches InputPayload.WriteTo.
            if (float.IsNaN(x) || float.IsInfinity(x))
                throw new InvalidOperationException("SendPositionUpdate: x is not finite");
            if (float.IsNaN(y) || float.IsInfinity(y))
                throw new InvalidOperationException("SendPositionUpdate: y is not finite");

            // Use SingleToInt32Bits + explicit byte extraction (same pattern as
            // TransformPacketBuilder.WriteF32LE) to avoid the two temporary byte[]
            // allocations that BitConverter.GetBytes(float) causes per call.
            var payload = new byte[8];
            int xBits = BitConverter.SingleToInt32Bits(x);
            int yBits = BitConverter.SingleToInt32Bits(y);
            payload[0] = (byte) xBits;
            payload[1] = (byte)(xBits >>  8);
            payload[2] = (byte)(xBits >> 16);
            payload[3] = (byte)(xBits >> 24);
            payload[4] = (byte) yBits;
            payload[5] = (byte)(yBits >>  8);
            payload[6] = (byte)(yBits >> 16);
            payload[7] = (byte)(yBits >> 24);

            var packet = _packetBuilder.Build(PacketType.PositionUpdate, PacketFlags.None, payload);
            EncryptAndSend(packet);
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by RoomManager when JoinRoom/CreateRoom succeeds and the server
        /// confirms the local player's room UUID. This is the identifier used by
        /// <see cref="NetworkBehaviour.IsOwner"/> for object ownership comparisons.
        /// </summary>
        /// <summary>
        /// Build and enqueue an RPC request packet for transmission.
        /// Convenience wrapper for game code that does not need
        /// the raw <see cref="BuildPacket"/> / <see cref="Send"/> split.
        /// The packet is built with <see cref="PacketFlags.Reliable"/> so the
        /// KCP layer will retransmit on loss.
        /// </summary>
        /// <param name="methodId">RPC method ID (see <see cref="RpcMethodId"/>).</param>
        /// <param name="rpcPayload">Method-specific payload bytes (max 4096 bytes).</param>
        public void SendRpc(uint methodId, byte[] rpcPayload)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendRpc: cannot send while not connected.");
                return;
            }

            // Source the correlation ID from the CSPRNG-backed allocator so a
            // network attacker cannot predict or race in-flight request IDs.
            uint requestId = RequestIdAllocator.Next();
#pragma warning disable CS0618  // intentional: built-in method IDs still use the legacy builder
            byte[] rpcMessage = RpcPacketBuilder.BuildRequest(methodId, LocalPlayerId, requestId, rpcPayload);
#pragma warning restore CS0618
            byte[] packet     = BuildPacket(PacketType.Rpc, PacketFlags.Reliable, rpcMessage);
            Send(packet, reliable: true);
        }

        /// <summary>
        /// Build and enqueue an Enhanced RPC request for a
        /// <see cref="RtmpeRpcAttribute"/>-decorated method on a
        /// <see cref="NetworkBehaviour"/> component.
        ///
       /// <para>Called internally by <see cref="NetworkBehaviour.RPC"/>. Game code
        /// should not call this directly — use <c>NetworkBehaviour.RPC()</c> instead.</para>
        /// </summary>
        /// <param name="sender">The <c>NetworkBehaviour</c> originating the call.</param>
        /// <param name="methodName">Name of the <c>[RtmpeRpc]</c>-decorated method.</param>
        /// <param name="args">Typed arguments (must be serializable by <see cref="RpcSerializer"/>).</param>
        public void SendEnhancedRpc(NetworkBehaviour sender, string methodName, object[] args)
        {
            if (!IsInRoom)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendEnhancedRpc: must be in a room.");
                return;
            }

            if (sender == null)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendEnhancedRpc: sender is null.");
                return;
            }

            if (!RpcRegistry.TryGetMethodId(sender.GetType(), methodName, out uint methodId))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkManager.SendEnhancedRpc: no [RtmpeRpc] method named " +
                    $"'{methodName}' on {sender.GetType().Name}. Ensure the method is public " +
                    "and decorated with [RtmpeRpc].");
                return;
            }

            // Read target from the attribute so callers do not pass it explicitly.
            RpcRegistry.TryFindMethod(sender.GetType(), methodId, out _, out var attr);
            var target = attr?.Target ?? RpcTarget.All;

            // CSPRNG-backed correlation ID; see RequestIdAllocator.
            uint requestId = RequestIdAllocator.Next();

            byte[] rpcPayload;
            try
            {
                rpcPayload = EnhancedRpcPacketBuilder.Build(
                    methodId, LocalPlayerId, requestId,
                    sender.NetworkObjectId, target, args);
            }
            catch (Exception ex)
            {
                RtmpeLog.Error(
                    $"[RTMPE] NetworkManager.SendEnhancedRpc: failed to build packet for " +
                    $"'{sender.GetType().Name}.{methodName}': {ex.Message}");
                return;
            }

            byte[] packet = BuildPacket(
                PacketType.Rpc,
                PacketFlags.Reliable | PacketFlags.EnhancedRpc,
                rpcPayload);
            Send(packet, reliable: true);
        }

        /// <summary>
        /// Handle a server-broadcast <c>ApplyDamage</c> (301) RPC.
        /// Payload: <c>[object_id:8 LE u64][damage:4 LE i32]</c>.
        /// Looks up the target <see cref="NetworkBehaviour"/> by object ID,
        /// retrieves its <see cref="IDamageable"/> component (if any), and
        /// calls <see cref="IDamageable.ReceiveApplyDamage"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Sample-grade handler retained for backward compatibility.</b>
        /// This method ships in the SDK runtime because removing it would
        /// break any game that currently relies on the gateway-emitted 301
        /// ApplyDamage RPC. It is, however, fundamentally a sample of how
        /// to bind a server-authoritative damage event to a game's local
        /// health system — the parsing of <c>(object_id, damage)</c>, the
        /// <see cref="IDamageable"/> lookup, and the
        /// <see cref="IDamageable.ReceiveApplyDamage"/> dispatch can all
        /// live in game code.</para>
        ///
        /// <para><b>Recommended pattern for new projects:</b> use a custom
        /// <c>[RtmpeRpc]</c> method on a <see cref="NetworkBehaviour"/>
        /// subclass instead. The Enhanced RPC system handles routing /
        /// authorisation / replay buffering uniformly; reserving the 301
        /// method-id for SDK use was a pre-Enhanced-RPC ergonomic
        /// shortcut that the Enhanced framework subsumes.</para>
        ///
        /// <para>This handler is kept active rather than wrapped in
        /// <see cref="ObsoleteAttribute"/> because it is invoked
        /// reflectively from the RPC dispatch table, not by user code —
        /// the deprecation warning would never fire at the call site that
        /// matters. The architectural status is documented here so a
        /// future cleanup pass can remove it once a sample-project
        /// replacement ships.</para>
        /// </remarks>
        private void HandleApplyDamageRpc(RpcRequest request)
        {
            var p = request.Payload;
            if (p == null || p.Length < 12)
            {
                LogDebug("ApplyDamage RPC: payload too short, dropped.");
                return;
            }

            ulong objectId = (ulong)p[0]       | ((ulong)p[1] << 8)  | ((ulong)p[2] << 16) |
                             ((ulong)p[3] << 24)| ((ulong)p[4] << 32) | ((ulong)p[5] << 40) |
                             ((ulong)p[6] << 48)| ((ulong)p[7] << 56);
            int damage = p[8] | (p[9] << 8) | (p[10] << 16) | (p[11] << 24);

            if (damage <= 0)
            {
                LogDebug("ApplyDamage RPC: non-positive damage, dropped.");
                return;
            }

            var nb = Spawner?.Registry?.Get(objectId);
            if (nb == null)
            {
                if (IsDebugLogEnabled)
                    LogDebug($"ApplyDamage RPC: no object with id {objectId}.");
                return;
            }

            // Look up the IDamageable interface on the target object.
            // IDamageable lives in the SDK Runtime assembly; game code (e.g. HealthController)
            // implements it. This avoids a compile-time dependency on Samples.
            var damageable = nb.GetComponentInParent<IDamageable>();
            if (damageable != null)
                damageable.ReceiveApplyDamage(damage);
            else if (IsDebugLogEnabled)
                LogDebug($"ApplyDamage RPC: object {objectId} has no IDamageable component.");
        }

        internal void SetLocalRoomPlayerId(string playerId)
        {
            _localPlayerStringId = playerId;
            LogDebug($"LocalRoomPlayerId set to: {playerId}");
        }

        /// <summary>
        /// Test-only helper to directly set <see cref="LocalPlayerStringId"/>.
        /// Accessible from <c>RTMPE.SDK.Tests</c> via <c>InternalsVisibleTo</c>.
        /// Do NOT call from production code.
        /// </summary>
        internal void SetLocalPlayerStringId(string id) => _localPlayerStringId = id;

        /// <summary>
        /// Wrap <paramref name="payload"/> in a <see cref="PacketType.Data"/> header
        /// and enqueue it on the network thread for transmission.
        ///
       /// Called by <c>NetworkTransform</c> and any other SDK component
        /// that needs to send a raw data payload without managing the PacketBuilder
        /// directly.  Must be called from the Unity main thread.
        /// </summary>
        /// <param name="payload">
        /// The serialised payload bytes.  A <see langword="null"/> or empty array
        /// is silently ignored.
        /// </param>
        internal void SendData(byte[] payload)
            => SendData(payload, payload?.Length ?? 0);

        /// <summary>
        /// Pooled-buffer overload of <see cref="SendData(byte[])"/>.
        /// </summary>
        internal void SendData(byte[] payload, int payloadLength)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payloadLength <= 0) return;

            var packet = _packetBuilder.Build(
                PacketType.Data,
                PacketFlags.None,
                payload, payloadLength);

            EncryptAndSend(packet);
        }

        /// <summary>
        /// Wrap <paramref name="payload"/> in a <see cref="PacketType.InputPayload"/>
        /// header (0x43) and transmit it as an unreliable UDP packet.
        ///
       /// <para>Called by <see cref="RTMPE.Sync.NetworkTransform"/> once per
        /// 30 Hz tick to ship the unacknowledged-input ring buffer to the Sync
        /// Service for server-authoritative simulation.  Built by
        /// <see cref="RTMPE.Sync.InputPacketBuilder.BuildBatchPayload"/>.</para>
        ///
       /// <para>Player identity is intentionally NOT in the payload — the
        /// gateway resolves session_id → authoritative player_id and embeds
        /// both in the NATS envelope before the Sync Service ever sees the
        /// bytes.  This eliminates the client-spoofing surface that would
        /// exist if a client could stamp any player_id on its inputs.</para>
        ///
       /// <para>Unreliable on purpose: the next batch supersedes the prior
        /// (the buffer holds every unacknowledged frame), so a dropped
        /// packet costs at most one tick of latency until the next send.</para>
        /// </summary>
        /// <param name="payload">
        /// Wire payload built by <c>InputPacketBuilder.BuildBatchPayload</c>.
        /// A <see langword="null"/> or empty array is silently ignored.
        /// </param>
        internal void SendInput(byte[] payload)
            => SendInput(payload, payload?.Length ?? 0);

        /// <summary>
        /// Pooled-buffer overload of <see cref="SendInput(byte[])"/>.
        /// </summary>
        internal void SendInput(byte[] payload, int payloadLength)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payloadLength <= 0) return;

            var packet = _packetBuilder.Build(
                PacketType.InputPayload,
                PacketFlags.None,
                payload, payloadLength);

            EncryptAndSend(packet);
        }

        /// <summary>
        /// Wrap <paramref name="payload"/> in a <see cref="PacketType.StateSync"/> header
        /// and transmit it as an unreliable UDP packet.
        ///
       /// <para>Called by <see cref="RTMPE.Sync.NetworkRigidbody"/> and
        /// <see cref="RTMPE.Sync.NetworkRigidbody2D"/> to send physics-state updates.
        /// StateSync packets flow through the Sync Engine which aggregates and
        /// rebroadcasts them to all room members at the 30 Hz tick rate.</para>
        ///
       /// <para>Sending as StateSync rather than Data means the Sync Engine
        /// processes the payload as object state, applying interest-zone filtering
        /// and dead-client pruning before the broadcast.</para>
        /// </summary>
        /// <param name="payload">
        /// Physics-state payload built by <see cref="RTMPE.Sync.PhysicsPacketBuilder"/>.
        /// A <see langword="null"/> or empty array is silently ignored.
        /// </param>
        internal void SendStateSync(byte[] payload)
            => SendStateSync(payload, payload?.Length ?? 0);

        /// <summary>
        /// Pooled-buffer overload of <see cref="SendStateSync(byte[])"/>.
        /// </summary>
        internal void SendStateSync(byte[] payload, int payloadLength)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payloadLength <= 0) return;

            var packet = _packetBuilder.Build(
                PacketType.StateSync,
                PacketFlags.None,
                payload, payloadLength);

            EncryptAndSend(packet);
        }

        /// <summary>
        /// Build a complete wire packet (13-byte header + payload) using the
        /// connection's shared <see cref="PacketBuilder"/>. Sequence numbers are
        /// atomically assigned so the gateway sees a monotonic counter regardless
        /// of which SDK component originates the packet.
        ///
       /// Called by SpawnManager, OwnershipManager, and any other SDK component
        /// that needs to build a typed packet for transmission via <see cref="Send"/>.
        /// </summary>
        internal byte[] BuildPacket(PacketType type, PacketFlags flags, byte[] payload)
        {
            if (_packetBuilder == null)
                throw new InvalidOperationException(
                    "NetworkManager.BuildPacket: no active PacketBuilder (not connected).");
            return _packetBuilder.Build(type, flags, payload);
        }

    }
}
