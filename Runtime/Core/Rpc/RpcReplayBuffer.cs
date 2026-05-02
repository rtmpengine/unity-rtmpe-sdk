// RTMPE SDK — Runtime/Core/Rpc/RpcReplayBuffer.cs
//
// Security-critical FIFO buffer that defers live Enhanced-RPC payloads
// arriving during an in-progress RpcBufferReplay drain so the buffered
// (historical) RPCs run in server-emitted order BEFORE any live RPC from
// the same delivery window. Without this gate, a live RPC's state
// mutation could be overwritten by an older buffered handler — the
// classic re-entrant-dispatch failure mode.
//
// Threading model:
//   • _replayInProgress is the re-entry guard. The replay path enters via
//     Interlocked.CompareExchange(0 → 1) and exits via Interlocked.Exchange(0)
//     in a finally block. Live RPC producers gate on Volatile.Read so the
//     read sees the producer-visible value without taking a lock.
//   • _pendingLiveRpcs and the running byte counter are touched only on
//     the Unity main thread (the dispatcher hop ensures this); they do
//     not require synchronisation. The CAS guard above is what keeps
//     producer (live) and consumer (drain) writes from interleaving.
//   • _droppedCount is mutated via Interlocked.Increment so a future
//     cross-thread caller does not need to revisit the contract.
//
// Capacity caps:
//   • MaxPendingDuringReplay = 4096   — slot count
//   • MaxPayloadBytes        = 64 KiB — per-payload size cap
//   • MaxCumulativeBytes     =  4 MiB — running total across the queue
//
// Constants surface:
//   MaxPendingDuringReplay, MaxPayloadBytes, MaxCumulativeBytes are the
//   authoritative definitions. NetworkManager re-exports them as `internal
//   const` passthroughs for test access.

using System.Collections.Generic;
using System.Threading;

namespace RTMPE.Core.Rpc
{
    internal sealed class RpcReplayBuffer
    {
        public const int MaxPendingDuringReplay = 4096;
        public const int MaxPayloadBytes        = 64 * 1024;
        public const int MaxCumulativeBytes     = 4 * 1024 * 1024;

        // Re-entry guard. 0 = idle, 1 = drain in progress.
        private int _replayInProgress;

        // FIFO of live payloads received while a drain is in progress.
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

        // ── Re-entry guard ──────────────────────────────────────────────
        /// <summary>
        /// True iff a buffer-replay drain is currently in progress. Live
        /// RPC producers MUST gate on this before dispatching directly —
        /// when true, defer the payload via <see cref="TryEnqueue"/>.
        /// </summary>
        public bool IsReplayInProgress => Volatile.Read(ref _replayInProgress) != 0;

        /// <summary>
        /// Attempt to enter the drain. Returns <see langword="true"/> on
        /// the first concurrent caller; <see langword="false"/> for any
        /// subsequent reentry attempt (a duplicate replay frame from a
        /// network thread retry would otherwise dispatch each catch-up
        /// RPC twice).
        /// </summary>
        public bool TryEnterDrain()
            => Interlocked.CompareExchange(ref _replayInProgress, 1, 0) == 0;

        /// <summary>
        /// Release the re-entry guard. Always paired with a successful
        /// <see cref="TryEnterDrain"/> in a try/finally block. Uses
        /// Interlocked.Exchange so the producer-side Volatile.Read sees
        /// the update with full release semantics.
        /// </summary>
        public void ExitDrain()
            => Interlocked.Exchange(ref _replayInProgress, 0);

        // ── Pending queue ──────────────────────────────────────────────
        public int PendingCount => _pending?.Count ?? 0;

        /// <summary>Atomic read of the running drop counter for diagnostics.</summary>
        public long DroppedCount => Interlocked.Read(ref _droppedCount);

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
        /// Dequeue the oldest pending payload. Returns <see langword="false"/>
        /// when the queue is empty (or unallocated).  Decrements the
        /// running byte counter, clamping at zero so a counter underflow
        /// from a programming error cannot wrap to <see cref="int.MaxValue"/>.
        /// </summary>
        public bool TryDequeue(out byte[] payload)
        {
            if (_pending == null || _pending.Count == 0)
            {
                payload = null;
                return false;
            }
            payload = _pending.Dequeue();
            _pendingBytes -= payload != null ? payload.Length : 0;
            if (_pendingBytes < 0) _pendingBytes = 0;
            return true;
        }

        /// <summary>
        /// Drop the queue and reset the byte counter. Called at session
        /// boundary (ClearSessionData) so a reconnect does not leak stale
        /// payloads into the new session's RPC stream. Also clears the
        /// re-entry guard via <see cref="ExitDrain"/> as a defensive
        /// safety — if a previous session was torn down mid-drain, the
        /// new session must start with the guard idle.
        /// </summary>
        public void Clear()
        {
            _pending?.Clear();
            _pending = null;
            _pendingBytes = 0;
            ExitDrain();
        }
    }
}
