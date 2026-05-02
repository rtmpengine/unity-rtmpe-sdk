// RTMPE SDK — Tests/Runtime/ClockSkewInterpolatorTests.cs
//
// Regression tests for the sender-tick / clock-skew path in
// NetworkTransformInterpolator.AddStateFromSenderTick
// (SDK_REVIEW_REPORT.md §8 — "clock-skew with late-join delay"):
//
//   A late-joining client starts receiving state snapshots whose sender tick
//   is already large (e.g. 10 000 at 30 Hz = 333 s into the session).
//   The receiver clock-offset EMA must correctly compute the offset on the
//   very first sample so the buffered timestamp falls inside the interpolation
//   window rather than in the far future or far past.
//
// Additional adversarial scenarios covered:
//   • Forward-skew guard: states whose receiver-domain timestamp exceeds
//     Time.timeAsDouble + 10 s are silently dropped.
//   • Out-of-order rejection: a tick ≤ latestSenderTick is ignored.
//   • EMA convergence: after enough consistent-offset packets the estimate
//     settles to within 0.01 s of the true offset.
//   • Wrap-around: a tick=0 arriving after tick=uint.MaxValue−1 is accepted
//     by the wrap-safe (int)(a−b) > 0 comparison.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    [Category("ClockSkew")]
    public class ClockSkewInterpolatorTests
    {
        // 30 Hz tick interval — matches the default production tick rate.
        private const double TickInterval30Hz = 1.0 / 30.0;

        private static TransformState MakeState(float x = 0f) => new TransformState
        {
            Position = new Vector3(x, 0f, 0f),
            Rotation = Quaternion.identity,
            Scale    = Vector3.one,
        };

        // ── Late-join: first packet at a large sender tick ─────────────────────

        [Test]
        [Description("First packet at a large sender tick sets ClockOffsetEstimate = receiverNow − senderTime.")]
        public void AddStateFromSenderTick_LateJoin_SetsCorrectClockOffset()
        {
            var go = new GameObject("LateJoin_Offset");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                // Late-joining at tick 10 000 (333 s of server uptime at 30 Hz).
                const uint tick = 10_000u;
                const double receiverNow = 5.0;
                double senderTime = tick * TickInterval30Hz;

                interp.AddStateFromSenderTick(MakeState(1f), tick, receiverNow, TickInterval30Hz);

                double expectedOffset = receiverNow - senderTime;
                Assert.AreEqual(expectedOffset, interp.ClockOffsetEstimate, 1e-9,
                    "Initial ClockOffsetEstimate must equal receiverNow − senderTime exactly.");
                Assert.AreEqual(1, interp.BufferCount,
                    "First packet must be accepted and buffered.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Forward-skew guard ─────────────────────────────────────────────────

        [Test]
        [Description("State whose receiver-domain timestamp > Time.timeAsDouble + 10 s is silently rejected.")]
        public void AddStateFromSenderTick_ForwardSkewBeyondLimit_Rejected()
        {
            var go = new GameObject("ForwardSkew");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                // receiverNow = 100.0 → timestamp = 100.0 > Time.timeAsDouble(≈0) + 10 s
                interp.AddStateFromSenderTick(MakeState(), 1u, receiverNow: 100.0, TickInterval30Hz);

                Assert.AreEqual(0, interp.BufferCount,
                    "State with far-future receiver timestamp must be rejected by the forward-skew guard.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        [Description("State whose receiver-domain timestamp is within the skew limit is accepted.")]
        public void AddStateFromSenderTick_ForwardSkewWithinLimit_Accepted()
        {
            var go = new GameObject("ForwardSkewOk");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                // receiverNow = 5.0 → timestamp = 5.0 ≤ Time.timeAsDouble(≈0) + 10 s
                interp.AddStateFromSenderTick(MakeState(), 1u, receiverNow: 5.0, TickInterval30Hz);

                Assert.AreEqual(1, interp.BufferCount,
                    "State within the skew limit must be accepted.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Out-of-order rejection ─────────────────────────────────────────────

        [Test]
        [Description("A sender tick ≤ latestSenderTick must be silently rejected.")]
        public void AddStateFromSenderTick_OutOfOrderTick_Rejected()
        {
            var go = new GameObject("OutOfOrder");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                interp.AddStateFromSenderTick(MakeState(1f), 5u, 2.0, TickInterval30Hz);
                Assert.AreEqual(1, interp.BufferCount, "precondition: first packet must be accepted");

                // Tick 3 is older than tick 5 — must be rejected.
                interp.AddStateFromSenderTick(MakeState(2f), 3u, 2.1, TickInterval30Hz);
                Assert.AreEqual(1, interp.BufferCount,
                    "Out-of-order tick must be silently rejected — buffer must not grow.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        [Description("A duplicate tick (equal to latestSenderTick) is also rejected.")]
        public void AddStateFromSenderTick_DuplicateTick_Rejected()
        {
            var go = new GameObject("DuplicateTick");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                interp.AddStateFromSenderTick(MakeState(1f), 5u, 2.0, TickInterval30Hz);
                interp.AddStateFromSenderTick(MakeState(2f), 5u, 2.1, TickInterval30Hz);

                Assert.AreEqual(1, interp.BufferCount,
                    "Duplicate sender tick must be rejected — only the first survives.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── EMA convergence ────────────────────────────────────────────────────

        [Test]
        [Description("After 200 packets with a constant 2.0 s offset, ClockOffsetEstimate converges to within 0.01 s.")]
        public void AddStateFromSenderTick_ConsistentOffset_EmaConverges()
        {
            var go = new GameObject("EmaConverge");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 64, interpolationDelay: 0.1f);

                // Use a tight tick interval so senderTime stays small and
                // receiverNow never exceeds the 10 s skew limit.
                const double trueOffset  = 2.0;
                const double tickInterval = 1.0 / 200.0;  // 5 ms per tick
                const int    packets      = 200;

                for (int i = 1; i <= packets; i++)
                {
                    double senderTime  = i * tickInterval;
                    double receiverNow = senderTime + trueOffset;
                    interp.AddStateFromSenderTick(MakeState((float)i), (uint)i, receiverNow, tickInterval);
                }

                Assert.AreEqual(trueOffset, interp.ClockOffsetEstimate, 0.01,
                    "After 200 constant-offset packets, EMA must converge to within 0.01 s of true offset.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Late-join: sequential ticks populate buffer correctly ──────────────

        [Test]
        [Description("After a late-join at a large tick, five sequential ticks are all accepted and buffered.")]
        public void AddStateFromSenderTick_LateJoin_SubsequentTicksBufferedCorrectly()
        {
            var go = new GameObject("LateJoin_Buffer");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                const uint   baseTick       = 10_000u;
                const double baseReceiverNow = 5.0;

                for (int i = 0; i < 5; i++)
                {
                    uint   tick        = (uint)(baseTick + i);
                    double receiverNow = baseReceiverNow + i * TickInterval30Hz;
                    interp.AddStateFromSenderTick(MakeState((float)i), tick, receiverNow, TickInterval30Hz);
                }

                Assert.AreEqual(5, interp.BufferCount,
                    "All 5 sequential ticks after a late-join must be accepted.");
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── Wrap-safe tick comparison ──────────────────────────────────────────

        [Test]
        [Description("Tick 0 arriving after uint.MaxValue-1 is accepted via wrap-safe (int)(a-b) > 0 comparison.")]
        public void AddStateFromSenderTick_TickWrapAround_AcceptedAfterWrap()
        {
            var go = new GameObject("TickWrap");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);

                // Seed with a tick near uint.MaxValue; receiverNow chosen so that
                // the computed timestamp (= receiverNow) stays within the skew limit.
                uint   nearMax     = uint.MaxValue - 1;
                double receiverNow = 2.0;
                interp.AddStateFromSenderTick(MakeState(0f), nearMax, receiverNow, TickInterval30Hz);
                Assert.AreEqual(1, interp.BufferCount, "Near-max tick must be accepted.");

                // Tick 0 wraps past uint.MaxValue.
                // (int)(0u − (uint.MaxValue−1)) = (int)(2u) = 2 > 0 → accepted.
                interp.AddStateFromSenderTick(MakeState(1f), 0u, receiverNow + TickInterval30Hz, TickInterval30Hz);
                Assert.AreEqual(2, interp.BufferCount,
                    "Post-wrap tick 0 must be accepted as newer than uint.MaxValue−1.");
            }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
