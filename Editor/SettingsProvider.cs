// RTMPE SDK — Editor/SettingsProvider.cs
//
// Adds a "Project / RTMPE" entry to Unity's project-wide Settings window
// (Edit > Project Settings > RTMPE).
//
// Day 1-2: bare-bones provider with placeholder UI.
// Day 4-5: bind live configuration fields (server host, port, log level).

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine.UIElements;

namespace RTMPE.Editor
{
    /// <summary>
    /// Project-Settings pane for RTMPE SDK configuration.
    /// Unity discovers this provider automatically via the <see cref="SettingsProviderAttribute"/>.
    /// </summary>
    internal static class RtmpeSettingsProvider
    {
        private const string SettingsPath = "Project/RTMPE";

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "RTMPE",
                activateHandler = Activate,
                keywords = new System.Collections.Generic.HashSet<string>
                {
                    "RTMPE", "Network", "Multiplayer", "UDP", "KCP"
                }
            };
        }

        private static void Activate(string searchContext, VisualElement root)
        {
            var container = new VisualElement();
            container.style.paddingLeft  = 10;
            container.style.paddingTop   = 10;
            container.style.paddingRight = 10;

            container.Add(new Label("RTMPE SDK Settings")
            {
                style = { fontSize = 14, unityFontStyleAndWeight = UnityEngine.FontStyle.Bold }
            });

            container.Add(new Label("Full configuration UI will be available in Day 4-5.")
            {
                style = { marginTop = 8, color = new UnityEngine.Color(0.6f, 0.6f, 0.6f) }
            });

            root.Add(container);
        }
    }
}
#endif
