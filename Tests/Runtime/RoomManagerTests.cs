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

        [Test]
        [Description("player_left prunes the leaver from the local roster (mirrors the kick path) — NEW-OWNERSHIP-1 precondition.")]
        public void HandlePlayerLeft_PrunesLeaverFromRoster()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "me",
                       players: new[] { ("host", "Host", true, true),
                                        ("a",    "A",    false, true),
                                        ("me",   "Me",   false, true) });

            _roomManager.HandleRoomPacket(PacketType.RoomLeave, BuildPlayerLeftNotification("a"));

            var ids = System.Array.ConvertAll(_roomManager.CurrentRoom.Players, p => p.PlayerId);
            Assert.AreEqual(2, _roomManager.CurrentRoom.Players.Length, "Leaver must be pruned.");
            CollectionAssert.DoesNotContain(ids, "a");
        }

        [Test]
        [Description("When the host leaves, pruning clears the stale IsHost so MasterId no longer resolves to the gone player.")]
        public void HandlePlayerLeft_DepartingHost_ClearsStaleMasterId()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "me",
                       players: new[] { ("host", "Host", true, true),
                                        ("me",   "Me",   false, true) });
            Assert.AreEqual("host", _roomManager.CurrentRoom.MasterId);

            _roomManager.HandleRoomPacket(PacketType.RoomLeave, BuildPlayerLeftNotification("host"));

            Assert.AreEqual(1, _roomManager.CurrentRoom.Players.Length);
            Assert.IsTrue(string.IsNullOrEmpty(_roomManager.CurrentRoom.MasterId),
                "Departed host must no longer be reported as MasterId.");
        }

        [Test]
        [Description("Pruning an id that is not on the roster is a safe no-op.")]
        public void HandlePlayerLeft_AbsentPlayer_RosterUnchanged()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "me",
                       players: new[] { ("host", "Host", true, true),
                                        ("me",   "Me",   false, true) });

            _roomManager.HandleRoomPacket(PacketType.RoomLeave, BuildPlayerLeftNotification("ghost"));

            Assert.AreEqual(2, _roomManager.CurrentRoom.Players.Length,
                "Pruning an absent id must be a no-op.");
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

        // ── Reserved-key validation (room properties) ──────────────────────────

        [Test]
        [Description("Non-master client writing a reserved-prefix key is blocked client-side: " +
                     "no packet is emitted and OnRoomError reports the rejected key.")]
        public void SetRoomProperties_NonMasterReservedKey_BlocksAndFiresError()
        {
            _currentState = NetworkState.InRoom;
            // Local player joins as a non-host on a roster where the master is a different player.
            JoinRoomAs(localPlayerId: "player-self",
                       players: new[] { ("player-master", "Host", true, true),
                                        ("player-self",   "Me",   false, true) });
            _sentPackets.Clear();

            string receivedError = null;
            _roomManager.OnRoomError += err => receivedError = err;

            _roomManager.SetRoomProperties(new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { "__scene", PropertyValue.OfString("Boss") },
            });

            Assert.AreEqual(0, _sentPackets.Count,
                "No packet should be emitted when a non-master writes a reserved key.");
            Assert.IsNotNull(receivedError, "OnRoomError should fire with a diagnostic.");
            StringAssert.Contains("__scene", receivedError);
            StringAssert.Contains("reserved", receivedError);
        }

        [Test]
        [Description("Master client may write reserved-prefix keys; the request is sent.")]
        public void SetRoomProperties_MasterReservedKey_SendsPacket()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-host",
                       players: new[] { ("player-host", "Host", true, true) });
            _sentPackets.Clear();

            _roomManager.SetRoomProperties(new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { "__scene", PropertyValue.OfString("Lobby") },
            });

            Assert.AreEqual(1, _sentPackets.Count,
                "Master client should be permitted to write reserved keys.");
        }

        [Test]
        [Description("Reserved-key block fails-fast — non-reserved keys in the same payload are " +
                     "NOT smuggled past the guard via partial submission.")]
        public void SetRoomProperties_NonMasterMixedReservedKeys_BlocksEntirePayload()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-self",
                       players: new[] { ("player-master", "Host", true, true),
                                        ("player-self",   "Me",   false, true) });
            _sentPackets.Clear();

            _roomManager.SetRoomProperties(new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { "score",  PropertyValue.OfInt(10) },
                { "__scene", PropertyValue.OfString("Boss") },
            });

            Assert.AreEqual(0, _sentPackets.Count,
                "A reserved key anywhere in the payload must reject the entire request.");
        }

        [Test]
        [Description("Non-master writing only non-reserved keys is permitted (server enforces master).")]
        public void SetRoomProperties_NonMasterNonReservedKey_SendsPacket()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-self",
                       players: new[] { ("player-master", "Host", true, true),
                                        ("player-self",   "Me",   false, true) });
            _sentPackets.Clear();

            _roomManager.SetRoomProperties(new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { "score", PropertyValue.OfInt(10) },
            });

            Assert.AreEqual(1, _sentPackets.Count,
                "Non-reserved keys must still be sent; the gateway is the authority on master-only writes.");
        }

        [Test]
        public void IsMasterClient_LocalIsHost_ReturnsTrue()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-host",
                       players: new[] { ("player-host", "Host", true, true) });

            Assert.IsTrue(_roomManager.IsMasterClient);
        }

        [Test]
        public void IsMasterClient_LocalIsNotHost_ReturnsFalse()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-self",
                       players: new[] { ("player-master", "Host", true, true),
                                        ("player-self",   "Me",   false, true) });

            Assert.IsFalse(_roomManager.IsMasterClient);
        }

        [Test]
        public void IsMasterClient_NotInRoom_ReturnsFalse()
        {
            Assert.IsFalse(_roomManager.IsMasterClient);
        }

        // ── Implicit room-switch ──────────────────────────────────────────────

        [Test]
        [Description("Joining room B while still in room A fires OnRoomLeft for the displaced room " +
                     "before OnRoomJoined for the new room — the gateway treats this as leave-then-join.")]
        public void HandleJoinResponse_SwitchRoom_FiresOnRoomLeftForPriorRoom()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-1",
                       roomId: "room-A",
                       players: new[] { ("player-1", "Me", true, true) });

            int leftFiredCount = 0;
            int joinedFiredCount = 0;
            _roomManager.OnRoomLeft   += () => leftFiredCount++;
            _roomManager.OnRoomJoined += _ => joinedFiredCount++;

            // Now the server delivers a Join response for room-B.
            var payload = BuildJoinRoomResponseOkWithLocalId(
                "room-B", "BCODE1", "Boss", 1, 16, true,
                new[] { ("player-1", "Me", true, true) },
                localPlayerId: "player-1");
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.AreEqual(1, leftFiredCount,
                "OnRoomLeft should fire exactly once for the displaced room.");
            Assert.AreEqual(1, joinedFiredCount,
                "OnRoomJoined fires exactly once for the new room.");
            Assert.AreEqual("room-B", _roomManager.CurrentRoom.RoomId);
        }

        [Test]
        [Description("On an implicit room-switch the OnRoomLeft listener chain runs synchronously and " +
                     "completes before _currentRoom is reassigned and before OnRoomJoined fires; " +
                     "during OnRoomLeft, CurrentRoom is null so listeners observe a coherent " +
                     "'between rooms' snapshot rather than the stale prior room paired with a " +
                     "post-leave _state.")]
        public void HandleJoinResponse_SwitchRoom_OnRoomLeftRunsBeforeRoomFlipAndOnRoomJoined()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-1",
                       roomId: "room-A",
                       players: new[] { ("player-1", "Me", true, true) });

            var sequence = new List<string>();
            bool roomVisibleInsideLeftListener = true;

            // Capture the room snapshot visible to the OnRoomLeft listener.
            // CurrentRoom is nulled before OnRoomLeft fires so listeners that
            // run inside this stack frame (e.g. SpawnManager.ClearAll user
            // OnNetworkDespawn callbacks) see a coherent transitional state
            // — neither room-A nor room-B is "current".
            _roomManager.OnRoomLeft += () =>
            {
                sequence.Add("left");
                roomVisibleInsideLeftListener = _roomManager.CurrentRoom != null;
            };
            _roomManager.OnRoomJoined += _ =>
            {
                sequence.Add("joined");
            };

            var payload = BuildJoinRoomResponseOkWithLocalId(
                "room-B", "BCODE1", "Boss", 1, 16, true,
                new[] { ("player-1", "Me", true, true) },
                localPlayerId: "player-1");
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.AreEqual(2, sequence.Count, "Both OnRoomLeft and OnRoomJoined must fire.");
            Assert.AreEqual("left",   sequence[0], "OnRoomLeft must fire first.");
            Assert.AreEqual("joined", sequence[1], "OnRoomJoined must fire after OnRoomLeft completes.");
            Assert.IsFalse(roomVisibleInsideLeftListener,
                "Inside the OnRoomLeft handler CurrentRoom must be null — listeners that run " +
                "synchronously on this stack frame (e.g. SpawnManager.ClearAll user despawn " +
                "callbacks) must see a coherent 'between rooms' snapshot, not the stale prior room.");
            Assert.AreEqual("room-B", _roomManager.CurrentRoom.RoomId,
                "After both listeners run, the new room must be in place.");
        }

        [Test]
        [Description("Rejoining the same room id (e.g. reliable retransmit echo) is idempotent — no " +
                     "OnRoomLeft fires, OnRoomJoined fires once for the unchanged membership.")]
        public void HandleJoinResponse_SameRoomId_DoesNotFireOnRoomLeft()
        {
            _currentState = NetworkState.InRoom;
            JoinRoomAs(localPlayerId: "player-1",
                       roomId: "room-A",
                       players: new[] { ("player-1", "Me", true, true) });

            int leftFiredCount = 0;
            _roomManager.OnRoomLeft += () => leftFiredCount++;

            var payload = BuildJoinRoomResponseOkWithLocalId(
                "room-A", "ACODE1", "Lobby", 1, 16, true,
                new[] { ("player-1", "Me", true, true) },
                localPlayerId: "player-1");
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.AreEqual(0, leftFiredCount,
                "Rejoining the same room must not raise a spurious OnRoomLeft.");
        }

        [Test]
        [Description("First Join (no prior room) does not synthesise OnRoomLeft.")]
        public void HandleJoinResponse_FirstJoin_DoesNotFireOnRoomLeft()
        {
            _currentState = NetworkState.Connected;
            int leftFiredCount = 0;
            _roomManager.OnRoomLeft += () => leftFiredCount++;

            var payload = BuildJoinRoomResponseOkWithLocalId(
                "room-A", "ACODE1", "Lobby", 1, 16, true,
                new[] { ("player-1", "Me", true, true) },
                localPlayerId: "player-1");
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);

            Assert.AreEqual(0, leftFiredCount,
                "First-time join must never fire OnRoomLeft.");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void JoinRoomAs(
            string localPlayerId,
            (string id, string display, bool host, bool ready)[] players,
            string roomId = "room-1",
            string roomCode = "CODE01")
        {
            var payload = BuildJoinRoomResponseOkWithLocalId(
                roomId, roomCode, "Lobby", players.Length, 16, true, players, localPlayerId);
            _roomManager.HandleRoomPacket(PacketType.RoomJoin, payload);
            _sentPackets.Clear();
        }

        private static byte[] BuildJoinRoomResponseOkWithLocalId(
            string roomId, string roomCode, string name,
            int playerCount, int maxPlayers, bool isPublic,
            (string id, string display, bool host, bool ready)[] players,
            string localPlayerId)
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

            ms.WriteString(localPlayerId ?? string.Empty);
            return ms.ToArray();
        }

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
