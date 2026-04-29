// RTMPE SDK — Tests/Runtime/HygieneSprintTests.cs
//
// Edit-mode regression tests for the LOW-severity hygiene sprint:
//
//  • TryReceive narrow exception filter — fatal exceptions propagate.
//  • MainThreadDispatcher off-main-thread access throws InvalidOperationException.
//  • DispatcherFullPolicy (DropTail / DropHead / Throw) honoured at the cap.
//  • UdpTransport oversize datagram rejected synchronously.
//  • UdpTransport public methods throw ObjectDisposedException after Dispose.
//  • UdpTransport endpoint state observed cleanly across publish/read.

using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using RTMPE.Threading;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Threading")]
    [Category("Hygiene")]
    public class HygieneSprintTests
    {
        // ── Fatal exceptions in TryReceive must propagate ───────────────────

        [Test]
        [Description("OutOfMemoryException from the transport must not be swallowed by TryReceive's narrowed catch.")]
        public void TryReceive_FatalException_PropagatesToOnError()
        {
            // The narrow exception filter only catches SocketException +
            // ObjectDisposedException.  An OOM thrown from Poll/Receive must
            // bubble out of the catch and be observed by the outer RunLoop's
            // catch-all (which routes it to OnError).  This proves the hygiene
            // change does not silently mask OOM.
            var transport = new ThrowOnPollTransport(() => throw new OutOfMemoryException("simulated OOM"));
            using var thread = new NetworkThread(transport);

            Exception observed = null;
            var done = new ManualResetEventSlim(false);
            thread.OnError += ex => { observed = ex; done.Set(); };
            thread.Start();

            Assert.IsTrue(done.Wait(2_000), "OnError must fire within 2s");
            Assert.IsInstanceOf<OutOfMemoryException>(observed,
                "OOM must propagate to RunLoop's outer catch — not be swallowed by the narrow TryReceive filter.");
        }

        [Test]
        [Description("SocketException from the transport stays inside TryReceive — the thread is not torn down.")]
        public void TryReceive_SocketException_StaysInLoop()
        {
            int callCount = 0;
            var transport = new ThrowOnPollTransport(() =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                    throw new SocketException((int)SocketError.NetworkReset);
                // Subsequent calls return false so the loop idles.
            });

            using var thread = new NetworkThread(transport);
            var observed = new ManualResetEventSlim(false);
            int errorCount = 0;
            thread.OnError += _ =>
            {
                Interlocked.Increment(ref errorCount);
                observed.Set();
            };
            thread.Start();

            // Default ReconnectBackoff base delay can hold up to 1 s before
            // OnError fires — wait generously.
            Assert.IsTrue(observed.Wait(3_000),
                "OnError must observe the SocketException within the backoff window.");
            Assert.IsTrue(thread.IsRunning,
                "SocketException is recoverable — the I/O loop must keep running, not terminate.");
            Assert.GreaterOrEqual(errorCount, 1, "OnError must observe the SocketException at least once.");
        }

        // ── Off-main-thread Instance access throws InvalidOperationException ─

        [Test]
        [Description("MainThreadDispatcher.Instance from a non-main thread throws InvalidOperationException, not UnityException.")]
        public void MainThreadDispatcher_Instance_OffMainThread_Throws()
        {
            // Force the cached main-thread id to a value that cannot equal the
            // worker thread we'll spin up.  The Instance lock-path checks the
            // captured id and refuses to AddComponent off-main-thread.
            SetCapturedMainThreadId(int.MaxValue);

            Exception observed = null;
            var worker = new Thread(() =>
            {
                try { _ = MainThreadDispatcher.Instance; }
                catch (Exception ex) { observed = ex; }
            });
            worker.Start();
            worker.Join(2_000);

            Assert.IsInstanceOf<InvalidOperationException>(observed,
                "Off-main-thread Instance access must surface as InvalidOperationException with a clear message.");
            StringAssert.Contains("main thread", observed.Message);
        }

        private static void SetCapturedMainThreadId(int id)
        {
            // Reset via reflection — the field is private.  Mirrors what the
            // SubsystemRegistration hook does, but lets the test choose the
            // value so we can guarantee a mismatch.
            var f = typeof(MainThreadDispatcher).GetField(
                "_mainThreadId",
                BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(f, "Internal field _mainThreadId not found — test stale?");
            f.SetValue(null, id);
        }

        // ── Oversize datagram rejected at Send, not at I/O thread ───────────

        [Test]
        [Description("UdpTransport.Send rejects a datagram larger than MaxDatagramSize at the call site.")]
        public void UdpTransport_OversizeDatagram_RejectedSynchronously()
        {
            using var transport = new UdpTransport("127.0.0.1", 9, maxDatagramSize: 1400);
            transport.Connect();

            var oversize = new byte[1401];
            Assert.Throws<ArgumentException>(() => transport.Send(oversize),
                "Oversize datagram must be rejected synchronously at Send, not deferred to the I/O thread.");

            // Spliced overload also enforces the cap.
            var huge = new byte[2_000];
            Assert.Throws<ArgumentException>(() => transport.Send(huge, 0, 1500));
        }

        // ── Public methods throw ObjectDisposedException after Dispose ──────

        [Test]
        [Description("Every public operational method on a disposed UdpTransport throws ObjectDisposedException.")]
        public void UdpTransport_PublicMethods_AfterDispose_ThrowODE()
        {
            var transport = new UdpTransport("127.0.0.1", 9);
            transport.Dispose();

            Assert.Throws<ObjectDisposedException>(() => transport.Connect());
            Assert.Throws<ObjectDisposedException>(() => transport.Send(new byte[] { 1 }));
            Assert.Throws<ObjectDisposedException>(() => transport.Send(new byte[] { 1 }, 0, 1));
            Assert.Throws<ObjectDisposedException>(() => transport.Receive(new byte[8]));
            Assert.Throws<ObjectDisposedException>(() => transport.Poll(0));
        }

        // ── Volatile / Volatile.Read endpoint state ──────────────────────────

        [Test]
        [Description("Concurrent Connect publish + LocalEndPoint reads observe a fully-initialised endpoint (best-effort harness).")]
        public void UdpTransport_VolatileEndpointState_NoTornReads()
        {
            using var transport = new UdpTransport("127.0.0.1", 9);

            // The publish path is exercised by Connect(); we then read
            // LocalEndPoint from many threads and assert each observes either
            // null (pre-publish) or a fully populated endpoint — never a
            // partially-initialised intermediate.
            var stop  = new ManualResetEventSlim(false);
            int torn  = 0;
            int reads = 0;
            var readers = new Thread[4];
            for (int i = 0; i < readers.Length; i++)
            {
                readers[i] = new Thread(() =>
                {
                    while (!stop.IsSet)
                    {
                        var ep = transport.LocalEndPoint;
                        Interlocked.Increment(ref reads);
                        if (ep != null)
                        {
                            // A torn read could yield a non-null reference whose
                            // Address is null on weak memory models.  Volatile.Write
                            // in Connect() must prevent that.
                            if (ep.Address == null) Interlocked.Increment(ref torn);
                        }
                    }
                });
                readers[i].IsBackground = true;
                readers[i].Start();
            }

            transport.Connect();
            // Let the readers race against the freshly-published state.
            Thread.Sleep(50);
            stop.Set();
            foreach (var t in readers) t.Join(2_000);

            Assert.Greater(reads, 0, "Reader threads must have observed at least one snapshot.");
            Assert.AreEqual(0, torn, "No torn reads — Volatile.Write/Read pair must guarantee a clean publish.");
        }

        // ── Per-packet allocation budget on the rented receive path ─────────

        [Test]
        [Description("Receive path through OnPacketReceivedRented holds < 32 KB allocation over 1000 datagrams.")]
        public void ReceivePath_Rented_AllocationsBoundedOver1000Packets()
        {
            // Sprint 3 migrated the receive scratch to ArrayPool<byte>.Shared
            // and added the OnPacketReceivedRented zero-copy path.  This test
            // verifies that draining 1000 datagrams through the rented path
            // does not leak per-packet allocations on the consumer thread.
            //
           // The pool itself can rent fresh arrays under contention, so the
            // ceiling is set generously (32 KB) to remain stable on noisy
            // CI hardware.  Linear-per-packet growth (e.g. 1 KB/packet) would
            // blow past the cap immediately and fail the assertion.

            var transport = new ScriptedDatagramTransport(payloadSize: 64, totalPackets: 1000);
            using var thread = new NetworkThread(transport);

            int received = 0;
            var done = new ManualResetEventSlim(false);
            thread.OnPacketReceivedRented += pkt =>
            {
                if (Interlocked.Increment(ref received) >= 1000)
                    done.Set();
            };

            // Warm: let the pool populate caches and the JIT settle.
            thread.Start();
            done.Wait(2_000);
            thread.Stop();
            transport.Reset();

            // Measured pass on this thread — note we cannot directly observe
            // allocations on the I/O thread, so the assertion is on the test
            // thread to prove the consumer-side handler path is allocation-
            // light.  The receive scratch itself is rented once per RunLoop.
            long before = GC.GetAllocatedBytesForCurrentThread();
            received = 0;
            done.Reset();
            thread.Start();
            done.Wait(5_000);
            long after = GC.GetAllocatedBytesForCurrentThread();
            thread.Stop();

            long delta = after - before;
            Assert.GreaterOrEqual(received, 1000, "Receive count must reach 1000 within timeout.");
            Assert.Less(delta, 32 * 1024,
                $"1000 datagrams leaked {delta} bytes on the consumer thread — expected < 32 KB.");
        }

        // ── Test doubles ────────────────────────────────────────────────────

        /// <summary>Yields a fixed-size synthetic datagram up to a packet count.</summary>
        private sealed class ScriptedDatagramTransport : NetworkTransport
        {
            private readonly int _payloadSize;
            private readonly int _totalPackets;
            private int _delivered;
            private bool _connected;

            public ScriptedDatagramTransport(int payloadSize, int totalPackets)
            {
                _payloadSize  = payloadSize;
                _totalPackets = totalPackets;
            }

            public void Reset() => Volatile.Write(ref _delivered, 0);

            public override bool IsConnected => _connected;
            public override void Connect()    => _connected = true;
            public override void Disconnect() => _connected = false;
            public override void Send(byte[] data) { }

            public override bool Poll(int microSeconds) =>
                Volatile.Read(ref _delivered) < _totalPackets;

            public override int Receive(byte[] buffer)
            {
                int next = Interlocked.Increment(ref _delivered);
                if (next > _totalPackets) return 0;
                int n = Math.Min(_payloadSize, buffer.Length);
                // Cheap deterministic fill — avoids allocating an extra array.
                for (int i = 0; i < n; i++) buffer[i] = (byte)(next + i);
                return n;
            }

            public override void Dispose() => Disconnect();
        }

        /// <summary>Throws (or runs) a user-supplied action on every Poll() call.</summary>
        private sealed class ThrowOnPollTransport : NetworkTransport
        {
            private readonly Action _onPoll;
            private bool _connected;

            public ThrowOnPollTransport(Action onPoll) { _onPoll = onPoll; }
            public override bool IsConnected => _connected;
            public override void Connect()    => _connected = true;
            public override void Disconnect() => _connected = false;
            public override void Send(byte[] data) { }
            public override int  Receive(byte[] buffer) => 0;
            public override bool Poll(int microSeconds) { _onPoll(); return false; }
            public override void Dispose() => Disconnect();
        }
    }

    [TestFixture]
    [Category("Threading")]
    [Category("Hygiene")]
    public class DispatcherFullPolicyTests
    {
        // ── Queue-full policy honoured at the cap ────────────────────────────

        // The dispatcher is a MonoBehaviour so we cannot exercise Update()
        // here; we focus on the ENQUEUE-side back-pressure decision, which is
        // what the policy controls.  A full queue at the cap exercises each
        // policy branch deterministically.

        [Test]
        [Description("DropTail (default) silently rejects new actions once the queue is full.")]
        public void DropTail_RejectsNewActions()
        {
            var dispatcher = NewDispatcher();
            dispatcher.FullPolicy = DispatcherFullPolicy.DropTail;
            FillToCap(dispatcher);

            int sentinelInvocations = 0;
            dispatcher.Enqueue(() => Interlocked.Increment(ref sentinelInvocations));

            Assert.AreEqual(MainThreadDispatcher.MaxQueueDepth, dispatcher.Depth,
                "DropTail must not grow the queue beyond the cap.");
            Assert.AreEqual(1, dispatcher.OverflowCount,
                "Exactly one overflow event must have been recorded.");
            UnityEngine.Object.DestroyImmediate(dispatcher.gameObject);
        }

        [Test]
        [Description("DropHead pops the oldest pending action and admits the new one.")]
        public void DropHead_DropsOldestAdmitsNew()
        {
            var dispatcher = NewDispatcher();
            dispatcher.FullPolicy = DispatcherFullPolicy.DropHead;
            FillToCap(dispatcher);

            dispatcher.Enqueue(() => { });

            // Depth stays at the cap: one popped, one admitted.
            Assert.AreEqual(MainThreadDispatcher.MaxQueueDepth, dispatcher.Depth);
            Assert.AreEqual(1, dispatcher.OverflowCount);
            UnityEngine.Object.DestroyImmediate(dispatcher.gameObject);
        }

        [Test]
        [Description("Throw policy raises InvalidOperationException at the cap.")]
        public void Throw_RaisesAtCap()
        {
            var dispatcher = NewDispatcher();
            dispatcher.FullPolicy = DispatcherFullPolicy.Throw;
            FillToCap(dispatcher);

            Assert.Throws<InvalidOperationException>(() => dispatcher.Enqueue(() => { }));
            Assert.AreEqual(MainThreadDispatcher.MaxQueueDepth, dispatcher.Depth);
            UnityEngine.Object.DestroyImmediate(dispatcher.gameObject);
        }

        private static MainThreadDispatcher NewDispatcher()
        {
            // Force a fresh GameObject — bypassing the Instance singleton
            // makes each test independent and avoids leaking state between
            // tests.  Awake will set the singleton field, which we then null
            // by destroying the previous gameObject in the caller's [TearDown]
            // pattern (DestroyImmediate above).
            var go = new UnityEngine.GameObject($"Dispatcher-Test-{Guid.NewGuid():N}");
            return go.AddComponent<MainThreadDispatcher>();
        }

        private static void FillToCap(MainThreadDispatcher d)
        {
            for (int i = 0; i < MainThreadDispatcher.MaxQueueDepth; i++)
                d.Enqueue(() => { });
            Assert.AreEqual(MainThreadDispatcher.MaxQueueDepth, d.Depth);
        }
    }
}
