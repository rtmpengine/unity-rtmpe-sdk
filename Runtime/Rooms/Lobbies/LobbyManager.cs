// RTMPE SDK — Runtime/Rooms/Lobbies/LobbyManager.cs
//
// Manages lobby browser state: joining/leaving the lobby namespace,
// requesting room lists, and applying server-push updates.
//
// Threading: all public methods must be called from the Unity main thread.
// Callbacks (OnRoomListUpdated) are also invoked on the Unity main thread
// because LobbyManager is only called from NetworkManager's main-thread dispatch path.

using System;
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Protocol;

#if UNITY_2017_1_OR_NEWER
using UnityEngine;
#endif

namespace RTMPE.Rooms
{
    /// <summary>
    /// Controls lobby browsing — joining a lobby namespace, requesting room
    /// lists with filters, and receiving push updates when the server
    /// broadcasts a new room list on <c>rtmpe.lobby.update.{lobby_name}</c>.
    /// </summary>
    public sealed class LobbyManager
    {
        private readonly Action<byte[]>         _sendPacket;
        private readonly PacketBuilder          _builder;

        // ── State ──────────────────────────────────────────────────────────────

        /// <summary>Name of the lobby the client is currently browsing ("" = Default).</summary>
        public string CurrentLobbyName { get; private set; } = string.Empty;

        /// <summary>Whether the client has joined a lobby and is receiving push updates.</summary>
        public bool IsInLobby { get; private set; }

        /// <summary>Last known room list for the current lobby.</summary>
        public IReadOnlyList<LobbyRoomInfo> Rooms => _rooms;
        private readonly List<LobbyRoomInfo> _rooms = new List<LobbyRoomInfo>();

        // Tracks a pending join so IsInLobby is only set true after the server
        // confirms with its first LobbyJoin reply, not optimistically on send
        // and not on a stray LobbyList reply that happens to arrive first.
        private bool   _joinPending;
        private string _pendingLobbyName = string.Empty;

        // Name of the most recently abandoned lobby (set in LeaveLobby and
        // before re-issuing a different JoinLobby).  When a join-reply for
        // this lobby arrives after the client has moved on it must be
        // ignored — without this defence, the JoinA / Leave / JoinB
        // sequence would let A's stale reply re-flip IsInLobby and adopt
        // A's room list as if it belonged to B.
        private string _abandonedLobbyName = string.Empty;
        // Deadline (seconds, Unity Time.realtimeSinceStartup) after which the
        // pending join is considered timed-out and discarded.  Defends against
        // an attacker silently consuming a "pending" slot established hours
        // earlier by feeding a forged LobbyJoin reply at an opportune moment.
        private float  _joinPendingDeadline;
        private const float JoinPendingTimeoutSeconds = 5.0f;

        // ── Events ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired on the Unity main thread whenever the room list is refreshed —
        /// either as the reply to <see cref="JoinLobby"/> / <see cref="ListRooms"/>,
        /// or as a server-push <c>LobbyRoomListUpdate</c> (0x2A).
        /// </summary>
        public event Action<IReadOnlyList<LobbyRoomInfo>> OnRoomListUpdated;

        // ── Constructor ────────────────────────────────────────────────────────

