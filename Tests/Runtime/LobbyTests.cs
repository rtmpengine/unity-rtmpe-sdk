// RTMPE SDK — Tests/Runtime/LobbyTests.cs
//
// NUnit tests for the lobby system (Phase 1.3):
//  • LobbyPacketBuilder     — JSON payload construction
//  • LobbyPacketParser      — JSON room-list parsing
//  • LobbyQueryOptions      — options validation / defaults
//  • NetworkConstants       — PacketType discriminant values (0x27–0x2A)
//
// Pure C# — no Unity engine dependencies; runs in Edit Mode Test Runner.

using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Rooms;

namespace RTMPE.Tests
{
    // ── PacketType discriminants ──────────────────────────────────────────────

    [TestFixture]
    [Category("Lobby")]
    public class LobbyPacketTypeTests
    {
        [Test]
        public void LobbyJoin_Discriminant_Is_0x27()
        {
            Assert.AreEqual(0x27, (byte)PacketType.LobbyJoin);
        }

        [Test]
        public void LobbyLeave_Discriminant_Is_0x28()
        {
            Assert.AreEqual(0x28, (byte)PacketType.LobbyLeave);
        }

        [Test]
        public void LobbyList_Discriminant_Is_0x29()
        {
            Assert.AreEqual(0x29, (byte)PacketType.LobbyList);
        }

        [Test]
        public void LobbyRoomListUpdate_Discriminant_Is_0x2A()
        {
            Assert.AreEqual(0x2A, (byte)PacketType.LobbyRoomListUpdate);
        }

        [Test]
        public void LobbyDiscriminants_Are_Unique()
        {
            var types = new byte[]
            {
                (byte)PacketType.LobbyJoin,
                (byte)PacketType.LobbyLeave,
                (byte)PacketType.LobbyList,
                (byte)PacketType.LobbyRoomListUpdate,
            };
            var set = new HashSet<byte>(types);
            Assert.AreEqual(types.Length, set.Count, "All lobby packet types must have unique discriminants");
        }

        [Test]
        public void LobbyPacketTypes_DoNotCollideWithExistingTypes()
        {
            // Verify no overlap with previously-defined packet types.
            var existingTypes = new byte[]
            {
                (byte)PacketType.Handshake,
                (byte)PacketType.RoomCreate,
                (byte)PacketType.RoomJoin,
                (byte)PacketType.RoomLeave,
                (byte)PacketType.RoomList,
                (byte)PacketType.RoomPropertyUpdate,
                (byte)PacketType.PlayerPropertyUpdate,
                (byte)PacketType.MasterClientChanged,
            };
            var lobbyTypes = new byte[]
            {
                (byte)PacketType.LobbyJoin,
                (byte)PacketType.LobbyLeave,
                (byte)PacketType.LobbyList,
                (byte)PacketType.LobbyRoomListUpdate,
            };
            foreach (var lobby in lobbyTypes)
                foreach (var existing in existingTypes)
                    Assert.AreNotEqual(existing, lobby,
                        $"Lobby type 0x{lobby:X2} collides with existing type 0x{existing:X2}");
        }
    }

    // ── LobbyPacketBuilder ────────────────────────────────────────────────────

    [TestFixture]
    [Category("Lobby")]
    public class LobbyPacketBuilderTests
    {
        [Test]
        public void BuildLobbyJoinPayload_DefaultLobby_EmptyString()
        {
            var bytes = LobbyPacketBuilder.BuildLobbyJoinPayload("");
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"lobby_name\":\"\"", json);
        }

