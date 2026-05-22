// RTMPE SDK — Runtime/Core/JwtValidator.cs
//
// Stateful JWT validator for session tokens issued by the RTMPE gateway.  Owns the structural,
// signature-binding, temporal, issuer, and audience checks applied to a
// SessionAck-bearing token.  Pairs with the free-standing static
// JwtSignatureVerifier (algorithm primitives) — the verifier is pure,
// JwtValidator binds it to NetworkSettings configuration and the one-shot
// misconfiguration advisories.
//
// Why this exists as a class (rather than free statics):
//   • Captures the per-validator NetworkSettings reference once at construction
//     so each TryValidate call does not re-fetch through a manager pointer
//     (cheap, but more importantly: makes the validator independently testable
//     with a freshly-constructed settings asset).
//   • Owns the AppDomain-scoped "warned-once" latches centrally.  Putting them
//     inside the validator removes them from NetworkManager's already-large
//     surface and makes the test reset hooks discoverable next to the warning
//     emit sites they pair with.
//   • The DTOs (JwtHeaderDto / JwtClaimsDto) and pure helpers
//     (NormaliseAudClaim / TryDecodeBase64Url) move with the validator so a
//     future audit only has to read this file to understand SessionAck
//     token acceptance.  Untrusted claim values folded into a rejection
//     message are scrubbed through the shared Diagnostics.UntrustedLogText
//     helper.
//
// Threading model:
//   • TryValidate runs on the Unity main thread (called from OnSessionAck
//     after the dispatcher hop).
//   • The static warned-once latches are written via Interlocked.CompareExchange
//     so cross-thread invocations from a future call site cannot fire the
//     advisory more than once per AppDomain.
//
// Wire-format / RFC notes:
//   • RFC 7519 §4.1 — claim names sub / iss / aud / exp / nbf / iat.
//   • RFC 7519 §4.1.3 — `aud` MAY be a string or array of strings.
//     NormaliseAudClaim rewrites the array shape into a single-string field
//     so Unity's JsonUtility (which cannot reflect a heterogeneous shape)
//     binds cleanly; the original array values are passed back via the
//     `audValues` out-parameter for the membership check.
//   • RFC 8725 §3.1 — `alg=none` is rejected unconditionally.
//   • RFC 8725 §2.1 — signature verification runs BEFORE claim inspection so
//     that an unsigned (or attacker-modified) token cannot probe for exp /
//     nbf / iss / aud claim shape via timing or distinct error messages.
//     A single GenericSignatureFailure string is surfaced for every
//     pre-signature failure.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

using UnityEngine;

namespace RTMPE.Core
{
    internal sealed class JwtValidator
    {
        private const string GenericSignatureFailure = "JWT verification failed";

        // ── AppDomain-scoped one-shot misconfiguration advisories ──────────
        // Per-SessionAck warnings would drown the console and fail to surface
        // in CI log scrapes; one LogError per AppDomain is enough to be
        // discoverable via grep / log-aggregator alerting without becoming
        // log spam during steady-state operation.
        private static int s_signatureUnverifiedWarned;
        private static int s_issuerUnconfiguredWarned;
        private static int s_audienceUnconfiguredWarned;

        private readonly NetworkSettings _settings;

        // When non-null, the 32-byte Ed25519 public key the JWT signature is
        // verified against, overriding the NetworkSettings algorithm/key.
        // Supplied by the SessionAck handler when the gateway advertised
        // CapabilityFlags.IdentitySignedJwt: the key is the server's static
        // identity key, already verified during the handshake Challenge.  Its
        // value as an independent trust anchor is bounded by the server-key
        // pinning mode (operator-pinned under strict pinning; otherwise as
        // strong as that mode's own guarantee).
        private readonly byte[] _identityVerificationKey;

        public JwtValidator(NetworkSettings settings, byte[] identityVerificationKey = null)
        {
            // Null `settings` is a supported configuration: a NetworkManager
            // that has not had a NetworkSettings asset attached falls through
            // to the secure-by-default Ed25519 algorithm and the 120-second
            // default clock skew, so a minimal inspector configuration still
            // validates tokens with secure defaults.
            _settings = settings;
            _identityVerificationKey = identityVerificationKey;
        }

