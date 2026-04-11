// RTMPE SDK — Tests/Runtime/RoomPacketBuilderTests.cs
//
// NUnit tests for RoomPacketBuilder.
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Text;
using NUnit.Framework;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class RoomPacketBuilderTests
    {
        // ── CreateRoom ─────────────────────────────────────────────────────────

        [Test]
        public void BuildCreateRoomPayload_DefaultOptions_MinimalPayload()
        {
            var payload = RoomPacketBuilder.BuildCreateRoomPayload(null);

            // [name_len:2=0][max_players:1=0][is_public:1=1]
            Assert.AreEqual(4, payload.Length);
            Assert.AreEqual(0, ReadU16LE(payload, 0), "name_len should be 0");
            Assert.AreEqual(0, payload[2], "max_players default should be 0 (server default)");
            Assert.AreEqual(1, payload[3], "is_public default should be true (1)");
        }

        [Test]
        public void BuildCreateRoomPayload_WithName_EncodesNameCorrectly()
        {
            var options = new CreateRoomOptions { Name = "Test Room", MaxPlayers = 8, IsPublic = false };
            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);

            int nameLen = ReadU16LE(payload, 0);
            Assert.AreEqual(9, nameLen, "name_len for 'Test Room'");

            string name = Encoding.UTF8.GetString(payload, 2, nameLen);
            Assert.AreEqual("Test Room", name);

            Assert.AreEqual(8, payload[2 + nameLen], "max_players should be 8");
            Assert.AreEqual(0, payload[2 + nameLen + 1], "is_public should be false (0)");
        }

        [Test]
        public void BuildCreateRoomPayload_MaxPlayersClamp_ClampedTo16()
        {
            var options = new CreateRoomOptions { MaxPlayers = 100 };
            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);

            int nameLen = ReadU16LE(payload, 0);
            Assert.AreEqual(16, payload[2 + nameLen], "max_players should be clamped to 16");
        }

        [Test]
        public void BuildCreateRoomPayload_MaxPlayersNegative_ClampedTo0()
        {
            var options = new CreateRoomOptions { MaxPlayers = -5 };
            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);

            int nameLen = ReadU16LE(payload, 0);
            Assert.AreEqual(0, payload[2 + nameLen], "negative max_players should clamp to 0");
        }

        [Test]
        public void BuildCreateRoomPayload_UnicodeRoomName_EncodesUtf8()
        {
            var options = new CreateRoomOptions { Name = "مرحبا" }; // Arabic
            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);

            int nameLen = ReadU16LE(payload, 0);
            Assert.Greater(nameLen, 0, "UTF-8 encoded Arabic should have length > 0");

            string decoded = Encoding.UTF8.GetString(payload, 2, nameLen);
            Assert.AreEqual("مرحبا", decoded);
        }

        // ── JoinRoom ───────────────────────────────────────────────────────────

        [Test]
        public void BuildJoinRoomPayload_ById_HasRoomIdAndEmptyCode()
        {
            var payload = RoomPacketBuilder.BuildJoinRoomPayload(
                "abc-123", null, new JoinRoomOptions { DisplayName = "Player1" });

            int offset = 0;
            int roomIdLen = ReadU16LE(payload, offset); offset += 2;
            string roomId = Encoding.UTF8.GetString(payload, offset, roomIdLen); offset += roomIdLen;
            Assert.AreEqual("abc-123", roomId);

            int roomCodeLen = ReadU16LE(payload, offset); offset += 2;
            Assert.AreEqual(0, roomCodeLen, "room_code should be empty when joining by ID");
            offset += roomCodeLen;

            int displayLen = ReadU16LE(payload, offset); offset += 2;
            string display = Encoding.UTF8.GetString(payload, offset, displayLen);
            Assert.AreEqual("Player1", display);
        }

        [Test]
        public void BuildJoinRoomPayload_ByCode_HasCodeAndEmptyRoomId()
        {
            var payload = RoomPacketBuilder.BuildJoinRoomPayload(
                null, "XKCD42", new JoinRoomOptions());

            int offset = 0;
            int roomIdLen = ReadU16LE(payload, offset); offset += 2;
            Assert.AreEqual(0, roomIdLen, "room_id should be empty when joining by code");
            offset += roomIdLen;

            int roomCodeLen = ReadU16LE(payload, offset); offset += 2;
            string roomCode = Encoding.UTF8.GetString(payload, offset, roomCodeLen);
            Assert.AreEqual("XKCD42", roomCode);
        }

        [Test]
        public void BuildJoinRoomPayload_NullOptions_DoesNotThrow()
        {
            var payload = RoomPacketBuilder.BuildJoinRoomPayload("room-1", null, null);
            Assert.IsNotNull(payload);
            Assert.Greater(payload.Length, 0);
        }

        [Test]
        public void BuildJoinRoomPayload_BothNullIds_ProducesEmptyStrings()
        {
            var payload = RoomPacketBuilder.BuildJoinRoomPayload(null, null, null);

            int offset = 0;
            Assert.AreEqual(0, ReadU16LE(payload, offset)); offset += 2; // room_id_len
            Assert.AreEqual(0, ReadU16LE(payload, offset)); offset += 2; // room_code_len
            Assert.AreEqual(0, ReadU16LE(payload, offset));              // display_name_len
        }

        // ── LeaveRoom ──────────────────────────────────────────────────────────

        [Test]
        public void BuildLeaveRoomPayload_ReturnsEmpty()
        {
            var payload = RoomPacketBuilder.BuildLeaveRoomPayload();
            Assert.AreEqual(0, payload.Length, "LeaveRoom payload should be empty");
        }

        // ── ListRooms ──────────────────────────────────────────────────────────

        [Test]
        public void BuildListRoomsPayload_PublicOnly_ByteIs1()
        {
            var payload = RoomPacketBuilder.BuildListRoomsPayload(publicOnly: true);
            Assert.AreEqual(1, payload.Length);
            Assert.AreEqual(1, payload[0]);
        }

        [Test]
        public void BuildListRoomsPayload_All_ByteIs0()
        {
            var payload = RoomPacketBuilder.BuildListRoomsPayload(publicOnly: false);
            Assert.AreEqual(1, payload.Length);
            Assert.AreEqual(0, payload[0]);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static int ReadU16LE(byte[] buf, int offset)
            => buf[offset] | (buf[offset + 1] << 8);
    }
}
