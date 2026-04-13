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
    }
}
