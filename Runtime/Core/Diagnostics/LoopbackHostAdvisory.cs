// RTMPE SDK — Runtime/Core/Diagnostics/LoopbackHostAdvisory.cs
//
// Surfaces a single actionable warning when a Standalone build attempts to
// connect to a loopback address (127.0.0.0/8, localhost, or ::1).  In the Unity
// Editor a loopback host is a valid and common configuration — a gateway running
// on the same developer machine.  In a Standalone build the binary runs as an
// independent OS process: packets addressed to the loopback range are routed to
// that build machine's own loopback interface, not to the developer's machine, so
// they never reach the gateway.
//
// Same warn-once + caller-evaluated-boolean pattern as ReliableSendAdvisory and
// MacOsIncomingFirewallAdvisory — no UnityEngine.Application dependency inside
// this class so it is fully exercisable from the headless dotnet xunit runner.

using System;
using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Process-scoped warn-once advisory that fires when a Standalone build
    /// connects with a loopback address (<c>127.0.0.0/8</c>, <c>localhost</c>,
    /// or <c>::1</c>).  A loopback address only reaches a gateway on the same
    /// machine; a distributed Standalone binary will route packets to its own
    /// loopback interface and never reach the developer's gateway.
    /// </summary>
    internal static class LoopbackHostAdvisory
    {
        internal const string MessageText =
            "[RTMPE] Standalone build is connecting to a loopback address " +
            "(127.0.0.0/8 / localhost / ::1) — packets addressed to a loopback " +
            "host are routed to the local machine's own loopback interface, not " +
            "to the machine hosting the gateway.  A Standalone binary running on " +
            "any other device will silently route packets to itself and never " +
            "reach the gateway.  Set serverHost in NetworkSettings to the " +
            "gateway's real IP address or hostname before making a Standalone " +
            "build.  In the Unity Editor a loopback host is valid for local " +
            "development; this advisory fires only in Standalone builds.  " +
            "This advisory is logged once per process.";

        private static int _emitted;

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="host"/> is a
        /// loopback address.  Centralised here so the test project can verify
        /// the predicate independently from the advisory emission path.
        /// </summary>
        internal static bool IsLoopback(string host)
        {
            if (string.IsNullOrEmpty(host)) return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            // IPAddress.IsLoopback covers the full 127.0.0.0/8 IPv4 block and
            // the IPv6 loopback ::1 — matching the companion IsLoopbackGatewayEndpoint
            // in AeadPipeline which uses the same API.
            return System.Net.IPAddress.TryParse(host, out var addr)
                && System.Net.IPAddress.IsLoopback(addr);
        }

        /// <summary>
        /// Emit the advisory at most once per process when the build is a
        /// Standalone Player connecting to a loopback address.  Both arguments
        /// are caller-evaluated so the method has no
        /// <c>UnityEngine.Application</c> dependency.
        /// </summary>
        /// <param name="isLoopbackHost">
        /// True when the configured <c>serverHost</c> is a loopback address.
        /// Pass <see cref="IsLoopback"/><c>(_settings.serverHost)</c> at the
        /// call site.
        /// </param>
        /// <param name="isStandaloneBuild">
        /// True when running as a Standalone Player (not in the Editor).
        /// Pass <c>!Application.isEditor</c> at the call site.
        /// </param>
        internal static void NotifyIfApplicable(bool isLoopbackHost, bool isStandaloneBuild)
        {
            if (!isLoopbackHost)    return;
            if (!isStandaloneBuild) return;
            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0) return;
            UnityEngine.Debug.LogWarning(MessageText);
        }

        /// <summary>
        /// True once the advisory has been emitted in the current process.
        /// Exposed for internal observers and test fixtures; application code
        /// must not use this for control flow.
        /// </summary>
        internal static bool WasEmitted => Volatile.Read(ref _emitted) != 0;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Resets the emission latch so the next qualifying call emits again.
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined.
        /// </summary>
        internal static void ResetForTests() => Interlocked.Exchange(ref _emitted, 0);
#endif
    }
}
