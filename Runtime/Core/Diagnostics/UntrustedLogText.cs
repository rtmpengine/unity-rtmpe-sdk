// RTMPE SDK — Runtime/Core/Diagnostics/UntrustedLogText.cs
//
// Renders attacker-influenced strings safe for inclusion in a log line.
//
// Values such as JWT `iss` / `aud` claims arrive across the wire and may be
// echoed verbatim into a rejection message that lands in the Unity console
// and any downstream log aggregator (Sentry, CloudWatch, ...).  Without
// scrubbing, a hostile value could embed ANSI escape sequences to rewrite a
// developer's terminal, line breaks to spoof additional log entries, or an
// oversized payload to flood the log pipeline.

using System.Text;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Sanitiser for untrusted text destined for a log message.
    /// </summary>
    internal static class UntrustedLogText
    {
        /// <summary>
        /// Upper bound, in UTF-8 bytes, on the rendered fragment.  The longest
        /// realistic OIDC issuer URL fits comfortably inside this budget; a
        /// pathological value that attempts to flood the log is clipped and
        /// marked with a trailing ellipsis.
        /// </summary>
        internal const int MaxBytes = 128;

        /// <summary>
        /// Returns <paramref name="raw"/> rendered safe for a log line:
        /// <list type="bullet">
        ///  <item>every C0 control character (<c>\x00</c>–<c>\x1F</c>, which
        ///        includes the <c>\x1B</c> ANSI CSI introducer) and
        ///        <c>\x7F</c> is replaced with <c>'?'</c> — the original byte
        ///        is irrecoverable but the visible length is preserved;</item>
        ///  <item>the content is capped at <see cref="MaxBytes"/> UTF-8 bytes,
        ///        measured in encoded bytes rather than UTF-16 code units so a
        ///        multi-byte value cannot stretch the log line past the bound;
        ///        an over-length value is clipped and a trailing <c>'…'</c> is
        ///        appended as the truncation marker.</item>
        /// </list>
        /// A <see langword="null"/> or empty input yields an empty string.
        /// </summary>
        internal static string Sanitise(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            var sb = new StringBuilder(MaxBytes + 1);
            int usedBytes = 0;
            bool truncated = false;

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                // A code point above U+FFFF is encoded as a high/low surrogate
                // pair across two chars and as four UTF-8 bytes.  Measure the
                // pair as a unit so the byte budget is exact and the four-byte
                // code point is never split across the truncation boundary.
                bool isSurrogatePair =
                    char.IsHighSurrogate(c)
                    && i + 1 < raw.Length
                    && char.IsLowSurrogate(raw[i + 1]);

                int charBytes;
                if (isSurrogatePair) charBytes = 4;
                else if (c <= 0x7F)  charBytes = 1;
                else if (c <= 0x7FF) charBytes = 2;
                else                 charBytes = 3; // U+0800–U+FFFF, incl. an unpaired surrogate

                if (usedBytes + charBytes > MaxBytes)
                {
                    truncated = true;
                    break;
                }
                usedBytes += charBytes;

                // A C0 control or \x7F is one UTF-8 byte; its '?' replacement
                // is also one byte, so the substitution leaves the byte budget
                // exact.  \x1B (ESC) is folded in here so a crafted value
                // cannot rewrite the terminal via colour / cursor escapes.
                if (c < 0x20 || c == 0x7F)
                {
                    sb.Append('?');
                }
                else if (isSurrogatePair)
                {
                    sb.Append(c);
                    sb.Append(raw[i + 1]);
                    i++;
                }
                else
                {
                    sb.Append(c);
                }
            }

            if (truncated) sb.Append('…');
            return sb.ToString();
        }
    }
}
