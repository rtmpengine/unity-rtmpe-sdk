// RTMPE SDK — Runtime/Rooms/PropertyPacketBuilder.cs
//
// Builds the payload bytes for custom property packets:
//
//   RoomPropertyUpdate   (0x24): client → server → all room clients
//   PlayerPropertyUpdate (0x25): client → server → all room clients
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

        // ─── Probe keys reserved for EnsureValueWithinLimit ──────────────
        //
        // Two single-key probe payloads measure (a) the "framing" byte count
        // (version + properties + type wrapper) and (b) the same framing
        // plus a specific value.  Their difference is the exact UTF-8
        // byte count of the serialised value — independent of char vs.
        // multi-byte concerns that plagued an earlier approach that used
        // string.Length (UTF-16 code units) instead of Encoding.UTF8 byte
        // count.  The probe key ("_P") is inert at MaxPropertyKeyBytes so
        // it contributes a constant to both measurements.
        private const string ProbeKey = "_P";

        /// <summary>
        /// Enforces the 512-byte value cap by measuring the value's exact
        /// UTF-8 byte count in the canonical JSON encoding — which is the
        /// authoritative size observed on the wire.
        ///
        /// Implementation detail: we compute the byte size of the entire
        /// canonical encoding twice — once with the probe value, once with
        /// a tiny int sentinel — and subtract the framing overhead, leaving
        /// the precise UTF-8 size of the value tuple.  This is O(1) in
        /// allocations and avoids the earlier string.Length heuristic that
        /// silently accepted multi-byte payloads over the limit.
        /// </summary>
        private static void EnsureValueWithinLimit(string key, PropertyValue v)
        {
            // Framing baseline: same key, trivial (1-byte) value.
            var baseline = new Dictionary<string, PropertyValue>(1)
            {
                { ProbeKey, PropertyValue.OfInt(0) },
            };
            int baselineBytes = Encoding.UTF8.GetByteCount(
                PropertyJson.EncodeRoomPayload(1, baseline));

            // With the actual value substituted under the same key.
            var probed = new Dictionary<string, PropertyValue>(1)
            {
                { ProbeKey, v },
            };
            int probedBytes = Encoding.UTF8.GetByteCount(
                PropertyJson.EncodeRoomPayload(1, probed));

            // The baseline encodes `{"type":"int","value":0}` for the inner
            // `value` tuple (23 bytes).  Subtracting that constant yields
            // the exact UTF-8 byte size of the value+type tuple we are
            // limiting.  Using a constant avoids parsing the probe output.
            const int baselineValueTupleBytes = 23; // strlen({"type":"int","value":0})
            int valueTupleBytes = probedBytes - baselineBytes + baselineValueTupleBytes;

            if (valueTupleBytes > PropertyLimits.MaxValueBytes)
                throw new ArgumentException(
                    $"Property '{key}' value exceeds max {PropertyLimits.MaxValueBytes} UTF-8 bytes "
                    + $"(measured {valueTupleBytes}).",
                    nameof(v));
        }
    }
}
