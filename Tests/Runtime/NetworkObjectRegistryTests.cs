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

        // ── Re-entrancy / snapshot overload ────────────────────────────────────

        [Test]
        [Description("GetAllSnapshot fills a caller-owned list independent of the shared buffer.")]
        public void GetAllSnapshot_FillsDestination_AndIsIndependentOfSharedBuffer()
        {
            var a = MakeObject(101UL);
            var b = MakeObject(102UL);
            _registry.Register(a);
            _registry.Register(b);

            var dst = new List<NetworkBehaviour>();
            _registry.GetAllSnapshot(dst);
            Assert.AreEqual(2, dst.Count);
            CollectionAssert.Contains(dst, a);
            CollectionAssert.Contains(dst, b);

            // The destination is completely independent of the registry's
            // internal storage: a subsequent GetAll cannot affect dst.
            var shared = _registry.GetAll();
            Assert.AreEqual(2, shared.Count);
            Assert.AreNotSame(shared, dst,
                "Snapshot must allocate or use the caller-owned list, never the shared buffer");
        }

        [Test]
        [Description("GetAllSnapshot clears the destination list before refilling.")]
        public void GetAllSnapshot_ClearsDestinationBeforePopulating()
        {
            var a = MakeObject(301UL);
            _registry.Register(a);

            var dst = new List<NetworkBehaviour> { null, null, null };
            _registry.GetAllSnapshot(dst);
            Assert.AreEqual(1, dst.Count, "Stale entries must be cleared before fill");
            Assert.AreSame(a, dst[0]);
        }

        [Test]
        [Description("GetAllSnapshot rejects a null destination with ArgumentNullException.")]
        public void GetAllSnapshot_NullDestination_Throws()
        {
            Assert.Throws<System.ArgumentNullException>(() => _registry.GetAllSnapshot(null));
        }

        [Test]
        [Description("Two top-level GetAll calls reuse the shared buffer (zero-allocation contract).")]
        public void GetAll_TopLevelCalls_ShareTheSameBufferReference()
        {
            var a = MakeObject(401UL);
            _registry.Register(a);

            var first  = _registry.GetAll();
            var second = _registry.GetAll();

            // The hot path must keep returning the same shared list reference
            // so callers do not pay an allocation per frame.
            Assert.AreSame(first, second,
                "GetAll's zero-allocation contract returns the shared buffer for both top-level calls");
        }

        // ── Re-entrance guard (V3 audit, fix #2) ──────────────────────────────
        //
        // Register releases its lock before invoking SetSpawned(false) on the
        // evicted entry so an OnNetworkDespawn handler that calls back into
        // the registry does not deadlock.  But that release also exposes a
        // window where re-entrant Register on the same id would clobber the
        // outer call's freshly-installed slot, then despawn it inside the
        // inner call's eviction step — silently corrupting state.
        //
        // The fix gates re-entrant Register calls issued from within the
        // despawn callback dispatch with a [ThreadStatic] depth counter and
        // refuses them, surfacing a clear error log so the offending call
        // site is visible.

        // Stub that calls back into the registry from OnNetworkDespawn.
        private sealed class ReentrantRegistryNB : NetworkBehaviour
        {
            public NetworkObjectRegistry Target;
            public NetworkBehaviour      Replacement;
            public bool                  ReentrantRegisterAttempted;

            protected override void OnNetworkDespawn()
            {
                if (Target == null || Replacement == null) return;
                ReentrantRegisterAttempted = true;
                // This is the corruption pattern fix #2 prevents: a despawn
                // handler that registers a NEW object under the SAME id
                // would clobber the outer Register's slot and then have its
                // own SetSpawned(false) tear down the new registration.
                Target.Register(Replacement);
            }
        }

        [Test]
        [Description(
            "Register from inside a same-id collision's OnNetworkDespawn " +
            "callback is rejected (re-entrance guard).  The outer call's " +
            "registration must remain intact and observable.")]
        public void Register_ReentrantFromDespawn_IsRejected()
        {
            // First object: vanilla, will become the eviction target.
            var go0 = new GameObject("evict-me");
            _created.Add(go0);
            var first = go0.AddComponent<ReentrantRegistryNB>();
            first.Initialize(100UL, "p1");
            first.SetSpawned(true);                  // make IsSpawned true so SetSpawned(false) fires OnNetworkDespawn
            _registry.Register(first);

            // The would-be replacement that the despawn callback will try to
            // register under the SAME id.  Without the guard, this would
            // clobber the outer Register's slot and then be despawned by the
            // outer's SetSpawned(false) call.
            var goRepl = new GameObject("replacement");
            _created.Add(goRepl);
            var replacement = goRepl.AddComponent<ConcreteNB>();
            replacement.Initialize(100UL, "p1");

            // Wire the despawn re-entrance.
            first.Target      = _registry;
            first.Replacement = replacement;

            // Trigger the eviction path: register a SECOND object under the
            // same id.  The eviction's SetSpawned(false) on `first` fires
            // first.OnNetworkDespawn, which tries the re-entrant Register.
            //
            // Expected logs:
            //   1. ERROR — same-id collision (eviction)
            //   2. ERROR — re-entrant Register from inside OnNetworkDespawn
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                "same-id collision"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex(
                "re-entrant call"));

            var go1 = new GameObject("outer-replacement");
            _created.Add(go1);
            var outer = go1.AddComponent<ConcreteNB>();
            outer.Initialize(100UL, "p1");
            _registry.Register(outer);

            // Re-entrant attempt was made.
            Assert.IsTrue(first.ReentrantRegisterAttempted,
                "Pre-condition: the despawn callback should have run and " +
                "attempted the re-entrant Register.");

            // Post-condition: the outer call's registration is the live
            // registry slot — neither the inner re-entrant attempt nor any
            // collateral despawn has clobbered it.
            Assert.AreSame(outer, _registry.Get(100UL),
                "Outer Register's installed object must remain authoritative; " +
                "the re-entrance guard rejected the inner Register and so the " +
                "outer slot is intact.");
        }

        [Test]
        [Description(
            "Re-entrance guard's depth counter is decremented after the " +
            "despawn callback returns, so subsequent legitimate Register " +
            "calls on the same thread are not rejected.")]
        public void Register_AfterReentrantAttempt_NextCallStillSucceeds()
        {
            // Set up a despawn callback that attempts a re-entrant Register
            // under id 200.  After the eviction completes, registering id 201
            // (a different id, from outside any despawn dispatch) must
            // succeed normally — the depth counter is back at zero.
            var goEvict = new GameObject("evict-me");
            _created.Add(goEvict);
            var evict = goEvict.AddComponent<ReentrantRegistryNB>();
            evict.Initialize(200UL, "p1");
            evict.SetSpawned(true);
            _registry.Register(evict);

            var goRepl = new GameObject("inner-repl");
            _created.Add(goRepl);
            var innerRepl = goRepl.AddComponent<ConcreteNB>();
            innerRepl.Initialize(200UL, "p1");
            evict.Target      = _registry;
            evict.Replacement = innerRepl;

            // Allow the same-id collision and re-entrance error logs.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("same-id collision"));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("re-entrant call"));

            var goOuter = new GameObject("outer");
            _created.Add(goOuter);
            var outer = goOuter.AddComponent<ConcreteNB>();
            outer.Initialize(200UL, "p1");
            _registry.Register(outer);

            // Now that the despawn dispatch has unwound, register an
            // unrelated id from outside any despawn.  The guard must NOT
            // reject this call.
            var goFresh = new GameObject("fresh");
            _created.Add(goFresh);
            var fresh = goFresh.AddComponent<ConcreteNB>();
            fresh.Initialize(201UL, "p1");
            Assert.DoesNotThrow(() => _registry.Register(fresh));
            Assert.AreSame(fresh, _registry.Get(201UL),
                "Post-despawn registrations must succeed normally — the " +
                "re-entrance guard's depth counter must have been " +
                "decremented when the despawn dispatch returned.");
        }

        [Test]
        [Description(
            "An exception from inside the despawn callback must NOT pin the " +
            "depth counter; subsequent Register calls on the same thread " +
            "must still succeed.  Validates the try/finally pairing in " +
            "fix #2's instrumentation.")]
        public void Register_ExceptionInDespawn_DepthCounterRestored()
        {
            // ThrowingNB is the existing test double (declared below) that
            // throws from OnNetworkDespawn.  Trigger an eviction on it; the
            // exception will surface from the try/catch around SetSpawned —
            // wait, that try/catch is in Clear() not Register().  In
            // Register(), exceptions ARE allowed to propagate — but the
            // try/finally that brackets _despawnReentryDepth must still
            // restore the depth even when the inner SetSpawned throws.
            //
            // The contract being verified is the try/finally pairing, not
            // the swallowing of exceptions.

            var goThrow = new GameObject("throw");
            _created.Add(goThrow);
            var thr = goThrow.AddComponent<ThrowingNB>();
            thr.Initialize(300UL, "p1");
            thr.SetSpawned(true);
            _registry.Register(thr);

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("same-id collision"));

            var go2 = new GameObject("repl");
            _created.Add(go2);
            var repl = go2.AddComponent<ConcreteNB>();
            repl.Initialize(300UL, "p1");

            // The eviction's SetSpawned(false) call propagates the
            // InvalidOperationException out of Register.  We catch it here
            // because Register doesn't swallow it (only Clear does).
            Assert.Throws<System.InvalidOperationException>(
                () => _registry.Register(repl));

            // After the exception, the depth counter MUST be back at zero —
            // verified by the next Register call succeeding without rejection.
            var go3 = new GameObject("after");
            _created.Add(go3);
            var after = go3.AddComponent<ConcreteNB>();
            after.Initialize(301UL, "p1");
            Assert.DoesNotThrow(() => _registry.Register(after));
            Assert.AreSame(after, _registry.Get(301UL),
                "The post-exception registration must have succeeded; the " +
                "try/finally pairing in fix #2 ensures the depth counter is " +
                "restored even on an exception.");
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
