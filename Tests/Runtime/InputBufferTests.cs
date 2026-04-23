// RTMPE SDK — Tests/Runtime/InputBufferTests.cs
//
// NUnit Edit-Mode tests for InputBuffer.
//
// These mirror the xunit CspTests but run in the Unity test runner so they
// exercise the exact same compiled assembly that ships in the SDK.

using NUnit.Framework;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("CSP")]
    public class InputBufferTests
    {
        // ── Capacity & initial state ─────────────────────────────────────────

        [Test]
        public void Capacity_Is64()
        {
            Assert.AreEqual(64, InputBuffer.Capacity);
        }

        [Test]
        public void NewBuffer_CountIsZero()
        {
            var buf = new InputBuffer();
            Assert.AreEqual(0, buf.Count);
        }

        // ── Push ─────────────────────────────────────────────────────────────

        [Test]
        public void Push_SingleEntry_CountIsOne()
        {
            var buf = new InputBuffer();
            buf.Push(new InputPayload { Tick = 1 });
            Assert.AreEqual(1, buf.Count);
        }

        [Test]
        public void Push_64Entries_CountIs64()
        {
            var buf = new InputBuffer();
            for (uint i = 0; i < 64; i++)
                buf.Push(new InputPayload { Tick = i });
            Assert.AreEqual(64, buf.Count);
        }

        [Test]
        public void Push_OverCapacity_CountRemainsAt64()
        {
            var buf = new InputBuffer();
            for (uint i = 0; i < 70; i++)
                buf.Push(new InputPayload { Tick = i });
            Assert.AreEqual(64, buf.Count);
        }

        [Test]
        public void Push_OverCapacity_OldestEntryOverwritten()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];

            for (uint i = 0; i < 64; i++)
                buf.Push(new InputPayload { Tick = i });

            for (uint i = 64; i < 69; i++)
                buf.Push(new InputPayload { Tick = i });

            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(64, n);
            Assert.AreEqual(5u,  dest[0].Tick);
            Assert.AreEqual(68u, dest[63].Tick);
        }

        // ── AcknowledgeUpTo ──────────────────────────────────────────────────

        [Test]
        public void AcknowledgeUpTo_EmptyBuffer_DoesNotThrow()
        {
            var buf = new InputBuffer();
            Assert.DoesNotThrow(() => buf.AcknowledgeUpTo(100));
            Assert.AreEqual(0, buf.Count);
        }

        [Test]
        public void AcknowledgeUpTo_AcksAll_CountBecomesZero()
        {
            var buf = new InputBuffer();
            for (uint i = 1; i <= 10; i++)
                buf.Push(new InputPayload { Tick = i });
            buf.AcknowledgeUpTo(10);
            Assert.AreEqual(0, buf.Count);
        }

        [Test]
        public void AcknowledgeUpTo_AcksPartial_LeavesCorrectRemainder()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];

            for (uint i = 1; i <= 10; i++)
                buf.Push(new InputPayload { Tick = i });

            buf.AcknowledgeUpTo(5);
            Assert.AreEqual(5, buf.Count);

            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(5, n);
            Assert.AreEqual(6u,  dest[0].Tick);
            Assert.AreEqual(10u, dest[4].Tick);
        }

        // ── CopyUnacknowledgedTo ─────────────────────────────────────────────

        [Test]
        public void CopyUnacknowledgedTo_PreservesOldestFirstOrder()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];

            for (uint i = 1; i <= 5; i++)
                buf.Push(new InputPayload { Tick = i });

            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(5, n);
            for (int i = 0; i < 5; i++)
                Assert.AreEqual((uint)(i + 1), dest[i].Tick);
        }

        [Test]
        public void CopyUnacknowledgedTo_WrappedBuffer_PreservesOrder()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];

            for (uint i = 0; i < 64; i++)
                buf.Push(new InputPayload { Tick = i });

            buf.AcknowledgeUpTo(31);
            for (uint i = 64; i < 96; i++)
                buf.Push(new InputPayload { Tick = i });

            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(64, n);
            Assert.AreEqual(32u, dest[0].Tick);
            Assert.AreEqual(95u, dest[63].Tick);
        }

        // ── Clear ────────────────────────────────────────────────────────────

        [Test]
        public void Clear_AfterPushes_CountBecomesZero()
        {
            var buf = new InputBuffer();
            for (uint i = 0; i < 20; i++)
                buf.Push(new InputPayload { Tick = i });
            buf.Clear();
            Assert.AreEqual(0, buf.Count);
        }

        [Test]
        public void Clear_ThenPush_BufferFunctionsCorrectly()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];

            for (uint i = 0; i < 10; i++)
                buf.Push(new InputPayload { Tick = i });
            buf.Clear();

            buf.Push(new InputPayload { Tick = 99 });
            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(1, n);
            Assert.AreEqual(99u, dest[0].Tick);
        }
    }
}
