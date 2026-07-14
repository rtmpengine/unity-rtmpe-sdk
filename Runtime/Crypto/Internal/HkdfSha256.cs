// RTMPE SDK — Runtime/Crypto/Internal/HkdfSha256.cs
//
// HKDF-SHA256 per RFC 5869.
//
// Used during the ECDH handshake to derive two directional session keys
// from the X25519 ECDH shared secret.
//
// Salt used by the gateway: b"RTMPE-v3-hkdf-salt-2026"
// Info base used by both sides: b"RTMPE-v3-session-key" + sorted(clientPub, serverPub)
// Then appended with b"\x00" for the initiator key and b"\x01" for the responder key.
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// This is a PURE-MANAGED C# cryptographic implementation.
//
// WHAT IT PROTECTS AGAINST (in scope):
//   • Key derivation correctness: HKDF provides domain-separation between the
//     two directional session keys via distinct info suffixes (\x00 / \x01).
//   • Weak IKM: HKDF-Extract whitens the ECDH output before expansion.
//
// IMPLEMENTATION NOTES:
//   • HMACSHA256 is from System.Security.Cryptography — this is a
//     platform-provided implementation, NOT a custom BigInteger one.
//     It carries the same constant-time properties as the .NET runtime.
//   • No BigInteger is used in this file; timing side-channels are limited
//     to the HMAC primitive, which is generally constant-time in .NET.
//
// TESTING:
//   RFC 5869 Appendix A test vectors verified in CryptoTests.cs.
// ============================================================================

using System;
using System.Security.Cryptography;

namespace RTMPE.Crypto.Internal
{
    /// <summary>
    /// HKDF-SHA256 per RFC 5869 §2.
    /// All operations are pure managed C# using <see cref="HMACSHA256"/>.
    /// </summary>
    internal static class HkdfSha256
    {
        // SHA-256 digest length in bytes.
        private const int HashLen = 32;

        // Working ceiling on the optional info parameter.  RFC 5869 places no
        // upper bound on info; in this SDK it is always assembled from a
        // short fixed prefix plus two 32-byte public keys (~64–96 B in
        // practice).  Capping the input keeps the per-block intermediate
        // allocation bounded for any future caller that might pass a
        // larger value — the 4 KiB ceiling is well above every legitimate
        // call site and well below the allocation-amplification surface
        // (255 blocks × ~4 KiB ≈ 1 MiB total).
        private const int MaxInfoLen = 4096;

        /// <summary>
        /// HKDF-Extract: PRK = HMAC-SHA256(salt, IKM).
        /// If <paramref name="salt"/> is null or empty the RFC 5869 default
        /// (HashLen zero bytes) is used.  <paramref name="ikm"/> must be
        /// non-null; passing null surfaces the misuse at the call site as a
        /// clean ArgumentNullException rather than as an opaque
        /// NullReferenceException deep inside HMACSHA256.ComputeHash.
        /// </summary>
        internal static byte[] Extract(byte[] salt, byte[] ikm)
        {
            // Symmetric contract with Expand(prk, ...) which already throws
            // ArgumentNullException on null prk.  An ECDH shared-secret of
            // zero bytes is allowed (RFC 5869 §2.2 admits empty IKM); only
            // a null reference is rejected.
            if (ikm == null)
                throw new ArgumentNullException(nameof(ikm),
                    "HKDF-Extract requires a non-null IKM (input keying material).");

            if (salt == null || salt.Length == 0)
                salt = new byte[HashLen]; // default salt: HashLen zero bytes

            using var hmac = new HMACSHA256(salt);
            return hmac.ComputeHash(ikm);
        }

        /// <summary>
        /// HKDF-Expand: produces <paramref name="outputLength"/> bytes of key material.
        ///
        /// T(0)  = empty
        /// T(i)  = HMAC-SHA256(PRK, T(i-1) || info || i)
        /// OKM   = first outputLength bytes of T(1) || T(2) || …
        /// </summary>
        internal static byte[] Expand(byte[] prk, byte[] info, int outputLength)
        {
            if (prk == null)
                throw new ArgumentNullException(nameof(prk),
                    "HKDF-Expand requires a non-null pseudo-random key.");
            if (outputLength < 1 || outputLength > 255 * HashLen)
                throw new ArgumentOutOfRangeException(nameof(outputLength),
                    "HKDF-Expand output length must be between 1 and 255 * HashLen bytes.");

            // RFC 5869 §2.3 permits info to be empty; treat null as the same
            // empty-info case rather than dereferencing it on the inner
            // BlockCopy.  An accidental null would otherwise surface as an
            // opaque NullReferenceException far from the call site.
            if (info == null) info = Array.Empty<byte>();
            if (info.Length > MaxInfoLen)
                throw new ArgumentOutOfRangeException(nameof(info),
                    $"HKDF-Expand info parameter is {info.Length} bytes; maximum is {MaxInfoLen}.");

            var okm    = new byte[outputLength];
            var t_prev = Array.Empty<byte>();
            int offset = 0;
            byte counter = 1;

            while (offset < outputLength)
            {
                // Build the HMAC input: T(i-1) || info || i
                var data = new byte[t_prev.Length + info.Length + 1];
                Buffer.BlockCopy(t_prev, 0, data, 0, t_prev.Length);
                Buffer.BlockCopy(info,   0, data, t_prev.Length, info.Length);
                data[data.Length - 1] = counter++;

                using var hmac = new HMACSHA256(prk);
                // Wipe previous T(i-1) — its bytes are now part of OKM
                // (which the caller controls the lifetime of) and the HMAC
                // input we just consumed. Holding extra copies serves no
                // purpose and prolongs key-derived material on the heap.
                if (t_prev.Length != 0) Array.Clear(t_prev, 0, t_prev.Length);
                t_prev = hmac.ComputeHash(data);

                int copyLen = Math.Min(HashLen, outputLength - offset);
                Buffer.BlockCopy(t_prev, 0, okm, offset, copyLen);
                offset += copyLen;

                // The HMAC input contains the previous T (key-derived);
                // wipe it now that the HMAC has consumed it.
                Array.Clear(data, 0, data.Length);
            }

            // Wipe the final T(i) — its bytes have already been copied into
            // the caller's `okm` and the buffer is no longer needed.
            if (t_prev.Length != 0) Array.Clear(t_prev, 0, t_prev.Length);

            return okm;
        }
    }
}
