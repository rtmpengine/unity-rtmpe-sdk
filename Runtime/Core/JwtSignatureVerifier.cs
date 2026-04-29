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
                rsa.ImportFromPem(keyPem.AsSpan());
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
    }
}
