// RTMPE SDK — Runtime/Infrastructure/Serialization/NetworkVariableEncoding.cs
//
// Ergonomic, type-safe wrapper around the discriminated-union encoding of
// network-variable updates (RTMPE.States.NetworkVariableUpdateV2).
//
// FlatBuffers code generation produces low-level Start/Add/End primitives plus
// a `Value<T>()` generic union accessor. Consumer code that talks directly to
// those primitives is verbose, easy to misuse, and forces every callsite to
// duplicate the same Start/Add/End ceremony. The helpers below collapse that
// boilerplate into one method per variant on the build path and one TryRead
// per variant on the read path.
//
// Build path:
//
//   var builder = new FlatBufferBuilder(64);
//   var offset  = NetworkVariableEncoding.CreateInt32Update(
//                     builder, objectId: 7, variableId: 0,
//                     value: 42, timestampUs: 0);
//   builder.Finish(offset.Value);
//   byte[] wire = builder.SizedByteArray();
//
// Read path:
//
//   if (NetworkVariableEncoding.TryReadInt32(update, out int v)) { ... }
//
// All numeric variants are fixed-size; the FlatBuffers structural verifier
// validates the (tag, table-offset) pair atomically and runs the variant
// table's own structural check. The string and bytes readers honour the
// MaxTotalVectorElements semantic cap enforced by VerifiedFlatBuffer.

using System;
using Google.FlatBuffers;
using RTMPE.States;

