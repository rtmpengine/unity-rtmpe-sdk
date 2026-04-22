// RTMPE SDK — Runtime/Core/ReconnectBackoff.cs
//
// Capped exponential backoff with Full Jitter — the industry-standard reconnect
// policy for distributed systems. Based on AWS Architecture Blog:
// "Exponential Backoff And Jitter" (Marc Brooker, 2015).
//
// Algorithm:
//     baseExp  = min(maxDelay, baseDelay * 2^attempt)
//     delay    = random_uniform(0, baseExp)
//
// Properties:
//   • Exponential growth between attempts prevents reconnect storms on long
//     outages (the server is not hammered by retry traffic while it recovers).
//   • Full Jitter de-correlates reconnect times across many clients — without
//     it, every client that dropped at t=0 would retry in lock-step at t=1s,
//     t=2s, t=4s, …, producing synchronized load spikes that are the
//     canonical cause of cascading failure during gateway recovery.
//   • The attempt counter is explicit so callers can reset it on success
//     without discarding the RNG state.
//
// Thread safety:
//   • Each instance owns its own System.Random. Callers on a single logical
//     connection (e.g. one NetworkManager) must not share a single backoff
//     instance across threads without external synchronization.
//
// Usage:
//     var backoff = new ReconnectBackoff();
//     while (!Connected && backoff.Attempt < MaxAttempts)
//     {
//         var delay = backoff.NextDelay();
//         yield return new WaitForSeconds((float)delay.TotalSeconds);
//         TryConnect();
//     }
//     if (Connected) backoff.Reset();

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
        /// null in production so the RNG is seeded from the system clock.
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
            _rng         = seed.HasValue ? new Random(seed.Value) : new Random();
            _attempt     = 0;
        }

        /// <summary>
        /// Draw the next delay and increment the attempt counter.
        /// </summary>
        public TimeSpan NextDelay()
        {
            var cap = ComputeExponentialCapMs(_attempt, _baseDelayMs, _maxDelayMs);
            // Random.Next(maxExclusive) returns [0, maxExclusive).  Add 1 so
            // the upper bound is inclusive — avoids the awkward "you can never
            // hit exactly cap" artifact that makes T-N3-03 flaky.
            var ms = _rng.Next(cap + 1);
            _attempt = checked(_attempt + 1); // overflow at ~2B is a bug
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
