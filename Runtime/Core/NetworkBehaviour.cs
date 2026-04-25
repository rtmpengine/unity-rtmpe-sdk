// RTMPE SDK — Runtime/Core/NetworkBehaviour.cs
//
// Base class for all networked GameObjects in RTMPE.
//
// Design decisions:
//   • _ownerPlayerId is a string (UUID) to match PlayerInfo.PlayerId and the
//     room service player identifiers.  The gateway session ID (u64) is a
//     DIFFERENT concept stored in NetworkManager.LocalPlayerId.
//   • IsOwner compares string UUIDs and short-circuits on null/empty so that
//     uninitialized objects never falsely claim ownership (unlike a ulong==0
//     comparison which would return true for every uninitialized object).
//   • Initialize / SetSpawned / SetOwner are internal so only the RTMPE SDK
//     itself (SpawnManager) can mutate network object state.
//     RTMPE.SDK.Tests can also call them via InternalsVisibleTo (AssemblyInfo.cs).
//   • DestroyWithOwner is declared here; enforcement is implemented by
//     SpawnManager when it handles PlayerLeft events.
//   • IsOwner accesses NetworkManager.Instance — safe for main-thread MonoBehaviour
//     code (OnNetworkSpawn, OnOwnershipChanged, etc. all run on main thread).

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

        // List of all NetworkVariables registered during OnNetworkSpawn.
        // Populated by NetworkVariableBase constructors via TrackVariable().
        // Flushed at 30 Hz by NetworkManager for owner clients.
        private readonly List<NetworkVariableBase> _trackedVariables =
            new List<NetworkVariableBase>();

        // RPC-collision validation cache.  Each concrete NetworkBehaviour subclass
        // is checked exactly once on first spawn; subsequent spawns of the same
        // type skip the reflection scan.  HashSet<Type> reads are lock-free under
        // the main-thread invariant (all spawns happen on the Unity main thread).
        private static readonly HashSet<Type> _validatedTypes = new HashSet<Type>();

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
        internal void DispatchEnhancedRpc(uint methodId, object[] args)
        {
            if (!RpcRegistry.TryFindMethod(GetType(), methodId, out MethodInfo method, out _))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkBehaviour: no [RtmpeRpc] method with id 0x{methodId:X8} " +
                    $"on {GetType().Name}. Check that the method exists and is decorated with [RtmpeRpc].");
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
                ValidateRpcMethodsOnce();
                OnNetworkSpawn();
            }
            else if (!spawned && _isSpawned)
            {
                _isSpawned = false;
                OnNetworkDespawn();
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
            OnOwnershipChanged(previous, _ownerPlayerId);
        }

        // ── NetworkVariable registration and flush ─────────────────────────────

        /// <summary>
        /// Register a <see cref="NetworkVariableBase"/> with this behaviour so it
        /// participates in the 30 Hz dirty-flush loop.
        /// Called automatically by the <c>NetworkVariableBase</c> constructor —
        /// user code should never call this directly.
        /// </summary>
        internal void TrackVariable(NetworkVariableBase variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            _trackedVariables.Add(variable);
        }

        /// <summary>
        /// Serialize all dirty tracked variables into a single <c>VariableUpdate</c>
        /// payload and call <paramref name="sendPayload"/> with it.
        /// No-op when not spawned, not owner, or all variables are clean.
        /// Called by <c>NetworkManager.FlushDirtyNetworkVariables</c> at 30 Hz.
        /// </summary>
        /// <param name="sendPayload">
        /// Delegate that transmits the built payload bytes, e.g.
        /// <c>NetworkManager.SendVariableUpdate</c>.
        /// </param>
        internal void FlushDirtyVariables(Action<byte[]> sendPayload)
        {
            if (!IsOwner || !IsSpawned || _trackedVariables.Count == 0) return;

            // Fast path: skip allocation when nothing is dirty.
            bool hasDirty = false;
            foreach (var v in _trackedVariables)
            {
                if (v.IsDirty) { hasDirty = true; break; }
            }
            if (!hasDirty) return;

            // Use a growable MemoryStream so that long NetworkVariableString values
            // (or many simultaneously dirty variables) never throw the
            // NotSupportedException that a fixed-capacity backing buffer raises.
            // InitialCapacity covers the common case without reallocation:
            // object_id(8) + count(1) + ~15 variables at ~16 bytes each ≈ 249 bytes.
            const int InitialCapacity = 256;
            using var ms     = new MemoryStream(InitialCapacity);
            using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

            // [object_id:8 LE]
            writer.Write(NetworkObjectId);

            // Reserve space for var_count; written at the end with the real count.
            long countOffset = ms.Position;
            writer.Write((byte)0);

            byte count = 0;
            foreach (var v in _trackedVariables)
            {
                if (!v.IsDirty) continue;
                v.SerializeWithId(writer);
                v.MarkClean();
                count++;
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

            // ms.ToArray() returns all bytes from 0 to _length (the high-water mark),
            // regardless of the current Position — exactly the bytes we wrote above.
            sendPayload(ms.ToArray());
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
            foreach (var v in _trackedVariables)
            {
                v.MarkDirtyForResync();
            }
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
            foreach (var v in _trackedVariables)
            {
                if (v.VariableId != variableId) continue;
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
