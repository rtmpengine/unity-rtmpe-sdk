// RTMPE SDK — Runtime/Infrastructure/Compression/Lz4Compressor.cs
//
// Pure C# LZ4 Block compressor/decompressor, wire-format compatible with the
// Rust lz4_flex crate used by the RTMPE gateway.
//
// Wire format (matches gateway compression.rs):
//   [uncompressed_len: u32 LE][lz4_block: N bytes]
//
// Size constraints (must match gateway constants):
//   MIN_COMPRESSIBLE = 128 bytes  — below this, don't compress
//   MAX_DECOMPRESSED = 16384 bytes — gateway hard cap
//
// The LZ4 Block format implemented here is the canonical spec:
//   https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
//
// Compression note: This is a greedy single-pass compressor using a 4096-entry
// hash table.  It prioritises speed and zero-allocation in the hot path over
// maximum compression ratio — consistent with the latency requirements of a
// real-time game protocol.

using System;

namespace RTMPE.Infrastructure.Compression
{
    /// <summary>
    /// Stateless LZ4 Block compressor/decompressor.
    /// Wire-format compatible with the Rust lz4_flex crate.
    /// </summary>
    public static class Lz4Compressor
    {
        // Must match gateway compression.rs constants.
        public const int MinCompressible = 128;
        public const int MaxDecompressed = 16384;

        // Prefix size: u32 LE uncompressed length.
        private const int PrefixSize = 4;

        // Hash table size for the compressor (power of 2 for fast masking).
        private const int HashTableSize = 4096;
        private const int HashShift = 20; // 32 - log2(HashTableSize)

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Attempt to compress <paramref name="data"/> using LZ4 Block format.
        /// </summary>
        /// <param name="data">Input bytes.</param>
        /// <param name="compressed">
        ///   <see langword="true"/> when the output is smaller than the input
        ///   and compression was beneficial; <see langword="false"/> when the
        ///   original data should be sent as-is.
        /// </param>
        /// <returns>
        ///   The wire-format compressed payload (prefix + LZ4 block), or
        ///   <paramref name="data"/> unchanged when compression is not beneficial.
        /// </returns>
        public static byte[] CompressIfBeneficial(byte[] data, out bool compressed)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (data.Length < MinCompressible || data.Length > MaxDecompressed)
            {
                compressed = false;
                return data;
            }

            var candidate = Compress(data);
            // Only use compressed form if it's actually smaller.
            if (candidate.Length >= PrefixSize + data.Length)
            {
                compressed = false;
                return data;
            }

            compressed = true;
            return candidate;
        }

