// RTMPE SDK — Tests/Runtime/NetworkTransformVelocityCapTests.cs
//
// NUnit Edit-Mode tests for the owner-velocity cap on NetworkTransform.
//
// The cap is a client-side anti-cheat scaffold that clamps the broadcast
// position whenever the apparent per-second speed since the last send
// exceeds NetworkSettings.maxOwnerVelocityMetersPerSecond.  The first
// send after spawn (or after OwnerTeleportTo) skips the check because
// no prior baseline exists.
//
// Test seams (internal, NOT public surface):
//   • ClampOwnerVelocityForTest(candidate)    — invokes the clamp helper
//                                               using the live Time.unscaledTimeAsDouble.
//   • PrimeVelocityBaselineForTest(pos, time) — seeds the "last-sent" pair
//                                               so the helper has a baseline
//                                               to derive a velocity from.
//   • OwnerTeleportTo(newPos)                 — clears the baseline.
//
// All test cases derive expected dt values by reading
// Time.unscaledTimeAsDouble at SetUp time and offsetting the primed
// baseline timestamp; this avoids any reliance on the Unity Editor
// running a real frame loop during Edit-Mode test execution.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    public class NetworkTransformVelocityCapTests
    {
        private GameObject       _nmGo;
        private NetworkManager   _manager;
        private GameObject       _go;
        private NetworkTransform _nt;

        // Default cap from NetworkSettings is 50 m/s; tests assume that
        // value so they exercise the realistic project default rather than
        // a synthetic one.  Any future change to the default will surface
        // here and prompt a conscious test update.
        private const float ExpectedDefaultCap = 50f;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager_VelocityCap");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _go = new GameObject("NT_VelocityCap");
            _nt = _go.AddComponent<NetworkTransform>();

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

        // ── First-send pass-through (no baseline) ─────────────────────────────

        [Test]
        [Description("Without a primed baseline the clamp helper returns the candidate unchanged.")]
        public void FirstSend_NoBaseline_PassesThroughUnclamped()
        {
            // No PrimeVelocityBaselineForTest call: _hasLastSent is false,
            // so ClampOwnerVelocity short-circuits at the very first guard.
            var candidate = new Vector3(1000f, 0f, 0f);
            var result = _nt.ClampOwnerVelocityForTest(candidate);
            Assert.AreEqual(candidate, result);
        }

        // ── Within-cap pass-through ───────────────────────────────────────────

        [Test]
        [Description("A 10 m/s move under the 50 m/s cap is returned unchanged.")]
        public void WithinCap_PassesThroughUnclamped()
        {
            // Baseline at the origin one second ago — dt = 1s.
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now - 1.0);

            var candidate = new Vector3(10f, 0f, 0f); // 10 m/s ≪ 50 m/s cap
            var result = _nt.ClampOwnerVelocityForTest(candidate);

            AssertVectorClose(candidate, result, 0.01f);
        }

        // ── Over-cap clamp (axis-aligned) ─────────────────────────────────────

        [Test]
        [Description("A 100 m/s move over the 50 m/s cap clamps to the cap × dt distance.")]
        public void OverCap_ClampsToMaxDistance()
        {
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now - 1.0);

            var candidate = new Vector3(100f, 0f, 0f); // 100 m/s — twice the cap
            var result = _nt.ClampOwnerVelocityForTest(candidate);

            // Expected: along the same axis, distance == cap × dt = 50 × 1 = 50.
            AssertVectorClose(new Vector3(ExpectedDefaultCap, 0f, 0f), result, 0.5f);
            Assert.LessOrEqual(result.magnitude, ExpectedDefaultCap + 0.5f);
        }

        // ── Over-cap clamp preserves direction ────────────────────────────────

        [Test]
        [Description("Over-cap displacement is clamped along the same ray as the candidate.")]
        public void OverCap_ClampsAlongSameDirection()
        {
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now - 1.0);

            // Diagonal candidate: 100√2 ≈ 141.4 m/s — well over the cap.
            var candidate = new Vector3(100f, 100f, 0f);
            var result = _nt.ClampOwnerVelocityForTest(candidate);

            // The clamped point must lie on the ray (origin → candidate) at
            // distance ≤ cap × dt.
            Assert.LessOrEqual(result.magnitude, ExpectedDefaultCap * 1.0f + 0.5f);

            // Direction preservation: cross-product with the candidate
            // direction should be ~zero (collinear vectors).
            Vector3 unitCandidate = candidate.normalized;
            Vector3 unitResult    = result.normalized;
            float crossMag = Vector3.Cross(unitCandidate, unitResult).magnitude;
            Assert.Less(crossMag, 1e-3f, "clamped result must be collinear with candidate displacement");
        }

        // ── Zero / negative dt short-circuits ─────────────────────────────────

        [Test]
        [Description("Same-frame timestamp (dt <= 0) short-circuits and returns the candidate.")]
        public void ZeroDt_ShortCircuitsAndReturnsCandidate()
        {
            // Baseline timestamp >= now → dt <= 0.  The helper must avoid
            // a divide-by-zero and pass the candidate through.  We seed the
            // baseline a tiny epsilon in the future to guarantee dt <= 0
            // even if Time.unscaledTimeAsDouble advances between the prime
            // call and the clamp call (Edit-Mode test ticks are not bound
            // to a real frame loop, so the delta is sub-microsecond — but
            // not strictly zero).
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now + 1.0);

            var candidate = new Vector3(1000f, 0f, 0f);
            var result = _nt.ClampOwnerVelocityForTest(candidate);

            Assert.AreEqual(candidate, result);
        }

        [Test]
        [Description("A baseline timestamp in the future (clock-rewind) must not throw or divide by zero.")]
        public void NegativeDt_ShortCircuitsSafely()
        {
            // Adversarial: a clock rewind would make _lastSentTimeUnscaled > now,
            // yielding a negative dt.  The helper must short-circuit (dt <= 0)
            // and not propagate a negative scale to the displacement.
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now + 5.0);

            var candidate = new Vector3(1000f, 0f, 0f);
            Vector3 result = Vector3.zero;
            Assert.DoesNotThrow(() => result = _nt.ClampOwnerVelocityForTest(candidate));
            Assert.AreEqual(candidate, result);
        }

        // ── OwnerTeleportTo resets the baseline ───────────────────────────────

        [Test]
        [Description("OwnerTeleportTo clears the baseline so the next send is treated as a teleport.")]
        public void OwnerTeleportTo_ResetsBaseline_NextSendNotClamped()
        {
            // Prime a baseline so the cap would otherwise engage.
            double now = Time.unscaledTimeAsDouble;
            _nt.PrimeVelocityBaselineForTest(Vector3.zero, now - 1.0);

            // Legitimate teleport: respawn point a kilometre away.
            var teleportTarget = new Vector3(1000f, 0f, 0f);
            _nt.OwnerTeleportTo(teleportTarget);

            // The next clamp call has _hasLastSent==false again, so even an
            // outrageous candidate is returned unchanged.
            var farCandidate = new Vector3(2000f, 0f, 0f);
            var result = _nt.ClampOwnerVelocityForTest(farCandidate);
            Assert.AreEqual(farCandidate, result);
        }

        // ── Cap disabled ──────────────────────────────────────────────────────

        [Test]
        [Description("When the cap setting is non-positive, the helper returns the candidate unchanged.")]
        public void CapDisabled_PassesThroughUnclamped()
        {
            // The default settings instance attached to NetworkManager has a
            // positive cap; flip it to zero to verify the early-out path.
            var settings = _manager.Settings;
            Assert.IsNotNull(settings, "NetworkManager.Settings should be auto-created.");
            float originalCap = settings.maxOwnerVelocityMetersPerSecond;
            settings.maxOwnerVelocityMetersPerSecond = 0f;
            try
            {
                double now = Time.unscaledTimeAsDouble;
                _nt.PrimeVelocityBaselineForTest(Vector3.zero, now - 1.0);

                var candidate = new Vector3(1000f, 0f, 0f);
                var result = _nt.ClampOwnerVelocityForTest(candidate);
                Assert.AreEqual(candidate, result);
            }
            finally
            {
                settings.maxOwnerVelocityMetersPerSecond = originalCap;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance)
        {
            Assert.AreEqual(expected.x, actual.x, tolerance, "x");
            Assert.AreEqual(expected.y, actual.y, tolerance, "y");
            Assert.AreEqual(expected.z, actual.z, tolerance, "z");
        }
    }
}
