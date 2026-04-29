// RTMPE SDK — Tests/Runtime/Lz4CompressorSecurityTests.cs
//
// Hardening tests for Lz4Compressor: compression-ratio bombs, RLE expansion
// amplifiers, and pooled-buffer reuse correctness.  The functional round-trip
// suite lives in CompressionTests; this file exercises the additional safety
// gates and the ArrayPool-backed code paths that protect mobile clients from
// CPU-DoS and GC pressure originating in attacker-controlled traffic.

using System;
using NUnit.Framework;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    public class Lz4CompressorSecurityTests
    {
        // Helper: build a wire-format frame with a chosen declared length
        // and a chosen number of arbitrary payload bytes.  Used to construct
        // pathological inputs that target the ratio gate.
        private static byte[] BuildFrame(uint declaredLen, byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            frame[0] = (byte) declaredLen;
            frame[1] = (byte)(declaredLen >>  8);
            frame[2] = (byte)(declaredLen >> 16);
            frame[3] = (byte)(declaredLen >> 24);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        // ── compression-ratio bomb ─────────────────────────────────────

        [Test]
        public void Decompress_RatioBomb_TinyInputDeclaringMaxOutput_Throws()
        {
            // Declare the full 16 KiB ceiling backed by only 16 bytes of
            // compressed payload — a 1024× ratio, well above the 100× cap.
            var payload = new byte[16];
            var frame   = BuildFrame((uint)Lz4Compressor.MaxDecompressed, payload);

            var ex = Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
            StringAssert.Contains("ratio", ex.Message,
                "ratio gate must be the rejecting check");
        }

        [Test]
        public void Decompress_RatioJustAtLimit_DoesNotTriggerRatioGate()
        {
            // We don't care about a successful round-trip here (the bytes are
            // garbage); the assertion is that the failure, if any, is NOT the
            // ratio message.  The actual decode will reject it as malformed.
            uint declared = 1280;     // == MinCompressible × 10
            const int payloadLen = 14; // 1280 / 14 == 91, comfortably ≤ 100
            var payload = new byte[payloadLen];

            var frame = BuildFrame(declared, payload);
            var ex = Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
            StringAssert.DoesNotContain("ratio", ex.Message,
                "ratios at or below the cap must fall through to the regular decoder");
        }

        [Test]
        public void Decompress_EmptyCompressedPayload_Throws()
        {
            // Prefix only, no LZ4 block — must be rejected before any decode.
            var frame = new byte[4];
            uint declared = (uint)Lz4Compressor.MinCompressible;
            frame[0] = (byte) declared;
            frame[1] = (byte)(declared >> 8);

            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
        }

        // ── per-match expansion cap ────────────────────────────────────

        [Test]
        public void Decompress_OversizedSingleMatch_Throws()
        {
            // Construct: literal 'A' followed by a single match of length
            // exactly 4097 bytes (one above MaxSingleMatchExpansion = 4096).
            //
           // Token layout:
            //  high nibble = 1 (one literal)
            //  low  nibble = 15 (matchLen >= 19, extended)
            // After the literal we emit a u16 offset (=1 → overlap RLE),
            // followed by the extended-match-length chain that sums to
            // (4097 - 4 - 15) = 4078 distributed as 0xFF×15 + remainder.

            const int targetMatchLen = 4097;
            int extra = targetMatchLen - 4 - 15;          // bytes after the 0x0F nibble
            int ffCount = extra / 255;
            int tail    = extra - (ffCount * 255);

            var payload = new System.Collections.Generic.List<byte>();
            payload.Add(0x1F);                            // token: 1 literal, 0x0F match
            payload.Add((byte)'A');                       // the literal
            payload.Add(0x01); payload.Add(0x00);         // u16 offset = 1 (RLE)
            for (int i = 0; i < ffCount; i++) payload.Add(0xFF);
            payload.Add((byte)tail);

            // Declared length chosen so that declared/payload <= 100, ensuring
            // the ratio gate does NOT fire and the per-match cap is the rejector.
            uint declared = (uint)(payload.Count * 90);
            if (declared < Lz4Compressor.MinCompressible)
                declared = (uint)Lz4Compressor.MinCompressible;
            var frame = BuildFrame(declared, payload.ToArray());

            var ex = Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
            // The decompressor returns -1 from DecompressBlock and the wrapper
            // reports the produced/expected mismatch (not a "ratio" message).
            StringAssert.DoesNotContain("ratio", ex.Message,
                "rejection must come from the per-match cap, not the ratio gate");
        }

        [Test]
        public void Decompress_PathologicalRleInput_RejectedQuickly()
        {
            // The classic 70-byte amplifier: literal one byte, then a single
            // RLE match expanding to the full 16 KiB ceiling.  The per-match
            // cap must reject this without entering the byte-loop for anything
            // approaching 16k iterations.
            int extra = Lz4Compressor.MaxDecompressed - 4 - 15;
            int ffCount = extra / 255;
            int tail    = extra - (ffCount * 255);

            var payload = new System.Collections.Generic.List<byte>();
            payload.Add(0x1F);
            payload.Add((byte)'X');
            payload.Add(0x01); payload.Add(0x00);
            for (int i = 0; i < ffCount; i++) payload.Add(0xFF);
            payload.Add((byte)tail);

            uint declared = (uint)Lz4Compressor.MaxDecompressed;
            var frame = BuildFrame(declared, payload.ToArray());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
            sw.Stop();

            // Well-formed rejection should be sub-millisecond on any platform;
            // a generous 100 ms budget catches accidental amplification.
            Assert.Less(sw.ElapsedMilliseconds, 100,
                "rejection must be fast — no full byte-loop expansion");
        }

        // ── non-overlap fast path correctness ──────────────────────────

        [Test]
        public void RoundTrip_NonOverlapMatch_FastPathProducesIdenticalOutput()
        {
            // A repetitive block where every match has matchOffset >= matchLen
            // exercises the Buffer.BlockCopy fast path.  Round-trip equality
            // is the regression check: the fast path must produce byte-exact
            // output relative to the byte-loop reference.
            //
           // 256 bytes of pattern A followed by 256 bytes of pattern B,
            // duplicated — the second half references the first via a long
            // (256-byte) backwards offset, well above any single match length.
            var data = new byte[1024];
            for (int i = 0; i < 256; i++) data[i]       = (byte)(i & 0x7F);
            for (int i = 0; i < 256; i++) data[256 + i] = (byte)((i * 3) & 0x7F);
            Buffer.BlockCopy(data, 0, data, 512, 512);

            var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed);

            var restored = Lz4Compressor.Decompress(wire);
            CollectionAssert.AreEqual(data, restored);
        }

        [Test]
        public void RoundTrip_OverlapMatch_ByteLoopProducesIdenticalOutput()
        {
            // A short repeating motif forces overlap matches (matchOffset <
            // matchLen) — the byte-loop path.  This guards against a future
            // regression where someone "optimises" the overlap path.
            var data = new byte[512];
            // Pattern "ABCD" repeated produces back-references with offset = 4
            // and match lengths much greater than 4.
            for (int i = 0; i < data.Length; i++) data[i] = (byte)('A' + (i % 4));

            var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed);

            var restored = Lz4Compressor.Decompress(wire);
            CollectionAssert.AreEqual(data, restored);
        }

        // ── ArrayPool reuse correctness ────────────────────────────────

        [Test]
        public void RoundTrip_ManyIterations_PooledBuffersDoNotCorruptOutput()
        {
            // Hash table is rented from ArrayPool<int> and must be cleared
            // before use; any stale entries from a previous tenant would
            // generate phantom back-references.  This loop allocates and
            // returns the same pool slots many times across varying inputs;
            // a missing Clear would surface as round-trip mismatches.
            var rng = new Random(20260427);
            const int iterations = 1000;

            for (int i = 0; i < iterations; i++)
            {
                int len = rng.Next(Lz4Compressor.MinCompressible,
                                   Lz4Compressor.MaxDecompressed);
                var data = new byte[len];

                // Mix of repetitive and random content so the compressor
                // populates the hash table differently on each iteration.
                int splitA = len / 3;
                int splitB = (2 * len) / 3;
                for (int j = 0; j < splitA; j++) data[j] = (byte)(j & 0xFF);
                for (int j = splitA; j < splitB; j++) data[j] = 0xAA;
                for (int j = splitB; j < len; j++) data[j] = (byte)rng.Next(0, 256);

                var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
                if (!compressed) continue;

                var restored = Lz4Compressor.Decompress(wire);
                CollectionAssert.AreEqual(data, restored,
                    $"pool corruption surfaced on iteration {i} (len={len})");
            }
        }

        // ── SDK-H-07: checked cast at MaxDecompressed ceiling ─────────────

        [Test]
        public void Decompress_DeclaredLenAtMaxCap_DoesNotOverflow()
        {
            // Verify that declaredLen == MaxDecompressed (16 384) does not
            // trigger OverflowException from checked((int)declaredLen).
            // 16 384 is well within int range, so the cast must succeed and
            // the round-trip must produce the correct output.
            var data = new byte[Lz4Compressor.MaxDecompressed];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0x7F);

            var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed, "16 KiB of low-entropy data must compress.");

            var restored = Lz4Compressor.Decompress(wire);
            Assert.AreEqual(Lz4Compressor.MaxDecompressed, restored.Length,
                "Decompressed length must match MaxDecompressed.");
            CollectionAssert.AreEqual(data, restored,
                "Round-trip at MaxDecompressed ceiling must be byte-exact.");
        }

        [Test]
        public void Compress_DoesNotRetainStateAcrossCalls()
        {
            // Two unrelated payloads in sequence must produce wire output
            // identical to running each in isolation — i.e. the rented hash
            // table is fully reinitialised between calls.
            var a = new byte[256]; for (int i = 0; i < a.Length; i++) a[i] = (byte)(i & 0x3F);
            var b = new byte[256]; for (int i = 0; i < b.Length; i++) b[i] = (byte)((i * 5) & 0x3F);

            var wireA1 = Lz4Compressor.CompressIfBeneficial(a, out _);
            var wireB  = Lz4Compressor.CompressIfBeneficial(b, out _);
            var wireA2 = Lz4Compressor.CompressIfBeneficial(a, out _);

            CollectionAssert.AreEqual(wireA1, wireA2,
                "compressor output for identical input must be deterministic " +
                "across an interleaved call — proves the hash table is reset");
            // Sanity: A and B should differ (they encode different bytes).
            CollectionAssert.AreNotEqual(wireA1, wireB,
                "distinct inputs must produce distinct compressed output");
        }
    }
}
