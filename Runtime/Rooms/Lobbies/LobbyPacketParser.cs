// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyPacketParser.cs
//
// Parses the server reply for LobbyJoin (0x27), LobbyList (0x29), and
// LobbyRoomListUpdate (0x2A).  All three carry the same payload — the
// canonical shape is a versioned JSON envelope of room summaries, with a
// legacy bare-array fallback retained for rolling-upgrade compatibility:
//
//   • Canonical (Room Service v2026-05-10+):
//       {"version": 1, "rooms": [ {...}, {...} ]}
//   • Legacy (pre-versioning):
//       [ {...}, {...} ]
//
// The gateway strips the outer NatsReply envelope before forwarding the raw
// payload to the client, so the client receives the inner shape directly.
//
// ── Schema-version guard ─────────────────────────────────────────────────────
//
// The `version` field encodes the wire-schema generation:
//   • 0 / absent  — legacy bare-array shape; treated as v1 semantics.
//   • 1           — current envelope, parsed as documented.
//   • > 1         — unknown future shape; the parser MUST refuse rather
//                   than read v1 fields under v2 semantics (a silent
//                   misread would surface as bogus rooms in the lobby UI
//                   that no operator dashboard could correlate to a wire
//                   format change).
//
// ── Hardening (2026-04-27) ────────────────────────────────────────────────────
// The parser is hand-rolled (no external JSON dependency) but has been
// hardened to defend against a hostile or compromised server:
//  • Top-level entry count is capped at NetworkSettings.maxLobbyRoomEntries
//    (default 256, parity with RoomPacketParser).
//  • Per-string fields are length-capped at NetworkSettings.maxLobbyStringBytes.
//  • String fields support JSON escapes (\\, \", \/, \b, \f, \n, \r, \t,
//    \uXXXX) and reject embedded NUL or control chars.
//  • Maximum nesting depth is bounded so a deeply-nested payload cannot
//    exhaust the parser's call stack.

