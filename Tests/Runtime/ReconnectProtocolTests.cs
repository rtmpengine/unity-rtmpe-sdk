// RTMPE SDK — Tests/Runtime/ReconnectProtocolTests.cs
//
// **N-1** — Unit coverage for the reconnect protocol client-side primitives.
// These tests are pure-C# (no Unity runtime required) and focus on:
//
//  T-N1-10  `PacketBuilder.BuildReconnectInit` emits the correct wire format.
//  T-N1-11  The gateway-level length cap (128 B) is enforced client-side too.
//  T-N1-12  Empty / null tokens are rejected with `ArgumentException` instead
//           of silently sending an invalid packet.
//  packet-type  `PacketType.ReconnectInit` is the agreed 0x09 byte and
//           `ReconnectAck` is 0x0A — any future protocol drift trips the test.
//
// The higher-level state-machine assertions (T-N1-09: transitions through
// Reconnecting, T-N1-11 manager: ClearSessionData preserves token when asked)
// require Unity's PlayMode harness + UDP transport and live in
// NetworkManagerTests under `[UnityTest]`.  This file is a pure EditMode
// companion — it compiles and runs without Unity Engine dependencies beyond
// the protocol/builder layer.

using System;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Protocol;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Reconnect")]
    public class ReconnectProtocolTests
    {
        // ── packet-type enum stays in sync with the Rust gateway ─────────────

        [Test]
        public void PacketType_ReconnectInit_Is_0x09()
        {
            Assert.AreEqual((byte)0x09, (byte)PacketType.ReconnectInit,
                "PacketType.ReconnectInit must be 0x09 (must match " +
                "modules/gateway/src/packet/header.rs).");
        }

        [Test]
        public void PacketType_ReconnectAck_Is_0x0A()
        {
            Assert.AreEqual((byte)0x0A, (byte)PacketType.ReconnectAck,
                "PacketType.ReconnectAck must be 0x0A (must match " +
                "modules/gateway/src/packet/header.rs).");
        }

        // ── T-N1-10 — Wire format matches gateway parser expectations ────────

        [Test]
        public void BuildReconnectInit_EmitsTokenLengthPrefixedPayload()
        {
            var token = "abcd-1234";
            var builder = new PacketBuilder();
            byte[] packet = builder.BuildReconnectInitWithoutProof(token);

            // Header = 13 bytes; then [token_len:2 LE][token_bytes]
            Assert.AreEqual(
                PacketProtocol.HEADER_SIZE + 2 + token.Length,
                packet.Length,
                "total length = header + 2-byte len + token UTF-8 bytes");

            Assert.AreEqual((byte)PacketType.ReconnectInit,
                packet[PacketProtocol.OFFSET_TYPE],
                "type byte must be 0x09");

            // payload_len field (u32 LE) must equal 2 + token.Length
            uint payloadLen =
                (uint)packet[PacketProtocol.OFFSET_PAYLOAD_LEN] |
                ((uint)packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] << 8) |
                ((uint)packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] << 16) |
                ((uint)packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] << 24);
            Assert.AreEqual((uint)(2 + token.Length), payloadLen);

            // Payload: [token_len:2 LE][token]
            int payloadStart = PacketProtocol.HEADER_SIZE;
            ushort tokenLen =
                (ushort)(packet[payloadStart] | (packet[payloadStart + 1] << 8));
            Assert.AreEqual(token.Length, tokenLen,
                "token_len prefix must match UTF-8 byte count");

            var roundTrip = System.Text.Encoding.UTF8.GetString(
                packet,
                payloadStart + 2,
                tokenLen);
            Assert.AreEqual(token, roundTrip);
        }

        [Test]
        public void BuildReconnectInit_IncrementsSequenceNumber()
        {
            var builder = new PacketBuilder();
            byte[] a = builder.BuildReconnectInitWithoutProof("tok-a");
            byte[] b = builder.BuildReconnectInitWithoutProof("tok-b");

            uint seqA = ReadSequence(a);
            uint seqB = ReadSequence(b);
            Assert.AreEqual(seqA + 1, seqB,
                "each build call must increment the sequence counter");
        }

        // ── Guardrails — misuse fails loudly ─────────────────────────────────

        [Test]
        public void BuildReconnectInit_Rejects_NullToken()
        {
            var builder = new PacketBuilder();
            Assert.Throws<ArgumentException>(() => builder.BuildReconnectInitWithoutProof(null));
        }

        [Test]
        public void BuildReconnectInit_Rejects_EmptyToken()
        {
            var builder = new PacketBuilder();
            Assert.Throws<ArgumentException>(() => builder.BuildReconnectInitWithoutProof(""));
        }

        // ── T-N1-11 — Mirrors the gateway's 128-byte cap ─────────────────────

        [Test]
        public void BuildReconnectInit_Rejects_TokenLongerThan128Bytes()
        {
            var builder = new PacketBuilder();
            // 129 ASCII chars → 129 UTF-8 bytes.
            string huge = new string('x', 129);
            Assert.Throws<ArgumentException>(() => builder.BuildReconnectInitWithoutProof(huge));
        }

        [Test]
        public void BuildReconnectInit_Accepts_TokenAtExactly128Bytes()
        {
            var builder = new PacketBuilder();
            string atLimit = new string('x', 128);
            Assert.DoesNotThrow(() => builder.BuildReconnectInitWithoutProof(atLimit),
                "128-byte token is on the boundary — must be accepted");
        }

        // ── Bounded-retry settings + event-shape exposure ────────────────────

        [Test]
        public void NetworkSettings_DefaultMaxReconnectAttempts_IsBounded()
        {
            // The bounded-retry budget MUST default to a small positive value
            // so an unconfigured asset still escapes the indefinite-Reconnecting
            // failure mode.  Range guarded by [Range(1, 50)] in the inspector.
            var s = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                Assert.GreaterOrEqual(s.maxReconnectAttempts, 1,
                    "default budget must be at least 1 attempt");
                Assert.LessOrEqual(s.maxReconnectAttempts, 50,
                    "default budget must respect inspector cap");
            }
            finally { ScriptableObject.DestroyImmediate(s); }
        }

        [Test]
        public void NetworkSettings_MaxReconnectAttempts_IsConfigurable()
        {
            var s = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                s.maxReconnectAttempts = 7;
                Assert.AreEqual(7, s.maxReconnectAttempts);
            }
            finally { ScriptableObject.DestroyImmediate(s); }
        }

        [Test]
        public void NetworkManager_OnReconnectFailed_EventIsExposed()
        {
            // The bounded-retry surface introduces a new event.  This test
            // documents its public visibility and signature so a refactor
            // that accidentally drops the event will trip CI.
            var ev = typeof(NetworkManager).GetEvent("OnReconnectFailed");
            Assert.NotNull(ev, "OnReconnectFailed event must be public on NetworkManager");
            Assert.AreEqual(typeof(Action<int>), ev.EventHandlerType,
                "OnReconnectFailed must carry Action<int> (attempts made)");
        }

        // ── Helper ───────────────────────────────────────────────────────────

        private static uint ReadSequence(byte[] packet)
        {
            int off = PacketProtocol.OFFSET_SEQUENCE;
            return (uint)packet[off]
                 | ((uint)packet[off + 1] << 8)
                 | ((uint)packet[off + 2] << 16)
                 | ((uint)packet[off + 3] << 24);
        }
    }
}
