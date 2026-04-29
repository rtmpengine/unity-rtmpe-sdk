// RTMPE SDK — Tests/Runtime/RpcSecurityTests.cs
//
// NUnit Edit-Mode tests covering the inbound RPC trust gate:
//
//  • RpcTypeRegistry: explicit-registration policy and AppDomain-scan
//    opt-in; unregistered type names must NOT resolve.
//  • EnhancedRpcPacketParser: target-byte enum check, sender-id policy,
//    and object-id sanity hook.
//  • RpcSerializer: rejected (unregistered) INetworkSerializable type
//    names surface as null parameters and the parser still advances
//    past the payload bytes so subsequent params parse cleanly.
//
// These tests intentionally do not exercise full NetworkBehaviour
// dispatch — that path requires a Unity scene and is covered by the
// existing PlayMode tests.  The purpose here is to lock down the
// payload-level trust gate that runs before any user code is reached.

using NUnit.Framework;
using RTMPE.Rpc;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("RpcSecurity")]
    public class RpcSecurityTests
    {
        // A registered, attribute-marked payload type used to exercise the
        // happy-path and to assert that registration is the load-bearing
        // gate (not the attribute alone — Resolve() must see a Register
        // call OR the AppDomain scan flag must be enabled).
        [RtmpeRpcSerializable]
        public struct AllowedPayload : INetworkSerializable
        {
            public int Value;
            public void NetworkSerialize(IRtmpeWriter writer) { writer.WriteInt32(Value); }
            public void NetworkDeserialize(IRtmpeReader reader) { Value = reader.ReadInt32(); }
        }

        // A type that implements INetworkSerializable but is NOT marked
        // with [RtmpeRpcSerializable] and is NOT explicitly registered.
        // Under the secure-by-default policy this type must never resolve
        // from a wire-supplied type name.
        public struct ForbiddenPayload : INetworkSerializable
        {
            public int Value;
            public void NetworkSerialize(IRtmpeWriter writer) { writer.WriteInt32(Value); }
            public void NetworkDeserialize(IRtmpeReader reader) { Value = reader.ReadInt32(); }
        }

        [SetUp]
        public void ResetState()
        {
            // Each test starts from an empty registry and a no-hook
            // verifier so policy interactions are isolated.
            RpcTypeRegistry.ResetForTests();
            EnhancedRpcVerifier.Reset();
        }

        [TearDown]
        public void TearDown()
        {
            RpcTypeRegistry.ResetForTests();
            EnhancedRpcVerifier.Reset();
        }

        // ──────────────────────────────────────────────────────────────────
        // RpcTypeRegistry — explicit registration is the only path
        // ──────────────────────────────────────────────────────────────────

        [Test]
        [Description("Resolve returns null for any type name that has not been explicitly registered.")]
        public void Registry_UnregisteredName_ResolvesToNull()
        {
            Assert.IsNull(RpcTypeRegistry.Resolve(typeof(AllowedPayload).FullName));
            Assert.IsNull(RpcTypeRegistry.Resolve(typeof(ForbiddenPayload).FullName));
        }

        [Test]
        [Description("Register<T> admits the type and Resolve then returns the concrete System.Type.")]
        public void Registry_Register_AdmitsType()
        {
            RpcTypeRegistry.Register<AllowedPayload>();
            Assert.AreEqual(typeof(AllowedPayload), RpcTypeRegistry.Resolve(typeof(AllowedPayload).FullName));
        }

        [Test]
        [Description("AllowAppDomainScan=false (default) keeps attributed but unregistered types invisible.")]
        public void Registry_DefaultPolicy_AttributeAloneIsNotSufficient()
        {
            // Sanity: AllowedPayload carries [RtmpeRpcSerializable] but
            // no Register call has run.  The default policy must keep
            // it invisible — the attribute is opt-in for the scan, not
            // a free pass to instantiation.
            Assert.IsFalse(RpcTypeRegistry.AllowAppDomainScan);
            Assert.IsNull(RpcTypeRegistry.Resolve(typeof(AllowedPayload).FullName));
        }

        [Test]
        [Description("AllowAppDomainScan=true admits attributed types and still rejects unattributed ones.")]
        public void Registry_AppDomainScan_FiltersByAttribute()
        {
            RpcTypeRegistry.AllowAppDomainScan = true;

            // Attributed type is auto-registered by the scan.
            Assert.AreEqual(typeof(AllowedPayload),
                RpcTypeRegistry.Resolve(typeof(AllowedPayload).FullName));

            // Unattributed type is NOT picked up even though it satisfies
            // the structural filter (public, parameterless ctor, implements
            // INetworkSerializable). The registry is closed: opt-in via the
            // [RtmpeRpcSerializable] attribute is required.
            Assert.IsNull(RpcTypeRegistry.Resolve(typeof(ForbiddenPayload).FullName));
        }

        [Test]
        [Description("Empty / null type names always resolve to null without throwing.")]
        public void Registry_NullOrEmptyName_ResolvesToNull()
        {
            Assert.IsNull(RpcTypeRegistry.Resolve(null));
            Assert.IsNull(RpcTypeRegistry.Resolve(string.Empty));
        }

        // ──────────────────────────────────────────────────────────────────
        // RpcSerializer — unregistered INetworkSerializable surfaces as null
        // ──────────────────────────────────────────────────────────────────

        [Test]
        [Description("ReadParam for an unregistered INetworkSerializable type name returns null and " +
                     "advances offset past the declared payload so subsequent params still align.")]
        public void Serializer_UnregisteredType_ReturnsNullAndAdvances()
        {
            // Build a synthetic INetworkSerializable param with a type
            // name that is NOT in the registry.  Wire format:
            //  tag(0x0A) | name_len(u16 LE) | name UTF-8 | payload_len(u16 LE) | payload
            string forbidden = typeof(ForbiddenPayload).FullName;
            var nameBytes = System.Text.Encoding.UTF8.GetBytes(forbidden);
            int payloadLen = 4;

            int total = 1 + 2 + nameBytes.Length + 2 + payloadLen + /*trailing sentinel*/ 1;
            var buf = new byte[total];
            int o = 0;
            buf[o++] = 0x0A;
            buf[o++] = (byte)nameBytes.Length;
            buf[o++] = (byte)(nameBytes.Length >> 8);
            System.Buffer.BlockCopy(nameBytes, 0, buf, o, nameBytes.Length); o += nameBytes.Length;
            buf[o++] = (byte)payloadLen;
            buf[o++] = (byte)(payloadLen >> 8);
            // payload bytes (arbitrary)
            buf[o++] = 0xDE; buf[o++] = 0xAD; buf[o++] = 0xBE; buf[o++] = 0xEF;
            // trailing sentinel — must remain untouched since the parser
            // is supposed to stop at the end of the declared payload.
            buf[o++] = 0xA5;

            int endOfParam = 1 + 2 + nameBytes.Length + 2 + payloadLen;
            int offset = 0;
            object val = RpcSerializer.ReadParam(buf, ref offset);

            Assert.IsNull(val, "Unregistered type name must surface as a null parameter.");
            Assert.AreEqual(endOfParam, offset,
                "Parser must advance exactly past the declared payload, leaving the sentinel intact.");
            Assert.AreEqual(0xA5, buf[offset]);
        }

        // ──────────────────────────────────────────────────────────────────
        // EnhancedRpcPacketParser — wire-id and target verification
        // ──────────────────────────────────────────────────────────────────

        // Build an Enhanced RPC payload with caller-controlled fields so
        // each test exercises exactly one verification gate.  The
        // EnhancedRpcPacketBuilder validates target via the enum type, so
        // we manually craft bytes here to inject hostile values.
        private static byte[] BuildPayload(uint methodId, ulong senderId, uint requestId,
                                           ulong objectId, byte targetByte, byte paramCount,
                                           byte[] paramTail = null)
        {
            int header = 27;
            int tail = paramTail?.Length ?? 0;
            var buf = new byte[header + tail];
            int o = 0;
            void WriteU16LE(ushort v) { buf[o++] = (byte)v; buf[o++] = (byte)(v >> 8); }
            void WriteU32LE(uint v)
            {
                buf[o++] = (byte)v;
                buf[o++] = (byte)(v >> 8);
                buf[o++] = (byte)(v >> 16);
                buf[o++] = (byte)(v >> 24);
            }
            void WriteU64LE(ulong v)
            {
                for (int i = 0; i < 8; i++) buf[o++] = (byte)(v >> (i * 8));
            }
            WriteU32LE(methodId);
            WriteU64LE(senderId);
            WriteU32LE(requestId);
            WriteU64LE(objectId);
            buf[o++] = targetByte;
            buf[o++] = 0x00; // rpc_flags reserved
            buf[o++] = paramCount;
            if (tail > 0) System.Buffer.BlockCopy(paramTail, 0, buf, o, tail);
            return buf;
        }

        [Test]
        [Description("Reject inbound RPC with out-of-range target byte (unchecked enum cast guard).")]
        public void Parser_OutOfRangeTarget_Rejected()
        {
            // 0xFF is not a defined RpcTarget value — defined: 0x00..0x03.
            var bad = BuildPayload(
                methodId: 0xDEADBEEF, senderId: 1234, requestId: 1,
                objectId: 99, targetByte: 0xFF, paramCount: 0);

            Assert.IsFalse(EnhancedRpcPacketParser.TryParse(bad, out var req));
            Assert.IsNull(req);
        }

        [Test]
        [Description("Reject inbound RPC with senderId == 0 (uninitialised-session sentinel).")]
        public void Parser_ZeroSender_Rejected()
        {
            var bad = BuildPayload(
                methodId: 1, senderId: 0, requestId: 1,
                objectId: 99, targetByte: (byte)RpcTarget.All, paramCount: 0);

            Assert.IsFalse(EnhancedRpcPacketParser.TryParse(bad, out _));
        }

        [Test]
        [Description("Integrator SenderVerifier rejects unknown senderIds (membership check).")]
        public void Parser_SenderVerifier_RejectsUnknownSender()
        {
            EnhancedRpcVerifier.SenderVerifier = sid => sid == 7777UL;

            var fromKnown = BuildPayload(
                methodId: 1, senderId: 7777, requestId: 1,
                objectId: 99, targetByte: (byte)RpcTarget.All, paramCount: 0);
            var fromStranger = BuildPayload(
                methodId: 1, senderId: 1234, requestId: 1,
                objectId: 99, targetByte: (byte)RpcTarget.All, paramCount: 0);

            Assert.IsTrue(EnhancedRpcPacketParser.TryParse(fromKnown, out _));
            Assert.IsFalse(EnhancedRpcPacketParser.TryParse(fromStranger, out _));
        }

        [Test]
        [Description("Integrator ObjectExistsVerifier rejects unknown objectIds.")]
        public void Parser_ObjectVerifier_RejectsUnknownObject()
        {
            EnhancedRpcVerifier.ObjectExistsVerifier = oid => oid == 42UL;

            var live = BuildPayload(
                methodId: 1, senderId: 5, requestId: 1,
                objectId: 42, targetByte: (byte)RpcTarget.All, paramCount: 0);
            var dead = BuildPayload(
                methodId: 1, senderId: 5, requestId: 1,
                objectId: 999, targetByte: (byte)RpcTarget.All, paramCount: 0);

            Assert.IsTrue(EnhancedRpcPacketParser.TryParse(live, out _));
            Assert.IsFalse(EnhancedRpcPacketParser.TryParse(dead, out _));
        }

        [Test]
        [Description("Accept inbound RPC matching all valid criteria (defined target, non-zero sender, " +
                     "verifiers pass).")]
        public void Parser_HappyPath_AllChecksPass()
        {
            EnhancedRpcVerifier.SenderVerifier = _ => true;
            EnhancedRpcVerifier.ObjectExistsVerifier = _ => true;

            var ok = BuildPayload(
                methodId: 0xCAFEBABE, senderId: 0x1234_5678_9ABC_DEF0,
                requestId: 7, objectId: 100,
                targetByte: (byte)RpcTarget.Others, paramCount: 0);

            Assert.IsTrue(EnhancedRpcPacketParser.TryParse(ok, out var req));
            Assert.IsNotNull(req);
            Assert.AreEqual(0xCAFEBABEU, req.MethodId);
            Assert.AreEqual(0x1234_5678_9ABC_DEF0UL, req.SenderId);
            Assert.AreEqual(100UL, req.ObjectId);
            Assert.AreEqual(RpcTarget.Others, req.Target);
            Assert.AreEqual(0, req.Args.Length);
        }

        [Test]
        [Description("All four defined RpcTarget enum values pass the structural target check.")]
        public void Parser_AllDefinedTargets_Accepted()
        {
            foreach (RpcTarget t in System.Enum.GetValues(typeof(RpcTarget)))
            {
                var bytes = BuildPayload(
                    methodId: 1, senderId: 1, requestId: 0,
                    objectId: 1, targetByte: (byte)t, paramCount: 0);
                Assert.IsTrue(EnhancedRpcPacketParser.TryParse(bytes, out var req),
                    $"Defined target {t} (0x{(byte)t:X2}) must be accepted.");
                Assert.AreEqual(t, req.Target);
            }
        }
    }
}
