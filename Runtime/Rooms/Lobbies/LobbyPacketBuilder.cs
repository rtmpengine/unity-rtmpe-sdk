// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyPacketBuilder.cs
//
// Builds binary lobby packets (0x27–0x29) in the RTMPE wire format.
// Payloads are JSON objects serialised to UTF-8 bytes, consistent with the
// existing RoomPacketBuilder pattern.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Builds the JSON payloads for LobbyJoin (0x27), LobbyLeave (0x28),
    /// and LobbyList (0x29) packets.  All serialisation is done without
    /// external JSON libraries so the SDK has no additional dependencies.
    /// </summary>
    internal static class LobbyPacketBuilder
    {
        // ── LobbyJoin (0x27) ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyJoin request.
        /// Server responds with a JSON array of <see cref="LobbyRoomInfo"/> objects.
        /// </summary>
        public static byte[] BuildLobbyJoinPayload(string lobbyName)
        {
            var json = $"{{\"lobby_name\":{JsonString(lobbyName ?? string.Empty)}}}";
            return Encoding.UTF8.GetBytes(json);
        }

        // ── LobbyLeave (0x28) ────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyLeave fire-and-forget message.
        /// </summary>
        public static byte[] BuildLobbyLeavePayload(string lobbyName)
        {
            var json = $"{{\"lobby_name\":{JsonString(lobbyName ?? string.Empty)}}}";
            return Encoding.UTF8.GetBytes(json);
        }

        // ── LobbyList (0x29) ─────────────────────────────────────────────────

        /// <summary>
        /// Builds the JSON payload for a LobbyList request.
        /// </summary>
        public static byte[] BuildLobbyListPayload(LobbyQueryOptions opts)
        {
            if (opts == null) opts = new LobbyQueryOptions();

            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"lobby_name\":{JsonString(opts.LobbyName ?? string.Empty)}");
            sb.Append($",\"max_results\":{opts.MaxResults}");
            sb.Append($",\"sort_by\":{(byte)opts.SortBy}");

            if (opts.Filters != null && opts.Filters.Count > 0)
            {
                sb.Append(",\"filters\":[");
                for (int i = 0; i < opts.Filters.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    var f = opts.Filters[i];
                    sb.Append('{');
                    sb.Append($"\"key\":{JsonString(f.Key ?? string.Empty)}");
                    sb.Append($",\"op\":{(byte)f.Op}");
                    sb.Append($",\"value\":{JsonValue(f.Value)}");
                    sb.Append('}');
                }
                sb.Append(']');
            }

            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Serialises <paramref name="s"/> as a JSON string, delegating to
        /// <see cref="PropertyJson.AppendJsonString"/> which escapes backslash,
        /// double-quote, AND all control characters (&#x3c; 0x20) as \uXXXX.
        /// The previous hand-rolled implementation escaped only \\ and \",
        /// producing malformed JSON for any lobby name or key containing a
        /// tab, newline, or other control character (SDKR-03).
        /// </summary>
        private static string JsonString(string s)
        {
            var sb = new StringBuilder();
            PropertyJson.AppendJsonString(sb, s ?? string.Empty);
            return sb.ToString();
        }

        private static string JsonValue(object v)
        {
            if (v == null)       return "null";
            if (v is bool b)     return b ? "true" : "false";
            if (v is string s)   return JsonString(s);
            if (v is int i)      return i.ToString();
            if (v is float f)    return f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (v is double d)   return d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return "null";
        }
    }
}
