// RTMPE SDK — Runtime/Rooms/PropertyValue.cs
//
// Typed custom-property value shared by RoomInfo and PlayerInfo.  Seven kinds
// are supported, matching the Go server's `entities.PropertyType`
// (see modules/room/domain/entities/properties.go).
//
// Wire limits (also enforced server-side):
//   • max 20 properties per room, 10 per player
//   • max 32-byte UTF-8 key
//   • max 512-byte serialised value

using System;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Discriminator for <see cref="PropertyValue"/>.
    /// String values match the JSON wire representation exactly so no
    /// translation table is required on either side.
    /// </summary>
    public enum PropertyType
    {
        /// <summary>32-bit signed integer.</summary>
        Int,
        /// <summary>32-bit IEEE-754 float.</summary>
        Float,
        /// <summary>Boolean.</summary>
        Bool,
        /// <summary>UTF-8 string.</summary>
        String,
        /// <summary>Arbitrary byte array (base64 in JSON).</summary>
        Bytes,
        /// <summary>[x, y, z] triple of floats.</summary>
        Vector3,
        /// <summary>[r, g, b, a] quadruple of floats (linear space, 0..1).</summary>
        Color,
    }

    /// <summary>
    /// Shared wire-limit constants for custom properties.  Must stay aligned
    /// with <c>entities.MaxProperties*</c> in the Go server.
    /// </summary>
    public static class PropertyLimits
    {
        /// <summary>Maximum keys allowed on a room's custom_properties map.</summary>
        public const int MaxPropertiesPerRoom   = 20;
        /// <summary>Maximum keys allowed on a player's custom_properties map.</summary>
        public const int MaxPropertiesPerPlayer = 10;
        /// <summary>Maximum UTF-8 byte length of a property key.</summary>
        public const int MaxKeyBytes            = 32;
        /// <summary>Maximum byte length of a serialised property value.</summary>
        public const int MaxValueBytes          = 512;
    }

    /// <summary>
    /// Immutable typed custom-property value.
    ///
    /// Construct via one of the static factory methods (<see cref="OfInt"/>,
    /// <see cref="OfFloat"/>, …) so the type/value coupling is always valid.
    /// Readers MAY inspect <see cref="Type"/> to dispatch on kind and then use
    /// the matching accessor — the mismatching accessors throw
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public readonly struct PropertyValue : IEquatable<PropertyValue>
    {
        /// <summary>The discriminator identifying which accessor to call.</summary>
        public PropertyType Type { get; }

        private readonly int            _int;
        private readonly float          _float;
        private readonly bool           _bool;
        private readonly string         _string;
        private readonly byte[]         _bytes;
        private readonly UnityEngine.Vector3 _vector3;
        private readonly UnityEngine.Color   _color;

        // ── Private ctor — callers use the factories ──────────────────────

        private PropertyValue(
            PropertyType type,
            int i = 0, float f = 0f, bool b = false,
            string s = null, byte[] bs = null,
            UnityEngine.Vector3 v3 = default, UnityEngine.Color c = default)
        {
            Type     = type;
            _int     = i;
            _float   = f;
            _bool    = b;
            _string  = s;
            _bytes   = bs;
            _vector3 = v3;
            _color   = c;
        }

        // ── Factories ─────────────────────────────────────────────────────

        /// <summary>Construct a typed int value.</summary>
        public static PropertyValue OfInt(int v)
            => new PropertyValue(PropertyType.Int, i: v);

        /// <summary>Construct a typed float value.</summary>
        public static PropertyValue OfFloat(float v)
            => new PropertyValue(PropertyType.Float, f: v);

        /// <summary>Construct a typed bool value.</summary>
        public static PropertyValue OfBool(bool v)
            => new PropertyValue(PropertyType.Bool, b: v);

        /// <summary>
        /// Construct a typed UTF-8 string value.
        /// <paramref name="v"/> may be null; empty string is accepted.
        /// </summary>
        public static PropertyValue OfString(string v)
            => new PropertyValue(PropertyType.String, s: v ?? string.Empty);

        /// <summary>
        /// Construct a typed byte-array value.  The SDK does NOT copy the
        /// supplied array — callers must treat it as owned by the value.
        /// A null input is normalised to <c>Array.Empty&lt;byte&gt;()</c>.
        /// </summary>
        public static PropertyValue OfBytes(byte[] v)
            => new PropertyValue(PropertyType.Bytes, bs: v ?? Array.Empty<byte>());

        /// <summary>Construct a typed <see cref="UnityEngine.Vector3"/> value.</summary>
        public static PropertyValue OfVector3(UnityEngine.Vector3 v)
            => new PropertyValue(PropertyType.Vector3, v3: v);

        /// <summary>Construct a typed <see cref="UnityEngine.Color"/> value (linear RGBA).</summary>
        public static PropertyValue OfColor(UnityEngine.Color v)
            => new PropertyValue(PropertyType.Color, c: v);

        // ── Typed accessors — throw on type mismatch ───────────────────────

        /// <summary>Returns the underlying int when <see cref="Type"/> is <see cref="PropertyType.Int"/>.</summary>
        public int AsInt()
        {
            Require(PropertyType.Int);
            return _int;
        }

        /// <summary>Returns the underlying float when <see cref="Type"/> is <see cref="PropertyType.Float"/>.</summary>
        public float AsFloat()
        {
            Require(PropertyType.Float);
            return _float;
        }

        /// <summary>Returns the underlying bool when <see cref="Type"/> is <see cref="PropertyType.Bool"/>.</summary>
        public bool AsBool()
        {
            Require(PropertyType.Bool);
            return _bool;
        }

        /// <summary>Returns the underlying string when <see cref="Type"/> is <see cref="PropertyType.String"/>.</summary>
        public string AsString()
        {
            Require(PropertyType.String);
            return _string ?? string.Empty;
        }

        /// <summary>Returns the underlying bytes when <see cref="Type"/> is <see cref="PropertyType.Bytes"/>.</summary>
        public byte[] AsBytes()
        {
            Require(PropertyType.Bytes);
            return _bytes ?? Array.Empty<byte>();
        }

        /// <summary>Returns the underlying vector when <see cref="Type"/> is <see cref="PropertyType.Vector3"/>.</summary>
        public UnityEngine.Vector3 AsVector3()
        {
            Require(PropertyType.Vector3);
            return _vector3;
        }

        /// <summary>Returns the underlying color when <see cref="Type"/> is <see cref="PropertyType.Color"/>.</summary>
        public UnityEngine.Color AsColor()
        {
            Require(PropertyType.Color);
            return _color;
        }

        private void Require(PropertyType expected)
        {
            if (Type != expected)
                throw new InvalidOperationException(
                    $"PropertyValue: accessor for {expected} called on {Type}");
        }

        // ── Boxed object accessor (useful for reflection / logging) ───────

        /// <summary>
        /// Returns the value as a boxed <see cref="object"/>.  Prefer the
        /// typed <c>As*</c> accessors for hot paths — this helper boxes
        /// primitive values and allocates.
        /// </summary>
        public object BoxedValue()
        {
            switch (Type)
            {
                case PropertyType.Int:     return _int;
                case PropertyType.Float:   return _float;
                case PropertyType.Bool:    return _bool;
                case PropertyType.String:  return _string ?? string.Empty;
                case PropertyType.Bytes:   return _bytes  ?? Array.Empty<byte>();
                case PropertyType.Vector3: return _vector3;
                case PropertyType.Color:   return _color;
                default:
                    throw new InvalidOperationException($"Unknown PropertyType: {Type}");
            }
        }

        // ── Equality ───────────────────────────────────────────────────────

        /// <inheritdoc/>
        public bool Equals(PropertyValue other)
        {
            if (Type != other.Type) return false;
            switch (Type)
            {
                case PropertyType.Int:     return _int     == other._int;
                case PropertyType.Float:   return _float.Equals(other._float);
                case PropertyType.Bool:    return _bool    == other._bool;
                case PropertyType.String:  return string.Equals(_string, other._string, StringComparison.Ordinal);
                case PropertyType.Bytes:   return BytesEqual(_bytes, other._bytes);
                case PropertyType.Vector3: return _vector3 == other._vector3;
                case PropertyType.Color:   return _color   == other._color;
                default: return false;
            }
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
            => obj is PropertyValue v && Equals(v);

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            // Hash type + a representative field so equal values collide.
            unchecked
            {
                int h = (int)Type * 397;
                switch (Type)
                {
                    case PropertyType.Int:     return h ^ _int;
                    case PropertyType.Float:   return h ^ _float.GetHashCode();
                    case PropertyType.Bool:    return h ^ (_bool ? 1 : 0);
                    case PropertyType.String:  return h ^ (_string?.GetHashCode() ?? 0);
                    case PropertyType.Bytes:   return h ^ (_bytes?.Length ?? 0);
                    case PropertyType.Vector3: return h ^ _vector3.GetHashCode();
                    case PropertyType.Color:   return h ^ _color.GetHashCode();
                    default: return h;
                }
            }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length)   return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }
    }
}
