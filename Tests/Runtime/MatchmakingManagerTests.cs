// RTMPE SDK — Tests/Runtime/MatchmakingManagerTests.cs
//
// NUnit tests for MatchmakingManager.  The fixture wires fake delegates
// for send/state/playerId so the manager can be constructed without
// the full NetworkManager.  Every public path is exercised
// deterministically — there is no timing dependency anywhere in this
// suite.
//
// Pure C# / Unity Edit-Mode — no live socket, no NetworkManager singleton.

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
    public class MatchmakingManagerTests
    {
        private PacketBuilder      _builder;
        private List<byte[]>       _sent;
        private NetworkState       _state;
        private string             _playerId;
        private MatchmakingManager _mm;

        [SetUp]
        public void SetUp()
        {
            _builder  = new PacketBuilder();
            _sent     = new List<byte[]>();
            _state    = NetworkState.Connected;
            _playerId = "player-1";
            _mm       = new MatchmakingManager(_builder, p => _sent.Add(p), () => _state, () => _playerId);
        }

        // ── Construction ─────────────────────────────────────────────────────

        [Test]
        public void Ctor_NullBuilder_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MatchmakingManager(null, _ => { }, () => NetworkState.Connected, () => "p"));
        }

        [Test]
        public void Ctor_NullSend_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MatchmakingManager(_builder, null, () => NetworkState.Connected, () => "p"));
        }

        [Test]
        public void Ctor_NullStateGetter_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MatchmakingManager(_builder, _ => { }, null, () => "p"));
        }

        [Test]
        public void Ctor_NullPlayerIdGetter_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new MatchmakingManager(_builder, _ => { }, () => NetworkState.Connected, null));
        }

        // ── StartMatchmaking — argument validation ───────────────────────────

        [Test]
        public void Start_NullOptions_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => _mm.StartMatchmaking(null));
        }

        [Test]
        public void Start_EmptyMode_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                _mm.StartMatchmaking(new MatchmakingOptions { Mode = "" }));
        }

        [Test]
        public void Start_NullMode_Throws()
        {
            Assert.Throws<ArgumentException>(() =>
                _mm.StartMatchmaking(new MatchmakingOptions { Mode = null }));
        }

        // ── StartMatchmaking — state guard ───────────────────────────────────

        [Test]
        public void Start_WhenDisconnected_Throws()
        {
            _state = NetworkState.Disconnected;
            Assert.Throws<InvalidOperationException>(() =>
                _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" }));
            Assert.AreEqual(0, _sent.Count);
        }

        [Test]
        public void Start_WhenConnecting_Throws()
        {
            _state = NetworkState.Connecting;
            Assert.Throws<InvalidOperationException>(() =>
                _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" }));
        }

        [Test]
        public void Start_WhenConnected_SendsMatchmakingRequestPacket()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            Assert.AreEqual(1, _sent.Count);
            byte type = _sent[0][PacketProtocol.OFFSET_TYPE];
            Assert.AreEqual((byte)PacketType.MatchmakingRequest, type);
        }

        [Test]
        public void Start_WhenInRoom_AlsoSends()
        {
            _state = NetworkState.InRoom;
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            Assert.AreEqual(1, _sent.Count);
        }

        [Test]
        public void Start_PacketCarriesReliableFlag()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            byte flags = _sent[0][PacketProtocol.OFFSET_FLAGS];
            Assert.AreEqual((byte)PacketFlags.Reliable, flags & (byte)PacketFlags.Reliable);
        }

        [Test]
        public void Start_PayloadIsValidJsonContainingMode()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM", DisplayName = "Alice" });
            string json = ExtractPayloadJson(_sent[0]);
            StringAssert.Contains("\"mode\":\"TDM\"", json);
            StringAssert.Contains("\"player_id\":\"player-1\"", json);
            StringAssert.Contains("\"display_name\":\"Alice\"", json);
        }

        [Test]
        public void Start_OmitsLobbyName_WhenNotSet()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            string json = ExtractPayloadJson(_sent[0]);
            Assert.IsFalse(json.Contains("lobby_name"),
                "Empty LobbyName must not be serialized — the server treats absent vs empty differently.");
        }

        [Test]
        public void Start_OmitsMinMaxPlayers_WhenZero()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            string json = ExtractPayloadJson(_sent[0]);
            Assert.IsFalse(json.Contains("min_players"));
            Assert.IsFalse(json.Contains("max_players"));
        }

        [Test]
        public void Start_IncludesMinMaxPlayers_WhenSet()
        {
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM", MinPlayers = 4, MaxPlayers = 8 });
            string json = ExtractPayloadJson(_sent[0]);
            StringAssert.Contains("\"min_players\":4", json);
            StringAssert.Contains("\"max_players\":8", json);
        }

        [Test]
        public void Start_NullPlayerId_SerializesAsEmptyString()
        {
            _playerId = null;
            _mm.StartMatchmaking(new MatchmakingOptions { Mode = "TDM" });
            string json = ExtractPayloadJson(_sent[0]);
            StringAssert.Contains("\"player_id\":\"\"", json);
        }

        // ── HandleMatchmakingResponse — failure paths ────────────────────────

        [Test]
        public void Handle_EmptyPayload_FiresOnMatchmakingFailed()
        {
            string err = null;
            _mm.OnMatchmakingFailed += e => err = e;
            _mm.HandleMatchmakingResponseForTest(new byte[0]);
            Assert.AreEqual("empty response", err);
        }

        [Test]
        public void Handle_NullPayload_FiresOnMatchmakingFailed()
        {
            string err = null;
            _mm.OnMatchmakingFailed += e => err = e;
            _mm.HandleMatchmakingResponseForTest(null);
            Assert.AreEqual("empty response", err);
        }

        [Test]
        public void Handle_OkFalseWithError_FiresOnMatchmakingFailedWithMessage()
        {
            string err = null;
            _mm.OnMatchmakingFailed += e => err = e;
            byte[] payload = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"no rooms available\"}");
            _mm.HandleMatchmakingResponseForTest(payload);
            Assert.AreEqual("no rooms available", err);
        }

        [Test]
        public void Handle_OkFalseWithoutError_FiresGenericFailureMessage()
        {
            string err = null;
            _mm.OnMatchmakingFailed += e => err = e;
            byte[] payload = Encoding.UTF8.GetBytes("{\"ok\":false}");
            _mm.HandleMatchmakingResponseForTest(payload);
            Assert.AreEqual("matchmaking failed", err);
        }

        // ── HandleMatchmakingResponse — success paths ────────────────────────

        [Test]
        public void Handle_OkTrueWithData_FiresOnMatchmakingComplete()
        {
            MatchmakingResult result = null;
            _mm.OnMatchmakingComplete += r => result = r;
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"ok\":true,\"data\":{\"room_id\":\"room-7\",\"room_code\":\"ABC123\",\"created\":true}}");
            _mm.HandleMatchmakingResponseForTest(payload);

            Assert.IsNotNull(result);
            Assert.AreEqual("room-7", result.RoomId);
            Assert.AreEqual("ABC123", result.RoomCode);
            Assert.IsTrue(result.Created);
        }

        [Test]
        public void Handle_OkTrueWithCreatedFalse_PreservesFlag()
        {
            MatchmakingResult result = null;
            _mm.OnMatchmakingComplete += r => result = r;
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"ok\":true,\"data\":{\"room_id\":\"room-7\",\"room_code\":\"ABC123\",\"created\":false}}");
            _mm.HandleMatchmakingResponseForTest(payload);

            Assert.IsNotNull(result);
            Assert.IsFalse(result.Created);
        }

        [Test]
        public void Handle_OkTrueWithoutDataObject_FiresWithEmptyFields()
        {
            // Defensive path: the parser must still fire OnMatchmakingComplete
            // (so the application is not left waiting), but with empty fields
            // the developer can detect.
            MatchmakingResult result = null;
            _mm.OnMatchmakingComplete += r => result = r;
            byte[] payload = Encoding.UTF8.GetBytes("{\"ok\":true}");
            _mm.HandleMatchmakingResponseForTest(payload);

            Assert.IsNotNull(result);
            Assert.AreEqual(string.Empty, result.RoomId);
            Assert.AreEqual(string.Empty, result.RoomCode);
            Assert.IsFalse(result.Created);
        }

        [Test]
        public void Handle_OkTrue_DoesNotFireFailedEvent()
        {
            int failureCount = 0;
            _mm.OnMatchmakingFailed += _ => failureCount++;
            byte[] payload = Encoding.UTF8.GetBytes(
                "{\"ok\":true,\"data\":{\"room_id\":\"r\",\"room_code\":\"c\",\"created\":false}}");
            _mm.HandleMatchmakingResponseForTest(payload);
            Assert.AreEqual(0, failureCount);
        }

        [Test]
        public void Handle_OkFalse_DoesNotFireCompleteEvent()
        {
            int completeCount = 0;
            _mm.OnMatchmakingComplete += _ => completeCount++;
            byte[] payload = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"x\"}");
            _mm.HandleMatchmakingResponseForTest(payload);
            Assert.AreEqual(0, completeCount);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string ExtractPayloadJson(byte[] packet)
        {
            // Header is 13 bytes (PacketProtocol.HEADER_SIZE).  Strip and
            // decode the remainder as UTF-8 JSON.
            int header = PacketProtocol.HEADER_SIZE;
            return Encoding.UTF8.GetString(packet, header, packet.Length - header);
        }
    }

    // The fixture invokes the internal HandleMatchmakingResponse via this
    // adapter to keep the test surface independent of compiler-version
    // accessibility quirks.  Lives in the same namespace + InternalsVisibleTo
    // grant.
    internal static class MatchmakingManagerTestAccess
    {
        public static void HandleMatchmakingResponseForTest(this MatchmakingManager m, byte[] payload)
            => m.HandleMatchmakingResponse(payload);
    }
}
