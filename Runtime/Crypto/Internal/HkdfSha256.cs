// RTMPE SDK — Runtime/Crypto/Internal/HkdfSha256.cs
//
// HKDF-SHA256 per RFC 5869.
//
// Used during the W6 handshake to derive two directional session keys
// from the X25519 ECDH shared secret.
//
// Salt used by the gateway: b"RTMPE-v3-hkdf-salt-2026"
// Info base used by both sides: b"RTMPE-v3-session-key" + sorted(clientPub, serverPub)
// Then appended with b"\x00" for the initiator key and b"\x01" for the responder key.

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

        /// <summary>
        /// HKDF-Extract: PRK = HMAC-SHA256(salt, IKM).
        /// If <paramref name="salt"/> is null or empty the RFC 5869 default
        /// (HashLen zero bytes) is used.
        /// </summary>
        internal static byte[] Extract(byte[] salt, byte[] ikm)
        {
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
            if (outputLength < 1 || outputLength > 255 * HashLen)
                throw new ArgumentOutOfRangeException(nameof(outputLength),
                    "HKDF-Expand output length must be between 1 and 255 * HashLen bytes.");

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
                t_prev = hmac.ComputeHash(data);

                int copyLen = Math.Min(HashLen, outputLength - offset);
                Buffer.BlockCopy(t_prev, 0, okm, offset, copyLen);
                offset += copyLen;
            }

            return okm;
        }
    }
}
