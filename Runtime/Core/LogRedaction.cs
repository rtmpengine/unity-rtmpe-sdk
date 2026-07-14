// RTMPE SDK — Runtime/Core/LogRedaction.cs
//
// Helpers that redact sensitive values before they reach a log line.
// Debug logs are frequently captured by analytics, third-party crash
// reporters, or pasted into bug reports — a player_id, invite token, or
// session identifier leaked through a debug print can survive long
// after the session itself.
//
// Two redaction families:
//
//  PII (display names / player ids / invite tokens):
//    • DisplayName : first character + "***".  Preserves a single-letter
//                    fingerprint useful for visual debugging.
//    • PlayerId    : first 4 characters + "***".  UUID-shaped ids retain
//                    enough prefix to correlate logs without exposing the
//                    full id.
//    • RoomCode    : "***".  Six-character invite tokens have insufficient
//                    entropy to allow any prefix leak.
//
//  Identifiers (crypto / session / arbitrary scalars):
//    • Redact(uint)   : 4 leading hex chars + "***" of the 8-char rendering.
//    • Redact(ulong)  : 4 leading hex chars + "***" of the 16-char rendering.
//    • Redact(string) : first 4 chars + "***" with explicit "<null>" /
//                       "<empty>" sentinels so absences themselves remain
//                       loggable.
//
// All redacted forms are deterministic — the same id always renders to the
// same prefix, which is what support workflows want when correlating a
// player-side log with a server-side trace.

using System.Globalization;

namespace RTMPE.Core
{
    /// <summary>
    /// Redact PII fields and sensitive identifiers before they are written
    /// to log sinks.
    /// </summary>
    public static class LogRedaction
    {
        /// <summary>
        /// Redact a display name to a single-character prefix plus
        /// <c>***</c>.  Returns the empty string for null or empty input.
        /// </summary>
        public static string DisplayName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            char first = name[0];
            // Replace control characters with a visible glyph before logging:
            // C0 (incl. the embedded NUL, which would truncate the line at any
            // downstream sink using C-style parsing), DEL, the C1 block, and the
            // Unicode line/paragraph separators (0x2028/0x2029) that a
            // Unicode-aware sink could treat as a line break to spoof an entry.
            if (first < 0x20 || first == 0x7F
                || (first >= 0x80 && first <= 0x9F)
                || first == 0x2028 || first == 0x2029) first = '?';
            // string.Concat(char, string) yields the same shape as the
            // char's ToString concatenated to "***" — using it explicitly
            // sidesteps a `char + string` overload-resolution surprise on
            // older C# language levels where the left-hand char widens to
            // int first and the resulting "0x3F***" form would silently
            // ship through to the log.
            return string.Concat(first.ToString(), "***");
        }

        /// <summary>
        /// Redact a player ID to its first four characters plus <c>***</c>.
        /// IDs shorter than four characters fall back to fully-redacted.
        /// </summary>
        public static string PlayerId(string id)
        {
            if (string.IsNullOrEmpty(id)) return string.Empty;
            if (id.Length < 4) return "***";
            return id.Substring(0, 4) + "***";
        }

        /// <summary>
        /// Fully redact a room code / invite token.  Always returns
        /// <c>***</c> for non-empty input.  Six-character codes have
        /// insufficient entropy to allow any prefix leak.
        /// </summary>
        public static string RoomCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return string.Empty;
            return "***";
        }

        /// <summary>
        /// Redact a 32-bit unsigned identifier to its first 4 hex characters
        /// followed by an asterisk marker.
        /// </summary>
        public static string Redact(uint id)
        {
            return id.ToString("x8", CultureInfo.InvariantCulture).Substring(0, 4) + "***";
        }

        /// <summary>
        /// Redact a 64-bit unsigned identifier to its first 4 hex characters.
        /// Used for the gateway session id.
        /// </summary>
        public static string Redact(ulong id)
        {
            return id.ToString("x16", CultureInfo.InvariantCulture).Substring(0, 4) + "***";
        }

        /// <summary>
        /// Redact an arbitrary identifier string — the first 4 characters
        /// are preserved, the remainder is replaced by <c>***</c>.  Null /
        /// empty input is rendered as a placeholder so the absence itself
        /// remains loggable.
        /// </summary>
        public static string Redact(string value)
        {
            if (value == null) return "<null>";
            if (value.Length == 0) return "<empty>";
            // Surface inputs that contain control characters as a dedicated
            // sentinel rather than emitting the raw prefix.  Covers C0 (incl.
            // embedded NUL, which would truncate the log line at any C-style
            // sink and collapse two distinct ids into one fingerprint), DEL,
            // the C1 block, and the Unicode line/paragraph separators.
            int prefixLen = value.Length <= 4 ? value.Length : 4;
            for (int i = 0; i < prefixLen; i++)
            {
                char c = value[i];
                if (c < 0x20 || c == 0x7F
                    || (c >= 0x80 && c <= 0x9F)
                    || c == 0x2028 || c == 0x2029) return "<ctrl>***";
            }
            if (value.Length <= 4) return value + "***";
            return value.Substring(0, 4) + "***";
        }
    }
}
