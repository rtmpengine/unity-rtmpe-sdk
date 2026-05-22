// RTMPE SDK — Runtime/Rooms/MasterClientPacketParser.cs
//
// Decodes the inbound Phase 2 room-management packets broadcast by the
// gateway's RoomEventReceiver:
//
//  • MasterClientChanged (0x2C) — delivered by server → client
//  • KickPlayer          (0x2E) — delivered by server → client (broadcast)
//  • SceneLoaded         (0x2F) — delivered by server → client (all-ready)
//
// The wire format is the JSON shape produced by the Go Room Service event
// publisher — see modules/room/infrastructure/messaging/nats_master_handler.go
// for the authoritative source.  All parsers return false for malformed
// input; callers log and discard.

using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Parsers for Phase 2 room-management server broadcasts.  Every method
    /// is a `Try*`-style pure function returning <see langword="false"/> on
    /// malformed input rather than throwing — room packet handlers cannot
    /// afford to terminate the receive loop on a bad message.
    /// </summary>
    public static class MasterClientPacketParser
    {
        // Strict UTF-8 codec — the lax decoder silently substitutes U+FFFD for
        // malformed byte sequences, which would let a hostile server collapse
        // two distinct player identifiers onto the same string and so defeat
        // the roster-equality comparisons the decoded values feed (host
        // promotion, kick targeting).  Symmetric with RoomPacketParser and the
        // RPC stack, both of which already decode untrusted input strictly.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Decode a server broadcast payload as strict UTF-8.  Returns false —
        // so the caller discards the packet, honouring its Try* contract —
        // when the bytes are not well-formed UTF-8.
        private static bool TryDecodeStrictUtf8(byte[] payload, out string json)
        {
            try
            {
                json = StrictUtf8.GetString(payload);
                return true;
            }
            catch (DecoderFallbackException)
            {
                json = null;
                return false;
            }
        }

        /// <summary>
        /// Parse a <c>master_client_changed</c> broadcast payload.
        /// JSON shape:
        /// <c>{"previous_master_id":"...","new_master_id":"..."}</c>
        /// </summary>
        public static bool ParseChanged(byte[] payload, out string previousMasterId, out string newMasterId)
        {
            previousMasterId = string.Empty;
            newMasterId      = string.Empty;
            if (payload == null || payload.Length == 0) return false;
            if (!TryDecodeStrictUtf8(payload, out string json)) return false;

            // Accept either the Phase 2 explicit key names or the Phase 1
            // leave-driven promotion shape ("previous_host_id" / "new_host_id").
            // The gateway bridges both NATS event types onto the same wire
            // packet, so the SDK must accept both key spellings.
            previousMasterId = MiniJson.ExtractStringField(json, "previous_master_id")
                ?? MiniJson.ExtractStringField(json, "previous_host_id")
                ?? string.Empty;
            newMasterId = MiniJson.ExtractStringField(json, "new_master_id")
                ?? MiniJson.ExtractStringField(json, "new_host_id")
                ?? string.Empty;

            return !string.IsNullOrEmpty(newMasterId);
        }

        /// <summary>
        /// Parse a <c>player_kicked</c> broadcast payload.
        /// JSON shape: <c>{"kicker_id":"...","target_player_id":"..."}</c>.
        /// </summary>
        public static bool ParseKick(byte[] payload, out string kickerId, out string targetPlayerId)
        {
            kickerId       = string.Empty;
            targetPlayerId = string.Empty;
            if (payload == null || payload.Length == 0) return false;
            if (!TryDecodeStrictUtf8(payload, out string json)) return false;

            kickerId       = MiniJson.ExtractStringField(json, "kicker_id") ?? string.Empty;
            targetPlayerId = MiniJson.ExtractStringField(json, "target_player_id") ?? string.Empty;

            return !string.IsNullOrEmpty(targetPlayerId);
        }

        /// <summary>
        /// Parse an <c>all_players_scene_loaded</c> broadcast payload.
        /// JSON shape: <c>{"scene_name":"..."}</c>.
        /// </summary>
        public static bool ParseSceneLoaded(byte[] payload, out string sceneName)
        {
            sceneName = string.Empty;
            if (payload == null || payload.Length == 0) return false;
            if (!TryDecodeStrictUtf8(payload, out string json)) return false;

            sceneName = MiniJson.ExtractStringField(json, "scene_name") ?? string.Empty;
            return !string.IsNullOrEmpty(sceneName);
        }

        // ── Minimal JSON field extractor ─────────────────────────────────
        //
       // We deliberately avoid pulling in a full JSON library — every
        // server broadcast at this layer has a fixed 1–3-field shape and a
        // single pass character scanner is cheaper than a generic parser.
        // The helper supports only string values; unknown fields or other
        // types are returned as null so the caller can fall back.

        private static class MiniJson
        {
            /// <summary>
            /// Return the value of <paramref name="fieldName"/> as a string,
            /// or <see langword="null"/> when the field is missing / not a
            /// string.  Handles standard JSON string escapes (\", \\, \b,
            /// \f, \n, \r, \t, \uXXXX).
            /// </summary>
            public static string ExtractStringField(string json, string fieldName)
            {
                // Search for `"fieldName"` followed by `:` and an opening quote.
                // A loop is required because the same token might appear as a
                // string value before it appears as a field key — we must skip
                // those false matches.
                var token = "\"" + fieldName + "\"";
                int searchFrom = 0;
                while (true)
                {
                    int i = json.IndexOf(token, searchFrom);
                    if (i < 0) return null;

                    // Verify this occurrence is a JSON field key, not a string value.
                    // A field key is always preceded (after optional whitespace) by
                    // `{` (first field in object) or `,` (subsequent field).
                    // Any other preceding character means we matched inside a value.
                    int check = i - 1;
                    while (check >= 0 &&
                           (json[check] == ' ' || json[check] == '\t' ||
                            json[check] == '\n' || json[check] == '\r'))
                        check--;
                    if (check < 0 || (json[check] != '{' && json[check] != ','))
                    {
                        searchFrom = i + token.Length;
                        continue;
                    }

                    i += token.Length;
                    // Skip whitespace and expect ':'.
                    while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
                    if (i >= json.Length || json[i] != ':') { searchFrom = i; continue; }
                    i++;
                    // Skip whitespace and expect opening '"'.
                    while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\n' || json[i] == '\r')) i++;
                    if (i >= json.Length || json[i] != '"') { searchFrom = i; continue; }
                    i++;

                    var sb = new StringBuilder();
                    while (i < json.Length)
                    {
                        char c = json[i];
                        if (c == '"') return sb.ToString();
                        if (c == '\\')
                        {
                            if (i + 1 >= json.Length) return null;
                            char esc = json[i + 1];
                            switch (esc)
                            {
                                case '"':  sb.Append('"');  i += 2; break;
                                case '\\': sb.Append('\\'); i += 2; break;
                                case '/':  sb.Append('/');  i += 2; break;
                                case 'b':  sb.Append('\b'); i += 2; break;
                                case 'f':  sb.Append('\f'); i += 2; break;
                                case 'n':  sb.Append('\n'); i += 2; break;
                                case 'r':  sb.Append('\r'); i += 2; break;
                                case 't':  sb.Append('\t'); i += 2; break;
                                case 'u':
                                    if (i + 6 > json.Length) return null;
                                    int code = 0;
                                    for (int k = 0; k < 4; k++)
                                    {
                                        char h = json[i + 2 + k];
                                        int v;
                                        if (h >= '0' && h <= '9') v = h - '0';
                                        else if (h >= 'a' && h <= 'f') v = 10 + (h - 'a');
                                        else if (h >= 'A' && h <= 'F') v = 10 + (h - 'A');
                                        else return null;
                                        code = (code << 4) | v;
                                    }
                                    // Reject an escaped NUL or C0 control code.
                                    // The strict-UTF-8 gate inspects the raw
                                    // payload bytes only — a \uXXXX escape is
                                    // itself plain ASCII and passes that gate,
                                    // so a control codepoint must be refused
                                    // here before it reaches a roster-compared
                                    // identifier.  Mirrors LobbyPacketParser.
                                    if (code < 0x20) return null;
                                    sb.Append((char)code);
                                    i += 6;
                                    break;
                                default: return null;
                            }
                        }
                        else
                        {
                            // A raw C0 control character (NUL included) is a
                            // JSON-spec violation — control codes must arrive
                            // \u-escaped.  A raw NUL is well-formed UTF-8 and
                            // would otherwise pass the strict-UTF-8 gate, so
                            // it is refused at the structural layer.
                            if (c < 0x20) return null;
                            sb.Append(c);
                            i++;
                        }
                    }
                    return null; // unterminated string
                }
            }
        }
    }
}
