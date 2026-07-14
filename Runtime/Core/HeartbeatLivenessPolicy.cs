// RTMPE SDK — Runtime/Core/HeartbeatLivenessPolicy.cs
//
// The single decision that declares an RTMPE session dead. Kept as a pure,
// UnityEngine-free seam — no clock, no fields — so the rule can be exercised
// in isolation the same way DiagnosticsFlushPolicy is, without a live session
// or thread timing.
//
// Two independent witnesses must agree before a teardown:
//   • the missed-ack COUNTER — a fast detector that the keep-alive cadence has
//     gone quiet; and
//   • a wall-clock span with no AUTHENTICATED ack — the authority that a quiet
//     cadence reflects a dead link rather than a client that briefly could not
//     drain the ack off its main thread.
//
// Requiring both is what lets a connection survive a transient local stall
// (frame starvation, a saturated main-thread dispatcher that sheds the ack
// frame) without being mistaken for a network loss: the counter may climb,
// but as long as one real ack landed inside the grace span the link is treated
// as alive. Only a span that produces NO authenticated ack at all is a genuine
// outage, and that is the one case this returns true for.

namespace RTMPE.Core
{
    /// <summary>
    /// Pure liveness verdict for <see cref="HeartbeatManager"/>: decides when a
    /// run of unacknowledged heartbeats has lasted long enough, with no
    /// authenticated ack in between, to be declared a connection loss.
    /// </summary>
    internal static class HeartbeatLivenessPolicy
    {
        /// <summary>
        /// True when the session should be torn down: the consecutive-miss
        /// counter has reached its threshold AND no AEAD-authenticated
        /// HeartbeatAck has been observed for at least <paramref name="graceMs"/>.
        /// </summary>
        /// <param name="missedAcks">Consecutive missed heartbeat cycles.</param>
        /// <param name="maxMissedAcks">Miss count that arms the timeout intent.</param>
        /// <param name="msSinceLastValidAck">
        /// Elapsed milliseconds since the most recent authenticated ack (or since
        /// the session began, before the first ack).
        /// </param>
        /// <param name="graceMs">
        /// Maximum span tolerated with no authenticated ack. The counter alone
        /// never disconnects — the link is only declared dead once this span is
        /// exceeded, so a stall that still delivers a real ack within it is
        /// forgiven.
        /// </param>
        public static bool ShouldDisconnect(
            int missedAcks, int maxMissedAcks, long msSinceLastValidAck, long graceMs)
        {
            // Short-circuit on the counter so the healthy path — where misses
            // never accrue — never even consults the clock and stays identical
            // to the pre-grace behaviour.
            return missedAcks >= maxMissedAcks && msSinceLastValidAck >= graceMs;
        }
    }
}