        /// <summary>
        /// Lowercase-hex encoding of <paramref name="bytes"/>.  Used to feed
        /// the identity verification key to <see cref="JwtSignatureVerifier"/>,
        /// whose Ed25519 path accepts the key as a hex string.
        /// </summary>
        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        // ── DTOs (private — JsonUtility binds to public fields by name) ────
        [Serializable]
        private sealed class JwtHeaderDto
        {
            // ReSharper disable InconsistentNaming — JSON contract.
#pragma warning disable IDE1006
            public string alg;
            public string typ;
            public string kid;
#pragma warning restore IDE1006
        }

        [Serializable]
        private sealed class JwtClaimsDto
        {
            // Serialized field names must match JWT claim names exactly.
            // ReSharper disable InconsistentNaming — JSON contract.
#pragma warning disable IDE1006
            public string sub;
            public string iss;
            public string aud;
            public long   exp;
            public long   nbf;
            public long   iat;
#pragma warning restore IDE1006
        }

        /// <summary>
        /// Validate the structure, signature, and temporal claims of a JWT
        /// and surface the <c>sub</c> claim on success.  See file header for
        /// the RFC references and threat model.
        /// </summary>
        /// <param name="jwt">Compact-serialised JWS (header.payload.sig).</param>
        /// <param name="expectedIssuer">
        /// Optional <c>iss</c> claim to enforce.  When non-empty the token
        /// must declare the same issuer or it is rejected.
        /// </param>
        /// <param name="expectedAudience">
        /// Optional <c>aud</c> claim to enforce.  RFC 7519 §4.1.3 array form
        /// is supported — the token is accepted if <paramref name="expectedAudience"/>
        /// appears anywhere in the array.
        /// </param>
        /// <param name="subject">Receives the decoded <c>sub</c> claim on success.</param>
        /// <param name="error">Receives a short reason string on failure.</param>
        public bool TryValidate(
            string jwt,
            string expectedIssuer,
            string expectedAudience,
            out string subject,
            out string error)
        {
            subject = null;
            error   = null;

            if (string.IsNullOrEmpty(jwt))
            {
                error = "JWT is null or empty";
                return false;
            }

            var parts = jwt.Split('.');
            if (parts.Length != 3)
            {
                error = "JWT is not a three-segment compact serialisation";
                return false;
            }

            // ── Header decode ────────────────────────────────────────────────
            // Header decode is unconditional so the alg can be cross-checked
            // against the configured algorithm pin below.
            JwtHeaderDto header = null;
            if (TryDecodeBase64Url(parts[0], out byte[] headerBytes))
            {
                try
                {
                    string headerJson = new UTF8Encoding(
                        encoderShouldEmitUTF8Identifier: false,
                        throwOnInvalidBytes: true).GetString(headerBytes);
                    header = JsonUtility.FromJson<JwtHeaderDto>(headerJson);
                }
                catch (System.Text.DecoderFallbackException) { /* header stays null */ }
                catch (Exception)                            { /* header stays null */ }
            }

            // ── Signature verification (runs before any claim parsing) ──────
            // Secure-by-default fallback: when no NetworkSettings asset is
            // attached we still demand a signed token.  Ed25519 mirrors the
            // serialised default on NetworkSettings so a missing settings
            // asset cannot silently downgrade verification to "None".
            // The SessionAck handler supplies the server's verified identity
            // key when the gateway advertised CapabilityFlags.IdentitySignedJwt
            // (the same key the SDK verified on the Challenge).  It overrides
            // the NetworkSettings algorithm/key and forces Ed25519
            // verification, so a deployment left at the default unverified
            // setting is upgraded — never downgraded — once the gateway
            // advertises identity-signed JWTs.
            bool useIdentityKey =
                _identityVerificationKey != null && _identityVerificationKey.Length > 0;
            var algMode = useIdentityKey
                ? NetworkSettings.JwtSignatureAlgorithm.Ed25519
                : (_settings != null
                    ? _settings.jwtSignatureAlgorithm
                    : NetworkSettings.JwtSignatureAlgorithm.Ed25519);

            // RFC 8725 §3.1: reject `alg=none` (and missing alg) unconditionally,
            // regardless of pin configuration, so a misconfigured verifier cannot
            // accept an unsigned token.
            if (header == null || string.IsNullOrEmpty(header.alg))
            {
                error = GenericSignatureFailure;
                return false;
            }
            if (string.Equals(header.alg, "none", StringComparison.OrdinalIgnoreCase))
            {
                error = GenericSignatureFailure;
                return false;
            }

            if (algMode == NetworkSettings.JwtSignatureAlgorithm.None)
            {
                WarnSignatureUnverifiedOnce();
            }
            else
            {
                if (!TryDecodeBase64Url(parts[2], out byte[] sig))
                {
                    error = GenericSignatureFailure;
                    return false;
                }
                // Signed input is the ASCII bytes of the first two segments
                // joined by '.'. base64url is a strict ASCII subset; the
                // ASCII encoder keeps the byte length deterministic.
                var signedInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);

                bool sigOk;
                try
                {
                    sigOk = JwtSignatureVerifier.Verify(
                        algMode,
                        header.alg,
                        sig,
                        signedInput,
                        useIdentityKey
                            ? ToHex(_identityVerificationKey)
                            : _settings?.jwtSigningKeyHex,
                        useIdentityKey ? null : _settings?.jwtSigningKeyPem,
                        out _);
                }
                catch (Exception)
                {
                    // Defensive catch-all.  The two attacker-controlled inputs
                    // — the raw signature bytes and the signed input — are
                    // delivered to the verifier as plain byte arrays and never
                    // trigger an exception path inside the underlying RSA /
                    // Ed25519 implementations.  The configured public-key
                    // material (hex / PEM) is operator-controlled, so a parse
                    // failure on a malformed key represents a misconfiguration
                    // rather than an attack — the verifier still surfaces a
                    // typed error in that case.  This catch is therefore a
                    // belt-and-braces guard against future verifier changes
                    // that might newly throw on edge-case inputs; the error
                    // payload is collapsed to a single generic message so the
                    // exception type cannot leak information through a side
                    // channel.
                    error = GenericSignatureFailure;
                    return false;
                }
                if (!sigOk)
                {
                    error = GenericSignatureFailure;
                    return false;
                }
            }

