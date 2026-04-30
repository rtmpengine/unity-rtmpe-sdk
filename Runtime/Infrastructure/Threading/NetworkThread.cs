// RTMPE SDK — Runtime/Infrastructure/Threading/NetworkThread.cs
//
// Dedicated background I/O thread for non-blocking UDP send/receive.
//
// Architectural decisions:
//  • Dedicated Thread (not ThreadPool/Task) — gives stable ThreadPriority.AboveNormal
//    scheduling, critical for P99 < 30 ms latency. ThreadPool tasks can be starved
//    when the pool is busy with other work.
//  • Thread.Sleep(1) — yields the OS scheduler each iteration (~1 kHz poll rate).
//    At a 30 Hz server tick rate (33 ms budget) this is more than sufficient. Replace
//    with Thread.SpinWait if sub-millisecond latency is required.
//  • A volatile bool _running acts as the cancellation signal; the thread checks it
//    each iteration. No CancellationToken is used (avoids allocation on hot path).
//  • Per-packet allocation is eliminated by renting receive and send buffers from
//    System.Buffers.ArrayPool<byte>.Shared.  When a subscriber to the rented event
//    is registered we hand the rented buffer through synchronously and return it
//    to the pool the moment the handler returns; otherwise the legacy event is
//    invoked with a freshly-allocated copy (still one fewer copy than before).

using System;
using System.Buffers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using RTMPE.Transport;

namespace RTMPE.Threading
{
    /// <summary>
    /// Dedicated background I/O thread that owns a <see cref="NetworkTransport"/> and
    /// drives the UDP send/receive loop at ~1 kHz.
    ///
    /// All events are raised on this background thread.
    /// Use <see cref="MainThreadDispatcher"/> to marshal them to Unity's main thread.
    /// </summary>
    public sealed class NetworkThread : IDisposable
    {
        // ── Windows timer resolution ───────────────────────────────
        // On Windows, Thread.Sleep(1) uses the system timer interrupt (~15.6 ms
        // default) giving ~64 Hz poll rate instead of the intended ~1 kHz.
        // timeBeginPeriod(1) requests 1 ms resolution for the process lifetime.
        // This is standard practice in multimedia / gaming applications.
        // The companion timeEndPeriod(1) is called in Dispose() to restore the
        // OS default when the network thread is no longer needed.
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);
        private bool _timerResSet;
#endif
        // ── Dependencies ───────────────────────────────────────────────────────
        private readonly NetworkTransport _transport;
        // Receive buffer size cap.  Each receive rents a buffer of this size from
        // ArrayPool and the kernel writes the datagram into it directly — no
        // shared scratch + copy step.
        private readonly int _receiveBufferSize;

        // ── Thread control ─────────────────────────────────────────────────────
        private Thread        _thread;
        private volatile bool _running;
        // Atomic guard for Start() — prevents two concurrent callers from
        // both passing the `if (_running) return` check and spawning duplicate threads.
        // 0 = stopped, 1 = running.  Interlocked.CompareExchange returns the old value;
        // if it was already 1 the caller lost the race and returns immediately.
        private int _startFlag;  // 0 = stopped, 1 = started

        // ── Outbound queue ─────────────────────────────────────────────────────
        // Holds (buffer, length, fromPool).  When fromPool is true the buffer
        // was rented from ArrayPool<byte>.Shared and MUST be returned exactly
        // once after the underlying transport has finished with it.  Reference
        // type byte[] in ConcurrentQueue does not allocate on the hot path
        // beyond the segment node; the struct itself is 16 bytes and fits in
        // ConcurrentQueue<T>'s internal slots without boxing.
        private readonly ThreadSafeQueue<SendItem> _sendQueue = new ThreadSafeQueue<SendItem>();

        private readonly struct SendItem
        {
            public readonly byte[] Buffer;
            public readonly int    Length;
            public readonly bool   FromPool;
            public SendItem(byte[] buffer, int length, bool fromPool)
            {
                Buffer   = buffer;
                Length   = length;
                FromPool = fromPool;
            }
        }

        // Back-pressure guard: drain at most this many packets per loop iteration
        // to avoid starving the receive side under heavy write load.
        private const int MaxSendPerIteration = 100;
        // Cap inbound drain similarly — under burst, leaves time for sends.
        private const int MaxReceivePerIteration = 100;

        // ── Public surface ─────────────────────────────────────────────────────

