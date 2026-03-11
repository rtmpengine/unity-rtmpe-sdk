// RTMPE SDK — Editor/NetworkManagerEditor.cs
//
// Custom Inspector for NetworkManager.
// Displays the Settings asset reference and, during Play Mode, live runtime state
// (connection state, session flags) as read-only diagnostic fields.

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Editor
{
    [CustomEditor(typeof(NetworkManager))]
    internal sealed class NetworkManagerEditor : UnityEditor.Editor
    {
        private SerializedProperty _settingsProp;

        private void OnEnable()
        {
            _settingsProp = serializedObject.FindProperty("_settings");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // ── Settings asset ─────────────────────────────────────────────────
            EditorGUILayout.PropertyField(
                _settingsProp,
                new GUIContent(
                    "Settings",
                    "RTMPE configuration asset. Leave blank to use built-in defaults."));

            serializedObject.ApplyModifiedProperties();

            // ── Runtime diagnostics (Play Mode only) ───────────────────────────
            if (!Application.isPlaying || !NetworkManager.HasInstance) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Runtime State", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.EnumPopup(
                    new GUIContent("State",         "Current connection lifecycle state."),
                    nm.State);

                EditorGUILayout.Toggle(
                    new GUIContent("Is Connected",  "True while state is Connected or InRoom."),
                    nm.IsConnected);

                EditorGUILayout.Toggle(
                    new GUIContent("Is In Room",    "True while connected and inside an active room."),
                    nm.IsInRoom);

                EditorGUILayout.LabelField(
                    new GUIContent("Local Player ID", "Assigned after SessionAck (Week 9+)."),
                    nm.LocalPlayerId == 0 ? "—" : nm.LocalPlayerId.ToString());

                EditorGUILayout.LabelField(
                    new GUIContent("Room ID", "Assigned after RoomJoin (Week 9+)."),
                    nm.CurrentRoomId == 0 ? "—" : nm.CurrentRoomId.ToString());
            }

            // Repaint every frame so the diagnostics stay current in Play Mode.
            if (Application.isPlaying)
                Repaint();
        }
    }
}
#endif

