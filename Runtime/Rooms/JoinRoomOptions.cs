// RTMPE SDK — Runtime/Rooms/JoinRoomOptions.cs
//
// Options for the RoomManager.JoinRoom() / JoinRoomByCode() calls.

namespace RTMPE.Rooms
{
    /// <summary>
    /// Options for joining an existing room.
    /// All fields have sensible defaults — pass an empty instance for defaults.
    /// </summary>
    public sealed class JoinRoomOptions
    {
        /// <summary>Display name visible to other players in the room (max 32 chars).</summary>
        public string DisplayName { get; set; } = string.Empty;
    }
}
