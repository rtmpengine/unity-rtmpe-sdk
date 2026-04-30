// RTMPE SDK — Editor/ApiKeyStore.cs
//
// Editor-only credential store for the RTMPE API key.
//
// ============================================================================
// SECURITY / THREAT MODEL
// ============================================================================
// The Unity Editor stores per-user "EditorPrefs" in plaintext (Windows
// registry HKCU, macOS ~/Library/Preferences plist, Linux ~/.config/unity3d).
// Persisting an RTMPE API key — which is sufficient to authenticate
// against the gateway as the developer's project — to plaintext disk is a
// credential-theft risk: any process running under the developer's user
// account (browser extensions, malicious npm packages, recovered backups)
// can read the key.
//
// This class hides that secret behind the platform's user-scoped
// credential vault:
//
//   • Windows  — DPAPI (CryptProtectData / CryptUnprotectData with
//                CRYPTPROTECT_LOCAL_MACHINE = 0). Ciphertext is bound to
//                the current user's Windows login; another local user
//                cannot recover the key. Per Microsoft DPAPI guidance.
//
//   • macOS    — Keychain Services via the `security` CLI (add/find/
//                delete-generic-password). The default user keychain is
//                gated by the user's login password (or biometrics on
//                Apple silicon). Same isolation guarantees as Xcode's
//                Apple-ID storage.
//
//   • Linux    — libsecret via the `secret-tool` CLI (freedesktop.org
//                Secret Service API). The active user's session keyring
//                (gnome-keyring, KWallet) decrypts the secret only while
//                the desktop session is unlocked.
//
//   • Other / unsupported — clear log warning + fallback to obfuscated
//                EditorPrefs. The fallback uses a per-machine random
//                32-byte vault key (also stored in EditorPrefs) and
//                ChaCha20-Poly1305 to detect tampering. This is
//                obfuscation, not encryption — explicitly documented in
//                the warning log so integrators can choose to opt out.
//
// IMPORTANT: this file is Editor-only. It is NEVER shipped with builds.
// API keys are only persisted locally during development; production
// builds embed the key in NetworkSettings via the Project Settings panel
// at build time, never at runtime via this store.
//
// References:
//   • Microsoft DPAPI:    learn.microsoft.com/dotnet/standard/security/
//                         how-to-use-data-protection
//   • Apple Keychain:     developer.apple.com/documentation/security/
//                         keychain_services
//   • Freedesktop Secret: specifications.freedesktop.org/secret-service/
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using Debug = UnityEngine.Debug;

namespace RTMPE.Editor
{
    /// <summary>
    /// Reads and writes the RTMPE API key from a per-user OS credential
    /// vault. Editor-only; never compiled into player builds.
    /// </summary>
    /// <remarks>
    /// Thread-affine to the Editor main thread (calls
    /// <c>UnityEditor.EditorPrefs</c> in the fallback path).
    /// </remarks>
    public static class ApiKeyStore
    {
        // Single canonical service+account pair so all SDK Editor tooling
        // reads/writes the same record.
        private const string ServiceName = "com.rtmpe.sdk";
        private const string AccountName = "ApiKey";

        // EditorPrefs fallback markers. The vault key lives separately so
        // even the obfuscated path can be wiped without losing the key.
        private const string FallbackEntropyPref = "RTMPE_ApiKey.fallback.entropy.v1";
        private const string FallbackBlobPref    = "RTMPE_ApiKey.fallback.blob.v1";

        // Migration: SetupWizard previously stored the API key in a
        // plaintext EditorPrefs entry under this name. Reads transparently
        // upgrade the entry into the secure store and delete the plaintext.
        private const string LegacyPlaintextPref = "RTMPE_ApiKey";

