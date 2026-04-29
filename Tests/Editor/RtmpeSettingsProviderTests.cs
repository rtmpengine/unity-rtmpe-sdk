// RTMPE SDK — Tests/Editor/RtmpeSettingsProviderTests.cs
//
// Edit-mode smoke tests for the Project / RTMPE settings panel.
//
// We can't drive the SettingsProvider GUI loop directly from a headless test
// runner, but we CAN exercise:
//   1. The provider is discovered by Unity's SettingsService (i.e. the
//      [SettingsProvider] attribute and path resolve correctly).
//   2. Constructing a SerializedObject around NetworkSettings exposes every
//      public field.  This is the same code path the panel uses to render
//      the Inspector — if it throws or omits a field, the panel breaks.

#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Tests.Editor
{
    [TestFixture]
    [Category("Editor")]
    public class RtmpeSettingsProviderTests
    {
        [Test]
        [Description("SettingsService exposes the 'Project/RTMPE' provider.")]
        public void Provider_IsDiscoverable_AtProjectRtmpePath()
        {
            var providers = SettingsService.FetchSettingsProviders();
            Assert.That(providers, Is.Not.Null.And.Not.Empty,
                "Unity should always return at least one settings provider.");

            var rtmpe = providers.FirstOrDefault(p => p.settingsPath == "Project/RTMPE");
            Assert.That(rtmpe, Is.Not.Null,
                "RtmpeSettingsProvider must register at 'Project/RTMPE'.");
            Assert.That(rtmpe.label, Is.EqualTo("RTMPE"));
        }

        [Test]
        [Description("All public NetworkSettings fields are reachable through SerializedObject.")]
        public void NetworkSettings_AllFields_AreVisibleViaSerializedObject()
        {
            var asset = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                var so = new SerializedObject(asset);

                // Collect every visible property the panel iterator walks over,
                // excluding the implicit m_Script reference that
                // ScriptableObject.SerializedObject always emits first.
                var visible = new HashSet<string>();
                var it = so.GetIterator();
                if (it.NextVisible(enterChildren: true))
                {
                    do
                    {
                        if (it.propertyPath == "m_Script") continue;
                        visible.Add(it.propertyPath);
                    } while (it.NextVisible(enterChildren: false));
                }

                // Expect every authoritative NetworkSettings field to show up.
                // Update this list whenever a new field is added.
                var required = new[]
                {
                    "serverHost",
                    "serverPort",
                    "heartbeatIntervalMs",
                    "connectionTimeoutMs",
                    "tickRate",
                    "autoRejoinLastRoomOnReconnect",
                    "sendBufferBytes",
                    "receiveBufferBytes",
                    "networkThreadBufferBytes",
                    "enableDebugLogs",
                    "apiKeyPskHex",
                    "pinnedServerPublicKeyHex",
                    "requirePinnedServerPublicKey",
                };

                foreach (var field in required)
                    Assert.That(visible, Contains.Item(field),
                        $"NetworkSettings field '{field}' must be visible " +
                        "to the Project/RTMPE settings panel.");
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        [Description("Default-constructed NetworkSettings has sensible runtime defaults.")]
        public void NetworkSettings_Defaults_AreSafeForLocalDevelopment()
        {
            var asset = ScriptableObject.CreateInstance<NetworkSettings>();
            try
            {
                Assert.That(asset.serverHost, Is.EqualTo("127.0.0.1"));
                Assert.That(asset.serverPort, Is.InRange(1, 65535));
                Assert.That(asset.tickRate,   Is.GreaterThan(0));
                Assert.That(asset.TickInterval,
                    Is.EqualTo(1f / asset.tickRate).Within(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
#endif
