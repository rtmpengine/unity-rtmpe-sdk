// RTMPE SDK — Runtime/Rooms/MatchmakingManager.cs
//
// High-level AutoJoinOrCreate API.
// Created by NetworkManager and receives MatchmakingResponse (0x2B) callbacks.
//
// Threading model:
//   • All public methods MUST be called from the Unity main thread.
//   • HandleMatchmakingResponse() is called from NetworkManager.ProcessPacket()
//     which runs on the main thread (via MainThreadDispatcher).

using System;
using System.Text;
using RTMPE.Core;
using RTMPE.Protocol;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Sends <c>MatchmakingRequest</c> (0x26) and processes the
    /// <c>MatchmakingResponse</c> (0x2B) reply.
    /// Access via <see cref="NetworkManager.Matchmaking"/>.
    /// </summary>
    public sealed class MatchmakingManager
    {
        private readonly PacketBuilder  _builder;
        private readonly Action<byte[]> _sendPacket;
        private readonly Func<NetworkState> _getState;

        // Player identity provided by NetworkManager at construction time.
        // Sent as the player_id field in every MatchmakingRequest so the Room
        // Service can record roster membership atomically.
        private readonly Func<string> _getPlayerId;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired on the Unity main thread when a <c>MatchmakingResponse</c> (0x2B)
        /// arrives and <c>ok=true</c>.  The argument carries the assigned room.
        /// </summary>
        public event Action<MatchmakingResult> OnMatchmakingComplete;

        /// <summary>
        /// Fired on the Unity main thread when a <c>MatchmakingResponse</c> (0x2B)
        /// arrives and <c>ok=false</c>.  The argument is the server error string.
        /// </summary>
        public event Action<string> OnMatchmakingFailed;

        // ── Constructor ──────────────────────────────────────────────────────────

        internal MatchmakingManager(
            PacketBuilder     builder,
            Action<byte[]>    sendPacket,
            Func<NetworkState> getState,
            Func<string>      getPlayerId)
        {
            _builder     = builder     ?? throw new ArgumentNullException(nameof(builder));
            _sendPacket  = sendPacket  ?? throw new ArgumentNullException(nameof(sendPacket));
            _getState    = getState    ?? throw new ArgumentNullException(nameof(getState));
            _getPlayerId = getPlayerId ?? throw new ArgumentNullException(nameof(getPlayerId));
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Send a <c>MatchmakingRequest</c> (0x26) to the server.
        /// The server atomically finds an open waiting room that matches
        /// <see cref="MatchmakingOptions.Mode"/> (and the optional lobby namespace),
        /// joins the player, or creates a new room if none is available.
        /// The result arrives via <see cref="OnMatchmakingComplete"/> or
        /// <see cref="OnMatchmakingFailed"/>.
        /// </summary>
        /// <param name="options">
        /// Matchmaking criteria. <see cref="MatchmakingOptions.Mode"/> is required.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the SDK is not connected (<see cref="NetworkState.Connected"/>
        /// or <see cref="NetworkState.InRoom"/>).
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="options"/> is null or <c>Mode</c> is empty.
        /// </exception>
        public void StartMatchmaking(MatchmakingOptions options)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Mode))
                throw new ArgumentException("MatchmakingOptions.Mode must not be empty.", nameof(options));

            var state = _getState();
            if (state != NetworkState.Connected && state != NetworkState.InRoom)
                throw new InvalidOperationException(
                    $"StartMatchmaking requires Connected or InRoom state; current state is {state}.");

            var payload = BuildMatchmakingPayload(options, _getPlayerId() ?? string.Empty);
            var packet  = _builder.Build(PacketType.MatchmakingRequest, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        // ── Inbound packet handler ────────────────────────────────────────────

        /// <summary>
        /// Called by <see cref="NetworkManager"/> when a <c>MatchmakingResponse</c>
        /// (0x2B) arrives.  Parses the JSON payload and fires the appropriate event.
        /// </summary>
        internal void HandleMatchmakingResponse(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                OnMatchmakingFailed?.Invoke("empty response");
                return;
            }

            var json = Encoding.UTF8.GetString(payload);

            bool   ok      = ExtractBool(json, "ok");
            string errorMsg = ExtractString(json, "error");

            if (!ok)
            {
                OnMatchmakingFailed?.Invoke(string.IsNullOrEmpty(errorMsg) ? "matchmaking failed" : errorMsg);
                return;
            }

            // Parse data object: { "room_id": "...", "room_code": "...", "created": bool }
            int dataStart = json.IndexOf("\"data\"", StringComparison.Ordinal);
            string roomId   = string.Empty;
            string roomCode = string.Empty;
            bool   created  = false;

            if (dataStart >= 0)
            {
                int objStart = json.IndexOf('{', dataStart);
                int objEnd   = objStart >= 0 ? json.IndexOf('}', objStart) : -1;

                if (objStart >= 0 && objEnd > objStart)
                {
                    var data = json.Substring(objStart, objEnd - objStart + 1);
                    roomId   = ExtractString(data, "room_id");
                    roomCode = ExtractString(data, "room_code");
                    created  = ExtractBool(data, "created");
                }
            }

            OnMatchmakingComplete?.Invoke(new MatchmakingResult(roomId, roomCode, created));
        }

        // ── Packet serialisation ──────────────────────────────────────────────

        private static byte[] BuildMatchmakingPayload(MatchmakingOptions opts, string playerId)
        {
            var sb = new StringBuilder(128);
            sb.Append('{');
            sb.Append($"\"mode\":{JsonString(opts.Mode)}");
            if (!string.IsNullOrEmpty(opts.LobbyName))
                sb.Append($",\"lobby_name\":{JsonString(opts.LobbyName)}");
            if (opts.MinPlayers > 0)
                sb.Append($",\"min_players\":{opts.MinPlayers}");
            if (opts.MaxPlayers > 0)
                sb.Append($",\"max_players\":{opts.MaxPlayers}");
            sb.Append($",\"player_id\":{JsonString(playerId)}");
            if (!string.IsNullOrEmpty(opts.DisplayName))
                sb.Append($",\"display_name\":{JsonString(opts.DisplayName)}");
            sb.Append('}');
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── Minimal JSON helpers ──────────────────────────────────────────────

        private static string JsonString(string s)
        {
            return "\"" + (s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static string ExtractString(string json, string key)
        {
            var needle = $"\"{key}\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return string.Empty;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return string.Empty;

            int quote1 = json.IndexOf('"', colon + 1);
            if (quote1 < 0) return string.Empty;

            int quote2 = quote1 + 1;
            while (quote2 < json.Length)
            {
                if (json[quote2] == '"' && json[quote2 - 1] != '\\') break;
                quote2++;
            }
            if (quote2 >= json.Length) return string.Empty;

            return json.Substring(quote1 + 1, quote2 - quote1 - 1);
        }

        private static bool ExtractBool(string json, string key)
        {
            var needle = $"\"{key}\"";
            int idx = json.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0) return false;

            int colon = json.IndexOf(':', idx + needle.Length);
            if (colon < 0) return false;

            int start = colon + 1;
            while (start < json.Length && json[start] == ' ') start++;

            return start + 3 < json.Length &&
                   json[start] == 't' &&
                   json[start + 1] == 'r' &&
                   json[start + 2] == 'u' &&
                   json[start + 3] == 'e';
        }
    }
}
