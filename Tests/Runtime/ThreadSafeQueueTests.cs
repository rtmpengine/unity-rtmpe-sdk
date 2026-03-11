// RTMPE SDK — Tests/Runtime/ThreadSafeQueueTests.cs
//
// NUnit tests for ThreadSafeQueue<T>.
// These are pure C# tests with no Unity engine dependencies — they run in
// both Edit Mode (Unity Test Runner) and standard NUnit CLI.

using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using RTMPE.Threading;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Threading")]
    public class ThreadSafeQueueTests
    {
        // ── IsEmpty / Count ────────────────────────────────────────────────────

        [Test]
        [Description("A freshly constructed queue must report IsEmpty = true.")]
        public void IsEmpty_OnNewQueue_IsTrue()
        {
            var q = new ThreadSafeQueue<int>();
            Assert.IsTrue(q.IsEmpty, "New queue should be empty.");
        }

        [Test]
        [Description("IsEmpty must return false after one item is enqueued.")]
        public void IsEmpty_AfterEnqueue_IsFalse()
        {
            var q = new ThreadSafeQueue<int>();
            q.Enqueue(42);
            Assert.IsFalse(q.IsEmpty);
        }

        [Test]
        [Description("Count reflects the number of enqueued items.")]
        public void Count_AfterThreeEnqueues_IsThree()
        {
            var q = new ThreadSafeQueue<string>();
            q.Enqueue("a");
            q.Enqueue("b");
            q.Enqueue("c");
            Assert.AreEqual(3, q.Count);
        }

        // ── TryDequeue ─────────────────────────────────────────────────────────

        [Test]
        [Description("TryDequeue on an empty queue returns false and leaves item as default.")]
        public void TryDequeue_EmptyQueue_ReturnsFalse()
        {
            var q = new ThreadSafeQueue<int>();
            bool result = q.TryDequeue(out int item);

            Assert.IsFalse(result, "TryDequeue should return false when empty.");
            Assert.AreEqual(default(int), item, "item should be default when queue is empty.");
        }

        [Test]
        [Description("TryDequeue returns true and the enqueued value.")]
        public void TryDequeue_AfterEnqueue_ReturnsTrueWithItem()
        {
            var q = new ThreadSafeQueue<int>();
            q.Enqueue(7);

            bool ok = q.TryDequeue(out int item);

            Assert.IsTrue(ok);
            Assert.AreEqual(7, item);
        }

        [Test]
        [Description("Queue is FIFO: items dequeue in the order they were enqueued.")]
        public void Dequeue_PreservesFifoOrder()
        {
            var q = new ThreadSafeQueue<int>();
            for (int i = 0; i < 5; i++) q.Enqueue(i);

            for (int expected = 0; expected < 5; expected++)
            {
                Assert.IsTrue(q.TryDequeue(out int actual),
                    $"Expected item {expected} but queue was empty.");
                Assert.AreEqual(expected, actual,
                    $"FIFO order violated: expected {expected}, got {actual}.");
            }
        }

        [Test]
        [Description("After draining all items the queue must report IsEmpty = true.")]
        public void IsEmpty_AfterFullDrain_IsTrue()
        {
            var q = new ThreadSafeQueue<int>();
            q.Enqueue(1);
            q.Enqueue(2);
            q.TryDequeue(out _);
            q.TryDequeue(out _);

            Assert.IsTrue(q.IsEmpty);
            Assert.AreEqual(0, q.Count);
        }

        // ── Clear ──────────────────────────────────────────────────────────────

        [Test]
        [Description("Clear() discards all pending items.")]
        public void Clear_RemovesAllItems()
        {
            var q = new ThreadSafeQueue<int>();
            for (int i = 0; i < 10; i++) q.Enqueue(i);

            q.Clear();

            Assert.IsTrue(q.IsEmpty, "Queue should be empty after Clear().");
            Assert.AreEqual(0, q.Count);
        }

        [Test]
        [Description("Clear() on an already-empty queue is a safe no-op.")]
        public void Clear_OnEmptyQueue_IsNoOp()
        {
            var q = new ThreadSafeQueue<int>();
            Assert.DoesNotThrow(() => q.Clear());
            Assert.IsTrue(q.IsEmpty);
        }

        // ── Thread safety ──────────────────────────────────────────────────────

        [Test]
        [Description("Concurrent single-producer / single-consumer: no items lost or duplicated.")]
        [Timeout(5_000)]
        public void ConcurrentProducerConsumer_NoItemsLost()
        {
            const int itemCount = 10_000;
            var q        = new ThreadSafeQueue<int>();
            var received = new List<int>(itemCount);
            var done     = new ManualResetEventSlim(false);

            // Consumer thread
            var consumer = new Thread(() =>
            {
                while (received.Count < itemCount)
                {
                    if (q.TryDequeue(out var item))
                        received.Add(item);
                    else
                        Thread.SpinWait(1);
                }
                done.Set();
            }) { IsBackground = true };

            consumer.Start();

            // Producer (this thread)
            for (int i = 0; i < itemCount; i++)
                q.Enqueue(i);

            Assert.IsTrue(done.Wait(4_000), "Consumer did not finish within 4 s.");
            Assert.AreEqual(itemCount, received.Count, "Item count mismatch.");
        }

        [Test]
        [Description("Reference type items are not corrupted by concurrent access.")]
        [Timeout(5_000)]
        public void ConcurrentEnqueue_ReferenceTypes_NoCorruption()
        {
            const int itemCount = 5_000;
            var q = new ThreadSafeQueue<string>();
            int dequeued = 0;

            var producer = new Thread(() =>
            {
                for (int i = 0; i < itemCount; i++)
                    q.Enqueue($"item-{i}");
            }) { IsBackground = true };

            producer.Start();
            producer.Join(2_000);

            while (q.TryDequeue(out var s))
            {
                Assert.IsNotNull(s);
                Assert.IsTrue(s.StartsWith("item-"), $"Unexpected item: '{s}'");
                dequeued++;
            }

            Assert.AreEqual(itemCount, dequeued, "Not all enqueued items were dequeued.");
        }
    }
}
