// RTMPE SDK — Tests/Editor/SetupWizardTests.cs
//
// Verifies the obfuscated API key store contract:
//  1. Round-trip: Save(x) then Load() returns x.
//  2. On-disk form is NOT plaintext (the key never appears in the
//     EditorPrefs string).
//  3. Legacy plaintext under "RTMPE_ApiKey" is migrated and erased on
//     first Load().
//  4. Clear() erases all forms.
//  5. AES-GCM tag verification rejects tampered ciphertext.

using System;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using RTMPE.Editor;

namespace RTMPE.Tests.Editor
{
    [TestFixture]
    [Category("SetupWizard")]
    public class SetupWizardTests
    {
        private const string EncPrefKey    = "RTMPE_ApiKey_Enc_v1";
        private const string LegacyPrefKey = "RTMPE_ApiKey";

        private string _savedEnc;
        private string _savedLegacy;
        private bool   _hadEnc;
        private bool   _hadLegacy;

        [SetUp]
        public void SetUp()
        {
            // Snapshot any pre-existing user values so we can restore them.
            _hadEnc      = EditorPrefs.HasKey(EncPrefKey);
            _hadLegacy   = EditorPrefs.HasKey(LegacyPrefKey);
            _savedEnc    = _hadEnc    ? EditorPrefs.GetString(EncPrefKey)    : null;
            _savedLegacy = _hadLegacy ? EditorPrefs.GetString(LegacyPrefKey) : null;

            EditorPrefs.DeleteKey(EncPrefKey);
            EditorPrefs.DeleteKey(LegacyPrefKey);
        }

        [TearDown]
        public void TearDown()
        {
            EditorPrefs.DeleteKey(EncPrefKey);
            EditorPrefs.DeleteKey(LegacyPrefKey);
            if (_hadEnc)    EditorPrefs.SetString(EncPrefKey,    _savedEnc);
            if (_hadLegacy) EditorPrefs.SetString(LegacyPrefKey, _savedLegacy);
        }

        // ── Round-trip ───────────────────────────────────────────────────

        [Test]
        [Description("Save then Load returns the original API key.")]
        public void RoundTrip_PreservesApiKey()
        {
            const string apiKey = "rk_live_abc123_THISISASECRET_xyz789";
            EditorApiKeyStore.Save(apiKey);
            var loaded = EditorApiKeyStore.Load();
            Assert.AreEqual(apiKey, loaded);
        }

        [Test]
        [Description("Stored value on disk does NOT contain the plaintext API key.")]
        public void Save_DoesNotPersistPlaintext()
        {
            const string apiKey = "rk_live_DETECTABLE_MARKER_token";
            EditorApiKeyStore.Save(apiKey);

            var stored = EditorPrefs.GetString(EncPrefKey, "");
            Assert.IsNotEmpty(stored, "Encrypted blob should have been written.");
            StringAssert.DoesNotContain(apiKey, stored,
                "Plaintext API key must not appear in the EditorPrefs blob.");
            StringAssert.DoesNotContain("DETECTABLE_MARKER", stored,
                "No plaintext fragment of the API key may leak.");
        }

        [Test]
        [Description("Two encrypts of the same plaintext yield distinct ciphertexts (fresh nonce).")]
        public void Save_UsesFreshNoncePerCall()
        {
            const string apiKey = "rk_test_nonce_freshness";
            EditorApiKeyStore.Save(apiKey);
            var first = EditorPrefs.GetString(EncPrefKey, "");
            EditorApiKeyStore.Save(apiKey);
            var second = EditorPrefs.GetString(EncPrefKey, "");

            Assert.AreNotEqual(first, second,
                "Each Save() must use a fresh random nonce; ciphertexts must differ.");
        }

        // ── Legacy migration ─────────────────────────────────────────────

