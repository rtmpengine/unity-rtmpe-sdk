// RTMPE SDK — Runtime/Core/NetworkManager.Jwt.cs
//
// Thin delegation layer onto Runtime/Core/JwtValidator.cs.
//
// Delegates JWT validation to <see cref="JwtValidator"/>, which owns
// the full RFC 7519 compliance logic: header decode, signature
// verification, audience normalisation, claim checks, log redaction,
// and one-shot misconfiguration advisories.
//
// Part of the NetworkManager partial class — see NetworkManager.cs for
// the canonical class declaration, base type, and Unity attributes.

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        /// <summary>
        /// Validate the structure, signature, and temporal claims of a
        /// JWT and surface the <c>sub</c> claim on success. Delegates to
        /// <see cref="JwtValidator"/>; see that class for the full RFC
        /// reference set and threat model.
        /// </summary>
        internal bool TryValidateJwt(
            string jwt,
            string expectedIssuer,
            string expectedAudience,
            out string subject,
            out string error)
        {
            // A new validator is allocated per call so the captured
            // _settings reference always reflects the current inspector
            // state. The allocation is amortised across the SessionAck
            // path (once per session) and the validator carries no
            // accumulated state — the warned-once latches are
            // AppDomain-scoped statics on JwtValidator itself.
            return new JwtValidator(_settings)
                .TryValidate(jwt, expectedIssuer, expectedAudience, out subject, out error);
        }

        // ── Test reset passthroughs ─────────────────────────────────────

        internal static void ResetJwtSignatureUnverifiedWarningForTests()
            => JwtValidator.ResetSignatureUnverifiedWarningForTests();

        internal static void ResetJwtIssuerUnconfiguredWarningForTests()
            => JwtValidator.ResetIssuerUnconfiguredWarningForTests();
    }
}
