// RTMPE SDK — Tests/Runtime/FlatBufferVerifierTests.cs
//
// Coverage for the structural-validation gate that guards every receive-side
// FlatBuffers parse. The contract under test is simple: TryGetRoot must
// reject every malformed input shape we can construct, never throw, and
// only return a usable root for a well-formed buffer. Additional parser
// tests (round-trips, schema evolution) live elsewhere; this fixture is
// adversarial-only.

using System;
using NUnit.Framework;
using Google.FlatBuffers;
using RTMPE.Infrastructure.Serialization;
using FbInputPayload = RTMPE.States.InputPayload;
using FbInputPayloadVerify = RTMPE.States.InputPayloadVerify;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Security")]
    public class FlatBufferVerifierTests
    {
        private static byte[] BuildValidInputPayload()
        {
            var b = new FlatBufferBuilder(initialSize: 64);
            FbInputPayload.StartInputPayload(b);
            FbInputPayload.AddTick(b, 1);
            FbInputPayload.AddMoveX(b, 0.0f);
            FbInputPayload.AddMoveY(b, 0.0f);
            FbInputPayload.AddFlags(b, 0);
            var end = FbInputPayload.EndInputPayload(b);
            b.Finish(end.Value);
            return b.SizedByteArray();
        }

        [Test]
        public void ValidBuffer_TryGetRoot_ReturnsTrueAndRoot()
        {
            var bytes = BuildValidInputPayload();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                bytes,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out var root,
                "InputPayload");

            Assert.IsTrue(ok);
            Assert.AreEqual(1u, root.Tick);
        }

        [Test]
        public void TruncatedBuffer_TryGetRoot_ReturnsFalse()
        {
            var bytes = BuildValidInputPayload();
            // Lop off the trailing half — destroys vtable / data integrity
            // without any chance of producing a still-parseable shape.
            var truncated = new byte[bytes.Length / 2];
            Buffer.BlockCopy(bytes, 0, truncated, 0, truncated.Length);

            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                truncated,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "InputPayload");

            Assert.IsFalse(ok);
        }

        [Test]
        public void RandomGarbage_TryGetRoot_ReturnsFalseAndDoesNotThrow()
        {
            // Deterministic seed — the goal is reproducible failure, not
            // statistical fuzzing (that lives in a separate dotnet-fuzz job).
            var rng = new Random(0xC0FFEE);
            for (int i = 0; i < 256; i++)
            {
                var len = rng.Next(0, 256);
                var bytes = new byte[len];
                rng.NextBytes(bytes);

                bool ok = false;
                Assert.DoesNotThrow(() =>
                {
                    ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                        bytes,
                        FbInputPayloadVerify.Verify,
                        FbInputPayload.GetRootAsInputPayload,
                        out _,
                        "InputPayload");
                });
                // We accept either reject-or-accept here only because random
                // bytes can in principle (with vanishingly small probability)
                // form a legal table; the load-bearing assertion is no throw.
                // For len < 4 the gate must always reject.
                if (len < 4)
                {
                    Assert.IsFalse(ok, "buffers shorter than the root prefix must always be rejected");
                }
            }
        }

        [Test]
        public void CorruptedRootOffset_TryGetRoot_ReturnsFalse()
        {
            var bytes = BuildValidInputPayload();
            // Overwrite the 4-byte root offset with a value that points
            // far past the end of the buffer.
            bytes[0] = 0xFF;
            bytes[1] = 0xFF;
            bytes[2] = 0xFF;
            bytes[3] = 0x7F;

            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                bytes,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "InputPayload");

            Assert.IsFalse(ok);
        }

        [Test]
        public void OversizedOffsets_TryGetRoot_ReturnsFalse()
        {
            // Poison every byte past the 4-byte root prefix with 0xFF so
            // the vtable walk encounters offsets / lengths that wildly
            // exceed the buffer. This exercises the same defensive paths
            // that catch attacker-supplied negative vector lengths and
            // out-of-range table offsets — the verifier must reject and
            // never let a sign-flipped uint propagate to a field accessor.
            var bytes = BuildValidInputPayload();
            for (int i = 4; i < bytes.Length; i++)
            {
                bytes[i] = 0xFF;
            }

            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                bytes,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "InputPayload");

            Assert.IsFalse(ok);
        }

        [Test]
        public void EmptyBuffer_TryGetRoot_ReturnsFalse()
        {
            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                Array.Empty<byte>(),
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "InputPayload");

            Assert.IsFalse(ok);
        }

        [Test]
        public void NullBuffer_TryGetRoot_ReturnsFalse()
        {
            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                null,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "InputPayload");

            Assert.IsFalse(ok);
        }

        [Test]
        public void NullVerifier_TryGetRoot_Throws()
        {
            var bytes = BuildValidInputPayload();
            Assert.Throws<ArgumentNullException>(() =>
            {
                VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                    bytes,
                    null,
                    FbInputPayload.GetRootAsInputPayload,
                    out _,
                    "InputPayload");
            });
        }
    }
}
