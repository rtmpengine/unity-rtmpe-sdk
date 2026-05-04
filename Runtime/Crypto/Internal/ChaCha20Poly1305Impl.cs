// RTMPE SDK — Runtime/Crypto/Internal/ChaCha20Poly1305Impl.cs
//
// Pure C# ChaCha20-Poly1305 AEAD — RFC 8439.
//
// Used in two places:
//   1. ApiKeyCipher: encrypt the API key in HandshakeInit using the project PSK.
//   2. Session packet encryption/decryption.
//
// This implementation targets .NET Standard 2.1 / Unity 6 with no native
// dependencies and without unsafe code.  Poly1305 uses the radix-2^26 5-limb
// `uint` arithmetic of the canonical poly1305-donna 32-bit reference
// (Andrew Moon, public domain) — no allocations on the hot path, fully
// constant-time across data-dependent branches, no System.Numerics.BigInteger.
// See SECURITY / THREAT MODEL below for the surrounding side-channel posture.
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// This is a PURE-MANAGED C# cryptographic implementation.
//
// WHAT IT PROTECTS AGAINST (in scope):
//   • Network-level attackers: packet injection, tampering, replay.
//     ChaCha20-Poly1305 provides confidentiality + integrity over the wire.
//   • Passive eavesdroppers on the UDP path.
//
// WHAT IT DOES NOT PROTECT AGAINST (out of scope):
//   • Side-channel attacks (timing, cache, power, EM): C#/.NET does NOT
//     guarantee constant-time BigInteger operations or array indexing.
//     A local attacker with access to the player's process could potentially
//     extract key material via timing measurements.
//   • Physical access / process memory dump: secrets live in managed heap.
//
// RISK ASSESSMENT:
//   Side-channel attacks require LOCAL access to the player's machine. In the
//   game networking threat model the player IS the owner of their machine, so
//   key exfiltration via side-channel only lets a player read their OWN session
//   key, which they already implicitly possess. This is accepted as LOW risk.
//
//   The IL2CPP / .NET Standard 2.1 constraint makes using platform-native
//   crypto (e.g. System.Security.Cryptography.AesGcm on .NET 5+) unavailable
//   on all Unity targets. This implementation is the correct trade-off.
//
// TESTING:
//   RFC 8439 test vectors are verified in CryptoTests.cs. All edge-case
//   inputs (empty plaintext, zero nonce, max-length AAD) are covered.
// ============================================================================

