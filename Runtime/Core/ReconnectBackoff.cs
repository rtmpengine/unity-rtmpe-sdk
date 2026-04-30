// RTMPE SDK — Runtime/Core/ReconnectBackoff.cs
//
// Capped exponential backoff with Full Jitter — the industry-standard reconnect
// policy for distributed systems. Based on AWS Architecture Blog:
// "Exponential Backoff And Jitter" (Marc Brooker, 2015).
//
// Algorithm:
//    baseExp  = min(maxDelay, baseDelay * 2^attempt)
//    delay    = random_uniform(0, baseExp)
//
// Properties:
//  • Exponential growth between attempts prevents reconnect storms on long
//    outages (the server is not hammered by retry traffic while it recovers).
//  • Full Jitter de-correlates reconnect times across many clients — without
//    it, every client that dropped at t=0 would retry in lock-step at t=1s,
//    t=2s, t=4s, …, producing synchronized load spikes that are the
//    canonical cause of cascading failure during gateway recovery.
//  • The attempt counter is explicit so callers can reset it on success
//    without discarding the RNG state.
//
// Thread safety:
//  • Each instance owns its own System.Random. Callers on a single logical
//    connection (e.g. one NetworkManager) must not share a single backoff
//    instance across threads without external synchronization.
//
// Usage:
//    var backoff = new ReconnectBackoff();
//    while (!Connected && backoff.Attempt < MaxAttempts)
//    {
//        var delay = backoff.NextDelay();
//        yield return new WaitForSeconds((float)delay.TotalSeconds);
//        TryConnect();
//    }
//    if (Connected) backoff.Reset();

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Capped exponential backoff with Full Jitter for reconnect scheduling.
    /// </summary>
    public sealed class ReconnectBackoff
    {
        /// <summary>Default base delay before the first retry (1 s).</summary>
        public const int DefaultBaseDelayMs = 1_000;

        /// <summary>Default upper bound on any single delay (30 s).</summary>
        public const int DefaultMaxDelayMs = 30_000;

        private readonly int    _baseDelayMs;
        private readonly int    _maxDelayMs;
        private readonly Random _rng;
        private int             _attempt;

        // Saturation cap for the attempt counter.  Once the exponential has
        // saturated to maxDelayMs (which happens around attempt = 30 with the
        // default 1 s base / 30 s max bounds), further increments are pure
        // bookkeeping — the returned delay no longer changes.  Capping at 30
        // is well past the point where ComputeExponentialCapMs short-circuits
        // (see its `if (attempt >= 30) return maxDelayMs;` early-out) and
        // gives more than two billion safe NextDelay() invocations before the
        // counter would have hit int.MaxValue under the old `checked(...)` —
        // a value never reached in any plausible reconnect scenario, but which
        // would otherwise raise OverflowException out of a long-running
        // reconnect coroutine.
        private const int MaxAttemptForBackoff = 30;

        /// <summary>
        /// Number of <see cref="NextDelay"/> calls since construction or the last
        /// <see cref="Reset"/>. Exposed for telemetry and cap checks.
        /// </summary>
        public int Attempt => _attempt;

        /// <summary>
        /// Build a backoff with the given bounds.
        /// </summary>
        /// <param name="baseDelayMs">
        /// Delay horizon for the first retry.  Must be &gt; 0.
        /// </param>
        /// <param name="maxDelayMs">
        /// Upper bound on any single delay, including jitter.  Must be
        /// &gt;= <paramref name="baseDelayMs"/>.
        /// </param>
        /// <param name="seed">
        /// Optional RNG seed.  Pass a fixed seed in deterministic tests; leave
        /// null in production so the RNG is seeded from a high-entropy source
        /// (cryptographic RNG-derived) instead of the system clock — many
        /// clients reconnecting in the same millisecond after a server outage
        /// would otherwise share a Random seed and produce correlated jitter,
        /// recreating the very reconnect-storm pattern Full Jitter is meant
        /// to prevent.
        /// </param>
        public ReconnectBackoff(
            int baseDelayMs = DefaultBaseDelayMs,
            int maxDelayMs  = DefaultMaxDelayMs,
            int? seed       = null)
        {
            if (baseDelayMs <= 0)
                throw new ArgumentOutOfRangeException(nameof(baseDelayMs),
                    "baseDelayMs must be positive.");
            if (maxDelayMs < baseDelayMs)
                throw new ArgumentOutOfRangeException(nameof(maxDelayMs),
                    "maxDelayMs must be >= baseDelayMs.");

            _baseDelayMs = baseDelayMs;
            _maxDelayMs  = maxDelayMs;
            _rng         = seed.HasValue ? new Random(seed.Value) : new Random(NewEntropySeed());
            _attempt     = 0;
        }

        // High-entropy seed for the per-instance Random.  Avoids the
        // System.Clock-derived default seed of `new Random()`, which
        // collides across instances created within the same tick — the
        // reconnect-storm scenario described in the constructor docs.
        // Uses a 4-byte CSPRNG draw and folds it into an int.
        private static int NewEntropySeed()
        {
            var buf = new byte[4];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(buf);
            }
            return BitConverter.ToInt32(buf, 0);
        }

        /// <summary>
        /// Draw the next delay and increment the attempt counter.
        /// </summary>
        public TimeSpan NextDelay()
        {
            var cap = ComputeExponentialCapMs(_attempt, _baseDelayMs, _maxDelayMs);
            // Random.Next(maxExclusive) returns [0, maxExclusive).  Compute
            // the exclusive upper bound in long-domain arithmetic so a
            // caller-supplied `maxDelayMs == int.MaxValue` (legal per the
            // constructor) cannot wrap `cap + 1` to int.MinValue and surface
            // an ArgumentOutOfRangeException out of Random.Next on the very
            // first call.  When `cap` is already saturated at int.MaxValue
            // the +1 saturation cannot increase the range further, so we
            // clamp the upper bound at int.MaxValue and the random draw
            // remains uniform over [0, int.MaxValue).
            int upperExclusive = cap == int.MaxValue ? int.MaxValue : cap + 1;
            var ms = _rng.Next(upperExclusive);
            // Saturating increment — once the backoff has saturated at
            // maxDelayMs the exact attempt number is irrelevant, so we clamp
            // at MaxAttemptForBackoff instead of allowing the counter to
            // approach int.MaxValue.  The previous `checked(_attempt + 1)`
            // would have thrown OverflowException after ~2 billion calls,
            // surfacing as an unhandled exception out of an app's reconnect
            // coroutine.  Capping is the conservative fix because callers
            // already observe a saturated delay long before this matters.
            if (_attempt < MaxAttemptForBackoff) _attempt++;
            return TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>
        /// Clear the attempt counter.  Call after a successful connection so
        /// the next outage starts from the base delay again.
        /// </summary>
        public void Reset() => _attempt = 0;

        /// <summary>
        /// Deterministic upper bound on the jittered delay for a given attempt,
        /// computed as <c>min(maxDelayMs, baseDelayMs × 2^attempt)</c>.
        /// The actual delay returned by <see cref="NextDelay"/> is uniform in
        /// <c>[0, this value]</c>.
        /// </summary>
        /// <remarks>
        /// <para>Exposed static for use in unit tests without constructing a
        /// backoff instance.</para>
        /// <para>Left-shift is avoided for <paramref name="attempt"/> values
        /// large enough to overflow <see cref="int"/>: we detect the saturation
        /// point where the exponential already exceeds <paramref name="maxDelayMs"/>
        /// and return <paramref name="maxDelayMs"/> directly.</para>
        /// </remarks>
        public static int ComputeExponentialCapMs(int attempt, int baseDelayMs, int maxDelayMs)
        {
            if (attempt < 0) throw new ArgumentOutOfRangeException(nameof(attempt));
            if (baseDelayMs <= 0) throw new ArgumentOutOfRangeException(nameof(baseDelayMs));
            if (maxDelayMs  < baseDelayMs) throw new ArgumentOutOfRangeException(nameof(maxDelayMs));

            // Guard against overflow: once 2^attempt × baseDelayMs would exceed
            // maxDelayMs we can short-circuit.  The log2(max/base) boundary is
            // cheap to compute.
            //
           // Example: baseDelayMs = 1000, maxDelayMs = 30_000 → saturation at
            // attempt = 5 (2^5 × 1000 = 32 000 > 30 000).  For attempts ≥ 5 we
            // return 30 000 without attempting the shift.
            if (attempt >= 30) return maxDelayMs; // 2^30 × 1ms already ≈ 12 days

            long exp = (long)baseDelayMs << attempt; // 2^attempt × baseDelayMs
            return exp >= maxDelayMs ? maxDelayMs : (int)exp;
        }
    }
}
