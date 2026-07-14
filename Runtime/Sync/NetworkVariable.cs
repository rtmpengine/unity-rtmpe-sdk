// RTMPE SDK — Runtime/Sync/NetworkVariable.cs
//
// Foundation for synchronising arbitrary typed values over the RTMPE network.
//
// Design decisions:
//  • Two-tier hierarchy:
//      NetworkVariableBase  — non-generic; holds VariableId, IsDirty, Owner.
//                             Enables generic lists/registration without
//                             reflection (List<NetworkVariableBase>).
//      NetworkVariable<T>   — generic; constrained to struct + IEquatable<T>
//                             to guarantee value-equality semantics and inline
//                             storage (no boxing on read).
//  • NetworkVariableString  — extends NetworkVariableBase directly.
//                             Strings are reference types; they cannot satisfy
//                             the struct constraint.  Uses reference equality
//                             (`!=`) plus null normalisation (null treated as "").
//  • IsDirty tracks whether the local value has changed since the last
//    MarkClean() call.  The dirt flag is set by Value setter; cleared by
//    MarkClean().  SetValueWithoutNotify() does NOT set IsDirty, because it
//    is intended for the RECEIVING side (applying an incoming update, not
//    originating a new one).
//  • OnValueChanged(oldValue, newValue) fires AFTER _value is updated so that
//    callbacks can safely read Value without re-entrancy issues.
//  • VariableId (ushort) is assigned by the caller to identify this variable
//    within its owning NetworkBehaviour.  Used by the packet serialiser
//    to route incoming updates to the correct variable.
//  • Owner (NetworkBehaviour) is stored for the send path — dirty variables
//    are flushed at 30 Hz for owning clients.
//    Not null-checked here — callers must pass a valid instance.
//
// NetworkVariableString.Value setter normalises null to "" on write.
//  A null value assigned via Value = null is stored as "" preventing
//  get→Serialize→Deserialize state divergence.
//
// Security note: no AEAD here.  These objects hold application-layer values;
// the surrounding gateway pipeline handles encryption.

