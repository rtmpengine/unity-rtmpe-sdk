// RTMPE SDK — Runtime/Core/TransportBindWaitPolicy.cs
//
// Decides how long the handshake-init dispatcher may wait for the transport
// to bind before giving up.  Transport bring-up runs on the network thread
// and includes DNS resolution (bounded by the transport's own resolver
// timeout) plus socket construction, so on a cold OS resolver cache or a
// hitching first frame it can legitimately take several seconds.  The wait
// budget therefore equals the connection watchdog's budget: any independent,
// shorter budget creates a window where the transport binds moments after
// the dispatcher has permanently given up — the attempt then idles, fully
// bound, until the watchdog reports a timeout with the init never sent.
// Kept UnityEngine-free so it is exercisable from the headless dotnet xunit
// runner.

namespace RTMPE.Core
{
    /// <summary>
    /// Pure policy for the bind-wait loop that precedes the first handshake
    /// packet (<c>HandshakeInit</c> / <c>ReconnectInit</c>) of a connection
    /// attempt.
    /// </summary>
    internal static class TransportBindWaitPolicy
    {
        /// <summary>
        /// The wait budget, in seconds, for a connection attempt whose
        /// watchdog fires after <paramref name="connectionTimeoutMs"/>.
        /// The two budgets are intentionally the same clock: the watchdog is
        /// the single authority on declaring the attempt failed, so the
        /// bind-wait never abandons an attempt the watchdog still considers
        /// live.  Non-positive timeouts yield a zero budget.
        /// </summary>
        internal static float MaxWaitSeconds(int connectionTimeoutMs)
            => connectionTimeoutMs > 0 ? connectionTimeoutMs / 1_000f : 0f;

        /// <summary>
        /// Returns <see langword="true"/> while the dispatcher should keep
        /// polling for the transport to bind: the endpoint is not yet bound,
        /// the connection attempt is still live (its state machine has not
        /// moved on, e.g. via teardown or a transport error), and the wait
        /// budget is not exhausted.
        /// </summary>
        internal static bool ShouldKeepWaiting(
            bool transportBound,
            bool attemptActive,
            float waitedSeconds,
            float maxWaitSeconds)
            => !transportBound
            && attemptActive
            && waitedSeconds < maxWaitSeconds;
    }
}