        /// <summary>
        /// Read the API key, returning <c>""</c> if no key has been saved.
        /// On first call, transparently migrates legacy plaintext entries
        /// into the secure store.
        /// </summary>
        public static string Load()
        {
            // 1. Legacy plaintext migration (one-shot).
            var legacy = EditorPrefs.GetString(LegacyPlaintextPref, null);
            if (!string.IsNullOrEmpty(legacy))
            {
                Save(legacy);

                // Confirm the secure store can return the key before
                // discarding the only surviving copy. Read back directly
                // instead of recursing into Load() (which would re-enter
                // this migration branch).
                string readback = null;
                try
                {
                    if (!TryReadFromOsKeychain(out readback)) readback = null;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[RTMPE] OS keychain read failed: {ex.GetType().Name} — {ex.Message}");
                    readback = null;
                }
                if (string.IsNullOrEmpty(readback)) readback = TryReadFallback();

                if (readback == legacy)
                {
                    EditorPrefs.DeleteKey(LegacyPlaintextPref);
                }
                else
                {
                    Debug.LogWarning(
                        "[RTMPE] Legacy API key migration could not verify the new " +
                        "secure-store entry; preserving the legacy EditorPrefs entry " +
                        "so the key is not lost. Migration will retry on next load.");
                }
                return legacy;
            }

            // 2. OS-keychain path.
            try
            {
                if (TryReadFromOsKeychain(out var fromOs)) return fromOs ?? "";
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain read failed: {ex.GetType().Name} — {ex.Message}");
            }

            // 3. Fallback path.
            return TryReadFallback() ?? "";
        }

        /// <summary>
        /// Persist the API key. Empty / null clears the entry.
        /// </summary>
        public static void Save(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Delete();
                return;
            }

            try
            {
                if (TryWriteToOsKeychain(apiKey)) return;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain write failed: {ex.GetType().Name} — {ex.Message}");
            }

            WriteFallback(apiKey);
            Debug.LogWarning(
                "[RTMPE] OS-keychain credential store unavailable — falling back to " +
                "obfuscated EditorPrefs. The API key is NOT cryptographically " +
                "protected against another process running as your user. Install " +
                "the platform credential helper (libsecret on Linux) or run the " +
                "Editor on a supported platform to enable secure storage.");
        }

        /// <summary>Delete any saved API key from every backend.</summary>
        public static void Delete()
        {
            try { TryDeleteFromOsKeychain(); }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RTMPE] OS keychain delete failed: {ex.GetType().Name} — {ex.Message}");
            }

            EditorPrefs.DeleteKey(FallbackEntropyPref);
            EditorPrefs.DeleteKey(FallbackBlobPref);
            EditorPrefs.DeleteKey(LegacyPlaintextPref);
        }

        // ── Platform dispatch ────────────────────────────────────────────────

        private static bool TryReadFromOsKeychain(out string apiKey)
        {
            apiKey = null;
#if UNITY_EDITOR_WIN
            return TryReadDpapi(out apiKey);
#elif UNITY_EDITOR_OSX
            return TryReadMacKeychain(out apiKey);
#elif UNITY_EDITOR_LINUX
            return TryReadSecretTool(out apiKey);
#else
            return false;
#endif
        }

        private static bool TryWriteToOsKeychain(string apiKey)
        {
#if UNITY_EDITOR_WIN
            return TryWriteDpapi(apiKey);
#elif UNITY_EDITOR_OSX
            return TryWriteMacKeychain(apiKey);
#elif UNITY_EDITOR_LINUX
            return TryWriteSecretTool(apiKey);
#else
            return false;
#endif
        }

        private static void TryDeleteFromOsKeychain()
        {
#if UNITY_EDITOR_WIN
            TryDeleteDpapi();
#elif UNITY_EDITOR_OSX
            TryDeleteMacKeychain();
#elif UNITY_EDITOR_LINUX
            TryDeleteSecretTool();
#endif
        }

        // ── Windows: DPAPI ───────────────────────────────────────────────────
        //
        // DPAPI is invoked via P/Invoke (System.Security.Cryptography.
        // ProtectedData is .NET-Framework-only — Unity's Mono runtime does
        // not expose it). The ciphertext is persisted to a per-user file
        // under %APPDATA%/RTMPE/ApiKey.bin so the secret never touches
        // EditorPrefs / the Windows registry.

