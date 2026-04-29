// RTMPE SDK — Runtime/Crypto/ServerPinningMode.cs
//
// Three explicit modes for server-static-key pinning, with a fail-closed
// default.  The literal name "InsecureNoPinning" is deliberate: a developer
// cannot end up in that mode by accident or by leaving a field blank — the
// security-degrading choice has to be typed out in source.

namespace RTMPE.Crypto
{
    /// <summary>
    /// How the SDK should treat the server's Ed25519 static public key
    /// embedded in the handshake Challenge.
    ///
   /// <para>
    /// <b>Strict</b> (default) — the key MUST equal an operator-supplied pin
    /// (see <see cref="Core.NetworkSettings.pinnedServerPublicKeyHex"/>).
    /// If no pin is configured, the handshake is refused.  Use this for any
    /// build that ships to end users.
    /// </para>
    /// <para>
    /// <b>TrustOnFirstUse</b> — on the first connection to a given endpoint,
    /// the server's static key is captured and persisted.  Every subsequent
    /// connection to the same endpoint MUST present the same key; a mismatch
    /// is treated as a MITM attempt and refused.  Suitable for in-house
    /// development and for shipping builds where the operator cannot embed a
    /// pin at compile time but is willing to accept the first-flight risk.
    /// </para>
    /// <para>
    /// <b>InsecureNoPinning</b> — the SDK accepts any self-consistent
    /// (staticPub, signature) triple from the server.  This is vulnerable to
    /// a substituted-key MITM attack and exists ONLY for local-loop testing
    /// or for environments that authenticate the server out of band (e.g. a
    /// mutually-authenticated TLS tunnel below RTMPE).  Emits a runtime
    /// warning each time a session is established.
    /// </para>
    /// </summary>
    public enum ServerPinningMode
    {
        /// <summary>Refuse connect unless the embedded key matches the configured pin.</summary>
        Strict = 0,

        /// <summary>Capture-and-pin on first connect; strict match on every subsequent connect.</summary>
        TrustOnFirstUse = 1,

        /// <summary>
        /// Accept any valid Ed25519 signature.  Logs a warning per session.
        /// Not safe for production.
        /// </summary>
        InsecureNoPinning = 2,
    }
}
