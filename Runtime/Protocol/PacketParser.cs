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
//
// ReadOnlySpan<byte> overloads exist alongside the byte[] overloads so callers
// holding a pool-rented buffer can parse without an intermediate ExtractPayload
// allocation.  The byte[] overloads delegate to the span overloads to keep the
// two paths bit-for-bit identical and trivially auditable.

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
            if (rawPacket == null) return Array.Empty<byte>();
            return ExtractPayloadCopy(new ReadOnlySpan<byte>(rawPacket));
        }

        /// <summary>
        /// Slice the payload bytes from a full wire packet without copying.
        /// Returns an empty span if the packet is malformed or too short.
        /// Prefer this overload when the caller is parsing a pool-rented buffer.
        /// </summary>
        public static ReadOnlySpan<byte> ExtractPayloadSpan(ReadOnlySpan<byte> rawPacket)
        {
            if (rawPacket.Length < PacketProtocol.HEADER_SIZE)
                return ReadOnlySpan<byte>.Empty;

            uint payloadLen = (uint)(rawPacket[9]
                                   | (rawPacket[10] << 8)
                                   | (rawPacket[11] << 16)
                                   | (rawPacket[12] << 24));

            // Sanity cap: reject any payload claim larger than 1 MiB.
            // Without this guard, a crafted packet with payload_len ≥ 2^31 causes
            // the (int) cast below to go negative, bypasses the length check, and
            // then a downstream allocation throws OverflowException.
            const uint MaxPayload = 1 * 1024 * 1024;
            if (payloadLen > MaxPayload) return ReadOnlySpan<byte>.Empty;

            int expectedTotal = PacketProtocol.HEADER_SIZE + (int)payloadLen;
            if (rawPacket.Length < expectedTotal) return ReadOnlySpan<byte>.Empty;
            if (payloadLen == 0) return ReadOnlySpan<byte>.Empty;

            return rawPacket.Slice(PacketProtocol.HEADER_SIZE, (int)payloadLen);
        }

        // Allocation-bearing convenience used by the byte[] overload.  The span
        // overload is preferred everywhere else.
        private static byte[] ExtractPayloadCopy(ReadOnlySpan<byte> rawPacket)
        {
            var slice = ExtractPayloadSpan(rawPacket);
            if (slice.IsEmpty) return Array.Empty<byte>();
            return slice.ToArray();
        }

        // ── Challenge (0x06) ──────────────────────────────────────────────────

        /// <summary>
        /// Parse the 128-byte <c>Challenge</c> payload.
        ///
        /// Layout: [server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64]
        /// </summary>
        public static bool ParseChallenge(
            byte[] payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload == null) return false;
            return ParseChallenge(new ReadOnlySpan<byte>(payload),
                                  out serverEphemeralPub,
                                  out serverStaticPub,
                                  out ed25519Sig);
        }

        /// <summary>
        /// Span-based parse of the 128-byte <c>Challenge</c> payload.
        /// The output arrays are freshly-allocated so the caller may keep them
        /// independently of the source buffer's lifetime.
        /// </summary>
        public static bool ParseChallenge(
            ReadOnlySpan<byte> payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload.Length != 128) return false;

            // The three sub-fields are returned as independent arrays because
            // they outlive the enclosing packet — one is fed to Ed25519Verify,
            // another is stored as the server's static identity for pinning.
            // Allocating once on a successful Challenge is unavoidable; the
            // span path simply ensures we don't allocate the redundant
            // intermediate `payload` byte[] that the legacy ExtractPayload did.
            serverEphemeralPub = payload.Slice(  0, 32).ToArray();
            serverStaticPub    = payload.Slice( 32, 32).ToArray();
            ed25519Sig         = payload.Slice( 64, 64).ToArray();
            return true;
        }

        // ── SessionAck (0x08) ─────────────────────────────────────────────────

        /// <summary>
        /// Parse the <c>SessionAck</c> payload.
        ///
        /// Layout: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R]
        /// </summary>
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
        {
            cryptoId       = 0;
            jwtToken       = null;
            reconnectToken = null;

            if (payload == null) return false;
            return ParseSessionAck(new ReadOnlySpan<byte>(payload),
                                   out cryptoId,
                                   out jwtToken,
                                   out reconnectToken);
        }

        /// <summary>
        /// Span-based parse of <c>SessionAck</c>.  String fields are decoded
        /// directly from the source span — no intermediate byte[] is allocated.
        /// </summary>
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
        {
            cryptoId       = 0;
            jwtToken       = null;
            reconnectToken = null;

            if (payload.Length < 8) return false; // 4 + 2 + 0 + 2 minimum

            int offset = 0;

            cryptoId = (uint)(payload[offset]
                            | (payload[offset + 1] << 8)
                            | (payload[offset + 2] << 16)
                            | (payload[offset + 3] << 24));
            offset += 4;

            int jwtLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            if (offset + jwtLen > payload.Length) return false;

            jwtToken = jwtLen > 0
                ? DecodeUtf8(payload.Slice(offset, jwtLen))
                : string.Empty;
            offset += jwtLen;

            if (offset + 2 > payload.Length) return false;
            int rcLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            if (offset + rcLen > payload.Length) return false;

            reconnectToken = rcLen > 0
                ? DecodeUtf8(payload.Slice(offset, rcLen))
                : string.Empty;

            return true;
        }

        // Encoding.UTF8.GetString accepts ReadOnlySpan<byte> on .NET Standard 2.1
        // (Unity 2021.2+) and .NET 5+.  Wrapped so call sites stay focused on
        // parsing logic; profile shows the span overload is genuinely
        // alloc-free for ASCII tokens (which JWT and reconnect tokens are).
        private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
            => Encoding.UTF8.GetString(bytes);
    }
}
