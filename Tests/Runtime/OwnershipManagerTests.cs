// RTMPE SDK — Tests/Runtime/OwnershipManagerTests.cs
//
// NUnit Edit-Mode tests for OwnershipManager.
//
// Internal members accessed via InternalsVisibleTo("RTMPE.SDK.Tests").
// Each test gets a fresh NetworkObjectRegistry + OwnershipManager instance.
// All GameObjects are destroyed in TearDown.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("OwnershipManager")]
    public class OwnershipManagerTests
    {
        private NetworkObjectRegistry _registry;
        private OwnershipManager      _ownership;
        private NetworkManager        _manager;

        private GameObject            _nmGo;
        private readonly List<GameObject> _created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            // NetworkManager singleton is required by NetworkBehaviour.IsOwner.
            _nmGo    = new GameObject("NetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _registry  = new NetworkObjectRegistry();
            _ownership = new OwnershipManager(_registry, _manager);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();

            Object.DestroyImmediate(_nmGo); // clears singleton
        }

        // ── Helper ─────────────────────────────────────────────────────────────

        private ConcreteNB RegisterObject(ulong objectId, string ownerId = "player-uuid-1")
        {
            var go = new GameObject($"obj-{objectId}");
            _created.Add(go);
            var nb = go.AddComponent<ConcreteNB>();
            nb.Initialize(objectId, ownerId);
            nb.SetSpawned(true);
            _registry.Register(nb);
            return nb;
        }

        // ── Constructor ────────────────────────────────────────────────────────

        [Test]
        [Description("Constructor throws when registry is null.")]
        public void Constructor_NullRegistry_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new OwnershipManager(null, _manager));
        }

        [Test]
        [Description("Constructor throws when networkManager is null.")]
        public void Constructor_NullNetworkManager_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new OwnershipManager(_registry, null));
        }

        // ── ApplyOwnershipGrant ────────────────────────────────────────────────

        [Test]
        [Description("ApplyOwnershipGrant updates the object's owner player ID.")]
        public void ApplyOwnershipGrant_KnownObject_SetsOwner()
        {
            var nb = RegisterObject(10UL, "old-owner");

            _ownership.ApplyOwnershipGrant(10UL, "new-owner");

            Assert.AreEqual("new-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("ApplyOwnershipGrant fires OnOwnershipChanged on the object.")]
        public void ApplyOwnershipGrant_FiresOwnershipChangedCallback()
        {
            var nb = RegisterObject(10UL, "old-owner");

            _ownership.ApplyOwnershipGrant(10UL, "new-owner");

            Assert.IsTrue(nb.OwnerChangeCallbackFired,         "OnOwnershipChanged should have been called.");
            Assert.AreEqual("old-owner", nb.PreviousOwnerOnChange);
            Assert.AreEqual("new-owner", nb.NewOwnerOnChange);
        }

        [Test]
        [Description("ApplyOwnershipGrant on unknown object ID logs warning and does not throw.")]
        public void ApplyOwnershipGrant_UnknownObject_IsNoOp()
        {
            // No object registered with ID 999.
            Assert.DoesNotThrow(() => _ownership.ApplyOwnershipGrant(999UL, "new-owner"));
        }

        // ── RequestOwnershipTransfer (stub) ─────────────────────────────────────

        [Test]
        [Description("RequestOwnershipTransfer when not the owner logs error and does not transfer.")]
        public void RequestOwnershipTransfer_NotOwner_LogsErrorAndDoesNotTransfer()
        {
            // Local player = "p-local"; object owned by "p-other".
            _manager.SetLocalPlayerStringId("p-local");
            var nb = RegisterObject(20UL, "p-other");

            // RequestOwnershipTransfer calls Debug.LogError when local player is not the owner.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not the current owner"));
            Assert.DoesNotThrow(() => _ownership.RequestOwnershipTransfer(20UL, "p-target"));

            // Owner must remain unchanged.
            Assert.AreEqual("p-other", nb.OwnerPlayerId);
        }

        [Test]
        [Description("RequestOwnershipTransfer when local IS the owner logs a stub-warning and does not mutate local state.")]
        public void RequestOwnershipTransfer_IsOwner_LogsWarningAndDoesNotMutate()
        {
            _manager.SetLocalPlayerStringId("p-local");
            var nb = RegisterObject(21UL, "p-local");

            Assert.DoesNotThrow(() => _ownership.RequestOwnershipTransfer(21UL, "p-new"));

            // Stub must not mutate ownership; only ApplyOwnershipGrant (server response) changes local state.
            Assert.AreEqual("p-local", nb.OwnerPlayerId);
        }

        [Test]
        [Description("RequestOwnershipTransfer for unknown object is a no-op.")]
        public void RequestOwnershipTransfer_UnknownObject_IsNoOp()
        {
            _manager.SetLocalPlayerStringId("p-local");

            Assert.DoesNotThrow(() => _ownership.RequestOwnershipTransfer(999UL, "p-new"));
        }

        [Test]
        [Description("RequestOwnershipTransfer with empty newOwnerPlayerId is rejected.")]
        public void RequestOwnershipTransfer_EmptyNewOwner_IsRejected()
        {
            _manager.SetLocalPlayerStringId("p-local");
            var nb = RegisterObject(22UL, "p-local");

            // RequestOwnershipTransfer calls Debug.LogError when newOwnerPlayerId is null or empty.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("must not be null or empty"));
            Assert.DoesNotThrow(() => _ownership.RequestOwnershipTransfer(22UL, string.Empty));

            // Owner must remain unchanged.
            Assert.AreEqual("p-local", nb.OwnerPlayerId);
        }

        // ── GetObjectsOwnedBy ──────────────────────────────────────────────────

        [Test]
        [Description("GetObjectsOwnedBy returns only objects owned by the given player.")]
        public void GetObjectsOwnedBy_CorrectPlayer_ReturnsMatchingObjects()
        {
            RegisterObject(1UL, "alice");
            RegisterObject(2UL, "bob");
            RegisterObject(3UL, "alice");

            var aliceObjects = _ownership.GetObjectsOwnedBy("alice");

            Assert.AreEqual(2, aliceObjects.Count);
            foreach (var obj in aliceObjects)
                Assert.AreEqual("alice", obj.OwnerPlayerId);
        }

        [Test]
        [Description("GetObjectsOwnedBy with unknown player returns empty list.")]
        public void GetObjectsOwnedBy_UnknownPlayer_ReturnsEmpty()
        {
            RegisterObject(1UL, "alice");

            var result = _ownership.GetObjectsOwnedBy("charlie");

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        [Description("GetObjectsOwnedBy with empty playerId returns empty list.")]
        public void GetObjectsOwnedBy_EmptyPlayerId_ReturnsEmpty()
        {
            RegisterObject(1UL, "alice");

            var result = _ownership.GetObjectsOwnedBy(string.Empty);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        [Description("GetObjectsOwnedBy with null playerId returns empty list.")]
        public void GetObjectsOwnedBy_NullPlayerId_ReturnsEmpty()
        {
            RegisterObject(1UL, "alice");

            var result = _ownership.GetObjectsOwnedBy(null);

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        [Description("ApplyOwnershipGrant with same owner does NOT fire OnOwnershipChanged.")]
        public void ApplyOwnershipGrant_SameOwner_NoCallbackFired()
        {
            var nb = RegisterObject(30UL, "alice");

            _ownership.ApplyOwnershipGrant(30UL, "alice");  // same owner — no-change

            Assert.IsFalse(nb.OwnerChangeCallbackFired,
                "OnOwnershipChanged must NOT fire when the owner is already 'alice'.");
            Assert.AreEqual("alice", nb.OwnerPlayerId);
        }

        [Test]
        [Description("GetObjectsOwnedBy reflects ownership change after ApplyOwnershipGrant.")]
        public void GetObjectsOwnedBy_AfterGrant_ReflectsNewOwnership()
        {
            RegisterObject(40UL, "alice");
            RegisterObject(41UL, "bob");

            _ownership.ApplyOwnershipGrant(40UL, "bob");   // transfer 40 from alice → bob

            var bobObjects = _ownership.GetObjectsOwnedBy("bob");
            Assert.AreEqual(2, bobObjects.Count, "Bob should now own 2 objects.");

            var aliceObjects = _ownership.GetObjectsOwnedBy("alice");
            Assert.AreEqual(0, aliceObjects.Count, "Alice should own nothing.");
        }

        // ── Outstanding-request bookkeeping ────────────────────────────────────
        //
        // Defends against forged ownership-transfer responses.  A predictable
        // request_id allocation lets an attacker race a fake reply into the
        // open correlation window; ids are now drawn from a CSPRNG and we
        // refuse any response whose id was never issued.

        [Test]
        [Description("Unknown request ids are rejected by TryAcknowledgeResponse.")]
        public void TryAcknowledgeResponse_UnknownId_Rejected()
        {
            _ownership.ResetOutstandingForTest();
            Assert.IsFalse(_ownership.TryAcknowledgeResponse(0xDEADBEEF));
            Assert.IsFalse(_ownership.TryAcknowledgeResponse(0));
            Assert.AreEqual(0, _ownership.OutstandingCount);
        }

        [Test]
        [Description("Stale entries past the TTL are pruned and no longer ack-able.")]
        public void PruneExpiredOutstanding_RemovesStaleEntries()
        {
            _ownership.ResetOutstandingForTest();

            // Inject one expired entry directly so the test does not depend on
            // real wall time.
            var fld = typeof(OwnershipManager).GetField(
                "_outstandingDeadlineMs",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var setFld = typeof(OwnershipManager).GetField(
                "_outstanding",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var deadlines = (System.Collections.Generic.Dictionary<uint, long>)fld.GetValue(_ownership);
            var set = (System.Collections.Generic.HashSet<uint>)setFld.GetValue(_ownership);
            deadlines[42u] = 0;     // already expired
            set.Add(42u);

            _ownership.PruneExpiredOutstanding();

            Assert.AreEqual(0, _ownership.OutstandingCount);
            Assert.IsFalse(_ownership.TryAcknowledgeResponse(42u));
        }

        [Test]
        [Description("Allocated ids are not the trivial monotonic 1, 2, 3 sequence.")]
        public void AllocateOutstandingRequestId_IsNotTrivialMonotonic()
        {
            _ownership.ResetOutstandingForTest();
            var mi = typeof(OwnershipManager).GetMethod(
                "AllocateOutstandingRequestId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            uint a = (uint)mi.Invoke(_ownership, null);
            uint b = (uint)mi.Invoke(_ownership, null);
            uint c = (uint)mi.Invoke(_ownership, null);

            Assert.AreNotEqual(0u, a);
            Assert.AreNotEqual(0u, b);
            Assert.AreNotEqual(0u, c);
            // Three random uint draws colliding to a 1,2,3 sequence has
            // probability ~2^-96 — catches a regression that reverts the
            // CSPRNG to a counter.
            bool monotonic = (b == a + 1u) && (c == b + 1u);
            Assert.IsFalse(monotonic);
            Assert.AreEqual(3, _ownership.OutstandingCount);
        }

        [Test]
        [Description("Known id ack succeeds exactly once; replay rejected.")]
        public void TryAcknowledgeResponse_KnownId_AcceptedOnceThenRejected()
        {
            _ownership.ResetOutstandingForTest();
            var mi = typeof(OwnershipManager).GetMethod(
                "AllocateOutstandingRequestId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            uint id = (uint)mi.Invoke(_ownership, null);

            Assert.IsTrue(_ownership.TryAcknowledgeResponse(id));
            Assert.IsFalse(_ownership.TryAcknowledgeResponse(id));
        }

        // ── Test double ────────────────────────────────────────────────────────

        private sealed class ConcreteNB : NetworkBehaviour
        {
            public bool   OwnerChangeCallbackFired { get; private set; }
            public string PreviousOwnerOnChange    { get; private set; }
            public string NewOwnerOnChange         { get; private set; }

            protected override void OnOwnershipChanged(string previousOwner, string newOwner)
            {
                OwnerChangeCallbackFired = true;
                PreviousOwnerOnChange    = previousOwner;
                NewOwnerOnChange         = newOwner;
            }
        }
    }
}
