// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyQueryOptions.cs
//
// Options for LobbyManager.ListRooms — controls sort order, filters,
// and result cap.  Mirrors the server-side LobbyListPayload JSON.

using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Sort order for <see cref="LobbyManager.ListRooms"/>.
    /// Values MUST match <c>LobbySort</c> in <c>modules/room/domain/ports/room_repository.go</c>.
    /// </summary>
    public enum LobbySort : byte
    {
        /// <summary>Fullest rooms first (most common matchmaking use case).</summary>
        PlayerCount = 0,
        /// <summary>Oldest rooms first.</summary>
        Age         = 1,
        /// <summary>Alphabetical by room name.</summary>
        Name        = 2,
    }

    /// <summary>
    /// Comparison operator for a <see cref="LobbyFilter"/>.
    /// Values MUST match <c>LobbyFilterOp</c> in <c>modules/room/domain/ports/room_repository.go</c>.
    /// </summary>
    public enum LobbyFilterOp : byte
    {
        Eq    = 0,
        NotEq = 1,
        Lt    = 2,
        Gt    = 3,
        LtEq  = 4,
        GtEq  = 5,
    }

    /// <summary>
    /// A single property filter applied server-side to the room list.
    /// Only rooms whose <c>CustomProperties[Key]</c> satisfies the operator
    /// comparison against <see cref="Value"/> are included.
    /// </summary>
    public sealed class LobbyFilter
    {
        /// <summary>CustomProperties key to filter on (max 32 bytes).</summary>
        public string       Key   { get; set; }
        /// <summary>Comparison operator.</summary>
        public LobbyFilterOp Op   { get; set; }
        /// <summary>Comparison target.  Must be a string, int, float, or bool.</summary>
        public object       Value { get; set; }
    }

    /// <summary>
    /// Options for <see cref="LobbyManager.ListRooms"/>.
    /// </summary>
    public sealed class LobbyQueryOptions
    {
        /// <summary>
        /// Name of the lobby to query.
        /// Empty string (default) means the Default lobby.
        /// </summary>
        public string LobbyName { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of rooms to return (1–100; 0 = server default = 100).
        /// </summary>
        public int MaxResults { get; set; } = 0;

        /// <summary>Sort order for the result set (default = PlayerCount desc).</summary>
        public LobbySort SortBy { get; set; } = LobbySort.PlayerCount;

        /// <summary>
        /// Optional server-side property filters.
        /// All filters must be satisfied for a room to appear in results.
        /// Null or empty = no filter (all matching rooms returned).
        /// </summary>
        public List<LobbyFilter> Filters { get; set; }
    }
}
