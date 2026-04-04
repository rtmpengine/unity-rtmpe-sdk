// RTMPE SDK — Tests/Runtime/PacketParserTests.cs
//
// NUnit tests for PacketParser.
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Text;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Protocol;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Protocol")]
    public class PacketParserTests
    {
        // ── Helper: build a minimal full packet ────────────────────────────────

        private static byte[] MakePacket(byte type, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            var pkt = new byte[PacketProtocol.HEADER_SIZE + payload.Length];

            // magic 0x5254 LE
            pkt[0] = 0x54;
            pkt[1] = 0x52;
            // version
            pkt[2] = PacketProtocol.VERSION;
            // type
            pkt[3] = type;
            // flags
            pkt[4] = 0;
            // seq = 1 LE
            pkt[5] = 1;
            // payload_len LE u32
            uint len = (uint)payload.Length;
            pkt[9]  = (byte)len;
            pkt[10] = (byte)(len >> 8);
            pkt[11] = (byte)(len >> 16);
            pkt[12] = (byte)(len >> 24);

            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, pkt, PacketProtocol.HEADER_SIZE, payload.Length);

            return pkt;
        }

        // ── ExtractPayload ─────────────────────────────────────────────────────

        [Test]
        public void ExtractPayload_ReturnsCorrectBytes()
        {
            var payload = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            var packet  = MakePacket(0x06, payload);
            var result  = PacketParser.ExtractPayload(packet);

            Assert.AreEqual(payload.Length, result.Length);
            for (int i = 0; i < payload.Length; i++)
                Assert.AreEqual(payload[i], result[i]);
        }

        [Test]
        public void ExtractPayload_EmptyPayload_ReturnsEmptyArray()
        {
            var packet = MakePacket(0x03, Array.Empty<byte>());
            var result = PacketParser.ExtractPayload(packet);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        [Test]
        public void ExtractPayload_NullPacket_ReturnsEmptyArray()
        {
            var result = PacketParser.ExtractPayload(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Length);
        }

        [Test]
        public void ExtractPayload_TooShortPacket_ReturnsEmptyArray()
        {
            // Less than 13 bytes — not even a valid header.
            var result = PacketParser.ExtractPayload(new byte[5]);
            Assert.AreEqual(0, result.Length);
        }

        [Test]
        public void ExtractPayload_LargePayload_IsCorrect()
        {
            var payload = new byte[512];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            var packet = MakePacket(0x10, payload);
            var result = PacketParser.ExtractPayload(packet);

            Assert.AreEqual(512, result.Length);
            for (int i = 0; i < 512; i++)
                Assert.AreEqual((byte)(i & 0xFF), result[i], $"byte[{i}] mismatch");
        }

        // ── ParseChallenge ─────────────────────────────────────────────────────

        [Test]
        public void ParseChallenge_Valid128BytePayload_ReturnsTrue()
        {
            var payload = new byte[128];
            // Fill with distinct, identifiable pattern
            for (int i = 0; i < 128; i++) payload[i] = (byte)(i + 1);

            bool ok = PacketParser.ParseChallenge(payload,
                out var ephPub, out var staticPub, out var sig);

            Assert.IsTrue(ok);
        }

        [Test]
        public void ParseChallenge_ExtractsCorrectFieldRanges()
        {
            var payload = new byte[128];
            for (int i = 0; i < 128; i++) payload[i] = (byte)i;

            PacketParser.ParseChallenge(payload, out var eph, out var sta, out var sig);

            // First 32 bytes → ephemeral public key
            for (int i = 0; i < 32; i++)
                Assert.AreEqual((byte)i, eph[i], $"eph[{i}]");

            // Bytes 32..63 → static public key
            for (int i = 0; i < 32; i++)
                Assert.AreEqual((byte)(i + 32), sta[i], $"sta[{i}]");

            // Bytes 64..127 → Ed25519 signature
            for (int i = 0; i < 64; i++)
                Assert.AreEqual((byte)(i + 64), sig[i], $"sig[{i}]");
        }

        [Test]
        public void ParseChallenge_WrongLength_ReturnsFalse()
        {
            Assert.IsFalse(PacketParser.ParseChallenge(new byte[127], out _, out _, out _),
                "127 bytes should fail");
            Assert.IsFalse(PacketParser.ParseChallenge(new byte[129], out _, out _, out _),
                "129 bytes should fail");
            Assert.IsFalse(PacketParser.ParseChallenge(Array.Empty<byte>(), out _, out _, out _),
                "empty should fail");
            Assert.IsFalse(PacketParser.ParseChallenge(null, out _, out _, out _),
                "null should fail");
        }

        [Test]
        public void ParseChallenge_OutputFieldSizes_AreExact()
        {
            var payload = new byte[128];
            PacketParser.ParseChallenge(payload, out var eph, out var sta, out var sig);

            Assert.AreEqual(32, eph.Length,  "ephemeral pub must be 32 bytes");
            Assert.AreEqual(32, sta.Length,  "static pub must be 32 bytes");
            Assert.AreEqual(64, sig.Length,  "Ed25519 sig must be 64 bytes");
        }

        // ── ParseSessionAck ────────────────────────────────────────────────────

        private static byte[] BuildSessionAckPayload(
            uint   cryptoId,
            string jwt,
            string reconnect = "")
        {
            var jwtBytes = jwt      != null ? Encoding.UTF8.GetBytes(jwt)       : Array.Empty<byte>();
            var rcBytes  = reconnect != null ? Encoding.UTF8.GetBytes(reconnect) : Array.Empty<byte>();

            // [crypto_id:4][jwt_len:2][jwt:N][rc_len:2][rc:R]
            int total = 4 + 2 + jwtBytes.Length + 2 + rcBytes.Length;
            var buf = new byte[total];
            int off = 0;

            buf[off++] = (byte)(cryptoId);
            buf[off++] = (byte)(cryptoId >> 8);
            buf[off++] = (byte)(cryptoId >> 16);
            buf[off++] = (byte)(cryptoId >> 24);

            buf[off++] = (byte)(jwtBytes.Length);
            buf[off++] = (byte)(jwtBytes.Length >> 8);
            Buffer.BlockCopy(jwtBytes, 0, buf, off, jwtBytes.Length);
            off += jwtBytes.Length;

            buf[off++] = (byte)(rcBytes.Length);
            buf[off++] = (byte)(rcBytes.Length >> 8);
            Buffer.BlockCopy(rcBytes, 0, buf, off, rcBytes.Length);

            return buf;
        }

        [Test]
        public void ParseSessionAck_WellFormed_ReturnsTrue()
        {
            var payload = BuildSessionAckPayload(12345u, "eyJhbGciOiJIUzI1NiJ9.test", "reconnect-abc");
            bool ok = PacketParser.ParseSessionAck(payload, out _, out _, out _);
            Assert.IsTrue(ok);
        }

        [Test]
        public void ParseSessionAck_CryptoId_ParsedCorrectly()
        {
            const uint expected = 0xDEADBEEF;
            var payload = BuildSessionAckPayload(expected, "jwt", "rc");
            PacketParser.ParseSessionAck(payload, out uint cryptoId, out _, out _);
            Assert.AreEqual(expected, cryptoId);
        }

        [Test]
        public void ParseSessionAck_JwtToken_ParsedCorrectly()
        {
            const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjMifQ.sig";
            var payload = BuildSessionAckPayload(1u, jwt, "");
            PacketParser.ParseSessionAck(payload, out _, out string parsedJwt, out _);
            Assert.AreEqual(jwt, parsedJwt);
        }

        [Test]
        public void ParseSessionAck_ReconnectToken_ParsedCorrectly()
        {
            const string rc = "reconnect-token-xyz123";
            var payload = BuildSessionAckPayload(1u, "jwt", rc);
            PacketParser.ParseSessionAck(payload, out _, out _, out string parsedRc);
            Assert.AreEqual(rc, parsedRc);
        }

        [Test]
        public void ParseSessionAck_EmptyJwt_ReturnsEmptyString()
        {
            var payload = BuildSessionAckPayload(99u, "", "");
            bool ok = PacketParser.ParseSessionAck(payload, out _, out string jwt, out _);
            Assert.IsTrue(ok);
            Assert.AreEqual(string.Empty, jwt);
        }

        [Test]
        public void ParseSessionAck_TooShort_ReturnsFalse()
        {
            Assert.IsFalse(PacketParser.ParseSessionAck(new byte[7], out _, out _, out _));
            Assert.IsFalse(PacketParser.ParseSessionAck(Array.Empty<byte>(), out _, out _, out _));
            Assert.IsFalse(PacketParser.ParseSessionAck(null, out _, out _, out _));
        }

        [Test]
        public void ParseSessionAck_JwtLenExceedsPayload_ReturnsFalse()
        {
            // Craft a malformed payload where jwt_len > remaining bytes.
            var bad = new byte[] { 0x01, 0x00, 0x00, 0x00,  // crypto_id = 1
                                   0xFF, 0x7F,               // jwt_len = 32767 (huge)
                                   0x41, 0x42 };             // only 2 bytes of JWT
            Assert.IsFalse(PacketParser.ParseSessionAck(bad, out _, out _, out _));
        }
    }
}