        [Test]
        public void BuildLobbyJoinPayload_NamedLobby_IncludesName()
        {
            var bytes = LobbyPacketBuilder.BuildLobbyJoinPayload("ranked");
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"lobby_name\":\"ranked\"", json);
        }

        [Test]
        public void BuildLobbyJoinPayload_NullName_TreatedAsEmpty()
        {
            var bytes = LobbyPacketBuilder.BuildLobbyJoinPayload(null);
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"lobby_name\":\"\"", json);
        }

        [Test]
        public void BuildLobbyLeavePayload_ProducesValidJson()
        {
            var bytes = LobbyPacketBuilder.BuildLobbyLeavePayload("ranked");
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"lobby_name\":\"ranked\"", json);
        }

        [Test]
        public void BuildLobbyListPayload_DefaultOptions_HasExpectedFields()
        {
            var bytes = LobbyPacketBuilder.BuildLobbyListPayload(new LobbyQueryOptions());
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"lobby_name\":\"\"", json);
            StringAssert.Contains("\"max_results\":0", json);
            StringAssert.Contains("\"sort_by\":0", json);
            // No filters field when filters are null/empty.
            StringAssert.DoesNotContain("\"filters\"", json);
        }

        [Test]
        public void BuildLobbyListPayload_WithFilters_IncludesFilterArray()
        {
            var opts = new LobbyQueryOptions
            {
                LobbyName  = "competitive",
                MaxResults = 20,
                SortBy     = LobbySort.PlayerCount,
                Filters    = new List<LobbyFilter>
                {
                    new LobbyFilter { Key = "GameMode", Op = LobbyFilterOp.Eq,  Value = "TDM" },
                    new LobbyFilter { Key = "MinRank",  Op = LobbyFilterOp.GtEq, Value = 5     },
                }
            };
            var bytes = LobbyPacketBuilder.BuildLobbyListPayload(opts);
            var json  = Encoding.UTF8.GetString(bytes);

            StringAssert.Contains("\"lobby_name\":\"competitive\"", json);
            StringAssert.Contains("\"max_results\":20", json);
            StringAssert.Contains("\"filters\":[", json);
            StringAssert.Contains("\"key\":\"GameMode\"", json);
            StringAssert.Contains("\"value\":\"TDM\"", json);
            StringAssert.Contains("\"key\":\"MinRank\"", json);
            StringAssert.Contains("\"value\":5", json);
            StringAssert.Contains("\"op\":5", json); // GtEq = 5
        }

        [Test]
        public void BuildLobbyListPayload_SortByAge_EncodesCorrectByte()
        {
            var opts  = new LobbyQueryOptions { SortBy = LobbySort.Age };
            var bytes = LobbyPacketBuilder.BuildLobbyListPayload(opts);
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"sort_by\":1", json);
        }

        [Test]
        public void BuildLobbyListPayload_NullOptions_UsesDefaults()
        {
            // BuildLobbyListPayload(null) should not throw.
            Assert.DoesNotThrow(() => LobbyPacketBuilder.BuildLobbyListPayload(null));
        }

        [Test]
        public void BuildLobbyListPayload_BoolFilterValue_EncodesCorrectly()
        {
            var opts = new LobbyQueryOptions
            {
                Filters = new List<LobbyFilter>
                {
                    new LobbyFilter { Key = "IsRanked", Op = LobbyFilterOp.Eq, Value = true }
                }
            };
            var bytes = LobbyPacketBuilder.BuildLobbyListPayload(opts);
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"value\":true", json);
        }

        [Test]
        public void BuildLobbyListPayload_FloatFilterValue_EncodesCorrectly()
        {
            var opts = new LobbyQueryOptions
            {
                Filters = new List<LobbyFilter>
                {
                    new LobbyFilter { Key = "Rating", Op = LobbyFilterOp.Gt, Value = 4.5f }
                }
            };
            var bytes = LobbyPacketBuilder.BuildLobbyListPayload(opts);
            var json  = Encoding.UTF8.GetString(bytes);
            StringAssert.Contains("\"value\":4.5", json);
        }
    }

    // ── LobbyPacketParser ──────────────────────────────────────────────────────

    [TestFixture]
    [Category("Lobby")]
    public class LobbyPacketParserTests
    {
        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

        [Test]
        public void ParseRoomList_EmptyArray_ReturnsEmptyList()
        {
            var result = LobbyPacketParser.ParseRoomList(Utf8("[]"));
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_NullPayload_ReturnsEmptyList()
        {
            var result = LobbyPacketParser.ParseRoomList(null);
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_EmptyPayload_ReturnsEmptyList()
        {
            var result = LobbyPacketParser.ParseRoomList(Array.Empty<byte>());
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_MalformedJson_ReturnsEmptyList()
        {
            var result = LobbyPacketParser.ParseRoomList(Utf8("{not valid"));
            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_SingleRoom_ParsesAllFields()
        {
            var json = @"[{""room_id"":""abc-123"",""room_code"":""XK4D2A"",""name"":""Arena"",""player_count"":3,""max_players"":8,""is_public"":true,""lobby_name"":""ranked""}]";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            var r = result[0];
            Assert.AreEqual("abc-123", r.RoomId);
            Assert.AreEqual("XK4D2A",  r.RoomCode);
            Assert.AreEqual("Arena",   r.Name);
            Assert.AreEqual(3,          r.PlayerCount);
            Assert.AreEqual(8,          r.MaxPlayers);
            Assert.IsTrue(r.IsPublic);
            Assert.AreEqual("ranked",  r.LobbyName);
        }

        [Test]
        public void ParseRoomList_MultipleRooms_ParsesAll()
        {
            var json = @"[
                {""room_id"":""r1"",""room_code"":""A1"",""name"":""R1"",""player_count"":1,""max_players"":4,""is_public"":true,""lobby_name"":""""},
                {""room_id"":""r2"",""room_code"":""B2"",""name"":""R2"",""player_count"":2,""max_players"":8,""is_public"":false,""lobby_name"":""ranked""}
            ]";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("r1", result[0].RoomId);
            Assert.AreEqual("r2", result[1].RoomId);
            Assert.AreEqual("ranked", result[1].LobbyName);
        }

        [Test]
        public void ParseRoomList_DefaultLobby_EmptyLobbyName()
        {
            var json = @"[{""room_id"":""r0"",""room_code"":""C0"",""name"":""R0"",""player_count"":0,""max_players"":16,""is_public"":true,""lobby_name"":""""}]";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(string.Empty, result[0].LobbyName);
        }

        [Test]
        public void ParseRoomList_IsPublicFalse_ParsesCorrectly()
        {
            var json = @"[{""room_id"":""r1"",""room_code"":""A1"",""name"":""R1"",""player_count"":0,""max_players"":4,""is_public"":false,""lobby_name"":""""}]";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            Assert.IsFalse(result[0].IsPublic);
        }

        // ── Versioned envelope (Audit Issue I) ─────────────────────────────
        //
        // The Room Service emits `{"version":1,"rooms":[...]}` for every
        // lobby room-list flow (LobbyJoin reply, LobbyList reply,
        // LobbyRoomListUpdate push).  These tests cover the four
        // wire-shape branches the parser must handle:
        //
        //   • legacy bare array `[...]`     — accepted (back-compat with
        //                                     pre-Issue-I Room builds)
        //   • current envelope `{v=1,...}`  — accepted, rooms parsed
        //   • future envelope  `{v=999,...}` — dropped to empty list
        //   • envelope shape, no version    — treated as v0, accepted as v1
        //
        // The "future envelope" rejection is the security-critical path:
        // silently parsing v999 fields under v1 semantics would surface as
        // inconsistent UI state with no operator-visible failure signal.

        [Test]
        public void ParseRoomList_VersionedEnvelopeV1_ParsesRooms()
        {
            var json = @"{""version"":1,""rooms"":[{""room_id"":""r1"",""room_code"":""A1"",""name"":""R1"",""player_count"":1,""max_players"":4,""is_public"":true,""lobby_name"":""ranked""}]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r1", result[0].RoomId);
            Assert.AreEqual("ranked", result[0].LobbyName);
        }

        [Test]
        public void ParseRoomList_VersionedEnvelopeV1_EmptyRooms()
        {
            var json = @"{""version"":1,""rooms"":[]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_VersionedEnvelopeV1_TwoRooms()
        {
            var json = @"{""version"":1,""rooms"":[
                {""room_id"":""r1"",""room_code"":""A1"",""name"":""R1"",""player_count"":1,""max_players"":4,""is_public"":true,""lobby_name"":""""},
                {""room_id"":""r2"",""room_code"":""B2"",""name"":""R2"",""player_count"":2,""max_players"":8,""is_public"":false,""lobby_name"":""ranked""}
            ]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(2, result.Count);
            Assert.AreEqual("r1", result[0].RoomId);
            Assert.AreEqual("r2", result[1].RoomId);
            Assert.AreEqual("ranked", result[1].LobbyName);
        }

        [Test]
        public void ParseRoomList_FutureEnvelopeVersion_DroppedToEmpty()
        {
            // v999 — the Room Service of some future deploy could publish
            // this shape with new fields the SDK does not understand.  The
            // contract is to drop the payload entirely rather than parse
            // recognised fields under v1 semantics.
            var json = @"{""version"":999,""rooms"":[{""room_id"":""r-future"",""room_code"":""F0"",""name"":""Future"",""player_count"":1,""max_players"":4,""is_public"":true,""lobby_name"":""""}]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.IsNotNull(result, "drop must produce an empty list, not null");
            Assert.AreEqual(0, result.Count,
                "an envelope newer than MaxKnownEnvelopeVersion must drop every entry");
        }

        [Test]
        public void ParseRoomList_EnvelopeShapeWithoutVersionField_TreatedAsV1()
        {
            // A Room Service build that emits the envelope shape without
            // setting `version` (e.g. an in-progress migration or a
            // hand-crafted test payload) is treated as v0, which the
            // dispatcher coerces to v1 — the original schema.  Same
            // policy as GatewayStateEnvelope on the Sync side.
            var json = @"{""rooms"":[{""room_id"":""r1"",""room_code"":""A1"",""name"":""R1"",""player_count"":0,""max_players"":4,""is_public"":true,""lobby_name"":""""}]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r1", result[0].RoomId);
        }

        [Test]
        public void ParseRoomList_EnvelopeWithLeadingWhitespace_StillDispatched()
        {
            // The dispatcher must skip ASCII whitespace before classifying
            // the leading character so a pretty-printed payload (CR/LF/TAB
            // produced by a development tool) still resolves to the
            // envelope branch rather than the "unrecognised" empty drop.
            var json = "  \t\r\n{\"version\":1,\"rooms\":[]}";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }

        [Test]
        public void ParseRoomList_LegacyBareArrayWithLeadingWhitespace_StillParsed()
        {
            // Symmetric whitespace handling for the legacy bare-array path.
            var json = "  \t\r\n[{\"room_id\":\"r1\",\"room_code\":\"A1\",\"name\":\"R1\",\"player_count\":0,\"max_players\":4,\"is_public\":true,\"lobby_name\":\"\"}]";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual("r1", result[0].RoomId);
        }

        [Test]
        public void ParseRoomList_UnknownLeadingChar_ReturnsEmpty()
        {
            // A payload that is neither `[` nor `{` after whitespace —
            // e.g. a string literal or a number — is treated as
            // unrecognised and produces an empty list, the same shape as
            // a malformed JSON payload.
            var json = "\"not an array\"";
            var result = LobbyPacketParser.ParseRoomList(Utf8(json));

            Assert.IsNotNull(result);
            Assert.AreEqual(0, result.Count);
        }
    }

    // ── LobbyQueryOptions / LobbyFilter ──────────────────────────────────────

    [TestFixture]
    [Category("Lobby")]
    public class LobbyQueryOptionsTests
    {
        [Test]
        public void DefaultOptions_LobbyName_IsEmpty()
        {
            var opts = new LobbyQueryOptions();
            Assert.AreEqual(string.Empty, opts.LobbyName);
        }

        [Test]
        public void DefaultOptions_MaxResults_IsZero()
        {
            var opts = new LobbyQueryOptions();
            Assert.AreEqual(0, opts.MaxResults);
        }

        [Test]
        public void DefaultOptions_SortBy_IsPlayerCount()
        {
            var opts = new LobbyQueryOptions();
            Assert.AreEqual(LobbySort.PlayerCount, opts.SortBy);
        }

        [Test]
        public void DefaultOptions_Filters_IsNull()
        {
            var opts = new LobbyQueryOptions();
            Assert.IsNull(opts.Filters);
        }

        [Test]
        public void LobbyFilterOp_Values_MatchServerConstants()
        {
            Assert.AreEqual(0, (byte)LobbyFilterOp.Eq);
            Assert.AreEqual(1, (byte)LobbyFilterOp.NotEq);
            Assert.AreEqual(2, (byte)LobbyFilterOp.Lt);
            Assert.AreEqual(3, (byte)LobbyFilterOp.Gt);
            Assert.AreEqual(4, (byte)LobbyFilterOp.LtEq);
            Assert.AreEqual(5, (byte)LobbyFilterOp.GtEq);
        }

        [Test]
        public void LobbySort_Values_MatchServerConstants()
        {
            Assert.AreEqual(0, (byte)LobbySort.PlayerCount);
            Assert.AreEqual(1, (byte)LobbySort.Age);
            Assert.AreEqual(2, (byte)LobbySort.Name);
        }
    }

    // ── LobbyRoomInfo ─────────────────────────────────────────────────────────

    [TestFixture]
    [Category("Lobby")]
    public class LobbyRoomInfoTests
    {
        [Test]
        public void Constructor_SetsAllFields()
        {
            var info = new LobbyRoomInfo("id1", "CODE1", "Room One", 3, 8, true, "ranked");
            Assert.AreEqual("id1",      info.RoomId);
            Assert.AreEqual("CODE1",    info.RoomCode);
            Assert.AreEqual("Room One", info.Name);
            Assert.AreEqual(3,          info.PlayerCount);
            Assert.AreEqual(8,          info.MaxPlayers);
            Assert.IsTrue(info.IsPublic);
            Assert.AreEqual("ranked",   info.LobbyName);
        }

        [Test]
        public void Constructor_NullStrings_DefaultToEmpty()
        {
            var info = new LobbyRoomInfo(null, null, null, 0, 0, false, null);
            Assert.AreEqual(string.Empty, info.RoomId);
            Assert.AreEqual(string.Empty, info.RoomCode);
            Assert.AreEqual(string.Empty, info.Name);
            Assert.AreEqual(string.Empty, info.LobbyName);
        }
    }
}
