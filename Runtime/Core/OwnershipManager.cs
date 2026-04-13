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
// Week 15 / Week 17 notes:
//   • RequestOwnershipTransfer() is a STUB. Actual transmission is deferred
//     to Week 17 when the RPC system (Method ID 200) is available.
//     See Plan/week-17-rpc.md for the wire format and IPC routing convention.
//   • The _networkManager field is RESERVED for the Week 17 implementation;
//     it is stored but not yet used for sending.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages ownership of <see cref="NetworkBehaviour"/> objects.
    /// Access via <c>SpawnManager.Ownership</c> (Week 16).
    /// All methods must be called from the Unity main thread.
    /// </summary>
    public sealed class OwnershipManager
    {
        private readonly NetworkObjectRegistry _registry;

        // Reserved for Week 17 RPC dispatch. Not read until RequestOwnershipTransfer
        // is fully implemented. Stored now so the constructor signature is stable
        // across weeks and callers don't need to change when Week 17 lands.
#pragma warning disable IDE0052  // private field assigned but never read (by design — Week 17)
        private readonly NetworkManager _networkManager;
#pragma warning restore IDE0052

        /// <summary>
        /// Create an OwnershipManager.
        /// </summary>
        /// <param name="registry">The shared object registry.</param>
        /// <param name="networkManager">
        /// The active NetworkManager; used in Week 17 to send RPC packets.
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
        /// <b>STUB — not yet implemented.</b>
        /// Full implementation requires the Week 17 RPC system (Method ID 200).
        /// See <c>Plan/week-17-rpc.md</c> for the wire format.
        ///
        /// Current behaviour: validates guards and logs a warning. No packet is sent.
        /// Local state is NOT mutated (server-authoritative ownership is preserved).
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

            // TODO Week 17: Transmit via RPC system.
            //   method_id = 200 (TransferOwnership), RequiresOwner = true.
            //   See Plan/week-17-rpc.md — IPC Message Routing Convention.
            //   var packet = RpcPacketBuilder.BuildServerRpc(
            //       methodId: 200, payload: TransferOwnershipPayload(objectId, newOwnerPlayerId));
            //   _networkManager.Send(packet, reliable: true);
            Debug.LogWarning(
                $"[OwnershipManager] RequestOwnershipTransfer for object {objectId} is not yet " +
                $"transmitted. Ownership transfer requires the Week 17 RPC system (method_id=200).");
        }

        /// <summary>
        /// Apply a server-confirmed ownership grant.
        ///
        /// Called by the packet handler when the server broadcasts an
        /// OwnershipGrant (or OwnershipTransfer RPC response in Week 17).
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
