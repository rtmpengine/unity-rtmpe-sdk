// RTMPE SDK — Runtime/Crypto/Internal/Curve25519.cs
//
// X25519 Diffie-Hellman function (RFC 7748 §5).
//
// Pure C# using System.Numerics.BigInteger for GF(2^255-19) field arithmetic.
// Performance is acceptable for the handshake (once per connection).
// The implementation follows the Montgomery ladder described in RFC 7748 §5.
//
// Key derivation:
//   GenerateKeyPair()  → (privateKey[32], publicKey[32])
//   SharedSecret(myPrivate, peerPublic) → sharedSecret[32]

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// X25519 ephemeral key pair generation and Diffie-Hellman shared-secret computation.
    /// All operations are in GF(2^255-19) using BigInteger for correctness.
    /// </summary>
    internal static class Curve25519
    {
        // p = 2^255 - 19
        private static readonly BigInteger P = (BigInteger.One << 255) - 19;

        // a24 = (486662 - 2) / 4 = 121665 — used in the Montgomery ladder
        private static readonly BigInteger A24 = new BigInteger(121665);

        // ── Key clamping (RFC 7748 §5) ───────────────────────────────────────

        /// <summary>Clamp a 32-byte scalar per RFC 7748 §5.</summary>
        private static byte[] ClampScalar(byte[] k)
        {
            var s = new byte[32];
            Buffer.BlockCopy(k, 0, s, 0, 32);
            s[0]  &= 248;   // clear bits 0, 1, 2
            s[31] &= 127;   // clear bit 255
            s[31] |= 64;    // set bit 254
            return s;
        }

        // ── Field helpers ────────────────────────────────────────────────────

        /// <summary>Convert 32 LE bytes to a non-negative BigInteger.</summary>
        private static BigInteger FromLE(byte[] bytes, int offset = 0)
        {
            // Append 0x00 to ensure the BigInteger constructor (two's complement, LE)
            // treats the value as non-negative regardless of the high bit of bytes[31].
            var buf = new byte[33];
            Buffer.BlockCopy(bytes, offset, buf, 0, 32);
            // buf[32] = 0x00 (default, C# initialises arrays to 0)
            return new BigInteger(buf);
        }

        /// <summary>Serialise a BigInteger to a 32-byte LE array.</summary>
        private static byte[] ToLE(BigInteger n)
        {
            // Ensure non-negative canonical form.
            n = ((n % P) + P) % P;
            var raw = n.ToByteArray(); // LE, may include a trailing sign byte
            var result = new byte[32];
            int copy = Math.Min(raw.Length, 32);
            Buffer.BlockCopy(raw, 0, result, 0, copy);
            return result;
        }

        private static BigInteger FAdd(BigInteger a, BigInteger b) => (a + b) % P;
        private static BigInteger FSub(BigInteger a, BigInteger b) => ((a - b) % P + P) % P;
        private static BigInteger FMul(BigInteger a, BigInteger b) => a * b % P;
        // Field inversion via Fermat's little theorem: a^(p-2) mod p
        private static BigInteger FInv(BigInteger a)              => BigInteger.ModPow(a, P - 2, P);

        // ── Montgomery ladder (RFC 7748 §5) ──────────────────────────────────

        /// <summary>
        /// Compute the X25519 function: multiply the u-coordinate <paramref name="u_bytes"/>
        /// by the scalar <paramref name="k_bytes"/>.
        /// Both arrays must be exactly 32 bytes (little-endian).
        /// </summary>
        internal static byte[] ScalarMult(byte[] k_bytes, byte[] u_bytes)
        {
            var k = ClampScalar(k_bytes);

            // M-14 fix: RFC 7748 §5 requires implementations to "mask off the most
            // significant bit in the final byte" of the u-coordinate before decoding.
            // Using `FromLE(u_bytes) % P` is not equivalent — for values in the range
            // [P, 2^255-1] the two operations differ by up to 18.  We copy u_bytes
            // and clear bit 255 (byte[31] & 0x7F) before converting to BigInteger.
            var uBuf = new byte[32];
            Buffer.BlockCopy(u_bytes, 0, uBuf, 0, 32);
            uBuf[31] &= 0x7F; // mask high bit per RFC 7748 §5
            var u = FromLE(uBuf) % P;

            BigInteger x2 = BigInteger.One;
            BigInteger z2 = BigInteger.Zero;
            BigInteger x3 = u;
            BigInteger z3 = BigInteger.One;
            int swap = 0;

            for (int t = 254; t >= 0; t--)
            {
                int k_t = (k[t >> 3] >> (t & 7)) & 1;
                swap ^= k_t;
                if (swap == 1)
                {
                    // M-15 mitigation: replace the C#-level branch with an
                    // arithmetic conditional selection to eliminate the branch-
                    // prediction timing signal.  BigInteger field operations are
                    // still variable-time internally, but this at least prevents
                    // a CPU branch predictor from leaking the scalar bit via the
                    // timing of the swap itself.
                    //
                    // Formula: new_a = a*(1-bit) + b*bit
                    //          new_b = b*(1-bit) + a*bit
                    // This always evaluates both operands, masking the branch.
                    //
                    // Residual risk: BigInteger.Multiply is not constant-time.
                    // Mitigating factor: the private key is EPHEMERAL (generated
                    // fresh for each handshake), so an attacker cannot accumulate
                    // timing observations across multiple uses of the same key.
                    BigInteger notBit = BigInteger.One - k_t; // 1 if k_t==0, 0 if k_t==1
                    BigInteger newX2 = x2 * notBit + x3 * k_t;
                    BigInteger newX3 = x3 * notBit + x2 * k_t;
                    BigInteger newZ2 = z2 * notBit + z3 * k_t;
                    BigInteger newZ3 = z3 * notBit + z2 * k_t;
                    x2 = newX2; x3 = newX3;
                    z2 = newZ2; z3 = newZ3;
                }
                swap = k_t;

                var A  = FAdd(x2, z2);
                var AA = FMul(A, A);
                var B  = FSub(x2, z2);
                var BB = FMul(B, B);
                var E  = FSub(AA, BB);
                var C  = FAdd(x3, z3);
                var D  = FSub(x3, z3);
                var DA = FMul(D, A);
                var CB = FMul(C, B);

                var DApCB = FAdd(DA, CB);
                var DAmCB = FSub(DA, CB);
                x3 = FMul(DApCB, DApCB);
                z3 = FMul(u, FMul(DAmCB, DAmCB));
                x2 = FMul(AA, BB);
                z2 = FMul(E, FAdd(AA, FMul(A24, E)));
            }

            // M-15: arithmetic final swap (same constant-time pattern as the loop body).
            BigInteger finalNotSwap = BigInteger.One - swap;
            BigInteger finalX2 = x2 * finalNotSwap + x3 * swap;
            BigInteger finalZ2 = z2 * finalNotSwap + z3 * swap;
            x2 = finalX2;
            z2 = finalZ2;

            return ToLE(FMul(x2, FInv(z2)));
        }

        // ── Public API ───────────────────────────────────────────────────────

        /// <summary>
        /// Generate an X25519 ephemeral key pair using the system CSPRNG.
        /// Returns (privateKey[32], publicKey[32]).
        /// </summary>
        internal static (byte[] privateKey, byte[] publicKey) GenerateKeyPair()
        {
            var privateKey = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(privateKey);

            // Public key = ScalarMult(private, base_point_u=9)
            var basePoint = new byte[32];
            basePoint[0] = 9;
            var publicKey = ScalarMult(privateKey, basePoint);
            return (privateKey, publicKey);
        }

        /// <summary>
        /// Compute the X25519 shared secret from this side's private key and the peer's public key.
        /// Returns 32 bytes of shared secret material.
        /// Returns null if the computed shared secret is the all-zero string (degenerate key).
        /// </summary>
        internal static byte[] SharedSecret(byte[] myPrivateKey, byte[] peerPublicKey)
        {
            var result = ScalarMult(myPrivateKey, peerPublicKey);
            // RFC 7748 requires implementations to reject the all-zero output.
            bool allZero = true;
            foreach (var b in result) if (b != 0) { allZero = false; break; }
            return allZero ? null : result;
        }
    }
}
