// RTMPE SDK — Runtime/Rpc/RpcTypeRegistry.cs
//
// Trust boundary:
//  This registry is the sole authority that maps a wire-supplied
//  INetworkSerializable type name to a concrete System.Type the inbound
//  parser will Activator-instantiate.  Every entry in the dictionary is
//  therefore part of the application's reflection-attack surface — any
//  public type reachable here can be constructed by a remote peer or a
//  relayed packet, and its NetworkDeserialize will run with hostile
//  bytes as input.
//
// Default policy (secure by default):
//  • The dictionary is empty until the application registers types.
//  • Authors mark each RPC payload type with [RtmpeRpcSerializable] or
//    pre-register it via Register<T>().  Types lacking both are NOT
//    resolvable.
//  • The legacy AppDomain-wide reflection scan is gated behind
//    AllowAppDomainScan (default false).  Even when re-enabled the scan
//    is filtered to attributed types only — turning the scan back on
//    never widens the surface beyond what the author opted into via
//    [RtmpeRpcSerializable].
//  • Resolve() returns null for any unregistered type name.  The caller
//    in RpcSerializer.ReadParam logs a warning and surfaces the
//    parameter as null, dropping the offending payload before it can
//    reach a [RtmpeRpc] handler.
//
// Threat model:
//  The wire field "typeName" is attacker-controlled; it MUST NOT be
//  used to navigate the AppDomain.  Resolve() is therefore a strict
//  dictionary lookup against the author-attested registry — no fallback
//  to Type.GetType, no string-based reflection, no assembly probing.
//
// Thread safety:
//  • Reads happen on the Unity main thread (RPC dispatch always
//    marshals to main).  Writes happen at static-init time and from
//    explicit Register<T>() calls.  ConcurrentDictionary keeps
//    Register<T>() safe under concurrent access.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Maps explicitly-registered <see cref="INetworkSerializable"/> type
    /// names to their concrete <see cref="System.Type"/> for inbound RPC
    /// parameter instantiation.
    ///
   /// <para>The registry is closed by default: a wire-supplied type name
    /// only resolves when the application has registered it via
    /// <see cref="Register{T}"/> or attributed it with
    /// <see cref="RtmpeRpcSerializableAttribute"/> AND opted into the
    /// AppDomain scan via <see cref="AllowAppDomainScan"/>.</para>
    /// </summary>
    public static class RpcTypeRegistry
    {
        // Keyed by Type.FullName (no assembly suffix).  ConcurrentDictionary
        // is used so Register<T>() is safe under concurrent access from a
        // background warm-up thread, even though steady-state is single-
        // threaded.
        private static readonly ConcurrentDictionary<string, Type> _byName =
            new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        // Tracks whether the AppDomain-wide reflection scan has run.
        private static int _scanState; // 0 = not scanned, 1 = scanned

        // Opt-in flag for the legacy AppDomain reflection scan.  Disabled
        // by default per the SDK's secure-by-default policy: the scan
        // walks every loaded assembly's type table and was historically
        // a reflection gadget surface even when the discovered types
        // were never intended to be reachable from inbound RPC.
        //
       // When re-enabled, the scan only picks up types decorated with
        // [RtmpeRpcSerializable] — toggling this flag therefore cannot
        // widen the registry beyond the application's attested set.
        // Set to true BEFORE the first inbound RPC arrives, e.g. in an
        // application bootstrap method.
        private static volatile bool _allowAppDomainScan;

        /// <summary>
        /// When <see langword="true"/>, the first call to
        /// <see cref="Resolve"/> performs a one-time AppDomain scan that
        /// auto-registers every type carrying
        /// <see cref="RtmpeRpcSerializableAttribute"/>.  Default is
        /// <see langword="false"/>: the registry only contains types
        /// added via <see cref="Register{T}"/>.
        ///
       /// <para>Security: even when this flag is enabled, the scan
        /// filters strictly by <see cref="RtmpeRpcSerializableAttribute"/>.
        /// Untagged types are never auto-registered.</para>
        /// </summary>
        public static bool AllowAppDomainScan
        {
            get => _allowAppDomainScan;
            set => _allowAppDomainScan = value;
        }

        // Reset on Play-Mode entry so a second run gets a clean registry.
        // Without this the registry would retain stale Type references from
        // assemblies that were unloaded between Play sessions.
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _byName.Clear();
            System.Threading.Interlocked.Exchange(ref _scanState, 0);
            _allowAppDomainScan = false;
        }

        /// <summary>
        /// Explicitly register <typeparamref name="T"/> as an inbound-RPC
        /// payload type.  This is the deterministic, secure path: it adds
        /// exactly one type and never triggers a reflection scan.
        ///
       /// <para>Idempotent.  Safe to call from any thread.</para>
        /// </summary>
        public static void Register<T>() where T : INetworkSerializable, new()
        {
            var type = typeof(T);
            if (type.FullName != null)
                _byName[type.FullName] = type;
        }

        /// <summary>
        /// Variant of <see cref="Register{T}"/> that takes a runtime
        /// <see cref="Type"/>.  Use only when the type is known
        /// dynamically; prefer the generic overload in author-time code so
        /// the compiler enforces the <c>new()</c> and
        /// <see cref="INetworkSerializable"/> constraints.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// The type is not a non-abstract <see cref="INetworkSerializable"/>
        /// with a public parameterless constructor.
        /// </exception>
        public static void Register(Type type)
        {
            if (type == null) throw new ArgumentNullException(nameof(type));
            if (!IsRegistrable(type))
                throw new ArgumentException(
                    $"RpcTypeRegistry.Register: '{type.FullName}' is not a registrable " +
                    "INetworkSerializable type (must be non-abstract, non-generic-definition, " +
                    "and either a value type or a class with a public parameterless ctor).",
                    nameof(type));

            if (type.FullName != null)
                _byName[type.FullName] = type;
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="fullName"/>
        /// has been explicitly registered.  Useful for diagnostic UIs and
        /// for tests that want to assert the registry contents.
        /// </summary>
        public static bool IsRegistered(string fullName)
            => !string.IsNullOrEmpty(fullName) && _byName.ContainsKey(fullName);

        /// <summary>
        /// Snapshot the currently registered type names.  Returned list is
        /// a copy and is safe to iterate without holding the registry
        /// lock.  Order is not specified.
        /// </summary>
        public static IReadOnlyList<string> RegisteredTypeNames()
        {
            // Materialise into a list so callers can iterate without
            // observing concurrent dictionary mutations.
            var keys = _byName.Keys;
            var copy = new List<string>(keys.Count);
            foreach (var k in keys) copy.Add(k);
            return copy;
        }

        /// <summary>
        /// Test-only: clear every registration and reset the scan-state
        /// flag.  NOT exposed to game code — incorrect use here would
        /// reopen the reflection-attack surface.  Reachable from the
        /// SDK test assembly via InternalsVisibleTo.
        /// </summary>
        internal static void ResetForTests()
        {
            _byName.Clear();
            System.Threading.Interlocked.Exchange(ref _scanState, 0);
            _allowAppDomainScan = false;
        }

        /// <summary>
        /// Look up the concrete <see cref="Type"/> previously registered
        /// for <paramref name="fullName"/>.  Returns <see langword="null"/>
        /// for any unregistered name — the caller in
        /// <c>RpcSerializer.ReadParam</c> treats null as a hard failure
        /// and surfaces the parameter as null with a warning.
        ///
       /// <para>Wire-supplied <paramref name="fullName"/> is treated as
        /// hostile input and is never used to navigate the AppDomain
        /// directly.  The only authorised path to a Type is the
        /// dictionary populated by <see cref="Register{T}"/> or the
        /// attribute-filtered AppDomain scan.</para>
        /// </summary>
        public static Type Resolve(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_byName.TryGetValue(fullName, out var hit)) return hit;

            // Optionally consult the attribute-filtered scan.  Disabled by
            // default; even when enabled, only types carrying
            // [RtmpeRpcSerializable] are added.
            if (_allowAppDomainScan)
            {
                EnsureScanned();
                if (_byName.TryGetValue(fullName, out hit)) return hit;
            }

            return null;
        }

        /// <summary>
        /// Run the attribute-filtered AppDomain scan exactly once.
        /// Subsequent callers short-circuit on the CompareExchange.
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
                        if (!IsRegistrable(t)) continue;

                        // Hard filter: ONLY types carrying the explicit
                        // opt-in attribute are admitted.  This is the
                        // load-bearing line of defence against the
                        // reflection-gadget surface.
                        if (t.GetCustomAttribute<RtmpeRpcSerializableAttribute>(inherit: false) == null)
                            continue;

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

        /// <summary>
        /// Common eligibility filter: a type is registrable only when it
        /// is a concrete (non-abstract, non-interface, non-open-generic)
        /// <see cref="INetworkSerializable"/> with a public parameterless
        /// constructor (structs always satisfy the latter).
        /// </summary>
        private static bool IsRegistrable(Type t)
        {
            if (t.IsAbstract) return false;
            if (t.IsInterface) return false;
            if (t.IsGenericTypeDefinition) return false;
            if (!typeof(INetworkSerializable).IsAssignableFrom(t)) return false;
            if (!t.IsValueType)
            {
                var ctor = t.GetConstructor(
                    BindingFlags.Public | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (ctor == null) return false;
            }
            return true;
        }
    }
}
