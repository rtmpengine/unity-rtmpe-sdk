// RTMPE SDK — Runtime/Core/SpawnLifecycleOps.cs
//
// Spawn-lifecycle fan-out over the NetworkBehaviour components of one networked
// object. A networked object carries one NetworkBehaviour per script, and
// always at least two — NetworkTransform is itself a NetworkBehaviour and is
// required on any synced object — so spawn and despawn must drive the lifecycle
// of EVERY component, not only the one chosen as the registry/routing anchor.
// The fan-out is expressed over the INbLifecycle seam rather than the
// Unity-only NetworkBehaviour so it can be exercised in isolation.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// The spawn-lifecycle surface of a networked component, abstracted from the
    /// Unity-only <c>NetworkBehaviour</c> so the fan-out can run without a Unity
    /// runtime. <c>NetworkBehaviour</c> implements it.
    /// </summary>
    internal interface INbLifecycle
    {
        bool IsSpawned { get; }
        void Initialize(ulong objectId, string ownerPlayerId);
        void SetSpawned(bool spawned);
        void MarkExternallyEvicted();
    }

    internal static class SpawnLifecycleOps
    {
        /// <summary>
        /// Initialise every not-yet-spawned component with the object's id and
        /// owner — the value <c>IsOwner</c> compares. A null entry (missing
        /// script) or an already-spawned one (pool reuse / duplicate spawn) is
        /// skipped so initialisation never re-runs on a live component.
        /// </summary>
        internal static void InitializeAll(
            IReadOnlyList<INbLifecycle> components, ulong objectId, string ownerPlayerId)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c == null || c.IsSpawned) continue;
                c.Initialize(objectId, ownerPlayerId);
            }
        }

        /// <summary>
        /// Mark every component spawned, firing each one's <c>OnNetworkSpawn</c>.
        /// <c>SetSpawned(true)</c> is idempotent, so an already-spawned component
        /// is a no-op.
        /// </summary>
        internal static void SpawnAll(IReadOnlyList<INbLifecycle> components)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null) c.SetSpawned(true);
            }
        }

        /// <summary>
        /// Tear down every component: flag them all evicted FIRST — so each
        /// component's imminent Unity <c>OnDestroy</c> is recognised as part of
        /// this teardown and cannot double-decrement the live-spawn counter —
        /// then unspawn them all, firing each <c>OnNetworkDespawn</c>.
        /// </summary>
        internal static void DespawnAll(IReadOnlyList<INbLifecycle> components)
        {
            if (components == null) return;
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null) c.MarkExternallyEvicted();
            }
            for (int i = 0; i < components.Count; i++)
            {
                var c = components[i];
                if (c != null) c.SetSpawned(false);
            }
        }
    }
}
