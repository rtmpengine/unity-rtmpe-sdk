// RTMPE SDK — Runtime/Core/ReliableChannel.cs
//
// Application-level Automatic Repeat-reQuest (ARQ) state for reliable
// outbound frames and an inbound dedup window for in-flight reliable
// receives.  Provides the four primitives required to bolt a Selective-
// Repeat reliability layer on top of the existing UDP transport once
// gateway-side ACK plumbing lands:
//
//   1. Per-channel monotonically-increasing sequence numbers, allocated
//      in 32-bit modular sequence space (RFC 1982).
//   2. A retransmit table indexed by sequence, with exponential-backoff
//      timers (initial RTO + 2^attempts up to a configurable ceiling).
//   3. Inbound dedup over a fixed-size sliding window so a packet that
//      crosses the wire twice (loss + retransmit, or routing duplication)
//      is delivered to the application exactly once.
//   4. ACK accounting that drains the retransmit table when an ACK
//      sequence is observed (cumulative ACK semantics) and keeps a record
//      of the highest contiguous ACK for piggyback.
//
// What this is NOT:
//
//   • A full Selective-ACK / SACK bitmap implementation.  Cumulative ACK
//     up to the highest contiguous sequence is sufficient for the SDK's
//     RPC / variable-update / ownership-transfer use cases — those
//     payloads are small (≤ 1.4 KB), strictly ordered, and rare enough
//     that head-of-line blocking is acceptable.
//   • Wired into the on-wire packet format yet.  The 4-byte sequence in
//     PacketProtocol.OFFSET_SEQUENCE today is the AEAD nonce counter,
//     owned by the Crypto / Security team.  Threading the ARQ sequence
//     into the wire requires either repurposing the AEAD nonce (must be
//     done in lock-step with the gateway) or adding a 4-byte ARQ header
//     under FLAG_RELIABLE.  That coordination is tracked separately;
//     this module ships the client half so it can land independently.
//
// All operations are O(1) expected.  Allocation-free after construction
// for the common-case small in-flight window.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Client-side ARQ state for a single reliable channel.  One instance
    /// is shared between the inbound dedup path and the outbound retransmit
    /// table because both share the same sequence space.
    /// </summary>
    public sealed class ReliableChannel
    {
        // ── Configuration ──────────────────────────────────────────────────────

        /// <summary>
        /// Maximum number of in-flight unacknowledged frames.  When the
        /// table is full, <see cref="TryRegisterOutbound"/> returns
        /// <see langword="false"/> and the caller must back off.
        /// </summary>
        public const int MaxInFlight = 64;

        /// <summary>
        /// Inbound dedup window size in sequence numbers.  A frame whose
        /// sequence falls outside the window is dropped silently as either
        /// a stale duplicate or an attacker replay.
        /// </summary>
        public const int DedupWindowSize = 1024;

        /// <summary>Initial retransmit timeout (seconds).</summary>
        public float InitialRtoSeconds { get; set; } = 0.2f;

        /// <summary>Upper cap on retransmit timeout (seconds).</summary>
        public float MaxRtoSeconds { get; set; } = 2.0f;

        /// <summary>Hard cap on retransmit attempts before the entry is dropped.</summary>
        public int MaxAttempts { get; set; } = 8;

        // ── Outbound state ─────────────────────────────────────────────────────

        private struct OutboundEntry
        {
            public uint    Sequence;
            public byte[]  Payload;
            public float   NextSendAt;   // monotonic seconds (caller-supplied clock)
            public int     Attempts;
            public bool    InUse;
        }

        private readonly OutboundEntry[] _outbound = new OutboundEntry[MaxInFlight];
        private uint _nextOutboundSeq;
        private int  _outboundCount;

        // ── Inbound dedup state ────────────────────────────────────────────────
        //
        // A bitmap-backed sliding window.  The window's high watermark is
        // _highestSeenSeq; bit i represents (highestSeen - i).  An incoming
        // sequence is accepted iff it is strictly greater than the high
        // watermark, OR it falls within the window and its bit is unset.

        private readonly ulong[] _dedupBitmap = new ulong[DedupWindowSize / 64];
        private uint _highestSeenSeq;
        private bool _hasInbound;

        // ── Outbound API ───────────────────────────────────────────────────────

        /// <summary>Number of unacknowledged outbound frames currently tracked.</summary>
        public int InFlightCount => _outboundCount;

        /// <summary>
        /// Allocate the next outbound sequence number and register
        /// <paramref name="payload"/> in the retransmit table.  The caller
        /// transmits the frame immediately and supplies the current monotonic
        /// clock reading (seconds) so the retransmit timer is anchored to
        /// the same time base as the consumer's tick.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> when registered.  <see langword="false"/>
        /// when the in-flight table is saturated — caller must back off and
        /// retry on the next tick once an ACK drains the table.
        /// </returns>
        public bool TryRegisterOutbound(byte[] payload, float nowSeconds, out uint sequence)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));

            if (_outboundCount >= MaxInFlight)
            {
                sequence = 0u;
                return false;
            }

            int slot = FindFreeSlot();
            sequence = _nextOutboundSeq++;
            _outbound[slot] = new OutboundEntry
            {
                Sequence   = sequence,
                Payload    = payload,
                NextSendAt = nowSeconds + InitialRtoSeconds,
                Attempts   = 1,
                InUse      = true,
            };
            _outboundCount++;
            return true;
        }

        /// <summary>
        /// Acknowledge every in-flight frame whose sequence is &lt;=
        /// <paramref name="ackedSequence"/> (cumulative-ACK semantics).
        /// Returns the number of entries cleared.
        /// </summary>
        public int AcknowledgeUpTo(uint ackedSequence)
        {
            int cleared = 0;
            for (int i = 0; i < _outbound.Length; i++)
            {
                if (!_outbound[i].InUse) continue;
                // Modular sequence-number comparison so wrap is handled.
                if ((int)(_outbound[i].Sequence - ackedSequence) <= 0)
                {
                    _outbound[i] = default;
                    cleared++;
                }
            }
            _outboundCount -= cleared;
            return cleared;
        }

        /// <summary>
        /// Walk the retransmit table and invoke <paramref name="resend"/>
        /// for every entry whose retransmit timer has expired.  The
        /// retransmit timer is then doubled (capped at
        /// <see cref="MaxRtoSeconds"/>) and the attempt counter incremented.
        /// Entries that exceed <see cref="MaxAttempts"/> are dropped and
        /// reported via the optional <paramref name="onDropped"/> callback.
        /// </summary>
        public void Tick(float nowSeconds, Action<uint, byte[]> resend, Action<uint> onDropped = null)
        {
            if (resend == null) throw new ArgumentNullException(nameof(resend));

            for (int i = 0; i < _outbound.Length; i++)
            {
                ref OutboundEntry e = ref _outbound[i];
                if (!e.InUse) continue;
                if (nowSeconds < e.NextSendAt) continue;

                if (e.Attempts >= MaxAttempts)
                {
                    uint dropped = e.Sequence;
                    e = default;
                    _outboundCount--;
                    onDropped?.Invoke(dropped);
                    continue;
                }

                resend(e.Sequence, e.Payload);
                e.Attempts++;

                // Exponential backoff: 1× RTO, 2× RTO, 4× RTO ... up to the cap.
                // Attempts is post-incremented so the next interval is
                // initialRto * 2^(attempts-1).  Clamp to MaxRtoSeconds to keep
                // long-stalled connections from hibernating their retransmits.
                float interval = InitialRtoSeconds * (float)(1 << Math.Min(e.Attempts - 1, 16));
                if (interval > MaxRtoSeconds) interval = MaxRtoSeconds;
                e.NextSendAt = nowSeconds + interval;
            }
        }

        // ── Inbound API ────────────────────────────────────────────────────────

        /// <summary>
        /// Test-and-set the dedup bit for <paramref name="sequence"/>.
        /// Returns <see langword="true"/> iff the sequence is fresh — i.e.
        /// inside the window and not previously delivered — in which case
        /// the bit is recorded and the caller should deliver the payload
        /// to the application.  Stale or far-out-of-window sequences return
        /// <see langword="false"/>.
        /// </summary>
        public bool TryAcceptInbound(uint sequence)
        {
            if (!_hasInbound)
            {
                _hasInbound      = true;
                _highestSeenSeq  = sequence;
                SetBit(0);
                return true;
            }

            int delta = (int)(sequence - _highestSeenSeq);

            if (delta > 0)
            {
                // Advance the window by `delta` slots.  Anything that falls
                // off the trailing edge is permanently considered "seen".
                ShiftWindow(delta);
                _highestSeenSeq = sequence;
                SetBit(0);
                return true;
            }

            int distance = -delta;
            if (distance >= DedupWindowSize)
            {
                // Far below the window — treat as stale duplicate / replay.
                return false;
            }

            if (TestBit(distance)) return false;
            SetBit(distance);
            return true;
        }

        /// <summary>
        /// Highest inbound sequence ever accepted — i.e. the head of the
        /// dedup window. Undefined when no inbound has been processed yet.
        /// </summary>
        public uint HighestSeenSequence => _highestSeenSeq;

        /// <summary>
        /// Highest cumulative-ACK candidate: the largest sequence
        /// <c>S</c> such that every sequence in
        /// <c>[S - dedupWindow + 1 .. S]</c> has been accepted (anything
        /// below the window is implicitly ACK'd by virtue of being too old
        /// for the receiver to retransmit). Walks the dedup bitmap from the
        /// oldest tracked position up toward the head, locating the lowest
        /// gap; cost is O(W/64) word reads in the worst case. Returns 0
        /// before any inbound frame has been processed.
        /// </summary>
        public uint HighestContiguousAck
        {
            get
            {
                if (!_hasInbound) return 0;
                int max = _dedupBitmap.Length * 64;
                // Walk from the oldest tracked distance (max-1) toward the
                // head (distance 0). The first set bit we hit anchors the
                // start of a contiguous run; we then keep walking until a
                // cleared bit terminates the run. The cumulative-ACK seq is
                // the one immediately below the terminating gap, or the
                // head when no gap is found.
                int d = max - 1;
                while (d >= 0 && !TestBit(d)) d--;
                if (d < 0) return _highestSeenSeq;
                while (d >= 0 && TestBit(d)) d--;
                if (d < 0) return _highestSeenSeq;
                return unchecked(_highestSeenSeq - (uint)d - 1u);
            }
        }

        /// <summary>True once the channel has accepted at least one inbound frame.</summary>
        public bool HasInbound => _hasInbound;

        // ── Test hooks ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the next unallocated outbound sequence — exposed for tests.
        /// </summary>
        internal uint NextOutboundSequence => _nextOutboundSeq;

        /// <summary>Test-only seed for the outbound sequence counter.</summary>
        internal void SeedOutboundSequence(uint seed) => _nextOutboundSeq = seed;

        // ── Helpers ────────────────────────────────────────────────────────────

        private int FindFreeSlot()
        {
            // Linear scan — MaxInFlight is small (64) and the table is
            // typically sparse during steady-state operation.
            for (int i = 0; i < _outbound.Length; i++)
                if (!_outbound[i].InUse) return i;
            // Should be unreachable thanks to the saturation check in
            // TryRegisterOutbound; throwing here surfaces a SDK invariant
            // violation rather than silently overwriting an in-flight entry.
            throw new InvalidOperationException("ReliableChannel: in-flight table full");
        }

        private void ShiftWindow(int delta)
        {
            if (delta >= DedupWindowSize)
            {
                Array.Clear(_dedupBitmap, 0, _dedupBitmap.Length);
                return;
            }

            int wholeWords = delta / 64;
            int bitShift   = delta % 64;

            // Shift LEFT (toward older bits) so the newest sample sits at
            // bit index 0 of the bitmap word at index 0.  This is the
            // conventional sliding-window encoding (older entries fall off
            // the trailing edge as they pass under the window).
            if (wholeWords > 0)
            {
                for (int i = _dedupBitmap.Length - 1; i >= 0; i--)
                {
                    int src = i - wholeWords;
                    _dedupBitmap[i] = src >= 0 ? _dedupBitmap[src] : 0UL;
                }
            }
            if (bitShift > 0)
            {
                ulong carry = 0UL;
                for (int i = 0; i < _dedupBitmap.Length; i++)
                {
                    ulong w = _dedupBitmap[i];
                    _dedupBitmap[i] = (w << bitShift) | carry;
                    carry = bitShift == 0 ? 0UL : w >> (64 - bitShift);
                }
            }
        }

        private void SetBit(int distance)
        {
            int word = distance >> 6;
            int bit  = distance & 0x3F;
            _dedupBitmap[word] |= 1UL << bit;
        }

        private bool TestBit(int distance)
        {
            int word = distance >> 6;
            int bit  = distance & 0x3F;
            return (_dedupBitmap[word] & (1UL << bit)) != 0UL;
        }
    }
}
