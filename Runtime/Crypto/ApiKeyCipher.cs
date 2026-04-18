// RTMPE SDK — Runtime/Crypto/ApiKeyCipher.cs
//
// PSK encryption of the API key included in HandshakeInit packets.
//
// Wire format for the HandshakeInit payload:
//   [nonce:12][ChaCha20-Poly1305([api_key_len:2 LE][api_key:N]) + tag:16]
//   = 12 + 2 + N + 16 bytes total
//
// AAD: canonical serialisation of the client's source IP:port.
//   IPv4: [0x04][ip:4 octets LE][port:2 LE]  = 7 bytes
//   IPv6: [0x06][ip:16 octets][port:2 LE]    = 19 bytes
//
// The gateway performs the matching decryption using the same PSK and AAD.
//
// Key distribution: the 32-byte PSK (as a 64-char hex string) is configured
// once per deployment and distributed to SDK users via the developer dashboard.

using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RTMPE.Crypto.Internal;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Encrypts the API key for inclusion in the <c>HandshakeInit</c> payload.
    /// Instances are stateless; use <see cref="Encrypt"/> directly.
    /// </summary>
    public static class ApiKeyCipher
    {
        /// <summary>Nonce length for ChaCha20-Poly1305 (12 bytes).</summary>
        public const int NonceLen = 12;

        /// <summary>AEAD authentication tag length (16 bytes).</summary>
        public const int TagLen = 16;

        // ── Encrypt ──────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypt the API key with the project's pre-shared key.
        ///
        /// Returns: <c>[nonce:12][ciphertext + tag:N+16]</c>
        /// where the plain content is <c>[api_key_len:2 LE][api_key:N]</c>.
        /// </summary>
        /// <param name="psk">32-byte pre-shared key (from developer dashboard).</param>
        /// <param name="apiKey">UTF-8 API key string.</param>
        /// <param name="sourceAddress">
        /// The client's local socket endpoint used as AAD.
        /// Must match the source address the gateway will see in the UDP header.
        /// </param>
        public static byte[] Encrypt(byte[] psk, string apiKey, IPEndPoint sourceAddress)
        {
            if (psk == null || psk.Length != 32)
                throw new ArgumentException("PSK must be exactly 32 bytes.", nameof(psk));
            if (string.IsNullOrEmpty(apiKey))
                throw new ArgumentException("apiKey must not be null or empty.", nameof(apiKey));

            var keyBytes  = Encoding.UTF8.GetBytes(apiKey);
            var plaintext = new byte[2 + keyBytes.Length];
            // [api_key_len:2 LE]
            plaintext[0] = (byte)(keyBytes.Length & 0xFF);
            plaintext[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
            // [api_key:N]
            Buffer.BlockCopy(keyBytes, 0, plaintext, 2, keyBytes.Length);

            // Generate a fresh random 12-byte nonce.
            var nonce = new byte[NonceLen];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(nonce);

            // Compute AAD from the source address.
            var aad = AddrAad(sourceAddress);

            // Encrypt (returns ciphertext + 16-byte tag).
            var sealedPayload = ChaCha20Poly1305Impl.Seal(psk, nonce, plaintext, aad);

            // Prepend the nonce: [nonce:12][ciphertext+tag].
            var result = new byte[NonceLen + sealedPayload.Length];
            Buffer.BlockCopy(nonce,         0, result, 0,        NonceLen);
            Buffer.BlockCopy(sealedPayload, 0, result, NonceLen, sealedPayload.Length);
            return result;
        }

        // ── PSK helpers ───────────────────────────────────────────────────────

        /// <summary>
        /// Decode a 64-character lowercase hex string into a 32-byte PSK array.
        /// Throws <see cref="ArgumentException"/> if the string is not valid hex of length 64.
        /// </summary>
        public static byte[] PskFromHex(string hex)
        {
            if (hex == null || hex.Length != 64)
                throw new ArgumentException("PSK hex string must be exactly 64 characters.", nameof(hex));

            var key = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                key[i] = (byte)((HexNibble(hex[i * 2]) << 4) | HexNibble(hex[i * 2 + 1]));
            }
            return key;
        }

        // ── AAD serialisation ──────────────────────────────────────────

        /// <summary>
        /// Serialise the client source address to the canonical AAD byte sequence.
        /// IPv4: <c>[0x04][ip:4][port:2 LE]</c> = 7 bytes.
        /// IPv6: <c>[0x06][ip:16][port:2 LE]</c> = 19 bytes.
        /// Null endpoint: returns empty array (empty AAD — for unit tests only).
        /// </summary>
        public static byte[] AddrAad(IPEndPoint endpoint)
        {
            if (endpoint == null) return Array.Empty<byte>();

            var addr  = endpoint.Address;
            var port  = (ushort)endpoint.Port;

            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var ipBytes = addr.GetAddressBytes(); // 4 bytes, big-endian octets
                var aad = new byte[7];
                aad[0] = 0x04;
                aad[1] = ipBytes[0]; aad[2] = ipBytes[1];
                aad[3] = ipBytes[2]; aad[4] = ipBytes[3];
                aad[5] = (byte)(port & 0xFF);
                aad[6] = (byte)(port >> 8);
                return aad;
            }
            else // IPv6
            {
                var ipBytes = addr.GetAddressBytes(); // 16 bytes
                var aad = new byte[19];
                aad[0] = 0x06;
                Buffer.BlockCopy(ipBytes, 0, aad, 1, 16);
                aad[17] = (byte)(port & 0xFF);
                aad[18] = (byte)(port >> 8);
                return aad;
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────

        private static byte HexNibble(char c)
        {
            if (c >= '0' && c <= '9') return (byte)(c - '0');
            if (c >= 'a' && c <= 'f') return (byte)(c - 'a' + 10);
            if (c >= 'A' && c <= 'F') return (byte)(c - 'A' + 10);
            throw new ArgumentException($"Invalid hex character: '{c}'");
        }
    }
}
