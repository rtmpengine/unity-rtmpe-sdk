// RTMPE SDK — Tests/Runtime/RoomPacketParserTests.cs
//
// NUnit tests for RoomPacketParser.
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Text;
using NUnit.Framework;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class RoomPacketParserTests
    {
        // ── CreateRoom Response ────────────────────────────────────────────────

        [Test]
        public void ParseCreateRoomResponse_Success_ParsesAllFields()
        {
            // Build: [ok=1][room_id_len:2][room_id][room_code_len:2][room_code][max_players:1]
            byte[] roomIdBytes   = Encoding.UTF8.GetBytes("abc-123");
            byte[] roomCodeBytes = Encoding.UTF8.GetBytes("XKCD42");

            var buf = new byte[1 + 2 + roomIdBytes.Length + 2 + roomCodeBytes.Length + 1];
            int offset = 0;
            buf[offset++] = 1; // ok
            WriteU16LE(buf, ref offset, (ushort)roomIdBytes.Length);
            WriteBytes(buf, ref offset, roomIdBytes);
            WriteU16LE(buf, ref offset, (ushort)roomCodeBytes.Length);
            WriteBytes(buf, ref offset, roomCodeBytes);
            buf[offset++] = 12; // max_players

            Assert.IsTrue(RoomPacketParser.ParseCreateRoomResponse(
                buf, out bool ok, out string roomId, out string roomCode,
                out int maxPlayers, out string error));
            Assert.IsTrue(ok);
            Assert.AreEqual("abc-123", roomId);
            Assert.AreEqual("XKCD42", roomCode);
            Assert.AreEqual(12, maxPlayers);
            Assert.IsNull(error);
        }

        [Test]
        public void ParseCreateRoomResponse_Failure_ParsesError()
        {
            byte[] errorBytes = Encoding.UTF8.GetBytes("room limit exceeded");

            var buf = new byte[1 + 2 + errorBytes.Length];
            int offset = 0;
            buf[offset++] = 0; // ok=false
            WriteU16LE(buf, ref offset, (ushort)errorBytes.Length);
            WriteBytes(buf, ref offset, errorBytes);

            Assert.IsTrue(RoomPacketParser.ParseCreateRoomResponse(
                buf, out bool ok, out _, out _, out _, out string error));
            Assert.IsFalse(ok);
            Assert.AreEqual("room limit exceeded", error);
        }

        [Test]
        public void ParseCreateRoomResponse_NullPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParseCreateRoomResponse(
                null, out _, out _, out _, out _, out _));
        }

        [Test]
        public void ParseCreateRoomResponse_EmptyPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParseCreateRoomResponse(
                Array.Empty<byte>(), out _, out _, out _, out _, out _));
        }

        [Test]
        public void ParseCreateRoomResponse_TruncatedSuccess_ReturnsFalse()
        {
            // ok=1 but no room_id_len
            var buf = new byte[] { 1 };
            Assert.IsFalse(RoomPacketParser.ParseCreateRoomResponse(
                buf, out _, out _, out _, out _, out _));
        }

        // ── JoinRoom Response ──────────────────────────────────────────────────

        [Test]
        public void ParseJoinRoomResponse_Success_ParsesRoomAndPlayers()
        {
            // Build: [msg_kind=0x00][ok=1]
            //   [room_id][room_code][name][player_count:1][max_players:1][is_public:1]
            //   for each player: [player_id][display_name][is_host:1][is_ready:1]

            var ms = new TestStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(1);    // ok = true
            WriteString(ms, "room-uuid-1");
            WriteString(ms, "ABC123");
            WriteString(ms, "My Room");
            ms.WriteByte(2);  // player_count
            ms.WriteByte(16); // max_players
            ms.WriteByte(1);  // is_public

            // Player 1
            WriteString(ms, "player-1");
            WriteString(ms, "Alice");
            ms.WriteByte(1); // is_host
            ms.WriteByte(1); // is_ready

            // Player 2
            WriteString(ms, "player-2");
            WriteString(ms, "Bob");
            ms.WriteByte(0); // is_host
            ms.WriteByte(0); // is_ready

            var payload = ms.ToArray();

            Assert.IsTrue(RoomPacketParser.ParseJoinRoomResponse(
                payload, out bool ok, out RoomInfo room, out string error));
            Assert.IsTrue(ok);
            Assert.IsNull(error);
            Assert.IsNotNull(room);
            Assert.AreEqual("room-uuid-1", room.RoomId);
            Assert.AreEqual("ABC123", room.RoomCode);
            Assert.AreEqual("My Room", room.Name);
            Assert.AreEqual(2, room.PlayerCount);
            Assert.AreEqual(16, room.MaxPlayers);
            Assert.IsTrue(room.IsPublic);
            Assert.AreEqual(2, room.Players.Length);

            Assert.AreEqual("player-1", room.Players[0].PlayerId);
            Assert.AreEqual("Alice", room.Players[0].DisplayName);
            Assert.IsTrue(room.Players[0].IsHost);
            Assert.IsTrue(room.Players[0].IsReady);

            Assert.AreEqual("player-2", room.Players[1].PlayerId);
            Assert.AreEqual("Bob", room.Players[1].DisplayName);
            Assert.IsFalse(room.Players[1].IsHost);
            Assert.IsFalse(room.Players[1].IsReady);
        }

        [Test]
        public void ParseJoinRoomResponse_Failure_ParsesError()
        {
            var ms = new TestStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(0);    // ok = false
            WriteString(ms, "room is full");

            var payload = ms.ToArray();

            Assert.IsTrue(RoomPacketParser.ParseJoinRoomResponse(
                payload, out bool ok, out _, out string error));
            Assert.IsFalse(ok);
            Assert.AreEqual("room is full", error);
        }

        [Test]
        public void ParseJoinRoomResponse_WrongMsgKind_ReturnsFalse()
        {
            var payload = new byte[] { 0x01, 1 }; // msg_kind=Notification, not Response
            Assert.IsFalse(RoomPacketParser.ParseJoinRoomResponse(
                payload, out _, out _, out _));
        }

        // ── PlayerJoined Notification ──────────────────────────────────────────

        [Test]
        public void ParsePlayerJoinedNotification_ValidPayload_ReturnsPlayer()
        {
            var ms = new TestStream();
            ms.WriteByte(0x01); // msg_kind = Notification
            WriteString(ms, "player-3");
            WriteString(ms, "Charlie");
            ms.WriteByte(0); // is_host
            ms.WriteByte(1); // is_ready

            Assert.IsTrue(RoomPacketParser.ParsePlayerJoinedNotification(
                ms.ToArray(), out PlayerInfo player));
            Assert.AreEqual("player-3", player.PlayerId);
            Assert.AreEqual("Charlie", player.DisplayName);
            Assert.IsFalse(player.IsHost);
            Assert.IsTrue(player.IsReady);
        }

        [Test]
        public void ParsePlayerJoinedNotification_WrongMsgKind_ReturnsFalse()
        {
            var payload = new byte[] { 0x00 }; // Response, not Notification
            Assert.IsFalse(RoomPacketParser.ParsePlayerJoinedNotification(
                payload, out _));
        }

        [Test]
        public void ParsePlayerJoinedNotification_NullPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParsePlayerJoinedNotification(null, out _));
        }

        // ── LeaveRoom Response ─────────────────────────────────────────────────

        [Test]
        public void ParseLeaveRoomResponse_Ok_ReturnsTrue()
        {
            var payload = new byte[] { 0x00, 1 }; // msg_kind=Response, ok=true
            Assert.IsTrue(RoomPacketParser.ParseLeaveRoomResponse(payload, out bool ok));
            Assert.IsTrue(ok);
        }

        [Test]
        public void ParseLeaveRoomResponse_NotOk_ReturnsFalse()
        {
            var payload = new byte[] { 0x00, 0 }; // msg_kind=Response, ok=false
            Assert.IsTrue(RoomPacketParser.ParseLeaveRoomResponse(payload, out bool ok));
            Assert.IsFalse(ok);
        }

        [Test]
        public void ParseLeaveRoomResponse_TooShort_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParseLeaveRoomResponse(
                new byte[] { 0x00 }, out _));
        }

        [Test]
        public void ParseLeaveRoomResponse_WrongMsgKind_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParseLeaveRoomResponse(
                new byte[] { 0x01, 1 }, out _));
        }

        // ── PlayerLeft Notification ────────────────────────────────────────────

        [Test]
        public void ParsePlayerLeftNotification_ValidPayload_ReturnsPlayerId()
        {
            var ms = new TestStream();
            ms.WriteByte(0x01); // msg_kind = Notification
            WriteString(ms, "player-99");

            Assert.IsTrue(RoomPacketParser.ParsePlayerLeftNotification(
                ms.ToArray(), out string playerId));
            Assert.AreEqual("player-99", playerId);
        }

        [Test]
        public void ParsePlayerLeftNotification_NullPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParsePlayerLeftNotification(null, out _));
        }

        // ── RoomList Response ──────────────────────────────────────────────────

        [Test]
        public void ParseRoomListResponse_EmptyList_ReturnsEmptyArray()
        {
            var payload = new byte[] { 0, 0 }; // room_count = 0
            Assert.IsTrue(RoomPacketParser.ParseRoomListResponse(payload, out RoomInfo[] rooms));
            Assert.AreEqual(0, rooms.Length);
        }

        [Test]
        public void ParseRoomListResponse_TwoRooms_ParsesAll()
        {
            var ms = new TestStream();
            WriteU16LE(ms, 2); // room_count

            // Room 1
            WriteString(ms, "room-1");
            WriteString(ms, "CODE01");
            WriteString(ms, "First");
            WriteString(ms, "waiting");
            ms.WriteByte(3);  // player_count
            ms.WriteByte(16); // max_players
            ms.WriteByte(1);  // is_public

            // Room 2
            WriteString(ms, "room-2");
            WriteString(ms, "CODE02");
            WriteString(ms, "Second");
            WriteString(ms, "playing");
            ms.WriteByte(8);  // player_count
            ms.WriteByte(10); // max_players
            ms.WriteByte(0);  // is_public (private)

            Assert.IsTrue(RoomPacketParser.ParseRoomListResponse(
                ms.ToArray(), out RoomInfo[] rooms));
            Assert.AreEqual(2, rooms.Length);

            Assert.AreEqual("room-1", rooms[0].RoomId);
            Assert.AreEqual("CODE01", rooms[0].RoomCode);
            Assert.AreEqual("First", rooms[0].Name);
            Assert.AreEqual("waiting", rooms[0].State);
            Assert.AreEqual(3, rooms[0].PlayerCount);
            Assert.AreEqual(16, rooms[0].MaxPlayers);
            Assert.IsTrue(rooms[0].IsPublic);

            Assert.AreEqual("room-2", rooms[1].RoomId);
            Assert.AreEqual("CODE02", rooms[1].RoomCode);
            Assert.AreEqual("Second", rooms[1].Name);
            Assert.AreEqual("playing", rooms[1].State);
            Assert.AreEqual(8, rooms[1].PlayerCount);
            Assert.AreEqual(10, rooms[1].MaxPlayers);
            Assert.IsFalse(rooms[1].IsPublic);
        }

        [Test]
        public void ParseRoomListResponse_NullPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.ParseRoomListResponse(null, out _));
        }

        [Test]
        public void ParseRoomListResponse_TruncatedRoom_ReturnsFalse()
        {
            var ms = new TestStream();
            WriteU16LE(ms, 1); // claim 1 room but provide no data
            Assert.IsFalse(RoomPacketParser.ParseRoomListResponse(ms.ToArray(), out _));
        }

        // ── TryGetJoinMsgKind ──────────────────────────────────────────────────

        [Test]
        public void TryGetJoinMsgKind_Response_Returns0x00()
        {
            Assert.IsTrue(RoomPacketParser.TryGetJoinMsgKind(
                new byte[] { 0x00 }, out byte kind));
            Assert.AreEqual(0x00, kind);
        }

        [Test]
        public void TryGetJoinMsgKind_Notification_Returns0x01()
        {
            Assert.IsTrue(RoomPacketParser.TryGetJoinMsgKind(
                new byte[] { 0x01 }, out byte kind));
            Assert.AreEqual(0x01, kind);
        }

        [Test]
        public void TryGetJoinMsgKind_InvalidByte_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.TryGetJoinMsgKind(
                new byte[] { 0xFF }, out _));
        }

        [Test]
        public void TryGetJoinMsgKind_NullPayload_ReturnsFalse()
        {
            Assert.IsFalse(RoomPacketParser.TryGetJoinMsgKind(null, out _));
        }

        // ── RoundTrip: Builder → Parser ────────────────────────────────────────

        [Test]
        public void RoundTrip_CreateRoom_BuilderThenParser()
        {
            var options = new CreateRoomOptions
            {
                Name       = "Round Trip Room",
                MaxPlayers = 10,
                IsPublic   = false
            };

            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);

            // Simulate server response using the same wire format
            byte[] roomIdBytes   = Encoding.UTF8.GetBytes("uuid-rt-1");
            byte[] roomCodeBytes = Encoding.UTF8.GetBytes("ROUND1");

            var response = new byte[1 + 2 + roomIdBytes.Length + 2 + roomCodeBytes.Length + 1];
            int offset = 0;
            response[offset++] = 1; // ok
            WriteU16LE(response, ref offset, (ushort)roomIdBytes.Length);
            WriteBytes(response, ref offset, roomIdBytes);
            WriteU16LE(response, ref offset, (ushort)roomCodeBytes.Length);
            WriteBytes(response, ref offset, roomCodeBytes);
            response[offset++] = 10; // max_players

            Assert.IsTrue(RoomPacketParser.ParseCreateRoomResponse(
                response, out bool ok, out string roomId, out string roomCode,
                out int maxPlayers, out _));
            Assert.IsTrue(ok);
            Assert.AreEqual("uuid-rt-1", roomId);
            Assert.AreEqual("ROUND1", roomCode);
            Assert.AreEqual(10, maxPlayers);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>Simple in-memory stream for building test payloads.</summary>
        private class TestStream
        {
            private byte[] _buf = new byte[256];
            private int _pos;

            public void WriteByte(byte b)
            {
                EnsureCapacity(1);
                _buf[_pos++] = b;
            }

            public void Write(byte[] data)
            {
                EnsureCapacity(data.Length);
                Buffer.BlockCopy(data, 0, _buf, _pos, data.Length);
                _pos += data.Length;
            }

            public byte[] ToArray()
            {
                var result = new byte[_pos];
                Buffer.BlockCopy(_buf, 0, result, 0, _pos);
                return result;
            }

            private void EnsureCapacity(int needed)
            {
                if (_pos + needed <= _buf.Length) return;
                var newBuf = new byte[Math.Max(_buf.Length * 2, _pos + needed)];
                Buffer.BlockCopy(_buf, 0, newBuf, 0, _pos);
                _buf = newBuf;
            }
        }

        private static void WriteString(TestStream ms, string value)
        {
            byte[] encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
            ms.WriteByte((byte)(encoded.Length & 0xFF));
            ms.WriteByte((byte)(encoded.Length >> 8));
            ms.Write(encoded);
        }

        private static void WriteU16LE(TestStream ms, int value)
        {
            ms.WriteByte((byte)(value & 0xFF));
            ms.WriteByte((byte)(value >> 8));
        }

        private static int ReadU16LE(byte[] buf, int offset)
            => buf[offset] | (buf[offset + 1] << 8);

        private static void WriteU16LE(byte[] buf, ref int offset, ushort value)
        {
            buf[offset++] = (byte)(value & 0xFF);
            buf[offset++] = (byte)(value >> 8);
        }

        private static void WriteBytes(byte[] buf, ref int offset, byte[] data)
        {
            Buffer.BlockCopy(data, 0, buf, offset, data.Length);
            offset += data.Length;
        }
    }
}
