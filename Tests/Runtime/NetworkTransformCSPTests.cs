// RTMPE SDK — Tests/Runtime/NetworkTransformCSPTests.cs
//
// NUnit Edit-Mode tests for NetworkTransform client-side prediction (CSP):
//   • GatherInput() default override returns zero input with stamped tick.
//   • ApplyReconciliation() snaps when error > _snapThreshold (2m).
//   • ApplyReconciliation() lerps when error > _lerpThreshold (0.1m) and < _snapThreshold.
//   • ApplyReconciliation() is a no-op when prediction is disabled.
//   • ApplyReconciliation() is a no-op when error <= lerpThreshold.
//
// Internal members are accessible via InternalsVisibleTo("RTMPE.SDK.Tests")
// declared in AssemblyInfo.cs.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("CSP")]
    public class NetworkTransformCSPTests
    {
        private GameObject       _nmGo;
        private NetworkManager   _manager;
        private GameObject       _go;
        private NetworkTransform _nt;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _go = new GameObject("CSP_Test");
            _nt = _go.AddComponent<NetworkTransform>();

            _go.transform.position = Vector3.zero;
            _go.transform.rotation = Quaternion.identity;
            _nt.MarkClean();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go   != null) { Object.DestroyImmediate(_go);   _go   = null; }
            if (_nmGo != null) { Object.DestroyImmediate(_nmGo); _nmGo = null; }
        }

        // ── GatherInput (via CollectInput) ────────────────────────────────────

        [Test]
        [Description("Default GatherInput() returns zero move, no jump; CollectInput stamps the tick.")]
        public void CollectInput_Default_ZeroMoveNoJump_TickStamped()
        {
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");

            var input = _nt.CollectInput(42u);

            Assert.AreEqual(42u, input.Tick);
            Assert.AreEqual(0f,  input.MoveX);
            Assert.AreEqual(0f,  input.MoveY);
            Assert.IsFalse(input.Jump);
        }

        // ── ApplyReconciliation — disabled ────────────────────────────────────

        [Test]
        [Description("ApplyReconciliation is a no-op when _enablePrediction is false.")]
        public void ApplyReconciliation_PredictionDisabled_PositionUnchanged()
        {
            // _enablePrediction defaults to false — no Inspector access needed.
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            _go.transform.position = new Vector3(1, 0, 0);
            _nt.MarkClean();

            // Server says position is at origin (large error).
            var serverState = new TransformState
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };
            _nt.ApplyReconciliation(serverState);

            // Position must be unchanged because prediction is off.
            Assert.AreEqual(new Vector3(1, 0, 0), _go.transform.position);
        }

        // ── ApplyReconciliation — snap ────────────────────────────────────────

        [Test]
        [Description("Error > 2m (snapThreshold) must snap position immediately.")]
        public void ApplyReconciliation_LargeError_SnapsToServerPosition()
        {
            EnablePrediction(_nt);
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            // Client is at (5, 0, 0); server says (0, 0, 0) — 5m error, > 2m snap threshold.
            _go.transform.position = new Vector3(5, 0, 0);
            _nt.MarkClean();

            var serverState = new TransformState
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };
            _nt.ApplyReconciliation(serverState);

            Assert.AreEqual(Vector3.zero, _go.transform.position);
        }

        // ── ApplyReconciliation — lerp ─────────────────────────────────────────

        [Test]
        [Description("Error between 0.1m and 2m must NOT snap (lerp scheduled instead).")]
        public void ApplyReconciliation_MediumError_DoesNotSnapImmediately()
        {
            EnablePrediction(_nt);
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            // Client at (0.5, 0, 0); server at (0, 0, 0) — 0.5m, between thresholds.
            _go.transform.position = new Vector3(0.5f, 0, 0);
            _nt.MarkClean();

            var serverState = new TransformState
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };
            _nt.ApplyReconciliation(serverState);

            // Must NOT have snapped to zero.
            Assert.AreNotEqual(Vector3.zero, _go.transform.position,
                "Medium error should not snap — lerp should be scheduled.");
            // Must still be at the predicted position (lerp has not fired yet).
            Assert.AreEqual(new Vector3(0.5f, 0, 0), _go.transform.position);
        }

        // ── ApplyReconciliation — within tolerance ─────────────────────────────

        [Test]
        [Description("Error < 0.1m (lerpThreshold) must not trigger any correction.")]
        public void ApplyReconciliation_SmallError_NoCorrection()
        {
            EnablePrediction(_nt);
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            // 0.01m error — well within the 0.1m lerp threshold.
            _go.transform.position = new Vector3(0.01f, 0, 0);
            _nt.MarkClean();

            var serverState = new TransformState
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };
            _nt.ApplyReconciliation(serverState);

            // Position must be unchanged — prediction was close enough.
            Assert.AreEqual(new Vector3(0.01f, 0, 0), _go.transform.position);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // NetworkTransform._enablePrediction is [SerializeField] private — we
        // toggle it via the serialised field using JsonUtility so the test can
        // exercise both branches without changing the access modifier.
        //
        // Alternative: expose a public test-only constructor or a reflection helper.
        // Using reflection here to keep the production API clean.
        private static void EnablePrediction(NetworkTransform nt)
        {
            var field = typeof(NetworkTransform).GetField(
                "_enablePrediction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            field?.SetValue(nt, true);
        }
    }
}
