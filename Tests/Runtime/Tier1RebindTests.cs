// RTMPE SDK — Tests/Runtime/Tier1RebindTests.cs
//
// Tier-1 sprint: verifies that NetworkSceneManager and LocalPlayerContext
// rebind their RoomManager reference after a Reconnect-style swap.  These
// tests use a Func<RoomManager> provider exactly the way NetworkManager
// does in production, then mutate the underlying field to simulate the
// effect of RecreateRoomAndSpawnManagers().
//
// Closes audit C-04 ("LocalPlayerContext and NetworkSceneManager hold stale
// RoomManager references after reconnect").  No Unity engine APIs are
// touched, so these run in Edit-Mode Test Runner without a scene.

using System.Collections.Generic;
using System.IO;
using System.Text;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Protocol;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Rooms")]
    [Category("Tier1")]
    public class Tier1RebindTests
    {
        // ── Test rig ────────────────────────────────────────────────────────

        private NetworkState _state;
        private List<byte[]> _sentA;
        private List<byte[]> _sentB;
        private RoomManager  _roomA;
        private RoomManager  _roomB;
        private RoomManager  _live;     // simulates NetworkManager._roomManager

        [SetUp]
        public void SetUp()
        {
            _state = NetworkState.Connected;
            _sentA = new List<byte[]>();
            _sentB = new List<byte[]>();

            _roomA = new RoomManager(new PacketBuilder(), p => _sentA.Add(p), () => _state);
            _roomB = new RoomManager(new PacketBuilder(), p => _sentB.Add(p), () => _state);
            _live  = _roomA;
        }

        // ── LocalPlayerContext rebinds across a RoomManager swap ────────────

        [Test]
        public void LocalPlayer_SetProperty_RoutesToLiveRoomManager_BeforeSwap()
        {
            // Mimic the lazy-init in NetworkManager.LocalPlayer (provider
            // closure that captures the field, not the instance).
            var ctx = new LocalPlayerContext(() => _live, () => "player-1");

            // Bring _roomA into a state where SetPlayerProperties will succeed
            // — RoomManager requires CurrentRoom to be set so the call is not
            // rejected by RequireInRoom.
            JoinRoom(_roomA, "room-A", "ACODE1", "player-1", _sentA);

            ctx.SetProperty("color", PropertyValue.OfString("red"));

            Assert.AreEqual(1, _sentA.Count, "Property write must reach roomA's send queue.");
            Assert.AreEqual(0, _sentB.Count);
        }

        [Test]
        public void LocalPlayer_SetProperty_RoutesToFreshRoomManager_AfterSwap()
        {
            var ctx = new LocalPlayerContext(() => _live, () => "player-1");

            JoinRoom(_roomA, "room-A", "ACODE1", "player-1", _sentA);
            ctx.SetProperty("color", PropertyValue.OfString("red"));
            Assert.AreEqual(1, _sentA.Count);

            // Reconnect: NetworkManager swaps _roomManager to a fresh
            // RoomManager.  The application STILL holds the old ctx.
            _live = _roomB;
            JoinRoom(_roomB, "room-A", "ACODE1", "player-1", _sentB);

            ctx.SetProperty("size", PropertyValue.OfInt(42));

            Assert.AreEqual(1, _sentA.Count, "Post-swap write must NOT reach the dead manager.");
            Assert.AreEqual(1, _sentB.Count, "Post-swap write must reach the fresh manager.");
        }

        [Test]
        public void LocalPlayer_SetProperties_RoutesToFreshRoomManager_AfterSwap()
        {
            var ctx = new LocalPlayerContext(() => _live, () => "player-1");
            JoinRoom(_roomA, "room-A", "ACODE1", "player-1", _sentA);

            _live = _roomB;
            JoinRoom(_roomB, "room-A", "ACODE1", "player-1", _sentB);

            ctx.SetProperties(new Dictionary<string, PropertyValue>
            {
                { "k1", PropertyValue.OfString("v1") },
                { "k2", PropertyValue.OfInt(7) },
            });

            Assert.AreEqual(0, _sentA.Count);
            Assert.AreEqual(1, _sentB.Count);
        }

        [Test]
        public void LocalPlayer_LegacyConstructor_StillBindsFixedReference()
        {
            // Back-compat: LocalPlayerContext(RoomManager, Func<string>) was
            // the original constructor.  Existing test fixtures and out-of-
            // tree consumers MUST keep working.
            JoinRoom(_roomA, "room-A", "ACODE1", "player-1", _sentA);
            var ctx = new LocalPlayerContext(_roomA, () => "player-1");
            ctx.SetProperty("k", PropertyValue.OfString("v"));

            Assert.AreEqual(1, _sentA.Count);
        }

        // ── NetworkSceneManager rebinds across a RoomManager swap ───────────

        [Test]
        public void SceneManager_OnSceneLoadStarted_FiresFromFreshRoomManager()
        {
            string lastScene = null;
            int    fires     = 0;

            var sm = new NetworkSceneManager(() => _live);

            sm.OnSceneLoadStarted += s => { lastScene = s; fires++; };

            // Stage 1: roomA emits a property broadcast — handler fires.
            JoinRoomWithScene(_roomA, "room-A", "ACODE1", "player-1", "Lobby", _sentA);
            Assert.AreEqual(1, fires, "OnSceneLoadStarted must fire on the FIRST RoomManager.");
            Assert.AreEqual("Lobby", lastScene);

            // Stage 2: swap to roomB.  The OLD subscription on roomA must
            // NOT cause a second fire when roomA is later poked.
            _live = _roomB;
            // Touching the manager forces EnsureBound to run via the
            // CurrentScene getter — the public surface application code
            // routinely hits.  This simulates the natural moment when the
            // application notices the disconnect and queries scene state.
            var _scene = sm.CurrentScene;

            // Late event from the dead manager — must be ignored.
            JoinRoomWithScene(_roomA, "room-A2", "ACODE2", "player-1", "Loading", _sentA);
            Assert.AreEqual("Lobby", lastScene,
                "Subscriptions to the dead RoomManager must be detached after rebind.");

            // Fresh event from roomB — must fire.
            JoinRoomWithScene(_roomB, "room-B", "BCODE1", "player-1", "Arena", _sentB);
            Assert.AreEqual("Arena", lastScene,
                "OnSceneLoadStarted must fire from the FRESH RoomManager after rebind.");
        }

        [Test]
        public void SceneManager_CurrentScene_ReadsFromFreshRoomManager()
        {
            JoinRoomWithScene(_roomA, "room-A", "ACODE1", "player-1", "Lobby", _sentA);
            var sm = new NetworkSceneManager(() => _live);
            Assert.AreEqual("Lobby", sm.CurrentScene);

            _live = _roomB;
            JoinRoomWithScene(_roomB, "room-B", "BCODE1", "player-1", "Arena", _sentB);

            Assert.AreEqual("Arena", sm.CurrentScene,
                "CurrentScene must mirror the live RoomManager, not the captured one.");
        }

        [Test]
        public void SceneManager_LegacyConstructor_StillBindsFixedReference()
        {
            var sm = new NetworkSceneManager(_roomA);
            int fires = 0;
            string captured = null;
            sm.OnSceneLoadStarted += s => { fires++; captured = s; };

            JoinRoomWithScene(_roomA, "room-A", "ACODE1", "player-1", "Lobby", _sentA);

            Assert.AreEqual(1, fires);
            Assert.AreEqual("Lobby", captured);
        }

        [Test]
        public void SceneManager_DoubleRebind_DoesNotDoubleFire()
        {
            var sm = new NetworkSceneManager(() => _live);
            int fires = 0;
            sm.OnSceneLoadStarted += _ => fires++;

            // First swap, then second swap (back to A).  The handler must
            // receive exactly one fire per RoomManager that publishes a
            // scene change — never duplicate fires from leaked subscriptions.
            JoinRoomWithScene(_roomA, "room-A", "ACODE1", "player-1", "Lobby", _sentA);
            Assert.AreEqual(1, fires);

            _live = _roomB; var _ = sm.CurrentScene;
            JoinRoomWithScene(_roomB, "room-B", "BCODE1", "player-1", "Arena", _sentB);
            Assert.AreEqual(2, fires);

            _live = _roomA; var __ = sm.CurrentScene;
            // _lastObservedScene is reset on rebind; "Lobby" appears as new
            // and re-fires — this is the documented contract: a rebind
            // delivers the live room's authoritative scene to the listener.
            // If the live room's scene matches the previously-displayed
            // value the application is responsible for no-op'ing in its
            // handler (CurrentScene is also exposed for this exact case).
            // Here we changed roomA's scene out from under us so a fresh
            // fire is the correct behaviour.
            JoinRoomWithScene(_roomA, "room-A2", "ACODE2", "player-1", "Final", _sentA);
            Assert.AreEqual(3, fires);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        // Drive a RoomJoin response into the given RoomManager so its
        // CurrentRoom snapshot is populated and OnRoomJoined fires.
        private static void JoinRoom(RoomManager rm, string roomId, string roomCode,
                                     string playerId, List<byte[]> sentSink)
        {
            var payload = BuildJoinRoomResponseOk(roomId, roomCode, "Lobby", 1, 16, true,
                new[] { (playerId, "P", true, true) });
            rm.HandleRoomPacket(PacketType.RoomJoin, payload);
            sentSink.Clear();
        }

        // Like JoinRoom but additionally publishes a __scene custom property
        // via RoomPropertyUpdate so OnRoomPropertiesChanged fires with a
        // RoomInfo whose CurrentScene returns the supplied scene name.
        // Increasing version counter shared across helper invocations so
        // monotonic-version guards in ApplyRoomPropertiesBroadcast accept
        // each subsequent push.
        private static int s_propVersion = 0;

        private static void JoinRoomWithScene(RoomManager rm, string roomId, string roomCode,
                                              string playerId, string sceneName,
                                              List<byte[]> sentSink)
        {
            JoinRoom(rm, roomId, roomCode, playerId, sentSink);
            var props = new Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene, PropertyValue.OfString(sceneName) },
            };
            rm.ApplyRoomPropertiesBroadcast(++s_propVersion, props);
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

        // Local mini-stream copy of the helper used by RoomManagerTests so
        // this fixture is self-contained.  Layout MUST match the wire
        // format used by RoomPacketParser.
        private sealed class SimpleStream
        {
            private readonly MemoryStream _ms = new MemoryStream();
            public void WriteByte(byte b) => _ms.WriteByte(b);
            public void WriteU16LE(ushort v)
            {
                _ms.WriteByte((byte)(v & 0xFF));
                _ms.WriteByte((byte)((v >> 8) & 0xFF));
            }
            public void WriteString(string s)
            {
                var bytes = Encoding.UTF8.GetBytes(s ?? string.Empty);
                WriteU16LE((ushort)bytes.Length);
                _ms.Write(bytes, 0, bytes.Length);
            }
            public byte[] ToArray() => _ms.ToArray();
        }
    }
}
