// RTMPE SDK — Runtime/Infrastructure/Compression/Lz4Compressor.cs
//
// Pure C# LZ4 Block compressor/decompressor, wire-format compatible with the
// Rust lz4_flex crate used by the RTMPE gateway.
//
// Wire format (matches gateway compression.rs):
//  [uncompressed_len: u32 LE][lz4_block: N bytes]
//
// Size constraints (must match gateway constants):
//  MIN_COMPRESSIBLE = 128 bytes  — below this, don't compress
//  MAX_DECOMPRESSED = 16384 bytes — gateway hard cap
//
// The LZ4 Block format implemented here is the canonical spec:
//  https://github.com/lz4/lz4/blob/dev/doc/lz4_Block_format.md
//
// Compression note: This is a greedy single-pass compressor using a 4096-entry
// hash table.  It prioritises speed and zero-allocation in the hot path over
// maximum compression ratio — consistent with the latency requirements of a
// real-time game protocol.

using System;
using System.Buffers;

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

        // Memory- and CPU-safety for decode rests on a single invariant: the
        // output never exceeds MaxDecompressed.  declaredLen is rejected above
        // that ceiling before allocation, the output buffer is sized to exactly
        // declaredLen, and every literal and match write is range-checked
        // against the remaining buffer in DecompressBlock — so total work is
        // bounded by the 16 KiB ceiling regardless of compression ratio or
        // individual match length.  The gateway's lz4_flex compressor applies
        // no ratio or per-match cap, and a highly compressible snapshot (e.g.
        // a near-idle StateSync full of default transforms) legitimately
        // exceeds any fixed ratio; this decoder therefore mirrors lz4_flex and
        // gates only on the decompressed-size ceiling, never on ratio or run
        // length, so it accepts every frame the gateway can produce.

        // Compile-time tripwire: MaxDecompressed must fit in a signed int so
        // the checked((int)declaredLen) cast in Decompress() is always safe.
        // Currently trivially true (MaxDecompressed = 16 384); the check
        // becomes meaningful if MaxDecompressed is ever widened to long.
        static Lz4Compressor()
        {
            if ((long)MaxDecompressed > int.MaxValue)
                throw new InvalidOperationException(
                    "MaxDecompressed exceeds int range — audit all (int) casts in this file.");
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Attempt to compress <paramref name="data"/> using LZ4 Block format.
        /// </summary>
        /// <param name="data">Input bytes.</param>
        /// <param name="compressed">
        ///  <see langword="true"/> when the output is smaller than the input
        ///  and compression was beneficial; <see langword="false"/> when the
        ///  original data should be sent as-is.
        /// </param>
        /// <returns>
        ///  The wire-format compressed payload (prefix + LZ4 block), or
        ///  <paramref name="data"/> unchanged when compression is not beneficial.
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

            // Reject empty/sub-minimum payloads.  The gateway never emits a
            // compressed frame for inputs below MinCompressible, so receiving
            // one indicates a crafted or corrupt packet.
            if (declaredLen < MinCompressible)
                throw new InvalidOperationException(
                    $"LZ4: declared length {declaredLen} below minimum {MinCompressible}.");

            int compressedPayload = data.Length - PrefixSize;
            if (compressedPayload <= 0)
                throw new InvalidOperationException("LZ4: compressed payload is empty.");

            // No ratio pre-check: declaredLen is already bounded by
            // MaxDecompressed (above) and the decode is range-checked write by
            // write, so a high ratio cannot amplify past the 16 KiB ceiling.
            // Gating on ratio here would reject legitimate highly-compressible
            // frames the gateway's uncapped lz4_flex compressor emits.
            var output = new byte[checked((int)declaredLen)];
            int produced = DecompressBlock(data, PrefixSize, compressedPayload,
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

            // Both the worst-case destination buffer and the hash table are
            // strictly transient and never escape this method; renting them
            // from the shared pool eliminates ~20 KB of per-call GC pressure
            // on iOS/Android where the SDK runs on Mono/IL2CPP.
            var pool = ArrayPool<byte>.Shared;
            var intPool = ArrayPool<int>.Shared;
            byte[] dst = pool.Rent(maxDst);
            int[]  table = intPool.Rent(HashTableSize);
            try
            {
                // Rented arrays are not zero-initialised; the hash table must
                // start as "no prior position" or stale entries from a previous
                // tenant would be interpreted as valid back-references and
                // corrupt the compressed stream.
                for (int i = 0; i < HashTableSize; i++) table[i] = -1;

                // Write uncompressed length prefix.
                dst[0] = (byte) srcLen;
                dst[1] = (byte)(srcLen >>  8);
                dst[2] = (byte)(srcLen >> 16);
                dst[3] = (byte)(srcLen >> 24);

                int dstOff = PrefixSize;

                int srcOff   = 0;
                int litStart = 0;

                // Leave MFLIMIT = 12 bytes at the end as literals (LZ4 spec requirement).
                const int MfLimit = 12;
                int srcLimit = srcLen - MfLimit;

                while (srcOff < srcLimit)
                {
                    // Hash the next 4 bytes.
                    uint h = Hash4(src, srcOff);
                    int  matchPos = table[(int)h];
                    table[(int)h] = srcOff;

                    // Check if a match exists and is within the 64KB distance limit.
                    if (matchPos >= 0 && srcOff - matchPos < 65536)
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

                // Trim to actual compressed size before returning ownership to caller.
                var result = new byte[dstOff];
                Buffer.BlockCopy(dst, 0, result, 0, dstOff);
                return result;
            }
            finally
            {
                // Clear before return so subsequent renters cannot read
                // residual compressed payload bytes from the shared pool —
                // the produced dst slice carries the just-compressed message,
                // and another component renting the same array would
                // otherwise observe the previous tenant's data.  The hash
                // table is re-initialised by every callee at line ~181 so
                // it does not need clearArray:true on return.
                pool.Return(dst, clearArray: true);
                intPool.Return(table);
            }
        }

        private static int WriteSequence(
            byte[] src, byte[] dst, int dstOff,
            int litStart, int litLen,
            int offset, int matchLen)
        {
            // Token byte: high nibble = literal run length (capped at 15),
            //            low  nibble = match extra length (matchLen - 4, capped at 15).
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

            // Bounds checks below use the overflow-safe form
            // `dstLen - dstOff < N` instead of `dstOff + N > dstLen`.
            // Extended literal/match lengths are decoded from attacker-supplied
            // 0xFF chains and can in theory grow large; the additive form would
            // wrap to a negative int and bypass the check.  Subtraction from
            // dstLen (which is bounded by MaxDecompressed) cannot overflow
            // because dstOff is always in [0, dstLen].

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
                    do {
                        if (srcOff >= srcEnd) return -1;
                        b = src[srcOff++]; litLen += b;
                        if (litLen < 0) return -1; // overflow guard on extended length
                    } while (b == 255);
                }

                // Copy literals.
                if (litLen < 0 || dstLen - dstOff < litLen) return -1; // overflow / out-of-bounds
                if (srcEnd - srcOff < litLen) return -1; // src truncated
                Buffer.BlockCopy(src, srcOff, dst, dstOff, litLen);
                srcOff += litLen;
                dstOff += litLen;

                // End-of-block: last sequence has no match.
                if (srcOff >= srcEnd) break;

                // Read match offset (u16 LE) — need exactly 2 bytes remaining.
                if (srcEnd - srcOff < 2) return -1;
                int matchOffset = src[srcOff] | (src[srcOff + 1] << 8);
                srcOff += 2;
                if (matchOffset == 0) return -1; // invalid
                int matchSrc = dstOff - matchOffset;
                // LZ4 back-references address bytes already produced by this
                // same decompression. A match that would read before the
                // start of the output buffer (matchSrc < 0) is invalid in
                // every LZ4 dialect and must be rejected.
                //
               // Importantly, this does NOT reject matches whose offset is
                // smaller than their length — those are the canonical LZ4
                // RLE construction (e.g. offset=1 with matchLen=N replicates
                // a single byte N times) and are required by the spec. The
                // overlap path below (`matchOffset < matchLen`) handles them
                // byte-by-byte; do not "tighten" this guard to forbid the
                // overlap or the run-length-encoded tail of every legitimate
                // packet will fail to decompress.
                if (matchSrc < 0) return -1;     // out-of-bounds back-reference

                // Extended match length (base = 4).
                int matchLen = 4 + matchExtra;
                if (matchExtra == 15)
                {
                    int b;
                    do {
                        if (srcOff >= srcEnd) return -1;
                        b = src[srcOff++]; matchLen += b;
                        if (matchLen < 0) return -1; // overflow guard on extended length
                    } while (b == 255);
                }

                // The match write is bounded by the remaining output buffer
                // (dstLen ≤ MaxDecompressed), which caps both memory and the
                // overlap byte-loop's total work across the block.  No
                // per-match length cap is applied — canonical lz4_flex emits
                // single matches up to the full block size for run-heavy data,
                // so capping them would reject valid gateway frames.  The
                // negative guard catches an overflowed 0xFF extension chain.
                if (matchLen < 0) return -1;
                if (dstLen - dstOff < matchLen) return -1;

                // Non-overlapping matches (offset >= length) cannot read bytes
                // we are about to write, so the entire run can be moved with a
                // bulk copy — orders of magnitude faster than the byte loop.
                // True overlap (offset < length) is the LZ4 RLE construction
                // and MUST proceed byte-by-byte: each output byte may depend on
                // a byte written earlier in this same match.
                if (matchOffset >= matchLen)
                {
                    Buffer.BlockCopy(dst, matchSrc, dst, dstOff, matchLen);
                }
                else
                {
                    for (int i = 0; i < matchLen; i++)
                        dst[dstOff + i] = dst[matchSrc + i];
                }
                dstOff += matchLen;
            }

            return dstOff - dstStart;
        }
    }
}
