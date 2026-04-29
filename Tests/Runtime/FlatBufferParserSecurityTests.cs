// RTMPE SDK — Tests/Runtime/FlatBufferParserSecurityTests.cs
//
// Adversarial coverage for the hardened FlatBuffers parser surface. Each
// test constructs a payload that previously would have either silently
// produced garbage (UTF-8 substitution, NaN floats), thrown an opaque
// exception (negative vector lengths via MemoryMarshal.Cast), or crashed
// on a big-endian build. After the hardening, every adversarial input
// must be rejected by VerifiedFlatBuffer.TryGetRoot returning false, by
// the InputPayload reader throwing at the parse boundary, or by the
// ByteBuffer bounds guard firing under all build flag combinations.

using System;
using System.Text;
using NUnit.Framework;
using Google.FlatBuffers;
using RTMPE.Infrastructure.Serialization;
using RTMPE.States;
using FbInputPayload = RTMPE.States.InputPayload;
using FbInputPayloadVerify = RTMPE.States.InputPayloadVerify;
using FbStateSync = RTMPE.States.StateSyncPayload;
using FbStateSyncVerify = RTMPE.States.StateSyncPayloadVerify;
using FbVarUpdate = RTMPE.States.NetworkVariableUpdate;
using FbVarUpdateVerify = RTMPE.States.NetworkVariableUpdateVerify;
using CoreInputPayload = RTMPE.Core.InputPayload;
// RTMPE.States exposes a ValueType enum that collides with System.ValueType.
// Alias so unqualified uses below resolve to the schema enum.
using ValueType = RTMPE.States.ValueType;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Security")]
    public class FlatBufferParserSecurityTests
    {
        private static byte[] BuildStateSyncWithRoomId(string roomId)
        {
            var b = new FlatBufferBuilder(initialSize: 256);
            var roomIdOffset = b.CreateString(roomId);
            FbStateSync.StartStateSyncPayload(b);
            FbStateSync.AddRoomId(b, roomIdOffset);
            FbStateSync.AddTick(b, 1);
            var end = FbStateSync.EndStateSyncPayload(b);
            b.Finish(end.Value);
            return b.SizedByteArray();
        }

        private static byte[] BuildStateSyncWithRawRoomBytes(byte[] roomBytes)
        {
            // Build a valid StateSyncPayload around a benign room id, then
            // overwrite the UTF-8 string contents with the supplied raw
            // bytes. Length stays identical so the structural verifier
            // does not reject; the strict UTF-8 / NUL guards in the
            // hardened __string accessor must catch the malformed payload.
            string placeholder = new string('a', roomBytes.Length);
            var bytes = BuildStateSyncWithRoomId(placeholder);
            // Locate the placeholder run inside the buffer and overwrite.
            for (int i = 0; i + roomBytes.Length <= bytes.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < roomBytes.Length; j++)
                {
                    if (bytes[i + j] != (byte)'a') { match = false; break; }
                }
                if (match)
                {
                    Buffer.BlockCopy(roomBytes, 0, bytes, i, roomBytes.Length);
                    return bytes;
                }
            }
            throw new InvalidOperationException("placeholder not found in built buffer");
        }

        [Test]
        public void Utf8_InvalidSequence_TryGetRoot_ReturnsFalse()
        {
            // 0xC3 0x28 is a classic invalid two-byte UTF-8 sequence: the
            // continuation byte does not start with 10xxxxxx. Encoding.UTF8
            // would silently emit U+FFFD; the strict decoder must throw and
            // the wrapper must surface that as a clean rejection.
            var raw = new byte[] { 0xC3, 0x28, 0xC3, 0x28 };
            var bytes = BuildStateSyncWithRawRoomBytes(raw);

            var ok = VerifiedFlatBuffer.TryGetRoot<FbStateSync>(
                bytes,
                FbStateSyncVerify.Verify,
                FbStateSync.GetRootAsStateSyncPayload,
                out var root,
                "StateSync.Utf8Invalid");
            // Verifier passes structurally; ensure that touching the field
            // surfaces an exception that any sane caller would catch.
            if (ok)
            {
                Assert.Throws<System.Text.DecoderFallbackException>(() => { var _ = root.RoomId; });
            }
            else
            {
                Assert.Pass("rejected before semantic field access");
            }
        }

        [Test]
        public void Utf8_EmbeddedNul_StringAccess_Throws()
        {
            var raw = new byte[] { (byte)'a', 0x00, (byte)'b', (byte)'c' };
            var bytes = BuildStateSyncWithRawRoomBytes(raw);

            var ok = VerifiedFlatBuffer.TryGetRoot<FbStateSync>(
                bytes,
                FbStateSyncVerify.Verify,
                FbStateSync.GetRootAsStateSyncPayload,
                out var root,
                "StateSync.EmbeddedNul");
            if (ok)
            {
                Assert.Throws<InvalidOperationException>(() => { var _ = root.RoomId; });
            }
            else
            {
                Assert.Pass("rejected before semantic field access");
            }
        }

        [Test]
        public void NegativeVectorLength_TryGetRoot_ReturnsFalse()
        {
            // Build a benign payload then locate the players-vector length
            // prefix and overwrite it with a negative int32 (sign-flip via
            // 0x80000000). The structural verifier should reject, but if
            // it slipped through the hardened __vector_len would catch it.
            var b = new FlatBufferBuilder(initialSize: 256);
            var roomIdOffset = b.CreateString("room");
            var playersVec = FbStateSync.CreatePlayersVector(b, new Offset<PlayerState>[0]);
            FbStateSync.StartStateSyncPayload(b);
            FbStateSync.AddRoomId(b, roomIdOffset);
            FbStateSync.AddPlayers(b, playersVec);
            var end = FbStateSync.EndStateSyncPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            // Brute-force scan for the four-byte little-endian zero that
            // represents the empty vector length and flip it to int.MinValue.
            for (int i = 0; i + 4 <= bytes.Length; i++)
            {
                if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 0)
                {
                    bytes[i] = 0x00; bytes[i + 1] = 0x00; bytes[i + 2] = 0x00; bytes[i + 3] = 0x80;
                    break;
                }
            }

            var ok = VerifiedFlatBuffer.TryGetRoot<FbStateSync>(
                bytes,
                FbStateSyncVerify.Verify,
                FbStateSync.GetRootAsStateSyncPayload,
                out _,
                "StateSync.NegLen");
            Assert.IsFalse(ok, "negative vector length must be rejected");
        }

        [Test]
        public void VectorLengthCap_RejectedByVectorLen()
        {
            // Direct unit test on the hardened __vector_len: synthesise a
            // ByteBuffer where a vector reports a length above the cap and
            // confirm the accessor throws with a clear message rather than
            // dumping out into MemoryMarshal.Cast / OOM.
            var bb = new ByteBuffer(new byte[64]);
            // Layout: vtable_offset(4)=4, vector_offset_offset(4)=4,
            //        vector_length=999_999. The accessor walks bb_pos -> +4 -> +length.
            bb.PutInt(0, 4);          // bb_pos = 0; relative offset to vector
            bb.PutInt(4, 999999);     // vector length read
            var table = new Table(0, bb);
            Assert.Throws<InvalidOperationException>(() => table.__vector_len(0));
        }

        [Test]
        public void NaNFloat_InCoreInputPayload_ReadFromThrows()
        {
            var buf = new byte[CoreInputPayload.WireSize];
            // Tick = 1
            buf[0] = 1;
            // MoveX = NaN  (0x7FC00000 little-endian)
            buf[4] = 0x00; buf[5] = 0x00; buf[6] = 0xC0; buf[7] = 0x7F;
            // MoveY = 0.0
            // Flags = 0
            Assert.Throws<InvalidOperationException>(() => CoreInputPayload.ReadFrom(buf, 0));
        }

        [Test]
        public void InfinityDouble_RejectedBySafeAccessor()
        {
            // SafeGetDouble is the parser-boundary helper for any double
            // field destined for game state. Feed it +Inf and confirm it
            // refuses to hand the value back to the consumer.
            var bb = new ByteBuffer(new byte[16]);
            bb.PutDouble(0, double.PositiveInfinity);
            Assert.Throws<InvalidOperationException>(
                () => SafeFlatBufferAccessors.SafeGetDouble(bb, 0));
        }

        [Test]
        public void NaNFloat_RejectedBySafeAccessor()
        {
            var bb = new ByteBuffer(new byte[8]);
            bb.PutFloat(0, float.NaN);
            Assert.Throws<InvalidOperationException>(
                () => SafeFlatBufferAccessors.SafeGetFloat(bb, 0));
        }

        [Test]
        public void OutOfRangeValueType_RejectedAtParserBoundary()
        {
            // Cast an undefined byte (7) to ValueType — only 0-6 are defined.
            // RequireValid must surface an InvalidOperationException so the
            // dispatch switch in the consumer never sees the unknown tag.
            var undefined = (ValueType)42;
            Assert.IsFalse(SafeFlatBufferAccessors.IsValid(undefined));
            Assert.Throws<InvalidOperationException>(
                () => SafeFlatBufferAccessors.RequireValid(undefined));
        }

        [Test]
        public void DefinedValueTypes_PassValidation()
        {
            for (int i = 0; i <= 6; i++)
            {
                var v = (ValueType)i;
                Assert.IsTrue(SafeFlatBufferAccessors.IsValid(v),
                    "tag " + i + " is part of the defined enum range");
                Assert.AreEqual(v, SafeFlatBufferAccessors.RequireValid(v));
            }
        }

        [Test]
        public void ByteVector_ReadsCorrectly_OnHostEndian()
        {
            // Smoke test confirming that the hardened __vector_as_array<byte>
            // path no longer throws on byte vectors. Byte ordering is
            // irrelevant for elementSize=1, so the same code path runs on
            // every platform.
            var b = new FlatBufferBuilder(initialSize: 64);
            var data = FbVarUpdate.CreateDataVector(b, new byte[] { 1, 2, 3, 4 });
            FbVarUpdate.StartNetworkVariableUpdate(b);
            FbVarUpdate.AddData(b, data);
            FbVarUpdate.AddValueType(b, ValueType.Bytes);
            var end = FbVarUpdate.EndNetworkVariableUpdate(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbVarUpdate>(
                bytes,
                FbVarUpdateVerify.Verify,
                FbVarUpdate.GetRootAsNetworkVariableUpdate,
                out var root,
                "VarUpdate.Bytes");
            Assert.IsTrue(ok);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4 }, root.GetDataArray());
        }

        [Test]
        public void OutOfBufferOffset_BoundsCheckFires()
        {
            // Confirm the unconditional bounds guard in AssertOffsetAndLength
            // refuses an offset past the buffer regardless of build flags.
            // This is the primitive that stops UNSAFE_BYTEBUFFER +
            // BYTEBUFFER_NO_BOUNDS_CHECK from becoming an arbitrary read.
            var bb = new ByteBuffer(new byte[8]);
            Assert.Throws<ArgumentOutOfRangeException>(() => bb.GetInt(7));
            Assert.Throws<ArgumentOutOfRangeException>(() => bb.GetUlong(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => bb.GetInt(-1));
        }

        [Test]
        public void ExcessiveStringLength_StringAccess_Throws()
        {
            // A string longer than MaxStringBytes must be rejected by the
            // hardened __string accessor before any decode work happens.
            var huge = new string('x', 5000);
            var b = new FlatBufferBuilder(initialSize: 8192);
            var idOffset = b.CreateString(huge);
            FbStateSync.StartStateSyncPayload(b);
            FbStateSync.AddRoomId(b, idOffset);
            var end = FbStateSync.EndStateSyncPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbStateSync>(
                bytes,
                FbStateSyncVerify.Verify,
                FbStateSync.GetRootAsStateSyncPayload,
                out var root,
                "StateSync.HugeStr");
            // Structural verifier may pass; touching the field must throw.
            if (ok)
            {
                Assert.Throws<InvalidOperationException>(() => { var _ = root.RoomId; });
            }
            else
            {
                Assert.Pass("rejected by structural verifier");
            }
        }

        [Test]
        public void StateSync_TotalVectorElementsCap_Rejected()
        {
            // Build a StateSyncPayload whose Players vector alone exceeds
            // the per-payload aggregate cap. The structural verifier walks
            // every element which would balloon parse time; the aggregate
            // cap in VerifiedFlatBuffer rejects before any element work.
            var b = new FlatBufferBuilder(initialSize: 1 << 16);
            var idOffset = b.CreateString("r");
            // 5000 element offsets — over the 4096 aggregate cap.
            FbStateSync.StartPlayersVector(b, 5000);
            for (int i = 0; i < 5000; i++) b.AddOffset(0);
            var playersVec = b.EndVector();
            FbStateSync.StartStateSyncPayload(b);
            FbStateSync.AddRoomId(b, idOffset);
            FbStateSync.AddPlayers(b, playersVec);
            var end = FbStateSync.EndStateSyncPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbStateSync>(
                bytes,
                FbStateSyncVerify.Verify,
                FbStateSync.GetRootAsStateSyncPayload,
                out _,
                "StateSync.HugeVec");
            Assert.IsFalse(ok, "payload over aggregate vector cap must be rejected");
        }

        [Test]
        public void NetworkVariableUpdate_UndefinedValueType_Rejected()
        {
            // The wire format permits any byte for ValueType; build a
            // payload with a byte just above the defined max and confirm
            // VerifiedFlatBuffer's semantic pass rejects the packet.
            var b = new FlatBufferBuilder(initialSize: 64);
            FbVarUpdate.StartNetworkVariableUpdate(b);
            FbVarUpdate.AddValueType(b, (ValueType)200);
            var end = FbVarUpdate.EndNetworkVariableUpdate(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbVarUpdate>(
                bytes,
                FbVarUpdateVerify.Verify,
                FbVarUpdate.GetRootAsNetworkVariableUpdate,
                out _,
                "VarUpdate.UnknownTag");
            Assert.IsFalse(ok, "undefined ValueType tag must be rejected at parse boundary");
        }
    }
}
