// RTMPE SDK — Runtime/Rooms/PlayerInfo.cs
//
// Immutable snapshot of a player's lobby-visible state.
// Mirrors the PlayerInfo message in modules/room/interface/grpc/room.proto.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Read-only snapshot of a player in a room.
    /// Populated from RoomJoin response and PlayerJoined notifications.
    /// </summary>
    public sealed class PlayerInfo
    {
        /// <summary>Server-assigned player identifier (UUID string).</summary>
        public string PlayerId { get; }

        /// <summary>Human-readable display name (max 32 chars).</summary>
        public string DisplayName { get; }

        /// <summary>True if this player is the room host.</summary>
        public bool IsHost { get; }

        /// <summary>True if the player has signalled ready state.</summary>
        public bool IsReady { get; }

        /// <summary>
        /// The player's <c>custom_properties</c> JSONB column mirrored as a
        /// read-only map.  Always non-null.
        /// </summary>
        public IReadOnlyDictionary<string, PropertyValue> Properties { get; }

        /// <summary>
        /// Monotonic per-player version counter advanced by the server on
        /// every accepted <c>PlayerPropertyUpdate</c>.
        /// </summary>
        public int PropertiesVersion { get; }

        public PlayerInfo(
            string playerId,
            string displayName,
            bool   isHost,
            bool   isReady,
            IReadOnlyDictionary<string, PropertyValue> properties = null,
            int    propertiesVersion = 0)
        {
            PlayerId          = playerId ?? string.Empty;
            DisplayName       = displayName ?? string.Empty;
            IsHost            = isHost;
            IsReady           = isReady;
            // Defensive copy — see [RoomInfo.FreezeProperties] for rationale.
            Properties        = RoomInfo.FreezeProperties(properties);
            PropertiesVersion = propertiesVersion;
        }

        /// <summary>
        /// Returns a new <see cref="PlayerInfo"/> identical to this one but
        /// with the supplied <paramref name="properties"/> and
        /// <paramref name="version"/>.
        /// </summary>
        public PlayerInfo WithProperties(IReadOnlyDictionary<string, PropertyValue> properties, int version)
            => new PlayerInfo(PlayerId, DisplayName, IsHost, IsReady, properties, version);

        /// <summary>
        /// Returns a new <see cref="PlayerInfo"/> identical to this one but
        /// with the <see cref="IsHost"/> flag replaced.  Used by the room
        /// manager to rehost a roster entry after a
        /// <c>master_client_changed</c> broadcast without losing the player's
        /// other state.
        /// </summary>
        public PlayerInfo WithIsHost(bool isHost)
            => new PlayerInfo(PlayerId, DisplayName, isHost, IsReady, Properties, PropertiesVersion);

        private static readonly IReadOnlyDictionary<string, PropertyValue> EmptyProperties
            = new Dictionary<string, PropertyValue>(0);

        public override string ToString()
            => $"Player({PlayerId}, \"{DisplayName}\", host={IsHost}, ready={IsReady})";
    }
}
