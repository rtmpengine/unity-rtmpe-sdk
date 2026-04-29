// RTMPE SDK — Runtime/Core/SpawnManager.cs
//
// Manages the lifecycle of networked objects: prefab registration,
// local instantiation/destruction, and owner-leave cleanup.
//
// Architecture notes:
//   • SpawnManager is the CENTRAL hub for object lifecycle. It owns the
//     NetworkObjectRegistry and OwnershipManager (exposed as properties).
//   • Spawn() / Despawn() create/destroy locally AND send a packet to
//     the server for relay to other clients in the room. When the server
//     relays a Spawn/Despawn to a receiving client, the NetworkManager
//     packet handler calls CreateLocal() / DestroyLocal() directly.
//   • CreateLocal() / DestroyLocal() are internal so only the SDK itself
//     (or tests via InternalsVisibleTo) can call them. These are the
//     primitives that the server-driven spawn path will invoke.
//   • OnPlayerLeftRoom() handles the DestroyWithOwner contract defined
//     on NetworkBehaviour. For objects with DestroyWithOwner=false, the
//     server is responsible for sending ownership grants — no local
//     mutation occurs here (server-authoritative ownership).
//   • ClearAll() is called on disconnect / room leave to tear down all
//     spawned objects (fires OnNetworkDespawn for each, then destroys GOs).
//   • Object IDs are 64-bit values produced by ObjectIdMath.Compose:
//     high 32 bits = avalanche-mixed digest of the FULL u64 gateway
//     session id, low 32 bits = monotonic per-session counter. The wire
//     field (SpawnPacketBuilder: object_id u64 LE) carries the full 64
//     bits with no truncation, and the digest mixes every session-id byte
//     so reconnects that reuse the low half of a prior session id cannot
//     collide with that session's still-live object ids.

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Manages network object spawning, prefab registration, and owner-leave cleanup.
    /// Access via <see cref="NetworkManager.Spawner"/>.
    /// All methods must be called from the Unity main thread.
    /// </summary>
    public sealed class SpawnManager
    {
        private readonly NetworkObjectRegistry _registry;
        private readonly OwnershipManager _ownership;
        private readonly NetworkManager _networkManager;

        private readonly Dictionary<uint, GameObject> _prefabs =
            new Dictionary<uint, GameObject>();

        // Remembers which prefab each spawned GameObject came from so that
        // Release() can route it back to the correct pool bucket at despawn.
        // Populated by CreateLocal; consulted (and cleared) in DestroyLocal.
        // Runtime cost is O(N_live_objects) — negligible vs the per-frame
        // registry traversal.
        private readonly Dictionary<ulong, uint> _prefabOfObject =
            new Dictionary<ulong, uint>();

        // Optional pluggable pool — null means "no pooling" (use Instantiate/Destroy).
        // Assigned via SetObjectPool; cleared via ClearObjectPool.  Main-thread only.
        private INetworkObjectPool _pool;

        // ── Spawn rate / count caps ────────────────────────────────────────────
        //
        // A hostile gateway can flood the receiver with Spawn frames in an
        // attempt to exhaust the main-thread Instantiate budget and the
        // GameObject heap (an OOM crash on mobile within seconds).  Two caps
        // gate every CreateLocal entry path:
        //
        //   • _spawnsThisSecond rolls inside a one-second bucket; bursts
        //     above NetworkSettings.maxSpawnsPerSecond are dropped.
        //   • _currentSpawnCount tracks the live object total against
        //     NetworkSettings.maxSpawnsPerRoom regardless of arrival rate.
        //
        // The bucket start is stored as Stopwatch ticks rather than wall-clock
        // ticks so the counter is immune to system-time adjustments (NTP,
        // user clock changes) that could otherwise either freeze the bucket
        // or roll it backwards into a permanent throttle.  The decrement on
        // unregister keeps the live total accurate even when an external
        // caller destroys an object outside the normal Despawn path.
        private int _spawnsThisSecond;
        private long _spawnRateBucketStartTicks;
        private int _currentSpawnCount;
        private bool _rateLimitWarnedThisBucket;
        private bool _countLimitWarnedThisBucket;

        // Re-entry guard: a user callback fired inside CreateLocal (Initialize,
        // OnNetworkSpawn, a custom INetworkObjectPool.Acquire) that synchronously
        // calls back into Spawn / CreateLocal would otherwise observe transient
        // state.  Counters are incremented eagerly (see CreateLocal) so the cap
        // is enforced on the re-entrant call, and this flag exists purely so
        // that re-entry is loud in the logs — silent recursion has historically
        // hidden cap-bypass bugs from review.
        private bool _isCreatingLocal;

        // ── Out-of-order despawn tracking ──────────────────────────────────────
        //
        // UDP reorder can deliver Despawn(id) before Spawn(id) for the same
        // object id when the gateway uses a different relay path for each
        // (rare but real on roaming mobile networks).  Bookkeeping is owned
        // by PendingDespawnTracker which keeps a dictionary, an insertion-
        // order LinkedList, and a side map of (id → node) in lockstep so
        // every state transition is O(1) on every axis and the order list
        // cannot accumulate ghost ids.
        private readonly PendingDespawnTracker _pendingDespawns =
            new PendingDespawnTracker();
        internal const long PendingDespawnTtlMs = PendingDespawnTracker.TtlMs;
        internal const int MaxPendingDespawns = PendingDespawnTracker.MaxEntries;

        // Monotonic counter for the low 32 bits of locally-generated object IDs.
        // Stored as `long` (not `ulong`) so that `Interlocked.Increment` — whose
        // public overload on .NET Standard 2.1 only accepts `ref long` — can be
        // used to guarantee thread safety.  The contract remains main-thread-only
        // for correctness of the GameObject lifecycle, but atomic increment
        // preserves uniqueness even if a third-party integration accidentally
        // calls Spawn() from a background thread.
        //
        // Starts at 0 so that the first Interlocked.Increment returns 1 — preserving
        // the historical behaviour of the previous post-increment implementation
        // (`_nextLocalId = 1` + `_nextLocalId++`).  The low 32 bits are masked at
        // use-time; values cast back to ulong are always in [1, uint.MaxValue].
        private long _nextLocalId;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>The shared object registry for all spawned objects.</summary>
        public NetworkObjectRegistry Registry => _registry;

        /// <summary>The ownership manager for server-authoritative ownership.</summary>
        public OwnershipManager Ownership => _ownership;

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Create a SpawnManager.
        /// </summary>
        /// <param name="registry">Shared object registry.</param>
        /// <param name="ownership">Ownership manager (created by this or externally).</param>
        /// <param name="networkManager">Active NetworkManager for ID generation and future sends.</param>
        public SpawnManager(
            NetworkObjectRegistry registry,
            OwnershipManager ownership,
            NetworkManager networkManager)
        {
            _registry       = registry       ?? throw new ArgumentNullException(nameof(registry));
            _ownership      = ownership      ?? throw new ArgumentNullException(nameof(ownership));
            _networkManager = networkManager ?? throw new ArgumentNullException(nameof(networkManager));
        }

        // ── Prefab Registration ────────────────────────────────────────────────

        /// <summary>
        /// Register a prefab for network spawning.
        /// The prefab must have a <see cref="NetworkBehaviour"/> component.
        /// Overwrites any previous registration with the same ID (logs warning).
        /// </summary>
        /// <param name="prefabId">Unique identifier for this prefab type.</param>
        /// <param name="prefab">The prefab <c>GameObject</c> to register.</param>
        public void RegisterPrefab(uint prefabId, GameObject prefab)
        {
            if (prefab == null)
                throw new ArgumentNullException(nameof(prefab));

            if (_prefabs.ContainsKey(prefabId))
                Debug.LogWarning(
                    $"[SpawnManager] RegisterPrefab: overwriting existing prefabId {prefabId}.");

            _prefabs[prefabId] = prefab;
        }

        /// <summary>
        /// Remove a prefab registration. Returns true if it was registered.
        /// </summary>
        public bool UnregisterPrefab(uint prefabId) => _prefabs.Remove(prefabId);

        /// <summary>True if a prefab is registered under <paramref name="prefabId"/>.</summary>
        public bool HasPrefab(uint prefabId) => _prefabs.ContainsKey(prefabId);

        // ── Pooling ────────────────────────────────────────────────────────────

        /// <summary>
        /// Install a custom <see cref="INetworkObjectPool"/>.
        /// From the next spawn onwards, <see cref="SpawnManager"/> routes every
        /// instantiate/destroy through the pool.  Pass <see langword="null"/>
        /// to restore the built-in Instantiate/Destroy path.
        /// </summary>
        /// <remarks>
        /// Swapping pools at runtime is allowed but does NOT migrate already-live
        /// objects — they will be released to whichever pool is active at despawn
        /// time.  Apps that need to migrate should despawn + respawn explicitly.
        /// </remarks>
        public void SetObjectPool(INetworkObjectPool pool) => _pool = pool;

        /// <summary>Remove any installed pool, reverting to Instantiate/Destroy.</summary>
        public void ClearObjectPool() => _pool = null;

        /// <summary>The currently-installed pool, or <see langword="null"/> when none is set.</summary>
        public INetworkObjectPool ObjectPool => _pool;

        // ── Spawn / Despawn (Public API) ───────────────────────────────────────

        /// <summary>
        /// Spawn a networked object from a registered prefab.
        ///
        /// Creates the object locally and sends a SpawnRequest to the server
        /// for relay to other clients in the room.
        /// </summary>
        /// <param name="prefabId">Registered prefab identifier.</param>
        /// <param name="position">World-space spawn position.</param>
        /// <param name="rotation">World-space spawn rotation.</param>
        /// <param name="ownerPlayerId">
        /// Room player UUID of the owner. When <see langword="null"/>,
        /// defaults to <see cref="NetworkManager.LocalPlayerStringId"/>.
        /// </param>
        /// <returns>The spawned <see cref="NetworkBehaviour"/>, or null on failure.</returns>
        public NetworkBehaviour Spawn(
            uint prefabId,
            Vector3 position,
            Quaternion rotation,
            string ownerPlayerId = null)
        {
            if (!_prefabs.ContainsKey(prefabId))
            {
                Debug.LogError($"[SpawnManager] Spawn: prefab {prefabId} is not registered.");
                return null;
            }

            var objectId = GenerateObjectId();
            var owner = ownerPlayerId
                ?? _networkManager.LocalPlayerStringId
                ?? string.Empty;

            var nb = CreateLocal(prefabId, objectId, owner, position, rotation);
            if (nb != null)
                SendSpawnPacket(prefabId, objectId, owner, position, rotation);
            return nb;
        }

        /// <summary>
        /// Despawn (destroy) a networked object.
        ///
        /// Destroys the object locally and sends a DespawnRequest to the server
        /// for relay to other clients in the room.
        /// </summary>
        /// <param name="objectId">The network object ID to despawn.</param>
        public void Despawn(ulong objectId)
        {
            // Send DespawnRequest to server for relay to other clients.
            SendDespawnPacket(objectId);

            DestroyLocal(objectId);
        }

        // ── Internal: Server-Driven Operations ─────────────────────────────────

        /// <summary>
        /// Instantiate a networked object locally from a registered prefab.
        /// Called by the inbound Spawn packet handler or by <see cref="Spawn"/>.
        /// </summary>
        /// <returns>The spawned <see cref="NetworkBehaviour"/>, or null on failure.</returns>
        internal NetworkBehaviour CreateLocal(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation)
        {
            // Out-of-order Despawn-before-Spawn: if a despawn for this id
            // landed first (still inside its TTL), the object is logically
            // dead — creating it now would produce a ghost.  Consume the
            // pending entry and skip the spawn.
            _pendingDespawns.Prune(NowMillis());
            if (_pendingDespawns.Consume(objectId))
            {
                RtmpeLog.Info(
                    "[SpawnManager] Spawn dropped: matching Despawn already arrived (out-of-order delivery).");
                return null;
            }

            // Rate / count gates run before any Instantiate work so a flood of
            // hostile spawns is rejected before allocating a GameObject.
            if (!CheckSpawnAdmission()) return null;

            if (!_prefabs.TryGetValue(prefabId, out var prefab))
            {
                Debug.LogWarning(
                    $"[SpawnManager] CreateLocal: prefab {prefabId} not registered.");
                return null;
            }

            // Re-entry visibility: a user callback fired during CreateLocal
            // that synchronously re-enters CreateLocal observes the eagerly
            // incremented counters, so the cap holds — but log it because
            // recursive spawn is almost always a bug worth surfacing.
            if (_isCreatingLocal)
            {
                RtmpeLog.Warning(
                    "[SpawnManager] CreateLocal re-entered from a user callback; " +
                    "the spawn cap is still enforced on the inner call.");
            }

            // Counters incremented eagerly so a user callback that re-enters
            // CreateLocal observes the post-spawn count, not the pre-spawn
            // count.  The Initialize / OnNetworkSpawn / pool-Acquire callbacks
            // below all run user code; without eager increment, recursion
            // depth N would let N extra spawns slip past the per-room cap
            // (each inner call sees the outer's not-yet-applied increment).
            _spawnsThisSecond++;
            _currentSpawnCount++;

            bool committed = false;
            bool prevCreating = _isCreatingLocal;
            _isCreatingLocal = true;
            try
            {
                // Route through the pool when installed; fall back to Instantiate
                // otherwise.  A null return from a pool is a contract violation and
                // is treated as a fatal error — the surrounding game code assumes
                // Spawn produces a live object.
                GameObject go;
                if (_pool != null)
                {
                    go = _pool.Acquire(prefabId, prefab, position, rotation);
                    if (go == null)
                    {
                        Debug.LogError(
                            $"[SpawnManager] CreateLocal: INetworkObjectPool.Acquire " +
                            $"returned null for prefabId {prefabId}. Pools MUST return " +
                            "a live GameObject. Falling back to Instantiate this time.");
                        go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                    }
                    else
                    {
                        // The pool may have handed us a cached instance whose position
                        // was set at a previous despawn.  Force both transform fields
                        // before the NetworkBehaviour wakes up so OnNetworkSpawn sees
                        // the correct pose.  Use localPosition/localRotation for safety
                        // when the pool parents instances under a reuse bucket.
                        go.transform.SetPositionAndRotation(position, rotation);
                        if (!go.activeSelf) go.SetActive(true);
                    }
                }
                else
                {
                    go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                }

                var nb = go.GetComponent<NetworkBehaviour>();
                if (nb == null)
                {
                    Debug.LogError(
                        $"[SpawnManager] CreateLocal: prefab {prefabId} has no " +
                        "NetworkBehaviour component. Destroying instantiated object.");
                    // A pooled instance that lost its NetworkBehaviour somehow —
                    // destroy rather than returning it to the pool to prevent a
                    // corrupted instance from being reused.
                    UnityEngine.Object.Destroy(go);
                    return null;
                }

                nb.Initialize(objectId, ownerPlayerId ?? string.Empty);
                _registry.Register(nb);
                _prefabOfObject[objectId] = prefabId;
                nb.SetSpawned(true);

                committed = true;
                return nb;
            }
            finally
            {
                _isCreatingLocal = prevCreating;
                // Roll the eager increments back when the spawn never produced
                // a live registered object (null prefab GO, missing component,
                // user callback throw).  The catch clause inside CheckSpawn-
                // Admission already runs before this point, so failure here
                // is strictly post-admission and never a false-positive
                // cap-headroom restore.
                if (!committed)
                {
                    if (_spawnsThisSecond > 0) _spawnsThisSecond--;
                    if (_currentSpawnCount > 0) _currentSpawnCount--;
                }
            }
        }

        /// <summary>
        /// Destroy a networked object locally.
        /// Fires <see cref="NetworkBehaviour.OnNetworkDespawn"/>, unregisters,
        /// then destroys the <c>GameObject</c>.
        /// </summary>
        internal void DestroyLocal(ulong objectId)
        {
            var nb = _registry.Get(objectId);
            if (nb == null)
            {
                // Despawn arrived before Spawn (UDP reorder).  Record the
                // intent under a TTL so the eventual Spawn for this id can
                // see "already despawned" and skip creating a ghost object.
                long now = NowMillis();
                _pendingDespawns.Prune(now);
                if (_pendingDespawns.Record(objectId, now))
                {
                    // Redacted: only the cap is logged, never the offending id.
                    RtmpeLog.Warning(
                        $"[SpawnManager] Pending-despawn cap reached ({MaxPendingDespawns}); evicting oldest entry.");
                }

                // Still try to clear any stale prefab mapping for this id so the
                // dictionary doesn't accumulate orphans when objects are
                // externally destroyed.
                _prefabOfObject.Remove(objectId);
                return;
            }

            // Despawn arrived AFTER Spawn — normal path.  Consume any pending
            // entry so the order list cannot retain a ghost.
            _pendingDespawns.Consume(objectId);

            // Flag the NetworkBehaviour first so its later OnDestroy (which
            // Unity dispatches during the Destroy() call below) observes the
            // teardown is already in progress and skips OnExternallyDestroyed.
            // Without this the counters double-decrement under any pool-less
            // path that completes synchronously inside the same frame.
            nb.MarkExternallyEvicted();

            nb.SetSpawned(false);
            _registry.Unregister(objectId);

            // Live-count decrement mirrors the increment in CreateLocal.
            // Clamp at zero so an external destroy that bypasses CreateLocal
            // can never drive the counter negative and silently extend the cap.
            if (_currentSpawnCount > 0) _currentSpawnCount--;

            _prefabOfObject.TryGetValue(objectId, out uint prefabId);
            _prefabOfObject.Remove(objectId);

            // Unity null check: the GO may have been destroyed externally.
            if (nb == null) return;

            // When a pool is installed, return the instance for reuse instead
            // of destroying it.  The pool is responsible for deactivating the
            // GameObject — but we do a final IsOwner-safe NetworkBehaviour
            // reset so the pooled instance is in a known state next spawn.
            if (_pool != null)
            {
                try { _pool.Release(prefabId, nb.gameObject); }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                    // If the pool throws, fall back to destroy to avoid a leak.
                    UnityEngine.Object.Destroy(nb.gameObject);
                }
            }
            else
            {
                UnityEngine.Object.Destroy(nb.gameObject);
            }
        }

        /// <summary>
        /// Reconcile counters and registry when a NetworkObject is destroyed
        /// via the Unity API (Object.Destroy on the GameObject) rather than
        /// the SpawnManager's <see cref="DestroyLocal"/> entry point.
        /// Idempotent so a normal <c>DestroyLocal</c> followed by Unity's
        /// own OnDestroy of the same instance does not double-decrement —
        /// the registry-presence check is the canonical "is this still
        /// owned by SpawnManager?" question.
        /// </summary>
        /// <remarks>
        /// Without this hook, code that calls
        /// <c>UnityEngine.Object.Destroy(networkBehaviour.gameObject)</c>
        /// directly leaks a slot in <c>_currentSpawnCount</c>, an entry in
        /// <c>_prefabOfObject</c>, and a registry binding.  Saturation of
        /// the live-count cap follows after enough such bypasses.
        /// </remarks>
        internal void OnExternallyDestroyed(ulong objectId)
        {
            if (objectId == 0) return;

            // Registry presence is the authority: a prior DestroyLocal has
            // already called _registry.Unregister(objectId), so this branch
            // returns immediately and the counters are not touched twice.
            if (_registry.Get(objectId) == null && !_prefabOfObject.ContainsKey(objectId))
                return;

            _registry.Unregister(objectId);

            if (_currentSpawnCount > 0) _currentSpawnCount--;

            _prefabOfObject.Remove(objectId);
        }

        // ── Late-Join Resync ───────────────────────────────────────────────────

        /// <summary>
        /// Mark every NetworkVariable on every locally-owned object as dirty
        /// so the next 30 Hz flush retransmits its current value.  Called by
        /// <see cref="NetworkManager"/> when another player joins the current
        /// room, giving the new joiner a full state snapshot within ~33 ms
        /// instead of waiting for a future value change.
        /// <para>
        /// No-op when the registry is empty or no objects are locally owned.
        /// Runs on the Unity main thread.
        /// </para>
        /// <para>
        /// Current scope: only <c>NetworkVariable</c> values participate.
        /// <c>NetworkTransform</c> already forces its next broadcast on each
        /// interpolation frame, so pose sync is naturally covered.  The
        /// trade-off of marking ALL variables (vs only unchanged ones) is a
        /// single redundant flush cycle per join — negligible at typical
        /// object counts, and far cheaper than a new protocol round trip.
        /// </para>
        /// </summary>
        public void MarkAllVariablesDirtyForResync()
        {
            // MarkAllVariablesDirty walks user subscribers; one of them may
            // legally re-enter the registry (e.g. by spawning or despawning in
            // response to the resync).  Using a private snapshot list keeps
            // this walk independent of the shared GetAll buffer so the inner
            // re-entry cannot perturb iteration here.
            _registry.GetAllSnapshot(_resyncScratch);
            for (int i = 0; i < _resyncScratch.Count; i++)
            {
                var nb = _resyncScratch[i];
                if (nb == null) continue;
                if (!nb.IsOwner || !nb.IsSpawned) continue;
                try { nb.MarkAllVariablesDirty(); }
                catch (Exception ex)
                {
                    // Isolate per-object failure: one misbehaving NetworkBehaviour
                    // must not block resync for the rest of the owned roster.
                    Debug.LogException(ex);
                }
            }
            _resyncScratch.Clear();
        }

        // Pre-allocated snapshot buffer for hot resync walks.  Owned by this
        // SpawnManager so it cannot collide with the registry's shared GetAll
        // buffer when a callback fires further iteration.
        private readonly List<NetworkBehaviour> _resyncScratch =
            new List<NetworkBehaviour>(64);

        // Same rationale for the ClearAll teardown — Registry.Clear dispatches
        // user OnNetworkDespawn callbacks, any of which may legally read the
        // registry, and we cannot leave the shared GetAll buffer parked across
        // that re-entry window.
        private readonly List<NetworkBehaviour> _clearAllScratch =
            new List<NetworkBehaviour>(64);

        // ── Owner Leave Handling ───────────────────────────────────────────────

        /// <summary>
        /// Handle a player leaving the room.
        /// Destroys all objects owned by <paramref name="playerId"/> that
        /// have <see cref="NetworkBehaviour.DestroyWithOwner"/> set to
        /// <see langword="true"/>. Objects with <c>DestroyWithOwner=false</c>
        /// are left intact — the server will send ownership grant packets
        /// to reassign them (server-authoritative).
        /// </summary>
        /// <param name="playerId">Room player UUID of the player who left.</param>
        public void OnPlayerLeftRoom(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;

            var owned = _ownership.GetObjectsOwnedBy(playerId);
            foreach (var obj in owned)
            {
                if (obj == null) continue;

                if (obj.DestroyWithOwner)
                {
                    // Exception isolation: continue processing remaining objects.
                    try { DestroyLocal(obj.NetworkObjectId); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
                // Non-DestroyWithOwner objects: server sends ownership grants.
                // No local mutation — server-authoritative ownership.
            }
        }

        // ── Cleanup ────────────────────────────────────────────────────────────

        /// <summary>
        /// Destroy all spawned objects and reset state.
        /// Called on room leave and disconnect.
        /// Fires <see cref="NetworkBehaviour.OnNetworkDespawn"/> for each live object,
        /// then destroys all <c>GameObject</c>s.
        /// </summary>
        public void ClearAll()
        {
            // Two-pass teardown is mandatory because user code in
            // OnNetworkDespawn (fired from pass 1) may legitimately call
            // Object.Destroy on its own GameObject or release it back to a
            // custom pool.  Pass 2 must therefore re-check the Unity-engine
            // null state of every captured reference before invoking Release
            // or Destroy — Unity's overloaded == operator returns true for a
            // C# reference whose underlying engine object has been destroyed,
            // and a custom pool's Release(prefabId, gameObject) on such a
            // reference may NRE when it tries to call SetActive.
            //
            // The snapshot is a list of (NetworkBehaviour, prefabId) pairs
            // captured BEFORE the despawn callbacks fire, so a callback that
            // unregisters or otherwise mutates the live registry cannot
            // perturb the iteration order or skip an entry.

            // Capture the live (NB, prefabId) tuples BEFORE Registry.Clear
            // fires the despawn callbacks; the snapshot is the authoritative
            // iteration order for pass 2 even if user code mutates the
            // registry from inside OnNetworkDespawn.  Using GetAllSnapshot
            // populates a private list so the registry's shared GetAll buffer
            // is not parked across the Clear call (which itself dispatches
            // user code that may invoke GetAll re-entrantly).
            _registry.GetAllSnapshot(_clearAllScratch);
            var snapshot = new List<(NetworkBehaviour Nb, uint PrefabId)>(_clearAllScratch.Count);
            for (int i = 0; i < _clearAllScratch.Count; i++)
            {
                var nb = _clearAllScratch[i];
                if (nb == null) continue;
                // Pre-mark every captured NB so their imminent OnDestroy
                // calls (driven by either the pool's Release or the direct
                // UnityEngine.Object.Destroy in pass 2 below) skip the
                // SpawnManager reconciliation path — ClearAll resets the
                // counters wholesale, and a per-object decrement on top of
                // that would underflow.
                nb.MarkExternallyEvicted();
                uint prefabId = _prefabOfObject.TryGetValue(nb.NetworkObjectId, out var pid)
                    ? pid : uint.MaxValue;
                snapshot.Add((nb, prefabId));
            }
            _clearAllScratch.Clear();

            // Pass 1: fire despawn callbacks via the registry sweep.
            // Registry.Clear captures its own snapshot under its lock and
            // invokes SetSpawned(false) outside the lock, so user code in
            // OnNetworkDespawn that re-enters the registry does not deadlock.
            _registry.Clear();

            // Pass 2: destroy or release surviving GameObjects.  Each access
            // is guarded by Unity's destroyed-object null check on both the
            // NetworkBehaviour itself (the user may have called Destroy on
            // the parent GameObject) and the GameObject reference (defence-
            // in-depth — typically redundant when the NB null-check fires).
            for (int i = 0; i < snapshot.Count; i++)
            {
                var entry = snapshot[i];
                var nb = entry.Nb;

                // The Unity == operator returns true for any reference whose
                // underlying engine object has been destroyed; this filters
                // out objects already torn down by user OnNetworkDespawn code.
                if (nb == null) continue;

                GameObject go;
                try { go = nb.gameObject; }
                catch (MissingReferenceException) { continue; }
                if (go == null) continue;

                try
                {
                    if (_pool != null)
                    {
                        // prefabId == uint.MaxValue means we never recorded a
                        // mapping (e.g. an object created outside CreateLocal);
                        // the pool must either route by its own bookkeeping or
                        // fall through to Destroy in its Release implementation.
                        _pool.Release(entry.PrefabId, go);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(go);
                    }
                }
                catch (Exception ex) { Debug.LogException(ex); }
            }

            _prefabOfObject.Clear();
            _pendingDespawns.Clear();

            // Reset spawn admission counters: a fresh room must start with a
            // clean rate bucket and a zero live count.
            _spawnsThisSecond           = 0;
            _spawnRateBucketStartTicks  = 0;
            _currentSpawnCount          = 0;
            _rateLimitWarnedThisBucket  = false;
            _countLimitWarnedThisBucket = false;

            // Interlocked.Exchange is atomic w.r.t. concurrent Increment calls —
            // a partial read/write mid-reset cannot observe a torn 64-bit value.
            // The contract is still "reset only after ClearAll on disconnect",
            // but the atomic reset provides defence against unexpected call sites.
            Interlocked.Exchange(ref _nextLocalId, 0);
        }

        // ── Despawn-before-Spawn (out-of-order) ────────────────────────────────

        // Test seams: every state transition keeps the three coupled
        // structures inside PendingDespawnTracker in lockstep.  Surfacing
        // each axis lets the adversarial coverage prove that normal
        // consumption never leaves ghost entries in the order list.
        internal int PendingDespawnCount      => _pendingDespawns.Count;
        internal int PendingDespawnOrderCount => _pendingDespawns.OrderCount;
        internal int PendingDespawnNodeCount  => _pendingDespawns.NodeCount;

        private static long NowMillis()
        {
            // Stopwatch-based monotonic clock — survives wall-time adjustments.
            long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
            return ticks * 1000L / System.Diagnostics.Stopwatch.Frequency;
        }

        // ── Spawn Admission ────────────────────────────────────────────────────

        /// <summary>
        /// Returns true when the spawn may proceed; false if either the
        /// per-second rate cap or the per-room concurrent-object cap is
        /// already saturated.  Bucket roll uses Stopwatch ticks so wall-clock
        /// adjustments cannot stick the rate limiter shut or open.
        /// </summary>
        private bool CheckSpawnAdmission()
        {
            int rateCap  = 100;
            int countCap = 5_000;
            var settings = _networkManager?.Settings;
            if (settings != null)
            {
                rateCap  = settings.maxSpawnsPerSecond > 0 ? settings.maxSpawnsPerSecond : rateCap;
                countCap = settings.maxSpawnsPerRoom    > 0 ? settings.maxSpawnsPerRoom    : countCap;
            }

            // Roll the bucket if the wall-time second has elapsed.  Using the
            // monotonic Stopwatch frequency avoids any reliance on system
            // wall-clock stability — an NTP step cannot freeze or open the gate.
            long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();
            long ticksPerSecond = System.Diagnostics.Stopwatch.Frequency;
            if (_spawnRateBucketStartTicks == 0
                || nowTicks - _spawnRateBucketStartTicks >= ticksPerSecond)
            {
                _spawnRateBucketStartTicks  = nowTicks;
                _spawnsThisSecond           = 0;
                _rateLimitWarnedThisBucket  = false;
                _countLimitWarnedThisBucket = false;
            }

            if (_spawnsThisSecond >= rateCap)
            {
                if (!_rateLimitWarnedThisBucket)
                {
                    _rateLimitWarnedThisBucket = true;
                    // Redacted: only the cap is logged.  An attacker reading
                    // the log cannot infer which prefab / owner was clipped.
                    RtmpeLog.Warning(
                        $"[SpawnManager] Spawn rate cap reached ({rateCap}/s); excess spawns dropped this bucket.");
                }
                return false;
            }

            if (_currentSpawnCount >= countCap)
            {
                if (!_countLimitWarnedThisBucket)
                {
                    _countLimitWarnedThisBucket = true;
                    RtmpeLog.Warning(
                        $"[SpawnManager] Spawn count cap reached ({countCap}); spawn dropped.");
                }
                return false;
            }

            return true;
        }

        // ── Private ────────────────────────────────────────────────────────────

        /// <summary>
        /// Generate a locally-unique 64-bit object ID.
        /// High 32 bits: avalanche-mixed digest of the FULL u64 gateway session
        /// id (xor-fold of high and low halves followed by a SplitMix64-style
        /// finalizer).  Mixing every input byte means two sessions whose low
        /// halves coincide map to different high-half digests with 1-in-2^32
        /// probability — closing the reconnect-collision class that the prior
        /// "low 32 bits of session id" scheme accepted by construction.
        /// Low 32 bits: per-session monotonic counter.  At 1000 spawns/s a 32-bit
        /// counter wraps in ≈49 days, comfortably outside any plausible session
        /// lifetime; <see cref="ClearAll"/> resets it to 0 on disconnect so a
        /// fresh session always starts from a clean slate.
        /// </summary>
        /// <remarks>
        /// The wire (SpawnPacketBuilder: object_id u64 LE) carries the full 64
        /// bits — no truncation happens at the framing layer.  The high/low
        /// split is purely a client-side allocation policy and can be replaced
        /// by a server-issued id space without touching the wire format.
        /// </remarks>
        private ulong GenerateObjectId()
        {
            // Interlocked.Increment returns the post-increment value, so first
            // call yields 1.  The mask is defence-in-depth — Interlocked on a
            // long can never produce a negative on the first 2^31 increments,
            // and ClearAll resets long before that horizon.
            ulong counter = (ulong)Interlocked.Increment(ref _nextLocalId);
            return ObjectIdMath.Compose(_networkManager.LocalPlayerId, counter);
        }

        /// <summary>
        /// Build and send a Spawn packet through the NetworkManager.
        /// Silently skips if not connected (local-only spawn still succeeds).
        /// </summary>
        private void SendSpawnPacket(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation)
        {
            if (!_networkManager.IsConnected) return;

            var payload = SpawnPacketBuilder.BuildSpawnRequest(
                prefabId, objectId, ownerPlayerId, position, rotation);
            _networkManager.Send(
                _networkManager.BuildPacket(PacketType.Spawn, PacketFlags.Reliable, payload),
                reliable: true);
        }

        /// <summary>
        /// Build and send a Despawn packet through the NetworkManager.
        /// Silently skips if not connected (local-only despawn still succeeds).
        /// </summary>
        private void SendDespawnPacket(ulong objectId)
        {
            if (!_networkManager.IsConnected) return;

            var payload = SpawnPacketBuilder.BuildDespawnRequest(objectId);
            _networkManager.Send(
                _networkManager.BuildPacket(PacketType.Despawn, PacketFlags.Reliable, payload),
                reliable: true);
        }
    }
}