        /// <summary>True while the background thread is running.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Raised on the network background thread when a datagram is received.
        /// The argument is an exclusively-owned copy of the payload bytes
        /// (length is exactly the datagram size).
        /// Subscribe before calling <see cref="Start"/>.
        ///
        /// Prefer <see cref="OnPacketReceivedRented"/> in hot paths — it elides
        /// the per-packet allocation by handing the receive buffer through
        /// directly.  The legacy event remains supported for callers that
        /// retain the bytes beyond the synchronous handler return.
        /// </summary>
        public event Action<byte[]> OnPacketReceived;

        /// <summary>
        /// Zero-copy inbound delivery.  The handler is invoked synchronously with
        /// a buffer rented from <see cref="ArrayPool{T}.Shared"/>.  The rented
        /// buffer's <c>Length</c> is ≥ <paramref name="length"/> and only the
        /// first <paramref name="length"/> bytes are valid datagram payload.
        ///
        /// The rented buffer is returned to the pool the moment every subscriber
        /// returns.  Callers MUST NOT retain a reference to the array beyond the
        /// duration of the synchronous call — copy out anything they wish to
        /// keep.  Failing this rule yields use-after-return data corruption.
        ///
        /// When at least one rented subscriber is registered the legacy
        /// <see cref="OnPacketReceived"/> event is NOT raised for that packet —
        /// the rented event is the new canonical path.
        /// </summary>
        public event RentedPacketHandler OnPacketReceivedRented;

        /// <summary>
        /// Raised on the network background thread when a non-recoverable
        /// transport error occurs. After this event the thread exits.
        /// </summary>
        public event Action<Exception> OnError;

        /// <summary>
        /// Synchronous handler for <see cref="OnPacketReceivedRented"/>.
        /// </summary>
        /// <param name="buffer">Pool-rented buffer — DO NOT retain past handler return.</param>
        /// <param name="offset">First byte of the datagram payload (always 0 today).</param>
        /// <param name="length">Number of valid bytes starting at <paramref name="offset"/>.</param>
        public delegate void RentedPacketHandler(byte[] buffer, int offset, int length);

        // ── Construction ───────────────────────────────────────────────────────

        /// <param name="transport">Transport to own and operate. Disposed on <see cref="Dispose"/>.</param>
        /// <param name="receiveBufferSize">Per-packet receive buffer size in bytes (default 8 KiB).</param>
        public NetworkThread(NetworkTransport transport, int receiveBufferSize = 8_192)
        {
            if (receiveBufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(receiveBufferSize));

            _transport         = transport ?? throw new ArgumentNullException(nameof(transport));
            _receiveBufferSize = receiveBufferSize;
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Start the dedicated I/O thread. No-op if already running.
        /// Thread-safe: concurrent calls are correctly serialised by an atomic guard.
        /// </summary>
        public void Start()
        {
            // Use Interlocked.CompareExchange as a lock-free atomic guard.
            // The volatile `if (_running) return` check-then-set was not atomic:
            // two threads could both read false and both spawn a thread on the same
            // socket.  CAS atomically sets _startFlag from 0→1; only the thread that
            // observes the old value as 0 proceeds.
            if (System.Threading.Interlocked.CompareExchange(ref _startFlag, 1, 0) != 0)
                return; // already started by this or another caller

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Raise Windows timer resolution to 1 ms so Thread.Sleep(1)
            // actually sleeps ~1 ms instead of the default ~15.6 ms.
            if (!_timerResSet) { timeBeginPeriod(1); _timerResSet = true; }
#endif

            _running = true;
            _thread  = new Thread(RunLoop)
            {
                Name         = "RTMPE-NetworkThread",
                IsBackground = true,   // Does not prevent process exit
                Priority     = ThreadPriority.AboveNormal
            };
            _thread.Start();
        }

        /// <summary>
        /// Signal the thread to stop and wait up to 2 seconds for a clean exit.
        /// Safe to call multiple times. Blocks the calling thread.
        ///
        /// Asymmetry — when invoked from the network thread itself (typically
        /// from inside an <see cref="OnError"/> handler that triggers a
        /// disconnect/reconnect), this method does NOT join.  Calling
        /// <see cref="Thread.Join(int)"/> on the current thread would deadlock
        /// for the full timeout (a self-join can never satisfy itself).
        /// Instead the running flag is cleared and the run loop exits naturally
        /// on its next iteration; cleanup that the caller normally relies on
        /// (transport disconnect, send-queue drain) runs in the loop's
        /// finally block and after RunLoop returns the thread terminates.
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;
            // Reset the atomic flag so Start() can be called again after Stop().
            System.Threading.Interlocked.Exchange(ref _startFlag, 0);

            // Self-join guard.  A consumer that wires OnError → Stop() (the
            // canonical disconnect-on-fatal-error pattern) executes this method
            // on the network thread — Thread.Join on Thread.CurrentThread blocks
            // for the full timeout and never completes.  Detect that case and
            // return early; the run loop observes _running=false on its next
            // poll and exits cleanly through the normal finally path.
            var t = _thread;
            if (t != null && Thread.CurrentThread == t)
            {
                // Do NOT null _thread here — the thread is still alive and
                // running; clearing the field would race a concurrent
                // foreign-thread Stop() that legitimately needs the reference
                // to call Join.  The thread self-references will be released
                // when RunLoop unwinds and a subsequent foreign Stop() (or
                // Dispose) completes the cleanup.
                return;
            }

            if (t != null && t.IsAlive)
            {
                if (!t.Join(2_000))
                {
                    // Thread.Interrupt() only works when the thread
                    // is in a managed blocking state (WaitSleepJoin). When blocked
                    // in a native ReceiveFrom, it has no effect. Closing the socket
                    // forces ReceiveFrom to throw a SocketException, which the
                    // RunLoop catch clause handles gracefully.
                    _transport.Disconnect();
                    t.Join(500);
                }
            }

            _thread = null;

            // Drain the send queue to release any pool-rented buffers that never
            // made it onto the wire.  Skipping this would leak rented arrays
            // permanently from the shared pool's perspective.
            DrainAndReleasePending();
        }

