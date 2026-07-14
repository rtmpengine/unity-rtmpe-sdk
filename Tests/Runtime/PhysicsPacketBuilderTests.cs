// RTMPE SDK — Tests/Runtime/PhysicsPacketBuilderTests.cs
//
// Unit tests for PhysicsPacketBuilder: verifies that BuildPayload and
// Build2DPayload produce byte arrays with the correct size, correct little-
// endian encoding, and correct type-marker bits.
//
// Tests are pure-logic (no MonoBehaviour, no scene) and run in NUnit without
// any Play Mode requirement.

using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    public class PhysicsPacketBuilderTests
    {
        // ── 3-D BuildPayload ──────────────────────────────────────────────────

        [Test]
        public void BuildPayload_HeaderOnly_ProducesNineBytes()
        {
            // dataMask = 0 → only the header (objectId + changedMask) is written.
            var buf = PhysicsPacketBuilder.BuildPayload(objectId: 0, state: default, dataMask: 0x00);

            Assert.AreEqual(PhysicsPacketBuilder.PayloadMinSize, buf.Length,
                "Payload with no data fields must be exactly PayloadMinSize bytes.");
        }

        [Test]
        public void BuildPayload_TypeMarker3D_AlwaysSet()
        {
            var buf = PhysicsPacketBuilder.BuildPayload(objectId: 1, state: default, dataMask: 0x00);

            byte changedMask = buf[8];
            Assert.IsTrue((changedMask & PhysicsPacketBuilder.TypeMarker3D) != 0,
                "TypeMarker3D (0x40) must always be set in 3-D physics payloads.");
        }

        [Test]
        public void BuildPayload_TypeMarker2D_NeverSet()
        {
            var buf = PhysicsPacketBuilder.BuildPayload(objectId: 1, state: default, dataMask: 0xFF);

            byte changedMask = buf[8];
            Assert.IsTrue((changedMask & PhysicsPacketBuilder.TypeMarker2D) == 0,
                "TypeMarker2D (0x80) must never be set in 3-D physics payloads.");
        }

        [Test]
        public void BuildPayload_AllFields_ProducesMaxSize()
        {
            // Header(9) + position(12) + rotation(16) + velocity(12) + angularVelocity(12) + sleep(1) = 62
            const int expectedMax = 9 + 12 + 16 + 12 + 12 + 1;
            byte allFields = PhysicsPacketBuilder.ChangedPosition
                           | PhysicsPacketBuilder.ChangedRotation
                           | PhysicsPacketBuilder.ChangedVelocity
                           | PhysicsPacketBuilder.ChangedAngularVelocity
                           | PhysicsPacketBuilder.ChangedSleep;
            var state = new PhysicsState
            {
                Position        = new Vector3(1f, 2f, 3f),
                Rotation        = new Quaternion(0.1f, 0.2f, 0.3f, 0.9f),
                Velocity        = new Vector3(4f, 5f, 6f),
                AngularVelocity = new Vector3(0.1f, 0.2f, 0.3f),
                IsSleeping      = false,
            };

            var buf = PhysicsPacketBuilder.BuildPayload(objectId: 42, state: state, dataMask: allFields);

            Assert.AreEqual(expectedMax, buf.Length, "All fields → max payload size must be 62 bytes.");
        }

        [Test]
        public void BuildPayload_ObjectId_EncodedLittleEndian()
        {
            const ulong testId = 0x0102030405060708UL;
            var buf = PhysicsPacketBuilder.BuildPayload(testId, default, dataMask: 0x00);

            Assert.AreEqual(0x08, buf[0], "byte[0] must be the low byte of objectId.");
            Assert.AreEqual(0x07, buf[1]);
            Assert.AreEqual(0x06, buf[2]);
            Assert.AreEqual(0x05, buf[3]);
            Assert.AreEqual(0x04, buf[4]);
            Assert.AreEqual(0x03, buf[5]);
            Assert.AreEqual(0x02, buf[6]);
            Assert.AreEqual(0x01, buf[7], "byte[7] must be the high byte of objectId.");
        }

        [Test]
        public void BuildPayload_Position_EncodedAtOffset9()
        {
            var state = new PhysicsState { Position = new Vector3(1f, 0f, 0f) };
            var buf = PhysicsPacketBuilder.BuildPayload(0, state,
                dataMask: PhysicsPacketBuilder.ChangedPosition);

            // offset 9: position.x as f32 LE → 1.0f = 0x3F800000
            Assert.AreEqual(0x00, buf[9],  "position.x LE byte[0]");
            Assert.AreEqual(0x00, buf[10], "position.x LE byte[1]");
            Assert.AreEqual(0x80, buf[11], "position.x LE byte[2]");
            Assert.AreEqual(0x3F, buf[12], "position.x LE byte[3]");
        }

        [Test]
        public void BuildPayload_SleepFlagTrue_WritesOneAtSleepByte()
        {
            byte allBut = PhysicsPacketBuilder.ChangedSleep;
            var state = new PhysicsState { IsSleeping = true };
            var buf = PhysicsPacketBuilder.BuildPayload(0, state, dataMask: allBut);

            // Only sleep in payload: header(9) + sleep(1) = 10 bytes total.
            // Sleep byte is at index 9.
            Assert.AreEqual(10, buf.Length);
            Assert.AreEqual(0x01, buf[9], "IsSleeping=true must write 0x01.");
        }

        [Test]
        public void BuildPayload_SleepFlagFalse_WritesZeroAtSleepByte()
        {
            var state = new PhysicsState { IsSleeping = false };
            var buf = PhysicsPacketBuilder.BuildPayload(0, state,
                dataMask: PhysicsPacketBuilder.ChangedSleep);

            Assert.AreEqual(0x00, buf[9], "IsSleeping=false must write 0x00.");
        }

        [Test]
        public void BuildPayload_HighDataBitsStripped()
        {
            // Passing bits > DataFieldMask should be silently cleared (only bits 0x01-0x1F kept).
            var buf = PhysicsPacketBuilder.BuildPayload(0, default, dataMask: 0xFF);
            byte changedMask = buf[8];

            // After stripping: data bits = 0x1F; type marker = 0x40 → changedMask = 0x5F
            Assert.AreEqual(0x5F, changedMask,
                "High bits outside DataFieldMask must be stripped; TypeMarker3D (0x40) always added.");
        }

        // ── 2-D Build2DPayload ────────────────────────────────────────────────

        [Test]
        public void Build2DPayload_HeaderOnly_ProducesNineBytes()
        {
            var buf = PhysicsPacketBuilder.Build2DPayload(0, default, dataMask: 0x00);
            Assert.AreEqual(PhysicsPacketBuilder.PayloadMinSize, buf.Length);
        }

        [Test]
        public void Build2DPayload_TypeMarker2D_AlwaysSet()
        {
            var buf = PhysicsPacketBuilder.Build2DPayload(1, default, dataMask: 0x00);
            byte changedMask = buf[8];
            Assert.IsTrue((changedMask & PhysicsPacketBuilder.TypeMarker2D) != 0,
                "TypeMarker2D (0x80) must always be set in 2-D physics payloads.");
        }

        [Test]
        public void Build2DPayload_AllFields_ProducesMaxSize()
        {
            // Header(9) + position(8) + rotation(4) + velocity(8) + angularVelocity(4) + sleep(1) = 34
            const int expectedMax = 9 + 8 + 4 + 8 + 4 + 1;
            byte allFields = PhysicsPacketBuilder.ChangedPosition
                           | PhysicsPacketBuilder.ChangedRotation
                           | PhysicsPacketBuilder.ChangedVelocity
                           | PhysicsPacketBuilder.ChangedAngularVelocity
                           | PhysicsPacketBuilder.ChangedSleep;
            var state = new PhysicsState2D
            {
                Position        = new Vector2(1f, 2f),
                Rotation        = 45f,
                Velocity        = new Vector2(3f, 4f),
                AngularVelocity = 90f,
                IsSleeping      = false,
            };

            var buf = PhysicsPacketBuilder.Build2DPayload(7, state, dataMask: allFields);

            Assert.AreEqual(expectedMax, buf.Length, "All 2-D fields → max payload size must be 34 bytes.");
        }

        [Test]
        public void Build2DPayload_ObjectId_EncodedLittleEndian()
        {
            const ulong testId = 0xDEADBEEFCAFEBABEUL;
            var buf = PhysicsPacketBuilder.Build2DPayload(testId, default, dataMask: 0x00);

            Assert.AreEqual(0xBE, buf[0]);
            Assert.AreEqual(0xBA, buf[1]);
            Assert.AreEqual(0xFE, buf[2]);
            Assert.AreEqual(0xCA, buf[3]);
            Assert.AreEqual(0xEF, buf[4]);
            Assert.AreEqual(0xBE, buf[5]);
            Assert.AreEqual(0xAD, buf[6]);
            Assert.AreEqual(0xDE, buf[7]);
        }

        [Test]
        public void Build2DPayload_Rotation_EncodedAsF32()
        {
            // 45.0f as little-endian IEEE 754 = 0x42340000
            var state = new PhysicsState2D { Rotation = 45f };
            var buf = PhysicsPacketBuilder.Build2DPayload(0, state,
                dataMask: PhysicsPacketBuilder.ChangedRotation);

            // rotation at offset 9 (only field in payload)
            Assert.AreEqual(0x00, buf[9],  "45f LE byte[0]");
            Assert.AreEqual(0x00, buf[10], "45f LE byte[1]");
            Assert.AreEqual(0x34, buf[11], "45f LE byte[2]");
            Assert.AreEqual(0x42, buf[12], "45f LE byte[3]");
        }

        [Test]
        public void Build2DPayload_PositionThenRotation_CorrectOffsets()
        {
            var state = new PhysicsState2D
            {
                Position = new Vector2(1f, 0f),
                Rotation = 0f,
            };
            byte mask = (byte)(PhysicsPacketBuilder.ChangedPosition | PhysicsPacketBuilder.ChangedRotation);
            var buf = PhysicsPacketBuilder.Build2DPayload(0, state, dataMask: mask);

            // Header: 9 bytes.
            // Position: 8 bytes (offset 9–16).
            // Rotation: 4 bytes (offset 17–20).
            Assert.AreEqual(9 + 8 + 4, buf.Length);
            // Position.x = 1.0f at byte 9 → 0x3F800000 LE
            Assert.AreEqual(0x00, buf[9]);
            Assert.AreEqual(0x00, buf[10]);
            Assert.AreEqual(0x80, buf[11]);
            Assert.AreEqual(0x3F, buf[12]);
        }
    }
}
