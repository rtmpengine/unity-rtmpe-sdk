// RTMPE SDK — Runtime/Rooms/RoomPacketParser.cs
//
// Parses inbound room-related packet payloads (0x20–0x23).
// These methods operate on the raw payload AFTER the 13-byte header has been
// stripped by PacketParser.ExtractPayload().
//
// Wire formats (all little-endian):
//
// ── RoomCreate Response (0x20) Server → Client ─────────────────────────────
//   [ok:1]
//   if ok=1: [room_id_len:2 LE][room_id:N][room_code_len:2 LE][room_code:N][max_players:1]
//            [local_player_id_len:2 LE][local_player_id:N]   ← appended (v3.1+)
//   if ok=0: [error_len:2 LE][error:N]
//
// ── RoomJoin Response (0x21, msg_kind=0x00) Server → Client ────────────────
//   [msg_kind:1=0x00][ok:1]
//   if ok=1: [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//            [name_len:2][name:N][player_count:1][max_players:1][is_public:1]
//            for each player:
//              [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//              [is_host:1][is_ready:1]
//            [local_player_id_len:2][local_player_id:N]       ← appended (v3.1+)
//   if ok=0: [error_len:2][error:N]
//
// ── PlayerJoined Notification (0x21, msg_kind=0x01) Server → Client ────────
//   [msg_kind:1=0x01]
//   [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//   [is_host:1][is_ready:1]
//
// ── RoomLeave Response (0x22, msg_kind=0x00) Server → Client ───────────────
//   [msg_kind:1=0x00][ok:1]
//
// ── PlayerLeft Notification (0x22, msg_kind=0x01) Server → Client ──────────
//   [msg_kind:1=0x01][player_id_len:2][player_id:N]
//
// ── RoomList Response (0x23) Server → Client ───────────────────────────────
//   [room_count:2 LE]
//   for each room:
//     [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//     [name_len:2][name:N][state_len:2][state:N]
//     [player_count:1][max_players:1][is_public:1]

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Message kind discriminator — first byte of RoomJoin (0x21) and RoomLeave (0x22) payloads.
    /// </summary>
    internal static class RoomMsgKind
    {
        internal const byte Response     = 0x00;
        internal const byte Notification = 0x01;
    }

    /// <summary>
    /// Parses inbound room packet payloads into typed structures.
    /// All methods are static and allocation-minimal where feasible.
    /// </summary>
    public static class RoomPacketParser
    {
        // ── CreateRoom Response (0x20) ─────────────────────────────────────────

        /// <summary>
        /// Parse a <c>RoomCreate</c> (0x20) response payload.
        /// </summary>
        /// <returns>True if the payload is well-formed.</returns>
        public static bool ParseCreateRoomResponse(
            byte[] payload,
            out bool      ok,
            out string    roomId,
            out string    roomCode,
            out int       maxPlayers,
            out string    error)
        {
            return ParseCreateRoomResponse(
                payload, out ok, out roomId, out roomCode,
                out maxPlayers, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomCreate</c> (0x20) response payload, also extracting the
        /// local player's room UUID appended by the server (v3.1+ protocol).
        /// <paramref name="localPlayerId"/> is empty string when the server is pre-v3.1
        /// and did not include the field.
        /// </summary>
        internal static bool ParseCreateRoomResponse(
            byte[] payload,
            out bool      ok,
            out string    roomId,
            out string    roomCode,
            out int       maxPlayers,
            out string    localPlayerId,
            out string    error)
        {
            ok            = false;
            roomId        = null;
            roomCode      = null;
            maxPlayers    = 0;
            localPlayerId = string.Empty;
            error         = null;

            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            ok = payload[offset++] != 0;

            if (ok)
            {
                // [room_id_len:2][room_id:N][room_code_len:2][room_code:N][max_players:1]
                if (!TryReadString(payload, ref offset, out roomId))   return false;
                if (!TryReadString(payload, ref offset, out roomCode)) return false;
                if (offset >= payload.Length)                          return false;
                maxPlayers = payload[offset++];

                // [local_player_id_len:2][local_player_id:N]  — v3.1+ optional field
                // Gracefully omit when server doesn't send it (old gateway).
                if (offset < payload.Length)
                    TryReadString(payload, ref offset, out localPlayerId);

                return true;
            }
            else
            {
                // [error_len:2][error:N]
                return TryReadString(payload, ref offset, out error);
            }
        }

        // ── RoomJoin (0x21) — Response or Notification ─────────────────────────

        /// <summary>
        /// Read the <c>msg_kind</c> byte from a <c>RoomJoin</c> (0x21) payload.
        /// Returns <see cref="RoomMsgKind.Response"/> (0x00) or
        /// <see cref="RoomMsgKind.Notification"/> (0x01).
        /// </summary>
        public static bool TryGetJoinMsgKind(byte[] payload, out byte msgKind)
        {
            msgKind = 0;
            if (payload == null || payload.Length < 1) return false;
            msgKind = payload[0];
            return msgKind == RoomMsgKind.Response || msgKind == RoomMsgKind.Notification;
        }

        /// <summary>
        /// Parse a <c>RoomJoin</c> (0x21) <b>response</b> payload (msg_kind=0x00).
        /// </summary>
        public static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     error)
        {
            return ParseJoinRoomResponse(
                payload, out ok, out room, out _, out error);
        }

        /// <summary>
        /// Parse a <c>RoomJoin</c> (0x21) <b>response</b> payload, also extracting the
        /// local player's room UUID appended by the server (v3.1+ protocol).
        /// <paramref name="localPlayerId"/> is empty string when the server is pre-v3.1.
        /// </summary>
        internal static bool ParseJoinRoomResponse(
            byte[] payload,
            out bool       ok,
            out RoomInfo   room,
            out string     localPlayerId,
            out string     error)
        {
            ok            = false;
            room          = null;
            localPlayerId = string.Empty;
            error         = null;

            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            byte msgKind = payload[offset++];
            if (msgKind != RoomMsgKind.Response) return false;

            ok = payload[offset++] != 0;

            if (ok)
            {
                if (!TryReadString(payload, ref offset, out string roomId))   return false;
                if (!TryReadString(payload, ref offset, out string roomCode)) return false;
                if (!TryReadString(payload, ref offset, out string name))     return false;
                if (offset + 3 > payload.Length)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                // Read player roster
                var players = new PlayerInfo[playerCount];
                for (int i = 0; i < playerCount; i++)
                {
                    if (!TryReadPlayerInfo(payload, ref offset, out players[i]))
                        return false;
                }

                room = new RoomInfo(roomId, roomCode, name, "waiting", playerCount, maxPlayers, isPublic, players);

                // [local_player_id_len:2][local_player_id:N]  — v3.1+ optional field
                if (offset < payload.Length)
                    TryReadString(payload, ref offset, out localPlayerId);

                return true;
            }
            else
            {
                return TryReadString(payload, ref offset, out error);
            }
        }

        /// <summary>
        /// Parse a <c>RoomJoin</c> (0x21) <b>notification</b> payload (msg_kind=0x01).
        /// Fired when another player joins the room.
        /// </summary>
        public static bool ParsePlayerJoinedNotification(
            byte[] payload,
            out PlayerInfo player)
        {
            player = null;
            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Notification) return false;

            return TryReadPlayerInfo(payload, ref offset, out player);
        }

        // ── RoomLeave (0x22) — Response or Notification ────────────────────────

        /// <summary>
        /// Read the <c>msg_kind</c> byte from a <c>RoomLeave</c> (0x22) payload.
        /// </summary>
        public static bool TryGetLeaveMsgKind(byte[] payload, out byte msgKind)
        {
            msgKind = 0;
            if (payload == null || payload.Length < 1) return false;
            msgKind = payload[0];
            return msgKind == RoomMsgKind.Response || msgKind == RoomMsgKind.Notification;
        }

        /// <summary>
        /// Parse a <c>RoomLeave</c> (0x22) <b>response</b> payload (msg_kind=0x00).
        /// </summary>
        public static bool ParseLeaveRoomResponse(byte[] payload, out bool ok)
        {
            ok = false;
            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Response) return false;
            ok = payload[offset++] != 0;
            return true;
        }

        /// <summary>
        /// Parse a <c>RoomLeave</c> (0x22) <b>notification</b> payload (msg_kind=0x01).
        /// Fired when another player leaves the room.
        /// </summary>
        public static bool ParsePlayerLeftNotification(byte[] payload, out string playerId)
        {
            playerId = null;
            if (payload == null || payload.Length < 1) return false;

            int offset = 0;
            if (payload[offset++] != RoomMsgKind.Notification) return false;

            return TryReadString(payload, ref offset, out playerId);
        }

        // ── RoomList Response (0x23) ───────────────────────────────────────────

        /// <summary>
        /// Parse a <c>RoomList</c> (0x23) response payload.
        /// </summary>
        public static bool ParseRoomListResponse(byte[] payload, out RoomInfo[] rooms)
        {
            rooms = null;
            if (payload == null || payload.Length < 2) return false;

            int offset = 0;
            int roomCount = ReadU16LE(payload, ref offset);

            // Cap room count to prevent oversized allocation from
            // malicious/buggy server claiming 65535 rooms. A valid RTMPE server
            // supports at most 256 rooms per project; this also matches the
            // upstream 1 MiB payload cap (~50 bytes per room summary minimum).
            const int MaxRoomCount = 256;
            if (roomCount > MaxRoomCount) return false;

            rooms = new RoomInfo[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                if (!TryReadString(payload, ref offset, out string roomId))   return false;
                if (!TryReadString(payload, ref offset, out string roomCode)) return false;
                if (!TryReadString(payload, ref offset, out string name))     return false;
                if (!TryReadString(payload, ref offset, out string state))    return false;
                if (offset + 3 > payload.Length)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                rooms[i] = new RoomInfo(roomId, roomCode, name, state, playerCount, maxPlayers, isPublic);
            }

            return true;
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Read a length-prefixed UTF-8 string: [len:2 LE][data:len].
        /// </summary>
        internal static bool TryReadString(byte[] buf, ref int offset, out string value)
        {
            value = null;
            if (offset + 2 > buf.Length) return false;

            int len = ReadU16LE(buf, ref offset);

            // Cap individual string length for defense-in-depth.
            const int MaxStringBytes = 4096;
            if (len > MaxStringBytes) return false;

            if (len == 0) { value = string.Empty; return true; }
            if (offset + len > buf.Length) return false;

            value = Encoding.UTF8.GetString(buf, offset, len);
            offset += len;
            return true;
        }

        /// <summary>
        /// Read a PlayerInfo record from the buffer.
        /// Layout: [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
        ///         [is_host:1][is_ready:1]
        /// </summary>
        internal static bool TryReadPlayerInfo(byte[] buf, ref int offset, out PlayerInfo player)
        {
            player = null;

            if (!TryReadString(buf, ref offset, out string playerId))    return false;
            if (!TryReadString(buf, ref offset, out string displayName)) return false;
            if (offset + 2 > buf.Length)                                 return false;

            bool isHost  = buf[offset++] != 0;
            bool isReady = buf[offset++] != 0;

            player = new PlayerInfo(playerId, displayName, isHost, isReady);
            return true;
        }

        private static int ReadU16LE(byte[] buf, ref int offset)
        {
            int value = buf[offset] | (buf[offset + 1] << 8);
            offset += 2;
            return value;
        }
    }
}
