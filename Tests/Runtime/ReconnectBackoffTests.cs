// RTMPE SDK — Tests/Runtime/ReconnectBackoffTests.cs
//
// Unit tests for the N-3 exponential-backoff-with-full-jitter reconnect policy.
// Covers the T-N3-01 .. T-N3-05 acceptance criteria from docs/reports/
// production-gaps-2026-04-21.md:
//
//   T-N3-01  First attempt delay is bounded by baseDelay.
//   T-N3-02  Upper bound doubles each attempt (until clamped).
//   T-N3-03  Delay is clamped at maxDelay.
//   T-N3-04  Jitter produces varied delays for concurrent clients.
//   T-N3-05  Reset() restores attempt counter to zero.
//
// Pure C# — runs in Edit Mode. The tests inject a fixed RNG seed so each
// expectation is fully deterministic.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Reconnect")]
    public class ReconnectBackoffTests
    {
        private const int BaseMs = 1_000;
        private const int MaxMs  = 30_000;

        // ── T-N3-01 ──────────────────────────────────────────────────────────

        [Test]
        public void FirstAttemptDelay_IsBoundedByBaseDelay()
        {
            // Full-Jitter contract: first draw ∈ [0, baseDelay].  Verify for a
            // generous sample that no draw ever exceeds baseDelay.
            var backoff = new ReconnectBackoff(BaseMs, MaxMs, seed: 12345);
            for (int i = 0; i < 1_000; i++)
            {
                backoff.Reset();
                var d = backoff.NextDelay();
                Assert.LessOrEqual(d.TotalMilliseconds, BaseMs,
                    "attempt=0 delay must not exceed baseDelayMs");
                Assert.GreaterOrEqual(d.TotalMilliseconds, 0);
            }
        }

        // ── T-N3-02 ──────────────────────────────────────────────────────────

        [Test]
        public void SubsequentAttempts_DoubleTheExponentialCap()
        {
            // The ceiling of the Full-Jitter distribution is 2× the previous
            // ceiling until it hits maxDelayMs.
            Assert.AreEqual(1_000,  ReconnectBackoff.ComputeExponentialCapMs(0, BaseMs, MaxMs));
            Assert.AreEqual(2_000,  ReconnectBackoff.ComputeExponentialCapMs(1, BaseMs, MaxMs));
            Assert.AreEqual(4_000,  ReconnectBackoff.ComputeExponentialCapMs(2, BaseMs, MaxMs));
            Assert.AreEqual(8_000,  ReconnectBackoff.ComputeExponentialCapMs(3, BaseMs, MaxMs));
            Assert.AreEqual(16_000, ReconnectBackoff.ComputeExponentialCapMs(4, BaseMs, MaxMs));
        }

        // ── T-N3-03 ──────────────────────────────────────────────────────────

        [Test]
        public void Delay_IsClampedAtMaxDelay()
        {
            // At attempt=5, 2^5 × 1000 = 32_000 > 30_000 → must saturate.
            Assert.AreEqual(MaxMs, ReconnectBackoff.ComputeExponentialCapMs(5, BaseMs, MaxMs));
            Assert.AreEqual(MaxMs, ReconnectBackoff.ComputeExponentialCapMs(20, BaseMs, MaxMs));
            Assert.AreEqual(MaxMs, ReconnectBackoff.ComputeExponentialCapMs(100, BaseMs, MaxMs));

            // No draw may exceed maxDelayMs, regardless of attempt depth.
            var backoff = new ReconnectBackoff(BaseMs, MaxMs, seed: 99);
            for (int i = 0; i < 50; i++)
            {
                var d = backoff.NextDelay();
                Assert.LessOrEqual(d.TotalMilliseconds, MaxMs,
                    $"attempt={i} exceeded maxDelayMs");
            }
        }

        // ── T-N3-04 ──────────────────────────────────────────────────────────

        [Test]
        public void Jitter_ProducesVariation_AcrossConcurrentClients()
        {
            // 100 independent clients enter reconnect at the same simulated
            // tick.  With Full Jitter, their first-attempt delays must spread
            // across the [0, baseDelay] range — if they were all identical
            // we'd have a thundering herd.
            var delays = Enumerable.Range(0, 100)
                .Select(i => new ReconnectBackoff(BaseMs, MaxMs, seed: i).NextDelay().TotalMilliseconds)
                .ToList();

            Assert.Greater(delays.Distinct().Count(), 50,
                "jitter must produce a wide spread; fewer than 50 unique values is a thundering-herd risk");

            // Sanity: mean should sit near the midpoint of [0, baseDelay] — a
            // value near 0 or near baseDelay would indicate broken jitter.
            var mean = delays.Average();
            Assert.That(mean, Is.InRange(BaseMs * 0.25, BaseMs * 0.75),
                $"Full-Jitter mean should be ≈ baseDelay/2; got {mean:F1}ms");
        }

        // ── T-N3-05 ──────────────────────────────────────────────────────────

        [Test]
        public void Reset_RestoresAttemptCounterToZero()
        {
            var backoff = new ReconnectBackoff(BaseMs, MaxMs, seed: 7);
            Assert.AreEqual(0, backoff.Attempt);

            backoff.NextDelay(); backoff.NextDelay(); backoff.NextDelay();
            Assert.AreEqual(3, backoff.Attempt, "counter must track calls");

            backoff.Reset();
            Assert.AreEqual(0, backoff.Attempt, "Reset must zero the counter");

            // After reset, the next draw is bounded by baseDelay again — not
            // by the saturated maxDelay that would apply at attempt=3.
            var postReset = backoff.NextDelay();
            Assert.LessOrEqual(postReset.TotalMilliseconds, BaseMs,
                "post-Reset draw must fall back to the attempt=0 ceiling");
        }

        // ── Guardrails ───────────────────────────────────────────────────────

        [Test]
        public void Constructor_RejectsInvalidBounds()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ReconnectBackoff(baseDelayMs: 0, maxDelayMs: 1000));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ReconnectBackoff(baseDelayMs: -1, maxDelayMs: 1000));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new ReconnectBackoff(baseDelayMs: 2000, maxDelayMs: 1000));
        }

        [Test]
        public void ComputeExponentialCap_HandlesLargeAttemptWithoutOverflow()
        {
            // Regression: a naive `baseDelayMs << attempt` overflows int at
            // attempt ≈ 31.  Must return maxDelayMs instead of a negative
            // number or a wrapped positive value.
            for (int attempt = 30; attempt < 10_000; attempt++)
            {
                var cap = ReconnectBackoff.ComputeExponentialCapMs(attempt, BaseMs, MaxMs);
                Assert.AreEqual(MaxMs, cap,
                    $"attempt={attempt} must saturate at maxDelayMs");
            }
        }

        [Test]
        public void Distribution_CoversFullRange()
        {
            // Draw many samples from attempt=0 and confirm they cover both
            // the lower and upper end of [0, baseDelay] — i.e. Full Jitter
            // behaves as advertised (not biased toward one end).
            var samples = new List<double>();
            for (int seed = 0; seed < 500; seed++)
            {
                var b = new ReconnectBackoff(BaseMs, MaxMs, seed);
                samples.Add(b.NextDelay().TotalMilliseconds);
            }
            Assert.Less(samples.Min(), BaseMs * 0.1,
                $"distribution should reach close to 0 (got min={samples.Min():F1})");
            Assert.Greater(samples.Max(), BaseMs * 0.9,
                $"distribution should reach close to baseDelay (got max={samples.Max():F1})");
        }
    }
}
