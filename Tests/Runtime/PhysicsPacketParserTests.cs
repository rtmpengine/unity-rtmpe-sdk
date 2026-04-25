// RTMPE SDK — Tests/Runtime/PhysicsPacketParserTests.cs
//
// NUnit tests for PhysicsPacketParser.
// Verifies:
//   • IsPhysics3D / IsPhysics2D type discrimination
//   • TryParsePhysicsState (3-D): guard cases, type-marker enforcement, unknown-
//     bit rejection, truncation detection, header-only, per-field and full
//     roundtrips, and objectId LE decoding
//   • TryParsePhysicsState2D: equivalent coverage for 2-D packets
//   • Disambiguation: each parser rejects payloads produced by the other builder
//
// Roundtrip tests build payloads via PhysicsPacketBuilder so that the parser
// is verified against the canonical wire-format writer rather than hand-crafted
// byte arrays.

using System;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Sync;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("Sync")]
    public class PhysicsPacketParserTests
    {
        // ── IsPhysics3D ───────────────────────────────────────────────────────

        [Test]
        public void IsPhysics3D_NullPayload_ReturnsFalse()
            => Assert.IsFalse(PhysicsPacketParser.IsPhysics3D(null));

        [Test]
        public void IsPhysics3D_TooShort_ReturnsFalse()
            => Assert.IsFalse(PhysicsPacketParser.IsPhysics3D(new byte[8]));

        [Test]
        public void IsPhysics3D_Bit0x40Set_Bit0x80Clear_ReturnsTrue()
        {
            var p = new byte[9];
            p[8] = 0x40;
            Assert.IsTrue(PhysicsPacketParser.IsPhysics3D(p));
        }

        [Test]
        public void IsPhysics3D_BothMarkersSet_ReturnsFalse()
        {
            // 2-D marker present → not a pure 3-D packet
            var p = new byte[9];
            p[8] = 0xC0;
            Assert.IsFalse(PhysicsPacketParser.IsPhysics3D(p));
        }

        [Test]
        public void IsPhysics3D_Only2DMarker_ReturnsFalse()
        {
            var p = new byte[9];
            p[8] = 0x80;
            Assert.IsFalse(PhysicsPacketParser.IsPhysics3D(p));
        }

        [Test]
        public void IsPhysics3D_NoMarker_ReturnsFalse()
        {
            var p = new byte[9];
            p[8] = 0x05;  // data bits only
            Assert.IsFalse(PhysicsPacketParser.IsPhysics3D(p));
        }

        // ── IsPhysics2D ───────────────────────────────────────────────────────

        [Test]
        public void IsPhysics2D_NullPayload_ReturnsFalse()
            => Assert.IsFalse(PhysicsPacketParser.IsPhysics2D(null));

        [Test]
        public void IsPhysics2D_TooShort_ReturnsFalse()
            => Assert.IsFalse(PhysicsPacketParser.IsPhysics2D(new byte[8]));

        [Test]
        public void IsPhysics2D_Bit0x80Set_ReturnsTrue()
        {
            var p = new byte[9];
            p[8] = 0x80;
            Assert.IsTrue(PhysicsPacketParser.IsPhysics2D(p));
        }

        [Test]
        public void IsPhysics2D_BothMarkersSet_ReturnsTrue()
        {
            var p = new byte[9];
            p[8] = 0xC0;
            Assert.IsTrue(PhysicsPacketParser.IsPhysics2D(p));
        }

        [Test]
        public void IsPhysics2D_Only3DMarker_ReturnsFalse()
        {
            var p = new byte[9];
            p[8] = 0x40;
            Assert.IsFalse(PhysicsPacketParser.IsPhysics2D(p));
        }

        // ── TryParsePhysicsState — guard cases ────────────────────────────────

        [Test]
        public void TryParsePhysicsState_NullPayload_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState(null, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState_EmptyPayload_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                Array.Empty<byte>(), out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState_8Bytes_TooShort_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                new byte[8], out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState_No3DMarker_ReturnsFalse()
        {
            // Payload with only a data bit and no type marker
            var p = new byte[9];
            p[8] = PhysicsPacketBuilder.ChangedPosition;  // 0x01, no 0x40
            bool ok = PhysicsPacketParser.TryParsePhysicsState(p, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState_Has2DMarker_ReturnsFalse()
        {
            // Both type markers set → 3-D parser must reject
            var p = new byte[9];
            p[8] = (byte)(PhysicsPacketBuilder.TypeMarker3D | PhysicsPacketBuilder.TypeMarker2D);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(p, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState_UnknownDataBit_ReturnsFalse()
        {
            // Bit 0x20 is outside DataFieldMask (0x1F) and is not a type marker
            var p = new byte[9];
            p[8] = (byte)(PhysicsPacketBuilder.TypeMarker3D | 0x20);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(p, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        // ── TryParsePhysicsState — truncation ─────────────────────────────────

        [Test]
        public void TryParsePhysicsState_TruncatedPosition_ReturnsFalse()
        {
            var full = PhysicsPacketBuilder.BuildPayload(
                1UL, new PhysicsState { Position = Vector3.one },
                PhysicsPacketBuilder.ChangedPosition);
            // Remove the last byte to create a truncated payload
            var cut = new byte[full.Length - 1];
            Array.Copy(full, cut, cut.Length);
            Assert.IsFalse(PhysicsPacketParser.TryParsePhysicsState(cut, out _, out _, out _));
        }

        [Test]
        public void TryParsePhysicsState_TruncatedRotation_ReturnsFalse()
        {
            var full = PhysicsPacketBuilder.BuildPayload(
                1UL, new PhysicsState { Rotation = Quaternion.identity },
                PhysicsPacketBuilder.ChangedRotation);
            var cut = new byte[full.Length - 4];
            Array.Copy(full, cut, cut.Length);
            Assert.IsFalse(PhysicsPacketParser.TryParsePhysicsState(cut, out _, out _, out _));
        }

        [Test]
        public void TryParsePhysicsState_TruncatedSleep_ReturnsFalse()
        {
            var full = PhysicsPacketBuilder.BuildPayload(
                1UL, new PhysicsState { IsSleeping = true },
                PhysicsPacketBuilder.ChangedSleep);
            // Sleep is 1 byte — remove it
            var cut = new byte[full.Length - 1];
            Array.Copy(full, cut, cut.Length);
            Assert.IsFalse(PhysicsPacketParser.TryParsePhysicsState(cut, out _, out _, out _));
        }

        // ── TryParsePhysicsState — header-only ────────────────────────────────

        [Test]
        public void TryParsePhysicsState_HeaderOnly_ReturnsTrue()
        {
            var payload = PhysicsPacketBuilder.BuildPayload(17UL, default, dataMask: 0x00);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out ulong objectId, out byte mask, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(17UL, objectId);
            Assert.AreEqual(PhysicsPacketBuilder.TypeMarker3D, mask,
                "Header-only changedMask must equal TypeMarker3D exactly.");
        }

        // ── TryParsePhysicsState — individual field roundtrips ─────────────────

        [Test]
        public void TryParsePhysicsState_Position_RoundtripCorrect()
        {
            var state   = new PhysicsState { Position = new Vector3(1.5f, -2.25f, 100f) };
            var payload = PhysicsPacketBuilder.BuildPayload(
                42UL, state, PhysicsPacketBuilder.ChangedPosition);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out ulong objectId, out byte mask, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(42UL, objectId);
            Assert.AreEqual(
                (byte)(PhysicsPacketBuilder.TypeMarker3D | PhysicsPacketBuilder.ChangedPosition),
                mask);
            Assert.AreEqual(1.5f,   result.Position.x, 1e-6f, "pos_x");
            Assert.AreEqual(-2.25f, result.Position.y, 1e-6f, "pos_y");
            Assert.AreEqual(100f,   result.Position.z, 1e-6f, "pos_z");
        }

        [Test]
        public void TryParsePhysicsState_Rotation_RoundtripCorrect()
        {
            var q     = new Quaternion(0.1f, 0.2f, 0.3f, 0.9274f);
            var state = new PhysicsState { Rotation = q };
            var payload = PhysicsPacketBuilder.BuildPayload(
                7UL, state, PhysicsPacketBuilder.ChangedRotation);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out _, out _, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(q.x, result.Rotation.x, 1e-6f, "rot_x");
            Assert.AreEqual(q.y, result.Rotation.y, 1e-6f, "rot_y");
            Assert.AreEqual(q.z, result.Rotation.z, 1e-6f, "rot_z");
            Assert.AreEqual(q.w, result.Rotation.w, 1e-6f, "rot_w");
        }

        [Test]
        public void TryParsePhysicsState_Velocity_RoundtripCorrect()
        {
            var state   = new PhysicsState { Velocity = new Vector3(3f, -1.5f, 0.75f) };
            var payload = PhysicsPacketBuilder.BuildPayload(
                0UL, state, PhysicsPacketBuilder.ChangedVelocity);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out _, out _, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(3f,    result.Velocity.x, 1e-6f, "vel_x");
            Assert.AreEqual(-1.5f, result.Velocity.y, 1e-6f, "vel_y");
            Assert.AreEqual(0.75f, result.Velocity.z, 1e-6f, "vel_z");
        }

        [Test]
        public void TryParsePhysicsState_AngularVelocity_RoundtripCorrect()
        {
            var state   = new PhysicsState { AngularVelocity = new Vector3(0.1f, 0.2f, 0.3f) };
            var payload = PhysicsPacketBuilder.BuildPayload(
                0UL, state, PhysicsPacketBuilder.ChangedAngularVelocity);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out _, out _, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(0.1f, result.AngularVelocity.x, 1e-6f, "ang_x");
            Assert.AreEqual(0.2f, result.AngularVelocity.y, 1e-6f, "ang_y");
            Assert.AreEqual(0.3f, result.AngularVelocity.z, 1e-6f, "ang_z");
        }

        [Test]
        public void TryParsePhysicsState_SleepTrue_RoundtripCorrect()
        {
            var payload = PhysicsPacketBuilder.BuildPayload(
                0UL, new PhysicsState { IsSleeping = true },
                PhysicsPacketBuilder.ChangedSleep);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out _, out _, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.IsTrue(result.IsSleeping);
        }

        [Test]
        public void TryParsePhysicsState_SleepFalse_RoundtripCorrect()
        {
            var payload = PhysicsPacketBuilder.BuildPayload(
                0UL, new PhysicsState { IsSleeping = false },
                PhysicsPacketBuilder.ChangedSleep);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out _, out _, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.IsFalse(result.IsSleeping);
        }

        // ── TryParsePhysicsState — all-fields roundtrip ───────────────────────

        [Test]
        public void TryParsePhysicsState_AllFields_RoundtripCorrect()
        {
            byte allMask = PhysicsPacketBuilder.ChangedPosition
                         | PhysicsPacketBuilder.ChangedRotation
                         | PhysicsPacketBuilder.ChangedVelocity
                         | PhysicsPacketBuilder.ChangedAngularVelocity
                         | PhysicsPacketBuilder.ChangedSleep;

            var sent = new PhysicsState
            {
                Position        = new Vector3(5f, -3f, 2f),
                Rotation        = new Quaternion(0f, 0f, 0f, 1f),
                Velocity        = new Vector3(1f, 2f, 3f),
                AngularVelocity = new Vector3(0.1f, 0f, -0.2f),
                IsSleeping      = false,
            };

            var payload = PhysicsPacketBuilder.BuildPayload(99UL, sent, allMask);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out ulong objectId, out byte mask, out PhysicsState result);

            Assert.IsTrue(ok);
            Assert.AreEqual(99UL, objectId);
            Assert.AreEqual((byte)(PhysicsPacketBuilder.TypeMarker3D | allMask), mask);

            Assert.AreEqual(sent.Position.x,        result.Position.x,        1e-6f, "pos_x");
            Assert.AreEqual(sent.Position.y,        result.Position.y,        1e-6f, "pos_y");
            Assert.AreEqual(sent.Position.z,        result.Position.z,        1e-6f, "pos_z");
            Assert.AreEqual(sent.Rotation.x,        result.Rotation.x,        1e-6f, "rot_x");
            Assert.AreEqual(sent.Rotation.y,        result.Rotation.y,        1e-6f, "rot_y");
            Assert.AreEqual(sent.Rotation.z,        result.Rotation.z,        1e-6f, "rot_z");
            Assert.AreEqual(sent.Rotation.w,        result.Rotation.w,        1e-6f, "rot_w");
            Assert.AreEqual(sent.Velocity.x,        result.Velocity.x,        1e-6f, "vel_x");
            Assert.AreEqual(sent.Velocity.y,        result.Velocity.y,        1e-6f, "vel_y");
            Assert.AreEqual(sent.Velocity.z,        result.Velocity.z,        1e-6f, "vel_z");
            Assert.AreEqual(sent.AngularVelocity.x, result.AngularVelocity.x, 1e-6f, "ang_x");
            Assert.AreEqual(sent.AngularVelocity.y, result.AngularVelocity.y, 1e-6f, "ang_y");
            Assert.AreEqual(sent.AngularVelocity.z, result.AngularVelocity.z, 1e-6f, "ang_z");
            Assert.AreEqual(sent.IsSleeping,        result.IsSleeping,               "sleep");
        }

        // ── TryParsePhysicsState — objectId LE decoding ───────────────────────

        [Test]
        public void TryParsePhysicsState_ObjectId_DecodedLittleEndian()
        {
            const ulong expected = 0xFEDCBA9876543210UL;
            var payload = PhysicsPacketBuilder.BuildPayload(expected, default, dataMask: 0x00);

            bool ok = PhysicsPacketParser.TryParsePhysicsState(
                payload, out ulong objectId, out _, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(expected, objectId);
        }

        // ═════════════════════════════════════════════════════════════════════
        // TryParsePhysicsState2D
        // ═════════════════════════════════════════════════════════════════════

        [Test]
        public void TryParsePhysicsState2D_NullPayload_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(null, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState2D_EmptyPayload_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                Array.Empty<byte>(), out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState2D_TooShort_ReturnsFalse()
        {
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                new byte[8], out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState2D_No2DMarker_ReturnsFalse()
        {
            // Only the 3-D marker set → 2-D parser must reject
            var p = new byte[9];
            p[8] = PhysicsPacketBuilder.TypeMarker3D;
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(p, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState2D_UnknownDataBit_ReturnsFalse()
        {
            // Bit 0x20 is outside DataFieldMask (0x1F)
            var p = new byte[9];
            p[8] = (byte)(PhysicsPacketBuilder.TypeMarker2D | 0x20);
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(p, out _, out _, out _);
            Assert.IsFalse(ok);
        }

        [Test]
        public void TryParsePhysicsState2D_TruncatedPosition_ReturnsFalse()
        {
            var full = PhysicsPacketBuilder.Build2DPayload(
                1UL, new PhysicsState2D { Position = Vector2.one },
                PhysicsPacketBuilder.ChangedPosition);
            var cut = new byte[full.Length - 2];
            Array.Copy(full, cut, cut.Length);
            Assert.IsFalse(PhysicsPacketParser.TryParsePhysicsState2D(cut, out _, out _, out _));
        }

        [Test]
        public void TryParsePhysicsState2D_TruncatedRotation_ReturnsFalse()
        {
            var full = PhysicsPacketBuilder.Build2DPayload(
                1UL, new PhysicsState2D { Rotation = 90f },
                PhysicsPacketBuilder.ChangedRotation);
            var cut = new byte[full.Length - 3];
            Array.Copy(full, cut, cut.Length);
            Assert.IsFalse(PhysicsPacketParser.TryParsePhysicsState2D(cut, out _, out _, out _));
        }

        [Test]
        public void TryParsePhysicsState2D_HeaderOnly_ReturnsTrue()
        {
            var payload = PhysicsPacketBuilder.Build2DPayload(5UL, default, dataMask: 0x00);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out ulong objectId, out byte mask, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(5UL, objectId);
            Assert.AreEqual(PhysicsPacketBuilder.TypeMarker2D, mask);
        }

        [Test]
        public void TryParsePhysicsState2D_Position_RoundtripCorrect()
        {
            var state   = new PhysicsState2D { Position = new Vector2(3.5f, -7f) };
            var payload = PhysicsPacketBuilder.Build2DPayload(
                10UL, state, PhysicsPacketBuilder.ChangedPosition);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out ulong objectId, out _, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.AreEqual(10UL, objectId);
            Assert.AreEqual(3.5f, result.Position.x, 1e-6f, "pos_x");
            Assert.AreEqual(-7f,  result.Position.y, 1e-6f, "pos_y");
        }

        [Test]
        public void TryParsePhysicsState2D_Rotation_RoundtripCorrect()
        {
            var state   = new PhysicsState2D { Rotation = 135f };
            var payload = PhysicsPacketBuilder.Build2DPayload(
                0UL, state, PhysicsPacketBuilder.ChangedRotation);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out _, out _, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.AreEqual(135f, result.Rotation, 1e-4f);
        }

        [Test]
        public void TryParsePhysicsState2D_Velocity_RoundtripCorrect()
        {
            var state   = new PhysicsState2D { Velocity = new Vector2(-5f, 2.5f) };
            var payload = PhysicsPacketBuilder.Build2DPayload(
                0UL, state, PhysicsPacketBuilder.ChangedVelocity);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out _, out _, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.AreEqual(-5f,  result.Velocity.x, 1e-6f, "vel_x");
            Assert.AreEqual(2.5f, result.Velocity.y, 1e-6f, "vel_y");
        }

        [Test]
        public void TryParsePhysicsState2D_AngularVelocity_RoundtripCorrect()
        {
            var state   = new PhysicsState2D { AngularVelocity = 90f };
            var payload = PhysicsPacketBuilder.Build2DPayload(
                0UL, state, PhysicsPacketBuilder.ChangedAngularVelocity);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out _, out _, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.AreEqual(90f, result.AngularVelocity, 1e-4f);
        }

        [Test]
        public void TryParsePhysicsState2D_SleepTrue_RoundtripCorrect()
        {
            var payload = PhysicsPacketBuilder.Build2DPayload(
                0UL, new PhysicsState2D { IsSleeping = true },
                PhysicsPacketBuilder.ChangedSleep);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out _, out _, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.IsTrue(result.IsSleeping);
        }

        [Test]
        public void TryParsePhysicsState2D_AllFields_RoundtripCorrect()
        {
            byte allMask = PhysicsPacketBuilder.ChangedPosition
                         | PhysicsPacketBuilder.ChangedRotation
                         | PhysicsPacketBuilder.ChangedVelocity
                         | PhysicsPacketBuilder.ChangedAngularVelocity
                         | PhysicsPacketBuilder.ChangedSleep;

            var sent = new PhysicsState2D
            {
                Position        = new Vector2(10f, -5f),
                Rotation        = 270f,
                Velocity        = new Vector2(-3f, 1.5f),
                AngularVelocity = -45f,
                IsSleeping      = false,
            };

            var payload = PhysicsPacketBuilder.Build2DPayload(55UL, sent, allMask);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out ulong objectId, out byte mask, out PhysicsState2D result);

            Assert.IsTrue(ok);
            Assert.AreEqual(55UL, objectId);
            Assert.AreEqual((byte)(PhysicsPacketBuilder.TypeMarker2D | allMask), mask);

            Assert.AreEqual(sent.Position.x,    result.Position.x,    1e-6f, "pos_x");
            Assert.AreEqual(sent.Position.y,    result.Position.y,    1e-6f, "pos_y");
            Assert.AreEqual(sent.Rotation,      result.Rotation,      1e-4f, "rotation");
            Assert.AreEqual(sent.Velocity.x,    result.Velocity.x,    1e-6f, "vel_x");
            Assert.AreEqual(sent.Velocity.y,    result.Velocity.y,    1e-6f, "vel_y");
            Assert.AreEqual(sent.AngularVelocity, result.AngularVelocity, 1e-4f, "ang_vel");
            Assert.AreEqual(sent.IsSleeping,    result.IsSleeping,          "sleep");
        }

        [Test]
        public void TryParsePhysicsState2D_ObjectId_DecodedLittleEndian()
        {
            const ulong expected = 0xDEADBEEFCAFEBABEUL;
            var payload = PhysicsPacketBuilder.Build2DPayload(expected, default, dataMask: 0x00);

            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(
                payload, out ulong objectId, out _, out _);

            Assert.IsTrue(ok);
            Assert.AreEqual(expected, objectId);
        }

        // ── Disambiguation: parsers reject opposite-type payloads ─────────────

        [Test]
        public void TryParsePhysicsState_Rejects2DPayload()
        {
            var payload = PhysicsPacketBuilder.Build2DPayload(1UL, default, dataMask: 0x00);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(payload, out _, out _, out _);
            Assert.IsFalse(ok, "3-D parser must reject a payload built by Build2DPayload.");
        }

        [Test]
        public void TryParsePhysicsState2D_Rejects3DPayload()
        {
            var payload = PhysicsPacketBuilder.BuildPayload(1UL, default, dataMask: 0x00);
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(payload, out _, out _, out _);
            Assert.IsFalse(ok, "2-D parser must reject a payload built by BuildPayload.");
        }

        // ── NaN / Inf rejection (audit fix C2-004) ────────────────────────────
        //
        // Before this fix, the physics parser passed crafted IEEE 754 NaN/Inf
        // bit patterns straight through to Rigidbody / Rigidbody2D, which
        // silently destabilises PhysX and PhysX2D and can crash the engine.
        // The 3-D and 2-D parsers must reject any non-finite float in any
        // declared field.

        [Test]
        public void TryParsePhysicsState_NaNPosition_ReturnsFalse()
        {
            var state = new PhysicsState { Position = new Vector3(float.NaN, 0f, 0f), Rotation = Quaternion.identity };
            var payload = PhysicsPacketBuilder.BuildPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedPosition);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(payload, out _, out _, out _);
            Assert.IsFalse(ok, "3-D parser must reject NaN position.");
        }

        [Test]
        public void TryParsePhysicsState_InfVelocity_ReturnsFalse()
        {
            var state = new PhysicsState
            {
                Velocity = new Vector3(float.PositiveInfinity, 0f, 0f),
                Rotation = Quaternion.identity,
            };
            var payload = PhysicsPacketBuilder.BuildPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedVelocity);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(payload, out _, out _, out _);
            Assert.IsFalse(ok, "3-D parser must reject +Inf velocity.");
        }

        [Test]
        public void TryParsePhysicsState_NaNAngularVelocity_ReturnsFalse()
        {
            var state = new PhysicsState
            {
                AngularVelocity = new Vector3(0f, float.NaN, 0f),
                Rotation        = Quaternion.identity,
            };
            var payload = PhysicsPacketBuilder.BuildPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedAngularVelocity);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(payload, out _, out _, out _);
            Assert.IsFalse(ok, "3-D parser must reject NaN angular velocity.");
        }

        [Test]
        public void TryParsePhysicsState_NonUnitQuaternion_ReturnsFalse()
        {
            var state = new PhysicsState
            {
                Rotation = new Quaternion(0f, 0f, 0f, 2f), // |q|² = 4 — well above 1.1
            };
            var payload = PhysicsPacketBuilder.BuildPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedRotation);
            bool ok = PhysicsPacketParser.TryParsePhysicsState(payload, out _, out _, out _);
            Assert.IsFalse(ok, "3-D parser must reject grossly non-unit quaternion.");
        }

        [Test]
        public void TryParsePhysicsState2D_NaNPosition_ReturnsFalse()
        {
            var state = new PhysicsState2D { Position = new Vector2(float.NaN, 0f) };
            var payload = PhysicsPacketBuilder.Build2DPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedPosition);
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(payload, out _, out _, out _);
            Assert.IsFalse(ok, "2-D parser must reject NaN position.");
        }

        [Test]
        public void TryParsePhysicsState2D_InfRotation_ReturnsFalse()
        {
            var state = new PhysicsState2D { Rotation = float.NegativeInfinity };
            var payload = PhysicsPacketBuilder.Build2DPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedRotation);
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(payload, out _, out _, out _);
            Assert.IsFalse(ok, "2-D parser must reject -Inf rotation.");
        }

        [Test]
        public void TryParsePhysicsState2D_NaNAngularVelocity_ReturnsFalse()
        {
            var state = new PhysicsState2D { AngularVelocity = float.NaN };
            var payload = PhysicsPacketBuilder.Build2DPayload(1UL, state,
                dataMask: PhysicsPacketBuilder.ChangedAngularVelocity);
            bool ok = PhysicsPacketParser.TryParsePhysicsState2D(payload, out _, out _, out _);
            Assert.IsFalse(ok, "2-D parser must reject NaN angular velocity.");
        }
    }
}
