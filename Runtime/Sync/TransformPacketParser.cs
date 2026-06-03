// RTMPE SDK — Runtime/Sync/TransformPacketParser.cs
//
// Parses server-to-client StateDelta payloads into TransformState values.
//
// Wire format produced by Go's StateDelta.Serialize() (state_delta.go):
//  [0..7]  object_id    : u64  (little-endian)
//  [8]     changed_mask : u8   (bit flags, see constants below)
//  [opt]   position     : 3 × f32 LE   (12 bytes; present iff bit 0x01 set)
//  [opt]   rotation     : 4 × f32 LE   (16 bytes; present iff bit 0x02 set)
//  [opt]   scale        : 3 × f32 LE   (12 bytes; present iff bit 0x04 set)
//  [opt]   input_tick   : u32 LE       (4 bytes;  present iff bit 0x08 set; SDKS-01)
//
// Changed-field bit constants MUST match Go's state_delta.go:
//  ChangedPosition  byte = 1 << 0  // 0x01
//  ChangedRotation  byte = 1 << 1  // 0x02
//  ChangedScale     byte = 1 << 2  // 0x04
//  ChangedInputTick byte = 1 << 3  // 0x08  (SDKS-01)
//  knownMask        byte = 0x0F
//
// Unknown bits (bits 4..7) are rejected → TryParseStateDelta returns false.
// This prevents silent field misalignment when the protocol adds new fields.
//
// Caller responsibility:
//  Check changedMask after a successful parse.  Only fields with their
//  corresponding bit set carry meaningful values.  State fields whose bits
//  are NOT set hold zero initialisation values and must be ignored.
//
// Thread safety: all methods are static; no shared state.

