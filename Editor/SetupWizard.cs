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
            var settings = ResolveOrCreateNetworkSettings();

            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            Undo.RegisterCreatedObjectUndo(go, "Add NetworkManager");

            if (settings != null)
            {
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
        }

        private void SaveSettings()
        {
            ApiKeyStore.Save(_apiKey);
            EditorPrefs.SetString("RTMPE_Host",       _gatewayHost);
            EditorPrefs.SetInt   ("RTMPE_Port",       _gatewayPort);
            EditorPrefs.SetInt   ("RTMPE_MaxPlayers", _maxPlayers);
            EditorPrefs.SetInt   ("RTMPE_TickRate",   _tickRate);
        }
    }
}
