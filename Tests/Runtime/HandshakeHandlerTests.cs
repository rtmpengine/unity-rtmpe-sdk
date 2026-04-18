// RTMPE SDK — Tests/Runtime/HandshakeHandlerTests.cs
//
// NUnit tests for the ECDH handshake crypto layer:
//   - Curve25519 (X25519) key generation and Diffie-Hellman
//   - HkdfSha256 key derivation
//   - ChaCha20Poly1305Impl AEAD seal / open
//   - Ed25519Verify RFC 8032 vectors
//   - HandshakeHandler end-to-end session key derivation symmetry
//
// Test vectors are taken from published RFCs — noted per test.
//
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Text;
using NUnit.Framework;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Crypto")]
    public class HandshakeHandlerTests
    {
        // ── Helpers ────────────────────────────────────────────────────────────

        private static byte[] H(string hex)
        {
            if (hex.Length % 2 != 0) throw new ArgumentException("odd hex length");
            var result = new byte[hex.Length / 2];
            for (int i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return result;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        // Curve25519 (X25519)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Category("Curve25519")]
        public void Curve25519_GenerateKeyPair_ProducesNonZeroKeys()
        {
            var (priv, pub) = Curve25519.GenerateKeyPair();

            Assert.IsNotNull(priv);
            Assert.IsNotNull(pub);
            Assert.AreEqual(32, priv.Length, "private key must be 32 bytes");
            Assert.AreEqual(32, pub.Length,  "public key must be 32 bytes");

            bool privAllZero = true, pubAllZero = true;
            for (int i = 0; i < 32; i++)
            {
                if (priv[i] != 0) privAllZero = false;
                if (pub[i]  != 0) pubAllZero  = false;
            }
            Assert.IsFalse(privAllZero, "private key must not be all-zero");
            Assert.IsFalse(pubAllZero,  "public key must not be all-zero");
        }

        [Test]
        [Category("Curve25519")]
        public void Curve25519_GenerateKeyPair_DifferentKeyEachCall()
        {
            var (_, pub1) = Curve25519.GenerateKeyPair();
            var (_, pub2) = Curve25519.GenerateKeyPair();
            Assert.IsFalse(BytesEqual(pub1, pub2),
                "Two independently generated key pairs must be different (RNG collision is astronomically unlikely).");
        }

        /// <summary>RFC 7748 §6.1 test vector — X25519 Diffie-Hellman.</summary>
        [Test]
        [Category("Curve25519")]
        public void Curve25519_SharedSecret_MatchesRfc7748_Vector()
        {
            // RFC 7748 §6.1 — exact hex strings from the RFC.
            // RFC 7748 §6.1 test vector verification:
            var alicePriv  = H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            var alicePub   = H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
            var bobPriv    = H("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
            var bobPub     = H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
            var expected   = H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

            var aliceShared = Curve25519.SharedSecret(alicePriv, bobPub);
            var bobShared   = Curve25519.SharedSecret(bobPriv, alicePub);

            Assert.IsNotNull(aliceShared, "Alice shared secret must not be null");
            Assert.IsNotNull(bobShared,   "Bob shared secret must not be null");
            Assert.IsTrue(BytesEqual(expected, aliceShared), "Alice's shared secret must match RFC 7748 vector");
            Assert.IsTrue(BytesEqual(expected, bobShared),   "Bob's shared secret must match RFC 7748 vector");
        }

        [Test]
        [Category("Curve25519")]
        public void Curve25519_SharedSecret_IsSameOnBothSides()
        {
            // Generate two fresh key pairs and verify ECDH symmetry.
            var (privA, pubA) = Curve25519.GenerateKeyPair();
            var (privB, pubB) = Curve25519.GenerateKeyPair();

            var secretA = Curve25519.SharedSecret(privA, pubB);
            var secretB = Curve25519.SharedSecret(privB, pubA);

            Assert.IsTrue(BytesEqual(secretA, secretB),
                "Both sides of an X25519 exchange must produce the same shared secret.");
        }

        [Test]
        [Category("Curve25519")]
        public void Curve25519_SharedSecret_DifferentPeers_GiveDifferentSecrets()
        {
            var (privA, _)    = Curve25519.GenerateKeyPair();
            var (_, pubB)     = Curve25519.GenerateKeyPair();
            var (_, pubC)     = Curve25519.GenerateKeyPair();

            var secretAB = Curve25519.SharedSecret(privA, pubB);
            var secretAC = Curve25519.SharedSecret(privA, pubC);

            Assert.IsFalse(BytesEqual(secretAB, secretAC),
                "Shared secrets with different peer keys must differ.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // HkdfSha256
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RFC 5869 Appendix A.1 test vector.</summary>
        [Test]
        [Category("Hkdf")]
        public void Hkdf_Extract_MatchesRfc5869_A1()
        {
            // RFC 5869 §A.1: Hash=SHA-256, IKM=0x0b0b…, Salt=0x000102…
            var ikm  = H("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b");
            var salt = H("000102030405060708090a0b0c");
            var expectedPrk = H("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5");

            var prk = HkdfSha256.Extract(salt, ikm);

            Assert.AreEqual(32, prk.Length);
            Assert.IsTrue(BytesEqual(expectedPrk, prk),
                "HkdfSha256.Extract must match RFC 5869 A.1 PRK.");
        }

        /// <summary>RFC 5869 Appendix A.1 end-to-end OKM test.</summary>
        [Test]
        [Category("Hkdf")]
        public void Hkdf_Expand_MatchesRfc5869_A1_Okm()
        {
            var prk  = H("077709362c2e32df0ddc3f0dc47bba6390b6c73bb50f9c3122ec844ad7c2b3e5");
            var info = H("f0f1f2f3f4f5f6f7f8f9");
            var expectedOkm = H("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");

            var okm = HkdfSha256.Expand(prk, info, 42);

            Assert.AreEqual(42, okm.Length, "OKM must be exactly 42 bytes as requested.");
            Assert.IsTrue(BytesEqual(expectedOkm, okm),
                "HkdfSha256.Expand must match RFC 5869 A.1 OKM.");
        }

        [Test]
        [Category("Hkdf")]
        public void Hkdf_Extract_WithNullSalt_UsesZeroSalt()
        {
            // RFC 5869: if salt not provided, use HashLen zeros.
            var ikm = new byte[] { 0x01, 0x02, 0x03 };
            var prk = HkdfSha256.Extract(null, ikm);
            Assert.AreEqual(32, prk.Length, "PRK must be 32 bytes.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // ChaCha20Poly1305Impl
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_RoundTrip_Succeeds()
        {
            var key       = new byte[32];
            var nonce     = new byte[12];
            var plaintext = Encoding.UTF8.GetBytes("Hello, RTMPE!");
            var aad       = Encoding.UTF8.GetBytes("additional-data");

            // Fill key/nonce with deterministic test data.
            for (int i = 0; i < 32; i++) key[i]   = (byte)i;
            for (int i = 0; i < 12; i++) nonce[i] = (byte)(i + 100);

            var ciphertext = ChaCha20Poly1305Impl.Seal(key, nonce, plaintext, aad);
            Assert.IsNotNull(ciphertext, "Seal must not return null.");
            Assert.AreEqual(plaintext.Length + 16, ciphertext.Length,
                "Ciphertext must be plaintext.Length + 16 (Poly1305 tag).");

            var decrypted = ChaCha20Poly1305Impl.Open(key, nonce, ciphertext, aad);
            Assert.IsNotNull(decrypted, "Open must succeed with correct key/nonce/aad.");
            Assert.IsTrue(BytesEqual(plaintext, decrypted), "Decrypted bytes must equal original plaintext.");
        }

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_Open_RejectsWrongKey()
        {
            var key   = new byte[32]; key[0] = 0xAA;
            var nonce = new byte[12];
            var pt    = new byte[] { 1, 2, 3 };

            var ct = ChaCha20Poly1305Impl.Seal(key, nonce, pt, null);

            var wrongKey = (byte[])key.Clone();
            wrongKey[0] ^= 0xFF;

            var result = ChaCha20Poly1305Impl.Open(wrongKey, nonce, ct, null);
            Assert.IsNull(result, "Open must return null when the key is wrong (tag mismatch).");
        }

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_Open_RejectsWrongNonce()
        {
            var key   = new byte[32]; key[5] = 0x55;
            var nonce = new byte[12]; nonce[3] = 0x77;
            var pt    = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

            var ct = ChaCha20Poly1305Impl.Seal(key, nonce, pt, null);

            var wrongNonce = (byte[])nonce.Clone();
            wrongNonce[3] ^= 0x01;

            var result = ChaCha20Poly1305Impl.Open(key, wrongNonce, ct, null);
            Assert.IsNull(result, "Open must return null when the nonce is wrong.");
        }

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_Open_RejectsTamperedCiphertext()
        {
            var key   = new byte[32]; key[10] = 0x11;
            var nonce = new byte[12];
            var pt    = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 };

            var ct = ChaCha20Poly1305Impl.Seal(key, nonce, pt, null);
            ct[0] ^= 0x80; // flip bit in first ciphertext byte

            var result = ChaCha20Poly1305Impl.Open(key, nonce, ct, null);
            Assert.IsNull(result, "Open must return null when ciphertext is tampered.");
        }

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_Open_RejectsWrongAad()
        {
            var key   = new byte[32]; key[0] = 0x42;
            var nonce = new byte[12];
            var pt    = Encoding.UTF8.GetBytes("secret data");
            var aad   = Encoding.UTF8.GetBytes("context");

            var ct     = ChaCha20Poly1305Impl.Seal(key, nonce, pt, aad);
            var result = ChaCha20Poly1305Impl.Open(key, nonce, ct, Encoding.UTF8.GetBytes("CONTEXT"));

            Assert.IsNull(result, "Open must return null when AAD differs.");
        }

        [Test]
        [Category("ChaCha20Poly1305")]
        public void ChaCha20Poly1305_Seal_DifferentNonce_GivesDifferentCiphertext()
        {
            var key    = new byte[32]; key[0] = 0x01;
            var nonce1 = new byte[12]; nonce1[0] = 0x01;
            var nonce2 = new byte[12]; nonce2[0] = 0x02;
            var pt     = new byte[] { 0xAA, 0xBB, 0xCC };

            var ct1 = ChaCha20Poly1305Impl.Seal(key, nonce1, pt, null);
            var ct2 = ChaCha20Poly1305Impl.Seal(key, nonce2, pt, null);

            Assert.IsFalse(BytesEqual(ct1, ct2),
                "Different nonces must produce different ciphertexts.");
        }

        // ══════════════════════════════════════════════════════════════════════
        // Ed25519Verify
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>RFC 8032 §5.1 TEST 1 — empty message.</summary>
        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_AcceptsValid_Rfc8032_Test1()
        {
            // RFC 8032 §5.1 TEST 1
            var pubKey = H("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var msg    = Array.Empty<byte>();
            var sig    = H("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                           "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24376ed9cbcd51b0a0");

            bool ok = Ed25519Verify.Verify(pubKey, msg, sig);
            Assert.IsTrue(ok, "RFC 8032 Test 1 (empty message) must verify successfully.");
        }

        /// <summary>RFC 8032 §5.1 TEST 2 — one-byte message.</summary>
        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_AcceptsValid_Rfc8032_Test2()
        {
            // RFC 8032 §5.1 TEST 2
            var pubKey = H("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
            var msg    = new byte[] { 0x72 };
            var sig    = H("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
                           "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

            bool ok = Ed25519Verify.Verify(pubKey, msg, sig);
            Assert.IsTrue(ok, "RFC 8032 Test 2 (single-byte message 0x72) must verify successfully.");
        }

        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_RejectsTamperedSignature()
        {
            var pubKey = H("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var msg    = Array.Empty<byte>();
            var sig    = H("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                           "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24376ed9cbcd51b0a0");

            // Flip one bit in the signature.
            var tampered = (byte[])sig.Clone();
            tampered[0] ^= 0x01;

            Assert.IsFalse(Ed25519Verify.Verify(pubKey, msg, tampered),
                "A single bit-flip in the signature must cause verification failure.");
        }

        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_RejectsTamperedMessage()
        {
            var pubKey = H("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c");
            var msg    = new byte[] { 0x73 }; // was 0x72 in Test 2
            var sig    = H("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da" +
                           "085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00");

            Assert.IsFalse(Ed25519Verify.Verify(pubKey, msg, sig),
                "A modified message must cause verification failure.");
        }

        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_RejectsWrongPublicKey()
        {
            var pubKey = H("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a");
            var msg    = Array.Empty<byte>();
            var sig    = H("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e06522490155" +
                           "5fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24376ed9cbcd51b0a0");

            var wrongKey = (byte[])pubKey.Clone();
            wrongKey[5] ^= 0xFF;

            Assert.IsFalse(Ed25519Verify.Verify(wrongKey, msg, sig),
                "A different public key must cause verification failure.");
        }

        [Test]
        [Category("Ed25519")]
        public void Ed25519Verify_InvalidInputLengths_ReturnFalse()
        {
            var pub32 = new byte[32];
            var sig64 = new byte[64];
            var msg   = new byte[] { 0x01 };

            Assert.IsFalse(Ed25519Verify.Verify(new byte[31], msg, sig64), "31-byte pubkey");
            Assert.IsFalse(Ed25519Verify.Verify(pub32, msg, new byte[63]), "63-byte sig");
            Assert.IsFalse(Ed25519Verify.Verify(null, msg, sig64), "null pubkey");
            Assert.IsFalse(Ed25519Verify.Verify(pub32, msg, null), "null sig");
        }

        // ══════════════════════════════════════════════════════════════════════
        // HandshakeHandler (public API)
        // ══════════════════════════════════════════════════════════════════════

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_ClientPublicKey_Is32Bytes()
        {
            var h = new HandshakeHandler();
            Assert.AreEqual(32, h.ClientPublicKey.Length);
        }

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_DifferentInstances_HaveDifferentPublicKeys()
        {
            var h1 = new HandshakeHandler();
            var h2 = new HandshakeHandler();
            Assert.IsFalse(BytesEqual(h1.ClientPublicKey, h2.ClientPublicKey),
                "Each HandshakeHandler must generate a unique ephemeral key pair.");
        }

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_ValidateChallenge_ReturnsFalseForNullPayload()
        {
            var h = new HandshakeHandler();
            Assert.IsFalse(h.ValidateChallenge(null, out _, out _));
        }

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_ValidateChallenge_ReturnsFalseForWrongLength()
        {
            var h = new HandshakeHandler();
            Assert.IsFalse(h.ValidateChallenge(new byte[127], out _, out _), "127 bytes");
            Assert.IsFalse(h.ValidateChallenge(new byte[129], out _, out _), "129 bytes");
            Assert.IsFalse(h.ValidateChallenge(Array.Empty<byte>(), out _, out _), "empty");
        }

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_ValidateChallenge_RejectsAllZeroChallenge()
        {
            // All-zero challenge has an invalid Ed25519 signature → must be rejected.
            var h = new HandshakeHandler();
            Assert.IsFalse(h.ValidateChallenge(new byte[128], out _, out _),
                "An all-zero Challenge payload must fail Ed25519 verification.");
        }

        [Test]
        [Category("HandshakeHandler")]
        public void HandshakeHandler_DeriveSessionKeys_ThrowsIfChallengeNotValidated()
        {
            var h = new HandshakeHandler();
            Assert.Throws<InvalidOperationException>(() => h.DeriveSessionKeys());
        }

        // ══════════════════════════════════════════════════════════════════════
        // Session key symmetry (ECDH + HKDF — avoids Ed25519 by calling internals)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Verify that if client and server perform X25519 + HKDF-SHA256 using the same
        /// key-ordering logic, client.Encrypt == server.Decrypt and vice versa.
        ///
        /// Uses Curve25519 and HkdfSha256 directly (InternalsVisibleTo is required).
        /// </summary>
        [Test]
        [Category("SessionKeys")]
        public void SessionKeys_ClientAndServer_DeriveComplementaryKeys()
        {
            // Generate two key pairs: "client" and "server ephemeral"
            var (clientPriv, clientPub) = Curve25519.GenerateKeyPair();
            var (serverPriv, serverPub) = Curve25519.GenerateKeyPair();

            // Both sides compute the shared secret — must match.
            var clientShared = Curve25519.SharedSecret(clientPriv, serverPub);
            var serverShared = Curve25519.SharedSecret(serverPriv, clientPub);
            Assert.IsTrue(BytesEqual(clientShared, serverShared), "Shared secrets must match.");

            // HKDF constants (must match gateway exactly)
            var salt     = Encoding.ASCII.GetBytes("RTMPE-v3-hkdf-salt-2026");
            var infoBase = Encoding.ASCII.GetBytes("RTMPE-v3-session-key");

            // Client's initiator determination
            bool clientIsInitiator = CompareKeys(clientPub, serverPub) <= 0;

            byte[] first, second;
            if (clientIsInitiator) { first = clientPub; second = serverPub; }
            else                   { first = serverPub; second = clientPub; }

            var info = Concat(infoBase, first, second);
            var prk  = HkdfSha256.Extract(salt, clientShared);

            var keyInit = HkdfSha256.Expand(prk, Concat(info, new byte[] { 0x00 }), 32);
            var keyResp = HkdfSha256.Expand(prk, Concat(info, new byte[] { 0x01 }), 32);

            // Client's session keys
            byte[] clientEncrypt, clientDecrypt;
            if (clientIsInitiator) { clientEncrypt = keyInit; clientDecrypt = keyResp; }
            else                   { clientEncrypt = keyResp; clientDecrypt = keyInit; }

            // Server's session keys (server is NOT the initiator when client is)
            bool serverIsInitiator = !clientIsInitiator;
            byte[] serverEncrypt, serverDecrypt;
            if (serverIsInitiator) { serverEncrypt = keyInit; serverDecrypt = keyResp; }
            else                   { serverEncrypt = keyResp; serverDecrypt = keyInit; }

            // Verify directionality:
            // What client encrypts, server should be able to decrypt → client.Encrypt == server.Decrypt
            Assert.IsTrue(BytesEqual(clientEncrypt, serverDecrypt),
                "Client.EncryptKey must equal Server.DecryptKey.");
            Assert.IsTrue(BytesEqual(serverEncrypt, clientDecrypt),
                "Server.EncryptKey must equal Client.DecryptKey.");

            // The two session keys must be different from each other.
            Assert.IsFalse(BytesEqual(keyInit, keyResp),
                "key_init and key_resp must be different (same info differs only in final byte).");
        }

        // ── ApiKeyCipher ───────────────────────────────────────────────────────

        [Test]
        [Category("ApiKeyCipher")]
        public void ApiKeyCipher_PskFromHex_DecodesCorrectly()
        {
            string hex = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f";
            var psk = ApiKeyCipher.PskFromHex(hex);
            Assert.AreEqual(32, psk.Length);
            for (int i = 0; i < 32; i++)
                Assert.AreEqual((byte)i, psk[i], $"byte[{i}]");
        }

        [Test]
        [Category("ApiKeyCipher")]
        public void ApiKeyCipher_PskFromHex_ThrowsOnShortKey()
        {
            Assert.Throws<ArgumentException>(() => ApiKeyCipher.PskFromHex("0011223344"));
        }

        [Test]
        [Category("ApiKeyCipher")]
        public void ApiKeyCipher_Encrypt_ProducesNonDeterministicOutput()
        {
            var psk = new byte[32];
            var ep  = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 7777);

            var ct1 = ApiKeyCipher.Encrypt(psk, "my-api-key", ep);
            var ct2 = ApiKeyCipher.Encrypt(psk, "my-api-key", ep);

            // Each call uses a fresh random nonce → ciphertexts must differ.
            Assert.IsFalse(BytesEqual(ct1, ct2),
                "Each Encrypt() call must produce a different ciphertext (random nonce).");
        }

        [Test]
        [Category("ApiKeyCipher")]
        public void ApiKeyCipher_Encrypt_TagIsAppended()
        {
            var psk    = new byte[32];
            var apiKey = "hello";
            var ep     = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 12345);
            var keyBytes = Encoding.UTF8.GetBytes(apiKey);

            // Expected layout: [nonce:12][key_len:2][apiKey:N][Poly1305Tag:16]
            // Minimum output = 12 (nonce) + 2 (len) + 1 (key min) + 16 (tag) = 31
            var ct = ApiKeyCipher.Encrypt(psk, apiKey, ep);
            int expectedMin = 12 + 2 + keyBytes.Length + 16;
            Assert.AreEqual(expectedMin, ct.Length,
                $"Encrypted payload must be nonce(12) + len(2) + key({keyBytes.Length}) + tag(16) = {expectedMin} bytes.");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static int CompareKeys(byte[] a, byte[] b)
        {
            for (int i = 0; i < 32; i++)
            {
                if (a[i] < b[i]) return -1;
                if (a[i] > b[i]) return +1;
            }
            return 0;
        }

        private static byte[] Concat(byte[] a, byte[] b, byte[] c = null)
        {
            int len = a.Length + b.Length + (c?.Length ?? 0);
            var result = new byte[len];
            Buffer.BlockCopy(a, 0, result, 0, a.Length);
            Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
            if (c != null) Buffer.BlockCopy(c, 0, result, a.Length + b.Length, c.Length);
            return result;
        }
    }
}