using System;
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
        //  ChangedPosition  byte = 1 << 0  // 0x01
        //  ChangedRotation  byte = 1 << 1  // 0x02
        //  ChangedScale     byte = 1 << 2  // 0x04
        //  ChangedInputTick byte = 1 << 3  // 0x08  (SDKS-01)
        //  knownMask        byte = 0x0F
        //
       // A mismatch causes the parser to silently decode wrong fields.

        /// <summary>Bit indicating the Position field is present in the delta.</summary>
        public const byte ChangedPosition = 0x01;

        /// <summary>Bit indicating the Rotation field is present in the delta.</summary>
        public const byte ChangedRotation = 0x02;

        /// <summary>Bit indicating the Scale field is present in the delta.</summary>
        public const byte ChangedScale = 0x04;

        /// <summary>
        /// Convenience mask covering the three transform fields (position,
        /// rotation, scale) — <b>not</b> the input-tick bit.  Mirrors Go's
        /// <c>ChangedAll</c> (0x07) in <c>state_delta.go</c>; use this when a
        /// caller or test means "all transform fields" as distinct from
        /// <see cref="KnownMask"/> (which also includes <see cref="ChangedInputTick"/>).
        /// </summary>
        public const byte ChangedAll = (byte)(ChangedPosition | ChangedRotation | ChangedScale); // 0x07

        /// <summary>
        /// Bit indicating an <c>InputTick</c> (u32 LE) trailing field is present
        /// (SDKS-01).  Set by the Sync Service on incremental deltas so the
        /// owning client can acknowledge its input buffer up to that tick.
        /// MUST equal <c>ChangedInputTick</c> in Go's <c>state_delta.go</c>.
        /// </summary>
        public const byte ChangedInputTick = 0x08;

        /// <summary>
        /// All currently known field bits.  Any bits outside this mask are
        /// unknown and cause a parse rejection.  MUST equal <c>knownMask</c>
        /// (0x0F) in Go's <c>state_delta.go</c>.
        /// </summary>
        public const byte KnownMask = 0x0F;

        /// <summary>
        /// Total wire size of a client-to-server quantized transform-update
        /// payload built by <c>TransformPacketBuilder.BuildQuantizedUpdatePayload</c>.
        /// </summary>
        public const int QUANTIZED_UPDATE_SIZE = 25;

        /// <summary>
        /// Bit set in the leading <c>flags</c> byte of a quantized payload.
        /// </summary>
        public const byte FLAG_QUANTIZED = 0x01;

        // ── Size constants ─────────────────────────────────────────────────────

        /// <summary>
        /// Minimum valid payload size: ObjectID(8) + ChangedMask(1) = 9 bytes.
        /// A delta with ChangedMask=0 is valid and means no fields changed.
        /// </summary>
        public const int DELTA_MIN_SIZE = 9;

        private const int POSITION_SIZE   = 12; // 3 × f32
        private const int ROTATION_SIZE   = 16; // 4 × f32 (x y z w)
        private const int SCALE_SIZE      = 12; // 3 × f32
        private const int INPUT_TICK_SIZE = 4;  // 1 × u32 LE (SDKS-01)

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

            if (payload == null) return false;
            int offset = 0;
            if (!TryParseStateDeltaAt(
                    payload, ref offset,
                    out objectId, out changedMask, out state))
                return false;

            // The single-record overload preserves its strict contract: a
            // well-formed StateDelta must end exactly where the last selected
            // field's bytes end.  Surplus bytes here indicate either an
            // ambiguous concatenated payload (handled by the iteration
            // overload elsewhere) or a protocol-drift / smuggling signal
            // that this overload's callers must reject.
            return offset == payload.Length;
        }

        /// <summary>
        /// Parse a single <c>StateDelta</c> record from <paramref name="payload"/>
        /// starting at <paramref name="offset"/>, advancing <paramref name="offset"/>
        /// past the consumed bytes on success.
        ///
        /// <para>Used by the receive iteration in <c>NetworkManager.HandleStateSyncPacket</c>
        /// to decode multi-delta packets emitted by the Sync Service's
        /// <c>BroadcastSyncFrame</c> (`.delta` subject), which concatenates one
        /// serialised <c>StateDelta</c> per changed object into a single
        /// <c>PacketType.StateSync</c> frame.</para>
        ///
        /// <para>Unlike <see cref="TryParseStateDelta"/> this method does NOT
        /// reject trailing bytes — the iteration's outer loop is responsible
        /// for resuming at the new <paramref name="offset"/> until the buffer
        /// is exhausted.  Truncation, unknown mask bits, non-finite floats,
        /// and non-unit quaternions are still rejected so a malformed record
        /// cannot corrupt the receiver's transform.</para>
        /// </summary>
        public static bool TryParseStateDeltaAt(
            byte[] payload,
            ref int offset,
            out ulong objectId,
            out byte changedMask,
            out TransformState state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            if (payload == null || offset < 0 || offset > payload.Length - DELTA_MIN_SIZE)
                return false;

            int off = offset;

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

            // Position (3 × f32 LE) — conditional on bit 0x01.  Bounds are
            // expressed in subtraction form (`size > available`) so the
            // additive form's int-wrap surface — `off` near int.MaxValue
            // would let `off + N` wrap to a negative value and bypass the
            // check — does not apply here.
            if ((changedMask & ChangedPosition) != 0)
            {
                if (POSITION_SIZE > payload.Length - off) return false;
                pos.x = ReadF32LE(payload, off);     off += 4;
                pos.y = ReadF32LE(payload, off);     off += 4;
                pos.z = ReadF32LE(payload, off);     off += 4;
                if (!IsFinite(pos.x) || !IsFinite(pos.y) || !IsFinite(pos.z)) return false;
            }

            // Rotation (4 × f32 LE, x y z w) — conditional on bit 0x02
            if ((changedMask & ChangedRotation) != 0)
            {
                if (ROTATION_SIZE > payload.Length - off) return false;
                rot.x = ReadF32LE(payload, off);     off += 4;
                rot.y = ReadF32LE(payload, off);     off += 4;
                rot.z = ReadF32LE(payload, off);     off += 4;
                rot.w = ReadF32LE(payload, off);     off += 4;
                if (!IsFinite(rot.x) || !IsFinite(rot.y) || !IsFinite(rot.z) || !IsFinite(rot.w)) return false;

                // Quaternions applied to transform.rotation or fed into physics
                // MUST be unit-length; a non-unit quaternion silently skews
                // interpolation, breaks Quaternion.Slerp, and — in extreme
                // cases — destabilises PhysX.  We reject clearly malformed
                // (|q|² outside a generous ±0.1 band) to catch corruption /
                // protocol bugs, then renormalise to erase benign FP drift so
                // downstream math (Slerp, inverse, multiplication) stays sane.
                float magSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (magSq < 0.9f || magSq > 1.1f) return false;
                // magSq ∈ [0.9, 1.1] here — guaranteed > 0, so sqrt and the
                // reciprocal are safe without an extra zero-guard.
                float invMag = 1f / (float)System.Math.Sqrt(magSq);
                rot.x *= invMag;
                rot.y *= invMag;
                rot.z *= invMag;
                rot.w *= invMag;
            }

            // Scale (3 × f32 LE) — conditional on bit 0x04
            if ((changedMask & ChangedScale) != 0)
            {
                if (SCALE_SIZE > payload.Length - off) return false;
                scale.x = ReadF32LE(payload, off);   off += 4;
                scale.y = ReadF32LE(payload, off);   off += 4;
                scale.z = ReadF32LE(payload, off);   off += 4;
                if (!IsFinite(scale.x) || !IsFinite(scale.y) || !IsFinite(scale.z)) return false;
            }

            // InputTick (u32 LE) — conditional on bit 0x08 (SDKS-01).  The
            // server appends this LAST, after every transform field, so the
            // offsets above are byte-identical to a record without the tick.
            // Tick 0 is a legitimate value; presence is carried by the bit, not
            // a sentinel, so HasConfirmedInputTick mirrors the mask exactly.
            uint confirmedInputTick   = 0u;
            bool hasConfirmedInputTick = false;
            if ((changedMask & ChangedInputTick) != 0)
            {
                if (INPUT_TICK_SIZE > payload.Length - off) return false;
                confirmedInputTick   = ReadU32LE(payload, off); off += 4;
                hasConfirmedInputTick = true;
            }

            state = new TransformState
            {
                Position              = pos,
                Rotation              = rot,
                Scale                 = scale,
                ConfirmedInputTick    = confirmedInputTick,
                HasConfirmedInputTick = hasConfirmedInputTick,
            };
            offset = off;
            return true;
        }

        /// <summary>
        /// Try to parse a client-to-server quantized transform-update payload.
        /// Used by tests and by gateway-side decoders that mirror this SDK's
        /// dispatch logic; the SDK itself only emits this format when
        /// <c>NetworkSettings.quantizeTransforms</c> is true.
        /// </summary>
        public static bool TryParseQuantizedUpdate(
            byte[] payload,
            out ulong objectId,
            out TransformState state)
        {
            objectId = 0;
            state    = default;

            if (payload == null || payload.Length != QUANTIZED_UPDATE_SIZE) return false;
            if ((payload[0] & FLAG_QUANTIZED) == 0) return false;

            objectId = ReadU64LE(payload, 1);

            float px = TransformQuantization.ReadHalf(payload, 9);
            float py = TransformQuantization.ReadHalf(payload, 11);
            float pz = TransformQuantization.ReadHalf(payload, 13);

            Quaternion rot = TransformQuantization.ReadSmallestThree(payload, 15);

            float sx = TransformQuantization.ReadHalf(payload, 19);
            float sy = TransformQuantization.ReadHalf(payload, 21);
            float sz = TransformQuantization.ReadHalf(payload, 23);

            // ReadHalf already maps malformed inputs to 0, so the only way a
            // non-finite value could land here is an in-band runtime bug;
            // a final guard makes the parser total over its declared domain.
            if (!IsFinite(px) || !IsFinite(py) || !IsFinite(pz)) return false;
            if (!IsFinite(sx) || !IsFinite(sy) || !IsFinite(sz)) return false;

            state = new TransformState
            {
                Position = new Vector3(px, py, pz),
                Rotation = rot,
                Scale    = new Vector3(sx, sy, sz),
            };
            return true;
        }

        // ── Private helpers ────────────────────────────────────────────────────

        // IsFinite returns true when v is neither NaN nor ±Infinity.
        // Malformed server packets can encode IEEE 754 bit patterns that
        // BitConverter.Int32BitsToSingle decodes as NaN or Inf; applying
        // such values directly to a Unity transform causes undefined physics
        // behaviour and can crash the engine.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

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

        // ReadU32LE reads four consecutive bytes as a little-endian u32.
        // Used for the SDKS-01 InputTick trailing field.
        private static uint ReadU32LE(byte[] buf, int off)
            =>  (uint)buf[off + 0]
             | ((uint)buf[off + 1] <<  8)
             | ((uint)buf[off + 2] << 16)
             | ((uint)buf[off + 3] << 24);

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
