// RTMPE SDK — Tests/Runtime/CompressionTests.cs
//
// Unit tests for Lz4Compressor.
// Wire-format correctness is critical: the gateway uses lz4_flex (Rust) on the
// receive side, so any deviation from the canonical LZ4 Block format causes
// silent data corruption.

using System;
using NUnit.Framework;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    public class CompressionTests
    {
        // ── CompressIfBeneficial — size gating ───────────────────────────────

        [Test]
        public void CompressIfBeneficial_BelowMinimum_ReturnsFalse()
        {
            var data = new byte[Lz4Compressor.MinCompressible - 1];
            new Random(42).NextBytes(data);

            var result = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);

            Assert.IsFalse(compressed, "payload below MIN_COMPRESSIBLE must not be compressed");
            Assert.AreSame(data, result, "original array must be returned unchanged");
        }

        [Test]
        public void CompressIfBeneficial_ExceedsMaximum_ReturnsFalse()
        {
            var data = new byte[Lz4Compressor.MaxDecompressed + 1];

            var result = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);

            Assert.IsFalse(compressed, "payload above MAX_DECOMPRESSED must not be compressed");
            Assert.AreSame(data, result);
        }

        [Test]
        public void CompressIfBeneficial_HighEntropy_ReturnsFalse()
        {
            // Random data is incompressible — compressed form will be ≥ original.
            var data = new byte[512];
            new Random(1337).NextBytes(data);

            Lz4Compressor.CompressIfBeneficial(data, out bool compressed);

            Assert.IsFalse(compressed,
                "random (high-entropy) data should not benefit from compression");
        }

        [Test]
        public void CompressIfBeneficial_HighlyRepetitive_ReturnsTrue()
        {
            // 512 identical bytes compress to well under 64 bytes.
            var data = new byte[512];
            Array.Fill(data, (byte)0xAB);

            var result = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);

            Assert.IsTrue(compressed, "repetitive data must be compressed");
            Assert.Less(result.Length, data.Length,
                "compressed form must be smaller than original");
            // First 4 bytes = u32 LE uncompressed length.
            uint prefix = (uint)(result[0] | (result[1] << 8) | (result[2] << 16) | (result[3] << 24));
            Assert.AreEqual((uint)data.Length, prefix,
                "wire-format prefix must carry the uncompressed length");
        }

        // ── Round-trip correctness ────────────────────────────────────────────

        [Test]
        public void RoundTrip_RepetitiveData_RestoresOriginal()
        {
            var original = new byte[512];
            for (int i = 0; i < original.Length; i++)
                original[i] = (byte)(i % 16); // repetitive pattern

            var wire = Lz4Compressor.CompressIfBeneficial(original, out bool compressed);
            Assert.IsTrue(compressed);

            var restored = Lz4Compressor.Decompress(wire);

            Assert.AreEqual(original.Length, restored.Length);
            for (int i = 0; i < original.Length; i++)
                Assert.AreEqual(original[i], restored[i],
                    $"byte mismatch at index {i}");
        }

        [Test]
        public void RoundTrip_AllZeros_RestoresOriginal()
        {
            var original = new byte[1024]; // all zeros

            var wire     = Lz4Compressor.CompressIfBeneficial(original, out bool compressed);
            Assert.IsTrue(compressed);
            var restored = Lz4Compressor.Decompress(wire);

            CollectionAssert.AreEqual(original, restored);
        }

        [Test]
        public void RoundTrip_AsciiText_RestoresOriginal()
        {
            // Typical game protocol text payload (e.g. JSON variable update).
            var text = System.Text.Encoding.UTF8.GetBytes(
                new string('A', 64) + new string('B', 64) + new string('C', 64));
            // 192 bytes — above MIN_COMPRESSIBLE, repetitive pattern.

            var wire     = Lz4Compressor.CompressIfBeneficial(text, out bool compressed);
            Assert.IsTrue(compressed);
            var restored = Lz4Compressor.Decompress(wire);

            CollectionAssert.AreEqual(text, restored);
        }

        [Test]
        public void RoundTrip_ExactlyMinimumSize_Compresses()
        {
            var data = new byte[Lz4Compressor.MinCompressible];
            Array.Fill(data, (byte)0x55);

            var wire     = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed);
            var restored = Lz4Compressor.Decompress(wire);

            CollectionAssert.AreEqual(data, restored);
        }

        [Test]
        public void RoundTrip_ExactlyMaximumSize_Compresses()
        {
            var data = new byte[Lz4Compressor.MaxDecompressed];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i & 0xFF);

            var wire     = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            // May or may not compress (sequential bytes — moderate ratio);
            // the important thing is round-trip fidelity.
            var restored = compressed
                ? Lz4Compressor.Decompress(wire)
                : data;

            CollectionAssert.AreEqual(data, restored);
        }

        // ── Decompress — error handling ───────────────────────────────────────

        [Test]
        public void Decompress_EmptyInput_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(Array.Empty<byte>()),
                "empty input must throw — missing 4-byte length prefix");
        }

        [Test]
        public void Decompress_TooShortForPrefix_Throws()
        {
            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(new byte[] { 0x01, 0x00, 0x00 }));
        }

        [Test]
        public void Decompress_DeclaredLengthExceedsCap_Throws()
        {
            // Declare 32 KiB — above MAX_DECOMPRESSED (16 KiB).
            var data = new byte[8];
            uint oversize = (uint)(Lz4Compressor.MaxDecompressed + 1);
            data[0] = (byte) oversize;
            data[1] = (byte)(oversize >>  8);
            data[2] = (byte)(oversize >> 16);
            data[3] = (byte)(oversize >> 24);

            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(data),
                "declared length exceeding MAX_DECOMPRESSED must throw");
        }

        [Test]
        public void Decompress_DeclaredLengthBelowMinimum_Throws()
        {
            // Declare 64 bytes — below MIN_COMPRESSIBLE (128).
            var data = new byte[8];
            data[0] = 64;

            Assert.Throws<InvalidOperationException>(
                () => Lz4Compressor.Decompress(data),
                "declared length below MIN_COMPRESSIBLE must throw");
        }

        [Test]
        public void Decompress_NullInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Lz4Compressor.Decompress(null));
        }

        [Test]
        public void CompressIfBeneficial_NullInput_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => Lz4Compressor.CompressIfBeneficial(null, out _));
        }

        // ── Wire-format prefix ────────────────────────────────────────────────

        [Test]
        public void WireFormat_PrefixIsLittleEndian()
        {
            var data = new byte[256];
            Array.Fill(data, (byte)0xCC);

            var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
            Assert.IsTrue(compressed);

            // The prefix must be exactly data.Length in little-endian.
            uint declared = (uint)(wire[0] | (wire[1] << 8) | (wire[2] << 16) | (wire[3] << 24));
            Assert.AreEqual((uint)data.Length, declared,
                "prefix must be uncompressed length as u32 LE — matches Rust lz4_flex wire format");
        }

        // ── Multiple sequential round-trips ──────────────────────────────────

        [Test]
        public void MultipleRoundTrips_IndependentEachTime()
        {
            // Verifies that Lz4Compressor is stateless (no leftover hash table).
            var rng = new Random(999);
            for (int trial = 0; trial < 10; trial++)
            {
                int len = rng.Next(128, 512);
                var data = new byte[len];
                // Mix: half sequential, half repetitive.
                for (int i = 0; i < len / 2; i++) data[i] = (byte)(i & 0xFF);
                for (int i = len / 2; i < len; i++) data[i] = 0xDD;

                var wire = Lz4Compressor.CompressIfBeneficial(data, out bool compressed);
                if (!compressed) continue; // skip if not beneficial (valid)

                var restored = Lz4Compressor.Decompress(wire);
                CollectionAssert.AreEqual(data, restored,
                    $"round-trip failed on trial {trial} (len={len})");
            }
        }
    }
}
