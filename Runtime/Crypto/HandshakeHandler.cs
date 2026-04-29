// RTMPE SDK — Runtime/Crypto/HandshakeHandler.cs
//
// Client-side orchestrator for the four-step ECDH handshake:
//
//  Round 1 (client → server):
//    HandshakeInit: [nonce:12][ChaCha20-Poly1305([key_len:2][key:N])] encrypted with PSK
//
//  Round 1 reply (server → client):
//    Challenge: [server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64] = 128 B
//
//  Round 2 (client → server):
//    HandshakeResponse: [client_ephemeral_pub:32]
//
//  Round 2 reply (server → client):
//    SessionAck: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][reconnect:R]
//
// After receiving Challenge, the client:
//  1. Recomputes the canonical handshake transcript (see HandshakeTranscript)
//     and verifies the Ed25519 signature against the 32-byte transcript hash.
//  2. Performs X25519: SharedSecret(client_private, server_ephemeral_pub)
//  3. Derives directional SessionKeys via HKDF-SHA256
//
// Channel binding: the signature is verified over the canonical transcript
// hash, not the bare ephemeral public key.  The transcript binds protocol
// version, cipher-suite identifier, server static public key, server
// ephemeral public key, and SHA-256(HandshakeInit ciphertext).  This closes:
//  • cross-session replay (different HandshakeInit → different transcript),
//  • version downgrade   (forged version byte    → different transcript),
//  • cipher-suite downgrade (forged suite id      → different transcript).
//
// HandshakeHandler implements IDisposable.  Dispose() zeros the ephemeral
// private key and the server ephemeral public key in-place, reducing the
// window in which sensitive key material can be recovered from a heap dump.
// The caller (NetworkManager) disposes this handler on disconnect.

