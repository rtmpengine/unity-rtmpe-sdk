// RTMPE SDK — Runtime/Core/Rpc/RpcReplayBuffer.cs
//
// Security-critical, order-preserving buffer for the late-join RPC catch-up
// path.  Two FIFOs are drained behind a single ordering barrier:
//
//   • _buffered — historical ("catch-up") RPCs decoded from an inbound
//     RpcBufferReplay frame, in server-emitted order.  Always drained first.
//   • _pending  — live Enhanced-RPC payloads that arrive while the barrier
//     is raised, in arrival order.  Drained only once _buffered is empty.
//
// Draining strictly buffered-before-live preserves the invariant that an
// older historical handler can never overwrite the state mutation of a newer
// live RPC from the same delivery window (the classic re-entrant-dispatch
// failure mode).
//
// Bounded, resumable drain:
//   The drain is split across main-thread pumps by a wall-clock budget
//   (see DrainBounded).  A single RpcBufferReplay can legitimately carry
//   hundreds of catch-up events, and each event invokes arbitrary game
//   [RtmpeRpc] code; dispatching them all synchronously would let a peer that
//   pre-loaded the room's buffer freeze every late-joiner's main thread.  The
//   budget caps per-pump work while the ordering barrier stays raised across
//   frames, so the remainder is dispatched on the next pump in the same order.
//
// Threading model:
//   • _replayInProgress is the ordering barrier AND the RpcBufferReplay
//     re-entry guard.  Raised via TryEnterDrain (CAS 0→1) when a frame begins
//     decoding and lowered only when BOTH queues are fully drained — which may
//     be several pumps later.  Live RPC producers gate on the Volatile.Read in
//     IsReplayInProgress so the read sees the producer-visible value without a
//     lock.
//   • _draining is the DrainBounded re-entry guard: a dispatched handler that
//     synchronously pumps again must not start a nested drain (it would
//     dequeue ahead of the outer loop and break FIFO); the nested call is a
//     no-op and the outer loop picks up whatever the handler enqueued.
//   • _buffered, _pending and the running byte counter are touched only on the
//     Unity main thread (the dispatcher hop guarantees this), so they need no
//     synchronisation: data-race safety for the queues rests on main-thread
//     confinement alone. The atomics above are the ordering barrier and the
//     re-entry guard — they sequence producer (live) and consumer (drain)
//     access on that single thread, not a cross-thread mutex.
//   • _droppedCount is mutated via Interlocked.Increment so a future
//     cross-thread caller does not need to revisit the contract.
//
// Capacity caps (live queue only — buffered is server-capped upstream by the
// frame's event_count ceiling and the transport packet-size limit):
//   • MaxPendingDuringReplay = 4096   — slot count
//   • MaxPayloadBytes        = 64 KiB — per-payload size cap
//   • MaxCumulativeBytes     =  4 MiB — running total across the queue
//
// Constants surface:
//   MaxPendingDuringReplay, MaxPayloadBytes, MaxCumulativeBytes are the
//   authoritative definitions. NetworkManager re-exports them as `internal
//   const` passthroughs for test access.

using System;
using System.Collections.Generic;
using System.Threading;

namespace RTMPE.Core.Rpc
{
    internal sealed class RpcReplayBuffer
    {
        public const int MaxPendingDuringReplay = 4096;
        public const int MaxPayloadBytes        = 64 * 1024;
        public const int MaxCumulativeBytes     = 4 * 1024 * 1024;

        // Ordering barrier + RpcBufferReplay re-entry guard. 0 = idle,
        // 1 = catch-up replay outstanding (buffered and/or pending queued).
        private int _replayInProgress;

        // DrainBounded re-entry guard. 0 = idle, 1 = a pump is on the stack.
        private int _draining;

        // Historical catch-up RPCs decoded from the replay frame, in
        // server-emitted order. Lazy-allocated; drained before _pending.
        private Queue<byte[]> _buffered;

        // FIFO of live payloads received while the barrier is raised.
        // Lazy-allocated on first enqueue so a client that never sees an
        // RpcBufferReplay packet does not pay for the queue allocation.
        private Queue<byte[]> _pending;

        // Running byte total of the payloads currently in _pending.
        // Maintained on every enqueue / dequeue so the cumulative-bytes
        // check is O(1).
        private int _pendingBytes;

