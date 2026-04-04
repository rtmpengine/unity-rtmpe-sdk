// RTMPE SDK — Tests/Runtime/PacketBuilderTests.cs
//
// NUnit tests for PacketBuilder.
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Linq;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Protocol;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Protocol")]
    public class PacketBuilderTests
    {
        private PacketBuilder _builder;

        [SetUp]
        public void SetUp() => _builder = new PacketBuilder();

        // ── Header: magic ──────────────────────────────────────────────────────

        [Test]
        public void BuildHeartbeat_HeaderMagic_Is0x5254_LittleEndian()
        {
            var pkt = _builder.BuildHeartbeat();
            Assert.AreEqual(0x54, pkt[0], "magic[0] should be 0x54 ('R' low byte)");
            Assert.AreEqual(0x52, pkt[1], "magic[1] should be 0x52 ('T' high byte)");
        }

        // ── Header: version ────────────────────────────────────────────────────

        [Test]
        public void Build_HeaderVersion_AlwaysThree()
        {
            Assert.AreEqual(3, _builder.BuildHeartbeat()[2]);
            Assert.AreEqual(3, _builder.BuildDisconnect()[2]);
            Assert.AreEqual(3, _builder.BuildHandshakeInit(new byte[1])[2]);
        }

        // ── Header: packet type ────────────────────────────────────────────────

        [Test]
        public void BuildHandshakeInit_TypeByte_Is0x05()
        {
            var pkt = _builder.BuildHandshakeInit(new byte[8]);
            Assert.AreEqual(0x05, pkt[3]);
        }

        [Test]
        public void BuildHandshakeResponse_TypeByte_Is0x07()
        {
            var pkt = _builder.BuildHandshakeResponse(new byte[32]);
            Assert.AreEqual(0x07, pkt[3]);
        }

        [Test]
        public void BuildHeartbeat_TypeByte_Is0x03()
        {
            var pkt = _builder.BuildHeartbeat();
            Assert.AreEqual(0x03, pkt[3]);
        }

        [Test]
        public void BuildDisconnect_TypeByte_Is0xFF()
        {
            var pkt = _builder.BuildDisconnect();
            Assert.AreEqual(0xFF, pkt[3]);
        }

        // ── Header: total length ───────────────────────────────────────────────

        [Test]
        public void BuildHeartbeat_Length_Is13Bytes_NoPayload()
        {
            Assert.AreEqual(PacketProtocol.HEADER_SIZE, _builder.BuildHeartbeat().Length);
        }

        [Test]
        public void BuildDisconnect_Length_Is13Bytes_NoPayload()
        {
            Assert.AreEqual(PacketProtocol.HEADER_SIZE, _builder.BuildDisconnect().Length);
        }

        [Test]
        public void BuildHandshakeResponse_Length_Is13Plus32()
        {
            Assert.AreEqual(PacketProtocol.HEADER_SIZE + 32,
                _builder.BuildHandshakeResponse(new byte[32]).Length);
        }

        [Test]
        public void BuildHandshakeInit_Length_Is13PlusPayload()
        {
            const int payloadSize = 55;
            var pkt = _builder.BuildHandshakeInit(new byte[payloadSize]);
            Assert.AreEqual(PacketProtocol.HEADER_SIZE + payloadSize, pkt.Length);
        }

        // ── Header: payload_len field ──────────────────────────────────────────

        [Test]
        public void Build_PayloadLenField_MatchesActualPayload()
        {
            const int payloadSize = 42;
            var pkt = _builder.BuildHandshakeInit(new byte[payloadSize]);

            // Bytes 9..12 are payload_len (LE u32)
            uint fieldLen = (uint)(pkt[9] | (pkt[10] << 8) | (pkt[11] << 16) | (pkt[12] << 24));
            Assert.AreEqual((uint)payloadSize, fieldLen);
        }

        [Test]
        public void Build_PayloadLenField_ZeroForEmptyPayload()
        {
            var pkt = _builder.BuildHeartbeat();
            uint fieldLen = (uint)(pkt[9] | (pkt[10] << 8) | (pkt[11] << 16) | (pkt[12] << 24));
            Assert.AreEqual(0u, fieldLen);
        }

        // ── Sequence counter: monotonic ────────────────────────────────────────

        [Test]
        public void SequenceCounter_IsMonoticallyIncreasing_AcrossBuilds()
        {
            var seqs = new uint[10];
            for (int i = 0; i < 10; i++)
            {
                var pkt = _builder.BuildHeartbeat();
                seqs[i] = (uint)(pkt[5] | (pkt[6] << 8) | (pkt[7] << 16) | (pkt[8] << 24));
            }

            for (int i = 1; i < seqs.Length; i++)
                Assert.Greater(seqs[i], seqs[i - 1],
                    $"seq[{i}]={seqs[i]} should be > seq[{i - 1}]={seqs[i - 1]}");
        }

        [Test]
        public void SequenceCounter_FirstPacket_Is1()
        {
            var pkt = new PacketBuilder().BuildHeartbeat(); // fresh builder
            uint seq = (uint)(pkt[5] | (pkt[6] << 8) | (pkt[7] << 16) | (pkt[8] << 24));
            Assert.AreEqual(1u, seq, "First packet from a fresh builder should have seq=1.");
        }

        [Test]
        public void SequenceCounters_ArePer_Instance()
        {
            var b1 = new PacketBuilder();
            var b2 = new PacketBuilder();

            var r1a = b1.BuildHeartbeat();
            var r2a = b2.BuildHeartbeat();
            var r1b = b1.BuildHeartbeat();

            uint s1a = (uint)(r1a[5] | (r1a[6] << 8) | (r1a[7] << 16) | (r1a[8] << 24));
            uint s2a = (uint)(r2a[5] | (r2a[6] << 8) | (r2a[7] << 16) | (r2a[8] << 24));
            uint s1b = (uint)(r1b[5] | (r1b[6] << 8) | (r1b[7] << 16) | (r1b[8] << 24));

            Assert.AreEqual(1u, s1a);
            Assert.AreEqual(1u, s2a, "Each builder instance starts at 1 independently.");
            Assert.AreEqual(2u, s1b);
        }

        // ── Payload bytes ──────────────────────────────────────────────────────

        [Test]
        public void Build_PayloadBytes_AreCopiedCorrectly()
        {
            var payload = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD };
            var pkt = _builder.BuildHandshakeInit(payload);

            for (int i = 0; i < payload.Length; i++)
                Assert.AreEqual(payload[i], pkt[PacketProtocol.HEADER_SIZE + i],
                    $"Payload byte [{i}] mismatch.");
        }

        [Test]
        public void BuildHandshakeResponse_Payload_MatchesPublicKey()
        {
            var pubKey = new byte[32];
            for (int i = 0; i < 32; i++) pubKey[i] = (byte)i;

            var pkt = _builder.BuildHandshakeResponse(pubKey);

            for (int i = 0; i < 32; i++)
                Assert.AreEqual(pubKey[i], pkt[PacketProtocol.HEADER_SIZE + i]);
        }

        // ── Validation ──────────────────────────────────────────────────────────

        [Test]
        public void BuildHandshakeResponse_ThrowsOn_NonExact32ByteKey()
        {
            Assert.Throws<ArgumentException>(() => _builder.BuildHandshakeResponse(new byte[16]));
            Assert.Throws<ArgumentException>(() => _builder.BuildHandshakeResponse(new byte[64]));
            Assert.Throws<ArgumentException>(() => _builder.BuildHandshakeResponse(Array.Empty<byte>()));
        }

        [Test]
        public void BuildHandshakeResponse_ThrowsOn_NullKey()
        {
            Assert.Throws<ArgumentException>(() => _builder.BuildHandshakeResponse(null));
        }

        // ── Flags field ────────────────────────────────────────────────────────

        [Test]
        public void Build_FlagsField_IsZero_ForHeartbeat()
        {
            var pkt = _builder.BuildHeartbeat();
            Assert.AreEqual(0x00, pkt[4], "flags byte should be 0x00 for standard heartbeat.");
        }
    }
}
