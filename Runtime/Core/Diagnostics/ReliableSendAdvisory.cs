// RTMPE SDK — Runtime/Core/Diagnostics/ReliableSendAdvisory.cs
//
// Surfaces single, process-wide advisories whenever
// `NetworkManager.Send(reliable: true)` cannot engage application-layer
// reliability because one or both halves of the contract is missing.  Two
// distinct causes can produce the same silent downgrade, each with its
// own actionable for the integrator:
//
//   1. The local deployment opt-in (`NetworkSettings.EmitArqSequence`) is
//      disabled.  The SDK has no instruction to emit the 4-byte ARQ
//      sub-header on the wire, so retransmits + DataAck cannot start
//      regardless of what the gateway is willing to do.  Surfaced by
//      `ReliableSendAdvisory.NotifyIfDowngrading`.
//
//   2. The negotiated peer capability set (returned in the SessionAck
//      `gateway_caps` tail) does not contain
//      `CapabilityFlags.ArqAck`.  Either the gateway is a legacy build
//      that never knew about the cap field, or the operator has not set
//      `RTMPE_ADVERTISE_ARQ_CAP=true`.  In either case the gateway will
//      not emit DataAck, so the SDK would loop retransmitting until the
//      attempt cap is reached.  Surfaced by
//      `PeerCapabilityAdvisory.NotifyIfArqUnavailable`.
//
// Why two advisories
// ------------------
// Keeping the two causes on separate latches lets a misconfiguration on
// both sides surface as two distinct, individually-actionable lines in
// the console rather than a single conflated message that elides one of
// the fixes.  Each cause is genuinely a deployment-time event, so the
// warn-once cadence matches the diagnostic interest profile — every
// further `Send(reliable: true)` for the rest of the process re-confirms
// the same misconfiguration and would only add noise.
//
// Threading
// ---------
// Each advisory's latch is a single int updated via
// Interlocked.CompareExchange.  Callers may invoke the notify entry
// points from any thread; the first successful CAS wins and emits, all
// later callers cheaply observe the latched value and return without
// side effects.
//
// Testability
// -----------
// Both advisories are fully pure-managed apart from the single
// UnityEngine.Debug.LogWarning sink, which keeps the types compilable
// into the standalone xunit test project under
// tests/unit/unity-sdk-arq-advisory.  Each advisory exposes a
// ResetForTests() seam that drains its latch between fixtures so a test
// observes a clean precondition.

using System.Threading;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Process-scoped warn-once advisory that fires when a caller invokes
    /// <see cref="RTMPE.Core.NetworkManager.Send"/> with
    /// <c>reliable: true</c> while the deployment-level opt-in
    /// (<see cref="RTMPE.Core.NetworkSettings.EmitArqSequence"/>) is
    /// disabled.  Surfaces the configuration mismatch through
    /// <see cref="UnityEngine.Debug.LogWarning"/> so the silent
    /// best-effort downgrade has a visible, named cause in the console.
    /// </summary>
    internal static class ReliableSendAdvisory
    {
        // The text is exposed as a constant so the test project can assert
        // on the message contents without duplicating the string.  Tests
        // failing on a wording tweak is the desirable behaviour — every
        // change to the advisory's text is then a deliberate, reviewed
        // edit rather than a silent drift.
        internal const string MessageText =
            "[RTMPE] NetworkManager.Send(reliable: true) — the deployment has " +
            "NetworkSettings.EmitArqSequence disabled, so the SDK is downgrading " +
            "this and all subsequent reliable sends to best-effort delivery (no " +
            "application-layer retransmit, no DataAck).  On raw UDP this means " +
            "lost packets are not recovered; enable EmitArqSequence on BOTH the " +
            "SDK settings asset and the gateway build to activate the ARQ wire " +
            "extension.  On KCP and WebSocket transports the underlying stream " +
            "already delivers bytes reliably, so leaving EmitArqSequence disabled " +
            "is the expected configuration for those.  This advisory is logged " +
            "once per process; the downgrade itself continues silently for " +
            "subsequent sends.";

        // 0 = advisory pending, 1 = already emitted.  Updated through
        // Interlocked.CompareExchange so concurrent first-time callers
        // race to the single emission deterministically without a lock.
        private static int _emitted;

        /// <summary>
        /// Emit the advisory at most once per process when the caller
        /// requested reliability but the deployment opt-in is disabled.
        /// Both arguments are caller-evaluated booleans so the method can
        /// be invoked without a live <see cref="RTMPE.Core.NetworkSettings"/>
        /// reference; callers pass <c>settings != null &amp;&amp;
        /// settings.EmitArqSequence</c> for the first argument and the
        /// caller-supplied intent for the second.
        /// </summary>
        /// <param name="emitArqSequence">
        /// The deployment-level opt-in.  When <see langword="true"/> the
        /// advisory never fires regardless of caller intent.
        /// </param>
        /// <param name="reliableRequested">
        /// The caller-supplied <c>reliable</c> argument to
        /// <see cref="RTMPE.Core.NetworkManager.Send"/>.  When
        /// <see langword="false"/> the advisory never fires; the caller
        /// already accepted best-effort semantics by construction.
        /// </param>
        public static void NotifyIfDowngrading(bool emitArqSequence, bool reliableRequested)
        {
            if (!reliableRequested) return;
            if (emitArqSequence)    return;

            // CompareExchange returns the prior value; a non-zero prior
            // means another caller already emitted, so this invocation
            // returns without touching the sink.
            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0) return;

            UnityEngine.Debug.LogWarning(MessageText);
        }

        /// <summary>
        /// Snapshot of the latch.  <see langword="true"/> once the
        /// advisory has been emitted in the current process.  Exposed for
        /// internal observers (test fixtures, editor diagnostics) — apps
        /// must not rely on this for control flow because the warn-once
        /// guarantee is the only contract the public surface offers.
        /// </summary>
        internal static bool WasEmitted =>
            Volatile.Read(ref _emitted) != 0;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Resets the latch so the next <see cref="NotifyIfDowngrading"/>
        /// call that meets the downgrade predicate emits again.  Exists
        /// exclusively to let unit tests observe the first-emission code
        /// path from a clean precondition; production code must not call
        /// this.  Compiled only when <c>UNITY_INCLUDE_TESTS</c> is defined
        /// so the shipped Player assembly does not expose a mutator on the
        /// process-wide emission latch.
        /// </summary>
        internal static void ResetForTests() =>
            Interlocked.Exchange(ref _emitted, 0);
