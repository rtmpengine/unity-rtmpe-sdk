// RTMPE SDK — Runtime/Rooms/RoomManager.cs
//
// High-level Room API for creating, joining, leaving, and listing rooms.
// Created by NetworkManager and receives room packet callbacks from it.
//
// Threading model:
//   • All public methods MUST be called from the Unity main thread.
//   • HandleRoomPacket() is called by NetworkManager.ProcessPacket() which
//     runs on the main thread (via MainThreadDispatcher).
//
// Lifecycle:
//   1. NetworkManager creates RoomManager in InitialiseNetwork().
//   2. ProcessPacket() routes room-type packets to HandleRoomPacket().
//   3. On cleanup, NetworkManager nulls its reference — no explicit Dispose needed.

using System;
using System.Collections.Generic;
using RTMPE.Core;
using RTMPE.Protocol;
using UnityEngine;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Manages room lifecycle from the client perspective.
    /// Access via <see cref="NetworkManager.Rooms"/>.
    /// </summary>
    public sealed class RoomManager
    {
        private readonly PacketBuilder _packetBuilder;
        private readonly Action<byte[]> _sendOwned;
        private readonly Func<NetworkState> _getState;
        // Called with the local player's room UUID after CreateRoom/JoinRoom succeeds.
        // Wired by NetworkManager so NetworkBehaviour.IsOwner comparisons have a valid ID.
        private readonly Action<string> _onLocalPlayerIdResolved;

        // Current room state (null when not in a room).
        private RoomInfo _currentRoom;

        // Replace the single _pendingCreateOptions field with a request-
        // correlated FIFO queue. The old field was overwritten on rapid successive
        // CreateRoom calls, causing early responses to be populated with the last
        // call's options.
        //
        // A Queue is correct here because:
        //   (a) RoomManager runs on the Unity main thread (no concurrency).
        //   (b) The server responds to room-creation requests in FIFO order.
        //   (c) No sequence-number echoing is required from the server.
        // Entries are dequeued as responses arrive; any remainders are purged in
        // ClearState() on disconnect.
        //
        // Capped at MaxPendingCreates to prevent unbounded memory growth if the
        // server stops responding to CreateRoom requests.
        private readonly Queue<CreateRoomOptions> _pendingCreateQueue =
            new Queue<CreateRoomOptions>();
        private const int MaxPendingCreates = 16;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current room the player is in. Null if not in a room.</summary>
        public RoomInfo CurrentRoom => _currentRoom;

        /// <summary>True when the local player is in a room.</summary>
        public bool IsInRoom => _currentRoom != null;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Fired when a CreateRoom request succeeds.</summary>
        public event Action<RoomInfo> OnRoomCreated;

        /// <summary>Fired when a JoinRoom/JoinRoomByCode request succeeds.</summary>
        public event Action<RoomInfo> OnRoomJoined;

        /// <summary>Fired when the local player successfully leaves a room.</summary>
        public event Action OnRoomLeft;

        /// <summary>Fired when another player joins the current room.</summary>
        public event Action<PlayerInfo> OnPlayerJoined;

        /// <summary>Fired when another player leaves the current room.</summary>
        public event Action<string> OnPlayerLeft;

        /// <summary>Fired when a RoomList response is received.</summary>
        public event Action<RoomInfo[]> OnRoomListReceived;

        /// <summary>Fired when a room request fails (create, join, leave).</summary>
        public event Action<string> OnRoomError;

        // ── Constructor ────────────────────────────────────────────────────────

        /// <summary>
        /// Create a RoomManager. Called internally by <see cref="NetworkManager"/>.
        /// </summary>
        /// <param name="packetBuilder">Shared packet builder (for sequence numbering).</param>
        /// <param name="sendOwned">Delegate to send a fully built packet without copying.</param>
        /// <param name="getState">Delegate to read the current <see cref="NetworkState"/>.</param>
        internal RoomManager(
            PacketBuilder packetBuilder,
            Action<byte[]> sendOwned,
            Func<NetworkState> getState,
            Action<string> onLocalPlayerIdResolved = null)
        {
            _packetBuilder             = packetBuilder ?? throw new ArgumentNullException(nameof(packetBuilder));
            _sendOwned                 = sendOwned     ?? throw new ArgumentNullException(nameof(sendOwned));
            _getState                  = getState      ?? throw new ArgumentNullException(nameof(getState));
            _onLocalPlayerIdResolved   = onLocalPlayerIdResolved;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Request the server to create a new room.
        /// The result arrives asynchronously via <see cref="OnRoomCreated"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void CreateRoom(CreateRoomOptions options = null)
        {
            if (!RequireConnected("CreateRoom")) return;

            if (_pendingCreateQueue.Count >= MaxPendingCreates)
            {
                Debug.LogError("[RTMPE] RoomManager.CreateRoom: too many in-flight room creates. " +
                               "Wait for the server to respond before calling CreateRoom again.");
                return;
            }

            var payload = RoomPacketBuilder.BuildCreateRoomPayload(options);
            var packet  = _packetBuilder.Build(PacketType.RoomCreate, PacketFlags.Reliable, payload);
            // Enqueue options in FIFO order so each response can dequeue
            // the matching options regardless of how many requests are in-flight.
            _pendingCreateQueue.Enqueue(options ?? new CreateRoomOptions());
            _sendOwned(packet);
        }

        /// <summary>
        /// Request to join an existing room by its server-assigned UUID.
        /// The result arrives asynchronously via <see cref="OnRoomJoined"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void JoinRoom(string roomId, JoinRoomOptions options = null)
        {
            if (!RequireConnected("JoinRoom")) return;
            if (string.IsNullOrEmpty(roomId))
            {
                Debug.LogError("[RTMPE] RoomManager.JoinRoom: roomId must not be null or empty.");
                return;
            }

            var payload = RoomPacketBuilder.BuildJoinRoomPayload(roomId, null, options);
            var packet  = _packetBuilder.Build(PacketType.RoomJoin, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Request to join an existing room by its 6-character join code.
        /// The result arrives asynchronously via <see cref="OnRoomJoined"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)
        {
            if (!RequireConnected("JoinRoomByCode")) return;
            if (string.IsNullOrEmpty(roomCode))
            {
                Debug.LogError("[RTMPE] RoomManager.JoinRoomByCode: roomCode must not be null or empty.");
                return;
            }

            var payload = RoomPacketBuilder.BuildJoinRoomPayload(null, roomCode, options);
            var packet  = _packetBuilder.Build(PacketType.RoomJoin, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Request to leave the current room.
        /// The result arrives asynchronously via <see cref="OnRoomLeft"/>
        /// or <see cref="OnRoomError"/>.
        /// </summary>
        public void LeaveRoom()
        {
            if (!RequireInRoom("LeaveRoom")) return;

            var payload = RoomPacketBuilder.BuildLeaveRoomPayload();
            var packet  = _packetBuilder.Build(PacketType.RoomLeave, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Request the list of available rooms from the server.
        /// The result arrives asynchronously via <see cref="OnRoomListReceived"/>.
        /// </summary>
        /// <param name="publicOnly">When true, exclude private rooms.</param>
        public void ListRooms(bool publicOnly = true)
        {
            if (!RequireConnected("ListRooms")) return;

            var payload = RoomPacketBuilder.BuildListRoomsPayload(publicOnly);
            var packet  = _packetBuilder.Build(PacketType.RoomList, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        // ── Packet handling (called by NetworkManager.ProcessPacket) ───────────

        /// <summary>
        /// Route an inbound room packet to the appropriate handler.
        /// Called by NetworkManager on the main thread.
        /// </summary>
        internal void HandleRoomPacket(PacketType type, byte[] payload)
        {
            switch (type)
            {
                case PacketType.RoomCreate: HandleCreateResponse(payload);  break;
                case PacketType.RoomJoin:   HandleJoinPacket(payload);      break;
                case PacketType.RoomLeave:  HandleLeavePacket(payload);     break;
                case PacketType.RoomList:   HandleListResponse(payload);    break;
            }
        }

        /// <summary>
        /// Clear room state when the connection drops.
        /// Called by NetworkManager during cleanup.
        /// </summary>
        internal void ClearState()
        {
            _currentRoom = null;
            _pendingCreateQueue.Clear();
        }

        // ── Response handlers ──────────────────────────────────────────────────

        private void HandleCreateResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseCreateRoomResponse(
                    payload, out bool ok, out string roomId,
                    out string roomCode, out int maxPlayers,
                    out string localPlayerId, out string error))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed RoomCreate response.");
                return;
            }

            if (ok)
            {
                // Dequeue the matching options in FIFO order.
                // RoomManager is single-threaded and the server responds in request order.
                var opts = _pendingCreateQueue.Count > 0
                    ? _pendingCreateQueue.Dequeue()
                    : new CreateRoomOptions();
                _currentRoom = new RoomInfo(
                    roomId, roomCode, opts.Name ?? string.Empty, "waiting",
                    1, maxPlayers, opts.IsPublic);

                // Populate LocalPlayerStringId so IsOwner checks work.
                if (!string.IsNullOrEmpty(localPlayerId))
                    _onLocalPlayerIdResolved?.Invoke(localPlayerId);

                OnRoomCreated?.Invoke(_currentRoom);
            }
            else
            {
                Debug.LogWarning($"[RTMPE] RoomManager: CreateRoom failed — {error}");
                OnRoomError?.Invoke(error ?? "Unknown error");
            }
        }

        private void HandleJoinPacket(byte[] payload)
        {
            if (!RoomPacketParser.TryGetJoinMsgKind(payload, out byte msgKind))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed RoomJoin payload (no msg_kind).");
                return;
            }

            if (msgKind == RoomMsgKind.Response)
                HandleJoinResponse(payload);
            else
                HandlePlayerJoinedNotification(payload);
        }

        private void HandleJoinResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseJoinRoomResponse(
                    payload, out bool ok, out RoomInfo room,
                    out string localPlayerId, out string error))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed JoinRoom response.");
                return;
            }

            if (ok)
            {
                _currentRoom = room;

                // Populate LocalPlayerStringId so IsOwner checks work.
                if (!string.IsNullOrEmpty(localPlayerId))
                    _onLocalPlayerIdResolved?.Invoke(localPlayerId);

                OnRoomJoined?.Invoke(room);
            }
            else
            {
                Debug.LogWarning($"[RTMPE] RoomManager: JoinRoom failed — {error}");
                OnRoomError?.Invoke(error ?? "Unknown error");
            }
        }

        private void HandlePlayerJoinedNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerJoinedNotification(payload, out PlayerInfo player))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerJoined notification.");
                return;
            }

            OnPlayerJoined?.Invoke(player);
        }

        private void HandleLeavePacket(byte[] payload)
        {
            if (!RoomPacketParser.TryGetLeaveMsgKind(payload, out byte msgKind))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed RoomLeave payload (no msg_kind).");
                return;
            }

            if (msgKind == RoomMsgKind.Response)
                HandleLeaveResponse(payload);
            else
                HandlePlayerLeftNotification(payload);
        }

        private void HandleLeaveResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseLeaveRoomResponse(payload, out bool ok))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed LeaveRoom response.");
                return;
            }

            if (ok)
            {
                _currentRoom = null;
                OnRoomLeft?.Invoke();
            }
            else
            {
                OnRoomError?.Invoke("LeaveRoom failed");
            }
        }

        private void HandlePlayerLeftNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerLeftNotification(payload, out string playerId))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerLeft notification.");
                return;
            }

            OnPlayerLeft?.Invoke(playerId);
        }

        private void HandleListResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseRoomListResponse(payload, out RoomInfo[] rooms))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed RoomList response.");
                return;
            }

            OnRoomListReceived?.Invoke(rooms);
        }

        // ── Guards ─────────────────────────────────────────────────────────────

        private bool RequireConnected(string method)
        {
            var state = _getState();
            if (state == NetworkState.Connected || state == NetworkState.InRoom)
                return true;

            Debug.LogWarning($"[RTMPE] RoomManager.{method}: requires Connected or InRoom state (current: {state}).");
            return false;
        }

        private bool RequireInRoom(string method)
        {
            var state = _getState();
            if (state == NetworkState.InRoom && _currentRoom != null)
                return true;

            Debug.LogWarning($"[RTMPE] RoomManager.{method}: requires InRoom state (current: {state}).");
            return false;
        }
    }
}
