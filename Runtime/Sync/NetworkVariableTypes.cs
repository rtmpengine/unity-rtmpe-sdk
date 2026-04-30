// RTMPE SDK — Runtime/Sync/NetworkVariableTypes.cs
//
// Concrete sealed NetworkVariable<T> implementations for the types most
// commonly synchronised in a multiplayer game.
//
// Each class:
//  • Seals the type (prevents further subclassing of concrete types).
//  • Calls the base constructor with (owner, variableId, initialValue).
//  • Implements Serialize   — uses BinaryWriter to emit value bytes LE.
//  • Implements Deserialize — uses BinaryReader + SetValueWithoutNotify.
//
// Wire format notes:
//  • BinaryWriter.Write(int/float/bool/etc.) on .NET uses the platform's
//    native byte order for multi-byte types.  All supported platforms
//    (x86, x64, ARM LE) are little-endian, so the output is LE — consistent
//    with TransformPacketBuilder (BitConverter.SingleToInt32Bits) and the Go
//    server's binary.LittleEndian encoding.
//  • BinaryWriter.Write(bool) writes a single 0x00/0x01 byte; ReadBoolean()
//    reads one byte and returns false iff the byte is 0. Symmetric. ✅
//  • BinaryWriter.Write(string) emits a 7-bit-encoded length prefix followed
//    by UTF-8 bytes (handled in NetworkVariableString, not here).
//
// Types provided:
//  NetworkVariableInt        — System.Int32    (4 bytes)
//  NetworkVariableFloat      — System.Single   (4 bytes, IEEE 754)
//  NetworkVariableBool       — System.Boolean  (1 byte)
//  NetworkVariableVector3    — UnityEngine.Vector3    (3 × 4 bytes)
//  NetworkVariableQuaternion — UnityEngine.Quaternion (4 × 4 bytes, XYZW)
//
// Note on Quaternion default value:
//  default(Quaternion) = Quaternion(0, 0, 0, 0) which is NOT a valid rotation.
//  Callers creating a rotation variable should pass Quaternion.identity
//  explicitly:
//      new NetworkVariableQuaternion(owner, 3, Quaternion.identity)
//
// Note on float NaN equality:
//  In .NET and Mono, IEquatable<float>.Equals(NaN, NaN) returns TRUE.
//  The Equals implementation special-cases NaN to satisfy the IEquatable
//  contract (an object must be equal to itself).  This differs from the
//  raw == operator which follows IEEE 754 (NaN != NaN).
//  Consequence: setting Value to NaN when it is already NaN is a no-op;
//  the second assignment does NOT fire OnValueChanged.

using System.IO;
using UnityEngine;

namespace RTMPE.Sync
{
    // ── int ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="int"/> (Int32) value.
    /// Serialises as 4 little-endian bytes.
    /// </summary>
    public sealed class NetworkVariableInt : NetworkVariable<int>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier (ushort).</param>
        /// <param name="initialValue">Starting value (default 0).</param>
        public NetworkVariableInt(
            NetworkBehaviour owner,
            ushort           variableId,
            int              initialValue = default)
            : base(owner, variableId, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
            => SetValueWithoutNotify(reader.ReadInt32());
    }

