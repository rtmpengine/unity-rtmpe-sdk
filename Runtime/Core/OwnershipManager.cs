// RTMPE SDK — Runtime/Core/OwnershipManager.cs
//
// Manages network object ownership.
//
// Security contract (unchanged from plan, hardened implementation):
//  • Only the SERVER can GRANT ownership transfers.
//  • Clients REQUEST a transfer; the server validates and confirms.
//  • Local state ONLY changes via ApplyOwnershipGrant(), called from
//    the packet handler after the server confirms.
//
// Design notes:
//  • RequestOwnershipTransfer() sends TransferOwnership RPC (Method ID 200)
//    to the server. The server validates and broadcasts OwnershipGrant to all
//    clients. Local state ONLY changes via ApplyOwnershipGrant() (server-authoritative).

using System;
using System.Collections.Generic;
using UnityEngine;
using RTMPE.Rpc;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages ownership of <see cref="NetworkBehaviour"/> objects.
    /// Access via <c>SpawnManager.Ownership</c>.
    /// All methods must be called from the Unity main thread.
    /// </summary>
    public sealed class OwnershipManager
    {
        private readonly NetworkObjectRegistry _registry;
        private readonly NetworkManager _networkManager;

        // Outstanding ownership-transfer correlation IDs.  An attacker who can
        // observe a session's traffic could otherwise predict the next id from
        // a plain monotonic counter and race a forged response into the open
        // correlation window before the genuine reply lands.  IDs are now
        // drawn from RequestIdAllocator (CSPRNG-backed); HandleOwnershipTransferResponse
        // refuses any id we did not issue, and unanswered ids are pruned after
        // OutstandingRequestTtlMs to bound memory and the spoofing surface.
        private readonly HashSet<uint> _outstanding = new HashSet<uint>();
        private readonly Dictionary<uint, long> _outstandingDeadlineMs = new Dictionary<uint, long>(16);

        // Ten seconds matches the worst-case RTT + server processing budget for
        // an ownership-transfer round trip.  Beyond that the response, if it
        // ever arrives, is too stale to be the original request's reply.
        internal const long OutstandingRequestTtlMs = 10_000;

        /// <summary>
        /// Create an OwnershipManager.
        /// </summary>
        /// <param name="registry">The shared object registry.</param>
        /// <param name="networkManager">
        /// The active NetworkManager; used to send RPC packets.
        /// </param>
        public OwnershipManager(NetworkObjectRegistry registry, NetworkManager networkManager)
        {
            _registry       = registry       ?? throw new ArgumentNullException(nameof(registry));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        }

        // ── Queries ────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns all currently registered objects whose owner matches
        /// <paramref name="playerId"/>.
        /// </summary>
        /// <param name="playerId">Room player UUID to query.</param>
        public IReadOnlyList<NetworkBehaviour> GetObjectsOwnedBy(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return Array.Empty<NetworkBehaviour>();

            var result = new List<NetworkBehaviour>();
            foreach (var obj in _registry.GetAll())
            {
                // Unity null check guards destroyed-but-not-unregistered objects.
                if (obj != null && obj.OwnerPlayerId == playerId)
                    result.Add(obj);
            }
            return result;
        }

        // ── Mutations ──────────────────────────────────────────────────────────

        /// <summary>
        /// Request an ownership transfer to <paramref name="newOwnerPlayerId"/>.
        ///
       /// Sends a TransferOwnership RPC (method_id = 200) to the server.
        /// The server validates and, if approved, broadcasts an OwnershipGrant
        /// to all clients. Local state is NOT mutated — only the server can grant
        /// ownership via <see cref="ApplyOwnershipGrant"/>.
        /// </summary>
        /// <param name="objectId">Network object ID to transfer.</param>
        /// <param name="newOwnerPlayerId">Target player's room UUID.</param>
        public void RequestOwnershipTransfer(ulong objectId, string newOwnerPlayerId)
        {
            var obj = _registry.Get(objectId);
            if (obj == null)
            {
                Debug.LogWarning($"[OwnershipManager] Object {objectId} not found in registry.");
                return;
            }

            if (!obj.IsOwner)
            {
                Debug.LogError(
                    $"[OwnershipManager] Cannot request ownership transfer for object {objectId}: " +
                    $"local player is not the current owner.");
                return;
            }

            if (string.IsNullOrEmpty(newOwnerPlayerId))
            {
                Debug.LogError("[OwnershipManager] newOwnerPlayerId must not be null or empty.");
                return;
            }

            if (!_networkManager.IsConnected)
            {
                Debug.LogWarning("[OwnershipManager] Cannot send ownership transfer: not connected.");
                return;
            }

            // CSPRNG-backed correlation id; rerolls if it would collide with
            // any already-outstanding request.  Tracking the issued id lets
            // HandleOwnershipTransferResponse drop forged responses whose
            // request_id we never sent.
            uint requestId = AllocateOutstandingRequestId();

            var rpcPayload = RpcPacketBuilder.BuildTransferOwnership(
                _networkManager.LocalPlayerId,
                requestId,
                objectId,
                newOwnerPlayerId);

            var packet = _networkManager.BuildPacket(
                PacketType.Rpc, PacketFlags.Reliable, rpcPayload);
            _networkManager.Send(packet, reliable: true);
        }

        /// <summary>
        /// Validate an inbound ownership-transfer response against the set of
        /// request ids the local SDK actually sent.  Returns true when the id
        /// matches an outstanding request (which is then closed); false when
        /// it does not — meaning the response is stale, duplicated, or forged.
        /// </summary>
        internal bool TryAcknowledgeResponse(uint requestId)
        {
            PruneExpiredOutstanding();
            if (_outstanding.Remove(requestId))
            {
                _outstandingDeadlineMs.Remove(requestId);
                return true;
            }
            // Redacted: only the action is logged.  The id and remote endpoint
            // are intentionally withheld from the message body to avoid
            // teaching an attacker which forgery attempts succeeded in landing.
            RtmpeLog.Warning(
                "[OwnershipManager] Dropped ownership-transfer response: unknown or expired request id.");
            return false;
        }

        /// <summary>
        /// Sweep stale outstanding ids.  Called from the RPC packet handler
        /// every entry, and also exposed for the periodic Tick path so a long
        /// quiescent session does not leak entries beyond the TTL.
        /// </summary>
        internal void PruneExpiredOutstanding()
        {
            if (_outstandingDeadlineMs.Count == 0) return;
            long nowMs = NowMs();
            List<uint> stale = null;
            foreach (var kv in _outstandingDeadlineMs)
            {
                if (kv.Value <= nowMs)
                {
                    if (stale == null) stale = new List<uint>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (var id in stale)
                {
                    _outstanding.Remove(id);
                    _outstandingDeadlineMs.Remove(id);
                }
            }
        }

        private uint AllocateOutstandingRequestId()
        {
            // RequestIdAllocator.Next is CSPRNG-backed but does NOT know about
            // this manager's pending set; reroll up to a small bound to ensure
            // the chosen id is not already outstanding here.
            uint id;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                id = RequestIdAllocator.Next();
                if (id != 0 && !_outstanding.Contains(id))
                {
                    _outstanding.Add(id);
                    _outstandingDeadlineMs[id] = NowMs() + OutstandingRequestTtlMs;
                    return id;
                }
            }
            // Saturation fallback: still register what the allocator gave us;
            // an in-flight collision drops the older entry's TTL when its
            // deadline lapses, so the system self-heals.
            id = RequestIdAllocator.Next();
            if (id == 0) id = 1u;
            _outstanding.Add(id);
            _outstandingDeadlineMs[id] = NowMs() + OutstandingRequestTtlMs;
            return id;
        }

        private static long NowMs()
        {
            // Stopwatch-based monotonic clock survives wall-time adjustments.
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            return ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>
        /// Test seam: clear the outstanding set without firing callbacks.
        /// </summary>
        internal void ResetOutstandingForTest()
        {
            _outstanding.Clear();
            _outstandingDeadlineMs.Clear();
        }

        /// <summary>
        /// Test seam: number of outstanding ownership-transfer requests.
        /// </summary>
        internal int OutstandingCount => _outstanding.Count;

        /// <summary>
        /// Apply a server-confirmed ownership grant.
        ///
       /// Called by the packet handler when the server broadcasts an
        /// OwnershipGrant (or OwnershipTransfer RPC response).
        /// This is the ONLY place where local ownership state changes.
        /// </summary>
        /// <param name="objectId">Network object whose ownership changed.</param>
        /// <param name="newOwnerPlayerId">Room UUID of the new owner.</param>
        public void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId)
        {
            var obj = _registry.Get(objectId);
            if (obj == null)
            {
                Debug.LogWarning(
                    $"[OwnershipManager] ApplyOwnershipGrant: object {objectId} not found.");
                return;
            }

            obj.SetOwner(newOwnerPlayerId);
        }
    }
}
