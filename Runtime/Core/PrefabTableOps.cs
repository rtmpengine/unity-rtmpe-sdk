// RTMPE SDK — Runtime/Core/PrefabTableOps.cs
//
// Operations over the spawn prefab table (prefab id → prefab GameObject).
//
// The prefab table is static game configuration, not per-session state: a
// given id resolves to the same prefab for the lifetime of the application.
// It is therefore carried forward when the session-scoped SpawnManager is
// rebuilt on (re)connect, so a prefab registered once stays registered rather
// than having to be re-registered after every connection. Keeping the carry as
// a free function over plain dictionaries lets it be exercised in isolation,
// independent of the Unity-only SpawnManager.

using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Core
{
    internal static class PrefabTableOps
    {
        /// <summary>
        /// Copy every entry of <paramref name="source"/> into
        /// <paramref name="target"/>, overwriting on id collision and leaving
        /// any entries present only in <paramref name="target"/> untouched.
        /// A null operand is treated as empty.
        /// </summary>
        internal static void CopyInto(
            IReadOnlyDictionary<uint, GameObject> source,
            IDictionary<uint, GameObject> target)
        {
            if (source == null || target == null) return;

            foreach (var entry in source)
                target[entry.Key] = entry.Value;
        }
    }
}
