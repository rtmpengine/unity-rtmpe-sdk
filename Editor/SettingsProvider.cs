// RTMPE SDK — Editor/SettingsProvider.cs
//
// Adds a "Project / RTMPE" entry to Unity's project-wide Settings window
// (Edit > Project Settings > RTMPE).
//
// The provider locates a project-wide NetworkSettings asset (or creates one
// on demand) and renders a SerializedObject-driven Inspector that exposes
// every field on NetworkSettings — gateway host/port, tick rate, heartbeat,
// reconnect, buffers, debug logs, and crypto pinning — bound through the
// standard SerializedProperty pipeline so undo, multi-edit, and Inspector
// search all work without bespoke wiring.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Project-Settings pane for RTMPE SDK configuration.
    /// Unity discovers this provider automatically via the
    /// <see cref="SettingsProviderAttribute"/>.
    /// </summary>
    internal static class RtmpeSettingsProvider
    {
        // ── Constants ─────────────────────────────────────────────────────────

        private const string SettingsPath          = "Project/RTMPE";
        private const string DefaultAssetDirectory = "Assets/Settings";
        private const string DefaultAssetName      = "RTMPESettings.asset";

        // ── Provider entry-point ──────────────────────────────────────────────

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label    = "RTMPE",
                guiHandler = OnGUI,
                keywords = SearchKeywords(),
            };
        }

        private static HashSet<string> SearchKeywords() => new HashSet<string>
        {
            "RTMPE", "Network", "Multiplayer", "UDP", "KCP",
            "Server", "Host", "Port", "Tick", "Heartbeat",
            "Reconnect", "Buffer", "Debug", "PSK", "Pinning",
            "ApiKey", "Ed25519",
        };

        // ── State ─────────────────────────────────────────────────────────────
        //
        // Cached SerializedObject so undo and dirty-tracking flow through the
        // standard Editor pipeline.  We rebuild it whenever the bound asset
        // changes (deleted, recreated, or re-imported externally).

        private static NetworkSettings  _bound;
        private static SerializedObject _serialized;
        private static Vector2          _scroll;

        // ── GUI ───────────────────────────────────────────────────────────────

        private static void OnGUI(string searchContext)
        {
            EnsureBound();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("RTMPE SDK Settings", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Edit project-wide RTMPE connection, timing, buffer, and crypto defaults.",
                EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space(6);

            DrawAssetRow();

            if (_bound == null)
            {
                EditorGUILayout.HelpBox(
                    "No NetworkSettings asset is bound. Click 'Create New' to author " +
                    $"'{DefaultAssetDirectory}/{DefaultAssetName}', or drag an existing " +
                    "asset into the field above.",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.Space(8);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            _serialized.Update();
            DrawAllProperties(_serialized);
            _serialized.ApplyModifiedProperties();

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(6);
            DrawFooterActions();
        }

        // ── Asset binding ─────────────────────────────────────────────────────

        private static void EnsureBound()
        {
            // Validate the cached object — Unity invalidates SerializedObject
            // when the underlying asset is deleted or re-imported.
            if (_bound != null && _serialized != null && _serialized.targetObject != null)
                return;

            _bound      = LocateExistingAsset();
            _serialized = _bound != null ? new SerializedObject(_bound) : null;
        }

        private static NetworkSettings LocateExistingAsset()
        {
            // AssetDatabase.FindAssets respects the active project; the t:
            // filter restricts results to NetworkSettings instances.
            var guids = AssetDatabase.FindAssets($"t:{nameof(NetworkSettings)}");
            if (guids == null || guids.Length == 0) return null;

            // Prefer an asset that lives in DefaultAssetDirectory; fall back
            // to the first result so projects with custom layouts still bind.
            NetworkSettings firstHit = null;
            foreach (var guid in guids)
            {
                var path  = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<NetworkSettings>(path);
                if (asset == null) continue;
                if (firstHit == null) firstHit = asset;
                if (path.StartsWith(DefaultAssetDirectory)) return asset;
            }
            return firstHit;
        }

        private static NetworkSettings CreateAsset()
        {
            if (!Directory.Exists(DefaultAssetDirectory))
                Directory.CreateDirectory(DefaultAssetDirectory);

            var asset = ScriptableObject.CreateInstance<NetworkSettings>();
            var path  = AssetDatabase.GenerateUniqueAssetPath(
                $"{DefaultAssetDirectory}/{DefaultAssetName}");

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            EditorGUIUtility.PingObject(asset);
            return asset;
        }

        // ── Drawers ───────────────────────────────────────────────────────────

        private static void DrawAssetRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = (NetworkSettings)EditorGUILayout.ObjectField(
                    new GUIContent("Active Asset",
                        "NetworkSettings asset edited by this panel. " +
                        "Assign different assets to swap dev/staging/prod profiles."),
                    _bound,
                    typeof(NetworkSettings),
                    allowSceneObjects: false);
                if (EditorGUI.EndChangeCheck())
                {
                    _bound      = picked;
                    _serialized = picked != null ? new SerializedObject(picked) : null;
                }

                if (GUILayout.Button("Create New", GUILayout.Width(96)))
                {
                    var created = CreateAsset();
                    _bound      = created;
                    _serialized = new SerializedObject(created);
                }

                using (new EditorGUI.DisabledScope(_bound == null))
                {
                    if (GUILayout.Button("Ping", GUILayout.Width(48)))
                        EditorGUIUtility.PingObject(_bound);
                }
            }
        }

        private static void DrawAllProperties(SerializedObject so)
        {
            // Iterate the SerializedObject in display order — every public
            // field on NetworkSettings (with its [Header] and [Tooltip]
            // attributes) is rendered automatically without per-field code.
            // Skipping m_Script keeps the panel clean of the script reference
            // that ScriptableObject.SerializedObject exposes by default.
            var iterator = so.GetIterator();
            iterator.NextVisible(enterChildren: true);
            do
            {
                if (iterator.propertyPath == "m_Script") continue;
                EditorGUILayout.PropertyField(iterator, includeChildren: true);
            }
            while (iterator.NextVisible(enterChildren: false));
        }

        private static void DrawFooterActions()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Reset to Defaults", GUILayout.Width(160)))
                {
                    if (EditorUtility.DisplayDialog(
                            "Reset RTMPE Settings",
                            "Reset all fields on this asset to their factory defaults? " +
                            "This action can be undone with Edit > Undo.",
                            "Reset",
                            "Cancel"))
                    {
                        Undo.RecordObject(_bound, "Reset RTMPE Settings");
                        var fresh = ScriptableObject.CreateInstance<NetworkSettings>();
                        EditorUtility.CopySerialized(fresh, _bound);
                        Object.DestroyImmediate(fresh);
                        EditorUtility.SetDirty(_bound);
                        _serialized = new SerializedObject(_bound);
                    }
                }

                GUILayout.FlexibleSpace();

                EditorGUILayout.LabelField(
                    AssetDatabase.GetAssetPath(_bound),
                    EditorStyles.miniLabel);
            }
        }
    }
}
#endif
