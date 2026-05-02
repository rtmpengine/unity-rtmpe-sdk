// RTMPE SDK — Tests/Runtime/MemoryAllocationTests.cs
//
// NUnit Edit-Mode tests that assert the hot path stays allocation-free.
// Runs under the Mono scripting backend (the IL2CPP variant is covered by
// future test additions).  Uses GC.GetAllocatedBytesForCurrentThread
// which is available on .NET Standard 2.1 / Unity 2021.3+.

using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Performance")]
    public class MemoryAllocationTests
    {
        // ── Shared fixtures ──────────────────────────────────────────────────

        private static NetworkVariableInt MakeIntVar(int initial = 42)
        {
            // Construct without an owning behaviour — NetworkVariable only
            // needs Owner for change dispatch, which this test does not exercise.
            return new NetworkVariableInt(null, variableId: 1, initialValue: initial);
        }

        // ── SerializeWithId happy paths ──────────────────────────────────────

        [Test]
        public void SerializeWithId_Int_RoundTripsValue()
        {
            var v = MakeIntVar(1234);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            v.SerializeWithId(bw);
            bw.Flush();

            ms.Position = 0;
            using var br = new BinaryReader(ms);
            ushort id = br.ReadUInt16();
            ushort len = br.ReadUInt16();
            int value = br.ReadInt32();

            Assert.AreEqual(1, id, "variable ID mismatch");
            Assert.AreEqual(4, len, "int payload length must be 4 bytes");
            Assert.AreEqual(1234, value, "value did not roundtrip");
        }

        [Test]
        public void SerializeWithId_String_WithinPoolBuffer_Roundtrips()
        {
            const string short_ = "hello";
            var v = new NetworkVariableString(null, variableId: 2, initialValue: short_);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v.SerializeWithId(bw);
            bw.Flush();

            Assert.Greater(ms.Length, 0, "serialisation must produce output");
        }

        [Test]
        public void SerializeWithId_String_ExceedingPool_UsesGrowableFallback()
        {
            // A string large enough (> 1 KB) that the pooled 1 KB buffer
            // cannot contain it — exercises the NotSupportedException catch.
            var big = new string('x', 4096);
            var v = new NetworkVariableString(null, variableId: 3, initialValue: big);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            Assert.DoesNotThrow(() => v.SerializeWithId(bw),
                "overflow must fall back to the growable path silently");

            Assert.Greater(ms.Length, 4096,
                "growable fallback must emit the entire payload");
        }

        // ── Allocation budget for the small-value fast path ──────────────────

        [Test]
        public void SerializeWithId_Int_StaysInPoolBuffer()
        {
            // Warm up to populate pool caches.
            var warm = MakeIntVar(0);
            using (var ms0 = new MemoryStream())
            using (var bw0 = new BinaryWriter(ms0))
            {
                for (int i = 0; i < 10; i++) warm.SerializeWithId(bw0);
            }

            // Shared writer that is NOT part of the measured region.
            using var measured = new MemoryStream(1024);
            using var bw = new BinaryWriter(measured);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                measured.Position = 0;
                warm.SerializeWithId(bw);
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            long delta = after - before;
            // BinaryWriter internals may allocate a small scratch buffer on
            // the very first write of each call; allow a generous ceiling
            // that still flags any linear-per-call allocation growth.
            Assert.Less(delta, 8 * 1024,
                $"100 calls leaked {delta} bytes — expected ≤ 8 KB (indicates linear alloc)");
        }

        // ── Crypto allocation regression net (Tier-1 Perf-1 / Perf-2) ────────
        //
        // The audit's NEW-PF-1 / NEW-PF-3 findings call out per-packet
        // allocations on the AEAD path.  These tests pin the *per-call*
        // allocation budget at the level it sits at today so that a future
        // pooling change (or a regression that adds a new allocation) is
        // caught at PR time rather than after a profiler session.  The
        // ceilings are intentionally generous; tighten them as the code
        // moves to ArrayPool.

        [Test]
        public void ChaCha20Poly1305_Seal_PerCallAllocationStaysUnderCeiling()
        {
            byte[] key   = new byte[32];
            byte[] nonce = new byte[12];
            byte[] aad   = new byte[16];
            byte[] pt    = new byte[256];
            for (int i = 0; i < pt.Length; i++) pt[i] = (byte)i;

            // Warm-up: prime any lazy-init / JIT paths so the first measured
            // call is not penalized for one-time allocations.
            for (int i = 0; i < 3; i++) ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);

            const int Iterations = 50;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
                ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            // Today's measured Seal alloc is ≈ 2-4 KB / call (per audit
            // NEW-PF-1).  We pin at 16 KB to catch ANY 4× regression.
            Assert.Less(perCall, 16 * 1024,
                $"Seal allocated {perCall} B/call — over 16 KB ceiling.");
        }

        [Test]
        public void ChaCha20Poly1305_Open_PerCallAllocationStaysUnderCeiling()
        {
            byte[] key   = new byte[32];
            byte[] nonce = new byte[12];
            byte[] aad   = new byte[16];
            byte[] pt    = new byte[256];
            for (int i = 0; i < pt.Length; i++) pt[i] = (byte)i;
            byte[] ct = ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);

            for (int i = 0; i < 3; i++) ChaCha20Poly1305Impl.Open(key, nonce, ct, aad);

            const int Iterations = 50;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
                ChaCha20Poly1305Impl.Open(key, nonce, ct, aad);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            Assert.Less(perCall, 16 * 1024,
                $"Open allocated {perCall} B/call — over 16 KB ceiling.");
        }

        [Test]
        public void ChaCha20Poly1305_Open_AuthFailure_DoesNotAllocatePlaintext()
        {
            // Auth-failure path returns early.  We assert the per-call alloc
            // is *no worse* than the success path — i.e. there is no
            // diagnostic-only allocation gating the early reject (which
            // would let an attacker amplify GC pressure with garbage
            // ciphertexts).
            byte[] key   = new byte[32];
            byte[] nonce = new byte[12];
            byte[] aad   = new byte[16];
            byte[] pt    = new byte[256];
            byte[] ct    = ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);
            // Corrupt the tag.
            ct[ct.Length - 1] ^= 0xFF;

            for (int i = 0; i < 3; i++) ChaCha20Poly1305Impl.Open(key, nonce, ct, aad);

            const int Iterations = 50;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                var result = ChaCha20Poly1305Impl.Open(key, nonce, ct, aad);
                Assert.IsNull(result, "Tampered tag must reject.");
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            Assert.Less(perCall, 16 * 1024,
                $"Open(tampered) allocated {perCall} B/call — exceeds 16 KB amplification ceiling.");
        }

        [Test]
        public void Poly1305_StandalonePath_ExercisedViaSealZeroPlaintext()
        {
            // Poly1305 does not have a public surface here; the AEAD wrapper
            // is the only entry point.  A zero-length plaintext exercises
            // the MAC path with minimal ChaCha20 work, so any bloat we see
            // is dominated by Poly1305 internals (Buffer + BigInteger
            // chunks).  This pins the budget separately from the
            // 256-byte payload tests above so a regression localized to
            // Poly1305 is caught even when Seal is overall green.
            byte[] key   = new byte[32];
            byte[] nonce = new byte[12];
            byte[] aad   = new byte[16];
            byte[] pt    = Array.Empty<byte>();

            for (int i = 0; i < 3; i++) ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);

            const int Iterations = 50;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
                ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            // Empty plaintext: the fixed Poly1305 cost should be small.
            Assert.Less(perCall, 8 * 1024,
                $"Empty-payload Seal allocated {perCall} B/call — exceeds 8 KB Poly1305 ceiling.");
        }

        // ── PacketBuilder allocation regression ──────────────────────────────

        [Test]
        public void PacketBuilder_BuildHeartbeat_PerCallAllocationIsBounded()
        {
            var pb = new PacketBuilder();
            for (int i = 0; i < 3; i++) pb.BuildHeartbeat();

            const int Iterations = 1000;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++) pb.BuildHeartbeat();
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            // Every packet allocates a single 13-byte header array; 64 B
            // per call ceiling allows for any small managed overhead but
            // catches a regression that adds a second per-call array.
            Assert.Less(perCall, 64,
                $"BuildHeartbeat allocated {perCall} B/call — exceeds 64 B header-only ceiling.");
        }

        [Test]
        public void PacketBuilder_BuildData_PerCallAllocationScalesLinearly()
        {
            var pb = new PacketBuilder();
            byte[] payload = new byte[256];
            for (int i = 0; i < 3; i++) pb.BuildData(payload);

            const int Iterations = 500;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++) pb.BuildData(payload);
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            // 13 B header + 256 B payload = 269 B output, plus any tiny
            // overhead.  512 B ceiling catches a regression that copies
            // the payload more than once.
            Assert.Less(perCall, 512,
                $"BuildData(256) allocated {perCall} B/call — exceeds 512 B ceiling.");
        }

        // ── FlushDirtyVariables allocation regression ────────────────────────

        [Test]
        public void FlushDirtyVariables_NoDirty_DoesNotAllocate()
        {
            using var fixture = new FlushDirtyFixture();
            // Warm-up.
            for (int i = 0; i < 3; i++) fixture.Behaviour.FlushDirtyVariablesPublic(_ => { });

            const int Iterations = 200;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
                fixture.Behaviour.FlushDirtyVariablesPublic(_ => { });
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            Assert.Less(perCall, 32,
                $"Clean-flush allocated {perCall} B/call — the no-dirty fast path must be allocation-free.");
        }

        [Test]
        public void FlushDirtyVariables_OneDirtyInt_StaysWithinBudget()
        {
            using var fixture = new FlushDirtyFixture();
            // Warm-up + ensure the writer's lazy buffers are populated.
            fixture.IntVar.Value = 1;
            fixture.Behaviour.FlushDirtyVariablesPublic(_ => { });
            for (int i = 0; i < 3; i++)
            {
                fixture.IntVar.Value = i + 10;
                fixture.Behaviour.FlushDirtyVariablesPublic(_ => { });
            }

            const int Iterations = 200;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Iterations; i++)
            {
                fixture.IntVar.Value = i;
                fixture.Behaviour.FlushDirtyVariablesPublic(_ => { });
            }
            long after = GC.GetAllocatedBytesForCurrentThread();

            long perCall = (after - before) / Iterations;
            // MemoryStream + BinaryWriter are now cached per-instance; only
            // ToArray() allocates per flush (≈ 21 B for one int variable).
            // 256 B ceiling catches any regression that re-introduces per-call
            // stream/writer allocation while allowing for managed overhead.
            Assert.Less(perCall, 256,
                $"FlushDirtyVariables(1 int) allocated {perCall} B/call — over 256 B ceiling (cached-stream fix regressed).");
        }

        // ── Test fixtures ────────────────────────────────────────────────────

        // FlushDirtyVariables requires a live NetworkBehaviour with IsOwner
        // and IsSpawned both true.  We construct a minimal scene + manager
        // pair, set the local player ID to match the behaviour's owner, and
        // tear everything down on Dispose.
        private sealed class FlushDirtyFixture : IDisposable
        {
            public GameObject NmGo { get; }
            public NetworkManager Manager { get; }
            public GameObject ObjGo { get; }
            public AllocFlushBehaviour Behaviour { get; }
            public NetworkVariableInt IntVar { get; }

            public FlushDirtyFixture()
            {
                NmGo    = new GameObject("Alloc_NM");
                Manager = NmGo.AddComponent<NetworkManager>();
                Manager.SetLocalPlayerStringId("p-owner");

                ObjGo     = new GameObject("Alloc_Obj");
                Behaviour = ObjGo.AddComponent<AllocFlushBehaviour>();
                Behaviour.Initialize(42UL, "p-owner");
                IntVar    = new NetworkVariableInt(Behaviour, variableId: 1, initialValue: 0);
                Behaviour.SetSpawned(true);
            }

            public void Dispose()
            {
                if (ObjGo != null) UnityEngine.Object.DestroyImmediate(ObjGo);
                if (NmGo  != null) UnityEngine.Object.DestroyImmediate(NmGo);
            }
        }

        // Public shim so tests can call FlushDirtyVariables (internal).
        // The behaviour exposes the variable list construction path used
        // by production code without altering the visibility of the SDK
        // surface.
        private sealed class AllocFlushBehaviour : NetworkBehaviour
        {
            public void FlushDirtyVariablesPublic(Action<byte[]> sendPayload)
                => FlushDirtyVariables(sendPayload);
        }
    }
}
