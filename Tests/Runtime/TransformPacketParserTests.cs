// RTMPE SDK — Tests/Runtime/TransformPacketParserTests.cs
//
// NUnit Edit-Mode tests for TransformPacketParser.
// Verifies that the C# parser correctly reads the binary format produced by
// the Go server's StateDelta.Serialize() function.
//
// The helper BuildStateDeltaPayload mirrors the Go source to ensure exact
// wire compatibility.  Any change to StateDelta.Serialize() in Go MUST be
// reflected here.

using System;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    public class TransformPacketParserTests
    {
        // ── Wire-format builder helpers (mirrors Go StateDelta.Serialize) ─────

        private static void WriteU64LE(byte[] buf, int off, ulong v)
        {
            buf[off + 0] = (byte) v;
            buf[off + 1] = (byte)(v >>  8);
            buf[off + 2] = (byte)(v >> 16);
            buf[off + 3] = (byte)(v >> 24);
            buf[off + 4] = (byte)(v >> 32);
            buf[off + 5] = (byte)(v >> 40);
            buf[off + 6] = (byte)(v >> 48);
            buf[off + 7] = (byte)(v >> 56);
        }

        private static void WriteF32LE(byte[] buf, int off, float v)
        {
            int bits = BitConverter.SingleToInt32Bits(v);
            buf[off + 0] = (byte) bits;
            buf[off + 1] = (byte)(bits >>  8);
            buf[off + 2] = (byte)(bits >> 16);
            buf[off + 3] = (byte)(bits >> 24);
        }

        /// <summary>
        /// Build a StateDelta payload byte array mirroring Go's StateDelta.Serialize().
        /// Layout: [ObjectID:8][ChangedMask:1][pos:0/12][rot:0/16][scale:0/12]
        /// </summary>
        private static byte[] BuildStateDeltaPayload(
            ulong  objectId,
            byte   changedMask,
            float  px = 0, float py = 0, float pz = 0,
            float  rx = 0, float ry = 0, float rz = 0, float rw = 1,
            float  sx = 1, float sy = 1, float sz = 1)
        {
            int size = 9; // ObjectID(8) + ChangedMask(1)
            if ((changedMask & TransformPacketParser.ChangedPosition) != 0) size += 12;
            if ((changedMask & TransformPacketParser.ChangedRotation) != 0) size += 16;
            if ((changedMask & TransformPacketParser.ChangedScale)    != 0) size += 12;

            var buf = new byte[size];
            int off = 0;

            WriteU64LE(buf, off, objectId); off += 8;
            buf[off++] = changedMask;

            if ((changedMask & TransformPacketParser.ChangedPosition) != 0)
            {
                WriteF32LE(buf, off, px); off += 4;
                WriteF32LE(buf, off, py); off += 4;
                WriteF32LE(buf, off, pz); off += 4;
            }
            if ((changedMask & TransformPacketParser.ChangedRotation) != 0)
            {
                WriteF32LE(buf, off, rx); off += 4;
                WriteF32LE(buf, off, ry); off += 4;
                WriteF32LE(buf, off, rz); off += 4;
                WriteF32LE(buf, off, rw); off += 4;
            }
            if ((changedMask & TransformPacketParser.ChangedScale) != 0)
            {
                WriteF32LE(buf, off, sx); off += 4;
                WriteF32LE(buf, off, sy); off += 4;
                WriteF32LE(buf, off, sz); off += 4;
            }

            return buf;
        }

        // ── Guard cases ───────────────────────────────────────────────────────

        [Test]
        public void TryParseStateDelta_NullPayload_ReturnsFalse()
        {
            bool ok = TransformPacketParser.TryParseStateDelta(null, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParseStateDelta_EmptyPayload_ReturnsFalse()
        {
            bool ok = TransformPacketParser.TryParseStateDelta(
                Array.Empty<byte>(), out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        [Description("8 bytes is one short of the 9-byte minimum (ObjectID only, no mask).")]
        public void TryParseStateDelta_8Bytes_TooShort_ReturnsFalse()
        {
            bool ok = TransformPacketParser.TryParseStateDelta(
                new byte[8], out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        [Description("Unknown mask bit 0x08 (outside KnownMask=0x07) must cause rejection.")]
        public void TryParseStateDelta_UnknownMaskBit_ReturnsFalse()
        {
            // Build a minimal 9-byte payload (no fields) but with bit 0x08 set.
            var payload = new byte[9];
            WriteU64LE(payload, 0, 1UL);
            payload[8] = 0x08; // unknown bit

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        [Description("Payload claims Position but is truncated — must return false.")]
        public void TryParseStateDelta_TruncatedPositionPayload_ReturnsFalse()
        {
            // 9 bytes minimum + position flag but only 3 extra bytes (need 12).
            var payload = new byte[12]; // 9 + 3 (insufficient for full position)
            WriteU64LE(payload, 0, 1UL);
            payload[8] = TransformPacketParser.ChangedPosition;
            // bytes 9..11 present but position needs 12 bytes (9..20 → off 20 > len 12)

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        // ── Zero-mask (no fields) ─────────────────────────────────────────────

        [Test]
        [Description("ChangedMask=0 is valid; only ObjectID is decoded.")]
        public void TryParseStateDelta_ZeroMask_ObjectIdDecoded_ReturnsTrue()
        {
            var payload = BuildStateDeltaPayload(objectId: 42UL, changedMask: 0);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out byte mask, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(42UL, objectId, "objectId should be 42");
            Assert.AreEqual(0, mask, "changedMask should be 0");
        }

        // ── All-changed ───────────────────────────────────────────────────────

        [Test]
        [Description("All-changed (0x07) payload: all three transform fields are decoded.")]
        public void TryParseStateDelta_AllChanged_ParsesPositionRotationScale()
        {
            byte allChanged = TransformPacketParser.ChangedPosition
                            | TransformPacketParser.ChangedRotation
                            | TransformPacketParser.ChangedScale;

            var payload = BuildStateDeltaPayload(
                objectId: 7UL, changedMask: allChanged,
                px: 1f, py: 2f, pz: 3f,
                rx: 0f, ry: 0f, rz: 0f, rw: 1f,
                sx: 2f, sy: 2f, sz: 2f);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out byte mask, out TransformState state);

            Assert.IsTrue(ok);
            Assert.AreEqual(7UL,       objectId);
            Assert.AreEqual(allChanged, mask);
            Assert.AreEqual(new Vector3(1, 2, 3),         state.Position, "Position");
            Assert.AreEqual(new Quaternion(0, 0, 0, 1),  state.Rotation, "Rotation");
            Assert.AreEqual(new Vector3(2, 2, 2),         state.Scale,    "Scale");
        }

        // ── Individual fields ─────────────────────────────────────────────────

        [Test]
        [Description("Position-only delta: only Position field populated.")]
        public void TryParseStateDelta_PositionOnly_ParsesPosition()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 10UL, changedMask: TransformPacketParser.ChangedPosition,
                px: 5f, py: -3f, pz: 7f);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out byte mask, out TransformState state);

            Assert.IsTrue(ok);
            Assert.AreEqual(10UL,                         objectId);
            Assert.AreEqual(TransformPacketParser.ChangedPosition, mask);
            Assert.AreEqual(new Vector3(5f, -3f, 7f),     state.Position, "Position");
        }

        [Test]
        [Description("Rotation-only delta: only Rotation field populated.")]
        public void TryParseStateDelta_RotationOnly_ParsesRotation()
        {
            float s = Mathf.Sin(Mathf.PI / 4f);
            float c = Mathf.Cos(Mathf.PI / 4f);

            var payload = BuildStateDeltaPayload(
                objectId: 20UL, changedMask: TransformPacketParser.ChangedRotation,
                rx: 0f, ry: s, rz: 0f, rw: c);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out byte mask, out TransformState state);

            Assert.IsTrue(ok);
            Assert.AreEqual(20UL,                              objectId);
            Assert.AreEqual(TransformPacketParser.ChangedRotation, mask);
            Assert.AreEqual(0f,  state.Rotation.x, 1e-6f, "rot_x");
            Assert.AreEqual(s,   state.Rotation.y, 1e-6f, "rot_y");
            Assert.AreEqual(0f,  state.Rotation.z, 1e-6f, "rot_z");
            Assert.AreEqual(c,   state.Rotation.w, 1e-6f, "rot_w");
        }

        [Test]
        [Description("Scale-only delta: only Scale field populated.")]
        public void TryParseStateDelta_ScaleOnly_ParsesScale()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 30UL, changedMask: TransformPacketParser.ChangedScale,
                sx: 3f, sy: 3f, sz: 3f);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out byte mask, out TransformState state);

            Assert.IsTrue(ok);
            Assert.AreEqual(30UL,                           objectId);
            Assert.AreEqual(TransformPacketParser.ChangedScale, mask);
            Assert.AreEqual(new Vector3(3f, 3f, 3f), state.Scale, "Scale");
        }

        // ── ObjectID encoding ─────────────────────────────────────────────────

        [Test]
        [Description("Large ObjectID is correctly decoded from the 8-byte LE encoding.")]
        public void TryParseStateDelta_LargeObjectId_DecodedCorrectly()
        {
            const ulong id      = 0xFEDCBA9876543210UL;
            var         payload = BuildStateDeltaPayload(objectId: id, changedMask: 0);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out ulong objectId, out _, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(id, objectId);
        }
    }
}