        /// <summary>
        /// Decompress a wire-format LZ4 Block payload (prefix + block).
        /// </summary>
        /// <param name="data">Wire-format bytes: [uncompressed_len:4 LE][lz4_block].</param>
        /// <returns>Decompressed bytes.</returns>
        /// <exception cref="InvalidOperationException">Malformed input or size violation.</exception>
        public static byte[] Decompress(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));

            if (data.Length < PrefixSize)
                throw new InvalidOperationException(
                    $"LZ4: payload too short ({data.Length} B), missing length prefix.");

            uint declaredLen = (uint)(
                data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24));

            if (declaredLen > MaxDecompressed)
                throw new InvalidOperationException(
                    $"LZ4: declared length {declaredLen} exceeds cap {MaxDecompressed}.");

            if (declaredLen < MinCompressible)
                throw new InvalidOperationException(
                    $"LZ4: declared length {declaredLen} below minimum {MinCompressible}.");

            var output = new byte[(int)declaredLen];
            int produced = DecompressBlock(data, PrefixSize, data.Length - PrefixSize,
                                           output, 0, (int)declaredLen);

            if (produced != (int)declaredLen)
                throw new InvalidOperationException(
                    $"LZ4: decompressed {produced} B but expected {declaredLen} B.");

            return output;
        }

        // ── LZ4 Block compressor ──────────────────────────────────────────────

        private static byte[] Compress(byte[] src)
        {
            int srcLen = src.Length;
            // Worst case: every byte is a literal (token + literal).
            // LZ4 worst-case expansion is src.Length + src.Length/255 + 16.
            int maxDst = PrefixSize + srcLen + (srcLen / 255) + 16;
            var dst = new byte[maxDst];

            // Write uncompressed length prefix.
            dst[0] = (byte) srcLen;
            dst[1] = (byte)(srcLen >>  8);
            dst[2] = (byte)(srcLen >> 16);
            dst[3] = (byte)(srcLen >> 24);

            int dstOff = PrefixSize;

            // Hash table: maps 4-byte hash → last position in src.
            var table = new int[HashTableSize];
            for (int i = 0; i < HashTableSize; i++) table[i] = -1;

            int srcOff   = 0;
            int litStart = 0;

            // Leave MFLIMIT = 12 bytes at the end as literals (LZ4 spec requirement).
            const int MfLimit = 12;
            int srcLimit = srcLen - MfLimit;

            while (srcOff < srcLimit)
            {
                // Hash the next 4 bytes.
                uint h = Hash4(src, srcOff);
                int  matchPos = table[h];
                table[h] = srcOff;

                // Check if a match exists and is within the 64KB distance limit.
                if (matchPos >= 0 && srcOff - matchPos < 65536 && matchPos >= 0)
                {
                    // Verify the 4-byte match.
                    if (src[matchPos]     == src[srcOff]     &&
                        src[matchPos + 1] == src[srcOff + 1] &&
                        src[matchPos + 2] == src[srcOff + 2] &&
                        src[matchPos + 3] == src[srcOff + 3])
                    {
                        // Extend match forward.
                        int matchLen = 4;
                        while (srcOff + matchLen < srcLen &&
                               src[matchPos + matchLen] == src[srcOff + matchLen])
                            matchLen++;

                        // Write the sequence: token + literals + offset + match extra.
                        int litLen = srcOff - litStart;
                        dstOff = WriteSequence(src, dst, dstOff,
                                               litStart, litLen,
                                               srcOff - matchPos,
                                               matchLen);

                        srcOff   += matchLen;
                        litStart  = srcOff;
                        continue;
                    }
                }

                srcOff++;
            }

            // Write remaining literals as a final sequence (no match).
            int finalLitLen = srcLen - litStart;
            dstOff = WriteFinalLiterals(src, dst, dstOff, litStart, finalLitLen);

            // Trim to actual compressed size.
            var result = new byte[dstOff];
            Buffer.BlockCopy(dst, 0, result, 0, dstOff);
            return result;
        }

        private static int WriteSequence(
            byte[] src, byte[] dst, int dstOff,
            int litStart, int litLen,
            int offset, int matchLen)
        {
            // Token byte: high nibble = literal run length (capped at 15),
            //             low  nibble = match extra length (matchLen - 4, capped at 15).
            int extraMatch = matchLen - 4; // min match is 4
            int tokenLit   = litLen  >= 15 ? 15 : litLen;
            int tokenMatch = extraMatch >= 15 ? 15 : extraMatch;

            dst[dstOff++] = (byte)((tokenLit << 4) | tokenMatch);

            // Extra literal length bytes.
            if (litLen >= 15)
            {
                int rem = litLen - 15;
                while (rem >= 255) { dst[dstOff++] = 255; rem -= 255; }
                dst[dstOff++] = (byte)rem;
            }

            // Literal bytes.
            Buffer.BlockCopy(src, litStart, dst, dstOff, litLen);
            dstOff += litLen;

            // Match offset (u16 LE).
            dst[dstOff++] = (byte) offset;
            dst[dstOff++] = (byte)(offset >> 8);

            // Extra match length bytes.
            if (extraMatch >= 15)
            {
                int rem = extraMatch - 15;
                while (rem >= 255) { dst[dstOff++] = 255; rem -= 255; }
                dst[dstOff++] = (byte)rem;
            }

            return dstOff;
        }

        private static int WriteFinalLiterals(
            byte[] src, byte[] dst, int dstOff,
            int litStart, int litLen)
        {
            // Final sequence: only literals, no match offset.
            int tokenLit = litLen >= 15 ? 15 : litLen;
            dst[dstOff++] = (byte)(tokenLit << 4); // low nibble = 0 (no match)

            if (litLen >= 15)
            {
                int rem = litLen - 15;
                while (rem >= 255) { dst[dstOff++] = 255; rem -= 255; }
                dst[dstOff++] = (byte)rem;
            }

            Buffer.BlockCopy(src, litStart, dst, dstOff, litLen);
            return dstOff + litLen;
        }

        private static uint Hash4(byte[] src, int off)
        {
            uint v = (uint)(src[off] | (src[off+1] << 8) | (src[off+2] << 16) | (src[off+3] << 24));
            return (v * 2654435761u) >> HashShift;
        }

        // ── LZ4 Block decompressor ────────────────────────────────────────────

        private static int DecompressBlock(
            byte[] src, int srcOff, int srcLen,
            byte[] dst, int dstOff, int dstLen)
        {
            int srcEnd = srcOff + srcLen;
            int dstStart = dstOff;

            while (srcOff < srcEnd)
            {
                // Read token.
                int token = src[srcOff++];
                int litLen   = (token >> 4) & 0x0F;
                int matchExtra = token & 0x0F;

                // Extended literal length.
                if (litLen == 15)
                {
                    int b;
                    do { b = src[srcOff++]; litLen += b; } while (b == 255);
                }

                // Copy literals.
                if (dstOff + litLen > dstLen) return -1; // overflow
                Buffer.BlockCopy(src, srcOff, dst, dstOff, litLen);
                srcOff += litLen;
                dstOff += litLen;

                // End-of-block: last sequence has no match.
                if (srcOff >= srcEnd) break;

                // Read match offset (u16 LE).
                int matchOffset = src[srcOff] | (src[srcOff + 1] << 8);
                srcOff += 2;
                if (matchOffset == 0) return -1; // invalid
                int matchSrc = dstOff - matchOffset;
                if (matchSrc < 0) return -1;     // out-of-bounds back-reference

                // Extended match length (base = 4).
                int matchLen = 4 + matchExtra;
                if (matchExtra == 15)
                {
                    int b;
                    do { b = src[srcOff++]; matchLen += b; } while (b == 255);
                }

                if (dstOff + matchLen > dstLen) return -1;

                // Copy match (may overlap — byte-by-byte to handle RLE correctly).
                for (int i = 0; i < matchLen; i++)
                    dst[dstOff + i] = dst[matchSrc + i];
                dstOff += matchLen;
            }

            return dstOff - dstStart;
        }
    }
}
