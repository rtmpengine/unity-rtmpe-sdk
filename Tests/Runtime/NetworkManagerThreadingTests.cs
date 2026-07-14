// RTMPE SDK — Tests/Runtime/NetworkManagerThreadingTests.cs
//
// Regression tests for two architecture/threading fixes (SDK_REVIEW_REPORT.md §5/6):
//
//   Issue 5 — _applicationIsQuitting read/write without Volatile barriers.
//     The `volatile` keyword has undefined semantics under IL2CPP on ARM; every
//     read site now uses Volatile.Read and every write site Volatile.Write.
//     These tests pin the observable contract: once the quitting flag is set,
//     all singleton accessors must return null — including from a background thread
//     that has no lock-based acquire barrier.
//
//   Issue 6 — RoomManager event subscription leak on early Disconnect / teardown.
//     Cleanup() (called from OnDestroy and OnApplicationQuit) previously left
//     _roomManager with live delegate references to the NetworkManager, creating
//     a reference cycle that prevented GC.  After the fix Cleanup() unsubscribes
//     all five events and nulls the field.

using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    // ── Issue 5: _applicationIsQuitting Volatile semantics ────────────────────

    [TestFixture]
    [Category("Threading")]
    public class ApplicationQuittingFlagTests
    {
        [SetUp]
        public void SetUp()
        {
            foreach (var nm in UnityEngine.Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(nm.gameObject);

            NetworkManager.ResetMissingInstanceWarningForTests();
            NetworkManager.ResetApplicationQuittingForTests();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var nm in UnityEngine.Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(nm.gameObject);

            // Always reset so a failing test does not poison the next fixture.
            NetworkManager.ResetApplicationQuittingForTests();
        }

        [Test]
        [Description("Instance must return null once the quitting flag is set, even when " +
                     "a scene-placed manager exists — prevents use-after-quit access.")]
        public void Instance_AfterSimulatedQuit_ReturnsNullEvenWithSceneManager()
        {
            var go = new GameObject("NM_QuitTest");
            var nm = go.AddComponent<NetworkManager>();
            Assert.AreSame(nm, NetworkManager.Instance,
                "precondition: manager is accessible before quit");

            NetworkManager.SimulateApplicationQuitForTests();

            Assert.IsNull(NetworkManager.Instance,
                "Instance must return null after _applicationIsQuitting is set.");
        }

        [Test]
        [Description("TryGetInstance must return false once the quitting flag is set.")]
        public void TryGetInstance_AfterSimulatedQuit_ReturnsFalse()
        {
            var go = new GameObject("NM_QuitTest2");
            go.AddComponent<NetworkManager>();

            NetworkManager.SimulateApplicationQuitForTests();

            bool found = NetworkManager.TryGetInstance(out var manager);
            Assert.IsFalse(found, "TryGetInstance must return false after quit");
            Assert.IsNull(manager, "out parameter must be null after quit");
        }

        [Test]
        [Description("HasInstance must return false once the quitting flag is set.")]
        public void HasInstance_AfterSimulatedQuit_ReturnsFalse()
        {
            var go = new GameObject("NM_QuitTest3");
            go.AddComponent<NetworkManager>();

            NetworkManager.SimulateApplicationQuitForTests();

            Assert.IsFalse(NetworkManager.HasInstance,
                "HasInstance must return false after _applicationIsQuitting is set.");
        }

        [Test]
        [Description("A background thread reading Instance after the quitting flag is " +
                     "set via Volatile.Write must observe null — validates that the " +
                     "write barrier is honoured across threads (IL2CPP-ARM regression).")]
        public void Instance_BackgroundThread_ObservesNullAfterVolatileWrite()
        {
            var go = new GameObject("NM_QuitTest4");
            go.AddComponent<NetworkManager>();

            // Set the flag via Volatile.Write on the main thread.
            NetworkManager.SimulateApplicationQuitForTests();

            // Full fence so the CPU cannot reorder the store before thread creation.
            // Thread.Start() is itself a sync point; the fence makes the intent explicit.
            Thread.MemoryBarrier();

            NetworkManager observed = null;
            Exception thrown = null;

            var t = new Thread(() =>
            {
                try { observed = NetworkManager.Instance; }
                catch (Exception ex) { thrown = ex; }
            });
            t.Start();
            Assert.IsTrue(t.Join(TimeSpan.FromSeconds(2)),
                "background thread must return within 2 s");

            Assert.IsNull(thrown, $"no exception expected; got: {thrown}");
            Assert.IsNull(observed,
                "background thread must see null from Instance after Volatile.Write(true)");
        }

        [Test]
        [Description("ResetApplicationQuittingForTests clears the flag so the singleton " +
                     "becomes accessible again — verifies the test-hook symmetry.")]
        public void ResetApplicationQuitting_AllowsInstanceAccessAgain()
        {
            var go = new GameObject("NM_ResetTest");
            go.AddComponent<NetworkManager>();

            NetworkManager.SimulateApplicationQuitForTests();
            Assert.IsNull(NetworkManager.Instance, "should be null after SimulateQuit");

            NetworkManager.ResetApplicationQuittingForTests();

            Assert.IsNotNull(NetworkManager.Instance,
                "Instance should be accessible again after ResetApplicationQuitting");
        }
    }

    // ── Issue 6: RoomManager subscription lifecycle ───────────────────────────

    [TestFixture]
    [Category("Architecture")]
    public class RoomManagerLeakTests
    {
        private GameObject _nmGo;
        private NetworkManager _nm;

        [SetUp]
        public void SetUp()
        {
            foreach (var existing in UnityEngine.Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(existing.gameObject);

            _nmGo = new GameObject("NM_LeakTest");
            _nm   = _nmGo.AddComponent<NetworkManager>();
            NetworkManager.ResetApplicationQuittingForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (_nmGo != null)
                UnityEngine.Object.DestroyImmediate(_nmGo);
            NetworkManager.ResetApplicationQuittingForTests();
        }

        [Test]
        [Description("SetupRoomManagerForTests wires a live RoomManager with subscriptions — " +
                     "prerequisite sanity check for the leak tests below.")]
        public void SetupRoomManagerForTests_CreatesRoomManagerWithSubscriptions()
        {
            _nm.SetupRoomManagerForTests();

            var rm = _nm.GetRoomManagerForTests();
            Assert.IsNotNull(rm, "RoomManager must be created by SetupRoomManagerForTests");

            // OnRoomJoined must have at least the one handler wired by NetworkManager.
            Assert.Greater(rm.GetOnRoomJoinedSubscriberCount(), 0,
                "NetworkManager must subscribe to OnRoomJoined during RecreateRoomAndSpawnManagers");
        }

        [Test]
        [Description("InvokeCleanupForTests (mirrors OnDestroy path) must unsubscribe all " +
                     "RoomManager events and null the _roomManager field — fixing the " +
                     "delegate reference cycle that prevented GC after early disconnect.")]
        public void Cleanup_AfterRoomManagerSetup_NullsRoomManagerAndRemovesHandlers()
        {
            _nm.SetupRoomManagerForTests();

            // Grab the reference BEFORE cleanup so we can inspect it afterwards.
            var rm = _nm.GetRoomManagerForTests();
            Assert.IsNotNull(rm, "precondition: RoomManager must exist before cleanup");

            // Verify at least one event has a subscriber before we call Cleanup.
            int handlersBefore = rm.GetOnRoomJoinedSubscriberCount()
                               + rm.GetOnRoomLeftSubscriberCount()
                               + rm.GetOnRoomCreatedSubscriberCount();
            Assert.Greater(handlersBefore, 0,
                "precondition: at least one event must have a handler before cleanup");

            // Invoke Cleanup via the test hook (equivalent to DestroyImmediate path).
            _nm.InvokeCleanupForTests();

            // _roomManager field must be null — breaking the reference cycle.
            Assert.IsNull(_nm.GetRoomManagerForTests(),
                "Cleanup must null _roomManager to allow GC of both sides.");

            // All five events on the CAPTURED rm instance must have zero handlers.
            // If any NetworkManager delegate is still wired, the reference cycle
            // persists even after the field is nulled.
            Assert.AreEqual(0, rm.GetOnRoomJoinedSubscriberCount(),
                "OnRoomJoined must have no handlers after Cleanup");
            Assert.AreEqual(0, rm.GetOnRoomLeftSubscriberCount(),
                "OnRoomLeft must have no handlers after Cleanup");
            Assert.AreEqual(0, rm.GetOnRoomCreatedSubscriberCount(),
                "OnRoomCreated must have no handlers after Cleanup");
            Assert.AreEqual(0, rm.GetOnPlayerLeftSubscriberCount(),
                "OnPlayerLeft must have no handlers after Cleanup");
            Assert.AreEqual(0, rm.GetOnPlayerJoinedSubscriberCount(),
                "OnPlayerJoined must have no handlers after Cleanup");
        }

        [Test]
        [Description("OnDestroy (via DestroyImmediate) must also unsubscribe RoomManager " +
                     "events — verifies the full production teardown path.")]
        public void OnDestroy_UnsubscribesRoomManagerEvents()
        {
            _nm.SetupRoomManagerForTests();
            var rm = _nm.GetRoomManagerForTests();
            Assert.IsNotNull(rm, "precondition");

            // Simulate normal component teardown.
            UnityEngine.Object.DestroyImmediate(_nmGo);
            _nmGo = null;  // TearDown guard: already destroyed.

            // After destruction all five event delegate lists must be empty.
            int total = rm.GetOnRoomJoinedSubscriberCount()
                      + rm.GetOnRoomLeftSubscriberCount()
                      + rm.GetOnRoomCreatedSubscriberCount()
                      + rm.GetOnPlayerLeftSubscriberCount()
                      + rm.GetOnPlayerJoinedSubscriberCount();

            Assert.AreEqual(0, total,
                "All RoomManager event handlers must be removed after OnDestroy. " +
                $"Found {total} lingering delegate(s).");
        }

        [Test]
        [Description("Calling SetupRoomManagerForTests twice (simulating reconnect) must " +
                     "not accumulate duplicate handlers on the new RoomManager — verifies " +
                     "that RecreateRoomAndSpawnManagers unsubscribes before re-wiring.")]
        public void DoubleRecreate_DoesNotAccumulateHandlers()
        {
            _nm.SetupRoomManagerForTests();
            _nm.SetupRoomManagerForTests(); // Second call simulates reconnect.

            var rm = _nm.GetRoomManagerForTests();
            Assert.IsNotNull(rm, "RoomManager must exist after double-setup");

            // Each event should have exactly 1 handler, not 2.
            Assert.AreEqual(1, rm.GetOnRoomJoinedSubscriberCount(),
                "OnRoomJoined must have exactly 1 handler after reconnect — " +
                "no duplicate accumulation across RecreateRoomAndSpawnManagers calls.");
        }
    }
}
