// RTMPE SDK — Runtime/Rooms/MatchmakingResult.cs
//
// Carries the server reply from a MatchmakingResponse (0x2B) packet.

using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Result delivered via <see cref="MatchmakingManager.OnMatchmakingComplete"/>
    /// after the server processes a <c>MatchmakingRequest</c> (0x26).
    /// </summary>
    public sealed class MatchmakingResult
    {
        /// <summary>UUID of the matched or created room.</summary>
        public string RoomId { get; }

        /// <summary>Human-readable 6-character join code for the room.</summary>
        public string RoomCode { get; }

        /// <summary>
        /// <see langword="true"/> when the server created a new room because
        /// no matching waiting room was found.  <see langword="false"/> when an
        /// existing room was found and the player was joined to it.
        /// </summary>
        public bool Created { get; }

        internal MatchmakingResult(string roomId, string roomCode, bool created)
        {
            RoomId   = roomId   ?? string.Empty;
            RoomCode = roomCode ?? string.Empty;
            Created  = created;
        }

        /// <summary>
        /// Diagnostic <see cref="ToString"/> override that redacts the invite
        /// <see cref="RoomCode"/>.  RoomId is retained because it is opaque
        /// (server-generated UUID, useless for unauthorised joins).
        /// </summary>
        public override string ToString()
            => $"MatchmakingResult(roomId={RoomId}, roomCode={LogRedaction.RoomCode(RoomCode)}, created={Created})";
    }
}
