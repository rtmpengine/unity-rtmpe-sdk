// RTMPE SDK — Tests/Runtime/TransformReconciliationTests.cs
//
// Hardening tests for NetworkTransform.ApplyReconciliation:
//  • Server corrections beyond MaxServerCorrectionDistance are rejected.
//  • When WorldBounds is enabled, positions outside the AABB are rejected.
//
// Reflective access to private prediction state, mirroring NetworkTransformTests.

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("SecuritySDK")]
    public class TransformReconciliationSecurityTests
    {
        private GameObject       _nmGo;
        private NetworkManager   _manager;
        private GameObject       _testGo;
        private NetworkTransform _nt;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("local-player");

            _testGo = new GameObject("nt-sec");
            _nt     = _testGo.AddComponent<NetworkTransform>();
            _nt.Initialize(1UL, "local-player");
            _nt.SetSpawned(true);

            // Enable prediction so ApplyReconciliation does work.
            SetField(_nt, "_enablePrediction", true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_testGo != null) Object.DestroyImmediate(_testGo);
            if (_nmGo   != null) Object.DestroyImmediate(_nmGo);
        }

        // ── Correction-distance cap ───────────────────────────────────────────

        [Test]
        [Description("a server correction beyond MaxServerCorrectionDistance is rejected and the transform is unchanged.")]
        public void ApplyReconciliation_BeyondCorrectionCap_Rejected()
        {
            _manager.Settings.maxServerCorrectionDistance = 50f;

            _testGo.transform.position = Vector3.zero;
            // 5000m claimed correction.
            _nt.ApplyReconciliation(new TransformState
            {
                Position = new Vector3(5_000f, 0f, 0f),
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            });

            Assert.AreEqual(Vector3.zero, _testGo.transform.position,
                "Hostile correction beyond cap must not move the local transform.");
        }

        // ── World-bounds gating ───────────────────────────────────────────────

        [Test]
        [Description("a server position outside WorldBounds is rejected.")]
        public void ApplyReconciliation_OutsideWorldBounds_Rejected()
        {
            _manager.Settings.maxServerCorrectionDistance = 0f;     // disable distance cap
            _manager.Settings.worldBoundsEnabled  = true;
            _manager.Settings.worldBoundsCenter   = Vector3.zero;
            _manager.Settings.worldBoundsExtents  = new Vector3(100f, 100f, 100f);

            _testGo.transform.position = Vector3.zero;
            _nt.ApplyReconciliation(new TransformState
            {
                Position = new Vector3(10_000f, 0f, 0f),    // outside the 100×100×100 AABB
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            });

            Assert.AreEqual(Vector3.zero, _testGo.transform.position,
                "Server position outside WorldBounds must not be applied.");
        }

        [Test]
        [Description("a moderate correction within the cap is accepted (snap path).")]
        public void ApplyReconciliation_WithinCap_Applied()
        {
            _manager.Settings.maxServerCorrectionDistance = 50f;
            _manager.Settings.worldBoundsEnabled = false;

            // Force the snap path by providing a position above the snap threshold.
            _testGo.transform.position = Vector3.zero;
            _nt.ApplyReconciliation(new TransformState
            {
                Position = new Vector3(10f, 0f, 0f), // 10m → above default _snapThreshold (2m)
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            });

            Assert.AreEqual(10f, _testGo.transform.position.x, 1e-3f,
                "Correction within the cap must be applied.");
        }

        // ── Reflection helpers ────────────────────────────────────────────────

        private static void SetField(object target, string name, object value)
        {
            var fi = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(fi, $"reflection: field '{name}' not found on {target.GetType().Name}");
            fi.SetValue(target, value);
        }
    }
}
