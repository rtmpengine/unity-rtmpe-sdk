// RTMPE SDK — Editor/EditorApiKeyStore.cs
//
// Editor-time obfuscated storage for the gateway API key.
//
// Threat model
// ────────────
// In-scope:
//  * Casual disk-image inspection of the developer's home directory
//    (e.g. a stolen laptop image, an unintended backup, a screenshot of
//    the Unity prefs file).  EditorPrefs serialises to a plaintext
//    plist / registry / ini file; storing the API key verbatim there
//    leaks it into every backup tool the developer runs.
// Out-of-scope:
//  * Malware running interactively as the developer's user account.
//    Any process with that privilege can read the device identifier
//    used to derive the key and replay this code path.  Defending
//    against that requires an OS-level keychain or hardware-backed
//    enclave, which is project-integrator territory.  The wizard UI
//    surfaces this caveat explicitly.
//
// Cryptographic construction
// ──────────────────────────
//  KEK = HKDF-SHA256(IKM = SystemInfo.deviceUniqueIdentifier,
//                    salt = STATIC_APP_SALT,
//                    info = "RTMPE/Editor/ApiKeyStore/v1",
//                    L    = 32)
//  ciphertext, tag = ChaCha20-Poly1305(KEK, nonce, plaintext, aad = "v1")
//  stored = base64( "v1" || nonce(12) || ciphertext || tag(16) )
//
// We use the package's managed ChaCha20-Poly1305 implementation rather
// than System.Security.Cryptography.AesGcm because AesGcm was added in
// .NET Core 3.0 / .NET 5+ — Unity's Editor Mono runtime on 2022.3 LTS
// (Mono 6.x) and even the Unity 6 Editor on most platforms do not expose
// it.  Instantiating AesGcm there throws TypeInitializationException the
// first time the developer tries to save an API key.  ChaCha20-Poly1305
// from Runtime/Crypto/Internal/ is pure managed C# and works on every
// runtime the package targets.
//
// A fresh 96-bit nonce is generated for every save.  Poly1305 tag is
// verified before plaintext is returned; on tag failure (corruption,
// machine swap) we treat the stored value as unreadable and return "".
//
// Key derivation portability
// ──────────────────────────
// The KEK is derived from the per-user device identifier ONLY — not from
// Application.dataPath.  Mixing the project path into the KEK made the
// stored ciphertext stop decrypting whenever the developer renamed or
// moved the project folder, silently losing their saved API key.  The
// per-machine binding still defeats casual cross-laptop replay; the
// project path was never part of the security model, only an accidental
// portability foot-gun.
//
// Migration
// ─────────
// Older builds wrote the API key directly into EditorPrefs under the key
// "RTMPE_ApiKey".  On Load() we detect any legacy plaintext value, re-encrypt
// it under the new scheme, delete the plaintext, and emit an INFO log.

using System;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using RTMPE.Crypto.Internal;

namespace RTMPE.Editor
{
    internal static class EditorApiKeyStore
    {
        private const string PrefKey         = "RTMPE_ApiKey_Enc_v1";
        private const string LegacyPrefKey   = "RTMPE_ApiKey";
        private const string Version         = "v1";
        private const int    NonceLen        = 12;
        private const int    TagLen          = 16;

        // A fixed application salt.  This is NOT a secret — its only role is
        // to domain-separate the HKDF output from any other tool that might
        // hash the same device identifier.
        private static readonly byte[] AppSalt =
            Encoding.UTF8.GetBytes("RTMPE.Editor.ApiKeyStore.salt.v1");

        private static readonly byte[] HkdfInfo =
            Encoding.UTF8.GetBytes("RTMPE/Editor/ApiKeyStore/v1");

        private static readonly byte[] Aad =
            Encoding.UTF8.GetBytes(Version);

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Persist the API key in obfuscated form.  Empty / null clears
        /// the stored value.  Always also clears any legacy plaintext entry.
        /// </summary>
        public static void Save(string apiKey)
        {
            // Always scrub the legacy plaintext slot, regardless of input.
            if (EditorPrefs.HasKey(LegacyPrefKey))
                EditorPrefs.DeleteKey(LegacyPrefKey);

            if (string.IsNullOrEmpty(apiKey))
            {
                EditorPrefs.DeleteKey(PrefKey);
                return;
            }

            try
            {
                var encoded = Encrypt(apiKey);
                EditorPrefs.SetString(PrefKey, encoded);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Failed to encrypt API key for storage: {ex.Message}");
            }
        }

