// RTMPE SDK — Runtime/Rooms/MatchmadePlayer.cs
//
// Lightweight roster entry carried by a matchmaking reply.

namespace RTMPE.Rooms
{
    /// <summary>
    /// One occupant of a room as reported by a matchmaking reply: the minimal
    /// facts the server seats in the matchmaking transaction (id, display name,
    /// host flag, ready flag).  Deliberately distinct from <see cref="PlayerInfo"/>
    /// — a matchmaking reply carries no custom properties or version counter, so
    /// the matchmaking layer stays decoupled from that machinery and the
    /// <see cref="RoomManager"/> promotes these entries to full
    /// <see cref="PlayerInfo"/> snapshots when it adopts the room.
    /// </summary>
    internal readonly struct MatchmadePlayer
    {
        /// <summary>Server-assigned player identifier (UUID string).</summary>
        public readonly string PlayerId;

        /// <summary>Human-readable display name; empty when none was set.</summary>
        public readonly string DisplayName;

        /// <summary>True when this occupant is the room host.</summary>
        public readonly bool IsHost;

        /// <summary>True when this occupant has signalled ready state.</summary>
        public readonly bool IsReady;

        public MatchmadePlayer(string playerId, string displayName, bool isHost, bool isReady)
        {
            PlayerId    = playerId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            IsHost      = isHost;
            IsReady     = isReady;
        }
    }
}
