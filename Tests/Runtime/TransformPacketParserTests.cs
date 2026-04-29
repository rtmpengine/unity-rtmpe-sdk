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

        // ── SDK-C3 regression: NaN/Inf fields must be rejected ────────────────
        //
       // Before the fix, ReadF32LE could return NaN or ±Infinity for malformed
        // server packets (any IEEE 754 bit pattern in the wire bytes decodes to a
        // float32, including NaN and Inf).  Applying those values to a Unity
        // transform crashes the physics engine.  The fix returns false whenever
        // any decoded float32 is non-finite.

        [Test]
        [Description("SDK-C3: NaN position X must cause TryParseStateDelta to return false.")]
        public void TryParseStateDelta_NaNPositionX_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedPosition,
                px: float.NaN, py: 0f, pz: 0f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "NaN px must be rejected");
        }

        [Test]
        [Description("SDK-C3: +Infinity position must cause TryParseStateDelta to return false.")]
        public void TryParseStateDelta_InfPosition_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedPosition,
                px: float.PositiveInfinity, py: 0f, pz: 0f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "+Inf px must be rejected");
        }

        [Test]
        [Description("SDK-C3: NaN rotation W (common degenerate quaternion) must be rejected.")]
        public void TryParseStateDelta_NaNRotationW_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedRotation,
                rx: 0f, ry: 0f, rz: 0f, rw: float.NaN);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "NaN rw must be rejected");
        }

        [Test]
        [Description("SDK-C3: NaN scale Z must cause TryParseStateDelta to return false.")]
        public void TryParseStateDelta_NaNScaleZ_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedScale,
                sx: 1f, sy: 1f, sz: float.NaN);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "NaN sz must be rejected");
        }

        [Test]
        [Description("SDK-C3: -Infinity scale must be rejected.")]
        public void TryParseStateDelta_NegInfScale_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedScale,
                sx: float.NegativeInfinity, sy: 1f, sz: 1f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "-Inf sx must be rejected");
        }

        [Test]
        [Description("SDK-C3: Finite values across all three fields must still succeed after the fix.")]
        public void TryParseStateDelta_FiniteValues_StillSucceeds()
        {
            byte allChanged = TransformPacketParser.ChangedPosition
                            | TransformPacketParser.ChangedRotation
                            | TransformPacketParser.ChangedScale;

            var payload = BuildStateDeltaPayload(
                objectId: 99UL, changedMask: allChanged,
                px: 1f, py: 2f, pz: 3f,
                rx: 0f, ry: 0f, rz: 0f, rw: 1f,
                sx: 1f, sy: 1f, sz: 1f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsTrue(ok, "all-finite packet must still parse successfully");
        }

        // ── Quaternion magnitude validation (audit fix C-002 / C-007) ─────────
        //
       // Quaternions handed to Unity's physics / interpolation APIs MUST be
        // unit-length (|q| = 1).  A corrupted or hostile packet can carry a
        // scaled quaternion; previously the parser accepted it (only NaN/Inf
        // were checked) and the engine silently degraded.  The parser now
        // rejects |q|² outside the range [0.9, 1.1] and renormalises mild FP
        // drift inside that band.

        [Test]
        [Description("Audit C-002/C-007: zero quaternion (|q|² = 0) must be rejected.")]
        public void TryParseStateDelta_ZeroQuaternion_ReturnsFalse()
        {
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedRotation,
                rx: 0f, ry: 0f, rz: 0f, rw: 0f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "zero quaternion must be rejected — no valid rotation has |q|² = 0");
        }

        [Test]
        [Description("Audit C-002/C-007: double-length quaternion (|q|² = 4) must be rejected.")]
        public void TryParseStateDelta_DoubleLengthQuaternion_ReturnsFalse()
        {
            // (0, 0, 0, 2) has |q|² = 4 — well outside the [0.9, 1.1] band.
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedRotation,
                rx: 0f, ry: 0f, rz: 0f, rw: 2f);

            bool ok = TransformPacketParser.TryParseStateDelta(payload, out _, out _, out _);
            Assert.IsFalse(ok, "grossly non-unit quaternion must be rejected");
        }

        [Test]
        [Description("Audit C-002/C-007: mildly non-unit quaternion is accepted AND renormalised.")]
        public void TryParseStateDelta_MildlyDenormalizedQuaternion_Normalizes()
        {
            // Construct a rotation that represents 45° around Y but scaled slightly
            // off-unit.  |q|² sits at ~1.0004 — inside the tolerance band.
            const float scale = 1.0002f;
            float rx = 0f;
            float ry = Mathf.Sin(Mathf.PI / 8f) * scale;
            float rz = 0f;
            float rw = Mathf.Cos(Mathf.PI / 8f) * scale;

            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedRotation,
                rx: rx, ry: ry, rz: rz, rw: rw);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out _, out TransformState state, out _);
            Assert.IsTrue(ok, "mild FP drift must be tolerated (not rejected)");

            // After normalisation |q|² must be essentially 1.
            float magSq = state.Rotation.x * state.Rotation.x
                        + state.Rotation.y * state.Rotation.y
                        + state.Rotation.z * state.Rotation.z
                        + state.Rotation.w * state.Rotation.w;
            Assert.That(magSq, Is.EqualTo(1f).Within(1e-5f),
                "parser must renormalise mildly denormalised quaternions");
        }

        [Test]
        [Description("Audit C-002/C-007: exact unit quaternion stays untouched (no drift introduced).")]
        public void TryParseStateDelta_UnitQuaternion_PreservesValues()
        {
            // 90° around Z axis: (0, 0, sin(45°), cos(45°)) — exactly unit-length.
            const float s = 0.7071067811865476f; // sqrt(2)/2
            var payload = BuildStateDeltaPayload(
                objectId: 1UL,
                changedMask: TransformPacketParser.ChangedRotation,
                rx: 0f, ry: 0f, rz: s, rw: s);

            bool ok = TransformPacketParser.TryParseStateDelta(
                payload, out _, out TransformState state, out _);
            Assert.IsTrue(ok);

            Assert.That(state.Rotation.x, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(state.Rotation.y, Is.EqualTo(0f).Within(1e-6f));
            Assert.That(state.Rotation.z, Is.EqualTo(s).Within(1e-6f));
            Assert.That(state.Rotation.w, Is.EqualTo(s).Within(1e-6f));
        }
    }
}
