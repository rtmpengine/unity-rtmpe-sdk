// RTMPE SDK — Runtime/Core/NetworkManager.cs
//
// Central entry point for all RTMPE networking. Singleton MonoBehaviour that wires
// together NetworkSettings, NetworkThread, and MainThreadDispatcher.
//
// Threading model:
//  • NetworkManager lives on the Unity main thread.
//  • NetworkThread runs on a dedicated background thread.
//  • Packets received on the background thread are delivered via MainThreadDispatcher
//    so that all state mutations and Unity API calls occur on the main thread.
//
// Singleton contract:
//  • [DefaultExecutionOrder(-1000)] — Awake runs before all other components.
//  • Instance getter auto-creates a persistent GameObject when no instance exists.
//  • _applicationIsQuitting flag guards against Unity's destroy-order issues.
//
// Protocol note:
//  • All header field constants use PacketProtocol.* from NetworkConstants.cs.
//    Do NOT introduce magic numbers here — sync failures with the Rust gateway are
//    silent and extremely difficult to debug.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    /// <summary>
    /// Main entry point for RTMPE networking.
    /// Add to a persistent GameObject or let the singleton auto-create one.
    /// All public methods must be called from the Unity main thread.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    public sealed class NetworkManager : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static NetworkManager  _instance;
        private static volatile bool   _applicationIsQuitting;
        private static readonly object _instLock = new object();

        // Reset static state on each Play-Mode entry (or standalone restart) so that
        // a second Play in the same Editor session gets a clean singleton.
        // SubsystemRegistration fires before Awake and before any scene is loaded.
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (_instLock)
            {
                _instance              = null;
                _applicationIsQuitting = false;
                // Do NOT clear _transportFactory here — tests and WebGL bootstraps
                // install it once at module init, before any singleton is created.
                // Clearing would break that install-then-play sequence.  Users
                // who need to reset it can call ClearTransportFactory() explicitly.
            }
        }

        /// <summary>
        /// Singleton instance. Auto-creates a persistent GameObject if none exists.
        /// Returns <see langword="null"/> after <c>OnApplicationQuit</c>.
        /// <b>MUST be called from the Unity main thread.</b>
        /// </summary>
        public static NetworkManager Instance
        {
            get
            {
                if (_applicationIsQuitting) return null;

                lock (_instLock)
                {
                    if (_applicationIsQuitting) return null;
                    if (_instance != null)       return _instance;

                    // FindFirstObjectByType — Unity 6 replacement for deprecated FindObjectOfType.
                    _instance = FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Exclude);

                    if (_instance == null)
                    {
                        var go = new GameObject("[RTMPE] NetworkManager");
                        _instance = go.AddComponent<NetworkManager>();
                        DontDestroyOnLoad(go);
                    }

                    return _instance;
                }
            }
        }

        /// <summary>
        /// Returns <see langword="true"/> if a valid instance exists and the application
        /// has not begun quitting. Thread-safe; no side effects.
        /// </summary>
        public static bool HasInstance
        {
            get
            {
                lock (_instLock) { return _instance != null && !_applicationIsQuitting; }
            }
        }

        // ── Transport factory (pluggable) ──────────────────────────────────────
        //
        // The SDK ships with a UDP-only transport that uses System.Net.Sockets
        // directly.  That is correct on every standalone platform (Windows,
        // macOS, Linux, Android, iOS) because the Rust gateway speaks UDP+KCP.
        //
        // WebGL is different: Unity WebGL runs inside the browser's JavaScript
        // sandbox, which has NO access to raw UDP sockets.  The only outbound
        // network path in WebGL is WebSocket / WebRTC.  Shipping a WebGL build
        // therefore requires:
        //   1. A WebSocket or WebRTC gateway (new server component), OR
        //   2. A WebSocket-to-UDP bridge deployed in front of the existing UDP gateway.
        //
        // Because neither is part of the default SDK image, we expose a static
        // factory delegate: games that need WebGL can install a WebSocket
        // transport at startup and the rest of NetworkManager is transport-
        // agnostic.  Tests use the same hook to inject mock transports.
        //
        // Invariants:
        //   • Assigning replaces any previous factory; assign before Connect().
        //   • A null factory (the default) selects the built-in UdpTransport.
        //   • The factory is called exactly once per InitialiseNetwork().
        //   • The factory MUST return a non-null, ready-to-Connect() transport.

        /// <summary>
        /// Delegate signature for custom transport factories.
        /// Receives the active <see cref="NetworkSettings"/> so the factory
        /// can read host/port/buffer fields.  The returned transport is owned
        /// by the resulting <see cref="NetworkThread"/> and disposed when the
        /// manager is cleaned up.
        /// </summary>
        public delegate RTMPE.Transport.NetworkTransport TransportFactoryFn(NetworkSettings settings);

        private static TransportFactoryFn _transportFactory;

        /// <summary>
        /// Install a custom transport factory (e.g. WebSocket for WebGL builds,
        /// mock transport for integration tests).  Pass <see langword="null"/>
        /// to restore the built-in UDP transport.
        /// </summary>
        /// <remarks>
        /// Set this BEFORE calling <see cref="Connect"/> or
        /// <see cref="Reconnect"/>.  Changing the factory after the manager
        /// has initialised does NOT re-create the live transport — call
        /// <see cref="Disconnect"/> first.
        /// </remarks>
        public static void SetTransportFactory(TransportFactoryFn factory) => _transportFactory = factory;

        /// <summary>
        /// Remove any installed transport factory, restoring the built-in
        /// <see cref="RTMPE.Transport.UdpTransport"/>.
        /// </summary>
        public static void ClearTransportFactory() => _transportFactory = null;

        /// <summary>True when a custom transport factory is installed.</summary>
        public static bool HasCustomTransportFactory => _transportFactory != null;

        // ── Inspector fields ───────────────────────────────────────────────────

        [SerializeField]
        [Tooltip("RTMPE connection settings asset. Leave blank to use built-in defaults.")]
        private NetworkSettings _settings;

        // ── Runtime state (main-thread only) ──────────────────────────────────
        private NetworkState      _state = NetworkState.Disconnected;
        private NetworkThread     _networkThread;
        private NetworkTransport  _transport;
        private MainThreadDispatcher _dispatcher;
        private Coroutine         _timeoutCoroutine;
        private Coroutine         _connectCoroutine;

        // Session tokens populated on SessionAck
        private uint   _cryptoId;
        private string _jwtToken;
        private string _reconnectToken;
        /// <summary>N-8: 32-byte HMAC key for IP-migration proofs, derived alongside session keys.</summary>
        private byte[] _ipMigrationKey;
        private ulong  _localPlayerId;
        private ulong  _currentRoomId;

        // Last-room snapshot (kept across a token-preserving ClearSessionData so
        // Reconnect() can rejoin automatically).  Both fields are cleared when
        // the reconnect token is cleared — they have no meaning without it.
        //
        // LastRoomId is the RoomInfo.RoomId (UUID string) returned by the room
        // service at join time; it survives session teardown precisely so the
        // SDK can feed it back into RoomManager.JoinRoom on a successful
        // ReconnectInit → SessionAck.  LastRoomCode is preserved as a fallback:
        // if the server has evicted the UUID but still knows the human-readable
        // code, apps can call JoinRoomByCode(LastRoomCode) from OnConnected.
        private string _lastRoomId;
        private string _lastRoomCode;

        // Room-level player identity (UUID string assigned by room service on JoinRoom).
        // Distinct from the gateway session ID stored in _localPlayerId (u64).
        // Populated by RoomManager via SetLocalRoomPlayerId() when JoinRoom succeeds,
        // or by tests via SetLocalPlayerStringId().
        // Used by NetworkBehaviour.IsOwner for object ownership checks.
        private string _localPlayerStringId;

        // Monotonic counter for RPC request correlation IDs.
        // Incremented atomically by SendRpc(); max-value wrap is safe (uint loops).
        private int _rpcRequestCounter;

        // Monotone client-side tick counter for CSP (client-side prediction).
        // Incremented once per 30 Hz variable-flush cycle while in a room.
        // Wraps naturally at uint.MaxValue with no ill effect (all comparisons are
        // tick-relative within a small window so overflow is safe by design).
        private uint _localTick;

        // Outbound AEAD nonce counter (separate from the application sequence number
        // assigned by PacketBuilder).  Starts at -1L so the first Interlocked.Increment
        // returns 0, matching the gateway NonceGenerator which also starts at 0.
        // Reset to -1L in ClearSessionData() and in Connect() so every new session
        // begins with counter = 0.
        //
        // Using long avoids the int→uint cast ambiguity: the counter advances from
        // 0 to uint.MaxValue (4,294,967,295) and then hard-stops rather than
        // wrapping silently back to 0 and reusing nonces.
        //
        // These thresholds mirror the Rust gateway's SEQUENCE_EXHAUSTION_THRESHOLD
        // and NEAR_EXHAUSTION_MARGIN (nonce.rs) so the SDK terminates sessions
        // proactively before the gateway's replay-window would reject inbound traffic.
        private const long OutboundNonceExhaustionThreshold    = (long)uint.MaxValue + 1L; // 2^32
        private const long OutboundNonceNearExhaustionMargin   = 1_048_576L;               // ~9.7 h @ 30 Hz
        private long _outboundNonceCounter = -1L;

        // Session crypto state
        private HandshakeHandler _handshakeHandler;
        private SessionKeys      _sessionKeys;
        private PacketBuilder    _packetBuilder;
        private HeartbeatManager _heartbeatManager;

        // Room management
        private RoomManager  _roomManager;

        // Lobby management
        private LobbyManager _lobbyManager;

        // Matchmaking
        private MatchmakingManager _matchmakingManager;

        // Spawn management
        private SpawnManager _spawnManager;

        // Cached heartbeat send callback — avoids per-frame closure allocation
        // at the call site in Update().
        private System.Action<byte[]> _sendPacketCallback;

        // Cached delegate for SendVariableUpdate — method-group-to-delegate
        // conversion allocates on every call unless stored in a field.
        private System.Action<byte[]> _sendVariableUpdateDelegate;

        // 30 Hz accumulator for the NetworkVariable flush loop.
        private float _variableFlushAccum;
        private const float VariableFlushInterval = 1f / 30f;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current network state.</summary>
        public NetworkState State => _state;

        /// <summary>True when connected to the gateway (Connected or InRoom).</summary>
        public bool IsConnected => _state == NetworkState.Connected
                                || _state == NetworkState.InRoom;

        /// <summary>True when connected and inside a room.</summary>
        public bool IsInRoom => _state == NetworkState.InRoom;

        /// <summary>Settings asset in use (may be the built-in default).</summary>
        public NetworkSettings Settings => _settings;

        /// <summary>Local player ID (gateway session ID) — valid after SessionAck.</summary>
        public ulong LocalPlayerId => _localPlayerId;

        /// <summary>Current room ID — valid after RoomJoin.</summary>
        public ulong CurrentRoomId => _currentRoomId;

        /// <summary>
        /// Local player's room-level UUID — set by RoomManager when JoinRoom succeeds.
        /// Valid only while in a room (<see cref="NetworkState.InRoom"/>).
        /// Used by <see cref="NetworkBehaviour.IsOwner"/> for ownership checks.
        /// </summary>
        public string LocalPlayerStringId => _localPlayerStringId;

        /// <summary>
        /// Monotone client-side tick counter, incremented at 30 Hz while in a room.
        /// Used by <see cref="RTMPE.Sync.NetworkTransform"/> to stamp
        /// <see cref="InputPayload"/> entries for client-side prediction.
        /// Wraps naturally at <c>uint.MaxValue</c> — tick-relative arithmetic
        /// within a small window is safe across the wrap boundary.
        /// </summary>
        public uint LocalTick => _localTick;

        /// <summary>Room management API — create, join, leave, and list rooms.</summary>
        public RoomManager Rooms => _roomManager;

        /// <summary>Lobby browser API — join a lobby, list rooms with filters, receive push updates.</summary>
        public LobbyManager Lobby => _lobbyManager;

        /// <summary>Matchmaking API — automatically find or create a room by game mode.</summary>
        public MatchmakingManager Matchmaking => _matchmakingManager;

        /// <summary>
        /// <see langword="true"/> when the local player is the current master
        /// client (room host) for <see cref="RoomManager.CurrentRoom"/>.  The
        /// value is derived from the cached room snapshot, so it updates
        /// automatically when the server publishes a
        /// <c>master_client_changed</c> or <c>host_changed</c> event.
        ///
        /// Returns <see langword="false"/> when not in a room, when the room
        /// snapshot has no master set, or when the local player ID is not yet
        /// known (e.g. during connection setup).
        /// </summary>
        public bool IsMasterClient
        {
            get
            {
                if (_roomManager == null) return false;
                var room = _roomManager.CurrentRoom;
                if (room == null) return false;
                var master = room.MasterId;
                var localId = _localPlayerStringId;
                return !string.IsNullOrEmpty(master)
                    && !string.IsNullOrEmpty(localId)
                    && master == localId;
            }
        }

        /// <summary>
        /// Local-player façade exposing
        /// <see cref="LocalPlayerContext.SetProperty"/> so developers can use
        /// the Photon-compatible <c>NetworkManager.LocalPlayer.SetProperty(...)</c>
        /// shape without repeating the local player's UUID at every call site.
        /// Never returns null after <c>Awake</c> — the inner context forwards
        /// to <see cref="RoomManager"/> and no-ops (with a log) if the session
        /// has not yet been authenticated.
        ///
        /// Thread safety: the lazy-init uses
        /// <see cref="System.Threading.Interlocked.CompareExchange{T}(ref T,T,T)"/>
        /// so concurrent access from the main thread plus a background thread
        /// (e.g. a transport callback that inadvertently touches it) only ever
        /// retains a single instance.  Production callers MUST still access
        /// from the Unity main thread; this safeguard prevents accidental
        /// double-allocation during SDK bootstraps.
        /// </summary>
        public LocalPlayerContext LocalPlayer
        {
            get
            {
                var existing = _localPlayer;
                if (existing != null) return existing;
                var fresh = new LocalPlayerContext(_roomManager, () => _localPlayerStringId);
                return System.Threading.Interlocked.CompareExchange(ref _localPlayer, fresh, null) ?? fresh;
            }
        }
        private LocalPlayerContext _localPlayer;

        /// <summary>Spawn management API — spawn, despawn, prefab registry, owner-leave handling.</summary>
        public SpawnManager Spawner => _spawnManager;

        /// <summary>
        /// Networked-scene management façade.  Drives room-wide scene
        /// transitions through the reserved <c>__scene</c> custom property
        /// and surfaces <c>SceneLoaded</c> (0x2F) readiness aggregation.
        /// Thread-safe lazy-init via <see cref="System.Threading.Interlocked"/>
        /// follows the same pattern as <see cref="LocalPlayer"/>.
        /// </summary>
        public NetworkSceneManager Scene
        {
            get
            {
                var existing = _sceneManager;
                if (existing != null) return existing;
                if (_roomManager == null) return null;
                var fresh = new NetworkSceneManager(_roomManager);
                return System.Threading.Interlocked.CompareExchange(ref _sceneManager, fresh, null) ?? fresh;
            }
        }
        private NetworkSceneManager _sceneManager;

        /// <summary>JWT bearer token — valid after SessionAck. Use for Room Service calls.</summary>
        public string JwtToken => _jwtToken;

        /// <summary>
        /// **N-1** — current reconnect token, non-null whenever a previous
        /// session's <c>SessionAck</c> supplied one and it has not yet been
        /// consumed.  Expose to apps that want to branch on <see cref="CanReconnect"/>.
        /// </summary>
        public string ReconnectToken => _reconnectToken;

        /// <summary>
        /// **N-1** — <see langword="true"/> when the SDK is holding a valid
        /// reconnect token AND the transport has been started at least once.
        /// Apps can use this to skip asking the user for credentials again on
        /// a transient disconnect.
        /// </summary>
        public bool CanReconnect => !string.IsNullOrEmpty(_reconnectToken);

        /// <summary>Last measured round-trip time in milliseconds (-1 if not yet available).</summary>
        public float LastRttMs { get; private set; } = -1f;

        /// <summary>
        /// The <see cref="RoomInfo.RoomId"/> of the most recently active room,
        /// preserved across a token-preserving disconnect so
        /// <see cref="Reconnect"/> can auto-rejoin it.  <see langword="null"/>
        /// when no room has been joined in the current reconnect window or
        /// after an explicit <see cref="Disconnect"/>.
        /// </summary>
        public string LastRoomId => _lastRoomId;

        /// <summary>
        /// The <see cref="RoomInfo.RoomCode"/> (human-readable join code) of
        /// the most recently active room.  Falls back to this if the UUID has
        /// been evicted server-side.  Same lifetime as <see cref="LastRoomId"/>.
        /// </summary>
        public string LastRoomCode => _lastRoomCode;

        // ── Events ─────────────────────────────────────────────────────────────

        /// <summary>Fired when the connection state changes (previous state, new state).</summary>
        public event Action<NetworkState, NetworkState> OnStateChanged;

        /// <summary>Fired when the connection reaches <see cref="NetworkState.Connected"/>.</summary>
        public event Action OnConnected;

        /// <summary>Fired when the connection drops for any reason.</summary>
        public event Action<DisconnectReason> OnDisconnected;

        /// <summary>Fired when the connection attempt fails (timeout or transport error).</summary>
        public event Action<string> OnConnectionFailed;

        /// <summary>Fired when the local player successfully joins a room.</summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomJoined or Rooms.OnRoomCreated instead.")]
        public event Action<ulong> OnJoinedRoom;

        /// <summary>Fired when the local player leaves a room.</summary>
        [Obsolete("Use NetworkManager.Rooms.OnRoomLeft instead.")]
        public event Action<ulong> OnLeftRoom;

        /// <summary>
        /// Fired when a <see cref="PacketType.Data"/> or <see cref="PacketType.StateSync"/>
        /// packet is received. Argument is the full raw packet (header + payload).
        /// </summary>
        public event Action<byte[]> OnDataReceived;

        /// <summary>Fired on each successful heartbeat with the measured RTT in ms.</summary>
        public event Action<float> OnRttUpdated;

        /// <summary>
        /// Fired when the server acknowledges a reliable Data packet.
        /// Reserved for future reliable-delivery retransmit suppression.
        /// </summary>
        public event Action OnDataAcknowledged;

        /// <summary>
        /// Fired when, after a successful <see cref="Reconnect"/>, the SDK
        /// begins an automatic rejoin of <see cref="LastRoomId"/>.  Apps can
        /// subscribe to update UI (e.g. "Reconnecting to room…").  The follow-up
        /// outcome is observable through the existing
        /// <see cref="RoomManager.OnRoomJoined"/> / <see cref="RoomManager.OnRoomError"/>.
        /// Not fired when <see cref="NetworkSettings.autoRejoinLastRoomOnReconnect"/>
        /// is disabled or when no last-room snapshot is available.
        /// </summary>
        public event Action<string> OnAutoRejoinAttempt;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            lock (_instLock)
            {
                if (_instance != null && _instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
                _instance = this;
            }

            DontDestroyOnLoad(gameObject);

            if (_settings == null)
                _settings = NetworkSettings.CreateDefault();

            // Subscribe to scene-load events BEFORE InitialiseNetwork so that
            // a scene load that races with the first frame doesn't leak stale
            // NetworkObject registry entries.  sceneUnloaded fires AFTER Unity
            // has destroyed all GameObjects in the unloaded scene — the
            // registry may then hold a bunch of managed references that
            // compare equal to null.  PruneDestroyed() sweeps them out so the
            // registry count matches the number of live objects.
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            SceneManager.sceneLoaded   += HandleSceneLoaded;

            InitialiseNetwork();
        }

        private void Start()
        {
            // Warm the MainThreadDispatcher singleton HERE (main thread, before any
            // background threads are started) so the first Enqueue() call is free
            // of the one-time GameObject allocation cost.
            _dispatcher = MainThreadDispatcher.Instance;

            // Cache the heartbeat send callback once so Update() does
            // not allocate a new closure object every frame.
            // Route through EncryptAndSend so heartbeat packets are AEAD-encrypted
            // once the session is established (i.e. _sessionKeys != null).
            _sendPacketCallback = packet => EncryptAndSend(packet);

            // Cache the SendVariableUpdate delegate once to eliminate
            // per-tick allocation on the 30 Hz flush path.
            _sendVariableUpdateDelegate = SendVariableUpdate;
        }

        private void Update()
        {
            // Drive the heartbeat tick each frame using the pre-cached callback.
            _heartbeatManager?.Tick(_sendPacketCallback);

            // Flush dirty NetworkVariables at 30 Hz for all owned objects.
            _variableFlushAccum += Time.deltaTime;
            if (_variableFlushAccum >= VariableFlushInterval)
            {
                _variableFlushAccum -= VariableFlushInterval;
                FlushDirtyNetworkVariables();
                // Advance the CSP tick in lock-step with the variable flush
                // so that InputPayload.Tick and NetworkVariable deltas share
                // the same 30 Hz cadence.  Only advance while in a room —
                // ticks outside a room are meaningless for CSP.
                if (IsInRoom) _localTick++;
            }
        }

        /// <summary>
        /// Flush dirty NetworkVariables for all objects owned by the local player.
        /// Called at 30 Hz from Update().
        /// </summary>
        private void FlushDirtyNetworkVariables()
        {
            if (_spawnManager == null || !IsInRoom) return;
            foreach (var nb in _spawnManager.Registry.GetAll())
            {
                if (nb == null || !nb.IsOwner || !nb.IsSpawned) continue;
                // Use the pre-cached delegate to avoid per-call allocation.
                nb.FlushDirtyVariables(_sendVariableUpdateDelegate);
            }
        }

        private void OnDestroy()
        {
            lock (_instLock)
            {
                if (_instance == this) _instance = null;
            }

            // Unsubscribe BEFORE Cleanup so that a scene-unload fired as part
            // of shutdown doesn't re-enter our handler after fields are nulled.
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            SceneManager.sceneLoaded   -= HandleSceneLoaded;

            StopAllCoroutines();
            Cleanup();
        }

        // ── Scene lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Unity raises this AFTER a scene is unloaded — the GameObjects in
        /// that scene have already been destroyed.  Sweep the NetworkObject
        /// registry to evict any entry whose GameObject compares equal to
        /// null, preventing a slow leak when apps use additive scene loading.
        /// </summary>
        /// <remarks>
        /// We do NOT attempt to send despawn packets for the pruned objects —
        /// the authoritative side (gateway + room service) still tracks them.
        /// Apps that want server-side cleanup should call <c>Despawn(objectId)</c>
        /// explicitly before unloading the scene, or use <c>DestroyWithOwner</c>
        /// in combination with a room leave.
        /// </remarks>
        private void HandleSceneUnloaded(Scene scene)
        {
            if (_spawnManager?.Registry == null) return;

            int pruned;
            try { pruned = _spawnManager.Registry.PruneDestroyed(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkObjectRegistry.PruneDestroyed threw " +
                    $"{ex.GetType().Name}: {ex.Message}. Skipping prune this cycle.");
                return;
            }

            if (pruned > 0)
                LogDebug(
                    $"Scene \"{scene.name}\" unloaded — pruned {pruned} stale NetworkObject entr" +
                    (pruned == 1 ? "y" : "ies") + " from registry.");
        }

        /// <summary>
        /// Unity raises this when a new scene finishes loading.  Single-scene
        /// loads (<see cref="LoadSceneMode.Single"/>) destroy all scene
        /// objects that were not <c>DontDestroyOnLoad</c>, so we prune here
        /// too — <see cref="HandleSceneUnloaded"/> alone does not cover the
        /// case where the PREVIOUS scene was unloaded by the Single mode
        /// before the new load completed.
        /// </summary>
        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;
            if (_spawnManager?.Registry == null) return;

            try { _spawnManager.Registry.PruneDestroyed(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkObjectRegistry.PruneDestroyed threw " +
                    $"{ex.GetType().Name}: {ex.Message}. Skipping prune this cycle.");
            }
        }

        private void OnApplicationQuit()
        {
            _applicationIsQuitting = true;
            Cleanup();
        }

        // ── Initialisation & teardown ──────────────────────────────────────────

        private void InitialiseNetwork()
        {
            // Pluggable transport: a factory installed via SetTransportFactory
            // overrides the built-in UDP transport.  This is the extension
            // point that WebGL builds (WebSocket transport) and integration
            // tests (mock transport) use.  When no factory is installed we
            // fall back to UdpTransport, preserving the historical behaviour.
            if (_transportFactory != null)
            {
                try { _transport = _transportFactory(_settings); }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[RTMPE] Custom transport factory threw {ex.GetType().Name}: {ex.Message}. " +
                        "Falling back to the built-in UdpTransport for this session.");
                    _transport = null;
                }

                if (_transport == null)
                {
                    Debug.LogWarning(
                        "[RTMPE] Custom transport factory returned null; " +
                        "falling back to the built-in UdpTransport.");
                }
            }

            if (_transport == null)
            {
                _transport = new UdpTransport(
                    _settings.serverHost,
                    _settings.serverPort,
                    _settings.sendBufferBytes,
                    _settings.receiveBufferBytes);
            }

            _networkThread = new NetworkThread(_transport, _settings.networkThreadBufferBytes);
            _networkThread.OnPacketReceived += HandlePacketReceived;
            _networkThread.OnError          += HandleTransportError;

            _packetBuilder = new PacketBuilder();

            // Room & Spawn managers share a single wiring path so InitialiseNetwork,
            // Connect, and Reconnect all produce the same event topology.
            RecreateRoomAndSpawnManagers();

            // Subscribe the state-sync packet handler so incoming StateDelta
            // broadcasts are routed to NetworkTransformInterpolators.
            OnDataReceived += HandleStateSyncPacket;
        }

        private void Cleanup()
        {
            _heartbeatManager?.Stop();
            _heartbeatManager = null;
            _handshakeHandler?.Dispose();  // Zero key material before GC can observe it
            _handshakeHandler = null;
            _sessionKeys?.Dispose();       // Zero session keys before GC can observe it
            _sessionKeys      = null;
            // Detach scene manager BEFORE tearing down the network thread so
            // any in-flight SceneLoaded callbacks don't fire into a disposed manager.
            _sceneManager?.Dispose();
            _sceneManager = null;
            // Unsubscribe before dispose to break delegate references.
            if (_networkThread != null)
            {
                _networkThread.OnPacketReceived -= HandlePacketReceived;
                _networkThread.OnError          -= HandleTransportError;
                _networkThread.Dispose();
            }
            _networkThread = null;
            _transport     = null;
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Begin connecting to the RTMPE gateway with the given API key.
        /// Transitions to <see cref="NetworkState.Connecting"/> immediately.
        /// </summary>
        /// <param name="apiKey">Project API key issued by the RTMPE dashboard.</param>
        public void Connect(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                Debug.LogError("[RTMPE] NetworkManager.Connect: apiKey must not be null or empty.");
                return;
            }

            if (_state != NetworkState.Disconnected)
            {
                Debug.LogWarning($"[RTMPE] NetworkManager.Connect ignored — already in state {_state}.");
                return;
            }

            TransitionTo(NetworkState.Connecting);

            // Reset the packet builder so sequence numbers start fresh on reconnect.
            _packetBuilder = new PacketBuilder();

            // Reset the outbound AEAD nonce counter so the first encrypted packet
            // of every session starts at counter = 0, matching the gateway's
            // NonceGenerator which also resets to 0 for each new EstablishedSession.
            System.Threading.Interlocked.Exchange(ref _outboundNonceCounter, -1L);

            // Recreate Room/Spawn managers with the fresh PacketBuilder.
            RecreateRoomAndSpawnManagers();

            // Create a fresh handshake handler (generates a new X25519 ephemeral keypair).
            _handshakeHandler = new HandshakeHandler();

            _networkThread.Start();

            // Kick off the async handshake-init coroutine — it waits for the transport
            // to be bound (LocalEndPoint != null) before building and sending the packet.
            _connectCoroutine = StartCoroutine(HandshakeInitCoroutine(apiKey));

            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
        }

        /// <summary>
        /// Recreate the RoomManager and SpawnManager with the current
        /// <see cref="_packetBuilder"/> and fresh registry/ownership objects,
        /// then wire every subscription they need.  Called from
        /// <see cref="Connect"/> and <see cref="Reconnect"/> so the same
        /// event topology is guaranteed on both paths — previously the two
        /// call sites duplicated the wiring which let drifts slip through
        /// (e.g. a new subscription added to one path only).
        /// </summary>
        private void RecreateRoomAndSpawnManagers()
        {
            // RoomManager shares PacketBuilder with the rest of the outbound
            // pipeline so room packets use a single monotonic sequence counter
            // — using an independent counter would be a protocol violation
            // and may trigger gateway replay protection.
            _roomManager = new RoomManager(
                _packetBuilder,
                packet => EncryptAndSend(packet),
                () => _state,
                id => SetLocalRoomPlayerId(id));
            _roomManager.OnRoomJoined   += OnRoomManagerJoined;
            _roomManager.OnRoomLeft     += OnRoomManagerLeft;
            _roomManager.OnRoomCreated  += OnRoomManagerCreated;

            _lobbyManager = new LobbyManager(
                _packetBuilder,
                packet => EncryptAndSend(packet));

            _matchmakingManager = new MatchmakingManager(
                _packetBuilder,
                packet => EncryptAndSend(packet),
                () => _state,
                () => _localPlayerStringId ?? string.Empty);

            var registry  = new NetworkObjectRegistry();
            var ownership = new OwnershipManager(registry, this);
            _spawnManager = new SpawnManager(registry, ownership, this);

            _roomManager.OnPlayerLeft   += playerId => _spawnManager?.OnPlayerLeftRoom(playerId);
            _roomManager.OnPlayerJoined += _ => _spawnManager?.MarkAllVariablesDirtyForResync();
        }

        /// <summary>
        /// **N-1** — shortcut reconnect using a previously-issued reconnect
        /// token.  Skips the PSK + PostgreSQL API-key validation path entirely;
        /// the gateway consumes the token atomically, re-derives an AEAD key
        /// from a fresh ECDH handshake, and issues a new JWT.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Call this after <see cref="CanReconnect"/> returns <see langword="true"/>
        /// and the SDK reports a transient disconnect (heartbeat timeout, transport
        /// error).  Usually this is driven by app code that observed
        /// <see cref="NetworkState.Disconnected"/>.
        /// </para>
        /// <para>
        /// The SDK uses <see cref="ReconnectBackoff"/> (Full-Jitter capped
        /// exponential — industry-standard) for the inter-attempt delays.
        /// On exhaustion the state transitions back to
        /// <see cref="NetworkState.Disconnected"/>; the app MUST then fall back
        /// to a full <see cref="Connect(string)"/> with credentials.
        /// </para>
        /// </remarks>
        /// <returns>
        /// <see langword="true"/> if a reconnect attempt was scheduled;
        /// <see langword="false"/> when no reconnect token is held or the
        /// manager is already connected / reconnecting.
        /// </returns>
        public bool Reconnect()
        {
            if (!CanReconnect)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Reconnect: no reconnect token — " +
                                 "client must call Connect(apiKey) to re-authenticate.");
                return false;
            }

            if (_state != NetworkState.Disconnected)
            {
                Debug.LogWarning($"[RTMPE] NetworkManager.Reconnect ignored — state is {_state}, " +
                                 "must be Disconnected.");
                return false;
            }

            TransitionTo(NetworkState.Reconnecting);

            // Reset per-connection protocol state — same pattern as Connect().
            _packetBuilder = new PacketBuilder();
            System.Threading.Interlocked.Exchange(ref _outboundNonceCounter, -1L);

            // Recreate Room/Spawn managers with identical event wiring to Connect().
            RecreateRoomAndSpawnManagers();

            _handshakeHandler = new HandshakeHandler();

            _networkThread.Start();

            // Kick off the reconnect coroutine — waits for transport bind, sends
            // ReconnectInit, then the existing Challenge/HandshakeResponse/SessionAck
            // handlers complete the flow exactly as for a fresh Connect().
            _connectCoroutine = StartCoroutine(ReconnectInitCoroutine());
            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
            return true;
        }

        /// <summary>
        /// Gracefully disconnect from the gateway and reset all session state.
        /// No-op when already disconnected or a disconnect is already in progress.
        /// </summary>
        public void Disconnect()
        {
            if (_state == NetworkState.Disconnected ||
                _state == NetworkState.Disconnecting) return;

            bool wasConnected = IsConnected;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            if (_connectCoroutine != null)
            {
                StopCoroutine(_connectCoroutine);
                _connectCoroutine = null;
            }

            TransitionTo(NetworkState.Disconnecting);

            if (wasConnected)
                SendDisconnect();

            _heartbeatManager?.Stop();
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ClientRequest);
        }

        /// <summary>
        /// Enqueue a raw packet for transmission. Thread-safe.
        /// The packet is AEAD-encrypted when the session is established.
        /// A defensive copy is made internally so the caller can safely reuse its buffer.
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Send: cannot send while not connected.");
                return;
            }

            if (data == null || data.Length == 0) return;

            // Copy so the caller can safely reuse or discard its buffer after this call,
            // which matches the original NetworkThread.Send(copy) contract.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            EncryptAndSend(copy);
        }

        // ── Handshake coroutine ────────────────────────────────────────────────

        /// <summary>
        /// Wait for the UDP transport to bind (background thread sets LocalEndPoint),
        /// then build and transmit the encrypted HandshakeInit packet.
        ///
        /// Polls LocalEndPoint each frame — the background thread calls
        /// _transport.Connect() which binds the socket and sets the endpoint
        /// within the first loop iteration (~1 ms after Start()).
        /// </summary>
        private IEnumerator HandshakeInitCoroutine(string apiKey)
        {
            const float maxWaitSecs = 2f;
            float waited = 0f;

            while (_transport.LocalEndPoint == null && waited < maxWaitSecs)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            if (_transport.LocalEndPoint == null)
            {
                LogDebug("Transport did not bind within 2 s — timeout coroutine will handle failure.");
                yield break;
            }

            SendHandshakeInit(apiKey);
        }

        /// <summary>
        /// **N-1** — reconnect variant of <see cref="HandshakeInitCoroutine"/>.
        /// Waits for the UDP transport to bind, then sends a single
        /// <c>ReconnectInit</c> carrying the stored reconnect token.
        /// </summary>
        /// <remarks>
        /// The server's response is a normal <see cref="PacketType.Challenge"/>,
        /// handled by the same pipeline as the full handshake.  No extra
        /// client-side state machine is required — the Reconnecting state just
        /// marks the intent for observers.
        /// </remarks>
        private IEnumerator ReconnectInitCoroutine()
        {
            const float maxWaitSecs = 2f;
            float waited = 0f;

            while (_transport.LocalEndPoint == null && waited < maxWaitSecs)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
            }

            _connectCoroutine = null;

            if (_transport.LocalEndPoint == null)
            {
                LogDebug("ReconnectInit: transport did not bind within 2 s — timeout coroutine will handle failure.");
                yield break;
            }

            SendReconnectInit(_reconnectToken);
        }

        // ── Receive path (raised on network thread → marshalled to main thread) ─

        private void HandlePacketReceived(byte[] data)
        {
            _dispatcher?.Enqueue(() => ProcessPacket(data));
        }

        private void ProcessPacket(byte[] data)
        {
            if (data == null || data.Length < PacketProtocol.HEADER_SIZE)
            {
                Debug.LogWarning("[RTMPE] Dropped packet: too short to contain a valid header.");
                return;
            }

            var magic = (ushort)(
                data[PacketProtocol.OFFSET_MAGIC]
              | (data[PacketProtocol.OFFSET_MAGIC + 1] << 8));

            if (magic != PacketProtocol.MAGIC)
            {
                Debug.LogWarning(
                    $"[RTMPE] Dropped packet: bad magic 0x{magic:X4} " +
                    $"(expected 0x{PacketProtocol.MAGIC:X4}).");
                return;
            }

            if (data[PacketProtocol.OFFSET_VERSION] != PacketProtocol.VERSION)
            {
                Debug.LogWarning(
                    $"[RTMPE] Dropped packet: unsupported protocol version " +
                    $"{data[PacketProtocol.OFFSET_VERSION]} (expected {PacketProtocol.VERSION}).");
                return;
            }

            // ── AEAD decryption (Fix: inbound decrypt pipeline) ──────────────────
            // If FLAG_ENCRYPTED is set the gateway has wrapped the payload in a
            // ChaCha20-Poly1305 AEAD envelope.  Decrypt before dispatching so every
            // handler always receives plaintext — handlers are unaware of encryption.
            //
            // Precondition: _sessionKeys must be non-null (set during Challenge/
            // HandshakeResponse exchange) and _cryptoId must be the gateway-assigned
            // session_id (set during OnSessionAck).  Pre-session packets (Challenge,
            // SessionAck, HandshakeAck) are always plaintext by gateway design, so
            // this branch is only reachable once the session is fully established.
            if ((data[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.Encrypted) != 0)
            {
                data = DecryptInboundPacket(data);
                if (data == null)
                {
                    Debug.LogWarning(
                        "[RTMPE] Dropped packet: AEAD authentication failed " +
                        "(tag mismatch or no session keys).");
                    return;
                }
            }

            var packetType = (PacketType)data[PacketProtocol.OFFSET_TYPE];

            LogDebug($"Received {packetType} ({data.Length} B).");

            switch (packetType)
            {
                // ── ECDH 4-step handshake ────────────────────────────────
                case PacketType.Challenge:    OnChallenge(data);    break;
                case PacketType.SessionAck:   OnSessionAck(data);   break;

                // ── Legacy handshake (backward compatibility) ────────────────
                case PacketType.HandshakeAck: OnHandshakeAck(data); break;

                // ── Keep-alive ───────────────────────────────────────────────
                case PacketType.HeartbeatAck: OnHeartbeatAck(data); break;

                // ── Room lifecycle (0x20–0x23) ─────────────────────
                case PacketType.RoomCreate:
                case PacketType.RoomJoin:
                case PacketType.RoomLeave:
                case PacketType.RoomList:
                    OnRoomPacket(packetType, data);
                    break;

                // ── Custom property broadcasts (0x24–0x25) ─────────
                case PacketType.RoomPropertyUpdate:
                    OnRoomPropertyUpdateBroadcast(data);
                    break;
                case PacketType.PlayerPropertyUpdate:
                    OnPlayerPropertyUpdateBroadcast(data);
                    break;

                // ── Matchmaking (0x26, 0x2B) ──────────────────────
                case PacketType.MatchmakingResponse:
                    _matchmakingManager?.HandleMatchmakingResponse(
                        PacketParser.ExtractPayload(data));
                    break;

                // ── Lobby system (0x27–0x2A) ───────────────────────
                case PacketType.LobbyJoin:
                case PacketType.LobbyList:
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyLeave:
                    // Fire-and-forget — no reply; notify listeners.
                    OnLobbyPacket(packetType, data);
                    break;
                case PacketType.LobbyRoomListUpdate:
                    OnLobbyRoomListUpdate(data);
                    break;

                // ── Room management broadcasts (0x2C, 0x2E, 0x2F) ──
                case PacketType.MasterClientChanged:
                case PacketType.KickPlayer:
                case PacketType.SceneLoaded:
                    OnRoomPacket(packetType, data);
                    break;

                case PacketType.Disconnect:      OnServerDisconnect(data); break;
                case PacketType.Data:
                case PacketType.StateSync:        OnDataReceived?.Invoke(data); break;

                // DataAck is a legitimate server acknowledgement; expose it as an event.
                case PacketType.DataAck:
                    OnDataAcknowledged?.Invoke();
                    LogDebug("DataAck received.");
                    break;

                // ── Networked object lifecycle ─────────────────────
                case PacketType.Spawn:            OnSpawnPacket(data);   break;
                case PacketType.Despawn:          OnDespawnPacket(data); break;

                // ── RPC system ─────────────────────────────────────
                case PacketType.Rpc:              OnRpcRequest(data);    break;
                case PacketType.RpcResponse:      OnRpcResponse(data);   break;
                case PacketType.RpcBufferReplay:  HandleRpcBufferReplay(PacketParser.ExtractPayload(data)); break;

                // Receive inbound variable update packets.
                case PacketType.VariableUpdate:   HandleVariableUpdatePacket(data); break;

                default:
                    LogDebug($"No handler for packet type 0x{(byte)packetType:X2}.");
                    break;
            }
        }

        // ── Handshake packet handlers ───────────────────────────────────────

        /// <summary>
        /// Handle an incoming <c>Challenge</c> (0x06) from the server.
        ///
        /// 1. Parse 128-byte payload: [ephemeral:32][static:32][sig:64].
        /// 2. Verify Ed25519 signature — reject on failure.
        /// 3. Derive session keys via X25519 ECDH + HKDF-SHA256.
        /// 4. Send <c>HandshakeResponse</c> containing the client public key.
        /// </summary>
        private void OnChallenge(byte[] data)
        {
            // Guard: only process Challenge while we are actively connecting
            // or reconnecting.  N-1 adds the Reconnecting state — the server
            // replies to ReconnectInit with the same Challenge packet format,
            // so the same handler runs for both flows.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;
            if (_handshakeHandler == null)         return;

            var payload = PacketParser.ExtractPayload(data);

            byte[] pinnedKey = null;
            try { pinnedKey = _settings?.PinnedServerPublicKeyBytes; }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Invalid pinnedServerPublicKeyHex in settings: {ex.Message}");
                return;
            }

            // Phase 2 (2026-04-22): enforce Ed25519 server pinning when the
            // operator has opted in via NetworkSettings.requirePinnedServerPublicKey.
            //
            // Rationale: ValidateChallenge() below always verifies the signature,
            // but without a pinned key the verification only asserts that the
            // server holds SOME Ed25519 private key — which is trivially true
            // for any rogue gateway.  Requiring a pinned public key binds the
            // session to an operator-chosen identity; a MITM with its own valid
            // keypair is rejected.  We fail BEFORE invoking ValidateChallenge
            // so the intent is unambiguous in logs and tests.
            if (_settings != null && _settings.requirePinnedServerPublicKey && pinnedKey == null)
            {
                Debug.LogError(
                    "[RTMPE] NetworkSettings.requirePinnedServerPublicKey is true but " +
                    "pinnedServerPublicKeyHex is empty. Refusing to complete handshake — " +
                    "any valid Ed25519 server would otherwise be accepted. Set the pinned " +
                    "public key in your NetworkSettings asset, or disable " +
                    "requirePinnedServerPublicKey for unpinned dev builds.");
                return;
            }

            if (!_handshakeHandler.ValidateChallenge(
                    payload,
                    out _,                  // serverEphemeralPub (stored inside handler)
                    out _,                  // serverStaticPub    (stored inside handler)
                    pinnedKey))
            {
                Debug.LogError("[RTMPE] Challenge validation failed — Ed25519 signature invalid or " +
                               "Challenge payload malformed. Possible MITM attack. Disconnecting.");
                // Let the timeout coroutine fire OnConnectionFailed cleanly.
                return;
            }

            // Derive directional session keys (AEAD) + N-8 IP migration key via HKDF-SHA256.
            // Three independent expansions from a single PRK — info suffixes \x00, \x01, \x02.
            _sessionKeys = _handshakeHandler.DeriveSessionKeys(out _ipMigrationKey);
            if (_sessionKeys == null)
            {
                Debug.LogError("[RTMPE] ECDH key derivation failed (degenerate shared secret). Disconnecting.");
                return;
            }

            // Send the client's X25519 ephemeral public key to the server.
            // Use SendOwned — response is a freshly allocated array that
            // will not be reused, so the extra copy inside Send() is unnecessary.
            var response = _packetBuilder.BuildHandshakeResponse(_handshakeHandler.ClientPublicKey);
            _networkThread.SendOwned(response);
            LogDebug("Sent HandshakeResponse — awaiting SessionAck.");
        }

        /// <summary>
        /// Handle <c>SessionAck</c> (0x08): parse crypto_id, JWT, and reconnect token,
        /// then transition to <see cref="NetworkState.Connected"/> and start heartbeat.
        /// </summary>
        private void OnSessionAck(byte[] data)
        {
            // Guard: ignore stale ACKs that arrive after a timeout.
            // N-1: reconnect flow also terminates with SessionAck, so accept
            // both Connecting and Reconnecting states.
            if (_state != NetworkState.Connecting && _state != NetworkState.Reconnecting) return;

            // Remember whether this SessionAck is closing a reconnect flow —
            // we check it BEFORE the state transition below clears the context.
            bool wasReconnecting = _state == NetworkState.Reconnecting;

            var payload = PacketParser.ExtractPayload(data);

            if (!PacketParser.ParseSessionAck(payload,
                    out uint   cryptoId,
                    out string jwtToken,
                    out string reconnectToken))
            {
                Debug.LogError("[RTMPE] SessionAck parse failed — malformed payload. Disconnecting.");
                return;
            }

            _cryptoId       = cryptoId;
            _jwtToken       = jwtToken;
            _reconnectToken = reconnectToken;

            // Extract gateway session ID from the JWT sub claim (u64 as string, e.g. "123456").
            // This is the GATEWAY session identity. The ROOM player UUID is set later
            // by RoomManager via SetLocalRoomPlayerId() when JoinRoom completes.
            if (ulong.TryParse(TryExtractJwtSub(jwtToken), out var sessionId))
                _localPlayerId = sessionId;

            LogDebug($"SessionAck received: crypto_id={cryptoId}, session_id={_localPlayerId}, jwt_len={jwtToken?.Length ?? 0}");

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }

            TransitionTo(NetworkState.Connected);

            // Start keep-alive heartbeat.
            // Pass _packetBuilder so heartbeat packets share the same sequence
            // counter as all other outbound packets (prevents nonce reuse under AEAD).
            _heartbeatManager = new HeartbeatManager(_settings.heartbeatIntervalMs, _packetBuilder);
            _heartbeatManager.OnRttUpdated     += rtt => { LastRttMs = rtt; OnRttUpdated?.Invoke(rtt); };
            _heartbeatManager.OnHeartbeatTimeout += OnHeartbeatTimeout;
            _heartbeatManager.Start();

            // Auto-rejoin the last room after a successful reconnect, if enabled.
            // The session is now fully established, so RoomManager.RequireConnected
            // will pass.  We intentionally do NOT clear _lastRoomId here — the
            // subsequent OnRoomJoined handler will refresh the snapshot with the
            // fresh RoomInfo returned by the server.
            if (wasReconnecting && _settings != null && _settings.autoRejoinLastRoomOnReconnect)
            {
                TryAutoRejoinLastRoom();
            }
        }

        /// <summary>
        /// Attempt to rejoin <see cref="LastRoomId"/> via
        /// <see cref="RoomManager.JoinRoom"/>.  No-op when no snapshot exists.
        /// Silent on RoomManager internal failures — the app can observe the
        /// outcome through the existing <see cref="RoomManager.OnRoomJoined"/> /
        /// <see cref="RoomManager.OnRoomError"/> events.
        /// </summary>
        private void TryAutoRejoinLastRoom()
        {
            if (string.IsNullOrEmpty(_lastRoomId))
            {
                LogDebug("Reconnect: no last room to auto-rejoin.");
                return;
            }

            if (_roomManager == null)
            {
                Debug.LogWarning("[RTMPE] Auto-rejoin: RoomManager is null (internal invariant violation).");
                return;
            }

            LogDebug($"Reconnect: auto-rejoining last room {_lastRoomId}.");
            OnAutoRejoinAttempt?.Invoke(_lastRoomId);
            _roomManager.JoinRoom(_lastRoomId);
        }

        // ── Legacy / other handlers ────────────────────────────────────────────

        private void OnHandshakeAck(byte[] _)
        {
            if (_state != NetworkState.Connecting) return;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            TransitionTo(NetworkState.Connected);
        }

        private void OnHeartbeatAck(byte[] _)
        {
            _heartbeatManager?.OnAckReceived();
        }

        private void OnHeartbeatTimeout()
        {
            Debug.LogWarning("[RTMPE] Heartbeat timeout — 3 consecutive misses. Disconnecting.");
            // Transition through Disconnecting first so listeners observing state
            // changes see the full lifecycle (Connected → Disconnecting → Disconnected),
            // consistent with the explicit Disconnect() path.
            TransitionTo(NetworkState.Disconnecting);
            _heartbeatManager?.Stop();
            _networkThread?.Stop();
            // N-1: preserve the reconnect token across the drop so apps can
            // observe OnDisconnected and call Reconnect() without the user
            // having to re-authenticate.  If the app doesn't want a reconnect
            // (e.g. explicit logout), calling Disconnect() still clears it.
            ClearSessionData(preserveReconnectToken: true);
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
        }

        /// <summary>
        /// Route room packets to the RoomManager (lifecycle 0x20–0x23,
        /// management 0x2C/0x2E/0x2F).
        /// </summary>
        private void OnRoomPacket(PacketType type, byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _roomManager.HandleRoomPacket(type, payload);
        }

        /// <summary>
        /// Routes a LobbyJoin reply (0x27) or LobbyList reply (0x29) to the
        /// LobbyManager.  LobbyLeave (0x28) has no server reply but is passed
        /// here for uniform event notification if needed.
        /// </summary>
        private void OnLobbyPacket(PacketType type, byte[] data)
        {
            if (_lobbyManager == null) return;
            if (type == PacketType.LobbyLeave) return; // fire-and-forget: no reply payload
            var payload = PacketParser.ExtractPayload(data);
            _lobbyManager.HandleLobbyReply(payload);
        }

        /// <summary>
        /// Routes a LobbyRoomListUpdate push (0x2A) to the LobbyManager.
        /// </summary>
        private void OnLobbyRoomListUpdate(byte[] data)
        {
            if (_lobbyManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _lobbyManager.HandleLobbyRoomListUpdate(payload);
        }

        /// <summary>
        /// Handle an inbound <c>RoomPropertyUpdate</c> (0x24) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the local <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnRoomPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(payload);
                var (version, props) = PropertyJson.DecodeRoomPayload(json);
                _roomManager.ApplyRoomPropertiesBroadcast(version, props);
            }
            catch (Exception ex)
            {
                LogDebug($"RoomPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle an inbound <c>PlayerPropertyUpdate</c> (0x25) broadcast from
        /// the server.  Decodes the JSON payload and applies the accepted
        /// property snapshot to the matching player in
        /// <see cref="RoomManager.CurrentRoom"/>.
        /// </summary>
        private void OnPlayerPropertyUpdateBroadcast(byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            if (payload == null || payload.Length == 0) return;
            try
            {
                var json = System.Text.Encoding.UTF8.GetString(payload);
                var (playerId, version, props) = PropertyJson.DecodePlayerPayload(json);
                _roomManager.ApplyPlayerPropertiesBroadcast(playerId, version, props);
            }
            catch (Exception ex)
            {
                LogDebug($"PlayerPropertyUpdate broadcast: decode failed: {ex.Message}");
            }
        }

        // ── Spawn / Despawn inbound handlers ─────────────────────────

        /// <summary>
        /// Handle an inbound <c>Spawn</c> (0x30) packet from the server.
        /// Parses the payload and calls <see cref="SpawnManager.CreateLocal"/>
        /// to instantiate the object on the receiving client.
        /// </summary>
        private void OnSpawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseSpawn(payload, out var spawnData))
            {
                LogDebug("Spawn packet: malformed payload, dropped.");
                return;
            }

            // Dedup: if this object was already spawned locally (e.g. server echoed
            // our own Spawn back), skip to avoid creating a duplicate GameObject.
            if (_spawnManager.Registry.Get(spawnData.ObjectId) != null)
            {
                LogDebug($"Spawn packet: objectId {spawnData.ObjectId} already exists, skipped (dedup).");
                return;
            }

            _spawnManager.CreateLocal(
                spawnData.PrefabId, spawnData.ObjectId,
                spawnData.OwnerPlayerId, spawnData.Position, spawnData.Rotation);
        }

        /// <summary>
        /// Handle an inbound <c>Despawn</c> (0x31) packet from the server.
        /// Parses the object ID and calls <see cref="SpawnManager.DestroyLocal"/>.
        /// </summary>
        private void OnDespawnPacket(byte[] data)
        {
            if (_spawnManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            if (!SpawnPacketParser.TryParseDespawn(payload, out var objectId))
            {
                LogDebug("Despawn packet: malformed payload, dropped.");
                return;
            }
            _spawnManager.DestroyLocal(objectId);
        }

        // ── RPC inbound handlers ─────────────────────────────────────

        /// <summary>
        /// Handle an inbound <c>Rpc</c> (0x50) request from the server.
        /// Dispatches ownership-related RPCs (200) and damage RPCs (301).
        /// </summary>
        private void OnRpcRequest(byte[] data)
        {
            // Distinguish Enhanced RPC (27-byte header, typed params) from legacy (18-byte).
            bool isEnhanced = (data[PacketProtocol.OFFSET_FLAGS] & (byte)PacketFlags.EnhancedRpc) != 0;

            var payload = PacketParser.ExtractPayload(data);

            if (isEnhanced)
            {
                OnEnhancedRpcRequest(payload);
                return;
            }

            // Legacy RPC path.
            if (!RpcPacketParser.TryParseRequest(payload, out var request))
            {
                LogDebug("RPC request: malformed payload, dropped.");
                return;
            }

            switch (request.MethodId)
            {
                case RpcMethodId.TransferOwnership:
                    HandleOwnershipTransferRpc(request);
                    break;
                // Server-broadcast ApplyDamage (301) → route to target HealthController.
                case RpcMethodId.ApplyDamage:
                    HandleApplyDamageRpc(request);
                    break;
                default:
                    LogDebug($"RPC request: unhandled method_id {request.MethodId}.");
                    break;
            }
        }

        /// <summary>
        /// Dispatch an inbound Enhanced RPC packet to the target <c>NetworkBehaviour</c>.
        /// Resolves the object via the spawn registry and invokes the correct
        /// <c>[RtmpeRpc]</c> method via <see cref="RTMPE.Core.NetworkBehaviour.DispatchEnhancedRpc"/>.
        /// </summary>
        private void OnEnhancedRpcRequest(byte[] payload)
        {
            if (!EnhancedRpcPacketParser.TryParse(payload, out var req))
            {
                LogDebug("Enhanced RPC: malformed payload, dropped.");
                return;
            }

            var nb = _spawnManager?.Registry?.Get(req.ObjectId);
            if (nb == null)
            {
                LogDebug($"Enhanced RPC: no spawned object with id {req.ObjectId} — dropped.");
                return;
            }

            nb.DispatchEnhancedRpc(req.MethodId, req.Args);
        }

        /// <summary>
        /// Handle an <c>RpcBufferReplay</c> (0x52) packet delivered immediately after joining a room.
        /// Decodes the binary replay buffer and dispatches each Enhanced RPC event as if it arrived live.
        /// </summary>
        /// <param name="payload">
        /// Binary payload: [event_count:2 LE u16][for each: [payload_len:2 LE u16][payload:N bytes]]
        /// </param>
        private void HandleRpcBufferReplay(byte[] payload)
        {
            if (payload == null || payload.Length < 2)
            {
                LogDebug("RpcBufferReplay: empty or truncated payload, skipped.");
                return;
            }

            int offset = 0;
            ushort eventCount = (ushort)(payload[offset] | (payload[offset + 1] << 8));
            offset += 2;

            for (int i = 0; i < eventCount; i++)
            {
                if (offset + 2 > payload.Length)
                {
                    LogDebug($"RpcBufferReplay: truncated at event {i}/{eventCount}, aborting replay.");
                    return;
                }
                ushort payloadLen = (ushort)(payload[offset] | (payload[offset + 1] << 8));
                offset += 2;

                if (offset + payloadLen > payload.Length)
                {
                    LogDebug($"RpcBufferReplay: event {i} payload truncated ({payloadLen} bytes), aborting.");
                    return;
                }

                var eventPayload = new byte[payloadLen];
                Array.Copy(payload, offset, eventPayload, 0, payloadLen);
                offset += payloadLen;

                if (!EnhancedRpcPacketParser.TryParse(eventPayload, out var request))
                {
                    LogDebug($"RpcBufferReplay: failed to parse event {i}, skipped.");
                    continue;
                }

                var nb = _spawnManager?.Registry?.Get(request.ObjectId);
                if (nb == null)
                {
                    LogDebug($"RpcBufferReplay: no spawned object {request.ObjectId} for event {i}, skipped.");
                    continue;
                }
                nb.DispatchEnhancedRpc(request.MethodId, request.Args);
            }
        }

        /// <summary>
        /// Handle an inbound <c>RpcResponse</c> (0x51) from the server.
        /// Routes ownership grant responses to the OwnershipManager.
        /// </summary>
        private void OnRpcResponse(byte[] data)
        {
            var payload = PacketParser.ExtractPayload(data);
            if (!RpcPacketParser.TryParseResponse(payload, out var response))
            {
                LogDebug("RPC response: malformed payload, dropped.");
                return;
            }

            switch (response.MethodId)
            {
                case RpcMethodId.TransferOwnership:
                    HandleOwnershipTransferResponse(response);
                    break;
                default:
                    LogDebug($"RPC response: unhandled method_id {response.MethodId}.");
                    break;
            }
        }

        /// <summary>
        /// Process a server-broadcast TransferOwnership RPC that tells this client
        /// to apply an ownership change (server-authoritative grant).
        /// Payload: [object_id:8 LE u64][new_owner_len:2 LE u16][new_owner:N UTF-8].
        /// </summary>
        private void HandleOwnershipTransferRpc(RpcRequest request)
        {
            if (_spawnManager == null) return;
            if (request.Payload.Length < 10) return;   // 8 + 2 minimum

            // Wire protocol is little-endian.  `BitConverter.ToUInt64/ToUInt16`
            // honour the host's native byte order, which is big-endian on some
            // platforms — the resulting object ID would be byte-reversed and
            // collide with a completely different spawned object, or none.
            // Read the 10 fixed bytes explicitly LE so the decoding matches
            // the gateway's binary.LittleEndian serialisation regardless of host.
            var p = request.Payload;
            ulong objectId =
                  (ulong)p[0]
                | ((ulong)p[1] << 8)
                | ((ulong)p[2] << 16)
                | ((ulong)p[3] << 24)
                | ((ulong)p[4] << 32)
                | ((ulong)p[5] << 40)
                | ((ulong)p[6] << 48)
                | ((ulong)p[7] << 56);
            ushort ownerLen = (ushort)(p[8] | (p[9] << 8));
            if (request.Payload.Length < 10 + ownerLen) return;

            string newOwner = ownerLen > 0
                ? System.Text.Encoding.UTF8.GetString(request.Payload, 10, ownerLen)
                : string.Empty;

            _spawnManager.Ownership.ApplyOwnershipGrant(objectId, newOwner);
        }

        /// <summary>
        /// Process the server's response to a client-initiated TransferOwnership RPC.
        /// On success, the server has already broadcast the grant to all clients via
        /// <see cref="HandleOwnershipTransferRpc"/>. On failure, log the error.
        /// </summary>
        private void HandleOwnershipTransferResponse(RpcResponse response)
        {
            if (!response.Success)
            {
                Debug.LogWarning(
                    $"[RTMPE] Ownership transfer request {response.RequestId} " +
                    $"rejected by server (error code: {response.ErrorCode}).");
            }
        }

        /// <summary>RoomManager fires OnRoomCreated → transition to InRoom.</summary>
        private void OnRoomManagerCreated(RoomInfo room)
        {
            RememberRoom(room);
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
#pragma warning disable CS0618 // Legacy event — users should migrate to RoomManager events
                OnJoinedRoom?.Invoke(0);
#pragma warning restore CS0618
            }
        }

        /// <summary>RoomManager fires OnRoomJoined → transition to InRoom.</summary>
        private void OnRoomManagerJoined(RoomInfo room)
        {
            RememberRoom(room);
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
#pragma warning disable CS0618
                OnJoinedRoom?.Invoke(0);
#pragma warning restore CS0618
            }
        }

        /// <summary>RoomManager fires OnRoomLeft → transition back to Connected.</summary>
        private void OnRoomManagerLeft()
        {
            // Explicit leave = user wants out of this room; clear the
            // last-room snapshot so a subsequent Reconnect() does NOT auto-rejoin.
            _lastRoomId   = null;
            _lastRoomCode = null;
            if (_state == NetworkState.InRoom)
            {
                _spawnManager?.ClearAll();  // Destroy all spawned objects on room leave
                TransitionTo(NetworkState.Connected);
#pragma warning disable CS0618
                OnLeftRoom?.Invoke(0);
#pragma warning restore CS0618
            }
        }

        /// <summary>
        /// Remember the currently-joined room so <see cref="Reconnect"/> can
        /// auto-rejoin it after a token-preserving disconnect.  A null or
        /// empty room argument clears the snapshot (defensive — the room
        /// parsers already return empty strings rather than null IDs).
        /// </summary>
        private void RememberRoom(RoomInfo room)
        {
            if (room == null || string.IsNullOrEmpty(room.RoomId))
            {
                _lastRoomId   = null;
                _lastRoomCode = null;
                return;
            }
            _lastRoomId   = room.RoomId;
            _lastRoomCode = room.RoomCode;
        }

        private void OnServerDisconnect(byte[] _)
        {
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ServerRequest);
        }

        // ── Transport error path ───────────────────────────────────────────────

        private void HandleTransportError(Exception ex)
        {
            Debug.LogError($"[RTMPE] Transport error: {ex.Message}");

            _dispatcher?.Enqueue(() =>
            {
                bool wasConnecting = _state == NetworkState.Connecting;

                if (_timeoutCoroutine != null)
                {
                    StopCoroutine(_timeoutCoroutine);
                    _timeoutCoroutine = null;
                }
                if (_connectCoroutine != null)
                {
                    StopCoroutine(_connectCoroutine);
                    _connectCoroutine = null;
                }

                if (wasConnecting)
                    OnConnectionFailed?.Invoke(ex.Message);

                _networkThread?.Stop();
                _heartbeatManager?.Stop();
                ClearSessionData();
                TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
            });
        }

        // ── State machine ──────────────────────────────────────────────────────

        private void TransitionTo(
            NetworkState   next,
            DisconnectReason reason = DisconnectReason.Unknown)
        {
            var prev = _state;
            if (prev == next) return;

            _state = next;
            LogDebug($"State: {prev} \u2192 {next}");
            OnStateChanged?.Invoke(prev, next);

            switch (next)
            {
                case NetworkState.Connected:
                    OnConnected?.Invoke();
                    break;

                case NetworkState.Disconnected when prev != NetworkState.Disconnected:
                    OnDisconnected?.Invoke(reason);
                    break;
            }
        }

        // ── Outbound helpers ───────────────────────────────────────────────────

        private void SendHandshakeInit(string apiKey)
        {
            byte[] psk = null;
            try { psk = _settings?.ApiKeyPskBytes; }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Invalid apiKeyPskHex in settings: {ex.Message}");
                // Fall through — will send without encryption (insecure dev path).
            }

            byte[] encryptedPayload;
            if (psk != null)
            {
                var localEp = _transport.LocalEndPoint;
                if (localEp == null)
                {
                    Debug.LogError("[RTMPE] SendHandshakeInit: transport not yet bound, aborting.");
                    return;
                }
                encryptedPayload = ApiKeyCipher.Encrypt(psk, apiKey, localEp);
                LogDebug($"SendHandshakeInit: API key encrypted with PSK, source={localEp}");
            }
            else
            {
                // No PSK configured — build unencrypted payload for local dev only.
                // Abort in release builds — the plaintext path is a developer
                // convenience and must not be used in production.
#if !UNITY_EDITOR && !DEVELOPMENT_BUILD
                Debug.LogError("[RTMPE] SendHandshakeInit: apiKeyPskHex MUST be configured in " +
                               "release builds. Sending the API key unencrypted exposes it to " +
                               "any network observer. Aborting connection. " +
                               "Set apiKeyPskHex in NetworkSettings.");
                return;
#else
                // WARNING: insecure — local dev / editor only.
                Debug.LogWarning("[RTMPE] apiKeyPskHex is not configured — sending API key unencrypted. " +
                                 "This is insecure. Set apiKeyPskHex in NetworkSettings for production.");
                var keyBytes = System.Text.Encoding.UTF8.GetBytes(apiKey);
                encryptedPayload = new byte[2 + keyBytes.Length];
                encryptedPayload[0] = (byte)(keyBytes.Length & 0xFF);
                encryptedPayload[1] = (byte)((keyBytes.Length >> 8) & 0xFF);
                Buffer.BlockCopy(keyBytes, 0, encryptedPayload, 2, keyBytes.Length);
#endif
            }

            // Use SendOwned — packet is a freshly built array; the caller
            // does not retain a reference after this call.
            var packet = _packetBuilder.BuildHandshakeInit(encryptedPayload);
            _networkThread.SendOwned(packet);
            LogDebug($"HandshakeInit sent ({packet.Length} B).");
        }

        private void SendDisconnect()
        {
            if (_packetBuilder == null) return;
            var packet = _packetBuilder.BuildDisconnect();
            // Route through EncryptAndSend so the Disconnect packet is AEAD-encrypted
            // when a session is active, matching gateway expectations.
            EncryptAndSend(packet);
            LogDebug("Sent Disconnect packet.");
        }

        /// <summary>
        /// **N-1** — emit a <c>ReconnectInit</c> packet carrying the stored
        /// reconnect token.  Payload is plaintext (no PSK encryption — the
        /// token itself IS the authentication) and does NOT go through
        /// <see cref="EncryptAndSend"/> because no session key exists yet.
        /// </summary>
        /// <param name="token">
        /// The previously-stored reconnect token.  Empty / null is treated as
        /// a programming error and aborts without sending.
        /// </param>
        private void SendReconnectInit(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                Debug.LogError("[RTMPE] SendReconnectInit: token is empty — aborting reconnect.");
                return;
            }
            if (_packetBuilder == null)
            {
                Debug.LogError("[RTMPE] SendReconnectInit: packet builder not initialised — aborting.");
                return;
            }

            // N-8: if we have an IP migration key, compute HMAC-SHA256(key, token_bytes)
            // and include it as a 32-byte proof so the gateway can accept a reconnect
            // from a new IP address (WiFi → 4G migration).
            byte[] proof = null;
            if (_ipMigrationKey != null)
            {
                var tokenBytes = System.Text.Encoding.UTF8.GetBytes(token);
                using var hmac = new System.Security.Cryptography.HMACSHA256(_ipMigrationKey);
                proof = hmac.ComputeHash(tokenBytes);
            }

            byte[] packet;
            try
            {
                packet = _packetBuilder.BuildReconnectInit(token, proof);
            }
            catch (ArgumentException ex)
            {
                Debug.LogError($"[RTMPE] SendReconnectInit: token rejected ({ex.Message}); aborting.");
                return;
            }

            _networkThread.SendOwned(packet);
            LogDebug($"ReconnectInit sent ({packet.Length} B, proof={proof != null}).");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private IEnumerator ConnectionTimeoutRoutine()
        {
            yield return new WaitForSeconds(_settings.connectionTimeoutMs / 1_000f);

            // N-1: the timeout applies to both the fresh-Connect path
            // (Connecting) and the reconnect path (Reconnecting).  On reconnect
            // timeout we also clear the reconnect token — the gateway has
            // already consumed it (single-use), so keeping it on the client
            // would just feed stale reconnect attempts that always fail.
            if (_state == NetworkState.Connecting || _state == NetworkState.Reconnecting)
            {
                bool wasReconnecting = _state == NetworkState.Reconnecting;
                OnConnectionFailed?.Invoke(
                    wasReconnecting ? "Reconnect timeout." : "Connection timeout.");
                _networkThread?.Stop();
                _heartbeatManager?.Stop();
                ClearSessionData(preserveReconnectToken: false);
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }

            _timeoutCoroutine = null;
        }

        private void ClearSessionData() => ClearSessionData(preserveReconnectToken: false);

        /// <summary>
        /// Tear down the current session's crypto + application state.
        /// </summary>
        /// <param name="preserveReconnectToken">
        /// **N-1** — when <see langword="true"/>, the <see cref="_reconnectToken"/>
        /// is deliberately left intact so a subsequent <c>ReconnectInit</c> can
        /// resume the session.  All other state (JWT, crypto keys, room context,
        /// spawned objects) is still wiped — the token alone is insufficient
        /// to speak the protocol until the handshake completes.
        /// <para>
        /// Default <see langword="false"/> preserves pre-N-1 semantics: a clean
        /// disconnect clears everything.
        /// </para>
        /// </param>
        private void ClearSessionData(bool preserveReconnectToken)
        {
            _cryptoId              = 0;
            _jwtToken              = null;
            if (!preserveReconnectToken)
            {
                _reconnectToken    = null;
                // N-8: ip_migration_key is only useful alongside the reconnect token.
                if (_ipMigrationKey != null)
                {
                    Array.Clear(_ipMigrationKey, 0, _ipMigrationKey.Length);
                    _ipMigrationKey = null;
                }
                // Last-room snapshot is a companion to the reconnect token —
                // both lose meaning once the token is cleared.  An explicit
                // Disconnect() therefore also wipes the snapshot so a later
                // Connect(apiKey) starts without dangling rejoin state.
                _lastRoomId   = null;
                _lastRoomCode = null;
            }
            // NOTE: when preserveReconnectToken is true we intentionally leave
            // _lastRoomId / _lastRoomCode intact so Reconnect() can feed them
            // back into RoomManager.JoinRoom after SessionAck.
            _localPlayerId         = 0;
            _localPlayerStringId   = null;
            _currentRoomId         = 0;
            // Reset AEAD nonce counter so a future reconnect starts at counter = 0.
            System.Threading.Interlocked.Exchange(ref _outboundNonceCounter, -1L);
            _roomManager?.ClearState();
            _spawnManager?.ClearAll();     // Destroy all spawned objects
            _handshakeHandler?.Dispose();  // Zero key material before GC can observe it
            _handshakeHandler = null;
            _sessionKeys?.Dispose();       // Zero session keys before GC can observe it
            _sessionKeys      = null;
            LastRttMs         = -1f;
        }

        // ── AEAD outbound / inbound pipeline ──────────────────────────────────

        /// <summary>
        /// Encrypts <paramref name="packet"/> with ChaCha20-Poly1305 AEAD and enqueues it
        /// for transmission on the network thread.
        ///
        /// <para>If session keys are not yet established (pre-handshake, e.g.
        /// <c>HandshakeInit</c>) the packet is sent as-is — the gateway expects those
        /// to arrive in plaintext.</para>
        ///
        /// <para>When session keys are present the following transformations are applied,
        /// mirroring Rust gateway <c>encrypt_outbound()</c> in
        /// <c>modules/gateway/src/crypto/pipeline.rs</c>:</para>
        /// <list type="number">
        ///   <item>The original application <c>header.sequence</c> is saved and prepended
        ///         as a 4-byte LE prefix to the plaintext before sealing.</item>
        ///   <item>AAD = <c>[packet_type, flags]</c> where <c>flags</c> does <b>not</b>
        ///         yet include <c>FLAG_ENCRYPTED</c>.</item>
        ///   <item>A 12-byte nonce is built: <c>[counter:8 LE u64][_cryptoId:4 LE u32]</c>.
        ///         The outbound counter is atomically incremented from
        ///         <c>_outboundNonceCounter</c>.</item>
        ///   <item><c>header.sequence</c> is overwritten with the nonce counter (lower
        ///         32 bits), <c>FLAG_ENCRYPTED</c> is set, and <c>payload_len</c> is
        ///         updated to reflect the enlarged ciphertext.</item>
        /// </list>
        /// </summary>
        private void EncryptAndSend(byte[] packet)
        {
            if (packet == null || packet.Length < PacketProtocol.HEADER_SIZE)
                return;

            // Pre-session: HandshakeInit and HandshakeResponse travel in plaintext.
            if (_sessionKeys == null)
            {
                _networkThread.SendOwned(packet);
                return;
            }

            // ── 1. Claim next nonce counter ──────────────────────────────────────
            // _outboundNonceCounter starts at -1L; first call returns 0, matching
            // the Rust NonceGenerator which also starts at 0.
            long rawCounter = System.Threading.Interlocked.Increment(
                ref _outboundNonceCounter);

            // Hard stop: counter reached 2^32 — the gateway's NonceGenerator
            // exhausts at the same threshold (SEQUENCE_EXHAUSTION_THRESHOLD).
            // Beyond this point every packet would reuse a nonce already in the
            // gateway's replay-protection window, guaranteeing rejection.
            // Disconnect immediately so the app can re-establish a fresh session.
            if (rawCounter >= OutboundNonceExhaustionThreshold)
            {
                Debug.LogError("[RTMPE] Outbound nonce counter exhausted after 2^32 packets. " +
                               "Session must be re-established with fresh session keys.");
                Disconnect();
                return;
            }

            // Advisory: warn when fewer than ~1 M nonces remain (~9.7 h @ 30 Hz).
            // Gives the application time to schedule a graceful reconnect before the
            // hard stop fires. Mirrors the gateway's is_near_exhaustion() check.
            if (rawCounter >= OutboundNonceExhaustionThreshold - OutboundNonceNearExhaustionMargin)
                Debug.LogWarning(
                    $"[RTMPE] Outbound nonce counter near exhaustion — " +
                    $"{OutboundNonceExhaustionThreshold - rawCounter:N0} packets remaining. " +
                    "Schedule a session re-establishment soon.");

            uint nonceCounter = (uint)rawCounter;

            // ── 2. Read original sequence and payload from header ────────────────
            uint origSeq = (uint)(
                  packet[PacketProtocol.OFFSET_SEQUENCE]
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 1] << 8)
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 2] << 16)
                | (packet[PacketProtocol.OFFSET_SEQUENCE + 3] << 24));

            uint payloadLen = (uint)(
                  packet[PacketProtocol.OFFSET_PAYLOAD_LEN]
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] << 8)
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] << 16)
                | (packet[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] << 24));

            byte packetType = packet[PacketProtocol.OFFSET_TYPE];
            byte flags      = packet[PacketProtocol.OFFSET_FLAGS];

            // ── 3. Build payload bytes, compressing if beneficial ────────────────
            // Compression happens before AEAD sealing so the tag covers the
            // compressed form.  FLAG_COMPRESSED is set in both the plaintext
            // prefix (restored after decryption) and the AAD so the gateway
            // can verify it didn't change in transit.
            byte[] rawPayload = null;
            if (payloadLen > 0)
            {
                rawPayload = new byte[(int)payloadLen];
                Buffer.BlockCopy(packet, PacketProtocol.HEADER_SIZE,
                                 rawPayload, 0, (int)payloadLen);
            }

            byte[] effectivePayload = rawPayload ?? Array.Empty<byte>();
            if (rawPayload != null)
            {
                var candidate = Lz4Compressor.CompressIfBeneficial(rawPayload, out bool didCompress);
                if (didCompress)
                {
                    effectivePayload = candidate;
                    flags |= (byte)PacketFlags.Compressed;
                }
            }

            // ── 4. Build plaintext = [orig_seq:4 LE] || effectivePayload ────────
            int ptLen = 4 + effectivePayload.Length;
            var plaintext = new byte[ptLen];
            plaintext[0] = (byte) origSeq;
            plaintext[1] = (byte)(origSeq >>  8);
            plaintext[2] = (byte)(origSeq >> 16);
            plaintext[3] = (byte)(origSeq >> 24);
            if (effectivePayload.Length > 0)
                Buffer.BlockCopy(effectivePayload, 0, plaintext, 4, effectivePayload.Length);

            // ── 5. Build AAD = [packet_type, flags_without_encrypted] ────────────
            // flags now includes FLAG_COMPRESSED if compression was applied.
            // flags does NOT yet include FLAG_ENCRYPTED — this must match exactly
            // what the gateway sees as AAD on its decrypt_inbound() path.
            var aad = new byte[] { packetType, flags };

            // ── 6. Build 12-byte nonce = [counter:8 LE][crypto_id:4 LE] ─────────
            var nonce = BuildAeadNonce(nonceCounter, _cryptoId);

            // ── 7. Seal (ChaCha20-Poly1305) ──────────────────────────────────────
            var ciphertext = ChaCha20Poly1305Impl.Seal(
                _sessionKeys.EncryptKey, nonce, plaintext, aad);
            // ciphertext.Length == ptLen + 16  (Poly1305 tag appended)

            // ── 8. Assemble the encrypted packet ────────────────────────────────
            var result = new byte[PacketProtocol.HEADER_SIZE + ciphertext.Length];
            // Copy header as-is first, then patch the three affected fields.
            Buffer.BlockCopy(packet, 0, result, 0, PacketProtocol.HEADER_SIZE);

            // header.sequence = nonce_counter  (gateway uses this to reconstruct nonce)
            result[PacketProtocol.OFFSET_SEQUENCE]     = (byte) nonceCounter;
            result[PacketProtocol.OFFSET_SEQUENCE + 1] = (byte)(nonceCounter >>  8);
            result[PacketProtocol.OFFSET_SEQUENCE + 2] = (byte)(nonceCounter >> 16);
            result[PacketProtocol.OFFSET_SEQUENCE + 3] = (byte)(nonceCounter >> 24);

            // header.flags |= FLAG_ENCRYPTED
            result[PacketProtocol.OFFSET_FLAGS] = (byte)(flags | (byte)PacketFlags.Encrypted);

            // header.payload_len = len(ciphertext)
            uint ctLen = (uint)ciphertext.Length;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte) ctLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(ctLen >>  8);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = (byte)(ctLen >> 16);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = (byte)(ctLen >> 24);

            Buffer.BlockCopy(ciphertext, 0, result,
                             PacketProtocol.HEADER_SIZE, ciphertext.Length);

            _networkThread.SendOwned(result);
        }

        /// <summary>
        /// Decrypts an inbound packet that has <c>FLAG_ENCRYPTED</c> set.
        ///
        /// <para>Reverses the transformations applied by the gateway's
        /// <c>encrypt_outbound()</c>:</para>
        /// <list type="number">
        ///   <item>Reconstructs the 12-byte nonce from <c>header.sequence</c> (the nonce
        ///         counter placed there by the gateway) and <c>_cryptoId</c>.</item>
        ///   <item>AAD = <c>[packet_type, flags &amp; ~FLAG_ENCRYPTED]</c>.</item>
        ///   <item>Opens (decrypts + verifies) the ciphertext with
        ///         <c>_sessionKeys.DecryptKey</c>.</item>
        ///   <item>Recovers the original application sequence from the first 4 bytes of
        ///         the plaintext (the SEQ prefix) and writes it back to
        ///         <c>header.sequence</c>.</item>
        ///   <item>Returns a rebuilt packet: decrypted payload, cleared
        ///         <c>FLAG_ENCRYPTED</c>, corrected <c>payload_len</c>.</item>
        /// </list>
        ///
        /// <returns>
        ///   The decrypted packet, or <see langword="null"/> on MAC failure, missing
        ///   session keys, or a malformed input — the caller must drop the packet silently.
        /// </returns>
        /// </summary>
        private byte[] DecryptInboundPacket(byte[] data)
        {
            if (_sessionKeys == null) return null;

            // Minimum valid encrypted packet:
            //   header(13) + SEQ_prefix(4) + Poly1305_tag(16) = 33 bytes.
            if (data == null || data.Length < PacketProtocol.HEADER_SIZE + 4 + 16)
                return null;

            // ── 1. Read nonce counter from header.sequence ───────────────────────
            // The gateway wrote nonce_counter here during encryption.
            uint nonceCounter = (uint)(
                  data[PacketProtocol.OFFSET_SEQUENCE]
                | (data[PacketProtocol.OFFSET_SEQUENCE + 1] << 8)
                | (data[PacketProtocol.OFFSET_SEQUENCE + 2] << 16)
                | (data[PacketProtocol.OFFSET_SEQUENCE + 3] << 24));

            byte packetType = data[PacketProtocol.OFFSET_TYPE];
            byte flags      = data[PacketProtocol.OFFSET_FLAGS];

            // ── 2. Build AAD = [packet_type, flags & ~FLAG_ENCRYPTED] ────────────
            // Stripping FLAG_ENCRYPTED reproduces the AAD the gateway used when sealing.
            var aad = new byte[] { packetType,
                                   (byte)(flags & ~(byte)PacketFlags.Encrypted) };

            // ── 3. Build 12-byte nonce ───────────────────────────────────────────
            var nonce = BuildAeadNonce(nonceCounter, _cryptoId);

            // ── 4. Extract ciphertext (everything after the header) ──────────────
            int ctLen = data.Length - PacketProtocol.HEADER_SIZE;
            var ciphertext = new byte[ctLen];
            Buffer.BlockCopy(data, PacketProtocol.HEADER_SIZE, ciphertext, 0, ctLen);

            // ── 5. Open: decrypt + verify Poly1305 tag ───────────────────────────
            var plaintext = ChaCha20Poly1305Impl.Open(
                _sessionKeys.DecryptKey, nonce, ciphertext, aad);
            if (plaintext == null) return null; // MAC failure — drop

            // plaintext = [orig_seq:4 LE] || actual_payload (possibly LZ4-compressed)
            if (plaintext.Length < 4) return null; // should never happen

            // ── 6. Recover original application sequence from SEQ prefix ─────────
            uint origSeq = (uint)(
                  plaintext[0]
                | (plaintext[1] << 8)
                | (plaintext[2] << 16)
                | (plaintext[3] << 24));

            // ── 7. Decompress payload if FLAG_COMPRESSED was set ─────────────────
            // Compression was authenticated via AAD, so this branch is only
            // reachable for legitimately sealed compressed packets.
            bool wasCompressed = (flags & (byte)PacketFlags.Compressed) != 0;
            byte[] finalPayload;
            int    finalPayloadLen;

            if (wasCompressed && plaintext.Length > 4)
            {
                var compressed = new byte[plaintext.Length - 4];
                Buffer.BlockCopy(plaintext, 4, compressed, 0, compressed.Length);
                try
                {
                    finalPayload    = Lz4Compressor.Decompress(compressed);
                    finalPayloadLen = finalPayload.Length;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] Dropped packet: LZ4 decompression failed — {ex.Message}");
                    return null;
                }
            }
            else
            {
                finalPayloadLen = plaintext.Length - 4;
                finalPayload    = null; // use plaintext[4..] via BlockCopy below
            }

            // ── 8. Rebuild packet with restored header ───────────────────────────
            var result = new byte[PacketProtocol.HEADER_SIZE + finalPayloadLen];
            Buffer.BlockCopy(data, 0, result, 0, PacketProtocol.HEADER_SIZE);

            // Restore original application sequence number.
            result[PacketProtocol.OFFSET_SEQUENCE]     = (byte) origSeq;
            result[PacketProtocol.OFFSET_SEQUENCE + 1] = (byte)(origSeq >>  8);
            result[PacketProtocol.OFFSET_SEQUENCE + 2] = (byte)(origSeq >> 16);
            result[PacketProtocol.OFFSET_SEQUENCE + 3] = (byte)(origSeq >> 24);

            // Clear FLAG_ENCRYPTED and FLAG_COMPRESSED — downstream handlers
            // always receive plaintext uncompressed packets.
            result[PacketProtocol.OFFSET_FLAGS] = (byte)(flags
                & ~(byte)PacketFlags.Encrypted
                & ~(byte)PacketFlags.Compressed);

            // Update payload_len: SEQ prefix, tag, and compression overhead removed.
            uint newPayloadLen = (uint)finalPayloadLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN]     = (byte) newPayloadLen;
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 1] = (byte)(newPayloadLen >>  8);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 2] = (byte)(newPayloadLen >> 16);
            result[PacketProtocol.OFFSET_PAYLOAD_LEN + 3] = (byte)(newPayloadLen >> 24);

            if (finalPayloadLen > 0)
            {
                if (finalPayload != null)
                    Buffer.BlockCopy(finalPayload, 0, result,
                                     PacketProtocol.HEADER_SIZE, finalPayloadLen);
                else
                    Buffer.BlockCopy(plaintext, 4, result,
                                     PacketProtocol.HEADER_SIZE, finalPayloadLen);
            }

            return result;
        }

        /// <summary>
        /// Build the 12-byte ChaCha20-Poly1305 nonce shared by both directions.
        ///
        /// <para>Layout mirrors
        /// <c>NonceGenerator::build_nonce_raw(counter, session_id)</c> in the Rust
        /// gateway (<c>modules/gateway/src/crypto/nonce.rs</c>):</para>
        /// <code>
        ///   [counter : 8 bytes LE u64] [session_id : 4 bytes LE u32]
        /// </code>
        ///
        /// <para><paramref name="counter"/> is a <see cref="uint"/> (zero-extended to 8
        /// bytes); the high 4 bytes are therefore always <c>0x00</c> for any session
        /// within its practical lifetime.</para>
        /// </summary>
        private static byte[] BuildAeadNonce(uint counter, uint sessionId)
        {
            var nonce = new byte[12];
            // counter — 8 bytes LE (high 32 bits are always 0)
            nonce[0] = (byte) counter;
            nonce[1] = (byte)(counter >>  8);
            nonce[2] = (byte)(counter >> 16);
            nonce[3] = (byte)(counter >> 24);
            // nonce[4..7] remain 0x00 (C# zero-initialises arrays)

            // session_id — 4 bytes LE
            nonce[8]  = (byte) sessionId;
            nonce[9]  = (byte)(sessionId >>  8);
            nonce[10] = (byte)(sessionId >> 16);
            nonce[11] = (byte)(sessionId >> 24);
            return nonce;
        }

        // ── State-sync inbound handler ────────────────────────────────

        /// <summary>
        /// Route incoming <c>StateSync</c>/<c>Data</c> server broadcasts to
        /// the <see cref="NetworkTransformInterpolator"/> on the matching spawned object.
        /// Subscribed to <see cref="OnDataReceived"/> in <see cref="InitialiseNetwork"/>.
        /// </summary>
        private void HandleStateSyncPacket(byte[] data)
        {
            if (_spawnManager == null) return;

            var payload = PacketParser.ExtractPayload(data);
            if (!TransformPacketParser.TryParseStateDelta(
                    payload, out ulong objectId, out byte changedMask, out TransformState state))
                return;

            var nb = _spawnManager.Registry.Get(objectId);
            if (nb == null) return;

            // Build a blended state: merge only the fields present in the delta.
            // Fields absent from the delta keep zero-initialised values in `state`
            // which would clobber the current transform — fall back to the live
            // transform for those axes.
            var current = nb.GetComponent<NetworkTransform>()?.GetState()
                          ?? new TransformState { Position = nb.transform.position,
                                                  Rotation = nb.transform.rotation,
                                                  Scale    = nb.transform.localScale };
            var blended = new TransformState
            {
                Position = (changedMask & TransformPacketParser.ChangedPosition) != 0
                               ? state.Position : current.Position,
                Rotation = (changedMask & TransformPacketParser.ChangedRotation) != 0
                               ? state.Rotation : current.Rotation,
                Scale    = (changedMask & TransformPacketParser.ChangedScale) != 0
                               ? state.Scale : current.Scale,
            };

            // Owning client: feed the server's authoritative state into the
            // NetworkTransform reconciliation path (CSP correction) if prediction
            // is enabled; otherwise discard as before.
            if (nb.IsOwner)
            {
                nb.GetComponent<NetworkTransform>()?.ApplyReconciliation(blended);
                return;
            }

            // Non-owning client: buffer into the interpolator for smooth playback.
            var interp = nb.GetComponent<NetworkTransformInterpolator>();
            if (interp == null) return;

            interp.AddState(blended, UnityEngine.Time.timeAsDouble);
        }

        // ── Variable update inbound handler ────────────────────────────

        /// <summary>
        /// Apply an inbound <c>VariableUpdate</c> (0x41) packet from the server
        /// to the matching spawned object's NetworkVariables.
        /// Payload: [object_id:8 LE][var_count:1][for each: [var_id:2 LE][value_len:2 LE][value_bytes:N]]
        /// </summary>
        private void HandleVariableUpdatePacket(byte[] data)
        {
            if (_spawnManager == null) return;

            var payload = PacketParser.ExtractPayload(data);
            // Minimum: object_id(8) + var_count(1) = 9 bytes.
            if (payload == null || payload.Length < 9) return;

            // Wire protocol is little-endian.  `BitConverter.ToUInt64` is
            // platform-endian — see `HandleOwnershipTransferRpc` for the same
            // correctness rationale.  Decode explicitly LE so the behaviour
            // matches the gateway on every target architecture.
            ulong objectId =
                  (ulong)payload[0]
                | ((ulong)payload[1] << 8)
                | ((ulong)payload[2] << 16)
                | ((ulong)payload[3] << 24)
                | ((ulong)payload[4] << 32)
                | ((ulong)payload[5] << 40)
                | ((ulong)payload[6] << 48)
                | ((ulong)payload[7] << 56);
            int varCount   = payload[8];
            if (varCount == 0) return;

            var nb = _spawnManager.Registry.Get(objectId);
            if (nb == null) return;

            try
            {
                using var ms     = new System.IO.MemoryStream(payload, 9, payload.Length - 9);
                using var reader = new System.IO.BinaryReader(ms);

                for (int i = 0; i < varCount; i++)
                {
                    // Wire format: [var_id:2 LE][value_len:2 LE][value_bytes:N]
                    // Read value_len before dispatching to ApplyVariableUpdate.
                    // If the var_id is unknown, advance the reader by value_len bytes
                    // so subsequent variables in this packet are parsed correctly.
                    if (ms.Length - ms.Position < 4) break; // need var_id(2) + value_len(2)
                    ushort varId    = reader.ReadUInt16();
                    ushort valueLen = reader.ReadUInt16();

                    if (ms.Length - ms.Position < valueLen) break; // truncated packet

                    long valueStart = ms.Position;
                    nb.ApplyVariableUpdate(varId, reader, valueLen);

                    // Ensure the reader is positioned exactly after value_bytes,
                    // even if ApplyVariableUpdate consumed fewer or more bytes.
                    ms.Position = valueStart + valueLen;
                }
            }
            catch (Exception ex)
            {
                LogDebug($"VariableUpdate: parse error for objectId {objectId}: {ex.Message}");
            }
        }

        // ── Variable update send path ─────────────────────────────────

        /// <summary>
        /// Build and enqueue a <c>VariableUpdate</c> (0x41) packet.
        /// Called by <see cref="NetworkBehaviour.FlushDirtyVariables"/> for each
        /// owned object that has dirty NetworkVariables.
        /// </summary>
        internal void SendVariableUpdate(byte[] payload)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payload.Length == 0) return;

            var packet = _packetBuilder.Build(
                PacketType.VariableUpdate,
                PacketFlags.Reliable,  // variable updates need guaranteed delivery
                payload);

            EncryptAndSend(packet);
        }

        // ── Position update send path (Feature #6 — Interest Management) ───

        /// <summary>
        /// Build and enqueue a <c>PositionUpdate</c> (0x42) packet carrying the
        /// client's 2-D world position so the gateway can apply zone-based
        /// interest filtering to room-wide broadcasts.
        ///
        /// <para>Call this from <see cref="RTMPE.Rooms.InterestManager"/> at the
        /// configured update interval while in a room.  Sending outside a room is
        /// a no-op (the gateway has no room context to filter against).</para>
        ///
        /// <para>Payload layout: <c>[x: f32 LE 4 B][y: f32 LE 4 B]</c> — 8 bytes.</para>
        /// </summary>
        internal void SendPositionUpdate(float x, float y)
        {
            if (_networkThread == null || _packetBuilder == null) return;

            var payload = new byte[8];
            var xBytes = BitConverter.GetBytes(x);
            var yBytes = BitConverter.GetBytes(y);
            if (!BitConverter.IsLittleEndian)
            {
                System.Array.Reverse(xBytes);
                System.Array.Reverse(yBytes);
            }
            System.Buffer.BlockCopy(xBytes, 0, payload, 0, 4);
            System.Buffer.BlockCopy(yBytes, 0, payload, 4, 4);

            var packet = _packetBuilder.Build(PacketType.PositionUpdate, PacketFlags.None, payload);
            EncryptAndSend(packet);
        }

        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Called by RoomManager when JoinRoom/CreateRoom succeeds and the server
        /// confirms the local player's room UUID. This is the identifier used by
        /// <see cref="NetworkBehaviour.IsOwner"/> for object ownership comparisons.
        /// </summary>
        /// <summary>
        /// Build and enqueue an RPC request packet for transmission.
        /// Convenience wrapper for game code that does not need
        /// the raw <see cref="BuildPacket"/> / <see cref="Send"/> split.
        /// The packet is built with <see cref="PacketFlags.Reliable"/> so the
        /// KCP layer will retransmit on loss.
        /// </summary>
        /// <param name="methodId">RPC method ID (see <see cref="RpcMethodId"/>).</param>
        /// <param name="rpcPayload">Method-specific payload bytes (max 4096 bytes).</param>
        public void SendRpc(uint methodId, byte[] rpcPayload)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendRpc: cannot send while not connected.");
                return;
            }

            uint requestId = (uint)System.Threading.Interlocked.Increment(ref _rpcRequestCounter);
