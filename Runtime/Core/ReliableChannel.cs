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
//   4. ACK accounting that clears one retransmit entry per gateway DataAck.
//      The gateway acknowledges each reliable frame individually — echoing
//      that frame's own arq_seq with no notion of contiguity — so the
//      outbound table clears the single matching entry and leaves any gap
//      intact for retransmit.  A separate highest-contiguous-ACK accessor
//      over the inbound window feeds piggyback acknowledgement in the
//      receive direction.
//
// What this is NOT:
//
//   • A SACK range/bitmap acknowledging arbitrary spans in one frame.  The
//     gateway's per-frame DataAck makes single-sequence clearing the exact
//     match for the wire protocol, and the SDK's RPC / variable-update /
//     ownership-transfer payloads are small (≤ 1.4 KB), strictly ordered,
//     and rare enough that the per-frame ack cadence carries no meaningful
//     overhead.
//
// On-wire integration:
//
//   The ARQ sequence IS wired into the on-wire format — it is carried in a
//   dedicated 4-byte sub-header emitted under FLAG_RELIABLE (see
//   NetworkManager.AeadPipeline), independent of the AEAD nonce counter,
//   and the gateway acknowledges it with DataAck (0x11).  ARQ activates
//   only when the caller requests reliable delivery, NetworkSettings.
//   EmitArqSequence is true, and the session negotiated the CAP_ARQ_ACK
//   capability; otherwise the send downgrades to a single best-effort
//   transmission.
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

        // Lower bound on the per-frame RTO.  Anything smaller would let a
        // pathological 0 / negative / NaN setter assignment collapse the
        // retransmit timer into a tight resend loop, exhausting the
        // outbound socket buffer before any ACK can arrive.
        private const float MinRtoSeconds = 0.01f;

        // Upper bound on the per-frame RTO.  Above the test ceiling the
        // retransmit cadence becomes indistinguishable from "no retransmit"
        // for the realtime gameplay window the SDK targets.
        private const float MaxRtoCeilingSeconds = 60f;

        // Lower / upper bounds on the retransmit attempt cap.  A negative
        // value would silently disable retransmission; values above the
        // ceiling exceed any plausible RTT × MaxRto budget.
        private const int MinMaxAttempts = 1;
        private const int MaxAttemptsCeiling = 64;

        private float _initialRtoSeconds = 0.2f;
        private float _maxRtoSeconds     = 2.0f;
        private int   _maxAttempts       = 8;

        /// <summary>Initial retransmit timeout (seconds).</summary>
        public float InitialRtoSeconds
        {
            get => _initialRtoSeconds;
            // Reject NaN / Infinity outright; clamp positive finite values
            // into the documented working range.  An out-of-range value
            // never silently wins — it is always observable as a clamped
            // read on the next get.  The exponential-backoff schedule
            // depends on Initial <= Max so a setter that pushes the floor
            // above the current ceiling lifts the ceiling along with it,
            // mirroring the symmetric guard on MaxRtoSeconds.
            set
            {
                float clamped = ClampRto(value, MinRtoSeconds, MaxRtoCeilingSeconds);
                _initialRtoSeconds = clamped;
                if (_maxRtoSeconds < clamped) _maxRtoSeconds = clamped;
            }
        }

        /// <summary>Upper cap on retransmit timeout (seconds).</summary>
        public float MaxRtoSeconds
        {
            get => _maxRtoSeconds;
            // The RTO ceiling must remain >= the RTO floor so the
            // exponential-backoff schedule never inverts; clamp up to the
            // initial RTO when an out-of-order setter assignment would
            // otherwise leave MaxRto < InitialRto.
            set
            {
                float clamped = ClampRto(value, MinRtoSeconds, MaxRtoCeilingSeconds);
                if (clamped < _initialRtoSeconds) clamped = _initialRtoSeconds;
                _maxRtoSeconds = clamped;
            }
        }

        /// <summary>Hard cap on retransmit attempts before the entry is dropped.</summary>
        public int MaxAttempts
        {
            get => _maxAttempts;
            set
            {
                if (value < MinMaxAttempts) value = MinMaxAttempts;
                else if (value > MaxAttemptsCeiling) value = MaxAttemptsCeiling;
                _maxAttempts = value;
            }
        }

        // Shared finite-and-clamped helper for the two RTO setters.  Keeping
        // the rule in one place ensures the InitialRto / MaxRto setters
        // always make the same finiteness decision.
        private static float ClampRto(float value, float lo, float hi)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return lo;
            if (value < lo) return lo;
            if (value > hi) return hi;
            return value;
        }

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
        /// Allocate the next outbound ARQ sequence number without registering
        /// a retransmit entry.  Used by the wire-emission path to stamp the
        /// 4-byte sub-header that appears under <see cref="PacketFlags.Reliable"/>
        /// when <see cref="Core.NetworkSettings.EmitArqSequence"/> is enabled.
        /// Call <see cref="TryRegisterOutbound"/> instead when the caller also
        /// needs the retransmit timer wired.
        /// </summary>
        public uint AllocateOutboundSequence() => _nextOutboundSeq++;

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
        /// Clear the retransmit entry for the single frame the gateway
        /// acknowledged.  The gateway emits one DataAck (0x11) per received
        /// reliable frame, echoing that frame's own <c>arq_seq</c> with no
        /// notion of contiguity, so an ack for <paramref name="sequence"/>
        /// proves delivery of that frame alone — never of lower-numbered
        /// frames, which may still be in flight or lost in transit.  Clearing
        /// only the matching entry keeps the retransmit ladder armed for any
        /// gap, so a frame that overtakes a lost predecessor cannot cancel the
        /// predecessor's resend.
        /// </summary>
        /// <returns>
        /// <c>1</c> when a matching entry was cleared; <c>0</c> when none
        /// matched — a duplicate ack, or an ack for an already-cleared or
        /// never-registered sequence.
        /// </returns>
        public int Acknowledge(uint sequence)
        {
            for (int i = 0; i < _outbound.Length; i++)
            {
                if (!_outbound[i].InUse) continue;
                if (_outbound[i].Sequence == sequence)
                {
                    _outbound[i] = default;
                    _outboundCount--;
                    return 1;
                }
            }
            return 0;
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

            // Clamp pathological clock readings so a NaN / negative input
            // cannot stall the retransmit ladder for the rest of the
            // session.  A single bad sample is tolerated (skip this tick);
            // a steady stream of bad samples is the caller's bug to address.
            if (float.IsNaN(nowSeconds) || float.IsInfinity(nowSeconds)) return;

            for (int i = 0; i < _outbound.Length; i++)
            {
                ref OutboundEntry e = ref _outbound[i];
                if (!e.InUse) continue;
                if (nowSeconds < e.NextSendAt) continue;

                // Strict greater-than: TryRegisterOutbound seeds Attempts=1 to
                // represent the initial transmit, so the cap measured here is
                // the number of *retransmits* performed by Tick.  With "> "
                // semantics, MaxAttempts=8 yields exactly 8 retransmits before
                // the entry is dropped, matching the field name's contract.
                if (e.Attempts > MaxAttempts)
                {
                    uint dropped = e.Sequence;
                    e = default;
                    _outboundCount--;
                    // Subscriber-isolation: a buggy onDropped handler must
                    // not abort the per-tick sweep.  The entry has already
                    // been cleared, so a thrown exception leaves no
                    // book-keeping gap.
                    try { onDropped?.Invoke(dropped); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            $"[RTMPE] ReliableChannel.Tick: onDropped threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Subscriber exception isolated.");
                    }
                    continue;
                }

                // Subscriber-isolation: a thrown resend (transport
                // disposed, send buffer full, etc.) must not abort the
                // sweep across siblings.  Increment attempts and reschedule
                // even on failure so the dropped-after-MaxAttempts ladder
                // still fires for an entry whose resend chronically fails.
                try { resend(e.Sequence, e.Payload); }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError(
                        $"[RTMPE] ReliableChannel.Tick: resend threw " +
                        $"{ex.GetType().Name}: {ex.Message}.  Continuing with backoff.");
                }
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

        // ── Session lifecycle ──────────────────────────────────────────────────

        /// <summary>
        /// Drop all per-session ARQ state — the outbound retransmit table, the
        /// outbound sequence counter, and the inbound dedup window — returning
        /// the channel to its as-constructed condition.  The RTO and attempt-cap
        /// tuning is deliberately preserved: it is application configuration set
        /// once at construction, not session state.
        /// </summary>
        /// <remarks>
        /// Invoked on every session boundary (disconnect / reconnect).  The
        /// retransmit table parks the <em>plaintext</em> of each unacknowledged
        /// reliable frame and re-seals it lazily at resend time under whatever
        /// session key is then current.  An entry that outlived its originating
        /// session would be re-encrypted under the next session's key and nonce
        /// stream and retransmitted under a stale sequence — arriving as a frame
        /// the peer can only reject on its replay window or AEAD tag.  Clearing
        /// the table (and the companion inbound dedup window, whose sequence
        /// space also restarts per session) at the boundary keeps each session's
        /// reliable traffic confined to that session.
        /// </remarks>
        public void Reset()
        {
            Array.Clear(_outbound, 0, _outbound.Length);
            _nextOutboundSeq = 0u;
            _outboundCount   = 0;

            Array.Clear(_dedupBitmap, 0, _dedupBitmap.Length);
            _highestSeenSeq  = 0u;
            _hasInbound      = false;
        }

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
