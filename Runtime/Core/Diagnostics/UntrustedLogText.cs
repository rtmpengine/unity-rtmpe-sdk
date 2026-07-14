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
        ///  <item>every control character a log sink could act on — C0
        ///        (<c>\x00</c>–<c>\x1F</c>, incl. the <c>\x1B</c> ANSI
        ///        introducer), <c>\x7F</c>, the C1 block (<c>\x80</c>–<c>\x9F</c>,
        ///        incl. NEL and the 8-bit CSI), and the Unicode line/paragraph
        ///        separators (<c>U+2028</c>/<c>U+2029</c>) — is replaced with
        ///        <c>'?'</c>; the original byte is irrecoverable but the visible
        ///        length is preserved;</item>
        ///  <item>the content is capped at <see cref="MaxBytes"/> UTF-8 bytes,
        ///        measured in encoded bytes rather than UTF-16 code units so a
        ///        multi-byte value cannot stretch the log line past the bound.
        ///        An over-length value is clipped and a trailing <c>'…'</c>
        ///        (U+2026, three UTF-8 bytes) is appended as the truncation
        ///        marker, so the total encoded output may reach
        ///        <see cref="MaxBytes"/> + 3 bytes.</item>
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

                // An unpaired surrogate (\uD800–\uDFFF without its partner) is
                // not a valid Unicode scalar and would cause Encoding.UTF8 to
                // throw or silently emit garbage on some runtimes.  Substitute
                // '?' so the output is always well-formed UTF-8.
                bool isLoneSurrogate = !isSurrogatePair
                    && (char.IsHighSurrogate(c) || char.IsLowSurrogate(c));

                // Control characters a Unicode-aware log sink could treat as a
                // line break or an 8-bit escape: C0 (0x00-0x1F, incl. ESC 0x1B),
                // DEL (0x7F), the C1 block (0x80-0x9F, incl. NEL 0x85 and the
                // 8-bit CSI 0x9B), and the Unicode line / paragraph separators
                // (U+2028 / U+2029).  All fold to '?' so a hostile value cannot
                // spoof an extra log entry or rewrite the terminal.
                bool isControl = c < 0x20 || c == 0x7F
                              || (c >= 0x80 && c <= 0x9F)
                              || c == 0x2028 || c == 0x2029;

                int charBytes;
                if (isSurrogatePair)                   charBytes = 4;
                else if (isLoneSurrogate || isControl) charBytes = 1; // '?' substitution costs one byte
                else if (c <= 0x7F)                    charBytes = 1;
                else if (c <= 0x7FF)                   charBytes = 2;
                else                                   charBytes = 3;

                if (usedBytes + charBytes > MaxBytes)
                {
                    truncated = true;
                    break;
                }
                usedBytes += charBytes;

                if (isControl || isLoneSurrogate)
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
