// RTMPE SDK — Runtime/Core/NetworkSettings.cs
//
// Project-wide RTMPE connection settings stored as a ScriptableObject asset.
//
// Create via: Assets > Create > RTMPE > Settings
// Then assign the created asset to the NetworkManager's "Settings" field in the Inspector.
//
// If no asset is assigned at runtime, NetworkManager.Awake() calls
// NetworkSettings.CreateDefault() to produce a runtime-only instance with the
// defaults defined here — zero configuration needed for local development.

using UnityEngine;

namespace RTMPE.Core
{
    /// <summary>
    /// Project-wide RTMPE connection settings.
    /// Store as a <see cref="ScriptableObject"/> asset. You can maintain multiple
    /// profiles (e.g. <c>RTMPESettings_Dev.asset</c>, <c>RTMPESettings_Prod.asset</c>)
    /// and swap them in the <see cref="NetworkManager"/> Inspector field.
    /// </summary>
    [CreateAssetMenu(
        fileName = "RTMPESettings",
        menuName  = "RTMPE/Settings",
        order     = 1)]
    public sealed class NetworkSettings : ScriptableObject
    {
        // ── Server ──────────────────────────────────────────────────────────────

        [Header("Server")]
        [Tooltip("RTMPE gateway hostname or IP address.")]
        public string serverHost = "127.0.0.1";

        [Tooltip("UDP port the RTMPE gateway listens on (default: 7777).")]
        [Range(1, 65535)]
        public int serverPort = 7777;

        // ── Timing ──────────────────────────────────────────────────────────────

        [Header("Timing")]
        [Tooltip("Interval in milliseconds between keepalive heartbeat packets.")]
        [Range(100, 60_000)]
        public int heartbeatIntervalMs = 5_000;

        [Tooltip("Maximum time in milliseconds to wait for the initial handshake to complete.")]
        [Range(1_000, 60_000)]
        public int connectionTimeoutMs = 10_000;

        [Tooltip("Expected server tick rate in Hz. Must match the room-service configuration.")]
        [Range(1, 128)]
        public int tickRate = 30;

        // ── Buffers ─────────────────────────────────────────────────────────────

        [Header("Buffers")]
        [Tooltip("UDP socket SO_SNDBUF size in bytes.")]
        [Range(1_024, 65_536)]
        public int sendBufferBytes = 4_096;

        [Tooltip("UDP socket SO_RCVBUF size in bytes.")]
        [Range(1_024, 65_536)]
        public int receiveBufferBytes = 4_096;

        [Tooltip("Size of the scratch buffer used by the network thread for reading incoming datagrams.")]
        [Range(1_024, 65_536)]
        public int networkThreadBufferBytes = 8_192;

        // ── Debug ────────────────────────────────────────────────────────────────

        [Header("Debug")]
        [Tooltip("Log verbose NetworkManager state transitions to the Unity Console.")]
        public bool enableDebugLogs;

        // ── Crypto ─────────────────────────────────────────────────────────────

        [Header("Crypto")]
        [Tooltip(
            "64-character lowercase hex string encoding the 32-byte pre-shared key used to " +
            "encrypt the API key in HandshakeInit packets.\n\n" +
            "Copy this value from the RTMPE developer dashboard. " +
            "It must match GATEWAY_API_KEY_ENCRYPTION_KEY_HEX on the server.\n\n" +
            "Leave blank to disable API-key encryption (insecure — dev/local only).")]
        public string apiKeyPskHex = "";

        [Tooltip(
            "Optional: 64-character lowercase hex string of the 32-byte Ed25519 static public key " +
            "of the gateway you expect to connect to (H4 server pinning).\n\n" +
            "Copy from the gateway startup log or developer dashboard. " +
            "Leaving blank skips pinning and trusts any valid Ed25519 signature.")]
        public string pinnedServerPublicKeyHex = "";

        // ── Derived ──────────────────────────────────────────────────────────────

        /// <summary>Tick interval in seconds (<c>1 / tickRate</c>).</summary>
        public float TickInterval => 1f / Mathf.Max(1, tickRate);

        /// <summary>
        /// Decode <see cref="apiKeyPskHex"/> to a 32-byte array, or return
        /// <see langword="null"/> if the field is empty (insecure dev path).
        /// Throws <see cref="System.ArgumentException"/> if the value is non-empty but invalid.
        /// </summary>
        public byte[] ApiKeyPskBytes =>
            string.IsNullOrEmpty(apiKeyPskHex) ? null : Crypto.ApiKeyCipher.PskFromHex(apiKeyPskHex);

        /// <summary>
        /// Decode <see cref="pinnedServerPublicKeyHex"/> to 32 bytes, or return
        /// <see langword="null"/> if pinning is not configured.
        /// Throws <see cref="System.ArgumentException"/> if the value is non-empty but invalid.
        /// </summary>
        public byte[] PinnedServerPublicKeyBytes
        {
            get
            {
                if (string.IsNullOrEmpty(pinnedServerPublicKeyHex)) return null;
                return Crypto.ApiKeyCipher.PskFromHex(pinnedServerPublicKeyHex);
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Create a runtime-only instance with factory-default values.
        /// Used by <see cref="NetworkManager"/> when no settings asset is assigned.
        /// Not saved to disk; garbage-collected when the manager is destroyed.
        /// </summary>
        internal static NetworkSettings CreateDefault()
        {
            var s = CreateInstance<NetworkSettings>();
            s.name = "RTMPESettings (runtime default)";
            return s;
        }
    }
}