using System;
using System.Security.Cryptography;
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

        // ── Handshake-transcript binding constants  ────────────────
        //
       // These MUST match `modules/gateway/src/crypto/server_auth.rs` exactly,
        // byte for byte.  Any divergence silently breaks server authentication.

        /// <summary>
        /// Current handshake protocol version.  Embedded as the first variable
        /// byte of the transcript so a downgrade attempt yields a different
        /// hash and an invalid signature.
        /// </summary>
        public const byte HandshakeProtocolVersion = 0x02;

        /// <summary>
        /// Cipher-suite identifier:
        /// X25519 + ChaCha20-Poly1305 + Ed25519 + HKDF-SHA256.
        /// </summary>
        public const byte CipherSuiteId = 0x01;

        /// <summary>
        /// 30-byte domain-separation tag (29-char ASCII label + NUL byte).
        /// The NUL terminator prevents prefix collisions with any future tag.
        /// </summary>
        private static readonly byte[] TranscriptDomainTag =
            Encoding.ASCII.GetBytes("RTMPE-handshake-v2-transcript\0");

        /// <summary>Length of the client-init-hash slot in the transcript.</summary>
        private const int ClientInitHashLen = 32;

        /// <summary>
        /// Sentinel for the client-init-hash slot when there is no inbound
        /// HandshakeInit ciphertext (reconnect flow).  Replay defence in that
        /// flow is provided by the single-use reconnect token instead.
        /// </summary>
        private static readonly byte[] ClientInitHashAbsent = new byte[ClientInitHashLen];

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
       /// Parses the three fields, recomputes the canonical handshake
        /// transcript, verifies the Ed25519 signature against it,
        /// and stores the server ephemeral public key for <see cref="DeriveSessionKeys"/>.
        ///
       /// If an optional pinned server public key is provided, it is also
        /// checked against the key embedded in the Challenge.
        /// </summary>
        /// <param name="challengePayload">128 bytes: [ephemeral:32][static:32][sig:64].</param>
        /// <param name="handshakeInitCiphertext">
        /// The exact bytes the client sent in its <c>HandshakeInit</c> packet
        /// (the encrypted API-key envelope produced by <c>ApiKeyCipher</c>).
        /// SHA-256 is computed over this slice and bound into the transcript.
        /// Pass <see langword="null"/> ONLY for the reconnect flow, where there
        /// is no inbound ciphertext and replay defence is provided by the
        /// single-use reconnect token instead.
        /// </param>
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
            byte[] handshakeInitCiphertext,
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

            // Deterministic-work validation: every failure path does roughly
            // the same amount of work, so a passive observer cannot tell
            // "pin mismatch" from "signature failure" by response time.
            //
           // Concretely: we ALWAYS run the (expensive) Ed25519 verify, even
            // when the pinning check has already failed.  The cost of an extra
            // signature verification on a rejected challenge is negligible
            // (it happens at most once per failed connection attempt) and
            // closes a meaningful side-channel — without it, an attacker who
            // pins their own key on the client could probe the legitimate
            // server's static key prefix by measuring how quickly the client
            // bails out.
            bool pinOk = true;
            if (pinnedServerStaticPub != null)
            {
                pinOk = pinnedServerStaticPub.Length == 32
                     && ConstantTimeEquals(pinnedServerStaticPub, staticPub);
            }

            // Compute the canonical 32-byte client_init_hash:
            //  • init flow:      SHA-256(HandshakeInit ciphertext bytes)
            //  • reconnect flow: 32 × 0x00 (the absent-sentinel agreed with
            //                    the gateway in `CLIENT_INIT_HASH_ABSENT`).
            byte[] clientInitHash = handshakeInitCiphertext != null
                ? Sha256(handshakeInitCiphertext)
                : ClientInitHashAbsent;

            // Reconstruct the transcript byte-for-byte and verify the
            // Ed25519 signature against it (always — see comment above).
            byte[] transcript = ComputeTranscript(
                staticPub,
                ephemeral,
                clientInitHash,
                HandshakeProtocolVersion,
                CipherSuiteId);

            bool sigOk = Ed25519Verify.Verify(staticPub, transcript, sig);

            if (!pinOk || !sigOk) return false;

            _serverEphemeralPub = ephemeral;
            serverEphemeralPub  = ephemeral;
            serverStaticPub     = staticPub;
            return true;
        }

        // ── Transcript construction ────────────────────────────────

        /// <summary>
        /// Compute the canonical 32-byte handshake transcript hash.
        ///
       /// MUST match <c>ServerAuthenticator::compute_transcript</c> in the
        /// Rust gateway byte-for-byte.  Layout (98-byte pre-image):
        ///  [0  .. 30) TranscriptDomainTag
        ///  [30 .. 31) protocol_version
        ///  [31 .. 32) cipher_suite_id
        ///  [32 .. 64) server_static_pub
        ///  [64 .. 96) server_ephemeral_pub
        ///  [96 ..128) client_init_hash
        /// All fields are fixed-width — the layout is unambiguous without
        /// length prefixes.
        /// </summary>
        internal static byte[] ComputeTranscript(
            byte[] serverStaticPub,
            byte[] serverEphemeralPub,
            byte[] clientInitHash,
            byte protocolVersion,
            byte cipherSuiteId)
        {
            const int Size = 30 + 1 + 1 + 32 + 32 + ClientInitHashLen;
            var buf = new byte[Size];
            int o = 0;
            Buffer.BlockCopy(TranscriptDomainTag, 0, buf, o, TranscriptDomainTag.Length);
            o += TranscriptDomainTag.Length;
            buf[o++] = protocolVersion;
            buf[o++] = cipherSuiteId;
            Buffer.BlockCopy(serverStaticPub,    0, buf, o, 32); o += 32;
            Buffer.BlockCopy(serverEphemeralPub, 0, buf, o, 32); o += 32;
            Buffer.BlockCopy(clientInitHash,     0, buf, o, ClientInitHashLen);

            return Sha256(buf);
        }

        private static byte[] Sha256(byte[] input)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(input);
        }

        /// <summary>
        /// Complete the X25519 ECDH and derive directional session keys + IP migration key
        /// via HKDF-SHA256.
        ///
       /// Must be called after <see cref="ValidateChallenge"/> succeeds.
        /// Returns <see langword="null"/> if the ECDH shared secret is degenerate (all-zero).
        /// </summary>
        /// <param name="ipMigrationKey">
        /// Receives the 32-byte N-8 IP migration HMAC key derived with info suffix <c>\x02</c>.
        /// Used to compute <c>HMAC-SHA256(ipMigrationKey, reconnect_token)</c> proofs so the
        /// client can reconnect from a new IP (WiFi → 4G) without re-authenticating.
        /// Set to <see langword="null"/> when the method returns <see langword="null"/>.
        /// </param>
        public SessionKeys DeriveSessionKeys(out byte[] ipMigrationKey)
        {
            ipMigrationKey = null;

            if (_serverEphemeralPub == null)
                throw new InvalidOperationException(
                    "ValidateChallenge must succeed before DeriveSessionKeys can be called.");

            // Compute ECDH shared secret.
            var sharedSecret = Curve25519.SharedSecret(_clientPrivateKey, _serverEphemeralPub);
            if (sharedSecret == null) return null; // degenerate key — reject

            SessionKeys result   = null;
            byte[] prk           = null;
            byte[] keyInit       = null;
            byte[] keyResp       = null;
            byte[] info          = null;
            byte[] infoInit      = null;
            byte[] infoResp      = null;
            byte[] infoMig       = null;
            bool   committed     = false;
            try
            {
                // Determine which side is the "initiator" (smaller public key).
                bool iAmInitiator = ComparePublicKeys(_clientPublicKey, _serverEphemeralPub) <= 0;

                // Build the HKDF info: base || min(clientPub, serverPub) || max(clientPub, serverPub)
                var (first, second) = iAmInitiator
                    ? (_clientPublicKey, _serverEphemeralPub)
                    : (_serverEphemeralPub, _clientPublicKey);

                info = new byte[HkdfInfoBase.Length + 32 + 32];
                Buffer.BlockCopy(HkdfInfoBase, 0, info, 0,                   HkdfInfoBase.Length);
                Buffer.BlockCopy(first,        0, info, HkdfInfoBase.Length, 32);
                Buffer.BlockCopy(second,       0, info, HkdfInfoBase.Length + 32, 32);

                // HKDF-Extract — single PRK for all three expansions.
                prk = HkdfSha256.Extract(HkdfSalt, sharedSecret);

                // HKDF-Expand × 3:
                //  info+\x00 → initiator AEAD key
                //  info+\x01 → responder AEAD key
                //  info+\x02 → IP migration HMAC key (N-8)
                infoInit = new byte[info.Length + 1];
                Buffer.BlockCopy(info, 0, infoInit, 0, info.Length);
                infoInit[info.Length] = 0x00;
                keyInit = HkdfSha256.Expand(prk, infoInit, 32);

                infoResp = new byte[info.Length + 1];
                Buffer.BlockCopy(info, 0, infoResp, 0, info.Length);
                infoResp[info.Length] = 0x01;
                keyResp = HkdfSha256.Expand(prk, infoResp, 32);

                infoMig = new byte[info.Length + 1];
                Buffer.BlockCopy(info, 0, infoMig, 0, info.Length);
                infoMig[info.Length] = 0x02;
                ipMigrationKey = HkdfSha256.Expand(prk, infoMig, 32);

                // Assign encrypt/decrypt based on initiator role (mirrors the Rust gateway logic).
                // SessionKeys takes ownership of the two 32-byte arrays at this
                // point — set `committed` so the failure-path in `finally`
                // doesn't zero arrays the caller now owns.
                result = iAmInitiator
                    ? new SessionKeys(encryptKey: keyInit, decryptKey: keyResp)
                    : new SessionKeys(encryptKey: keyResp, decryptKey: keyInit);
                committed = true;
            }
            finally
            {
                Array.Clear(sharedSecret, 0, sharedSecret.Length);
                if (prk     != null) Array.Clear(prk,     0, prk.Length);
                // The info buffers contain ephemeral public-key material.
                // Clearing them limits the window during which a heap dump
                // could recover per-session identifiers.
                if (info    != null) Array.Clear(info,    0, info.Length);
                if (infoInit != null) Array.Clear(infoInit, 0, infoInit.Length);
                if (infoResp != null) Array.Clear(infoResp, 0, infoResp.Length);
                if (infoMig  != null) Array.Clear(infoMig,  0, infoMig.Length);
                // If an exception interrupted derivation after one or more
                // directional keys were expanded, the caller never received
                // them and they must be wiped from memory.  Once `committed`
                // flips (handing ownership to SessionKeys), SessionKeys.Dispose
                // is responsible for clearing the backing arrays.
                if (!committed)
                {
                    if (keyInit != null) Array.Clear(keyInit, 0, keyInit.Length);
                    if (keyResp != null) Array.Clear(keyResp, 0, keyResp.Length);
                    if (ipMigrationKey != null)
                    {
                        Array.Clear(ipMigrationKey, 0, ipMigrationKey.Length);
                        ipMigrationKey = null;
                    }
                }
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Lexicographic comparison of two 32-byte public keys.
        /// Returns negative if a &lt; b, zero if equal, positive if a &gt; b.
        /// Used only for HKDF role assignment — both inputs are public, so
        /// non-constant-time is acceptable here.
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

        /// <summary>
        /// Constant-time equality of two equal-length byte arrays.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Used for the pinned server public key check.  Even though pinned
        /// public keys are not secret in the cryptographic sense, an early-exit
        /// per-byte compare leaks the matched prefix length via timing — a
        /// passive observer learning "first byte differs" vs. "first 16 bytes
        /// match" could brute-force the pinned key offline.  Constant-time
        /// closes that side-channel.
        /// </para>
        /// <para>
        /// <b>Length handling:</b> when <paramref name="a"/> and
        /// <paramref name="b"/> differ in length the method returns
        /// <see langword="false"/> immediately, without scanning either input.
        /// This is intentional: in every RTMPE call site both inputs are
        /// fixed-size (32-byte public keys, 16-byte MACs) — the lengths are
        /// public protocol constants, not secrets — so revealing a
        /// length mismatch leaks no useful information.  When the lengths
        /// match (the common case) the body runs a constant number of
        /// XOR-OR operations regardless of where a difference occurs.
        /// </para>
        /// </remarks>
        internal static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
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
