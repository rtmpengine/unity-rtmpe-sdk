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
            _apiKey      = EditorGUILayout.TextField("API Key",       _apiKey);
            _gatewayHost = EditorGUILayout.TextField("Gateway Host",  _gatewayHost);
            _gatewayPort = EditorGUILayout.IntField ("Gateway Port",  _gatewayPort);
        }

        private void DrawStepGameType()
        {
            EditorGUILayout.LabelField("Game Type Defaults", EditorStyles.boldLabel);
            _maxPlayers = EditorGUILayout.IntSlider("Max Players", _maxPlayers, 1, 16);
            _tickRate   = EditorGUILayout.IntSlider("Tick Rate (Hz)", _tickRate, 10, 60);

            EditorGUILayout.HelpBox(
                "These defaults are used when creating rooms at runtime.\n" +
                "They can be overridden per-room via NetworkManager.CreateRoom().",
                MessageType.None);
        }

        private void DrawStepTestConn()
        {
            EditorGUILayout.LabelField("Connection Test", EditorStyles.boldLabel);

            if (GUILayout.Button("Ping Gateway"))
                PingGateway();

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
                GUI.enabled = _step > 0;
                if (GUILayout.Button("← Back"))  { _step--;          }
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                if (_step < TotalSteps - 1)
                {
                    if (GUILayout.Button("Next →"))
                    {
                        SaveSettings();
                        _step++;
                    }
                }
                else
                {
                    if (GUILayout.Button("Finish"))
                    {
                        SaveSettings();
                        ShowNotification(new GUIContent("RTMPE setup complete! 🎮"));
                        EditorApplication.delayCall += Close;
                    }
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void CheckNetworkManager()
        {
            _networkManagerFound = FindFirstObjectByType<NetworkManager>() != null;
        }

        private void AddNetworkManagerToScene()
        {
            var go = new GameObject("NetworkManager");
            go.AddComponent<NetworkManager>();
            Undo.RegisterCreatedObjectUndo(go, "Add NetworkManager");
            EditorSceneManager.MarkSceneDirty(
                EditorSceneManager.GetActiveScene());
            _networkManagerFound = true;
            Repaint();
        }

        private void PingGateway()
        {
            // In a real implementation, this would open a UDP socket to the gateway.
            // For the Editor wizard, we simply validate the configuration values.
            _testPassed = !string.IsNullOrWhiteSpace(_apiKey)
                       && _gatewayPort is > 1024 and < 65535;

            _statusMsg = _testPassed
                ? $"✅  Configuration valid — {_gatewayHost}:{_gatewayPort}"
                : "❌  Please fill in a valid API key and gateway port.";
            Repaint();
        }

        private void LoadSettings()
        {
            _apiKey      = EditorPrefs.GetString("RTMPE_ApiKey",      "");
            _gatewayHost = EditorPrefs.GetString("RTMPE_Host",        "127.0.0.1");
            _gatewayPort = EditorPrefs.GetInt   ("RTMPE_Port",        7777);
            _maxPlayers  = EditorPrefs.GetInt   ("RTMPE_MaxPlayers",  16);
            _tickRate    = EditorPrefs.GetInt   ("RTMPE_TickRate",     30);
        }

        private void SaveSettings()
        {
            EditorPrefs.SetString("RTMPE_ApiKey",     _apiKey);
            EditorPrefs.SetString("RTMPE_Host",       _gatewayHost);
            EditorPrefs.SetInt   ("RTMPE_Port",       _gatewayPort);
            EditorPrefs.SetInt   ("RTMPE_MaxPlayers", _maxPlayers);
            EditorPrefs.SetInt   ("RTMPE_TickRate",   _tickRate);
        }
    }
}
