// RTMPE SDK — Runtime/Rooms/PlayerInfo.cs
//
// Immutable snapshot of a player's lobby-visible state.
// Mirrors the PlayerInfo message in modules/room/interface/grpc/room.proto.

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

        public PlayerInfo(string playerId, string displayName, bool isHost, bool isReady)
        {
            PlayerId    = playerId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsHost      = isHost;
            IsReady     = isReady;
        }

        public override string ToString()
            => $"Player({PlayerId}, \"{DisplayName}\", host={IsHost}, ready={IsReady})";
    }
}