        public LobbyManager(PacketBuilder builder, Action<byte[]> sendPacket)
        {
            _builder    = builder    ?? throw new ArgumentNullException(nameof(builder));
            _sendPacket = sendPacket ?? throw new ArgumentNullException(nameof(sendPacket));
        }

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Sends a <c>LobbyJoin</c> (0x27) request.  The server replies with the
        /// current room list and begins forwarding push updates for the named lobby.
        /// </summary>
        /// <param name="lobbyName">Lobby to join ("" = Default lobby).</param>
        public void JoinLobby(string lobbyName = "")
        {
            var nextName = lobbyName ?? string.Empty;

            // If we are switching lobbies (either an earlier pending join is
            // still in flight, or we are already in a different lobby),
            // record the previous target so a late reply for it does not
            // bind to the new IsInLobby/CurrentLobbyName slot.  The matcher
            // in HandleLobbyReply uses this to drop stale acks.
            if (_joinPending && !string.Equals(_pendingLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = _pendingLobbyName ?? string.Empty;
            }
            else if (IsInLobby && !string.Equals(CurrentLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = CurrentLobbyName ?? string.Empty;
            }

            // A new join targeting the previously-abandoned lobby name retires
            // the abandonment marker so the legitimate reply is not dropped as
            // stale.  Without this clear, Join("A") -> Leave -> Join("A") would
            // see the second join's reply matched against the abandoned-name
            // guard above and dropped, leaving the user blocked for the full
            // join-pending timeout.
            if (string.Equals(_abandonedLobbyName, nextName, StringComparison.Ordinal))
            {
                _abandonedLobbyName = string.Empty;
            }

            _pendingLobbyName    = nextName;
            _joinPending         = true;
            _joinPendingDeadline = NowSeconds() + JoinPendingTimeoutSeconds;

            var payload = LobbyPacketBuilder.BuildLobbyJoinPayload(_pendingLobbyName);
            var packet  = _builder.Build(PacketType.LobbyJoin, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        /// <summary>
        /// Sends a <c>LobbyLeave</c> (0x28) fire-and-forget message.
        /// The server stops forwarding push updates to this session.
        /// </summary>
        public void LeaveLobby()
        {
            // Cancel a pending join even if the server reply hasn't arrived yet.
            var nameToLeave = _joinPending ? _pendingLobbyName : CurrentLobbyName;
            _joinPending      = false;
            _pendingLobbyName = string.Empty;

            // Capture the lobby we are walking away from so a late join-reply
            // for it cannot resurrect IsInLobby after a subsequent JoinLobby
            // for a different lobby has been issued.  The reply matcher in
            // HandleLobbyReply consults this to drop stale acknowledgements.
            _abandonedLobbyName = nameToLeave ?? string.Empty;

            if (!IsInLobby) return;

            var payload = LobbyPacketBuilder.BuildLobbyLeavePayload(nameToLeave);
            var packet  = _builder.Build(PacketType.LobbyLeave, PacketFlags.None, payload);
            _sendPacket(packet);

            IsInLobby        = false;
            CurrentLobbyName = string.Empty;
            _rooms.Clear();
        }

        /// <summary>
        /// Sends a <c>LobbyList</c> (0x29) request with the given options.
        /// Use this for one-shot filtered queries without joining the lobby.
        /// </summary>
        public void ListRooms(LobbyQueryOptions opts = null)
        {
            var payload = LobbyPacketBuilder.BuildLobbyListPayload(opts ?? new LobbyQueryOptions());
            var packet  = _builder.Build(PacketType.LobbyList, PacketFlags.Reliable, payload);
            _sendPacket(packet);
        }

        // ── Internal: inbound packet dispatch ──────────────────────────────────

        /// <summary>
        /// Called by NetworkManager when a LobbyJoin or LobbyList reply arrives.
        /// Only a <see cref="PacketType.LobbyJoin"/> reply with an outstanding
        /// <see cref="JoinLobby"/> in flight (within the timeout window) is
        /// permitted to flip <see cref="IsInLobby"/> to true.  A stray /
        /// out-of-band <see cref="PacketType.LobbyList"/> reply only refreshes
        /// the cached room list.
        /// </summary>
        internal void HandleLobbyReply(PacketType replyType, byte[] payload)
        {
            // Time-out a forgotten pending join before we use it.
            if (_joinPending && NowSeconds() > _joinPendingDeadline)
            {
                _joinPending      = false;
                _pendingLobbyName = string.Empty;
            }

            // Parse the reply once, up front.  We need the room list both to
            // discriminate stale replies (via the per-room lobby_name tag)
            // and to feed ApplyRoomList below.  The lobby-name discriminant
            // is sourced from the first room — the gateway guarantees every
            // entry in a single reply belongs to the same lobby, so the
            // first row is canonical.
            var parsed   = LobbyPacketParser.ParseRoomList(payload);
            string replyLobby = parsed.Count > 0 ? (parsed[0].LobbyName ?? string.Empty) : null;

            // Drop a join-reply that is a ghost from a lobby the client has
            // already left or switched away from.  Without this guard the
            // sequence Join("A") → Leave → Join("B") followed by an
            // out-of-order arrival of A's reply before B's would re-flip
            // IsInLobby and overwrite CurrentLobbyName with the new pending
            // target — leaving the manager claiming to be in B while
            // surfacing A's room list.
            //
            // We can only enforce the discriminant when the reply contains
            // at least one room (replyLobby != null).  An empty room list
            // carries no lobby tag; we fall through to the existing
            // pending-join match in that case, which is safe because the
            // empty list has no rooms to mis-attribute.
            if (replyType == PacketType.LobbyJoin
                && replyLobby != null
                && !string.IsNullOrEmpty(_abandonedLobbyName)
                && string.Equals(replyLobby, _abandonedLobbyName, StringComparison.Ordinal))
            {
                // Stale ack for an abandoned lobby — drop without applying
                // the room list and without flipping any state.  Leave
                // _abandonedLobbyName set so further duplicates are also
                // dropped; it is cleared once the in-flight join's reply
                // arrives below or on a subsequent JoinLobby/LeaveLobby.
                return;
            }

            // Only consume the pending-join slot on a LobbyJoin reply.  Stray
            // LobbyList replies (which carry the same payload shape) cannot
            // promote the manager to "in lobby" — that closes the lobby-reply
            // confusion.
            if (replyType == PacketType.LobbyJoin && _joinPending)
            {
                // If the reply carries a lobby tag, it MUST match the
                // in-flight join name.  A mismatch means the reply belongs
                // to an earlier abandoned join — drop it.
                if (replyLobby != null
                    && !string.Equals(replyLobby, _pendingLobbyName ?? string.Empty, StringComparison.Ordinal))
                {
                    return;
                }

                CurrentLobbyName    = _pendingLobbyName;
                IsInLobby           = true;
                _joinPending        = false;
                _pendingLobbyName   = string.Empty;
                _abandonedLobbyName = string.Empty;
            }

            // Gate the cached room list against the lobby we are actually in
            // (or pending on).  A late reply for a lobby we have already left
            // would otherwise clear the live room list and surface the stale
            // peer set via OnRoomListUpdated, even though the join branch above
            // correctly refuses to flip IsInLobby.
            if (replyLobby != null
                && !string.Equals(replyLobby, CurrentLobbyName ?? string.Empty, StringComparison.Ordinal)
                && !string.Equals(replyLobby, _pendingLobbyName ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            // Apply the parsed room list directly so we do not parse twice.
            _rooms.Clear();
            _rooms.AddRange(parsed);
            OnRoomListUpdated?.Invoke(_rooms);
        }

        // Test-friendly overload preserved for callers that pre-date the
        // type-discriminating signature; defaults to LobbyJoin so legacy unit
        // tests retain their existing behaviour.
        internal void HandleLobbyReply(byte[] payload)
            => HandleLobbyReply(PacketType.LobbyJoin, payload);

        // Wall-clock helper; use Unity time when present so editor tests that
        // do not start a NetworkManager still tick the timeout deterministically.
        private static float NowSeconds()
        {
#if UNITY_2017_1_OR_NEWER
            return Time.realtimeSinceStartup;
#else
            return (float)((System.DateTime.UtcNow - new System.DateTime(1970,1,1)).TotalSeconds);
#endif
        }

        /// <summary>
        /// Called by NetworkManager when a LobbyRoomListUpdate (0x2A) push arrives.
        /// Replaces the cached room list and raises <see cref="OnRoomListUpdated"/>.
        /// </summary>
        internal void HandleLobbyRoomListUpdate(byte[] payload)
        {
            ApplyRoomList(payload);
        }

        // ── Private helpers ──────────────────────────────────────────────────────

        private void ApplyRoomList(byte[] payload)
        {
            var parsed = LobbyPacketParser.ParseRoomList(payload);
            _rooms.Clear();
            _rooms.AddRange(parsed);
            OnRoomListUpdated?.Invoke(_rooms);
        }
    }
}
