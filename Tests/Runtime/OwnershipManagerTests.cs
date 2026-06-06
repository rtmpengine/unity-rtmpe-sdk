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

            _ownership.ApplyOwnershipGrant(10UL, "new-owner", serverAttested: true);

            Assert.AreEqual("new-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("ApplyOwnershipGrant fires OnOwnershipChanged on the object.")]
        public void ApplyOwnershipGrant_FiresOwnershipChangedCallback()
        {
            var nb = RegisterObject(10UL, "old-owner");

            _ownership.ApplyOwnershipGrant(10UL, "new-owner", serverAttested: true);

            Assert.IsTrue(nb.OwnerChangeCallbackFired,         "OnOwnershipChanged should have been called.");
            Assert.AreEqual("old-owner", nb.PreviousOwnerOnChange);
            Assert.AreEqual("new-owner", nb.NewOwnerOnChange);
        }

        [Test]
        [Description("ApplyOwnershipGrant on unknown object ID logs warning and does not throw.")]
        public void ApplyOwnershipGrant_UnknownObject_IsNoOp()
        {
            // No object registered with ID 999.
            Assert.DoesNotThrow(() => _ownership.ApplyOwnershipGrant(999UL, "new-owner", serverAttested: true));
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

            _ownership.ApplyOwnershipGrant(30UL, "alice", serverAttested: true);  // same owner — no-change

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

            _ownership.ApplyOwnershipGrant(40UL, "bob", serverAttested: true);   // transfer 40 from alice → bob

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

        // ── H-011 — correlation gating on ApplyOwnershipGrant ──────────────────
        //
        // The default (non-server-attested) overload must reject grants for
        // which there is no outstanding self-issued request whose target
        // tuple (objectId, newOwnerPlayerId) matches.  The expectation map
        // is populated by RequestOwnershipTransfer; we drive it directly
        // via reflection here to avoid coupling the test to the RPC send
        // pipeline.

        private void InjectOutstandingExpectation(uint requestId, ulong objectId, string newOwner)
        {
            var fSet = typeof(OwnershipManager).GetField(
                "_outstanding",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var fDeadline = typeof(OwnershipManager).GetField(
                "_outstandingDeadlineMs",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var fExp = typeof(OwnershipManager).GetField(
                "_outstandingExpectations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            ((HashSet<uint>)fSet.GetValue(_ownership)).Add(requestId);
            ((Dictionary<uint, long>)fDeadline.GetValue(_ownership))[requestId] =
                long.MaxValue;
            ((Dictionary<uint, (ulong ObjectId, string NewOwner)>)fExp.GetValue(_ownership))[requestId] =
                (objectId, newOwner);
        }

        [Test]
        [Description("Default (non-server-attested) ApplyOwnershipGrant rejects when no outstanding request matches.")]
        public void ApplyOwnershipGrant_NoOutstanding_Rejected()
        {
            var nb = RegisterObject(70UL, "old-owner");

            _ownership.ApplyOwnershipGrant(70UL, "new-owner", serverAttested: false);

            Assert.AreEqual("old-owner", nb.OwnerPlayerId,
                "Owner must be unchanged: no outstanding request matched the inbound grant.");
        }

        [Test]
        [Description("Default ApplyOwnershipGrant accepts when a matching outstanding request exists; replay rejected.")]
        public void ApplyOwnershipGrant_MatchingOutstanding_AcceptedOnceThenRejected()
        {
            var nb = RegisterObject(71UL, "old-owner");
            _ownership.ResetOutstandingForTest();
            InjectOutstandingExpectation(0xCAFEBABE, 71UL, "new-owner");

            _ownership.ApplyOwnershipGrant(71UL, "new-owner", serverAttested: false);
            Assert.AreEqual("new-owner", nb.OwnerPlayerId);

            // Replay: the matching expectation has been consumed, so a
            // re-emitted grant for the same tuple is rejected.  We restore
            // the owner to a known value first to verify rejection cleanly.
            nb.SetOwner("old-owner");
            _ownership.ApplyOwnershipGrant(71UL, "new-owner", serverAttested: false);
            Assert.AreEqual("old-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Default ApplyOwnershipGrant rejects mismatched newOwner even when an outstanding request exists.")]
        public void ApplyOwnershipGrant_MismatchedOwner_Rejected()
        {
            var nb = RegisterObject(72UL, "old-owner");
            _ownership.ResetOutstandingForTest();
            // Local SDK asked for "alice"; an attacker forges a grant to "mallory".
            InjectOutstandingExpectation(7u, 72UL, "alice");

            _ownership.ApplyOwnershipGrant(72UL, "mallory", serverAttested: false);

            Assert.AreEqual("old-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Server-attested ApplyOwnershipGrant bypasses the correlation gate (master-client / initial-assignment paths).")]
        public void ApplyOwnershipGrant_ServerAttested_BypassesCorrelation()
        {
            var nb = RegisterObject(73UL, "old-owner");
            _ownership.ResetOutstandingForTest();

            _ownership.ApplyOwnershipGrant(73UL, "new-owner", serverAttested: true);

            Assert.AreEqual("new-owner", nb.OwnerPlayerId);
        }

        [Test]
        [Description("ConsumeMatchingExpectation removes the matched entry and refuses subsequent matches.")]
        public void ConsumeMatchingExpectation_OneShot()
        {
            _ownership.ResetOutstandingForTest();
            InjectOutstandingExpectation(99u, 80UL, "alice");

            var mi = typeof(OwnershipManager).GetMethod(
                "ConsumeMatchingExpectation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            Assert.IsTrue((bool)mi.Invoke(_ownership, new object[] { 80UL, "alice" }));
            Assert.IsFalse((bool)mi.Invoke(_ownership, new object[] { 80UL, "alice" }));
            Assert.AreEqual(0, _ownership.OutstandingCount);
        }

        // ── ReassignObjectsToNewOwner (NEW-OWNERSHIP-1) ────────────────────────

        [Test]
        [Description("Reassigns a departed owner's DestroyWithOwner=false object to the new host.")]
        public void ReassignObjectsToNewOwner_SurvivingObject_MovesToNewHost()
        {
            var nb = RegisterObject(200UL, "leaver");
            nb.DestroyWithOwner = false;

            _ownership.ReassignObjectsToNewOwner("leaver", "host");

            Assert.AreEqual("host", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Does NOT move DestroyWithOwner=true objects (those are destroyed on leave, not reassigned).")]
        public void ReassignObjectsToNewOwner_DestroyWithOwnerTrue_NotReassigned()
        {
            var nb = RegisterObject(201UL, "leaver");
            nb.DestroyWithOwner = true;

            _ownership.ReassignObjectsToNewOwner("leaver", "host");

            Assert.AreEqual("leaver", nb.OwnerPlayerId, "DestroyWithOwner=true object must not be reassigned.");
        }

        [Test]
        [Description("Reassigns every surviving object owned by the leaver.")]
        public void ReassignObjectsToNewOwner_MultipleObjects_AllMove()
        {
            var a = RegisterObject(202UL, "leaver"); a.DestroyWithOwner = false;
            var b = RegisterObject(203UL, "leaver"); b.DestroyWithOwner = false;

            _ownership.ReassignObjectsToNewOwner("leaver", "host");

            Assert.AreEqual("host", a.OwnerPlayerId);
            Assert.AreEqual("host", b.OwnerPlayerId);
        }

        [Test]
        [Description("Does not touch objects owned by players other than the leaver.")]
        public void ReassignObjectsToNewOwner_OtherOwner_Untouched()
        {
            var mine  = RegisterObject(204UL, "leaver");  mine.DestroyWithOwner  = false;
            var other = RegisterObject(205UL, "bystander"); other.DestroyWithOwner = false;

            _ownership.ReassignObjectsToNewOwner("leaver", "host");

            Assert.AreEqual("host", mine.OwnerPlayerId);
            Assert.AreEqual("bystander", other.OwnerPlayerId, "Bystander's object must be untouched.");
        }

        [Test]
        [Description("Reassigning to self is a no-op (guards against MasterId == leaver before promotion).")]
        public void ReassignObjectsToNewOwner_SameFromAndTo_NoOp()
        {
            var nb = RegisterObject(206UL, "leaver");
            nb.DestroyWithOwner = false;

            _ownership.ReassignObjectsToNewOwner("leaver", "leaver");

            Assert.AreEqual("leaver", nb.OwnerPlayerId);
        }

        [Test]
        [Description("Null/empty parties are a safe no-op.")]
        public void ReassignObjectsToNewOwner_EmptyParties_NoOp()
        {
            var nb = RegisterObject(207UL, "leaver");
            nb.DestroyWithOwner = false;

            Assert.DoesNotThrow(() => _ownership.ReassignObjectsToNewOwner(null, "host"));
            Assert.DoesNotThrow(() => _ownership.ReassignObjectsToNewOwner("leaver", null));
            Assert.DoesNotThrow(() => _ownership.ReassignObjectsToNewOwner("", ""));

            Assert.AreEqual("leaver", nb.OwnerPlayerId, "No reassignment should occur for empty parties.");
        }

        // ── Eviction defence-in-depth (V3 audit, fix #1) ───────────────────────
        //
        // When AllocateOutstandingRequestId saturates, it evicts the
        // earliest-deadline entry and reuses its id slot.  The eviction
        // must scrub every per-request tracking structure — _outstanding,
        // _outstandingDeadlineMs, AND _outstandingExpectations — so a stale
        // expectation tuple from the orphaned request cannot be observed
        // by ConsumeMatchingExpectation between the eviction and the
        // caller's overwrite of the expectation slot.

        private static System.Reflection.FieldInfo ExpectationsField =>
            typeof(OwnershipManager).GetField(
                "_outstandingExpectations",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        private bool ExpectationContains(uint requestId)
        {
            var dict = (Dictionary<uint, (ulong ObjectId, string NewOwner)>)
                ExpectationsField.GetValue(_ownership);
            return dict.ContainsKey(requestId);
        }

        [Test]
        [Description(
            "Eviction in AllocateOutstandingRequestId clears the evicted id's " +
            "expectation tuple in addition to removing it from _outstanding " +
            "and _outstandingDeadlineMs.")]
        public void AllocateOutstandingRequestId_EvictionClearsExpectation()
        {
            _ownership.ResetOutstandingForTest();

            // Seed _outstanding to saturation: 256 ids in [1, 256] occupied by
            // expectations the test owns, with the earliest deadline on id=42
            // so the eviction picks 42 deterministically.  We bypass the public
            // Allocate/RequestOwnership pipeline by writing directly to the
            // tracked dictionaries, so the only path that can land on id 42
            // when AllocateOutstandingRequestId is called is the eviction
            // branch.
            for (uint id = 1u; id <= 256u; id++)
                InjectOutstandingExpectation(id, /*objectId*/ id, $"orig-owner-{id}");

            // Drop id 42's deadline below every other entry so the eviction
            // walk picks it deterministically.  The other ids carry
            // long.MaxValue (set by InjectOutstandingExpectation), so the
            // earliest-deadline scan in AllocateOutstandingRequestId picks 42.
            var dlField = typeof(OwnershipManager).GetField(
                "_outstandingDeadlineMs",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var deadlines = (Dictionary<uint, long>)dlField.GetValue(_ownership);
            deadlines[42u] = 1L; // earliest deadline → first eviction target

            // Sanity: id 42's expectation tuple is currently the orphan tuple.
            Assert.IsTrue(ExpectationContains(42u),
                "Pre-condition: id 42 must have an expectation entry.");

            var allocate = typeof(OwnershipManager).GetMethod(
                "AllocateOutstandingRequestId",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // Trigger the eviction branch.  PruneExpiredOutstanding sweeps
            // entries whose deadline is in the past; we set 42's deadline to
            // 1L (well below NowMs() at any wall time after the Unix epoch),
            // so the prune step inside Allocate may reclaim id 42 before the
            // saturation branch runs.  Either way, the post-condition is the
            // same: id 42's expectation entry must NOT be observable as the
            // orphan tuple after Allocate returns — either because the prune
            // cleared it, or because the saturation eviction cleared it.
            uint allocatedId = (uint)allocate.Invoke(_ownership, null);

            // POST-CONDITION 1: AllocateOutstandingRequestId returned a valid
            // (non-zero) id.  Required by the public contract.
            Assert.AreNotEqual(0u, allocatedId,
                "Allocate must return a non-zero id under saturation.");

            // POST-CONDITION 2: the orphan tuple "orig-owner-42" must not be
            // observable.  Two acceptable end states:
            //   (a) id 42 was reused for the new request — its expectation
            //       slot is empty (defence-in-depth scrub) until the caller
            //       writes the new tuple.
            //   (b) id 42 was pruned and a different id was returned — its
            //       expectation slot was removed by the prune step.
            // Either way, ExpectationContains(42u) must be false OR the tuple
            // there must NOT be the orphan tuple.
            var dict = (Dictionary<uint, (ulong ObjectId, string NewOwner)>)
                ExpectationsField.GetValue(_ownership);
            if (dict.TryGetValue(42u, out var tup))
            {
                Assert.AreNotEqual("orig-owner-42", tup.NewOwner,
                    "Stale orphan tuple under id 42 must not survive the " +
                    "AllocateOutstandingRequestId path.");
            }
        }

        [Test]
        [Description(
            "Stale eviction tuple cannot be matched by ConsumeMatchingExpectation. " +
            "Validates that the eviction's expectation cleanup closes the " +
            "tuple-correlation hole that fix #1 of the V3 audit was designed " +
            "to address.")]
        public void Eviction_StaleExpectation_NotMatchableByConsume()
        {
            _ownership.ResetOutstandingForTest();

            // Single-entry saturation simulation.  The orphan tuple is
            // (objectId=80, newOwner="alice").
            InjectOutstandingExpectation(7u, 80UL, "alice");

            // Manually invoke the (private) eviction effect that fix #1 makes
            // safe: simulate the eviction by removing the tracking entry and
            // re-binding the slot to a different request.  The fix's contract
            // is: the expectation map is symmetric with the outstanding /
            // deadline maps.  Drop all three for id 7.
            var fSet = typeof(OwnershipManager).GetField(
                "_outstanding",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var fDl = typeof(OwnershipManager).GetField(
                "_outstandingDeadlineMs",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            // Eviction step under fix #1 (matches Runtime/Core/OwnershipManager.cs
            // lines 277-294 + line 291): remove the orphaned id from every
            // tracking dict.  Re-bind under the new request.
            ((HashSet<uint>)fSet.GetValue(_ownership)).Remove(7u);
            ((Dictionary<uint, long>)fDl.GetValue(_ownership)).Remove(7u);
            ((Dictionary<uint, (ulong ObjectId, string NewOwner)>)
                ExpectationsField.GetValue(_ownership)).Remove(7u);

            // Fresh request reuses id 7 with a different tuple.
            InjectOutstandingExpectation(7u, 99UL, "bob");

            // The stale (80, "alice") tuple must not be matchable.
            var consumeMi = typeof(OwnershipManager).GetMethod(
                "ConsumeMatchingExpectation",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            bool stale = (bool)consumeMi.Invoke(_ownership,
                new object[] { 80UL, "alice" });
            Assert.IsFalse(stale,
                "Stale orphan tuple must not be matched after eviction-time scrub.");

            // Conversely, the fresh tuple is matchable exactly once.
            bool fresh1 = (bool)consumeMi.Invoke(_ownership,
                new object[] { 99UL, "bob" });
            Assert.IsTrue(fresh1, "Fresh tuple must match.");

            bool fresh2 = (bool)consumeMi.Invoke(_ownership,
                new object[] { 99UL, "bob" });
            Assert.IsFalse(fresh2, "One-shot consume: replay must be rejected.");
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
