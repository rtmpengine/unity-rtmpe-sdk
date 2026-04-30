// RTMPE SDK — Runtime/Crypto/ApiKeyCipher.cs
//
// PSK encryption of the API key included in HandshakeInit packets.
//
// Wire format for the HandshakeInit payload:
//  [nonce:12][ChaCha20-Poly1305([api_key_len:2 LE][api_key:N]) + tag:16]
//  = 12 + 2 + N + 16 bytes total
//
// AAD: canonical serialisation of the client's source IP:port.
//  IPv4: [0x04][ip:4 octets, network order][port:2 LE]  = 7 bytes
//  IPv6: [0x06][ip:16 octets, network order][port:2 LE] = 19 bytes
//
// Network order ("big-endian") matches IPAddress.GetAddressBytes() on the
// client and Ipv4Addr::octets() / Ipv6Addr::octets() on the gateway (Rust).
// The port, by contrast, is serialised little-endian on both sides.
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

        // Hard ceiling on the UTF-8-encoded length of the API key.  The wire
        // format prefixes the key with a 16-bit little-endian length so any
        // value above ushort.MaxValue would silently truncate the prefix while
        // emitting the full plaintext — leaving a length/payload mismatch the
        // gateway parses past the next field.  The 1 KiB working ceiling sits
        // an order of magnitude above any realistic operator-issued key while
        // bounding the pre-encryption allocation to a constant size regardless
        // of how the apiKey arrived at this method.
        private const int MaxApiKeyBytes = 1024;

        // ── Encrypt ──────────────────────────────────────────────────────────

        /// <summary>
        /// Encrypt the API key with the project's pre-shared key.
        ///
       /// Returns: <c>[nonce:12][ciphertext + tag:N+16]</c>
        /// where the plain content is <c>[api_key_len:2 LE][api_key:N]</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Nonce strategy:</b> the 12-byte nonce is built as
        /// <c>[salt:4 random][counter:8 monotonic]</c>.  The counter is a
        /// per-process strict-monotonic 64-bit value seeded at AppDomain
        /// startup from the system CSPRNG so two processes that share the
        /// same PSK cannot accidentally agree on a counter prefix; within a
        /// single process the counter never repeats so within-process nonce
        /// uniqueness is structural, not probabilistic.  The 4-byte salt is
        /// drawn fresh per call and protects against the cross-process
        /// collision case where an adversary forks N processes hoping for a
        /// counter-prefix collision.  Effective collision space is therefore
        /// ~2^96 (random salt × random counter seed), well above the 2^32
        /// birthday threshold of a pure-random 96-bit construction.
        /// </para>
        /// <para>
        /// The gateway treats the 12-byte nonce as opaque — only uniqueness
        /// per (PSK, source endpoint) matters, and the AAD already binds
        /// the source endpoint so cross-client collisions don't even
        /// register as policy events on the receiver.
        /// </para>
        /// <para>
        /// <b>AAD:</b> the AAD is the canonical encoding of the source
        /// <see cref="IPEndPoint"/> only; it does not include a transcript hash
        /// of the surrounding handshake, because at this point in the protocol
        /// no transcript has yet been constructed.  Channel binding is
        /// established later by the Ed25519 signature over the full
        /// transcript (see <see cref="HandshakeHandler.ComputeTranscript"/>),
        /// which already mixes in <c>SHA-256(HandshakeInit ciphertext)</c>.
        /// </para>
        /// </remarks>
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

            // Range-check the encoded length BEFORE allocating the UTF-8
            // buffer.  GetByteCount walks the string but does not allocate
            // (per .NET BCL contract); rejecting an oversize key here keeps
            // the failure local to the caller and avoids the multi-MB
            // intermediate alloc + zeroise on the rejection path.
            int encodedLen = Encoding.UTF8.GetByteCount(apiKey);
            if (encodedLen > MaxApiKeyBytes)
                throw new ArgumentException(
                    $"apiKey is {encodedLen} UTF-8 bytes; maximum is {MaxApiKeyBytes}.",
                    nameof(apiKey));

            byte[] keyBytes  = null;
            byte[] plaintext = null;
            try
            {
                keyBytes  = Encoding.UTF8.GetBytes(apiKey);
                plaintext = new byte[2 + keyBytes.Length];
                // [api_key_len:2 LE]
                plaintext[0] = (byte)(keyBytes.Length & 0xFF);
                plaintext[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
                // [api_key:N]
                Buffer.BlockCopy(keyBytes, 0, plaintext, 2, keyBytes.Length);

                // Build the nonce from a fresh 4-byte CSPRNG salt and the
                // process-monotonic 8-byte counter.  See class remarks for
                // the threat-model rationale.
                var nonce = NextNonce();

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
            finally
            {
                // Wipe intermediates that contain (or are derived from) the
                // plaintext API key, so a managed-heap dump after this call
                // cannot recover the secret.  ChaCha20Poly1305Impl.Seal
                // allocates and discards its own Poly1305 one-time key
                // internally; that array becomes unreachable on return and
                // is best-effort GC-zeroed — we cannot reach it from here
                // without changing the AEAD signature.
                if (keyBytes  != null) Array.Clear(keyBytes,  0, keyBytes.Length);
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
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
        /// IPv4: <c>[0x04][ip:4 octets, network order][port:2 LE]</c> = 7 bytes.
        /// IPv6: <c>[0x06][ip:16 octets, network order][port:2 LE]</c> = 19 bytes.
        /// Null endpoint: returns empty array (empty AAD — for unit tests only).
        /// </summary>
        /// <remarks>
        /// IP octets are emitted in network order (big-endian) — matching
        /// <see cref="System.Net.IPAddress.GetAddressBytes"/> on the client and
        /// <c>Ipv4Addr::octets()</c> / <c>Ipv6Addr::octets()</c> on the gateway.
        /// The port is little-endian on both sides.
        /// </remarks>
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

        // ── Nonce derivation ──────────────────────────────────────────────────

        // Process-monotonic counter used as the high 8 bytes of every
        // HandshakeInit nonce.  Seeded from the system CSPRNG at AppDomain
        // startup so two cooperating processes that share the same PSK do
        // not align their counter prefixes.  Incremented strictly under
        // Interlocked so a single process never re-issues the same value
        // even under concurrent reconnect attempts.
        //
       // Why a 64-bit counter and not 96-bit: the AEAD nonce is 12 bytes
        // (96 bits) total; we reserve 4 bytes for a per-call random salt so
        // a forked process cannot collide its own next-counter against a
        // sibling.  The 64-bit counter alone gives 2^63 unique values
        // before risk of wrap (≈ 292 years at 1 GHz call rate); the 32-bit
        // salt then lifts birthday-bound collision space to ~2^96.
        private static long _nonceCounter = SeedNonceCounter();

        private static long SeedNonceCounter()
        {
            // Draw the seed from the system CSPRNG.  Convert.ToInt64
            // accepts the full 64-bit range — a negative value is fine
            // because Interlocked.Increment treats it as a two's-complement
            // increment with no semantic asymmetry.
            Span<byte> seed = stackalloc byte[8];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(seed);
            long v = 0;
            for (int i = 0; i < 8; i++) v |= ((long)seed[i]) << (i * 8);
            return v;
        }

        /// <summary>
        /// Build the next 12-byte nonce as <c>[salt:4 random][counter:8 LE u64]</c>.
        /// Internal so the unit-test fixture can drive it directly without
        /// having to pump full HandshakeInit packets.
        /// </summary>
        internal static byte[] NextNonce()
        {
            var nonce = new byte[NonceLen];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(nonce, 0, 4);
            }
            ulong c = unchecked((ulong)System.Threading.Interlocked.Increment(ref _nonceCounter));
            nonce[4]  = (byte) c;
            nonce[5]  = (byte)(c >> 8);
            nonce[6]  = (byte)(c >> 16);
            nonce[7]  = (byte)(c >> 24);
            nonce[8]  = (byte)(c >> 32);
            nonce[9]  = (byte)(c >> 40);
            nonce[10] = (byte)(c >> 48);
            nonce[11] = (byte)(c >> 56);
            return nonce;
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