using System;
using System.IO;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Sync
{
    // ── Base class (non-generic) ───────────────────────────────────────────────

    /// <summary>
    /// Non-generic base for all RTMPE network variables.
    /// Use this type when you need to store heterogeneous variables in a common
    /// list or call <see cref="MarkClean"/> / <see cref="Serialize"/> without
    /// knowing the concrete <c>T</c>.
    /// </summary>
    public abstract class NetworkVariableBase
    {
        // ── Identity ───────────────────────────────────────────────────────────

        /// <summary>
        /// Caller-assigned identifier for this variable within its owning
        /// <see cref="NetworkBehaviour"/>.  Must be unique per object and stable
        /// across all clients (i.e. assigned in the same order in code).
        /// </summary>
        public ushort VariableId { get; }

        /// <summary>
        /// The <see cref="NetworkBehaviour"/> that owns this variable.
        /// Used by the send path to flush dirty variables on each tick.
        /// </summary>
        protected NetworkBehaviour Owner { get; }

        // ── Dirty tracking ─────────────────────────────────────────────────────

        /// <summary>
        /// True when the local value has changed since the last
        /// <see cref="MarkClean"/> call.  The send path reads this
        /// to decide whether to include this variable in the next update packet.
        /// </summary>
        public bool IsDirty { get; protected set; }

        // ── Per-variable throttling (Feature: NetworkVariableAttribute) ────────

        /// <summary>
        /// Per-variable send-rate cap in Hz.  Zero means "use the global flush
        /// cadence" (currently 30 Hz, set by <c>NetworkManager.VariableFlushInterval</c>).
        /// Positive values throttle this specific variable independently of its
        /// siblings; the dirty flag is preserved across skipped flushes so the
        /// next eligible window picks up the most recent value.
        ///
       /// <para>Configured declaratively via
        /// <see cref="NetworkVariableAttribute.SendRateHz"/> on the owning
        /// field/property and applied by <see cref="NetworkBehaviour"/> when
        /// the variable is registered.  May also be assigned manually for
        /// dynamic throttling (e.g. raise the rate during a boss fight, lower
        /// it while idle).</para>
        ///
       /// <para>Negative assignments are clamped to <c>0</c>.</para>
        /// </summary>
        public float SendRateHz
        {
            get => _sendRateHz;
            set => _sendRateHz = value < 0f ? 0f : value;
        }
        private float _sendRateHz;

        /// <summary>
        /// Wall-clock timestamp of the last successful flush, sampled from
        /// <c>Time.unscaledTime</c>.  Updated by the flush loop after the
        /// variable's bytes have been appended to the outbound packet.
        ///
       /// <para>Reset to <c>0f</c> on ownership change and on disconnect so a
        /// freshly owning client can flush its first value immediately rather
        /// than waiting out a stale throttle window inherited from the
        /// previous owner.</para>
        ///
       /// <para>Internal — only the flush path and the
        /// ownership-reset hook should mutate this field.</para>
        /// </summary>
        internal float LastFlushTimeUnscaled { get; set; }

        /// <summary>
        /// Reset the per-variable throttle book-keeping.  Called when ownership
        /// changes hands or the local session disconnects so that
        /// <see cref="LastFlushTimeUnscaled"/> does not gate the first flush on
        /// the new owner with a phantom send-time inherited from the old owner.
        /// </summary>
        internal void ResetThrottleState()
        {
            LastFlushTimeUnscaled = 0f;
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialise the base with owner and variable ID.
        /// </summary>
        /// <param name="owner">
        /// Owning <see cref="NetworkBehaviour"/> instance.
        /// Must not be <see langword="null"/> (not checked here; callers are
        /// responsible for passing valid instances).
        /// </param>
        /// <param name="variableId">
        /// Per-object identifier, unique within the owning NetworkBehaviour.
        /// </param>
        protected NetworkVariableBase(NetworkBehaviour owner, ushort variableId)
        {
            Owner      = owner;
            VariableId = variableId;
            // Register with the owning NetworkBehaviour so the 30 Hz flush loop
            // can discover and serialize dirty values. Create variables inside
            // OnNetworkSpawn() where the object is guaranteed to be fully initialized.
            owner?.TrackVariable(this);
        }

        // ── API ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clear the dirty flag.  Call this after the current value has been
        /// successfully transmitted over the network.
        /// </summary>
        public void MarkClean() => IsDirty = false;

        /// <summary>
        /// Force the dirty flag to <see langword="true"/> without changing the
        /// stored value.  Used by the SDK when a late joiner enters the room
        /// and needs a full state snapshot — every variable on every owned
        /// object is re-flagged so the next 30 Hz flush retransmits its
        /// current value.
        /// <para>
        /// Does NOT fire <c>OnValueChanged</c> — the value is unchanged; only
        /// the send-queue state is reset.  Safe to call multiple times.
        /// </para>
        /// </summary>
        internal virtual void MarkDirtyForResync() => IsDirty = true;

        /// <summary>
        /// Write the current value to <paramref name="writer"/> in a format
        /// that can be recovered by <see cref="Deserialize"/>.
        /// Value bytes only — the variable ID is NOT included.
        /// Use <see cref="SerializeWithId"/> for the framed wire format.
        /// </summary>
        public abstract void Serialize(BinaryWriter writer);

        /// <summary>
        /// Write the <see cref="VariableId"/> (2 bytes LE) followed by a
        /// 2-byte LE value length, then the value bytes.
        /// This is the framed wire format used in 'variable update' packets
        /// so the receiver can identify which variable to update AND skip
        /// unknown variable IDs without corrupting the stream.
        ///
       /// Wire layout: [var_id:2 LE][value_len:2 LE][value_bytes:N]
        /// </summary>
        // GC Round 2 (2026-05-02) — cached fast-path serializer state.
        //
        // The fast path needs a non-growable MemoryStream + BinaryWriter to
        // detect "value too big for the pool buffer" via NotSupportedException
        // and fall back to the growable slow path.  Pre-Round-2, both objects
        // were `using var` locals — one MemoryStream + one BinaryWriter
        // allocated per SerializeWithId call (≈ N variables × M objects ×
        // 30 Hz = several hundred allocs/sec on a busy game).
        //
        // Caching strategy:
        //   • The rented byte[] still comes from ArrayPool<byte>.Shared per
        //     call.  This is required because the buffer must be *cleared*
        //     before return to avoid leaking the prior tick's variable
        //     payload to the next renter; a per-instance buffer would leak
        //     the same payload across ticks of the SAME variable.
        //   • The MemoryStream + BinaryWriter wrappers are allocated once
        //     per NetworkVariable instance (stored in _fastMs / _fastBw)
        //     and re-targeted onto each new rented buffer via reflection-
        //     free APIs: SetLength(0)+TrySetBuffer.  .NET Standard 2.1 does
        //     not expose SetBuffer publicly, so we wrap a fresh
        //     MemoryStream around the rented buffer the first time and
        //     leave the wrapper objects alive afterwards.
        //   • Threading: NetworkVariableBase is touched only from the Unity
        //     main thread (FlushDirtyVariables runs on Update); the cached
        //     fields are NOT thread-safe.  See NetworkVariable threading
        //     notes at the file header.
        private MemoryStream _fastMs;
        private BinaryWriter _fastBw;
        private byte[]       _fastMsBuffer;  // Cached reference for fast identity check (avoids GetBuffer's UnauthorizedAccessException risk path)
        private MemoryStream _slowMs;
        private BinaryWriter _slowBw;

        public void SerializeWithId(BinaryWriter writer)
        {
            // Write var_id first.
            writer.Write(VariableId);

            // Write a 2-byte LE length prefix for the value payload.  The
            // prefix lets the receiver skip entries with unknown variable
            // IDs instead of stopping mid-packet and losing all subsequent
            // variables.
            //
           // Fast path: rent a pool-backed byte[] large enough for every
            // struct value (Quaternion = 16 B) and most strings.  This is
            // zero-heap for the common case (30 Hz × N objects × M vars).
            //
           // Slow path: NetworkVariableString may emit up to 65,537 bytes.
            // Writing past the rented buffer throws NotSupportedException on
            // a non-growable MemoryStream; we detect this and fall through
            // to a growable stream.  The slow path executes only for the
            // long-string edge case — ≈ 0 % of gameplay traffic.
            //
           // Hard cap: the wire format encodes the per-value length as a
            // ushort, so any value longer than ushort.MaxValue would silently
            // truncate and desync receiver state.  Detect and skip such
            // values before writing anything to the outer writer.
            const int PoolBufferSize = 1024;
            const int MaxValueLen = ushort.MaxValue;
            var pool = System.Buffers.ArrayPool<byte>.Shared;
            byte[] rented = pool.Rent(PoolBufferSize);
            bool overflowed = false;
            try
            {
                // Cached MemoryStream + BinaryWriter (lazy-init).  The
                // MemoryStream is bound to the rented buffer on construction;
                // since rented buffers vary in length call-to-call (ArrayPool
                // returns the same bucket size for a given Rent request, but
                // .Length may exceed the requested size), we always
                // construct a fresh MemoryStream around the new rented
                // buffer — but we cache the BinaryWriter against the cached
                // stream.  In practice ArrayPool's bucket logic means the
                // rented array reference is usually identical across calls
                // on a given instance, so the MemoryStream allocation is
                // O(1) but its internal buffer reference does not change.
                if (_fastMs == null || !ReferenceEquals(_fastMsBuffer, rented))
                {
                    // (Re)bind: the rented buffer reference changed since
                    // last call (or first call).  Allocate a new
                    // MemoryStream over the new buffer.  We use the 5-arg
                    // constructor with publiclyVisible: true so the cached
                    // stream's GetBuffer() call (used elsewhere) does not
                    // throw UnauthorizedAccessException — the buffer is
                    // ours to expose since we rented it locally.  We also
                    // store the buffer reference in _fastMsBuffer for the
                    // ReferenceEquals check above; calling GetBuffer() on
                    // a non-rebound stream is allowed but is only invoked
                    // for the buffer-identity check, never for slicing.
                    // Disposing the BinaryWriter would close the
                    // underlying stream, so we deliberately do NOT dispose
                    // either object — they are root-rooted by _fastMs /
                    // _fastBw and reclaimed when this NetworkVariable is
                    // finalized.
                    _fastMs = new MemoryStream(rented, 0, rented.Length,
                                               writable: true, publiclyVisible: true);
                    _fastBw = new BinaryWriter(_fastMs, Encoding.UTF8, leaveOpen: true);
                    _fastMsBuffer = rented;
                }
                else
                {
                    // Same buffer reference — just reset the position.
                    _fastMs.SetLength(0);
                    _fastMs.Position = 0;
                }
                var fast = _fastMs;
                var bw   = _fastBw;
                try
                {
                    Serialize(bw);
                    bw.Flush();
                }
                catch (NotSupportedException)
                {
                    // Rented buffer is too small — the growable fallback
                    // below will be used instead.
                    overflowed = true;
                }
                catch (Exception ex)
                {
                    // A buggy custom Serialize() must not abort the entire
                    // flush cycle for sibling NetworkVariables.  Skip this
                    // value by writing a zero-length record.
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable '{GetType().Name}': Serialize() threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Variable will publish empty payload this tick.");
                    writer.Write((ushort)0);
                    return;
                }

                if (!overflowed)
                {
                    int valueLen = (int)fast.Position;
                    if (valueLen > MaxValueLen)
                    {
                        Debug.LogError(
                            $"[RTMPE] NetworkVariable '{GetType().Name}': serialized " +
                            $"size {valueLen} exceeds ushort wire cap ({MaxValueLen}).  Skipped.");
                        writer.Write((ushort)0);
                        return;
                    }
                    writer.Write((ushort)valueLen);
                    writer.Write(rented, 0, valueLen);
                    return;
                }
            }
            finally
            {
                // Clear before return so subsequent renters cannot read
                // residual variable payloads from the shared pool — a peer
                // app component that rents the same buffer next would
                // otherwise observe the previous owner's serialized state.
                pool.Return(rented, clearArray: true);
            }

            // Slow path: cache a growable stream + writer the first time
            // it's needed.  The slow path is rare (long-string variables
            // only) so the cache pays for itself slowly, but it costs
            // nothing on instances that never hit it.  We must reset
            // length/position on each entry because the stream is reused.
            if (_slowMs == null)
            {
                _slowMs = new MemoryStream(PoolBufferSize);
                _slowBw = new BinaryWriter(_slowMs, Encoding.UTF8, leaveOpen: true);
            }
            else
            {
                _slowMs.SetLength(0);
                _slowMs.Position = 0;
            }
            {
                var growable = _slowMs;
                var bw       = _slowBw;
                try
                {
                    Serialize(bw);
                    bw.Flush();
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable '{GetType().Name}': Serialize() threw " +
                        $"{ex.GetType().Name} on growable path: {ex.Message}.  Variable will publish empty payload this tick.");
                    writer.Write((ushort)0);
                    return;
                }
                int valueLen = (int)growable.Length;
                if (valueLen > MaxValueLen)
                {
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable '{GetType().Name}': serialized " +
                        $"size {valueLen} exceeds ushort wire cap ({MaxValueLen}).  Skipped.");
                    writer.Write((ushort)0);
                    return;
                }
                writer.Write((ushort)valueLen);
                writer.Write(growable.GetBuffer(), 0, valueLen);
            }
        }


        /// <summary>
        /// Read a value from <paramref name="reader"/> and apply it without
        /// firing <see cref="OnValueChanged"/> or marking dirty.
        /// Use on the receiving side when applying an incoming server update.
        /// </summary>
        public abstract void Deserialize(BinaryReader reader);

        /// <summary>
        /// Read the <see cref="VariableId"/> prefix (2 bytes LE) from the
        /// reader and return it.  The caller uses the returned ID to look up
        /// the correct variable, then calls <see cref="Deserialize"/> on it.
        /// </summary>
        public static ushort ReadVariableId(BinaryReader reader)
        {
            return reader.ReadUInt16();
        }

        // ── Per-variable inbound tick gate ─────────────────────────────────────
        //
        // Tracks the highest tick this variable has applied from a server
        // VariableUpdate so a re-ordered datagram cannot silently roll the
        // value back.  Comparison is RFC 1982 modular so a uint32 wrap during
        // a long-running session does not wedge the gate.

        private uint _lastAppliedTick;
        private bool _hasLastAppliedTick;

        /// <summary>
        /// Returns <see langword="true"/> when an inbound update stamped with
        /// <paramref name="incomingTick"/> should be applied — i.e. when the
        /// tick is strictly greater than the highest tick already applied to
        /// this variable.  The first call (no prior tick) always accepts.
        /// </summary>
        internal bool TryAcceptInboundTick(uint incomingTick)
        {
            if (!_hasLastAppliedTick)
            {
                _hasLastAppliedTick = true;
                _lastAppliedTick    = incomingTick;
                return true;
            }
            // (int)(a - b) > 0 iff a is strictly greater than b on the
            // 32-bit ring; matches InputBuffer.SeqGreater so the whole SDK
            // observes the same wrap semantics.
            if ((int)(incomingTick - _lastAppliedTick) <= 0)
                return false;
            _lastAppliedTick = incomingTick;
            return true;
        }

        /// <summary>
        /// Reset the inbound tick gate.  Called on ownership change and on
        /// disconnect so the gate cannot block the first update under a fresh
        /// session whose tick counter restarts at zero.
        /// </summary>
        internal void ResetInboundTickGate()
        {
            _hasLastAppliedTick = false;
            _lastAppliedTick    = 0u;
        }
    }

    // ── Generic typed variable ─────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised value of type <typeparamref name="T"/>.
    ///
   /// <typeparamref name="T"/> must be a value type (<c>struct</c>) that
    /// implements <see cref="IEquatable{T}"/>, ensuring that the equality
    /// check in the <see cref="Value"/> setter is allocation-free and correct.
    ///
   /// Concrete sealed subclasses should call back to this via
    /// <c>base(owner, variableId, initialValue)</c> and then implement
    /// <see cref="NetworkVariableBase.Serialize"/> and
    /// <see cref="NetworkVariableBase.Deserialize"/>.
    /// </summary>
    public abstract class NetworkVariable<T> : NetworkVariableBase
        where T : struct, IEquatable<T>
    {
        // ── Stored value ───────────────────────────────────────────────────────

        private T _value;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires when the value changes via the <see cref="Value"/> setter.
        /// Arguments are (previousValue, newValue).
        /// Does NOT fire for <see cref="SetValueWithoutNotify"/>.
        /// </summary>
        public event Action<T, T> OnValueChanged;

        // ── Value property ─────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the current value.
        ///
       /// <b>Set:</b> if the new value differs from the current value
        /// (per <see cref="IEquatable{T}.Equals"/>), the internal field is
        /// updated, <see cref="NetworkVariableBase.IsDirty"/> is set to
        /// <see langword="true"/>, and <see cref="OnValueChanged"/> fires.
        ///
       /// Setting the same value a second time is a no-op (no event, no dirty).
        /// </summary>
        public T Value
        {
            get => _value;
            set
            {
                // IEquatable<T>.Equals — no boxing, no allocation.
                if (_value.Equals(value)) return;

                // Reject writes after OnNetworkDespawn so user code that
                // mutates Value from a torn-down object cannot mark a
                // dead variable dirty (the next flush would re-publish
                // post-despawn state) or fire callbacks against
                // already-cleared subscribers on a destroying object.
                if (Owner != null && !Owner.IsSpawned) return;

                T oldValue = _value;
                _value     = value;
                IsDirty    = true;

                // Fire AFTER _value is updated so callbacks can safely read Value.
                // A user-supplied OnValueChanged subscriber that throws must
                // not abort the calling code path — for the owning side this
                // is the variable-flush hot loop, where one bad subscriber
                // would otherwise stop every sibling NetworkVariable on the
                // same object from publishing this tick.  Catch and surface;
                // the dirty flag was already set, so the next flush retries.
                try
                {
                    OnValueChanged?.Invoke(oldValue, value);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[RTMPE] NetworkVariable<{typeof(T).Name}>.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated; " +
                        "the new value is already stored locally.");
                }
            }
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialise with an owner, variable ID, and optional initial value.
        /// </summary>
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier.</param>
        /// <param name="initialValue">
        /// Starting value (default for <typeparamref name="T"/> when omitted).
        /// For <c>Quaternion</c>, pass <c>Quaternion.identity</c> explicitly
        /// because <c>default(Quaternion)</c> is the zero quaternion, not identity.
        /// </param>
        protected NetworkVariable(
            NetworkBehaviour owner,
            ushort           variableId,
            T                initialValue = default)
            : base(owner, variableId)
        {
            // Assign directly (bypassing the setter) so no event fires and
            // IsDirty remains false at construction.
            _value = initialValue;
        }

        // ── Receive-side API ───────────────────────────────────────────────────

        /// <summary>
        /// Apply a value received from the server without firing
        /// <see cref="OnValueChanged"/> or setting <see cref="NetworkVariableBase.IsDirty"/>.
        ///
       /// Use this on the receiving client when handling an incoming variable
        /// update packet so that the local UI/gameplay does not re-broadcast
        /// the value it just received.
        /// </summary>
        public void SetValueWithoutNotify(T value)
        {
            // Drop late inbound updates that arrive after OnNetworkDespawn:
            // the owning NetworkBehaviour may have already cleared subscribers
            // and torn down its game-side state, and the GameObject itself
            // may be mid-Destroy.  Mutating _value here would let user code
            // observe the post-despawn value through whatever callbacks
            // remain in flight, and re-anchor the variable's last-applied
            // tick gate against a packet whose target object is gone.
            if (Owner == null || !Owner.IsSpawned) return;
            _value = value;
            // Intentionally does NOT set IsDirty or fire OnValueChanged.
        }
    }

    // ── String variable (reference type — not struct) ─────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="string"/> value.
    /// Extends <see cref="NetworkVariableBase"/> directly because <c>string</c>
    /// is a reference type and cannot satisfy the <c>struct</c> constraint on
    /// <see cref="NetworkVariable{T}"/>.
    ///
   /// <b>Null normalisation:</b> <see langword="null"/> is treated as
    /// <see cref="string.Empty"/> everywhere (Value property, constructor,
    /// SetValueWithoutNotify, Serialize).  This prevents null-reference
    /// exceptions in callbacks and ensures Serialize/Deserialize round-trips
    /// are stable (<c>null → Serialize → Deserialize</c> yields <c>""</c>).
    /// </summary>
    public sealed class NetworkVariableString : NetworkVariableBase
    {
        // Strict UTF-8 codec — the lax decoder silently substitutes U+FFFD
        // for malformed byte sequences, letting a hostile peer smuggle bytes
        // that survive the decode but mutate downstream string-equality
        // invariants (display names, scene keys, room tags, reserved-key
        // checks).  Symmetric with the RPC stack (M19-RPC-04/05) and the
        // RoomPacketParser (M18-UTF8-01).
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ── Stored value ───────────────────────────────────────────────────────

        private string _value;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Fires when the value changes via the <see cref="Value"/> setter.
        /// Arguments are (previousValue, newValue).  Both are non-null.
        /// </summary>
        public event Action<string, string> OnValueChanged;

        // ── Value property ─────────────────────────────────────────────────────

        /// <summary>
        /// Gets or sets the current string value.
        /// <see langword="null"/> is normalised to <see cref="string.Empty"/> on
        /// write, so the getter always returns a non-null string.
        /// </summary>
        public string Value
        {
            get => _value;
            set
            {
                // Normalise null to "" before comparison and storage to ensure
                // consistent behaviour when null is assigned as a value.
                string normalized = value ?? string.Empty;
                if (_value == normalized) return;

                // Symmetric guard with NetworkVariable<T>.Value: reject writes
                // after OnNetworkDespawn so a torn-down object cannot re-emit
                // post-despawn state or fire callbacks at cleared subscribers.
                if (Owner != null && !Owner.IsSpawned) return;

                string oldValue = _value;
                _value          = normalized;
                IsDirty         = true;

                // Subscriber-isolation: see the symmetric guard on
                // NetworkVariable<T>.Value.  The new value is already in
                // _value; a throwing subscriber must not stop the flush.
                try
                {
                    OnValueChanged?.Invoke(oldValue, normalized);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[RTMPE] NetworkVariableString.OnValueChanged threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated; " +
                        "the new value is already stored locally.");
                }
            }
        }

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Initialise with an owner, variable ID, and optional initial string.
        /// </summary>
        public NetworkVariableString(
            NetworkBehaviour owner,
            ushort           variableId,
            string           initialValue = "")
            : base(owner, variableId)
        {
            _value = initialValue ?? string.Empty;
        }

        // ── Receive-side API ───────────────────────────────────────────────────

        /// <summary>
        /// Apply a value without firing <see cref="OnValueChanged"/> or setting dirty.
        /// Null is normalised to <see cref="string.Empty"/>.
        /// </summary>
        public void SetValueWithoutNotify(string value)
        {
            // See NetworkVariable<T>.SetValueWithoutNotify for the rationale.
            if (Owner == null || !Owner.IsSpawned) return;
            _value = value ?? string.Empty;
        }

        // ── Serialisation ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when the encoded UTF-8 length exceeds the wire-format cap of
        /// <see cref="ushort.MaxValue"/> bytes.  Previously this case was
        /// silently truncated with only a Debug.LogWarning, causing the client
        /// and server to diverge on any long string — the caller had no signal
        /// to react.  The fix-forward for a long payload is to either shorten
        /// the string, split it across multiple NetworkVariables, or move
        /// bulk data to an out-of-band channel (RPC with large payload).
        /// </exception>
        public override void Serialize(BinaryWriter writer)
        {
            // Encode as a 2-byte LE uint16 length prefix followed by raw UTF-8
            // bytes.  Wire-compatible with the Go server (binary.LittleEndian.Uint16).
            // `BinaryWriter.Write(string)` would emit a .NET 7-bit variable-length
            // integer prefix which is NOT compatible with the Go wire format.
            byte[] bytes = StrictUtf8.GetBytes(_value ?? string.Empty);
            if (bytes.Length > ushort.MaxValue)
            {
                throw new ArgumentException(
                    $"NetworkVariableString value is {bytes.Length} UTF-8 bytes, " +
                    $"which exceeds the wire-format maximum of {ushort.MaxValue}.  " +
                    "Shorten the string, split across multiple variables, or use an RPC for bulk data.",
                    nameof(_value));
            }

            // 2-byte LE length prefix (compatible with Go binary.LittleEndian.Uint16).
            // BinaryWriter.Write(ushort) is little-endian per .NET Standard 2.1 spec.
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
        {
            // Read the 2-byte LE length prefix, then read exactly that many
            // UTF-8 bytes — matches the Serialize path above and the Go server format.
            ushort len   = reader.ReadUInt16();
            byte[] bytes = reader.ReadBytes(len);
            // BinaryReader.ReadBytes returns FEWER than the requested count
            // when the underlying stream ends before the prefix's worth of
            // bytes is available — no exception is raised.  Without this
            // guard a truncated inbound packet would be silently decoded as
            // a short string, leaving the receiver with a value that does
            // not round-trip its peer's serialise output.  Surface a clean
            // EndOfStreamException so the variable-update dispatcher logs
            // and skips the entry.
            if (bytes.Length != len)
                throw new EndOfStreamException(
                    $"NetworkVariableString.Deserialize: declared {len} UTF-8 bytes, " +
                    $"only {bytes.Length} available — packet truncated.");
            try
            {
                SetValueWithoutNotify(StrictUtf8.GetString(bytes));
            }
            catch (DecoderFallbackException ex)
            {
                // Surface malformed UTF-8 as the same truncation contract the
                // dispatcher already handles — the packet is dropped and the
                // prior value is preserved.
                throw new EndOfStreamException(
                    "NetworkVariableString.Deserialize: malformed UTF-8 in payload.",
                    ex);
            }
        }
    }
}
