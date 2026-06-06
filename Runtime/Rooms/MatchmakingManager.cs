// RTMPE SDK — Runtime/Rooms/MatchmakingManager.cs
//
// High-level AutoJoinOrCreate API.
// Created by NetworkManager and receives MatchmakingResponse (0x2B) callbacks.
//
// Threading model:
//   • All public methods MUST be called from the Unity main thread.
//   • HandleMatchmakingResponse() is called from NetworkManager.ProcessPacket()
//     which runs on the main thread (via MainThreadDispatcher).
//   • Tick(double) MUST be called from the Unity main thread (typically
//     NetworkManager.Update()).  If never called, timeout enforcement is
//     simply disabled — Cancel and once-only-delivery gates still work.
//
// Reliability guarantees:
//   • Once-only callback delivery — exactly one of OnMatchmakingComplete /
//     OnMatchmakingFailed / OnMatchmakingCancelled / OnMatchmakingTimedOut
//     fires per StartMatchmaking call, even if the server retries the
//     response or the client cancels mid-flight.
//   • Cancel — CancelFindMatch() drops any in-flight request silently;
//     subsequent server responses for that request are discarded.  Fires
//     OnMatchmakingCancelled exactly once.
//   • Timeout — when configured, fires OnMatchmakingTimedOut and latches
//     the request so a late server response is ignored.

