// RTMPE SDK — Tests/Runtime/NetworkTransformInterpolatorTests.cs
//
// NUnit Edit-Mode tests for NetworkTransformInterpolator.
//
// Test strategy:
//   • All interpolation logic is tested via the public TryInterpolate(double)
//     method, which is a pure function decoupled from the Unity frame loop.
//     This avoids the need to call Update() or advance Time — tests pass any
//     renderTime value they need.
//   • Buffer management (AddState, overflow trim) is tested by inspecting
//     BufferCount after a known number of AddState calls.
//   • MonoBehaviour lifecycle: SetUp creates a real GameObject and AddComponent
//     so that Unity's reflection-based initialisation runs normally.  TearDown
//     calls DestroyImmediate to clean up scene state between tests.
//   • ConfigureForTest (internal, accessible via InternalsVisibleTo) is used to
//     set buffer parameters without Inspector serialisation.
//
// Fixtures covered:
//   1.  EmptyBuffer_TryInterpolate_ReturnsFalse
//   2.  SingleState_TryInterpolate_ReturnsFalse
//   3.  TwoStates_RenderTimeBeforeFirst_ReturnsFalse
//   4.  TwoStates_RenderTimeAfterLast_ReturnsFalse
//   5.  TwoStates_RenderTimeAtFromTimestamp_ReturnsFromState
//   6.  TwoStates_RenderTimeAtToTimestamp_ReturnsToState
//   7.  TwoStates_RenderTimeMidpoint_LerpsPosition
//   8.  TwoStates_RenderTimeMidpoint_SlerpsRotation
//   9.  TwoStates_EqualTimestamps_ReturnsFromState   (P-4 division-by-zero guard)
//  10.  ThreeStates_RenderTimeInSecondSegment_UsesCorrectPair
//  11.  AddState_ExceedsBufferSize_TrimsOldest
//  12.  AddState_WithinBufferSize_DoesNotTrim