#pragma warning disable CS0618  // intentional: built-in method IDs still use the legacy builder
            byte[] rpcMessage = RpcPacketBuilder.BuildRequest(methodId, LocalPlayerId, requestId, rpcPayload);
#pragma warning restore CS0618
            byte[] packet     = BuildPacket(PacketType.Rpc, PacketFlags.Reliable, rpcMessage);
            Send(packet, reliable: true);
        }

        /// <summary>
        /// Build and enqueue an Enhanced RPC request for a
        /// <see cref="RtmpeRpcAttribute"/>-decorated method on a
        /// <see cref="NetworkBehaviour"/> component.
        ///
        /// <para>Called internally by <see cref="NetworkBehaviour.RPC"/>. Game code
        /// should not call this directly — use <c>NetworkBehaviour.RPC()</c> instead.</para>
        /// </summary>
        /// <param name="sender">The <c>NetworkBehaviour</c> originating the call.</param>
        /// <param name="methodName">Name of the <c>[RtmpeRpc]</c>-decorated method.</param>
        /// <param name="args">Typed arguments (must be serializable by <see cref="RpcSerializer"/>).</param>
        public void SendEnhancedRpc(NetworkBehaviour sender, string methodName, object[] args)
        {
            if (!IsInRoom)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendEnhancedRpc: must be in a room.");
                return;
            }

            if (sender == null)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.SendEnhancedRpc: sender is null.");
                return;
            }

            if (!RpcRegistry.TryGetMethodId(sender.GetType(), methodName, out uint methodId))
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkManager.SendEnhancedRpc: no [RtmpeRpc] method named " +
                    $"'{methodName}' on {sender.GetType().Name}. Ensure the method is public " +
                    "and decorated with [RtmpeRpc].");
                return;
            }

            // Read target from the attribute so callers do not pass it explicitly.
            RpcRegistry.TryFindMethod(sender.GetType(), methodId, out _, out var attr);
            var target = attr?.Target ?? RpcTarget.All;

            uint requestId = (uint)System.Threading.Interlocked.Increment(ref _rpcRequestCounter);

            byte[] rpcPayload;
            try
            {
                rpcPayload = EnhancedRpcPacketBuilder.Build(
                    methodId, LocalPlayerId, requestId,
                    sender.NetworkObjectId, target, args);
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] NetworkManager.SendEnhancedRpc: failed to build packet for " +
                    $"'{sender.GetType().Name}.{methodName}': {ex.Message}");
                return;
            }

            byte[] packet = BuildPacket(
                PacketType.Rpc,
                PacketFlags.Reliable | PacketFlags.EnhancedRpc,
                rpcPayload);
            Send(packet, reliable: true);
        }

        /// <summary>
        /// Handle a server-broadcast <c>ApplyDamage</c> (301) RPC.
        /// Payload: [object_id:8 LE u64][damage:4 LE i32].
        /// Looks up the target <see cref="NetworkBehaviour"/> by object ID, retrieves
        /// its <c>HealthController</c> component (if any), and calls
        /// <c>ReceiveApplyDamage</c> to apply validated server-authorised damage.
        /// </summary>
        private void HandleApplyDamageRpc(RpcRequest request)
        {
            var p = request.Payload;
            if (p == null || p.Length < 12)
            {
                LogDebug("ApplyDamage RPC: payload too short, dropped.");
                return;
            }

            ulong objectId = (ulong)p[0]       | ((ulong)p[1] << 8)  | ((ulong)p[2] << 16) |
                             ((ulong)p[3] << 24)| ((ulong)p[4] << 32) | ((ulong)p[5] << 40) |
                             ((ulong)p[6] << 48)| ((ulong)p[7] << 56);
            int damage = p[8] | (p[9] << 8) | (p[10] << 16) | (p[11] << 24);

            if (damage <= 0)
            {
                LogDebug("ApplyDamage RPC: non-positive damage, dropped.");
                return;
            }

            var nb = Spawner?.Registry?.Get(objectId);
            if (nb == null)
            {
                LogDebug($"ApplyDamage RPC: no object with id {objectId}.");
                return;
            }

            // Look up the IDamageable interface on the target object.
            // IDamageable lives in the SDK Runtime assembly; game code (e.g. HealthController)
            // implements it. This avoids a compile-time dependency on Samples.
            var damageable = nb.GetComponentInParent<IDamageable>();
            if (damageable != null)
                damageable.ReceiveApplyDamage(damage);
            else
                LogDebug($"ApplyDamage RPC: object {objectId} has no IDamageable component.");
        }

        internal void SetLocalRoomPlayerId(string playerId)
        {
            _localPlayerStringId = playerId;
            LogDebug($"LocalRoomPlayerId set to: {playerId}");
        }

        /// <summary>
        /// Test-only helper to directly set <see cref="LocalPlayerStringId"/>.
        /// Accessible from <c>RTMPE.SDK.Tests</c> via <c>InternalsVisibleTo</c>.
        /// Do NOT call from production code.
        /// </summary>
        internal void SetLocalPlayerStringId(string id) => _localPlayerStringId = id;

        /// <summary>
        /// Wrap <paramref name="payload"/> in a <see cref="PacketType.Data"/> header
        /// and enqueue it on the network thread for transmission.
        ///
        /// Called by <c>NetworkTransform</c> and any other SDK component
        /// that needs to send a raw data payload without managing the PacketBuilder
        /// directly.  Must be called from the Unity main thread.
        /// </summary>
        /// <param name="payload">
        /// The serialised payload bytes.  A <see langword="null"/> or empty array
        /// is silently ignored.
        /// </param>
        internal void SendData(byte[] payload)
        {
            if (_networkThread == null || _packetBuilder == null) return;
            if (payload == null || payload.Length == 0) return;

            var packet = _packetBuilder.Build(
                PacketType.Data,
                PacketFlags.None,
                payload);

            EncryptAndSend(packet);
        }

        /// <summary>
        /// Build a complete wire packet (13-byte header + payload) using the
        /// connection's shared <see cref="PacketBuilder"/>. Sequence numbers are
        /// atomically assigned so the gateway sees a monotonic counter regardless
        /// of which SDK component originates the packet.
        ///
        /// Called by SpawnManager, OwnershipManager, and any other SDK component
        /// that needs to build a typed packet for transmission via <see cref="Send"/>.
        /// </summary>
        internal byte[] BuildPacket(PacketType type, PacketFlags flags, byte[] payload)
        {
            if (_packetBuilder == null)
                throw new InvalidOperationException(
                    "NetworkManager.BuildPacket: no active PacketBuilder (not connected).");
            return _packetBuilder.Build(type, flags, payload);
        }

        /// <summary>
        /// Extract the <c>sub</c> claim from a JWT without an external JSON library.
        /// The JWT claims segment is base64url-decoded to UTF-8 JSON, then a simple
        /// string search locates the <c>"sub"</c> key.
        /// Returns <see langword="null"/> if the JWT is malformed or has no sub claim.
        /// </summary>
        private static string TryExtractJwtSub(string jwt)
        {
            if (string.IsNullOrEmpty(jwt)) return null;

            var parts = jwt.Split('.');
            if (parts.Length != 3) return null;

            // Convert base64url → base64 (replace URL-safe chars, fix padding).
            var base64 = parts[1].Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "=";  break;
            }

            string json;
            try
            {
                var bytes = Convert.FromBase64String(base64);
                json = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException) { return null; }

            // Locate "sub":"<value>" — the sub claim is always a plain string.
            // This avoids a JSON library dependency for a single string extraction.
            const string subKey = "\"sub\":\"";
            var start = json.IndexOf(subKey, StringComparison.Ordinal);
            if (start < 0) return null;

            start += subKey.Length;
            var end = json.IndexOf('"', start);
            if (end < 0 || end == start) return null;

            return json.Substring(start, end - start);
        }

        private void LogDebug(string message)
        {
            if (_settings != null && _settings.enableDebugLogs)
                Debug.Log($"[RTMPE] {message}");
        }
    }


    // ── Connection state ──────────────────────────────────────────────────────

    /// <summary>Connection lifecycle states for <see cref="NetworkManager"/>.</summary>
    public enum NetworkState
    {
        /// <summary>No active connection.</summary>
        Disconnected,

        /// <summary>Handshake in progress; waiting for gateway response.</summary>
        Connecting,

        /// <summary>Authenticated and connected; not yet in a room.</summary>
        Connected,

        /// <summary>Connected and inside an active room.</summary>
        InRoom,

        /// <summary>Graceful disconnect in progress.</summary>
        Disconnecting,

        /// <summary>
        /// **N-1** — heartbeat timed out or the transport dropped, but the
        /// previous session's reconnect token is still valid.  The SDK is
        /// backing off + retrying a shortcut <c>ReconnectInit</c> handshake.
        /// On success, the state transitions straight back to
        /// <see cref="Connected"/> (or <see cref="InRoom"/> once the SDK
        /// auto-rejoins the last room).  On token exhaustion or hard failure
        /// the state falls back to <see cref="Disconnected"/>.
        /// </summary>
        Reconnecting
    }

    /// <summary>Reason codes for <see cref="NetworkManager.OnDisconnected"/>.</summary>
    public enum DisconnectReason
    {
        Unknown,
        ClientRequest,
        ServerRequest,
        Timeout,
        ConnectionLost,
        Kicked
    }
}
