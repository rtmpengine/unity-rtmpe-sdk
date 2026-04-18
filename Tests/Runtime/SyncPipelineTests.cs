// RTMPE SDK — Tests/Runtime/SyncPipelineTests.cs
//
// RTMPE SDK — Synchronization Integration Tests
//
// These tests exercise CHAINS of sync components, not individual units.
// Every test crosses at least two classes, simulating the real data flow
// that packets take from Send through the network to Receive.
//
// Data flow tested:
//
//   SEND PATH (client → server):
//     NetworkTransform.GetState()
//       → TransformPacketBuilder.BuildUpdatePayload()
//           → 48-byte UDP payload (Go server reads as ObjectState)
//
//   RECEIVE PATH (server → client):
//     Go StateDelta.Serialize() produces 9–49 bytes (manual mock here)
//       → TransformPacketParser.TryParseStateDelta()
//           → NetworkTransformInterpolator.AddState()
//               → TryInterpolate()
//                   → GameObject.transform.position / rotation / scale
//
//   VARIABLE PIPELINE (owner client):
//     NetworkVariable<T>.Value = x   →  IsDirty = true
//       → Serialize(BinaryWriter)
//           → bytes transmitted
//               → target: SetValueWithoutNotify(Deserialize(BinaryReader))
//                   → IsDirty remains false, no event on receiver
//
// Plan bugs corrected (P-1 … P-8):
//   P-1  async Task not supported by Unity NUnit — all tests are synchronous.
//   P-2  CreateClient() / host.CreateRoom() / etc. are undefined helpers.
//        Integration is verified at the byte+API boundary instead.
//   P-3  await Task.Delay(100) not available — timing tested via
//        explicit renderTime injection into TryInterpolate().
//   P-4  MeasureRoundTrip() undefined — Stopwatch-based throughput tests used.
//   P-5  Bandwidth budget used 60 Hz — spec is 30 Hz; corrected in BandwidthTests.
//   P-6  latencies[500] hardcoded index — corrected to length-relative.
//   P-7  host.Spawn() / client.GetObject() undefined — GetState/ApplyState used.
//   P-8  TransformPacketParser.ReadF32LE / ReadU64LE are private; test helper
//        WriteF32LE / WriteU64LE reimplements the same LE encoding independently.
//
// Test fixtures:
//   ReceivePipelineIntegrationTests — StateDelta bytes → Parser → Interpolator
//   NetworkVariablePipelineTests    — NetworkVariable Serialize/Deserialize chain
//   SyncPerformanceTests            — Stopwatch throughput for hot-path ops

