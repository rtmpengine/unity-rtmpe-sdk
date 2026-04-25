// RTMPE SDK — Runtime/Rpc/RpcRegistry.cs
//
// Discovers [RtmpeRpc]-decorated methods by reflection and maps them to stable
// wire method IDs using FNV-1a 32-bit hashing of "TypeName.MethodName".
//
// Design decisions:
//   • Lazy per-type discovery: a type's methods are scanned on first access,
//     not at app startup.  This avoids Assembly.GetTypes() over all loaded
//     assemblies (expensive on IL2CPP) and eliminates ordering dependencies.
//   • Thread safety: the _cache dictionary is guarded by a lock.  Unity main-
//     thread callers (which is the only supported call site) never contend.
//   • Collision guard: Validate(type) checks that none of the FNV-1a hashes
//     collide with each other or with the reserved manual RpcMethodId constants.
//     Call Validate() from NetworkBehaviour.OnNetworkSpawn to catch problems
//     at object spawn time rather than at first RPC invocation.
//   • Method name uniqueness: two [RtmpeRpc] methods with the same name on the
//     same type produce a hash collision — Validate() treats that as a fatal
//     configuration error (not an overload system).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Maps FNV-1a method IDs to <see cref="MethodInfo"/> entries for all
    /// <see cref="RtmpeRpcAttribute"/>-decorated methods on a given type.
    /// </summary>
    public static class RpcRegistry
    {
        // ── FNV-1a 32-bit constants ────────────────────────────────────────────
        private const uint FnvOffsetBasis = 2166136261u;
        private const uint FnvPrime       = 16777619u;

        // ── Reserved manual method IDs that FNV-1a hashes must not collide with
        private static readonly HashSet<uint> ReservedIds = new HashSet<uint>
        {
            RpcMethodId.Ping,
            RpcMethodId.TransferOwnership,
            RpcMethodId.RequestDamage,
            RpcMethodId.ApplyDamage,
            RpcMethodId.GameStateChange,
            RpcMethodId.SyncGameState,
        };

        // ── Per-type cache: Type → Dictionary<methodId, (MethodInfo, attr)> ──
        private static readonly Dictionary<Type, Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>> _cache
            = new Dictionary<Type, Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>>();

        private static readonly object _lock = new object();

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Compute the FNV-1a 32-bit hash of <c>"TypeName.MethodName"</c>.
        /// This is the stable wire method ID used in Enhanced RPC packets.
        /// </summary>
        public static uint ComputeMethodId(string typeName, string methodName)
        {
            string key  = typeName + "." + methodName;
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            uint hash   = FnvOffsetBasis;
            foreach (byte b in bytes)
                hash = (hash ^ b) * FnvPrime;
            return hash;
        }

        /// <summary>
        /// Look up the <see cref="MethodInfo"/> for <paramref name="methodId"/> on
        /// <paramref name="type"/>, together with its <see cref="RtmpeRpcAttribute"/>.
        /// Returns <see langword="false"/> when the type has no [RtmpeRpc] method
        /// with that ID.
        /// </summary>
        public static bool TryFindMethod(
            Type type,
            uint methodId,
            out MethodInfo method,
            out RtmpeRpcAttribute attr)
        {
            var map = GetOrBuild(type);
            if (map.TryGetValue(methodId, out var entry))
            {
                method = entry.Method;
                attr   = entry.Attr;
                return true;
            }

            method = null;
            attr   = null;
            return false;
        }

        /// <summary>
        /// Look up the wire method ID for a named [RtmpeRpc] method on
        /// <paramref name="type"/>.
        /// Returns <see langword="false"/> when no such method is registered
        /// (wrong name or missing attribute).
        /// </summary>
        public static bool TryGetMethodId(Type type, string methodName, out uint methodId)
        {
            methodId = ComputeMethodId(type.Name, methodName);
            var map  = GetOrBuild(type);
            return map.ContainsKey(methodId);
        }

        /// <summary>
        /// Validate all [RtmpeRpc] methods on <paramref name="type"/> for hash
        /// collisions with reserved IDs or with each other.  Called automatically
        /// from <c>NetworkBehaviour.OnNetworkSpawn</c> for early error detection.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when one or more [RtmpeRpc] methods on <paramref name="type"/>
        /// produce an FNV-1a hash that collides with a reserved
        /// <see cref="RpcMethodId"/> constant or with another method on the same
        /// type.  The exception message lists every conflicting method so the
        /// developer can rename them in a single pass.
        /// </exception>
        public static void Validate(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            var collisions = CollectCollisions(type);
            if (collisions.Count == 0) return;

            var sb = new StringBuilder();
            sb.Append("[RTMPE] RpcRegistry.Validate: ");
            sb.Append(collisions.Count);
            sb.Append(" RPC method ID collision(s) detected on type '");
            sb.Append(type.Name);
            sb.Append("':");
            foreach (var c in collisions)
            {
                sb.Append("\n  • ");
                sb.Append(c);
            }
            sb.Append("\nRename the conflicting [RtmpeRpc] methods to resolve.");
            throw new InvalidOperationException(sb.ToString());
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)> GetOrBuild(Type type)
        {
            lock (_lock)
            {
                if (_cache.TryGetValue(type, out var existing))
                    return existing;

                var map = BuildMap(type);
                _cache[type] = map;
                return map;
            }
        }

        private static Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)> BuildMap(Type type)
        {
            var map = new Dictionary<uint, (MethodInfo Method, RtmpeRpcAttribute Attr)>();

            // Scan all public instance methods declared on this specific type.
            // We do NOT include inherited methods — a base class registers its own
            // map separately, preventing duplicate dispatch when a derived type
            // overrides or shadows an RPC method.
            var methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public   |
                BindingFlags.DeclaredOnly);

            foreach (var mi in methods)
            {
                var attr = mi.GetCustomAttribute<RtmpeRpcAttribute>(inherit: false);
                if (attr == null) continue;

                uint id = ComputeMethodId(type.Name, mi.Name);

                // Check against reserved manual IDs.
                if (ReservedIds.Contains(id))
                {
                    Debug.LogError(
                        $"[RTMPE] RpcRegistry: method '{type.Name}.{mi.Name}' produces FNV-1a " +
                        $"hash 0x{id:X8} which collides with a reserved RpcMethodId. " +
                        "Rename the method to resolve the collision.");
                    continue;
                }

                // Check for intra-type collision (same type, two methods hash to same ID).
                if (map.ContainsKey(id))
                {
                    Debug.LogError(
                        $"[RTMPE] RpcRegistry: method '{type.Name}.{mi.Name}' has FNV-1a " +
                        $"hash 0x{id:X8} that collides with another [RtmpeRpc] method on the " +
                        "same type. Rename one of the methods to resolve the collision.");
                    continue;
                }

                map[id] = (mi, attr);
            }

            return map;
        }

        /// <summary>
        /// Re-scans <paramref name="type"/> from scratch (independent of the
        /// per-type cache built by <see cref="BuildMap"/>) and returns the list
        /// of collision descriptions.  Empty list ⇒ no collisions.
        /// </summary>
        /// <remarks>
        /// We deliberately do NOT consult <see cref="GetOrBuild"/> here:
        /// <see cref="BuildMap"/> drops collided methods and only logs them, so
        /// the cached map is not authoritative for collision detection.
        /// Re-scanning is O(methods) and runs once per type at first
        /// <see cref="OnNetworkSpawn"/> — negligible.
        /// </remarks>
        private static List<string> CollectCollisions(Type type)
        {
            var collisions = new List<string>();
            var seen       = new Dictionary<uint, string>();

            var methods = type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Public   |
                BindingFlags.DeclaredOnly);

            foreach (var mi in methods)
            {
                var attr = mi.GetCustomAttribute<RtmpeRpcAttribute>(inherit: false);
                if (attr == null) continue;

                uint id = ComputeMethodId(type.Name, mi.Name);

                if (ReservedIds.Contains(id))
                {
                    collisions.Add(
                        $"'{type.Name}.{mi.Name}' (FNV-1a 0x{id:X8}) collides with reserved RpcMethodId");
                    continue;
                }

                if (seen.TryGetValue(id, out var prior))
                {
                    collisions.Add(
                        $"'{type.Name}.{mi.Name}' (FNV-1a 0x{id:X8}) collides with prior '{prior}'");
                    continue;
                }

                seen[id] = mi.Name;
            }

            return collisions;
        }
    }
}
