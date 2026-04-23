// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyInfo.cs
//
// Immutable snapshot of a single room as returned by the lobby system
// (LobbyJoin 0x27 reply and LobbyRoomListUpdate 0x2A push).

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Snapshot of a single room in a lobby listing.
    /// Fields mirror the JSON object the server sends in NatsReply.Data.
    /// </summary>
    public sealed class LobbyRoomInfo
    {
        /// <summary>Unique room identifier (UUID v4).</summary>
        public string RoomId       { get; }
        /// <summary>Short human-readable join code (e.g. "ABC4H2").</summary>
        public string RoomCode     { get; }
        /// <summary>Display name of the room.</summary>
        public string Name         { get; }
        /// <summary>Number of players currently in the room.</summary>
        public int    PlayerCount  { get; }
        /// <summary>Maximum players allowed in the room.</summary>
        public int    MaxPlayers   { get; }
        /// <summary>Whether the room appears in public listings.</summary>
        public bool   IsPublic     { get; }
        /// <summary>Lobby namespace this room belongs to (empty = Default lobby).</summary>
        public string LobbyName    { get; }

        public LobbyRoomInfo(
            string roomId,
            string roomCode,
            string name,
            int    playerCount,
            int    maxPlayers,
            bool   isPublic,
            string lobbyName)
        {
            RoomId      = roomId      ?? string.Empty;
            RoomCode    = roomCode    ?? string.Empty;
            Name        = name        ?? string.Empty;
            PlayerCount = playerCount;
            MaxPlayers  = maxPlayers;
            IsPublic    = isPublic;
            LobbyName   = lobbyName   ?? string.Empty;
        }
    }
}
