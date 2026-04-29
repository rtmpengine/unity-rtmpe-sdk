// RTMPE SDK — Tests/Editor/CryptoLowSeverityHardeningTests.cs
//
// Hardening tests for Editor-only crypto paths:
//
//  • EditorApiKeyStore.Encrypt — the try/finally cleanup path does not
//    corrupt the produced ciphertext (ptBytes is cleared AFTER the blob is
//    assembled, so the returned base64 string is unaffected).
//  • EditorApiKeyStore.Encrypt/Decrypt — full round-trip still works after
//    the try/finally restructure.
//  • Encrypt returns a ciphertext that does not contain the plaintext key.
//
// Note: managed local variables (like the ptBytes buffer) cannot be inspected
// after a method returns — they are released to the GC with no stable address.
// The tests below verify the *behavioural* postconditions of the cleanup:
// the returned ciphertext is valid, the plaintext is not present in the output,
// and two encryptions of the same key differ (fresh nonce per call).
//
// Editor-only: requires UnityEditor + RTMPE.SDK.Editor assembly.

using System;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using RTMPE.Editor;

namespace RTMPE.Tests.Editor
{
    [TestFixture]
    [Category("Crypto")]
    [Category("EditorApiKeyStore")]
    public class CryptoLowSeverityHardeningTests
    {
        private const string EncPrefKey = "RTMPE_ApiKey_Enc_v1";

        private bool   _hadEnc;
        private string _savedEnc;

        [SetUp]
        public void SetUp()
        {
            _hadEnc   = EditorPrefs.HasKey(EncPrefKey);
            _savedEnc = _hadEnc ? EditorPrefs.GetString(EncPrefKey) : null;
            EditorPrefs.DeleteKey(EncPrefKey);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(EncPrefKey);
            if (_hadEnc) EditorPrefs.SetString(EncPrefKey, _savedEnc);
        }

        // ── try/finally path does not corrupt the ciphertext ──────────────

        [Test]
        [Description("Encrypt's finally block clears ptBytes after the blob is returned — ciphertext must remain valid.")]
        public void Encrypt_TryFinallyCleanup_DoesNotCorruptCiphertext()
        {
            // If the finally block accidentally cleared `ct` instead of `ptBytes`,
            // Decrypt would throw a GCM tag-mismatch (CryptographicException).
            const string apiKey = "rtmpe-hardening-test-key-alpha";
            var blob = EditorApiKeyStore.Encrypt(apiKey);
            Assert.IsNotNull(blob, "Encrypt must return a non-null base64 blob.");
            Assert.IsNotEmpty(blob);

            var recovered = EditorApiKeyStore.Decrypt(blob);
            Assert.AreEqual(apiKey, recovered,
                "Decrypt must recover the original key; a corrupted ciphertext would throw CryptographicException.");
        }

        // ── full round-trip integrity ─────────────────────────────────────

        [Test]
        [Description("Encrypt/Decrypt round-trip returns the exact original string.")]
        public void Encrypt_Decrypt_RoundTrip_Succeeds()
        {
            const string apiKey = "rk_live_roundtrip_test_9f8e7d6c";
            var blob     = EditorApiKeyStore.Encrypt(apiKey);
            var recovered = EditorApiKeyStore.Decrypt(blob);
            Assert.AreEqual(apiKey, recovered);
        }

        [Test]
        [Description("Encrypt/Decrypt round-trip works for an empty-ish but non-null key.")]
        public void Encrypt_Decrypt_SingleCharKey_RoundTrips()
        {
            const string apiKey = "X";
            var blob     = EditorApiKeyStore.Encrypt(apiKey);
            var recovered = EditorApiKeyStore.Decrypt(blob);
            Assert.AreEqual(apiKey, recovered);
        }

        // ── plaintext does not appear in ciphertext output ────────────────

        [Test]
        [Description("Returned blob must not contain the plaintext API key — ptBytes is a sensitive intermediate.")]
        public void Encrypt_PlaintextBytes_NotPresentInReturnedBlob()
        {
            const string apiKey = "rtmpe-canary-plaintext-marker-z9y8";
            var blob = EditorApiKeyStore.Encrypt(apiKey);

            var keyBytes  = Encoding.UTF8.GetBytes(apiKey);
            var blobBytes = Convert.FromBase64String(blob);

            Assert.IsFalse(ContainsSubsequence(blobBytes, keyBytes),
                "The ciphertext blob must not contain the plaintext API key bytes.");
        }

        // ── nonce freshness (proves the try/finally path executes fully) ──

        [Test]
        [Description("Two successive Encrypt calls for the same key produce different ciphertexts.")]
        public void Encrypt_ConsecutiveCalls_ProduceDifferentCiphertexts()
        {
            const string apiKey = "rk_test_nonce_freshness_var_a";
            var first  = EditorApiKeyStore.Encrypt(apiKey);
            var second = EditorApiKeyStore.Encrypt(apiKey);
            Assert.AreNotEqual(first, second,
                "Fresh random nonce must produce a distinct ciphertext each call.");
        }

        // ── helper ───────────────────────────────────────────────────────

        private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || haystack.Length < needle.Length) return false;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return true;
            }
            return false;
        }
    }
}
