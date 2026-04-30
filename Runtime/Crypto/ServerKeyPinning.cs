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
            // Reject embedded NUL bytes in the host string.  PlayerPrefs and
            // platform-specific keystores (iOS Keychain, Android
            // SharedPreferences) routinely round-trip strings through
            // C-style APIs that truncate at the first NUL — a host of the
            // shape "good.example\0evil" would persist under "good.example"
            // on those backends, breaking pin-retrieval on the next
            // connection and silently demoting a previously-pinned
            // endpoint back into TOFU capture.
            if (host.IndexOf('\0') >= 0)
                throw new System.ArgumentException(
                    "host must not contain embedded NUL bytes.", nameof(host));
            // Unicode-normalise the host before the lowercase fold.  Two
            // visually-equivalent forms — e.g. U+212B (Å) versus the
            // canonically-equivalent U+00C5 (Å), or any combining-mark
            // composition versus its precomposed counterpart — would
            // otherwise hash into distinct pin slots, splitting a single
            // logical endpoint across multiple PlayerPrefs entries and
            // silently demoting a previously-pinned endpoint to a fresh
            // TOFU capture whenever the host is re-typed in a different
            // Unicode form.  NFC is the standard form for IDN compatibility.
            string normalised = host.Normalize(System.Text.NormalizationForm.FormC);
            return normalised.Trim().ToLowerInvariant() + ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
            // Backwards-compatible default: first-use capture is permitted.
            // Callers that want to refuse first-flight TOFU MUST opt in via
            // the overload below.
            return PreparePin(mode, configuredPin, store, host, port,
                requireFirstUseProvisioned: false);
        }

        /// <summary>
        /// Decide which pin (if any) to enforce for the upcoming Challenge,
        /// with explicit control over first-flight TOFU capture.
        /// </summary>
        /// <param name="mode">Configured pinning mode.</param>
        /// <param name="configuredPin">Operator-embedded pin (32 bytes), or null.</param>
        /// <param name="store">Pin storage (used only for TOFU mode).</param>
        /// <param name="host">Server host (raw, as configured).</param>
        /// <param name="port">Server port.</param>
        /// <param name="requireFirstUseProvisioned">
        /// When <see langword="true"/>, refuse to perform first-flight TOFU
        /// capture against an unseen endpoint.  TOFU's accepted-risk gap is
        /// the very first connect to a new <c>host:port</c>: a network-
        /// positioned attacker on that single flight can substitute their
        /// Ed25519 key and have it persisted as the durable pin, defeating
        /// every subsequent connect's pinning check.  Setting this flag
        /// closes that gap by mandating that the pin be present BEFORE the
        /// SDK ever opens a socket to the endpoint — the pin must arrive via
        /// a trusted side-channel (signed bootstrap config, MDM push, staged
        /// install, etc.) and be written into the <see cref="IServerKeyPinStore"/>
        /// out-of-band.  An operator-supplied <paramref name="configuredPin"/>
        /// is still honoured because it is itself a pre-provisioned pin.
        /// </param>
        public static PinResolution PreparePin(
            ServerPinningMode mode,
            byte[] configuredPin,
            IServerKeyPinStore store,
            string host,
            int port,
            bool requireFirstUseProvisioned)
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

                    // No pin known for this endpoint.  Under the hardened
                    // contract, refuse rather than capturing whatever the
                    // network delivers on this first flight — the pin MUST
                    // arrive via a trusted out-of-band channel.  The refuse
                    // verdict reuses RefuseStrictNoPin because the operator-
                    // visible remediation is identical: provision a pin.
                    if (requireFirstUseProvisioned)
                        return new PinResolution(PinDecision.RefuseStrictNoPin, null, endpoint);

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