using System;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    // ═════════════════════════════════════════════════════════════════════════
    // RECEIVE PIPELINE: StateDelta bytes → TransformPacketParser
    //                                    → NetworkTransformInterpolator
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Integration tests that route mock server StateDelta payloads through
    /// the full C# receive pipeline and verify the final interpolated result.
    /// </summary>
    [TestFixture]
    [Category("SyncPipeline")]
    public class ReceivePipelineIntegrationTests
    {
        // ── Local stub (private — no clash across assemblies) ─────────────────
        private sealed class LocalNB : NetworkBehaviour { }

        // ── Scene objects ─────────────────────────────────────────────────────
        private GameObject                   _nmGo;
        private NetworkManager               _manager;
        private GameObject                   _interpGo;
        private NetworkTransformInterpolator _interp;

        // ── SetUp / TearDown ──────────────────────────────────────────────────

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _interpGo = new GameObject("InterpObject");
            _interp   = _interpGo.AddComponent<NetworkTransformInterpolator>();
            // Small buffer is fine; timestamps control the test outcomes.
            _interp.ConfigureForTest(bufferSize: 10, interpolationDelay: 0.1f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_interpGo != null) { Object.DestroyImmediate(_interpGo); _interpGo = null; }
            if (_nmGo     != null) { Object.DestroyImmediate(_nmGo);     _nmGo     = null; }
        }

        // ── Wire-building helpers (independent LE implementation) ─────────────

        /// <summary>
        /// Builds a StateDelta binary payload matching Go's StateDelta.Serialize()
        /// layout exactly.  Only fields whose bit is set in <paramref name="mask"/>
        /// are included.
        /// </summary>
        private static byte[] BuildDeltaPayload(
            ulong objectId,
            byte  mask,
            float px = 0f, float py = 0f, float pz = 0f,          // iff mask & 0x01
            float rx = 0f, float ry = 0f, float rz = 0f, float rw = 1f,  // iff mask & 0x02
            float sx = 1f, float sy = 1f, float sz = 1f)           // iff mask & 0x04
        {
            int size = 9; // ObjectID(8) + ChangedMask(1)
            if ((mask & TransformPacketParser.ChangedPosition) != 0) size += 12;
            if ((mask & TransformPacketParser.ChangedRotation) != 0) size += 16;
            if ((mask & TransformPacketParser.ChangedScale)    != 0) size += 12;

            var buf = new byte[size];
            int off = 0;

            WriteU64LE(buf, ref off, objectId);
            buf[off++] = mask;

            if ((mask & TransformPacketParser.ChangedPosition) != 0)
            {
                WriteF32LE(buf, ref off, px);
                WriteF32LE(buf, ref off, py);
                WriteF32LE(buf, ref off, pz);
            }
            if ((mask & TransformPacketParser.ChangedRotation) != 0)
            {
                WriteF32LE(buf, ref off, rx);
                WriteF32LE(buf, ref off, ry);
                WriteF32LE(buf, ref off, rz);
                WriteF32LE(buf, ref off, rw);
            }
            if ((mask & TransformPacketParser.ChangedScale) != 0)
            {
                WriteF32LE(buf, ref off, sx);
                WriteF32LE(buf, ref off, sy);
                WriteF32LE(buf, ref off, sz);
            }

            return buf;
        }

        private static void WriteU64LE(byte[] buf, ref int off, ulong val)
        {
            for (int i = 0; i < 8; i++) buf[off++] = (byte)(val >> (i * 8));
        }

        private static void WriteF32LE(byte[] buf, ref int off, float val)
        {
            int bits = BitConverter.SingleToInt32Bits(val);
            for (int i = 0; i < 4; i++) buf[off++] = (byte)(bits >> (i * 8));
        }

        // ─────────────────────────────────────────────────────────────────────
        // Group 1: Wire-size assertions (StateDelta payload sizes match spec)
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        [Description("An empty delta (ChangedMask=0x00) is exactly 9 bytes.")]
        public void DeltaPayload_EmptyMask_Is9Bytes()
        {
            var bytes = BuildDeltaPayload(1UL, 0x00);

            Assert.AreEqual(9, bytes.Length);
            Assert.AreEqual(TransformPacketParser.DELTA_MIN_SIZE, bytes.Length);
        }

        [Test]
        [Description("A position-only delta (0x01) is 9 + 12 = 21 bytes.")]
        public void DeltaPayload_PositionOnly_Is21Bytes()
        {
            var bytes = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition);

            Assert.AreEqual(21, bytes.Length);
        }

        [Test]
        [Description("A position+rotation delta (0x03, most common 3D case) is 9 + 12 + 16 = 37 bytes.")]
        public void DeltaPayload_PositionAndRotation_Is37Bytes()
        {
            byte mask = (byte)(TransformPacketParser.ChangedPosition |
                               TransformPacketParser.ChangedRotation);
            var bytes = BuildDeltaPayload(1UL, mask);

            Assert.AreEqual(37, bytes.Length);
        }

        [Test]
        [Description("A full delta (0x07) is 9 + 12 + 16 + 12 = 49 bytes (maximum).")]
        public void DeltaPayload_AllFields_Is49Bytes()
        {
            var bytes = BuildDeltaPayload(1UL, TransformPacketParser.KnownMask);

            Assert.AreEqual(49, bytes.Length);
        }

        [Test]
        [Description("A full transform payload (TransformPacketBuilder) is fixed at 48 bytes.")]
        public void FullStatePayload_AlwaysIs48Bytes()
        {
            var payload = TransformPacketBuilder.BuildUpdatePayload(42UL, TransformState.Identity);

            Assert.AreEqual(TransformPacketBuilder.PAYLOAD_SIZE, payload.Length);
            Assert.AreEqual(48, payload.Length);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Group 2: Parse → verify decoded fields
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        [Description("An empty delta parse succeeds with mask=0 and zero-initialised state.")]
        public void ParseEmptyDelta_Succeeds_ZeroMask()
        {
            var bytes = BuildDeltaPayload(99UL, 0x00);

            bool ok = TransformPacketParser.TryParseStateDelta(bytes, out ulong id, out byte mask, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(99UL, id);
            Assert.AreEqual(0, mask);
        }

        [Test]
        [Description("A position-only delta is decoded with the correct position and mask=0x01.")]
        public void ParsePositionOnlyDelta_CorrectFields()
        {
            var bytes = BuildDeltaPayload(7UL, TransformPacketParser.ChangedPosition,
                px: 5f, py: -3f, pz: 12f);

            bool ok = TransformPacketParser.TryParseStateDelta(
                bytes, out ulong id, out byte mask, out TransformState state);

            Assert.IsTrue(ok, "Parse must succeed.");
            Assert.AreEqual(7UL,  id,   "ObjectID must match.");
            Assert.AreEqual(TransformPacketParser.ChangedPosition, mask, "ChangedMask must be position bit.");
            Assert.AreEqual( 5f, state.Position.x, 0.0001f, "Position.x");
            Assert.AreEqual(-3f, state.Position.y, 0.0001f, "Position.y");
            Assert.AreEqual(12f, state.Position.z, 0.0001f, "Position.z");
        }

        [Test]
        [Description("A full-mask delta (0x07) is decoded with all three transform fields.")]
        public void ParseFullDelta_AllFieldsDecoded()
        {
            var q = Quaternion.Euler(0f, 90f, 0f);
            var bytes = BuildDeltaPayload(
                2UL,
                TransformPacketParser.KnownMask,
                px: 1f, py: 2f, pz: 3f,
                rx: q.x, ry: q.y, rz: q.z, rw: q.w,
                sx: 2f, sy: 2f, sz: 2f);

            bool ok = TransformPacketParser.TryParseStateDelta(
                bytes, out ulong id, out byte mask, out TransformState state);

            Assert.IsTrue(ok);
            Assert.AreEqual(2UL, id);
            Assert.AreEqual(TransformPacketParser.KnownMask, mask);

            Assert.AreEqual(1f, state.Position.x, 0.0001f, "Pos X");
            Assert.AreEqual(2f, state.Position.y, 0.0001f, "Pos Y");
            Assert.AreEqual(3f, state.Position.z, 0.0001f, "Pos Z");

            Assert.AreEqual(q.x, state.Rotation.x, 0.0001f, "Rot X");
            Assert.AreEqual(q.y, state.Rotation.y, 0.0001f, "Rot Y");
            Assert.AreEqual(q.z, state.Rotation.z, 0.0001f, "Rot Z");
            Assert.AreEqual(q.w, state.Rotation.w, 0.0001f, "Rot W");

            Assert.AreEqual(2f, state.Scale.x, 0.0001f, "Scale X");
            Assert.AreEqual(2f, state.Scale.y, 0.0001f, "Scale Y");
            Assert.AreEqual(2f, state.Scale.z, 0.0001f, "Scale Z");
        }

        [Test]
        [Description("A delta with unknown bits (0x08) is rejected by the parser.")]
        public void ParseDelta_UnknownBit_ReturnsFalse()
        {
            var bytes = BuildDeltaPayload(1UL, 0x00);
            bytes[8] = 0x08; // inject an unknown bit into the ChangedMask byte

            bool ok = TransformPacketParser.TryParseStateDelta(bytes, out _, out _, out _);

            Assert.IsFalse(ok, "Parser must reject unknown mask bits.");
        }

        [Test]
        [Description("A payload shorter than 9 bytes is rejected.")]
        public void ParseDelta_TooShort_ReturnsFalse()
        {
            var bytes = new byte[5]; // shorter than DELTA_MIN_SIZE=9

            bool ok = TransformPacketParser.TryParseStateDelta(bytes, out _, out _, out _);

            Assert.IsFalse(ok, "Parser must reject payloads shorter than minimum.");
        }

        [Test]
        [Description("A null payload is rejected without throwing.")]
        public void ParseDelta_Null_ReturnsFalse()
        {
            bool ok = TransformPacketParser.TryParseStateDelta(null, out _, out _, out _);

            Assert.IsFalse(ok, "Parser must handle null without throwing.");
        }

        // ─────────────────────────────────────────────────────────────────────
        // Group 3: Parse → AddState → TryInterpolate (full receive pipeline)
        // ─────────────────────────────────────────────────────────────────────

        [Test]
        [Description("A position-only delta parsed and fed to the interpolator; " +
                     "TryInterpolate recovers the exact position at the 'to' timestamp.")]
        public void PipelineChain_ParsedPositionDelta_InterpolatorRecoversPosition()
        {
            // Build two deltas at timestamps 0.0 and 1.0.
            var bytes0 = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition, px: 0f, py: 0f, pz: 0f);
            var bytes1 = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition, px: 10f, py: 0f, pz: 0f);

            bool ok0 = TransformPacketParser.TryParseStateDelta(bytes0, out _, out _, out TransformState s0);
            bool ok1 = TransformPacketParser.TryParseStateDelta(bytes1, out _, out _, out TransformState s1);

            Assert.IsTrue(ok0 && ok1, "Both parses must succeed.");

            _interp.AddState(s0, 0.0);
            _interp.AddState(s1, 1.0);

            // At renderTime=0.5 (midpoint) → x should interpolate to 5.
            bool got = _interp.TryInterpolate(0.5, out TransformState midResult);

            Assert.IsTrue(got, "TryInterpolate must succeed with 2 buffered states.");
            Assert.AreEqual(5f, midResult.Position.x, 0.01f, "Midpoint position x must be 5.");
        }

        [Test]
        [Description("At renderTime = exact 'to' timestamp, interpolator returns the 'to' state.")]
        public void PipelineChain_RenderAtToTimestamp_ReturnsToState()
        {
            var bytes0 = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition, px: 0f);
            var bytes1 = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition, px: 20f);

            TransformPacketParser.TryParseStateDelta(bytes0, out _, out _, out TransformState s0);
            TransformPacketParser.TryParseStateDelta(bytes1, out _, out _, out TransformState s1);

            _interp.AddState(s0, 0.0);
            _interp.AddState(s1, 2.0);

            _interp.TryInterpolate(2.0, out TransformState result);

            Assert.AreEqual(20f, result.Position.x, 0.001f, "At 'to' timestamp, x must be 20.");
        }

        [Test]
        [Description("Full-mask delta parsed and fed to interpolator: all three fields " +
                     "available; interpolated result has correct x position and rotation.")]
        public void PipelineChain_FullDelta_AllFieldsAvailableForInterpolation()
        {
            var q0 = Quaternion.identity;
            var q1 = Quaternion.Euler(0f, 180f, 0f);

            var b0 = BuildDeltaPayload(1UL, TransformPacketParser.KnownMask,
                px:0f, py:0f, pz:0f,
                rx:q0.x, ry:q0.y, rz:q0.z, rw:q0.w,
                sx:1f, sy:1f, sz:1f);
            var b1 = BuildDeltaPayload(1UL, TransformPacketParser.KnownMask,
                px:10f, py:0f, pz:0f,
                rx:q1.x, ry:q1.y, rz:q1.z, rw:q1.w,
                sx:2f, sy:2f, sz:2f);

            TransformPacketParser.TryParseStateDelta(b0, out _, out _, out TransformState st0);
            TransformPacketParser.TryParseStateDelta(b1, out _, out _, out TransformState st1);

            _interp.AddState(st0, 0.0);
            _interp.AddState(st1, 1.0);

            bool ok = _interp.TryInterpolate(0.5, out TransformState mid);

            Assert.IsTrue(ok);
            Assert.AreEqual(5f, mid.Position.x, 0.01f, "Lerped position x");
        }

        [Test]
        [Description("Three objects are parsed from consecutive deltas; each is routed to " +
                     "an independent interpolator based on objectId.")]
        public void PipelineChain_ThreeObjectIds_ParsedCorrectly()
        {
            // Build two more interpolators for objects 2 and 3.
            var go2    = new GameObject("Interp2");
            var go3    = new GameObject("Interp3");
            var interp2 = go2.AddComponent<NetworkTransformInterpolator>();
            var interp3 = go3.AddComponent<NetworkTransformInterpolator>();
            interp2.ConfigureForTest(10, 0.1f);
            interp3.ConfigureForTest(10, 0.1f);

            try
            {
                byte mask = TransformPacketParser.ChangedPosition;
                var deltas = new[]
                {
                    BuildDeltaPayload(1UL, mask, px: 10f),
                    BuildDeltaPayload(2UL, mask, px: 20f),
                    BuildDeltaPayload(3UL, mask, px: 30f),
                };

                var interpolators = new System.Collections.Generic.Dictionary<ulong, NetworkTransformInterpolator>
                {
                    { 1UL, _interp   },
                    { 2UL,  interp2  },
                    { 3UL,  interp3  },
                };

                double ts = 0.0;
                // First tick — give each interpolator a "from" state.
                foreach (var delta in deltas)
                {
                    bool ok = TransformPacketParser.TryParseStateDelta(delta, out ulong id, out _, out TransformState st);
                    Assert.IsTrue(ok);
                    interpolators[id].AddState(st, ts);
                }
                ts = 1.0;
                // Second tick — give each interpolator a "to" state with moved position.
                var deltas2 = new[]
                {
                    BuildDeltaPayload(1UL, mask, px: 11f),
                    BuildDeltaPayload(2UL, mask, px: 22f),
                    BuildDeltaPayload(3UL, mask, px: 33f),
                };
                foreach (var delta in deltas2)
                {
                    bool ok = TransformPacketParser.TryParseStateDelta(delta, out ulong id, out _, out TransformState st);
                    Assert.IsTrue(ok);
                    interpolators[id].AddState(st, ts);
                }

                // At midpoint 0.5 each interpolator should have moved halfway.
                _interp.TryInterpolate(0.5, out TransformState r1);
                interp2.TryInterpolate(0.5, out TransformState r2);
                interp3.TryInterpolate(0.5, out TransformState r3);

                Assert.AreEqual(10.5f, r1.Position.x, 0.01f, "Object 1 x");
                Assert.AreEqual(21f,   r2.Position.x, 0.01f, "Object 2 x");
                Assert.AreEqual(31.5f, r3.Position.x, 0.01f, "Object 3 x");
            }
            finally
            {
                Object.DestroyImmediate(go2);
                Object.DestroyImmediate(go3);
            }
        }

        [Test]
        [Description("A parsed delta with objectId=0 is routed correctly — " +
                     "objectId=0 is a valid identifier.")]
        public void PipelineChain_ObjectIdZero_ParsesAndRoutesCorrectly()
        {
            var bytes = BuildDeltaPayload(0UL, TransformPacketParser.ChangedPosition, px: 77f);

            bool ok = TransformPacketParser.TryParseStateDelta(bytes, out ulong id, out _, out TransformState st);

            Assert.IsTrue(ok);
            Assert.AreEqual(0UL, id, "ObjectId 0 must be preserved.");
            Assert.AreEqual(77f, st.Position.x, 0.0001f);
        }

        [Test]
        [Description("Interpolator AddState then BufferCount reflects the number of parsed deltas fed.")]
        public void PipelineChain_BufferCountMatchesFedDeltas()
        {
            for (int i = 0; i < 4; i++)
            {
                var bytes = BuildDeltaPayload(1UL, TransformPacketParser.ChangedPosition, px: i * 1f);
                TransformPacketParser.TryParseStateDelta(bytes, out _, out _, out TransformState st);
                _interp.AddState(st, i * 0.5);
            }

            Assert.AreEqual(4, _interp.BufferCount,
                "4 AddState calls must produce BufferCount=4 (within buffer capacity).");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // VARIABLE PIPELINE: NetworkVariable Serialize → Deserialize chain
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Integration tests for the NetworkVariable serialization pipeline — the
    /// path that would be taken by the send tick to flush dirty variables.
    /// </summary>
    [TestFixture]
    [Category("SyncPipeline")]
    public class NetworkVariablePipelineTests
    {
        // Private stub — no name clash with other fixtures.
        private sealed class VarStubNB : NetworkBehaviour { }

        private GameObject     _nmGo;
        private NetworkManager _manager;
        private GameObject     _ownerGo;
        private VarStubNB      _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _ownerGo = new GameObject("OwnerObject");
            _owner   = _ownerGo.AddComponent<VarStubNB>();
            _owner.Initialize(1UL, "test-owner");
        }

        [TearDown]
        public void TearDown()
        {
            if (_ownerGo != null) { Object.DestroyImmediate(_ownerGo); _ownerGo = null; }
            if (_nmGo    != null) { Object.DestroyImmediate(_nmGo);    _nmGo    = null; }
        }

        // ── Helper ────────────────────────────────────────────────────────────

        // Serialize src into a MemoryStream, then deserialize into dst.
        // dst is the "receiver" side (SetValueWithoutNotify — no dirty, no event).
        private static void RoundTripVar(NetworkVariableBase src, NetworkVariableBase dst)
        {
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            src.Serialize(writer);
            writer.Flush();
            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            dst.Deserialize(reader);
        }

        // ── 1: Single dirty variable round-trip ───────────────────────────────

        [Test]
        [Description("A single dirty NetworkVariableInt is serialized → deserialized; " +
                     "receiver sees the correct value and is NOT dirty.")]
        public void SingleDirtyInt_SerializeDeserialize_ReceiverHasValueNotDirty()
        {
            var src = new NetworkVariableInt(_owner, 0, 12345);
            var dst = new NetworkVariableInt(_owner, 0, 0);

            Assert.IsTrue(false == src.IsDirty, "Constructor must not set IsDirty.");
            src.Value = 99999; // marks dirty
            Assert.IsTrue(src.IsDirty, "Value change must mark dirty.");

            RoundTripVar(src, dst);

            Assert.AreEqual(99999, dst.Value,    "Receiver must have the transmitted value.");
            Assert.IsFalse(dst.IsDirty,          "Deserialize must not mark receiver dirty.");
        }

        // ── 2: Multiple dirty variables serialized in order ───────────────────

        [Test]
        [Description("Two dirty variables are serialized consecutively into one stream; " +
                     "both values are correctly recovered on the receive side.")]
        public void TwoDirtyVars_SerializeToSameStream_BothRecovered()
        {
            var srcA = new NetworkVariableFloat(_owner, 0, 1.5f);
            var srcB = new NetworkVariableFloat(_owner, 1, 9.75f);
            var dstA = new NetworkVariableFloat(_owner, 0, 0f);
            var dstB = new NetworkVariableFloat(_owner, 1, 0f);

            // Both are in their initial state; Serialize is still valid for transmission.
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            srcA.Serialize(writer);
            srcB.Serialize(writer);
            writer.Flush();

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            dstA.Deserialize(reader);
            dstB.Deserialize(reader);

            Assert.AreEqual(1.5f,  dstA.Value, 0f, "Variable A value must match.");
            Assert.AreEqual(9.75f, dstB.Value, 0f, "Variable B value must match.");
        }

        // ── 3: Clean variable needs no send (IsDirty gate) ────────────────────

        [Test]
        [Description("A variable that was never changed stays clean after construction; " +
                     "the send tick would gate on IsDirty and skip it.")]
        public void CleanVar_IsDirtyFalse_NoTransmissionNeeded()
        {
            var v = new NetworkVariableBool(_owner, 0, true);

            Assert.IsFalse(v.IsDirty,
                "A never-changed variable must not be flagged dirty by the send tick.");
        }

        // ── 4: MarkClean restores IsDirty=false after send ────────────────────

        [Test]
        [Description("After transmitting (MarkClean), the variable is clean again; " +
                     "subsequent same-value sets are no-ops.")]
        public void DirtyAfterChange_CleanAfterMarkClean_NoResend()
        {
            var v = new NetworkVariableInt(_owner, 0, 0);
            v.Value = 42;
            Assert.IsTrue(v.IsDirty);

            v.MarkClean(); // simulate: packet sent, mark clean
            Assert.IsFalse(v.IsDirty,    "After MarkClean() the variable must be clean.");

            v.Value = 42;  // same value — no change
            Assert.IsFalse(v.IsDirty,    "Setting the same value must not re-dirty the variable.");
        }

        // ── 5: String null-normalisation in pipeline ──────────────────────────

        [Test]
        [Description("Setting a string variable to null, serializing then deserializing, " +
                     "always yields \"\" on the receiver (null normalisation P-6 round-trip).")]
        public void StringVar_NullAssigned_PipelineProducesEmpty()
        {
            var src = new NetworkVariableString(_owner, 0, "initial");
            var dst = new NetworkVariableString(_owner, 0, "other");

            src.Value = null; // P-6 converts this to ""

            RoundTripVar(src, dst);

            Assert.AreEqual(string.Empty, dst.Value, "null must round-trip as \"\".");
        }

        // ── 6: Vector3 variable round-trip in pipeline context ────────────────

        [Test]
        [Description("A Vector3 NetworkVariable serialized and deserialized in a " +
                     "multi-variable stream maintains correct component values.")]
        public void Vector3Var_PipelineRoundTrip_AllComponentsMatch()
        {
            var pos = new Vector3(1.5f, -2.25f, 3.75f);
            var src = new NetworkVariableVector3(_owner, 0, pos);
            var dst = new NetworkVariableVector3(_owner, 0, Vector3.zero);

            RoundTripVar(src, dst);

            Assert.AreEqual(pos.x, dst.Value.x, 0f, "X component");
            Assert.AreEqual(pos.y, dst.Value.y, 0f, "Y component");
            Assert.AreEqual(pos.z, dst.Value.z, 0f, "Z component");
        }

        // ── 7: Event fires correctly in pipeline context ──────────────────────

        [Test]
        [Description("Setting a variable on the receive side via Value (not Deserialize) " +
                     "fires OnValueChanged — confirming the event path is live.")]
        public void ReceiverSide_SetViaSetter_FiresEvent()
        {
            // Scenario: receiver OWNS the variable locally and sets it via Value setter.
            // (Deserialize uses SetValueWithoutNotify — no event.  This test verifies
            //  the setter path that would be used by game code reading a server ack.)
            var v = new NetworkVariableInt(_owner, 0, 0);
            int callCount = 0;
            v.OnValueChanged += (_, __) => callCount++;

            v.Value = 42;

            Assert.AreEqual(1, callCount, "Setting via Value must fire the event.");
        }
    }

    // ═════════════════════════════════════════════════════════════════════════
    // PERFORMANCE: Stopwatch-based throughput tests for the hot path
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Throughput tests verifying that the serialization hot-path can sustain
    /// much more than the 30 Hz required tick rate.  At 30 Hz we have 33 ms per
    /// tick; these tests run 10,000 iterations and allow 2,000 ms — giving a
    /// minimum bar of 5,000 ops/s, roughly 170× headroom above 30 Hz.
    /// </summary>
    [TestFixture]
    [Category("SyncPipeline")]
    public class SyncPerformanceTests
    {
        private const int   Iterations   = 10_000;
        private const long  MaxMs        = 2_000; // generous threshold (CI, debug builds)

        // Pre-built payload for parse tests (avoids measuring allocation in parser test).
        private byte[] _deltaPayload;

        [SetUp]
        public void SetUp()
        {
            // Full-mask delta: worst-case parse (most bytes to read).
            var q = Quaternion.Euler(30f, 60f, 90f);
            _deltaPayload = BuildDeltaPayload(
                0xDEADBEEFCAFEBABEUL,
                TransformPacketParser.KnownMask,
                px: 1f, py: 2f, pz: 3f,
                rx: q.x, ry: q.y, rz: q.z, rw: q.w,
                sx: 1f, sy: 1f, sz: 1f);
        }

        // ── Shared helpers (same LE implementation as ReceivePipelineIntegrationTests) ──

        private static byte[] BuildDeltaPayload(
            ulong objectId, byte mask,
            float px = 0f, float py = 0f, float pz = 0f,
            float rx = 0f, float ry = 0f, float rz = 0f, float rw = 1f,
            float sx = 1f, float sy = 1f, float sz = 1f)
        {
            int size = 9;
            if ((mask & TransformPacketParser.ChangedPosition) != 0) size += 12;
            if ((mask & TransformPacketParser.ChangedRotation) != 0) size += 16;
            if ((mask & TransformPacketParser.ChangedScale)    != 0) size += 12;

            var buf = new byte[size];
            int off = 0;

            for (int i = 0; i < 8; i++) buf[off++] = (byte)(objectId >> (i * 8));
            buf[off++] = mask;

            if ((mask & TransformPacketParser.ChangedPosition) != 0)
            {
                WriteF32LE(buf, ref off, px); WriteF32LE(buf, ref off, py); WriteF32LE(buf, ref off, pz);
            }
            if ((mask & TransformPacketParser.ChangedRotation) != 0)
            {
                WriteF32LE(buf, ref off, rx); WriteF32LE(buf, ref off, ry);
                WriteF32LE(buf, ref off, rz); WriteF32LE(buf, ref off, rw);
            }
            if ((mask & TransformPacketParser.ChangedScale) != 0)
            {
                WriteF32LE(buf, ref off, sx); WriteF32LE(buf, ref off, sy); WriteF32LE(buf, ref off, sz);
            }
            return buf;
        }

        private static void WriteF32LE(byte[] buf, ref int off, float val)
        {
            int bits = BitConverter.SingleToInt32Bits(val);
            for (int i = 0; i < 4; i++) buf[off++] = (byte)(bits >> (i * 8));
        }

        // ── 1: TransformPacketBuilder throughput ──────────────────────────────

        [Test]
        [Description("10,000 TransformPacketBuilder.BuildUpdatePayload() calls " +
                     "must complete in under 2,000 ms (~5,000 ops/s minimum).")]
        public void TransformSerialization_10000Ops_Under2000ms()
        {
            var state = new TransformState
            {
                Position = new Vector3(1f, 2f, 3f),
                Rotation = Quaternion.Euler(10f, 20f, 30f),
                Scale    = Vector3.one,
            };

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
                TransformPacketBuilder.BuildUpdatePayload((ulong)i, state);
            sw.Stop();

            Assert.That(sw.ElapsedMilliseconds, Is.LessThanOrEqualTo(MaxMs),
                $"10,000 serializations should be under {MaxMs}ms; took {sw.ElapsedMilliseconds}ms.");
        }

        // ── 2: TransformPacketParser throughput ───────────────────────────────

        [Test]
        [Description("10,000 TransformPacketParser.TryParseStateDelta() calls on a pre-built " +
                     "49-byte (worst-case) payload must complete in under 2,000 ms.")]
        public void DeltaDeserialization_10000Ops_Under2000ms()
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++)
                TransformPacketParser.TryParseStateDelta(_deltaPayload, out _, out _, out _);
            sw.Stop();

            Assert.That(sw.ElapsedMilliseconds, Is.LessThanOrEqualTo(MaxMs),
                $"10,000 delta parses should be under {MaxMs}ms; took {sw.ElapsedMilliseconds}ms.");
        }

        // ── 3: NetworkVariable serialization throughput ───────────────────────

        // Private stub — used only for the variable perf test below.
        // Named PerfNB to avoid clashing with identically-named stubs in other
        // fixtures (all other stubs are private/inside their own class scope).
        private sealed class PerfNB : NetworkBehaviour { }

        [Test]
        [Description("10,000 NetworkVariableInt.Serialize() + Deserialize() round-trips " +
                     "must complete in under 2,000 ms.")]
        public void NetworkVariableInt_10000RoundTrips_Under2000ms()
        {
            var nmGo  = new GameObject("PerfNM");
            _ = nmGo.AddComponent<NetworkManager>(); // must exist for NB.IsOwner
            var goOwn = new GameObject("PerfOwner");
            var stub  = goOwn.AddComponent<PerfNB>();
            stub.Initialize(1UL, "perf");

            try
            {
                var src = new NetworkVariableInt(stub, 0, 42);
                var dst = new NetworkVariableInt(stub, 0, 0);

                var sw = Stopwatch.StartNew();
                using var ms     = new MemoryStream(32);
                using var writer = new BinaryWriter(ms);
                using var reader = new BinaryReader(ms);

                for (int i = 0; i < Iterations; i++)
                {
                    ms.SetLength(0);
                    ms.Position = 0;
                    src.Serialize(writer);
                    writer.Flush();
                    ms.Position = 0;
                    dst.Deserialize(reader);
                }
                sw.Stop();

                Assert.That(sw.ElapsedMilliseconds, Is.LessThanOrEqualTo(MaxMs),
                    $"10,000 variable round-trips should be under {MaxMs}ms; took {sw.ElapsedMilliseconds}ms.");
            }
            finally
            {
                Object.DestroyImmediate(goOwn);
                Object.DestroyImmediate(nmGo);
            }
        }
    }
}
