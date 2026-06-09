// RTMPE SDK — Runtime/Core/NetworkBehaviour.cs
//
// Base class for all networked GameObjects in RTMPE.
//
// Design decisions:
//  • _ownerPlayerId is a string (UUID) to match PlayerInfo.PlayerId and the
//    room service player identifiers.  The gateway session ID (u64) is a
//    DIFFERENT concept stored in NetworkManager.LocalPlayerId.
//  • IsOwner compares string UUIDs and short-circuits on null/empty so that
//    uninitialized objects never falsely claim ownership (unlike a ulong==0
//    comparison which would return true for every uninitialized object).
//  • Initialize / SetSpawned / SetOwner are internal so only the RTMPE SDK
//    itself (SpawnManager) can mutate network object state.
//    RTMPE.SDK.Tests can also call them via InternalsVisibleTo (AssemblyInfo.cs).
//  • DestroyWithOwner is declared here; enforcement is implemented by
//    SpawnManager when it handles PlayerLeft events.
//  • IsOwner accesses NetworkManager.Instance — safe for main-thread MonoBehaviour
//    code (OnNetworkSpawn, OnOwnershipChanged, etc. all run on main thread).

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using RTMPE.Rpc;
using RTMPE.Sync;

namespace RTMPE.Core
{
    /// <summary>
    /// Base class for all RTMPE-networked components.
    /// Attach to a <c>GameObject</c> that will be spawned across the network via
    /// <c>SpawnManager</c>.
    /// </summary>
    public abstract class NetworkBehaviour : MonoBehaviour
    {
        // ── State ──────────────────────────────────────────────────────────────

        private ulong  _networkObjectId;
        private string _ownerPlayerId = string.Empty;
        private bool   _isSpawned;

        // Captured at spawn so IsOwner remains correct after the
        // NetworkManager singleton has been torn down (OnApplicationQuit,
        // domain reload, scene unload).  Without this snapshot OnDestroy of a
        // spawned prefab cannot distinguish owner-only cleanup paths from
        // remote-replica paths once Instance returns null, and the owning
        // client leaks any native resources owned by the IsOwner branch.
        // Refreshed by SetSpawned(true) on every spawn transition so a
        // reconnect that produces a new local player id picks up the latest
        // value before user code sees IsOwner.
        private string _cachedLocalPlayerId = string.Empty;

        // List of all NetworkVariables registered during OnNetworkSpawn.
        // Populated by NetworkVariableBase constructors via TrackVariable().
        // Flushed at 30 Hz by NetworkManager for owner clients.
        private readonly List<NetworkVariableBase> _trackedVariables =
            new List<NetworkVariableBase>();

        // Per-type cache of (field/property → NetworkVariableAttribute) maps.
        // Built lazily once per concrete NetworkBehaviour subclass on first
        // TrackVariable() invocation; reused for every subsequent spawn of the
        // same type so the reflection scan is amortised across the whole
        // application lifetime.  HashSet<Type> reads are lock-free under the
        // main-thread invariant, and Dictionary<Type, …> follows the same
        // single-thread access pattern.
        private static readonly Dictionary<Type, IReadOnlyList<NetworkVariableMetadata>>
            _variableMetadataCache =
                new Dictionary<Type, IReadOnlyList<NetworkVariableMetadata>>();

        // Cached reflection result for a single field or property carrying a
        // NetworkVariableAttribute.  Stored once per declaring type and matched
        // to instance variables by reading the field/property value.
        private readonly struct NetworkVariableMetadata
        {
            public readonly FieldInfo    Field;     // null when the source is a property
            public readonly PropertyInfo Property;  // null when the source is a field
            public readonly float        SendRateHz;

            public NetworkVariableMetadata(FieldInfo field, float sendRateHz)
            {
                Field      = field;
                Property   = null;
                SendRateHz = sendRateHz;
            }

            public NetworkVariableMetadata(PropertyInfo property, float sendRateHz)
            {
                Field      = null;
                Property   = property;
                SendRateHz = sendRateHz;
            }

            public object ReadValue(object instance) =>
                Field != null ? Field.GetValue(instance) : Property.GetValue(instance);
        }

