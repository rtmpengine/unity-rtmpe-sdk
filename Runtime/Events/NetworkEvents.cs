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

        // ── Room ──────────────────────────────────────────────────────────────

        /// <summary>Raised when the local player successfully joins a room.</summary>
        public struct JoinedRoomArgs
        {
            /// <summary>Server-assigned room identifier.</summary>
            public ulong RoomId;
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
