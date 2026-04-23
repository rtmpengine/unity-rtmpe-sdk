// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyPacketParser.cs
//
// Parses the server reply for LobbyJoin (0x27), LobbyList (0x29), and
// LobbyRoomListUpdate (0x2A).  All three carry the same payload shape:
// a JSON array of room-summary objects wrapped inside a NatsReply.Data field.
//
// The gateway strips the outer NatsReply envelope before forwarding the raw
// payload to the client, so the client receives the inner JSON array directly.

using System;
using System.Collections.Generic;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Parses lobby response payloads into <see cref="LobbyRoomInfo"/> lists.
    /// </summary>
    internal static class LobbyPacketParser
    {
        /// <summary>
        /// Parses a UTF-8 JSON array of room summaries from a lobby response
        /// payload.  Returns an empty list on parse failure (never throws).
        /// </summary>
        public static List<LobbyRoomInfo> ParseRoomList(byte[] payload)
        {
            var rooms = new List<LobbyRoomInfo>();
            if (payload == null || payload.Length == 0) return rooms;

            try
            {
                var json = Encoding.UTF8.GetString(payload);
                ParseJsonArray(json, rooms);
            }
            catch
            {
                // Silently return empty list — caller logs if needed.
            }
            return rooms;
        }

        // ── Minimal JSON array parser ────────────────────────────────────────
        // We avoid UnityEngine.JsonUtility (no List<T> support without wrappers)
        // and Newtonsoft (optional dependency) by hand-rolling a lightweight
        // parser sufficient for the fixed lobby-response schema.

        private static void ParseJsonArray(string json, List<LobbyRoomInfo> out_)
        {
            int pos = json.IndexOf('[');
            if (pos < 0) return;

            int depth = 0;
            int objStart = -1;
            for (int i = pos; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '{')
                {
                    if (depth == 1) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 1 && objStart >= 0)
                    {
                        var obj = json.Substring(objStart, i - objStart + 1);
                        var room = ParseRoomObject(obj);
                        if (room != null) out_.Add(room);
                        objStart = -1;
                    }
                }
                else if (c == '[')
                {
                    depth++;
                }
                else if (c == ']' && depth <= 1)
                {
                    break;
                }
            }
        }

        private static LobbyRoomInfo ParseRoomObject(string obj)
        {
            string roomId      = ReadStringField(obj, "room_id");
            string roomCode    = ReadStringField(obj, "room_code");
            string name        = ReadStringField(obj, "name");
            int    playerCount = ReadIntField(obj,    "player_count");
            int    maxPlayers  = ReadIntField(obj,    "max_players");
            bool   isPublic    = ReadBoolField(obj,   "is_public");
            string lobbyName   = ReadStringField(obj, "lobby_name");

            return new LobbyRoomInfo(roomId, roomCode, name, playerCount, maxPlayers, isPublic, lobbyName);
        }

        private static string ReadStringField(string json, string key)
        {
            var pattern = $"\"{key}\":\"";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            start += pattern.Length;
            int end = json.IndexOf('"', start);
            return end < 0 ? string.Empty : json.Substring(start, end - start);
        }

        private static int ReadIntField(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return 0;
            start += pattern.Length;
            // Skip whitespace.
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return 0;
            int.TryParse(json.Substring(start, end - start), out int v);
            return v;
        }

        private static bool ReadBoolField(string json, string key)
        {
            var pattern = $"\"{key}\":";
            int start = json.IndexOf(pattern, StringComparison.Ordinal);
            if (start < 0) return false;
            start += pattern.Length;
            while (start < json.Length && json[start] == ' ') start++;
            return start < json.Length && json[start] == 't'; // "true" starts with 't'
        }
    }
}