        // RPC-collision validation cache.  Each concrete NetworkBehaviour subclass
        // is checked exactly once on first spawn; subsequent spawns of the same
        // type skip the reflection scan.  HashSet<Type> reads are lock-free under
        // the main-thread invariant (all spawns happen on the Unity main thread).
        private static readonly HashSet<Type> _validatedTypes = new HashSet<Type>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetValidatedTypes()
        {
            _validatedTypes.Clear();
            _variableMetadataCache.Clear();
        }

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
       /// Compares <see cref="OwnerPlayerId"/> against the local player UUID
        /// captured at <c>OnNetworkSpawn</c> time from
        /// <see cref="NetworkManager.LocalPlayerStringId"/>.  The captured
        /// snapshot survives <c>NetworkManager</c> teardown so
        /// <c>OnDestroy</c> can still take the owner-only cleanup branch
        /// after the application has begun quitting.
        ///
       /// Returns <see langword="false"/> when either ID is null or empty,
        /// preventing false-positive ownership on uninitialized objects.
        /// </summary>
        public bool IsOwner
        {
            get
            {
                if (string.IsNullOrEmpty(_ownerPlayerId)) return false;
                // Read the cached id captured at spawn rather than reaching
                // through NetworkManager.Instance: this is a hot-path property
                // (sampled per-frame by NetworkVariable flush, NetworkTransform,
                // user code) and it must remain authoritative after Instance
                // has been torn down so OnDestroy can run owner-only cleanup.
                if (string.IsNullOrEmpty(_cachedLocalPlayerId)) return false;
                return _ownerPlayerId == _cachedLocalPlayerId;
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
        /// Enforcement is performed by <c>SpawnManager</c>.
        /// </summary>
        public bool DestroyWithOwner { get; set; } = true;

        // ── Enhanced RPC API ──────────────────────────────────────────────────

        /// <summary>
        /// Send an Enhanced RPC call to the network.
        /// The method named <paramref name="methodName"/> must exist on this
        /// component's type and be decorated with <see cref="RtmpeRpcAttribute"/>.
        /// Delivery audience is taken from the attribute (<c>All</c>, <c>Others</c>,
        /// or <c>Server</c>).
        ///
       /// <para>Must be called from the Unity main thread while connected and in a room.</para>
        /// </summary>
        /// <param name="methodName">
        /// Name of a public, non-static method on this type decorated with
        /// <c>[RtmpeRpc]</c>.  The name is resolved via <see cref="RpcRegistry"/>
        /// (FNV-1a hash of <c>"TypeName.MethodName"</c>).
        /// </param>
        /// <param name="args">
        /// Typed arguments forwarded to the remote method.  Supported types:
        /// <c>int</c>, <c>float</c>, <c>bool</c>, <c>string</c>, <c>byte[]</c>,
        /// <c>ulong</c>, <c>Vector3</c>, <c>Color</c>, <c>Quaternion</c>.
        /// </param>
        public void RPC(string methodName, params object[] args)
        {
            var nm = NetworkManager.Instance;
            if (nm == null)
            {
                Debug.LogWarning("[RTMPE] NetworkBehaviour.RPC: NetworkManager not available.");
                return;
            }
            nm.SendEnhancedRpc(this, methodName, args);
        }

        /// <summary>
        /// Dispatch an inbound Enhanced RPC to the [RtmpeRpc]-decorated method
        /// with the matching method ID.  Called by <c>NetworkManager</c> after it
        /// resolves the target object from the registry.
        /// </summary>
        /// <param name="methodId">FNV-1a method ID of the resolved RPC.</param>
        /// <param name="wireTarget">
        /// The <see cref="RTMPE.Rpc.RpcTarget"/> audience decoded from the
        /// packet; checked against the method's declared audience.
        /// </param>
        /// <param name="args">Deserialized argument vector.</param>
        internal void DispatchEnhancedRpc(uint methodId, RTMPE.Rpc.RpcTarget wireTarget, object[] args)
        {
            // Lifecycle gate: an RPC that lands after OnNetworkDespawn or
            // before OnNetworkSpawn has no business mutating component state.
            // Reflection-based dispatch would happily Invoke and the method
            // body might read NetworkBehaviour.IsOwner / NetworkManager.Instance
            // and observe a half-torn-down object.  Drop with a single warning
            // so the operator can spot the protocol-level race in player logs.
            if (!_isSpawned)
            {
                RTMPE.Core.RtmpeLog.Warning(
                    $"[RTMPE] RPC dispatched to non-spawned NetworkBehaviour " +
                    $"({GetType().Name}, methodId=0x{methodId:X8}); dropping.");
                return;
            }

            if (!RpcRegistry.TryFindMethod(
                    GetType(), methodId, out MethodInfo method, out RtmpeRpcAttribute attr))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour: no [RtmpeRpc] method with id 0x{methodId:X8} " +
                    $"on {GetType().Name}. Check that the method exists and is decorated with [RtmpeRpc].");
                return;
            }

            // Audience contract: the [RtmpeRpc] declaration is authoritative.
            // An inbound call whose wire audience diverges from the declared
            // audience — or that targets the server, which a client must never
            // execute locally — is refused before any argument is bound.
            if (!RTMPE.Rpc.EnhancedRpcVerifier.IsDispatchPermitted(attr.Target, wireTarget))
            {
                Debug.LogWarning(
                    $"[RTMPE] RPC '{GetType().Name}.{method.Name}' refused: wire target " +
                    $"{wireTarget} is not permitted for a method declared {attr.Target}.");
                return;
            }

            // Validate the deserialized argument vector against the method
            // signature BEFORE invoking.  MethodBase.Invoke would otherwise
            // throw TargetParameterCountException / ArgumentException with a
            // generic message that gives the operator no clue which RPC the
            // server-supplied payload failed to satisfy.  A stale registry on
            // the client (e.g. running an older build than the server) is the
            // most common cause of this mismatch in practice.
            var parameters = method.GetParameters();
            int suppliedCount = args == null ? 0 : args.Length;
            if (suppliedCount != parameters.Length)
            {
                Debug.LogError(
                    $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg count mismatch: " +
                    $"server sent {suppliedCount}, method expects {parameters.Length}. " +
                    "Likely cause: client and server SDK are out of sync.");
                return;
            }
            for (int i = 0; i < parameters.Length; i++)
            {
                object value = args[i];
                Type expected = parameters[i].ParameterType;
                if (value == null)
                {
                    // Reference / nullable types accept null; value types do not.
                    if (expected.IsValueType && Nullable.GetUnderlyingType(expected) == null)
                    {
                        Debug.LogError(
                            $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg #{i} is null " +
                            $"but parameter '{parameters[i].Name}' is non-nullable {expected.Name}.");
                        return;
                    }
                    continue;
                }
                if (!expected.IsInstanceOfType(value))
                {
                    Debug.LogError(
                        $"[RTMPE] RPC '{GetType().Name}.{method.Name}' arg #{i} type mismatch: " +
                        $"got {value.GetType().Name}, parameter '{parameters[i].Name}' expects {expected.Name}.");
                    return;
                }
            }

            try
            {
                method.Invoke(this, args);
            }
            catch (TargetInvocationException tie)
            {
                Debug.LogError(
                    $"[RTMPE] RPC method '{GetType().Name}.{method.Name}' threw: " +
                    $"{tie.InnerException?.Message ?? tie.Message}");
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] RPC dispatch error for '{GetType().Name}.{method.Name}': {ex.Message}");
            }
        }

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

