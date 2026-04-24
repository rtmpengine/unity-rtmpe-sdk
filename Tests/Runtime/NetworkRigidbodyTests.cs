// RTMPE SDK — Tests/Runtime/NetworkRigidbodyTests.cs
//
// NUnit tests for NetworkRigidbody (3-D physics sync component).
//
// Coverage:
//   GetState()          — default before spawn; captures Rigidbody position/sleep
//                         after spawn.
//   ApplyRemoteState()  — verifies partial-mask field merging: only fields whose
//                         bit is set in changedMask are overwritten; all others
//                         are preserved from prior calls.
//   ApplyReconciliation — confirmed to be a no-op (must not throw).
//
// Private fields (_receivedState, _hasReceivedState) are accessed via reflection
// so that production code carries no test-only surface area.
//
// Each test creates its own GameObjects and tears them down to prevent singleton
// leaks between test cases.

using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("Sync")]
    public class NetworkRigidbodyTests
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

            _testGo = new GameObject("nrb-test");
            _testGo.AddComponent<Rigidbody>();            // Rigidbody must exist before NetworkRigidbody.OnNetworkSpawn
            _nrb    = _testGo.AddComponent<NetworkRigidbody>();
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
        [Description("GetState returns a zeroed struct when _rb has not been cached (before spawn).")]
        public void GetState_BeforeSpawn_ReturnsDefaultPosition()
        {
            // SetSpawned has not been called → OnNetworkSpawn has not run → _rb is null
            var state = _nrb.GetState();
            Assert.AreEqual(Vector3.zero, state.Position,
                "Position must be zero-vector when Rigidbody is not yet cached.");
        }

        [Test]
        [Description("GetState captures Rigidbody.position after OnNetworkSpawn caches the Rigidbody.")]
        public void GetState_AfterSpawn_CapturesTransformPosition()
        {
            _nrb.Initialize(1UL, "local-player");
            _nrb.SetSpawned(true);  // → OnNetworkSpawn → _rb cached

            _testGo.transform.position = new Vector3(4f, 8f, 15f);
            var state = _nrb.GetState();

            Assert.AreEqual(4f,  state.Position.x, 1e-5f, "pos_x");
            Assert.AreEqual(8f,  state.Position.y, 1e-5f, "pos_y");
            Assert.AreEqual(15f, state.Position.z, 1e-5f, "pos_z");
        }

        [Test]
        [Description("GetState IsSleeping field is false in Edit Mode (no active physics simulation).")]
        public void GetState_AfterSpawn_IsSleepingFalse()
        {
            _nrb.Initialize(1UL, "local-player");
            _nrb.SetSpawned(true);

            var state = _nrb.GetState();
            Assert.IsFalse(state.IsSleeping,
                "IsSleeping must be false in Edit Mode (physics engine not running).");
        }

        // ── ApplyRemoteState — field merging ──────────────────────────────────

        [Test]
        [Description("ApplyRemoteState with ChangedPosition updates only _receivedState.Position.")]
        public void ApplyRemoteState_PositionOnly_UpdatesPosition()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            var incoming = new PhysicsState { Position = new Vector3(10f, 20f, 30f) };
            _nrb.ApplyRemoteState(incoming, PhysicsPacketBuilder.ChangedPosition);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(10f, stored.Position.x, 1e-5f, "pos_x");
            Assert.AreEqual(20f, stored.Position.y, 1e-5f, "pos_y");
            Assert.AreEqual(30f, stored.Position.z, 1e-5f, "pos_z");
        }

        [Test]
        [Description("Fields absent from changedMask are preserved from the previous ApplyRemoteState.")]
        public void ApplyRemoteState_PartialMask_PreservesUnincludedFields()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            // Call 1: set position
            _nrb.ApplyRemoteState(
                new PhysicsState { Position = new Vector3(5f, 6f, 7f) },
                PhysicsPacketBuilder.ChangedPosition);

            // Call 2: update velocity only — position must be retained
            _nrb.ApplyRemoteState(
                new PhysicsState { Velocity = new Vector3(1f, 2f, 3f) },
                PhysicsPacketBuilder.ChangedVelocity);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(5f, stored.Position.x, 1e-5f, "Position.x must be retained from first call.");
            Assert.AreEqual(6f, stored.Position.y, 1e-5f, "Position.y must be retained.");
            Assert.AreEqual(7f, stored.Position.z, 1e-5f, "Position.z must be retained.");
            Assert.AreEqual(1f, stored.Velocity.x, 1e-5f, "Velocity.x must be set by second call.");
        }

        [Test]
        [Description("Rotation is only written when ChangedRotation bit is set.")]
        public void ApplyRemoteState_RotationOnly_UpdatesRotation()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            var q = Quaternion.Euler(45f, 90f, 0f);
            _nrb.ApplyRemoteState(
                new PhysicsState { Rotation = q },
                PhysicsPacketBuilder.ChangedRotation);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(q.x, stored.Rotation.x, 1e-5f, "rot_x");
            Assert.AreEqual(q.y, stored.Rotation.y, 1e-5f, "rot_y");
            Assert.AreEqual(q.z, stored.Rotation.z, 1e-5f, "rot_z");
            Assert.AreEqual(q.w, stored.Rotation.w, 1e-5f, "rot_w");
        }

        [Test]
        [Description("All fields are merged when the full data mask is set.")]
        public void ApplyRemoteState_AllFields_UpdatesAll()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            byte allMask = PhysicsPacketBuilder.ChangedPosition
                         | PhysicsPacketBuilder.ChangedRotation
                         | PhysicsPacketBuilder.ChangedVelocity
                         | PhysicsPacketBuilder.ChangedAngularVelocity
                         | PhysicsPacketBuilder.ChangedSleep;

            var incoming = new PhysicsState
            {
                Position        = new Vector3(1f, 2f, 3f),
                Rotation        = Quaternion.identity,
                Velocity        = new Vector3(0.5f, 0f, 0f),
                AngularVelocity = new Vector3(0f, 0.1f, 0f),
                IsSleeping      = true,
            };
            _nrb.ApplyRemoteState(incoming, allMask);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(incoming.Position.x,   stored.Position.x,   1e-5f, "pos_x");
            Assert.AreEqual(incoming.Velocity.x,   stored.Velocity.x,   1e-5f, "vel_x");
            Assert.AreEqual(incoming.AngularVelocity.y, stored.AngularVelocity.y, 1e-5f, "ang_y");
            Assert.IsTrue(stored.IsSleeping, "IsSleeping must be set to true.");
        }

        [Test]
        [Description("Zero changedMask leaves _receivedState unchanged.")]
        public void ApplyRemoteState_ZeroMask_NoFieldsOverwritten()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            // Establish a known position
            _nrb.ApplyRemoteState(
                new PhysicsState { Position = new Vector3(99f, 0f, 0f) },
                PhysicsPacketBuilder.ChangedPosition);

            // Apply with a contradictory value and zero mask
            _nrb.ApplyRemoteState(
                new PhysicsState { Position = new Vector3(-1f, -1f, -1f) },
                0x00);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.AreEqual(99f, stored.Position.x, 1e-5f,
                "Position must not be overwritten when changedMask is zero.");
        }

        [Test]
        [Description("IsSleeping is set correctly by ChangedSleep.")]
        public void ApplyRemoteState_SleepFlag_UpdatesIsSleeping()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            _nrb.ApplyRemoteState(
                new PhysicsState { IsSleeping = true },
                PhysicsPacketBuilder.ChangedSleep);

            var stored = GetField<PhysicsState>(_nrb, "_receivedState");
            Assert.IsTrue(stored.IsSleeping);
        }

        // ── ApplyRemoteState — _hasReceivedState flag ─────────────────────────

        [Test]
        [Description("_hasReceivedState transitions from false to true after the first ApplyRemoteState.")]
        public void ApplyRemoteState_SetsHasReceivedState()
        {
            _nrb.Initialize(1UL, "other-player");
            _nrb.SetSpawned(true);

            bool before = GetField<bool>(_nrb, "_hasReceivedState");
            Assert.IsFalse(before, "Pre-condition: _hasReceivedState must be false before first call.");

            _nrb.ApplyRemoteState(new PhysicsState(), 0x00);

            bool after = GetField<bool>(_nrb, "_hasReceivedState");
            Assert.IsTrue(after, "_hasReceivedState must be true after ApplyRemoteState.");
        }

        // ── ApplyReconciliation ────────────────────────────────────────────────

        [Test]
        [Description("ApplyReconciliation is a no-op and must not throw.")]
        public void ApplyReconciliation_DoesNotThrow()
        {
            _nrb.Initialize(1UL, "local-player");
            _nrb.SetSpawned(true);

            Assert.DoesNotThrow(() =>
                _nrb.ApplyReconciliation(new PhysicsState(), PhysicsPacketBuilder.ChangedPosition));
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