            // ── Claim parsing (post-signature) ──────────────────────────────
            if (!TryDecodeBase64Url(parts[1], out byte[] claimBytes))
            {
                error = "JWT claims segment is not valid base64url";
                return false;
            }

            string json;
            try
            {
                json = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(claimBytes);
            }
            catch (System.Text.DecoderFallbackException)
            {
                error = "JWT claims segment is not valid UTF-8";
                return false;
            }

            JwtClaimsDto dto;
            string[] audValues;
            try
            {
                // RFC 7519 §4.1.3 permits `aud` to be either a JSON string OR
                // a JSON array of strings.  Unity's JsonUtility cannot reflect
                // both shapes onto the same field; rewrite an array-shaped
                // `aud` into a single string first so the existing field
                // binds cleanly.  The original array values are surfaced via
                // the `audValues` out-parameter for the membership check
                // below — kept as a local rather than an instance field so
                // a future cross-thread caller cannot observe leaked state
                // between concurrent TryValidate invocations.
                json = NormaliseAudClaim(json, out audValues);
                dto = JsonUtility.FromJson<JwtClaimsDto>(json);
            }
            catch (Exception ex)
            {
                error = $"JWT claims JSON parse failed: {ex.GetType().Name}";
                return false;
            }

            if (dto == null || string.IsNullOrEmpty(dto.sub))
            {
                error = "JWT has no sub claim";
                return false;
            }

            int skew = _settings != null
                ? Math.Max(0, _settings.jwtClockSkewSeconds)
                : 120;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (dto.exp <= 0)
            {
                error = "JWT missing exp claim";
                return false;
            }
            if (now > dto.exp + skew)
            {
                error = $"JWT exp {dto.exp} is in the past (now={now}, skew={skew}s)";
                return false;
            }
            if (dto.nbf != 0 && now + skew < dto.nbf)
            {
                error = $"JWT nbf {dto.nbf} is in the future (now={now}, skew={skew}s)";
                return false;
            }

