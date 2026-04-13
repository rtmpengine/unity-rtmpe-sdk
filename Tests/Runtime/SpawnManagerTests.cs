// RTMPE SDK — Tests/Runtime/SpawnManagerTests.cs
//
// NUnit Edit-Mode tests for SpawnManager.
//
// Internal members (CreateLocal, DestroyLocal) are accessible via InternalsVisibleTo.
// Each test gets a fresh NetworkManager + SpawnManager.
// All GameObjects created per-test are destroyed in TearDown.
//
// Note on Object.Destroy vs Object.DestroyImmediate:
//   Object.Destroy() is used by production code (frame-safe) but deferred in
//   Edit Mode tests. Tests validate logical state (IsSpawned, registry) rather
//   than physical destruction of GameObjects. TearDown uses DestroyImmediate.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("SpawnManager")]
    public class SpawnManagerTests
    {
        private NetworkManager        _manager;
        private SpawnManager          _spawnManager;
        private NetworkObjectRegistry _registry;
        private OwnershipManager      _ownership;

        private GameObject            _nmGo;
        private GameObject            _prefabGo;
        private readonly List<GameObject> _created = new List<GameObject>();

        private const uint PREFAB_ID = 1;

        [SetUp]
        public void SetUp()
        {
            // NetworkManager singleton (required by NetworkBehaviour.IsOwner).
            _nmGo    = new GameObject("NetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _registry  = new NetworkObjectRegistry();
            _ownership = new OwnershipManager(_registry, _manager);
            _spawnManager = new SpawnManager(_registry, _ownership, _manager);

            // Create a reusable "prefab" — a plain GO with a SpawnableNB component.
            _prefabGo = new GameObject("Prefab");
            _prefabGo.AddComponent<SpawnableNB>();
            _created.Add(_prefabGo);
        }

        [TearDown]
        public void TearDown()
        {
            // Destroy spawned objects via registry to keep state clean.
            foreach (var obj in _registry.GetAll())
            {
                if (obj != null)
                    Object.DestroyImmediate(obj.gameObject);
            }

            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();

            Object.DestroyImmediate(_nmGo);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void RegisterDefaultPrefab()
        {
            _spawnManager.RegisterPrefab(PREFAB_ID, _prefabGo);
        }

        private SpawnableNB SpawnDefault(
            Vector3? pos = null,
            Quaternion? rot = null,
            string owner = null)
        {
            var nb = (SpawnableNB)_spawnManager.Spawn(
                PREFAB_ID,
                pos ?? Vector3.zero,
                rot ?? Quaternion.identity,
                owner);
            // Track the instantiated GO for TearDown cleanup.
            // Object.Destroy is deferred in Edit Mode tests — DestroyImmediate
            // in TearDown ensures the GO is gone before the next test.
            if (nb != null) _created.Add(nb.gameObject);
            return nb;
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Constructor ────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("Constructor throws when registry is null.")]
        public void Constructor_NullRegistry_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new SpawnManager(null, _ownership, _manager));
        }

        [Test]
        [Description("Constructor throws when ownership is null.")]
        public void Constructor_NullOwnership_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new SpawnManager(_registry, null, _manager));
        }

        [Test]
        [Description("Constructor throws when networkManager is null.")]
        public void Constructor_NullNetworkManager_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => new SpawnManager(_registry, _ownership, null));
        }

        [Test]
        [Description("Constructor succeeds with valid parameters.")]
        public void Constructor_ValidArgs_SetsProperties()
        {
            Assert.AreSame(_registry, _spawnManager.Registry);
            Assert.AreSame(_ownership, _spawnManager.Ownership);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Prefab Registration ────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("RegisterPrefab stores the prefab for later spawning.")]
        public void RegisterPrefab_Basic_CanBeQueried()
        {
            _spawnManager.RegisterPrefab(PREFAB_ID, _prefabGo);

            Assert.IsTrue(_spawnManager.HasPrefab(PREFAB_ID));
        }

        [Test]
        [Description("RegisterPrefab with null prefab throws.")]
        public void RegisterPrefab_NullPrefab_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(
                () => _spawnManager.RegisterPrefab(PREFAB_ID, null));
        }

        [Test]
        [Description("RegisterPrefab with duplicate ID overwrites and logs warning.")]
        public void RegisterPrefab_DuplicateId_OverwritesWithWarning()
        {
            var prefab2 = new GameObject("Prefab2");
            prefab2.AddComponent<SpawnableNB>();
            _created.Add(prefab2);

            _spawnManager.RegisterPrefab(PREFAB_ID, _prefabGo);

            // Second registration with same ID should log warning.
            _spawnManager.RegisterPrefab(PREFAB_ID, prefab2);

            Assert.IsTrue(_spawnManager.HasPrefab(PREFAB_ID));
        }

        [Test]
        [Description("UnregisterPrefab removes a registered prefab.")]
        public void UnregisterPrefab_Registered_ReturnsTrue()
        {
            _spawnManager.RegisterPrefab(PREFAB_ID, _prefabGo);

            Assert.IsTrue(_spawnManager.UnregisterPrefab(PREFAB_ID));
            Assert.IsFalse(_spawnManager.HasPrefab(PREFAB_ID));
        }

        [Test]
        [Description("UnregisterPrefab on unknown ID returns false.")]
        public void UnregisterPrefab_NotRegistered_ReturnsFalse()
        {
            Assert.IsFalse(_spawnManager.UnregisterPrefab(999));
        }

        [Test]
        [Description("HasPrefab returns false for an unknown ID.")]
        public void HasPrefab_Unknown_ReturnsFalse()
        {
            Assert.IsFalse(_spawnManager.HasPrefab(42));
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Spawn ──────────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("Spawn with a registered prefab creates an object with correct state.")]
        public void Spawn_RegisteredPrefab_CreatesObjectWithCorrectState()
        {
            RegisterDefaultPrefab();

            var nb = SpawnDefault(owner: "alice");

            Assert.IsNotNull(nb);
            Assert.IsTrue(nb.IsSpawned);
            Assert.AreNotEqual(0UL, nb.NetworkObjectId);
            Assert.AreEqual("alice", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Spawn registers the object in the registry.")]
        public void Spawn_RegisteredPrefab_ObjectIsInRegistry()
        {
            RegisterDefaultPrefab();

            var nb = SpawnDefault(owner: "bob");

            Assert.AreSame(nb, _registry.Get(nb.NetworkObjectId));
        }

        [Test]
        [Description("Spawn fires OnNetworkSpawn callback.")]
        public void Spawn_RegisteredPrefab_FiresOnNetworkSpawn()
        {
            RegisterDefaultPrefab();

            var nb = SpawnDefault();

            Assert.IsTrue(nb.SpawnCalled, "OnNetworkSpawn should have been called.");
        }

        [Test]
        [Description("Spawn with unregistered prefab returns null and logs error.")]
        public void Spawn_UnregisteredPrefab_ReturnsNullAndLogsError()
        {
            // No prefab registered for PREFAB_ID.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not registered"));

            var result = _spawnManager.Spawn(PREFAB_ID, Vector3.zero, Quaternion.identity);

            Assert.IsNull(result);
        }

        [Test]
        [Description("Spawn preserves position and rotation.")]
        public void Spawn_WithPositionAndRotation_PreservesTransform()
        {
            RegisterDefaultPrefab();
            var pos = new Vector3(1.5f, 2.5f, 3.5f);
            var rot = Quaternion.Euler(10f, 20f, 30f);

            var nb = _spawnManager.Spawn(PREFAB_ID, pos, rot);

            Assert.IsNotNull(nb);
            Assert.AreEqual(pos.x, nb.transform.position.x, 0.01f);
            Assert.AreEqual(pos.y, nb.transform.position.y, 0.01f);
            Assert.AreEqual(pos.z, nb.transform.position.z, 0.01f);
            Assert.AreEqual(rot.eulerAngles.x, nb.transform.rotation.eulerAngles.x, 0.5f);
            Assert.AreEqual(rot.eulerAngles.y, nb.transform.rotation.eulerAngles.y, 0.5f);
            Assert.AreEqual(rot.eulerAngles.z, nb.transform.rotation.eulerAngles.z, 0.5f);
        }

        [Test]
        [Description("Spawn generates unique IDs for consecutive objects.")]
        public void Spawn_MultipleCalls_GeneratesUniqueIds()
        {
            RegisterDefaultPrefab();

            var nb1 = SpawnDefault(owner: "p1");
            var nb2 = SpawnDefault(owner: "p1");
            var nb3 = SpawnDefault(owner: "p1");

            Assert.AreNotEqual(nb1.NetworkObjectId, nb2.NetworkObjectId);
            Assert.AreNotEqual(nb2.NetworkObjectId, nb3.NetworkObjectId);
            Assert.AreNotEqual(nb1.NetworkObjectId, nb3.NetworkObjectId);
        }

        [Test]
        [Description("Spawn with null owner defaults to empty string when LocalPlayerStringId is null.")]
        public void Spawn_NullOwner_DefaultsToEmpty()
        {
            RegisterDefaultPrefab();

            // LocalPlayerStringId is null in test environment.
            var nb = SpawnDefault();

            Assert.AreEqual(string.Empty, nb.OwnerPlayerId);
        }

        [Test]
        [Description("Spawn with explicit owner uses the provided owner.")]
        public void Spawn_ExplicitOwner_Overrides()
        {
            RegisterDefaultPrefab();
            _manager.SetLocalPlayerStringId("default-player");

            var nb = SpawnDefault(owner: "explicit-owner");

            Assert.AreEqual("explicit-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Spawn with null owner defaults to LocalPlayerStringId if set.")]
        public void Spawn_NullOwner_UsesLocalPlayerStringId()
        {
            RegisterDefaultPrefab();
            _manager.SetLocalPlayerStringId("local-uuid");

            var nb = SpawnDefault();

            Assert.AreEqual("local-uuid", nb.OwnerPlayerId);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Despawn ────────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("Despawn marks object as not spawned and fires OnNetworkDespawn.")]
        public void Despawn_ExistingObject_DespawnsAndFiresCallback()
        {
            RegisterDefaultPrefab();
            var nb = SpawnDefault(owner: "alice");
            var id = nb.NetworkObjectId;

            _spawnManager.Despawn(id);

            Assert.IsFalse(nb.IsSpawned);
            Assert.IsTrue(nb.DespawnCalled, "OnNetworkDespawn should have been called.");
        }

        [Test]
        [Description("Despawn removes the object from the registry.")]
        public void Despawn_ExistingObject_RemovesFromRegistry()
        {
            RegisterDefaultPrefab();
            var nb = SpawnDefault(owner: "alice");
            var id = nb.NetworkObjectId;

            _spawnManager.Despawn(id);

            Assert.IsNull(_registry.Get(id));
        }

        [Test]
        [Description("Despawn with unknown ID is a safe no-op.")]
        public void Despawn_UnknownId_IsNoOp()
        {
            Assert.DoesNotThrow(() => _spawnManager.Despawn(99999UL));
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── CreateLocal / DestroyLocal (internal) ──────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("CreateLocal instantiates an object with correct ID and owner.")]
        public void CreateLocal_Basic_SetsIdAndOwner()
        {
            RegisterDefaultPrefab();

            var nb = _spawnManager.CreateLocal(PREFAB_ID, 42UL, "p-owner", Vector3.zero, Quaternion.identity);
            if (nb != null) _created.Add(nb.gameObject);

            Assert.IsNotNull(nb);
            Assert.AreEqual(42UL, nb.NetworkObjectId);
            Assert.AreEqual("p-owner", nb.OwnerPlayerId);
            Assert.IsTrue(nb.IsSpawned);
        }

        [Test]
        [Description("CreateLocal with unregistered prefab returns null.")]
        public void CreateLocal_UnregisteredPrefab_ReturnsNull()
        {
            // prefabId 999 not registered — logs warning.
            var nb = _spawnManager.CreateLocal(999, 1UL, "p1", Vector3.zero, Quaternion.identity);

            Assert.IsNull(nb);
        }

        [Test]
        [Description("CreateLocal fires OnNetworkSpawn.")]
        public void CreateLocal_FiresOnNetworkSpawn()
        {
            RegisterDefaultPrefab();

            var nb = (SpawnableNB)_spawnManager.CreateLocal(
                PREFAB_ID, 100UL, "p1", Vector3.zero, Quaternion.identity);
            if (nb != null) _created.Add(nb.gameObject);

            Assert.IsTrue(nb.SpawnCalled);
        }

        [Test]
        [Description("CreateLocal with prefab that has no NetworkBehaviour logs error and returns null.")]
        public void CreateLocal_NoBehaviourOnPrefab_ReturnsNullAndLogsError()
        {
            var barePrefab = new GameObject("BarePrefab");
            _created.Add(barePrefab);
            _spawnManager.RegisterPrefab(2, barePrefab);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("no NetworkBehaviour"));

            var nb = _spawnManager.CreateLocal(2, 200UL, "p1", Vector3.zero, Quaternion.identity);

            Assert.IsNull(nb);
        }

        [Test]
        [Description("DestroyLocal fires OnNetworkDespawn and removes from registry.")]
        public void DestroyLocal_ExistingObject_DespawnsAndUnregisters()
        {
            RegisterDefaultPrefab();
            var nb = (SpawnableNB)_spawnManager.CreateLocal(
                PREFAB_ID, 300UL, "p1", Vector3.zero, Quaternion.identity);
            if (nb != null) _created.Add(nb.gameObject);

            _spawnManager.DestroyLocal(300UL);

            Assert.IsFalse(nb.IsSpawned);
            Assert.IsTrue(nb.DespawnCalled);
            Assert.IsNull(_registry.Get(300UL));
        }

        [Test]
        [Description("DestroyLocal with unknown ID is a safe no-op.")]
        public void DestroyLocal_UnknownId_IsNoOp()
        {
            Assert.DoesNotThrow(() => _spawnManager.DestroyLocal(88888UL));
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── OnPlayerLeftRoom ───────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("OnPlayerLeftRoom destroys objects with DestroyWithOwner=true.")]
        public void OnPlayerLeftRoom_DestroyWithOwner_DespawnsObject()
        {
            RegisterDefaultPrefab();
            var nb = SpawnDefault(owner: "leaving-player");
            nb.DestroyWithOwner = true;
            var id = nb.NetworkObjectId;

            _spawnManager.OnPlayerLeftRoom("leaving-player");

            Assert.IsFalse(nb.IsSpawned);
            Assert.IsNull(_registry.Get(id));
        }

        [Test]
        [Description("OnPlayerLeftRoom leaves objects with DestroyWithOwner=false intact.")]
        public void OnPlayerLeftRoom_NoDestroyWithOwner_LeavesObjectAlive()
        {
            RegisterDefaultPrefab();
            var nb = SpawnDefault(owner: "leaving-player");
            nb.DestroyWithOwner = false;
            var id = nb.NetworkObjectId;

            _spawnManager.OnPlayerLeftRoom("leaving-player");

            Assert.IsTrue(nb.IsSpawned, "Object must remain spawned.");
            Assert.AreSame(nb, _registry.Get(id), "Object must remain in registry.");
        }

        [Test]
        [Description("OnPlayerLeftRoom does not affect objects owned by other players.")]
        public void OnPlayerLeftRoom_OtherPlayer_DoesNotAffect()
        {
            RegisterDefaultPrefab();
            var nbKeep = SpawnDefault(owner: "staying-player");
            nbKeep.DestroyWithOwner = true;
            var nbLeave = SpawnDefault(owner: "leaving-player");
            nbLeave.DestroyWithOwner = true;

            _spawnManager.OnPlayerLeftRoom("leaving-player");

            Assert.IsTrue(nbKeep.IsSpawned, "Object owned by staying player must not be affected.");
            Assert.IsFalse(nbLeave.IsSpawned, "Object owned by leaving player must be despawned.");
        }

        [Test]
        [Description("OnPlayerLeftRoom with empty/null player ID is a safe no-op.")]
        public void OnPlayerLeftRoom_EmptyPlayerId_IsNoOp()
        {
            RegisterDefaultPrefab();
            var nb = SpawnDefault(owner: "alice");

            Assert.DoesNotThrow(() => _spawnManager.OnPlayerLeftRoom(null));
            Assert.DoesNotThrow(() => _spawnManager.OnPlayerLeftRoom(string.Empty));

            Assert.IsTrue(nb.IsSpawned, "Object must not be affected by null/empty player ID.");
        }

        [Test]
        [Description("OnPlayerLeftRoom handles mixture of DestroyWithOwner true and false.")]
        public void OnPlayerLeftRoom_MixedDestroyWithOwner_CorrectBehaviour()
        {
            RegisterDefaultPrefab();
            var nbDestroy = SpawnDefault(owner: "player-x");
            nbDestroy.DestroyWithOwner = true;

            var nbKeep = SpawnDefault(owner: "player-x");
            nbKeep.DestroyWithOwner = false;

            _spawnManager.OnPlayerLeftRoom("player-x");

            Assert.IsFalse(nbDestroy.IsSpawned, "DestroyWithOwner=true object must be despawned.");
            Assert.IsTrue(nbKeep.IsSpawned, "DestroyWithOwner=false object must survive.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── ClearAll ───────────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("ClearAll fires OnNetworkDespawn for all spawned objects.")]
        public void ClearAll_DespawnsAllObjects()
        {
            RegisterDefaultPrefab();
            var nb1 = SpawnDefault(owner: "p1");
            var nb2 = SpawnDefault(owner: "p2");

            _spawnManager.ClearAll();

            Assert.IsFalse(nb1.IsSpawned);
            Assert.IsFalse(nb2.IsSpawned);
            Assert.IsTrue(((SpawnableNB)nb1).DespawnCalled);
            Assert.IsTrue(((SpawnableNB)nb2).DespawnCalled);
        }

        [Test]
        [Description("ClearAll empties the registry.")]
        public void ClearAll_EmptiesRegistry()
        {
            RegisterDefaultPrefab();
            SpawnDefault(owner: "p1");
            SpawnDefault(owner: "p2");

            _spawnManager.ClearAll();

            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        [Test]
        [Description("ClearAll resets object ID counter so subsequent spawns start fresh.")]
        public void ClearAll_ResetsIdCounter()
        {
            RegisterDefaultPrefab();
            var first = SpawnDefault(owner: "p1");
            var firstId = first.NetworkObjectId;

            _spawnManager.ClearAll();

            // Spawn again — should get same ID pattern since counter reset.
            var second = SpawnDefault(owner: "p1");
            Assert.AreEqual(firstId, second.NetworkObjectId,
                "Object ID should restart after ClearAll.");
        }

        [Test]
        [Description("ClearAll on empty registry is a safe no-op.")]
        public void ClearAll_EmptyRegistry_IsNoOp()
        {
            Assert.DoesNotThrow(() => _spawnManager.ClearAll());
            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── NetworkManager Integration ─────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Description("NetworkManager.Spawner property exposes the SpawnManager.")]
        public void NetworkManager_SpawnerProperty_IsAccessible()
        {
            // The _manager created in SetUp has InitialiseNetwork called in Awake.
            // Spawner should be available.
            Assert.IsNotNull(_manager.Spawner,
                "NetworkManager.Spawner should be non-null after Awake.");
        }

        [Test]
        [Description("NetworkManager.Spawner.Registry is accessible.")]
        public void NetworkManager_SpawnerRegistry_IsAccessible()
        {
            Assert.IsNotNull(_manager.Spawner?.Registry);
        }

        [Test]
        [Description("NetworkManager.Spawner.Ownership is accessible.")]
        public void NetworkManager_SpawnerOwnership_IsAccessible()
        {
            Assert.IsNotNull(_manager.Spawner?.Ownership);
        }

        // ══════════════════════════════════════════════════════════════════════
        // ── Test doubles ───────────────────────────────────────────────────────
        // ══════════════════════════════════════════════════════════════════════

        private sealed class SpawnableNB : NetworkBehaviour
        {
            public bool SpawnCalled   { get; private set; }
            public bool DespawnCalled { get; private set; }

            protected override void OnNetworkSpawn()   => SpawnCalled = true;
            protected override void OnNetworkDespawn() => DespawnCalled = true;
        }
    }
}
