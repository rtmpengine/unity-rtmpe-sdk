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

        // Local player's room UUID, populated by HandleCreateResponse /
        // HandleJoinResponse when the server echoes the assignment.  Empty
        // string when no membership has been confirmed yet.  Used by the
        // reserved-property guard in SetRoomProperties to determine whether
        // the local session holds the master-client role.
        private string _localPlayerId = string.Empty;

        // Pending CreateRoom requests carry a client-generated UUID (request_id)
        // alongside the original options and a wall-clock send timestamp.
        //
        // Correlation strategy:
        //   • Outbound: each CreateRoom call mints a v4-class GUID and stores
        //     the (id, options, sentAt) tuple in _pendingCreateById and the id
        //     in _pendingCreateOrder.
        //   • Inbound (TryMatchCreateResponse): when the server echoes a
        //     request_id, we look it up by id directly — order-independent,
        //     race-proof.  When the server omits it (current gateway), we fall
        //     back to head-of-_pendingCreateOrder.  Either way the entry is
        //     removed atomically.
        //   • Stale entries (no response within PendingCreateTtlSeconds) are
        //     swept on every CreateRoom and on ClearState — bounded growth.
        //
        // Wire-protocol coordination point:
        //   The current RoomCreate (0x20) request payload defined in
        //   RoomPacketBuilder does NOT carry the request_id, and the response
        //   parser in RoomPacketParser does not extract it.  Promoting this
        //   correlator from "client-side defence-in-depth + FIFO fallback" to
        //   "true ID-based matching" requires:
        //     (a) Prefixing BuildCreateRoomPayload with [request_id_len:2 LE]
        //         [request_id:16 bytes] (raw GUID bytes; or 36-byte UTF-8 form).
        //     (b) Gateway echoes the same bytes verbatim in the response after
        //         the existing fields.
        //     (c) ParseCreateRoomResponse extends to surface the echoed id.
        //   Step (a)/(c) live in RoomPacketBuilder/Parser; step (b) is
        //   gateway-side.  Until those land, FIFO is the operative match path
        //   and the per-id bookkeeping here is a future-proof scaffold and a
        //   useful debugging aid (logged via OnRoomError when stale entries
        //   are evicted).
        private readonly PendingCreateTable<CreateRoomOptions> _pendingCreates =
            new PendingCreateTable<CreateRoomOptions>(
                capacity: MaxPendingCreates,
                ttlSeconds: PendingCreateTtlSeconds);

        private const int    MaxPendingCreates       = 16;
        // 30 s is a comfortable upper bound on a healthy round trip; anything
        // older is treated as a request the gateway will never answer.
        private const double PendingCreateTtlSeconds = 30.0;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current room the player is in. Null if not in a room.</summary>
        public RoomInfo CurrentRoom => _currentRoom;

        /// <summary>True when the local player is in a room.</summary>
        public bool IsInRoom => _currentRoom != null;

        /// <summary>
        /// True when the local player currently holds the master-client (host)
        /// role in <see cref="CurrentRoom"/>.  Returns <see langword="false"/>
        /// when not in a room, when the master id is unknown, or when the
        /// local player UUID has not yet been resolved by a successful
        /// Create/Join response.
        /// </summary>
        public bool IsMasterClient
        {
            get
            {
                if (_currentRoom == null) return false;
                if (string.IsNullOrEmpty(_localPlayerId)) return false;
                var master = _currentRoom.MasterId;
                return !string.IsNullOrEmpty(master) && master == _localPlayerId;
            }
        }

        /// <summary>
        /// Number of CreateRoom requests still awaiting a server response.
        /// Exposed to allow tests and diagnostic tooling to assert on
        /// in-flight book-keeping; production code should treat this as
        /// observational only.
        /// </summary>
        internal int PendingCreateCount => _pendingCreates.Count;

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

            // Sweep first so a back-pressured client whose responses are missing
            // does not get permanently locked out by stale book-keeping.
            // Take a single monotonic-clock sample so Sweep and Register share
            // a consistent view of "now" within this request-handling pass.
            long nowTicks = PendingCreateTable<CreateRoomOptions>.NowTicks();
            foreach (var (staleId, _) in _pendingCreates.SweepExpired(nowTicks))
            {
                Debug.LogWarning(
                    $"[RTMPE] RoomManager: CreateRoom request {staleId} timed out " +
                    $"after {PendingCreateTtlSeconds}s with no response. Discarding.");
                SafeRaise(OnRoomError, "CreateRoom timed out");
            }

            if (_pendingCreates.IsFull)
            {
                Debug.LogError("[RTMPE] RoomManager.CreateRoom: too many in-flight room creates. " +
                               "Wait for the server to respond before calling CreateRoom again.");
                return;
            }

            var opts      = options ?? new CreateRoomOptions();
            var requestId = _pendingCreates.Register(opts, nowTicks);

            var payload = RoomPacketBuilder.BuildCreateRoomPayload(opts);
            var packet  = _packetBuilder.Build(PacketType.RoomCreate, PacketFlags.Reliable, payload);
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

            // Keys with the reserved "__" prefix are server-managed (e.g.
            // __scene, __scene_additive); the gateway silently discards
            // unauthorised writes to them and never echoes a property update.
            // Surfacing the rejection client-side turns an opaque "no-op"
            // into an actionable diagnostic — the developer sees exactly
            // which key was rejected and why, instead of debugging a missing
            // OnRoomPropertiesChanged.  Non-master writers are blocked
            // outright; the request is not sent.
            if (!IsMasterClient)
            {
                foreach (var key in properties.Keys)
                {
                    if (ReservedPropertyKeys.IsReserved(key))
                    {
                        var error =
                            $"SetRoomProperties: key '{key}' is reserved (prefix '{ReservedPropertyKeys.Prefix}') " +
                            "and may only be written by the room's master client. Request not sent.";
                        Debug.LogWarning("[RTMPE] RoomManager." + error);
                        SafeRaise(OnRoomError, error);
                        return;
                    }
                }
            }

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
            _currentRoom   = null;
            _localPlayerId = string.Empty;
            _pendingCreates.Clear();
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
            SafeRaise(OnRoomPropertiesChanged, _currentRoom);
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
            SafeRaise(OnPlayerPropertiesChanged, playerId, updated);
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
                // Match by echoed request_id when present; FIFO fallback when not
                // (current gateway omits the field — see wire-protocol coordination
                // note above).  When the response cannot be matched at all we
                // synthesize default options rather than dropping the response —
                // the room metadata in the response is authoritative; only the
                // client-supplied IsPublic/Name view is reconstructed.
                CreateRoomOptions opts =
                    _pendingCreates.TryMatch(echoedRequestId: null, out var matched)
                        ? matched
                        : new CreateRoomOptions();

                _currentRoom = new RoomInfo(
                    roomId, roomCode, opts.Name ?? string.Empty, "waiting",
                    1, maxPlayers, opts.IsPublic);

                // Populate LocalPlayerStringId so IsOwner checks work.
                if (!string.IsNullOrEmpty(localPlayerId))
                {
                    _localPlayerId = localPlayerId;
                    _onLocalPlayerIdResolved?.Invoke(localPlayerId);
                }

                SafeRaise(OnRoomCreated, _currentRoom);
            }
            else
            {
                Debug.LogWarning($"[RTMPE] RoomManager: CreateRoom failed — {error}");
                SafeRaise(OnRoomError, error ?? "Unknown error");
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
                // An implicit room-switch (Join while still in another room
                // without an intervening LeaveRoom call) must mirror the
                // gateway's "leave A then join B" semantics on the client:
                // tear down the spawn registry and fire OnRoomLeft for the
                // displaced room before swapping in the new snapshot.
                // Otherwise OnRoomJoined fires twice in a row, the previous
                // room's spawned objects leak, and IsOwner comparisons
                // straddle two rooms with diverging player rosters.
                //
                // OnRoomLeft is a synchronous System.Action — the
                // NetworkManager listener (OnRoomManagerLeft) calls
                // SpawnManager.ClearAll() on this stack frame, so the
                // displaced room's GameObjects are destroyed before the
                // _currentRoom assignment below makes the new room visible.
                //
                // Idempotent on rejoin: if the response carries the same room
                // id we already track, skip the displaced-room cleanup so a
                // duplicate Join (e.g. a reliable-channel retransmit echo)
                // does not raise spurious OnRoomLeft / spawn-registry resets.
                if (_currentRoom != null
                    && room != null
                    && _currentRoom.RoomId != room.RoomId)
                {
                    // Null the prior room before broadcasting OnRoomLeft so
                    // listeners that consult CurrentRoom see a coherent
                    // "between rooms" snapshot rather than the stale prior
                    // room.  The synchronous NetworkManager listener
                    // (OnRoomManagerLeft) transitions _state back to
                    // Connected and runs SpawnManager.ClearAll on this stack
                    // frame; without this assignment, user OnNetworkDespawn
                    // callbacks would observe (state==Connected, room==A)
                    // — an impossible-in-steady-state combination.
                    _currentRoom = null;
                    SafeRaise(OnRoomLeft);
                }

                _currentRoom = room;

                // Populate LocalPlayerStringId so IsOwner checks work.
                if (!string.IsNullOrEmpty(localPlayerId))
                {
                    _localPlayerId = localPlayerId;
                    _onLocalPlayerIdResolved?.Invoke(localPlayerId);
                }

                SafeRaise(OnRoomJoined, room);
            }
            else
            {
                Debug.LogWarning($"[RTMPE] RoomManager: JoinRoom failed — {error}");
                SafeRaise(OnRoomError, error ?? "Unknown error");
            }
        }

        private void HandlePlayerJoinedNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerJoinedNotification(payload, out PlayerInfo player))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerJoined notification.");
                return;
            }

            SafeRaise(OnPlayerJoined, player);
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
                _currentRoom   = null;
                _localPlayerId = string.Empty;
                SafeRaise(OnRoomLeft);
            }
            else
            {
                SafeRaise(OnRoomError, "LeaveRoom failed");
            }
        }

        private void HandlePlayerLeftNotification(byte[] payload)
        {
            if (!RoomPacketParser.ParsePlayerLeftNotification(payload, out string playerId))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed PlayerLeft notification.");
                return;
            }

            SafeRaise(OnPlayerLeft, playerId);
        }

        private void HandleListResponse(byte[] payload)
        {
            if (!RoomPacketParser.ParseRoomListResponse(payload, out RoomInfo[] rooms))
            {
                Debug.LogWarning("[RTMPE] RoomManager: malformed RoomList response.");
                return;
            }

            SafeRaise(OnRoomListReceived, rooms);
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
            SafeRaise(OnMasterClientChanged, previousMasterId ?? string.Empty, newMasterId ?? string.Empty);
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
            SafeRaise(OnPlayerKicked, kickerId ?? string.Empty, targetPlayerId);
            SafeRaise(OnPlayerLeft, targetPlayerId);
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
            SafeRaise(OnAllPlayersSceneLoaded, sceneName ?? string.Empty);
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

        // ── Subscriber-isolated event invocation ───────────────────────────────
        //
        // Every public OnRoom* / OnPlayer* event is fired during inbound
        // packet processing.  A throwing application subscriber would
        // propagate up through HandleRoomPacket → NetworkManager.ProcessPacket
        // and short-circuit the rest of the inbound buffer for that frame,
        // leaving subsequent same-tick packets to act against half-applied
        // state.  Walk each multicast invocation list explicitly so the
        // failure of one subscriber does not deny delivery to its siblings.
        // Same discipline as M19-SYNC-01 (NetworkVariable.OnValueChanged) and
        // M19-CORE-07 (ReliableChannel callbacks).

        private static void SafeRaise(Action handler)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action)subs[i])(); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void SafeRaise<T>(Action<T> handler, T arg)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T>)subs[i])(arg); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void SafeRaise<T1, T2>(Action<T1, T2> handler, T1 a1, T2 a2)
        {
            if (handler == null) return;
            var subs = handler.GetInvocationList();
            for (int i = 0; i < subs.Length; i++)
            {
                try { ((Action<T1, T2>)subs[i])(a1, a2); }
                catch (Exception ex) { LogSubscriberThrow(ex); }
            }
        }

        private static void LogSubscriberThrow(Exception ex)
        {
            Debug.LogError(
                "[RTMPE] RoomManager: event subscriber threw " +
                $"{ex.GetType().Name}: {ex.Message}.  Continuing with " +
                "remaining subscribers.");
        }
    }
}
