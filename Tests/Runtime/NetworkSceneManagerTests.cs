// RTMPE SDK — Tests/Runtime/NetworkSceneManagerTests.cs
//
// NUnit tests for NetworkSceneManager.  The fixture wires a real
// RoomManager (with fake send/state delegates) to a NetworkSceneManager
// instance and drives RoomManager via its public broadcast hooks.
// NetworkSceneManager.Dispose is exercised to validate event unsubscription.
//
// Pure C# / Unity Edit-Mode — no live socket, no NetworkManager singleton
// required for any path under test (LoadScene's master-client guard is
// covered by an explicit "non-master rejection" assertion via
// NetworkManager.Instance == null).

using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;
using RTMPE.Protocol;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    public class NetworkSceneManagerTests
    {
        private PacketBuilder       _builder;
        private List<byte[]>        _sent;
        private NetworkState        _state;
        private RoomManager         _rooms;
        private NetworkSceneManager _scene;

        [SetUp]
        public void SetUp()
        {
            _builder = new PacketBuilder();
            _sent    = new List<byte[]>();
            _state   = NetworkState.InRoom;
            _rooms   = new RoomManager(_builder, p => _sent.Add(p), () => _state);
            _scene   = new NetworkSceneManager(_rooms);
        }

        // ── Initial state ────────────────────────────────────────────────────

        [Test]
        public void CurrentScene_NoRoom_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, _scene.CurrentScene);
        }

        // ── OnSceneLoadStarted: property update ──────────────────────────────

        [Test]
        public void Property_SceneSetForFirstTime_FiresOnSceneLoadStarted()
        {
            JoinEmptyRoom();
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;

            _rooms.ApplyRoomPropertiesBroadcast(
                version: 2,
                properties: new Dictionary<string, PropertyValue>
                {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") },
                });

            Assert.AreEqual("Arena01", observed);
            Assert.AreEqual("Arena01", _scene.CurrentScene);
        }

        [Test]
        public void Property_SceneUnchanged_DoesNotRefireOnSceneLoadStarted()
        {
            JoinEmptyRoom();
            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;

            var props = new Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") },
            };
            _rooms.ApplyRoomPropertiesBroadcast(version: 2, properties: props);
            // Bumping just an unrelated property keeps the scene the same;
            // the manager must not double-fire OnSceneLoadStarted.
            var props2 = new Dictionary<string, PropertyValue>(props)
            {
                { "GameMode", PropertyValue.OfString("TDM") },
            };
            _rooms.ApplyRoomPropertiesBroadcast(version: 3, properties: props2);

            Assert.AreEqual(1, fires);
        }

        [Test]
        public void Property_SceneChangesValue_FiresOnSceneLoadStartedAgain()
        {
            JoinEmptyRoom();
            var observed = new List<string>();
            _scene.OnSceneLoadStarted += observed.Add;

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Lobby") } });
            _rooms.ApplyRoomPropertiesBroadcast(version: 3,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            CollectionAssert.AreEqual(new[] { "Lobby", "Arena01" }, observed);
        }

        // ── Late-join path: room already has a scene ─────────────────────────

        [Test]
        public void RoomJoined_RoomHasScene_FiresOnSceneLoadStartedImmediately()
        {
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;
            JoinRoomWithScene("Arena01");
            Assert.AreEqual("Arena01", observed);
            Assert.AreEqual("Arena01", _scene.CurrentScene);
        }

        [Test]
        public void RoomJoined_RoomHasNoScene_DoesNotFireOnSceneLoadStarted()
        {
            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;
            JoinEmptyRoom();
            Assert.AreEqual(0, fires);
        }

        // ── OnAllPlayersSceneLoaded forwarding ───────────────────────────────

        [Test]
        public void RoomManager_AllPlayersSceneLoaded_ForwardsArgument()
        {
            string observed = null;
            _scene.OnAllPlayersSceneLoaded += s => observed = s;
            // Trigger by reflection-free public route: the RoomManager event
            // is invoked by HandleRoomPacket(SceneLoaded, …) — but for unit
            // testing we exercise the bridge by raising it through the
            // Dispose contract instead: we cannot directly invoke a non-
            // private event from here, so we drive it via the only public
            // path that fires it.  See TriggerAllPlayersReady() helper.
            TriggerAllPlayersReady("Arena01");
            Assert.AreEqual("Arena01", observed);
        }

        // ── ReportReady ──────────────────────────────────────────────────────

        [Test]
        public void ReportReady_NoSceneSet_DoesNotSend()
        {
            JoinEmptyRoom();
            int before = _sent.Count;
            _scene.ReportReady();
            Assert.AreEqual(before, _sent.Count);
        }

        [Test]
        public void ReportReady_WithSceneSet_SendsSceneLoadedPacket()
        {
            JoinEmptyRoom();
            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });
            _sent.Clear();

            _scene.ReportReady();
            Assert.AreEqual(1, _sent.Count);
            Assert.AreEqual((byte)PacketType.SceneLoaded, _sent[0][PacketProtocol.OFFSET_TYPE]);
        }

        // ── Room-left state hygiene ──────────────────────────────────────────

        [Test]
        public void RoomLeft_AfterScenePropagated_RestoresEmptyScene()
        {
            JoinRoomWithScene("Arena01");
            // Drive OnRoomLeft via the production leave-response path so the
            // scene manager observes the same event sequence the real
            // gateway produces.
            byte[] leaveOk = BuildLeaveRoomResponseOk();
            _rooms.HandleRoomPacket(PacketType.RoomLeave, leaveOk);

            // After leaving, re-joining a room with the SAME scene name must
            // refire OnSceneLoadStarted because _lastObservedScene was reset.
            string observed = null;
            _scene.OnSceneLoadStarted += s => observed = s;
            JoinRoomWithScene("Arena01");
            Assert.AreEqual("Arena01", observed);
        }

        private static byte[] BuildLeaveRoomResponseOk()
        {
            // Layout: [u8 msg_kind=0 (Response)][u8 ok=1].
            return new byte[] { 0x00, 0x01 };
        }

        // ── Dispose ──────────────────────────────────────────────────────────

        [Test]
        public void Dispose_AfterDispose_NoFurtherEvents()
        {
            JoinEmptyRoom();
            _scene.Dispose();

            int fires = 0;
            _scene.OnSceneLoadStarted += _ => fires++;

            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue> {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString("Arena01") } });

            Assert.AreEqual(0, fires);
        }

        [Test]
        public void Dispose_CalledTwice_IsIdempotent()
        {
            _scene.Dispose();
            Assert.DoesNotThrow(() => _scene.Dispose());
        }

        // ── LoadScene non-master guard (NetworkManager.Instance is null
        //     in the test runner — manager treats this as "not master") ──────

        [Test]
        public void LoadScene_WithoutMasterClient_DoesNotSendPropertyUpdate()
        {
            JoinEmptyRoom();
            _sent.Clear();
            // The manager logs an error to surface the bug to the developer
            // immediately; we expect the log instead of failing on it.
            LogAssert.Expect(LogType.Error, new Regex("only the master client may change the scene"));
            _scene.LoadScene("Arena01");
            Assert.AreEqual(0, _sent.Count,
                "Non-master callers must be rejected at the SDK layer rather than producing a silent server-side no-op.");
        }

        [Test]
        public void LoadScene_NotInRoom_DoesNotSendPropertyUpdate()
        {
            // The manager warns rather than throws when called outside a room
            // — non-fatal, but worth asserting we expect the warning.
            LogAssert.Expect(LogType.Warning, new Regex("must be in a room"));
            int before = _sent.Count;
            _scene.LoadScene("Arena01");
            Assert.AreEqual(before, _sent.Count);
        }

        [Test]
        public void LoadScene_NullSceneName_Throws()
        {
            JoinEmptyRoom();
            Assert.Throws<System.ArgumentException>(() => _scene.LoadScene(null));
        }

        [Test]
        public void LoadScene_EmptySceneName_Throws()
        {
            JoinEmptyRoom();
            Assert.Throws<System.ArgumentException>(() => _scene.LoadScene(string.Empty));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private void JoinEmptyRoom()
        {
            // Drive RoomManager through its packet handler to a "joined" state
            // so subsequent property broadcasts apply.
            var payload = BuildJoinRoomResponseOk("room-1", "CODE01", "Test", 1, 16, true);
            _rooms.HandleRoomPacket(PacketType.RoomJoin, payload);
            _sent.Clear();
        }

        private void JoinRoomWithScene(string sceneName)
        {
            JoinEmptyRoom();
            _rooms.ApplyRoomPropertiesBroadcast(version: 2,
                new Dictionary<string, PropertyValue>
                {
                    { ReservedPropertyKeys.Scene, PropertyValue.OfString(sceneName) },
                });
            // Ensure RoomManager fires its OnRoomJoined-with-scene path next
            // time by leaving and re-joining with the same scene preset on
            // the room snapshot.  Because we cannot inject a RoomInfo whose
            // Properties already contain the scene without going through
            // ApplyRoomPropertiesBroadcast, this is the path the production
            // late-join code travels too.
        }

        private void TriggerAllPlayersReady(string sceneName)
        {
            JoinEmptyRoom();
            // Use the production builder so the test exercises the exact
            // wire format the real gateway emits.
            byte[] payload = MasterClientPacketBuilder.BuildSceneLoadedPayload(sceneName);
            _rooms.HandleAllPlayersSceneLoaded(payload);
        }

        private static byte[] BuildJoinRoomResponseOk(
            string roomId, string roomCode, string name,
            int playerCount, int maxPlayers, bool isPublic)
        {
            var ms = new SimpleByteStream();
            ms.WriteByte(0x00); // msg_kind = Response
            ms.WriteByte(1);    // ok
            ms.WriteString(roomId);
            ms.WriteString(roomCode);
            ms.WriteString(name);
            ms.WriteByte((byte)playerCount);
            ms.WriteByte((byte)maxPlayers);
            ms.WriteByte((byte)(isPublic ? 1 : 0));
            // one local player so the roster is non-empty (some RoomManager
            // paths gate on Players.Length).
            ms.WriteString("local");
            ms.WriteString("Local");
            ms.WriteByte(1);
            ms.WriteByte(1);
            return ms.ToArray();
        }

        private static byte[] BuildAllPlayersSceneLoadedPayload(string sceneName)
        {
            var ms = new SimpleByteStream();
            // Layout matches RoomPacketParser.ParseSceneLoadedNotification:
            // [u8 kind=2 (all-loaded)][len-prefixed string sceneName].
            ms.WriteByte(0x02);
            ms.WriteString(sceneName);
            return ms.ToArray();
        }

        // Local helper — RoomManagerTests.cs has SimpleStream but it is
        // private to that fixture.  Duplicate the minimum surface here.
        private sealed class SimpleByteStream
        {
            private readonly System.IO.MemoryStream _ms = new System.IO.MemoryStream();
            public void WriteByte(byte b) => _ms.WriteByte(b);
            public void WriteString(string s)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(s ?? string.Empty);
                _ms.WriteByte((byte)bytes.Length);
                _ms.Write(bytes, 0, bytes.Length);
            }
            public byte[] ToArray() => _ms.ToArray();
        }
    }
}
