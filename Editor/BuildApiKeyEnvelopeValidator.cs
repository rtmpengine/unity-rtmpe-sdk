// RTMPE SDK — Editor/BuildApiKeyEnvelopeValidator.cs
//
// Build-time guard: a Player build must ship an API-key envelope (a sealed-box
// server public key or a PSK) in a NetworkSettings asset, because outside the
// Unity Editor the runtime refuses to send the API key unencrypted.  Without
// one, every connection fails at the handshake with a Timeout that is only
// diagnosable after the binary is distributed.  Failing the build here turns
// that late, opaque failure into an immediate, named one.
//
// Scope: a lightweight project-level check (no scene loading).  Three outcomes:
//   * one or more assets exist but NOT ONE carries an envelope -> fail the build
//     (the unambiguous "an asset was authored yet left blank" case);
//   * some assets carry an envelope and some are blank -> warn (this check
//     cannot tell which asset the NetworkManager in the built scenes references,
//     so blocking could be a false positive, yet a blank wired asset would still
//     fail at connect time — so surface it without blocking);
//   * no NetworkSettings asset exists at all -> warn, because a project may
//     legitimately construct NetworkSettings at runtime and ship no asset.
// The complementary runtime warning (NetworkManager.Awake, unbound Settings
// field) and the connect-time diagnostic (a wired-but-empty asset) cover the
// finer cases.  The whole body is guarded so a fault in the validator itself
// degrades to a warning and can never block a legitimate build; only the
// intended, explained failure throws.

using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Core.Diagnostics;

namespace RTMPE.Editor
{
    internal sealed class BuildApiKeyEnvelopeValidator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            int assetCount;
            int configuredCount;
            int emptyCount;
            try
            {
                string[] guids = AssetDatabase.FindAssets("t:NetworkSettings");
                assetCount = guids != null ? guids.Length : 0;
                configuredCount = 0;
                emptyCount = 0;
                if (guids != null)
                {
                    foreach (string guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        var settings = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);
                        if (settings == null)
                        {
                            // Unreadable asset: leave it uncounted rather than
                            // treat a load failure as a blank envelope.
                            continue;
                        }
                        if (ApiKeyEnvelopeCheck.IsConfigured(
                                settings.apiKeySealServerPublicKeyHex, settings.apiKeyPskHex))
                        {
                            configuredCount++;
                        }
                        else
                        {
                            emptyCount++;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                // A fault in the validator must never block a legitimate build;
                // degrade to a warning and let the build proceed.
                Debug.LogWarning(
                    "[RTMPE] Build-time API-key envelope check skipped " +
                    $"({ex.GetType().Name}: {ex.Message}).");
                return;
            }

            if (assetCount == 0)
            {
                // Ambiguous: no asset to validate.  A project may assign
                // NetworkSettings at runtime, so warn rather than block.  The
                // runtime NetworkManager.Awake warning and the connect-time
                // diagnostic still surface a genuinely unconfigured build.
                Debug.LogWarning(
                    "[RTMPE] No NetworkSettings asset was found in this build. If you assign " +
                    "NetworkSettings at runtime this is expected; otherwise create one (Project " +
                    "Settings > RTMPE or the Setup Wizard) and set apiKeySealServerPublicKeyHex " +
                    "(the gateway's X25519 key) or apiKeyPskHex, or the build will fail to connect " +
                    "outside the Unity Editor.");
                return;
            }

            if (configuredCount == 0)
            {
                throw new BuildFailedException(
                    $"[RTMPE] {assetCount} NetworkSettings asset(s) exist but none carries an API-key " +
                    "envelope.  A build cannot connect without one — outside the Unity Editor the runtime " +
                    "refuses to send the API key unencrypted.  Set apiKeySealServerPublicKeyHex (the " +
                    "gateway's X25519 key) or apiKeyPskHex on the NetworkSettings asset used by your " +
                    "NetworkManager — via Project Settings > RTMPE or the Setup Wizard — then rebuild.  " +
                    "Note: the Editor-only Keychain key store is never shipped with builds.");
            }

            if (emptyCount > 0)
            {
                // Mixed: at least one asset carries an envelope and at least one
                // is blank.  This project-level check cannot tell which asset the
                // NetworkManager in the built scenes references, so it warns
                // rather than blocks — but a blank wired asset would still fail at
                // connect time, so the risk is surfaced now instead.
                Debug.LogWarning(
                    $"[RTMPE] Of {configuredCount + emptyCount} NetworkSettings assets, {configuredCount} " +
                    $"carry an API-key envelope and {emptyCount} are blank. This build-time check cannot " +
                    "tell which asset your NetworkManager references — make sure the one wired in your " +
                    "built scenes is a configured asset, or a Standalone build will fail to connect with " +
                    "\"No API-key envelope configured\".");
            }
        }
    }
}