        /// <summary>
        /// Reconcile SpawnManager bookkeeping when this object is destroyed
        /// via Unity's API (e.g. <c>Object.Destroy(gameObject)</c> from user
        /// code) without going through <see cref="SpawnManager.DestroyLocal"/>.
        /// Without this hook the SpawnManager's live-count never decrements
        /// for the bypass path and the per-room cap eventually saturates;
        /// the prefab side-map and registry slot also leak.
        /// <para>
        /// Subclasses that override <c>OnDestroy</c> MUST call <c>base.OnDestroy()</c>
        /// or arrange equivalent cleanup, otherwise the symptoms above return.
        /// </para>
        /// </summary>
        protected virtual void OnDestroy()
        {
            // Idempotency flag: when DestroyLocal already ran, registry-eviction
            // and counter decrement happened there.  SpawnManager's
            // OnExternallyDestroyed double-checks via the registry so the
            // counters cannot move twice — this flag is a fast-path skip.
            if (_externallyEvicted) return;
            _externallyEvicted = true;

            // During application shutdown the singleton accessor may itself
            // throw (object finalisation order is undefined); swallow so the
            // shutdown path remains exception-free.
            try
            {
                var nm = NetworkManager.Instance;
                nm?.SpawnManagerInternal?.OnExternallyDestroyed(_networkObjectId);
            }
            catch { /* best-effort during shutdown */ }
        }

        // Set by either DestroyLocal (when SpawnManager owns the teardown)
        // or by OnDestroy itself (when Unity destroys the GameObject before
        // DestroyLocal runs).  Either way the second caller observes true
        // and skips the reconciliation work.  Internal so SpawnManager can
        // mark the flag from its DestroyLocal path.
        private bool _externallyEvicted;
        internal void MarkExternallyEvicted() => _externallyEvicted = true;

        // Latched once per instance the first time FlushDirtyVariables hits
        // the 255-variable wire-format cap.  Prevents the diagnostic warning
        // from spamming every flush tick when the configuration is permanent.
        private bool _overflowWarned;

        // Cached serialization resources for FlushDirtyVariables.  Lazy-init
        // on the first dirty flush so objects that are never dirty (read-only
        // replicas) pay zero cost.  MemoryStream and BinaryWriter hold no
        // unmanaged handles; GC collects them when the behaviour is destroyed.
        // Reset via SetLength(0) before each flush — reuses the internal buffer
        // that the stream already allocated, eliminating per-tick heap churn.
        private MemoryStream _flushMs;
        private BinaryWriter _flushWriter;

        // ── Sync-component cache ────────────────────────────────────────────
        //
        // The receive hot-path (HandleStateSyncPacket / HandlePhysicsSyncPacket
        // / HandlePhysicsSync2DPacket / HandleVariableUpdatePacket) dispatches
        // each inbound packet to one of these sync components on the same
        // GameObject.  Unity 2022+ resolves GetComponent<T> in O(1) via an
        // internal typed cache (~50–100 ns/call), but at 30 Hz × 8 peers that
        // tallies ~24 µs/sec per object spent on type lookups; under IL2CPP
        // without the JIT cache the overhead is materially higher.
        //
        // These fields hold the lazily-resolved sync components for this
        // GameObject so the hot-path resolves to a single field load.
        // Lookups are gated by a "queried" flag so an object that genuinely
        // lacks a sync component pays the GetComponent cost exactly once
        // per spawn cycle rather than per packet.  Both fields and flags
        // are cleared on despawn — see [ResetSyncComponentCache].
        //
        // The Unity null operator (==) is used everywhere instead of `is`
        // so a destroyed-but-not-finalised component is treated as missing
        // and re-queried; `(object)` checks would observe the destroyed
        // wrapper and incorrectly skip the refetch.
        [NonSerialized] private NetworkTransform              _cachedNetworkTransform;
        [NonSerialized] private bool                          _cachedNetworkTransformQueried;
        [NonSerialized] private NetworkTransformInterpolator  _cachedNetworkTransformInterpolator;
        [NonSerialized] private bool                          _cachedNetworkTransformInterpolatorQueried;
        [NonSerialized] private NetworkRigidbody              _cachedNetworkRigidbody;
        [NonSerialized] private bool                          _cachedNetworkRigidbodyQueried;
        [NonSerialized] private NetworkRigidbody2D            _cachedNetworkRigidbody2D;
        [NonSerialized] private bool                          _cachedNetworkRigidbody2DQueried;

