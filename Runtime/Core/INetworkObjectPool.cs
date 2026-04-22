// RTMPE SDK — Runtime/Core/INetworkObjectPool.cs
//
// Pluggable object-pool contract used by SpawnManager for every network
// spawn/despawn.  Pooling is OPTIONAL — when no pool is installed,
// SpawnManager falls back to Object.Instantiate / Object.Destroy (the
// historical behaviour).  Apps with high spawn churn (bullets, hit FX,
// short-lived props) install a pool to avoid per-spawn GC pressure and
// the Instantiate/Destroy cost.
//
// Contract:
//   • Acquire must return a fully-configured GameObject with the same
//     NetworkBehaviour component the prefab has.  It MUST NOT return null
//     on success; instead, throw or return a fresh Instantiate if the
//     pool is empty.  SpawnManager treats a null return as a fatal error.
//   • Release is called instead of Object.Destroy when an object despawns.
//     Implementations SHOULD deactivate the GameObject and keep it for
//     later reuse.  Implementations MAY choose to Destroy rarely-used
//     prefabs to cap pool memory; that decision is purely internal.
//   • All calls happen on the Unity main thread.  Implementations do NOT
//     need to be thread-safe.
//
// Why an interface rather than a concrete class:
//   Different games want wildly different pooling strategies (global pool,
//   per-scene pool, LRU-capped, warm-up on scene load, etc.).  The SDK
//   intentionally ships no built-in pool so we don't paint consumers into
//   a specific design.  Users with no pooling needs pay zero overhead.

using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Contract for plugging a custom object pool into <see cref="SpawnManager"/>.
    /// Install via <see cref="SpawnManager.SetObjectPool"/>.
    /// When no pool is installed, <see cref="SpawnManager"/> falls back to
    /// <see cref="UnityEngine.Object.Instantiate(UnityEngine.Object, Vector3, Quaternion)"/>
    /// and <see cref="UnityEngine.Object.Destroy(UnityEngine.Object)"/>.
    /// </summary>
    public interface INetworkObjectPool
    {
        /// <summary>
        /// Acquire an instance of <paramref name="prefab"/>, positioned and rotated as requested.
        /// Called by <see cref="SpawnManager"/> on every spawn (local or server-driven).
        /// </summary>
        /// <param name="prefabId">Registered prefab identifier (the key used in <see cref="SpawnManager.RegisterPrefab"/>).</param>
        /// <param name="prefab">Source prefab <see cref="GameObject"/> — may be used as a fallback Instantiate source when the pool is cold.</param>
        /// <param name="position">Desired world-space position.</param>
        /// <param name="rotation">Desired world-space rotation.</param>
        /// <returns>
        /// A live, active <see cref="GameObject"/> with the prefab's <see cref="NetworkBehaviour"/>
        /// component attached.  MUST NOT be <see langword="null"/>.
        /// </returns>
        GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation);

        /// <summary>
        /// Return <paramref name="instance"/> to the pool.  Called by
        /// <see cref="SpawnManager"/> on every despawn (local or server-driven).
        /// </summary>
        /// <param name="prefabId">
        /// The prefab identifier associated with this instance when it was acquired.
        /// The pool may use this to route the instance back to the correct bucket.
        /// May be <see cref="uint.MaxValue"/> (sentinel) if the instance was not
        /// tagged at acquire time; in that case the pool should fall back to
        /// destroying the instance.
        /// </param>
        /// <param name="instance">The instance to release. Must not be null.</param>
        void Release(uint prefabId, GameObject instance);
    }
}