#endif // UNITY_INCLUDE_TESTS
    }

    /// <summary>
    /// Process-scoped warn-once advisory that fires when a caller invokes
    /// <see cref="RTMPE.Core.NetworkManager.Send"/> with
    /// <c>reliable: true</c> after a session whose negotiated capability
    /// set excludes <see cref="RTMPE.Core.Protocol.CapabilityFlags.ArqAck"/>.
    /// Distinct from <see cref="ReliableSendAdvisory"/>: this advisory
    /// reports the *peer-side* cause (the gateway never promised to emit
    /// DataAck for this session), whereas
    /// <see cref="ReliableSendAdvisory"/> reports the *local-side* cause
    /// (<see cref="RTMPE.Core.NetworkSettings.EmitArqSequence"/> is off).
    /// Two latches keep the two causes individually actionable when both
    /// apply.
    /// </summary>
    internal static class PeerCapabilityAdvisory
    {
        /// <summary>
        /// Canonical message text emitted on first downgrade due to the
        /// peer-side cap being absent.  Centralised as a constant so the
        /// test project asserts on a stable string and a future wording
        /// edit is a single, reviewed location.
        /// </summary>
        internal const string MessageText =
            "[RTMPE] NetworkManager.Send(reliable: true) — the gateway did not " +
            "advertise CAP_ARQ_ACK during the handshake, so the SDK has nothing " +
            "to anchor the retransmit ladder against and is downgrading this and " +
            "all subsequent reliable sends to best-effort delivery (no application-" +
            "layer retransmit, no DataAck).  Confirm the gateway build is recent " +
            "enough to include the capability tail on SessionAck and that the " +
            "operator has set RTMPE_ADVERTISE_ARQ_CAP=true on the gateway process.  " +
            "On KCP and WebSocket gateways the underlying stream already provides " +
            "reliability, so the cap is intentionally not advertised there — the " +
            "expected configuration for those transports is to keep " +
            "NetworkSettings.EmitArqSequence disabled on the client side.  This " +
            "advisory is logged once per process; the downgrade itself continues " +
            "silently for subsequent sends.";

        // 0 = advisory pending, 1 = already emitted.  Independent latch
        // from `ReliableSendAdvisory._emitted` so an integrator whose
        // misconfiguration applies on both sides sees BOTH advisories.
        private static int _emitted;

        /// <summary>
        /// Emit the advisory at most once per process when the caller
        /// requested reliability, the local opt-in is enabled, but the
        /// session's negotiated capability bitmask excludes ArqAck.  The
        /// local-opt-in branch is the responsibility of
        /// <see cref="ReliableSendAdvisory.NotifyIfDowngrading"/>; gating
        /// on <c>emitArqSequence</c> here keeps the two advisories from
        /// firing simultaneously for the same single root cause.
        /// </summary>
        /// <param name="emitArqSequence">
        /// The deployment-level opt-in.  When <see langword="false"/>
        /// this advisory never fires — the local-side advisory already
        /// owns that case.
        /// </param>
        /// <param name="peerSupportsArqAck">
        /// Whether the session's negotiated capability set contains
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.ArqAck"/>.  When
        /// <see langword="true"/> the advisory never fires; ARQ is
        /// engaging normally.
        /// </param>
        /// <param name="reliableRequested">
        /// The caller-supplied <c>reliable</c> argument to
        /// <see cref="RTMPE.Core.NetworkManager.Send"/>.  When
        /// <see langword="false"/> the advisory never fires; the caller
        /// already accepted best-effort semantics by construction.
        /// </param>
        public static void NotifyIfArqUnavailable(
            bool emitArqSequence,
            bool peerSupportsArqAck,
            bool reliableRequested)
        {
            if (!reliableRequested)   return;
            if (!emitArqSequence)     return;
            if (peerSupportsArqAck)   return;

            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0) return;

            UnityEngine.Debug.LogWarning(MessageText);
        }

        /// <summary>
        /// Snapshot of the latch.  <see langword="true"/> once the
        /// advisory has been emitted in the current process.  Exposed for
        /// internal observers (test fixtures, editor diagnostics) — apps
        /// must not rely on this for control flow because the warn-once
        /// guarantee is the only contract the public surface offers.
        /// </summary>
        internal static bool WasEmitted =>
            Volatile.Read(ref _emitted) != 0;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Resets the latch so the next
        /// <see cref="NotifyIfArqUnavailable"/> call that meets the
        /// downgrade predicate emits again.  Exists exclusively to let
        /// unit tests observe the first-emission code path from a clean
        /// precondition; production code must not call this.  Compiled
        /// only when <c>UNITY_INCLUDE_TESTS</c> is defined so the shipped
        /// Player assembly does not expose a mutator on the process-wide
        /// emission latch.
        /// </summary>
        internal static void ResetForTests() =>
            Interlocked.Exchange(ref _emitted, 0);
