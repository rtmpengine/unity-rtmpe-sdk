// RTMPE SDK — Runtime/Core/ConnectionDiagnostics.cs
//
// Turns an opaque connection timeout into a single line that names the exact
// stage the handshake stalled at. Kept as a pure, UnityEngine-free seam — no
// fields, no clock, no transport — so the classification can be exercised in
// isolation the same way HeartbeatLivenessPolicy is.
//
// A fresh connect walks a fixed ladder: the transport must bind, the
// HandshakeInit must leave, the gateway must answer with a Challenge, and the
// SessionAck must finalise the session. A timeout means one of those rungs was
// never reached. Reporting the FIRST unreached rung — rather than the generic
// "timeout" the caller already knows — is what lets the operator separate a
// build that cannot emit UDP at all from one whose packets reach a gateway that
// rejects them, without a packet capture. The two look identical to the caller
// (no SessionAck) but land on different rungs here.

namespace RTMPE.Core
{
    /// <summary>
    /// The first handshake rung a failed connect attempt did not reach, ordered
    /// earliest to latest so the classifier reports the earliest unmet step.
    /// </summary>
    internal enum ConnectionFailureStage
    {
        /// <summary>The transport never reported a bound local endpoint.</summary>
        TransportNotBound,

        /// <summary>The transport bound but no HandshakeInit was dispatched.</summary>
        HandshakeInitNotSent,

        /// <summary>HandshakeInit left the client but no Challenge ever arrived.</summary>
        NoServerReply,

        /// <summary>A Challenge arrived but the session was never finalised.</summary>
        ServerReplyNotFinalized,

        /// <summary>All rungs were reached — there is no failure to diagnose.</summary>
        Completed,
    }

    /// <summary>
    /// Pure assembler for the connection-failure diagnostic line. Given the four
    /// handshake-progress witnesses captured during a connect attempt, it names
    /// the stalled stage and the operator action that resolves it.
    /// </summary>
    internal static class ConnectionDiagnostics
    {
        /// <summary>
        /// Resolve the earliest handshake rung that was not reached. The order of
        /// the checks is load-bearing: a later witness is only meaningful once
        /// every earlier one holds (a Challenge cannot arrive before the init is
        /// sent), so the first failing check is the true root stage.
        /// </summary>
        internal static ConnectionFailureStage Classify(
            bool transportBound,
            bool handshakeInitSent,
            bool challengeReceived,
            bool sessionEstablished)
        {
            if (sessionEstablished) return ConnectionFailureStage.Completed;
            if (!transportBound) return ConnectionFailureStage.TransportNotBound;
            if (!handshakeInitSent) return ConnectionFailureStage.HandshakeInitNotSent;
            if (!challengeReceived) return ConnectionFailureStage.NoServerReply;
            return ConnectionFailureStage.ServerReplyNotFinalized;
        }

        /// <summary>
        /// Build the full diagnostic line: the elapsed budget, the four witness
        /// states, and the stage-specific guidance that points at the one thing
        /// worth checking next.
        /// </summary>
        internal static string Describe(
            bool transportBound,
            bool handshakeInitSent,
            bool challengeReceived,
            bool sessionEstablished,
            int elapsedMs)
        {
            ConnectionFailureStage stage = Classify(
                transportBound, handshakeInitSent, challengeReceived, sessionEstablished);

            string guidance = stage switch
            {
                ConnectionFailureStage.TransportNotBound =>
                    "the UDP socket never bound — the OS denied it (check this build's " +
                    "local-network / firewall permission) or there is no route to the gateway host.",
                ConnectionFailureStage.HandshakeInitNotSent =>
                    "the transport bound but no HandshakeInit was dispatched — the API-key " +
                    "envelope is almost certainly unconfigured (set apiKeySealServerPublicKeyHex " +
                    "or apiKeyPskHex on the NetworkSettings asset used by this build).",
                ConnectionFailureStage.NoServerReply =>
                    "HandshakeInit was sent but the gateway never answered.  Three possible " +
                    "causes: (A) incoming-UDP block — on macOS the Application Firewall may be " +
                    "silently discarding the gateway's reply (the Firewall is INCOMING-only; " +
                    "SendTo still returns success); fix: System Settings → Network → Firewall → " +
                    "Options, allow this .app.  (B) network drop — a NAT, ISP, or host firewall " +
                    "rule is dropping packets between client and gateway; verify with a direct " +
                    "UDP echo from the same host and port.  (C) gateway rejection — the gateway " +
                    "received the packet but silently dropped it due to a wrong PSK or seal key; " +
                    "check apiKeyPskHex / apiKeySealServerPublicKeyHex in NetworkSettings and " +
                    "confirm the gateway logs show no crypto-reject events.",
                ConnectionFailureStage.ServerReplyNotFinalized =>
                    "the gateway answered with a Challenge but the session was never finalised — " +
                    "most likely a pinnedServerPublicKeyHex mismatch or a lost handshake response.",
                _ =>
                    "the attempt reported as established — no failure stage to diagnose.",
            };

            string header = stage == ConnectionFailureStage.Completed
                ? $"connection succeeded after {elapsedMs} ms at stage '{stage}'"
                : $"connection failed after {elapsedMs} ms at stage '{stage}'";
            return $"{header} " +
                   $"(transport={(transportBound ? "bound" : "not-bound")}, " +
                   $"handshakeInit={(handshakeInitSent ? "sent" : "not-sent")}, " +
                   $"challenge={(challengeReceived ? "received" : "not-received")}, " +
                   $"session={(sessionEstablished ? "established" : "not-established")}) — {guidance}";
        }
    }
}
