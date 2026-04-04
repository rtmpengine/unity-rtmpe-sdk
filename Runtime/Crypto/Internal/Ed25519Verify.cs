// RTMPE SDK — Runtime/Crypto/Internal/Ed25519Verify.cs
//
// Ed25519 signature VERIFICATION (RFC 8032 §5.1.7).
//
// This implements verification only (not signing — the server signs, the client verifies).
// All arithmetic is in GF(2^255-19) or on the Edwards25519 group.
//
// Used in the W6 handshake: verify sign(server_static_privkey, server_ephemeral_pub)
// before proceeding with ECDH, to prevent man-in-the-middle key substitution (H4 fix).
//
// Pure C# — no native dependencies. System.Security.Cryptography.SHA512 is used
// for the SHA-512 hash, which is available in .NET Standard 2.1.

using System;
using System.Numerics;
using System.Security.Cryptography;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// Ed25519 signature verification (RFC 8032 §5.1).
    /// </summary>
    internal static class Ed25519Verify
    {
        // ── Field / curve constants ─────────────────────────────────────────

        // p = 2^255 - 19
        private static readonly BigInteger P = (BigInteger.One << 255) - 19;

        // Group order l = 2^252 + 27742317777372353535851937790883648493
        private static readonly BigInteger L = (BigInteger.One << 252)
            + BigInteger.Parse("27742317777372353535851937790883648493");

        // d = -121665/121666 mod p
        // Pre-computed decimal value from RFC 8032 §5.1.
        private static readonly BigInteger D =
            BigInteger.Parse("37095705934669439343138083508754565189542113879843219016388785533085940283555");

        // sqrt(-1) mod p = 2^((p-1)/4) mod p
        private static readonly BigInteger SqrtM1 =
            BigInteger.Parse("19681161376707505956807079304988542015446066515923890162744021073123829784752");

        // Base point (Bx, By) from RFC 8032
        private static readonly BigInteger By =
            BigInteger.Parse("46316835694926478169428394003475163141307993866256225615783033011972563625558");
        private static readonly BigInteger Bx =
            BigInteger.Parse("15112221349535807912866137220509078750507884956996801189549095605099360729027");

        // ── Point representation ─────────────────────────────────────────────

        // Extended homogeneous coordinates (X, Y, Z, T) where x = X/Z, y = Y/Z, T = x*y.
        private struct Point
        {
            public BigInteger X, Y, Z, T;

            public static readonly Point Identity = new Point
            {
                X = BigInteger.Zero,
                Y = BigInteger.One,
                Z = BigInteger.One,
                T = BigInteger.Zero
            };
        }

        // ── Field helpers ────────────────────────────────────────────────────

        private static BigInteger FMod(BigInteger a) => ((a % P) + P) % P;
        private static BigInteger FAdd(BigInteger a, BigInteger b) => FMod(a + b);
        private static BigInteger FSub(BigInteger a, BigInteger b) => FMod(a - b);
        private static BigInteger FMul(BigInteger a, BigInteger b) => FMod(a * b);
        private static BigInteger FInv(BigInteger a)               => BigInteger.ModPow(a, P - 2, P);

        // ── Edwards25519 point operations ────────────────────────────────────

        /// <summary>Extended twisted Edwards point addition (RFC 8032 §5.1.4).</summary>
        private static Point PointAdd(Point P1, Point P2)
        {
            var A = FMul(FSub(P1.Y, P1.X), FSub(P2.Y, P2.X));
            var B = FMul(FAdd(P1.Y, P1.X), FAdd(P2.Y, P2.X));
            var C = FMul(FMul(P1.T, 2 * D % P), P2.T);
            var Dv = FMul(FMul(P1.Z, 2), P2.Z);
            var E  = FSub(B, A);
            var F  = FSub(Dv, C);
            var G  = FAdd(Dv, C);
            var H  = FAdd(B, A);
            return new Point
            {
                X = FMul(E, F),
                Y = FMul(G, H),
                Z = FMul(F, G),
                T = FMul(E, H)
            };
        }

        /// <summary>Extended twisted Edwards point doubling (RFC 8032 §5.1.4).</summary>
        private static Point PointDouble(Point P1)
        {
            // Formulas: dbl-2008-hwcd
            // A = X1^2, B = Y1^2, C = 2*Z1^2, H = A+B
            // E = H-(X1+Y1)^2, G = A-B, F = C+G
            // X3 = E*F, Y3 = G*H, Z3 = F*G, T3 = E*H
            var A    = FMul(P1.X, P1.X);
            var B    = FMul(P1.Y, P1.Y);
            var C    = FMul(FMul(2, P1.Z), P1.Z);
            var H    = FAdd(A, B);
            var xpy  = FMod(P1.X + P1.Y);
            var E    = FSub(H, FMul(xpy, xpy));
            var G    = FSub(A, B);
            var F    = FAdd(C, G);
            return new Point
            {
                X = FMul(E, F),
                Y = FMul(G, H),
                Z = FMul(F, G),
                T = FMul(E, H)
            };
        }

        /// <summary>
        /// Scalar multiplication on Edwards25519 using the double-and-add method
        /// (processes scalar bits from most significant to least significant).
        /// </summary>
        private static Point ScalarMult(Point pt, BigInteger n)
        {
            // Guard: multiplying by 0 returns the identity (neutral element).
            // Also prevents BigInteger.Log(0, 2) ArgumentOutOfRangeException on
            // adversarial inputs such as an all-zero S value in the signature.
            if (n == BigInteger.Zero) return Point.Identity;

            var Q = Point.Identity;
            int bits = (int)BigInteger.Log(n, 2) + 1;

            for (int i = bits - 1; i >= 0; i--)
            {
                Q = PointDouble(Q);
                if ((n >> i & BigInteger.One) == BigInteger.One)
                    Q = PointAdd(Q, pt);
            }
            return Q;
        }

        // ── Point encoding / decoding ─────────────────────────────────────────

        private static readonly Point BasePoint = new Point
        {
            X = FMod(Bx),
            Y = FMod(By),
            Z = BigInteger.One,
            T = FMod(FMul(Bx, By))
        };

        /// <summary>
        /// Decode a compressed 32-byte Ed25519 point per RFC 8032 §5.1.3.
        /// Returns false if the encoding is invalid.
        /// </summary>
        private static bool DecodePoint(byte[] s, out Point pt)
        {
            pt = default;
            if (s.Length != 32) return false;

            // Copy so we can mask the sign bit without modifying the input.
            var buf = (byte[])s.Clone();
            int xSign = (buf[31] >> 7) & 1;
            buf[31] &= 0x7F; // clear sign bit

            var yBuf = new byte[33];
            Buffer.BlockCopy(buf, 0, yBuf, 0, 32);
            // yBuf[32] = 0 (positive BigInteger)
            var y = new BigInteger(yBuf);

            if (y >= P) return false;

            // Recover x: x^2 = (y^2 - 1) / (d*y^2 + 1)
            var y2  = FMul(y, y);
            var u   = FSub(y2, BigInteger.One);
            var v   = FAdd(FMul(D, y2), BigInteger.One);
            var vInv = FInv(v);
            var uv  = FMul(u, vInv);

            // Candidate: x = uv^((p+3)/8)
            // Using the identity: if v * x^2 == u then x is the correct root,
            // else x = x * sqrt(-1).
            var exp = (P + 3) / 8;
            var x   = BigInteger.ModPow(uv, exp, P);

            // Check: vx^2 should equal u
            var vx2 = FMul(v, FMul(x, x));
            if (vx2 == FMod(u))
            {
                // x is correct (but adjust sign below)
            }
            else if (vx2 == FMod(P - u))
            {
                // Multiply by sqrt(-1) to get the correct square root
                x = FMul(x, SqrtM1);
            }
            else
            {
                return false; // Not a valid point
            }

            // Adjust sign to match the encoded sign bit.
            if (x == BigInteger.Zero && xSign == 1) return false;
            var xSign_actual = (int)(x % 2);
            if (xSign_actual != xSign)
                x = P - x;

            pt = new Point
            {
                X = x,
                Y = y,
                Z = BigInteger.One,
                T = FMul(x, y)
            };
            return true;
        }

        /// <summary>Check that two group points are equal by cross-multiplying Z coordinates.</summary>
        private static bool PointEqual(Point p1, Point p2)
        {
            // X1/Z1 == X2/Z2  ↔  X1*Z2 == X2*Z1 (mod P)
            // Y1/Z1 == Y2/Z2  ↔  Y1*Z2 == Y2*Z1 (mod P)
            return FMul(p1.X, p2.Z) == FMul(p2.X, p1.Z)
                && FMul(p1.Y, p2.Z) == FMul(p2.Y, p1.Z);
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Verify an Ed25519 signature per RFC 8032 §5.1.7.
        /// </summary>
        /// <param name="publicKeyBytes">32-byte compressed Ed25519 public key.</param>
        /// <param name="message">The message that was signed (server_ephemeral_pub in RTMPE).</param>
        /// <param name="signature">64-byte Ed25519 signature (R || S).</param>
        /// <returns>
        /// <see langword="true"/> if the signature is valid;
        /// <see langword="false"/> for any invalid input or failed verification.
        /// </returns>
        internal static bool Verify(byte[] publicKeyBytes, byte[] message, byte[] signature)
        {
            if (publicKeyBytes == null || publicKeyBytes.Length != 32) return false;
            if (signature      == null || signature.Length      != 64) return false;
            if (message        == null)                                  return false;

            // 1. Decode the public key A.
            if (!DecodePoint(publicKeyBytes, out var A)) return false;

            // 2. Split the signature into R (first 32 bytes) and S (last 32 bytes).
            var R_bytes = new byte[32];
            var S_bytes = new byte[32];
            Buffer.BlockCopy(signature,  0, R_bytes, 0, 32);
            Buffer.BlockCopy(signature, 32, S_bytes, 0, 32);

            // 3. Decode R.
            if (!DecodePoint(R_bytes, out var R)) return false;

            // 4. Decode S as a little-endian integer and check S < l.
            var sBuf = new byte[33];
            Buffer.BlockCopy(S_bytes, 0, sBuf, 0, 32);
            var S = new BigInteger(sBuf);
            if (S < BigInteger.Zero || S >= L) return false;

            // 5. Compute k = SHA-512(R_bytes || publicKeyBytes || message).
            byte[] kHash;
            using (var sha = SHA512.Create())
            {
                var hashInput = new byte[R_bytes.Length + publicKeyBytes.Length + message.Length];
                Buffer.BlockCopy(R_bytes,        0, hashInput,  0,                             R_bytes.Length);
                Buffer.BlockCopy(publicKeyBytes, 0, hashInput,  R_bytes.Length,                publicKeyBytes.Length);
                Buffer.BlockCopy(message,        0, hashInput,  R_bytes.Length + publicKeyBytes.Length, message.Length);
                kHash = sha.ComputeHash(hashInput);
            }

            // k as a 512-bit integer, then reduce mod l.
            var kBuf = new byte[65]; // 64 data bytes + 1 sign byte
            Buffer.BlockCopy(kHash, 0, kBuf, 0, 64);
            var k = new BigInteger(kBuf) % L;

            // 6. Verify: [8][S]B == [8]R + [8][k]A
            // Optimisation: check [S]B == R + [k]A instead (cofactor = 8 cancels out when S < l).
            var lhs = ScalarMult(BasePoint, S);
            var A8k = ScalarMult(A, k);
            var rhs = PointAdd(R, A8k);

            return PointEqual(lhs, rhs);
        }
    }
}
