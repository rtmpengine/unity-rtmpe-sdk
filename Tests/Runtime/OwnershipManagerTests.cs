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

        // ── RequestOwnershipTransfer (stub — Week 17) ──────────────────────────

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
        [Description("RequestOwnershipTransfer when local IS the owner logs stub-warning and does not mutate (Week 17 pending).")]
        public void RequestOwnershipTransfer_IsOwner_LogsWarningAndDoesNotMutate()
        {
            _manager.SetLocalPlayerStringId("p-local");
            var nb = RegisterObject(21UL, "p-local");

            Assert.DoesNotThrow(() => _ownership.RequestOwnershipTransfer(21UL, "p-new"));

            // C-2 fix: stub must NOT mutate ownership — Week 17 will do that.
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
        [Description("ApplyOwnershipGrant with same owner does NOT fire OnOwnershipChanged (H-2 fix via OwnershipManager path).")]
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
