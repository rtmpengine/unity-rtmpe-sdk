// RTMPE SDK — Tests/Runtime/RpcPacketParserTests.cs
//
// NUnit Edit-Mode tests for RpcPacketParser.
// Validates response parsing, request parsing, BuildResponse, and edge cases.

using System;
using NUnit.Framework;
using RTMPE.Rpc;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("RpcPacketParser")]
    public class RpcPacketParserTests
    {
        // ══════════════════════════════════════════════════════════════════════
        // ── TryParseResponse ───────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("TryParseResponse with null data returns false.")]
        public void TryParseResponse_NullData_ReturnsFalse()
        {
            Assert.IsFalse(RpcPacketParser.TryParseResponse(null, out _));
        }

        [Test]
        [Description("TryParseResponse with data shorter than header returns false.")]
        public void TryParseResponse_TruncatedHeader_ReturnsFalse()
        {
            var data = new byte[RpcLimits.ResponseHeaderSize - 1];

            Assert.IsFalse(RpcPacketParser.TryParseResponse(data, out _));
        }

        [Test]
        [Description("TryParseResponse with exact header and no payload succeeds.")]
        public void TryParseResponse_HeaderOnly_Succeeds()
        {
            var data = RpcPacketParser.BuildResponse(1, 100, 42UL, true, RpcErrorCode.OK);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(data, out var resp));
            Assert.AreEqual(1U, resp.RequestId);
            Assert.AreEqual(100U, resp.MethodId);
            Assert.AreEqual(42UL, resp.SenderId);
            Assert.IsTrue(resp.Success);
            Assert.AreEqual(RpcErrorCode.OK, resp.ErrorCode);
            Assert.AreEqual(0, resp.Payload.Length);
        }

        [Test]
        [Description("TryParseResponse with payload round-trips via BuildResponse.")]
        public void TryParseResponse_WithPayload_RoundTrips()
        {
            var payload = new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };
            var data = RpcPacketParser.BuildResponse(
                7, 200, 999UL, false, RpcErrorCode.Unauthorized, payload);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(data, out var resp));
            Assert.AreEqual(7U, resp.RequestId);
            Assert.AreEqual(200U, resp.MethodId);
            Assert.AreEqual(999UL, resp.SenderId);
            Assert.IsFalse(resp.Success);
            Assert.AreEqual(RpcErrorCode.Unauthorized, resp.ErrorCode);
            Assert.AreEqual(4, resp.Payload.Length);
            Assert.AreEqual(0xCA, resp.Payload[0]);
            Assert.AreEqual(0xBE, resp.Payload[3]);
        }

        [Test]
        [Description("TryParseResponse with truncated payload returns false.")]
        public void TryParseResponse_TruncatedPayload_ReturnsFalse()
        {
            // Build a valid response with 4-byte payload
            var data = RpcPacketParser.BuildResponse(
                1, 100, 0UL, true, RpcErrorCode.OK, new byte[4]);

            // Truncate: remove last 2 bytes (payload is incomplete)
            var truncated = new byte[data.Length - 2];
            Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);

            Assert.IsFalse(RpcPacketParser.TryParseResponse(truncated, out _));
        }

        [Test]
        [Description("TryParseResponse with all error codes parses correctly.")]
        public void TryParseResponse_AllErrorCodes_ParseCorrectly()
        {
            foreach (RpcErrorCode code in Enum.GetValues(typeof(RpcErrorCode)))
            {
                var data = RpcPacketParser.BuildResponse(0, 0, 0UL, false, code);

                Assert.IsTrue(RpcPacketParser.TryParseResponse(data, out var resp),
                    $"Failed to parse error code {code}");
                Assert.AreEqual(code, resp.ErrorCode);
            }
        }

        [Test]
        [Description("TryParseResponse with success=true and success=false.")]
        public void TryParseResponse_SuccessFlag_ParsedCorrectly()
        {
            var successData = RpcPacketParser.BuildResponse(0, 0, 0UL, true, RpcErrorCode.OK);
            var failData    = RpcPacketParser.BuildResponse(0, 0, 0UL, false, RpcErrorCode.HandlerError);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(successData, out var s));
            Assert.IsTrue(s.Success);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(failData, out var f));
            Assert.IsFalse(f.Success);
        }

        [Test]
        [Description("TryParseResponse with max-size payload succeeds.")]
        public void TryParseResponse_MaxPayload_Succeeds()
        {
            var payload = new byte[RpcLimits.MaxPayloadBytes];
            var data = RpcPacketParser.BuildResponse(0, 0, 0UL, true, RpcErrorCode.OK, payload);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(data, out var resp));
            Assert.AreEqual(RpcLimits.MaxPayloadBytes, resp.Payload.Length);
        }

        [Test]
        [Description("TryParseResponse rejects crafted payloadLen > MaxPayloadBytes (adversarial).")]
        public void TryParseResponse_CraftedOversizedPayloadLen_ReturnsFalse()
        {
            // Build a valid response, then tamper the payload_len field to exceed MaxPayloadBytes.
            var data = RpcPacketParser.BuildResponse(1, 100, 42UL, true, RpcErrorCode.OK);

            // Ensure we have enough space for the tampered value.
            var tampered = new byte[RpcLimits.ResponseHeaderSize + RpcLimits.MaxPayloadBytes + 100];
            Buffer.BlockCopy(data, 0, tampered, 0, data.Length);

            // Write payloadLen = MaxPayloadBytes + 1 at offset 19 (LE u16).
            ushort oversized = (ushort)(RpcLimits.MaxPayloadBytes + 1);
            tampered[19] = (byte)(oversized);
            tampered[20] = (byte)(oversized >> 8);

            Assert.IsFalse(RpcPacketParser.TryParseResponse(tampered, out _));
        }

        [Test]
        [Description("TryParseResponse with large sender_id preserves all 64 bits.")]
        public void TryParseResponse_LargeSenderId_PreservesAllBits()
        {
            ulong id = 0xFEDCBA9876543210UL;
            var data = RpcPacketParser.BuildResponse(0, 0, id, true, RpcErrorCode.OK);

            Assert.IsTrue(RpcPacketParser.TryParseResponse(data, out var resp));
            Assert.AreEqual(id, resp.SenderId);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── TryParseRequest ────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("TryParseRequest with null data returns false.")]
        public void TryParseRequest_NullData_ReturnsFalse()
        {
            Assert.IsFalse(RpcPacketParser.TryParseRequest(null, out _));
        }

        [Test]
        [Description("TryParseRequest with truncated header returns false.")]
        public void TryParseRequest_TruncatedHeader_ReturnsFalse()
        {
            var data = new byte[RpcLimits.RequestHeaderSize - 1];

            Assert.IsFalse(RpcPacketParser.TryParseRequest(data, out _));
        }

        [Test]
        [Description("TryParseRequest with truncated payload returns false.")]
        public void TryParseRequest_TruncatedPayload_ReturnsFalse()
        {
            // Build header claiming 10 bytes of payload, but only include 5
            var data = RpcPacketBuilder.BuildRequest(100, 0UL, 0, new byte[10]);
            var truncated = new byte[RpcLimits.RequestHeaderSize + 5];
            Buffer.BlockCopy(data, 0, truncated, 0, truncated.Length);

            Assert.IsFalse(RpcPacketParser.TryParseRequest(truncated, out _));
        }

        [Test]
        [Description("TryParseRequest rejects crafted payloadLen > MaxPayloadBytes (adversarial).")]
        public void TryParseRequest_CraftedOversizedPayloadLen_ReturnsFalse()
        {
            // Build a minimal valid request, then tamper payload_len at offset 16.
            var tampered = new byte[RpcLimits.RequestHeaderSize + RpcLimits.MaxPayloadBytes + 100];

            // Write payloadLen = MaxPayloadBytes + 1 at offset 16 (LE u16).
            ushort oversized = (ushort)(RpcLimits.MaxPayloadBytes + 1);
            tampered[16] = (byte)(oversized);
            tampered[17] = (byte)(oversized >> 8);

            Assert.IsFalse(RpcPacketParser.TryParseRequest(tampered, out _));
        }

        [Test]
        [Description("TryParseRequest round-trips with BuildRequest.")]
        public void TryParseRequest_RoundTrip_AllFields()
        {
            var payload = new byte[] { 10, 20, 30, 40, 50 };
            var data = RpcPacketBuilder.BuildRequest(300, 0xABCD1234UL, 77, payload);

            Assert.IsTrue(RpcPacketParser.TryParseRequest(data, out var req));
            Assert.AreEqual(300U, req.MethodId);
            Assert.AreEqual(0xABCD1234UL, req.SenderId);
            Assert.AreEqual(77U, req.RequestId);
            Assert.AreEqual(5, req.Payload.Length);
            Assert.AreEqual(10, req.Payload[0]);
            Assert.AreEqual(50, req.Payload[4]);
        }

        [Test]
        [Description("TryParseRequest with empty payload succeeds.")]
        public void TryParseRequest_EmptyPayload_Succeeds()
        {
            var data = RpcPacketBuilder.BuildRequest(100, 1UL, 1);

            Assert.IsTrue(RpcPacketParser.TryParseRequest(data, out var req));
            Assert.AreEqual(0, req.Payload.Length);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── BuildResponse ──────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("BuildResponse with oversized payload throws ArgumentException.")]
        public void BuildResponse_OversizedPayload_Throws()
        {
            var big = new byte[RpcLimits.MaxPayloadBytes + 1];

            Assert.Throws<ArgumentException>(
                () => RpcPacketParser.BuildResponse(0, 0, 0UL, true, RpcErrorCode.OK, big));
        }

        [Test]
        [Description("BuildResponse with null payload produces header-only response.")]
        public void BuildResponse_NullPayload_ProducesHeaderOnly()
        {
            var data = RpcPacketParser.BuildResponse(1, 2, 3UL, true, RpcErrorCode.OK, null);

            Assert.AreEqual(RpcLimits.ResponseHeaderSize, data.Length);
        }

        [Test]
        [Description("BuildResponse encodes success=true as byte value 1.")]
        public void BuildResponse_SuccessTrue_ByteIs1()
        {
            var data = RpcPacketParser.BuildResponse(0, 0, 0UL, true, RpcErrorCode.OK);

            Assert.AreEqual(1, data[16]); // success byte at offset 16
        }

        [Test]
        [Description("BuildResponse encodes success=false as byte value 0.")]
        public void BuildResponse_SuccessFalse_ByteIs0()
        {
            var data = RpcPacketParser.BuildResponse(0, 0, 0UL, false, RpcErrorCode.HandlerError);

            Assert.AreEqual(0, data[16]); // success byte at offset 16
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Constants ──────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("RpcLimits.RequestHeaderSize matches documented 18 bytes.")]
        public void Constants_RequestHeaderSize_Is18()
        {
            Assert.AreEqual(18, RpcLimits.RequestHeaderSize);
        }

        [Test]
        [Description("RpcLimits.ResponseHeaderSize matches documented 21 bytes.")]
        public void Constants_ResponseHeaderSize_Is21()
        {
            Assert.AreEqual(21, RpcLimits.ResponseHeaderSize);
        }

        [Test]
        [Description("RpcLimits.MaxPayloadBytes is 4096.")]
        public void Constants_MaxPayload_Is4096()
        {
            Assert.AreEqual(4096, RpcLimits.MaxPayloadBytes);
        }
    }
}
