// RTMPE SDK — Runtime/Rooms/RoomInfo.cs
//
// Immutable snapshot of a room's state.
// Mirrors the GetRoomResponse / RoomSummary messages in room.proto.

using System;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Read-only snapshot of a room.
    /// Populated from CreateRoom response, JoinRoom response, or ListRooms.
    /// </summary>
    public sealed class RoomInfo
    {
        /// <summary>Server-assigned room UUID.</summary>
        public string RoomId { get; }

        /// <summary>6-character human-readable join code (e.g. "XKCD42").</summary>
        public string RoomCode { get; }

        /// <summary>Display name of the room.</summary>
        public string Name { get; }

        /// <summary>Room lifecycle state: "waiting", "playing", or "finished".</summary>
        public string State { get; }

        /// <summary>Current number of players in the room.</summary>
        public int PlayerCount { get; }

        /// <summary>Maximum allowed players (1–16).</summary>
        public int MaxPlayers { get; }

        /// <summary>Whether the room is publicly listed.</summary>
        public bool IsPublic { get; }

        /// <summary>Player roster snapshot. May be empty for list responses.</summary>
        public PlayerInfo[] Players { get; }

        public RoomInfo(
            string roomId,
            string roomCode,
            string name,
            string state,
            int    playerCount,
            int    maxPlayers,
            bool   isPublic,
            PlayerInfo[] players = null)
        {
            RoomId      = roomId ?? string.Empty;
            RoomCode    = roomCode ?? string.Empty;
            Name        = name ?? string.Empty;
            State       = state ?? string.Empty;
            PlayerCount = playerCount;
            MaxPlayers  = maxPlayers;
            IsPublic    = isPublic;
            Players     = players ?? Array.Empty<PlayerInfo>();
        }

        public override string ToString()
            => $"Room({RoomId}, \"{Name}\", {PlayerCount}/{MaxPlayers}, {State})";
    }
}
