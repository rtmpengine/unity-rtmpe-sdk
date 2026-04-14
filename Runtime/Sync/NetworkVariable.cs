// RTMPE SDK — Runtime/Sync/NetworkVariable.cs
//
// Foundation for synchronising arbitrary typed values over the RTMPE network.
//
// Design decisions:
//   • Two-tier hierarchy:
//       NetworkVariableBase  — non-generic; holds VariableId, IsDirty, Owner.
//                              Enables generic lists/registration without
//                              reflection (List<NetworkVariableBase>).
//       NetworkVariable<T>   — generic; constrained to struct + IEquatable<T>
//                              to guarantee value-equality semantics and inline
//                              storage (no boxing on read).
//   • NetworkVariableString  — extends NetworkVariableBase directly.
//                              Strings are reference types; they cannot satisfy
//                              the struct constraint.  Uses reference equality
//                              (`!=`) plus null normalisation (null treated as "").
//   • IsDirty tracks whether the local value has changed since the last
//     MarkClean() call.  The dirt flag is set by Value setter; cleared by
//     MarkClean().  SetValueWithoutNotify() does NOT set IsDirty, because it
//     is intended for the RECEIVING side (applying an incoming update, not
//     originating a new one).
//   • OnValueChanged(oldValue, newValue) fires AFTER _value is updated so that
//     callbacks can safely read Value without re-entrancy issues.
//   • VariableId (ushort) is assigned by the caller to identify this variable
//     within its owning NetworkBehaviour.  Used by the packet serialiser
//     (Week 25) to route incoming updates to the correct variable.
//   • Owner (NetworkBehaviour) is stored for future use by the send path
//     (Week 25: dirty variables are flushed on each tick for owning clients).
//     Not null-checked here — callers must pass a valid instance.
//
// P-6 fix: NetworkVariableString.Value setter normalises null → "" on write.
//   A null value assigned via Value = null is stored as "" preventing
//   get→Serialize→Deserialize state divergence.
//
// Security note: no AEAD here.  These objects hold application-layer values;
// the surrounding gateway pipeline handles encryption.

using System;
using System.IO;
using UnityEngine;

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
        /// Used by the send path (Week 25) to flush dirty variables on each tick.
        /// </summary>
        protected NetworkBehaviour Owner { get; }

        // ── Dirty tracking ─────────────────────────────────────────────────────

        /// <summary>
        /// True when the local value has changed since the last
        /// <see cref="MarkClean"/> call.  The send path (Week 25) reads this
        /// to decide whether to include this variable in the next update packet.
        /// </summary>
        public bool IsDirty { get; protected set; }

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
        }

        // ── API ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Clear the dirty flag.  Call this after the current value has been
        /// successfully transmitted over the network.
        /// </summary>
        public void MarkClean() => IsDirty = false;

        /// <summary>
        /// Write the current value to <paramref name="writer"/> in a format
        /// that can be recovered by <see cref="Deserialize"/>.
        /// Value bytes only — the variable ID is NOT included.
        /// Use <see cref="SerializeWithId"/> for the framed wire format.
        /// </summary>
        public abstract void Serialize(BinaryWriter writer);

        /// <summary>
        /// Write the <see cref="VariableId"/> (2 bytes LE) followed by the
        /// value bytes.  This is the framed wire format used in 'variable
        /// update' packets so the receiver can identify which variable to
        /// update.
        /// </summary>
        public void SerializeWithId(BinaryWriter writer)
        {
            writer.Write(VariableId);  // BinaryWriter.Write(ushort) → 2 bytes LE
            Serialize(writer);
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

                T oldValue = _value;
                _value     = value;
                IsDirty    = true;

                // Fire AFTER _value is updated so callbacks can safely read Value.
                OnValueChanged?.Invoke(oldValue, value);
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
                // P-6 fix: normalise null → "" before comparison and storage.
                string normalized = value ?? string.Empty;
                if (_value == normalized) return;

                string oldValue = _value;
                _value          = normalized;
                IsDirty         = true;

                OnValueChanged?.Invoke(oldValue, normalized);
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
            _value = value ?? string.Empty;
        }

        // ── Serialisation ──────────────────────────────────────────────────────

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            // BinaryWriter.Write(string) uses a 7-bit-encoded length prefix
            // followed by UTF-8 bytes.  This is the standard .NET encoding and
            // is matched symmetrically by BinaryReader.ReadString().
            writer.Write(_value);
        }

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
        {
            SetValueWithoutNotify(reader.ReadString());
        }
    }
}
