// RTMPE SDK — Runtime/Crypto/SealedApiKeyCipher.cs
//
// Public-key (anonymous) encryption of the API key for the HandshakeInit
// packet, replacing the symmetric pre-shared key path in ApiKeyCipher.
//
// The client seals the API key to the gateway's STATIC X25519 public key — the
// only value a developer needs, already surfaced by the gateway-config endpoint
// as the pinned server key. No shared secret is distributed, which removes both
// the provisioning gap and the swap trap that arise when an operator-held PSK
// and a public pin are entered into adjacent look-alike fields.
//
// The API key stays in the first packet (HandshakeInit), so the gateway keeps
// its existing cheap pre-authentication: it opens the sealed box and validates
// the key BEFORE spending an ECDH/Ed25519 Challenge on an unauthenticated peer.
//
// Construction — an X25519 sealed box over the project's fixed primitives:
//
//   Seal(pk_S, apiKey):
//     (sk_E, pk_E) = X25519 keygen                       (fresh per seal)
//     shared       = X25519(sk_E, pk_S)
//     key          = HKDF-SHA256(ikm=shared,
//                                salt=pk_E || pk_S,       (binds both parties)
//                                info=SealInfo, len=32)
//     ct           = ChaCha20-Poly1305(key, nonce=0¹², plaintext, aad=∅)
//     output       = pk_E (32) || ct‖tag
//
//   plaintext = [api_key_len:2 LE][api_key:N]            (identical to the
//                                                         legacy ApiKeyCipher
//                                                         framing, so the
//                                                         gateway parse is
//                                                         unchanged after open)
//
// A constant all-zero nonce is sound here: the per-seal ephemeral key makes the
// derived `key` unique for every message, so each (key, nonce) pair occurs
// exactly once — the single-use property a sealed box relies on.

