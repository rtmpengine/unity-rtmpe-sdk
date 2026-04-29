// RTMPE SDK — Tests/Runtime/LobbySecurityTests.cs
//
// Hardening-regression tests for the lobby parser and lobby-reply correlation:
//  • LobbyPacketParser — entry-count cap, per-string length cap, NUL/control
//                        rejection, escape decoding, depth bound.
//  • LobbyManager      — stray LobbyList reply must not flip IsInLobby; only
//                        a LobbyJoin reply with an outstanding pending-join
//                        (within timeout) consumes the slot.

using System.Reflection;
using System.Text;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Protocol;
using RTMPE.Rooms;

namespace RTMPE.Tests.Runtime
{
    [TestFixture]
    [Category("SecuritySDK")]
    public class LobbyPacketParserSecurityTests
    {
        // ── Entry-count cap ────────────────────────────────────────────

        [Test]
        [Description("lobby room list with > maxEntries is rejected (returns empty list).")]
        public void ParseRoomList_EntryCountAboveCap_Rejected()
        {
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < 1_000; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"room_id\":\"r");
                sb.Append(i);
                sb.Append("\",\"room_code\":\"AB\",\"name\":\"x\",\"player_count\":0,\"max_players\":4,\"is_public\":true,\"lobby_name\":\"\"}");
            }
            sb.Append(']');
            byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());

            var rooms = LobbyPacketParser.ParseRoomList(payload, maxEntries: 256, maxStringBytes: 256);
            Assert.AreEqual(0, rooms.Count,
                "Oversize lobby payload must produce an empty list (whole-payload reject).");
        }

        // ── Embedded NUL byte in a string field ────────────────────────

        [Test]
        [Description("an embedded raw NUL inside a string field aborts that field's decode.")]
        public void ParseRoomList_EmbeddedNul_FieldRejected()
        {
            // Construct a UTF-8 payload containing a literal 0x00 inside the room name.
            var prefix = Encoding.UTF8.GetBytes(
                "[{\"room_id\":\"r\",\"room_code\":\"\",\"name\":\"hello");
            var suffix = Encoding.UTF8.GetBytes(
                "world\",\"player_count\":0,\"max_players\":0,\"is_public\":false,\"lobby_name\":\"\"}]");
            var payload = new byte[prefix.Length + 1 + suffix.Length];
            System.Array.Copy(prefix, 0, payload, 0, prefix.Length);
            payload[prefix.Length] = 0x00; // embedded NUL inside the JSON string
            System.Array.Copy(suffix, 0, payload, prefix.Length + 1, suffix.Length);

            var rooms = LobbyPacketParser.ParseRoomList(payload, 256, 256);
            // Parser may yield zero rooms (whole row rejected) OR a row with empty name —
            // either way, the literal "hello\0world" must not have been accepted.
            if (rooms.Count > 0)
                Assert.AreNotEqual("hello\0world", rooms[0].Name,
                    "Raw NUL must never be merged into a string field.");
        }

        // ── Escape decoding ────────────────────────────────────────────

        [Test]
        [Description("\\\" inside a JSON string is decoded correctly and does NOT terminate the value.")]
        public void ParseRoomList_EscapedQuote_Decoded()
        {
            // {"room_id":"a\"b", ...}
            string json = "[{\"room_id\":\"a\\\"b\",\"room_code\":\"\",\"name\":\"\",\"player_count\":0,\"max_players\":0,\"is_public\":false,\"lobby_name\":\"\"}]";
            byte[] payload = Encoding.UTF8.GetBytes(json);

            var rooms = LobbyPacketParser.ParseRoomList(payload, 256, 256);
            Assert.AreEqual(1, rooms.Count, "single-row decode");
            Assert.AreEqual("a\"b", rooms[0].RoomId,
                "Escaped \\\" must be decoded to a literal '\"'.");
        }

        // ── String length cap ──────────────────────────────────────────

        [Test]
        [Description("a string field longer than maxStringBytes is rejected.")]
        public void ParseRoomList_OversizedString_Rejected()
        {
            string huge = new string('A', 4_096);
            string json = "[{\"room_id\":\"" + huge + "\",\"room_code\":\"\",\"name\":\"\",\"player_count\":0,\"max_players\":0,\"is_public\":false,\"lobby_name\":\"\"}]";
            byte[] payload = Encoding.UTF8.GetBytes(json);

            var rooms = LobbyPacketParser.ParseRoomList(payload, 256, maxStringBytes: 64);
            Assert.AreEqual(1, rooms.Count, "row still emitted");
            Assert.AreEqual(string.Empty, rooms[0].RoomId,
                "Oversized string field must be reduced to empty (parse-failure null → empty).");
        }
    }

    [TestFixture]
    [Category("SecuritySDK")]
    public class LobbyReplyCorrelationTests
    {
        private LobbyManager _lobby;

        [SetUp]
        public void SetUp()
        {
            // PacketBuilder requires no constructor args.
            var builder = new PacketBuilder();
            _lobby = new LobbyManager(builder, _ => { });
        }

        [Test]
        [Description("a stray LobbyList reply with no pending Join must NOT flip IsInLobby true.")]
        public void StrayLobbyListReply_NoPendingJoin_DoesNotPromote()
        {
            byte[] empty = Encoding.UTF8.GetBytes("[]");
            _lobby.HandleLobbyReply(PacketType.LobbyList, empty);

            Assert.IsFalse(_lobby.IsInLobby,
                "LobbyList reply must never promote the manager to in-lobby.");
        }

        [Test]
        [Description("a LobbyList reply with a pending Join must NOT consume the pending-join slot.")]
        public void StrayLobbyListReply_WithPendingJoin_DoesNotConsumeSlot()
        {
            _lobby.JoinLobby("xx");
            byte[] empty = Encoding.UTF8.GetBytes("[]");

            _lobby.HandleLobbyReply(PacketType.LobbyList, empty);
            Assert.IsFalse(_lobby.IsInLobby,
                "Stray LobbyList reply must not satisfy a pending Join.");

            bool joinPending = (bool)typeof(LobbyManager)
                .GetField("_joinPending", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(_lobby);
            Assert.IsTrue(joinPending,
                "Pending-join slot must remain reserved for the actual LobbyJoin reply.");
        }

        [Test]
        [Description("a LobbyJoin reply that follows JoinLobby is correctly accepted.")]
        public void LobbyJoinReply_WithPendingJoin_Promotes()
        {
            _lobby.JoinLobby("default");
            byte[] empty = Encoding.UTF8.GetBytes("[]");

            _lobby.HandleLobbyReply(PacketType.LobbyJoin, empty);
            Assert.IsTrue(_lobby.IsInLobby,
                "LobbyJoin reply with outstanding JoinLobby must promote the manager.");
            Assert.AreEqual("default", _lobby.CurrentLobbyName);
        }
    }
}
