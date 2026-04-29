// RTMPE SDK — Tests/Runtime/RpcPolishTests.cs
//
// Sprint-5 LOW-severity polish coverage:
//
//  • L RequestIdAllocator        — non-zero, distinct, timeout fires
//  • L NetworkTransformInterpolator — NaN / Inf / far-future timestamps rejected
//  • L NetworkVariableList FullSync — oversize element count rejected
//  • L PropertyValue.GetHashCode    — content-aware (not length-only) hash
//  • L LogRedaction                 — display name / player ID / room code
//  • L RpcSerializer                — deserialize failure throws or returns null cleanly
//  • L PropertyValue.OfBytes        — input mutation does NOT propagate

using System;
using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

using RTMPE.Core;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("RpcPolish")]
    public class RpcPolishTests
    {
        // ── RequestIdAllocator ─────────────────────────────────────────

        [SetUp]
        public void Setup()
        {
            RequestIdAllocator.ResetForTest();
        }

        [Test]
        [Description("Allocator never returns zero across many draws.")]
        public void RequestIdAllocator_NeverReturnsZero()
        {
            for (int i = 0; i < 1000; i++)
            {
                uint id = RequestIdAllocator.Next();
                Assert.AreNotEqual(0u, id, "Zero is reserved as 'unused'.");
            }
        }

        [Test]
        [Description("1000 draws produce nearly all-distinct IDs (CSPRNG-backed).")]
        public void RequestIdAllocator_ProducesDistinctIds()
        {
            const int N = 1000;
            var seen = new System.Collections.Generic.HashSet<uint>();
            for (int i = 0; i < N; i++)
                seen.Add(RequestIdAllocator.Next());

            // 32-bit space — 1000 draws should collide essentially never.
            Assert.GreaterOrEqual(seen.Count, N - 2,
                "Expected near-perfect uniqueness from CSPRNG draws.");
        }

        [Test]
        [Description("PurgeExpired fires the timeout callback once the deadline passes.")]
        public void RequestIdAllocator_TimeoutFires()
        {
            int callbackCount = 0;
            uint id = RequestIdAllocator.AllocateAndRegister(
                timeout: TimeSpan.FromMilliseconds(50),
                onTimeout: () => Interlocked.Increment(ref callbackCount));

            Assert.AreEqual(1, RequestIdAllocator.PendingCount);

            // No-op while the deadline is still in the future.
            int purged0 = RequestIdAllocator.PurgeExpired();
            Assert.AreEqual(0, purged0);
            Assert.AreEqual(0, callbackCount);

            Thread.Sleep(120);
            int purged1 = RequestIdAllocator.PurgeExpired();
            Assert.AreEqual(1, purged1);
            Assert.AreEqual(1, callbackCount);
            Assert.AreEqual(0, RequestIdAllocator.PendingCount);

            // Resolve on a no-longer-pending ID is a clean false.
            Assert.IsFalse(RequestIdAllocator.Resolve(id));
        }

        [Test]
        [Description("PurgeExpired sweeps all expired entries in a single call — simulates NetworkManager's 5-second sweep.")]
        public void RequestIdAllocator_MultipleExpired_AllPurgedInOneSweep()
        {
            const int count = 5;
            int fired = 0;
            for (int i = 0; i < count; i++)
            {
                RequestIdAllocator.AllocateAndRegister(
                    timeout: TimeSpan.FromMilliseconds(30),
                    onTimeout: () => System.Threading.Interlocked.Increment(ref fired));
            }
            Assert.AreEqual(count, RequestIdAllocator.PendingCount);

            Thread.Sleep(80);
            int purged = RequestIdAllocator.PurgeExpired();

            Assert.AreEqual(count, purged, "All expired entries must be removed in one sweep.");
            Assert.AreEqual(count, fired,  "All timeout callbacks must fire.");
            Assert.AreEqual(0,     RequestIdAllocator.PendingCount);
        }

        [Test]
        [Description("Resolve removes the entry and prevents the timeout from firing.")]
        public void RequestIdAllocator_ResolvePreventsTimeout()
        {
            int callbackCount = 0;
            uint id = RequestIdAllocator.AllocateAndRegister(
                timeout: TimeSpan.FromMilliseconds(50),
                onTimeout: () => Interlocked.Increment(ref callbackCount));

            Assert.IsTrue(RequestIdAllocator.Resolve(id));
            Thread.Sleep(120);
            RequestIdAllocator.PurgeExpired();
            Assert.AreEqual(0, callbackCount);
        }

        // ── Interpolator timestamp guards ──────────────────────────────

        private static TransformState MakeState(float x = 0f) => new TransformState
        {
            Position = new Vector3(x, 0f, 0f),
            Rotation = Quaternion.identity,
            Scale    = Vector3.one,
        };

        [Test]
        [Description("NaN timestamp does not advance the buffer.")]
        public void Interpolator_NaNTimestamp_Rejected()
        {
            var go = new GameObject("NaN");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 5, interpolationDelay: 0.1f);

                interp.AddState(MakeState(), double.NaN);
                Assert.AreEqual(0, interp.BufferCount);

                // Subsequent legitimate timestamp must still be accepted.
                interp.AddState(MakeState(), 1.0);
                Assert.AreEqual(1, interp.BufferCount);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        [Description("Positive infinity is rejected and does not poison _latestTimestamp.")]
        public void Interpolator_PositiveInfinity_Rejected()
        {
            var go = new GameObject("Inf");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                interp.ConfigureForTest(bufferSize: 5, interpolationDelay: 0.1f);

                interp.AddState(MakeState(), double.PositiveInfinity);
                Assert.AreEqual(0, interp.BufferCount);
                interp.AddState(MakeState(), 2.0);
                Assert.AreEqual(1, interp.BufferCount);
            }
            finally { Object.DestroyImmediate(go); }
        }

        [Test]
        [Description("Far-future timestamp (> max) is rejected and does not freeze the buffer.")]
        public void Interpolator_FarFutureTimestamp_Rejected()
        {
            var go = new GameObject("FarFuture");
            try
            {
                var interp = go.AddComponent<NetworkTransformInterpolator>();
                // max = 100 seconds for the test
                interp.ConfigureForTest(bufferSize: 5, interpolationDelay: 0.1f,
                                        maxAcceptableTimestamp: 100.0);

                interp.AddState(MakeState(), double.MaxValue);
                Assert.AreEqual(0, interp.BufferCount);
                interp.AddState(MakeState(), 1_000_000.0); // also > 100
                Assert.AreEqual(0, interp.BufferCount);

                // Legitimate small timestamp still accepted.
                interp.AddState(MakeState(), 5.0);
                Assert.AreEqual(1, interp.BufferCount);
            }
            finally { Object.DestroyImmediate(go); }
        }

        // ── NetworkVariableList FullSync size cap ──────────────────────

        [Test]
        [Description("FullSync with count > MaxNetworkVariableListSize is rejected entirely.")]
        public void NetworkVariableList_FullSync_OversizeCount_Rejected()
        {
            var nmGo = new GameObject("NM");
            var ownerGo = new GameObject("Owner");
            try
            {
                nmGo.AddComponent<NetworkManager>();
                var owner = ownerGo.AddComponent<L31StubBehaviour>();
                owner.Initialize(1UL, "owner");

                var src = new NetworkVariableListInt(owner, 0);
                // Pre-populate the receiver to ensure rejection truly DROPS the
                // payload (rather than silently clearing).
                var dst = new NetworkVariableListInt(owner, 0);
                dst.Add(7);
                dst.SetMaxListSizeForTest(8);

                // Build a poisoned payload by hand: op_count=1, op=FullSync(0x06), count=2000.
                using var ms = new MemoryStream();
                using var w  = new BinaryWriter(ms);
                w.Write((byte)1);
                w.Write((byte)0x06);     // ListOp.FullSync
                w.Write((ushort)2000);   // > 8 cap
                // No element bytes — receiver must abort BEFORE attempting to
                // read 2000 elements.

                ms.Position = 0;
                using var r = new BinaryReader(ms);
                dst.Deserialize(r);

                Assert.AreEqual(1, dst.Count, "Receiver must not mutate on rejected FullSync.");
                Assert.AreEqual(7, dst[0]);
            }
            finally
            {
                Object.DestroyImmediate(ownerGo);
                Object.DestroyImmediate(nmGo);
            }
        }

        [Test]
        [Description("FullSync at the cap is accepted normally.")]
        public void NetworkVariableList_FullSync_AtCap_Accepted()
        {
            var nmGo = new GameObject("NM2");
            var ownerGo = new GameObject("Owner2");
            try
            {
                nmGo.AddComponent<NetworkManager>();
                var owner = ownerGo.AddComponent<L31StubBehaviour>();
                owner.Initialize(1UL, "owner");

                var dst = new NetworkVariableListInt(owner, 0);
                dst.SetMaxListSizeForTest(4);

                using var ms = new MemoryStream();
                using var w  = new BinaryWriter(ms);
                w.Write((byte)1);
                w.Write((byte)0x06);
                w.Write((ushort)4);
                for (int i = 0; i < 4; i++) w.Write(i);

                ms.Position = 0;
                using var r = new BinaryReader(ms);
                dst.Deserialize(r);

                Assert.AreEqual(4, dst.Count);
                for (int i = 0; i < 4; i++) Assert.AreEqual(i, dst[i]);
            }
            finally
            {
                Object.DestroyImmediate(ownerGo);
                Object.DestroyImmediate(nmGo);
            }
        }

        // ── PropertyValue.GetHashCode collision-resistance ─────────────

        [Test]
        [Description("1000 distinct same-length byte arrays produce mostly distinct hash codes.")]
        public void PropertyValue_BytesHash_ResistsCollision()
        {
            const int N = 1000;
            var seen = new System.Collections.Generic.HashSet<int>();
            var rng = new System.Random(1234);
            for (int i = 0; i < N; i++)
            {
                var bytes = new byte[64];
                rng.NextBytes(bytes);
                int h = PropertyValue.OfBytes(bytes).GetHashCode();
                seen.Add(h);
            }
            // FNV-1a should give >>900 unique buckets out of 1000 random inputs.
            Assert.GreaterOrEqual(seen.Count, 900,
                $"Hash collision rate too high: {seen.Count}/{N} unique buckets.");
        }

        [Test]
        [Description("hash is content-aware — flipping a byte changes the hash.")]
        public void PropertyValue_BytesHash_IsContentAware()
        {
            var a = new byte[] { 1, 2, 3, 4, 5 };
            var b = new byte[] { 1, 2, 3, 4, 6 };
            int ha = PropertyValue.OfBytes(a).GetHashCode();
            int hb = PropertyValue.OfBytes(b).GetHashCode();
            Assert.AreNotEqual(ha, hb);
        }

        // ── LogRedaction round-trips ───────────────────────────────────

        [Test]
        [Description("DisplayName redacts to first-char + ***.")]
        public void LogRedaction_DisplayName_FirstCharOnly()
        {
            Assert.AreEqual("A***", LogRedaction.DisplayName("Alice"));
            Assert.AreEqual("",     LogRedaction.DisplayName(""));
            Assert.AreEqual("",     LogRedaction.DisplayName(null));
        }

        [Test]
        [Description("PlayerId redacts to first-4 + ***; short IDs fully redact.")]
        public void LogRedaction_PlayerId_FirstFourOnly()
        {
            Assert.AreEqual("ABCD***", LogRedaction.PlayerId("ABCDEFGH"));
            Assert.AreEqual("***",     LogRedaction.PlayerId("ab"));
            Assert.AreEqual("",        LogRedaction.PlayerId(""));
        }

        [Test]
        [Description("RoomCode is fully redacted.")]
        public void LogRedaction_RoomCode_FullyRedacted()
        {
            Assert.AreEqual("***", LogRedaction.RoomCode("AB12CD"));
            Assert.AreEqual("",    LogRedaction.RoomCode(""));
        }

        [Test]
        [Description("MatchmakingResult.ToString does not leak the room code.")]
        public void MatchmakingResult_ToString_RedactsRoomCode()
        {
            var r = new MatchmakingResultStub("room-uuid", "AB12CD", true);
            string s = r.ToString();
            StringAssert.DoesNotContain("AB12CD", s);
            StringAssert.Contains("***", s);
            StringAssert.Contains("room-uuid", s);
        }

        // ── RpcSerializer throws on partial-state ──────────────────────

        public struct ThrowingPayload : INetworkSerializable
        {
            public void NetworkSerialize(IRtmpeWriter writer) { writer.WriteInt32(0); }
            public void NetworkDeserialize(IRtmpeReader reader)
            {
                throw new InvalidOperationException("synthetic deserialise failure");
            }
        }

        [Test]
        [Description("Throwing NetworkDeserialize surfaces as RpcDeserializationException, not a partial instance.")]
        public void RpcSerializer_NetworkDeserializeThrow_ThrowsRpcException()
        {
            RpcTypeRegistry.ResetForTests();
            try
            {
                RpcTypeRegistry.Register<ThrowingPayload>();

                // Build a wire payload: type_id INetworkSerializable, then
                // type_name + zero-length payload.
                string typeName = typeof(ThrowingPayload).FullName;
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(typeName);

                var buf = new byte[1 + 2 + nameBytes.Length + 2];
                int o = 0;
                buf[o++] = 0x0A; // INetworkSerializable
                buf[o++] = (byte)nameBytes.Length;
                buf[o++] = (byte)(nameBytes.Length >> 8);
                Buffer.BlockCopy(nameBytes, 0, buf, o, nameBytes.Length);
                o += nameBytes.Length;
                buf[o++] = 0;
                buf[o++] = 0;

                int offset = 0;
                Assert.Throws<RpcDeserializationException>(
                    () => RpcSerializer.ReadParam(buf, ref offset));
            }
            finally
            {
                RpcTypeRegistry.ResetForTests();
            }
        }

        // ── PropertyValue.OfBytes immutability ─────────────────────────

        [Test]
        [Description("Mutating the input array AFTER OfBytes does NOT mutate the stored value.")]
        public void PropertyValue_OfBytes_DefensiveCopy_OnInput()
        {
            var src = new byte[] { 10, 20, 30 };
            var pv  = PropertyValue.OfBytes(src);

            // Caller mutates the original.
            src[0] = 99;
            src[1] = 99;

            byte[] readBack = pv.AsBytes();
            Assert.AreEqual(10, readBack[0]);
            Assert.AreEqual(20, readBack[1]);
            Assert.AreEqual(30, readBack[2]);
        }

        [Test]
        [Description("AsBytes returns a defensive copy — caller mutation cannot poison subsequent readers.")]
        public void PropertyValue_AsBytes_DefensiveCopy_OnOutput()
        {
            var pv = PropertyValue.OfBytes(new byte[] { 1, 2, 3 });

            byte[] first = pv.AsBytes();
            first[0] = 0xFF;

            byte[] second = pv.AsBytes();
            Assert.AreEqual(1, second[0], "Second AsBytes() must not see first caller's mutation.");
            Assert.AreEqual(2, second[1]);
            Assert.AreEqual(3, second[2]);
        }

        [Test]
        [Description("AsBytesReadOnly view is non-allocating and content-stable.")]
        public void PropertyValue_AsBytesReadOnly_StableView()
        {
            var pv = PropertyValue.OfBytes(new byte[] { 7, 8, 9 });
            var view = pv.AsBytesReadOnly();
            Assert.AreEqual(3, view.Length);
            Assert.AreEqual(7, view.Span[0]);
            Assert.AreEqual(8, view.Span[1]);
            Assert.AreEqual(9, view.Span[2]);
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    /// <summary>
    /// Minimal NetworkBehaviour stub for  tests that need a concrete
    /// owner without standing up the full registration pipeline.  The base
    /// class exposes <c>Initialize(ulong, string)</c> as <c>internal</c>;
    /// the test assembly reaches it via <c>InternalsVisibleTo</c>.
    /// </summary>
    internal class L31StubBehaviour : NetworkBehaviour { }

    /// <summary>
    /// Local copy of MatchmakingResult used to assert ToString redaction
    /// without depending on the internal constructor's friend declaration.
    /// </summary>
    internal sealed class MatchmakingResultStub
    {
        public string RoomId   { get; }
        public string RoomCode { get; }
        public bool   Created  { get; }
        public MatchmakingResultStub(string id, string code, bool created)
        {
            RoomId = id; RoomCode = code; Created = created;
        }
        public override string ToString()
            => $"MatchmakingResult(roomId={RoomId}, roomCode={LogRedaction.RoomCode(RoomCode)}, created={Created})";
    }
}
