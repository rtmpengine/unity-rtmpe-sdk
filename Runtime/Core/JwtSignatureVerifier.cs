// RTMPE SDK — Runtime/Core/JwtSignatureVerifier.cs
//
// Free-standing static verifier for SessionAck JWT signatures.  Lives outside
// NetworkManager so headless xUnit harnesses can exercise it without a live
// Unity scene; NetworkManager wraps it with the per-instance settings shim.
//
// Algorithms supported:
//   • EdDSA (Ed25519, RFC 8037)        → 64-byte signature, 32-byte public key
//   • RS256 (PKCS#1 v1.5, SHA-256)     → ≥ 2048-bit RSA, PEM SubjectPublicKeyInfo
//
// `alg` cross-check: the JWS header's alg MUST match the algorithm of the
// configured pin.  An attacker who flips alg from RS256 to EdDSA (or vice
// versa) hoping the verifier picks the weaker primitive is rejected at the
// alg gate, before any signature primitive runs (RFC 8725 §3.1).

using System;
using System.Security.Cryptography;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;

namespace RTMPE.Core
{
    /// <summary>
    /// Static JWT-signature verifier.  Stateless.  All inputs are
    /// attacker-controlled except the pinned key material loaded from
    /// <see cref="NetworkSettings"/>.  Defensive: never throws on
    /// hostile inputs (parse / decode failures are reported via
    /// <paramref name="error"/> and a <see langword="false"/> return).
    /// </summary>
    public static class JwtSignatureVerifier
    {
        /// <summary>
        /// Verify the signature segment of a JWS compact serialization
        /// against the configured pin.
        /// </summary>
        /// <param name="algMode">Pin selected by the integrator.</param>
        /// <param name="headerAlg">Algorithm declared in the JWS header.</param>
        /// <param name="signature">Decoded signature bytes (segment 3, base64url-decoded).</param>
        /// <param name="signedInput">ASCII bytes of segment1 + "." + segment2.</param>
        /// <param name="ed25519PublicKeyHex">
        /// 64-character lowercase hex of the 32-byte Ed25519 public key.
        /// Required when <paramref name="algMode"/> is
        /// <see cref="NetworkSettings.JwtSignatureAlgorithm.Ed25519"/>.
        /// </param>
        /// <param name="rsaPublicKeyPem">
        /// PEM-encoded SubjectPublicKeyInfo.  Required when
        /// <paramref name="algMode"/> is
        /// <see cref="NetworkSettings.JwtSignatureAlgorithm.RsaPkcs1Sha256"/>.
        /// </param>
        /// <param name="error">On failure, a short reason string.  null on success.</param>
        public static bool Verify(
            NetworkSettings.JwtSignatureAlgorithm algMode,
            string headerAlg,
            byte[] signature,
            byte[] signedInput,
            string ed25519PublicKeyHex,
            string rsaPublicKeyPem,
            out string error)
        {
            error = null;
            if (signature == null || signature.Length == 0)
            {
                error = "JWT signature is empty";
                return false;
            }
            if (signedInput == null || signedInput.Length == 0)
            {
                error = "JWT signed-input is empty";
                return false;
            }

            switch (algMode)
            {
                case NetworkSettings.JwtSignatureAlgorithm.Ed25519:
                    return VerifyEd25519(headerAlg, signature, signedInput, ed25519PublicKeyHex, out error);

                case NetworkSettings.JwtSignatureAlgorithm.RsaPkcs1Sha256:
                    return VerifyRs256(headerAlg, signature, signedInput, rsaPublicKeyPem, out error);

                default:
                    error = $"unsupported jwtSignatureAlgorithm: {algMode}";
                    return false;
            }
        }

