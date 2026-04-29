// RTMPE SDK — Tests/Runtime/PhysicsSyncSecurityTests.cs
//
// Hardening-regression tests for the receive-side defences added to
// NetworkRigidbody / NetworkRigidbody2D:
//  • Linear-velocity plausibility cap     (drops absurdly fast packets).
//  • Angular-velocity plausibility cap.
//  • Per-tick position-delta cap          (defeats single-tick teleport).
//  • Per-object inbound rate limit        (token-bucket).
//  • ConstraintMask gating                (default deny; allowmask filter).
//
// Reflective access mirrors the existing NetworkRigidbodyTests pattern so the
// production code carries no test-only API surface.

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("SecuritySDK")]
    public class PhysicsSyncSecurityTests
    {
        private GameObject       _nmGo;
        private NetworkManager   _manager;
        private GameObject       _testGo;
        private NetworkRigidbody _nrb;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("local-player");

            _testGo = new GameObject("nrb-sec");
            _testGo.AddComponent<Rigidbody>();
            _nrb    = _testGo.AddComponent<NetworkRigidbody>();
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            // The bucket is pre-filled to capacity in OnNetworkSpawn so a
            // single ApplyRemoteState immediately after spawn is always
            // admitted by the rate-limiter.  Tests still tighten or disable
            // the limiter where they specifically exercise it.
        }

        [TearDown]
        public void TearDown()
        {
            if (_testGo != null) Object.DestroyImmediate(_testGo);
            if (_nmGo   != null) Object.DestroyImmediate(_nmGo);
        }

        // ── Velocity-cap rejection ────────────────────────────────────────────

        [Test]
        [Description("linear velocity above maxLinearVelocity is dropped wholesale; no field merged.")]
        public void ApplyRemoteState_LinearVelocityAboveCap_Dropped()
        {
            _manager.Settings.maxLinearVelocity = 100f;
            // Establish a baseline so we can see whether anything got applied.
            _nrb.ApplyRemoteState(new PhysicsState { Position = Vector3.zero },
                                  PhysicsPacketBuilder.ChangedPosition);
            var before = GetField<PhysicsState>(_nrb, "_receivedState");

            // A velocity an order of magnitude over the cap.
            var hostile = new PhysicsState { Velocity = new Vector3(10_000f, 0f, 0f) };
            _nrb.ApplyRemoteState(hostile, PhysicsPacketBuilder.ChangedVelocity);

            var after = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(before.Velocity, after.Velocity,
                "Velocity over the cap must be rejected and not merged into _receivedState.");
        }

        // ── Angular-velocity cap rejection ────────────────────────────────────

        [Test]
        [Description("angular velocity above maxAngularVelocity is dropped.")]
        public void ApplyRemoteState_AngularVelocityAboveCap_Dropped()
        {
            _manager.Settings.maxAngularVelocity = 50f;
            var hostile = new PhysicsState { AngularVelocity = new Vector3(99_999f, 0f, 0f) };
            _nrb.ApplyRemoteState(hostile, PhysicsPacketBuilder.ChangedAngularVelocity);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(0f, stored.AngularVelocity.x, 1e-5f,
                "Hostile angular velocity must not have been merged.");
        }

        // ── Per-tick position delta cap ───────────────────────────────────────

        [Test]
        [Description("/a position delta beyond MaxPositionDeltaPerTick is rejected.")]
        public void ApplyRemoteState_PositionDeltaTooLarge_Dropped()
        {
            _manager.Settings.maxPositionDeltaPerTick = 25f;
            // Establish prior position 0,0,0.
            _nrb.ApplyRemoteState(new PhysicsState { Position = Vector3.zero },
                                  PhysicsPacketBuilder.ChangedPosition);

            // Attempt 1000m teleport in one packet.
            _nrb.ApplyRemoteState(new PhysicsState { Position = new Vector3(1000f, 0f, 0f) },
                                  PhysicsPacketBuilder.ChangedPosition);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(0f, stored.Position.x, 1e-3f,
                "Packet exceeding per-tick delta cap must not have been applied.");
        }

        // ── Token-bucket rate limit ───────────────────────────────────────────

        [Test]
        [Description("rapid packet burst from a single source is dropped after the bucket empties.")]
        public void ApplyRemoteState_RateLimit_BurstDroppedAfterCapacity()
        {
            // Tighten the rate to 5 packets/second so the test runs quickly,
            // and reset the bucket to the new capacity (the spawn-time
            // pre-fill captured the old default rate).
            _manager.Settings.maxPhysicsPacketsPerSecond = 5f;
            SetField(_nrb, "_rateBucketTokens", 5f);
            SetField(_nrb, "_rateBucketLastTime", Time.fixedTime);

            int accepted = 0;
            for (int i = 0; i < 50; i++)
            {
                uint seqBefore = GetField<uint>(_nrb, "_appliedSequence");
                _nrb.ApplyRemoteState(
                    new PhysicsState { Position = new Vector3(i * 0.001f, 0f, 0f) },
                    PhysicsPacketBuilder.ChangedPosition);
                uint seqAfter = GetField<uint>(_nrb, "_appliedSequence");
                if (seqAfter != seqBefore) accepted++;
            }

            // With a 5-token bucket and dt≈0 between calls in EditMode, only
            // the first ~5 packets should be admitted before the bucket
            // empties; remaining packets must be silently dropped.
            Assert.LessOrEqual(accepted, 6,
                "Token bucket must drop excess packets in a tight burst (capacity ≈ 5).");
            Assert.Greater(accepted, 0,
                "At least one packet must have been admitted before the bucket emptied.");
        }

        private static void SetField(object target, string name, object value)
        {
            var fi = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(fi, $"reflection: field '{name}' not found on {target.GetType().Name}");
            fi.SetValue(target, value);
        }

        // ── ConstraintMask gating ─────────────────────────────────────────────

        [Test]
        [Description("ConstraintMask updates are ignored by default (allowDynamicConstraints=false).")]
        public void ApplyRemoteState_Constraints_IgnoredByDefault()
        {
            _manager.Settings.allowDynamicConstraints = false;

            byte mask = PhysicsPacketBuilder.ChangedConstraints;
            _nrb.ApplyRemoteState(new PhysicsState { ConstraintMask = 0x3F }, mask);

            bool received = GetField<bool>(_nrb, "_hasReceivedConstraints");
            Assert.IsFalse(received,
                "Constraint mask must not be honoured while AllowDynamicConstraints is false.");
        }

        [Test]
        [Description("ConstraintMask bits outside DynamicConstraintsAllowMask are stripped.")]
        public void ApplyRemoteState_Constraints_AllowmaskFilter()
        {
            _manager.Settings.allowDynamicConstraints     = true;
            _manager.Settings.dynamicConstraintsAllowMask = 0x03; // only low two bits writable

            byte hostile = 0xFF;
            _nrb.ApplyRemoteState(new PhysicsState { ConstraintMask = hostile },
                                  PhysicsPacketBuilder.ChangedConstraints);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual((byte)(hostile & 0x03), stored.ConstraintMask,
                "Bits outside the allowmask must be stripped before assignment.");
        }

        // ── Reflection helpers ────────────────────────────────────────────────

        private static T GetField<T>(object target, string name)
        {
            var fi = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(fi, $"reflection: field '{name}' not found on {target.GetType().Name}");
            return (T)fi.GetValue(target);
        }
    }
}
