// RTMPE SDK — Runtime/Crypto/PlayerPrefsPinStore.cs
//
// Default IServerKeyPinStore backed by UnityEngine.PlayerPrefs.
//
// Each pin is stored as a 64-character lowercase hex string under the key
// "RTMPE.ServerPin.<host>:<port>".  PlayerPrefs is platform-appropriate
// (Windows registry, macOS plist, Android SharedPreferences, iOS user
// defaults, WebGL IndexedDB) and is the only cross-platform persistent
// store Unity exposes without additional packages.
//
// Pins are NOT confidential — they are the server's PUBLIC key — so the
// modest tamper resistance of PlayerPrefs is acceptable.  An attacker who
// can write to PlayerPrefs can also write to the application binary, which
// is a strictly broader compromise.

using UnityEngine;

namespace RTMPE.Crypto
{
    // Storage backend abstraction.  PlayerPrefs is a static Unity API and
    // therefore cannot be exercised under xUnit without an Editor process;
    // the interface lets us substitute an in-memory implementation in unit
    // tests while production builds keep the zero-overhead PlayerPrefs path.
    // Internal so the SDK's public surface (constructor / interface) is
    // unchanged for game code.
    internal interface IPersistentKeyValueStore
    {
        string GetString(string key, string defaultValue);
        void   SetString(string key, string value);
        void   DeleteKey(string key);
        void   Save();
    }

    // Default backend forwarding to UnityEngine.PlayerPrefs.  Kept private
    // to PlayerPrefsPinStore so the abstraction is invisible to callers.
    internal sealed class UnityPlayerPrefsStore : IPersistentKeyValueStore
    {
        public string GetString(string key, string defaultValue) => PlayerPrefs.GetString(key, defaultValue);
        public void   SetString(string key, string value)        => PlayerPrefs.SetString(key, value);
        public void   DeleteKey(string key)                      => PlayerPrefs.DeleteKey(key);
        public void   Save()                                     => PlayerPrefs.Save();
    }

    /// <summary>
    /// Default <see cref="IServerKeyPinStore"/> implementation that persists
    /// pins via <see cref="UnityEngine.PlayerPrefs"/>.  Used by the SDK when
    /// no custom store has been injected.
    /// </summary>
    public sealed class PlayerPrefsPinStore : IServerKeyPinStore
    {
        // Namespaced prefix avoids accidental collision with game-app keys.
        // Changing this prefix would invalidate every pin in existing
        // installs — treat it as a wire-format constant.
        internal const string KeyPrefix = "RTMPE.ServerPin.";

        private readonly IPersistentKeyValueStore _backend;

        /// <summary>
        /// Construct the default Unity-backed pin store.  Production callers
        /// use this — pins are persisted to platform-appropriate storage via
        /// <see cref="UnityEngine.PlayerPrefs"/>.
        /// </summary>
        public PlayerPrefsPinStore() : this(new UnityPlayerPrefsStore()) { }

        // Test seam.  An in-memory <see cref="IPersistentKeyValueStore"/>
        // exercises the storage path without an active Unity Editor process,
        // which the static PlayerPrefs API requires.  Internal so external
        // callers cannot accidentally bypass the platform-appropriate
        // backend on a shipped build.
        internal PlayerPrefsPinStore(IPersistentKeyValueStore backend)
        {
            _backend = backend ?? new UnityPlayerPrefsStore();
        }

        public byte[] Load(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;
            string hex = _backend.GetString(KeyPrefix + endpoint, string.Empty);
            if (string.IsNullOrEmpty(hex)) return null;

            // PskFromHex throws on malformed input; we treat a corrupt pin as
            // "no pin" rather than blowing up the connection path.  A corrupt
            // pin still fails the comparison anyway (since strict mode then
            // refuses with no pin), so failing soft here loses no security.
            try { return ApiKeyCipher.PskFromHex(hex); }
            catch { return null; }
        }

        public void Save(string endpoint, byte[] pin)
        {
            if (string.IsNullOrEmpty(endpoint) || pin == null || pin.Length != 32) return;

            // Hex-encode for human inspection in the OS-level prefs store.
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < 32; i++) sb.Append(pin[i].ToString("x2"));
            string nextHex = sb.ToString();

            // Detect pin replacement against the existing entry.  TOFU policy
            // captures the pin on the very first connect, so any subsequent
            // change is either a legitimate operator rotation (a fresh key
            // pair was deployed) or a tampering attempt (an attacker with
            // PlayerPrefs write access swapped in a key they control).  The
            // SDK has no out-of-band signal to distinguish those two cases,
            // but surfacing the transition via a structured warning gives
            // ops a forensics trail that a silent overwrite would not.
            string priorHex = _backend.GetString(KeyPrefix + endpoint, string.Empty);
            if (!string.IsNullOrEmpty(priorHex) &&
                !string.Equals(priorHex, nextHex, System.StringComparison.OrdinalIgnoreCase))
            {
                Debug.LogWarning(
                    $"[RTMPE] PlayerPrefsPinStore: replacing existing pin for endpoint {endpoint}. " +
                    "This is expected after an operator-driven key rotation; an unexpected change " +
                    "indicates either a re-installation or unauthorised modification of local " +
                    "preferences.  Verify the new pin against the server's published key out of band.");
            }

            _backend.SetString(KeyPrefix + endpoint, nextHex);
            // Save() is required on iOS/Android to flush before app suspend;
            // skipping it would lose the pin if the OS killed the process
            // immediately after the first handshake.
            _backend.Save();
        }

        public void Clear(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            _backend.DeleteKey(KeyPrefix + endpoint);
            _backend.Save();
        }
    }
}
