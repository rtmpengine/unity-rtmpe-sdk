// RTMPE SDK — Runtime/Crypto/HandshakeHandler.cs
//
// Client-side orchestrator for the four-step ECDH handshake:
//
//   Round 1 (client → server):
//     HandshakeInit: [nonce:12][ChaCha20-Poly1305([key_len:2][key:N])] encrypted with PSK
//
//   Round 1 reply (server → client):
//     Challenge: [server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64] = 128 B
//
//   Round 2 (client → server):
//     HandshakeResponse: [client_ephemeral_pub:32]
//
//   Round 2 reply (server → client):
//     SessionAck: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][reconnect:R]
//
// After receiving Challenge, the client:
//   1. Verifies the Ed25519 signature: Verify(server_static_pub, server_ephemeral_pub, sig)
//   2. Performs X25519: SharedSecret(client_private, server_ephemeral_pub)
//   3. Derives directional SessionKeys via HKDF-SHA256
//
// HandshakeHandler implements IDisposable.  Dispose() zeros the ephemeral
// private key and the server ephemeral public key in-place, reducing the
// window in which sensitive key material can be recovered from a heap dump.
// The caller (NetworkManager) disposes this handler on disconnect.

using System;
using System.Text;
using RTMPE.Crypto.Internal;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Per-session handshake state machine.
    /// Create one instance per <c>Connect()</c> call; discard on disconnect.
    /// Implements <see cref="IDisposable"/> — call Dispose() (or use a using-statement)
    /// after the handshake completes to zero the ephemeral private key in-place.
    /// </summary>
    public sealed class HandshakeHandler : IDisposable
    {
        // HKDF constants must match the gateway exactly.
        private static readonly byte[] HkdfSalt = Encoding.ASCII.GetBytes("RTMPE-v3-hkdf-salt-2026");
        private static readonly byte[] HkdfInfoBase = Encoding.ASCII.GetBytes("RTMPE-v3-session-key");

        // ── Per-session ephemeral key pair ───────────────────────────────────
        private readonly byte[] _clientPrivateKey;
        private readonly byte[] _clientPublicKey;

        // Stored on Challenge receipt; needed for ECDH completion.
        private byte[] _serverEphemeralPub;

        private bool _disposed;

        // ── Construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Generate a fresh X25519 ephemeral key pair for this handshake.
        /// </summary>
        public HandshakeHandler()
        {
            (_clientPrivateKey, _clientPublicKey) = Curve25519.GenerateKeyPair();
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>32-byte X25519 ephemeral public key to send in <c>HandshakeResponse</c>.</summary>
        public byte[] ClientPublicKey => _clientPublicKey;

        /// <summary>
        /// Validate the 128-byte <c>Challenge</c> payload received from the server.
        ///
        /// Parses the three fields, verifies the Ed25519 signature,
        /// and stores the server ephemeral public key for <see cref="DeriveSessionKeys"/>.
        ///
        /// If an optional pinned server public key is provided, it is also
        /// checked against the key embedded in the Challenge.
        /// </summary>
        /// <param name="challengePayload">128 bytes: [ephemeral:32][static:32][sig:64].</param>
        /// <param name="serverEphemeralPub">Receives the server's X25519 ephemeral public key.</param>
        /// <param name="serverStaticPub">Receives the server's Ed25519 static public key.</param>
        /// <param name="pinnedServerStaticPub">
        /// Optional 32-byte pinned public key. The Challenge is rejected if this does not match
        /// the embedded static public key. Pass <see langword="null"/> to skip pinning.
        /// </param>
        /// <returns>
        /// <see langword="true"/> when the Challenge is valid and the Ed25519 signature
        /// passes verification; <see langword="false"/> otherwise.
        /// </returns>
        public bool ValidateChallenge(
            byte[] challengePayload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            byte[] pinnedServerStaticPub = null)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;

            if (challengePayload == null || challengePayload.Length != 128)
                return false;

            // Parse the three fields.
            var ephemeral = new byte[32];
            var staticPub = new byte[32];
            var sig       = new byte[64];
            Buffer.BlockCopy(challengePayload,  0, ephemeral, 0, 32);
            Buffer.BlockCopy(challengePayload, 32, staticPub, 0, 32);
            Buffer.BlockCopy(challengePayload, 64, sig,       0, 64);

            // Pinning check: if the developer has pinned the server public key,
            // reject the connection if the embedded key doesn't match.
            if (pinnedServerStaticPub != null)
            {
                if (pinnedServerStaticPub.Length != 32) return false;
                for (int i = 0; i < 32; i++)
                    if (pinnedServerStaticPub[i] != staticPub[i]) return false;
            }

            // Verify the Ed25519 signature of the ephemeral key.
            // The server signs: sign(server_static_priv, server_ephemeral_pub)
            if (!Ed25519Verify.Verify(staticPub, ephemeral, sig))
                return false;

            _serverEphemeralPub = ephemeral;
            serverEphemeralPub  = ephemeral;
            serverStaticPub     = staticPub;
            return true;
        }

        /// <summary>
        /// Complete the X25519 ECDH and derive two directional session keys via HKDF-SHA256.
        ///
        /// Must be called after <see cref="ValidateChallenge"/> succeeds.
        /// Returns <see langword="null"/> if the challenge has not been validated yet
        /// or if the ECDH shared secret is degenerate (all-zero).
        /// </summary>
        public SessionKeys DeriveSessionKeys()
        {
            if (_serverEphemeralPub == null)
                throw new InvalidOperationException(
                    "ValidateChallenge must succeed before DeriveSessionKeys can be called.");

            // Compute ECDH shared secret.
            var sharedSecret = Curve25519.SharedSecret(_clientPrivateKey, _serverEphemeralPub);
            if (sharedSecret == null) return null; // degenerate key — reject

            SessionKeys result;
            // Declare prk outside the try block so the finally clause can
            // zero it.  prk is derived from the ECDH shared secret and must not
            // linger in the managed heap after the session keys are produced.
            byte[] prk = null;
            try
            {
                // Determine which side is the "initiator" (smaller public key).
                bool iAmInitiator = ComparePublicKeys(_clientPublicKey, _serverEphemeralPub) <= 0;

                // Build the HKDF info: base || min(clientPub, serverPub) || max(clientPub, serverPub)
                var (first, second) = iAmInitiator
                    ? (_clientPublicKey, _serverEphemeralPub)
                    : (_serverEphemeralPub, _clientPublicKey);

                var info = new byte[HkdfInfoBase.Length + 32 + 32];
                Buffer.BlockCopy(HkdfInfoBase, 0, info, 0,                   HkdfInfoBase.Length);
                Buffer.BlockCopy(first,        0, info, HkdfInfoBase.Length, 32);
                Buffer.BlockCopy(second,       0, info, HkdfInfoBase.Length + 32, 32);

                // HKDF-Extract
                prk = HkdfSha256.Extract(HkdfSalt, sharedSecret);

                // HKDF-Expand × 2: info+\x00 → initiator key, info+\x01 → responder key
                var infoInit = new byte[info.Length + 1];
                Buffer.BlockCopy(info, 0, infoInit, 0, info.Length);
                infoInit[info.Length] = 0x00;
                var keyInit = HkdfSha256.Expand(prk, infoInit, 32);

                var infoResp = new byte[info.Length + 1];
                Buffer.BlockCopy(info, 0, infoResp, 0, info.Length);
                infoResp[info.Length] = 0x01;
                var keyResp = HkdfSha256.Expand(prk, infoResp, 32);

                // Assign encrypt/decrypt based on initiator role (mirrors the Rust gateway logic).
                // Initiator sends with key_init and receives with key_resp; responder is opposite.
                result = iAmInitiator
                    ? new SessionKeys(encryptKey: keyInit, decryptKey: keyResp)
                    : new SessionKeys(encryptKey: keyResp, decryptKey: keyInit);
            }
            finally
            {
                // Zero the ECDH shared secret immediately after key derivation.
                // The secret is no longer needed once PRK is derived; clearing it in-place
                // limits the lifetime of raw DH output in the managed heap.
                Array.Clear(sharedSecret, 0, sharedSecret.Length);
                // Zero the PRK (HKDF pseudo-random key derived from the
                // shared secret).  prk may be null if Extract threw before assignment.
                if (prk != null) Array.Clear(prk, 0, prk.Length);
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Lexicographic comparison of two 32-byte public keys.
        /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
        /// </summary>
        private static int ComparePublicKeys(byte[] a, byte[] b)
        {
            for (int i = 0; i < 32; i++)
            {
                int diff = a[i] - b[i];
                if (diff != 0) return diff;
            }
            return 0;
        }

        // ── IDisposable ───────────────────────────────────────────────────────

        /// <summary>
        /// Zero the ephemeral private key and server ephemeral public key in-place.
        /// Safe to call multiple times (subsequent calls are no-ops).
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Zeroize sensitive key material to minimise the window in which
            // a managed-heap dump or cold-boot attack can recover keys.
            if (_clientPrivateKey != null) Array.Clear(_clientPrivateKey, 0, _clientPrivateKey.Length);
            if (_serverEphemeralPub != null) Array.Clear(_serverEphemeralPub, 0, _serverEphemeralPub.Length);
        }
    }
}
