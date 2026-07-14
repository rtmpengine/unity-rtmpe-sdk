// RTMPE SDK — Runtime/Core/Diagnostics/DiagnosticsUplink.cs
//
// Captures Unity log output and streams it to the gateway as Diagnostics
// (0x0C) packets so an operator can watch a developer's client-side errors in
// the gateway journal during testing. Default-off (NetworkSettings); enabled
// only for a controlled test window.
//
// Threading: subscribes to Application.logMessageReceivedThreaded — NOT
// logMessageReceived — because the SDK logs the diagnostics that matter most
// (transport errors, dropped packets) from its background I/O thread, which the
// main-thread-only event never delivers. The threaded callback can run on any
// thread concurrently, so it touches only the concurrent capture queue and the
// [ThreadStatic] re-entrancy guard. All framing and sending happens on the main
// thread — in Tick(), and in the best-effort flush on Stop(), which the SDK only
// calls from main-thread teardown — so the batcher stays single-threaded.

using System;
using UnityEngine;
using RTMPE.Protocol;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Main-thread-driven uplink that batches captured Unity logs and sends
    /// them as best-effort Diagnostics packets. One instance per established
    /// session; <see cref="Stop"/> unsubscribes the log hook.
    /// </summary>
    internal sealed class DiagnosticsUplink
    {
        // Per-entry truncation caps (UTF-8 bytes). Chosen so a single worst-case
        // entry (header + caps) fits well inside MaxApplicationPayloadBytes.
        private const int MaxMsgBytes = 512;
        private const int MaxStackBytes = 512;

        // Upper bound on un-drained captures so a crash storm cannot grow memory
        // without bound before the next Tick drains the queue; oldest is dropped.
        private const int MaxQueuedEntries = 1024;

        // Hard ceiling on entries packed per packet, regardless of the configured
        // value. Matches the gateway's DEFAULT cap (GATEWAY_SDK_DIAGNOSTICS_MAX_ENTRIES)
        // and the NetworkSettings field [Range] so a code-set value cannot exceed
        // what a default gateway accepts — a higher count would be rejected
        // whole-batch as DiagError::TooManyEntries, silently losing diagnostics.
        private const int MaxEntriesPerPacketCap = 50;

        // Minimum spacing for the prompt release of a crash-grade entry. It floors
        // the per-flush rate during an Error storm (bounded with MaxPacketsPerInterval)
        // while still delivering within a frame or two — fast enough to escape a
        // connection that is about to drop, which is the moment those entries
        // describe.
        private const int PromptFlushSpacingMs = 250;

        // Set while the uplink is sending on the main thread, so a log emitted
        // synchronously by the send path (or by this class) on that thread is not
        // re-captured. [ThreadStatic] suppresses only SAME-THREAD recapture: a
        // transport error the background network thread logs *later* about a sent
        // packet is still captured on the next Tick — that is desired (it is a real
        // error) and is bounded by the flush throttle + MaxQueuedEntries, so it
        // cannot amplify into a feedback storm.
        [ThreadStatic] private static bool _inUplink;

        private readonly PacketBuilder _builder;
        private readonly bool _captureWarnings;
        private readonly int _maxPacketsPerInterval;
        private readonly int _flushIntervalMs;
        private readonly DiagnosticsBatcher _batcher;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Captured> _queue =
            new System.Collections.Concurrent.ConcurrentQueue<Captured>();
        private readonly System.Diagnostics.Stopwatch _clock =
            System.Diagnostics.Stopwatch.StartNew();

        private int _queuedApprox;
        private long _lastFlushMs;
        private bool _started;

        // Raised on the capture thread when a crash-grade entry is buffered, so the
        // next main-thread Tick releases it promptly instead of on the routine
        // interval. Volatile: written off the main thread, read/cleared on it.
        private volatile bool _highSeverityPending;

        // The wire send delegate, cached from Tick so a teardown flush can run
        // without it being threaded through Stop().
        private Action<byte[]> _cachedSend;

        // Pre-session capture: logs collected between Connect() and SessionAck.
        // The buffer is drained into _queue by Start() so handshake errors are
        // included in the first post-session flush.
        private readonly DiagnosticsPreSessionBuffer _preSession =
            new DiagnosticsPreSessionBuffer();
        private volatile bool _preSessionCapturing;

        private readonly struct Captured
        {
            public readonly byte Level;
            public readonly uint TsMs;
            public readonly string Msg;
            public readonly string Stack;

            public Captured(byte level, uint tsMs, string msg, string stack)
            {
                Level = level;
                TsMs = tsMs;
                Msg = msg;
                Stack = stack;
            }
        }

        public DiagnosticsUplink(NetworkSettings settings, PacketBuilder builder)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            _builder = builder ?? throw new ArgumentNullException(nameof(builder));
            _captureWarnings = settings.diagnosticsCaptureWarnings;
            _maxPacketsPerInterval = Math.Max(1, settings.diagnosticsMaxPacketsPerInterval);
            _flushIntervalMs = Math.Max(250, settings.diagnosticsFlushIntervalMs);
            _batcher = new DiagnosticsBatcher(
                Math.Min(Math.Max(settings.diagnosticsMaxEntriesPerPacket, 1), MaxEntriesPerPacketCap),
                PacketBuilder.MaxApplicationPayloadBytes,
                MaxMsgBytes,
                MaxStackBytes);
        }

        /// <summary>
        /// Begin capturing log entries into the pre-session buffer before the
        /// network session is established.  Buffered entries are promoted to
        /// the main queue when <see cref="Start"/> is called on SessionAck, so
        /// handshake errors are included in the first post-session flush.
        /// </summary>
        internal void StartPreSessionCapture()
        {
            if (_preSessionCapturing) return;
            _preSessionCapturing = true;
            Application.logMessageReceivedThreaded += OnPreSessionLogThreaded;
        }

        /// <summary>
        /// Transition from pre-session capture to normal post-session capture.
        /// If pre-session capture was active its buffered entries are drained
        /// into the main queue first; the pre-session hook is unsubscribed
        /// before subscribing the normal hook so no entry is double-counted.
        /// </summary>
        public void Start()
        {
            if (_preSessionCapturing)
            {
                _preSessionCapturing = false;
                Application.logMessageReceivedThreaded -= OnPreSessionLogThreaded;
                // Promote pre-session entries into the main queue, respecting the
                // main-queue capacity so a pre-session crash storm cannot overflow it.
                _preSession.DrainInto((level, ts, msg, stack) =>
                {
                    if (System.Threading.Volatile.Read(ref _queuedApprox) < MaxQueuedEntries)
                    {
                        _queue.Enqueue(new Captured(level, ts, msg, stack));
                        System.Threading.Interlocked.Increment(ref _queuedApprox);
                    }
                });
            }
            if (_started) return;
            _started = true;
            _lastFlushMs = _clock.ElapsedMilliseconds;
            Application.logMessageReceivedThreaded += OnLogThreaded;
        }

        /// <summary>
        /// Unsubscribe all log hooks.  Handles both the normal post-session hook
        /// and any still-active pre-session hook.  Idempotent and safe to call
        /// from any teardown path; must run before the instance is replaced so
        /// a reconnect never leaks a subscriber.
        /// </summary>
        public void Stop()
        {
            // Unsubscribe the pre-session hook if the connection failed before
            // SessionAck.  Clear the buffer so stale entries are not carried
            // into a future attempt's uplink.
            if (_preSessionCapturing)
            {
                _preSessionCapturing = false;
                Application.logMessageReceivedThreaded -= OnPreSessionLogThreaded;
                _preSession.Clear();
            }
            if (!_started) return;
            _started = false;
            // Unsubscribe before the final flush so the flush's own send-path logs
            // are not recaptured.
            Application.logMessageReceivedThreaded -= OnLogThreaded;
            FlushRemaining();
        }

        // Fires on ANY thread; keep it allocation-light and lock-free.
        private void OnLogThreaded(string condition, string stackTrace, LogType type)
        {
            if (_inUplink) return;                 // never capture our own send-path logs
            if (!ShouldCapture(type)) return;

            if (System.Threading.Volatile.Read(ref _queuedApprox) >= MaxQueuedEntries)
            {
                // Drop-oldest to bound memory during a crash storm.
                if (_queue.TryDequeue(out _))
                    System.Threading.Interlocked.Decrement(ref _queuedApprox);
            }

            _queue.Enqueue(new Captured(
                WireLevel(type),
                (uint)_clock.ElapsedMilliseconds,
                condition,
                stackTrace));
            System.Threading.Interlocked.Increment(ref _queuedApprox);
            if (IsHighSeverity(type)) _highSeverityPending = true;
        }

        // Pre-session capture callback — routes to the bounded pre-session
        // buffer rather than the main queue.  Fires on ANY thread.
        private void OnPreSessionLogThreaded(string condition, string stackTrace, LogType type)
        {
            if (_inUplink) return;
            if (!ShouldCapture(type)) return;
            _preSession.Enqueue(WireLevel(type), (uint)_clock.ElapsedMilliseconds, condition, stackTrace);
            if (IsHighSeverity(type)) _highSeverityPending = true;
        }

        /// <summary>
        /// Main-thread tick: drain captures into the batcher and, when the flush
        /// policy allows, send up to <c>maxPacketsPerInterval</c> packets — crash-grade
        /// entries promptly, routine ones on the interval. Cheap when nothing is pending.
        /// </summary>
        public void Tick(Action<byte[]> sendCallback)
        {
            if (!_started || sendCallback == null) return;
            _cachedSend = sendCallback;

            DrainQueueIntoBatcher();

            long now = _clock.ElapsedMilliseconds;
            if (_batcher.PendingCount == 0)
            {
                // Keep the clock fresh while idle so the next entry's interval is
                // measured from now, not from the last send.
                _lastFlushMs = now;
                return;
            }

            if (!DiagnosticsFlushPolicy.ShouldFlush(
                    _batcher.PendingCount,
                    _highSeverityPending,
                    now - _lastFlushMs,
                    _flushIntervalMs,
                    PromptFlushSpacingMs))
            {
                return;
            }

            _lastFlushMs = now;
            _highSeverityPending = false;
            SendBatched(sendCallback);
        }

        // Move every queued capture into the batcher (main thread only).
        private void DrainQueueIntoBatcher()
        {
            while (_queue.TryDequeue(out Captured c))
            {
                System.Threading.Interlocked.Decrement(ref _queuedApprox);
                _batcher.Add(c.Level, c.TsMs, c.Msg, c.Stack);
            }
        }

        // Send up to the per-flush packet cap. The re-entrancy guard suppresses
        // recapture of the send path's own logs; any failure is swallowed so a
        // diagnostics send never surfaces as an app-visible fault.
        private void SendBatched(Action<byte[]> sendCallback)
        {
            int sent = 0;
            _inUplink = true;
            try
            {
                while (sent < _maxPacketsPerInterval
                       && _batcher.TryDrainPacket(out byte[] payload))
                {
                    sendCallback(_builder.BuildDiagnostics(payload));
                    sent++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] diagnostics uplink send failed: {ex.Message}");
            }
            finally
            {
                _inUplink = false;
            }
        }

        // Best-effort delivery of buffered diagnostics on teardown, so a clean
        // disconnect does not silently discard them. The send is downstream-guarded
        // (a no-op once the network thread is stopped), so on a connection-loss
        // teardown — where the wire is already gone — it neither delivers nor harms.
        private void FlushRemaining()
        {
            Action<byte[]> send = _cachedSend;
            if (send == null) return; // stopped before the first Tick cached the channel
            DrainQueueIntoBatcher();
            if (_batcher.PendingCount == 0) return;
            SendBatched(send);
        }

        private bool ShouldCapture(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                case LogType.Assert:
                    return true;
                case LogType.Warning:
                    return _captureWarnings;
                default:                       // LogType.Log — too noisy to ship
                    return false;
            }
        }

        // Crash-grade levels worth releasing ahead of the routine interval.
        private static bool IsHighSeverity(LogType type)
            => type == LogType.Error || type == LogType.Exception || type == LogType.Assert;

        // Map Unity's LogType (whose ordinals are NOT severity-ordered) to the
        // wire level scheme the gateway labels: 0=log 1=warning 2=error
        // 3=exception 4=assert.
        private static byte WireLevel(LogType type)
        {
            switch (type)
            {
                case LogType.Warning: return 1;
                case LogType.Error: return 2;
                case LogType.Exception: return 3;
                case LogType.Assert: return 4;
                default: return 0;             // LogType.Log
            }
        }
    }
}
