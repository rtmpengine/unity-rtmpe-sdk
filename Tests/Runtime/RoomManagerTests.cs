// RTMPE SDK — Tests/Runtime/RoomManagerTests.cs
//
// NUnit tests for RoomManager.
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.
// Uses fake delegates instead of the real NetworkManager.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Protocol;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class RoomManagerTests
    {
        private PacketBuilder _packetBuilder;
        private List<byte[]>  _sentPackets;
        private NetworkState  _currentState;
        private RoomManager   _roomManager;

        [SetUp]
        public void SetUp()
        {
            _packetBuilder = new PacketBuilder();
            _sentPackets   = new List<byte[]>();
            _currentState  = NetworkState.Connected;

            _roomManager = new RoomManager(
                _packetBuilder,
                packet => _sentPackets.Add(packet),
                () => _currentState);
        }

        // ── CreateRoom ─────────────────────────────────────────────────────────

        [Test]
        public void CreateRoom_WhenConnected_SendsRoomCreatePacket()
        {
            _roomManager.CreateRoom(new CreateRoomOptions { Name = "Test" });

            Assert.AreEqual(1, _sentPackets.Count, "Expected one packet sent");

            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketType.RoomCreate, pkt[PacketProtocol.OFFSET_TYPE]);
        }

        [Test]
        public void CreateRoom_WhenDisconnected_DoesNotSend()
        {
            _currentState = NetworkState.Disconnected;
            _roomManager.CreateRoom();
            Assert.AreEqual(0, _sentPackets.Count);
        }

        [Test]
        public void CreateRoom_NullOptions_DoesNotThrow()
        {
            _roomManager.CreateRoom(null);
            Assert.AreEqual(1, _sentPackets.Count);
        }

        // ── JoinRoom ───────────────────────────────────────────────────────────

        [Test]
        public void JoinRoom_WhenConnected_SendsRoomJoinPacket()
        {
            _roomManager.JoinRoom("room-1");

            Assert.AreEqual(1, _sentPackets.Count);
            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketType.RoomJoin, pkt[PacketProtocol.OFFSET_TYPE]);
        }

        [Test]
        public void JoinRoom_NullRoomId_DoesNotSend()
        {
            _roomManager.JoinRoom(null);
            Assert.AreEqual(0, _sentPackets.Count);
        }

        [Test]
        public void JoinRoom_EmptyRoomId_DoesNotSend()
        {
            _roomManager.JoinRoom("");
            Assert.AreEqual(0, _sentPackets.Count);
        }

        [Test]
        public void JoinRoom_WhenDisconnected_DoesNotSend()
        {
            _currentState = NetworkState.Disconnected;
            _roomManager.JoinRoom("room-1");
            Assert.AreEqual(0, _sentPackets.Count);
        }

        // ── JoinRoomByCode ─────────────────────────────────────────────────────

        [Test]
        public void JoinRoomByCode_WhenConnected_SendsRoomJoinPacket()
        {
            _roomManager.JoinRoomByCode("XKCD42");

            Assert.AreEqual(1, _sentPackets.Count);
            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketType.RoomJoin, pkt[PacketProtocol.OFFSET_TYPE]);
        }

        [Test]
        public void JoinRoomByCode_NullCode_DoesNotSend()
        {
            _roomManager.JoinRoomByCode(null);
            Assert.AreEqual(0, _sentPackets.Count);
        }

        [Test]
        public void JoinRoomByCode_EmptyCode_DoesNotSend()
        {
            _roomManager.JoinRoomByCode("");
            Assert.AreEqual(0, _sentPackets.Count);
        }

        // ── LeaveRoom ──────────────────────────────────────────────────────────

        [Test]
        public void LeaveRoom_WhenInRoom_SendsRoomLeavePacket()
        {
            _currentState = NetworkState.InRoom;
            // Simulate being in a room by feeding a create response
            SimulateRoomCreated("room-1", "CODE01");

            _roomManager.LeaveRoom();

            Assert.AreEqual(1, _sentPackets.Count);
            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketType.RoomLeave, pkt[PacketProtocol.OFFSET_TYPE]);
        }

        [Test]
        public void LeaveRoom_WhenNotInRoom_DoesNotSend()
        {
            _currentState = NetworkState.Connected;
            _roomManager.LeaveRoom();
            Assert.AreEqual(0, _sentPackets.Count);
        }

        // ── ListRooms ──────────────────────────────────────────────────────────

        [Test]
        public void ListRooms_WhenConnected_SendsRoomListPacket()
        {
            _roomManager.ListRooms();

            Assert.AreEqual(1, _sentPackets.Count);
            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketType.RoomList, pkt[PacketProtocol.OFFSET_TYPE]);
        }

        [Test]
        public void ListRooms_WhenInRoom_SendsPacket()
        {
            _currentState = NetworkState.InRoom;
            _roomManager.ListRooms();
            Assert.AreEqual(1, _sentPackets.Count);
        }

        [Test]
        public void ListRooms_WhenDisconnected_DoesNotSend()
        {
            _currentState = NetworkState.Disconnected;
            _roomManager.ListRooms();
            Assert.AreEqual(0, _sentPackets.Count);
        }

        // ── HandleRoomPacket: CreateRoom Response ──────────────────────────────

        [Test]
        public void HandleCreateResponse_Success_FiresOnRoomCreated()
        {
            RoomInfo receivedRoom = null;
            _roomManager.OnRoomCreated += room => receivedRoom = room;

            var payload = BuildCreateRoomResponseOk("room-A", "ACODE1", 16);
            _roomManager.HandleRoomPacket(PacketType.RoomCreate, payload);

            Assert.IsNotNull(receivedRoom);
            Assert.AreEqual("room-A", receivedRoom.RoomId);
            Assert.AreEqual("ACODE1", receivedRoom.RoomCode);
            Assert.AreEqual(16, receivedRoom.MaxPlayers);
        }

        [Test]
        public void HandleCreateResponse_Success_SetsCurrentRoom()
        {
            var payload = BuildCreateRoomResponseOk("room-B", "BCODE2", 8);
            _roomManager.HandleRoomPacket(PacketType.RoomCreate, payload);

            Assert.IsTrue(_roomManager.IsInRoom);
            Assert.AreEqual("room-B", _roomManager.CurrentRoom.RoomId);
        }

        [Test]
        public void HandleCreateResponse_Failure_FiresOnRoomError()
        {
            string receivedError = null;
            _roomManager.OnRoomError += err => receivedError = err;

            var payload = BuildCreateRoomResponseError("limit exceeded");
            _roomManager.HandleRoomPacket(PacketType.RoomCreate, payload);

            Assert.AreEqual("limit exceeded", receivedError);
            Assert.IsFalse(_roomManager.IsInRoom);
        }

        // ── HandleRoomPacket: JoinRoom Response ────────────────────────────────

        [Test]
        public void HandleJoinResponse_Success_FiresOnRoomJoined()
        {
            RoomInfo receivedRoom = null;
            _roomManager.OnRoomJoined += room => receivedRoom = room;

            var payload = BuildJoinRoomResponseOk("room-J", "JCODE1", "Lobby", 1, 16, true,
                new[] { ("player-1", "Alice", true, true) });
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.IsNotNull(receivedRoom);
            Assert.AreEqual("room-J", receivedRoom.RoomId);
            Assert.AreEqual(1, receivedRoom.Players.Length);
            Assert.AreEqual("Alice", receivedRoom.Players[0].DisplayName);
        }

        [Test]
        public void HandleJoinResponse_Failure_FiresOnRoomError()
        {
            string receivedError = null;
            _roomManager.OnRoomError += err => receivedError = err;

            var payload = BuildJoinRoomResponseError("room is full");
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.AreEqual("room is full", receivedError);
        }

        // ── HandleRoomPacket: PlayerJoined Notification ────────────────────────

        [Test]
        public void HandlePlayerJoined_FiresOnPlayerJoined()
        {
            PlayerInfo receivedPlayer = null;
            _roomManager.OnPlayerJoined += p => receivedPlayer = p;

            var payload = BuildPlayerJoinedNotification("player-3", "Charlie", false, false);
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.IsNotNull(receivedPlayer);
            Assert.AreEqual("player-3", receivedPlayer.PlayerId);
            Assert.AreEqual("Charlie", receivedPlayer.DisplayName);
        }

        // ── HandleRoomPacket: LeaveRoom Response ───────────────────────────────

        [Test]
        public void HandleLeaveResponse_Ok_ClearsCurrentRoom()
        {
            // First, simulate being in a room
            SimulateRoomCreated("room-X", "XCODE1");
            Assert.IsTrue(_roomManager.IsInRoom);

            bool leftFired = false;
            _roomManager.OnRoomLeft += () => leftFired = true;

            var payload = new byte[] { 0x00, 1 }; // msg_kind=Response, ok=true
            _roomManager.HandleRoomPacket(PacketType.RoomLeave, payload);

            Assert.IsTrue(leftFired);
            Assert.IsFalse(_roomManager.IsInRoom);
            Assert.IsNull(_roomManager.CurrentRoom);
        }

        [Test]
        public void HandleLeaveResponse_NotOk_FiresOnRoomError()
        {
            string receivedError = null;
            _roomManager.OnRoomError += err => receivedError = err;

            var payload = new byte[] { 0x00, 0 }; // msg_kind=Response, ok=false
            _roomManager.HandleRoomPacket(PacketType.RoomLeave, payload);

            Assert.IsNotNull(receivedError);
        }

        // ── HandleRoomPacket: PlayerLeft Notification ──────────────────────────

        [Test]
        public void HandlePlayerLeft_FiresOnPlayerLeft()
        {
            string leftPlayerId = null;
            _roomManager.OnPlayerLeft += id => leftPlayerId = id;

            var payload = BuildPlayerLeftNotification("player-99");
            _roomManager.HandleRoomPacket(PacketType.RoomLeave, payload);

            Assert.AreEqual("player-99", leftPlayerId);
        }

        // ── HandleRoomPacket: RoomList Response ────────────────────────────────

        [Test]
        public void HandleRoomListResponse_FiresOnRoomListReceived()
        {
            RoomInfo[] receivedRooms = null;
            _roomManager.OnRoomListReceived += rooms => receivedRooms = rooms;

            var payload = BuildRoomListResponse(new[]
            {
                ("room-1", "C1", "First", "waiting", 2, 16, true),
                ("room-2", "C2", "Second", "playing", 5, 8, false),
            });
            _roomManager.HandleRoomPacket(PacketType.RoomList, payload);

            Assert.IsNotNull(receivedRooms);
            Assert.AreEqual(2, receivedRooms.Length);
            Assert.AreEqual("room-1", receivedRooms[0].RoomId);
            Assert.AreEqual("room-2", receivedRooms[1].RoomId);
        }

        // ── ClearState ─────────────────────────────────────────────────────────

        [Test]
        public void ClearState_ResetsCurrentRoom()
        {
            SimulateRoomCreated("room-Z", "ZCODE1");
            Assert.IsTrue(_roomManager.IsInRoom);

            _roomManager.ClearState();

            Assert.IsFalse(_roomManager.IsInRoom);
            Assert.IsNull(_roomManager.CurrentRoom);
        }

        // ── PacketType in wire format ──────────────────────────────────────────

        [Test]
        public void CreateRoom_PacketHasReliableFlag()
        {
            _roomManager.CreateRoom();

            Assert.AreEqual(1, _sentPackets.Count);
            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketFlags.Reliable, pkt[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.Reliable,
                "Room packets should have the Reliable flag set");
        }

        [Test]
        public void JoinRoom_PacketHasReliableFlag()
        {
            _roomManager.JoinRoom("room-1");

            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketFlags.Reliable, pkt[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.Reliable);
        }

        [Test]
        public void LeaveRoom_PacketHasReliableFlag()
        {
            _currentState = NetworkState.InRoom;
            SimulateRoomCreated("room-1", "CODE01");
            _sentPackets.Clear();

            _roomManager.LeaveRoom();

            var pkt = _sentPackets[0];
            Assert.AreEqual((byte)PacketFlags.Reliable, pkt[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.Reliable);
        }

        // ── Sequence Number Increments ─────────────────────────────────────────

        [Test]
        public void MultipleOperations_SequenceNumbersIncrement()
        {
            _roomManager.CreateRoom();
            _roomManager.ListRooms();

            uint seq0 = ReadU32LE(_sentPackets[0], PacketProtocol.OFFSET_SEQUENCE);
            uint seq1 = ReadU32LE(_sentPackets[1], PacketProtocol.OFFSET_SEQUENCE);

            Assert.AreEqual(seq0 + 1, seq1, "Sequence numbers should increment");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void SimulateRoomCreated(string roomId, string roomCode)
        {
            var payload = BuildCreateRoomResponseOk(roomId, roomCode, 16);
            _roomManager.HandleRoomPacket(PacketType.RoomCreate, payload);
            _sentPackets.Clear();
        }

        private static byte[] BuildCreateRoomResponseOk(string roomId, string roomCode, int maxPlayers)
        {
            var ms = new SimpleStream();
            ms.WriteByte(1); // ok
            ms.WriteString(roomId);
            ms.WriteString(roomCode);
            ms.WriteByte((byte)maxPlayers);
            return ms.ToArray();
        }

        private static byte[] BuildCreateRoomResponseError(string error)
        {
            var ms = new SimpleStream();
            ms.WriteByte(0); // ok=false
            ms.WriteString(error);
            return ms.ToArray();
        }

        private static byte[] BuildJoinRoomResponseOk(
            string roomId, string roomCode, string name,
            int playerCount, int maxPlayers, bool isPublic,
            (string id, string display, bool host, bool ready)[] players)
        {
            var ms = new SimpleStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(1);    // ok
            ms.WriteString(roomId);
            ms.WriteString(roomCode);
            ms.WriteString(name);
            ms.WriteByte((byte)playerCount);
            ms.WriteByte((byte)maxPlayers);
            ms.WriteByte((byte)(isPublic ? 1 : 0));

            foreach (var p in players)
            {
                ms.WriteString(p.id);
                ms.WriteString(p.display);
                ms.WriteByte((byte)(p.host ? 1 : 0));
                ms.WriteByte((byte)(p.ready ? 1 : 0));
            }

            return ms.ToArray();
        }

        private static byte[] BuildJoinRoomResponseError(string error)
        {
            var ms = new SimpleStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(0);    // ok=false
            ms.WriteString(error);
            return ms.ToArray();
        }

        private static byte[] BuildPlayerJoinedNotification(
            string playerId, string displayName, bool isHost, bool isReady)
        {
            var ms = new SimpleStream();
            ms.WriteByte(0x01); // msg_kind = Notification
            ms.WriteString(playerId);
            ms.WriteString(displayName);
            ms.WriteByte((byte)(isHost ? 1 : 0));
            ms.WriteByte((byte)(isReady ? 1 : 0));
            return ms.ToArray();
        }

        private static byte[] BuildPlayerLeftNotification(string playerId)
        {
            var ms = new SimpleStream();
            ms.WriteByte(0x01); // msg_kind = Notification
            ms.WriteString(playerId);
            return ms.ToArray();
        }

        private static byte[] BuildRoomListResponse(
            (string id, string code, string name, string state, int playerCount, int maxPlayers, bool isPublic)[] rooms)
        {
            var ms = new SimpleStream();
            ms.WriteU16LE((ushort)rooms.Length);
            foreach (var r in rooms)
            {
                ms.WriteString(r.id);
                ms.WriteString(r.code);
                ms.WriteString(r.name);
                ms.WriteString(r.state);
                ms.WriteByte((byte)r.playerCount);
                ms.WriteByte((byte)r.maxPlayers);
                ms.WriteByte((byte)(r.isPublic ? 1 : 0));
            }
            return ms.ToArray();
        }

        private static uint ReadU32LE(byte[] buf, int offset)
            => (uint)(buf[offset] | (buf[offset + 1] << 8)
                    | (buf[offset + 2] << 16) | (buf[offset + 3] << 24));

        /// <summary>Simple growable byte buffer for building test payloads.</summary>
        private class SimpleStream
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

            public void WriteU16LE(ushort value)
            {
                EnsureCapacity(2);
                _buf[_pos++] = (byte)(value & 0xFF);
                _buf[_pos++] = (byte)(value >> 8);
            }

            public void WriteString(string value)
            {
                byte[] encoded = Encoding.UTF8.GetBytes(value ?? string.Empty);
                WriteU16LE((ushort)encoded.Length);
                Write(encoded);
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
    }
}
