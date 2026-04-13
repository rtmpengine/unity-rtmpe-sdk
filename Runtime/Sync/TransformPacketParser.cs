// RTMPE SDK — Runtime/Sync/TransformPacketParser.cs
//
// Parses server-to-client StateDelta payloads into TransformState values.
//
// Wire format produced by Go's StateDelta.Serialize() (state_delta.go):
//   [0..7]  object_id    : u64  (little-endian)
//   [8]     changed_mask : u8   (bit flags, see constants below)
//   [opt]   position     : 3 × f32 LE   (12 bytes; present iff bit 0x01 set)
//   [opt]   rotation     : 4 × f32 LE   (16 bytes; present iff bit 0x02 set)
//   [opt]   scale        : 3 × f32 LE   (12 bytes; present iff bit 0x04 set)
//
// Changed-field bit constants MUST match Go's state_delta.go:
//   ChangedPosition byte = 1 << 0  // 0x01
//   ChangedRotation byte = 1 << 1  // 0x02
//   ChangedScale    byte = 1 << 2  // 0x04
//   knownMask       byte = 0x07
//
// Unknown bits (bits 3..7) are rejected → TryParseStateDelta returns false.
// This prevents silent field misalignment when the protocol adds new fields.
//
// Caller responsibility:
//   Check changedMask after a successful parse.  Only fields with their
//   corresponding bit set carry meaningful values.  State fields whose bits
//   are NOT set hold zero initialisation values and must be ignored.
//
// Thread safety: all methods are static; no shared state.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Parses server-to-client <c>StateDelta</c> payloads.
    /// Bit constants mirror Go's <c>domain/entities/state_delta.go</c>.
    /// </summary>
    public static class TransformPacketParser
    {
        // ── Changed-field bit flags ────────────────────────────────────────────
        //
        // SYNC RULE: These values must equal the Go constants in state_delta.go.
        //   ChangedPosition byte = 1 << 0  // 0x01
        //   ChangedRotation byte = 1 << 1  // 0x02
        //   ChangedScale    byte = 1 << 2  // 0x04
        //   knownMask       byte = 0x07
        //
        // A mismatch causes the parser to silently decode wrong fields.

        /// <summary>Bit indicating the Position field is present in the delta.</summary>
        public const byte ChangedPosition = 0x01;

        /// <summary>Bit indicating the Rotation field is present in the delta.</summary>
        public const byte ChangedRotation = 0x02;

        /// <summary>Bit indicating the Scale field is present in the delta.</summary>
        public const byte ChangedScale = 0x04;

        /// <summary>All currently known field bits.  Any bits outside this mask are unknown.</summary>
        public const byte KnownMask = 0x07;

        // ── Size constants ─────────────────────────────────────────────────────

        /// <summary>
        /// Minimum valid payload size: ObjectID(8) + ChangedMask(1) = 9 bytes.
        /// A delta with ChangedMask=0 is valid and means no fields changed.
        /// </summary>
        public const int DELTA_MIN_SIZE = 9;

        private const int POSITION_SIZE = 12; // 3 × f32
        private const int ROTATION_SIZE = 16; // 4 × f32 (x y z w)
        private const int SCALE_SIZE    = 12; // 3 × f32

        // ── Parser ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Try to parse a server-sent <c>StateDelta</c> payload.
        /// </summary>
        /// <param name="payload">
        /// Raw payload bytes (the data AFTER the 13-byte RTMPE header).
        /// </param>
        /// <param name="objectId">
        /// On success: the server-assigned object ID this delta targets.
        /// </param>
        /// <param name="changedMask">
        /// On success: the bit mask indicating which fields are present.
        /// Inspect with <see cref="ChangedPosition"/>, <see cref="ChangedRotation"/>,
        /// <see cref="ChangedScale"/> before reading the corresponding
        /// <paramref name="state"/> field.
        /// </param>
        /// <param name="state">
        /// On success: the decoded transform fields.
        /// <b>Only fields whose bit is set in <paramref name="changedMask"/>
        /// carry valid data.</b>  All other fields hold zero-initialised values.
        /// </param>
        /// <returns>
        /// <see langword="true"/> if the payload is well-formed;
        /// <see langword="false"/> on any truncation, null, or unknown bit.
        /// </returns>
        public static bool TryParseStateDelta(
            byte[] payload,
            out ulong objectId,
            out byte  changedMask,
            out TransformState state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            // Guard: null or too short to hold ObjectID + ChangedMask.
            if (payload == null || payload.Length < DELTA_MIN_SIZE)
                return false;

            int off = 0;

            // ObjectID (u64 LE)
            objectId = ReadU64LE(payload, off);
            off += 8;

            // ChangedMask (u8)
            changedMask = payload[off++];

            // Reject unknown bits — any future protocol extension will set
            // a bit outside KnownMask; reading unknown fields would misalign
            // all subsequent offsets and corrupt the decoded state.
            if ((changedMask & ~KnownMask) != 0)
                return false;

            // Start from zero-initialised values; only populate bits that are set.
            var pos   = Vector3.zero;
            var rot   = new Quaternion(0f, 0f, 0f, 0f); // raw zero — NOT identity; caller checks mask
            var scale = Vector3.zero;

            // Position (3 × f32 LE) — conditional on bit 0x01
            if ((changedMask & ChangedPosition) != 0)
            {
                if (off + POSITION_SIZE > payload.Length) return false;
                pos.x = ReadF32LE(payload, off);     off += 4;
                pos.y = ReadF32LE(payload, off);     off += 4;
                pos.z = ReadF32LE(payload, off);     off += 4;
            }

            // Rotation (4 × f32 LE, x y z w) — conditional on bit 0x02
            if ((changedMask & ChangedRotation) != 0)
            {
                if (off + ROTATION_SIZE > payload.Length) return false;
                rot.x = ReadF32LE(payload, off);     off += 4;
                rot.y = ReadF32LE(payload, off);     off += 4;
                rot.z = ReadF32LE(payload, off);     off += 4;
                rot.w = ReadF32LE(payload, off);     off += 4;
            }

            // Scale (3 × f32 LE) — conditional on bit 0x04
            if ((changedMask & ChangedScale) != 0)
            {
                if (off + SCALE_SIZE > payload.Length) return false;
                scale.x = ReadF32LE(payload, off);   off += 4;
                scale.y = ReadF32LE(payload, off);   off += 4;
                scale.z = ReadF32LE(payload, off);   off += 4;
            }

            state = new TransformState { Position = pos, Rotation = rot, Scale = scale };
            return true;
        }

        // ── Private read helpers ───────────────────────────────────────────────

        // ReadU64LE reads eight consecutive bytes as a little-endian u64.
        private static ulong ReadU64LE(byte[] buf, int off)
            =>  (ulong)buf[off + 0]
             | ((ulong)buf[off + 1] <<  8)
             | ((ulong)buf[off + 2] << 16)
             | ((ulong)buf[off + 3] << 24)
             | ((ulong)buf[off + 4] << 32)
             | ((ulong)buf[off + 5] << 40)
             | ((ulong)buf[off + 6] << 48)
             | ((ulong)buf[off + 7] << 56);

        // ReadF32LE reads four consecutive bytes as a little-endian IEEE 754 f32.
        // BitConverter.Int32BitsToSingle performs zero-allocation bit reinterpretation
        // (available in .NET Standard 2.1 / Unity 2019.3+).
        private static float ReadF32LE(byte[] buf, int off)
        {
            int bits =  buf[off + 0]
                     | (buf[off + 1] <<  8)
                     | (buf[off + 2] << 16)
                     | (buf[off + 3] << 24);
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
