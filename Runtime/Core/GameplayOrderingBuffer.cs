// RTMPE SDK — Runtime/Core/GameplayOrderingBuffer.cs
//
// Bounded reorder buffer for gameplay packets that share a 32-bit monotonic
// gameplay sequence (Fix forward-compat scaffold for RPC ↔ StateSync ordering).
//
// Why a bounded buffer:
//   UDP delivers gameplay packets in arrival order, not send order.  When a
//   designer assumes that an RPC carrying "OnPickedUp(itemId)" cannot land
//   before the StateDelta that flipped the item's owner field, every
//   adversarial UDP reorder is a latent gameplay bug.  A bounded reorder
//   queue ahead of the dispatcher resolves the inversion: each handler is
//   only invoked once every prior sequence has either been delivered or has
//   been confirmed lost.
//
// Why bounded:
//   An attacker can replay a flood of low-sequence packets to force the
//   buffer into worst-case occupancy.  Capping the buffer at a small
//   constant defeats the memory-amplification vector while preserving
//   correctness — an arrival whose sequence falls outside the window is
//   delivered immediately rather than queued, accepting a single inversion
//   in exchange for bounded memory.
//
// Sequence comparison uses RFC 1982 modular arithmetic: a 32-bit unsigned
// wraparound after 4 billion packets (~4.5 years at 30 Hz) is handled
// transparently by the sign of (a - b) cast to int.
//
// Threading: not thread-safe.  Callers (NetworkManager dispatcher,
// per-room handler) hold the dispatch monitor on the main thread.

