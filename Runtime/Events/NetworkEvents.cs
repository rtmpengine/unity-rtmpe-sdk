// RTMPE SDK — Runtime/Events/NetworkEvents.cs
//
// Structured event data types for the RTMPE SDK public event surface.
// All structs are value types to avoid GC pressure on the hot event path.

namespace RTMPE.Events
{
    /// <summary>
    /// Contains all strongly-typed event argument types raised by
    /// <see cref="Core.NetworkManager"/>.
    /// </summary>
    public static class NetworkEvents
    {
        // ── Connection ────────────────────────────────────────────────────────

        /// <summary>Raised when <see cref="Core.NetworkState"/> transitions.</summary>
        public struct StateChangedArgs
        {
            /// <summary>The state the connection was in before the transition.</summary>
            public Core.NetworkState Previous;
            /// <summary>The state the connection is now in.</summary>
            public Core.NetworkState Current;
        }

        /// <summary>Raised when the connection is fully established (<c>SessionAck</c> received).</summary>
        public struct ConnectedArgs
        {
            /// <summary>Server-issued JWT bearer token.</summary>
            public string JwtToken;
            /// <summary>Reconnect token (opaque; store for reconnect attempts).</summary>
            public string ReconnectToken;
            /// <summary>Crypto session ID assigned by the server.</summary>
            public uint CryptoId;
            /// <summary>Round-trip time in milliseconds at the time of connection.</summary>
            public float RttMs;
        }

        // ── Disconnection ─────────────────────────────────────────────────────

        /// <summary>Raised when the connection drops or is closed.</summary>
        public struct DisconnectedArgs
        {
            /// <summary>Reason the connection ended.</summary>
            public Core.DisconnectReason Reason;
        }

        // ── Room (Week 14) ─────────────────────────────────────────────────────

        /// <summary>Raised when the local player successfully joins a room.</summary>
        public struct JoinedRoomArgs
        {
            /// <summary>Server-assigned room identifier.</summary>
            public ulong RoomId;
        }

        /// <summary>Raised when a CreateRoom request succeeds.</summary>
        public struct RoomCreatedArgs
        {
            /// <summary>Info about the newly created room.</summary>
            public Rooms.RoomInfo Room;
        }

        /// <summary>Raised when a JoinRoom/JoinRoomByCode request succeeds.</summary>
        public struct RoomJoinedArgs
        {
            /// <summary>Info about the room joined, including player roster.</summary>
            public Rooms.RoomInfo Room;
        }

        /// <summary>Raised when the local player leaves a room.</summary>
        public struct RoomLeftArgs { }

        /// <summary>Raised when another player joins the current room.</summary>
        public struct PlayerJoinedArgs
        {
            /// <summary>Info about the player who joined.</summary>
            public Rooms.PlayerInfo Player;
        }

        /// <summary>Raised when another player leaves the current room.</summary>
        public struct PlayerLeftArgs
        {
            /// <summary>ID of the player who left.</summary>
            public string PlayerId;
        }

        /// <summary>Raised when a ListRooms response arrives.</summary>
        public struct RoomListReceivedArgs
        {
            /// <summary>Array of available rooms.</summary>
            public Rooms.RoomInfo[] Rooms;
        }

        // ── Heartbeat / RTT ───────────────────────────────────────────────────

        /// <summary>Raised each time a <see cref="Core.PacketType.HeartbeatAck"/> is received.</summary>
        public struct HeartbeatAckArgs
        {
            /// <summary>Round-trip time in milliseconds for this heartbeat.</summary>
            public float RttMs;
        }
    }
}