        [Test]
        [Description("Legacy plaintext key is migrated to encrypted form and the plaintext is deleted.")]
        public void Load_MigratesLegacyPlaintext_AndDeletesIt()
        {
            const string legacyKey = "rk_legacy_PLAINTEXT_keymaterial";
            EditorPrefs.SetString(LegacyPrefKey, legacyKey);
            EditorPrefs.DeleteKey(EncPrefKey);

            var loaded = EditorApiKeyStore.Load();

            Assert.AreEqual(legacyKey, loaded, "Legacy value must be returned to caller.");
            Assert.IsFalse(EditorPrefs.HasKey(LegacyPrefKey),
                "Plaintext legacy entry must be deleted after migration.");
            var migrated = EditorPrefs.GetString(EncPrefKey, "");
            Assert.IsNotEmpty(migrated, "Migrated ciphertext must be written.");
            StringAssert.DoesNotContain(legacyKey, migrated,
                "Migrated blob must not contain the plaintext key.");
        }

        [Test]
        [Description("Empty legacy value does not create a stale encrypted entry.")]
        public void Load_EmptyLegacyValue_ClearsAndReturnsEmpty()
        {
            EditorPrefs.SetString(LegacyPrefKey, "");
            EditorPrefs.DeleteKey(EncPrefKey);

            var loaded = EditorApiKeyStore.Load();

            Assert.AreEqual("", loaded);
            Assert.IsFalse(EditorPrefs.HasKey(LegacyPrefKey));
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
        }

        // ── Clear / empty contracts ──────────────────────────────────────

        [Test]
        [Description("Clear erases both legacy and encrypted slots.")]
        public void Clear_RemovesAllSlots()
        {
            EditorApiKeyStore.Save("anything");
            EditorPrefs.SetString(LegacyPrefKey, "leftover");

            EditorApiKeyStore.Clear();

            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
            Assert.IsFalse(EditorPrefs.HasKey(LegacyPrefKey));
        }

        [Test]
        [Description("Save(empty) clears the encrypted slot rather than persisting an empty blob.")]
        public void SaveEmpty_ClearsEncryptedSlot()
        {
            EditorApiKeyStore.Save("something");
            Assert.IsTrue(EditorPrefs.HasKey(EncPrefKey));

            EditorApiKeyStore.Save("");
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey));
        }

        // ── Auto-open opt-out ────────────────────────────────────────────

        [Test]
        [Description("ToggleAutoOpen flips the EditorPrefs flag back and forth deterministically.")]
        public void ToggleAutoOpen_TogglesEditorPrefFlag()
        {
            const string key = SetupWizard.AutoOpenDisabledPrefKey;

            bool hadPrev = EditorPrefs.HasKey(key);
            bool prev    = hadPrev && EditorPrefs.GetBool(key, false);

            try
            {
                // Start from a known-disabled state: flag absent / false.
                EditorPrefs.DeleteKey(key);
                Assert.IsFalse(EditorPrefs.GetBool(key, false),
                    "Pre-condition: auto-open is enabled by default.");

                SetupWizard.ToggleAutoOpen();
                Assert.IsTrue(EditorPrefs.GetBool(key, false),
                    "After first toggle: auto-open must be disabled.");

                SetupWizard.ToggleAutoOpen();
                Assert.IsFalse(EditorPrefs.GetBool(key, false),
                    "After second toggle: auto-open must be re-enabled.");
            }
            finally
            {
                EditorPrefs.DeleteKey(key);
                if (hadPrev) EditorPrefs.SetBool(key, prev);
            }
        }

        // ── Tamper detection ─────────────────────────────────────────────

        [Test]
        [Description("Tampering with the ciphertext causes Decrypt to fail and the slot to be cleared.")]
        public void Load_TamperedCiphertext_RejectedAndCleared()
        {
            EditorApiKeyStore.Save("rk_tamper_target");
            var blob = EditorPrefs.GetString(EncPrefKey, "");
            Assert.IsNotEmpty(blob);

            // Flip the final base64 char to a different valid one to corrupt
            // the GCM ciphertext / tag region.
            var raw = Convert.FromBase64String(blob);
            raw[raw.Length - 1] ^= 0x01;
            EditorPrefs.SetString(EncPrefKey, Convert.ToBase64String(raw));

            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            var loaded = EditorApiKeyStore.Load();
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = false;

            Assert.AreEqual("", loaded, "Tampered blob must not yield plaintext.");
            Assert.IsFalse(EditorPrefs.HasKey(EncPrefKey),
                "Unreadable blob should be cleared so the wizard prompts for a fresh key.");
        }
    }
}
