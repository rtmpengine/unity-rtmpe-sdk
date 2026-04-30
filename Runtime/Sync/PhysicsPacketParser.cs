// RTMPE SDK — Runtime/Sync/PhysicsPacketParser.cs
//
// Parses physics-state payloads received as PacketType.StateSync (0x40) by
// NetworkManager.HandleStateSyncPacket after TransformPacketParser has already
// rejected the packet (because physics payloads always carry type-marker bits
// outside the transform parser's KnownMask of 0x07).
//
// ── Disambiguation logic ──────────────────────────────────────────────────────
//
//  IsPhysics3D(payload) → true iff bit 0x40 set AND bit 0x80 clear.
//  IsPhysics2D(payload) → true iff bit 0x80 set (3-D marker can coexist but
//                          bit 0x80 wins — caller checks 2-D first is optional
//                          since IsPhysics3D already requires bit 0x80 to be clear).
//
// ── Error handling ────────────────────────────────────────────────────────────
//
//  All TryParse methods follow the TryParse pattern: returns false on any
//  truncation, null input, wrong type marker, or unknown data bits.
//  Unknown bits (bits not in DataFieldMask plus the relevant type marker) are
//  rejected to prevent silent field misalignment on future protocol extensions.
//
// Thread safety: all methods are static; no shared state.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Parses physics-state payloads built by <see cref="PhysicsPacketBuilder"/>.
    /// </summary>
    public static class PhysicsPacketParser
    {
        // ── Type-detection helpers ────────────────────────────────────────────

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="payload"/> is a
        /// 3-D physics packet (bit 0x40 set, bit 0x80 clear) of valid minimum size.
        /// </summary>
        public static bool IsPhysics3D(byte[] payload)
        {
            if (payload == null || payload.Length < PhysicsPacketBuilder.PayloadMinSize)
                return false;
            byte mask = payload[8];
            return (mask & PhysicsPacketBuilder.TypeMarker3D) != 0
                && (mask & PhysicsPacketBuilder.TypeMarker2D) == 0;
        }

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="payload"/> is a
        /// 2-D physics packet (bit 0x80 set) of valid minimum size.
        /// </summary>
        public static bool IsPhysics2D(byte[] payload)
        {
            if (payload == null || payload.Length < PhysicsPacketBuilder.PayloadMinSize)
                return false;
            return (payload[8] & PhysicsPacketBuilder.TypeMarker2D) != 0;
        }

        // ── 3-D parser ────────────────────────────────────────────────────────

        /// <summary>
        /// Try to parse a 3-D physics-state payload built by
        /// <see cref="PhysicsPacketBuilder.BuildPayload"/>.
        /// </summary>
        /// <param name="payload">
        /// Raw payload bytes (the data AFTER the 13-byte RTMPE header).
        /// </param>
        /// <param name="objectId">
        /// On success: the server-assigned object ID the update targets.
        /// </param>
        /// <param name="changedMask">
        /// On success: the full changed_mask byte (includes the type-marker bit).
        /// Inspect with <see cref="PhysicsPacketBuilder"/> constants before reading
        /// the corresponding <paramref name="state"/> field.
        /// </param>
        /// <param name="state">
        /// On success: decoded physics state.
        /// <b>Only fields whose bit is set in <paramref name="changedMask"/>
        /// carry valid data.</b>  All other fields hold zero-initialised values.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the payload is well-formed.
        /// <see langword="false"/> on null, truncation, wrong type marker, or
        /// unknown data bits.
        /// </returns>
        public static bool TryParsePhysicsState(
            byte[]         payload,
            out ulong      objectId,
            out byte       changedMask,
            out PhysicsState state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            if (payload == null || payload.Length < PhysicsPacketBuilder.PayloadMinSize)
                return false;

            int off = 0;
            objectId    = ReadU64LE(payload, off); off += 8;
            changedMask = payload[off++];

            // Must have 3-D marker set and 2-D marker clear.
            if ((changedMask & PhysicsPacketBuilder.TypeMarker3D) == 0) return false;
            if ((changedMask & PhysicsPacketBuilder.TypeMarker2D) != 0) return false;

            // Reject unknown data bits (anything outside the data-field mask and the
            // 3-D type marker).  Unknown bits indicate a future protocol extension
            // the current parser cannot safely handle.
            byte dataOnly = (byte)(changedMask & ~PhysicsPacketBuilder.TypeMarker3D);
            if ((dataOnly & ~PhysicsPacketBuilder.DataFieldMask) != 0) return false;

            var  pos        = Vector3.zero;
            var  rot        = new Quaternion(0f, 0f, 0f, 0f); // raw zero — caller checks mask
            var  vel        = Vector3.zero;
            var  angVel     = Vector3.zero;
            bool sleep      = false;
            byte constraint = 0;

            // ── Position (3 × f32 LE) ─────────────────────────────────────────
            //
            // Bounds expressed in subtraction form (`size > available`) so
            // an `off` near int.MaxValue cannot wrap `off + N` to a negative
            // value that bypasses the check.  Same discipline as the
            // transform / spawn / RPC parsers.
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                if (12 > payload.Length - off) return false;
                pos.x = ReadF32LE(payload, off); off += 4;
                pos.y = ReadF32LE(payload, off); off += 4;
                pos.z = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(pos.x) || !IsFinite(pos.y) || !IsFinite(pos.z)) return false;
            }

            // ── Rotation (4 × f32 LE, x y z w) ──────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                if (16 > payload.Length - off) return false;
                rot.x = ReadF32LE(payload, off); off += 4;
                rot.y = ReadF32LE(payload, off); off += 4;
                rot.z = ReadF32LE(payload, off); off += 4;
                rot.w = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(rot.x) || !IsFinite(rot.y) || !IsFinite(rot.z) || !IsFinite(rot.w)) return false;

                // Per-component cap: unit quaternions have |component| ≤ 1.
                // Rejecting components outside [-1.1, 1.1] before squaring them
                // prevents crafted values whose individual squares sum to a
                // magnitude near 1.0 but whose raw floats are unexpectedly large,
                // which could cause numerical instability in PhysX integration.
                const float MaxComponent = 1.1f;
                if (rot.x < -MaxComponent || rot.x > MaxComponent ||
                    rot.y < -MaxComponent || rot.y > MaxComponent ||
                    rot.z < -MaxComponent || rot.z > MaxComponent ||
                    rot.w < -MaxComponent || rot.w > MaxComponent) return false;

                // Same magnitude / renormalisation discipline as
                // TransformPacketParser: a non-unit quaternion fed to
                // Rigidbody.MoveRotation destabilises PhysX integration.
                float magSq = rot.x * rot.x + rot.y * rot.y + rot.z * rot.z + rot.w * rot.w;
                if (magSq < 0.9f || magSq > 1.1f) return false;
                float invMag = 1f / (float)System.Math.Sqrt(magSq);
                rot.x *= invMag;
                rot.y *= invMag;
                rot.z *= invMag;
                rot.w *= invMag;
            }

            // ── Velocity (3 × f32 LE) ─────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
            {
                if (12 > payload.Length - off) return false;
                vel.x = ReadF32LE(payload, off); off += 4;
                vel.y = ReadF32LE(payload, off); off += 4;
                vel.z = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(vel.x) || !IsFinite(vel.y) || !IsFinite(vel.z)) return false;
            }

            // ── Angular velocity (3 × f32 LE) ─────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
            {
                if (12 > payload.Length - off) return false;
                angVel.x = ReadF32LE(payload, off); off += 4;
                angVel.y = ReadF32LE(payload, off); off += 4;
                angVel.z = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(angVel.x) || !IsFinite(angVel.y) || !IsFinite(angVel.z)) return false;
            }

            // ── Sleep flag (u8) ───────────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedSleep) != 0)
            {
                if (1 > payload.Length - off) return false;
                sleep = payload[off++] != 0x00;
            }

            // ── Constraint mask (u8) ──────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedConstraints) != 0)
            {
                if (1 > payload.Length - off) return false;
                constraint = payload[off++];
            }

            // Reject trailing residue.  A well-formed payload ends exactly
            // where the last selected field's bytes end; surplus bytes are a
            // protocol-drift / smuggling signal that would otherwise survive
            // through replay-window dedup keyed on the full payload.
            if (off != payload.Length) return false;

            state = new PhysicsState
            {
                Position        = pos,
                Rotation        = rot,
                Velocity        = vel,
                AngularVelocity = angVel,
                IsSleeping      = sleep,
                ConstraintMask  = constraint,
            };
            return true;
        }

        // ── 2-D parser ────────────────────────────────────────────────────────

        /// <summary>
        /// Try to parse a 2-D physics-state payload built by
        /// <see cref="PhysicsPacketBuilder.Build2DPayload"/>.
        /// </summary>
        /// <param name="payload">
        /// Raw payload bytes (after the 13-byte RTMPE header).
        /// </param>
        /// <param name="objectId">On success: target object ID.</param>
        /// <param name="changedMask">
        /// On success: full changed_mask byte (includes the 2-D type-marker bit).
        /// </param>
        /// <param name="state">
        /// On success: decoded 2-D physics state.
        /// <b>Only fields whose bit is set in <paramref name="changedMask"/> are valid.</b>
        /// </param>
        /// <returns>
        /// <see langword="true"/> when well-formed; <see langword="false"/> on any
        /// error (null, truncation, missing 2-D marker, unknown data bits).
        /// </returns>
        public static bool TryParsePhysicsState2D(
            byte[]           payload,
            out ulong        objectId,
            out byte         changedMask,
            out PhysicsState2D state)
        {
            objectId    = 0;
            changedMask = 0;
            state       = default;

            if (payload == null || payload.Length < PhysicsPacketBuilder.PayloadMinSize)
                return false;

            int off = 0;
            objectId    = ReadU64LE(payload, off); off += 8;
            changedMask = payload[off++];

            // Must have 2-D marker set.
            if ((changedMask & PhysicsPacketBuilder.TypeMarker2D) == 0) return false;

            // Reject unknown data bits (anything outside the data-field mask and the
            // 2-D type marker).
            byte dataOnly = (byte)(changedMask & ~PhysicsPacketBuilder.TypeMarker2D);
            if ((dataOnly & ~PhysicsPacketBuilder.DataFieldMask) != 0) return false;

            var  pos        = Vector2.zero;
            float rot       = 0f;
            var  vel        = Vector2.zero;
            float angVel    = 0f;
            bool sleep      = false;
            byte constraint = 0;

            // Bounds expressed in subtraction form to match the 3-D parser
            // and the SDK-wide integer-overflow discipline.

            // ── Position (2 × f32 LE) ─────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedPosition) != 0)
            {
                if (8 > payload.Length - off) return false;
                pos.x = ReadF32LE(payload, off); off += 4;
                pos.y = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(pos.x) || !IsFinite(pos.y)) return false;
            }

            // ── Rotation (1 × f32 LE, degrees) ───────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedRotation) != 0)
            {
                if (4 > payload.Length - off) return false;
                rot = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(rot)) return false;
            }

            // ── Velocity (2 × f32 LE) ─────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedVelocity) != 0)
            {
                if (8 > payload.Length - off) return false;
                vel.x = ReadF32LE(payload, off); off += 4;
                vel.y = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(vel.x) || !IsFinite(vel.y)) return false;
            }

            // ── Angular velocity (1 × f32 LE, deg/s) ─────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedAngularVelocity) != 0)
            {
                if (4 > payload.Length - off) return false;
                angVel = ReadF32LE(payload, off); off += 4;
                if (!IsFinite(angVel)) return false;
            }

            // ── Sleep flag (u8) ───────────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedSleep) != 0)
            {
                if (1 > payload.Length - off) return false;
                sleep = payload[off++] != 0x00;
            }

            // ── Constraint mask (u8) ──────────────────────────────────────────
            if ((changedMask & PhysicsPacketBuilder.ChangedConstraints) != 0)
            {
                if (1 > payload.Length - off) return false;
                constraint = payload[off++];
            }

            // Trailing-residue rejection — symmetric with the 3-D parser.
            if (off != payload.Length) return false;

            state = new PhysicsState2D
            {
                Position        = pos,
                Rotation        = rot,
                Velocity        = vel,
                AngularVelocity = angVel,
                IsSleeping      = sleep,
                ConstraintMask  = constraint,
            };
            return true;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        // IsFinite returns true when v is neither NaN nor ±Infinity.  Mirrors
        // the discipline in TransformPacketParser: a NaN/Inf written into a
        // Rigidbody silently destabilises PhysX and can crash the engine, so
        // any malformed packet is rejected at parse time.
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
