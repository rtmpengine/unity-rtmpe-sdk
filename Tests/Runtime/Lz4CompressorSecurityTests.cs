// RTMPE SDK — Tests/Runtime/Lz4CompressorSecurityTests.cs
//
// Hardening tests for Lz4Compressor: amplification attempts, malformed RLE
// overruns, and pooled-buffer reuse correctness.  The functional round-trip
// suite lives in CompressionTests; this file exercises the decompressed-size
// ceiling and the per-write bounds that cap memory and CPU on attacker-
// controlled traffic, and confirms that legitimately high-ratio frames (which
// the gateway's uncapped lz4_flex emits) still round-trip.

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
        // pathological inputs that probe the decode bounds.
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

        // ── amplification is bounded by the decompressed-size ceiling ──────

        [Test]
        public void Decompress_TinyInputDeclaringMaxOutput_Throws()
        {
            // A genuine amplification attempt — 16 compressed bytes declaring
            // the full 16 KiB ceiling — is rejected because the block exhausts
            // before declaredLen, so the decoder reports a produced/expected
            // mismatch.  Memory stays bounded by MaxDecompressed regardless of
            // the declared ratio.
            var payload = new byte[16];
            var frame   = BuildFrame((uint)Lz4Compressor.MaxDecompressed, payload);

            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
        }

        [Test]
        public void Decompress_HighRatioValidFrame_RoundTrips()
        {
            // Ratio is not a rejection criterion: a near-idle StateSync of
            // mostly-default data compresses well past any fixed ratio, and the
            // gateway's lz4_flex compressor emits exactly that shape, so the
            // decoder must accept it.  Memory safety comes from the
            // decompressed-size ceiling, not from gating on ratio.
            var data = new byte[Lz4Compressor.MaxDecompressed]; // all-zero → maximal ratio
            var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed);

            var restored = Lz4Compressor.Decompress(wire);
            CollectionAssert.AreEqual(data, restored);
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

        // ── match overrunning the declared output ──────────────────────

        [Test]
        public void Decompress_MatchOverrunsDeclaredOutput_Throws()
        {
            // Construct: literal 'A' followed by a single 4097-byte match while
            // the declared output is far smaller.  A match that would write
            // past the declared buffer is rejected by the aggregate per-write
            // bound (dstLen - dstOff < matchLen), independent of match length.
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

            // Declared length is far below the 4097-byte match, so the match
            // overruns the output buffer and DecompressBlock returns -1.
            uint declared = (uint)(payload.Count * 90);
            if (declared < Lz4Compressor.MinCompressible)
                declared = (uint)Lz4Compressor.MinCompressible;
            var frame = BuildFrame(declared, payload.ToArray());

            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(frame));
        }

        [Test]
        public void Decompress_MalformedRleOverrun_RejectedQuickly()
        {
            // A run-heavy frame whose single match overruns the declared output
            // (one literal precedes a match sized to the full ceiling) is
            // rejected by the aggregate per-write bound before the overlap
            // byte-loop runs, so rejection stays sub-millisecond.
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
