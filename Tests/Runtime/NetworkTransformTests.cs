// RTMPE SDK — Tests/Runtime/NetworkTransformTests.cs
//
// NUnit Edit-Mode tests for NetworkTransform.
//
// Tests verify the change-detection logic (HasPositionChanged,
// HasRotationChanged), baseline management (MarkClean), state capture
// (GetState), and state application (ApplyState) against real Unity
// transforms.
//
// These tests do NOT exercise the Update() send path — that path calls
// NetworkManager.Instance.SendData() which requires a real network thread.
// The send path is covered by integration tests.
//
// Setup/Teardown pattern matches NetworkBehaviourTests.cs:
//   SetUp   — create a minimal NetworkManager + the test GameObject.
//   TearDown — DestroyImmediate both so the singleton is clean.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    public class NetworkTransformTests
    {
        private GameObject        _nmGo;
        private NetworkManager    _manager;
        private GameObject        _go;
        private NetworkTransform  _nt;

        [SetUp]
        public void SetUp()
        {
            // NetworkManager singleton is required by NetworkBehaviour.IsOwner.
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _go = new GameObject("NT_Test");
            _nt = _go.AddComponent<NetworkTransform>();

            // Position the object at the origin and establish a clean baseline.
            _go.transform.position   = Vector3.zero;
            _go.transform.rotation   = Quaternion.identity;
            _go.transform.localScale = Vector3.one;
            _nt.MarkClean();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go   != null) { Object.DestroyImmediate(_go);   _go   = null; }
            if (_nmGo != null) { Object.DestroyImmediate(_nmGo); _nmGo = null; }
        }

        // ── HasPositionChanged ────────────────────────────────────────────────

        [Test]
        [Description("Moving beyond the default 0.01-unit threshold sets HasPositionChanged.")]
        public void HasPositionChanged_WhenMovedBeyondThreshold_ReturnsTrue()
        {
            _go.transform.position = new Vector3(1f, 0f, 0f); // >> 0.01 threshold

            Assert.IsTrue(_nt.HasPositionChanged);
        }

        [Test]
        [Description("A sub-threshold move (< 0.01 units) does not set HasPositionChanged.")]
        public void HasPositionChanged_WithinThreshold_ReturnsFalse()
        {
            // 0.005 units is half the default threshold of 0.01 — below the limit.
            _go.transform.position = new Vector3(0.005f, 0f, 0f);

            Assert.IsFalse(_nt.HasPositionChanged);
        }

        // ── HasRotationChanged ────────────────────────────────────────────────

        [Test]
        [Description("A 90-degree rotation exceeds the default 0.1-degree threshold.")]
        public void HasRotationChanged_WhenRotatedBeyondThreshold_ReturnsTrue()
        {
            _go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            Assert.IsTrue(_nt.HasRotationChanged);
        }

        // ── MarkClean ─────────────────────────────────────────────────────────

        [Test]
        [Description("MarkClean resets HasPositionChanged to false after a move.")]
        public void MarkClean_AfterPositionChange_ResetsDirtyFlag()
        {
            _go.transform.position = new Vector3(5f, 0f, 0f);
            Assert.IsTrue(_nt.HasPositionChanged, "precondition: dirty before MarkClean");

            _nt.MarkClean();

            Assert.IsFalse(_nt.HasPositionChanged, "should be clean after MarkClean");
        }

        // ── GetState ──────────────────────────────────────────────────────────

        [Test]
        [Description("GetState captures the current position, rotation, and scale exactly.")]
        public void GetState_ReturnsCurrentTransformValues()
        {
            var expectedPos   = new Vector3(3f, 7f, -2f);
            var expectedRot   = Quaternion.Euler(45f, 30f, 60f);
            var expectedScale = new Vector3(2f, 2f, 2f);

            _go.transform.position   = expectedPos;
            _go.transform.rotation   = expectedRot;
            _go.transform.localScale = expectedScale;

            var state = _nt.GetState();

            Assert.AreEqual(expectedPos,   state.Position, "Position");
            Assert.AreEqual(expectedRot,   state.Rotation, "Rotation");
            Assert.AreEqual(expectedScale, state.Scale,    "Scale");
        }

        // ── ApplyState ────────────────────────────────────────────────────────

        [Test]
        [Description("ApplyState sets position and rotation (default flags); scale is not synced by default.")]
        public void ApplyState_WithDefaultFlags_AppliesPositionAndRotation()
        {
            var expectedPos   = new Vector3(10f, 5f, -3f);
            var expectedRot   = Quaternion.Euler(0f, 90f, 0f);
            var originalScale = _go.transform.localScale; // should remain unchanged

            var incomingState = new TransformState
            {
                Position = expectedPos,
                Rotation = expectedRot,
                Scale    = new Vector3(99f, 99f, 99f), // should be ignored (syncScale=false)
            };

            _nt.ApplyState(incomingState);

            Assert.AreEqual(expectedPos,   _go.transform.position,   "Position should be applied");
            Assert.AreEqual(expectedRot,   _go.transform.rotation,   "Rotation should be applied");
            Assert.AreEqual(originalScale, _go.transform.localScale, "Scale must NOT change (syncScale=false)");
        }
    }
}