        // Monotonic counter of live RPC payloads dropped at enqueue time
        // because of one of the size / count caps. Cross-thread atomic.
        private long _droppedCount;

        // ── Ordering barrier ────────────────────────────────────────────
        /// <summary>
        /// True while a catch-up replay is outstanding. Live RPC producers
        /// MUST gate on this before dispatching directly — when true, defer
        /// the payload via <see cref="TryEnqueue"/> so it drains after the
        /// historical (buffered) events in arrival order. The barrier stays
        /// raised across main-thread pumps until both queues are empty.
        /// </summary>
        public bool IsReplayInProgress => Volatile.Read(ref _replayInProgress) != 0;

        /// <summary>
        /// Raise the ordering barrier for a newly-arrived RpcBufferReplay
        /// frame. Returns <see langword="true"/> for the first caller;
        /// <see langword="false"/> while a prior replay is still draining
        /// (a duplicate frame would otherwise dispatch each catch-up RPC
        /// twice). Paired with the barrier being lowered by the final
        /// fully-drained <see cref="DrainBounded"/> pump.
        /// </summary>
        public bool TryEnterDrain()
            => Interlocked.CompareExchange(ref _replayInProgress, 1, 0) == 0;

        /// <summary>
        /// Lower the ordering barrier unconditionally. Used on the error and
        /// session-teardown paths; the normal path lowers it from
        /// <see cref="DrainBounded"/> once both queues are empty. Uses
        /// Interlocked.Exchange so the producer-side Volatile.Read sees the
        /// update with full release semantics.
        /// </summary>
        public void ExitDrain()
            => Interlocked.Exchange(ref _replayInProgress, 0);

        // ── Queues ──────────────────────────────────────────────────────
        public int BufferedCount => _buffered?.Count ?? 0;
        public int PendingCount  => _pending?.Count ?? 0;

        /// <summary>
        /// True while either queue still holds undelivered work. Equivalent
        /// to the barrier in steady state; exposed separately so callers can
        /// reason about residual work independently of the atomic flag.
        /// </summary>
        public bool HasPendingWork => BufferedCount > 0 || PendingCount > 0;

        /// <summary>Atomic read of the running drop counter for diagnostics.</summary>
        public long DroppedCount => Interlocked.Read(ref _droppedCount);

        /// <summary>
        /// Enqueue a historical catch-up payload decoded from a replay frame.
        /// Buffered events are authoritative and server-capped upstream (by
        /// the frame's event_count ceiling and the transport packet-size
        /// limit), so they are not subject to the live-flood caps below.
        /// </summary>
        public void EnqueueBuffered(byte[] payload)
        {
            if (_buffered == null)
                _buffered = new Queue<byte[]>();
            _buffered.Enqueue(payload);
        }

        /// <summary>
        /// Result of a <see cref="TryEnqueue"/> attempt. Distinct from
        /// just <see langword="bool"/> so the caller can emit the matching
        /// rate-limited diagnostic (per-payload cap vs cumulative cap vs
        /// slot cap).
        /// </summary>
        public enum EnqueueResult
        {
            /// <summary>Enqueued successfully.</summary>
            Ok,
            /// <summary>Single payload exceeds <see cref="MaxPayloadBytes"/>.</summary>
            DroppedPayloadTooLarge,
            /// <summary>Cumulative bytes would exceed <see cref="MaxCumulativeBytes"/>.</summary>
            DroppedCumulativeTooLarge,
            /// <summary>Queue would exceed <see cref="MaxPendingDuringReplay"/> slots.</summary>
            DroppedSlotCapReached,
        }

        /// <summary>
        /// Enqueue a live RPC payload onto the deferred queue. Returns
        /// <see cref="EnqueueResult.Ok"/> on success, or one of the cap
        /// reasons on rejection. Drop counter is incremented atomically
        /// on every rejection.
        /// </summary>
        public EnqueueResult TryEnqueue(byte[] payload)
        {
            if (payload != null && payload.Length > MaxPayloadBytes)
            {
                Interlocked.Increment(ref _droppedCount);
                return EnqueueResult.DroppedPayloadTooLarge;
            }

            int payloadLen = payload != null ? payload.Length : 0;
            if (_pendingBytes + payloadLen > MaxCumulativeBytes)
            {
                Interlocked.Increment(ref _droppedCount);
                return EnqueueResult.DroppedCumulativeTooLarge;
            }

            if (_pending == null)
                _pending = new Queue<byte[]>();

            if (_pending.Count >= MaxPendingDuringReplay)
            {
                Interlocked.Increment(ref _droppedCount);
                return EnqueueResult.DroppedSlotCapReached;
            }

            _pending.Enqueue(payload);
            _pendingBytes += payloadLen;
            return EnqueueResult.Ok;
        }