        /// <summary>
        /// Read the API key.  Returns "" when nothing is stored or the
        /// ciphertext is unreadable on this machine (corruption, machine
        /// reinstall, etc.).  Migrates legacy plaintext on first load.
        /// </summary>
        public static string Load()
        {
            // Legacy migration first — if a plaintext key exists, promote it.
            if (EditorPrefs.HasKey(LegacyPrefKey))
            {
                var legacy = EditorPrefs.GetString(LegacyPrefKey, "");
                EditorPrefs.DeleteKey(LegacyPrefKey);
                if (!string.IsNullOrEmpty(legacy))
                {
                    Debug.Log("[RTMPE] Migrated legacy plaintext API key from EditorPrefs to obfuscated store.");
                    Save(legacy);
                    return legacy;
                }
            }

            var encoded = EditorPrefs.GetString(PrefKey, "");
            if (string.IsNullOrEmpty(encoded)) return "";

            try
            {
                return Decrypt(encoded);
            }
            catch (CryptographicException)
            {
                // Tag mismatch — most commonly the project was copied to a
                // different developer's machine.  Drop the unreadable blob
                // so the wizard prompts for a fresh key.
                Debug.LogWarning("[RTMPE] Stored API key cannot be decrypted on this machine; clearing.");
                EditorPrefs.DeleteKey(PrefKey);
                return "";
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Failed to decrypt stored API key: {ex.Message}");
                return "";
            }
        }

        /// <summary>Unconditionally remove every stored form of the key.</summary>
        public static void Clear()
        {
            EditorPrefs.DeleteKey(PrefKey);
            EditorPrefs.DeleteKey(LegacyPrefKey);
        }

        // ── Internals ─────────────────────────────────────────────────────

        // Public for unit tests so they can verify ciphertext round-trips
        // without re-implementing the wire format.
        internal static string Encrypt(string plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

            var key       = DeriveKey();
            var nonce     = new byte[NonceLen];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(nonce);

            var ptBytes = Encoding.UTF8.GetBytes(plaintext);
            byte[] sealedBlob = null;
            try
            {
                // ChaCha20Poly1305Impl.Seal returns ciphertext || tag.
                sealedBlob = ChaCha20Poly1305Impl.Seal(key, nonce, ptBytes, Aad);

                var versionBytes = Encoding.ASCII.GetBytes(Version);
                var blob = new byte[versionBytes.Length + NonceLen + sealedBlob.Length];
                Buffer.BlockCopy(versionBytes, 0, blob, 0,                                versionBytes.Length);
                Buffer.BlockCopy(nonce,        0, blob, versionBytes.Length,              NonceLen);
                Buffer.BlockCopy(sealedBlob,   0, blob, versionBytes.Length + NonceLen,    sealedBlob.Length);
                return Convert.ToBase64String(blob);
            }
            finally
            {
                // Best-effort scrub of sensitive intermediate buffers from managed heap.
                Array.Clear(ptBytes, 0, ptBytes.Length);
                Array.Clear(key,     0, key.Length);
            }
        }

        internal static string Decrypt(string encoded)
        {
            var blob = Convert.FromBase64String(encoded);
            var versionBytes = Encoding.ASCII.GetBytes(Version);

            if (blob.Length < versionBytes.Length + NonceLen + TagLen)
                throw new CryptographicException("Ciphertext blob is truncated.");

            for (int i = 0; i < versionBytes.Length; i++)
                if (blob[i] != versionBytes[i])
                    throw new CryptographicException("Unknown ciphertext version.");

            var nonce = new byte[NonceLen];
            var ctWithTagLen = blob.Length - versionBytes.Length - NonceLen;
            var ctWithTag = new byte[ctWithTagLen];

            Buffer.BlockCopy(blob, versionBytes.Length,            nonce,     0, NonceLen);
            Buffer.BlockCopy(blob, versionBytes.Length + NonceLen, ctWithTag, 0, ctWithTagLen);

            var key = DeriveKey();
            try
            {
                var pt = ChaCha20Poly1305Impl.Open(key, nonce, ctWithTag, Aad);
                if (pt == null)
                    throw new CryptographicException("ChaCha20-Poly1305 tag verification failed.");
                try
                {
                    return Encoding.UTF8.GetString(pt);
                }
                finally
                {
                    Array.Clear(pt, 0, pt.Length);
                }
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        /// <summary>
        /// HKDF-SHA256 derivation from the per-machine device identifier.
        /// Project path is intentionally excluded so renaming or moving the
        /// project on the same machine does not invalidate the stored
        /// ciphertext.  Per-project isolation is not part of the threat
        /// model: a developer who can read EditorPrefs for project A on a
        /// machine can already trivially open project B in the Editor.
        /// </summary>
        private static byte[] DeriveKey()
        {
            var deviceId = SystemInfo.deviceUniqueIdentifier ?? "rtmpe-unknown-device";
            var ikm      = Encoding.UTF8.GetBytes(deviceId);

            // RFC 5869 HKDF-SHA256, single-block (L = 32 ≤ HashLen).
            byte[] prk;
            using (var hmac = new HMACSHA256(AppSalt))
                prk = hmac.ComputeHash(ikm);

            var t1Input = new byte[HkdfInfo.Length + 1];
            Buffer.BlockCopy(HkdfInfo, 0, t1Input, 0, HkdfInfo.Length);
            t1Input[HkdfInfo.Length] = 0x01;

            byte[] okm;
            using (var hmac = new HMACSHA256(prk))
                okm = hmac.ComputeHash(t1Input);

            Array.Clear(prk, 0, prk.Length);
            Array.Clear(ikm, 0, ikm.Length);
            return okm; // 32 bytes
        }
    }
}
