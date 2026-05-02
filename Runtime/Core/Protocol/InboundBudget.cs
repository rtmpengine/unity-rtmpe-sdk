// RTMPE SDK — Runtime/Core/Protocol/InboundBudget.cs
//
// Token-bucket admission controller that bounds CPU under a packet flood.
//
// Threat model — why this exists:
//   The SDK speaks to exactly one peer (the gateway), so this is effectively
//   a hostile-gateway / replay-amplifier defence. Without a token bucket, a
//   compromised or hijacked gateway could saturate the SDK's main thread by
//   replaying legitimate AEAD-valid packets at line rate; every replayed
//   packet would otherwise reach DecryptInboundPacket, fail anti-replay
//   admission, and burn an Interlocked.CompareExchange iteration on the way
//   out. Putting the bucket BEFORE any header / AEAD work caps the per-
//   second cost of a flood at a constant.
//
// Capacity choices:
//   • Sustained: 1500 pps refill — an order of magnitude above legitimate
//     30 Hz × 16-player traffic (~480 pps) so steady-state play never trips
//     the gate, and above the gateway dispatcher's MaxQueueDepth refill so
//     this bucket cannot become the bottleneck for healthy traffic.
//   • Burst: 3000 pps — absorbs application bursts (resync after pause,
//     mass spawn frame, etc.) without dropping; held below twice the
//     sustained rate so a burst can clear within ~1 second of headroom.
//
// Threading contract:
//   • TryConsume() and the underlying token-bucket fields are main-thread-
//     only (matches ProcessPacket's single-thread invariant). The bucket
//     cannot underflow into negatives because each call decrements at most
//     once.
//   • The dropped-flood counter is mutated cross-thread in principle (a
//     future receive path that elects to count without consuming would
//     still want atomicity); kept Interlocked-protected to keep that future
//     valid without revisiting the contract.

using System;
using System.Diagnostics;
using System.Threading;

namespace RTMPE.Core.Protocol
{
    internal sealed class InboundBudget
    {
        /// <summary>Maximum bucket capacity in tokens (i.e. the burst cap).</summary>
        public const float MaxTokens = 3000f;

        /// <summary>Bucket refill rate in tokens per wall-clock second.</summary>
        public const float RefillPerSec = 1500f;

        // Main-thread-only state.
        private long  _lastRefillTicks;
        private float _tokens = MaxTokens;

        // Atomic — see threading contract above.
        private long  _droppedFloodPacketCount;

        /// <summary>
        /// Total packets dropped because <see cref="TryConsume"/> returned
        /// <see langword="false"/> at the gate. Surfaced for backpressure
        /// observability — any persistent non-zero rate means either a
        /// hostile gateway or a configuration mismatch (legitimate burst
        /// above the cap).
        /// </summary>
        public long DroppedFloodPacketCount =>
            Interlocked.Read(ref _droppedFloodPacketCount);

        /// <summary>
        /// Attempts to consume one token. Refills the bucket from elapsed
        /// wall-clock time first, capped at <see cref="MaxTokens"/>. Returns
        /// <see langword="false"/> when the bucket is empty — caller drops
        /// the packet to bound CPU under flood.
        /// </summary>
        /// <remarks>
        /// Stopwatch ticks are monotonic so an NTP step cannot freeze or
        /// open the gate; <see cref="Stopwatch.Frequency"/> is used to
        /// convert ticks to seconds.
        /// </remarks>
        public bool TryConsume()
        {
            long now = Stopwatch.GetTimestamp();
            long prev = _lastRefillTicks;
            if (prev == 0)
            {
                _lastRefillTicks = now;
            }
            else if (now > prev)
            {
                double elapsedSec = (now - prev) / (double)Stopwatch.Frequency;
                _tokens = Math.Min(
                    MaxTokens,
                    _tokens + (float)(elapsedSec * RefillPerSec));
                _lastRefillTicks = now;
            }

            if (_tokens >= 1f)
            {
                _tokens -= 1f;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Records that one inbound packet was dropped at the bucket gate.
        /// Atomic so a future cross-thread caller does not need to revisit
        /// the contract.
        /// </summary>
        public void RecordDrop()
        {
            Interlocked.Increment(ref _droppedFloodPacketCount);
        }
    }
}