using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    public class NetworkTransformInterpolatorTests
    {
        private GameObject                   _go;
        private NetworkTransformInterpolator _interp;

        // ── Helpers ───────────────────────────────────────────────────────────

        private static TransformState MakeState(
            float px, float py, float pz,
            float rx = 0f, float ry = 0f, float rz = 0f, float rw = 1f,
            float sx = 1f, float sy = 1f, float sz = 1f)
            => new TransformState
            {
                Position = new Vector3(px, py, pz),
                Rotation = new Quaternion(rx, ry, rz, rw),
                Scale    = new Vector3(sx, sy, sz),
            };

        // ── SetUp / TearDown ──────────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            _go     = new GameObject("Interp_Test");
            _interp = _go.AddComponent<NetworkTransformInterpolator>();

            // Use small bufferSize=5 for overflow tests; interpolationDelay
            // is irrelevant for TryInterpolate() tests because we pass
            // renderTime explicitly.
            _interp.ConfigureForTest(bufferSize: 5, interpolationDelay: 0.1f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) { Object.DestroyImmediate(_go); _go = null; }
        }

        // ── 1: Empty buffer ───────────────────────────────────────────────────

        [Test]
        [Description("Empty buffer — TryInterpolate returns false.")]
        public void EmptyBuffer_TryInterpolate_ReturnsFalse()
        {
            bool ok = _interp.TryInterpolate(1.0, out _);

            Assert.IsFalse(ok);
        }

        // ── 2: Single state ───────────────────────────────────────────────────

        [Test]
        [Description("One buffered state — TryInterpolate needs ≥ 2 states; returns false.")]
        public void SingleState_TryInterpolate_ReturnsFalse()
        {
            _interp.AddState(MakeState(0f, 0f, 0f), timestamp: 0.0);

            bool ok = _interp.TryInterpolate(0.0, out _);

            Assert.IsFalse(ok);
        }

        // ── 3: renderTime before first state ─────────────────────────────────

        [Test]
        [Description("renderTime earlier than all buffered states — no 'from' anchor exists; returns false.")]
        public void TwoStates_RenderTimeBeforeFirst_ReturnsFalse()
        {
            _interp.AddState(MakeState(0f, 0f, 0f), timestamp: 1.0);
            _interp.AddState(MakeState(10f, 0f, 0f), timestamp: 2.0);

            bool ok = _interp.TryInterpolate(0.5, out _); // before t=1.0

            Assert.IsFalse(ok);
        }

        // ── 4: renderTime after last state ───────────────────────────────────

        [Test]
        [Description("renderTime later than all buffered states — no 'to' target exists; returns false.")]
        public void TwoStates_RenderTimeAfterLast_ReturnsFalse()
        {
            _interp.AddState(MakeState(0f, 0f, 0f), timestamp: 1.0);
            _interp.AddState(MakeState(10f, 0f, 0f), timestamp: 2.0);

            bool ok = _interp.TryInterpolate(3.0, out _); // after t=2.0

            Assert.IsFalse(ok);
        }

        // ── 5: renderTime == from.Timestamp (t = 0) ──────────────────────────

        [Test]
        [Description("renderTime == from timestamp → t = 0 → result equals 'from' state.")]
        public void TwoStates_RenderTimeAtFromTimestamp_ReturnsFromState()
        {
            var fromState = MakeState(0f, 0f, 0f);
            var toState   = MakeState(10f, 0f, 0f);
            _interp.AddState(fromState, timestamp: 1.0);
            _interp.AddState(toState,   timestamp: 2.0);

            bool ok = _interp.TryInterpolate(1.0, out TransformState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(fromState.Position, result.Position, "Position should match 'from'.");
        }

        // ── 6: renderTime == to.Timestamp (t = 1) ────────────────────────────

        [Test]
        [Description("renderTime == to timestamp → t = 1 → result equals 'to' state.")]
        public void TwoStates_RenderTimeAtToTimestamp_ReturnsToState()
        {
            var fromState = MakeState(0f, 0f, 0f);
            var toState   = MakeState(10f, 0f, 0f);
            _interp.AddState(fromState, timestamp: 1.0);
            _interp.AddState(toState,   timestamp: 2.0);

            bool ok = _interp.TryInterpolate(2.0, out TransformState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(toState.Position, result.Position, "Position should match 'to'.");
        }

        // ── 7: Midpoint position Lerp ─────────────────────────────────────────

        [Test]
        [Description("renderTime exactly halfway → position is the midpoint of from and to.")]
        public void TwoStates_RenderTimeMidpoint_LerpsPosition()
        {
            _interp.AddState(MakeState(0f, 0f, 0f),   timestamp: 0.0);
            _interp.AddState(MakeState(10f, 0f, 0f),  timestamp: 1.0);

            // renderTime = 0.5 → t = (0.5 - 0) / (1.0 - 0) = 0.5
            bool ok = _interp.TryInterpolate(0.5, out TransformState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(5f, result.Position.x, 0.0001f, "X should be midpoint 5.");
            Assert.AreEqual(0f, result.Position.y, 0.0001f, "Y should remain 0.");
            Assert.AreEqual(0f, result.Position.z, 0.0001f, "Z should remain 0.");
        }

        // ── 8: Midpoint rotation Slerp ────────────────────────────────────────

        [Test]
        [Description("renderTime exactly halfway → rotation is Slerp(from, to, 0.5).")]
        public void TwoStates_RenderTimeMidpoint_SlerpsRotation()
        {
            var fromRot = Quaternion.identity;                        // 0° around Y
            var toRot   = Quaternion.Euler(0f, 90f, 0f);             // 90° around Y

            _interp.AddState(
                new TransformState { Position = Vector3.zero, Rotation = fromRot, Scale = Vector3.one },
                timestamp: 0.0);
            _interp.AddState(
                new TransformState { Position = Vector3.zero, Rotation = toRot, Scale = Vector3.one },
                timestamp: 1.0);

            bool ok = _interp.TryInterpolate(0.5, out TransformState result);

            Assert.IsTrue(ok);

            Quaternion expected = Quaternion.Slerp(fromRot, toRot, 0.5f); // 45° around Y
            Assert.AreEqual(expected.x, result.Rotation.x, 0.0001f, "Rotation.x");
            Assert.AreEqual(expected.y, result.Rotation.y, 0.0001f, "Rotation.y");
            Assert.AreEqual(expected.z, result.Rotation.z, 0.0001f, "Rotation.z");
            Assert.AreEqual(expected.w, result.Rotation.w, 0.0001f, "Rotation.w");
        }

        // ── 9: Equal timestamps (division-by-zero guard — P-4) ───────────────

        [Test]
        [Description("from and to share the same timestamp → t = 0 (no division by zero); returns 'from' state.")]
        public void TwoStates_EqualTimestamps_ReturnsFromState()
        {
            var fromState = MakeState(0f, 0f, 0f);
            var toState   = MakeState(99f, 0f, 0f);

            _interp.AddState(fromState, timestamp: 1.0);
            _interp.AddState(toState,   timestamp: 1.0); // same timestamp

            bool ok = _interp.TryInterpolate(1.0, out TransformState result);

            Assert.IsTrue(ok, "Should succeed even with equal timestamps.");
            // t defaults to 0 so result is the 'from' state.
            Assert.AreEqual(0f, result.Position.x, 0.0001f, "X should be from-state (0), not to-state (99).");
        }

        // ── 10: Three states — second segment ────────────────────────────────

        [Test]
        [Description("Three buffered states: renderTime in the second segment uses states[1] and states[2].")]
        public void ThreeStates_RenderTimeInSecondSegment_UsesCorrectPair()
        {
            _interp.AddState(MakeState(0f,  0f, 0f), timestamp: 0.0);
            _interp.AddState(MakeState(10f, 0f, 0f), timestamp: 1.0);
            _interp.AddState(MakeState(30f, 0f, 0f), timestamp: 2.0);

            // renderTime = 1.5 is in the [1.0, 2.0] segment.
            // from=(10, 0, 0), to=(30, 0, 0), t = 0.5 → expected X = 20.
            bool ok = _interp.TryInterpolate(1.5, out TransformState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(20f, result.Position.x, 0.0001f, "X should be 20 (midpoint of 10 and 30).");
        }

        // ── 11: Buffer overflow — trims oldest ────────────────────────────────

        [Test]
        [Description("Adding more states than bufferSize keeps only the newest bufferSize states.")]
        public void AddState_ExceedsBufferSize_TrimsOldest()
        {
            // bufferSize was set to 5 in SetUp.
            for (int i = 0; i < 7; i++)
                _interp.AddState(MakeState(i, 0f, 0f), timestamp: i);

            Assert.AreEqual(5, _interp.BufferCount, "Buffer must not exceed configured bufferSize.");
        }

        // ── 12: Buffer within cap — no trim ───────────────────────────────────

        [Test]
        [Description("Adding ≤ bufferSize states does not trim any entries.")]
        public void AddState_WithinBufferSize_DoesNotTrim()
        {
            _interp.AddState(MakeState(0f, 0f, 0f), timestamp: 0.0);
            _interp.AddState(MakeState(1f, 0f, 0f), timestamp: 0.1);
            _interp.AddState(MakeState(2f, 0f, 0f), timestamp: 0.2);

            Assert.AreEqual(3, _interp.BufferCount, "All states within cap must be retained.");
        }

        // ── Sender-clock alignment (Tier-1) ───────────────────────────────────
        //
        // These exercise AddStateFromSenderTick — the wire-tick ingress that
        // decouples ordering from receive jitter.  The renderTime read by
        // TryInterpolate must still produce the same lerp result that the
        // receiver-clock path would produce in the absence of jitter, AND
        // crucially must do so even when the receive-time intervals are
        // collapsed by jitter.

        [Test]
        [Description("Sender-tick path: monotonic ticks under jittery receive times still " +
                     "expose a smooth interpolation segment in the buffer.")]
        public void SenderTick_JitteryReceive_PreservesSenderInterval()
        {
            const double tickInterval = 1.0 / 30.0;

            // Two consecutive sender ticks (10, 11) — the sender sent them
            // exactly tickInterval apart, but the receiver clock saw them
            // arrive 5 ms apart due to a one-time burst.  After the EMA
            // converges across the second sample, the stored timestamps
            // should still be ≈ tickInterval apart, NOT 5 ms apart.
            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: 10, receiverNow: 100.000, tickIntervalSeconds: tickInterval);
            _interp.AddStateFromSenderTick(MakeState(1f, 0f, 0f),
                senderTick: 11, receiverNow: 100.005, tickIntervalSeconds: tickInterval);

            Assert.AreEqual(2, _interp.BufferCount,
                "Both sender-stamped states must be enqueued under jittery arrival.");

            // Render at the midpoint between the two sender ticks.  We can
            // recover the expected midpoint by reading the clock-offset and
            // computing the sender-time midpoint, then applying the offset.
            double offset = _interp.ClockOffsetEstimate;
            double midSenderTime = 10.5 * tickInterval;
            double renderTime    = midSenderTime + offset;

            bool ok = _interp.TryInterpolate(renderTime, out TransformState result);

            Assert.IsTrue(ok, "Interpolation must succeed at the sender-time midpoint.");
            // EMA has only seen 2 samples here so the offset estimate is
            // slightly biased; the true sender-paced midpoint sits within
            // ~5% of 0.5.  The receive-paced ordering would put the midpoint
            // somewhere far from 0.5 (closer to 0 or 1 depending on which
            // packet's receive timestamp dominated), so a 0.05 band is tight
            // enough to discriminate the two regimes.
            Assert.AreEqual(0.5f, result.Position.x, 0.05f,
                "Position should be the sender-paced midpoint, not the receive-paced one.");
        }

        [Test]
        [Description("Sender-tick path: out-of-order sender tick (lower than already accepted) " +
                     "is rejected without corrupting the buffer.")]
        public void SenderTick_OutOfOrder_Rejected()
        {
            const double tickInterval = 1.0 / 30.0;

            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: 100, receiverNow: 50.0, tickIntervalSeconds: tickInterval);
            _interp.AddStateFromSenderTick(MakeState(1f, 0f, 0f),
                senderTick: 101, receiverNow: 50.0 + tickInterval, tickIntervalSeconds: tickInterval);

            // Late delivery of an older tick — must be dropped.
            _interp.AddStateFromSenderTick(MakeState(99f, 0f, 0f),
                senderTick: 99, receiverNow: 50.5, tickIntervalSeconds: tickInterval);

            Assert.AreEqual(2, _interp.BufferCount, "Stale sender tick must not be enqueued.");
        }

        [Test]
        [Description("Sender-tick path: tick wrap (uint.MaxValue → 0) is treated as a " +
                     "forward step under RFC 1982 modular arithmetic.")]
        public void SenderTick_WrapBoundary_AcceptsForward()
        {
            const double tickInterval = 1.0 / 30.0;

            // Seed the high-water tick near uint.MaxValue, then push the
            // wrap-forward neighbour.  Forward of uint.MaxValue under the
            // 32-bit ring is 0; (int)(0 - uint.MaxValue) == 1, which is > 0,
            // so the second push must be accepted.
            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: uint.MaxValue, receiverNow: 0.0, tickIntervalSeconds: tickInterval);
            _interp.AddStateFromSenderTick(MakeState(1f, 0f, 0f),
                senderTick: 0u, receiverNow: tickInterval, tickIntervalSeconds: tickInterval);

            Assert.AreEqual(2, _interp.BufferCount,
                "Tick wrap forward must be accepted, not silently dropped.");
        }

        [Test]
        [Description("Sender-tick path: pathological tickInterval (0 / negative / NaN) is " +
                     "rejected without mutating buffer state.")]
        public void SenderTick_InvalidTickInterval_NoOp()
        {
            int beforeCount = _interp.BufferCount;

            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: 1, receiverNow: 0.0, tickIntervalSeconds: 0.0);
            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: 2, receiverNow: 0.0, tickIntervalSeconds: -1.0);
            _interp.AddStateFromSenderTick(MakeState(0f, 0f, 0f),
                senderTick: 3, receiverNow: 0.0, tickIntervalSeconds: double.NaN);

            Assert.AreEqual(beforeCount, _interp.BufferCount,
                "Pathological tickInterval values must be rejected without side effects.");
        }

        [Test]
        [Description("Sender-tick path: clock-offset EMA converges toward the true offset " +
                     "after sustained streaming.")]
        public void SenderTick_ClockOffsetEma_Converges()
        {
            const double tickInterval = 1.0 / 30.0;
            const double trueOffset   = 1234.5; // receiver clock - sender clock

            for (int i = 1; i <= 200; i++)
            {
                double senderTime  = i * tickInterval;
                double receiverNow = senderTime + trueOffset;
                _interp.AddStateFromSenderTick(MakeState(i, 0f, 0f),
                    senderTick: (uint)i, receiverNow: receiverNow,
                    tickIntervalSeconds: tickInterval);
            }

            double offset = _interp.ClockOffsetEstimate;
            Assert.AreEqual(trueOffset, offset, 0.01,
                "EMA should converge to within 10 ms of the true offset after 200 samples.");
        }

        // ── Bracket cursor (positive path) ───────────────────────────────────
        //
        // Render time advances monotonically with the sample stream so the
        // bracket-pair search must produce the correct interpolated x for
        // every renderTime visited, regardless of how the cursor walked the
        // buffer.  Positions are linearly spaced on the timestamp grid so a
        // correct lerp returns a position that matches the timestamp's
        // fractional offset — any cursor bug (stale, off-by-one, walked too
        // far) would produce a wrong x.

        [Test]
        [Description("Bracket cursor: monotonic renderTime sweep returns correct " +
                     "interpolated values for every step.")]
        public void BracketCursor_MonotonicSweep_ReturnsCorrectInterpolation()
        {
            _interp.ConfigureForTest(bufferSize: 16, interpolationDelay: 0.0f);

            // Fill 16 samples: timestamps 0.0, 0.1, ..., 1.5; x = timestamp * 10
            // so the linear interpolation result for any renderTime t is x = 10·t.
            for (int i = 0; i < 16; i++)
                _interp.AddState(MakeState(i, 0f, 0f), i * 0.1);

            // Walk renderTime forward in 1 ms steps from 0.0 to 1.5.
            for (double t = 0.0; t <= 1.5; t += 0.001)
            {
                bool ok = _interp.TryInterpolate(t, out var s);
                Assert.IsTrue(ok, $"Expected a bracket pair for renderTime={t}");
                Assert.AreEqual(t * 10.0, s.Position.x, 0.001,
                    $"Interpolated x must follow x = 10·t at renderTime={t}");
            }
        }

        // ── Bracket cursor (adversarial path) ────────────────────────────────
        //
        // A render-time rewind (clock skew, scene reload, or a fresh stream
        // that mapped behind the cursor under the EMA) must not pin the
        // cursor on a stale window — TryInterpolate must reset and locate the
        // correct earlier pair instead of returning a stale or wrong result.

        [Test]
        [Description("Bracket cursor: rewinding renderTime restarts the search and " +
                     "returns the correct earlier bracket pair.")]
        public void BracketCursor_RenderTimeRewind_RestartsSearch()
        {
            _interp.ConfigureForTest(bufferSize: 16, interpolationDelay: 0.0f);

            for (int i = 0; i < 16; i++)
                _interp.AddState(MakeState(i, 0f, 0f), i * 0.1);

            // Advance the cursor far into the buffer.
            Assert.IsTrue(_interp.TryInterpolate(1.4, out var late));
            Assert.AreEqual(14.0, late.Position.x, 0.001);

            // Rewind to a much earlier renderTime: the cursor must reset and
            // find the correct earlier bracket pair (timestamps 0.2, 0.3).
            Assert.IsTrue(_interp.TryInterpolate(0.25, out var early),
                "After a renderTime rewind the search must restart from logical 0.");
            Assert.AreEqual(2.5, early.Position.x, 0.001,
                "Rewound search must return the early pair, not a stale cursor result.");
        }

        // ── Bracket cursor (out-of-order ingress) ────────────────────────────
        //
        // Adversarial / out-of-order writes that fail the monotonicity guard
        // are silently rejected by AddState — the cursor must not move and
        // subsequent reads must return correct results.  Combined with the
        // ring overwrite path (bufferSize exceeded) the cursor is dragged
        // backward by one logical step per overwrite so it never points past
        // the new oldest entry.

        [Test]
        [Description("Bracket cursor: out-of-order AddState rejections leave the " +
                     "buffer and cursor consistent.")]
        public void BracketCursor_OutOfOrderRejected_BufferConsistent()
        {
            _interp.ConfigureForTest(bufferSize: 8, interpolationDelay: 0.0f);

            // Stable monotonic stream.
            for (int i = 0; i < 8; i++)
                _interp.AddState(MakeState(i, 0f, 0f), i * 0.1);

            // Adversarial bursts of older / equal timestamps (each must be
            // rejected — strict monotonicity).
            int beforeCount = _interp.BufferCount;
            for (int j = 0; j < 32; j++)
                _interp.AddState(MakeState(-1, -1, -1), 0.05); // older than head
            Assert.AreEqual(beforeCount, _interp.BufferCount,
                "Older-than-latest writes must be rejected and the buffer unchanged.");

            // After the rejected burst, a normal interpolation must still
            // land on the genuine pair, not on the rejected garbage.
            Assert.IsTrue(_interp.TryInterpolate(0.35, out var s));
            Assert.AreEqual(3.5, s.Position.x, 0.001,
                "Cursor must not be polluted by rejected out-of-order writes.");
        }

        [Test]
        [Description("Bracket cursor: ring overwrite drags the cursor inward so it " +
                     "never points past the newly-evicted oldest entry.")]
        public void BracketCursor_RingOverwrite_CursorStaysValid()
        {
            _interp.ConfigureForTest(bufferSize: 4, interpolationDelay: 0.0f);

            // Fill capacity.
            for (int i = 0; i < 4; i++)
                _interp.AddState(MakeState(i, 0f, 0f), i * 0.1);

            // Walk the cursor to the latest pair.
            Assert.IsTrue(_interp.TryInterpolate(0.25, out _));

            // Overwrite the buffer entirely with a fresh, contiguous stream.
            for (int i = 4; i < 12; i++)
                _interp.AddState(MakeState(i, 0f, 0f), i * 0.1);

            // The buffer now holds timestamps 0.8, 0.9, 1.0, 1.1.  A
            // renderTime in the new window must locate the correct pair
            // even though the cursor was advanced past the original window.
            Assert.IsTrue(_interp.TryInterpolate(0.95, out var s));
            Assert.AreEqual(9.5, s.Position.x, 0.001,
                "Overwrite must keep the cursor in-bounds for the rotated window.");
        }
    }
}
