// RTMPE SDK — Tests/Runtime/NetworkBehaviourTests.cs
//
// NUnit Edit-Mode tests for NetworkBehaviour.
//
// All tests create and destroy their own GameObjects.
// SetUp creates a NetworkManager so that NetworkManager.Instance is non-null
// and NetworkBehaviour.IsOwner can successfully call Instance.LocalPlayerStringId.
// TearDown destroys BOTH the test object AND the NetworkManager to prevent
// singleton leaks between test cases.
//
// Internal members (Initialize, SetSpawned, SetOwner, SetLocalPlayerStringId)
// are accessible because AssemblyInfo.cs declares:
//  [assembly: InternalsVisibleTo("RTMPE.SDK.Tests")]

using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("NetworkBehaviour")]
    public class NetworkBehaviourTests
    {
        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _testObject;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_testObject != null)
            {
                Object.DestroyImmediate(_testObject);
                _testObject = null;
            }
            if (_nmGo != null)
            {
                // OnDestroy sets NetworkManager._instance = null via lock.
                Object.DestroyImmediate(_nmGo);
                _nmGo = null;
            }
        }

        // ── IsOwner ────────────────────────────────────────────────────────────

        [Test]
        [Description("IsOwner returns true when ownerPlayerId matches LocalPlayerStringId.")]
        public void IsOwner_OwnerMatchesLocalPlayer_ReturnsTrue()
        {
            _manager.SetLocalPlayerStringId("player-uuid-abc");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "player-uuid-abc");

            Assert.IsTrue(nb.IsOwner);
        }

        [Test]
        [Description("IsOwner returns false when ownerPlayerId differs from LocalPlayerStringId.")]
        public void IsOwner_OwnerDiffersFromLocalPlayer_ReturnsFalse()
        {
            _manager.SetLocalPlayerStringId("player-uuid-abc");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "player-uuid-xyz");

            Assert.IsFalse(nb.IsOwner);
        }

        [Test]
        [Description("IsOwner returns false when LocalPlayerStringId is null (not yet set).")]
        public void IsOwner_LocalPlayerNotSet_ReturnsFalse()
        {
            // LocalPlayerStringId is null after fresh construction.
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "some-owner");

            Assert.IsFalse(nb.IsOwner);
        }

        [Test]
        [Description("IsOwner returns false when ownerPlayerId is empty (uninitialized object).")]
        public void IsOwner_EmptyOwnerPlayerId_ReturnsFalse()
        {
            _manager.SetLocalPlayerStringId("player-uuid-abc");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, string.Empty);

            Assert.IsFalse(nb.IsOwner);
        }

        [Test]
        [Description("IsOwner returns false when ownerPlayerId is null (Initialize with null).")]
        public void IsOwner_NullOwnerPlayerId_ReturnsFalse()
        {
            _manager.SetLocalPlayerStringId("player-uuid-abc");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, null);   // null → stored as empty string

            Assert.IsFalse(nb.IsOwner);
        }

        // ── Initialize ─────────────────────────────────────────────────────────

        [Test]
        [Description("Initialize sets NetworkObjectId.")]
        public void Initialize_SetsNetworkObjectId()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(42UL, "player-1");

            Assert.AreEqual(42UL, nb.NetworkObjectId);
        }

        [Test]
        [Description("Initialize sets OwnerPlayerId.")]
        public void Initialize_SetsOwnerPlayerId()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "player-uuid-456");

            Assert.AreEqual("player-uuid-456", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Initialize with null ownerId stores empty string, not null.")]
        public void Initialize_NullOwnerId_StoresEmptyString()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, null);

            Assert.AreEqual(string.Empty, nb.OwnerPlayerId);
        }

        // ── IsSpawned / SetSpawned ─────────────────────────────────────────────

        [Test]
        [Description("IsSpawned is false before SetSpawned is called.")]
        public void IsSpawned_BeforeSetSpawned_IsFalse()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            Assert.IsFalse(nb.IsSpawned);
        }

        [Test]
        [Description("SetSpawned(true) fires OnNetworkSpawn and sets IsSpawned=true.")]
        public void SetSpawned_True_SetsIsSpawnedAndFiresCallback()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            nb.SetSpawned(true);

            Assert.IsTrue(nb.IsSpawned);
            Assert.IsTrue(nb.SpawnedCalled);
        }

        [Test]
        [Description("SetSpawned(false) after true fires OnNetworkDespawn and sets IsSpawned=false.")]
        public void SetSpawned_FalseAfterTrue_SetsIsSpawnedFalseAndFiresCallback()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            nb.SetSpawned(true);
            nb.SetSpawned(false);

            Assert.IsFalse(nb.IsSpawned);
            Assert.IsTrue(nb.DespawnedCalled);
        }

        [Test]
        [Description("Calling SetSpawned(true) twice only fires OnNetworkSpawn once.")]
        public void SetSpawned_TrueTwice_OnlySpawnsOnce()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            nb.SetSpawned(true);
            nb.SetSpawned(true);

            Assert.AreEqual(1, nb.SpawnCount);
        }

        [Test]
        [Description("Calling SetSpawned(false) when already not spawned does nothing.")]
        public void SetSpawned_FalseWhenAlreadyFalse_DoesNotFire()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            nb.SetSpawned(false);   // no-op: was already false

            Assert.IsFalse(nb.DespawnedCalled);
        }

        // ── SetOwner / OnOwnershipChanged ──────────────────────────────────────

        [Test]
        [Description("SetOwner changes OwnerPlayerId and fires OnOwnershipChanged.")]
        public void SetOwner_UpdatesOwnerAndFiresCallback()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "original-owner");

            nb.SetOwner("new-owner");

            Assert.AreEqual("new-owner",       nb.OwnerPlayerId);
            Assert.AreEqual("original-owner",  nb.PreviousOwnerOnChange);
            Assert.AreEqual("new-owner",       nb.NewOwnerOnChange);
        }

        [Test]
        [Description("SetOwner with null stores empty string and fires callback.")]
        public void SetOwner_Null_StoresEmptyStringAndFires()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "original-owner");

            nb.SetOwner(null);

            Assert.AreEqual(string.Empty, nb.OwnerPlayerId);
            Assert.IsTrue(nb.OwnershipCallbackFired);
        }

        [Test]
        [Description("SetOwner with the same value does NOT fire OnOwnershipChanged when value is unchanged.")]
        public void SetOwner_SameValue_DoesNotFireCallback()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "alice");

            nb.SetOwner("alice");   // same — should be a no-op

            Assert.IsFalse(nb.OwnershipCallbackFired, "Callback must NOT fire when owner is unchanged.");
            Assert.AreEqual("alice", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Initialize called twice logs a warning; second call overwrites.")]
        public void Initialize_CalledTwice_OverwritesIdWithWarning()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");
            nb.Initialize(99UL, "p2");   // should log warning and overwrite

            Assert.AreEqual(99UL,  nb.NetworkObjectId);
            Assert.AreEqual("p2",  nb.OwnerPlayerId);
        }

        // ── DestroyWithOwner default ───────────────────────────────────────────

        [Test]
        [Description("DestroyWithOwner defaults to true.")]
        public void DestroyWithOwner_Default_IsTrue()
        {
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "p1");

            Assert.IsTrue(nb.DestroyWithOwner);
        }

        // ── NetworkManager.LocalPlayerStringId (internal helper) ───────────────

        [Test]
        [Description("SetLocalPlayerStringId updates LocalPlayerStringId.")]
        public void NetworkManager_SetLocalPlayerStringId_UpdatesProperty()
        {
            _manager.SetLocalPlayerStringId("test-player-id-999");
            Assert.AreEqual("test-player-id-999", _manager.LocalPlayerStringId);
        }

        [Test]
        [Description("LocalPlayerStringId is null before any assignment.")]
        public void NetworkManager_LocalPlayerStringId_NullByDefault()
        {
            Assert.IsNull(_manager.LocalPlayerStringId);
        }

        // ── Cached local-player snapshot ───────────────────────────────────────

        [Test]
        [Description("IsOwner returns true after Initialize captures the local-player UUID, even " +
                     "if NetworkManager.Instance is later torn down (OnDestroy/OnApplicationQuit).")]
        public void IsOwner_RemainsTrue_AfterInstanceTeardown()
        {
            _manager.SetLocalPlayerStringId("player-uuid-abc");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "player-uuid-abc");
            // Confirm the precondition.
            Assert.IsTrue(nb.IsOwner);

            // Tear down the manager — NetworkManager.Instance now returns null.
            Object.DestroyImmediate(_nmGo);
            _nmGo = null;
            Assert.IsNull(NetworkManager.Instance,
                "Precondition: Instance must be null after manager destruction.");

            // IsOwner must still return true so OnDestroy can take owner cleanup.
            Assert.IsTrue(nb.IsOwner,
                "IsOwner must remain true post-Instance-teardown using the cached snapshot.");
        }

        [Test]
        [Description("IsOwner returns false post-Instance-teardown when the captured local-player " +
                     "UUID does not match the owner.")]
        public void IsOwner_FalseAfterInstanceTeardown_WhenOwnerMismatch()
        {
            _manager.SetLocalPlayerStringId("player-uuid-self");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "player-uuid-other");

            Object.DestroyImmediate(_nmGo);
            _nmGo = null;
            Assert.IsNull(NetworkManager.Instance);

            Assert.IsFalse(nb.IsOwner,
                "Cached snapshot must still differentiate non-owner objects post-teardown.");
        }

        [Test]
        [Description("SetSpawned(true) refreshes the cached local-player UUID so a reconnect that " +
                     "minted a new local id between Initialize and Spawn is observable.")]
        public void SetSpawned_RefreshesCachedLocalPlayerId()
        {
            _manager.SetLocalPlayerStringId("old-id");
            _testObject = new GameObject("obj");
            var nb = _testObject.AddComponent<ConcreteNetworkBehaviour>();
            nb.Initialize(1UL, "new-id");
            Assert.IsFalse(nb.IsOwner, "Pre-spawn cache should reflect the old local id.");

            // Reconnect produces a new local player id before spawn fires.
            _manager.SetLocalPlayerStringId("new-id");
            nb.SetSpawned(true);

            Assert.IsTrue(nb.IsOwner,
                "SetSpawned(true) must refresh the cache so the post-reconnect id is observable.");
        }

        // ── Concrete test double ───────────────────────────────────────────────

        private sealed class ConcreteNetworkBehaviour : NetworkBehaviour
        {
            public bool   SpawnedCalled          { get; private set; }
            public bool   DespawnedCalled         { get; private set; }
            public int    SpawnCount              { get; private set; }
            public bool   OwnershipCallbackFired  { get; private set; }
            public string PreviousOwnerOnChange   { get; private set; }
            public string NewOwnerOnChange        { get; private set; }

            protected override void OnNetworkSpawn()
            {
                SpawnedCalled = true;
                SpawnCount++;
            }

            protected override void OnNetworkDespawn()
            {
                DespawnedCalled = true;
            }

            protected override void OnOwnershipChanged(string previousOwner, string newOwner)
            {
                OwnershipCallbackFired = true;
                PreviousOwnerOnChange  = previousOwner;
                NewOwnerOnChange       = newOwner;
            }
        }
    }
}
