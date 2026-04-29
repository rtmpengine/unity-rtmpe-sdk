// RTMPE SDK — Runtime/Sync/PhysicsPacketBuilder.cs
//
// Builds binary payloads for physics-state packets sent by NetworkRigidbody
// and NetworkRigidbody2D.  Payloads are transmitted as PacketType.StateSync
// (0x40) so they flow through the existing Sync Engine broadcast path without
// requiring new gateway packet types.
//
// ── Disambiguation from TransformPacketParser ─────────────────────────────────
//
// TransformPacketParser rejects any changed_mask byte with bits outside 0x07
// set (KnownMask = 0x07).  Physics payloads always have bit 0x40 (3-D) or
// 0x80 (2-D) set as a type marker, guaranteeing the transform parser returns
// false and falls through to PhysicsPacketParser.
//
// ── 3-D Physics wire format ───────────────────────────────────────────────────
//
//  [0..7]  object_id    : u64 LE
//  [8]     changed_mask : u8  (TypeMarker3D = 0x40 always set)
//            bit 0x01 = position        (3 × f32 LE = 12 bytes)
//            bit 0x02 = rotation        (4 × f32 LE = 16 bytes, x y z w)
//            bit 0x04 = velocity        (3 × f32 LE = 12 bytes)
//            bit 0x08 = angular_velocity(3 × f32 LE = 12 bytes)
//            bit 0x10 = is_sleeping     (u8: 0x00 = awake, 0x01 = sleeping)
//            bit 0x20 = constraint_mask (u8: bitmask of UnityEngine.RigidbodyConstraints)
//            bit 0x40 = TYPE MARKER     (always set; causes transform parser reject)
//  [9+]    conditional fields in the bit-order listed above
//  Min: 9 bytes (header only).  Max: 9 + 12 + 16 + 12 + 12 + 1 + 1 = 63 bytes.
//
// ── 2-D Physics wire format ───────────────────────────────────────────────────
//
//  [0..7]  object_id    : u64 LE
//  [8]     changed_mask : u8  (TypeMarker2D = 0x80 always set)
//            bit 0x01 = position        (2 × f32 LE = 8 bytes)
//            bit 0x02 = rotation        (1 × f32 LE = 4 bytes, degrees)
//            bit 0x04 = velocity        (2 × f32 LE = 8 bytes)
//            bit 0x08 = angular_velocity(1 × f32 LE = 4 bytes, deg/s)
//            bit 0x10 = is_sleeping     (u8: 0x00 = awake, 0x01 = sleeping)
//            bit 0x20 = constraint_mask (u8: bitmask of UnityEngine.RigidbodyConstraints2D)
//            bit 0x80 = TYPE MARKER     (always set; also discriminates from 3-D)
//  [9+]    conditional fields in the bit-order listed above
//  Min: 9 bytes.  Max: 9 + 8 + 4 + 8 + 4 + 1 + 1 = 35 bytes.
//
// ── Security ──────────────────────────────────────────────────────────────────
//
// No AEAD here.  The surrounding gateway pipeline applies ChaCha20-Poly1305
// encryption before any packet leaves the device.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Builds binary physics-state payloads for <see cref="NetworkRigidbody"/>
    /// and <see cref="NetworkRigidbody2D"/>.  All methods are static and produce
    /// exactly-sized byte arrays (no excess allocation).
    /// </summary>
    public static class PhysicsPacketBuilder
    {
        // ── Changed-field bit constants ────────────────────────────────────────
        //
       // Bits 0x01–0x10 are shared between 3-D and 2-D packets.
        // Bits 0x40 / 0x80 are exclusive type markers.

        /// <summary>
        /// Bit indicating Position is present.
        /// 3-D: 3 × f32 (12 bytes).  2-D: 2 × f32 (8 bytes).
        /// </summary>
        public const byte ChangedPosition = 0x01;

        /// <summary>
        /// Bit indicating Rotation is present.
        /// 3-D: 4 × f32 (x y z w, 16 bytes).  2-D: 1 × f32 (degrees, 4 bytes).
        /// </summary>
        public const byte ChangedRotation = 0x02;

        /// <summary>
        /// Bit indicating Velocity is present.
        /// 3-D: 3 × f32 (12 bytes).  2-D: 2 × f32 (8 bytes).
        /// </summary>
        public const byte ChangedVelocity = 0x04;

        /// <summary>
        /// Bit indicating AngularVelocity is present.
        /// 3-D: 3 × f32 rad/s (12 bytes).  2-D: 1 × f32 deg/s (4 bytes).
        /// </summary>
        public const byte ChangedAngularVelocity = 0x08;

        /// <summary>
        /// Bit indicating IsSleeping is present (u8: 0x00 = awake, 0x01 = sleeping).
        /// </summary>
        public const byte ChangedSleep = 0x10;

        /// <summary>
        /// Bit indicating ConstraintMask is present (u8: bitmask of
        /// <see cref="UnityEngine.RigidbodyConstraints"/> for 3-D, or
        /// <see cref="UnityEngine.RigidbodyConstraints2D"/> for 2-D).
        /// Sent only when the constraint set changes — owners with static
        /// constraint configurations pay zero per-tick bandwidth.
        /// </summary>
        public const byte ChangedConstraints = 0x20;

        /// <summary>
        /// Type-marker bit that is ALWAYS set in 3-D physics payloads.
        /// <para>
        /// Because <see cref="TransformPacketParser"/> rejects any changed_mask byte
        /// with bits outside its KnownMask (0x07) set, every 3-D physics payload is
        /// guaranteed to fall through to <see cref="PhysicsPacketParser"/> without
        /// ambiguity.
        /// </para>
        /// </summary>
        public const byte TypeMarker3D = 0x40;

        /// <summary>
        /// Type-marker bit that is ALWAYS set in 2-D physics payloads.
        /// Serves the same role as <see cref="TypeMarker3D"/> and additionally
        /// discriminates 2-D payloads from 3-D ones (bit 0x80 vs bit 0x40).
        /// </summary>
        public const byte TypeMarker2D = 0x80;

        /// <summary>
        /// All known data-field bits (shared by 3-D and 2-D parsers):
        /// 0x01 | 0x02 | 0x04 | 0x08 | 0x10 | 0x20 = 0x3F.
        /// Bits outside this range (excluding type markers) indicate unknown fields.
        /// </summary>
        public const byte DataFieldMask = 0x3F;

        /// <summary>
        /// Minimum valid payload size in bytes: object_id(8) + changed_mask(1).
        /// A payload with only the type-marker set (no data fields) is valid.
        /// </summary>
        public const int PayloadMinSize = 9;

        // ── 3-D builder ───────────────────────────────────────────────────────

        /// <summary>
        /// Build a physics-state payload for a 3-D <see cref="UnityEngine.Rigidbody"/>.
        /// </summary>
        /// <param name="objectId">
        /// The server-assigned <c>NetworkObjectId</c> of the sending object.
        /// </param>
        /// <param name="state">The current physics snapshot.</param>
        /// <param name="dataMask">
        /// Which data fields to include (bits 0x01–0x10 only).
        /// <see cref="TypeMarker3D"/> (0x40) is OR-ed in automatically.
        /// Bits outside <see cref="DataFieldMask"/> are silently cleared.
        /// </param>
        /// <returns>An exactly-sized byte array ready to pass to
        /// <c>NetworkManager.SendStateSync()</c>.</returns>
        public static byte[] BuildPayload(ulong objectId, PhysicsState state, byte dataMask)
        {
            // Clamp data bits to the known range and force the 3-D type marker.
            byte changedMask = (byte)((dataMask & DataFieldMask) | TypeMarker3D);

            // Calculate the exact payload size (no padding or trailing zeros).
            int size = PayloadMinSize;
            if ((changedMask & ChangedPosition)        != 0) size += 12;
            if ((changedMask & ChangedRotation)        != 0) size += 16;
            if ((changedMask & ChangedVelocity)        != 0) size += 12;
            if ((changedMask & ChangedAngularVelocity) != 0) size += 12;
            if ((changedMask & ChangedSleep)           != 0) size +=  1;
            if ((changedMask & ChangedConstraints)     != 0) size +=  1;

            var buf = new byte[size];
            int off = 0;

            // ── Header ────────────────────────────────────────────────────────
            WriteU64LE(buf, off, objectId);  off += 8;
            buf[off++] = changedMask;

            // ── Conditional data fields (in bit-order) ────────────────────────
            if ((changedMask & ChangedPosition) != 0)
            {
                WriteF32LE(buf, off, state.Position.x); off += 4;
                WriteF32LE(buf, off, state.Position.y); off += 4;
                WriteF32LE(buf, off, state.Position.z); off += 4;
            }
            if ((changedMask & ChangedRotation) != 0)
            {
                WriteF32LE(buf, off, state.Rotation.x); off += 4;
                WriteF32LE(buf, off, state.Rotation.y); off += 4;
                WriteF32LE(buf, off, state.Rotation.z); off += 4;
                WriteF32LE(buf, off, state.Rotation.w); off += 4;
            }
            if ((changedMask & ChangedVelocity) != 0)
            {
                WriteF32LE(buf, off, state.Velocity.x); off += 4;
                WriteF32LE(buf, off, state.Velocity.y); off += 4;
                WriteF32LE(buf, off, state.Velocity.z); off += 4;
            }
            if ((changedMask & ChangedAngularVelocity) != 0)
            {
                WriteF32LE(buf, off, state.AngularVelocity.x); off += 4;
                WriteF32LE(buf, off, state.AngularVelocity.y); off += 4;
                WriteF32LE(buf, off, state.AngularVelocity.z); off += 4;
            }
            if ((changedMask & ChangedSleep) != 0)
            {
                buf[off] = state.IsSleeping ? (byte)0x01 : (byte)0x00;
                off++;
            }
            if ((changedMask & ChangedConstraints) != 0)
            {
                buf[off++] = state.ConstraintMask;
            }

            // Defensive: assert the offset matches the computed size.
            // In a correct implementation this never fires; kept for diagnostics.
            UnityEngine.Debug.Assert(off == size,
                $"[RTMPE] PhysicsPacketBuilder.BuildPayload: offset {off} != size {size}");

            return buf;
        }

        // ── 2-D builder ───────────────────────────────────────────────────────

        /// <summary>
        /// Build a physics-state payload for a 2-D <see cref="UnityEngine.Rigidbody2D"/>.
        /// </summary>
        /// <param name="objectId">Server-assigned <c>NetworkObjectId</c>.</param>
        /// <param name="state">The current 2-D physics snapshot.</param>
        /// <param name="dataMask">
        /// Which data fields to include (bits 0x01–0x10 only).
        /// <see cref="TypeMarker2D"/> (0x80) is OR-ed in automatically.
        /// Bits outside <see cref="DataFieldMask"/> are silently cleared.
        /// </param>
        /// <returns>An exactly-sized byte array ready to pass to
        /// <c>NetworkManager.SendStateSync()</c>.</returns>
        public static byte[] Build2DPayload(ulong objectId, PhysicsState2D state, byte dataMask)
        {
            byte changedMask = (byte)((dataMask & DataFieldMask) | TypeMarker2D);

            int size = PayloadMinSize;
            if ((changedMask & ChangedPosition)        != 0) size +=  8;
            if ((changedMask & ChangedRotation)        != 0) size +=  4;
            if ((changedMask & ChangedVelocity)        != 0) size +=  8;
            if ((changedMask & ChangedAngularVelocity) != 0) size +=  4;
            if ((changedMask & ChangedSleep)           != 0) size +=  1;
            if ((changedMask & ChangedConstraints)     != 0) size +=  1;

            var buf = new byte[size];
            int off = 0;

            WriteU64LE(buf, off, objectId); off += 8;
            buf[off++] = changedMask;

            if ((changedMask & ChangedPosition) != 0)
            {
                WriteF32LE(buf, off, state.Position.x); off += 4;
                WriteF32LE(buf, off, state.Position.y); off += 4;
            }
            if ((changedMask & ChangedRotation) != 0)
            {
                WriteF32LE(buf, off, state.Rotation); off += 4;
            }
            if ((changedMask & ChangedVelocity) != 0)
            {
                WriteF32LE(buf, off, state.Velocity.x); off += 4;
                WriteF32LE(buf, off, state.Velocity.y); off += 4;
            }
            if ((changedMask & ChangedAngularVelocity) != 0)
            {
                WriteF32LE(buf, off, state.AngularVelocity); off += 4;
            }
            if ((changedMask & ChangedSleep) != 0)
            {
                buf[off] = state.IsSleeping ? (byte)0x01 : (byte)0x00;
                off++;
            }
            if ((changedMask & ChangedConstraints) != 0)
            {
                buf[off++] = state.ConstraintMask;
            }

            UnityEngine.Debug.Assert(off == size,
                $"[RTMPE] PhysicsPacketBuilder.Build2DPayload: offset {off} != size {size}");

            return buf;
        }

        // ── Private write helpers ─────────────────────────────────────────────

        // WriteU64LE writes an unsigned 64-bit integer in little-endian byte order.
        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            buf[off + 0] = (byte) v;
            buf[off + 1] = (byte)(v >>  8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }

        // WriteF32LE writes an IEEE 754 single-precision float in little-endian
        // byte order using BitConverter.SingleToInt32Bits for zero-allocation
        // bit reinterpretation (available in .NET Standard 2.1 / Unity 2019.3+).
        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            buf[off + 0] = (byte) bits;
            buf[off + 1] = (byte)(bits >>  8);
            buf[off + 2] = (byte)(bits >> 16);
            buf[off + 3] = (byte)(bits >> 24);
        }
    }
}
