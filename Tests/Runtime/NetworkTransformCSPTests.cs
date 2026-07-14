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

        // ─────────────────────────────────────────────────────────────────────
        // Reconciliation thresholds — project default vs Inspector override
        // (Tier-1 finding #5).  ConfigureReconcileForTest is the test seam
        // exposed by NetworkTransform so a fixture can flip between the two
        // resolution paths without re-spawning the component.
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        [Description("Sentinel (-1) on both fields resolves to NetworkSettings.reconcile* defaults.")]
        public void Thresholds_SentinelInputs_InheritProjectDefaults()
        {
            // Mutate the runtime settings so the inherited values differ from
            // the historical hard-coded 0.1 / 2.0 pair — proving the lookup
            // really happens at resolution time.
            _manager.Settings.reconcileLerpThreshold = 0.25f;
            _manager.Settings.reconcileSnapThreshold = 4.0f;

            _nt.ConfigureReconcileForTest(
                NetworkTransform.ReconcileUseProjectDefault,
                NetworkTransform.ReconcileUseProjectDefault);

            Assert.AreEqual(0.25f, _nt.ResolvedLerpThreshold, 1e-5f);
            Assert.AreEqual(4.0f,  _nt.ResolvedSnapThreshold, 1e-5f);
        }

        [Test]
        [Description("Inspector overrides (non-negative) win over the project default.")]
        public void Thresholds_InspectorOverride_WinsOverDefault()
        {
            _manager.Settings.reconcileLerpThreshold = 0.1f;
            _manager.Settings.reconcileSnapThreshold = 2.0f;

            _nt.ConfigureReconcileForTest(lerpThreshold: 0.5f, snapThreshold: 10f);

            Assert.AreEqual(0.5f,  _nt.ResolvedLerpThreshold, 1e-5f);
            Assert.AreEqual(10.0f, _nt.ResolvedSnapThreshold, 1e-5f);
        }

        [Test]
        [Description("Mixed sentinel + override: only the sentinel side falls back to the project default.")]
        public void Thresholds_MixedSentinelAndOverride_FallbackOnlyOnSentinelSide()
        {
            _manager.Settings.reconcileLerpThreshold = 0.05f;
            _manager.Settings.reconcileSnapThreshold = 3.0f;

            _nt.ConfigureReconcileForTest(
                lerpThreshold: NetworkTransform.ReconcileUseProjectDefault,
                snapThreshold: 7.5f);

            Assert.AreEqual(0.05f, _nt.ResolvedLerpThreshold, 1e-5f);
            Assert.AreEqual(7.5f,  _nt.ResolvedSnapThreshold, 1e-5f);
        }

        [Test]
        [Description("Inverted authoring (snap < lerp) is clamped at runtime so the snap branch stays reachable.")]
        public void Thresholds_InvertedAuthoring_ClampsSnapToLerp()
        {
            _nt.ConfigureReconcileForTest(lerpThreshold: 1.0f, snapThreshold: 0.2f);

            Assert.AreEqual(1.0f, _nt.ResolvedLerpThreshold, 1e-5f);
            Assert.AreEqual(1.0f, _nt.ResolvedSnapThreshold, 1e-5f,
                "Snap below lerp must be clamped upward to lerp; otherwise the snap " +
                "branch (error >= snap) is reached only when error >= lerp, making it " +
                "indistinguishable from the lerp branch.");
        }

        [Test]
        [Description("ApplyReconciliation honours the resolved (project-default) snap threshold.")]
        public void Thresholds_ProjectDefaultSnap_TriggersSnapPath()
        {
            // Wire prediction on so ApplyReconciliation runs its full path.
            EnablePrediction(_nt);
            _nt.Initialize(1, "p");
            _manager.SetLocalPlayerStringId("p");
            _nt.SetSpawned(true);

            // Settings: snap at 1m exactly.
            _manager.Settings.reconcileLerpThreshold = 0.1f;
            _manager.Settings.reconcileSnapThreshold = 1.0f;
            _nt.ConfigureReconcileForTest(
                NetworkTransform.ReconcileUseProjectDefault,
                NetworkTransform.ReconcileUseProjectDefault);

            // Place local at +1.5 m — error = 1.5 m, exceeds the 1 m snap.
            _go.transform.position = new Vector3(1.5f, 0, 0);
            _nt.MarkClean();

            var serverState = new TransformState
            {
                Position = Vector3.zero,
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };
            _nt.ApplyReconciliation(serverState);

            // Snap path: position becomes the server's position immediately.
            Assert.AreEqual(Vector3.zero, _go.transform.position,
                "Project-default 1m snap threshold must trigger an immediate snap at 1.5m error.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Tick-driver hosted input collection.  The CSP buffer must accept
        // exactly one Push per simulated tick when the central tick driver
        // calls OnFixedTick — and zero pushes outside the prediction-enabled
        // owner-spawned path.  Together these tests prove the input cadence
        // is locked to the simulation tick rather than the visual frame.
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        [Description("OnFixedTick pushes exactly one input to the buffer per tick when " +
                     "prediction is enabled and the local player owns the object.")]
        public void OnFixedTick_PredictionEnabled_PushesOneInputPerTick()
        {
            EnablePrediction(_nt);
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            var bufField = typeof(NetworkTransform).GetField(
                "_inputBuffer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = (RTMPE.Core.InputBuffer)bufField.GetValue(_nt);

            // Drive 5 successive ticks.  LocalTick advances under the hood
            // when InvokeOnFixedTick fires after _localTick++ in the real
            // tick loop; in the test we mutate _localTick via reflection
            // so each call observes a strictly-greater tick.
            var localTickField = typeof(NetworkManager).GetField(
                "_localTick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            for (uint t = 1u; t <= 5u; t++)
            {
                localTickField.SetValue(_manager, t);
                _nt.InvokeOnFixedTick(1f / 30f);
            }

            Assert.AreEqual(5, buf.Count,
                "OnFixedTick must push exactly one input per simulated tick.");
        }

        [Test]
        [Description("OnFixedTick is a no-op when prediction is disabled (no ghost pushes).")]
        public void OnFixedTick_PredictionDisabled_NoPush()
        {
            // _enablePrediction defaults to false.
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            var bufField = typeof(NetworkTransform).GetField(
                "_inputBuffer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = (RTMPE.Core.InputBuffer)bufField.GetValue(_nt);

            for (int i = 0; i < 10; i++)
                _nt.InvokeOnFixedTick(1f / 30f);

            Assert.AreEqual(0, buf.Count,
                "Prediction-off must never add inputs to the buffer.");
        }

        [Test]
        [Description("OnFixedTick fired twice with the same LocalTick still pushes only once " +
                     "(belt-and-braces against re-entrant dispatch).")]
        public void OnFixedTick_DuplicateTick_DoesNotDoublePush()
        {
            EnablePrediction(_nt);
            _nt.Initialize(1, "player-1");
            _manager.SetLocalPlayerStringId("player-1");
            _nt.SetSpawned(true);

            var bufField = typeof(NetworkTransform).GetField(
                "_inputBuffer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = (RTMPE.Core.InputBuffer)bufField.GetValue(_nt);

            var localTickField = typeof(NetworkManager).GetField(
                "_localTick",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            localTickField.SetValue(_manager, 7u);
            _nt.InvokeOnFixedTick(1f / 30f);
            _nt.InvokeOnFixedTick(1f / 30f); // same tick — should be ignored
            _nt.InvokeOnFixedTick(1f / 30f); // same tick — should be ignored

            Assert.AreEqual(1, buf.Count,
                "Duplicate ticks must not be pushed twice; the per-instance dedupe runs after the driver guard.");
        }
    }
}