#endif // UNITY_INCLUDE_TESTS
    }

    /// <summary>
    /// Process-scoped warn-once advisory that fires when a caller invokes
    /// <see cref="RTMPE.Core.NetworkManager.Send"/> with <c>reliable: true</c>
    /// on a session where the ARQ contract is fully engaged, but the outbound
    /// retransmit window is saturated, so the packet ships once with no retry.
    /// Distinct from <see cref="ReliableSendAdvisory"/> and
    /// <see cref="PeerCapabilityAdvisory"/>: those report a *misconfiguration*
    /// (reliability is not in force at all), whereas this reports *runtime
    /// back-pressure* (reliability is in force but the in-flight window is
    /// momentarily full).  A separate latch keeps the saturation signal from
    /// being conflated with the two configuration signals.
    /// </summary>
    internal static class ReliableSaturationAdvisory
    {
        /// <summary>
        /// Canonical message text, centralised as a constant so the test
        /// project asserts on a stable string and a future wording edit is a
        /// single reviewed location.
        /// </summary>
        internal const string MessageText =
            "[RTMPE] NetworkManager.Send(reliable: true) — the outbound ARQ " +
            "retransmit window is full, so this packet (and further reliable sends " +
            "while it stays saturated) is delivered best-effort with no retransmit.  " +
            "This is runtime back-pressure, not a misconfiguration: the producer is " +
            "outpacing acknowledgement.  Lower the reliable send rate, or watch " +
            "NetworkManager.SendQueueDroppedCount to track saturation.  Logged once " +
            "per process; the downgrade continues silently for subsequent saturated " +
            "sends.";

        // 0 = advisory pending, 1 = already emitted.  Independent latch from the
        // two configuration advisories so a saturated link surfaces its own
        // distinct, individually-actionable line.
        private static int _emitted;

        /// <summary>
        /// Emit the advisory at most once per process.  Called only from the
        /// saturation branch of the send path, where reliability is engaged but
        /// <c>ReliableChannel.TryRegisterOutbound</c> reported a full window.
        /// </summary>
        public static void NotifyOnSaturation()
        {
            if (Interlocked.CompareExchange(ref _emitted, 1, 0) != 0) return;

            UnityEngine.Debug.LogWarning(MessageText);
        }

        /// <summary>
        /// Snapshot of the latch.  <see langword="true"/> once the advisory has
        /// been emitted in the current process.  For internal observers (test
        /// fixtures, editor diagnostics) only.
        /// </summary>
        internal static bool WasEmitted =>
            Volatile.Read(ref _emitted) != 0;

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Resets the latch so the next <see cref="NotifyOnSaturation"/> call
        /// emits again.  Test-only seam; production code must not call this.
        /// </summary>
        internal static void ResetForTests() =>
            Interlocked.Exchange(ref _emitted, 0);
#endif // UNITY_INCLUDE_TESTS
    }
}
