// RTMPE SDK — Runtime/Rooms/PropertyJson.cs
//
// Hand-rolled JSON encoder/decoder for Custom Property payloads.
//
// Why hand-rolled: Unity's built-in JsonUtility cannot serialise
// Dictionary<string, T>, and System.Text.Json is not shipped with .NET
// Standard 2.1 in Unity 6.  The property payload is tiny (≤ 20 keys ×
// ≤ 512 bytes each), so a targeted encoder keeps the SDK dependency-free
// without pulling in a general-purpose JSON library.
//
// Wire shape (matches the Go server's RoomPropertyUpdatePayload):
//   {
//     "version": 3,
//     "properties": {
//       "GameMode": {"type":"string","value":"TDM"},
//       "MaxScore": {"type":"int","value":100}
//     }
//   }
//
// PlayerPropertyUpdatePayload additionally has a top-level "player_id":"…".
//
// Type-discriminator strings match exactly:
//   int | float | bool | string | bytes | vector3 | color

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Encoder/decoder for custom property payloads.  Produces
    /// canonical JSON (no whitespace, LF-free) suitable for use as the raw
    /// <c>payload</c> field of a <c>RoomPropertyUpdate</c> / <c>PlayerPropertyUpdate</c>
    /// packet.
    /// </summary>
    public static class PropertyJson
    {
        // ─── Type-tag constants ────────────────────────────────────────────
        //
        // These MUST match `entities.PropertyType*` strings in the Go server.
        // Any rename here must happen simultaneously on both sides.

        internal const string TagInt     = "int";
        internal const string TagFloat   = "float";
        internal const string TagBool    = "bool";
        internal const string TagString  = "string";
        internal const string TagBytes   = "bytes";
        internal const string TagVector3 = "vector3";
        internal const string TagColor   = "color";

        // ─── Encode ────────────────────────────────────────────────────────

        /// <summary>
        /// Encode a Custom Property payload for a <c>RoomPropertyUpdate</c> (0x24) packet.
        /// </summary>
        public static string EncodeRoomPayload(int version, IReadOnlyDictionary<string, PropertyValue> props)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"version\":").Append(version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"properties\":");
            AppendPropertiesMap(sb, props);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Encode a Custom Property payload for a <c>PlayerPropertyUpdate</c> (0x25) packet.
        /// </summary>
        public static string EncodePlayerPayload(
            string playerId, int version, IReadOnlyDictionary<string, PropertyValue> props)
        {
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId must not be null or empty.", nameof(playerId));

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"player_id\":");
            AppendJsonString(sb, playerId);
            sb.Append(",\"version\":").Append(version.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"properties\":");
            AppendPropertiesMap(sb, props);
            sb.Append('}');
            return sb.ToString();
        }

        private static void AppendPropertiesMap(StringBuilder sb, IReadOnlyDictionary<string, PropertyValue> props)
        {
            sb.Append('{');
            if (props != null)
            {
                bool first = true;
                foreach (var kv in props)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendJsonString(sb, kv.Key);
                    sb.Append(":{\"type\":");
                    AppendJsonString(sb, TagFor(kv.Value.Type));
                    sb.Append(",\"value\":");
                    AppendValue(sb, kv.Value);
                    sb.Append('}');
                }
            }
            sb.Append('}');
        }

        private static string TagFor(PropertyType t)
        {
            switch (t)
            {
                case PropertyType.Int:     return TagInt;
                case PropertyType.Float:   return TagFloat;
                case PropertyType.Bool:    return TagBool;
                case PropertyType.String:  return TagString;
                case PropertyType.Bytes:   return TagBytes;
                case PropertyType.Vector3: return TagVector3;
                case PropertyType.Color:   return TagColor;
                default: throw new InvalidOperationException($"Unknown PropertyType: {t}");
            }
        }

        private static void AppendValue(StringBuilder sb, PropertyValue v)
        {
            switch (v.Type)
            {
                case PropertyType.Int:
                    sb.Append(v.AsInt().ToString(CultureInfo.InvariantCulture));
                    break;
                case PropertyType.Float:
                    // "R" round-trips losslessly; invariant culture avoids locale decimal commas.
                    sb.Append(v.AsFloat().ToString("R", CultureInfo.InvariantCulture));
                    break;
                case PropertyType.Bool:
                    sb.Append(v.AsBool() ? "true" : "false");
                    break;
                case PropertyType.String:
                    AppendJsonString(sb, v.AsString());
                    break;
                case PropertyType.Bytes:
                    // Base64 — matches Go's []byte JSON encoding.
                    sb.Append('"').Append(Convert.ToBase64String(v.AsBytes())).Append('"');
                    break;
                case PropertyType.Vector3:
                    {
                        var vec = v.AsVector3();
                        sb.Append('[');
                        sb.Append(vec.x.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(vec.y.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(vec.z.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(']');
                        break;
                    }
                case PropertyType.Color:
                    {
                        var c = v.AsColor();
                        sb.Append('[');
                        sb.Append(c.r.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(c.g.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(c.b.ToString("R", CultureInfo.InvariantCulture)).Append(',');
                        sb.Append(c.a.ToString("R", CultureInfo.InvariantCulture));
                        sb.Append(']');
                        break;
                    }
                default: throw new InvalidOperationException($"Unknown PropertyType: {v.Type}");
            }
        }

        /// <summary>
        /// Appends a JSON-escaped string literal (including the surrounding
        /// quotes) to <paramref name="sb"/>.
        /// </summary>
        internal static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"':  sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b");  break;
                        case '\f': sb.Append("\\f");  break;
                        case '\n': sb.Append("\\n");  break;
                        case '\r': sb.Append("\\r");  break;
                        case '\t': sb.Append("\\t");  break;
                        default:
                            if (c < 0x20)
                            {
                                sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        // ─── Decode ────────────────────────────────────────────────────────
        //
        // Minimal recursive-descent parser — sufficient for the property
        // payload shape above.  Rejects malformed input with
        // FormatException carrying an actionable error message.

        /// <summary>
        /// Decode the <c>room_properties_updated</c> broadcast payload the
        /// server publishes after an accepted 0x24 packet.  Returns the
        /// parsed version and properties.
        /// </summary>
        public static (int Version, Dictionary<string, PropertyValue> Properties) DecodeRoomPayload(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new FormatException("PropertyJson: empty JSON");

            int  pos      = 0;
            int  version  = 0;
            var  props    = new Dictionary<string, PropertyValue>();
            bool seenVer  = false, seenProps = false;

            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '{');
            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == '}') { pos++; break; }
                string key = ReadString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                if (key == "version")
                {
                    version = ReadInt(json, ref pos);
                    seenVer = true;
                }
                else if (key == "properties")
                {
                    ReadPropertiesMap(json, ref pos, props);
                    seenProps = true;
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == ',') { pos++; continue; }
            }

            if (!seenVer)   throw new FormatException("PropertyJson: missing 'version'");
            if (!seenProps) throw new FormatException("PropertyJson: missing 'properties'");
            return (version, props);
        }

        /// <summary>
        /// Decode the <c>player_properties_updated</c> broadcast payload.
        /// </summary>
        public static (string PlayerId, int Version, Dictionary<string, PropertyValue> Properties)
            DecodePlayerPayload(string json)
        {
            if (string.IsNullOrEmpty(json))
                throw new FormatException("PropertyJson: empty JSON");

            int  pos      = 0;
            int  version  = 0;
            string player = null;
            var  props    = new Dictionary<string, PropertyValue>();
            bool seenVer  = false, seenProps = false, seenPlayer = false;

            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '{');
            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == '}') { pos++; break; }
                string key = ReadString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                if (key == "player_id")
                {
                    player     = ReadString(json, ref pos);
                    seenPlayer = true;
                }
                else if (key == "version")
                {
                    version = ReadInt(json, ref pos);
                    seenVer = true;
                }
                else if (key == "properties")
                {
                    ReadPropertiesMap(json, ref pos, props);
                    seenProps = true;
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == ',') { pos++; continue; }
            }

            if (!seenPlayer) throw new FormatException("PropertyJson: missing 'player_id'");
            if (!seenVer)    throw new FormatException("PropertyJson: missing 'version'");
            if (!seenProps)  throw new FormatException("PropertyJson: missing 'properties'");
            return (player, version, props);
        }

        private static void ReadPropertiesMap(string json, ref int pos, Dictionary<string, PropertyValue> props)
        {
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '{');
            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == '}') { pos++; break; }
                string key = ReadString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                var val = ReadPropertyValue(json, ref pos);
                props[key] = val;
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == ',') { pos++; continue; }
            }
        }

        private static PropertyValue ReadPropertyValue(string json, ref int pos)
        {
            SkipWhitespace(json, ref pos);
            Expect(json, ref pos, '{');
            string type   = null;
            string rawVal = null;
            int    valStart = -1, valEnd = -1;
            while (true)
            {
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == '}') { pos++; break; }
                string key = ReadString(json, ref pos);
                SkipWhitespace(json, ref pos);
                Expect(json, ref pos, ':');
                SkipWhitespace(json, ref pos);
                if (key == "type")
                {
                    type = ReadString(json, ref pos);
                }
                else if (key == "value")
                {
                    valStart = pos;
                    SkipValue(json, ref pos);
                    valEnd = pos;
                }
                else
                {
                    SkipValue(json, ref pos);
                }
                SkipWhitespace(json, ref pos);
                if (Peek(json, pos) == ',') { pos++; continue; }
            }

            if (type == null || valStart < 0)
                throw new FormatException("PropertyJson: property missing type or value");

            rawVal = json.Substring(valStart, valEnd - valStart).Trim();
            return DecodeByType(type, rawVal);
        }

        private static PropertyValue DecodeByType(string type, string raw)
        {
            switch (type)
            {
                case TagInt:
                    return PropertyValue.OfInt(int.Parse(raw, CultureInfo.InvariantCulture));
                case TagFloat:
                    return PropertyValue.OfFloat(float.Parse(raw, CultureInfo.InvariantCulture));
                case TagBool:
                    if (raw == "true")  return PropertyValue.OfBool(true);
                    if (raw == "false") return PropertyValue.OfBool(false);
                    throw new FormatException("PropertyJson: invalid bool: " + raw);
                case TagString:
                    {
                        int p = 0;
                        return PropertyValue.OfString(ReadString(raw, ref p));
                    }
                case TagBytes:
                    {
                        int p = 0;
                        var s = ReadString(raw, ref p);
                        return PropertyValue.OfBytes(Convert.FromBase64String(s));
                    }
                case TagVector3:
                    {
                        var arr = ReadFloatArray(raw, 3);
                        return PropertyValue.OfVector3(new UnityEngine.Vector3(arr[0], arr[1], arr[2]));
                    }
                case TagColor:
                    {
                        var arr = ReadFloatArray(raw, 4);
                        return PropertyValue.OfColor(new UnityEngine.Color(arr[0], arr[1], arr[2], arr[3]));
                    }
                default:
                    throw new FormatException("PropertyJson: unknown type: " + type);
            }
        }

        private static float[] ReadFloatArray(string raw, int expected)
        {
            var parts = raw.Trim().TrimStart('[').TrimEnd(']').Split(',');
            if (parts.Length != expected)
                throw new FormatException($"PropertyJson: expected array length {expected}, got {parts.Length}");
            var result = new float[expected];
            for (int i = 0; i < expected; i++)
            {
                result[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            }
            return result;
        }

        // ─── Parser primitives ─────────────────────────────────────────────

        private static void SkipWhitespace(string s, ref int pos)
        {
            while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
                pos++;
        }

        private static char Peek(string s, int pos) => pos < s.Length ? s[pos] : '\0';

        private static void Expect(string s, ref int pos, char c)
        {
            if (pos >= s.Length || s[pos] != c)
                throw new FormatException($"PropertyJson: expected '{c}' at {pos}");
            pos++;
        }

        private static string ReadString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"')
                throw new FormatException($"PropertyJson: expected '\"' at {pos}");
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length && s[pos] != '"')
            {
                if (s[pos] == '\\' && pos + 1 < s.Length)
                {
                    char esc = s[pos + 1];
                    switch (esc)
                    {
                        case '"':  sb.Append('"');  pos += 2; break;
                        case '\\': sb.Append('\\'); pos += 2; break;
                        case '/':  sb.Append('/');  pos += 2; break;
                        case 'b':  sb.Append('\b'); pos += 2; break;
                        case 'f':  sb.Append('\f'); pos += 2; break;
                        case 'n':  sb.Append('\n'); pos += 2; break;
                        case 'r':  sb.Append('\r'); pos += 2; break;
                        case 't':  sb.Append('\t'); pos += 2; break;
                        case 'u':
                            {
                                if (pos + 6 > s.Length)
                                    throw new FormatException("PropertyJson: truncated \\u escape");
                                int codeUnit = int.Parse(
                                    s.Substring(pos + 2, 4),
                                    NumberStyles.HexNumber,
                                    CultureInfo.InvariantCulture);
                                pos += 6;

                                // UTF-16 surrogate pair handling.
                                //
                                // Characters above U+FFFF must be encoded as a
                                // high-surrogate (0xD800-0xDBFF) followed by a
                                // low-surrogate (0xDC00-0xDFFF).  A bare
                                // surrogate (unpaired) is RFC 8259 §7-invalid
                                // in strict mode, but many producers emit them
                                // — we therefore:
                                //   1. Combine a valid pair into one char pair
                                //      (which StringBuilder stores correctly).
                                //   2. Append a bare high-surrogate as-is to
                                //      preserve round-trip identity with the
                                //      Unicode replacement-character rule.
                                if (codeUnit >= 0xD800 && codeUnit <= 0xDBFF)
                                {
                                    // High-surrogate — peek for an immediately
                                    // following \uDCxx low-surrogate.
                                    if (pos + 6 <= s.Length && s[pos] == '\\' && s[pos + 1] == 'u')
                                    {
                                        int low = int.Parse(
                                            s.Substring(pos + 2, 4),
                                            NumberStyles.HexNumber,
                                            CultureInfo.InvariantCulture);
                                        if (low >= 0xDC00 && low <= 0xDFFF)
                                        {
                                            sb.Append((char)codeUnit);
                                            sb.Append((char)low);
                                            pos += 6;
                                            break;
                                        }
                                    }
                                    // Bare high-surrogate — preserve so
                                    // round-trip does not silently drop bytes.
                                    sb.Append((char)codeUnit);
                                }
                                else
                                {
                                    sb.Append((char)codeUnit);
                                }
                                break;
                            }
                        default:
                            throw new FormatException("PropertyJson: invalid escape \\" + esc);
                    }
                }
                else
                {
                    sb.Append(s[pos]);
                    pos++;
                }
            }
            if (pos >= s.Length)
                throw new FormatException("PropertyJson: unterminated string");
            pos++; // consume closing quote
            return sb.ToString();
        }

        private static int ReadInt(string s, ref int pos)
        {
            int start = pos;
            if (pos < s.Length && (s[pos] == '-' || s[pos] == '+')) pos++;
            while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') pos++;
            if (start == pos)
                throw new FormatException($"PropertyJson: expected integer at {pos}");
            return int.Parse(s.Substring(start, pos - start), CultureInfo.InvariantCulture);
        }

        private static void SkipValue(string s, ref int pos)
        {
            SkipWhitespace(s, ref pos);
            if (pos >= s.Length)
                throw new FormatException("PropertyJson: unexpected EOF");
            char c = s[pos];
            if (c == '"')
            {
                int tmp = pos;
                ReadString(s, ref tmp);
                pos = tmp;
                return;
            }
            if (c == '{' || c == '[')
            {
                char open = c, close = (c == '{') ? '}' : ']';
                int depth = 0;
                while (pos < s.Length)
                {
                    char cur = s[pos];
                    if (cur == '"')
                    {
                        int tmp = pos;
                        ReadString(s, ref tmp);
                        pos = tmp;
                        continue;
                    }
                    if (cur == open)  depth++;
                    if (cur == close) { depth--; if (depth == 0) { pos++; return; } }
                    pos++;
                }
                throw new FormatException("PropertyJson: unterminated collection");
            }
            // Primitive: read until , } ] whitespace
            while (pos < s.Length)
            {
                char cur = s[pos];
                if (cur == ',' || cur == '}' || cur == ']' ||
                    cur == ' ' || cur == '\t' || cur == '\n' || cur == '\r')
                    break;
                pos++;
            }
        }
    }
}