        /// <summary>
        /// Enqueue <paramref name="data"/> for transmission on the next loop iteration.
        /// Thread-safe; returns immediately. The incoming array is copied internally
        /// so the caller can safely reuse or discard its buffer.
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!_running || data == null || data.Length == 0) return;

            // Copy into a pool-rented buffer.  ArrayPool may hand back an array
            // larger than data.Length; we record the exact byte count alongside
            // the buffer so the transport sends only the meaningful prefix.
            var rented = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                Buffer.BlockCopy(data, 0, rented, 0, data.Length);
                _sendQueue.Enqueue(new SendItem(rented, data.Length, fromPool: true));
            }
            catch
            {
                // Enqueue throwing (OOM during segment grow, BlockCopy on a
                // bogus rented buffer) would otherwise leak the rental for
                // the lifetime of the process.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                throw;
            }

            // Stop() races: if the worker shut down between the entry guard
            // and Enqueue, the item lives in the queue with no future
            // drainer.  Re-check and drain the queue ourselves so the
            // rental is returned and the caller's data is not silently
            // lost in a quiet leak.  Drain is idempotent — DrainAndReleasePending
            // is safe to call repeatedly.
            if (!_running) DrainAndReleasePending();
        }

        /// <summary>
        /// Enqueue <paramref name="ownedData"/> for transmission without copying.
        /// The caller MUST NOT read or modify <paramref name="ownedData"/> after
        /// this call — ownership is transferred to the send queue.
        ///
        /// Use this instead of <see cref="Send"/> when <paramref name="ownedData"/>
        /// was freshly allocated (e.g. the return value of
        /// <see cref="RTMPE.Protocol.PacketBuilder.Build"/>) and will not be
        /// reused.  Eliminates the redundant copy that <see cref="Send"/> makes,
        /// halving per-packet GC pressure on the hot data path.
        /// </summary>
        public void SendOwned(byte[] ownedData)
        {
            if (!_running || ownedData == null || ownedData.Length == 0) return;
            // Caller-owned arrays are sent in full; they must not be returned to
            // the pool because they were never rented from it.  Send's
            // try/catch wraps the rented-buffer return contract on
            // Enqueue-throw — there is no symmetric resource to release
            // here (the caller still holds the reference and the GC
            // reclaims it once it goes out of scope), so this path
            // intentionally has no try/catch.
            _sendQueue.Enqueue(new SendItem(ownedData, ownedData.Length, fromPool: false));

            // Stop()-race recovery, symmetric with Send.  Without it, an
            // ownedData byte[] enqueued AFTER the worker drained but BEFORE
            // the entry guard observed the new _running=false is silently
            // lost in the queue (the next Start() would transmit it, possibly
            // violating handshake ordering, or process exit drops it
            // entirely).  DrainAndReleasePending is idempotent and only
            // touches pool-rented items, so non-pooled SendItems passed here
            // are still GC-cleaned by reference loss — but the rented items
            // sharing the queue are returned to the pool either way.
            if (!_running) DrainAndReleasePending();
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
            _transport.Dispose();
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            // Restore the OS default timer resolution.
            if (_timerResSet) { timeEndPeriod(1); _timerResSet = false; }
#endif
        }

        // ── Private I/O loop ───────────────────────────────────────────────────

        private void RunLoop()
        {
            try
            {
                _transport.Connect();

                while (_running)
                {
                    DrainSendQueue();
                    TryReceive();
                    Thread.Sleep(1);   // ~1 kHz poll cadence
                }
            }
            catch (ThreadInterruptedException)
            {
                // Normal: raised by Stop() when Join times out.
                _running = false;
            }
            catch (Exception ex)
            {
                // Reset _running BEFORE invoking OnError so that any reconnect
                // attempt inside the handler can call Start() successfully.
                // Without this reset, _running stays true and the next Start()
                // is a no-op — permanently locking out reconnection.
                _running = false;
                // Also reset the atomic flag so Start() allows re-entry.
                System.Threading.Interlocked.Exchange(ref _startFlag, 0);
                OnError?.Invoke(ex);
            }
            finally
            {
                _transport.Disconnect();
            }
        }

        // Backoff state for ENOBUFS handling.  When the kernel's send buffer
        // is exhausted we cannot make progress until it drains; spinning at
        // ~1 kHz and firing OnError on every iteration would create an error
        // storm visible to the SDK consumer.  Instead we sleep for an
        // exponentially-increasing interval (1 ms → cap) and keep the
        // pending item at the head of the queue by re-enqueuing it.
        private const int EnobufsBackoffStartMs = 1;
        private const int EnobufsBackoffCapMs   = 4;
        private int  _enobufsBackoffMs = EnobufsBackoffStartMs;
        private long _enobufsCount;

        /// <summary>
        /// Total number of ENOBUFS events the send loop has absorbed.
        /// Exposed for telemetry; never resets across the thread's lifetime.
        /// </summary>
        public long EnobufsCount => Interlocked.Read(ref _enobufsCount);

        private void DrainSendQueue()
        {
            for (int i = 0;
                 i < MaxSendPerIteration && _sendQueue.TryDequeue(out var item);
                 i++)
            {
                try
                {
                    // UdpTransport exposes a slice-aware overload that avoids
                    // copying the rented buffer down to its meaningful prefix.
                    if (_transport is UdpTransport udp)
                    {
                        udp.Send(item.Buffer, 0, item.Length);
                    }
                    else
                    {
                        // Fallback for other transports (KCP, mock): if the
                        // rented buffer is exactly the right size we hand it
                        // straight in; otherwise copy down to a temporary
                        // exact-sized array because the abstract Send contract
                        // sends the entire array.
                        if (item.Buffer.Length == item.Length)
                        {
                            _transport.Send(item.Buffer);
                        }
                        else
                        {
                            var exact = new byte[item.Length];
                            Buffer.BlockCopy(item.Buffer, 0, exact, 0, item.Length);
                            _transport.Send(exact);
                        }
                    }
                    // Successful send — reset the ENOBUFS backoff so the next
                    // exhaustion event starts at the minimum sleep again.
                    _enobufsBackoffMs = EnobufsBackoffStartMs;
                }
                catch (SocketException sx)
                    when (sx.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
                {
                    // Kernel send buffer full.  This is transient: stop the
                    // current drain pass, sleep with exponential backoff,
                    // and re-enqueue the unsent item so it is retried on
                    // the next iteration.  Re-enqueue is to the tail (the
                    // backing ConcurrentQueue exposes no head-insertion
                    // primitive); under saturation any subsequent items
                    // already enqueued ahead are equally blocked, so the
                    // tail-reorder is bounded by MaxSendPerIteration and
                    // not observable in practice.  Crucially, do NOT raise
                    // OnError — at 1 kHz poll cadence that would generate
                    // up to a thousand error callbacks per second under
                    // sustained uplink saturation.
                    Interlocked.Increment(ref _enobufsCount);
                    _sendQueue.Enqueue(item);
                    int sleep = _enobufsBackoffMs;
                    _enobufsBackoffMs = Math.Min(_enobufsBackoffMs * 2, EnobufsBackoffCapMs);
                    Thread.Sleep(sleep);
                    return;
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                    if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
                    break;  // Stop draining on transport error; let RunLoop handle recovery
                }

                if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
            }
        }

        // Drain any send items that remain queued at shutdown, returning rented
        // buffers to the pool.  Without this the pool sees the rentals leaked.
        private void DrainAndReleasePending()
        {
            while (_sendQueue.TryDequeue(out var item))
            {
                if (item.FromPool) ArrayPool<byte>.Shared.Return(item.Buffer, clearArray: true);
            }
        }

        private void TryReceive()
        {
            // Drain all available datagrams each iteration instead of
            // consuming only one.  At 30 Hz with 16 players up to 16 packets can
            // arrive within a single 33 ms window.  Reading only one per 1 ms cycle
            // adds up to 15 ms of queuing latency for late-arriving packets.
            //
            // Each receive rents a fresh buffer from ArrayPool — no shared
            // scratch + copy step.  The buffer is returned to the pool the
            // moment the synchronous subscriber chain returns.
            try
            {
                for (int i = 0; i < MaxReceivePerIteration; i++)
                {
                    if (!_transport.Poll(0)) break; // no more data

                    var rented = ArrayPool<byte>.Shared.Rent(_receiveBufferSize);
                    int n;
                    try
                    {
                        n = _transport.Receive(rented);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        throw;
                    }

                    if (n == 0)
                    {
                        // Would-block / socket disposed mid-syscall.  Stop
                        // the drain pass; RunLoop polls again on the next
                        // iteration after a 1 ms sleep.
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        break;
                    }

                    if (n < 0)
                    {
                        // UdpTransport.ReceiveSourceRejected: a datagram was
                        // consumed and dropped because the source endpoint
                        // failed pinning.  More datagrams may be queued —
                        // continue draining in this iteration so an
                        // off-path flood does not add per-burst latency to
                        // legitimate responses.  The inner loop is bounded
                        // by MaxReceivePerIteration, so a kernel that ever
                        // (incorrectly) reports readiness without data
                        // cannot pin the CPU.
                        ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                        continue;
                    }

                    DispatchReceived(rented, n);
                }
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }

        // Hand the just-received datagram to subscribers, then return the
        // rented buffer to the shared pool exactly once.  The try/finally
        // guarantees return even if a subscriber throws.
        private void DispatchReceived(byte[] rented, int length)
        {
            // Snapshot delegates once — Action invocation is not racy with
            // concurrent subscribe/unsubscribe but reading the field twice
            // could observe different values.
            var rentedHandler = OnPacketReceivedRented;
            var legacyHandler = OnPacketReceived;

            try
            {
                if (rentedHandler != null)
                {
                    // Zero-copy delivery: one buffer, one synchronous call.
                    // After all subscribers return the buffer goes back to
                    // the pool and is reused on the next receive.
                    //
                    // Per-subscriber isolation: a buggy integrator subscriber
                    // that throws would otherwise (a) prevent every later
                    // subscriber from observing the packet for this datagram
                    // and (b) propagate out of DispatchReceived to TryReceive's
                    // outer catch, which fires OnError and tears down the
                    // receive loop.  Walk the invocation list explicitly so
                    // each subscriber's exception is caught and logged without
                    // affecting siblings.  Same discipline as M19-SYNC-01 and
                    // M19-CORE-07.
                    InvokeRentedSubscribers(rentedHandler, rented, length);
                }
                else if (legacyHandler != null)
                {
                    // Legacy contract guarantees an exclusively-owned array
                    // sized exactly to the datagram length.  This path still
                    // saves one copy vs. the original "shared scratch + new
                    // byte[n]" implementation: kernel writes directly into
                    // `rented` and we copy out once into the exact-sized array.
                    var packet = new byte[length];
                    Buffer.BlockCopy(rented, 0, packet, 0, length);
                    InvokeLegacySubscribers(legacyHandler, packet);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        private static void InvokeRentedSubscribers(
            RentedPacketHandler handler, byte[] rented, int length)
        {
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    ((RentedPacketHandler)subs[i])(rented, 0, length);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        "[RTMPE] NetworkThread: rented-packet subscriber threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                        "remaining subscribers.");
                }
            }
        }

        private static void InvokeLegacySubscribers(Action<byte[]> handler, byte[] packet)
        {
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try
                {
                    ((Action<byte[]>)subs[i])(packet);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        "[RTMPE] NetworkThread: legacy packet subscriber threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                        "remaining subscribers.");
                }
            }
        }
    }
}
