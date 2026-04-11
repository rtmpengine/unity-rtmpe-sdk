// RTMPE SDK — Runtime/Rooms/RoomPacketBuilder.cs
//
// Builds payload bytes for room-related packets (0x20–0x23).
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte header + payload).
//
// Wire formats (all little-endian):
//
// ── RoomCreate (0x20) Client → Server ──────────────────────────────────────
//   [name_len:2 LE][name:N UTF-8]
//   [max_players:1]
//   [is_public:1]
//
// ── RoomJoin (0x21) Client → Server ────────────────────────────────────────
//   [room_id_len:2 LE][room_id:N UTF-8]           (empty if joining by code)
//   [room_code_len:2 LE][room_code:N UTF-8]       (empty if joining by ID)
//   [display_name_len:2 LE][display_name:N UTF-8]
//
// ── RoomLeave (0x22) Client → Server ───────────────────────────────────────
//   (empty payload — server identifies player by session)
//
// ── RoomList (0x23) Client → Server ────────────────────────────────────────
//   [public_only:1]

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Builds payload byte arrays for room protocol packets.
    /// All methods are static and produce a fresh byte[] on each call.
    /// </summary>
    public static class RoomPacketBuilder
    {
        private const int MaxNameBytes        = 256;   // 64 runes × 4 bytes worst case
        private const int MaxDisplayNameBytes = 128;   // 32 runes × 4 bytes worst case
        private const int MaxRoomIdBytes      = 128;   // UUID ≤ 36 chars
        private const int MaxRoomCodeBytes    = 24;    // 6 chars × 4 bytes worst case

        // ── CreateRoom payload ─────────────────────────────────────────────────

        /// <summary>
        /// Build the payload for a <c>RoomCreate</c> (0x20) request.
        /// </summary>
        public static byte[] BuildCreateRoomPayload(CreateRoomOptions options)
        {
            if (options == null) options = new CreateRoomOptions();

            byte[] nameBytes = SafeEncodeUtf8(options.Name, MaxNameBytes);

            // Layout: [name_len:2][name:N][max_players:1][is_public:1]
            int size = 2 + nameBytes.Length + 1 + 1;
            var buf = new byte[size];
            int offset = 0;

            WriteU16LE(buf, ref offset, (ushort)nameBytes.Length);
            WriteBytes(buf, ref offset, nameBytes);
            buf[offset++] = ClampByte(options.MaxPlayers, 0, 16);
            buf[offset++] = (byte)(options.IsPublic ? 1 : 0);

            return buf;
        }

        // ── JoinRoom payload ───────────────────────────────────────────────────

        /// <summary>
        /// Build the payload for a <c>RoomJoin</c> (0x21) request.
        /// Supply either <paramref name="roomId"/> or <paramref name="roomCode"/>
        /// (the other should be null or empty).
        /// </summary>
        public static byte[] BuildJoinRoomPayload(
            string roomId,
            string roomCode,
            JoinRoomOptions options)
        {
            if (options == null) options = new JoinRoomOptions();

            byte[] roomIdBytes      = SafeEncodeUtf8(roomId, MaxRoomIdBytes);
            byte[] roomCodeBytes    = SafeEncodeUtf8(roomCode, MaxRoomCodeBytes);
            byte[] displayNameBytes = SafeEncodeUtf8(options.DisplayName, MaxDisplayNameBytes);

            // Layout: [room_id_len:2][room_id:N][room_code_len:2][room_code:N]
            //         [display_name_len:2][display_name:N]
            int size = 2 + roomIdBytes.Length
                     + 2 + roomCodeBytes.Length
                     + 2 + displayNameBytes.Length;
            var buf = new byte[size];
            int offset = 0;

            WriteU16LE(buf, ref offset, (ushort)roomIdBytes.Length);
            WriteBytes(buf, ref offset, roomIdBytes);
            WriteU16LE(buf, ref offset, (ushort)roomCodeBytes.Length);
            WriteBytes(buf, ref offset, roomCodeBytes);
            WriteU16LE(buf, ref offset, (ushort)displayNameBytes.Length);
            WriteBytes(buf, ref offset, displayNameBytes);

            return buf;
        }

        // ── LeaveRoom payload ──────────────────────────────────────────────────

        /// <summary>
        /// Build the payload for a <c>RoomLeave</c> (0x22) request.
        /// The server identifies the player by session — no payload is needed.
        /// </summary>
        public static byte[] BuildLeaveRoomPayload()
            => Array.Empty<byte>();

        // ── ListRooms payload ──────────────────────────────────────────────────

        /// <summary>
        /// Build the payload for a <c>RoomList</c> (0x23) request.
        /// </summary>
        /// <param name="publicOnly">When true, exclude private rooms from the response.</param>
        public static byte[] BuildListRoomsPayload(bool publicOnly = true)
            => new byte[] { (byte)(publicOnly ? 1 : 0) };

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>Encode a string to UTF-8, clamped to <paramref name="maxBytes"/>.</summary>
        private static byte[] SafeEncodeUtf8(string value, int maxBytes)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

            byte[] raw = Encoding.UTF8.GetBytes(value);
            if (raw.Length <= maxBytes) return raw;

            // M-5 fix: walk backwards to find a valid UTF-8 character boundary.
            // UTF-8 continuation bytes have the pattern 10xxxxxx (0x80..0xBF).
            int end = maxBytes;
            while (end > 0 && (raw[end] & 0xC0) == 0x80)
                end--;

            if (end == 0) return Array.Empty<byte>();

            var truncated = new byte[end];
            Buffer.BlockCopy(raw, 0, truncated, 0, end);
            return truncated;
        }

        private static void WriteU16LE(byte[] buf, ref int offset, ushort value)
        {
            buf[offset++] = (byte)(value & 0xFF);
            buf[offset++] = (byte)(value >> 8);
        }

        private static void WriteBytes(byte[] buf, ref int offset, byte[] data)
        {
            if (data.Length == 0) return;
            Buffer.BlockCopy(data, 0, buf, offset, data.Length);
            offset += data.Length;
        }

        private static byte ClampByte(int value, int min, int max)
        {
            if (value < min) return (byte)min;
            if (value > max) return (byte)max;
            return (byte)value;
        }
    }
}
