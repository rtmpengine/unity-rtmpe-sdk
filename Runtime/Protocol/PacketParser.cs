// RTMPE SDK — Runtime/Protocol/PacketParser.cs
//
// Parse inbound RTMPE packet payloads.
//
// This class only parses payloads that the CLIENT receives from the server:
//   - Challenge   (0x06): [ephemeral:32][static:32][sig:64] = 128 bytes
//   - SessionAck  (0x08): [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][rc:R]
//
// Header validation (magic, version) is done in NetworkManager.ProcessPacket.
// PacketParser only handles the *payload* (bytes after the 13-byte header).

using System;
using System.Text;
using RTMPE.Core;

namespace RTMPE.Protocol
{
    /// <summary>
    /// Parses inbound RTMPE packet payloads into typed structures.
    /// All methods are static and allocation-minimal.
    /// </summary>
    public static class PacketParser
    {
        // ── Header extraction ─────────────────────────────────────────────────

        /// <summary>
        /// Extract the payload bytes from a full wire packet (header + payload).
        /// Returns an empty array if the packet is too short.
        /// </summary>
        public static byte[] ExtractPayload(byte[] rawPacket)
        {
            if (rawPacket == null || rawPacket.Length < PacketProtocol.HEADER_SIZE)
                return Array.Empty<byte>();

            // Read payload_len from header bytes 9..12 (LE u32).
            uint payloadLen = (uint)(rawPacket[9]
                                   | (rawPacket[10] << 8)
                                   | (rawPacket[11] << 16)
                                   | (rawPacket[12] << 24));

            // Sanity cap: reject any payload claim larger than 1 MiB.
            // Without this guard, a crafted packet with payload_len ≥ 2^31 causes
            // the (int) cast below to go negative, bypasses the length check, and
            // then new byte[payloadLen] throws OverflowException.
            // Valid RTMPE datagrams are at most a few KiB; 1 MiB is a safe ceiling.
            const uint MaxPayload = 1 * 1024 * 1024;
            if (payloadLen > MaxPayload) return Array.Empty<byte>();

            int expectedTotal = PacketProtocol.HEADER_SIZE + (int)payloadLen;
            if (rawPacket.Length < expectedTotal) return Array.Empty<byte>();

            if (payloadLen == 0) return Array.Empty<byte>();

            var payload = new byte[payloadLen];
            Buffer.BlockCopy(rawPacket, PacketProtocol.HEADER_SIZE, payload, 0, (int)payloadLen);
            return payload;
        }

        // ── Challenge (0x06) ──────────────────────────────────────────────────

        /// <summary>
        /// Parse the 128-byte <c>Challenge</c> payload.
        ///
        /// Layout: [server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64]
        /// </summary>
        /// <param name="payload">The raw payload bytes (not the full packet).</param>
        /// <param name="serverEphemeralPub">32-byte X25519 public key of the server's ephemeral keypair.</param>
        /// <param name="serverStaticPub">32-byte Ed25519 static identity public key.</param>
        /// <param name="ed25519Sig">64-byte Ed25519 signature of <paramref name="serverEphemeralPub"/>.</param>
        /// <returns>
        /// <see langword="true"/> if the payload length is exactly 128 bytes.
        /// </returns>
        public static bool ParseChallenge(
            byte[] payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload == null || payload.Length != 128) return false;

            serverEphemeralPub = new byte[32];
            serverStaticPub    = new byte[32];
            ed25519Sig         = new byte[64];

            Buffer.BlockCopy(payload,  0, serverEphemeralPub, 0, 32);
            Buffer.BlockCopy(payload, 32, serverStaticPub,    0, 32);
            Buffer.BlockCopy(payload, 64, ed25519Sig,         0, 64);
            return true;
        }

        // ── SessionAck (0x08) ─────────────────────────────────────────────────

        /// <summary>
        /// Parse the <c>SessionAck</c> payload.
        ///
        /// Layout: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R]
        /// </summary>
        /// <param name="payload">The raw payload bytes.</param>
        /// <param name="cryptoId">4-byte LE crypto session ID.</param>
        /// <param name="jwtToken">JWT bearer token string.</param>
        /// <param name="reconnectToken">Reconnect token string (may be empty).</param>
        /// <returns>
        /// <see langword="true"/> if the payload is well-formed and all lengths are consistent.
        /// </returns>
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
        {
            cryptoId       = 0;
            jwtToken       = null;
            reconnectToken = null;

            if (payload == null || payload.Length < 8) return false; // 4 + 2 + 0 + 2 minimum

            int offset = 0;

            // [crypto_id: 4 LE]
            cryptoId = (uint)(payload[offset]
                            | (payload[offset + 1] << 8)
                            | (payload[offset + 2] << 16)
                            | (payload[offset + 3] << 24));
            offset += 4;

            // [jwt_len: 2 LE]
            int jwtLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            if (offset + jwtLen > payload.Length) return false;

            jwtToken = jwtLen > 0
                ? Encoding.UTF8.GetString(payload, offset, jwtLen)
                : string.Empty;
            offset += jwtLen;

            // [reconnect_len: 2 LE]
            if (offset + 2 > payload.Length) return false;
            int rcLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            if (offset + rcLen > payload.Length) return false;

            reconnectToken = rcLen > 0
                ? Encoding.UTF8.GetString(payload, offset, rcLen)
                : string.Empty;

            return true;
        }
    }
}
