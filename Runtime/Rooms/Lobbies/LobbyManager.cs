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
        // confirms with its first room-list reply, not optimistically on send.
        private bool   _joinPending;
        private string _pendingLobbyName = string.Empty;

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
            _pendingLobbyName = lobbyName ?? string.Empty;
            _joinPending      = true;

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
        /// Parses the room list and raises <see cref="OnRoomListUpdated"/>.
        /// </summary>
        internal void HandleLobbyReply(byte[] payload)
        {
            // Confirm a pending join: the server's first reply is the proof of acceptance.
            if (_joinPending)
            {
                CurrentLobbyName  = _pendingLobbyName;
                IsInLobby         = true;
                _joinPending      = false;
                _pendingLobbyName = string.Empty;
            }
            ApplyRoomList(payload);
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