        /// <summary>
        /// Sync-component accessor used by the receive hot-path.  Caches
        /// <see cref="UnityEngine.Component.GetComponent{T}"/> after the
        /// first call within a spawn cycle so per-packet dispatch resolves
        /// to a field load rather than a typed component lookup.
        /// </summary>
        internal NetworkTransform CachedNetworkTransform
        {
            get
            {
                if (_cachedNetworkTransform != null) return _cachedNetworkTransform;
                if (_cachedNetworkTransformQueried) return null;
                _cachedNetworkTransform = GetComponent<NetworkTransform>();
                _cachedNetworkTransformQueried = true;
                return _cachedNetworkTransform;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkTransformInterpolator CachedNetworkTransformInterpolator
        {
            get
            {
                if (_cachedNetworkTransformInterpolator != null) return _cachedNetworkTransformInterpolator;
                if (_cachedNetworkTransformInterpolatorQueried) return null;
                _cachedNetworkTransformInterpolator = GetComponent<NetworkTransformInterpolator>();
                _cachedNetworkTransformInterpolatorQueried = true;
                return _cachedNetworkTransformInterpolator;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkRigidbody CachedNetworkRigidbody
        {
            get
            {
                if (_cachedNetworkRigidbody != null) return _cachedNetworkRigidbody;
                if (_cachedNetworkRigidbodyQueried) return null;
                _cachedNetworkRigidbody = GetComponent<NetworkRigidbody>();
                _cachedNetworkRigidbodyQueried = true;
                return _cachedNetworkRigidbody;
            }
        }

        /// <summary>
        /// Sync-component accessor — see <see cref="CachedNetworkTransform"/>.
        /// </summary>
        internal NetworkRigidbody2D CachedNetworkRigidbody2D
        {
            get
            {
                if (_cachedNetworkRigidbody2D != null) return _cachedNetworkRigidbody2D;
                if (_cachedNetworkRigidbody2DQueried) return null;
                _cachedNetworkRigidbody2D = GetComponent<NetworkRigidbody2D>();
                _cachedNetworkRigidbody2DQueried = true;
                return _cachedNetworkRigidbody2D;
            }
        }

        /// <summary>
        /// Wipe the sync-component cache.  Invoked on every despawn so a
        /// pool-recycled instance does not retain references from its
        /// previous spawn — components attached after pool re-acquire will
        /// be picked up by the next hot-path access.
        /// </summary>
        private void ResetSyncComponentCache()
        {
            _cachedNetworkTransform                   = null;
            _cachedNetworkTransformQueried            = false;
            _cachedNetworkTransformInterpolator       = null;
            _cachedNetworkTransformInterpolatorQueried = false;
            _cachedNetworkRigidbody                   = null;
            _cachedNetworkRigidbodyQueried            = false;
            _cachedNetworkRigidbody2D                 = null;
            _cachedNetworkRigidbody2DQueried          = false;
        }

        // ── Internal SDK API (called by SpawnManager) ──────────────────────────

        /// <summary>
        /// Initialise the network identity of this object.
        /// Called by <c>SpawnManager</c> immediately after instantiation.
        /// </summary>
        /// <param name="objectId">Server-assigned unique object ID (u64).</param>
        /// <param name="ownerId">Room player UUID of the object's owner.</param>
        internal void Initialize(ulong objectId, string ownerId)
        {
            // Guard against double-initialisation — warn if the object is already
            // spawned, which can occur via duplicate Spawn packets on reliable transport.
            if (_networkObjectId != 0)
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour.Initialize called twice on object " +
                    $"{_networkObjectId} → overwriting with {objectId}. " +
                    "Possible duplicate Spawn packet received via reliable transport retransmit.");

            // Clear the externally-evicted latch so a pool-recycled instance
            // starts each spawn with the same baseline as a fresh
            // GameObject.  Without the reset, the latch set during the
            // previous DestroyLocal would short-circuit OnDestroy on the
            // next user-driven Object.Destroy(go), skipping
            // OnExternallyDestroyed and leaking one slot from
            // SpawnManager._currentSpawnCount per pool re-acquire cycle.
            _externallyEvicted = false;

            _networkObjectId = objectId;
            _ownerPlayerId   = ownerId ?? string.Empty;

            // Snapshot the local player UUID at construction so IsOwner remains
            // correct after the NetworkManager singleton has been torn down
            // (OnApplicationQuit, scene unload, domain reload).  Refreshed
            // again at SetSpawned(true) to absorb any reconnect-driven id
            // change that lands between Initialize and the spawn transition.
            var nm = NetworkManager.Instance;
            _cachedLocalPlayerId = nm != null
                ? (nm.LocalPlayerStringId ?? string.Empty)
                : string.Empty;
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
                // Snapshot the local player UUID before user code in
                // OnNetworkSpawn observes IsOwner.  Capturing here (rather
                // than in Initialize) ensures that a reconnect which mints a
                // new local player id is picked up by the next spawn cycle —
                // SetSpawned(true) is invoked after the registry adopts the
                // post-reconnect identity.  Instance is allowed to be null
                // here only during teardown; in that case the empty cached
                // id correctly causes IsOwner to return false.
                var nm = NetworkManager.Instance;
                _cachedLocalPlayerId = nm != null
                    ? (nm.LocalPlayerStringId ?? string.Empty)
                    : string.Empty;

                _isSpawned = true;
                ValidateRpcMethodsOnce();
                OnNetworkSpawn();
            }
            else if (!spawned && _isSpawned)
            {
                _isSpawned = false;
                OnNetworkDespawn();
                // Wipe the sync-component cache after user code has run so
                // OnNetworkDespawn handlers can still observe the cached
                // references during teardown.  A pool-recycled instance
                // re-acquired by SpawnManager will populate the cache
                // afresh on its next hot-path access.
                ResetSyncComponentCache();
            }
        }

        /// <summary>
        /// Run <see cref="RpcRegistry.Validate"/> exactly once per concrete
        /// subclass.  RPC ID collisions are a programming error that must be
        /// fixed before shipping; the runtime logs them as a Unity error so
        /// they are visible in the Editor console and in player logs.
        /// </summary>
        /// <remarks>
        /// We log + swallow rather than throw, because a single misbehaving
        /// prefab should not abort the spawn pipeline for other (correctly
        /// authored) objects.  Tests and editor tooling that want a hard
        /// failure should call <see cref="RpcRegistry.Validate"/> directly.
        /// </remarks>
        private void ValidateRpcMethodsOnce()
        {
            var type = GetType();
            if (_validatedTypes.Contains(type)) return;
            _validatedTypes.Add(type);
            try
            {
                RpcRegistry.Validate(type);
            }
            catch (InvalidOperationException ex)
            {
                Debug.LogError(ex.Message, this);
            }
        }

        /// <summary>
        /// Apply a server-confirmed ownership change.
        /// Only call from <c>OwnershipManager.ApplyOwnershipGrant</c>.
        /// </summary>
        /// <param name="newOwner">Room player UUID of the new owner.</param>
        internal void SetOwner(string newOwner)
        {
            // Suppress no-change callbacks to avoid redundant notifications on
            // retransmitted ownership updates.
            var normalized = newOwner ?? string.Empty;
            if (_ownerPlayerId == normalized) return;

            var previous   = _ownerPlayerId;
            _ownerPlayerId = normalized;

            // Reset per-variable throttle state on every ownership transfer so
            // the new owner's first flush is not gated behind a stale
            // LastFlushTimeUnscaled inherited from the previous owner's
            // bookkeeping.  Cheap (one float assignment per variable) and
            // correctness-critical for variables with low SendRateHz where the
            // throttle interval can exceed the gap between ownership handoffs.
            //
            // Reset the inbound-tick gate as well: the new owner's first
            // VariableUpdate may carry a tick lower than the highest tick the
            // previous owner observed (a different sender's tick clock), and
            // without resetting the gate that update would be dropped as a
            // stale replay until the new owner's tick passed the high-water
            // mark — visible to the user as a multi-second silent under-
            // replication after every handoff.
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                _trackedVariables[i].ResetThrottleState();
                _trackedVariables[i].ResetInboundTickGate();
            }

            OnOwnershipChanged(previous, _ownerPlayerId);
        }

        // ── NetworkVariable registration and flush ─────────────────────────────

        /// <summary>
        /// Register a <see cref="NetworkVariableBase"/> with this behaviour so it
        /// participates in the 30 Hz dirty-flush loop.
        /// Called automatically by the <c>NetworkVariableBase</c> constructor —
        /// user code should never call this directly.
        /// </summary>
        /// <summary>
        /// Read-only snapshot of every <see cref="NetworkVariableBase"/>
        /// registered with this behaviour.  Used by Editor tooling (Network
        /// Debugger window) to enumerate the live variable set without
        /// duplicating the bookkeeping that lives on the SDK side.
        ///
       /// <para>The returned list is the live tracking list — do not mutate
        /// it.  Callers must inspect on the Unity main thread.</para>
        /// </summary>
        public IReadOnlyList<NetworkVariableBase> TrackedVariables => _trackedVariables;

        internal void TrackVariable(NetworkVariableBase variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));