            if (string.IsNullOrEmpty(expectedIssuer))
            {
                WarnIssuerUnconfiguredOnce();
            }
            else if (!string.Equals(dto.iss, expectedIssuer, StringComparison.Ordinal))
            {
                error = $"JWT iss '{Diagnostics.UntrustedLogText.Sanitise(dto.iss)}' does not match expected issuer";
                return false;
            }

            if (string.IsNullOrEmpty(expectedAudience))
            {
                WarnAudienceUnconfiguredOnce();
            }
            else
            {
                bool audOk;
                if (audValues != null && audValues.Length > 0)
                {
                    // Array-shaped aud: token is valid only if expectedAudience
                    // is a member of the array (RFC 7519 §4.1.3).
                    audOk = false;
                    for (int i = 0; i < audValues.Length; i++)
                    {
                        if (string.Equals(audValues[i], expectedAudience, StringComparison.Ordinal))
                        {
                            audOk = true;
                            break;
                        }
                    }
                }
                else
                {
                    audOk = string.Equals(dto.aud, expectedAudience, StringComparison.Ordinal);
                }

                if (!audOk)
                {
                    // Sanitise the untrusted aud value before embedding it in
                    // a log line: an attacker-supplied JWT could otherwise
                    // inject ANSI escape sequences or forged log delimiters.
                    error = $"JWT aud '{Diagnostics.UntrustedLogText.Sanitise(dto.aud)}' does not match expected audience";
                    return false;
                }
            }

            subject = dto.sub;
            return true;
        }

        // ── Aud claim normalisation ────────────────────────────────────────
        /// <summary>
        /// RFC 7519 §4.1.3 — rewrite a JSON array-shaped <c>aud</c> claim
        /// into a single-string <c>aud</c> so JsonUtility can bind it.
        /// Returns the JSON unchanged when <c>aud</c> is already a string
        /// (or absent).  Internal for unit-test access.
        /// </summary>
        internal static string NormaliseAudClaim(string json, out string[] audValues)
        {
            audValues = null;
            if (string.IsNullOrEmpty(json)) return json;

            // Locate the aud key — only the first occurrence at top level
            // matters; nested objects are not relevant to the JWT envelope.
            int keyIdx = json.IndexOf("\"aud\"", StringComparison.Ordinal);
            if (keyIdx < 0) return json;

            int colonIdx = json.IndexOf(':', keyIdx);
            if (colonIdx < 0) return json;

            int valueStart = colonIdx + 1;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
                valueStart++;
            if (valueStart >= json.Length || json[valueStart] != '[')
                return json; // already a string (or null) — leave unchanged.

            // Find the matching ']' — JWT aud arrays are flat string arrays,
            // but a defensive bracket counter handles any nested literal that
            // a malformed token might present.
            int depth = 0;
            int closeIdx = -1;
            bool inString = false;
            bool escape = false;
            for (int i = valueStart; i < json.Length; i++)
            {
                char c = json[i];
                if (escape)            { escape = false; continue; }
                if (c == '\\')         { escape = true;  continue; }
                if (c == '"')          { inString = !inString; continue; }
                if (inString)          continue;
                if (c == '[')          depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { closeIdx = i; break; }
                }
            }
            if (closeIdx < 0) return json;

            string arraySegment = json.Substring(valueStart, closeIdx - valueStart + 1);

            // Extract every quoted string literal inside the array; ignore
            // non-string elements (token would be rejected downstream
            // because none would match the configured audience).
            var values = new List<string>();
            inString = false;
            escape = false;
            var sb = new StringBuilder();
            for (int i = 0; i < arraySegment.Length; i++)
            {
                char c = arraySegment[i];
                if (escape)
                {
                    if (c == 'n') sb.Append('\n');
                    else if (c == 't') sb.Append('\t');
                    else if (c == 'r') sb.Append('\r');
                    else sb.Append(c);
                    escape = false;
                    continue;
                }
                if (c == '\\' && inString) { escape = true; continue; }
                if (c == '"')
                {
                    if (inString) { values.Add(sb.ToString()); sb.Clear(); inString = false; }
                    else          { inString = true; }
                    continue;
                }
                if (inString) sb.Append(c);
            }