#if UNITY_EDITOR_WIN
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
            int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct,
            int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static string DpapiFilePath()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "RTMPE");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "ApiKey.bin");
        }

        private static bool TryWriteDpapi(string apiKey)
        {
            var plaintext = Encoding.UTF8.GetBytes(apiKey);
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = plaintext.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptProtectData(ref inBlob, "RTMPE-ApiKey", IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return false;

                var ciphertext = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, ciphertext, 0, outBlob.cbData);
                File.WriteAllBytes(DpapiFilePath(), ciphertext);
                return true;
            }
            finally
            {
                Array.Clear(plaintext, 0, plaintext.Length);
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static bool TryReadDpapi(out string apiKey)
        {
            apiKey = null;
            var path = DpapiFilePath();
            if (!File.Exists(path)) return false;
            var ciphertext = File.ReadAllBytes(path);
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            var handle = GCHandle.Alloc(ciphertext, GCHandleType.Pinned);
            try
            {
                inBlob.cbData = ciphertext.Length;
                inBlob.pbData = handle.AddrOfPinnedObject();

                if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero,
                        IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    return false;

                var plaintext = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, plaintext, 0, outBlob.cbData);
                apiKey = Encoding.UTF8.GetString(plaintext);
                Array.Clear(plaintext, 0, plaintext.Length);
                return true;
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static void TryDeleteDpapi()
        {
            var path = DpapiFilePath();
            if (File.Exists(path)) File.Delete(path);
        }
#endif

        // ── macOS: Keychain Services via `security` CLI ──────────────────────

#if UNITY_EDITOR_OSX
        private static bool TryWriteMacKeychain(string apiKey)
        {
            // The API key MUST NOT appear in argv: every user on the system
            // can read another user's argv via `ps -ef` / `ps aux`, and on
            // shared CI runners or developer workstations with multiple
            // accounts that exposure leaks the key beyond the Editor's
            // owning user.  Mirror the Linux secret-tool flow: spawn
            // `security -i` (interactive command mode), then write the
            // command — including the secret — to stdin.  Stdin is a pipe
            // visible only to the parent and child processes; argv is not.
            var psi = new ProcessStartInfo("security", "-i")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            // `security -i` reads one command per line.  Account / service
            // / label values are quoted with backslash-escaping so a stray
            // quote in those (developer-supplied) constants cannot break
            // the parser; the apiKey is similarly quoted so embedded
            // whitespace survives.  Documented `security` quoting rules
            // accept C-style backslash escapes inside double quotes.
            p.StandardInput.WriteLine(
                $"add-generic-password -U -a \"{EscapeShell(AccountName)}\" -s \"{EscapeShell(ServiceName)}\" -w \"{EscapeShell(apiKey)}\"");
            p.StandardInput.Close();
            return TryWaitForCleanExit(p, 5_000, out int exit) && exit == 0;
        }

        private static bool TryReadMacKeychain(out string apiKey)
        {
            apiKey = null;
            var psi = new ProcessStartInfo("security",
                $"find-generic-password -a \"{AccountName}\" -s \"{ServiceName}\" -w")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            string stdout = p.StandardOutput.ReadToEnd();
            if (!TryWaitForCleanExit(p, 5_000, out int exit) || exit != 0) return false;
            apiKey = stdout.TrimEnd('\r', '\n');
            return !string.IsNullOrEmpty(apiKey);
        }

        private static void TryDeleteMacKeychain()
        {
            var psi = new ProcessStartInfo("security",
                $"delete-generic-password -a \"{AccountName}\" -s \"{ServiceName}\"")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            TryWaitForCleanExit(p, 5_000, out _);
        }

        private static string EscapeShell(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
#endif

        // ── Linux: libsecret via `secret-tool` CLI ───────────────────────────

#if UNITY_EDITOR_LINUX
        private static bool TryWriteSecretTool(string apiKey)
        {
            if (!CommandExists("secret-tool")) return false;
            var psi = new ProcessStartInfo("secret-tool",
                $"store --label=\"RTMPE API Key\" service {ServiceName} account {AccountName}")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            p.StandardInput.Write(apiKey);
            p.StandardInput.Close();
            return TryWaitForCleanExit(p, 5_000, out int exit) && exit == 0;
        }

        private static bool TryReadSecretTool(out string apiKey)
        {
            apiKey = null;
            if (!CommandExists("secret-tool")) return false;
            var psi = new ProcessStartInfo("secret-tool",
                $"lookup service {ServiceName} account {AccountName}")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            apiKey = p.StandardOutput.ReadToEnd();
            if (!TryWaitForCleanExit(p, 5_000, out int exit) || exit != 0)
            {
                apiKey = null;
                return false;
            }
            apiKey = apiKey.TrimEnd('\r', '\n');
            return !string.IsNullOrEmpty(apiKey);
        }

        private static void TryDeleteSecretTool()
        {
            if (!CommandExists("secret-tool")) return;
            var psi = new ProcessStartInfo("secret-tool",
                $"clear service {ServiceName} account {AccountName}")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            TryWaitForCleanExit(p, 5_000, out _);
        }

        private static bool CommandExists(string cmd)
        {
            try
            {
                var psi = new ProcessStartInfo("/bin/sh", $"-c \"command -v {cmd}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                using var p = Process.Start(psi);
                return TryWaitForCleanExit(p, 2_000, out int exit) && exit == 0;
            }
            catch { return false; }
        }
#endif

#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        // Wait for the child to terminate within timeoutMs and report its exit
        // code.  Process.WaitForExit(int) returns FALSE on timeout WITHOUT
        // killing the child; reading p.ExitCode in that state throws
        // InvalidOperationException("Process must exit before requested
        // information can be determined").  A wedged `security` (macOS) or
        // `secret-tool` (Linux) child — gnome-keyring locked, libsecret D-Bus
        // broker hung, mid-update keychain — would otherwise propagate that
        // exception out of the keystore reader and silently downgrade the
        // developer to the obfuscated-EditorPrefs fallback path.  Killing on
        // timeout keeps the keystore semantics observable.
        private static bool TryWaitForCleanExit(Process p, int timeoutMs, out int exitCode)
        {
            if (p == null) { exitCode = -1; return false; }
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { /* already exited or platform refused — best-effort */ }
                exitCode = -1;
                return false;
            }
            try { exitCode = p.ExitCode; }
            catch (InvalidOperationException) { exitCode = -1; return false; }
            return true;
        }
#endif

        // ── Fallback: ChaCha20-Poly1305 over EditorPrefs ─────────────────────
        //
        // This is OBFUSCATION, not encryption — the AEAD key is generated
        // once and stored alongside the ciphertext under EditorPrefs, so
        // any process with access to EditorPrefs can decrypt. The point is
        // (a) tamper-detection (bit-flip in EditorPrefs is detected) and
        // (b) reduction of accidental disclosure (e.g. a developer
        // screen-sharing the Unity preferences plist will not show the
        // API key in cleartext).

        private static byte[] FallbackKey()
        {
            var hex = EditorPrefs.GetString(FallbackEntropyPref, null);
            if (!string.IsNullOrEmpty(hex) && hex.Length == 64)
            {
                return HexToBytes(hex);
            }
            var key = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            EditorPrefs.SetString(FallbackEntropyPref, BytesToHex(key));
            return key;
        }

        private static void WriteFallback(string apiKey)
        {
            var key = FallbackKey();
            try
            {
                var nonce = new byte[12];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                var plaintext = Encoding.UTF8.GetBytes(apiKey);
                var ciphertext = RTMPE.Crypto.Internal.ChaCha20Poly1305Impl.Seal(
                    key, nonce, plaintext, Array.Empty<byte>());
                var blob = new byte[12 + ciphertext.Length];
                Buffer.BlockCopy(nonce, 0, blob, 0, 12);
                Buffer.BlockCopy(ciphertext, 0, blob, 12, ciphertext.Length);
                EditorPrefs.SetString(FallbackBlobPref, Convert.ToBase64String(blob));
                Array.Clear(plaintext, 0, plaintext.Length);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        private static string TryReadFallback()
        {
            var b64 = EditorPrefs.GetString(FallbackBlobPref, null);
            if (string.IsNullOrEmpty(b64)) return null;
            byte[] blob;
            try { blob = Convert.FromBase64String(b64); }
            catch { return null; }
            if (blob.Length < 12 + 16) return null;

            var key = FallbackKey();
            try
            {
                var nonce = new byte[12];
                Buffer.BlockCopy(blob, 0, nonce, 0, 12);
                var ct = new byte[blob.Length - 12];
                Buffer.BlockCopy(blob, 12, ct, 0, ct.Length);
                var pt = RTMPE.Crypto.Internal.ChaCha20Poly1305Impl.Open(
                    key, nonce, ct, Array.Empty<byte>());
                if (pt == null) return null;
                var s = Encoding.UTF8.GetString(pt);
                Array.Clear(pt, 0, pt.Length);
                return s;
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        private static string BytesToHex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (var x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static byte[] HexToBytes(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = byte.Parse(hex.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber);
            return b;
        }
    }
}
#endif
