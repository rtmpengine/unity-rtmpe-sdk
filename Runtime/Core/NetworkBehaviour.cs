// RTMPE SDK — Runtime/Core/NetworkBehaviour.cs
//
// Base class for all networked GameObjects in RTMPE.
//
// Design decisions (Week 15):
//   • _ownerPlayerId is a string (UUID) to match PlayerInfo.PlayerId and the
//     room service player identifiers.  The gateway session ID (u64) is a
//     DIFFERENT concept stored in NetworkManager.LocalPlayerId.
//   • IsOwner compares string UUIDs and short-circuits on null/empty so that
//     uninitialized objects never falsely claim ownership (unlike a ulong==0
//     comparison which would return true for every uninitialized object).
//   • Initialize / SetSpawned / SetOwner are internal so only the RTMPE SDK
//     itself (SpawnManager, Week 16) can mutate network object state.
//     RTMPE.SDK.Tests can also call them via InternalsVisibleTo (AssemblyInfo.cs).
//   • DestroyWithOwner is declared here; enforcement is implemented by
//     SpawnManager in Week 16 when it handles PlayerLeft events.
//   • IsOwner accesses NetworkManager.Instance — safe for main-thread MonoBehaviour
//     code (OnNetworkSpawn, OnOwnershipChanged, etc. all run on main thread).

using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Base class for all RTMPE-networked components.
    /// Attach to a <c>GameObject</c> that will be spawned across the network via
    /// <c>SpawnManager</c> (Week 16).
    /// </summary>
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        // ── State ──────────────────────────────────────────────────────────────

        private ulong  _networkObjectId;
        private string _ownerPlayerId = string.Empty;
        private bool   _isSpawned;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>
        /// Server-assigned unique ID for this networked object.
        /// Zero before <see cref="Initialize"/> is called.
        /// </summary>
        public ulong NetworkObjectId => _networkObjectId;

        /// <summary>
        /// The room-level UUID of the player who owns this object.
        /// Matches <see cref="PlayerInfo.PlayerId"/> from the Rooms API.
        /// Empty string before <see cref="Initialize"/> is called.
        /// </summary>
        public string OwnerPlayerId => _ownerPlayerId;

        /// <summary>
        /// True when this object is owned by the local player.
        ///
        /// Compares <see cref="OwnerPlayerId"/> against
        /// <see cref="NetworkManager.LocalPlayerStringId"/> (the room UUID set by
        /// <c>RoomManager</c> after JoinRoom/CreateRoom succeeds).
        ///
        /// Returns <see langword="false"/> when either ID is null or empty,
        /// preventing false-positive ownership on uninitialized objects.
        /// </summary>
        public bool IsOwner
        {
            get
            {
                if (string.IsNullOrEmpty(_ownerPlayerId)) return false;
                var localId = NetworkManager.Instance?.LocalPlayerStringId;
                return !string.IsNullOrEmpty(localId) && _ownerPlayerId == localId;
            }
        }

        /// <summary>
        /// True while this object is live on the network (after
        /// <see cref="OnNetworkSpawn"/> and before <see cref="OnNetworkDespawn"/>).
        /// </summary>
        public bool IsSpawned => _isSpawned;

        /// <summary>
        /// When <see langword="true"/>, this object is automatically despawned
        /// when its owner leaves the room.
        /// Enforcement is performed by <c>SpawnManager</c> (Week 16).
        /// </summary>
        public bool DestroyWithOwner { get; set; } = true;

        // ── Overridable callbacks ──────────────────────────────────────────────

        /// <summary>
        /// Called on all clients when this object is spawned on the network.
        /// Safe to read <see cref="IsOwner"/> here.
        /// Override in a subclass — do not call directly; use <c>SetSpawned(true)</c>.
        /// </summary>
        protected virtual void OnNetworkSpawn() { }

        /// <summary>
        /// Called on all clients when this object is removed from the network.
        /// Override in a subclass — do not call directly; use <c>SetSpawned(false)</c>.
        /// </summary>
        protected virtual void OnNetworkDespawn() { }

        /// <summary>
        /// Called when ownership of this object transfers to a different player.
        /// Only fires when the owner actually changes (same-value calls are suppressed).
        /// Override in a subclass — do not call directly; use <c>SetOwner()</c>.
        /// </summary>
        /// <param name="previousOwner">Player UUID of the previous owner.</param>
        /// <param name="newOwner">Player UUID of the new owner.</param>
        protected virtual void OnOwnershipChanged(string previousOwner, string newOwner) { }

        // ── Internal SDK API (called by SpawnManager in Week 16) ──────────────

        /// <summary>
        /// Initialise the network identity of this object.
        /// Called by <c>SpawnManager</c> immediately after instantiation.
        /// </summary>
        /// <param name="objectId">Server-assigned unique object ID (u64).</param>
        /// <param name="ownerId">Room player UUID of the object's owner.</param>
        internal void Initialize(ulong objectId, string ownerId)
        {
            // M-3 guard: warn on double-initialisation (e.g. duplicate Spawn packet via KCP retransmit).
            if (_networkObjectId != 0)
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour.Initialize called twice on object " +
                    $"{_networkObjectId} → overwriting with {objectId}. " +
                    "Possible duplicate Spawn packet or SpawnManager bug.");

            _networkObjectId = objectId;
            _ownerPlayerId   = ownerId ?? string.Empty;
        }

        /// <summary>
        /// Transition the spawn state of this object.
        /// Fires <see cref="OnNetworkSpawn"/> or <see cref="OnNetworkDespawn"/> as needed.
        /// Idempotent: calling <c>SetSpawned(true)</c> twice only fires the callback once.
        /// </summary>
        internal void SetSpawned(bool spawned)
        {
            if (spawned && !_isSpawned)
            {
                _isSpawned = true;
                OnNetworkSpawn();
            }
            else if (!spawned && _isSpawned)
            {
                _isSpawned = false;
                OnNetworkDespawn();
            }
        }

        /// <summary>
        /// Apply a server-confirmed ownership change.
        /// Only call from <c>OwnershipManager.ApplyOwnershipGrant</c>.
        /// </summary>
        /// <param name="newOwner">Room player UUID of the new owner.</param>
        internal void SetOwner(string newOwner)
        {
            // H-2 fix: suppress no-change callbacks (server retransmit safety).
            var normalized = newOwner ?? string.Empty;
            if (_ownerPlayerId == normalized) return;

            var previous   = _ownerPlayerId;
            _ownerPlayerId = normalized;
            OnOwnershipChanged(previous, _ownerPlayerId);
        }
    }
}