namespace RTMPE.Infrastructure.Serialization
{
    /// <summary>
    /// Build- and read-path helpers for
    /// <see cref="RTMPE.States.NetworkVariableUpdateV2"/>. Hides the
    /// per-variant Start/Add/End ceremony behind a single method per type
    /// and exposes a TryRead pattern for type-safe consumption.
    /// </summary>
    public static class NetworkVariableEncoding
    {
        // ── Build path: one method per union variant ────────────────────────

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableBool"/> variant.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateBoolUpdate(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            bool value,
            ulong timestampUs)
        {
            NetworkVariableBool.StartNetworkVariableBool(builder);
            NetworkVariableBool.AddValue(builder, value);
            var variant = NetworkVariableBool.EndNetworkVariableBool(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableBool,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableInt32"/> variant.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateInt32Update(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            int value,
            ulong timestampUs)
        {
            NetworkVariableInt32.StartNetworkVariableInt32(builder);
            NetworkVariableInt32.AddValue(builder, value);
            var variant = NetworkVariableInt32.EndNetworkVariableInt32(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableInt32,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableInt64"/> variant.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateInt64Update(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            long value,
            ulong timestampUs)
        {
            NetworkVariableInt64.StartNetworkVariableInt64(builder);
            NetworkVariableInt64.AddValue(builder, value);
            var variant = NetworkVariableInt64.EndNetworkVariableInt64(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableInt64,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableFloat32"/> variant. The numeric value is
        /// not validated here; the consumer should reject non-finite values
        /// at the application layer if its game logic requires it.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateFloat32Update(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            float value,
            ulong timestampUs)
        {
            NetworkVariableFloat32.StartNetworkVariableFloat32(builder);
            NetworkVariableFloat32.AddValue(builder, value);
            var variant = NetworkVariableFloat32.EndNetworkVariableFloat32(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableFloat32,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableFloat64"/> variant.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateFloat64Update(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            double value,
            ulong timestampUs)
        {
            NetworkVariableFloat64.StartNetworkVariableFloat64(builder);
            NetworkVariableFloat64.AddValue(builder, value);
            var variant = NetworkVariableFloat64.EndNetworkVariableFloat64(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableFloat64,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableString"/> variant. A null value produces
        /// an absent string; FlatBuffers represents this as offset 0.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateStringUpdate(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            string value,
            ulong timestampUs)
        {
            var stringOffset = value == null
                ? default(StringOffset)
                : builder.CreateString(value);

            NetworkVariableString.StartNetworkVariableString(builder);
            if (value != null)
            {
                NetworkVariableString.AddValue(builder, stringOffset);
            }
            var variant = NetworkVariableString.EndNetworkVariableString(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableString,
                variant.Value,
                timestampUs);
        }

        /// <summary>
        /// Build a <see cref="NetworkVariableUpdateV2"/> carrying a
        /// <see cref="NetworkVariableBytes"/> variant. A null array produces
        /// an absent vector; an empty array produces a zero-length vector.
        /// </summary>
        public static Offset<NetworkVariableUpdateV2> CreateBytesUpdate(
            FlatBufferBuilder builder,
            uint objectId,
            byte variableId,
            byte[] value,
            ulong timestampUs)
        {
            var bytesOffset = value == null
                ? default(VectorOffset)
                : NetworkVariableBytes.CreateValueVector(builder, value);

            NetworkVariableBytes.StartNetworkVariableBytes(builder);
            if (value != null)
            {
                NetworkVariableBytes.AddValue(builder, bytesOffset);
            }
            var variant = NetworkVariableBytes.EndNetworkVariableBytes(builder);

            return NetworkVariableUpdateV2.CreateNetworkVariableUpdateV2(
                builder,
                objectId,
                variableId,
                NetworkVariableValue.NetworkVariableBytes,
                variant.Value,
                timestampUs);
        }

        // ── Read path: TryRead per variant ──────────────────────────────────

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableBool"/>; returns
        /// <c>false</c> with <paramref name="value"/> set to <c>false</c>
        /// otherwise.
        /// </summary>
        public static bool TryReadBool(NetworkVariableUpdateV2 update, out bool value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableBool)
            {
                value = false;
                return false;
            }
            var variant = update.Value<NetworkVariableBool>();
            if (!variant.HasValue)
            {
                value = false;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableInt32"/>.
        /// </summary>
        public static bool TryReadInt32(NetworkVariableUpdateV2 update, out int value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableInt32)
            {
                value = 0;
                return false;
            }
            var variant = update.Value<NetworkVariableInt32>();
            if (!variant.HasValue)
            {
                value = 0;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableInt64"/>.
        /// </summary>
        public static bool TryReadInt64(NetworkVariableUpdateV2 update, out long value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableInt64)
            {
                value = 0;
                return false;
            }
            var variant = update.Value<NetworkVariableInt64>();
            if (!variant.HasValue)
            {
                value = 0;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableFloat32"/>.
        /// </summary>
        public static bool TryReadFloat32(NetworkVariableUpdateV2 update, out float value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableFloat32)
            {
                value = 0f;
                return false;
            }
            var variant = update.Value<NetworkVariableFloat32>();
            if (!variant.HasValue)
            {
                value = 0f;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableFloat64"/>.
        /// </summary>
        public static bool TryReadFloat64(NetworkVariableUpdateV2 update, out double value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableFloat64)
            {
                value = 0d;
                return false;
            }
            var variant = update.Value<NetworkVariableFloat64>();
            if (!variant.HasValue)
            {
                value = 0d;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> when
        /// the update carries a <see cref="NetworkVariableString"/>. The
        /// returned string may be <c>null</c> if the underlying offset was
        /// absent on the wire.
        /// </summary>
        public static bool TryReadString(NetworkVariableUpdateV2 update, out string value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableString)
            {
                value = null;
                return false;
            }
            var variant = update.Value<NetworkVariableString>();
            if (!variant.HasValue)
            {
                value = null;
                return false;
            }
            value = variant.Value.Value;
            return true;
        }

        /// <summary>
        /// Returns <c>true</c> and assigns <paramref name="value"/> to a
        /// fresh copy of the variant's bytes when the update carries a
        /// <see cref="NetworkVariableBytes"/>. The returned array is owned
        /// by the caller and is independent of the underlying ByteBuffer.
        /// </summary>
        public static bool TryReadBytes(NetworkVariableUpdateV2 update, out byte[] value)
        {
            if (update.ValueType != NetworkVariableValue.NetworkVariableBytes)
            {
                value = null;
                return false;
            }
            var variant = update.Value<NetworkVariableBytes>();
            if (!variant.HasValue)
            {
                value = null;
                return false;
            }
            // GetValueArray copies the underlying vector into a freshly-
            // allocated byte[]; the caller owns the result and may mutate
            // it without affecting any subsequent reader of the same
            // ByteBuffer.
            value = variant.Value.GetValueArray() ?? Array.Empty<byte>();
            return true;
        }
    }
}