        /// <summary>
        /// Dequeue the oldest pending (live) payload. Returns
        /// <see langword="false"/> when the queue is empty (or unallocated).
        /// Decrements the running byte counter, clamping at zero so a counter
        /// underflow from a programming error cannot wrap to
        /// <see cref="int.MaxValue"/>.
        /// </summary>
        public bool TryDequeue(out byte[] payload)
        {
            if (_pending == null || _pending.Count == 0)
            {
                payload = null;
                return false;
            }
            payload = DequeuePending();
            return true;
        }

        // Dequeue one live payload and keep the running byte counter coherent.
        // Shared by TryDequeue and the DrainBounded inner loop.
        private byte[] DequeuePending()
        {
            byte[] payload = _pending.Dequeue();
            _pendingBytes -= payload != null ? payload.Length : 0;
            if (_pendingBytes < 0) _pendingBytes = 0;
            return payload;
        }

        /// <summary>
        /// Drain queued catch-up work — historical (buffered) events first,
        /// then live (pending) events — invoking <paramref name="dispatch"/>
        /// per payload, until either queue is exhausted, <paramref name="maxItems"/>
        /// dispatches have run, or the wall-clock budget is spent. Returns the
        /// number of payloads dispatched on this pump.
        ///
        /// Each pump re-samples its start time and checks the budget only after
        /// a dispatch, so at least one item is always delivered per pump before
        /// the budget can trip; a single handler slower than the entire budget
        /// therefore cannot stall the queue. Anything left over stays queued
        /// with the ordering barrier still raised, so the next pump resumes in
        /// the same order. The barrier is lowered only when both queues are empty.
        ///
        /// <paramref name="dispatch"/> MUST contain its own exception handling:
        /// an exception thrown back into this loop aborts the remaining drain.
        /// <paramref name="nowTicks"/> returns a monotonic timestamp in the
        /// same unit as <paramref name="budgetTicks"/> (see
        /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>); a
        /// non-positive budget disables the time bound and drains up to
        /// <paramref name="maxItems"/>.
        /// </summary>
        public int DrainBounded(Action<byte[]> dispatch, int maxItems, long budgetTicks, Func<long> nowTicks)
        {
            if (dispatch == null || nowTicks == null)
                return 0;

            // Refuse a nested pump: a handler invoked below that synchronously
            // pumps again must not dequeue ahead of this loop.
            if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
                return 0;

            int dispatched = 0;
            try
            {
                long start = nowTicks();
                while (dispatched < maxItems)
                {
                    byte[] payload;
                    if (_buffered != null && _buffered.Count > 0)
                    {
                        payload = _buffered.Dequeue();
                    }
                    else if (_pending != null && _pending.Count > 0)
                    {
                        payload = DequeuePending();
                    }
                    else
                    {
                        break; // both queues drained
                    }

                    dispatch(payload);
                    dispatched++;

                    if (budgetTicks > 0 && (nowTicks() - start) >= budgetTicks)
                        break;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
                // Lower the ordering barrier only once nothing remains, so
                // live producers keep deferring across pumps until the very
                // last catch-up payload has been delivered.
                if (!HasPendingWork)
                    ExitDrain();
            }
            return dispatched;
        }

        /// <summary>
        /// Drop both queues and reset the byte counter. Called at the session
        /// boundary (ClearSessionData) so a reconnect does not leak stale
        /// payloads into the new session's RPC stream. Also lowers the
        /// ordering barrier via <see cref="ExitDrain"/> as a defensive safety —
        /// if a previous session was torn down mid-drain, the new session must
        /// start with the barrier idle.
        /// </summary>
        public void Clear()
        {
            _buffered?.Clear();
            _buffered = null;
            _pending?.Clear();
            _pending = null;
            _pendingBytes = 0;
            ExitDrain();
        }
    }
}
