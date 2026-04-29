// RTMPE SDK — Runtime/Crypto/ServerKeyPinning.cs
//
// Pure logic that drives the per-session pinning decision.  Has no Unity
// dependencies so it runs unchanged inside the test runner.
//
// The resolver runs in two phases:
//
//  1. Pre-Challenge (PreparePin):
//       Decide whether to refuse outright (Strict + no pin), and otherwise
//       compute the byte[] that NetworkManager will pass to
//       HandshakeHandler.ValidateChallenge as `pinnedServerStaticPub`.
//       TOFU with no persisted pin returns null here — the cryptographic
//       verification in ValidateChallenge is still the gate; the captured
//       key is persisted only AFTER verification succeeds.
//
//  2. Post-Challenge (PersistFirstUse):
//       Called only on the success path, with the staticPub returned by
//       ValidateChallenge.  Writes the pin in TOFU mode if and only if no
//       pin was previously persisted for this endpoint.

namespace RTMPE.Crypto
{
    /// <summary>
    /// Outcome of <see cref="ServerKeyPinning.PreparePin"/>.
    /// </summary>
    public enum PinDecision
    {
        /// <summary>Caller proceeds to ValidateChallenge with no enforcement (InsecureNoPinning, with warning).</summary>
        ProceedUnpinned = 0,

        /// <summary>Caller proceeds to ValidateChallenge with an explicit pin to enforce.</summary>
        ProceedWithPin = 1,

        /// <summary>Caller is in TOFU mode and no pin is persisted yet — capture the key after success.</summary>
        ProceedCaptureFirstUse = 2,

        /// <summary>Caller MUST refuse the handshake — Strict mode but no pin configured.</summary>
        RefuseStrictNoPin = 3,
    }

    /// <summary>
    /// Snapshot of the pinning decision for a single Challenge round.
    /// </summary>
    public readonly struct PinResolution
    {
        public PinDecision Decision { get; }

        /// <summary>Pin to enforce in <see cref="HandshakeHandler.ValidateChallenge"/>, or null.</summary>
        public byte[] PinToEnforce { get; }

        /// <summary>Canonical "host:port" used as the persistence key in TOFU.</summary>
        public string Endpoint { get; }

        public PinResolution(PinDecision decision, byte[] pinToEnforce, string endpoint)
        {
            Decision     = decision;
            PinToEnforce = pinToEnforce;
            Endpoint     = endpoint;
        }
    }

    /// <summary>
    /// Stateless helper that decides what to do with the server static key
    /// during a handshake.  All inputs are explicit; the helper does not
    /// touch Unity APIs directly.
    /// </summary>
    public static class ServerKeyPinning
    {
        /// <summary>
        /// Build the canonical "host:port" key used to address persisted
        /// pins.  Trims whitespace and lowercases the host so that
        /// "Example.com" and "example.com" map to the same pin slot — but
        /// does NOT perform DNS resolution: a pin is bound to the literal
        /// address the user configured, so a hostile resolver swapping in
        /// an attacker IP is forced through TOFU again.
        /// </summary>
        public static string CanonicalEndpoint(string host, int port)
        {
            if (string.IsNullOrEmpty(host)) host = "";
            return host.Trim().ToLowerInvariant() + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Decide which pin (if any) to enforce for the upcoming Challenge.
        /// </summary>
        /// <param name="mode">Configured pinning mode.</param>
        /// <param name="configuredPin">
        /// Operator-embedded pin (32 bytes) decoded from
        /// <see cref="Core.NetworkSettings.pinnedServerPublicKeyHex"/>, or
        /// <see langword="null"/> if no pin is configured.  Takes precedence
        /// over a TOFU-persisted pin in <see cref="ServerPinningMode.Strict"/>.
        /// </param>
        /// <param name="store">Pin storage (used only for TOFU mode).</param>
        /// <param name="host">Server host (raw, as configured).</param>
        /// <param name="port">Server port.</param>
        public static PinResolution PreparePin(
            ServerPinningMode mode,
            byte[] configuredPin,
            IServerKeyPinStore store,
            string host,
            int port)
        {
            var endpoint = CanonicalEndpoint(host, port);

            switch (mode)
            {
                case ServerPinningMode.Strict:
                    if (configuredPin == null || configuredPin.Length != 32)
                        return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);
                    return new PinResolution(PinDecision.ProceedWithPin, configuredPin, endpoint);

                case ServerPinningMode.TrustOnFirstUse:
                    // An explicit configured pin in TOFU mode is honoured
                    // (treats TOFU as "use configured pin if you have one,
                    // otherwise capture on first use").  This avoids a
                    // surprise downgrade where an operator who provided a
                    // pin discovers it was silently ignored.
                    if (configuredPin != null && configuredPin.Length == 32)
                        return new PinResolution(PinDecision.ProceedWithPin, configuredPin, endpoint);

                    var persisted = store?.Load(endpoint);
                    if (persisted != null && persisted.Length == 32)
                        return new PinResolution(PinDecision.ProceedWithPin, persisted, endpoint);

                    return new PinResolution(PinDecision.ProceedCaptureFirstUse, null, endpoint);

                case ServerPinningMode.InsecureNoPinning:
                    return new PinResolution(PinDecision.ProceedUnpinned, null, endpoint);

                default:
                    // Unknown enum value — fail closed.  An attacker who can
                    // poison the settings asset to a bogus enum value must
                    // not slide into "no pinning"; refuse instead.
                    return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);
            }
        }

        /// <summary>
        /// In TOFU mode, persist the verified server static key to the pin
        /// store after a successful handshake.  The caller MUST invoke this
        /// only after <see cref="HandshakeHandler.ValidateChallenge"/>
        /// returned <see langword="true"/> AND
        /// <see cref="HandshakeHandler.DeriveSessionKeys"/> succeeded —
        /// writing the pin earlier would persist an attacker-supplied key
        /// for any malformed Challenge.
        /// </summary>
        /// <returns>
        /// <see langword="true"/> if a new pin was written.
        /// <see langword="false"/> if no write was needed (mode was not TOFU,
        /// or a pin already existed).
        /// </returns>
        public static bool PersistFirstUse(
            PinResolution resolution,
            IServerKeyPinStore store,
            byte[] verifiedServerStaticPub)
        {
            if (resolution.Decision != PinDecision.ProceedCaptureFirstUse) return false;
            if (store == null) return false;
            if (verifiedServerStaticPub == null || verifiedServerStaticPub.Length != 32) return false;

            store.Save(resolution.Endpoint, verifiedServerStaticPub);
            return true;
        }
    }
}
