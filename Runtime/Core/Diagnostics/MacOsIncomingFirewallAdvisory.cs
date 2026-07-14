// RTMPE SDK — Runtime/Core/Diagnostics/MacOsIncomingFirewallAdvisory.cs
//
// Surfaces a single actionable warning when a fresh-connect attempt stalls at
// NoServerReply on macOS.  The macOS Application Firewall blocks INCOMING UDP
// replies at the OS level — SendTo() succeeds (no SocketException is thrown),
// the HandshakeInit leaves the device, but the gateway's reply is silently
// discarded before the socket layer ever sees it.  The failure is
// indistinguishable from a NAT/ISP drop or a gateway PSK rejection until the
// developer inspects Firewall settings.
//
// Kept as a pure, Unity-free seam so the advisory is exercisable from the
// headless dotnet xunit runner without a Unity runtime.  The caller evaluates
// the platform and stage conditions and passes them as caller-evaluated booleans,
// following the same pattern as ReliableSendAdvisory — no Application.platform
// dependency exists inside this class.
//
// Threading: the latch is updated via Interlocked.CompareExchange; callers may
// invoke NotifyIfApplicable from any thread without additional synchronisation.

using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Process-scoped warn-once advisory that fires when a connection times out
    /// at the <see cref="ConnectionFailureStage.NoServerReply"/> stage on macOS.
    /// The macOS Application Firewall silently discards incoming UDP replies for
    /// unsigned or first-run Standalone builds.  The failure is identical to a
    /// NAT drop from the SDK's perspective — <c>SendTo</c> returns without error
    /// but no reply ever arrives.  This advisory names the Firewall as the most
    /// probable cause and provides the exact navigation path to resolve it.
    /// </summary>
    internal static class MacOsIncomingFirewallAdvisory
    {
        // Exposed as a constant so the test project can assert on message
        // contents without duplicating the string.  A wording change here
        // intentionally breaks tests — that is the desired behaviour.
        internal const string MessageText =
            "[RTMPE] Connection timed out at NoServerReply on macOS — the most " +
            "likely cause is the macOS Application Firewall blocking the gateway's " +
            "UDP reply.  The Firewall is INCOMING-only: your HandshakeInit left the " +
            "device successfully (SendTo returned without a SocketException), but " +
            "the gateway's reply was silently discarded before the socket layer.  " +
            "Fix: System Settings → Network → Firewall → Options — locate this " +
            ".app and set it to Allow Incoming Connections.  If the binary is " +
            "unsigned or run for the first time, macOS prompts once per binary; " +
            "signing with an Apple Developer certificate eliminates the repeated " +
            "prompt across builds.  Other causes for NoServerReply (NAT/ISP drop, " +
            "wrong host/port, gateway PSK mismatch) remain possible; this advisory " +
            "fires because the Firewall is the most common cause in local-dev " +
            "scenarios.  This advisory is logged once per process.";

        // 0 = pending, 1 = emitted.  Interlocked CAS ensures exactly one caller
        // wins the emission race without a lock on the hot check path.
        private static int _emitted;

        /// <summary>
        /// Emit the advisory at most once per process when both conditions hold:
        /// the classified failure stage is <c>NoServerReply</c> and the runtime
        /// platform is macOS.  Both arguments are caller-evaluated so the method
        /// has no <c>UnityEngine.Application</c> dependency and is fully
        /// exercisable in headless test environments.
        /// </summary>
        /// <param name="isNoServerReply">
        /// True when the connection failure stage is
        /// <see cref="ConnectionFailureStage.NoServerReply"/>.
        /// </param>
        /// <param name="isMacOsPlatform">
        /// True when running on macOS (Editor or Standalone Player).
        /// Pass <c>Application.platform == RuntimePlatform.OSXPlayer ||
        /// Application.platform == RuntimePlatform.OSXEditor</c> at the call site.
        /// </param>
        internal static void NotifyIfApplicable(bool isNoServerReply, bool isMacOsPlatform)
        {
            if (!isNoServerReply) return;
            if (!isMacOsPlatform) return;
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
        /// Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined; production
        /// assemblies must never call this.
        /// </summary>
        internal static void ResetForTests() => Interlocked.Exchange(ref _emitted, 0);
#endif
    }
}