    // ── float ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="float"/> (Single, IEEE 754) value.
    /// Serialises as 4 little-endian bytes.
    /// </summary>
    public sealed class NetworkVariableFloat : NetworkVariable<float>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier.</param>
        /// <param name="initialValue">Starting value (default 0.0f).</param>
        public NetworkVariableFloat(
            NetworkBehaviour owner,
            ushort           variableId,
            float            initialValue = default)
            : base(owner, variableId, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        /// <remarks>
        /// Defensive validation against malicious or corrupted payloads:
        /// non-finite floats (NaN, ±Infinity) are rejected so an
        /// <c>OnValueChanged</c> subscriber that pipes the value into a UI
        /// lerp, a HP-bar fill, or any cumulative float math cannot inherit
        /// a NaN that would freeze the consumer permanently.  On rejection
        /// the prior value is preserved (no event fires) and a Unity warning
        /// is logged.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float v = reader.ReadSingle();
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableFloat.Deserialize: rejected non-finite " +
                    $"value {v} — keeping prior value.");
                return;
            }
            SetValueWithoutNotify(v);
        }
    }

    // ── bool ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="bool"/> value.
    /// Serialises as a single byte (0x00 = false, 0x01 = true).
    /// </summary>
    public sealed class NetworkVariableBool : NetworkVariable<bool>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier.</param>
        /// <param name="initialValue">Starting value (default false).</param>
        public NetworkVariableBool(
            NetworkBehaviour owner,
            ushort           variableId,
            bool             initialValue = default)
            : base(owner, variableId, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer) => writer.Write(Value);

        /// <inheritdoc/>
        public override void Deserialize(BinaryReader reader)
            => SetValueWithoutNotify(reader.ReadBoolean());
    }

    // ── Vector3 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="Vector3"/> value.
    /// Serialises as 12 little-endian bytes (3 × IEEE 754 f32: X, Y, Z).
    ///
   /// Equality uses <see cref="Vector3.Equals(Vector3)"/> which is exact
    /// IEEE 754 component-wise equality.  Set the value only when the change
    /// is intentional, not on every frame, to avoid unnecessary dirty marks.
    /// </summary>
    public sealed class NetworkVariableVector3 : NetworkVariable<Vector3>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier.</param>
        /// <param name="initialValue">Starting value (default Vector3.zero).</param>
        public NetworkVariableVector3(
            NetworkBehaviour owner,
            ushort           variableId,
            Vector3          initialValue = default)
            : base(owner, variableId, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Value.x);
            writer.Write(Value.y);
            writer.Write(Value.z);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Componentwise finiteness gate.  A NaN/Inf component would otherwise
        /// reach an <c>OnValueChanged</c> subscriber that assigns to
        /// <c>transform.position</c> or <c>transform.localScale</c>, which
        /// Unity persists indefinitely and which culls the affected
        /// GameObject's renderer until a domain reload.  Mirrors the
        /// Quaternion validation discipline already in place.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableVector3.Deserialize: rejected non-finite " +
                    $"vector ({x},{y},{z}) — keeping prior value.");
                return;
            }
            SetValueWithoutNotify(new Vector3(x, y, z));
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    // ── Quaternion ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A network-synchronised <see cref="Quaternion"/> value.
    /// Serialises as 16 little-endian bytes (4 × IEEE 754 f32: X, Y, Z, W).
    ///
   /// <b>Important:</b> <c>default(Quaternion)</c> is <c>(0, 0, 0, 0)</c>,
    /// which is NOT a valid unit quaternion.  Pass <see cref="Quaternion.identity"/>
    /// as <paramref name="initialValue"/> for rotation variables:
    /// <code>new NetworkVariableQuaternion(owner, id, Quaternion.identity)</code>
    /// </summary>
    public sealed class NetworkVariableQuaternion : NetworkVariable<Quaternion>
    {
        /// <param name="owner">Owning <see cref="NetworkBehaviour"/>.</param>
        /// <param name="variableId">Per-object unique identifier.</param>
        /// <param name="initialValue">
        /// Starting value.  Pass <see cref="Quaternion.identity"/> for
        /// rotation variables — <c>default(Quaternion)</c> is NOT identity.
        /// </param>
        public NetworkVariableQuaternion(
            NetworkBehaviour owner,
            ushort           variableId,
            Quaternion       initialValue = default)
            : base(owner, variableId, initialValue) { }

        /// <inheritdoc/>
        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Value.x);
            writer.Write(Value.y);
            writer.Write(Value.z);
            writer.Write(Value.w);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Defensive validation against malicious or corrupted payloads:
        /// <list type="bullet">
        ///  <item>NaN/Inf components are rejected (would propagate into
        ///  <c>transform.rotation</c> via game code and break physics).</item>
        ///  <item>Magnitude must lie in [0.9, 1.1] — a legitimate sender may
        ///  accumulate small rounding error, but anything outside that band
        ///  is either a bug or a hostile client trying to inject a non-rotation.</item>
        ///  <item>In-band quaternions are renormalised to unit length before
        ///  exposure so consumers always observe a valid rotation.</item>
        /// </list>
        /// On rejection the prior value is preserved (no <c>OnValueChanged</c>
        /// fires) and a Unity warning is logged to aid investigation.
        /// </remarks>
        public override void Deserialize(BinaryReader reader)
        {
            float x = reader.ReadSingle();
            float y = reader.ReadSingle();
            float z = reader.ReadSingle();
            float w = reader.ReadSingle();

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z) || !IsFinite(w))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableQuaternion.Deserialize: rejected non-finite " +
                    $"quaternion ({x},{y},{z},{w}) — keeping prior value.");
                return;
            }

            float magSq = x * x + y * y + z * z + w * w;
            if (magSq < 0.81f || magSq > 1.21f) // [0.9², 1.1²]
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkVariableQuaternion.Deserialize: rejected non-unit " +
                    $"quaternion (magSq={magSq:F4}, expected ≈1) — keeping prior value.");
                return;
            }

            // Renormalise small rounding error so consumers always read a unit quaternion.
            float invMag = 1f / Mathf.Sqrt(magSq);
            SetValueWithoutNotify(new Quaternion(x * invMag, y * invMag, z * invMag, w * invMag));
        }

        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
