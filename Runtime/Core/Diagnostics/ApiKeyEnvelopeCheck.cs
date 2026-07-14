// RTMPE SDK — Runtime/Core/Diagnostics/ApiKeyEnvelopeCheck.cs
//
// Single source of truth for "does this configuration carry an API-key
// envelope?" — true when either the sealed-box server public key or the shared
// PSK is present.  Outside the Unity Editor the runtime refuses to send the API
// key unencrypted, so a build with neither credential cannot complete a
// handshake.  The runtime unbound-settings warning and the build-time validator
// both consult this predicate, so the definition of "configured" cannot drift
// between them.  Kept UnityEngine-free so it is exercisable from the headless
// dotnet xunit runner.

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Pure predicate for the presence of an API-key envelope credential.
    /// </summary>
    internal static class ApiKeyEnvelopeCheck
    {
        /// <summary>
        /// Returns <see langword="true"/> when at least one API-key envelope
        /// credential is present.  A hex value is treated as absent when it is
        /// <see langword="null"/>, empty, or only whitespace, so a field left
        /// blank (or wiped to spaces) never counts as configured.
        /// </summary>
        /// <param name="sealServerPublicKeyHex">
        /// The gateway's static X25519 public key for the sealed-box path
        /// (<c>NetworkSettings.apiKeySealServerPublicKeyHex</c>).
        /// </param>
        /// <param name="apiKeyPskHex">
        /// The shared PSK for the legacy encrypted-envelope path
        /// (<c>NetworkSettings.apiKeyPskHex</c>).
        /// </param>
        internal static bool IsConfigured(string sealServerPublicKeyHex, string apiKeyPskHex)
            => !string.IsNullOrWhiteSpace(sealServerPublicKeyHex)
            || !string.IsNullOrWhiteSpace(apiKeyPskHex);
    }
}
