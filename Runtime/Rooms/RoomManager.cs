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

        /// <summary>
        /// Fired after the server accepts a <c>RoomPropertyUpdate</c> and
        /// broadcasts the new state to all clients in the room.  The argument
        /// is the new <see cref="RoomInfo"/> snapshot;
        /// <see cref="RoomInfo.Properties"/> reflects the authoritative
        /// post-update map.  Subscribers that need a delta should diff against
        /// <see cref="CurrentRoom"/> captured BEFORE the event fires — the
        /// RoomManager swaps <see cref="CurrentRoom"/> to the new snapshot
        /// BEFORE invoking this event.
        /// </summary>
        public event Action<RoomInfo> OnRoomPropertiesChanged;

        /// <summary>
        /// Fired after the server accepts a <c>PlayerPropertyUpdate</c> and
        /// broadcasts the new state to all clients.  The first argument is the
        /// player UUID; the second is the updated <see cref="PlayerInfo"/> snapshot.
        /// </summary>
        public event Action<string, PlayerInfo> OnPlayerPropertiesChanged;

        /// <summary>
        /// Fired when the room's master client changes, either automatically
        /// (FIFO promotion after the previous master disconnected) or manually
        /// (a <see cref="TransferMasterClient"/> request was accepted).  The
        /// arguments are <c>(previousMasterId, newMasterId)</c>; either may be
        /// an empty string when unknown (e.g. initial assignment).
        /// </summary>
        public event Action<string, string> OnMasterClientChanged;

        /// <summary>
        /// Fired when the host removes a player from the room via
        /// <see cref="KickPlayer"/>.  The arguments are
        /// <c>(kickerId, targetPlayerId)</c>.  Every client in the room
        /// receives this event — the kicked client observes their own ID as
        /// the target and should treat it as an authoritative disconnect.
        /// </summary>
        public event Action<string, string> OnPlayerKicked;

        /// <summary>
        /// Fired when every player in the room has reported scene-loaded
        /// readiness for the authoritative scene (as stored in the reserved
        /// <see cref="ReservedPropertyKeys.Scene"/> property).  The argument
        /// is the scene name that just finished loading for everyone.
        /// </summary>
        public event Action<string> OnAllPlayersSceneLoaded;

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

        // ── Custom Properties ──────────────────────────────────────────────────

        /// <summary>
        /// Request the server to update one or more room-level custom properties.
        /// Only the room host may issue this call; the server rejects all others
        /// with no broadcast.
        ///
        /// The update is asynchronous — <see cref="OnRoomPropertiesChanged"/>
        /// fires after the server accepts and broadcasts the change.  On
        /// rejection (non-host, version conflict, oversized) no event fires;
        /// the client must rely on its local <see cref="RoomInfo.PropertiesVersion"/>
        /// remaining unchanged to detect failure.
        /// </summary>
        /// <param name="properties">One or more properties to set.  A
        /// property key limit of <see cref="PropertyLimits.MaxPropertiesPerRoom"/>
        /// is enforced client-side before the packet leaves the SDK.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the local player is not currently in a room.
        /// </exception>
        public void SetRoomProperties(IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (!RequireInRoom("SetRoomProperties")) return;
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            int expectedVersion = _currentRoom.PropertiesVersion + 1;
            byte[] payload = PropertyPacketBuilder.BuildRoomPayload(expectedVersion, properties);
            byte[] packet  = _packetBuilder.Build(PacketType.RoomPropertyUpdate, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Convenience overload for updating a single room-level property.
        /// </summary>
        public void SetRoomProperty(string key, PropertyValue value)
        {
            if (string.IsNullOrEmpty(key))
                throw new ArgumentException("key must not be null or empty.", nameof(key));
            SetRoomProperties(new Dictionary<string, PropertyValue> { { key, value } });
        }

        /// <summary>
        /// Update one or more properties for a specific player.  The local
        /// session must own <paramref name="playerId"/> — the server enforces
        /// the self-only invariant and rejects mismatches.
        /// </summary>
        /// <param name="playerId">The player UUID whose properties to update.
        /// Must equal <see cref="NetworkManager.LocalPlayerStringId"/>; the
        /// server rejects mismatches.</param>
        /// <param name="properties">The properties to set.</param>
        public void SetPlayerProperties(
            string playerId,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (!RequireInRoom("SetPlayerProperties")) return;
            if (string.IsNullOrEmpty(playerId))
                throw new ArgumentException("playerId must not be null or empty.", nameof(playerId));
            if (properties == null) throw new ArgumentNullException(nameof(properties));

            // Look up the player's local view of PropertiesVersion so the
            // request carries the correct expected-version tag.  An unknown
            // player id starts from version 0 + 1 = 1.
            int currentVersion = 0;
            foreach (var p in _currentRoom.Players)
            {
                if (p.PlayerId == playerId)
                {
                    currentVersion = p.PropertiesVersion;
                    break;
                }
            }

            int expectedVersion = currentVersion + 1;
            byte[] payload = PropertyPacketBuilder.BuildPlayerPayload(playerId, expectedVersion, properties);
            byte[] packet  = _packetBuilder.Build(PacketType.PlayerPropertyUpdate, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        // ── Master Client ──────────────────────────────────────────────────────

        /// <summary>
        /// Request the server to hand the master-client role to
        /// <paramref name="targetPlayerId"/>.  Only the current master may
        /// issue this call; non-host senders are silently rejected.
        ///
        /// The transition fires <see cref="OnMasterClientChanged"/> on every
        /// client in the room (including the sender) once the server accepts
        /// and broadcasts.  No local state change happens synchronously — the
        /// caller must react to the event, not the return value.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="targetPlayerId"/> is null or empty.
        /// </exception>
        public void TransferMasterClient(string targetPlayerId)
        {
            if (!RequireInRoom("TransferMasterClient")) return;
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));

            byte[] payload = MasterClientPacketBuilder.BuildTransferPayload(targetPlayerId);
            byte[] packet  = _packetBuilder.Build(PacketType.MasterClientTransfer, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        /// <summary>
        /// Request the server to remove <paramref name="targetPlayerId"/> from
        /// the room.  Only the current master may issue this call; non-host
        /// senders are silently rejected.
        ///
        /// Every remaining client (including the kicker) receives
        /// <see cref="OnPlayerKicked"/> once the server accepts.  The kicked
        /// client additionally receives an authoritative
        /// <see cref="OnPlayerLeft"/> — SDK consumers should treat either
        /// event as the signal to drop the player from their local roster.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="targetPlayerId"/> is null or empty.
        /// </exception>
        public void KickPlayer(string targetPlayerId)
        {
            if (!RequireInRoom("KickPlayer")) return;
            if (string.IsNullOrEmpty(targetPlayerId))
                throw new ArgumentException("targetPlayerId must not be null or empty.", nameof(targetPlayerId));

            byte[] payload = MasterClientPacketBuilder.BuildKickPayload(targetPlayerId);
            byte[] packet  = _packetBuilder.Build(PacketType.KickPlayer, PacketFlags.Reliable, payload);
            _sendOwned(packet);
        }

        // ── Scene readiness ────────────────────────────────────────────────────

        /// <summary>
        /// Notify the server that this client has finished loading
        /// <paramref name="sceneName"/>.  Idempotent — the server treats
        /// duplicate reports from the same player as a no-op.
        ///
        /// Typically invoked by <c>NetworkSceneManager</c> automatically; most
        /// application code should not need to call this directly.
        /// </summary>
        public void ReportSceneLoaded(string sceneName)
        {
            if (!RequireInRoom("ReportSceneLoaded")) return;
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));

            byte[] payload = MasterClientPacketBuilder.BuildSceneLoadedPayload(sceneName);
            byte[] packet  = _packetBuilder.Build(PacketType.SceneLoaded, PacketFlags.Reliable, payload);
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
                case PacketType.RoomCreate:          HandleCreateResponse(payload);          break;
                case PacketType.RoomJoin:            HandleJoinPacket(payload);              break;
                case PacketType.RoomLeave:           HandleLeavePacket(payload);             break;
                case PacketType.RoomList:            HandleListResponse(payload);            break;
                case PacketType.MasterClientChanged: HandleMasterClientChanged(payload);     break;
                case PacketType.KickPlayer:          HandlePlayerKicked(payload);            break;
                case PacketType.SceneLoaded:         HandleAllPlayersSceneLoaded(payload);   break;
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

        // ── Inbound property broadcasts ────────────────────────────────────────
        //
        // These internal entry points are called by NetworkManager's broadcast
        // receiver (or, in tests, directly) when a `room_properties_updated`
        // or `player_properties_updated` event arrives on the wire.  The
        // broadcast receiver is responsible for decoding the NATS
        // RoomEvent → JSON payload → PropertyJson.Decode* — this class
        // consumes the already-parsed typed payload and swaps the current
        // snapshot atomically before firing the public event.

        /// <summary>
        /// Apply a <c>room_properties_updated</c> broadcast to the local
        /// <see cref="CurrentRoom"/> snapshot.  No-op when not in a room or
        /// when the broadcast version is ≤ the local version (stale).
        /// Public so it can be exercised by unit tests; wire routing happens
        /// via the broadcast receiver in NetworkManager.
        /// </summary>
        public void ApplyRoomPropertiesBroadcast(
            int version,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (_currentRoom == null) return;
            if (version <= _currentRoom.PropertiesVersion) return; // monotonic guard

            _currentRoom = _currentRoom.WithProperties(properties, version);
            OnRoomPropertiesChanged?.Invoke(_currentRoom);
        }

        /// <summary>
        /// Apply a <c>player_properties_updated</c> broadcast to the matching
        /// player in the local roster.  No-op when not in a room, the player
        /// is unknown, or the broadcast is stale.
        /// </summary>
        public void ApplyPlayerPropertiesBroadcast(
            string playerId,
            int version,
            IReadOnlyDictionary<string, PropertyValue> properties)
        {
            if (_currentRoom == null || string.IsNullOrEmpty(playerId)) return;

            // Swap the matching player's snapshot in-place on a copied roster.
            var roster = _currentRoom.Players;
            if (roster == null || roster.Length == 0) return;

            PlayerInfo updated = null;
            var newRoster = new PlayerInfo[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == playerId)
                {
                    if (version <= roster[i].PropertiesVersion) return; // stale
                    updated    = roster[i].WithProperties(properties, version);
                    newRoster[i] = updated;
                }
                else
                {
                    newRoster[i] = roster[i];
                }
            }
            if (updated == null) return; // playerId not on roster

            _currentRoom = _currentRoom.WithPlayers(newRoster);
            OnPlayerPropertiesChanged?.Invoke(playerId, updated);
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

        // ── Phase 2 inbound handlers ──────────────────────────────────────────

        /// <summary>
        /// Apply a <c>master_client_changed</c> server broadcast (packet
        /// <c>0x2C</c>) to the local room snapshot and fire
        /// <see cref="OnMasterClientChanged"/>.
        /// Public so unit tests can exercise the path without a live socket;
        /// production code should rely on the routing in
        /// <see cref="HandleRoomPacket"/>.
        /// </summary>
        public void HandleMasterClientChanged(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseChanged(
                    payload, out string previousMasterId, out string newMasterId))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed MasterClientChanged payload.");
                return;
            }

            if (_currentRoom != null && !string.IsNullOrEmpty(newMasterId))
            {
                _currentRoom = RehostRoom(_currentRoom, newMasterId);
            }
            OnMasterClientChanged?.Invoke(previousMasterId ?? string.Empty, newMasterId ?? string.Empty);
        }

        /// <summary>
        /// Apply a <c>player_kicked</c> broadcast (packet <c>0x2E</c>
        /// delivered inbound) to the local roster and fire
        /// <see cref="OnPlayerKicked"/> and <see cref="OnPlayerLeft"/>.
        /// </summary>
        public void HandlePlayerKicked(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseKick(
                    payload, out string kickerId, out string targetPlayerId))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed KickPlayer payload.");
                return;
            }
            if (string.IsNullOrEmpty(targetPlayerId)) return;

            if (_currentRoom != null)
            {
                _currentRoom = RemovePlayerFromRoom(_currentRoom, targetPlayerId);
            }
            OnPlayerKicked?.Invoke(kickerId ?? string.Empty, targetPlayerId);
            OnPlayerLeft?.Invoke(targetPlayerId);
        }

        /// <summary>
        /// Apply an <c>all_players_scene_loaded</c> broadcast (packet
        /// <c>0x2F</c> delivered inbound) by firing
        /// <see cref="OnAllPlayersSceneLoaded"/>.  No local state change —
        /// the room's authoritative scene already lives in
        /// <see cref="RoomInfo.CurrentScene"/>.
        /// </summary>
        public void HandleAllPlayersSceneLoaded(byte[] payload)
        {
            if (!MasterClientPacketParser.ParseSceneLoaded(payload, out string sceneName))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed SceneLoaded broadcast.");
                return;
            }
            OnAllPlayersSceneLoaded?.Invoke(sceneName ?? string.Empty);
        }

        /// <summary>
        /// Return a new <see cref="RoomInfo"/> identical to
        /// <paramref name="room"/> but with <paramref name="newMasterId"/>
        /// marked as host on the roster.  When the target is not on the
        /// roster the room is returned unchanged — a late-arriving broadcast
        /// for a player the SDK has already pruned should not create a fresh
        /// phantom entry.
        /// </summary>
        private static RoomInfo RehostRoom(RoomInfo room, string newMasterId)
        {
            var roster = room.Players;
            if (roster == null || roster.Length == 0) return room;
            bool targetOnRoster = false;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == newMasterId)
                {
                    targetOnRoster = true;
                    break;
                }
            }
            if (!targetOnRoster) return room;

            var newRoster = new PlayerInfo[roster.Length];
            for (int i = 0; i < roster.Length; i++)
            {
                var p = roster[i];
                if (p == null) continue;
                newRoster[i] = (p.PlayerId == newMasterId) == p.IsHost
                    ? p
                    : p.WithIsHost(p.PlayerId == newMasterId);
            }
            return room.WithPlayers(newRoster);
        }

        /// <summary>
        /// Return a new <see cref="RoomInfo"/> with <paramref name="targetId"/>
        /// removed from the roster.  Unchanged when the target is not found.
        /// </summary>
        private static RoomInfo RemovePlayerFromRoom(RoomInfo room, string targetId)
        {
            var roster = room.Players;
            if (roster == null || roster.Length == 0) return room;
            int matchIndex = -1;
            for (int i = 0; i < roster.Length; i++)
            {
                if (roster[i] != null && roster[i].PlayerId == targetId)
                {
                    matchIndex = i;
                    break;
                }
            }
            if (matchIndex < 0) return room;

            var newRoster = new PlayerInfo[roster.Length - 1];
            int j = 0;
            for (int i = 0; i < roster.Length; i++)
            {
                if (i == matchIndex) continue;
                newRoster[j++] = roster[i];
            }
            return room.WithPlayers(newRoster);
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
