// RTMPE SDK — Runtime/Core/RtmpeLog.cs
//
// Internal logging facade that respects NetworkSettings.enableDebugLogs.
//
// Motivation:
//  Direct calls to UnityEngine.Debug.LogError are picked up by every crash
//  reporter the host app has installed (Crashlytics, Bugsnag, Unity Cloud
//  Diagnostics, Sentry, etc.) regardless of severity.  Routine transport
//  blips — a peer rebooting, a roaming Wi-Fi handoff, a backgrounded mobile
//  client — must not look like crashes in those dashboards.
//
// Policy:
//  * "Routine" runtime errors (transport exceptions, malformed inbound
//    packets, dropped frames) route through Error()/Warning() which are
//    SUPPRESSED unless enableDebugLogs is true on the active NetworkSettings.
//    When suppressed they are emitted at Debug.Log severity so they remain
//    visible in editor consoles for SDK developers but are not ingested
//    by crash pipelines.
//  * "Fatal" conditions (cryptography failures, configuration errors that
//    prevent any session from starting, nonce exhaustion) continue to call
//    UnityEngine.Debug.LogError directly — those genuinely warrant a crash
//    report because the SDK cannot recover.
//
// The gate read is a single volatile bool field deref through a cached
// NetworkSettings reference.  No allocations, no locks.

using UnityEngine;

namespace RTMPE.Core
{
    internal static class RtmpeLog
    {
        // Cached settings handle.  Set by NetworkManager.Awake and cleared by
        // OnDestroy / ResetStaticState.  A test seam is provided so unit tests
        // can flip the flag without instantiating a full NetworkManager.
        private static volatile NetworkSettings _settings;

        // Test override: when non-null, beats the live _settings reference.
        // Lets edit-mode tests verify gating without spinning a manager.
        private static volatile NetworkSettings _testOverride;

        internal static void SetActiveSettings(NetworkSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Test-only: force the gate to a specific NetworkSettings (or null
        /// to revert).  Tests should pair this with a TearDown that restores
        /// the previous state.
        /// </summary>
        internal static void SetTestOverride(NetworkSettings settings)
        {
            _testOverride = settings;
        }

        private static bool DebugLogsEnabled
        {
            get
            {
                var ov = _testOverride;
                if (ov != null) return ov.enableDebugLogs;
                var s = _settings;
                return s != null && s.enableDebugLogs;
            }
        }

        // Gating rule: hot- and warm-path callers should test IsDebugEnabled
        // before building the interpolated string they pass to LogDebug.  The
        // interpolation step allocates a fresh formatted System.String even
        // when the downstream sink later suppresses the message, so any site
        // that runs more than a handful of times per second per session must
        // gate the format work to keep steady-state GC flat.

        /// <summary>
        /// Hot- and warm-path gate.  Callers that build a debug message via
        /// string interpolation should test this property first so the format
        /// step is elided entirely when verbose logging is off — string
        /// interpolation otherwise allocates a fresh formatted
        /// <see cref="System.String"/> per call regardless of whether the
        /// downstream sink consumes it.  At ~600 packets/s the elided
        /// allocations dominate inbound steady-state GC pressure.
        /// </summary>
        public static bool IsDebugEnabled => DebugLogsEnabled;

        /// <summary>
        /// Log a routine runtime error.  Surfaces as Debug.LogError only when
        /// the user has explicitly opted into verbose logs; otherwise emitted
        /// at Debug.Log severity so crash reporters do not ingest it.
        /// </summary>
        public static void Error(string message)
        {
            if (DebugLogsEnabled) Debug.LogError(message);
            else                  Debug.Log(message);
        }

        /// <summary>
        /// Log a routine warning.  Suppressed (downgraded to Debug.Log) when
        /// the verbose flag is off.  Use for peer-induced or transient
        /// conditions that do not represent SDK bugs.
        /// </summary>
        public static void Warning(string message)
        {
            if (DebugLogsEnabled) Debug.LogWarning(message);
            else                  Debug.Log(message);
        }

        /// <summary>
        /// Informational log.  Emitted only when verbose logs are enabled.
        /// </summary>
        public static void Info(string message)
        {
            if (DebugLogsEnabled) Debug.Log(message);
        }
    }
}
