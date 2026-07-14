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
        /// Maximum accepted owner-id length, in UTF-8 bytes.  Generous for any
        /// UUID (max 36 chars) plus margin; a value above this is a protocol
        /// violation, not merely a large owner.  Enforced symmetrically on the
        /// build path (<see cref="SpawnPacketBuilder.BuildSpawnRequest"/>) so a
        /// locally-built Spawn always round-trips through this parser.
        /// </summary>
        public const int MaxOwnerIdBytes = 128;

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
            // Zero is never a valid network object ID.
            if (objectId == 0) return false;
            ushort ownerLen = ReadU16LE(payload, ref o);

            // Cap owner length before the bounds arithmetic so a ushort.MaxValue-
            // shaped attacker value cannot overflow `o + ownerLen + 28` into a
            // negative int that bypasses the additive-form check.  The ceiling
            // (MaxOwnerIdBytes) is shared with the build path so the two stay
            // in lockstep — a value above it is a protocol violation, not just
            // a large owner.
            if (ownerLen > MaxOwnerIdBytes)
                return false;

            // Subtraction-form bounds: the owner string plus the seven
            // trailing floats (28 B) must fit inside the remaining
            // payload.  Computed as `available - constant >= ownerLen`
            // so neither side of the comparison can wrap.
            if (ownerLen > payload.Length - o - 28)
                return false;

            // Strict UTF-8 — the default Encoding.UTF8 silently substitutes the
            // U+FFFD replacement character for any malformed byte sequence, which
            // turns an attacker-corruptible owner-id into a string that compares
            // unequal to a legitimate UUID but still satisfies non-empty checks.
            // DecodeStrictUtf8 throws on either malformed UTF-8 or an embedded
            // NUL; we treat that the same as any other malformed payload.
            string owner;
            if (ownerLen > 0)
            {
                try
                {
                    owner = DecodeStrictUtf8(payload, o, ownerLen);
                }
                catch (Exception)
                {
                    return false;
                }
            }
            else
            {
                owner = string.Empty;
            }
            o += ownerLen;

            float px = ReadF32LE(payload, ref o);
            float py = ReadF32LE(payload, ref o);
            float pz = ReadF32LE(payload, ref o);
            float rx = ReadF32LE(payload, ref o);
            float ry = ReadF32LE(payload, ref o);
            float rz = ReadF32LE(payload, ref o);
            float rw = ReadF32LE(payload, ref o);

            // Finiteness gate.  GameObject.Instantiate(...) with a NaN /
            // Inf transform corrupts PhysX state on the very first
            // FixedUpdate (the rigidbody is reported as missing from the
            // simulation; ragdoll children become detached).  Reject the
            // spawn payload outright rather than persisting the corruption.
            if (!IsFinite(px) || !IsFinite(py) || !IsFinite(pz)) return false;
            if (!IsFinite(rx) || !IsFinite(ry) || !IsFinite(rz) || !IsFinite(rw)) return false;

            // Quaternion magnitude gate.  The IsFinite check above admits
            // finite-but-degenerate rotations: a (0,0,0,0) zero-quaternion
            // assigned to transform.rotation produces per-frame "Look
            // rotation viewing vector is zero" warnings (each ~1 KB on the
            // managed heap) and silently substitutes identity, while
            // grossly-non-unit quaternions (e.g. 1e10 in each component)
            // produce NaN once squared during downstream parent-transform
            // multiplication.  The transform / physics parsers reject any
            // quaternion whose squared magnitude lies outside [0.9, 1.1]
            // (≈ unit norm with 5 % tolerance for FP rounding); mirror
            // the same band here so the spawn-time pose carries the
            // identical invariant as every other inbound rotation.
            float qMagSq = rx * rx + ry * ry + rz * rz + rw * rw;
            if (qMagSq < 0.9f || qMagSq > 1.1f) return false;

            // Reject trailing residue.  A well-formed spawn payload ends
            // exactly after the 7th float; surplus bytes are a protocol-
            // drift / smuggling signal.
            if (o != payload.Length) return false;

            data = new SpawnData(
                prefabId,
                objectId,
                owner,
                new Vector3(px, py, pz),
                new Quaternion(rx, ry, rz, rw));

            return true;
        }

        // .NET Standard 2.1 has float.IsFinite, but the SDK targets older
        // Unity runtimes where it is not always available.  Spelled out
        // explicitly so the code compiles unchanged.
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>
        /// Parse a Despawn payload (received from server).
        /// </summary>
        /// <param name="payload">The payload bytes (after the 13-byte header).</param>
        /// <param name="objectId">The object ID to despawn.</param>
        /// <returns>True if parsing succeeded.</returns>
        public static bool TryParseDespawn(byte[] payload, out ulong objectId)
        {
            objectId = 0;
            // Exactly 8 bytes: the u64 object ID and nothing else.
            // Trailing bytes are a protocol-drift / smuggling signal (same
            // principle as TryParseSpawn's trailing-residue check).
            if (payload == null || payload.Length != 8)
                return false;

            int o = 0;
            objectId = ReadU64LE(payload, ref o);
            // Zero is never a valid network object ID.
            return objectId != 0;
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

        // Strict UTF-8 codec — the default Encoding.UTF8 silently substitutes
        // U+FFFD for a malformed byte sequence, which would turn an
        // attacker-corruptible owner identifier into a string that compares
        // unequal to a legitimate UUID yet still passes a non-empty check.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Decode a slice of <paramref name="bytes"/> as UTF-8 with strict
        // validation.  Throws DecoderFallbackException for any malformed byte
        // sequence and InvalidOperationException for an embedded NUL — the
        // caller treats either as a malformed payload.
        private static string DecodeStrictUtf8(byte[] bytes, int offset, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || length < 0 || offset > bytes.Length - length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            for (int i = 0; i < length; i++)
            {
                if (bytes[offset + i] == 0)
                    throw new InvalidOperationException("string contains embedded NUL");
            }
            return StrictUtf8.GetString(bytes, offset, length);
        }
    }
}
