// RTMPE SDK — Tests/Runtime/FlatBufferLowSeveritySecurityTests.cs
//
// Adversarial coverage for the Sprint-5 LOW-severity FlatBuffers hardening:
//
//  L ByteBuffer.ConvertTsToBytes / ArraySize must use checked arithmetic
//        so a hostile element count cannot wrap to a small positive byte
//        count and alias the buffer-range guard.
//  L Verifier Options.DEFAULT_MAX_TABLES must default to the SDK's
//        16384 cap, not the upstream 1_000_000 admit-everything ceiling.
//  L TryGetRoot must reject a (verifier, root-factory) pair whose
//        declaring-type relationship does not match — defensive substitute
//        for the absent FlatBuffers file_identifier on RTMPE schemas.
//  L ByteBuffer.ConvertBytesToTs must throw when the byte length is not
//        a multiple of sizeof(T), even under BYTEBUFFER_NO_BOUNDS_CHECK.

using System;
using NUnit.Framework;
using Google.FlatBuffers;
using RTMPE.Infrastructure.Serialization;
using FbInputPayload = RTMPE.States.InputPayload;
using FbInputPayloadVerify = RTMPE.States.InputPayloadVerify;
using FbStateSync = RTMPE.States.StateSyncPayload;
using FbStateSyncVerify = RTMPE.States.StateSyncPayloadVerify;
using FbNVU = RTMPE.States.NetworkVariableUpdate;
using FbDSP = RTMPE.States.DeltaStateSyncPayload;
using FbDSPVerify = RTMPE.States.DeltaStateSyncPayloadVerify;
using FbSpawn = RTMPE.States.SpawnPayload;
using FbSpawnVerify = RTMPE.States.SpawnPayloadVerify;
using FbValueType = RTMPE.States.ValueType;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Security")]
    public class FlatBufferLowSeveritySecurityTests
    {
        // ──────────────────────────────────────────────────────────────────
        // — checked arithmetic on element-count × sizeof(T)
        // ──────────────────────────────────────────────────────────────────
        [Test]
        public void ConvertTsToBytes_OverflowingProduct_Throws()
        {
            // sizeof(double) = 8. A count of (Int32.MaxValue / 4) multiplied
            // by 8 wraps Int32 cleanly; the unchecked form returned a small
            // positive number that aliased the buffer range guard. The
            // hardened form must surface OverflowException so the receive
            // wrapper can drop the packet.
            int hostileCount = (int.MaxValue / 4);
            Assert.Throws<OverflowException>(
                () => ByteBuffer.ConvertTsToBytes<double>(hostileCount));
        }

        [Test]
        public void ArraySize_Overflow_Throws()
        {
            // ArraySize<long>(huge[]) would silently wrap pre-fix. We cannot
            // actually allocate Int32.MaxValue/2 longs in CI, so we exercise
            // the equivalent ConvertTsToBytes path which the same `checked`
            // block guards. This keeps the test memory-safe while still
            // proving the multiply is wrapped in `checked(...)`.
            Assert.Throws<OverflowException>(
                () => ByteBuffer.ConvertTsToBytes<long>(int.MaxValue / 2));
        }

        // ──────────────────────────────────────────────────────────────────
        // — verifier max_tables default
        // ──────────────────────────────────────────────────────────────────
        [Test]
        public void VerifierOptions_DefaultMaxTables_MatchesSdkCap()
        {
            // The default constructor wires DEFAULT_MAX_TABLES into the
            // mutable maxTables field. Sprint 1 sized the SDK cap at 16 384
            // (VerifiedFlatBuffer.MaxTablesPerBuffer); Sprint 5 brings the
            // upstream default into agreement so any caller that constructs
            // a Verifier with default Options gets the SDK-wide cap rather
            // than the upstream 1 000 000 admit-everything ceiling.
            var options = new Options();
            Assert.AreEqual(16384, options.maxTables);
            Assert.AreEqual(16384, Options.DEFAULT_MAX_TABLES);
        }

        // ──────────────────────────────────────────────────────────────────
        // — boundary discriminant rejects mismatched (verifier, TRoot)
        // ──────────────────────────────────────────────────────────────────
        [Test]
        public void MismatchedVerifierAndRoot_TryGetRoot_ReturnsFalse()
        {
            // Build a structurally valid InputPayload — its bytes parse
            // cleanly as that table. Pass it through TryGetRoot but supply
            // the StateSyncPayload verifier paired with the InputPayload
            // root factory. The structural verifier would FAIL anyway (the
            // schemas differ), but the boundary discriminant must reject
            // the mismatch BEFORE ever invoking the verifier so that even
            // payload shapes that happened to validate under both verifiers
            // (e.g. trivially small tables) cannot slip through.
            var b = new FlatBufferBuilder(64);
            FbInputPayload.StartInputPayload(b);
            FbInputPayload.AddTick(b, 1);
            var end = FbInputPayload.EndInputPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            // Verifier from StateSyncPayloadVerify, factory from InputPayload.
            // The declaring-type names disagree (StateSyncPayloadVerify vs
            // InputPayload) and the boundary check rejects the call.
            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                bytes,
                FbStateSyncVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out _,
                "L26-mismatch");

            Assert.IsFalse(ok);
        }

        [Test]
        public void MatchingVerifierAndRoot_TryGetRoot_ReturnsTrue()
        {
            // Sanity check: the boundary discriminant must NOT reject the
            // legitimate (verifier, root) pairing it was designed to admit.
            var b = new FlatBufferBuilder(64);
            FbInputPayload.StartInputPayload(b);
            FbInputPayload.AddTick(b, 7);
            FbInputPayload.AddMoveX(b, 0.0f);
            FbInputPayload.AddMoveY(b, 0.0f);
            FbInputPayload.AddFlags(b, 0);
            var end = FbInputPayload.EndInputPayload(b);
            b.Finish(end.Value);
            var bytes = b.SizedByteArray();

            var ok = VerifiedFlatBuffer.TryGetRoot<FbInputPayload>(
                bytes,
                FbInputPayloadVerify.Verify,
                FbInputPayload.GetRootAsInputPayload,
                out var root,
                "L26-match");

            Assert.IsTrue(ok);
            Assert.AreEqual(7u, root.Tick);
        }

        // ──────────────────────────────────────────────────────────────────
        // — non-multiple length must throw under all build flags
        // ──────────────────────────────────────────────────────────────────
        [Test]
        public void ConvertBytesToTs_NonMultipleLength_Throws()
        {
            // 7 bytes is not a multiple of sizeof(int)=4. The pre-fix code
            // gated this check on !BYTEBUFFER_NO_BOUNDS_CHECK so an SDK
            // built with the perf flag would silently truncate. The
            // hardened path makes the check unconditional.
            Assert.Throws<ArgumentException>(
                () => ByteBuffer.ConvertBytesToTs<int>(7));
        }

        [Test]
        public void ConvertBytesToTs_NegativeLength_Throws()
        {
            // A negative byte length must be rejected before the divide,
            // otherwise the resulting valueInTs would also be negative and
            // bypass downstream allocation sizing. The new explicit guard
            // surfaces ArgumentException uniformly across all build flags.
            Assert.Throws<ArgumentException>(
                () => ByteBuffer.ConvertBytesToTs<int>(-4));
        }

        [Test]
        public void ConvertBytesToTs_MultipleLength_Succeeds()
        {
            // The success path must remain unchanged: 16 bytes / sizeof(int)
            // returns 4 elements with no exception.
            Assert.AreEqual(4, ByteBuffer.ConvertBytesToTs<int>(16));
        }

        // ──────────────────────────────────────────────────────────────────
        // VAR-C — DeltaStateSyncPayload cumulative vector cap
        // ──────────────────────────────────────────────────────────────────

        [Test]
        public void DeltaStateSyncPayload_OversizedVariablesVector_Rejected()
        {
            // Build a DeltaStateSyncPayload with MaxTotalVectorElements + 1
            // minimal NetworkVariableUpdate entries.  The structural verifier
            // accepts this (4 097 < MaxTablesPerBuffer = 16 384); the semantic
            // validator rejects it via the cumulative cap.
            int overCount = SafeFlatBufferAccessors.MaxTotalVectorElements + 1;
            var b = new FlatBufferBuilder(overCount * 52 + 512);

            var updates = new Offset<FbNVU>[overCount];
            for (int i = 0; i < overCount; i++)
            {
                var dataVec = FbNVU.CreateDataVector(b, new byte[] { 0 });
                FbNVU.StartNetworkVariableUpdate(b);
                FbNVU.AddValueType(b, FbValueType.Bool);
                FbNVU.AddData(b, dataVec);
                updates[i] = FbNVU.EndNetworkVariableUpdate(b);
            }

            var varsVec = FbDSP.CreateChangedVariablesVector(b, updates);
            var roomStr = b.CreateString("r");
            FbDSP.StartDeltaStateSyncPayload(b);
            FbDSP.AddRoomId(b, roomStr);
            FbDSP.AddChangedVariables(b, varsVec);
            var root = FbDSP.EndDeltaStateSyncPayload(b);
            b.Finish(root.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbDSP>(
                bytes,
                FbDSPVerify.Verify,
                FbDSP.GetRootAsDeltaStateSyncPayload,
                out _,
                "varc-delta-oversize");

            Assert.IsFalse(ok,
                "DeltaStateSyncPayload with > MaxTotalVectorElements changed_variables must be rejected.");
        }

        [Test]
        public void DeltaStateSyncPayload_AtCap_Accepted()
        {
            // Exactly MaxTotalVectorElements entries — must be accepted.
            int atCap = SafeFlatBufferAccessors.MaxTotalVectorElements;
            var b = new FlatBufferBuilder(atCap * 52 + 512);

            var updates = new Offset<FbNVU>[atCap];
            for (int i = 0; i < atCap; i++)
            {
                var dataVec = FbNVU.CreateDataVector(b, new byte[] { 0 });
                FbNVU.StartNetworkVariableUpdate(b);
                FbNVU.AddValueType(b, FbValueType.Bool);
                FbNVU.AddData(b, dataVec);
                updates[i] = FbNVU.EndNetworkVariableUpdate(b);
            }

            var varsVec = FbDSP.CreateChangedVariablesVector(b, updates);
            var roomStr = b.CreateString("r");
            FbDSP.StartDeltaStateSyncPayload(b);
            FbDSP.AddRoomId(b, roomStr);
            FbDSP.AddChangedVariables(b, varsVec);
            var root = FbDSP.EndDeltaStateSyncPayload(b);
            b.Finish(root.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbDSP>(
                bytes,
                FbDSPVerify.Verify,
                FbDSP.GetRootAsDeltaStateSyncPayload,
                out _,
                "varc-delta-at-cap");

            Assert.IsTrue(ok,
                "DeltaStateSyncPayload at exactly MaxTotalVectorElements must be accepted.");
        }

        // ──────────────────────────────────────────────────────────────────
        // VAR-C — SpawnPayload OwnerId length cap
        // ──────────────────────────────────────────────────────────────────

        [Test]
        public void SpawnPayload_OversizedOwnerId_Rejected()
        {
            // 129 ASCII characters = 129 UTF-8 bytes, exceeding MaxOwnerIdBytes (128).
            var b       = new FlatBufferBuilder(512);
            var longId  = b.CreateString(new string('A', VerifiedFlatBuffer.MaxOwnerIdBytes + 1));
            FbSpawn.StartSpawnPayload(b);
            FbSpawn.AddOwnerId(b, longId);
            var root = FbSpawn.EndSpawnPayload(b);
            b.Finish(root.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbSpawn>(
                bytes,
                FbSpawnVerify.Verify,
                FbSpawn.GetRootAsSpawnPayload,
                out _,
                "varc-spawn-oversize");

            Assert.IsFalse(ok, "SpawnPayload with OwnerId > MaxOwnerIdBytes must be rejected.");
        }

        [Test]
        public void SpawnPayload_OwnerIdAtCap_Accepted()
        {
            // Exactly MaxOwnerIdBytes ASCII characters — must be accepted.
            var b      = new FlatBufferBuilder(512);
            var exactId = b.CreateString(new string('A', VerifiedFlatBuffer.MaxOwnerIdBytes));
            FbSpawn.StartSpawnPayload(b);
            FbSpawn.AddOwnerId(b, exactId);
            var root = FbSpawn.EndSpawnPayload(b);
            b.Finish(root.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbSpawn>(
                bytes,
                FbSpawnVerify.Verify,
                FbSpawn.GetRootAsSpawnPayload,
                out _,
                "varc-spawn-at-cap");

            Assert.IsTrue(ok, "SpawnPayload with OwnerId exactly at MaxOwnerIdBytes must be accepted.");
        }

        [Test]
        public void SpawnPayload_OversizedInitialState_Rejected()
        {
            // InitialState vector with MaxTotalVectorElements + 1 bytes.
            int overCount = SafeFlatBufferAccessors.MaxTotalVectorElements + 1;
            var b        = new FlatBufferBuilder(overCount + 256);
            var stateVec = FbSpawn.CreateInitialStateVector(b, new byte[overCount]);
            FbSpawn.StartSpawnPayload(b);
            FbSpawn.AddInitialState(b, stateVec);
            var root = FbSpawn.EndSpawnPayload(b);
            b.Finish(root.Value);
            var bytes = b.SizedByteArray();

            bool ok = VerifiedFlatBuffer.TryGetRoot<FbSpawn>(
                bytes,
                FbSpawnVerify.Verify,
                FbSpawn.GetRootAsSpawnPayload,
                out _,
                "varc-spawn-state-oversize");

            Assert.IsFalse(ok, "SpawnPayload with InitialState > MaxTotalVectorElements must be rejected.");
        }
    }
}
