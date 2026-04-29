// RTMPE SDK — Runtime/Infrastructure/Serialization/SafeFlatBufferAccessors.cs
//
// Hardened accessors layered on top of the vendored Google.FlatBuffers
// runtime. The vendored ByteBuffer / Table read APIs were designed for
// trusted producers and silently accept several adversarial inputs:
//
//  • Encoding.UTF8.GetString substitutes U+FFFD for malformed sequences
//    instead of failing closed.
//  • GetFloat / GetDouble reinterpret raw 4 / 8 bytes with no IsFinite
//    check, allowing NaN / +Inf / -Inf to leak into Unity transforms,
//    physics, or interpolators.
//  • A byte that is not a valid ValueType enum value still casts cleanly
//    and reaches the dispatch switch.
//
// This file centralises the validating versions used by VerifiedFlatBuffer
// after the structural verifier has run. Throwing here is intentional: the
// receive-path wrapper (VerifiedFlatBuffer.TryGetRoot) catches everything
// and converts to a packet drop, so a malformed field becomes a logged
// rejection rather than a corrupt-state propagation.

using System;
using System.Text;
using Google.FlatBuffers;
using RTMPE.States;
// RTMPE.States.ValueType collides with System.ValueType; alias to the
// schema enum so unqualified references below resolve unambiguously.
using ValueType = RTMPE.States.ValueType;

namespace RTMPE.Infrastructure.Serialization
{
    /// <summary>
    /// Hardened scalar / string / enum accessors used at the FlatBuffers
    /// receive boundary. Each accessor throws an exception on adversarial
    /// input so the caller can drop the packet via a single catch site.
    /// </summary>
    public static class SafeFlatBufferAccessors
    {
        /// <summary>
        /// Hard cap on the cumulative element count across every vector in
        /// a single payload. The largest legitimate StateSyncPayload in the
        /// SDK carries fewer than 256 entries combined; 4096 leaves room
        /// for future growth without admitting bombs that allocate one
        /// managed object per element on a 16 KiB packet.
        /// </summary>
        public const int MaxTotalVectorElements = 4096;

        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Decode <paramref name="bytes"/> as UTF-8 with strict validation.
        /// Throws <see cref="System.Text.DecoderFallbackException"/> for
        /// any malformed byte sequence and <see cref="InvalidOperationException"/>
        /// when an embedded NUL is present.
        /// </summary>
        public static string DecodeStrictUtf8(byte[] bytes, int offset, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || length < 0 || offset > bytes.Length - length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            for (int i = 0; i < length; i++)
            {
                if (bytes[offset + i] == 0)
                {
                    throw new InvalidOperationException("string contains embedded NUL");
                }
            }
            return StrictUtf8.GetString(bytes, offset, length);
        }

        /// <summary>
        /// Read a 32-bit little-endian float from the buffer and reject any
        /// non-finite value. Use at the parser boundary where the value is
        /// destined for Unity transforms / physics / interpolators.
        /// </summary>
        public static float SafeGetFloat(ByteBuffer bb, int offset)
        {
            float v = bb.GetFloat(offset);
            if (!IsFinite(v))
            {
                throw new InvalidOperationException("float field is not finite");
            }
            return v;
        }

        /// <summary>
        /// Read a 64-bit little-endian double from the buffer and reject
        /// any non-finite value.
        /// </summary>
        public static double SafeGetDouble(ByteBuffer bb, int offset)
        {
            double v = bb.GetDouble(offset);
            if (!IsFinite(v))
            {
                throw new InvalidOperationException("double field is not finite");
            }
            return v;
        }

        /// <summary>
        /// True when <paramref name="value"/> is not NaN and not infinite.
        /// Equivalent to <c>float.IsFinite</c> on .NET Standard 2.1; provided
        /// here so the helper compiles on the older .NET Standard 2.0 target
        /// the Unity 2021 LTS path still uses.
        /// </summary>
        public static bool IsFinite(float value)
        {
            // NaN compared to itself is false; ±Inf has the exponent saturated.
            // Combining both guards covers the full non-finite set.
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// True when <paramref name="value"/> is not NaN and not infinite.
        /// </summary>
        public static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// True when the byte represents a defined member of the
        /// <see cref="ValueType"/> enum. Used to fail closed before the
        /// downstream dispatch switch sees an undefined tag.
        /// </summary>
        public static bool IsValid(ValueType valueType)
        {
            switch (valueType)
            {
                case ValueType.Bool:
                case ValueType.Int32:
                case ValueType.Int64:
                case ValueType.Float32:
                case ValueType.Float64:
                case ValueType.String:
                case ValueType.Bytes:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Validate a <see cref="ValueType"/> tag at the parser boundary.
        /// Throws when the byte falls outside the defined range so callers
        /// never observe an undefined union discriminator.
        /// </summary>
        public static ValueType RequireValid(ValueType valueType)
        {
            if (!IsValid(valueType))
            {
                throw new InvalidOperationException(
                    "ValueType tag out of defined range: " + (byte)valueType);
            }
            return valueType;
        }
    }

    /// <summary>
    /// Extension surface that hangs the safe accessors off the generated
    /// FlatBuffers structs without touching the auto-generated source. The
    /// FlatBuffers compiler emits non-partial struct types, so partial-class
    /// patching is not available; extension methods are the next-best seam.
    /// </summary>
    public static class FlatBufferSafeExtensions
    {
        /// <summary>
        /// Validate the <see cref="NetworkVariableUpdate.ValueType"/> tag
        /// before any consumer dispatches on it.
        /// </summary>
        public static ValueType SafeValueType(this NetworkVariableUpdate update)
        {
            return SafeFlatBufferAccessors.RequireValid(update.ValueType);
        }

        /// <summary>
        /// Validate the <see cref="InputPayload.MoveX"/> field is finite.
        /// </summary>
        public static float SafeMoveX(this InputPayload payload)
        {
            float v = payload.MoveX;
            if (!SafeFlatBufferAccessors.IsFinite(v))
            {
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            }
            return v;
        }

        /// <summary>
        /// Validate the <see cref="InputPayload.MoveY"/> field is finite.
        /// </summary>
        public static float SafeMoveY(this InputPayload payload)
        {
            float v = payload.MoveY;
            if (!SafeFlatBufferAccessors.IsFinite(v))
            {
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            }
            return v;
        }
    }
}
