// RTMPE SDK — Runtime/Crypto/Internal/ChaCha20Poly1305Impl.cs
//
// Pure C# ChaCha20-Poly1305 AEAD — RFC 8439.
//
// Used in two places:
//   1. ApiKeyCipher: encrypt the API key in HandshakeInit using the project PSK.
//   2. Session packet encryption/decryption.
//
// This implementation targets .NET Standard 2.1 / Unity 6 with no native
// dependencies and without unsafe code. BigInteger is used for Poly1305
// 130-bit accumulator arithmetic; it is invoked on every Seal/Open call
// (i.e. on every encrypted packet, not only during the handshake).
// System.Numerics.BigInteger is not constant-time — see SECURITY / THREAT MODEL below.
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
using System.Numerics;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD (RFC 8439) encapsulated in a static class.
    /// </summary>
    internal static class ChaCha20Poly1305Impl
    {
        // ── ChaCha20 constants ("expand 32-byte k") ──────────────────────────
        private const uint C0 = 0x61707865u;
        private const uint C1 = 0x3320646eu;
        private const uint C2 = 0x79622d32u;
        private const uint C3 = 0x6b206574u;

        // ── ChaCha20 core ────────────────────────────────────────────────────

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
            // Build initial state (16 × uint32).
            var s = new uint[16];
            s[0]  = C0; s[1]  = C1; s[2]  = C2; s[3]  = C3;
            s[4]  = ReadLE32(key,    0); s[5]  = ReadLE32(key,    4);
            s[6]  = ReadLE32(key,    8); s[7]  = ReadLE32(key,   12);
            s[8]  = ReadLE32(key,   16); s[9]  = ReadLE32(key,   20);
            s[10] = ReadLE32(key,   24); s[11] = ReadLE32(key,   28);
            s[12] = counter;
            s[13] = ReadLE32(nonce,  0);
            s[14] = ReadLE32(nonce,  4);
            s[15] = ReadLE32(nonce,  8);

            // Working copy.
            var w = (uint[])s.Clone();

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

        /// <summary>XOR input with the ChaCha20 keystream starting at <paramref name="initialCounter"/>.</summary>
        private static void ChaCha20XorKeyStream(
            byte[] key, uint initialCounter, byte[] nonce,
            byte[] input, int inputOffset,
            byte[] output, int outputOffset,
            int length)
        {
            var block = new byte[64];
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

        // ── Poly1305 MAC ─────────────────────────────────────────────────────

        // p = 2^130 - 5
        private static readonly BigInteger Poly1305Prime = (BigInteger.One << 130) - 5;

        /// <summary>
        /// Poly1305 MAC (RFC 8439 §2.5).
        /// <paramref name="key32"/> is the 32-byte one-time key
        ///   (r = key32[0..15] clamped, s = key32[16..31]).
        /// Returns a 16-byte authentication tag.
        /// </summary>
        private static byte[] Poly1305Mac(byte[] msg, int msgOffset, int msgLen, byte[] key32)
        {
            // r = key32[0..15] with clamped bits (RFC 8439 §2.5.1).
            var rBytes = new byte[17]; // 16 data + 1 sign byte for BigInteger
            Buffer.BlockCopy(key32, 0, rBytes, 0, 16);
            rBytes[3]  &= 0x0F;
            rBytes[7]  &= 0x0F;
            rBytes[11] &= 0x0F;
            rBytes[15] &= 0x0F;
            rBytes[4]  &= 0xFC;
            rBytes[8]  &= 0xFC;
            rBytes[12] &= 0xFC;
            // rBytes[16] = 0x00 ensures BigInteger is non-negative (LE two's complement sign bit).

            var sBytes = new byte[17]; // 16 data + 1 sign byte
            Buffer.BlockCopy(key32, 16, sBytes, 0, 16);

            var r = new BigInteger(rBytes); // LE, non-negative
            var s = new BigInteger(sBytes); // LE, non-negative
            var p = Poly1305Prime;

            var acc = BigInteger.Zero;
            int pos = 0;

            while (pos < msgLen)
            {
                int blockLen = Math.Min(16, msgLen - pos);
                // Build the LE integer: message block with 1-bit appended.
                // n = LE(msg[pos..pos+blockLen]) + 2^(8*blockLen)
                var block = new byte[blockLen + 2]; // +1 for the "1" bit, +1 for BigInteger sign
                Buffer.BlockCopy(msg, msgOffset + pos, block, 0, blockLen);
                block[blockLen] = 0x01; // append the 1 bit (= 2^(8*blockLen) in integer)
                // block[blockLen+1] = 0x00 (default, positive sign for BigInteger)

                acc = (acc + new BigInteger(block)) * r % p;
                pos += 16;
            }

            // Final accumulator: (acc + s) mod 2^128, serialised as 16 bytes LE.
            acc = (acc + s) & ((BigInteger.One << 128) - 1);

            var result = acc.ToByteArray(); // LE, may have trailing sign byte
            var tag = new byte[16];
            int copy = Math.Min(result.Length, 16);
            Buffer.BlockCopy(result, 0, tag, 0, copy);
            // Remaining bytes of tag are already 0 (C# default).
            return tag;
        }

        // ── AEAD Construction ────────────────────────────────────────────────

        /// <summary>
        /// Build the Poly1305 input buffer per RFC 8439 §2.8:
        ///   AAD || pad16(AAD) || Ciphertext || pad16(Ciphertext)
        ///   || len(AAD):8LE || len(Ciphertext):8LE
        /// </summary>
        private static byte[] BuildPolyInput(
            byte[] aad,        int aadLen,
            byte[] ciphertext, int ctLen)
        {
            int aadPad = ((aadLen + 15) / 16) * 16;
            int ctPad  = ((ctLen  + 15) / 16) * 16;
            var buf    = new byte[aadPad + ctPad + 16];

            if (aadLen > 0)
                Buffer.BlockCopy(aad, 0, buf, 0, aadLen);
            if (ctLen > 0)
                Buffer.BlockCopy(ciphertext, 0, buf, aadPad, ctLen);

            // len(aad) as 8-byte LE uint64
            ulong aadLenU = (ulong)aadLen;
            for (int i = 0; i < 8; i++)
                buf[aadPad + ctPad + i] = (byte)(aadLenU >> (i * 8));

            // len(ciphertext) as 8-byte LE uint64
            ulong ctLenU = (ulong)ctLen;
            for (int i = 0; i < 8; i++)
                buf[aadPad + ctPad + 8 + i] = (byte)(ctLenU >> (i * 8));

            return buf;
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
            if (key   == null || key.Length   != 32) throw new ArgumentException("key must be 32 bytes",   nameof(key));
            if (nonce == null || nonce.Length  != 12) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
            if (plaintext == null) plaintext = Array.Empty<byte>();
            if (aad       == null) aad = Array.Empty<byte>();

            // 1. Derive one-time Poly1305 key from block counter 0 (first 32 bytes).
            var block0 = new byte[64];
            ChaCha20Block(key, 0, nonce, block0);
            var poly1305Key = new byte[32];
            Buffer.BlockCopy(block0, 0, poly1305Key, 0, 32);

            // 2. Encrypt plaintext with ChaCha20 starting at counter 1.
            var ciphertext = new byte[plaintext.Length];
            ChaCha20XorKeyStream(key, 1, nonce, plaintext, 0, ciphertext, 0, plaintext.Length);

            // 3. Compute Poly1305 MAC over: AAD || pad16 || ciphertext || pad16 || lengths.
            var polyInput = BuildPolyInput(aad, aad.Length, ciphertext, ciphertext.Length);
            var tag = Poly1305Mac(polyInput, 0, polyInput.Length, poly1305Key);

            // 4. Return ciphertext || tag.
            var result = new byte[ciphertext.Length + 16];
            Buffer.BlockCopy(ciphertext, 0, result, 0, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, ciphertext.Length, 16);
            return result;
        }

        /// <summary>
        /// Decrypt and verify a ChaCha20-Poly1305 AEAD ciphertext.
        /// The last 16 bytes of <paramref name="ciphertextWithTag"/> are the authentication tag.
        /// Returns <see langword="null"/> if the authentication tag is invalid.
        /// </summary>
        public static byte[] Open(byte[] key, byte[] nonce, byte[] ciphertextWithTag, byte[] aad)
        {
            if (key   == null || key.Length   != 32) throw new ArgumentException("key must be 32 bytes",   nameof(key));
            if (nonce == null || nonce.Length  != 12) throw new ArgumentException("nonce must be 12 bytes", nameof(nonce));
            if (ciphertextWithTag == null || ciphertextWithTag.Length < 16) return null;
            if (aad == null) aad = Array.Empty<byte>();

            int ctLen = ciphertextWithTag.Length - 16;

            // Derive one-time Poly1305 key.
            var block0 = new byte[64];
            ChaCha20Block(key, 0, nonce, block0);
            var poly1305Key = new byte[32];
            Buffer.BlockCopy(block0, 0, poly1305Key, 0, 32);

            // Verify tag before decrypting (authenticate-then-decrypt).
            var polyInput = BuildPolyInput(aad, aad.Length, ciphertextWithTag, ctLen);
            var expectedTag = Poly1305Mac(polyInput, 0, polyInput.Length, poly1305Key);

            if (!ConstantTimeEquals(expectedTag, 0, ciphertextWithTag, ctLen, 16))
                return null; // MAC verification failed — reject

            // Decrypt.
            var plaintext = new byte[ctLen];
            ChaCha20XorKeyStream(key, 1, nonce, ciphertextWithTag, 0, plaintext, 0, ctLen);
            return plaintext;
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