using System;
using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// In-memory reorder buffer keyed on a 32-bit monotonic gameplay sequence.
    /// Hands each enqueued payload to a delegate exactly once, in original
    /// sequence order, subject to the configured capacity.
    /// </summary>
    public sealed class GameplayOrderingBuffer
    {
        // Sorted-by-sequence pending entries.  A SortedDictionary keeps the
        // capacity-bounded set ordered by gameplay sequence so the head can
        // be peeled in O(log n) and adversarial worst-case insertion remains
        // bounded by the cap.
        private readonly SortedDictionary<uint, byte[]> _pending = new SortedDictionary<uint, byte[]>();

        private readonly int _capacity;

        // Highest sequence that has been delivered (passed to the dispatcher).
        // Used to identify duplicates and to drive the strict-greater advance
        // condition.  Initialised to "no deliveries yet" via _hasDelivered.
        private uint _lastDelivered;
        private bool _hasDelivered;

        // Number of times a sequence already pending in the buffer was
        // re-Enqueued with a different (or same) payload.  In a well-behaved
        // session this counter remains zero — a legitimate duplicate
        // sequence cannot survive the AEAD replay window upstream.  A
        // non-zero count surfaces either (a) a sender bug producing
        // duplicate gameplay sequences across distinct AEAD packets or
        // (b) a downstream reordering pathology.  Surfaced for tests and
        // future telemetry.
        private long _duplicatePendingCount;

        /// <summary>
        /// Number of <see cref="Enqueue"/> calls that arrived for a
        /// sequence already buffered.  The first-writer-wins policy keeps
        /// the original payload; this counter records that the
        /// later-arriving duplicate was rejected.
        /// </summary>
        public long DuplicatePendingCount => _duplicatePendingCount;

        /// <summary>
        /// Construct a reorder buffer with the given capacity.  The capacity
        /// is clamped to [2, 64] — values below 2 cannot resolve any
        /// inversion; values above 64 amplify memory beyond useful reordering
        /// windows for a 30 Hz tick simulation.
        /// </summary>
        public GameplayOrderingBuffer(int capacity)
        {
            if (capacity < 2)  capacity = 2;
            if (capacity > 64) capacity = 64;
            _capacity = capacity;
        }

        /// <summary>Number of buffered (pending) payloads.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Highest delivered sequence (only meaningful after the first delivery).</summary>
        public uint LastDelivered => _lastDelivered;

        /// <summary>True when at least one payload has been delivered.</summary>
        public bool HasDelivered => _hasDelivered;

        /// <summary>
        /// Submit a payload at the given gameplay sequence.  Delivers any
        /// payloads now eligible (the new payload itself plus any buffered
        /// successors) to <paramref name="deliver"/> in sequence order.
        /// </summary>
        public void Enqueue(uint sequence, byte[] payload, Action<byte[]> deliver)
        {
            if (deliver == null) throw new ArgumentNullException(nameof(deliver));

            // Drop duplicates and out-of-window stragglers.  RFC 1982 modular
            // comparison: (delivered - sequence) cast to int is positive iff
            // sequence is in the past relative to delivered.
            if (_hasDelivered)
            {
                int relative = unchecked((int)(_lastDelivered - sequence));
                if (relative >= 0)
                {
                    // Sequence already delivered or duplicate of the watermark.
                    return;
                }
            }

            // First delivery: accept directly to avoid stalling indefinitely
            // on a session that begins mid-stream after a reconnect.
            if (!_hasDelivered)
            {
                Deliver(sequence, payload, deliver);
                DrainContiguous(deliver);
                return;
            }

            uint expected = unchecked(_lastDelivered + 1u);
            if (sequence == expected)
            {
                Deliver(sequence, payload, deliver);
                DrainContiguous(deliver);
                return;
            }

            // Out-of-order arrival: park it pending the missing predecessor.
            // First-writer-wins on collision: a sequence already buffered is
            // not overwritten — the original payload is the AEAD-validated
            // arrival that earned the slot, and silently replacing it would
            // let a sender re-issue the same gameplay sequence with a fresh
            // payload (data-integrity hazard) without any visible signal.
            // The duplicate counter surfaces the event for diagnostics and
            // for assertions in adversarial tests.
            if (_pending.ContainsKey(sequence))
            {
                _duplicatePendingCount = unchecked(_duplicatePendingCount + 1);
                return;
            }
            _pending[sequence] = payload;

            // Memory-amplification guard.  Once the buffer reaches its cap we
            // refuse to wait for further missing predecessors and flush the
            // head immediately, accepting one inversion to keep memory and
            // latency bounded.  This is intentional: a hostile sender that
            // withholds a single low sequence cannot pin our memory.
            if (_pending.Count > _capacity)
            {
                FlushHead(deliver);
            }
        }

        /// <summary>
        /// Force-deliver every buffered entry in sequence order without
        /// waiting for the missing predecessors.  Called when a session is
        /// torn down so partial gameplay packets are not silently dropped.
        /// </summary>
        public void Flush(Action<byte[]> deliver)
        {
            if (deliver == null) throw new ArgumentNullException(nameof(deliver));
            while (_pending.Count > 0)
            {
                FlushHead(deliver);
            }
        }

        /// <summary>Drop every buffered payload and reset the watermark.</summary>
        public void Reset()
        {
            _pending.Clear();
            _lastDelivered         = 0u;
            _hasDelivered          = false;
            _duplicatePendingCount = 0;
        }

        // ── Internals ────────────────────────────────────────────────────

        private void Deliver(uint sequence, byte[] payload, Action<byte[]> deliver)
        {
            _lastDelivered = sequence;
            _hasDelivered  = true;
            deliver(payload);
        }

        // After a successful delivery, peel any contiguous successors out of
        // the pending set in sequence order.
        private void DrainContiguous(Action<byte[]> deliver)
        {
            while (_pending.Count > 0)
            {
                var enumerator = _pending.GetEnumerator();
                enumerator.MoveNext();
                uint head = enumerator.Current.Key;
                byte[] payload = enumerator.Current.Value;
                enumerator.Dispose();

                if (head != unchecked(_lastDelivered + 1u)) return;

                _pending.Remove(head);
                Deliver(head, payload, deliver);
            }
        }

        // Force-deliver the lowest pending entry, advancing the watermark
        // past any missing predecessors.  Triggered on capacity overflow or
        // explicit flush.
        private void FlushHead(Action<byte[]> deliver)
        {
            var enumerator = _pending.GetEnumerator();
            enumerator.MoveNext();
            uint head = enumerator.Current.Key;
            byte[] payload = enumerator.Current.Value;
            enumerator.Dispose();

            _pending.Remove(head);
            Deliver(head, payload, deliver);
            DrainContiguous(deliver);
        }
    }
}
