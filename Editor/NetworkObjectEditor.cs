// RTMPE SDK — Editor/NetworkObjectEditor.cs
//
// Custom Inspector for NetworkBehaviour components.
// Shows network identity, ownership, authority status, and provides
// quick-action buttons for the Editor play-mode workflow.

using UnityEngine;
using UnityEditor;
using RTMPE.Core;

namespace RTMPE.Editor
{
    /// <summary>
    /// Enhanced inspector for all <see cref="NetworkBehaviour"/> subclasses.
    /// Displayed whenever a GameObject with a NetworkBehaviour component is selected.
    /// </summary>
    [CustomEditor(typeof(NetworkBehaviour), true)]   // true = apply to subclasses
    [CanEditMultipleObjects]
    public sealed class NetworkObjectEditor : UnityEditor.Editor
    {
        // ── Serialized properties ─────────────────────────────────────────────
        // Field names must match the private backing fields in NetworkBehaviour.cs
        private SerializedProperty _networkObjectIdProp;
        private SerializedProperty _ownerPlayerIdProp;
        private SerializedProperty _isSpawnedProp;

        // ── Style cache ───────────────────────────────────────────────────────
        private GUIStyle _headerStyle;
        private GUIStyle _badgeStyle;

        // ── Foldout state ─────────────────────────────────────────────────────
        private bool _showAdvanced;

        private void OnEnable()
        {
            _networkObjectIdProp = serializedObject.FindProperty("_networkObjectId");
            _ownerPlayerIdProp   = serializedObject.FindProperty("_ownerPlayerId");
            _isSpawnedProp       = serializedObject.FindProperty("_isSpawned");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            EnsureStyles();

            var nb = (NetworkBehaviour)target;

            // ── Header ────────────────────────────────────────────────────────
            EditorGUILayout.LabelField("Network Identity", _headerStyle);
            EditorGUI.indentLevel++;

            using (new EditorGUI.DisabledScope(true))
            {
                if (_networkObjectIdProp != null)
                    EditorGUILayout.PropertyField(_networkObjectIdProp,
                        new GUIContent("Network Object ID", "Unique ID assigned by the server at spawn."));
                if (_ownerPlayerIdProp != null)
                    EditorGUILayout.PropertyField(_ownerPlayerIdProp,
                        new GUIContent("Owner Player ID", "Session ID of the owning player."));
                if (_isSpawnedProp != null)
                    EditorGUILayout.PropertyField(_isSpawnedProp,
                        new GUIContent("Is Spawned", "True after NetworkSpawn() has fired on this object."));
            }

            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Authority / ownership badge ───────────────────────────────────
            DrawOwnershipBadge(nb);
            EditorGUILayout.Space(4);

            // ── Advanced: draw component's own serialized fields ──────────────
            _showAdvanced = EditorGUILayout.Foldout(_showAdvanced, "Component Fields", true);
            if (_showAdvanced)
            {
                EditorGUI.indentLevel++;
                DrawDefaultInspector();
                EditorGUI.indentLevel--;
            }

            // ── Play-mode quick actions ───────────────────────────────────────
            if (Application.isPlaying)
                DrawPlayModeActions(nb);

            serializedObject.ApplyModifiedProperties();
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void DrawOwnershipBadge(NetworkBehaviour nb)
        {
            bool isOwner = Application.isPlaying && nb.IsOwner;
            var color    = isOwner ? new Color(0.2f, 0.8f, 0.2f) : new Color(0.8f, 0.5f, 0.2f);
            var label    = isOwner ? "✅  Local Owner" : "🔒  Remote / Not Spawned";

            var prevColor = GUI.backgroundColor;
            GUI.backgroundColor = color;
            EditorGUILayout.LabelField(label, _badgeStyle);
            GUI.backgroundColor = prevColor;
        }

        private void DrawPlayModeActions(NetworkBehaviour nb)
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Play-Mode Actions", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Log State"))
                    Debug.Log(
                        $"[RTMPE] {nb.GetType().Name} — ObjectId: {nb.NetworkObjectId} " +
                        $"| Owner: {nb.OwnerPlayerId} | IsOwner: {nb.IsOwner} | IsSpawned: {nb.IsSpawned}",
                        nb);

                if (GUILayout.Button("Request Authority"))
                    Debug.LogWarning(
                        "[RTMPE] Authority is server-authoritative. " +
                        "Use NetworkManager.RequestAuthority() from gameplay code.", nb);
            }
        }

        private void EnsureStyles()
        {
            if (_headerStyle != null) return;

            _headerStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                margin   = new RectOffset(0, 0, 4, 4),
            };

            _badgeStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };
        }
    }
}
