// RTMPE SDK — Tests/Runtime/NetworkVariableUnionTests.cs
//
// Coverage for the discriminated-union encoding of network-variable updates:
//
//  • Round-trip for each of the seven NetworkVariableValue union variants
//    (Bool, Int32, Int64, Float32, Float64, String, Bytes) through the
//    ergonomic build helpers in NetworkVariableEncoding and back through
//    the matching TryRead accessor.
//  • Wire-format integrity: the union type-tag byte and the union table
//    offset round-trip identically across the FlatBuffers structural
//    verifier so a (tag, offset) mismatch cannot survive a parse.
//  • TryRead returns false (without throwing) when the requested variant
//    does not match the carried tag — production code must drop a
//    type-mismatched update without unwinding through an exception.
//  • VerifiedFlatBuffer accepts the new root binding and rejects a
//    pathological string / bytes variant that exceeds the semantic cap.
//  • WireFormatVersion enum has the documented numeric values and the
//    Default constant exposes the V2 (legacy) format.

using System;
using System.Text;
using Google.FlatBuffers;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Infrastructure.Serialization;
using FbNetworkVariableUpdateV2       = RTMPE.States.NetworkVariableUpdateV2;
using FbNetworkVariableUpdateV2Verify = RTMPE.States.NetworkVariableUpdateV2Verify;
using FbNetworkVariableValue          = RTMPE.States.NetworkVariableValue;
using FbNetworkVariableBytes          = RTMPE.States.NetworkVariableBytes;
using FbNetworkVariableString         = RTMPE.States.NetworkVariableString;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Security")]
    [Category("FlatBuffers")]
    public class NetworkVariableUnionTests
    {
        private const uint  ObjectId    = 17u;
        private const byte  VariableId  = 3;
        private const ulong TimestampUs = 1234567890UL;

        // ───────────────────────────────────────────────────────────────
        // Round-trip — every variant
        // ───────────────────────────────────────────────────────────────

        [Test]
        public void RoundTrip_Bool_PreservesValue()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBoolUpdate(
                b, ObjectId, VariableId, true, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableBool, update.ValueType);
            Assert.AreEqual(ObjectId,    update.ObjectId);
            Assert.AreEqual(VariableId,  update.VariableId);
            Assert.AreEqual(TimestampUs, update.TimestampUs);

            Assert.IsTrue(NetworkVariableEncoding.TryReadBool(update, out bool readback));
            Assert.IsTrue(readback);
        }

        [Test]
        public void RoundTrip_Bool_FalseValue_PreservesValue()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBoolUpdate(
                b, ObjectId, VariableId, false, TimestampUs));

            Assert.IsTrue(NetworkVariableEncoding.TryReadBool(update, out bool readback));
            Assert.IsFalse(readback);
        }

        [Test]
        public void RoundTrip_Int32_PreservesValue()
        {
            const int Sentinel = -987_654;
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateInt32Update(
                b, ObjectId, VariableId, Sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableInt32, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadInt32(update, out int readback));
            Assert.AreEqual(Sentinel, readback);
        }

        [Test]
        public void RoundTrip_Int64_PreservesValue()
        {
            const long Sentinel = 9_223_372_036_854_775_000L;
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateInt64Update(
                b, ObjectId, VariableId, Sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableInt64, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadInt64(update, out long readback));
            Assert.AreEqual(Sentinel, readback);
        }

        [Test]
        public void RoundTrip_Float32_PreservesValue()
        {
            const float Sentinel = -3.141_5926f;
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateFloat32Update(
                b, ObjectId, VariableId, Sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableFloat32, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadFloat32(update, out float readback));
            Assert.AreEqual(Sentinel, readback);
        }

        [Test]
        public void RoundTrip_Float64_PreservesValue()
        {
            const double Sentinel = 2.718_281_828_459_045d;
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateFloat64Update(
                b, ObjectId, VariableId, Sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableFloat64, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadFloat64(update, out double readback));
            Assert.AreEqual(Sentinel, readback);
        }

        [Test]
        public void RoundTrip_String_PreservesValue()
        {
            const string Sentinel = "rtmpe-network-variable-string-sentinel-αβγ";
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateStringUpdate(
                b, ObjectId, VariableId, Sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableString, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadString(update, out string readback));
            Assert.AreEqual(Sentinel, readback);
        }

        [Test]
        public void RoundTrip_Bytes_PreservesValue()
        {
            var sentinel = new byte[] { 0x00, 0x10, 0x7F, 0x80, 0xFE, 0xFF };
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBytesUpdate(
                b, ObjectId, VariableId, sentinel, TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableBytes, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadBytes(update, out byte[] readback));
            CollectionAssert.AreEqual(sentinel, readback);
        }

        [Test]
        public void RoundTrip_Bytes_EmptyArray_ProducesEmptyVector()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBytesUpdate(
                b, ObjectId, VariableId, Array.Empty<byte>(), TimestampUs));

            Assert.AreEqual(FbNetworkVariableValue.NetworkVariableBytes, update.ValueType);
            Assert.IsTrue(NetworkVariableEncoding.TryReadBytes(update, out byte[] readback));
            Assert.AreEqual(0, readback.Length);
        }

        // ───────────────────────────────────────────────────────────────
        // TryRead — type-mismatch must return false without throwing
        // ───────────────────────────────────────────────────────────────

        [Test]
        public void TryReadInt32_OnBoolUpdate_ReturnsFalse()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBoolUpdate(
                b, ObjectId, VariableId, true, TimestampUs));

            Assert.IsFalse(NetworkVariableEncoding.TryReadInt32(update, out int v));
            Assert.AreEqual(0, v);
        }

        [Test]
        public void TryReadString_OnBytesUpdate_ReturnsFalse()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateBytesUpdate(
                b, ObjectId, VariableId, new byte[] { 1, 2, 3 }, TimestampUs));

            Assert.IsFalse(NetworkVariableEncoding.TryReadString(update, out string v));
            Assert.IsNull(v);
        }

        [Test]
        public void TryReadBytes_OnFloat64Update_ReturnsFalse()
        {
            var update = BuildAndParse(b => NetworkVariableEncoding.CreateFloat64Update(
                b, ObjectId, VariableId, 1.0, TimestampUs));

            Assert.IsFalse(NetworkVariableEncoding.TryReadBytes(update, out byte[] v));
            Assert.IsNull(v);
        }

        // ───────────────────────────────────────────────────────────────
        // VerifiedFlatBuffer integration
        // ───────────────────────────────────────────────────────────────

        [Test]
        public void VerifiedFlatBuffer_AcceptsValidUpdateV2()
        {
            var bytes = BuildWire(b => NetworkVariableEncoding.CreateInt32Update(
                b, ObjectId, VariableId, 42, TimestampUs));

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                FbNetworkVariableUpdateV2Verify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out var update,
                "union-int32-valid");

            Assert.IsTrue(ok, "Structurally valid Int32 union update must pass VerifiedFlatBuffer");
            Assert.AreEqual(42, NetworkVariableEncoding.TryReadInt32(update, out int v) ? v : -1);
        }

        [Test]
        public void VerifiedFlatBuffer_RejectsMismatchedVerifierAndRoot()
        {
            // Build a structurally valid InputPayload, then attempt to parse
            // it through the NetworkVariableUpdateV2 (verifier, factory)
            // pair. The boundary discriminant in IsRegisteredRootBinding
            // must reject the mismatch before any structural check runs.
            var b = new FlatBufferBuilder(64);
            RTMPE.States.InputPayload.StartInputPayload(b);
            RTMPE.States.InputPayload.AddTick(b, 1);
            var end = RTMPE.States.InputPayload.EndInputPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                RTMPE.States.InputPayloadVerify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out _,
                "union-mismatched-verifier");

            Assert.IsFalse(ok, "A (verifier, root) declaring-type mismatch must be rejected");
        }

        [Test]
        public void VerifiedFlatBuffer_RejectsOversizeStringVariant()
        {
            // A UTF-8 byte length above MaxTotalVectorElements must be
            // rejected by the semantic-pass validator (not by the structural
            // verifier — strings of that size are still in-bounds inside a
            // 16 KB MTU when the test buffer is sized accordingly).
            int excess = SafeFlatBufferAccessors.MaxTotalVectorElements + 1;
            string huge = new string('A', excess);

            var bytes = BuildWire(b => NetworkVariableEncoding.CreateStringUpdate(
                b, ObjectId, VariableId, huge, TimestampUs));

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                FbNetworkVariableUpdateV2Verify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out _,
                "union-string-oversize");

            Assert.IsFalse(ok, "String variant exceeding MaxTotalVectorElements must be rejected");
        }

        [Test]
        public void VerifiedFlatBuffer_RejectsOversizeBytesVariant()
        {
            int excess = SafeFlatBufferAccessors.MaxTotalVectorElements + 1;
            var huge   = new byte[excess];

            var bytes = BuildWire(b => NetworkVariableEncoding.CreateBytesUpdate(
                b, ObjectId, VariableId, huge, TimestampUs));

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                FbNetworkVariableUpdateV2Verify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out _,
                "union-bytes-oversize");

            Assert.IsFalse(ok, "Bytes variant exceeding MaxTotalVectorElements must be rejected");
        }

        [Test]
        public void VerifiedFlatBuffer_AcceptsStringVariantAtCap()
        {
            // Exactly MaxTotalVectorElements UTF-8 bytes (one byte per ASCII
            // char) — the semantic check uses strict `>` so this must pass.
            int atCap = SafeFlatBufferAccessors.MaxTotalVectorElements;
            string sized = new string('A', atCap);

            var bytes = BuildWire(b => NetworkVariableEncoding.CreateStringUpdate(
                b, ObjectId, VariableId, sized, TimestampUs));

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                FbNetworkVariableUpdateV2Verify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out var update,
                "union-string-at-cap");

            Assert.IsTrue(ok, "String at exactly MaxTotalVectorElements UTF-8 bytes must be accepted");
            Assert.IsTrue(NetworkVariableEncoding.TryReadString(update, out string readback));
            Assert.AreEqual(atCap, Encoding.UTF8.GetByteCount(readback));
        }

        // ───────────────────────────────────────────────────────────────
        // Wire-format version constants
        // ───────────────────────────────────────────────────────────────

        [Test]
        public void WireFormatVersion_HasDocumentedNumericValues()
        {
            Assert.AreEqual(2, (byte)WireFormatVersion.V2,
                "V2 numeric value drives gateway negotiation; must remain stable");
            Assert.AreEqual(4, (byte)WireFormatVersion.V4,
                "V4 numeric value drives gateway negotiation; must remain stable");
        }

        [Test]
        public void WireFormat_DefaultIsV4_PreferStructuralVerifier()
        {
            // V4 routes every variable update through the FlatBuffers
            // structural verifier (closes the V2 (tag, payload)
            // inconsistency class).  Deployments whose gateway still
            // speaks V2 opt in explicitly via WireFormat.LegacyDefault
            // — the constant exists so a single per-build switch flips
            // the SDK's preferred shape without per-call-site edits.
            Assert.AreEqual(WireFormatVersion.V4, WireFormat.Default,
                "The preferred default is the structural-verifier shape; " +
                "legacy V2 deployments use WireFormat.LegacyDefault explicitly.");
            Assert.AreEqual(WireFormatVersion.V2, WireFormat.LegacyDefault,
                "LegacyDefault must remain V2 so an unmodified gateway path is reachable.");
        }

        // ───────────────────────────────────────────────────────────────
        // Structural verifier — direct invocation (sanity check)
        // ───────────────────────────────────────────────────────────────

        [Test]
        public void StructuralVerifier_AcceptsAllSevenVariants()
        {
            // Exercise the FlatBuffers compiler-emitted Verify path directly
            // (bypassing VerifiedFlatBuffer's semantic pass) so a regression
            // in code generation surfaces independently of SDK validators.
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateBoolUpdate(b, 1, 0, true,  0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateInt32Update(b, 1, 0, 1,    0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateInt64Update(b, 1, 0, 1L,   0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateFloat32Update(b, 1, 0, 1f, 0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateFloat64Update(b, 1, 0, 1d, 0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateStringUpdate(b, 1, 0, "x", 0));
            AssertStructurallyValid(b => NetworkVariableEncoding.CreateBytesUpdate(b, 1, 0, new byte[]{1}, 0));
        }

        // ── Helpers ────────────────────────────────────────────────────

        private static byte[] BuildWire(
            Func<FlatBufferBuilder, Offset<FbNetworkVariableUpdateV2>> build)
        {
            // Sized large enough for the oversize-string / oversize-bytes
            // tests; FlatBufferBuilder grows on demand for smaller inputs.
            var b = new FlatBufferBuilder(8192);
            var offset = build(b);
            b.Finish(offset.Value);
            return b.SizedByteArray();
        }

        private static FbNetworkVariableUpdateV2 BuildAndParse(
            Func<FlatBufferBuilder, Offset<FbNetworkVariableUpdateV2>> build)
        {
            var bytes = BuildWire(build);
            bool ok = VerifiedFlatBuffer.TryGetRoot<FbNetworkVariableUpdateV2>(
                bytes,
                FbNetworkVariableUpdateV2Verify.Verify,
                FbNetworkVariableUpdateV2.GetRootAsNetworkVariableUpdateV2,
                out var update,
                "union-roundtrip");
            Assert.IsTrue(ok, "Round-trip parse must succeed for a builder-emitted buffer");
            return update;
        }

        private static void AssertStructurallyValid(
            Func<FlatBufferBuilder, Offset<FbNetworkVariableUpdateV2>> build)
        {
            var bytes    = BuildWire(build);
            var byteBuf  = new ByteBuffer(bytes);
            var verifier = new Verifier(byteBuf);
            Assert.IsTrue(
                verifier.VerifyBuffer(null, false, FbNetworkVariableUpdateV2Verify.Verify),
                "Compiler-emitted Verify must accept builder output for every union variant");
        }
    }
}
