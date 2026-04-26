// RTMPE SDK — Runtime/Rpc/RpcTypeRegistry.cs
//
// Lookup table for INetworkSerializable concrete types referenced by the
// RPC wire format.  The wire encoding tags each custom-typed parameter with
// the type's FullName (Namespace.TypeName) so the receiver can Activator-
// instantiate the correct concrete type before calling NetworkDeserialize.
//
// Why a registry vs. a global Type.GetType()?
//   • Type.GetType("Foo.Bar") only resolves names declared in mscorlib /
//     System.Private.CoreLib OR in the calling assembly.  Game types live in
//     Assembly-CSharp (or a custom asmdef), neither of which is the SDK's
//     own assembly.  Without a registry we'd need an assembly-qualified name
//     on the wire — fragile, leaks build-config info, and wastes bandwidth.
//   • The registry self-populates on first use via reflection across the
//     loaded AppDomain.  Authors who want zero-reflection startup may call
//     RpcTypeRegistry.Register<T>() ahead of time; both paths converge to the
//     same dictionary.
//
// Thread safety:
//   • Reads happen on the Unity main thread (RPC dispatch always
//     marshals to main).  Writes happen at static-init time (registry warm-up)
//     and from explicit Register<T>() calls.  We use a ConcurrentDictionary
//     to make explicit Register<T>() calls from any thread safe even if a
//     transport callback happens to run a lookup concurrently.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Maps <see cref="INetworkSerializable"/> type names to their concrete
    /// <see cref="System.Type"/> for fast Activator-based instantiation
    /// during inbound RPC dispatch.
    ///
    /// <para>The registry self-populates the first time
    /// <see cref="Resolve"/> is called by scanning every loaded assembly for
    /// public, non-abstract types that implement
    /// <see cref="INetworkSerializable"/> and have a parameterless
    /// constructor.  Manual <see cref="Register{T}"/> calls bypass the scan
    /// and are safe to call at any time.</para>
    /// </summary>
    public static class RpcTypeRegistry
    {
        // Keyed by Type.FullName (no assembly suffix).  ConcurrentDictionary
        // is used so Register<T>() is safe under concurrent access from a
        // background warm-up thread, even though the steady-state is
        // single-threaded.
        private static readonly ConcurrentDictionary<string, Type> _byName =
            new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        // Tracks whether the AppDomain-wide reflection scan has run.  Used to
        // ensure we scan exactly once instead of on every Resolve miss.
        private static int _scanState; // 0 = not scanned, 1 = scanned

        // Reset on Play-Mode entry so a second run gets a clean registry.
        // Without this the registry would retain stale Type references from
        // assemblies that were unloaded between Play sessions.
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _byName.Clear();
            System.Threading.Interlocked.Exchange(ref _scanState, 0);
        }

        /// <summary>
        /// Pre-register <typeparamref name="T"/> so the first inbound RPC
        /// carrying it does not pay the AppDomain reflection scan.
        ///
        /// <para>Idempotent: registering the same type twice is a no-op.
        /// Calling from any thread is safe.</para>
        /// </summary>
        public static void Register<T>() where T : INetworkSerializable, new()
        {
            var type = typeof(T);
            if (type.FullName != null)
                _byName[type.FullName] = type;
        }

        /// <summary>
        /// Look up the concrete <see cref="Type"/> previously registered (or
        /// discovered via the AppDomain scan) for the given
        /// <paramref name="fullName"/>.  Returns <see langword="null"/> when
        /// no match is found, in which case the caller must surface the
        /// inbound parameter as <see langword="null"/> with a warning.
        /// </summary>
        public static Type Resolve(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_byName.TryGetValue(fullName, out var hit)) return hit;

            EnsureScanned();
            return _byName.TryGetValue(fullName, out hit) ? hit : null;
        }

        /// <summary>
        /// Run the AppDomain-wide reflection scan exactly once.  Subsequent
        /// callers wait briefly behind the CompareExchange and then return
        /// (the dictionary will already contain everything the scan would
        /// have inserted by the time the first scanner returns).
        /// </summary>
        private static void EnsureScanned()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _scanState, 1, 0) != 0)
                return; // another caller already scanned

            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in assemblies)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException ex)
                    {
                        // Some types may be unloadable (e.g. editor-only
                        // assemblies in a player build); use the partial list.
                        types = ex.Types?.Where(t => t != null).ToArray() ?? Array.Empty<Type>();
                    }
                    catch (Exception)
                    {
                        // Defensive: a hostile assembly (or one with broken
                        // reflection metadata) must not abort the scan.
                        continue;
                    }

                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        if (t.IsAbstract) continue;
                        if (t.IsInterface) continue;
                        if (t.IsGenericTypeDefinition) continue;
                        if (!typeof(INetworkSerializable).IsAssignableFrom(t)) continue;
                        // Reference types must have an accessible no-arg ctor; structs always do.
                        if (!t.IsValueType)
                        {
                            var ctor = t.GetConstructor(
                                BindingFlags.Public | BindingFlags.Instance,
                                binder: null, types: Type.EmptyTypes, modifiers: null);
                            if (ctor == null) continue;
                        }
                        if (t.FullName != null)
                            _byName[t.FullName] = t;
                    }
                }
            }
            catch (Exception ex)
            {
                // The scan is best-effort.  A failure here only means
                // automatic discovery did not work — explicit Register<T>()
                // is still available as the deterministic fallback.
                Debug.LogWarning(
                    $"[RTMPE] RpcTypeRegistry: AppDomain scan failed " +
                    $"({ex.GetType().Name}: {ex.Message}).  " +
                    "Use RpcTypeRegistry.Register<T>() to register custom RPC types explicitly.");
            }
        }
    }
}