using System;
using System.Collections.Generic;
using System.Text;
using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Parses lobby response payloads into <see cref="LobbyRoomInfo"/> lists.
    /// </summary>
    internal static class LobbyPacketParser
    {
        // Hard ceilings used when no NetworkSettings instance is available
        // (defensive fallback — e.g. in EditMode unit tests that exercise the
        // parser directly without a NetworkManager).
        private const int FallbackMaxEntries     = 256;
        private const int FallbackMaxStringBytes = 256;
        private const int MaxNestingDepth        = 32;

        /// <summary>
        /// Highest wire-schema version this consumer understands.  Payloads
        /// declaring a version above this value are dropped rather than
        /// parsed under v1 semantics — a silent misread would surface as
        /// bogus rooms in the lobby UI with no operator-visible failure
        /// signal.
        ///
        /// <para>Coordinated with the publisher constant
        /// <c>LobbyRoomListEnvelopeVersion</c> in
        /// <c>modules/room/infrastructure/messaging/nats_handler.go</c>.
        /// Bumping this constant is a two-side change and should be made
        /// in lock-step with the Go publisher.</para>
        /// </summary>
        internal const int MaxKnownEnvelopeVersion = 1;

        // Strict UTF-8 codec.  The lax decoder silently substitutes U+FFFD
        // for malformed bytes, which lets a hostile or compromised server
        // smuggle bytes that survive the parse but mutate downstream
        // string-equality (the existing _abandonedLobbyName defence in
        // LobbyManager keys on string equality, which U+FFFD substitution
        // defeats — two distinct lobby names can collapse to the same
        // fingerprint).  Symmetric with M19-PROTO-04 / M19-RPC-04/05.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        /// <summary>
        /// Parses a UTF-8 JSON array of room summaries from a lobby response
        /// payload.  Returns an empty list on parse failure (never throws).
        /// </summary>
        public static List<LobbyRoomInfo> ParseRoomList(byte[] payload)
        {
            int maxEntries     = FallbackMaxEntries;
            int maxStringBytes = FallbackMaxStringBytes;
            var settings = NetworkManager.Instance?.Settings;
            if (settings != null)
            {
                if (settings.maxLobbyRoomEntries > 0)
                    maxEntries = settings.maxLobbyRoomEntries;
                if (settings.maxLobbyStringBytes > 0)
                    maxStringBytes = settings.maxLobbyStringBytes;
            }
            return ParseRoomList(payload, maxEntries, maxStringBytes);
        }

        /// <summary>
        /// Test-friendly overload that accepts explicit caps.  Public for use
        /// by EditMode tests that exercise the parser without a live
        /// <see cref="NetworkManager"/>.
        /// </summary>
        internal static List<LobbyRoomInfo> ParseRoomList(
            byte[] payload, int maxEntries, int maxStringBytes)
        {
            var rooms = new List<LobbyRoomInfo>();
            if (payload == null || payload.Length == 0) return rooms;
            if (maxEntries     <= 0) maxEntries     = FallbackMaxEntries;
            if (maxStringBytes <= 0) maxStringBytes = FallbackMaxStringBytes;

            try
            {
                // Strict UTF-8 routes a malformed-byte payload through the
                // existing bare-catch into the empty-list return path,
                // exactly as a parser-throw would.  No new failure shape on
                // the call site contract.
                var json = StrictUtf8.GetString(payload);

                // Wire-shape dispatch:
                //   • leading '['  →  legacy bare-array shape (pre-versioning).
                //   • leading '{'  →  versioned envelope; require the declared
                //                     version is at or below this consumer's
                //                     `MaxKnownEnvelopeVersion`, then parse the
                //                     `rooms` array within.  Higher versions
                //                     drop the payload outright — silently
                //                     reading v1 fields under v2 semantics
                //                     would surface as inconsistent UI state
                //                     with no operator-visible failure signal.
                //   • anything else → unrecognised; treat as empty.
                int firstNonWs = SkipWhitespace(json, 0);
                if (firstNonWs >= json.Length) return rooms;
                char first = json[firstNonWs];
                if (first == '[')
                {
                    // Legacy bare-array shape — the payload itself is the
                    // room array, starting at this first non-whitespace char.
                    ParseJsonArray(json, firstNonWs, rooms, maxEntries, maxStringBytes);
                }
                else if (first == '{')
                {
                    // Versioned envelope.  Both the schema-version guard and
                    // the room-array lookup resolve their fields by walking
                    // only the envelope's own top-level members, so a
                    // `version` key or an array nested inside a room object
                    // is never mistaken for the envelope's own field — a
                    // non-canonical or hostile payload cannot smuggle either.
                    int declaredVersion = 0;
                    if (TryFindTopLevelMember(json, firstNonWs, "version",
                                              out int versionValueStart))
                    {
                        declaredVersion = ReadIntValueAt(json, versionValueStart);
                    }
                    if (declaredVersion > MaxKnownEnvelopeVersion)
                    {
                        // Future-version envelope from a Room Service build
                        // ahead of this consumer — drop without raising the
                        // partial list, mirroring the GatewayEnvelope
                        // versioning policy on the Sync side.
                        return rooms;
                    }
                    // Parse the `rooms` member by key.  Selecting it by key
                    // rather than by the first '[' in the payload prevents a
                    // non-canonical envelope that places another array ahead
                    // of `rooms` from being parsed as the room list.
                    if (TryFindTopLevelMember(json, firstNonWs, "rooms",
                                              out int roomsValueStart)
                        && roomsValueStart < json.Length
                        && json[roomsValueStart] == '[')
                    {
                        ParseJsonArray(json, roomsValueStart, rooms,
                                       maxEntries, maxStringBytes);
                    }
                }
            }
            catch
            {
                // Silently return empty list — caller logs if needed.
            }
            return rooms;
        }

        // Skip ASCII whitespace (space, tab, CR, LF) starting from `pos`.
        // Used by the version-shape dispatcher to tolerate compact-formatted
        // and pretty-printed payloads symmetrically.
        private static int SkipWhitespace(string json, int pos)
        {
            while (pos < json.Length)
            {
                char c = json[pos];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n') break;
                pos++;
            }
            return pos;
        }

        // ── Top-level envelope member lookup ─────────────────────────────────
        //
        // The schema-version guard and the `rooms` array selection resolve
        // their fields against the envelope's ROOT object only.  Walking just
        // the depth-1 members means a `version` key or an array nested inside
        // a room object is never read as the envelope's own field, so a
        // non-canonical or hostile payload cannot shadow either by embedding
        // it in a nested object.

        // Find the closing quote of a JSON string whose opening quote has
        // already been consumed; `start` is the first content char.  Returns
        // the index of the unescaped terminating quote, or -1 if unterminated.
        private static int IndexOfStringEnd(string json, int start)
        {
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\') { i++; continue; }   // skip the escaped char
                if (c == '"') return i;
            }
            return -1;
        }

        // Skip one JSON value beginning at json[valueStart].  Returns the
        // index immediately past the value, or -1 on a malformed/truncated
        // value.  Strings and balanced object/array spans are skipped whole;
        // a primitive (number, true, false, null) is scanned up to the next
        // structural delimiter.
        private static int SkipValue(string json, int valueStart)
        {
            if (valueStart >= json.Length) return -1;
            char c = json[valueStart];
            if (c == '"')
            {
                int end = IndexOfStringEnd(json, valueStart + 1);
                return end < 0 ? -1 : end + 1;
            }
            if (c == '{' || c == '[')
            {
                int depth = 0;
                bool inString = false;
                for (int i = valueStart; i < json.Length; i++)
                {
                    char ch = json[i];
                    if (inString)
                    {
                        if (ch == '\\') { i++; continue; }
                        if (ch == '"') inString = false;
                        continue;
                    }
                    if (ch == '"') { inString = true; continue; }
                    if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }
                return -1;   // unbalanced object/array
            }
            // Primitive value — consume up to the next structural delimiter.
            int j = valueStart;
            while (j < json.Length)
            {
                char ch = json[j];
                if (ch == ',' || ch == '}' || ch == ']') break;
                j++;
            }
            return j;
        }

        // Locate the value of a depth-1 member of the root object whose
        // opening brace is at json[rootBraceIndex].  On success sets
        // <paramref name="valueStart"/> to the index of the value's first
        // non-whitespace char and returns true; returns false when the key
        // is absent or the envelope is malformed.
        private static bool TryFindTopLevelMember(
            string json, int rootBraceIndex, string key, out int valueStart)
        {
            valueStart = -1;
            if (rootBraceIndex < 0 || rootBraceIndex >= json.Length
                || json[rootBraceIndex] != '{')
                return false;

            int i = rootBraceIndex + 1;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',')
                {
                    i++;
                    continue;
                }
                if (c == '}') return false;     // end of root object — key absent
                if (c != '"') return false;     // malformed — a member must start with a key

                int keyStart = i + 1;
                int keyEnd   = IndexOfStringEnd(json, keyStart);
                if (keyEnd < 0) return false;

                int colon = SkipWhitespace(json, keyEnd + 1);
                if (colon >= json.Length || json[colon] != ':') return false;

                int vStart = SkipWhitespace(json, colon + 1);
                if (vStart >= json.Length) return false;

                if (keyEnd - keyStart == key.Length
                    && string.CompareOrdinal(json, keyStart, key, 0, key.Length) == 0)
                {
                    valueStart = vStart;
                    return true;
                }

                int afterValue = SkipValue(json, vStart);
                if (afterValue < 0) return false;
                i = afterValue;
            }
            return false;
        }

        // ── Minimal JSON array parser ────────────────────────────────────────
        // We avoid UnityEngine.JsonUtility (no List<T> support without wrappers)
        // and Newtonsoft (optional dependency) by hand-rolling a lightweight
        // parser sufficient for the fixed lobby-response schema.

        private static void ParseJsonArray(
            string json,
            int arrayStart,
            List<LobbyRoomInfo> out_,
            int maxEntries,
            int maxStringBytes)
        {
            // arrayStart is the index of the opening '[' located by the
            // caller: the bare-array dispatch passes the payload's first
            // non-whitespace char; the envelope dispatch passes the start of
            // the `rooms` member's array value.
            int pos = arrayStart;
            if (pos < 0 || pos >= json.Length || json[pos] != '[') return;

            int depth = 0;
            int objStart = -1;
            for (int i = pos; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '{')
                {
                    if (depth == 1) objStart = i;
                    depth++;
                    if (depth > MaxNestingDepth) return;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 1 && objStart >= 0)
                    {
                        // Reject the entire payload once the configured cap is
                        // exceeded — truncating silently would mask buggy /
                        // hostile servers.  Returns without raising the partial
                        // list to the caller, matching the "empty on failure"
                        // contract documented on ParseRoomList.
                        if (out_.Count >= maxEntries)
                        {
                            out_.Clear();
                            return;
                        }

                        var obj = json.Substring(objStart, i - objStart + 1);
                        var room = ParseRoomObject(obj, maxStringBytes);
                        if (room != null) out_.Add(room);
                        objStart = -1;
                    }
                }
                else if (c == '[')
                {
                    depth++;
                    if (depth > MaxNestingDepth) return;
                }
                else if (c == ']' && depth <= 1)
                {
                    break;
                }
            }
        }

        private static LobbyRoomInfo ParseRoomObject(string obj, int maxStringBytes)
        {
            // Reject objects whose own nesting exceeds a tight per-object bound.
            // ParseJsonArray already enforces top-level array depth; this catches
            // deeply nested custom_properties or similar server-added fields that
            // would not appear in the outer traversal.
            int nestDepth = 0;
            for (int ci = 0; ci < obj.Length; ci++)
            {
                char ch = obj[ci];
                if (ch == '{' || ch == '[') nestDepth++;
                else if (ch == '}' || ch == ']') nestDepth--;
                if (nestDepth > MaxNestingDepth) return null;
            }

            // Resolve every field against the room object's own depth-1
            // members.  Selecting fields structurally — rather than by the
            // first "key" substring anywhere in the slice — prevents an
            // attacker-chosen value (notably the room name) that embeds a
            // `"room_id":"…"` fragment from shadowing the genuine field.
            int rootBrace = SkipWhitespace(obj, 0);
            if (rootBrace >= obj.Length || obj[rootBrace] != '{')
                return null;

            string roomId      = ReadStringField(obj, rootBrace, "room_id",      maxStringBytes);
            string roomCode    = ReadStringField(obj, rootBrace, "room_code",    maxStringBytes);
            string name        = ReadStringField(obj, rootBrace, "name",         maxStringBytes);
            int    playerCount = ReadIntField(obj,    rootBrace, "player_count");
            int    maxPlayers  = ReadIntField(obj,    rootBrace, "max_players");
            bool   isPublic    = ReadBoolField(obj,   rootBrace, "is_public");
            string lobbyName   = ReadStringField(obj, rootBrace, "lobby_name",   maxStringBytes);

            // Reject the row entirely if any required string was malformed.  An
            // explicit null (rather than empty string) signals a parse error.
            if (roomId    == null) roomId    = string.Empty;
            if (roomCode  == null) roomCode  = string.Empty;
            if (name      == null) name      = string.Empty;
            if (lobbyName == null) lobbyName = string.Empty;

            // Sanity-check numeric fields: negative counts and zero-capacity
            // rooms are protocol violations from a hostile or buggy server.
            if (playerCount < 0) playerCount = 0;
            if (maxPlayers  < 1) maxPlayers  = 1;
            if (playerCount > maxPlayers) playerCount = maxPlayers;

            return new LobbyRoomInfo(roomId, roomCode, name, playerCount, maxPlayers, isPublic, lobbyName);
        }

        // Read a string field of the room object whose opening brace is at
        // json[rootBrace].  The key is located among the object's depth-1
        // members only — a `"key":"…"` fragment buried inside another field's
        // string value cannot shadow it.  Returns null on a malformed value
        // (truncated escape, embedded control char, exceeds maxBytes); returns
        // empty string when the field is absent or is not a JSON string.
        private static string ReadStringField(string json, int rootBrace, string key, int maxBytes)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return string.Empty;
            if (valueStart >= json.Length || json[valueStart] != '"')
                return string.Empty;   // present, but not a string value
            return ReadStringValueAt(json, valueStart + 1, maxBytes);
        }

        // Decode a JSON string value whose content begins at json[contentStart]
        // (i.e. immediately after the opening quote), applying length caps and
        // full escape handling.  Returns null on malformed input (truncated
        // escape, embedded control char, exceeds maxBytes).
        private static string ReadStringValueAt(string json, int contentStart, int maxBytes)
        {
            var sb = new StringBuilder();
            int i = contentStart;
            while (i < json.Length)
            {
                char c = json[i];

                if (c == '"')
                {
                    // Closing quote — caller already required the value to be
                    // double-quoted via the pattern.  Backslash-escape was
                    // consumed in the c == '\\' branch below, so this is the
                    // genuine string terminator.
                    return sb.ToString();
                }

                // Reject literal NUL or any C0 control character.  JSON strings
                // must encode them as \u00XX; receiving a raw byte here is a
                // protocol violation that we refuse to silently accept.
                if (c == '\0' || c < 0x20)
                    return null;

                if (c == '\\')
                {
                    if (i + 1 >= json.Length) return null;
                    char esc = json[++i];
                    switch (esc)
                    {
                        case '"':  sb.Append('"');  break;
                        case '\\': sb.Append('\\'); break;
                        case '/':  sb.Append('/');  break;
                        case 'b':  sb.Append('\b'); break;
                        case 'f':  sb.Append('\f'); break;
                        case 'n':  sb.Append('\n'); break;
                        case 'r':  sb.Append('\r'); break;
                        case 't':  sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= json.Length) return null;
                            int cp = 0;
                            for (int k = 1; k <= 4; k++)
                            {
                                int hex = HexDigit(json[i + k]);
                                if (hex < 0) return null;
                                cp = (cp << 4) | hex;
                            }
                            i += 4;
                            // Reject any \u00XX in the C0 control range.
                            if (cp == 0 || cp < 0x20) return null;
                            sb.Append((char)cp);
                            break;
                        default: return null; // unknown escape
                    }
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }

                // Guard against pathological inputs by capping output length in
                // UTF-8 byte units.  Conservative upper-bound: each char is at
                // most 4 UTF-8 bytes; bail when the length × 4 would exceed
                // maxBytes.  Saves the cost of re-encoding to validate.
                if (sb.Length * 4 > maxBytes && Encoding.UTF8.GetByteCount(sb.ToString()) > maxBytes)
                    return null;
            }
            return null; // unterminated string
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
            if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
            return -1;
        }

        // Read an integer JSON value beginning at json[pos] (leading spaces
        // are skipped first).  The scan length is capped so an absurdly long
        // digit run cannot drive an unbounded substring allocation.  Returns
        // 0 when no integer digits are present.
        private static int ReadIntValueAt(string json, int pos)
        {
            int start = pos;
            while (start < json.Length && json[start] == ' ') start++;
            int end = start;
            int maxScan = Math.Min(json.Length, start + 16);
            while (end < maxScan && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == start) return 0;
            int.TryParse(json.Substring(start, end - start), out int v);
            return v;
        }

        // Read an integer field of the room object whose opening brace is at
        // json[rootBrace].  The key is resolved among the object's depth-1
        // members, so a `"key":` fragment nested inside another field's value
        // cannot shadow it.  Returns 0 when the field is absent.
        private static int ReadIntField(string json, int rootBrace, string key)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return 0;
            return ReadIntValueAt(json, valueStart);
        }

        // Read a boolean field of the room object whose opening brace is at
        // json[rootBrace], resolved among the object's depth-1 members.
        // Returns false when the field is absent.  TryFindTopLevelMember
        // already advances valueStart past leading whitespace.
        private static bool ReadBoolField(string json, int rootBrace, string key)
        {
            if (!TryFindTopLevelMember(json, rootBrace, key, out int valueStart))
                return false;
            return valueStart < json.Length && json[valueStart] == 't'; // "true" starts with 't'
        }
    }
}