using System;
using System.Text;
using RTMPE.Core;
using RTMPE.Protocol;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Sends <c>MatchmakingRequest</c> (0x26) and processes the
    /// <c>MatchmakingResponse</c> (0x2B) reply.
    /// Access via <see cref="NetworkManager.Matchmaking"/>.
    /// </summary>
    public sealed class MatchmakingManager
    {
        // Strict UTF-8 codec.  The default Encoding.UTF8 silently substitutes
        // U+FFFD for malformed sequences — a hostile gateway can use that to
        // smuggle bytes through `room_id` / `room_code` / `error` strings
        // that survive parse but mutate downstream string-equality
        // invariants (e.g., a JoinRoom reply matched against a U+FFFD-folded
        // RoomId).  Symmetric with M19-PROTO-04 / M19-RPC-04/05 / M18-UTF8-01.
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Default per-request timeout used when the caller does not pass an
        // explicit one.  30 s matches typical matchmaking SLA budgets and is
        // long enough to absorb a single server retry.
        internal const double DefaultTimeoutSeconds = 30.0;

        // Hard upper bound on the configurable timeout.  Caps absurd values
        // that would silently block UI flows on a stale request — a
        // matchmaking attempt that has not resolved in 5 minutes is
        // effectively a server fault and the application should restart it
        // with explicit user feedback.
        internal const double MaxTimeoutSeconds = 300.0;

        private readonly PacketBuilder  _builder;
        private readonly Action<byte[]> _sendPacket;
        private readonly Func<NetworkState> _getState;

        // Player identity provided by NetworkManager at construction time.
        // Sent as the player_id field in every MatchmakingRequest so the Room
        // Service can record roster membership atomically.
        private readonly Func<string> _getPlayerId;

        // Optional callback to record the server-derived player_id echoed in a
        // matchmaking reply (A5-2), mirroring the JoinRoom path.  Null in unit
        // tests that do not exercise identity propagation.
        private readonly Action<string> _setPlayerId;

        // Current in-flight request — null when no matchmaking is pending.
        // The sentinel doubles as the once-only-delivery latch: any inbound
        // response or cancel/timeout transition first checks this is non-null
        // and atomically clears it before invoking callbacks, so a duplicate
        // server response (gateway retry, replay) cannot fire the callback
        // a second time.
        private PendingRequest _pending;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired on the Unity main thread when a <c>MatchmakingResponse</c> (0x2B)
        /// arrives and <c>ok=true</c>.  The argument carries the assigned room.
        /// Fires AT MOST ONCE per <see cref="StartMatchmaking"/> call.
        /// </summary>
        public event Action<MatchmakingResult> OnMatchmakingComplete;

        /// <summary>
        /// Fired on the Unity main thread when a <c>MatchmakingResponse</c> (0x2B)
        /// arrives and <c>ok=false</c>.  The argument is the server error string.
        /// Fires AT MOST ONCE per <see cref="StartMatchmaking"/> call.
        /// </summary>
        public event Action<string> OnMatchmakingFailed;

        /// <summary>
        /// Fired exactly once when <see cref="CancelFindMatch"/> aborts an
        /// in-flight matchmaking request.  Useful for UI flows that want to
        /// distinguish a user-driven cancel from a server-driven failure.
        /// </summary>
        public event Action OnMatchmakingCancelled;

        /// <summary>
        /// Fired exactly once when an in-flight matchmaking request exceeds
        /// the configured timeout.  After this fires, the request is latched
        /// so a late server response is silently discarded.
        /// </summary>
        public event Action OnMatchmakingTimedOut;

        /// <summary><see langword="true"/> while a matchmaking request is in flight.</summary>
        public bool IsMatchmaking => _pending != null;

        // ── Constructor ──────────────────────────────────────────────────────────

        internal MatchmakingManager(
            PacketBuilder     builder,
            Action<byte[]>    sendPacket,
            Func<NetworkState> getState,
            Func<string>      getPlayerId,
            Action<string>    setPlayerId = null)
        {
            _builder     = builder     ?? throw new ArgumentNullException(nameof(builder));
            _sendPacket  = sendPacket  ?? throw new ArgumentNullException(nameof(sendPacket));
            _getState    = getState    ?? throw new ArgumentNullException(nameof(getState));
            _getPlayerId = getPlayerId ?? throw new ArgumentNullException(nameof(getPlayerId));
            _setPlayerId = setPlayerId; // optional — see field doc (A5-2)
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Send a <c>MatchmakingRequest</c> (0x26) to the server using the
        /// default timeout (<c>30 s</c>).  See the long overload for details.
        /// </summary>
        public void StartMatchmaking(MatchmakingOptions options)
            => StartMatchmaking(options, DefaultTimeoutSeconds);

        /// <summary>
        /// Send a <c>MatchmakingRequest</c> (0x26) to the server.
        /// The server atomically finds an open waiting room that matches
        /// <see cref="MatchmakingOptions.Mode"/> (and the optional lobby namespace),
        /// joins the player, or creates a new room if none is available.
        /// The result arrives via exactly ONE of
        /// <see cref="OnMatchmakingComplete"/>, <see cref="OnMatchmakingFailed"/>,
        /// <see cref="OnMatchmakingCancelled"/>, or <see cref="OnMatchmakingTimedOut"/>.
        /// </summary>
        /// <param name="options">
        /// Matchmaking criteria. <see cref="MatchmakingOptions.Mode"/> is required.
        /// </param>
        /// <param name="timeoutSeconds">
        /// Per-request timeout in seconds.  Values are clamped to
        /// <c>(0, <see cref="MaxTimeoutSeconds"/>]</c>; pass
        /// <c>double.PositiveInfinity</c> to disable timeout enforcement.
        /// Negative or zero values fall back to <see cref="DefaultTimeoutSeconds"/>
        /// — the SDK does not accept "fire immediate timeout" because that
        /// races every legitimate response.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the SDK is not connected, or a matchmaking request is
        /// already in flight (call <see cref="CancelFindMatch"/> first).
        /// </exception>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="options"/> is null or <c>Mode</c> is empty.
        /// </exception>
        public void StartMatchmaking(MatchmakingOptions options, double timeoutSeconds)
        {
            if (options == null)
                throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(options.Mode))
                throw new ArgumentException("MatchmakingOptions.Mode must not be empty.", nameof(options));

            var state = _getState();
            if (state != NetworkState.Connected && state != NetworkState.InRoom)
                throw new InvalidOperationException(
                    $"StartMatchmaking requires Connected or InRoom state; current state is {state}.");

            // Reject overlapping requests so the once-only-delivery latch can
            // remain a single PendingRequest.  Apps that want to switch
            // criteria mid-flight must Cancel first — explicit per design,
            // because silently overwriting a pending request would orphan its
            // callback path.
            if (_pending != null)
                throw new InvalidOperationException(
                    "StartMatchmaking: a matchmaking request is already in flight. " +
                    "Call CancelFindMatch() before issuing a new request.");

            // NaN must be rejected explicitly — the prior chain admits NaN
            // through the `!(timeoutSeconds > 0.0)` branch (which is true
            // for NaN) and silently substitutes the default timeout.
            // Silent coercion of pathological input across an API surface
            // is inconsistent with the matchmaking-options validator (which
            // throws ArgumentException for invalid options) and masks a
            // caller-side bug behind a 30-second wait.
            if (double.IsNaN(timeoutSeconds))
                throw new ArgumentException(
                    "timeoutSeconds must not be NaN.", nameof(timeoutSeconds));

            double effectiveTimeout;
            if (double.IsPositiveInfinity(timeoutSeconds))
                effectiveTimeout = double.PositiveInfinity;
            else if (!(timeoutSeconds > 0.0))
                effectiveTimeout = DefaultTimeoutSeconds;
            else if (timeoutSeconds > MaxTimeoutSeconds)
                effectiveTimeout = MaxTimeoutSeconds;
            else
                effectiveTimeout = timeoutSeconds;

            _pending = new PendingRequest(effectiveTimeout);

            var payload = BuildMatchmakingPayload(options, _getPlayerId() ?? string.Empty);
            var packet  = _builder.Build(PacketType.MatchmakingRequest, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        /// <summary>
        /// Abort an in-flight matchmaking request.  Idempotent — calling
        /// when nothing is pending is a no-op and does NOT raise events.
        /// On a real cancel, fires <see cref="OnMatchmakingCancelled"/>
        /// exactly once and discards any server response that arrives later
        /// for the same logical request.
        /// </summary>
        public void CancelFindMatch()
        {
            // ConsumePending() flips the latch atomically (single-threaded
            // main-thread contract), so concurrent Cancel + late server
            // response cannot both fire callbacks.  No-op when nothing is
            // pending — a UX-driven Cancel from a "Find Match" button must
            // be safe to press repeatedly.
            var pending = ConsumePending();
            if (pending == null) return;
            OnMatchmakingCancelled?.Invoke();
        }

        /// <summary>
        /// Drive the in-flight request's timeout clock.  Call once per frame
        /// from the host (typically <see cref="NetworkManager"/>.Update).
        /// Fires <see cref="OnMatchmakingTimedOut"/> exactly once when the
        /// elapsed wall-clock since <see cref="StartMatchmaking"/> exceeds
        /// the configured timeout.  Safe to call when no request is pending
        /// (no-op).
        /// </summary>
        /// <param name="nowSeconds">Monotonic clock reading in seconds.
        /// Most callers pass <c>UnityEngine.Time.unscaledTimeAsDouble</c>.</param>
        public void Tick(double nowSeconds)
        {
            // Reject pathological clock readings.  A NaN nowSeconds would
            // make every comparison below return false (NaN < anything is
            // false; NaN < pending.DeadlineSeconds is false), causing the
            // timeout to fire on the next sane tick — but the deadline
            // captured here would be NaN too, and once captured a NaN
            // deadline would never compare strictly less than any future
            // sample, permanently deferring the timeout.  Skipping the
            // tick is the strictly safer behaviour.
            if (double.IsNaN(nowSeconds) || double.IsNegativeInfinity(nowSeconds)) return;

            var pending = _pending;
            if (pending == null) return;
            if (double.IsPositiveInfinity(pending.TimeoutSeconds)) return;

            // First Tick after StartMatchmaking captures the deadline.  We
            // intentionally avoid stamping the deadline inside StartMatchmaking
            // so the manager remains decoupled from any specific clock source
            // — a unit test can drive Tick with synthetic timestamps.
            if (!pending.DeadlineSet)
            {
                pending.DeadlineSeconds = nowSeconds + pending.TimeoutSeconds;
                pending.DeadlineSet     = true;
                return;
            }

            if (nowSeconds < pending.DeadlineSeconds) return;

            // Latch & fire — same protocol as Cancel/response paths.  A late
            // server response after timeout is silently discarded by
            // HandleMatchmakingResponse below because _pending is null.
            if (ConsumePending() == null) return;
            OnMatchmakingTimedOut?.Invoke();
        }

        // ── Inbound packet handler ────────────────────────────────────────────

        /// <summary>
        /// Called by <see cref="NetworkManager"/> when a <c>MatchmakingResponse</c>
        /// (0x2B) arrives.  Parses the JSON payload and fires the appropriate event.
        /// </summary>
        internal void HandleMatchmakingResponse(byte[] payload)
        {
            // Once-only-delivery gate: if no request is pending the response
            // is either (a) a server retry of an already-fired response, (b)
            // an out-of-band response after Cancel, or (c) an out-of-band
            // response after Timeout.  Drop silently — callbacks already
            // fired, surfacing the duplicate would corrupt UI state.
            var pending = ConsumePending();
            if (pending == null) return;

            if (payload == null || payload.Length == 0)
            {
                OnMatchmakingFailed?.Invoke("empty response");
                return;
            }

            string json;
            try
            {
                json = StrictUtf8.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                OnMatchmakingFailed?.Invoke("malformed response");
                return;
            }

            // JsonUtility is the parser used elsewhere in this SDK
            // (NetworkManager.JwtClaimsDto / JwtHeaderDto).  Switching off
            // the prior hand-rolled needle-search helpers eliminates the
            // "true_extra → true" / unescaped-backslash misclassifications
            // that the regex-style scan accepted.  Unknown fields are
            // ignored by JsonUtility, which matches our existing forward-
            // compatibility contract for gateway responses.
            MatchmakingResponseDto dto;
            try
            {
                dto = JsonUtility.FromJson<MatchmakingResponseDto>(json);
            }
            catch (Exception)
            {
                OnMatchmakingFailed?.Invoke("malformed response");
                return;
            }

            if (dto == null)
            {
                OnMatchmakingFailed?.Invoke("malformed response");
                return;
            }

            if (!dto.ok)
            {
                OnMatchmakingFailed?.Invoke(string.IsNullOrEmpty(dto.error) ? "matchmaking failed" : dto.error);
                return;
            }

            string roomId   = string.Empty;
            string roomCode = string.Empty;
            bool   created  = false;
            string playerId = string.Empty;
            if (dto.data != null)
            {
                roomId   = dto.data.room_id   ?? string.Empty;
                roomCode = dto.data.room_code ?? string.Empty;
                created  = dto.data.created;
                playerId = dto.data.player_id ?? string.Empty;
            }

            // A5-2: record the server-derived player_id (parity with JoinRoom) so
            // the SDK's local identity is correct after a matchmaking-only flow.
            // Previously it stayed empty, which also fed an empty player_id into
            // any subsequent MatchmakingRequest.
            if (!string.IsNullOrEmpty(playerId))
                _setPlayerId?.Invoke(playerId);

            OnMatchmakingComplete?.Invoke(new MatchmakingResult(roomId, roomCode, created));
        }

        // ── Response DTOs (consumed by JsonUtility.FromJson) ───────────────────

        // Public fields are required by JsonUtility's reflection-based binder.
        // Names mirror the gateway's snake_case wire schema verbatim.
        [Serializable]
        private sealed class MatchmakingResponseDto
        {
            public bool   ok;
            public string error;
            public MatchmakingDataDto data;
        }

        [Serializable]
        private sealed class MatchmakingDataDto
        {
            public string room_id;
            public string room_code;
            public bool   created;
            public string player_id; // A5-2: server-derived authoritative id
        }

        // ── Latch ──────────────────────────────────────────────────────────────

        // Atomically detach the pending request and return it.  The "atomic"
        // qualifier here is the single-threaded main-thread contract — a
        // background thread MUST NOT call into MatchmakingManager.  This
        // method is the only place _pending becomes null after a successful
        // Start, so all four terminal paths (Complete / Failed / Cancelled /
        // TimedOut) funnel through it and the latch invariant holds.
        private PendingRequest ConsumePending()
        {
            var p = _pending;
            _pending = null;
            return p;
        }

        // ── Pending-request record ─────────────────────────────────────────────

        // Sealed class instead of struct so the field-replace pattern in
        // ConsumePending is genuinely atomic on the main thread (an
        // assignment to a reference field is a single store).  A struct
        // would require Interlocked or risk a torn read on misaligned
        // platforms (e.g. 32-bit IL2CPP on older mobile).
        private sealed class PendingRequest
        {
            public readonly double TimeoutSeconds;
            public bool   DeadlineSet;
            public double DeadlineSeconds;

            public PendingRequest(double timeoutSeconds)
            {
                TimeoutSeconds = timeoutSeconds;
            }
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

        // ── JSON helper ──────────────────────────────────────────────────────

        /// <summary>
        /// Serialises <paramref name="s"/> as a JSON string, using the
        /// canonical <see cref="PropertyJson.AppendJsonString"/> helper that
        /// escapes backslash, double-quote, AND all control characters
        /// (&#x3c; 0x20) as \uXXXX sequences.  The previous hand-rolled
        /// implementation escaped only backslash and double-quote, so a
        /// developer-supplied string containing a tab or newline produced
        /// malformed JSON that was silently rejected by the server (SDKR-03).
        /// </summary>
        private static string JsonString(string s)
        {
            var sb = new System.Text.StringBuilder();
            PropertyJson.AppendJsonString(sb, s ?? string.Empty);
            return sb.ToString();
        }

    }
}
