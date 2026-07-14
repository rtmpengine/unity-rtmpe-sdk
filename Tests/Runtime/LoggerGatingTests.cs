// RTMPE SDK — Tests/Runtime/LoggerGatingTests.cs
//
// Verify that the gated logger (RtmpeLog) honours NetworkSettings.enableDebugLogs:
//  * When the flag is FALSE, "routine" runtime errors and warnings are
//    suppressed (downgraded to Debug.Log severity) so external crash
//    reporters do not ingest them.
//  * When the flag is TRUE, the same calls surface at LogError / LogWarning
//    so SDK developers can see them in the Unity console.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Logging")]
    public class LoggerGatingTests
    {
        private NetworkSettings _settings;

        [SetUp]
        public void SetUp()
        {
            // Build a fresh ScriptableObject — CreateInstance is the only
            // legal allocation path for a NetworkSettings asset and matches
            // how the runtime obtains the default profile.
            _settings = ScriptableObject.CreateInstance<NetworkSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            RtmpeLog.SetTestOverride(null);
            if (_settings != null)
            {
                Object.DestroyImmediate(_settings);
                _settings = null;
            }
        }

        [Test]
        [Description("Log.Error is suppressed (no LogError emitted) when enableDebugLogs is false.")]
        public void Error_FlagDisabled_DoesNotEmitLogError()
        {
            _settings.enableDebugLogs = false;
            RtmpeLog.SetTestOverride(_settings);

            // Drive the call.  The test fails if any *unexpected* LogError
            // surfaces — Unity Test Runner asserts the LogAssert state on
            // teardown.  We do NOT call LogAssert.Expect(LogError) because
            // exactly the opposite is being asserted.
            RtmpeLog.Error("[RTMPE] suppressed error fixture");

            // No LogAssert.Expect line means: zero LogError messages allowed.
        }

        [Test]
        [Description("Log.Error surfaces as Debug.LogError when enableDebugLogs is true.")]
        public void Error_FlagEnabled_EmitsLogError()
        {
            _settings.enableDebugLogs = true;
            RtmpeLog.SetTestOverride(_settings);

            LogAssert.Expect(LogType.Error,
                new System.Text.RegularExpressions.Regex("surfaced error fixture"));

            RtmpeLog.Error("[RTMPE] surfaced error fixture");
        }

        [Test]
        [Description("Log.Warning is suppressed (no LogWarning emitted) when enableDebugLogs is false.")]
        public void Warning_FlagDisabled_DoesNotEmitLogWarning()
        {
            _settings.enableDebugLogs = false;
            RtmpeLog.SetTestOverride(_settings);

            RtmpeLog.Warning("[RTMPE] suppressed warning fixture");
        }

        [Test]
        [Description("Log.Warning surfaces as Debug.LogWarning when enableDebugLogs is true.")]
        public void Warning_FlagEnabled_EmitsLogWarning()
        {
            _settings.enableDebugLogs = true;
            RtmpeLog.SetTestOverride(_settings);

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("surfaced warning fixture"));

            RtmpeLog.Warning("[RTMPE] surfaced warning fixture");
        }

        [Test]
        [Description("Log.Info is silent when enableDebugLogs is false.")]
        public void Info_FlagDisabled_IsSilent()
        {
            _settings.enableDebugLogs = false;
            RtmpeLog.SetTestOverride(_settings);

            RtmpeLog.Info("[RTMPE] suppressed info fixture");
        }

        [Test]
        [Description("Null active settings behaves as if the flag were false (defensive).")]
        public void NullSettings_BehavesAsDisabled()
        {
            RtmpeLog.SetTestOverride(null);
            RtmpeLog.SetActiveSettings(null);

            // No LogError / LogWarning should escape — the gate must read
            // the null reference defensively rather than NRE'ing.
            RtmpeLog.Error("[RTMPE] suppressed under null settings");
            RtmpeLog.Warning("[RTMPE] suppressed under null settings");
        }
    }
}
