// RTMPE SDK — Runtime/Crypto/SessionKeys.cs
//
// Two directional symmetric keys derived from X25519 ECDH + HKDF-SHA256.
//
// G-H1 fix (mirrored from the gateway): each session uses two independent
// ChaCha20-Poly1305 keys so that client→server and server→client traffic
// cannot be XOR-combined by a passive observer to reveal plaintext.
//
// The "initiator" is the side with the lexicographically smaller X25519
// public key. Both sides determine this autonomously—no extra signalling.

namespace RTMPE.Crypto
{
    /// <summary>
    /// A pair of 32-byte ChaCha20-Poly1305 session keys derived from a single
    /// ECDH shared secret via HKDF-SHA256.
    ///
    /// <list type="bullet">
    ///   <item><term><see cref="EncryptKey"/></term><description>Key used to seal outbound packets.</description></item>
    ///   <item><term><see cref="DecryptKey"/></term><description>Key used to open inbound packets.</description></item>
    /// </list>
    /// </summary>
    public sealed class SessionKeys
    {
        /// <summary>32-byte key for sealing packets sent by this side.</summary>
        public byte[] EncryptKey { get; }

        /// <summary>32-byte key for opening packets received from the peer.</summary>
        public byte[] DecryptKey { get; }

        internal SessionKeys(byte[] encryptKey, byte[] decryptKey)
        {
            EncryptKey = encryptKey;
            DecryptKey = decryptKey;
        }
    }
}
