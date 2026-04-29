// RTMPE SDK — Runtime/Crypto/IServerKeyPinStore.cs
//
// Persistent storage abstraction for server static-public-key pins.
//
// A pin is keyed by the canonical "host:port" string of the gateway endpoint.
// The store is consulted on every Challenge to enforce strict equality, and
// is written exactly once per endpoint after a TOFU first-connect succeeds.
//
// Default implementation: PlayerPrefsPinStore (UnityEngine.PlayerPrefs).
// Test code injects an in-memory implementation via NetworkManager.

using System;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Per-endpoint persistent pin storage for the server's 32-byte Ed25519
    /// static public key.  Implementations MUST be safe to call from the Unity
    /// main thread; they are not required to be thread-safe across threads.
    /// </summary>
    public interface IServerKeyPinStore
    {
        /// <summary>
        /// Return the previously persisted 32-byte pin for the given
        /// endpoint, or <see langword="null"/> if no pin has been recorded.
        /// </summary>
        /// <param name="endpoint">
        /// Canonical "host:port" string.  Must be exactly the form produced by
        /// <see cref="ServerKeyPinning.CanonicalEndpoint"/>; consumers MUST
        /// not normalise further (e.g. DNS resolution) — pins are bound to
        /// the user-visible address so that a hostile resolver cannot silently
        /// substitute an attacker-controlled IP under the same pin.
        /// </param>
        byte[] Load(string endpoint);

        /// <summary>
        /// Persist the given 32-byte pin for the endpoint, overwriting any
        /// prior value.  Implementations should call this only after the
        /// caller has cryptographically verified the key (transcript + Ed25519
        /// signature) — writing an unverified key would be an attack vector.
        /// </summary>
        void Save(string endpoint, byte[] pin);

        /// <summary>
        /// Remove any persisted pin for the endpoint.  No-op when no pin is
        /// present.  Used by <see cref="Core.NetworkManager.ClearPinnedKey"/>
        /// to support legitimate server rotation flows.
        /// </summary>
        void Clear(string endpoint);
    }
}