using System;
using System.Text;
using RTMPE.Crypto.Internal;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Anonymous X25519 sealed-box encryption of the API key, sealed to the
    /// gateway's static public key. The counterpart of the gateway's open path;
    /// supersedes the symmetric <see cref="ApiKeyCipher"/> PSK envelope.
    /// </summary>
    public static class SealedApiKeyCipher
    {
        /// <summary>Length of an X25519 public key, in bytes.</summary>
        public const int PublicKeyLen = 32;

        /// <summary>Length of an X25519 private key, in bytes.</summary>
        public const int PrivateKeyLen = 32;

        // ChaCha20-Poly1305 fixed parameters.
        private const int NonceLen = 12;
        private const int TagLen = 16;

        /// <summary>
        /// Smallest well-formed sealed box: the ephemeral public key, the
        /// 2-byte length prefix, and the Poly1305 tag (zero-length API key).
        /// </summary>
        public const int MinSealedLen = PublicKeyLen + 2 + TagLen;

        // Upper bound on the UTF-8 API key, mirroring ApiKeyCipher so a key that
        // the legacy path accepts is accepted here too.
        private const int MaxApiKeyBytes = 1024;

        // Domain-separation label mixed into HKDF-Expand. Bumping the version
        // suffix forces a distinct key schedule for any future format revision.
        private static readonly byte[] SealInfo =
            Encoding.ASCII.GetBytes("RTMPE-api-key-seal-v1");

        // A sealed box carries one message under a per-message key, so a fixed
        // nonce is safe (see file header). Allocated per call to keep the helper
        // free of shared mutable state.
        private static byte[] ZeroNonce() => new byte[NonceLen];

        /// <summary>
        /// Seal <paramref name="apiKey"/> to the gateway's static X25519 public
        /// key. Returns <c>pk_E ‖ ciphertext‖tag</c>.
        /// </summary>
        /// <param name="recipientPublicKey">Gateway static X25519 public key (32 bytes).</param>
        /// <param name="apiKey">UTF-8 API key string.</param>
        public static byte[] Seal(byte[] recipientPublicKey, string apiKey)
        {
            if (recipientPublicKey == null || recipientPublicKey.Length != PublicKeyLen)
                throw new ArgumentException(
                    $"recipientPublicKey must be exactly {PublicKeyLen} bytes.",
                    nameof(recipientPublicKey));
            if (apiKey == null)
                throw new ArgumentNullException(nameof(apiKey));

            // Range-check the encoded length BEFORE allocating the UTF-8 buffer
            // so an oversize key is rejected without leaving a secret plaintext
            // buffer to be dropped un-wiped (GetByteCount walks the string but
            // does not allocate, per the BCL contract).  Mirrors ApiKeyCipher.
            int encodedLen = Encoding.UTF8.GetByteCount(apiKey);
            if (encodedLen > MaxApiKeyBytes)
                throw new ArgumentException(
                    $"apiKey is {encodedLen} UTF-8 bytes; maximum is {MaxApiKeyBytes}.",
                    nameof(apiKey));

            byte[] keyBytes = Encoding.UTF8.GetBytes(apiKey);
            byte[] ephPriv = null, ephPub = null, shared = null, key = null, plaintext = null;
            try
            {
                (ephPriv, ephPub) = Curve25519.GenerateKeyPair();
                shared    = Curve25519.SharedSecret(ephPriv, recipientPublicKey);
                // A low-order recipient key collapses the X25519 ladder to the
                // all-zero shared secret (surfaced as null).  Reject it with a
                // precise argument error rather than let the null reach HKDF as an
                // opaque failure deep inside the key schedule.
                if (shared == null)
                    throw new ArgumentException(
                        "recipientPublicKey is a low-order X25519 point and yields no usable shared secret.",
                        nameof(recipientPublicKey));
                key       = DeriveKey(shared, ephPub, recipientPublicKey);

                plaintext = new byte[2 + keyBytes.Length];
                plaintext[0] = (byte)(keyBytes.Length & 0xFF);
                plaintext[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(keyBytes, 0, plaintext, 2, keyBytes.Length);

                byte[] ct = ChaCha20Poly1305Impl.Seal(key, ZeroNonce(), plaintext, Array.Empty<byte>());

                var sealedBox = new byte[PublicKeyLen + ct.Length];
                Buffer.BlockCopy(ephPub, 0, sealedBox, 0,            PublicKeyLen);
                Buffer.BlockCopy(ct,     0, sealedBox, PublicKeyLen, ct.Length);
                return sealedBox;
            }
            finally
            {
                Wipe(ephPriv); Wipe(shared); Wipe(key); Wipe(plaintext); Wipe(keyBytes);
            }
        }

        /// <summary>
        /// Open a sealed box with the gateway's static X25519 key pair and
        /// return the UTF-8 API key, or <see langword="null"/> if the box is
        /// malformed or fails authentication.
        /// </summary>
        /// <param name="recipientPrivateKey">Gateway static X25519 private key (32 bytes).</param>
        /// <param name="recipientPublicKey">Gateway static X25519 public key (32 bytes).</param>
        /// <param name="sealedBox"><c>pk_E ‖ ciphertext‖tag</c>.</param>
        public static string Open(
            byte[] recipientPrivateKey, byte[] recipientPublicKey, byte[] sealedBox)
        {
            if (recipientPrivateKey == null || recipientPrivateKey.Length != PrivateKeyLen)
                throw new ArgumentException(
                    $"recipientPrivateKey must be exactly {PrivateKeyLen} bytes.",
                    nameof(recipientPrivateKey));
            if (recipientPublicKey == null || recipientPublicKey.Length != PublicKeyLen)
                throw new ArgumentException(
                    $"recipientPublicKey must be exactly {PublicKeyLen} bytes.",
                    nameof(recipientPublicKey));
            if (sealedBox == null || sealedBox.Length < MinSealedLen)
                return null;

            byte[] ephPub = new byte[PublicKeyLen];
            Buffer.BlockCopy(sealedBox, 0, ephPub, 0, PublicKeyLen);

            int ctLen = sealedBox.Length - PublicKeyLen;
            byte[] ct = new byte[ctLen];
            Buffer.BlockCopy(sealedBox, PublicKeyLen, ct, 0, ctLen);

            byte[] shared = null, key = null, plaintext = null;
            try
            {
                shared    = Curve25519.SharedSecret(recipientPrivateKey, ephPub);
                // A low-order / non-contributory ephemeral key drives the ladder to
                // the all-zero output (surfaced as null).  Such a box carries no
                // recoverable key, so it is rejected as malformed rather than fed
                // into the HKDF salt.
                if (shared == null) return null;
                key       = DeriveKey(shared, ephPub, recipientPublicKey);
                plaintext = ChaCha20Poly1305Impl.Open(key, ZeroNonce(), ct, Array.Empty<byte>());
                if (plaintext == null) return null;

                // [api_key_len:2 LE][api_key:N] — reject any length/frame mismatch.
                if (plaintext.Length < 2) return null;
                int declared = plaintext[0] | (plaintext[1] << 8);
                if (declared != plaintext.Length - 2) return null;

                return Encoding.UTF8.GetString(plaintext, 2, declared);
            }
            finally
            {
                Wipe(shared); Wipe(key); Wipe(plaintext);
            }
        }

        // HKDF-SHA256 over the ECDH output, salted with both public keys so the
        // derived key is bound to this exact (ephemeral, recipient) pairing.
        private static byte[] DeriveKey(byte[] shared, byte[] ephPub, byte[] recipientPub)
        {
            byte[] salt = new byte[PublicKeyLen * 2];
            Buffer.BlockCopy(ephPub,       0, salt, 0,            PublicKeyLen);
            Buffer.BlockCopy(recipientPub, 0, salt, PublicKeyLen, PublicKeyLen);

            byte[] prk = null;
            try
            {
                prk = HkdfSha256.Extract(salt, shared);
                return HkdfSha256.Expand(prk, SealInfo, 32);
            }
            finally
            {
                Wipe(prk);
            }
        }

        private static void Wipe(byte[] b)
        {
            if (b != null) Array.Clear(b, 0, b.Length);
        }
    }
}
