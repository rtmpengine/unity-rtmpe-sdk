// RTMPE SDK — Tests/Runtime/Tier1InterestHysteresisTests.cs
//
// Tier-1 sprint: verifies that the receive-side interest filter applies
// hysteresis (enter at R, leave at R + margin) so an object loitering at
// the boundary stops flapping in/out across consecutive ticks.
//
// Closes audit H-GN-02 ("InterestManager has no spatial hysteresis").

using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Rooms;
using UnityEngine;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    [Category("Tier1")]
    public class Tier1InterestHysteresisTests
    {
        private GameObject       _go;
        private InterestManager  _im;
        private NetworkManager   _nm;

        [SetUp]
        public void SetUp()
        {
            // NetworkManager.Instance auto-creates a singleton; capture it
            // so we can apply test-specific NetworkSettings overrides and
            // restore them in TearDown.  This indirection is necessary
            // because InterestManager.EffectiveHysteresisMargin consults
            // NetworkManager.Instance.Settings to honour project-wide
            // tuning configured in the Inspector.
            _nm = NetworkManager.Instance;

            _go = new GameObject("InterestManagerForTest");
            _im = _go.AddComponent<InterestManager>();
            // OnEnable installs the singleton invariant.

            // Disable the per-frame send loop so tests can drive ShouldDeliver
            // deterministically without racing Update.
            _im.StopTracking();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) Object.DestroyImmediate(_go);
            // Reset the project-wide override so subsequent fixtures see
            // the factory default.
            if (_nm != null && _nm.Settings != null)
                _nm.Settings.interestHysteresisMargin = 1f;
        }

        // ── Filter inactive ─────────────────────────────────────────────────

        [Test]
        public void ShouldDeliver_FilterInactive_AlwaysReturnsTrue()
        {
            // No PrimeForTests — _hasSentOnce false → IsReceiveFilterActive false.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, 1_000_000f));
            Assert.IsTrue(InterestManager.ShouldDeliver(1, 0f));
        }

        // ── Enter / leave with hysteresis ───────────────────────────────────

        [Test]
        public void ShouldDeliver_HiddenObject_EntersOnlyInsideInnerRadius()
        {
            ForceMargin(0f);           // disable override → use inspector field
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 1f);

            // Just outside r — must NOT enter.
            Assert.IsFalse(InterestManager.ShouldDeliver(42, Sq(10.001f)),
                "Hidden object outside r must remain hidden.");

            // Just inside r — must enter.
            Assert.IsTrue(InterestManager.ShouldDeliver(42, Sq(9.999f)),
                "Hidden object inside r must become visible.");

            // Now visible — distSq between r² and (r+m)² must STAY visible.
            Assert.IsTrue(InterestManager.ShouldDeliver(42, Sq(10.5f)),
                "Visible object inside dead-band must remain visible (no flap).");

            // Beyond outer bound — must leave.
            Assert.IsFalse(InterestManager.ShouldDeliver(42, Sq(11.001f)),
                "Visible object past r+m must become hidden.");

            // Hidden again, sitting in dead-band — must remain hidden until
            // it crosses the inner bound.  This is the asymmetry that kills
            // the boundary flap.
            Assert.IsFalse(InterestManager.ShouldDeliver(42, Sq(10.5f)),
                "Hidden object in dead-band must remain hidden.");
        }

        [Test]
        public void ShouldDeliver_BoundaryFlap_IsEliminated()
        {
            ForceMargin(0f);
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 1f);

            // Object oscillating between 9.99 and 10.01 m — without
            // hysteresis this flips every call.  With hysteresis it enters
            // once at 9.99 and stays visible until the outer bound.
            int flips = 0;
            bool prev = InterestManager.ShouldDeliver(7, Sq(9.99f));
            for (int i = 0; i < 100; i++)
            {
                bool curr = InterestManager.ShouldDeliver(7, Sq(i % 2 == 0 ? 10.01f : 9.99f));
                if (curr != prev) flips++;
                prev = curr;
            }
            Assert.AreEqual(0, flips, "Hysteresis must eliminate boundary flap.");
        }

        // ── Per-object isolation ─────────────────────────────────────────────

        [Test]
        public void ShouldDeliver_PerObjectStateIsIsolated()
        {
            ForceMargin(0f);
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 1f);

            // Object A enters.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(5f)));
            // Object B is far away — must remain hidden.
            Assert.IsFalse(InterestManager.ShouldDeliver(2, Sq(20f)));

            // A loiters in dead-band — still visible.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(10.5f)));
            // B in dead-band but never entered — still hidden.
            Assert.IsFalse(InterestManager.ShouldDeliver(2, Sq(10.5f)));
        }

        // ── ForgetObject ─────────────────────────────────────────────────────

        [Test]
        public void ForgetObject_DespawnedObject_RestartsHidden()
        {
            ForceMargin(0f);
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 1f);

            Assert.IsTrue(InterestManager.ShouldDeliver(99, Sq(5f)));
            Assert.IsTrue(InterestManager.ShouldDeliver(99, Sq(10.5f)));   // dead-band visible
            InterestManager.ForgetObject(99);
            Assert.IsFalse(InterestManager.ShouldDeliver(99, Sq(10.5f)),
                "After ForgetObject the next ShouldDeliver call starts hidden.");
        }

        // ── NetworkSettings override ─────────────────────────────────────────

        [Test]
        public void EffectiveMargin_NetworkSettingsOverridesInspectorField()
        {
            // Inspector says 0.1, project-wide override says 5.0 — override
            // must win so a single tuning change propagates to every prefab.
            _nm.Settings.interestHysteresisMargin = 5f;
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 0.1f);

            // Enter, then test that the dead-band extends out to 15 m
            // (10 + 5) rather than the inspector's 10.1 m.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(5f)));
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(14.5f)),
                "Project-wide margin override must extend the dead-band.");
            Assert.IsFalse(InterestManager.ShouldDeliver(1, Sq(15.5f)));
        }

        [Test]
        public void EffectiveMargin_NegativeOverride_FallsBackToInspector()
        {
            // -1 is the documented opt-out for the project-wide override.
            _nm.Settings.interestHysteresisMargin = -1f;
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 3f);

            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(5f)));
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(12.9f)),
                "With override opted out, inspector field drives the dead-band.");
            Assert.IsFalse(InterestManager.ShouldDeliver(1, Sq(13.5f)));
        }

        // ── Adversarial ─────────────────────────────────────────────────────

        [Test]
        public void ShouldDeliver_ZeroMargin_DegradesToHardThreshold()
        {
            ForceMargin(0f);
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: 0f);

            // r = r+m → dead-band collapses → equality keeps visible (bias
            // toward retention).  Strict-greater than r leaves.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(10f)));
            Assert.IsFalse(InterestManager.ShouldDeliver(1, Sq(10.001f)));
        }

        [Test]
        public void ShouldDeliver_NegativeMarginInspector_IsClampedToZero()
        {
            // Inspector field is [Min(0)] but enforce in code too —
            // EffectiveHysteresisMargin uses Mathf.Max(0, ...).
            ForceMargin(0f);
            _im.PrimeForTests(0f, 0f, receiveRadius: 10f, hysteresisMargin: -100f);

            // Behaves identically to margin=0 — no negative dead-band.
            Assert.IsTrue(InterestManager.ShouldDeliver(1, Sq(10f)));
            Assert.IsFalse(InterestManager.ShouldDeliver(1, Sq(10.001f)));
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // Force the project-wide override out of the way so the inspector
        // field is the source of truth for the test under inspection.  -1
        // is the documented "opt out of override" value defined by the
        // NetworkSettings.interestHysteresisMargin contract.
        private void ForceMargin(float _ignoredMargin)
            => _nm.Settings.interestHysteresisMargin = -1f;

        private static float Sq(float d) => d * d;
    }
}
