// RTMPE SDK — Tests/Runtime/RpcPacketBuilderTests.cs
//
// NUnit Edit-Mode tests for RpcPacketBuilder.
// Validates wire format, byte ordering, boundary conditions, and convenience methods.

using System;
using NUnit.Framework;
using RTMPE.Rpc;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("RpcPacketBuilder")]
    public class RpcPacketBuilderTests
    {
        // ══════════════════════════════════════════════════════════════════════
        // ── BuildRequest ───────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("BuildRequest with no payload produces correct 18-byte header.")]
        public void BuildRequest_NoPayload_Produces18Bytes()
        {
            var result = RpcPacketBuilder.BuildRequest(100, 42UL, 7);

            Assert.AreEqual(RpcLimits.RequestHeaderSize, result.Length);
        }

        [Test]
        [Description("BuildRequest encodes method_id as LE u32 at offset 0.")]
        public void BuildRequest_MethodId_EncodedCorrectly()
        {
            var result = RpcPacketBuilder.BuildRequest(0x04030201, 0UL, 0);

            Assert.AreEqual(0x01, result[0]);
            Assert.AreEqual(0x02, result[1]);
            Assert.AreEqual(0x03, result[2]);
            Assert.AreEqual(0x04, result[3]);
        }

        [Test]
        [Description("BuildRequest encodes sender_id as LE u64 at offset 4.")]
        public void BuildRequest_SenderId_EncodedCorrectly()
        {
            var result = RpcPacketBuilder.BuildRequest(0, 0x0807060504030201UL, 0);

            Assert.AreEqual(0x01, result[4]);
            Assert.AreEqual(0x02, result[5]);
            Assert.AreEqual(0x03, result[6]);
            Assert.AreEqual(0x04, result[7]);
            Assert.AreEqual(0x05, result[8]);
            Assert.AreEqual(0x06, result[9]);
            Assert.AreEqual(0x07, result[10]);
            Assert.AreEqual(0x08, result[11]);
        }

        [Test]
        [Description("BuildRequest encodes request_id as LE u32 at offset 12.")]
        public void BuildRequest_RequestId_EncodedCorrectly()
        {
            var result = RpcPacketBuilder.BuildRequest(0, 0UL, 0xAABBCCDD);

            Assert.AreEqual(0xDD, result[12]);
            Assert.AreEqual(0xCC, result[13]);
            Assert.AreEqual(0xBB, result[14]);
            Assert.AreEqual(0xAA, result[15]);
        }

        [Test]
        [Description("BuildRequest encodes payload_len as LE u16 at offset 16.")]
        public void BuildRequest_PayloadLen_EncodedCorrectly()
        {
            var payload = new byte[300]; // 0x012C

            var result = RpcPacketBuilder.BuildRequest(0, 0UL, 0, payload);

            Assert.AreEqual(0x2C, result[16]);
            Assert.AreEqual(0x01, result[17]);
        }

        [Test]
        [Description("BuildRequest appends payload bytes after the 18-byte header.")]
        public void BuildRequest_WithPayload_AppendsAfterHeader()
        {
            var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

            var result = RpcPacketBuilder.BuildRequest(0, 0UL, 0, payload);

            Assert.AreEqual(RpcLimits.RequestHeaderSize + 4, result.Length);
            Assert.AreEqual(0xDE, result[18]);
            Assert.AreEqual(0xAD, result[19]);
            Assert.AreEqual(0xBE, result[20]);
            Assert.AreEqual(0xEF, result[21]);
        }

        [Test]
        [Description("BuildRequest with null payload is equivalent to empty payload.")]
        public void BuildRequest_NullPayload_TreatedAsEmpty()
        {
            var result = RpcPacketBuilder.BuildRequest(100, 1UL, 1, null);

            Assert.AreEqual(RpcLimits.RequestHeaderSize, result.Length);
            Assert.AreEqual(0, result[16]); // payload_len low byte
            Assert.AreEqual(0, result[17]); // payload_len high byte
        }

        [Test]
        [Description("BuildRequest with max-size payload succeeds.")]
        public void BuildRequest_MaxPayload_Succeeds()
        {
            var payload = new byte[RpcLimits.MaxPayloadBytes]; // 4096

            Assert.DoesNotThrow(() => RpcPacketBuilder.BuildRequest(0, 0UL, 0, payload));
        }

        [Test]
        [Description("BuildRequest with oversized payload throws ArgumentException.")]
        public void BuildRequest_OversizedPayload_Throws()
        {
            var payload = new byte[RpcLimits.MaxPayloadBytes + 1]; // 4097

            Assert.Throws<ArgumentException>(
                () => RpcPacketBuilder.BuildRequest(0, 0UL, 0, payload));
        }

        [Test]
        [Description("BuildRequest round-trips: parser can reconstruct the original fields.")]
        public void BuildRequest_RoundTrip_ParsesCorrectly()
        {
            var payload = new byte[] { 1, 2, 3 };
            var result = RpcPacketBuilder.BuildRequest(200, 12345UL, 99, payload);

            Assert.IsTrue(RpcPacketParser.TryParseRequest(result, out var req));
            Assert.AreEqual(200U, req.MethodId);
            Assert.AreEqual(12345UL, req.SenderId);
            Assert.AreEqual(99U, req.RequestId);
            Assert.AreEqual(3, req.Payload.Length);
            Assert.AreEqual(1, req.Payload[0]);
            Assert.AreEqual(2, req.Payload[1]);
            Assert.AreEqual(3, req.Payload[2]);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── BuildPing ──────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("BuildPing uses method_id=100 with no payload.")]
        public void BuildPing_CorrectMethodIdAndNoPayload()
        {
            var result = RpcPacketBuilder.BuildPing(55UL, 1);

            Assert.IsTrue(RpcPacketParser.TryParseRequest(result, out var req));
            Assert.AreEqual(RpcMethodId.Ping, req.MethodId);
            Assert.AreEqual(55UL, req.SenderId);
            Assert.AreEqual(1U, req.RequestId);
            Assert.AreEqual(0, req.Payload.Length);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── BuildTransferOwnership ─────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("BuildTransferOwnership encodes object_id + new_owner in payload.")]
        public void BuildTransferOwnership_EncodesPayloadCorrectly()
        {
            var result = RpcPacketBuilder.BuildTransferOwnership(
                10UL, 42, 0x0102030405060708UL, "alice");

            Assert.IsTrue(RpcPacketParser.TryParseRequest(result, out var req));
            Assert.AreEqual(RpcMethodId.TransferOwnership, req.MethodId);
            Assert.AreEqual(10UL, req.SenderId);
            Assert.AreEqual(42U, req.RequestId);

            // Payload: [object_id:8][new_owner_len:2][new_owner:N]
            Assert.AreEqual(8 + 2 + 5, req.Payload.Length); // "alice" = 5 bytes UTF-8
            // object_id LE
            Assert.AreEqual(0x08, req.Payload[0]);
            Assert.AreEqual(0x07, req.Payload[1]);
            // new_owner_len = 5
            Assert.AreEqual(5, req.Payload[8]);
            Assert.AreEqual(0, req.Payload[9]);
            // "alice" ASCII
            Assert.AreEqual((byte)'a', req.Payload[10]);
            Assert.AreEqual((byte)'e', req.Payload[14]);
        }

        [Test]
        [Description("BuildTransferOwnership with null owner throws.")]
        public void BuildTransferOwnership_NullOwner_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => RpcPacketBuilder.BuildTransferOwnership(1UL, 1, 1UL, null));
        }

        [Test]
        [Description("BuildTransferOwnership with empty owner throws.")]
        public void BuildTransferOwnership_EmptyOwner_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => RpcPacketBuilder.BuildTransferOwnership(1UL, 1, 1UL, string.Empty));
        }
    }
}
