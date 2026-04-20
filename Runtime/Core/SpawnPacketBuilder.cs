// RTMPE SDK — Runtime/Core/SpawnPacketBuilder.cs
//
// Builds payload bytes for Spawn/Despawn request packets.
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte standard header + spawn payload).
//
// Wire formats (all little-endian):
//
// ── SpawnRequest (client → server, type 0x30) ──────────────────────────────
//   [prefab_id   : 4 LE u32]
//   [object_id   : 8 LE u64]  — client-generated from GenerateObjectId()
//   [owner_len   : 2 LE u16]
//   [owner       : N UTF-8]   — room player UUID
//   [pos_x       : 4 LE f32]
//   [pos_y       : 4 LE f32]
//   [pos_z       : 4 LE f32]
//   [rot_x       : 4 LE f32]
//   [rot_y       : 4 LE f32]
//   [rot_z       : 4 LE f32]
//   [rot_w       : 4 LE f32]
//
// Total: 14 + owner_len + 28 bytes.
//
// ── DespawnRequest (client → server, type 0x31) ────────────────────────────
//   [object_id   : 8 LE u64]
//
// Total: 8 bytes.

using System;
using System.Text;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Builds payload byte arrays for Spawn/Despawn protocol packets.
    /// All methods are static and produce a fresh byte[] on each call.
    /// </summary>
    public static class SpawnPacketBuilder
    {
        /// <summary>
        /// Build the payload for a Spawn request (PacketType.Spawn = 0x30).
        /// </summary>
        /// <param name="prefabId">Registered prefab identifier.</param>
        /// <param name="objectId">Client-generated unique object ID.</param>
        /// <param name="ownerPlayerId">Room player UUID of the owner.</param>
        /// <param name="position">World-space spawn position.</param>
        /// <param name="rotation">World-space spawn rotation.</param>
        /// <returns>Spawn payload ready for <c>PacketBuilder.Build()</c>.</returns>
        public static byte[] BuildSpawnRequest(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation)
        {
            byte[] ownerBytes = string.IsNullOrEmpty(ownerPlayerId)
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(ownerPlayerId);

            if (ownerBytes.Length > 256)
                throw new ArgumentException(
                    "ownerPlayerId UTF-8 encoding exceeds 256 bytes.",
                    nameof(ownerPlayerId));

            // 4 + 8 + 2 + N + 7*4 = 42 + N
            int size = 4 + 8 + 2 + ownerBytes.Length + 28;
            var buf = new byte[size];
            int o = 0;

            WriteU32LE(buf, ref o, prefabId);
            WriteU64LE(buf, ref o, objectId);
            WriteU16LE(buf, ref o, (ushort)ownerBytes.Length);
            if (ownerBytes.Length > 0)
            {
                Buffer.BlockCopy(ownerBytes, 0, buf, o, ownerBytes.Length);
                o += ownerBytes.Length;
            }
            WriteF32LE(buf, ref o, position.x);
            WriteF32LE(buf, ref o, position.y);
            WriteF32LE(buf, ref o, position.z);
            WriteF32LE(buf, ref o, rotation.x);
            WriteF32LE(buf, ref o, rotation.y);
            WriteF32LE(buf, ref o, rotation.z);
            WriteF32LE(buf, ref o, rotation.w);

            return buf;
        }

        /// <summary>
        /// Build the payload for a Despawn request (PacketType.Despawn = 0x31).
        /// </summary>
        /// <param name="objectId">The network object ID to despawn.</param>
        /// <returns>Despawn payload ready for <c>PacketBuilder.Build()</c>.</returns>
        public static byte[] BuildDespawnRequest(ulong objectId)
        {
            var buf = new byte[8];
            int o = 0;
            WriteU64LE(buf, ref o, objectId);
            return buf;
        }

        // ── LE writers ─────────────────────────────────────────────────────────

        private static void WriteU16LE(byte[] buf, ref int offset, ushort value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
        }

        private static void WriteU32LE(byte[] buf, ref int offset, uint value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
            buf[offset++] = (byte)(value >> 16);
            buf[offset++] = (byte)(value >> 24);
        }

        private static void WriteU64LE(byte[] buf, ref int offset, ulong value)
        {
            buf[offset++] = (byte)(value);
            buf[offset++] = (byte)(value >> 8);
            buf[offset++] = (byte)(value >> 16);
            buf[offset++] = (byte)(value >> 24);
            buf[offset++] = (byte)(value >> 32);
            buf[offset++] = (byte)(value >> 40);
            buf[offset++] = (byte)(value >> 48);
            buf[offset++] = (byte)(value >> 56);
        }

        private static void WriteF32LE(byte[] buf, ref int offset, float value)
        {
            // Use SingleToInt32Bits + explicit byte extraction for endian-safe LE encoding.
            // BitConverter.GetBytes(float) is platform-endian and would produce wrong byte
            // order on big-endian platforms. This matches TransformPacketBuilder.WriteF32LE.
            int bits = BitConverter.SingleToInt32Bits(value);
            buf[offset++] = (byte) bits;
            buf[offset++] = (byte)(bits >>  8);
            buf[offset++] = (byte)(bits >> 16);
            buf[offset++] = (byte)(bits >> 24);
        }
    }
}
