// RTMPE SDK — Tests/Runtime/NetworkRigidbody2DTests.cs
//
// NUnit tests for NetworkRigidbody2D (2-D physics sync component).
//
// Coverage mirrors NetworkRigidbodyTests but targets PhysicsState2D:
//  GetState()          — default before spawn; position capture after spawn.
//  ApplyRemoteState()  — partial-mask field merging, zero-mask no-op,
//                        sleep flag, _hasReceivedState flag.
//  ApplyReconciliation — confirmed no-op (must not throw).
//
// Private fields are accessed via reflection — no test-only surface in production.

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("Sync")]
    public class NetworkRigidbody2DTests
    {
        private GameObject         _nmGo;
        private NetworkManager     _manager;
        private GameObject         _testGo;
        private NetworkRigidbody2D _nrb2d;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("local-player");

            _testGo  = new GameObject("nrb2d-test");
            _testGo.AddComponent<Rigidbody2D>();          // cached in OnNetworkSpawn
            _nrb2d   = _testGo.AddComponent<NetworkRigidbody2D>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_testGo != null) Object.DestroyImmediate(_testGo);
            if (_nmGo   != null) Object.DestroyImmediate(_nmGo);
            _testGo = null;
            _nmGo   = null;
        }

        // ── GetState ──────────────────────────────────────────────────────────

        [Test]
        [Description("GetState returns zero Position when Rigidbody2D has not been cached (before spawn).")]
        public void GetState_BeforeSpawn_ReturnsDefaultPosition()
        {
            var state = _nrb2d.GetState();
            Assert.AreEqual(Vector2.zero, state.Position,
                "Position must be zero when Rigidbody2D is not yet cached.");
        }

        [Test]
        [Description("GetState captures Rigidbody2D.position after OnNetworkSpawn caches the body.")]
        public void GetState_AfterSpawn_CapturesTransformPosition()
        {
            _nrb2d.Initialize(2UL, "local-player");
            _nrb2d.SetSpawned(true);

            _testGo.transform.position = new Vector3(3f, 7f, 0f);
            var state = _nrb2d.GetState();

            Assert.AreEqual(3f, state.Position.x, 1e-5f, "pos_x");
            Assert.AreEqual(7f, state.Position.y, 1e-5f, "pos_y");
        }

        [Test]
        [Description("GetState.Rotation reflects the 2-D rotation (degrees) of the Rigidbody2D.")]
        public void GetState_AfterSpawn_RotationIsZeroByDefault()
        {
            _nrb2d.Initialize(2UL, "local-player");
            _nrb2d.SetSpawned(true);

            // Default rotation is 0 degrees in 2-D
            var state = _nrb2d.GetState();
            Assert.AreEqual(0f, state.Rotation, 1e-4f);
        }

        [Test]
        [Description("GetState IsSleeping is false in Edit Mode (no active physics simulation).")]
        public void GetState_AfterSpawn_IsSleepingFalse()
        {
            _nrb2d.Initialize(2UL, "local-player");
            _nrb2d.SetSpawned(true);

            Assert.IsFalse(_nrb2d.GetState().IsSleeping);
        }

        // ── ApplyRemoteState — field merging ──────────────────────────────────

        [Test]
        [Description("ApplyRemoteState with ChangedPosition updates only Position.")]
        public void ApplyRemoteState_PositionOnly_UpdatesPosition()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            var incoming = new PhysicsState2D { Position = new Vector2(15f, -8f) };
            _nrb2d.ApplyRemoteState(incoming, PhysicsPacketBuilder.ChangedPosition);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(15f, stored.Position.x, 1e-5f, "pos_x");
            Assert.AreEqual(-8f, stored.Position.y, 1e-5f, "pos_y");
        }

        [Test]
        [Description("Fields absent from changedMask are preserved from the previous ApplyRemoteState.")]
        public void ApplyRemoteState_PartialMask_PreservesUnincludedFields()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            // Call 1: set position
            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Position = new Vector2(9f, 3f) },
                PhysicsPacketBuilder.ChangedPosition);

            // Call 2: update rotation only — position must survive
            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Rotation = 45f },
                PhysicsPacketBuilder.ChangedRotation);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(9f,  stored.Position.x, 1e-5f, "Position.x must be retained.");
            Assert.AreEqual(3f,  stored.Position.y, 1e-5f, "Position.y must be retained.");
            Assert.AreEqual(45f, stored.Rotation,   1e-4f, "Rotation must be set by second call.");
        }

        [Test]
        [Description("ChangedRotation writes the 2-D angle in degrees.")]
        public void ApplyRemoteState_RotationOnly_UpdatesRotation()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Rotation = 180f },
                PhysicsPacketBuilder.ChangedRotation);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(180f, stored.Rotation, 1e-4f);
        }

        [Test]
        [Description("ChangedVelocity writes the 2-D linear velocity.")]
        public void ApplyRemoteState_VelocityOnly_UpdatesVelocity()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Velocity = new Vector2(-4f, 2f) },
                PhysicsPacketBuilder.ChangedVelocity);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(-4f, stored.Velocity.x, 1e-5f, "vel_x");
            Assert.AreEqual(2f,  stored.Velocity.y, 1e-5f, "vel_y");
        }

        [Test]
        [Description("ChangedAngularVelocity writes the 2-D angular velocity (deg/s).")]
        public void ApplyRemoteState_AngularVelocityOnly_UpdatesAngularVelocity()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { AngularVelocity = -90f },
                PhysicsPacketBuilder.ChangedAngularVelocity);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(-90f, stored.AngularVelocity, 1e-4f);
        }

        [Test]
        [Description("All five fields are updated when the full data mask is set.")]
        public void ApplyRemoteState_AllFields_UpdatesAll()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            byte allMask = PhysicsPacketBuilder.ChangedPosition
                         | PhysicsPacketBuilder.ChangedRotation
                         | PhysicsPacketBuilder.ChangedVelocity
                         | PhysicsPacketBuilder.ChangedAngularVelocity
                         | PhysicsPacketBuilder.ChangedSleep;

            var incoming = new PhysicsState2D
            {
                Position        = new Vector2(1f, 2f),
                Rotation        = 90f,
                Velocity        = new Vector2(0.5f, -0.5f),
                AngularVelocity = 30f,
                IsSleeping      = true,
            };
            _nrb2d.ApplyRemoteState(incoming, allMask);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(incoming.Position.x,   stored.Position.x,   1e-5f, "pos_x");
            Assert.AreEqual(incoming.Position.y,   stored.Position.y,   1e-5f, "pos_y");
            Assert.AreEqual(incoming.Rotation,     stored.Rotation,     1e-4f, "rotation");
            Assert.AreEqual(incoming.Velocity.x,   stored.Velocity.x,   1e-5f, "vel_x");
            Assert.AreEqual(incoming.AngularVelocity, stored.AngularVelocity, 1e-4f, "ang_vel");
            Assert.IsTrue(stored.IsSleeping, "IsSleeping must be true.");
        }

        [Test]
        [Description("Zero changedMask leaves _receivedState unchanged.")]
        public void ApplyRemoteState_ZeroMask_NoFieldsOverwritten()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            // Establish a known position
            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Position = new Vector2(55f, 0f) },
                PhysicsPacketBuilder.ChangedPosition);

            // Apply contradictory value with zero mask — position must survive
            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { Position = new Vector2(-1f, -1f) },
                0x00);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.AreEqual(55f, stored.Position.x, 1e-5f,
                "Position must not be overwritten when changedMask is zero.");
        }

        [Test]
        [Description("IsSleeping is correctly set by ChangedSleep.")]
        public void ApplyRemoteState_SleepFlag_UpdatesIsSleeping()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            _nrb2d.ApplyRemoteState(
                new PhysicsState2D { IsSleeping = true },
                PhysicsPacketBuilder.ChangedSleep);

            var stored = GetField<PhysicsState2D>(_nrb2d, "_receivedState");
            Assert.IsTrue(stored.IsSleeping);
        }

        // ── ApplyRemoteState — _hasReceivedState flag ─────────────────────────

        [Test]
        [Description("_hasReceivedState transitions from false to true on the first ApplyRemoteState call.")]
        public void ApplyRemoteState_SetsHasReceivedState()
        {
            _nrb2d.Initialize(2UL, "other-player");
            _nrb2d.SetSpawned(true);

            bool before = GetField<bool>(_nrb2d, "_hasReceivedState");
            Assert.IsFalse(before, "Pre-condition: _hasReceivedState must be false before first call.");

            _nrb2d.ApplyRemoteState(new PhysicsState2D(), 0x00);

            bool after = GetField<bool>(_nrb2d, "_hasReceivedState");
            Assert.IsTrue(after, "_hasReceivedState must be true after ApplyRemoteState.");
        }

        // ── ApplyReconciliation ────────────────────────────────────────────────

        [Test]
        [Description("ApplyReconciliation is a no-op and must not throw.")]
        public void ApplyReconciliation_DoesNotThrow()
        {
            _nrb2d.Initialize(2UL, "local-player");
            _nrb2d.SetSpawned(true);

            Assert.DoesNotThrow(() =>
                _nrb2d.ApplyReconciliation(new PhysicsState2D(), PhysicsPacketBuilder.ChangedPosition));
        }

        // ── Reflection helper ─────────────────────────────────────────────────

        private static T GetField<T>(object target, string name)
        {
            var fi = target.GetType().GetField(
                name, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(fi, $"Private field '{name}' not found on {target.GetType().Name}.");
            return (T)fi.GetValue(target);
        }
    }
}
