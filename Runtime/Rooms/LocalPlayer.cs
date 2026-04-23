// RTMPE SDK — Runtime/Rooms/LocalPlayer.cs
//
// Convenience wrapper around RoomManager.SetPlayerProperties that scopes
// the operation to the local player (the authenticated session's owner).
// Matches the Photon-style `NetworkManager.LocalPlayer.SetProperty(...)` API
// so the SDK reads naturally for developers migrating from Photon PUN.
//
// Obtain an instance via `NetworkManager.LocalPlayer` — constructed lazily
// by NetworkManager once the session is established (LocalPlayerStringId
// is non-empty).  Calls made before a session exists log an error and
// become no-ops.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Local-player façade over <see cref="RoomManager.SetPlayerProperties"/>.
    /// Provides the Photon-compatible API shape
    /// <c>NetworkManager.LocalPlayer.SetProperty(key, value)</c>.
    /// </summary>
    public sealed class LocalPlayerContext
    {
        private readonly RoomManager     _rooms;
        private readonly Func<string>    _getLocalPlayerId;

        internal LocalPlayerContext(RoomManager rooms, Func<string> getLocalPlayerId)
        {
            _rooms            = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _getLocalPlayerId = getLocalPlayerId ?? throw new ArgumentNullException(nameof(getLocalPlayerId));
        }

        /// <summary>
        /// The authenticated local player's UUID, or an empty string when
        /// no session is established yet.  Prefer this over
        /// <c>NetworkManager.LocalPlayerId</c> (u64) when a string identifier
        /// is required (e.g. UI display, logs, protocol payloads).
        /// </summary>
        public string PlayerId => _getLocalPlayerId() ?? string.Empty;

        /// <summary>
        /// Set a single custom property for the local player.  The result
        /// arrives asynchronously via
        /// <see cref="RoomManager.OnPlayerPropertiesChanged"/> once the server
        /// has accepted and broadcast the change.
        /// </summary>
        public void SetProperty(string key, PropertyValue value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key must not be null or empty.", nameof(key));

            string playerId = PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperty: no authenticated session yet — call after handshake completes.");
                return;
            }

            _rooms.SetPlayerProperties(
                playerId,
                new Dictionary<string, PropertyValue> { { key, value } });
        }

        /// <summary>
        /// Set multiple custom properties for the local player in a single
        /// packet.  Preferred over repeated <see cref="SetProperty"/> calls
        /// when updating several keys at once — fewer version-conflict
        /// opportunities and one broadcast event instead of N.
        /// </summary>
        public void SetProperties(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            string playerId = PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                Debug.LogError(
                    "[RTMPE] LocalPlayer.SetProperties: no authenticated session yet — call after handshake completes.");
                return;
            }
            _rooms.SetPlayerProperties(playerId, properties);
        }
    }
}
