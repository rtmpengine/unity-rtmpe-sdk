// RTMPE SDK — Runtime/Core/NetworkObjectRegistry.cs
//
// Central registry of all live networked objects.
//
// Design decisions:
//  • All methods are main-thread only (Unity objects must be accessed from
//    the main thread). The lock is retained for defensive safety in case of
//    future async operations, but callers should treat this as single-threaded.
//  • Get() performs a Unity null check (op_Equality override) to detect
//    destroyed GameObjects and auto-evicts them, preventing stale references.
//  • Clear() despawns all objects before clearing so that OnNetworkDespawn()
//    fires and _isSpawned is set to false for every registered object.
//    Despawning happens OUTSIDE the lock to prevent re-entrance deadlocks if
//    an OnNetworkDespawn callback calls registry methods.
//  • GetAll() returns a defensive snapshot (IReadOnlyList) so callers
//    iterating the list can't observe concurrent modifications.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Registry for all spawned <see cref="NetworkBehaviour"/> objects.
    /// One instance per <c>SpawnManager</c>; cleared on room leave.
    /// </summary>
    public sealed class NetworkObjectRegistry
    {
        private readonly Dictionary<ulong, NetworkBehaviour> _objects =
            new Dictionary<ulong, NetworkBehaviour>();

        private readonly object _lock = new object();

        // ── Mutation ───────────────────────────────────────────────────────────

        /// <summary>
        /// Register a newly spawned object.
        /// If a <em>different</em> object is already registered under the same
        /// <see cref="NetworkBehaviour.NetworkObjectId"/>, that object is despawned
        /// before being evicted so <see cref="NetworkBehaviour.OnNetworkDespawn"/> fires
        /// and <see cref="NetworkBehaviour.IsSpawned"/> is reset to <see langword="false"/>.
        /// </summary>
        public void Register(NetworkBehaviour obj)
        {
            if (obj == null) return;

            NetworkBehaviour previous = null;
            lock (_lock)
            {
                _objects.TryGetValue(obj.NetworkObjectId, out previous);
                _objects[obj.NetworkObjectId] = obj;
            }

            // Despawn the evicted object outside the lock to prevent re-entrance
            // if OnNetworkDespawn calls registry methods.
            // ReferenceEquals guard skips the no-op case of re-registering the same instance.
            if (previous != null && !ReferenceEquals(previous, obj))
                previous.SetSpawned(false);
        }

        /// <summary>Remove the entry for the given object ID (if present).</summary>
        public void Unregister(ulong objectId)
        {
            lock (_lock)
            {
                _objects.Remove(objectId);
            }
            // Drop hysteresis state so a future spawn that re-uses the id
            // starts hidden, matching the "first contact" semantics of the
            // interest filter.
            Rooms.InterestManager.ForgetObject(objectId);
        }

        // ── Query ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Look up a networked object by its ID.
        ///
       /// Performs a Unity null-equality check: if a <c>GameObject</c> was
        /// destroyed externally (<c>Object.Destroy</c>) without calling
        /// <see cref="Unregister"/>, the stale entry is evicted automatically
        /// and <see langword="null"/> is returned.
        /// </summary>
        public NetworkBehaviour Get(ulong objectId)
        {
            lock (_lock)
            {
                if (!_objects.TryGetValue(objectId, out var obj)) return null;

                // Unity overloads == so that a destroyed UnityEngine.Object
                // compares equal to null even though the C# reference is not null.
                if (obj == null)
                {
                    _objects.Remove(objectId);
                    Rooms.InterestManager.ForgetObject(objectId);
                    return null;
                }

                return obj;
            }
        }

        /// <summary>
        /// Return all currently registered objects, excluding any entries
        /// whose <c>GameObject</c> has been destroyed externally.
        ///
        /// <para><b>Buffer ownership contract:</b> every call returns a
        /// freshly-allocated <see cref="List{T}"/>.  Sharing a re-used
        /// buffer across calls — even with a depth counter — is unsafe
        /// when the consumer dispatches into user code mid-walk: a
        /// sibling main-thread call observes a depth that does not
        /// reflect the buffer's in-use status (the buffer is consumed
        /// outside the lock that produced it) and silently clobbers the
        /// outer walker's view of the registry.  Allocating a fresh list
        /// here is the only correctness-preserving option.</para>
        ///
        /// <para>Hot paths that dispatch user callbacks during caller-side
        /// iteration should prefer <see cref="GetAllSnapshot"/> with a
        /// pre-allocated caller-owned list — that pattern avoids the
        /// per-call allocation while still being safe under nesting.</para>
        /// </summary>
        public IReadOnlyList<NetworkBehaviour> GetAll()
        {
            lock (_lock)
            {
                // Always return a fresh copy.  The caller may dispatch into
                // arbitrary user code while iterating the result; any
                // shared-buffer optimisation would let a nested or sibling
                // GetAll call mutate the outer walker's view mid-iteration.
                var snapshot = new List<NetworkBehaviour>(_objects.Count);
                foreach (var obj in _objects.Values)
                {
                    // Unity null check: skip destroyed-but-not-unregistered entries.
                    if (obj != null) snapshot.Add(obj);
                }
                return snapshot;
            }
        }

        /// <summary>
        /// Fill <paramref name="destination"/> with every currently registered
        /// object whose <c>GameObject</c> has not been destroyed.  Zero-allocation
        /// alternative to <see cref="GetAll"/> for hot paths that own their
        /// iteration buffer and may dispatch into user code mid-walk — passing
        /// a private list sidesteps the shared-buffer re-entrancy hazard
        /// entirely.  The destination list is cleared before population.
        /// </summary>
        public void GetAllSnapshot(IList<NetworkBehaviour> destination)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            destination.Clear();
            lock (_lock)
            {
                foreach (var obj in _objects.Values)
                {
                    if (obj != null) destination.Add(obj);
                }
            }
        }

        /// <summary>
        /// Remove entries whose <c>GameObject</c> has been destroyed by Unity
        /// (scene unload, external <c>Object.Destroy</c>, explicit scene load).
        /// Returns the number of evicted entries.
        ///
       /// <para>Unlike <see cref="Get"/>, which lazily evicts one stale entry
        /// per call, this method sweeps the full dictionary in a single pass.
        /// Call it after scene transitions to keep the registry tight.</para>
        ///
       /// <para>Does NOT fire <see cref="NetworkBehaviour.OnNetworkDespawn"/> —
        /// the managed reference is unusable by the time this runs (Unity's
        /// null-equality returns true even before the C# field is set to null),
        /// so calling SetSpawned(false) would fault the user's handler.  Apps
        /// that care about despawn callbacks must destroy via
        /// <see cref="SpawnManager.Despawn"/> rather than letting a scene load
        /// reap the GameObject.</para>
        ///
       /// <para>Caller must be on the Unity main thread — uses Unity's null
        /// equality which is not safe from background threads.</para>
        /// </summary>
        public int PruneDestroyed()
        {
            List<ulong> staleIds = null;
            lock (_lock)
            {
                // Single pass: collect keys whose GameObject has been Unity-
                // destroyed.  Mutating a Dictionary while iterating throws
                // InvalidOperationException, so we accumulate into a scratch
                // list first and remove afterwards.
                foreach (var kv in _objects)
                {
                    // Unity's overloaded == compares destroyed UnityEngine.Object
                    // to null even when the managed reference is still live.
                    if (kv.Value == null)
                    {
                        (staleIds ??= new List<ulong>()).Add(kv.Key);
                    }
                }

                if (staleIds == null) return 0;

                foreach (var id in staleIds)
                {
                    _objects.Remove(id);
                    Rooms.InterestManager.ForgetObject(id);
                }
                return staleIds.Count;
            }
        }

        /// <summary>
        /// Clear all registered objects.
        ///
       /// Calls <see cref="NetworkBehaviour.SetSpawned(bool)"/> with
        /// <see langword="false"/> on each live object before removing it from
        /// the registry, so <see cref="NetworkBehaviour.OnNetworkDespawn"/> fires
        /// and <see cref="NetworkBehaviour.IsSpawned"/> is set to <see langword="false"/>.
        ///
       /// Despawning occurs OUTSIDE the lock to prevent re-entrance deadlocks if
        /// <c>OnNetworkDespawn</c> triggers further registry operations.
        /// </summary>
        public void Clear()
        {
            List<NetworkBehaviour> snapshot;
            lock (_lock)
            {
                snapshot = new List<NetworkBehaviour>(_objects.Values);
                _objects.Clear();
            }

            // Call despawn callbacks outside the lock.
            foreach (var obj in snapshot)
            {
                // Unity null check: skip already-destroyed GameObjects.
                if (obj == null) continue;

                // Isolate per-object despawn: an exception in one object's
                // OnNetworkDespawn callback must not prevent others from being despawned.
                try   { obj.SetSpawned(false); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }
    }
}