            audValues = values.ToArray();

            // Replace the array with a single string ("" when empty) so
            // JsonUtility binds the legacy `aud` field cleanly.
            string replacement = values.Count > 0
                ? "\"" + values[0].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
                : "\"\"";
            return json.Substring(0, valueStart) + replacement + json.Substring(closeIdx + 1);
        }

        // ── Pure helpers ───────────────────────────────────────────────────
        /// <summary>
        /// Decode a base64url segment (RFC 7515 §2) into raw bytes.  Returns
        /// <see langword="false"/> for malformed padding, characters outside
        /// the base64url alphabet, or zero-length input.
        /// </summary>
        private static bool TryDecodeBase64Url(string input, out byte[] bytes)
        {
            bytes = null;
            if (string.IsNullOrEmpty(input)) return false;

            var b64 = input.Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "=";  break;
                case 0: break;
                default: return false;
            }
            try
            {
                bytes = Convert.FromBase64String(b64);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        // ── One-shot misconfiguration advisories ──────────────────────────
        private static void WarnSignatureUnverifiedOnce()
        {
            if (Interlocked.CompareExchange(ref s_signatureUnverifiedWarned, 1, 0) == 0)
            {
                // LogError (not LogWarning) so CI log scraping that filters
                // on error severity surfaces the unsigned-JWT condition
                // without any extra integrator configuration.  A
                // misconfigured production deployment that ships with the
                // default jwtSignatureAlgorithm = None is a real-world
                // security regression; the previous LogWarning was easy to
                // miss in a noisy build log.
                Debug.LogError(
                    "[RTMPE] NetworkSettings.jwtSignatureAlgorithm is None — " +
                    "SessionAck JWT signatures are NOT being verified. " +
                    "Production deployments MUST configure a JWKS pin " +
                    "(jwtSigningKeyHex for Ed25519 / jwtSigningKeyPem for RS256) " +
                    "so a forged SessionAck cannot install attacker-chosen " +
                    "session_id / reconnect_token / crypto_id values.");
            }
        }

        private static void WarnIssuerUnconfiguredOnce()
        {
            if (Interlocked.CompareExchange(ref s_issuerUnconfiguredWarned, 1, 0) == 0)
            {
                Debug.LogError(
                    "[RTMPE] NetworkSettings.expectedJwtIssuer is empty — " +
                    "SessionAck JWT 'iss' claim is NOT being validated. " +
                    "Production deployments MUST configure expectedJwtIssuer " +
                    "so a token minted for a different tenant cannot be " +
                    "accepted by this client.");
            }
        }

        private static void WarnAudienceUnconfiguredOnce()
        {
            if (Interlocked.CompareExchange(ref s_audienceUnconfiguredWarned, 1, 0) == 0)
            {
                Debug.LogError(
                    "[RTMPE] NetworkSettings.expectedJwtAudience is empty — " +
                    "SessionAck JWT 'aud' claim is NOT being validated. " +
                    "Production deployments MUST configure expectedJwtAudience " +
                    "so a token minted for a different relying party cannot " +
                    "be accepted by this client.");
            }
        }

#if UNITY_INCLUDE_TESTS
        // ── Test seams (internal) ──────────────────────────────────────────
        // Reset the AppDomain-scoped one-shot latches so a fixture can
        // re-observe the warning.  The UNITY_INCLUDE_TESTS gate keeps these
        // entry points out of Player builds: Production code never calls
        // them, and a shipped DLL has no reason to expose mutators on
        // process-wide warning state.
        internal static void ResetSignatureUnverifiedWarningForTests()
            => Interlocked.Exchange(ref s_signatureUnverifiedWarned, 0);

        internal static void ResetIssuerUnconfiguredWarningForTests()
            => Interlocked.Exchange(ref s_issuerUnconfiguredWarned, 0);

        internal static void ResetAudienceUnconfiguredWarningForTests()
            => Interlocked.Exchange(ref s_audienceUnconfiguredWarned, 0);
#endif // UNITY_INCLUDE_TESTS
    }
}