            // VariableId must be unique within a behaviour: the inbound update
            // path dispatches to the first match by id, so a duplicate would
            // leave every later variable permanently unreplicated.  Surface the
            // clash at registration (OnNetworkSpawn) instead of letting it
            // manifest as silent desync — mirroring the RpcRegistry method-id
            // collision guard.
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                if (_trackedVariables[i].VariableId == variable.VariableId)
                    throw new InvalidOperationException(
                        $"[RTMPE] {GetType().Name}: two NetworkVariables share " +
                        $"VariableId {variable.VariableId}.  Give each NetworkVariable " +
                        "on a behaviour a unique id — inbound updates dispatch by id and " +
                        "would otherwise reach only the first.");
            }

            _trackedVariables.Add(variable);

            // Apply [NetworkVariable(SendRateHz = …)] declaratively, matching
            // attribute declarations to the variable instance by reading the
            // field/property value.  Handled inline so dynamically created
            // NetworkVariable instances (e.g. via TrackVariable from a
            // non-attributed source) keep their default 0 (use global cadence).
            ApplyVariableAttributesIfAny(variable);
        }

        /// <summary>
        /// Match <see cref="NetworkVariableAttribute"/> declarations on this
        /// behaviour's type to <paramref name="variable"/> by reading each
        /// candidate field/property and comparing the value reference against
        /// <paramref name="variable"/>.  When a match is found the attribute's
        /// <see cref="NetworkVariableAttribute.SendRateHz"/> is copied onto the
        /// variable instance so the per-tick flush loop can throttle it.
        ///
       /// <para>The reflection scan is performed at most once per concrete
        /// subclass; subsequent spawns reuse the cached metadata list.</para>
        /// </summary>
        private void ApplyVariableAttributesIfAny(NetworkVariableBase variable)
        {
            var type     = GetType();
            var metadata = GetOrBuildMetadata(type);
            if (metadata.Count == 0) return;

            for (int i = 0; i < metadata.Count; i++)
            {
                var entry = metadata[i];
                object held;
                try { held = entry.ReadValue(this); }
                catch (Exception ex)
                {
                    // A property-getter throwing should not abort registration of
                    // a sibling variable — log once and move on.
                    Debug.LogWarning(
                        $"[RTMPE] NetworkVariable attribute scan: reading " +
                        $"{type.Name}.{(entry.Field?.Name ?? entry.Property?.Name)} threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Skipping this declaration.");
                    continue;
                }

                if (!ReferenceEquals(held, variable)) continue;

                variable.SendRateHz = entry.SendRateHz;
                return; // a single field/property declares a single variable
            }
        }

        /// <summary>
        /// Build (and cache) the list of <see cref="NetworkVariableAttribute"/>
        /// declarations for <paramref name="type"/>.  Declared fields/properties
        /// up the inheritance chain are included; static members are skipped.
        /// </summary>
        private static IReadOnlyList<NetworkVariableMetadata> GetOrBuildMetadata(Type type)
        {
            if (_variableMetadataCache.TryGetValue(type, out var cached)) return cached;

            var list = new List<NetworkVariableMetadata>();

            // Walk the inheritance chain so attributes declared on a base
            // class are honoured for subclasses too.  Stop at NetworkBehaviour
            // because anything above it (MonoBehaviour, Component, Object)
            // cannot legally hold NetworkVariable fields.
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            for (Type t = type; t != null && t != typeof(NetworkBehaviour); t = t.BaseType)
            {
                foreach (var f in t.GetFields(flags))
                {
                    if (!typeof(NetworkVariableBase).IsAssignableFrom(f.FieldType)) continue;
                    var attr = f.GetCustomAttribute<NetworkVariableAttribute>(inherit: true);
                    if (attr == null) continue;
                    list.Add(new NetworkVariableMetadata(f, attr.SendRateHz));
                }

                foreach (var p in t.GetProperties(flags))
                {
                    if (!p.CanRead) continue;
                    if (!typeof(NetworkVariableBase).IsAssignableFrom(p.PropertyType)) continue;
                    var attr = p.GetCustomAttribute<NetworkVariableAttribute>(inherit: true);
                    if (attr == null) continue;
                    // Indexer properties are not supported (they require an index argument).
                    if (p.GetIndexParameters().Length != 0) continue;
                    list.Add(new NetworkVariableMetadata(p, attr.SendRateHz));
                }
            }

            IReadOnlyList<NetworkVariableMetadata> result = list;
            _variableMetadataCache[type] = result;
            return result;
        }

        /// <summary>
        /// Backward-compatibility shim that copies the cached buffer's
        /// leading <c>length</c> bytes into a fresh <c>byte[]</c> before
        /// forwarding to the caller.  Production hot-paths use the
        /// <see cref="FlushDirtyVariables(Action{byte[], int})"/> overload
        /// directly to avoid this per-call copy; this overload exists for
        /// SDK test fixtures that pre-date the GC Round 2 (2026-05-02)
        /// signature change.
        /// </summary>
        internal void FlushDirtyVariables(Action<byte[]> sendPayload)
        {
            if (sendPayload == null) return;
            FlushDirtyVariables((buf, len) =>
            {
                var copy = new byte[len];
                if (len > 0) System.Buffer.BlockCopy(buf, 0, copy, 0, len);
                sendPayload(copy);
            });
        }

        /// <summary>
        /// Serialize all dirty tracked variables into a single <c>VariableUpdate</c>
        /// payload and call <paramref name="sendPayload"/> with it.
        /// No-op when not spawned, not owner, or all variables are clean.
        /// Called by <c>NetworkManager.FlushDirtyNetworkVariables</c> at 30 Hz.
        /// </summary>
        /// <param name="sendPayload">
        /// Delegate that transmits the built payload bytes.  Receives
        /// <c>(buffer, length)</c> — only the leading <c>length</c> bytes of
        /// <c>buffer</c> are valid (the buffer is the cached MemoryStream's
        /// internal array, which may be larger than the logical payload).
        /// Implementations must NOT retain a reference to the buffer past
        /// the call: it is reused on the next flush tick.  Routes to
        /// <c>NetworkManager.SendVariableUpdate(byte[], int)</c> in the
        /// non-batching path, or <c>VariableBatchManager.CollectIntoBatch</c>
        /// (which copies into a per-pending entry) when batching is enabled.
        /// </param>
        internal void FlushDirtyVariables(Action<byte[], int> sendPayload)
        {
            if (!IsOwner || !IsSpawned || _trackedVariables.Count == 0) return;

            // ── Fast path: skip allocation when nothing is dirty AND eligible.
            //
           // A variable is "eligible to flush this tick" when:
            //  • IsDirty == true, AND
            //  • either SendRateHz <= 0 (use global cadence; always eligible
            //    while dirty), OR (now - LastFlushTimeUnscaled) >= 1/SendRateHz.
            //
           // Throttled-but-dirty variables remain dirty until the next eligible
            // tick — the dirty flag is preserved across skipped flushes so the
            // most recent value is sent on the first allowed window.
            float now = UnityEngine.Time.unscaledTime;
            bool hasEligibleDirty = false;
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                var v = _trackedVariables[i];
                if (!v.IsDirty) continue;
                if (!IsThrottleEligible(v, now)) continue;
                hasEligibleDirty = true;
                break;
            }
            if (!hasEligibleDirty) return;

            // Lazy-init cached stream + writer.  Reusing them across ticks
            // eliminates per-flush MemoryStream and BinaryWriter allocations
            // (previously ~700–900 B per call at 30 Hz × N objects).
            // InitialCapacity covers the common case without internal realloc:
            // object_id(8) + tick(4) + count(1) + ~15 variables at ~16 B each ≈ 253 bytes.
            const int InitialCapacity = 256;
            if (_flushMs == null)
            {
                _flushMs     = new MemoryStream(InitialCapacity);
                _flushWriter = new BinaryWriter(_flushMs, Encoding.UTF8, leaveOpen: true);
            }
            else
            {
                // Reset without deallocating the internal buffer — reuses the
                // previously grown capacity without any heap allocation.
                _flushMs.SetLength(0);
            }
            var ms     = _flushMs;
            var writer = _flushWriter;

            // [object_id:8 LE]
            writer.Write(NetworkObjectId);

            // [tick:4 LE] — sender's current LocalTick.  The receiver compares
            // against the per-variable last-applied tick (RFC 1982 modular
            // arithmetic) and rejects deltas whose tick is not strictly
            // greater than the highest tick already applied for that
            // (object, variable) pair.  Without this gate, a re-ordered or
            // late-arriving UDP datagram from a transient routing change
            // would silently overwrite newer state with older state.
            uint flushTick = NetworkManager.Instance != null
                                 ? NetworkManager.Instance.LocalTick
                                 : 0u;
            writer.Write(flushTick);

            // Reserve space for var_count; written at the end with the real count.
            long countOffset = ms.Position;
            writer.Write((byte)0);

            byte count = 0;
            for (int i = 0; i < _trackedVariables.Count; i++)
            {
                var v = _trackedVariables[i];
                if (!v.IsDirty) continue;

                // Per-variable throttle: skip serialisation when the
                // configured send-rate window has not yet elapsed.  The dirty
                // flag is intentionally NOT cleared so the next eligible tick
                // still flushes the most recent value.
                if (!IsThrottleEligible(v, now)) continue;

                v.SerializeWithId(writer);
                v.MarkClean();
                v.LastFlushTimeUnscaled = now;
                count++;

                // VariableUpdate uses a single-byte count prefix; ensure we
                // never overflow it.  Any remaining dirty variables stay
                // dirty and are sent on the next tick.  This is a hard wire
                // limit; the alternative would be silent data corruption.
                if (count == byte.MaxValue)
                {
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        UnityEngine.Debug.LogWarning(
                            "[RTMPE] FlushDirtyVariables: " +
                            $"NetworkBehaviour '{name}' (type {GetType().Name}) " +
                            $"hit the 255-variable wire-format cap; remaining dirty " +
                            "variables will be sent on later ticks. Consider splitting " +
                            "the variable set across multiple NetworkBehaviours so each " +
                            "object's flush fits in a single VariableUpdate packet.");
                    }
                    break;
                }
            }

            // Flush the BinaryWriter so its internal buffer is fully committed to ms
            // BEFORE seeking back. BinaryWriter does not buffer in .NET Standard but
            // the Flush() guards against any future implementation change.
            writer.Flush();

            // Write back the actual variable count.
            // ms.ToArray() uses the stream's internal _length (= high-water mark),
            // which was set when we wrote the variable data, so seeking back here
            // to overwrite the placeholder does not truncate the payload.
            ms.Position = countOffset;
            writer.Write(count);
            writer.Flush();

            // GC Round 2 (2026-05-02): hand the cached MemoryStream's
            // backing buffer + written length to sendPayload instead of
            // copying via ms.ToArray().  The non-batching consumer
            // (SendVariableUpdate(byte[], int)) wraps only the leading
            // `length` bytes into a packet and never retains the
            // reference; the batching consumer (CollectIntoBatch) copies
            // into a per-pending entry before returning.  Both paths are
            // safe against the buffer being reused on the next tick.
            // ms.GetBuffer() returns the underlying array (possibly larger
            // than ms.Length); we always pass (int)ms.Length as the
            // payload length so the wire frame's payload_len matches the
            // bytes actually written.
            sendPayload(ms.GetBuffer(), (int)ms.Length);
        }

        /// <summary>
        /// Force every tracked <see cref="NetworkVariableBase"/> back into the
        /// dirty set so the next 30 Hz flush transmits its current value, even
        /// when the stored value has not changed since the last send.
        ///
       /// <para>Used by the SDK to bootstrap late joiners: when another
        /// player joins the room, every existing owner client calls this on
        /// each of their owned objects so the new player sees a full state
        /// snapshot on the next tick instead of waiting for a future
        /// value-change event that may never come for static variables.</para>
        ///
       /// <para>Safe to call on non-owned or non-spawned objects — the dirty
        /// flag is still set, but <see cref="FlushDirtyVariables"/> is a no-op
        /// under those conditions so the flag will remain sticky until this
        /// object is owned + spawned again.  Callers that want to avoid that
        /// corner case should filter via <see cref="IsOwner"/> and
        /// <see cref="IsSpawned"/> before calling.</para>
        /// </summary>
        internal void MarkAllVariablesDirty()
        {
            // NetworkVariableBase.IsDirty setter is protected, so we use
            // the public MarkDirtyForResync hook below that each variable
            // exposes through its own public API via a new internal method.
            //
           // Resetting the per-variable throttle state alongside the dirty
            // flag is intentional: a late joiner must see the full snapshot
            // on the next eligible tick, not be blocked behind a stale
            // throttle window inherited from an earlier private send.
            //
           // Iterate a snapshot rather than the live list so a subscriber
            // callback that registers a NEW NetworkVariable during
            // MarkDirtyForResync (or unregisters one) cannot mutate the
            // underlying collection while the foreach is in progress.
            // Defensive — the SDK does not currently hand application code a
            // synchronous hook here, but that contract is not enforced by the
            // type system and a future refactor that introduces one must not
            // be able to throw InvalidOperationException out of a resync.
            var snapshot = new NetworkVariableBase[_trackedVariables.Count];
            _trackedVariables.CopyTo(snapshot, 0);
            for (int i = 0; i < snapshot.Length; i++)
            {
                snapshot[i].MarkDirtyForResync();
                snapshot[i].ResetThrottleState();
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if <paramref name="variable"/>'s
        /// configured per-variable send-rate cap permits a flush at
        /// <paramref name="nowUnscaled"/>.  A non-positive
        /// <see cref="NetworkVariableBase.SendRateHz"/> always returns true
        /// (the global flush cadence is the only gate).
        /// </summary>
        private static bool IsThrottleEligible(NetworkVariableBase variable, float nowUnscaled)
        {
            float rate = variable.SendRateHz;
            if (rate <= 0f) return true;

            float interval = 1f / rate;
            float since    = nowUnscaled - variable.LastFlushTimeUnscaled;

            // LastFlushTimeUnscaled == 0 covers two cases:
            //  • freshly registered (never flushed),
            //  • explicitly reset on ownership change / disconnect.
            // In both cases we want to flush immediately rather than wait out
            // a phantom interval against unscaled-time = 0.
            if (variable.LastFlushTimeUnscaled <= 0f) return true;

            // Use ">= interval - epsilon" so that when the global flush tick
            // and the per-variable interval line up exactly (e.g. SendRateHz
            // == 30 == global rate), we don't lose a tick to floating-point
            // jitter.  A 0.5 ms tolerance is well below any actual gameplay
            // tick rate (1/30 ≈ 33 ms) so it cannot cause double-fires.
            const float Epsilon = 0.0005f;
            return since >= interval - Epsilon;
        }

        // ── Client-Side Prediction hook ───────────────────────────────────────

        /// <summary>
        /// Override in a subclass to supply this frame's player input for
        /// client-side prediction.  Only called on the owning client by
        /// <see cref="RTMPE.Sync.NetworkTransform"/> when prediction is enabled.
        ///
       /// <para>Leave <see cref="InputPayload.Tick"/> at its default zero —
        /// <see cref="CollectInput"/> stamps the correct tick before the payload
        /// is pushed to the buffer.</para>
        ///
       /// <para>Return <c>default</c> for frames with no input.</para>
        /// </summary>
        protected virtual InputPayload GatherInput() => default;

        /// <summary>
        /// Override in a subclass to deterministically apply a single
        /// <see cref="InputPayload"/> to the local game state during a CSP
        /// rollback / replay.  Called once per unacknowledged input by
        /// <see cref="RTMPE.Sync.NetworkTransform.ApplyReconciliation"/> after
        /// the transform has been snapped back to the server-authoritative
        /// pose at the confirmed tick.
        ///
       /// <para>The implementation must be deterministic: the same starting
        /// pose plus the same input must yield the same resulting pose every
        /// time, otherwise the predicted-state divergence the replay was
        /// meant to repair will simply re-emerge each tick.  For the same
        /// reason it must NOT consume <see cref="UnityEngine.Time.deltaTime"/> —
        /// the SDK passes the fixed simulation step in
        /// <paramref name="deltaTime"/>.</para>
        ///
       /// <para>Default implementation is a no-op so legacy behaviours that
        /// do not opt into prediction continue to work unchanged.</para>
        /// </summary>
        /// <param name="input">The input frame to apply.</param>
        /// <param name="deltaTime">Fixed simulation step in seconds.</param>
        protected virtual void ApplyInput(InputPayload input, float deltaTime) { }

        /// <summary>
        /// Collect this frame's input, stamp it with <paramref name="tick"/>,
        /// and return the result.  Called by <see cref="RTMPE.Sync.NetworkTransform"/>
        /// on the owning client.
        /// </summary>
        internal InputPayload CollectInput(uint tick)
        {
            var p  = GatherInput();
            p.Tick = tick;
            return p;
        }

        /// <summary>
        /// Internal entry point for the reconciliation replay loop.  Forwards
        /// to the protected virtual <see cref="ApplyInput"/> so subclasses can
        /// continue to define replay semantics with the standard access
        /// modifier while the SDK still drives the loop from outside the
        /// class hierarchy.
        /// </summary>
        internal void ReplayInput(InputPayload input, float deltaTime)
            => ApplyInput(input, deltaTime);

        /// <summary>
        /// Called exactly once per simulated tick on every owned, spawned
        /// NetworkBehaviour by the central tick driver in
        /// <see cref="NetworkManager"/>.  Override in a subclass to perform
        /// per-tick work that must observe a fixed cadence regardless of frame
        /// rate: input sampling for client-side prediction, deterministic
        /// game-logic timers, server-authoritative inputs, etc.
        ///
        /// <para>Hosting this work on the tick driver — instead of MonoBehaviour
        /// <c>Update</c> — guarantees exactly one invocation per simulated
        /// tick even on long frames.  A frame that integrates several ticks of
        /// elapsed time fires this callback once per integrated tick; a frame
        /// shorter than the tick interval fires zero callbacks.  The
        /// alternative — sampling once per <c>Update</c> with a "has the tick
        /// changed?" guard — silently drops input on stutters and produces
        /// non-deterministic sub-tick collection at high frame rates.</para>
        /// </summary>
        /// <param name="deltaTime">The fixed tick interval in seconds.</param>
        protected virtual void OnFixedTick(float deltaTime) { }

        /// <summary>
        /// SDK-internal forwarder so the tick driver can invoke the protected
        /// virtual without exposing it on the public surface.
        /// </summary>
        internal void InvokeOnFixedTick(float deltaTime) => OnFixedTick(deltaTime);

        // ── Variable update (server → client) ────────────────────────────────

        /// <summary>
        /// Apply a single variable update received from the server.
        /// Called by <c>NetworkManager.HandleVariableUpdatePacket</c> for each
        /// [var_id:2 LE][value_len:2 LE][value_bytes:N] entry in the payload.
        ///
       /// <paramref name="valueLen"/> is used by the caller to advance the
        /// stream past the value bytes regardless of what this method reads,
        /// guaranteeing subsequent variables in the same packet are parsed from
        /// correct offsets even on unknown-ID or schema-mismatch scenarios.
        /// </summary>
        internal void ApplyVariableUpdate(ushort variableId, BinaryReader reader, ushort valueLen = 0)
        {
            ApplyVariableUpdate(variableId, reader, valueLen, packetTick: 0u, hasPacketTick: false);
        }

        /// <summary>
        /// Tick-aware overload.  Drops the deserialised value when
        /// <paramref name="hasPacketTick"/> is set and
        /// <paramref name="packetTick"/> is not strictly greater than the
        /// highest tick already applied to the matching variable.  The caller
        /// must still advance the reader past <paramref name="valueLen"/>
        /// regardless of acceptance — this method does not seek.
        /// </summary>
        internal void ApplyVariableUpdate(
            ushort       variableId,
            BinaryReader reader,
            ushort       valueLen,
            uint         packetTick,
            bool         hasPacketTick)
        {
            foreach (var v in _trackedVariables)
            {
                if (v.VariableId != variableId) continue;

                // Reject older / duplicate deltas before touching the wire
                // value.  Note we still let the caller advance past the
                // valueLen bytes — the seek is owned by the dispatch loop in
                // NetworkManager.HandleVariableUpdatePacket so all variables
                // in the same packet are framed correctly even when one is
                // gated out here.
                if (hasPacketTick && !v.TryAcceptInboundTick(packetTick))
                    return;

                v.Deserialize(reader);
                return;
            }
            // Unknown ID: warn but do NOT read — the caller will skip valueLen bytes.
            Debug.LogWarning(
                $"[RTMPE] NetworkBehaviour: unknown variableId {variableId} in VariableUpdate — " +
                "skipping value bytes. Verify variable IDs are consistent across all clients.");
        }
    }
}
