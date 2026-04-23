// RTMPE SDK — Runtime/Rooms/MasterClientPacketBuilder.cs
//
// Builds the payload bytes for the Phase 2 room-management packets:
//
//   • MasterClientTransfer (0x2D) — client → server
//   • KickPlayer            (0x2E) — client → server
//   • SceneLoaded           (0x2F) — client → server
//
// The payload is a minimal JSON document.  Keeping the builder in a
// dedicated file (mirroring PropertyPacketBuilder) lets the packet layer
// stay Unity-agnostic and keeps the wire format self-documenting.

using System;
using System.Text;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Static helpers that serialise the inner JSON payloads for Phase 2
    /// room-management packets.  Every method returns UTF-8 bytes ready to
    /// pass to <see cref="RTMPE.Protocol.PacketBuilder.Build"/>.
    /// </summary>
    public static class MasterClientPacketBuilder
    {
        /// <summary>
        /// Build the payload for a <c>MasterClientTransfer</c> (0x2D) packet.
        /// Shape: <c>{"target_player_id":"..."}</c>.
        /// </summary>
        public static byte[] BuildTransferPayload(string targetPlayerId)
        {
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));
            return Encoding.UTF8.GetBytes(
                "{\"target_player_id\":" + JsonEncodeString(targetPlayerId) + "}");
        }

        /// <summary>
        /// Build the payload for a <c>KickPlayer</c> (0x2E) packet.
        /// Shape: <c>{"target_player_id":"..."}</c>.
        /// </summary>
        public static byte[] BuildKickPayload(string targetPlayerId)
        {
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));
            return Encoding.UTF8.GetBytes(
                "{\"target_player_id\":" + JsonEncodeString(targetPlayerId) + "}");
        }

        /// <summary>
        /// Build the payload for a <c>SceneLoaded</c> (0x2F) packet.
        /// Shape: <c>{"scene_name":"..."}</c>.
        /// </summary>
        public static byte[] BuildSceneLoadedPayload(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));
            return Encoding.UTF8.GetBytes(
                "{\"scene_name\":" + JsonEncodeString(sceneName) + "}");
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Minimal JSON string encoder: wraps in quotes and escapes the
        /// characters required by RFC 8259.  Kept private so the payload
        /// shape stays owned by this file.
        /// </summary>
        private static string JsonEncodeString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
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
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
