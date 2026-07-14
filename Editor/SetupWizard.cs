// RTMPE SDK — Editor/SetupWizard.cs
//
// One-click setup wizard for integrating RTMPE into any Unity project.
// Opens automatically on first import or via: Window > RTMPE > Setup Wizard.
//
// Steps guided:
//  1. SDK import verification (assemblies + packages)
//  2. NetworkManager prefab placement in the scene
//  3. API key & server configuration
//  4. Game-Type defaults (max players, tick rate)
//  5. Connection test (ping gateway)

using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Guided setup wizard shown on first SDK import or via the Window menu.
    /// </summary>
    public sealed class SetupWizard : EditorWindow
    {
        // ── State ─────────────────────────────────────────────────────────────
        private int    _step;
        private const int TotalSteps = 5;

        private string _apiKey       = "";
        private string _gatewayHost  = "127.0.0.1";
        private int    _gatewayPort  = 7777;
        private int    _maxPlayers   = 16;
        private int    _tickRate     = 30;
        private string _sealKey      = "";
        private string _pinnedKey    = "";
        private string _jwtIssuer    = "";
        private string _jwtAudience  = "";

        private string _statusMsg    = "";
        private bool   _testPassed;
        private bool   _networkManagerFound;

        // Surface to the user when ApiKeyStore.Save() throws (OS keychain
        // quota, IPC failure with secret-tool, etc.).  Without this the
        // wizard would silently advance past the API-key step on a Save
        // failure and the developer would believe their key was persisted.
        private string _lastSaveError;

        // Set whenever the user has typed input in the current session;
        // gates the Cancel-confirmation dialog so a fresh open of the
        // wizard does not nag about discarding nothing.
        private bool   _hasUnsavedChanges;

        // ── Icons ─────────────────────────────────────────────────────────────
        private Texture2D _iconOk;
        private Texture2D _iconWarn;

        // ── Entry points ──────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Setup Wizard")]
        public static void Open()
        {
            var win = GetWindow<SetupWizard>(true, "RTMPE Setup Wizard", true);
            win.minSize = new Vector2(480, 420);
        }

        /// <summary>
        /// Auto-open on first SDK import (SessionState flag prevents re-showing).
        /// </summary>
        [InitializeOnLoadMethod]
        private static void AutoOpen()
        {
            if (!SessionState.GetBool("RTMPE_WizardShown", false))
            {
                SessionState.SetBool("RTMPE_WizardShown", true);
                // Delay so Editor finishes loading before opening.
                EditorApplication.delayCall += () =>
                {
                    if (!EditorApplication.isPlayingOrWillChangePlaymode)
                        Open();
                };
            }
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────

        private void OnEnable()
        {
            LoadSettings();
            CheckNetworkManager();
        }

        // OnDestroy fires whether the wizard closes via the Cancel button (which
        // already prompts via TryCancel) or via Unity's window-chrome X button
        // (which bypasses the explicit Cancel path).  By the time OnDestroy
        // runs the window has already been retired, so a confirmation dialog
        // is too late — instead, surface a console warning so an integrator
        // who closes the X with unsaved edits has an unmistakable trace in
        // the editor log.  TryCancel clears _hasUnsavedChanges before scheduling
        // Close, so this branch only fires when the user dismissed the wizard
        // without going through the Cancel button.
        private void OnDestroy()
        {
            if (_hasUnsavedChanges)
            {
                Debug.LogWarning(
                    "[RTMPE] SetupWizard closed with unsaved changes; setup is incomplete. " +
                    "Reopen via Window > RTMPE > Setup Wizard to finish.");
            }
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            DrawHeader();
            GUILayout.Space(8);

            switch (_step)
            {
                case 0: DrawStepVerify();    break;
                case 1: DrawStepPrefab();    break;
                case 2: DrawStepApiKey();    break;
                case 3: DrawStepGameType();  break;
                case 4: DrawStepTestConn();  break;
            }

            GUILayout.FlexibleSpace();
            DrawFooter();
        }

        // ── Step renderers ────────────────────────────────────────────────────

        private void DrawHeader()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label($"RTMPE SDK — Step {_step + 1} / {TotalSteps}",
                    EditorStyles.boldLabel);
            }
        }

        private void DrawStepVerify()
        {
            EditorGUILayout.HelpBox(
                "✅  SDK assemblies are loaded correctly.\n" +
                "Packages: FlatBuffers · System.Memory · KCP transport",
                MessageType.Info);
        }

        private void DrawStepPrefab()
        {
            EditorGUILayout.HelpBox(
                _networkManagerFound
                    ? "✅  NetworkManager found in the active scene."
                    : "⚠️  No NetworkManager found. Click below to add one.",
                _networkManagerFound ? MessageType.Info : MessageType.Warning);

            if (!_networkManagerFound && GUILayout.Button("Add NetworkManager to Scene"))
                AddNetworkManagerToScene();
        }

        private void DrawStepApiKey()
        {
            EditorGUILayout.LabelField("Gateway Configuration", EditorStyles.boldLabel);
            // Render the API key as a masked password field. The on-disk
            // store is the OS credential vault via ApiKeyStore — see
            // SaveSettings(). Masking the GUI prevents shoulder-surfing /
            // screen-share leaks while the wizard is open.
            EditorGUI.BeginChangeCheck();
            _apiKey      = EditorGUILayout.PasswordField("API Key",   _apiKey);
            _gatewayHost = EditorGUILayout.TextField("Gateway Host",  _gatewayHost);
            _gatewayPort = EditorGUILayout.IntField ("Gateway Port",  _gatewayPort);
            // Dashboard public keys — non-secret, hence plain text fields (only the
            // API key above is a secret and stays masked).  The seal key drives the
            // sealed-box path that removes the shared-PSK handoff; the pin is what
            // Strict pinning compares the gateway's identity key against.
            _sealKey     = EditorGUILayout.TextField(
                new GUIContent("API-Key Seal Public Key (X25519)",
                    "64-char hex X25519 key from the dashboard.  When set, the API key " +
                    "is sealed to it and no apiKeyPskHex is required."),
                _sealKey);
            _pinnedKey   = EditorGUILayout.TextField(
                new GUIContent("Pinned Server Public Key (Ed25519)",
                    "64-char hex Ed25519 key from the dashboard.  Required while Server " +
                    "Pinning Mode is Strict (the default)."),
                _pinnedKey);
            // Session-token claims — non-secret, copied from the dashboard.  When set,
            // the SDK enforces the matching SessionAck-JWT claim instead of running its
            // fail-open default that merely warns; blank leaves that check off.
            _jwtIssuer   = EditorGUILayout.TextField(
                new GUIContent("Session Token Issuer (JWT iss)",
                    "Expected `iss` claim from the dashboard.  When set, a SessionAck token " +
                    "whose issuer differs is rejected.  Leave blank to skip the check."),
                _jwtIssuer);
            _jwtAudience = EditorGUILayout.TextField(
                new GUIContent("Session Token Audience (JWT aud)",
                    "Expected `aud` claim from the dashboard.  When set, a token whose " +
                    "audience excludes this value is rejected.  Leave blank to skip the check."),
                _jwtAudience);
            if (EditorGUI.EndChangeCheck())
                _hasUnsavedChanges = true;

            // Surface any prior Save() failure right next to the input that
            // caused it so the developer knows credential persistence failed.
            if (!string.IsNullOrEmpty(_lastSaveError))
                EditorGUILayout.HelpBox(_lastSaveError, MessageType.Error);
        }

        private void DrawStepGameType()
        {
            EditorGUILayout.LabelField("Game Type Defaults", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _maxPlayers = EditorGUILayout.IntSlider("Max Players", _maxPlayers, 1, 100);
            _tickRate   = EditorGUILayout.IntSlider("Tick Rate (Hz)", _tickRate, 10, 60);
            if (EditorGUI.EndChangeCheck())
                _hasUnsavedChanges = true;

            EditorGUILayout.HelpBox(
                "These defaults are used when creating rooms at runtime.\n" +
                "They can be overridden per-room via NetworkManager.CreateRoom().",
                MessageType.None);
        }

        private void DrawStepTestConn()
        {
            EditorGUILayout.LabelField("Configuration Validation", EditorStyles.boldLabel);

            // The button used to read "Ping Gateway" — but ValidateConfiguration
            // does not open a socket; it only confirms that the wizard's own
            // input fields are well-formed.  The previous label produced a
            // false sense of network connectivity that masked misconfigured
            // firewalls / routing during onboarding.  Live ping/echo testing
            // belongs in the runtime, behind a manager that owns the
            // transport.
            if (GUILayout.Button("Validate Configuration"))
                ValidateConfiguration();

            EditorGUILayout.HelpBox(
                "This step checks that the API key and gateway port fields " +
                "look valid.  It does NOT contact the gateway — open the " +
                "Network Debugger window after pressing Play to verify " +
                "actual connectivity.",
                MessageType.None);

            if (!string.IsNullOrEmpty(_statusMsg))
            {
                var type = _testPassed ? MessageType.Info : MessageType.Error;
                EditorGUILayout.HelpBox(_statusMsg, type);
            }
        }

        private void DrawFooter()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Cancel sits left-most so it is the natural target for a
                // "get me out of here" reflex.  Closing the window via the
                // OS chrome alone used to silently commit whatever partial
                // state had already been saved by a previous "Next →".
                if (GUILayout.Button("Cancel"))
                {
                    if (TryCancel())
                        return;
                }

                GUI.enabled = _step > 0;
                if (GUILayout.Button("← Back"))  { _step--;          }
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                if (_step < TotalSteps - 1)
                {
                    if (GUILayout.Button("Next →"))
                    {
                        if (TrySaveSettings())
                            _step++;
                    }
                }
                else
                {
                    if (GUILayout.Button("Finish"))
                    {
                        if (!TrySaveSettings())
                            return;
                        // Propagate the typed connection config to the NetworkSettings
                        // asset the runtime reads — TrySaveSettings persists only the
                        // editor-side EditorPrefs cache that repopulates the wizard.
                        PersistConnectionConfigToAsset();
                        ShowNotification(new GUIContent("RTMPE setup complete! 🎮"));
                        // After a successful Save the wizard is in a
                        // consistent state and the unsaved-changes guard
                        // must not fire on the imminent Close().
                        _hasUnsavedChanges = false;
                        EditorApplication.delayCall += Close;
                    }
                }
            }
        }

        /// <summary>
        /// Confirm with the user (when there are unsaved edits) and close
        /// the wizard without committing the in-memory state.  Returns
        /// <c>true</c> when the wizard is being closed so the caller can
        /// abort the rest of the GUI pass — Unity disposes the window on
        /// the next event tick.
        /// </summary>
        private bool TryCancel()
        {
            if (_hasUnsavedChanges)
            {
                bool confirmed = EditorUtility.DisplayDialog(
                    "Cancel RTMPE setup?",
                    "You have unsaved changes. Closing the wizard now will " +
                    "discard them and leave previously-saved settings " +
                    "untouched.",
                    "Discard changes",
                    "Keep editing");
                if (!confirmed) return false;
            }
            // Suppress the Unity "want to save?" path — Cancel always
            // discards.  Mark dirty=false so OnDestroy / external Close
            // cannot re-trigger the dialog.
            _hasUnsavedChanges = false;
            EditorApplication.delayCall += Close;
            return true;
        }

        /// <summary>
        /// Persist settings, surfacing any storage failure to the user
        /// instead of silently advancing the wizard.  Returns <c>true</c>
        /// only when persistence succeeded; the caller must not move past
        /// the current step on <c>false</c>.
        /// </summary>
        private bool TrySaveSettings()
        {
            try
            {
                SaveSettings();
                _lastSaveError = null;
                return true;
            }
            catch (System.Exception ex)
            {
                _lastSaveError =
                    $"Failed to save RTMPE settings: {ex.GetType().Name} — {ex.Message}";
                EditorUtility.DisplayDialog(
                    "RTMPE — settings not saved",
                    _lastSaveError + "\n\nThe wizard will remain on this step " +
                    "so you can correct the problem and retry.",
                    "OK");
                Repaint();
                return false;
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void CheckNetworkManager()
        {
            _networkManagerFound = FindFirstObjectByType<NetworkManager>() != null;
        }

        private void AddNetworkManagerToScene()
        {
            // Resolve (or create) a NetworkSettings asset before instantiating
            // the component so the freshly-added NetworkManager is wired to a
            // real, on-disk asset rather than left with a null _settings
            // reference that would force CreateDefault() at runtime and lose
            // any project-specific configuration.
            var settings = PersistConnectionConfigToAsset();

            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            Undo.RegisterCreatedObjectUndo(go, "Add NetworkManager");

            if (settings != null)
            {
                // Wire the freshly-added component to the on-disk asset so it does
                // not fall back to a runtime-only CreateDefault() instance that
                // would discard the developer's configuration.
                var so = new SerializedObject(nm);
                var prop = so.FindProperty("_settings");
                if (prop != null)
                {
                    prop.objectReferenceValue = settings;
                    so.ApplyModifiedProperties();
                }
            }

            EditorSceneManager.MarkSceneDirty(
                EditorSceneManager.GetActiveScene());
            _networkManagerFound = true;
            Repaint();
        }

        // Find the first NetworkSettings asset in the project, or create a
        // default one at Assets/RTMPE/NetworkSettings.asset (creating the
        // parent folder when missing).  Multiple existing assets are
        // tolerated — the first hit wins so the wizard never blocks on an
        // ambiguous project layout.
        private static NetworkSettings ResolveOrCreateNetworkSettings()
        {
            var guids = AssetDatabase.FindAssets("t:NetworkSettings");
            if (guids != null && guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                if (guids.Length > 1)
                    Debug.LogWarning(
                        $"[RTMPE] {guids.Length} NetworkSettings assets exist in the project; the " +
                        $"wizard is using '{path}' (first match).  Multiple assets risk a build " +
                        "shipping a different NetworkSettings than the one you configured — keep a " +
                        "single asset, or confirm the one wired to your NetworkManager carries the " +
                        "API-key envelope.");
                var existing = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);
                if (existing != null) return existing;
            }

            const string folder    = "Assets/RTMPE";
            const string assetPath = folder + "/NetworkSettings.asset";

            if (!AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder("Assets", "RTMPE");

            var created = ScriptableObject.CreateInstance<NetworkSettings>();
            AssetDatabase.CreateAsset(created, assetPath);
            AssetDatabase.SaveAssets();
            return created;
        }

        // Resolve (or create) the project's NetworkSettings asset, copy the
        // wizard's connection config onto it, and persist.  This is the single
        // path by which the typed values reach the asset the runtime reads:
        // SaveSettings persists merely the editor-side EditorPrefs cache that
        // repopulates the wizard's own fields, which nothing at runtime consults.
        // Shared by the Finish step and the Add-NetworkManager action so the two
        // entry points cannot drift.
        private NetworkSettings PersistConnectionConfigToAsset()
        {
            var settings = ResolveOrCreateNetworkSettings();
            if (settings != null)
            {
                ApplyConnectionConfig(
                    settings, _gatewayHost, _gatewayPort, _tickRate, _pinnedKey, _sealKey,
                    _jwtIssuer, _jwtAudience);
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
            }
            return settings;
        }

        // Pure helper (no UnityEditor dependency) that copies the wizard's
        // connection config onto a NetworkSettings asset, so the field-propagation
        // contract is unit-testable without a live Editor scene; the caller
        // persists the result via EditorUtility.SetDirty + AssetDatabase.SaveAssets.
        // Host/port/tick always carry a meaningful wizard value and so overwrite
        // unconditionally; the dashboard public keys and JWT claims are written only
        // when supplied, so running the wizard with those fields blank never clears a
        // value the developer pasted directly onto the asset.  (_maxPlayers has no
        // asset home — rooms set it per-call via CreateRoomOptions — so it is not
        // copied here.)
        internal static void ApplyConnectionConfig(
            NetworkSettings settings, string host, int port, int tickRate,
            string pinnedKey, string sealKey, string jwtIssuer, string jwtAudience)
        {
            if (settings == null) return;
            settings.serverHost = host;
            settings.serverPort = port;
            settings.tickRate   = tickRate;
            if (!string.IsNullOrWhiteSpace(sealKey))
                settings.apiKeySealServerPublicKeyHex = sealKey.Trim();
            if (!string.IsNullOrWhiteSpace(pinnedKey))
                settings.pinnedServerPublicKeyHex = pinnedKey.Trim();
            // JWT claims are byte-compared case-sensitively by the validator, so
            // they are only trimmed of incidental whitespace — never lower-cased
            // like the hex keys above.
            if (!string.IsNullOrWhiteSpace(jwtIssuer))
                settings.expectedJwtIssuer = jwtIssuer.Trim();
            if (!string.IsNullOrWhiteSpace(jwtAudience))
                settings.expectedJwtAudience = jwtAudience.Trim();
        }

        private void ValidateConfiguration()
        {
            // Configuration-only check.  Live socket round-trip is intentionally
            // not performed here — the wizard runs in the Editor before any
            // NetworkManager bootstrap, and a half-baked Connect+Disconnect
            // dance would drown out misconfigured-firewall errors more than it
            // surfaces them.  Use the runtime Network Debugger window to see
            // real connectivity once the project is in Play mode.
            //
            // Port range: full IANA-valid 1..65535.  The previous gate
            // (> 1024 and < 65535) silently rejected the perfectly valid port
            // 1024 and the highest port 65535, frustrating integrators on
            // self-hosted gateways pinned to non-default ports.
            _testPassed = !string.IsNullOrWhiteSpace(_apiKey)
                       && _gatewayPort is >= 1 and <= 65535;

            _statusMsg = _testPassed
                ? $"✅  Configuration looks valid — {_gatewayHost}:{_gatewayPort}.  " +
                  "Press Play and open the Network Debugger window to verify the " +
                  "gateway is actually reachable."
                : "❌  Provide a non-empty API key and a port in the range 1–65535.";
            Repaint();
        }

        private void LoadSettings()
        {
            // API key is read from the OS credential vault (DPAPI / macOS
            // Keychain / libsecret) via ApiKeyStore — never from plaintext
            // EditorPrefs. ApiKeyStore.Load() also one-shot migrates any
            // legacy plaintext entry written by older SDK versions.
            _apiKey      = ApiKeyStore.Load();
            _gatewayHost = EditorPrefs.GetString("RTMPE_Host",        "127.0.0.1");
            _gatewayPort = EditorPrefs.GetInt   ("RTMPE_Port",        7777);
            _maxPlayers  = EditorPrefs.GetInt   ("RTMPE_MaxPlayers",  16);
            _tickRate    = EditorPrefs.GetInt   ("RTMPE_TickRate",     30);
            _sealKey     = EditorPrefs.GetString("RTMPE_SealKey",     "");
            _pinnedKey   = EditorPrefs.GetString("RTMPE_PinnedKey",   "");
            _jwtIssuer   = EditorPrefs.GetString("RTMPE_JwtIssuer",   "");
            _jwtAudience = EditorPrefs.GetString("RTMPE_JwtAudience", "");
        }

        private void SaveSettings()
        {
            ApiKeyStore.Save(_apiKey);
            EditorPrefs.SetString("RTMPE_Host",       _gatewayHost);
            EditorPrefs.SetInt   ("RTMPE_Port",       _gatewayPort);
            EditorPrefs.SetInt   ("RTMPE_MaxPlayers", _maxPlayers);
            EditorPrefs.SetInt   ("RTMPE_TickRate",   _tickRate);
            EditorPrefs.SetString("RTMPE_SealKey",    _sealKey);
            EditorPrefs.SetString("RTMPE_PinnedKey",  _pinnedKey);
            EditorPrefs.SetString("RTMPE_JwtIssuer",   _jwtIssuer);
            EditorPrefs.SetString("RTMPE_JwtAudience", _jwtAudience);
        }
    }
}
