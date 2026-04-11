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
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Protocol;
using RTMPE.Rooms;

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
        private ulong  _localPlayerId;
        private ulong  _currentRoomId;

        // Session crypto state (Week 9)
        private HandshakeHandler _handshakeHandler;
        private SessionKeys      _sessionKeys;
        private PacketBuilder    _packetBuilder;
        private HeartbeatManager _heartbeatManager;

        // Room management (Week 14)
        private RoomManager _roomManager;

        // M-19 fix: cache the heartbeat send callback to avoid per-frame lambda
        // allocation. The lambda `packet => _networkThread?.Send(packet)` creates
        // a new closure object every Update() call (60+ Hz). Caching it once in
        // Start() eliminates the allocation entirely.
        private System.Action<byte[]> _sendPacketCallback;

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

        /// <summary>Local player ID — valid after SessionAck.</summary>
        public ulong LocalPlayerId => _localPlayerId;

        /// <summary>Current room ID — valid after RoomJoin.</summary>
        public ulong CurrentRoomId => _currentRoomId;

        /// <summary>Room management API — create, join, leave, and list rooms.</summary>
        public RoomManager Rooms => _roomManager;

        /// <summary>JWT bearer token — valid after SessionAck. Use for Room Service calls.</summary>
        public string JwtToken => _jwtToken;

        /// <summary>Last measured round-trip time in milliseconds (-1 if not yet available).</summary>
        public float LastRttMs { get; private set; } = -1f;

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

            InitialiseNetwork();
        }

        private void Start()
        {
            // Warm the MainThreadDispatcher singleton HERE (main thread, before any
            // background threads are started) so the first Enqueue() call is free
            // of the one-time GameObject allocation cost.
            _dispatcher = MainThreadDispatcher.Instance;

            // M-19 fix: cache the heartbeat send callback once so Update() does
            // not allocate a new closure object every frame.
            _sendPacketCallback = packet => _networkThread?.Send(packet);
        }

        private void Update()
        {
            // Drive the heartbeat tick each frame. Reuse the cached callback to
            // avoid per-frame heap allocation (M-19 fix).
            _heartbeatManager?.Tick(_sendPacketCallback);
        }

        private void OnDestroy()
        {
            lock (_instLock)
            {
                if (_instance == this) _instance = null;
            }

            StopAllCoroutines();
            Cleanup();
        }

        private void OnApplicationQuit()
        {
            _applicationIsQuitting = true;
            Cleanup();
        }

        // ── Initialisation & teardown ──────────────────────────────────────────

        private void InitialiseNetwork()
        {
            _transport = new UdpTransport(
                _settings.serverHost,
                _settings.serverPort,
                _settings.sendBufferBytes,
                _settings.receiveBufferBytes);

            _networkThread = new NetworkThread(_transport, _settings.networkThreadBufferBytes);
            _networkThread.OnPacketReceived += HandlePacketReceived;
            _networkThread.OnError          += HandleTransportError;

            _packetBuilder = new PacketBuilder();

            // Week 14: Room API manager. Receives the shared PacketBuilder for
            // sequence numbering and a delegate to send fully-built packets.
            _roomManager = new RoomManager(
                _packetBuilder,
                packet => _networkThread?.SendOwned(packet),
                () => _state);

            // Wire room events to update NetworkManager state.
            _roomManager.OnRoomJoined += OnRoomManagerJoined;
            _roomManager.OnRoomLeft   += OnRoomManagerLeft;
            _roomManager.OnRoomCreated += OnRoomManagerCreated;
        }

        private void Cleanup()
        {
            _heartbeatManager?.Stop();
            _heartbeatManager = null;
            _handshakeHandler?.Dispose();  // C-1 fix: zero key material before GC can observe it
            _handshakeHandler = null;
            _sessionKeys?.Dispose();       // C-1 fix: zero session keys before GC can observe it
            _sessionKeys      = null;
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

            // CR-1 fix (Week 14): Recreate RoomManager with the new PacketBuilder so
            // room operations share the same sequence counter as handshake/heartbeat.
            // Without this, reconnections cause two independent counters on the wire
            // — a protocol violation that may trigger gateway replay protection.
            _roomManager = new RoomManager(
                _packetBuilder,
                packet => _networkThread?.SendOwned(packet),
                () => _state);
            _roomManager.OnRoomJoined  += OnRoomManagerJoined;
            _roomManager.OnRoomLeft    += OnRoomManagerLeft;
            _roomManager.OnRoomCreated += OnRoomManagerCreated;

            // Create a fresh handshake handler (generates a new X25519 ephemeral keypair).
            _handshakeHandler = new HandshakeHandler();

            _networkThread.Start();

            // Kick off the async handshake-init coroutine — it waits for the transport
            // to be bound (LocalEndPoint != null) before building and sending the packet.
            _connectCoroutine = StartCoroutine(HandshakeInitCoroutine(apiKey));

            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
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
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Send: cannot send while not connected.");
                return;
            }

            if (data == null || data.Length == 0) return;

            _networkThread?.Send(data, reliable);
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

            var packetType = (PacketType)data[PacketProtocol.OFFSET_TYPE];

            LogDebug($"Received {packetType} ({data.Length} B).");

            switch (packetType)
            {
                // ── W6 ECDH 4-step handshake ────────────────────────────────
                case PacketType.Challenge:    OnChallenge(data);    break;
                case PacketType.SessionAck:   OnSessionAck(data);   break;

                // ── Legacy W3 handshake (backward compatibility) ─────────────
                case PacketType.HandshakeAck: OnHandshakeAck(data); break;

                // ── Keep-alive ───────────────────────────────────────────────
                case PacketType.HeartbeatAck: OnHeartbeatAck(data); break;

                // ── Room / session (Week 14) ─────────────────────────────────
                case PacketType.RoomCreate:
                case PacketType.RoomJoin:
                case PacketType.RoomLeave:
                case PacketType.RoomList:
                    OnRoomPacket(packetType, data);
                    break;

                case PacketType.Disconnect:      OnServerDisconnect(data); break;
                case PacketType.Data:
                case PacketType.StateSync:        OnDataReceived?.Invoke(data); break;

                default:
                    LogDebug($"No handler for packet type 0x{(byte)packetType:X2}.");
                    break;
            }
        }

        // ── W6 Handshake packet handlers ───────────────────────────────────────

        /// <summary>
        /// Handle an incoming <c>Challenge</c> (0x06) from the server.
        ///
        /// 1. Parse 128-byte payload: [ephemeral:32][static:32][sig:64].
        /// 2. Verify Ed25519 signature (H4 fix) — reject on failure.
        /// 3. Derive session keys via X25519 ECDH + HKDF-SHA256 (G-H1).
        /// 4. Send <c>HandshakeResponse</c> containing the client public key.
        /// </summary>
        private void OnChallenge(byte[] data)
        {
            // Guard: only process Challenge while we are actively connecting.
            if (_state != NetworkState.Connecting) return;
            if (_handshakeHandler == null)         return;

            var payload = PacketParser.ExtractPayload(data);

            byte[] pinnedKey = null;
            try { pinnedKey = _settings?.PinnedServerPublicKeyBytes; }
            catch (Exception ex)
            {
                Debug.LogError($"[RTMPE] Invalid pinnedServerPublicKeyHex in settings: {ex.Message}");
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

            // Derive directional session keys (G-H1 fix: two independent keys).
            _sessionKeys = _handshakeHandler.DeriveSessionKeys();
            if (_sessionKeys == null)
            {
                Debug.LogError("[RTMPE] ECDH key derivation failed (degenerate shared secret). Disconnecting.");
                return;
            }

            // Send the client's X25519 ephemeral public key to the server.
            // M-13 fix: use SendOwned — response is a freshly allocated array that
            // we will not reuse, so the copy inside Send() is unnecessary here.
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
            if (_state != NetworkState.Connecting) return;

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

            LogDebug($"SessionAck received: crypto_id={cryptoId}, jwt_len={jwtToken?.Length ?? 0}");

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }

            TransitionTo(NetworkState.Connected);

            // Start keep-alive heartbeat.
            // H-1 fix: pass _packetBuilder so heartbeat packets share the same sequence
            // counter as all other outbound packets (prevents nonce reuse under AEAD).
            _heartbeatManager = new HeartbeatManager(_settings.heartbeatIntervalMs, _packetBuilder);
            _heartbeatManager.OnRttUpdated     += rtt => { LastRttMs = rtt; OnRttUpdated?.Invoke(rtt); };
            _heartbeatManager.OnHeartbeatTimeout += OnHeartbeatTimeout;
            _heartbeatManager.Start();
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
            // L-SDK7 fix: transition through Disconnecting first so that
            // listeners observing state changes see the full lifecycle
            // (Connected → Disconnecting → Disconnected), consistent with
            // the explicit Disconnect() path.
            TransitionTo(NetworkState.Disconnecting);
            _heartbeatManager?.Stop();
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ConnectionLost);
        }

        /// <summary>
        /// Route all room packets (0x20–0x23) to the RoomManager.
        /// </summary>
        private void OnRoomPacket(PacketType type, byte[] data)
        {
            if (_roomManager == null) return;
            var payload = PacketParser.ExtractPayload(data);
            _roomManager.HandleRoomPacket(type, payload);
        }

        /// <summary>Week 14: RoomManager fires OnRoomCreated → transition to InRoom.</summary>
        private void OnRoomManagerCreated(RoomInfo room)
        {
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
#pragma warning disable CS0618 // Legacy event — users should migrate to RoomManager events
                OnJoinedRoom?.Invoke(0);
#pragma warning restore CS0618
            }
        }

        /// <summary>Week 14: RoomManager fires OnRoomJoined → transition to InRoom.</summary>
        private void OnRoomManagerJoined(RoomInfo room)
        {
            if (_state == NetworkState.Connected)
            {
                TransitionTo(NetworkState.InRoom);
#pragma warning disable CS0618
                OnJoinedRoom?.Invoke(0);
#pragma warning restore CS0618
            }
        }

        /// <summary>Week 14: RoomManager fires OnRoomLeft → transition back to Connected.</summary>
        private void OnRoomManagerLeft()
        {
            if (_state == NetworkState.InRoom)
            {
                TransitionTo(NetworkState.Connected);
#pragma warning disable CS0618
                OnLeftRoom?.Invoke(0);
#pragma warning restore CS0618
            }
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
                // M-18 fix: abort the connection in release builds rather than
                // transmitting the API key in plaintext.  The plaintext path is a
                // developer convenience that MUST NOT ship to production.
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

            // M-13 fix: use SendOwned — packet is a freshly built array we do not
            // reuse after the send call (LogDebug only reads packet.Length, which
            // is safe since the array is still referenced by the send queue).
            var packet = _packetBuilder.BuildHandshakeInit(encryptedPayload);
            _networkThread.SendOwned(packet);
            LogDebug($"HandshakeInit sent ({packet.Length} B).");
        }

        private void SendDisconnect()
        {
            if (_packetBuilder == null) return;
            // M-13 fix: BuildDisconnect returns a fresh array; use SendOwned.
            var packet = _packetBuilder.BuildDisconnect();
            _networkThread?.SendOwned(packet);
            LogDebug("Sent Disconnect packet.");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private IEnumerator ConnectionTimeoutRoutine()
        {
            yield return new WaitForSeconds(_settings.connectionTimeoutMs / 1_000f);

            if (_state == NetworkState.Connecting)
            {
                OnConnectionFailed?.Invoke("Connection timeout.");
                _networkThread?.Stop();
                _heartbeatManager?.Stop();
                ClearSessionData();
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }

            _timeoutCoroutine = null;
        }

        private void ClearSessionData()
        {
            _cryptoId         = 0;
            _jwtToken         = null;
            _reconnectToken   = null;
            _localPlayerId    = 0;
            _currentRoomId    = 0;
            _roomManager?.ClearState();
            _handshakeHandler?.Dispose();  // C-1 fix: zero key material before GC can observe it
            _handshakeHandler = null;
            _sessionKeys?.Dispose();       // C-1 fix: zero session keys before GC can observe it
            _sessionKeys      = null;
            LastRttMs         = -1f;
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
        Disconnecting
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
