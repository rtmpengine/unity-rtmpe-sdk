// RTMPE SDK — Tests/Runtime/NetworkObjectRegistryTests.cs
//
// NUnit Edit-Mode tests for NetworkObjectRegistry.
//
// Internal members (Initialize, SetSpawned) are accessible via InternalsVisibleTo.
// Uses concrete ConcreteNetworkBehaviour (MonoBehaviour) created via AddComponent.
// All GameObjects created per-test are destroyed in TearDown.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("NetworkObjectRegistry")]
    public class NetworkObjectRegistryTests
    {
        private NetworkObjectRegistry _registry;
        private readonly List<GameObject> _created = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _registry = new NetworkObjectRegistry();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var go in _created)
                if (go != null) Object.DestroyImmediate(go);
            _created.Clear();
        }

        // ── Helper: create a ConcreteNetworkBehaviour with a given ID ──────────

        private ConcreteNB MakeObject(ulong objectId, string ownerId = "p1")
        {
            var go = new GameObject($"obj-{objectId}");
            _created.Add(go);
            var nb = go.AddComponent<ConcreteNB>();
            nb.Initialize(objectId, ownerId);
            return nb;
        }

        // ── Register / Get ─────────────────────────────────────────────────────

        [Test]
        [Description("Get returns the registered object.")]
        public void Get_AfterRegister_ReturnsObject()
        {
            var nb = MakeObject(10UL);
            _registry.Register(nb);

            Assert.AreSame(nb, _registry.Get(10UL));
        }

        [Test]
        [Description("Get returns null for unknown object ID.")]
        public void Get_UnknownId_ReturnsNull()
        {
            Assert.IsNull(_registry.Get(999UL));
        }

        [Test]
        [Description("Register overwrites a previous entry with the same ID.")]
        public void Register_SameId_Overwrites()
        {
            var nb1 = MakeObject(5UL, "p1");
            var nb2 = MakeObject(5UL, "p2");
            _registry.Register(nb1);
            _registry.Register(nb2);

            Assert.AreSame(nb2, _registry.Get(5UL));
        }

        [Test]
        [Description("Register with null is a no-op and does not throw.")]
        public void Register_Null_IsNoOp()
        {
            Assert.DoesNotThrow(() => _registry.Register(null));
            Assert.IsNull(_registry.Get(0UL));
        }

        // ── Unregister ─────────────────────────────────────────────────────────

        [Test]
        [Description("Unregister removes the object; Get returns null afterwards.")]
        public void Unregister_RemovesObject()
        {
            var nb = MakeObject(20UL);
            _registry.Register(nb);
            _registry.Unregister(20UL);

            Assert.IsNull(_registry.Get(20UL));
        }

        [Test]
        [Description("Unregistering an ID that was never registered is a no-op.")]
        public void Unregister_UnknownId_IsNoOp()
        {
            Assert.DoesNotThrow(() => _registry.Unregister(999UL));
        }

        // ── GetAll ─────────────────────────────────────────────────────────────

        [Test]
        [Description("GetAll returns all registered objects.")]
        public void GetAll_ReturnsAllRegistered()
        {
            var nb1 = MakeObject(1UL);
            var nb2 = MakeObject(2UL);
            _registry.Register(nb1);
            _registry.Register(nb2);

            var all = _registry.GetAll();
            Assert.AreEqual(2, all.Count);
        }

        [Test]
        [Description("GetAll returns empty list when registry is empty.")]
        public void GetAll_Empty_ReturnsEmptyList()
        {
            var all = _registry.GetAll();
            Assert.IsNotNull(all);
            Assert.AreEqual(0, all.Count);
        }

        [Test]
        [Description("GetAll returns a snapshot; subsequent modifications don't affect it.")]
        public void GetAll_IsSnapshot_NotLiveView()
        {
            var nb1 = MakeObject(1UL);
            _registry.Register(nb1);

            var snap = _registry.GetAll();
            var nb2  = MakeObject(2UL);
            _registry.Register(nb2);

            // Snapshot taken before second Register — should still have count 1.
            Assert.AreEqual(1, snap.Count);
        }

        // ── Clear ──────────────────────────────────────────────────────────────

        [Test]
        [Description("Clear empties the registry.")]
        public void Clear_EmptiesRegistry()
        {
            _registry.Register(MakeObject(1UL));
            _registry.Register(MakeObject(2UL));
            _registry.Clear();

            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        [Test]
        [Description("Clear calls OnNetworkDespawn on each spawned object.")]
        public void Clear_CallsDespawnOnSpawnedObjects()
        {
            var nb = MakeObject(1UL);
            nb.SetSpawned(true);
            _registry.Register(nb);

            _registry.Clear();

            Assert.IsTrue(nb.DespawnedCalled,  "OnNetworkDespawn should have been called.");
            Assert.IsFalse(nb.IsSpawned,       "IsSpawned should be false after despawn.");
        }

        [Test]
        [Description("Clear does not throw if an object is already despawned.")]
        public void Clear_AlreadyDespawnedObject_DoesNotThrow()
        {
            var nb = MakeObject(1UL);
            nb.SetSpawned(false);   // already false (never spawned)
            _registry.Register(nb);

            Assert.DoesNotThrow(() => _registry.Clear());
        }

        [Test]
        [Description("Clear on empty registry is a no-op.")]
        public void Clear_EmptyRegistry_IsNoOp()
        {
            Assert.DoesNotThrow(() => _registry.Clear());
            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        // ── Stale (destroyed) object auto-eviction ──────────────────────────────

        [Test]
        [Description("Get auto-evicts a destroyed GameObject and returns null.")]
        public void Get_DestroyedObject_ReturnsNullAndEvicts()
        {
            var nb = MakeObject(30UL);
            _registry.Register(nb);

            // Simulate external Destroy — remove from our own cleanup list so
            // TearDown doesn't double-destroy.
            _created.Remove(nb.gameObject);
            Object.DestroyImmediate(nb.gameObject);

            // Get should detect the Unity null and evict.
            Assert.IsNull(_registry.Get(30UL));
            // Verify the entry was removed (second Get also returns null).
            Assert.IsNull(_registry.Get(30UL));
        }

        // ── Register ID-collision despawn ────────────────────────────────────────

        [Test]
        [Description("Register with a duplicate ID despawns the previously registered object.")]
        public void Register_SameId_DespawnsPreviousSpawnedObject()
        {
            var nb1 = MakeObject(50UL);
            nb1.SetSpawned(true);
            _registry.Register(nb1);

            var nb2 = MakeObject(50UL);   // same network ID — will evict nb1
            _registry.Register(nb2);

            Assert.IsTrue(nb1.DespawnedCalled, "Previous object must be despawned on eviction.");
            Assert.IsFalse(nb1.IsSpawned,      "Previous object IsSpawned must be false.");
            Assert.AreSame(nb2, _registry.Get(50UL), "New object must be registered.");
        }

        [Test]
        [Description("Re-registering the SAME instance is idempotent and does not despawn it.")]
        public void Register_SameInstance_IsIdempotent()
        {
            var nb = MakeObject(51UL);
            nb.SetSpawned(true);
            _registry.Register(nb);
            _registry.Register(nb);   // same instance — no despawn

            Assert.IsFalse(nb.DespawnedCalled, "Re-registering the same reference must not despawn it.");
            Assert.IsTrue(nb.IsSpawned);
        }

        // ── Clear exception isolation ───────────────────────────────────────────

        [Test]
        [Description("Clear continues despawning remaining objects when one callback throws.")]
        public void Clear_ExceptionInCallback_ContinuesRemainingDespawns()
        {
            var throwing = new GameObject("throwing");
            _created.Add(throwing);
            var nbThrow = throwing.AddComponent<ThrowingNB>();
            nbThrow.Initialize(60UL, "p1");
            nbThrow.SetSpawned(true);
            _registry.Register(nbThrow);

            var nb2 = MakeObject(61UL);
            nb2.SetSpawned(true);
            _registry.Register(nb2);

            // Clear must not propagate the exception from nbThrow.
            // ThrowingNB.OnNetworkDespawn() throws, which Clear() catches and re-logs via
            // Debug.LogException. Declare it expected so Unity Test Runner does not fail.
            LogAssert.Expect(LogType.Exception, new System.Text.RegularExpressions.Regex("Simulated despawn failure"));
            Assert.DoesNotThrow(() => _registry.Clear());

            // nb2 must still have been despawned despite nbThrow throwing.
            Assert.IsTrue(nb2.DespawnedCalled,
                "Object after the throwing one must still be despawned.");
            Assert.AreEqual(0, _registry.GetAll().Count);
        }

        // ── GetAll null filter ───────────────────────────────────────────────────

        [Test]
        [Description("GetAll excludes destroyed GameObjects from the snapshot.")]
        public void GetAll_ExcludesDestroyedObjects()
        {
            var nb1 = MakeObject(70UL);
            var nb2 = MakeObject(71UL);
            _registry.Register(nb1);
            _registry.Register(nb2);

            // Destroy nb1 externally without unregistering.
            _created.Remove(nb1.gameObject);
            Object.DestroyImmediate(nb1.gameObject);

            var all = _registry.GetAll();
            Assert.AreEqual(1, all.Count, "GetAll must exclude destroyed objects.");
            Assert.AreSame(nb2, all[0]);
        }

        // ── Test doubles ───────────────────────────────────────────────────────

        private sealed class ThrowingNB : NetworkBehaviour
        {
            protected override void OnNetworkDespawn()
            {
                throw new System.InvalidOperationException("Simulated despawn failure.");
            }
        }

        private sealed class ConcreteNB : NetworkBehaviour
        {
            public bool DespawnedCalled { get; private set; }

            protected override void OnNetworkDespawn()
            {
                DespawnedCalled = true;
            }
        }
    }
}
