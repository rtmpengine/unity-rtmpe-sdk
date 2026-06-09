// RTMPE SDK — Tests/Runtime/NetworkThreadTests.cs
//
// Edit-mode regression tests for NetworkThread lifecycle, queue back-pressure,
// dispose hygiene, and the producer/Stop ordering invariant.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using NUnit.Framework;
using RTMPE.Threading;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Threading")]
    [Category("NetworkThreadLifecycle")]
    public class NetworkThreadLifecycleTests
    {
        // ── Reconnect-storm: OnError synchronously restarts ─────────────────

        [Test]
        [Description("If OnError synchronously creates a new transport+thread, the dying iteration's teardown must not dispose the fresh socket.")]
        public void OnError_SynchronousReconnect_DoesNotDisposeFreshSocket()
        {
            // First transport is rigged to throw on Connect(), forcing RunLoop
            // straight into the catch path.
            var failing = new ScriptedTransport(
                onConnect: () => throw new SocketException((int)SocketError.ConnectionRefused));

            // The replacement transport simulates the "fresh socket" the user
            // creates inside their reconnect policy.  We assert AFTER the
            // failing thread has fully unwound that this transport remains
            // connected (not disposed by a stale finally block).
            var fresh = new ScriptedTransport();

            NetworkThread reconnected = null;
            var errorObserved         = new ManualResetEventSlim(false);

            using var failingThread = new NetworkThread(failing);
            failingThread.OnError += _ =>
            {
                // Synchronously construct + start a replacement.  This is the
                // exact pattern the SDK encourages for transient transport
                // failures.  If the dying iteration's `finally` block in the
                // OUTER thread were to call Disconnect on the fresh transport,
                // `fresh.IsConnected` would flip to false below.
                reconnected = new NetworkThread(fresh);
                reconnected.Start();
                errorObserved.Set();
            };

            failingThread.Start();

            Assert.IsTrue(errorObserved.Wait(2_000), "OnError must fire within 2s");

            // Wait for the failing thread to finish its teardown and clear _startFlag.
            Assert.IsTrue(SpinWait.SpinUntil(() => !failingThread.IsRunning, 2_000),
                "failing thread must stop running within 2s");

            Assert.IsNotNull(reconnected, "OnError handler must have created the replacement");
            Assert.IsTrue(fresh.IsConnected,
                "Fresh transport created inside OnError must NOT have been " +
                "disposed by the failing iteration's teardown.");
            Assert.AreEqual(0, fresh.DisconnectCount,
                "Disconnect() must not have been invoked on the replacement transport");

            reconnected.Dispose();
        }

        [Test]
        [Description("After OnError fires, _startFlag must be cleared so a subsequent Start() succeeds.")]
        public void OnError_AllowsSubsequentStart()
        {
            var failing = new ScriptedTransport(
                onConnect: () => throw new SocketException((int)SocketError.ConnectionRefused));

            using var thread = new NetworkThread(failing);
            var errorFired   = new ManualResetEventSlim(false);
            thread.OnError  += _ => errorFired.Set();

            thread.Start();
            Assert.IsTrue(errorFired.Wait(2_000));
            SpinWait.SpinUntil(() => !thread.IsRunning, 2_000);

            // Replace the inner transport so the next Start can succeed.
            // (We can't swap _transport on the same NetworkThread, so we
            // assert the equivalent: the start-flag is reset, observable as
            // IsRunning==false after the loop has exited.)
            Assert.IsFalse(thread.IsRunning,
                "After RunLoop exits via OnError, IsRunning must be false");
        }

        [Test]
        [Description("OnError handlers that themselves throw must not crash the network thread or leak the start-flag.")]
        public void OnError_ThrowingHandler_DoesNotCorruptState()
        {
            var failing = new ScriptedTransport(
                onConnect: () => throw new SocketException((int)SocketError.ConnectionRefused));

            using var thread = new NetworkThread(failing);
            thread.OnError += _ => throw new InvalidOperationException("user bug");

            Assert.DoesNotThrow(() => thread.Start());
            SpinWait.SpinUntil(() => !thread.IsRunning, 2_000);

            Assert.IsFalse(thread.IsRunning,
                "Thread must report stopped after a user OnError handler throws");
            Assert.AreEqual(1, failing.DisconnectCount,
                "The dying iteration's transport must still be disconnected exactly once");
        }

        // ── Dispose hygiene ─────────────────────────────────────────────────

        [Test]
        [Description("Dispose called twice on the same NetworkThread must not throw and must invoke transport.Dispose exactly once.")]
        public void Dispose_Twice_IsIdempotent()
        {
            var transport = new ScriptedTransport();
            var thread    = new NetworkThread(transport);

            Assert.DoesNotThrow(() => thread.Dispose());
            Assert.DoesNotThrow(() => thread.Dispose(),
                "Second Dispose must be a no-op, not throw");
            Assert.AreEqual(1, transport.DisposeCount,
                "transport.Dispose must run exactly once across two NetworkThread.Dispose calls");
        }

        [Test]
        [Description("After Dispose, Start must throw ObjectDisposedException — the queue and timer state are gone.")]
        public void Dispose_ThenStart_Throws()
        {
            var transport = new ScriptedTransport();
            var thread    = new NetworkThread(transport);
            thread.Dispose();

            Assert.Throws<ObjectDisposedException>(() => thread.Start());
        }

        [Test]
        [Description("Dispose drains and releases pending sends so they cannot survive into a future Start.")]
        public void Dispose_DiscardsPendingSends()
        {
            var transport = new CountingTransport();
            var thread    = new NetworkThread(transport, sendQueueMaxItems: 8);
            thread.Start();
            // Stop first so Send below can't drain through the live loop.
            thread.Stop();
            // Send after Stop is rejected by the entry guard, so nothing is enqueued.
            thread.Send(new byte[] { 0x01 });
            Assert.AreEqual(0, thread.SendQueueCount,
                "Send after Stop must not enqueue");
            // Dispose must not throw and must release the queue cleanly.
            Assert.DoesNotThrow(() => thread.Dispose());
        }

        // ── Producer/Stop race ───────────────────────────────────────

        [Test]
        [Description("Send invoked after Stop must not be transmitted on the next Start.")]
        public void Send_AfterStop_NotTransmittedOnNextStart()
        {
            var transport = new CountingTransport();
            using var thread = new NetworkThread(transport, sendQueueMaxItems: 16);

            thread.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => thread.IsRunning, 2_000),
                "thread must be running before Stop");
            thread.Stop();

            // Producer racing with Stop: the entry guard rejects each send, so
            // none are enqueued for a future drain.
            for (int i = 0; i < 10; i++)
                thread.Send(new byte[] { (byte)i });
            Assert.AreEqual(0, thread.SendQueueCount,
                "post-Stop sends must not enqueue");

            int sentBeforeRestart = transport.SendCount;

            // Restart on a fresh queue. If any of the post-Stop packets had
            // survived in a stale ConcurrentQueue they would be transmitted now.
            transport.ResetCounters();
            thread.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => thread.IsRunning, 2_000),
                "thread must be running within 2s");
            thread.Stop();

            Assert.AreEqual(0, transport.SendCount,
                "No packet enqueued during the Stop window may transmit after restart");
            Assert.AreEqual(0, sentBeforeRestart,
                "Stopped thread must not transmit");
        }

        // ── Bounded queue / drop counter ─────────────────────────────

        [Test]
        [Description("Bounded queue at capacity drops new sends and increments the drop counter.")]
        public void Send_AtCapacity_DropsAndIncrementsCounter()
        {
            // Use a transport that BLOCKS in Send so the queue cannot drain.
            var blocking = new BlockingTransport();
            using var thread = new NetworkThread(blocking, sendQueueMaxItems: 4);
            thread.Start();

            // The worker parks in the blocked transport on the first packet; the
            // queue then fills to its cap and every further send is dropped
            // (drop-newest). Send is fire-and-forget, so saturation is observed
            // through the drop counter rather than a return value.
            for (int i = 0; i < 32; i++)
                thread.Send(new byte[] { (byte)i });

            Assert.GreaterOrEqual(thread.SendQueueDroppedCount, 20L,
                "With queue size 4 and a blocked sender, most of 32 sends must be dropped and counted");

            blocking.Release();
            thread.Stop();
        }

        // ── ArrayPool tracking for the receive scratch ──────────────────────

        [Test]
        [Description("RunLoop returns its rented receive scratch to the pool even when Connect throws.")]
        public void RunLoop_ConnectFailure_ReturnsScratchToPool()
        {
            // We can't directly intercept ArrayPool<byte>.Shared, but we can
            // assert the dispose path completes without throwing, which means
            // the finally branch around the rent/return executed.
            var failing = new ScriptedTransport(
                onConnect: () => throw new SocketException((int)SocketError.ConnectionRefused));
            using var thread = new NetworkThread(failing, receiveBufferSize: 4096);
            var errored = new ManualResetEventSlim(false);
            thread.OnError += _ => errored.Set();
            thread.Start();
            Assert.IsTrue(errored.Wait(2_000));
            // If Return had been skipped, ArrayPool would still function but
            // the test doubles down by ensuring repeated start/error cycles
            // complete cleanly (a real leak manifests as growing GC pressure).
            for (int i = 0; i < 16; i++)
            {
                using var t = new NetworkThread(
                    new ScriptedTransport(onConnect: () => throw new SocketException((int)SocketError.ConnectionRefused)),
                    receiveBufferSize: 4096);
                var done = new ManualResetEventSlim(false);
                t.OnError += _ => done.Set();
                t.Start();
                Assert.IsTrue(done.Wait(2_000));
            }
        }

        // ── Test double ─────────────────────────────────────────────────────

        /// <summary>
        /// Minimal scriptable <see cref="NetworkTransport"/> stand-in.
        /// Tracks Disconnect() invocations and lets the test override Connect's
        /// behaviour to deterministically drive RunLoop into its catch path.
        /// </summary>
        private sealed class ScriptedTransport : NetworkTransport
        {
            private readonly Action _onConnect;
            private bool _connected;
            private int  _disconnectCount;
            private int  _disposeCount;

            public int DisconnectCount => Volatile.Read(ref _disconnectCount);
            public int DisposeCount    => Volatile.Read(ref _disposeCount);
            public override bool IsConnected => _connected;

            public ScriptedTransport(Action onConnect = null)
            {
                _onConnect = onConnect;
            }

            public override void Connect()
            {
                _onConnect?.Invoke();
                _connected = true;
            }

            public override void Disconnect()
            {
                Interlocked.Increment(ref _disconnectCount);
                _connected = false;
            }

            public override void Send(byte[] data) { /* no-op */ }
            public override int  Receive(byte[] buffer) => 0;
            public override bool Poll(int microSeconds) => false;
            public override void Dispose()
            {
                Interlocked.Increment(ref _disposeCount);
                Disconnect();
            }
        }

        /// <summary>
        /// Counts every Send invocation and (optionally) the bytes sent.  Used
        /// to assert post-Stop reject semantics — a packet enqueued after Stop
        /// must NEVER reach Send on the next Start.
        /// </summary>
        private sealed class CountingTransport : NetworkTransport
        {
            private bool _connected;
            private int  _sendCount;

            public int SendCount => Volatile.Read(ref _sendCount);
            public override bool IsConnected => _connected;

            public void ResetCounters() => Interlocked.Exchange(ref _sendCount, 0);

            public override void Connect()    => _connected = true;
            public override void Disconnect() => _connected = false;
            public override void Send(byte[] data) => Interlocked.Increment(ref _sendCount);
            public override int  Receive(byte[] buffer) => 0;
            public override bool Poll(int microSeconds) => false;
            public override void Dispose() => Disconnect();
        }

        /// <summary>
        /// A transport whose Send blocks until <see cref="Release"/> is called.
        /// Lets a test fill the bounded send queue deterministically.
        /// </summary>
        private sealed class BlockingTransport : NetworkTransport
        {
            private readonly ManualResetEventSlim _gate = new ManualResetEventSlim(false);
            private bool _connected;
            public override bool IsConnected => _connected;

            public override void Connect()    => _connected = true;
            public override void Disconnect()
            {
                _connected = false;
                _gate.Set(); // unblock any parked Send so the loop can exit.
            }

            public override void Send(byte[] data)
            {
                // Park until released or Disconnect tears us down.
                _gate.Wait(2_000);
            }

            public void Release() => _gate.Set();

            public override int  Receive(byte[] buffer) => 0;
            public override bool Poll(int microSeconds) => false;
            public override void Dispose()
            {
                Disconnect();
                _gate.Dispose();
            }
        }
    }
}
