// RTMPE SDK — Runtime/Rooms/PropertyPacketBuilder.cs
//
// Builds the payload bytes for custom property packets:
//
//  RoomPropertyUpdate   (0x24): client → server → all room clients
//  PlayerPropertyUpdate (0x25): client → server → all room clients
//
// Payload encoding is UTF-8 JSON (not binary), matching the Room Service's
// existing `json.Unmarshal(envelope.Payload, &payload)` handler contract.
// The caller passes the returned bytes to PacketBuilder.Build with the
// appropriate PacketType and FLAG_RELIABLE.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Produces the JSON-encoded payload bytes for
    /// <c>RoomPropertyUpdate</c> (0x24) and <c>PlayerPropertyUpdate</c> (0x25)
    /// packets.  Enforces client-side size caps up-front — a malformed request
    /// is rejected with an <see cref="ArgumentException"/> before it leaves
    /// the SDK, so the server never has to see it.
    /// </summary>
    public static class PropertyPacketBuilder
    {
        /// <summary>
        /// Build the payload for a <c>RoomPropertyUpdate</c> (0x24) packet.
        /// </summary>
        /// <param name="expectedVersion">
        /// The version number the client expects AFTER the update commits.
        /// Server rejects anything other than <c>currentVersion + 1</c>.
        /// </param>
        /// <param name="properties">The properties to set.  A property with
        /// the default / zero-valued <see cref="PropertyValue"/> is reserved
        /// for deletion semantics in a future release — callers should only
        /// pass explicit typed values.</param>
        public static byte[] BuildRoomPayload(
            int expectedVersion,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (expectedVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                    "expectedVersion must be >= 1 (monotonic, 1-based).");
            ValidateProperties(properties, PropertyLimits.MaxPropertiesPerRoom);

            string json = PropertyJson.EncodeRoomPayload(expectedVersion, properties);
            return Encoding.UTF8.GetBytes(json);
        }

        /// <summary>
        /// Build the payload for a <c>PlayerPropertyUpdate</c> (0x25) packet.
        /// </summary>
        /// <param name="playerId">
        /// The authenticated player's UUID.  The server rejects the packet
        /// when this does not match the session's player (self-only invariant).
        /// </param>
        /// <param name="expectedVersion">See <see cref="BuildRoomPayload"/>.</param>
        /// <param name="properties">The properties to set.</param>
        public static byte[] BuildPlayerPayload(
            string playerId,
            int expectedVersion,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId must not be null or empty.", nameof(playerId));
            if (expectedVersion < 1)
                throw new ArgumentOutOfRangeException(nameof(expectedVersion),
                    "expectedVersion must be >= 1 (monotonic, 1-based).");
            ValidateProperties(properties, PropertyLimits.MaxPropertiesPerPlayer);

            string json = PropertyJson.EncodePlayerPayload(playerId, expectedVersion, properties);
            return Encoding.UTF8.GetBytes(json);
        }

        // ─── Shared validation ─────────────────────────────────────────────

        private static void ValidateProperties(
            IReadOnlyDictionary<string, PropertyValue> properties,
            int maxCount)
        {
            if (properties == null)
                throw new ArgumentNullException(nameof(properties));
            if (properties.Count == 0)
                throw new ArgumentException(
                    "At least one property must be supplied.", nameof(properties));
            if (properties.Count > maxCount)
                throw new ArgumentException(
                    $"Too many properties: {properties.Count} > limit {maxCount}.",
                    nameof(properties));

            foreach (var kv in properties)
            {
                if (string.IsNullOrEmpty(kv.Key))
                    throw new ArgumentException("Property key must not be null or empty.", nameof(properties));

                int keyBytes = Encoding.UTF8.GetByteCount(kv.Key);
                if (keyBytes > PropertyLimits.MaxKeyBytes)
                    throw new ArgumentException(
                        $"Property key '{kv.Key}' exceeds max {PropertyLimits.MaxKeyBytes} UTF-8 bytes (got {keyBytes}).",
                        nameof(properties));

                EnsureValueWithinLimit(kv.Key, kv.Value);
            }
        }

        /// <summary>
        /// Enforces the 512-byte value cap by ESTIMATING the value tuple's
        /// UTF-8 byte count without re-serialising the whole payload.  The
        /// previous implementation called <c>EncodeRoomPayload</c> twice per
        /// property (baseline + probed) — quadratic in dictionary size when
        /// validating a 50-key update (100 full encodings).  The estimator
        /// here mirrors <c>PropertyJson</c>'s formatting so the result is
        /// exact (not heuristic) and is computed in O(value_size) without
        /// allocating a probe dictionary.
        /// </summary>
        private static void EnsureValueWithinLimit(string key, PropertyValue v)
        {
            int valueTupleBytes = PropertyJsonSizing.EstimateValueTupleBytes(v);
            if (valueTupleBytes > PropertyLimits.MaxValueBytes)
                throw new ArgumentException(
                    $"Property '{key}' value exceeds max {PropertyLimits.MaxValueBytes} UTF-8 bytes "
                    + $"(measured {valueTupleBytes}).",
                    nameof(v));
        }
    }

    /// <summary>
    /// Internal sizing helper.  Mirrors the formatting decisions made by
    /// <see cref="PropertyJson"/> so the byte count returned here matches
    /// the byte count of the actual encoded payload exactly (no slack
    /// margin).  The encoder and the estimator MUST be updated together —
    /// any divergence would let a too-large value slip past validation
    /// or reject a legal value at the boundary.
    /// </summary>
    internal static class PropertyJsonSizing
    {
        /// <summary>
        /// Returns the UTF-8 byte count of the canonical JSON encoding of
        /// <c>{"type":"&lt;tag&gt;","value":&lt;value&gt;}</c> for the
        /// supplied <paramref name="v"/> — i.e. the inner tuple wrapped
        /// around each property's value in the wire payload.
        /// </summary>
        internal static int EstimateValueTupleBytes(PropertyValue v)
        {
            // Fixed framing of the {"type":"<T>","value":<V>} wrapper:
            //     `{`        →  1
            //     `"type":"` →  8
            //     <T>        →  T
            //     `","value":` → 10
            //     <V>        →  V
            //     `}`        →  1
            //   Total = 20 + T + V.
            int typeTagLen = TagFor(v.Type).Length;
            int valueChars = ValueByteCount(v);
            return 20 + typeTagLen + valueChars;
        }

        private static string TagFor(PropertyType t)
        {
            switch (t)
            {
                case PropertyType.Int:     return PropertyJson.TagInt;
                case PropertyType.Float:   return PropertyJson.TagFloat;
                case PropertyType.Bool:    return PropertyJson.TagBool;
                case PropertyType.String:  return PropertyJson.TagString;
                case PropertyType.Bytes:   return PropertyJson.TagBytes;
                case PropertyType.Vector3: return PropertyJson.TagVector3;
                case PropertyType.Color:   return PropertyJson.TagColor;
                default: throw new InvalidOperationException($"Unknown PropertyType: {t}");
            }
        }

        /// <summary>
        /// UTF-8 byte count of the JSON encoding of the value alone (without
        /// the `"value":` key prefix or surrounding tuple braces).  Mirrors
        /// <see cref="PropertyJson.AppendValue"/> exactly.
        /// </summary>
        private static int ValueByteCount(PropertyValue v)
        {
            switch (v.Type)
            {
                case PropertyType.Int:
                    return v.AsInt().ToString(System.Globalization.CultureInfo.InvariantCulture).Length;

                case PropertyType.Float:
                    // Round-trip "R" formatting matches PropertyJson exactly;
                    // ASCII-only, so .Length == UTF-8 byte count.
                    return v.AsFloat().ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;

                case PropertyType.Bool:
                    return v.AsBool() ? 4 /* true */ : 5 /* false */;

                case PropertyType.String:
                    {
                        // Two surrounding quotes plus the JSON-escaped UTF-8
                        // body — escape rules MUST match PropertyJson.AppendJsonString.
                        var s = v.AsString();
                        return 2 + EscapedJsonStringByteCount(s);
                    }

                case PropertyType.Bytes:
                    {
                        // Base64: 4 chars per 3 input bytes, padded.  Two
                        // surrounding quotes added on top.  Base64 is ASCII so
                        // char count == byte count.
                        int n = v.AsBytesReadOnly().Length;
                        return 2 + ((n + 2) / 3) * 4;
                    }

                case PropertyType.Vector3:
                    {
                        var vec = v.AsVector3();
                        // [x,y,z]  — three R-formatted floats, two commas, two brackets.
                        int len = 2 /* [] */ + 2 /* commas */;
                        len += vec.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += vec.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += vec.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        return len;
                    }

                case PropertyType.Color:
                    {
                        var c = v.AsColor();
                        int len = 2 /* [] */ + 3 /* commas */;
                        len += c.r.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.g.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.b.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        len += c.a.ToString("R", System.Globalization.CultureInfo.InvariantCulture).Length;
                        return len;
                    }

                default:
                    throw new InvalidOperationException($"Unknown PropertyType: {v.Type}");
            }
        }

        /// <summary>
        /// UTF-8 byte count of the JSON-escaped string body (excluding the
        /// surrounding quotes).  Replicates the escape decisions made by
        /// <see cref="PropertyJson.AppendJsonString"/>.
        /// </summary>
        private static int EscapedJsonStringByteCount(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':
                    case '\\':
                    case '\b':
                    case '\f':
                    case '\n':
                    case '\r':
                    case '\t':
                        total += 2;  // backslash escape
                        break;
                    default:
                        if (c < 0x20)
                        {
                            total += 6;  // \uXXXX
                        }
                        else
                        {
                            // Multi-byte UTF-8 widths.  Mirror System.Text.Encoding.UTF8
                            // exactly:
                            //   U+0000..U+007F    →  1 byte
                            //   U+0080..U+07FF    →  2 bytes
                            //   U+0800..U+FFFF    →  3 bytes
                            //   U+10000..U+10FFFF →  4 bytes (surrogate pair)
                            if (c < 0x80)
                            {
                                total += 1;
                            }
                            else if (c < 0x800)
                            {
                                total += 2;
                            }
                            else if (char.IsHighSurrogate(c)
                                && i + 1 < s.Length
                                && char.IsLowSurrogate(s[i + 1]))
                            {
                                // Surrogate pair = single Unicode scalar in
                                // the supplementary plane → 4 UTF-8 bytes.
                                total += 4;
                                i++;
                            }
                            else
                            {
                                // BMP (or unpaired surrogate, which UTF-8
                                // emitter encodes as 3-byte replacement).
                                total += 3;
                            }
                        }
                        break;
                }
            }
            return total;
        }
    }
}
