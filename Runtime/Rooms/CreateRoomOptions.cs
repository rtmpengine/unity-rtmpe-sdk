// RTMPE SDK — Runtime/Rooms/CreateRoomOptions.cs
//
// Options for the RoomManager.CreateRoom() call.

namespace RTMPE.Rooms
{
    /// <summary>
    /// Options for creating a new room.
    /// All fields have sensible defaults — pass an empty instance for defaults.
    /// </summary>
    public sealed class CreateRoomOptions
    {
        /// <summary>Display name for the room (max 64 chars). Empty = server default.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Maximum players allowed (1–16). Zero = server default (16).</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Whether the room appears in ListRooms results.</summary>
        public bool IsPublic { get; set; } = true;
    }
}
