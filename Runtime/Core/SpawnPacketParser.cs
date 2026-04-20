// RTMPE SDK — Runtime/Core/SpawnPacketParser.cs
//
// Parses incoming Spawn/Despawn packets from the server.
// The standard 13-byte packet header has already been stripped —
// this parser operates on the payload portion only.
//
// Wire formats match SpawnPacketBuilder.cs.

using System;
using System.Text;
using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Parsed spawn data from a server Spawn packet.
    /// </summary>
    public readonly struct SpawnData
    {
        public readonly uint PrefabId;
        public readonly ulong ObjectId;
        public readonly string OwnerPlayerId;
        public readonly Vector3 Position;
        public readonly Quaternion Rotation;

        public SpawnData(
            uint prefabId,
            ulong objectId,
            string ownerPlayerId,
            Vector3 position,
            Quaternion rotation)
        {
            PrefabId      = prefabId;
            ObjectId      = objectId;
            OwnerPlayerId = ownerPlayerId;
            Position      = position;
            Rotation      = rotation;
        }
    }

    /// <summary>
    /// Parses Spawn/Despawn payload bytes into structured data.
    /// All methods return false on malformed input (no exceptions).
    /// </summary>
    public static class SpawnPacketParser
    {
        /// <summary>
        /// Parse a Spawn payload (received from server).
        /// </summary>
        /// <param name="payload">The payload bytes (after the 13-byte header).</param>
        /// <param name="data">The parsed spawn data if successful.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseSpawn(byte[] payload, out SpawnData data)
        {
            data = default;

            // Minimum: 4 + 8 + 2 + 0 + 28 = 42 bytes (empty owner)
            if (payload == null || payload.Length < 42)
                return false;

            int o = 0;
            uint prefabId = ReadU32LE(payload, ref o);
            ulong objectId = ReadU64LE(payload, ref o);
            ushort ownerLen = ReadU16LE(payload, ref o);

            // Bounds check: owner + 7 floats (28 bytes) must fit
            if (o + ownerLen + 28 > payload.Length)
                return false;

            // Cap owner length (defense-in-depth)
            if (ownerLen > 256)
                return false;

            string owner = ownerLen > 0
                ? Encoding.UTF8.GetString(payload, o, ownerLen)
                : string.Empty;
            o += ownerLen;

            float px = ReadF32LE(payload, ref o);
            float py = ReadF32LE(payload, ref o);
            float pz = ReadF32LE(payload, ref o);
            float rx = ReadF32LE(payload, ref o);
            float ry = ReadF32LE(payload, ref o);
            float rz = ReadF32LE(payload, ref o);
            float rw = ReadF32LE(payload, ref o);

            data = new SpawnData(
                prefabId,
                objectId,
                owner,
                new Vector3(px, py, pz),
                new Quaternion(rx, ry, rz, rw));

            return true;
        }

        /// <summary>
        /// Parse a Despawn payload (received from server).
        /// </summary>
        /// <param name="payload">The payload bytes (after the 13-byte header).</param>
        /// <param name="objectId">The object ID to despawn.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseDespawn(byte[] payload, out ulong objectId)
        {
            objectId = 0;
            if (payload == null || payload.Length < 8)
                return false;

            int o = 0;
            objectId = ReadU64LE(payload, ref o);
            return true;
        }

        // ── LE readers ─────────────────────────────────────────────────────────

        private static ushort ReadU16LE(byte[] buf, ref int offset)
        {
            ushort v = (ushort)(buf[offset] | (buf[offset + 1] << 8));
            offset += 2;
            return v;
        }

        private static uint ReadU32LE(byte[] buf, ref int offset)
        {
            uint v = (uint)(
                buf[offset]
              | (buf[offset + 1] << 8)
              | (buf[offset + 2] << 16)
              | (buf[offset + 3] << 24));
            offset += 4;
            return v;
        }

        private static ulong ReadU64LE(byte[] buf, ref int offset)
        {
            ulong v =
                  (ulong)buf[offset]
                | ((ulong)buf[offset + 1] << 8)
                | ((ulong)buf[offset + 2] << 16)
                | ((ulong)buf[offset + 3] << 24)
                | ((ulong)buf[offset + 4] << 32)
                | ((ulong)buf[offset + 5] << 40)
                | ((ulong)buf[offset + 6] << 48)
                | ((ulong)buf[offset + 7] << 56);
            offset += 8;
            return v;
        }

        private static float ReadF32LE(byte[] buf, ref int offset)
        {
            // Explicit byte assembly + Int32BitsToSingle for endian-safe LE decoding.
            // BitConverter.ToSingle(buf, offset) is platform-endian and would misread
            // bytes on big-endian platforms. This matches TransformPacketParser.ReadF32LE.
            int bits = buf[offset]
                     | (buf[offset + 1] <<  8)
                     | (buf[offset + 2] << 16)
                     | (buf[offset + 3] << 24);
            offset += 4;
            return BitConverter.Int32BitsToSingle(bits);
        }
    }
}
