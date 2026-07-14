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

        /// <summary>Maximum players allowed (1–100). Zero = server default (100).</summary>
        public int MaxPlayers { get; set; }

        /// <summary>Whether the room appears in ListRooms results.</summary>
        public bool IsPublic { get; set; } = true;

        /// <summary>
        /// When true (the default), a successful CreateRoom automatically issues
        /// the JoinRoom that seats the creator as the room's host, so creating a
        /// room leaves the caller inside it — the behaviour every mainstream engine
        /// exposes. The server records a player only on JoinRoom, so without this a
        /// freshly created room stays empty ("waiting", zero players) and the host
        /// is never seated. Set false only for the deliberate two-step flow where
        /// the caller drives JoinRoom itself (e.g. creating a room it does not
        /// immediately occupy); then start gameplay from OnRoomJoined, not
        /// OnRoomCreated.
        /// </summary>
        public bool AutoJoinAsHost { get; set; } = true;
    }
}