        private static bool VerifyEd25519(
            string headerAlg,
            byte[] signature,
            byte[] signedInput,
            string keyHex,
            out string error)
        {
            error = null;
            // alg must match exactly: case-sensitive per RFC 7515 §4.1.1.
            if (!string.Equals(headerAlg, "EdDSA", StringComparison.Ordinal))
            {
                error = $"JWT alg '{headerAlg}' does not match pinned EdDSA";
                return false;
            }
            if (string.IsNullOrEmpty(keyHex))
            {
                error = "Ed25519 pin selected but key hex is empty";
                return false;
            }
            byte[] pubKey;
            try { pubKey = ApiKeyCipher.PskFromHex(keyHex); }
            catch (ArgumentException ex)
            {
                error = $"Ed25519 key hex invalid: {ex.Message}";
                return false;
            }
            if (pubKey == null || pubKey.Length != 32)
            {
                error = "Ed25519 public key must be 32 bytes";
                return false;
            }
            if (signature.Length != 64)
            {
                error = $"Ed25519 signature must be 64 bytes, got {signature.Length}";
                return false;
            }
            bool ok = Ed25519Verify.Verify(pubKey, signedInput, signature);
            if (!ok) error = "Ed25519 signature did not verify";
            return ok;
        }

        private static bool VerifyRs256(
            string headerAlg,
            byte[] signature,
            byte[] signedInput,
            string keyPem,
            out string error)
        {
            error = null;
            if (!string.Equals(headerAlg, "RS256", StringComparison.Ordinal))
            {
                error = $"JWT alg '{headerAlg}' does not match pinned RS256";
                return false;
            }
            if (string.IsNullOrEmpty(keyPem))
            {
                error = "RS256 pin selected but key PEM is empty";
                return false;
            }
            using var rsa = RSA.Create();
            try
            {
                ImportRsaPublicKey(rsa, keyPem);
            }
            catch (Exception ex)
            {
                error = $"RSA PEM parse failed: {ex.GetType().Name}";
                return false;
            }
            // Refuse sub-2048-bit moduli — NIST SP 800-131A retires 1024-bit
            // RSA, and 2048 is the documented floor for new deployments.
            if (rsa.KeySize < 2048)
            {
                error = $"RSA modulus {rsa.KeySize} bits is below the 2048-bit floor";
                return false;
            }
            bool ok = rsa.VerifyData(
                signedInput, signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            if (!ok) error = "RS256 signature did not verify";
            return ok;
        }

        // Import an RSA SubjectPublicKeyInfo ("PUBLIC KEY") PEM into rsa.
        // RSA.ImportFromPem is .NET 5+ and is absent from the .NET Standard 2.1
        // BCL surface (Unity's declared minimum, 2022.3).  The fallback strips
        // the PEM armor and base64-decodes the body to DER, then delegates the
        // SubjectPublicKeyInfo parse to RSA.ImportSubjectPublicKeyInfo — a vetted
        // BCL method that IS available on netstandard2.1 — so no hand-rolled
        // ASN.1 is introduced (SDKC-01).
        private static void ImportRsaPublicKey(RSA rsa, string keyPem)
        {
#if NET5_0_OR_GREATER
            rsa.ImportFromPem(keyPem.AsSpan());
#else
            rsa.ImportSubjectPublicKeyInfo(DecodeSpkiPem(keyPem), out _);
#endif
        }

        /// <summary>
        /// Decode a SubjectPublicKeyInfo ("-----BEGIN PUBLIC KEY-----") PEM to
        /// its DER bytes: strip the armor lines and all whitespace, then
        /// base64-decode.  This mirrors exactly what RSA.ImportFromPem does for
        /// the "PUBLIC KEY" label; the structural parse is left to the BCL.
        /// Always compiled so it is unit-tested on .NET 5+ even though only the
        /// netstandard2.1 build path calls it.  Throws on a non-SPKI or
        /// malformed block so VerifyRs256's catch surfaces a parse failure.
        /// </summary>
        internal static byte[] DecodeSpkiPem(string keyPem)
        {
            if (keyPem == null) throw new ArgumentNullException(nameof(keyPem));
            const string begin = "-----BEGIN PUBLIC KEY-----";
            const string end = "-----END PUBLIC KEY-----";
            int b = keyPem.IndexOf(begin, StringComparison.Ordinal);
            int e = keyPem.IndexOf(end, StringComparison.Ordinal);
            if (b < 0 || e < 0 || e <= b)
                throw new FormatException("PEM is not a SubjectPublicKeyInfo (\"PUBLIC KEY\") block");
            string body = keyPem.Substring(b + begin.Length, e - (b + begin.Length));
            var sb = new System.Text.StringBuilder(body.Length);
            foreach (char ch in body)
                if (!char.IsWhiteSpace(ch)) sb.Append(ch);
            return Convert.FromBase64String(sb.ToString());
        }
    }
}