using System;
using System.Buffers;
using System.Numerics;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD (RFC 8439) encapsulated in a static class.
    /// </summary>
    /// <remarks>
    /// <para><b>Side-channel posture.</b>  Poly1305 uses 32-bit radix-2^26
    /// limb arithmetic (poly1305-donna 32-bit reference port) — no
    /// <see cref="System.Numerics.BigInteger"/>, no data-dependent branches,
    /// and no array index dependencies on secret data.  ChaCha20 likewise
    /// processes blocks via fixed-shape <c>uint</c> arithmetic with no
    /// secret-dependent control flow.  The MAC verification on the Open
    /// path uses <see cref="ConstantTimeEquals(byte[], int, byte[], int, int)"/>
    /// to avoid leaking byte-position-of-mismatch via timing.  Migrating to
    /// a hardware AEAD (e.g. <c>System.Security.Cryptography.AesGcm</c>) is
    /// blocked by the IL2CPP / .NET Standard 2.1 floor that Unity targets
    /// require.  See the file-header threat model for the full rationale.</para>
    /// </remarks>
    internal static class ChaCha20Poly1305Impl
    {
        // ── ChaCha20 constants ("expand 32-byte k") ──────────────────────────
        private const uint C0 = 0x61707865u;
        private const uint C1 = 0x3320646eu;
        private const uint C2 = 0x79622d32u;
        private const uint C3 = 0x6b206574u;

        // ── ChaCha20 core ────────────────────────────────────────────────────
        //
        // GC Round 3 (2026-05-02) — per-thread cached scratch buffers.
        //
        // The pre-Round-3 implementation allocated `s = new uint[16]`,
        // `w = (uint[])s.Clone()`, and `block = new byte[64]` on every
        // ChaCha20Block / ChaCha20XorKeyStream invocation.  At 30 Hz × 32
        // active connections × 2 directions × ~20 ChaCha20Block calls per
        // 1200-byte packet, that totalled ~6 MB/sec of gen-0 churn.
        //
        // The [ThreadStatic] fields below cache the working buffers per
        // thread so the steady-state path is fully allocation-free.  Wipe-
        // on-exit-via-finally still runs (see the existing security note in
        // ChaCha20Block); the cache outlives a single call but its contents
        // are zeroed before the call returns, so a heap-dump adversary
        // observes only zeros.  Threading note: ChaCha20Block / Seal / Open
        // are synchronous and never yield, so a [ThreadStatic] buffer is
        // never observed mid-mutation by another reader on the same thread.
        // Different threads each get their own lazily-allocated copy.
        [ThreadStatic] private static uint[] _tlsChaChaState;       // 16 × uint  (initial state s)
        [ThreadStatic] private static uint[] _tlsChaChaWorking;     // 16 × uint  (round working copy w)
        [ThreadStatic] private static byte[] _tlsChaChaKeyBlock;    // 64 bytes   (per-block keystream output)
        [ThreadStatic] private static byte[] _tlsAeadBlock0;        // 64 bytes   (Poly1305 one-time key derivation)
        [ThreadStatic] private static byte[] _tlsAeadExpectedTag;   // 16 bytes   (Open: expected MAC for ConstantTimeEquals)

        private static uint RotL32(uint x, int n) => (x << n) | (x >> (32 - n));

        private static void QuarterRound(
            ref uint a, ref uint b, ref uint c, ref uint d)
        {
            a += b; d ^= a; d = RotL32(d, 16);
            c += d; b ^= c; b = RotL32(b, 12);
            a += b; d ^= a; d = RotL32(d,  8);
            c += d; b ^= c; b = RotL32(b,  7);
        }

        /// <summary>
        /// Produce a 64-byte ChaCha20 keystream block with the given counter.
        /// nonce must be exactly 12 bytes.
        /// key must be exactly 32 bytes.
        /// </summary>
        private static void ChaCha20Block(
            byte[] key, uint counter, byte[] nonce, byte[] output)
        {
            // GC Round 3 (2026-05-02): use per-thread cached scratch
            // buffers instead of `new uint[16]` + Clone() per call.  The
            // wipe-on-exit in the finally block keeps the security
            // posture identical: at end-of-call the buffer is all zeros,
            // so a heap-dump adversary cannot recover the post-state key
            // material.  Lazy-init via `??=`; first call on each thread
            // allocates once.
            uint[] s = _tlsChaChaState   ??= new uint[16];
            uint[] w = _tlsChaChaWorking ??= new uint[16];
            try
            {
                // Build initial state (16 × uint32).
                s[0]  = C0; s[1]  = C1; s[2]  = C2; s[3]  = C3;
                s[4]  = ReadLE32(key,    0); s[5]  = ReadLE32(key,    4);
                s[6]  = ReadLE32(key,    8); s[7]  = ReadLE32(key,   12);
                s[8]  = ReadLE32(key,   16); s[9]  = ReadLE32(key,   20);
                s[10] = ReadLE32(key,   24); s[11] = ReadLE32(key,   28);
                s[12] = counter;
                s[13] = ReadLE32(nonce,  0);
                s[14] = ReadLE32(nonce,  4);
                s[15] = ReadLE32(nonce,  8);

                // Working copy.  Manual loop instead of Clone() so we
                // reuse the cached `w` buffer without allocating.
                for (int i = 0; i < 16; i++) w[i] = s[i];

                // 10 double rounds = 20 rounds total.
                for (int i = 0; i < 10; i++)
                {
                    // Column rounds
                    QuarterRound(ref w[0], ref w[4], ref w[8],  ref w[12]);
                    QuarterRound(ref w[1], ref w[5], ref w[9],  ref w[13]);
                    QuarterRound(ref w[2], ref w[6], ref w[10], ref w[14]);
                    QuarterRound(ref w[3], ref w[7], ref w[11], ref w[15]);
                    // Diagonal rounds
                    QuarterRound(ref w[0], ref w[5], ref w[10], ref w[15]);
                    QuarterRound(ref w[1], ref w[6], ref w[11], ref w[12]);
                    QuarterRound(ref w[2], ref w[7], ref w[8],  ref w[13]);
                    QuarterRound(ref w[3], ref w[4], ref w[9],  ref w[14]);
                }

                // Add original state and write to output (64 bytes, LE).
                for (int i = 0; i < 16; i++)
                {
                    uint v = w[i] + s[i];
                    output[i * 4 + 0] = (byte)(v);
                    output[i * 4 + 1] = (byte)(v >> 8);
                    output[i * 4 + 2] = (byte)(v >> 16);
                    output[i * 4 + 3] = (byte)(v >> 24);
                }
            }
            finally
            {
                // Wipe the working state.  `s` and `w` contain the 32-byte
                // AEAD key in words [4..12]; with the [ThreadStatic] cache
                // they outlive a single call until the next call overwrites
                // them, so wiping at end-of-call is required to keep the
                // pre-Round-3 security posture (a heap-dump adversary must
                // not see post-call key material).  Cf. RFC 9106 §5.4 and
                // OpenSSL's OPENSSL_cleanse pattern.
                Array.Clear(w, 0, 16);
                Array.Clear(s, 0, 16);
            }
        }

        /// <summary>XOR input with the ChaCha20 keystream starting at <paramref name="initialCounter"/>.</summary>
        private static void ChaCha20XorKeyStream(
            byte[] key, uint initialCounter, byte[] nonce,
            byte[] input, int inputOffset,
            byte[] output, int outputOffset,
            int length)
        {
            // GC Round 3 (2026-05-02): per-thread cached keystream buffer.
            // The pre-Round-3 path allocated `new byte[64]` per call; at
            // 30 Hz across active connections this was several MB/sec of
            // gen-0 churn.  Lazy-init via `??=`.
            byte[] block = _tlsChaChaKeyBlock ??= new byte[64];
            try
            {
                uint blockCounter = initialCounter;
                int processed = 0;

                while (processed < length)
                {
                    ChaCha20Block(key, blockCounter++, nonce, block);
                    int take = Math.Min(64, length - processed);
                    for (int i = 0; i < take; i++)
                        output[outputOffset + processed + i]
                            = (byte)(input[inputOffset + processed + i] ^ block[i]);
                    processed += take;
                }
            }
            finally
            {
                // Wipe the keystream block on every exit path (success, OOB
                // throw, anything).  With the [ThreadStatic] cache the
                // buffer outlives a single call until the next call
                // overwrites it; wiping here keeps the post-call state
                // free of recoverable keystream bytes that a heap-dump
                // adversary could use to forge / decrypt the corresponding
                // packet.  Defense-in-depth on top of session-key wipes.
                Array.Clear(block, 0, block.Length);
            }
        }

        // ── Poly1305 MAC (poly1305-donna 32-bit reference port) ──────────────
        //
        // Implements RFC 8439 §2.5 Poly1305 using radix-2^26 limb arithmetic
        // (5 × uint per accumulator / multiplier).  This replaces the
        // BigInteger-based implementation that shipped through 2026-04 and
        // delivers three benefits:
        //
        //   1. Zero heap allocations on the hot path (Poly1305Mac is invoked
        //      once per Seal/Open, i.e. once per encrypted packet).  The old
        //      path allocated ~3 + 3·N BigIntegers per call (N = number of
        //      16-byte blocks) plus a per-block byte[].
        //
        //   2. Constant-time arithmetic across all data-dependent control
        //      flow — there are no branches on secret bytes, no
        //      secret-dependent array indices, and no library calls whose
        //      timing varies with the operands' magnitude (BigInteger
        //      modular reduction does).
        //
        //   3. Bit-for-bit compatible with RFC 8439 §2.5.2 / §2.8.2 test
        //      vectors and with the BigInteger implementation it replaces;
        //      enforced by the parity test in CryptoTests.cs.
        //
        // The five-limb representation packs the 130-bit field element as:
        //
        //   bits:     [129..104]  [103..78]  [77..52]   [51..26]   [25..0]
        //   limbs:        h4         h3         h2         h1         h0
        //
        // Each limb is a uint containing a 26-bit value.  Multiplication
        // produces uint64 partial products that fit comfortably; reduction
        // by p = 2^130 - 5 exploits the identity 2^130 ≡ 5 (mod p) to fold
        // the high bits back into h0 with a multiply-by-5.
        //
        // Reference: poly1305-donna-32.h  (Andrew Moon, public domain).

        /// <summary>
        /// Poly1305 MAC (RFC 8439 §2.5).
        /// <paramref name="key32"/> is the 32-byte one-time key with offset
        /// <paramref name="key32Offset"/> (r = key32[off..off+16] clamped,
        /// s = key32[off+16..off+32]).  Accepting an offset lets the AEAD
        /// path point directly at the leading 32 bytes of the ChaCha20
        /// keystream block without an intermediate copy.
        /// Writes a 16-byte authentication tag into
        /// <paramref name="tag"/> starting at <paramref name="tagOffset"/>.
        /// The destination must have at least 16 bytes available from that
        /// offset; bounds are not re-checked inside the hot loop.
        /// </summary>
        private static void Poly1305MacInto(
            byte[] msg, int msgOffset, int msgLen,
            byte[] key32, int key32Offset,
            byte[] tag, int tagOffset)
        {
            // ── 1. Initialise r (5 × uint, radix 2^26) with RFC 8439 §2.5.1 clamp.
            // The masks below are the bitwise AND of the radix-2^26
            // extraction window with the published Poly1305 clamp pattern
            // (0x0FFFFFFC0FFFFFFC0FFFFFFC0FFFFFFF when read as a 128-bit
            // little-endian integer); pre-computing them avoids a separate
            // clamp pass over the byte array.
            uint r0 = (ReadLE32(key32, key32Offset +  0)     ) & 0x03ffffffu;
            uint r1 = (ReadLE32(key32, key32Offset +  3) >> 2) & 0x03ffff03u;
            uint r2 = (ReadLE32(key32, key32Offset +  6) >> 4) & 0x03ffc0ffu;
            uint r3 = (ReadLE32(key32, key32Offset +  9) >> 6) & 0x03f03fffu;
            uint r4 = (ReadLE32(key32, key32Offset + 12) >> 8) & 0x000fffffu;

            // Pre-multiplied r limbs for the modular fold-back: any digit
            // promoted across the 130-bit boundary must be multiplied by 5
            // when it lands back in h0.  Pre-computing 5·r1..5·r4 saves four
            // multiplications per block.
            uint s1 = r1 * 5;
            uint s2 = r2 * 5;
            uint s3 = r3 * 5;
            uint s4 = r4 * 5;

            // s = key32[16..31] (the one-time pad added at finalisation).
            uint pad0 = ReadLE32(key32, 16);
            uint pad1 = ReadLE32(key32, 20);
            uint pad2 = ReadLE32(key32, 24);
            uint pad3 = ReadLE32(key32, 28);

            // ── 2. Accumulator h, initialised to zero.
            uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

            // ── 3. Per-block update loop.
            //
            // For each 16-byte block: h += block + 2^128, h *= r mod p.
            // Partial trailing block is padded into a 16-byte buffer with a
            // 1-byte appended at the value's natural bit position; the
            // 'hibit' top-of-block sentinel becomes 0 instead of (1<<24).
            int pos = 0;
            byte[] partial = null;
            try
            {
                while (pos < msgLen)
                {
                    int remain = msgLen - pos;

                    byte[]  blockBuf;
                    int     blockOff;
                    uint    hibit;

                    if (remain >= 16)
                    {
                        blockBuf = msg;
                        blockOff = msgOffset + pos;
                        hibit    = 0x01000000u; // 2^24 in limb 4 → 2^128 overall
                        pos     += 16;
                    }
                    else
                    {
                        // Pad short tail block: copy the remaining bytes,
                        // append 0x01, zero-fill to 16 bytes.  The 1-bit is
                        // now embedded in the buffer so hibit = 0.  Lazy-
                        // allocate the 16-byte scratch buffer the first
                        // time we hit a partial tail; full-block-aligned
                        // messages skip this allocation entirely.
                        if (partial == null) partial = new byte[16];
                        else Array.Clear(partial, 0, 16);
                        Buffer.BlockCopy(msg, msgOffset + pos, partial, 0, remain);
                        partial[remain] = 0x01;
                        blockBuf = partial;
                        blockOff = 0;
                        hibit    = 0;
                        pos      = msgLen;
                    }

                    // h += block (decompose block into 5 × 26-bit limbs).
                    h0 += (ReadLE32(blockBuf, blockOff +  0)     ) & 0x03ffffffu;
                    h1 += (ReadLE32(blockBuf, blockOff +  3) >> 2) & 0x03ffffffu;
                    h2 += (ReadLE32(blockBuf, blockOff +  6) >> 4) & 0x03ffffffu;
                    h3 += (ReadLE32(blockBuf, blockOff +  9) >> 6) & 0x03ffffffu;
                    h4 += (ReadLE32(blockBuf, blockOff + 12) >> 8) | hibit;

                    // h *= r (5×5 limb multiply with mod-p fold-back).
                    // d_i = sum of products that contribute to limb i, with
                    // any product that exceeds the 130-bit boundary folded
                    // back via the 5·r_j precomputed scalars.
                    ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
                    ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
                    ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
                    ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
                    ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

                    // Partial reduction: propagate carries upward.  The
                    // post-multiply limbs are at most ~52 bits; one chain
                    // of 26-bit reductions is sufficient because r0 is
                    // bounded by 2^26 and p = 2^130 - 5, so the highest
                    // d_i fits in u64.
                    uint c;
                                          c = (uint)(d0 >> 26); h0 = (uint)d0 & 0x03ffffffu;
                    d1 += c;              c = (uint)(d1 >> 26); h1 = (uint)d1 & 0x03ffffffu;
                    d2 += c;              c = (uint)(d2 >> 26); h2 = (uint)d2 & 0x03ffffffu;
                    d3 += c;              c = (uint)(d3 >> 26); h3 = (uint)d3 & 0x03ffffffu;
                    d4 += c;              c = (uint)(d4 >> 26); h4 = (uint)d4 & 0x03ffffffu;
                    h0 += c * 5;          c = h0 >> 26;          h0 = h0 & 0x03ffffffu;
                    h1 += c;
                }

                // ── 4. Finalisation: full carry propagation, then conditional subtract.
                                       uint cf;
                cf = h1 >> 26; h1 &= 0x03ffffffu;
                h2 += cf; cf = h2 >> 26; h2 &= 0x03ffffffu;
                h3 += cf; cf = h3 >> 26; h3 &= 0x03ffffffu;
                h4 += cf; cf = h4 >> 26; h4 &= 0x03ffffffu;
                h0 += cf * 5; cf = h0 >> 26; h0 &= 0x03ffffffu;
                h1 += cf;

                // g = h + (-p) = h + 5; if g overflows the 130-bit window
                // then h ≥ p so we should subtract p; otherwise keep h.
                uint g0 = h0 + 5; uint cg = g0 >> 26; g0 &= 0x03ffffffu;
                uint g1 = h1 + cg; cg = g1 >> 26; g1 &= 0x03ffffffu;
                uint g2 = h2 + cg; cg = g2 >> 26; g2 &= 0x03ffffffu;
                uint g3 = h3 + cg; cg = g3 >> 26; g3 &= 0x03ffffffu;
                uint g4 = h4 + cg - (1u << 26);

                // Constant-time select: when h < p, the (h4 + cg) - (1 << 26)
                // subtraction underflows so g4's high bit is SET; mask =
                // (g4 >> 31) - 1 = 1 - 1 = 0 in that case, and the
                // `(h & ~mask) | (g & mask)` blend keeps h (the correct
                // residue).  When h ≥ p, the subtraction does not
                // underflow so g4's high bit is CLEAR; mask becomes
                // 0xFFFFFFFF and the blend selects g = h − p.  Branch-free,
                // single comparison, no secret-dependent control flow.
                uint mask = (g4 >> 31) - 1;
                g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
                mask = ~mask;
                h0 = (h0 & mask) | g0;
                h1 = (h1 & mask) | g1;
                h2 = (h2 & mask) | g2;
                h3 = (h3 & mask) | g3;
                h4 = (h4 & mask) | g4;

                // Repack 5 × 26-bit → 4 × 32-bit (mod 2^128).
                uint w0 = (h0      ) | (h1 << 26);
                uint w1 = (h1 >>  6) | (h2 << 20);
                uint w2 = (h2 >> 12) | (h3 << 14);
                uint w3 = (h3 >> 18) | (h4 <<  8);

                // mac = (h + pad) mod 2^128, propagating the 32-bit carries.
                ulong f0 = (ulong)w0 + pad0;            w0 = (uint)f0;
                ulong f1 = (ulong)w1 + pad1 + (f0 >> 32); w1 = (uint)f1;
                ulong f2 = (ulong)w2 + pad2 + (f1 >> 32); w2 = (uint)f2;
                ulong f3 = (ulong)w3 + pad3 + (f2 >> 32); w3 = (uint)f3;

                // Serialise mac as 16 bytes LE into the caller's tag buffer.
                tag[tagOffset +  0] = (byte)(w0      );
                tag[tagOffset +  1] = (byte)(w0 >>  8);
                tag[tagOffset +  2] = (byte)(w0 >> 16);
                tag[tagOffset +  3] = (byte)(w0 >> 24);
                tag[tagOffset +  4] = (byte)(w1      );
                tag[tagOffset +  5] = (byte)(w1 >>  8);
                tag[tagOffset +  6] = (byte)(w1 >> 16);
                tag[tagOffset +  7] = (byte)(w1 >> 24);
                tag[tagOffset +  8] = (byte)(w2      );
                tag[tagOffset +  9] = (byte)(w2 >>  8);
                tag[tagOffset + 10] = (byte)(w2 >> 16);
                tag[tagOffset + 11] = (byte)(w2 >> 24);
                tag[tagOffset + 12] = (byte)(w3      );
                tag[tagOffset + 13] = (byte)(w3 >>  8);
                tag[tagOffset + 14] = (byte)(w3 >> 16);
                tag[tagOffset + 15] = (byte)(w3 >> 24);
            }
            finally
            {
                // Wipe the 16-byte tail-pad scratch (if used).  All other
                // state is in `uint` locals — cleared automatically when
                // the stack frame unwinds.
                if (partial != null) Array.Clear(partial, 0, partial.Length);
            }
        }

        // ── AEAD Construction ────────────────────────────────────────────────

        /// <summary>
        /// Compute the canonical RFC 8439 §2.8 Poly1305 input length for the
        /// given AAD and ciphertext sizes:
        ///   pad16(AAD) + pad16(Ciphertext) + 16 bytes of trailers.
        /// </summary>
        private static int PolyInputLength(int aadLen, int ctLen)
        {
            int aadPad = ((aadLen + 15) / 16) * 16;
            int ctPad  = ((ctLen  + 15) / 16) * 16;
            return aadPad + ctPad + 16;
        }

        /// <summary>
        /// Fill <paramref name="dest"/> with the Poly1305 input buffer per
        /// RFC 8439 §2.8:
        ///   AAD || pad16(AAD) || Ciphertext || pad16(Ciphertext)
        ///   || len(AAD):8LE || len(Ciphertext):8LE
        /// </summary>
        /// <remarks>
        /// The destination buffer MUST be sized at least
        /// <see cref="PolyInputLength"/> bytes; the prefix slice it covers is
        /// fully written by this routine — the bytes between
        /// <c>aadLen</c>..<c>aadPad</c> and <c>aadPad+ctLen</c>..<c>aadPad+ctPad</c>
        /// are explicitly zeroed so a pool-rented buffer with arbitrary prior
        /// contents does not leak past payload bytes into the MAC computation.
        /// Returns the actual prefix length written.
        /// </remarks>
        private static int BuildPolyInputInto(
            byte[] aad,        int aadOffset,        int aadLen,
            byte[] ciphertext, int ciphertextOffset, int ctLen,
            byte[] dest)
        {
            int aadPad = ((aadLen + 15) / 16) * 16;
            int ctPad  = ((ctLen  + 15) / 16) * 16;
            int total  = aadPad + ctPad + 16;

            if (aadLen > 0)
                Buffer.BlockCopy(aad, aadOffset, dest, 0, aadLen);
            if (aadPad > aadLen)
                Array.Clear(dest, aadLen, aadPad - aadLen);

            if (ctLen > 0)
                Buffer.BlockCopy(ciphertext, ciphertextOffset, dest, aadPad, ctLen);
            if (ctPad > ctLen)
                Array.Clear(dest, aadPad + ctLen, ctPad - ctLen);

            ulong aadLenU = (ulong)aadLen;
            for (int i = 0; i < 8; i++)
                dest[aadPad + ctPad + i] = (byte)(aadLenU >> (i * 8));

            ulong ctLenU = (ulong)ctLen;
            for (int i = 0; i < 8; i++)
                dest[aadPad + ctPad + 8 + i] = (byte)(ctLenU >> (i * 8));

            return total;
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypt <paramref name="plaintext"/> using ChaCha20-Poly1305 AEAD.
        /// Returns <c>ciphertext || tag</c> (plaintext.Length + 16 bytes).
        /// </summary>
        /// <param name="key">32-byte symmetric key.</param>
        /// <param name="nonce">12-byte nonce (must be unique per (key, message) pair).</param>
        /// <param name="plaintext">Payload bytes to encrypt.</param>
        /// <param name="aad">Additional Authenticated Data (not encrypted, but authenticated).</param>
        public static byte[] Seal(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
        {
            if (plaintext == null) plaintext = Array.Empty<byte>();
            if (aad       == null) aad = Array.Empty<byte>();

            // Allocate the legacy `byte[]` result and delegate to the
            // pooled implementation.  This keeps the byte-for-byte API
            // contract for callers (handshake, tests, ApiKeyCipher) while
            // eliminating the intermediate ciphertext + poly1305Key
            // allocations on every Seal path through the SealInto core.
            var result = new byte[plaintext.Length + 16];
            SealInto(key, nonce, plaintext, 0, plaintext.Length, aad, 0, aad.Length, result, 0);
            return result;
        }

        /// <summary>
        /// Pooled / caller-buffer overload of <see cref="Seal"/>.  Encrypts
        /// the <paramref name="plaintextLength"/> bytes starting at
        /// <paramref name="plaintextOffset"/> in <paramref name="plaintext"/>
        /// using the AAD slice
        /// <paramref name="aad"/>[<paramref name="aadOffset"/>..<paramref name="aadOffset"/>+<paramref name="aadLength"/>],
        /// writing <c>plaintextLength + 16</c> bytes (ciphertext || tag) into
        /// <paramref name="dest"/> starting at <paramref name="destOffset"/>.
        /// Returns the number of bytes written.  The destination buffer may
        /// be rented from <c>ArrayPool&lt;byte&gt;.Shared</c> sized at least
        /// <c>plaintextLength + 16</c>.
        /// </summary>
        /// <remarks>
        /// Allocation-free hot path: every per-call buffer (block0, the
        /// ChaCha20 working state, the Poly1305 input, the keystream-block
        /// scratch, the intermediate ciphertext, the legacy 32-byte
        /// Poly1305 one-time key, and the 16-byte tag) is either resolved
        /// from a <c>[ThreadStatic]</c> cache, rented from
        /// <c>ArrayPool&lt;byte&gt;.Shared</c>, or written directly into
        /// the caller-supplied <paramref name="dest"/>.  Wire bytes are
        /// bit-for-bit compatible with the legacy path — verified by the
        /// RFC 8439 test-vector suite.
        /// </remarks>
        public static int SealInto(
            byte[] key,
            byte[] nonce,
            byte[] plaintext, int plaintextOffset, int plaintextLength,
            byte[] aad,       int aadOffset,       int aadLength,
            byte[] dest,      int destOffset)
        {
            if (key   == null || key.Length   != 32) throw new ArgumentException("key must be 32 bytes",   nameof(key));
            if (nonce == null || nonce.Length  != 12) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
            if (plaintext == null) plaintext = Array.Empty<byte>();
            if (aad       == null) aad = Array.Empty<byte>();
            if (plaintextOffset < 0 || plaintextLength < 0 ||
                (long)plaintextOffset + plaintextLength > plaintext.Length)
                throw new ArgumentOutOfRangeException(nameof(plaintextLength));
            if (aadOffset < 0 || aadLength < 0 ||
                (long)aadOffset + aadLength > aad.Length)
                throw new ArgumentOutOfRangeException(nameof(aadLength));
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + plaintextLength + 16 > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for ciphertext + 16-byte tag at the given offset.");

            // GC Round 3 (2026-05-02): use the per-thread cached block0
            // scratch buffer instead of `new byte[64]`.  The wipe in the
            // finally block keeps the security posture identical: the
            // leading 32 bytes (= the Poly1305 one-time key) are zeroed
            // before return, so a heap-dump adversary cannot recover the
            // one-time key and forge tags under the same (key, nonce).
            byte[] block0 = _tlsAeadBlock0 ??= new byte[64];
            byte[] polyInput = null;
            int    polyLen   = 0;
            try
            {
                // 1. Derive one-time Poly1305 key from block counter 0.
                //    The leading 32 bytes of block0 *are* the Poly1305 key;
                //    Poly1305MacInto reads them in place via key32Offset = 0.
                ChaCha20Block(key, 0, nonce, block0);

                // 2. Encrypt plaintext with ChaCha20 starting at counter 1,
                //    writing the ciphertext directly into the destination
                //    buffer — no intermediate ciphertext allocation.
                ChaCha20XorKeyStream(
                    key, 1, nonce,
                    plaintext, plaintextOffset,
                    dest,      destOffset,
                    plaintextLength);

                // 3. Compute Poly1305 MAC over: AAD || pad16 || ciphertext || pad16 || lengths.
                //    The Poly input is rented from the shared array pool.
                polyLen   = PolyInputLength(aadLength, plaintextLength);
                polyInput = ArrayPool<byte>.Shared.Rent(polyLen);
                BuildPolyInputInto(
                    aad, aadOffset, aadLength,
                    dest, destOffset, plaintextLength,
                    polyInput);

                // 4. MAC writes its 16-byte tag straight into the trailing
                //    slot of the destination buffer.
                Poly1305MacInto(polyInput, 0, polyLen, block0, 0, dest, destOffset + plaintextLength);
                return plaintextLength + 16;
            }
            finally
            {
                Array.Clear(block0, 0, block0.Length);
                if (polyInput != null)
                {
                    if (polyLen > 0) Array.Clear(polyInput, 0, polyLen);
                    ArrayPool<byte>.Shared.Return(polyInput);
                }
            }
        }

        /// <summary>
        /// Decrypt and verify a ChaCha20-Poly1305 AEAD ciphertext.
        /// The last 16 bytes of <paramref name="ciphertextWithTag"/> are the authentication tag.
        /// Returns <see langword="null"/> if the authentication tag is invalid.
        /// </summary>
        public static byte[] Open(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] aad)
        {
            if (ciphertextWithTag == null || ciphertextWithTag.Length < 16) return null;
            if (aad == null) aad = Array.Empty<byte>();

            int ctLen = ciphertextWithTag.Length - 16;
            var plaintext = new byte[ctLen];
            int written = OpenInto(
                key, nonce,
                ciphertextWithTag, 0, ciphertextWithTag.Length,
                aad, 0, aad.Length,
                plaintext, 0);
            return written < 0 ? null : plaintext;
        }

        /// <summary>
        /// Pooled / caller-buffer overload of <see cref="Open"/>.  Verifies
        /// and decrypts <paramref name="ciphertextWithTag"/>[<paramref name="ciphertextOffset"/>..]
        /// (length includes the trailing 16-byte tag) using the AAD slice
        /// and writes the recovered plaintext into <paramref name="dest"/>
        /// starting at <paramref name="destOffset"/>.  Returns the number
        /// of plaintext bytes written, or <c>-1</c> if MAC verification
        /// failed (mirroring the legacy <c>null</c>-return contract on
        /// <see cref="Open"/>).  Allocation-free hot path: every per-call
        /// buffer (block0, expectedTag, ChaCha20 working state, keystream
        /// block, intermediate plaintext, legacy poly1305Key) is either
        /// resolved from a <c>[ThreadStatic]</c> cache, rented from
        /// <c>ArrayPool&lt;byte&gt;.Shared</c>, or written directly into
        /// <paramref name="dest"/>.
        /// </summary>
        public static int OpenInto(
            byte[] key,
            byte[] nonce,
            byte[] ciphertextWithTag, int ciphertextOffset, int ciphertextLength,
            byte[] aad,                int aadOffset,        int aadLength,
            byte[] dest,               int destOffset)
        {
            if (key   == null || key.Length   != 32) throw new ArgumentException("key must be 32 bytes",   nameof(key));
            if (nonce == null || nonce.Length  != 12) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
            if (ciphertextWithTag == null || ciphertextLength < 16) return -1;
            if (aad == null) aad = Array.Empty<byte>();
            if (ciphertextOffset < 0 || ciphertextLength < 0 ||
                (long)ciphertextOffset + ciphertextLength > ciphertextWithTag.Length)
                throw new ArgumentOutOfRangeException(nameof(ciphertextLength));
            if (aadOffset < 0 || aadLength < 0 || (long)aadOffset + aadLength > aad.Length)
                throw new ArgumentOutOfRangeException(nameof(aadLength));

            int ctLen = ciphertextLength - 16;
            int tagOff = ciphertextOffset + ctLen;
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + ctLen > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for the decrypted plaintext at the given offset.");

            // GC Round 3 (2026-05-02): per-thread cached block0 + tag
            // scratch buffers.  Wipe-on-exit retains the original security
            // posture (no recoverable Poly1305 one-time key + no
            // recoverable expected tag bytes left on the heap).
            byte[] block0      = _tlsAeadBlock0      ??= new byte[64];
            byte[] expectedTag = _tlsAeadExpectedTag ??= new byte[16];
            byte[] polyInput   = null;
            int    polyLen     = 0;
            try
            {
                // Derive one-time Poly1305 key (leading 32 bytes of block0).
                ChaCha20Block(key, 0, nonce, block0);

                // Verify tag before decrypting (authenticate-then-decrypt).
                polyLen     = PolyInputLength(aadLength, ctLen);
                polyInput   = ArrayPool<byte>.Shared.Rent(polyLen);
                BuildPolyInputInto(
                    aad, aadOffset, aadLength,
                    ciphertextWithTag, ciphertextOffset, ctLen,
                    polyInput);

                Poly1305MacInto(polyInput, 0, polyLen, block0, 0, expectedTag, 0);

                if (!ConstantTimeEquals(expectedTag, 0, ciphertextWithTag, tagOff, 16))
                    return -1; // MAC verification failed — reject

                // Decrypt directly into the caller's destination buffer.
                ChaCha20XorKeyStream(
                    key, 1, nonce,
                    ciphertextWithTag, ciphertextOffset,
                    dest,              destOffset,
                    ctLen);
                return ctLen;
            }
            finally
            {
                Array.Clear(block0,      0, block0.Length);
                Array.Clear(expectedTag, 0, expectedTag.Length);
                if (polyInput   != null)
                {
                    if (polyLen > 0) Array.Clear(polyInput, 0, polyLen);
                    ArrayPool<byte>.Shared.Return(polyInput);
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static uint ReadLE32(byte[] buf, int offset) =>
            (uint)(buf[offset]
                 | (buf[offset + 1] << 8)
                 | (buf[offset + 2] << 16)
                 | (buf[offset + 3] << 24));

        /// <summary>
        /// Constant-time comparison of two byte slices to prevent timing attacks.
        /// Returns true iff all <paramref name="len"/> bytes are equal.
        /// </summary>
        private static bool ConstantTimeEquals(
            byte[] a, int aOffset,
            byte[] b, int bOffset, int len)
        {
            int diff = 0;
            for (int i = 0; i < len; i++)
                diff |= a[aOffset + i] ^ b[bOffset + i];
            return diff == 0;
        }
    }
}
