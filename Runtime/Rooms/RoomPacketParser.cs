// RTMPE SDK — Runtime/Rooms/RoomPacketParser.cs
//
// Parses inbound room-related packet payloads (0x20–0x23).
// These methods operate on the raw payload AFTER the 13-byte header has been
// stripped by PacketParser.ExtractPayload().
//
// Wire formats (all little-endian):
//
// ── RoomCreate Response (0x20) Server → Client ─────────────────────────────
//  [ok:1]
//  if ok=1: [room_id_len:2 LE][room_id:N][room_code_len:2 LE][room_code:N][max_players:1]
//           [local_player_id_len:2 LE][local_player_id:N]   ← appended (v3.1+)
//  if ok=0: [error_len:2 LE][error:N]
//
// ── RoomJoin Response (0x21, msg_kind=0x00) Server → Client ────────────────
//  [msg_kind:1=0x00][ok:1]
//  if ok=1: [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//           [name_len:2][name:N][player_count:1][max_players:1][is_public:1]
//           for each player:
//             [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//             [is_host:1][is_ready:1]
//           [local_player_id_len:2][local_player_id:N]       ← appended (v3.1+)
//  if ok=0: [error_len:2][error:N]
//
// ── PlayerJoined Notification (0x21, msg_kind=0x01) Server → Client ────────
//  [msg_kind:1=0x01]
//  [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
//  [is_host:1][is_ready:1]
//
// ── RoomLeave Response (0x22, msg_kind=0x00) Server → Client ───────────────
//  [msg_kind:1=0x00][ok:1]
//
// ── PlayerLeft Notification (0x22, msg_kind=0x01) Server → Client ──────────
//  [msg_kind:1=0x01][player_id_len:2][player_id:N]
//
// ── RoomList Response (0x23) Server → Client ───────────────────────────────
//  [room_count:2 LE]
//  for each room:
//    [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
//    [name_len:2][name:N][state_len:2][state:N]
//    [player_count:1][max_players:1][is_public:1]

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
                if (offset > payload.Length - 3)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                // Reject impossibly-sized rosters before allocating the
                // PlayerInfo[].  Every player record needs at least
                // MinPlayerInfoBytes bytes; if the declared count would
                // require more bytes than the remaining payload, the packet
                // is malformed and a malicious server is attempting an
                // alloc-amplification attack against the heap.
                int remaining = payload.Length - offset;
                if ((long)playerCount * MinPlayerInfoBytes > remaining) return false;

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

            // Each room summary needs at minimum 4 length-prefixes (8 bytes)
            // for the four string fields plus 3 trailing bytes
            // (player_count, max_players, is_public).  Reject when the
            // declared count cannot possibly fit in the remaining payload
            // — the per-iteration TryReadString check catches this too,
            // but a pre-allocation guard avoids the worst-case
            // `RoomInfo[256]` heap allocation on a single byte of
            // attacker-controlled count.
            const int MinRoomSummaryBytes = 4 * 2 + 3;
            int remainingForRooms = payload.Length - offset;
            if ((long)roomCount * MinRoomSummaryBytes > remainingForRooms) return false;

            rooms = new RoomInfo[roomCount];
            for (int i = 0; i < roomCount; i++)
            {
                if (!TryReadString(payload, ref offset, out string roomId))   return false;
                if (!TryReadString(payload, ref offset, out string roomCode)) return false;
                if (!TryReadString(payload, ref offset, out string name))     return false;
                if (!TryReadString(payload, ref offset, out string state))    return false;
                if (offset > payload.Length - 3)                              return false;

                int playerCount = payload[offset++];
                int maxPlayers  = payload[offset++];
                bool isPublic   = payload[offset++] != 0;

                rooms[i] = new RoomInfo(roomId, roomCode, name, state, playerCount, maxPlayers, isPublic);
            }

            return true;
        }

        // ── Internal helpers ───────────────────────────────────────────────────

        // Hard ceiling that no per-call cap can exceed.  16-bit length fields
        // top out at 65 535, so 4 096 is already two orders of magnitude
        // above any legitimate field; anything larger is treated as
        // protocol-violation by every well-behaved gateway.
        private const int HardMaxStringBytes = 4096;
        private const int DefaultMaxStringBytes = 4096;

        // Configurable cap surfaced for NetworkManager to align with
        // <c>NetworkSettings.maxLobbyStringBytes</c>.  Static rather than
        // threaded through every Parse* method to avoid touching the broad
        // public API surface; the field is written once on
        // <c>NetworkManager.Awake</c> and never again from production code.
        // Reads are non-volatile because the only concurrent reader is the
        // single main-thread parser.
        private static int _configuredMaxStringBytes = DefaultMaxStringBytes;

        /// <summary>
        /// Wire the per-parser string-length cap to a deployment-supplied
        /// value (typically <c>NetworkSettings.maxLobbyStringBytes</c>).
        /// Values are clamped to the documented [16, 4096] range so a
        /// misconfigured Inspector field cannot disable the parser's
        /// defensive ceiling.  Called by <c>NetworkManager.Awake</c>;
        /// idempotent.
        /// </summary>
        public static void ConfigureMaxStringBytes(int maxStringBytes)
        {
            if (maxStringBytes < 16) maxStringBytes = 16;
            if (maxStringBytes > HardMaxStringBytes) maxStringBytes = HardMaxStringBytes;
            _configuredMaxStringBytes = maxStringBytes;
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Reset the per-parser string-length cap to the package default.
        /// Test seam used by <c>[TearDown]</c> hooks that want a clean
        /// starting state between fixtures.  Compiled only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined so the shipped Player
        /// assembly does not expose a public mutator on the parser's
        /// process-wide string-length cap.
        /// </summary>
        public static void ResetMaxStringBytesForTests()
        {
            _configuredMaxStringBytes = DefaultMaxStringBytes;
        }
#endif // UNITY_INCLUDE_TESTS

        // Strict UTF-8 decoder.  The default <see cref="Encoding.UTF8"/>
        // silently replaces malformed sequences with U+FFFD, which lets a
        // hostile server smuggle bytes that survive the parser but mutate
        // upstream string-comparison invariants (reserved-key checks, scene
        // names).  The strict variant throws <see cref="DecoderFallbackException"/>
        // on the first invalid sequence; the parser converts that into a
        // clean parse-failure return.
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        /// <summary>
        /// Read a length-prefixed UTF-8 string: [len:2 LE][data:len].
        /// </summary>
        internal static bool TryReadString(byte[] buf, ref int offset, out string value)
        {
            value = null;
            // Use subtraction not addition for the boundary check so
            // `offset + 2` cannot overflow into a permissive read on a
            // pathological large `offset`.  The same pattern is applied to
            // every multi-byte read in this file.
            if (buf == null || offset < 0 || offset > buf.Length - 2) return false;

            int len = ReadU16LE(buf, ref offset);

            // Per-call cap from the configured setting (typically 256 from
            // NetworkSettings.maxLobbyStringBytes); the hard 4 096-byte
            // ceiling is enforced regardless via the clamp in
            // <see cref="ConfigureMaxStringBytes"/>.
            if (len > _configuredMaxStringBytes) return false;

            if (len == 0) { value = string.Empty; return true; }
            if (len > buf.Length - offset) return false;

            try { value = StrictUtf8.GetString(buf, offset, len); }
            catch (DecoderFallbackException) { return false; }
            offset += len;
            return true;
        }

        /// <summary>
        /// Read a PlayerInfo record from the buffer.
        /// Layout: [player_id_len:2][player_id:N][display_name_len:2][display_name:N]
        ///        [is_host:1][is_ready:1]
        /// </summary>
        internal static bool TryReadPlayerInfo(byte[] buf, ref int offset, out PlayerInfo player)
        {
            player = null;

            if (!TryReadString(buf, ref offset, out string playerId))    return false;
            if (!TryReadString(buf, ref offset, out string displayName)) return false;
            if (buf == null || offset > buf.Length - 2)                  return false;

            bool isHost  = buf[offset++] != 0;
            bool isReady = buf[offset++] != 0;

            player = new PlayerInfo(playerId, displayName, isHost, isReady);
            return true;
        }

        // Smallest legal serialised PlayerInfo: 2 (id_len=0) + 0 + 2 (name_len=0) + 0 + 1 (is_host) + 1 (is_ready).
        internal const int MinPlayerInfoBytes = 6;

        private static int ReadU16LE(byte[] buf, ref int offset)
        {
            int value = buf[offset] | (buf[offset + 1] << 8);
            offset += 2;
            return value;
        }
    }
}
