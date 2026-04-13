// RTMPE SDK — Tests/Runtime/TransformPacketBuilderTests.cs
//
// NUnit Edit-Mode tests for TransformPacketBuilder.
// Pure serialisation tests — no Unity scene or GameObjects required.
// All tests are [Category("Sync")] for easy filtering in the Test Runner.

using System;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Sync")]
    public class TransformPacketBuilderTests
    {
        // ── Helpers ───────────────────────────────────────────────────────────

        private static ulong ReadU64LE(byte[] buf, int off)
            =>  (ulong)buf[off + 0]
             | ((ulong)buf[off + 1] <<  8)
             | ((ulong)buf[off + 2] << 16)
             | ((ulong)buf[off + 3] << 24)
             | ((ulong)buf[off + 4] << 32)
             | ((ulong)buf[off + 5] << 40)
             | ((ulong)buf[off + 6] << 48)
             | ((ulong)buf[off + 7] << 56);

        private static float ReadF32LE(byte[] buf, int off)
        {
            int bits = buf[off + 0]
                     | (buf[off + 1] <<  8)
                     | (buf[off + 2] << 16)
                     | (buf[off + 3] << 24);
            return BitConverter.Int32BitsToSingle(bits);
        }

        private static TransformState MakeState(
            float px, float py, float pz,
            float rx, float ry, float rz, float rw,
            float sx, float sy, float sz)
            => new TransformState
            {
                Position = new Vector3(px, py, pz),
                Rotation = new Quaternion(rx, ry, rz, rw),
                Scale    = new Vector3(sx, sy, sz),
            };

        // ── Size ──────────────────────────────────────────────────────────────

        [Test]
        [Description("BuildUpdatePayload always returns exactly 48 bytes.")]
        public void BuildUpdatePayload_ProducesExactly48Bytes()
        {
            var payload = TransformPacketBuilder.BuildUpdatePayload(1UL, TransformState.Identity);

            Assert.AreEqual(TransformPacketBuilder.PAYLOAD_SIZE, payload.Length);
            Assert.AreEqual(48, payload.Length);
        }

        // ── ObjectID encoding ─────────────────────────────────────────────────

        [Test]
        [Description("ObjectID is written as a little-endian u64 at bytes 0..7.")]
        public void BuildUpdatePayload_ObjectId_WrittenAsLittleEndian()
        {
            // 0x0807060504030201 → LE bytes: 01 02 03 04 05 06 07 08
            const ulong id = 0x0807060504030201UL;
            var payload = TransformPacketBuilder.BuildUpdatePayload(id, TransformState.Identity);

            Assert.AreEqual(0x01, payload[0], "byte 0 (LSB)");
            Assert.AreEqual(0x02, payload[1], "byte 1");
            Assert.AreEqual(0x03, payload[2], "byte 2");
            Assert.AreEqual(0x04, payload[3], "byte 3");
            Assert.AreEqual(0x05, payload[4], "byte 4");
            Assert.AreEqual(0x06, payload[5], "byte 5");
            Assert.AreEqual(0x07, payload[6], "byte 6");
            Assert.AreEqual(0x08, payload[7], "byte 7 (MSB)");
        }

        [Test]
        [Description("Zero ObjectID encodes as eight zero bytes at the start of the payload.")]
        public void BuildUpdatePayload_ZeroObjectId_LeadsWithEightZeroBytes()
        {
            var payload = TransformPacketBuilder.BuildUpdatePayload(0UL, TransformState.Identity);

            for (int i = 0; i < 8; i++)
                Assert.AreEqual(0, payload[i], $"byte {i} should be 0 for ObjectID=0");
        }

        [Test]
        [Description("max-value ObjectID round-trips correctly through the 8-byte LE encoding.")]
        public void BuildUpdatePayload_MaxObjectId_RoundTripsCorrectly()
        {
            const ulong id = 0xFEDCBA9876543210UL;
            var payload = TransformPacketBuilder.BuildUpdatePayload(id, TransformState.Identity);

            ulong decoded = ReadU64LE(payload, TransformPacketBuilder.OFFSET_OBJECT_ID);
            Assert.AreEqual(id, decoded);
        }

        // ── Position encoding ─────────────────────────────────────────────────

        [Test]
        [Description("Position X/Y/Z are written as f32 LE at bytes 8..19.")]
        public void BuildUpdatePayload_Position_XYZ_WrittenAtCorrectOffset()
        {
            var state   = MakeState(10f, 20f, 30f, 0f, 0f, 0f, 1f, 1f, 1f, 1f);
            var payload = TransformPacketBuilder.BuildUpdatePayload(999UL, state);

            Assert.AreEqual(10f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 0), "pos_x");
            Assert.AreEqual(20f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 4), "pos_y");
            Assert.AreEqual(30f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 8), "pos_z");
        }

        [Test]
        [Description("Negative position values encode correctly (sign bit in f32 MSB).")]
        public void BuildUpdatePayload_NegativePosition_EncodesCorrectly()
        {
            var state   = MakeState(-5.5f, -10f, -0.001f, 0f, 0f, 0f, 1f, 1f, 1f, 1f);
            var payload = TransformPacketBuilder.BuildUpdatePayload(1UL, state);

            Assert.AreEqual(-5.5f,   ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 0), "pos_x");
            Assert.AreEqual(-10f,    ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 4), "pos_y");
            Assert.AreEqual(-0.001f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_POSITION + 8), "pos_z");
        }

        // ── Rotation encoding ─────────────────────────────────────────────────

        [Test]
        [Description("Rotation X/Y/Z/W are written as f32 LE at bytes 20..35.")]
        public void BuildUpdatePayload_Rotation_XYZW_WrittenAtCorrectOffset()
        {
            // Use 90-degree rotation around Y axis: Q(0, sin45, 0, cos45)
            float s = Mathf.Sin(Mathf.PI / 4f);
            float c = Mathf.Cos(Mathf.PI / 4f);
            var state   = MakeState(0f, 0f, 0f, 0f, s, 0f, c, 1f, 1f, 1f);
            var payload = TransformPacketBuilder.BuildUpdatePayload(1UL, state);

            Assert.AreEqual(0f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 0),  1e-6f, "rot_x");
            Assert.AreEqual(s,  ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 4),  1e-6f, "rot_y");
            Assert.AreEqual(0f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 8),  1e-6f, "rot_z");
            Assert.AreEqual(c,  ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 12), 1e-6f, "rot_w");
        }

        [Test]
        [Description("Identity rotation (0,0,0,1) writes W=1 at offset 32.")]
        public void BuildUpdatePayload_IdentityRotation_WIs1AtOffset32()
        {
            var payload = TransformPacketBuilder.BuildUpdatePayload(1UL, TransformState.Identity);

            Assert.AreEqual(0f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 0),  "rot_x");
            Assert.AreEqual(0f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 4),  "rot_y");
            Assert.AreEqual(0f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 8),  "rot_z");
            Assert.AreEqual(1f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_ROTATION + 12), "rot_w");
        }

        // ── Scale encoding ────────────────────────────────────────────────────

        [Test]
        [Description("Scale X/Y/Z are written as f32 LE at bytes 36..47.")]
        public void BuildUpdatePayload_Scale_XYZ_WrittenAtCorrectOffset()
        {
            var state   = MakeState(0f, 0f, 0f, 0f, 0f, 0f, 1f, 3f, 4f, 5f);
            var payload = TransformPacketBuilder.BuildUpdatePayload(1UL, state);

            Assert.AreEqual(3f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_SCALE + 0), "scale_x");
            Assert.AreEqual(4f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_SCALE + 4), "scale_y");
            Assert.AreEqual(5f, ReadF32LE(payload, TransformPacketBuilder.OFFSET_SCALE + 8), "scale_z");
        }

        // ── Full round-trip ───────────────────────────────────────────────────

        [Test]
        [Description("Build then Parse restores the original ObjectID and every transform field.")]
        public void BuildUpdatePayload_RoundTrip_ParseRestoresAllFields()
        {
            const ulong id     = 12345678901UL;
            var         state  = MakeState(1.5f, 2.5f, 3.5f, 0.1f, 0.2f, 0.3f, 0.9f, 2f, 3f, 4f);
            var         payload = TransformPacketBuilder.BuildUpdatePayload(id, state);

            // Verify ObjectID directly (the builder payload is NOT a StateDelta;
            // use ReadU64LE directly instead of TransformPacketParser which expects the StateDelta format).
            Assert.AreEqual(id, ReadU64LE(payload, 0), "ObjectID");

            // Verify all transform fields via direct reads.
            Assert.AreEqual(1.5f, ReadF32LE(payload, 8),  1e-6f, "pos_x");
            Assert.AreEqual(2.5f, ReadF32LE(payload, 12), 1e-6f, "pos_y");
            Assert.AreEqual(3.5f, ReadF32LE(payload, 16), 1e-6f, "pos_z");
            Assert.AreEqual(0.1f, ReadF32LE(payload, 20), 1e-6f, "rot_x");
            Assert.AreEqual(0.2f, ReadF32LE(payload, 24), 1e-6f, "rot_y");
            Assert.AreEqual(0.3f, ReadF32LE(payload, 28), 1e-6f, "rot_z");
            Assert.AreEqual(0.9f, ReadF32LE(payload, 32), 1e-6f, "rot_w");
            Assert.AreEqual(2f,   ReadF32LE(payload, 36), 1e-6f, "scale_x");
            Assert.AreEqual(3f,   ReadF32LE(payload, 40), 1e-6f, "scale_y");
            Assert.AreEqual(4f,   ReadF32LE(payload, 44), 1e-6f, "scale_z");
        }

        // ── Zero state ────────────────────────────────────────────────────────

        [Test]
        [Description("Payload bytes 8..47 are all zero when position, rotation (except W), and scale are zero.")]
        public void BuildUpdatePayload_ZeroPositionAndScale_FieldBytesAreZero()
        {
            // Zero position and scale; rotation = (0,0,0,0) — deliberately not identity
            // to test that no implicit default injection occurs.
            var state   = MakeState(0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f);
            var payload = TransformPacketBuilder.BuildUpdatePayload(0UL, state);

            for (int i = 0; i < 48; i++)
                Assert.AreEqual(0, payload[i], $"payload[{i}] should be 0");
        }
    }
}
