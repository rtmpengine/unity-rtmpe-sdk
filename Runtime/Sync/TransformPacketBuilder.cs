// RTMPE SDK — Runtime/Sync/TransformPacketBuilder.cs
//
// Builds the binary payload for a client-to-server transform update packet.
//
// Wire format (48 bytes, all fields little-endian):
//   [0..7]   object_id : u64  — Unity NetworkObject.NetworkObjectId
//   [8..11]  pos_x     : f32  — world-space position X
//   [12..15] pos_y     : f32  — world-space position Y
//   [16..19] pos_z     : f32  — world-space position Z
//   [20..23] rot_x     : f32  — quaternion X  (world-space rotation)
//   [24..27] rot_y     : f32  — quaternion Y
//   [28..31] rot_z     : f32  — quaternion Z
//   [32..35] rot_w     : f32  — quaternion W
//   [36..39] scale_x   : f32  — local-space scale X
//   [40..43] scale_y   : f32  — local-space scale Y
//   [44..47] scale_z   : f32  — local-space scale Z
//
// The payload is wrapped in a 13-byte RTMPE header (PacketType.Data, 0x10)
// by the caller via NetworkManager.SendData().
//
// The layout matches the Go server's ObjectState struct field order so that
// future server-side deserialisers can read the raw bytes directly.
//
//   Go reference: modules/synchronization/domain/entities/object_state.go
//     type ObjectState struct {
//         ObjectID uint64
//         Position Vec3         // float32 × 3
//         Rotation Quaternion   // float32 × 4 (X Y Z W)
//         Scale    Vec3         // float32 × 3
//     }
//
// Security note: no AEAD here. The surrounding gateway pipeline applies
// ChaCha20-Poly1305 encryption before the packet leaves the device.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Builds binary payloads for client-to-server transform update packets.
    /// All methods are static and allocation-minimal (one <c>new byte[48]</c> per call).
    /// </summary>
    public static class TransformPacketBuilder
    {
        // ── Layout constants ──────────────────────────────────────────────────

        /// <summary>
        /// Size in bytes of a transform update payload.
        /// ObjectID(8) + Position(12) + Rotation(16) + Scale(12) = 48 bytes.
        /// </summary>
        public const int PAYLOAD_SIZE = 48;

        /// <summary>Byte offset of <c>object_id</c> within the payload.</summary>
        internal const int OFFSET_OBJECT_ID = 0;

        /// <summary>Byte offset of <c>pos_x</c> within the payload.</summary>
        internal const int OFFSET_POSITION = 8;

        /// <summary>Byte offset of <c>rot_x</c> within the payload.</summary>
        internal const int OFFSET_ROTATION = 20;

        /// <summary>Byte offset of <c>scale_x</c> within the payload.</summary>
        internal const int OFFSET_SCALE = 36;

        // ── Factory method ────────────────────────────────────────────────────

        /// <summary>
        /// Build a 48-byte binary payload encoding <paramref name="objectId"/>
        /// and <paramref name="state"/> in the RTMPE transform update wire format.
        /// </summary>
        /// <param name="objectId">
        /// Unity <c>NetworkObject.NetworkObjectId</c> — identifies the object
        /// being updated on the server.
        /// </param>
        /// <param name="state">
        /// The current transform snapshot to encode.
        /// </param>
        /// <returns>
        /// A newly allocated 48-byte array.  Pass to
        /// <c>NetworkManager.Instance.SendData()</c> to transmit.
        /// </returns>
        public static byte[] BuildUpdatePayload(ulong objectId, TransformState state)
        {
            var payload = new byte[PAYLOAD_SIZE];

            // ── ObjectID (u64 LE) ─────────────────────────────────────────────
            WriteU64LE(payload, OFFSET_OBJECT_ID, objectId);

            // ── Position (3 × f32 LE) ─────────────────────────────────────────
            WriteF32LE(payload, OFFSET_POSITION + 0,  state.Position.x);
            WriteF32LE(payload, OFFSET_POSITION + 4,  state.Position.y);
            WriteF32LE(payload, OFFSET_POSITION + 8,  state.Position.z);

            // ── Rotation (4 × f32 LE, x y z w) ──────────────────────────────
            WriteF32LE(payload, OFFSET_ROTATION + 0,  state.Rotation.x);
            WriteF32LE(payload, OFFSET_ROTATION + 4,  state.Rotation.y);
            WriteF32LE(payload, OFFSET_ROTATION + 8,  state.Rotation.z);
            WriteF32LE(payload, OFFSET_ROTATION + 12, state.Rotation.w);

            // ── Scale (3 × f32 LE) ────────────────────────────────────────────
            WriteF32LE(payload, OFFSET_SCALE + 0, state.Scale.x);
            WriteF32LE(payload, OFFSET_SCALE + 4, state.Scale.y);
            WriteF32LE(payload, OFFSET_SCALE + 8, state.Scale.z);

            return payload;
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
        // bit reinterpretation.
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
