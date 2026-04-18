// RTMPE SDK — Tests/Runtime/ThreadingTests.cs
//
// NUnit tests for protocol constants and Day 1-2 package-structure validations.
// Run from the Unity Test Runner: Window > General > Test Runner > EditMode.
//
// TODO (Day 4-5): Add NetworkThread construction / teardown tests.
// TODO: Add KCP send/receive round-trip tests against a loopback server.

using System;
using NUnit.Framework;
using RTMPE.Core;

namespace RTMPE.Tests
{
    /// <summary>
    /// Validates that <see cref="PacketProtocol"/>, <see cref="PacketType"/>, and
    /// <see cref="PacketFlags"/> constants match the Rust gateway wire-protocol exactly.
    ///
    /// These tests act as a static contract guard: if any constant diverges from the
    /// values in <c>modules/gateway/src/packet/header.rs</c>, the build pipeline fails
    /// before a single packet is sent over the network.
    /// </summary>
    [TestFixture]
    [Category("Protocol")]
    public class ProtocolConstantsTests
    {
        // ── PacketProtocol ────────────────────────────────────────────────────

        [Test]
        [Description("Framing magic 0x5254 = ASCII 'RT' — must match Rust MAGIC constant")]
        public void Magic_Matches_GatewayConstant()
        {
            Assert.AreEqual(0x5254, PacketProtocol.MAGIC,
                "MAGIC must equal 0x5254 (Rust: pub const MAGIC: u16 = 0x5254)");
        }

        [Test]
        [Description("Protocol version byte must be 3 — must match Rust VERSION constant")]
        public void Version_Matches_GatewayConstant()
        {
            Assert.AreEqual(3, PacketProtocol.VERSION,
                "VERSION must equal 3 (Rust: pub const VERSION: u8 = 3)");
        }

        [Test]
        [Description("Fixed header size must be 13 bytes — must match Rust HEADER_SIZE constant")]
        public void HeaderSize_Is_ThirteenBytes()
        {
            Assert.AreEqual(13, PacketProtocol.HEADER_SIZE,
                "HEADER_SIZE must be 13 bytes: magic(2)+version(1)+type(1)+flags(1)+seq(4)+payload_len(4)");
        }

        // ── PacketFlags ───────────────────────────────────────────────────────

        [Test]
        [Description("Flag bits must be distinct single-bit values and must not overlap")]
        public void PacketFlags_HaveDistinctBits()
        {
            var compressed = (byte)PacketFlags.Compressed; // 0x01
            var encrypted  = (byte)PacketFlags.Encrypted;  // 0x02
            var reliable   = (byte)PacketFlags.Reliable;   // 0x04

            Assert.AreEqual(0x01, compressed, "Compressed flag must be bit 0 (0x01)");
            Assert.AreEqual(0x02, encrypted,  "Encrypted flag must be bit 1 (0x02)");
            Assert.AreEqual(0x04, reliable,   "Reliable flag must be bit 2 (0x04)");

            // No two flags may share a bit
            Assert.AreEqual(0, compressed & encrypted,  "Compressed and Encrypted must not overlap");
            Assert.AreEqual(0, compressed & reliable,   "Compressed and Reliable must not overlap");
            Assert.AreEqual(0, encrypted  & reliable,   "Encrypted and Reliable must not overlap");
        }

        [Test]
        [Description("PacketFlags.None must be zero so it is safe to use as a default")]
        public void PacketFlags_None_IsZero()
        {
            Assert.AreEqual(0, (byte)PacketFlags.None,
                "PacketFlags.None must be 0x00 for safe default initialisation");
        }

        // ── PacketType coverage ───────────────────────────────────────────────

        [Test]
        [Description("All 20 PacketType values are defined and match gateway enum")]
        public void PacketType_AllValues_Defined()
        {
            // Check by explicit byte value — mirrors header.rs exhaustively.
            Assert.AreEqual(0x01, (byte)PacketType.Handshake);
            Assert.AreEqual(0x02, (byte)PacketType.HandshakeAck);
            Assert.AreEqual(0x05, (byte)PacketType.HandshakeInit);
            Assert.AreEqual(0x06, (byte)PacketType.Challenge);
            Assert.AreEqual(0x07, (byte)PacketType.HandshakeResponse);
            Assert.AreEqual(0x08, (byte)PacketType.SessionAck);
            Assert.AreEqual(0x03, (byte)PacketType.Heartbeat);
            Assert.AreEqual(0x04, (byte)PacketType.HeartbeatAck);
            Assert.AreEqual(0x10, (byte)PacketType.Data);
            Assert.AreEqual(0x11, (byte)PacketType.DataAck);
            Assert.AreEqual(0x20, (byte)PacketType.RoomCreate);
            Assert.AreEqual(0x21, (byte)PacketType.RoomJoin);
            Assert.AreEqual(0x22, (byte)PacketType.RoomLeave);
            Assert.AreEqual(0x23, (byte)PacketType.RoomList);
            Assert.AreEqual(0x30, (byte)PacketType.Spawn);
            Assert.AreEqual(0x31, (byte)PacketType.Despawn);
            Assert.AreEqual(0x40, (byte)PacketType.StateSync);
            Assert.AreEqual(0x50, (byte)PacketType.Rpc);
            Assert.AreEqual(0x51, (byte)PacketType.RpcResponse);
            Assert.AreEqual(0xFF, (byte)PacketType.Disconnect);
        }

        [Test]
        [Description("Exactly 20 PacketType values are defined — guard against accidental drift")]
        public void PacketType_Count_IsEighteen()
        {
            var values = Enum.GetValues(typeof(PacketType));
            Assert.AreEqual(20, values.Length,
                "Exactly 20 PacketType members must exist (Rust gateway defines 20)"));
        }

        // ── Header field offset consistency ───────────────────────────────────

        [Test]
        [Description("Derived header field offsets must be self-consistent with HEADER_SIZE")]
        public void PacketProtocol_FieldOffsets_ConsistentWithHeaderSize()
        {
            // Verify HEADER_SIZE matches the sum of all field widths:
            // magic(2) + version(1) + type(1) + flags(1) + sequence(4) + payload_len(4) = 13
            //
            // NOTE: PacketProtocol.OFFSET_* constants are `internal` (implementation detail);
            // tests must not depend on them directly.  Instead, verify the total from first
            // principles — this is the only public contract that matters.
            const int computedSize = 2   // magic       (u16 LE)
                                   + 1   // version     (u8)
                                   + 1   // packet_type (u8)
                                   + 1   // flags       (u8)
                                   + 4   // sequence    (u32 LE)
                                   + 4;  // payload_len (u32 LE)

            Assert.AreEqual(PacketProtocol.HEADER_SIZE, computedSize,
                "HEADER_SIZE must equal the sum of all header field widths (13 bytes).");
        }
    }
}
