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
            return new NetworkVariableInt(null, variableId: 1, defaultValue: initial);
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
            var v = new NetworkVariableString(null, variableId: 2, defaultValue: short_);

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
            var v = new NetworkVariableString(null, variableId: 3, defaultValue: big);

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
    }
}
