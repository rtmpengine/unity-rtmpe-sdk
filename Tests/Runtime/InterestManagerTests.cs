// RTMPE SDK — Tests/Runtime/InterestManagerTests.cs
//
// NUnit Edit-Mode tests for InterestManager.  Tier-1 GameNet-2 may extend
// this manager with hysteresis; the suite below pins today's contract:
//   • Singleton-active accessor (s_active) follows the most-recently-
//     enabled rule.
//   • Receive-filter activation requires (active manager) AND
//     (radius > 0) AND (at least one position reported).
//   • Tracking can be paused / resumed via StopTracking / StartTracking.
//
// All tests construct disposable GameObjects and tear them down in
// TearDown to keep the static singleton clean across fixtures.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class InterestManagerTests
    {
        private GameObject _go;

        [TearDown]
        public void TearDown()
        {
            if (_go != null)
            {
                Object.DestroyImmediate(_go);
                _go = null;
            }
        }

        // ── Default state ────────────────────────────────────────────────────

        [Test]
        public void DefaultsBeforeAnyManagerEnabled_ReceiveFilterInactive()
        {
            // Static accessors must report the safe defaults that tell
            // NetworkManager "no filter — deliver everything".
            Assert.IsFalse(InterestManagerStatics.IsReceiveFilterActive());
            Assert.AreEqual(0f, InterestManagerStatics.LocalReceiveRadius());
            Assert.IsTrue(InterestManagerStatics.LocalUsesXzPlane(),
                "Defaults to XZ-plane semantics so callers using 3-D conventions stay correct when no manager is active.");
        }

        // ── Tracking flag ────────────────────────────────────────────────────

        [Test]
        public void StopTracking_FollowedByStartTracking_IsTrackingTrueAgain()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            Assert.IsTrue(im.IsTracking);
            im.StopTracking();
            Assert.IsFalse(im.IsTracking);
            im.StartTracking();
            Assert.IsTrue(im.IsTracking);
        }

        [Test]
        public void Disabled_IsTrackingFalse()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            im.enabled = false;
            Assert.IsFalse(im.IsTracking);
        }

        // ── Singleton-active accessor ────────────────────────────────────────

        [Test]
        public void OnEnable_SetsStaticActive()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            // Component is enabled by default; verify the s_active static
            // mirrors this through the ReceiveFilterRadius getter, which
            // returns 0 (not -1) when active but radius is 0.
            Assert.AreEqual(0f, InterestManagerStatics.LocalReceiveRadius());
            // Now toggle radius to a non-zero value with a sent position.
            im.ReceiveFilterRadius = 50f;
            Assert.AreEqual(50f, InterestManagerStatics.LocalReceiveRadius());
        }

        [Test]
        public void OnDisable_ClearsStaticActive()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            im.ReceiveFilterRadius = 50f;
            // Disabling the component must zero the static accessor so a
            // subsequent IsReceiveFilterActive query returns false.
            im.enabled = false;
            Assert.AreEqual(0f, InterestManagerStatics.LocalReceiveRadius());
            Assert.IsFalse(InterestManagerStatics.IsReceiveFilterActive());
        }

        [Test]
        public void IsReceiveFilterActive_RadiusZero_ReturnsFalse()
        {
            _go = new GameObject("Im");
            _go.AddComponent<InterestManager>();
            // Default radius is 0; even though a manager is active it must
            // NOT advertise an active receive filter — that would trap the
            // (0, 0) origin and silently drop every remote object.
            Assert.IsFalse(InterestManagerStatics.IsReceiveFilterActive());
        }

        [Test]
        public void IsReceiveFilterActive_NoPositionReported_ReturnsFalse()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            im.ReceiveFilterRadius = 50f;
            // _hasSentOnce starts false; gating IsReceiveFilterActive on
            // it is the documented protection against the (0, 0) trap
            // described in the SendCurrentPosition comment.
            Assert.IsFalse(InterestManagerStatics.IsReceiveFilterActive(),
                "Filter must remain inactive until at least one position has been reported.");
        }

        // ── ReceiveFilterRadius clamping ─────────────────────────────────────

        [Test]
        public void LocalReceiveRadius_NegativeFieldValue_ClampsToZero()
        {
            _go = new GameObject("Im");
            var im = _go.AddComponent<InterestManager>();
            // [Min(0)] would clamp at the Inspector layer; the runtime
            // accessor performs Mathf.Max(0, …) defensively so a script
            // assignment that bypasses the attribute is still safe.
            im.ReceiveFilterRadius = -10f;
            Assert.AreEqual(0f, InterestManagerStatics.LocalReceiveRadius());
        }
    }

    // Reflection-free accessor wrappers for the internal static surface.
    // The InterestManager exposes its statics as `internal` so we can read
    // them from RTMPE.SDK.Tests via InternalsVisibleTo without bouncing
    // through reflection (which would be brittle to renames).
    internal static class InterestManagerStatics
    {
        public static bool  IsReceiveFilterActive() => InterestManager_IsReceiveFilterActive;
        public static float LocalReceiveRadius()    => InterestManager_LocalReceiveRadius;
        public static bool  LocalUsesXzPlane()      => InterestManager_LocalUsesXzPlane;

        // Properties indirect to the InterestManager statics via the
        // friend-assembly grant.  Keeping them as expression-bodied
        // members ensures any rename in production lights up here at
        // compile time, not at runtime.
        private static bool  InterestManager_IsReceiveFilterActive => InterestManager.IsReceiveFilterActive;
        private static float InterestManager_LocalReceiveRadius    => InterestManager.LocalReceiveRadius;
        private static bool  InterestManager_LocalUsesXzPlane      => InterestManager.LocalUsesXzPlane;
    }
}
