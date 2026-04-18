// RTMPE SDK — Runtime/Core/OwnershipManager.cs
//
// Manages network object ownership.
//
// Security contract (unchanged from plan, hardened implementation):
//   • Only the SERVER can GRANT ownership transfers.
//   • Clients REQUEST a transfer; the server validates and confirms.
//   • Local state ONLY changes via ApplyOwnershipGrant(), called from
//     the packet handler after the server confirms.
//
// Design notes:
//   • RequestOwnershipTransfer() sends TransferOwnership RPC (Method ID 200)
//     to the server. The server validates and broadcasts OwnershipGrant to all
//     clients. Local state ONLY changes via ApplyOwnershipGrant() (server-authoritative).

using System;
using System.Collections.Generic;
using System.Threading;
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

        // Monotonic counter for RPC request correlation IDs.
        private int _nextRequestId;

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

            uint requestId = (uint)Interlocked.Increment(ref _nextRequestId);
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
