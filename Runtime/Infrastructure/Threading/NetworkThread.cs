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
//    with Thread.SpinWait in Week 10 if sub-millisecond latency is required.
//  • Received bytes are immediately copied to an exclusive byte[] before raising
//    OnPacketReceived, so the shared _receiveBuffer is free for the next read.
//  • Outbound data is also copied in Send() so the caller can reuse its buffer.
//  • A volatile bool _running acts as the cancellation signal; the thread checks it
//    each iteration. No CancellationToken is used (avoids allocation on hot path).

using System;
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
        // ── Dependencies ───────────────────────────────────────────────────────
        private readonly NetworkTransport _transport;
        private readonly byte[]           _receiveBuffer;   // shared scratch; never exposed

        // ── Thread control ─────────────────────────────────────────────────────
        private Thread        _thread;
        private volatile bool _running;

        // ── Outbound queue ─────────────────────────────────────────────────────
        private readonly ThreadSafeQueue<byte[]> _sendQueue = new ThreadSafeQueue<byte[]>();

        // Back-pressure guard: drain at most this many packets per loop iteration
        // to avoid starving the receive side under heavy write load.
        private const int MaxSendPerIteration = 100;

        // ── Public surface ─────────────────────────────────────────────────────

        /// <summary>True while the background thread is running.</summary>
        public bool IsRunning => _running;

        /// <summary>
        /// Raised on the network background thread when a datagram is received.
        /// The argument is an exclusively-owned copy of the payload bytes.
        /// Subscribe before calling <see cref="Start"/>.
        /// </summary>
        public event Action<byte[]> OnPacketReceived;

        /// <summary>
        /// Raised on the network background thread when a non-recoverable
        /// transport error occurs. After this event the thread exits.
        /// </summary>
        public event Action<Exception> OnError;

        // ── Construction ───────────────────────────────────────────────────────

        /// <param name="transport">Transport to own and operate. Disposed on <see cref="Dispose"/>.</param>
        /// <param name="receiveBufferSize">Scratch buffer size in bytes (default 8 KiB).</param>
        public NetworkThread(NetworkTransport transport, int receiveBufferSize = 8_192)
        {
            _transport     = transport ?? throw new ArgumentNullException(nameof(transport));
            _receiveBuffer = new byte[receiveBufferSize];
        }

        // ── Lifecycle ──────────────────────────────────────────────────────────

        /// <summary>
        /// Start the dedicated I/O thread. No-op if already running.
        /// </summary>
        public void Start()
        {
            if (_running) return;

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
        /// </summary>
        public void Stop()
        {
            if (!_running) return;

            _running = false;

            if (_thread != null && _thread.IsAlive)
            {
                if (!_thread.Join(2_000))
                    _thread.Interrupt();  // Force exit if Join timed out
            }

            _thread = null;
        }

        /// <summary>
        /// Enqueue <paramref name="data"/> for transmission on the next loop iteration.
        /// Thread-safe; returns immediately. The incoming array is copied internally.
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!_running || data == null || data.Length == 0) return;

            // Copy so the caller can safely reuse or discard its buffer.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            _sendQueue.Enqueue(copy);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Stop();
            _transport.Dispose();
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
                OnError?.Invoke(ex);
            }
            finally
            {
                _transport.Disconnect();
            }
        }

        private void DrainSendQueue()
        {
            for (int i = 0;
                 i < MaxSendPerIteration && _sendQueue.TryDequeue(out var packet);
                 i++)
            {
                try
                {
                    _transport.Send(packet);
                }
                catch (Exception ex)
                {
                    OnError?.Invoke(ex);
                    break;  // Stop draining on transport error; let RunLoop handle recovery
                }
            }
        }

        private void TryReceive()
        {
            try
            {
                if (!_transport.Poll(0)) return;

                int n = _transport.Receive(_receiveBuffer);
                if (n <= 0) return;

                // Copy to an exclusive array before raising the event — the shared
                // _receiveBuffer is immediately recycled for the next read.
                var packet = new byte[n];
                Buffer.BlockCopy(_receiveBuffer, 0, packet, 0, n);
                OnPacketReceived?.Invoke(packet);
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }
        }
    }
}
