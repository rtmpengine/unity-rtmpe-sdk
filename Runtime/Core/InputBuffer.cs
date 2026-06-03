// RTMPE SDK — Runtime/Core/InputBuffer.cs
//
// Fixed-capacity ring buffer that stores unacknowledged InputPayloads for
// client-side prediction rollback.
//
// Capacity: 64 entries (power of two — enables bitwise AND masking).
// At 30 Hz, 64 entries covers >2 seconds of unacknowledged input — well
// beyond any realistic server round-trip time.  When the buffer is full
// the NEWEST push is rejected (Push returns false): the oldest entry is
// the rollback anchor closest to server-confirmed state and must be
// preserved so the replay path can re-simulate from a coherent baseline.
// Overwriting the oldest would silently roll the rollback horizon past
// the server's last ack, producing undetectable state corruption when
// the next reconciliation arrives.  A bounded counter
// (DroppedInputCount) surfaces the rejection rate to telemetry.
//
// No UnityEngine dependency — testable from pure .NET xunit projects.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Fixed-capacity ring buffer of unacknowledged <see cref="InputPayload"/> entries
    /// for client-side prediction and rollback.
    ///
   /// <para>All operations are O(1) and allocation-free after construction.</para>
    /// </summary>
    public sealed class InputBuffer
    {
        // ── Constants ──────────────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of entries the buffer can hold simultaneously.
        /// Must be a power of two for the bitwise AND mask to work correctly.
        /// </summary>
        public const int Capacity = 64;

        // Bitwise AND mask for O(1) index wrapping without modulo arithmetic.
        private const int Mask = Capacity - 1;

        // ── Storage ────────────────────────────────────────────────────────────

        private readonly InputPayload[] _slots = new InputPayload[Capacity];

        // _head: index of the oldest unacknowledged entry.
        // _count: number of valid unacknowledged entries (0..Capacity inclusive).
        private int _head;
        private int _count;

        // Tracks the highest ack tick seen so far.  AcknowledgeUpTo rejects
        // any value that is not strictly greater than this, preventing a hostile
        // server from replaying a stale ack to clear the buffer out of order.
        private uint _lastAckedTick;
        private bool _hasLastAckedTick;

        // Monotonic counter incremented every time Push rejects an entry
        // because the buffer is saturated.  Exposed so integrators can wire
        // it into telemetry / on-screen debugging without poking internal
        // state.  64-bit width because at sustained-rejection extremes
        // (10 ms acks for hours on a misconfigured server) the count can
        // exceed 2³¹ across a single session.
        private long _droppedInputCount;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Number of unacknowledged entries currently stored.</summary>
        public int Count => _count;

        /// <summary>
        /// Total number of <see cref="Push"/> calls rejected because the
        /// buffer was full and the oldest entry could not be evicted (it is
        /// the rollback anchor for the next reconciliation).  Useful for
        /// surfacing replay-window saturation in telemetry: a non-zero,
        /// climbing value indicates the server-ack pipeline is stalled or
        /// the round-trip exceeds the buffer's coverage.
        /// </summary>
        public long DroppedInputCount => _droppedInputCount;

        // ── Operations ─────────────────────────────────────────────────────────

        /// <summary>
        /// Append <paramref name="payload"/> to the back of the buffer.
        ///
       /// <para>When the rollback window saturates, the oldest input is the
        /// anchor for the next reconciliation replay; preserving it requires
        /// rejecting the newest, not the oldest.  In a saturated state the
        /// new payload is dropped, <see cref="DroppedInputCount"/> is
        /// incremented, and the method returns <see langword="false"/> so the
        /// caller can surface back-pressure (e.g. throttle local input or
        /// alert telemetry).  Returns <see langword="true"/> when the
        /// payload was accepted.</para>
        /// </summary>
        public bool Push(InputPayload payload)
        {
            // Reject non-finite movement axes before they enter the rollback
            // buffer.  WriteTo enforces finiteness at the wire boundary and
            // throws, but an unfinished/buggy GatherInput callback can still
            // push NaN into the local CSP simulation where it poisons predicted
            // transforms for every subsequent replay tick (SDKS-04).  Mirroring
            // the WriteTo guard here keeps the buffer free of non-finite inputs
            // regardless of whether the packet ever reaches the wire.
            if (float.IsNaN(payload.MoveX)      || float.IsInfinity(payload.MoveX)
                || float.IsNaN(payload.MoveY)   || float.IsInfinity(payload.MoveY))
            {
                unchecked { _droppedInputCount++; }
                return false;
            }

            if (_count >= Capacity)
            {
                // Saturated: refuse the newest write.  The oldest entry is
                // the rollback anchor closest to the last server-confirmed
                // state; evicting it would silently roll the replay horizon
                // past the server's ack and produce undetectable corruption
                // on the next reconciliation.
                unchecked { _droppedInputCount++; }
                return false;
            }

            int slot = (_head + _count) & Mask;
            _slots[slot] = payload;
            _count++;
            return true;
        }

        /// <summary>
        /// Remove all entries whose <see cref="InputPayload.Tick"/> is &lt;=
        /// <paramref name="ackedTick"/>.  No-op when the buffer is empty or when
        /// <paramref name="ackedTick"/> does not advance beyond the last accepted ack.
        ///
       /// <para>Tick comparisons use 32-bit modular sequence-number arithmetic
        /// (RFC 1982 §3.2 SerialNumberArithmetic) so the buffer continues to
        /// drain correctly when the local tick counter wraps the
        /// <see cref="uint"/> boundary.  A direct numeric comparison would
        /// stall every reconciliation for ≈2³¹ ticks once a session crosses
        /// 4 294 967 296 ticks (~1657 days at 30 Hz, but reachable inside a
        /// single fuzzing run that seeds the counter near uint.MaxValue).</para>
        ///
       /// <para>Complexity: O(k) where k is the number of acknowledged entries.</para>
        /// </summary>
        public void AcknowledgeUpTo(uint ackedTick)
        {
            // Monotonicity guard: a replayed or out-of-order ack cannot bulk-clear
            // inputs the server has not yet processed.  SeqLessOrEqual handles
            // the wrap boundary so an ack of (uint.MaxValue - 1) does not block
            // a subsequent ack of (uint.MaxValue + 1) ≡ 0.
            if (_hasLastAckedTick && SeqLessOrEqual(ackedTick, _lastAckedTick)) return;
            _hasLastAckedTick = true;
            _lastAckedTick    = ackedTick;

            while (_count > 0 && SeqLessOrEqual(_slots[_head].Tick, ackedTick))
            {
                _head  = (_head + 1) & Mask;
                _count--;
            }
        }

        // ── Modular sequence-number arithmetic (RFC 1982) ──────────────────────
        //
        // A signed 32-bit difference treats two unsigned values as "near" on a
        // ring of size 2³² when the gap between them is less than 2³¹.  The
        // CSP buffer is bounded at 64 entries, so any in-flight ack is at most
        // a few thousand ticks behind the head — orders of magnitude below the
        // 2³¹ wrap-distance threshold the comparison relies on.

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="a"/> is strictly
        /// greater than <paramref name="b"/> in 32-bit modular sequence-number
        /// space (i.e. <c>(int)(a - b) &gt; 0</c>).
        /// </summary>
        internal static bool SeqGreater(uint a, uint b) => (int)(a - b) > 0;

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="a"/> is less than
        /// or equal to <paramref name="b"/> in 32-bit modular sequence-number
        /// space.
        /// </summary>
        internal static bool SeqLessOrEqual(uint a, uint b) => (int)(a - b) <= 0;

        /// <summary>
        /// Copy all unacknowledged entries — oldest first — into
        /// <paramref name="dest"/>.
        ///
       /// <para>The caller must allocate <paramref name="dest"/> with
        /// <c>Length &gt;= <see cref="Count"/></c> (or &gt;= <see cref="Capacity"/>
        /// to be safe against concurrent pushes on the same frame).</para>
        /// </summary>
        /// <returns>Number of entries written into <paramref name="dest"/>.</returns>
        public int CopyUnacknowledgedTo(InputPayload[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int written = 0;
            for (int i = 0; i < _count; i++)
                dest[written++] = _slots[(_head + i) & Mask];
            return written;
        }

        /// <summary>
        /// Copy every entry whose <see cref="InputPayload.Tick"/> is strictly
        /// greater than <paramref name="afterTick"/> — in oldest-first order —
        /// into <paramref name="dest"/>.  Used by the CSP replay loop to walk
        /// the inputs that the server has not yet confirmed and re-apply them
        /// on top of the just-snapped authoritative state.
        ///
       /// <para>Comparison uses the same RFC 1982 modular arithmetic as
        /// <see cref="AcknowledgeUpTo"/> so the replay works correctly across
        /// the uint32 wrap boundary.</para>
        /// </summary>
        /// <returns>Number of entries written to <paramref name="dest"/>.</returns>
        public int CopyUnacknowledgedAfter(uint afterTick, InputPayload[] dest)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int written = 0;
            for (int i = 0; i < _count; i++)
            {
                var slot = _slots[(_head + i) & Mask];
                if (SeqGreater(slot.Tick, afterTick))
                    dest[written++] = slot;
            }
            return written;
        }

        /// <summary>Remove all stored entries, resetting head and count to zero.
        /// Does NOT reset <see cref="DroppedInputCount"/>: the drop counter is
        /// session-scoped telemetry that survives spawn/despawn cycles so a
        /// flapping connection does not hide its own back-pressure.</summary>
        public void Clear()
        {
            _head             = 0;
            _count            = 0;
            _hasLastAckedTick = false;
            _lastAckedTick    = 0;
        }
    }
}
