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
        private NetworkState  _state = NetworkState.Disconnected;
        private NetworkThread _networkThread;
        private NetworkTransport _transport;
        private MainThreadDispatcher _dispatcher;
        private Coroutine _timeoutCoroutine;

        // Session tokens populated on SessionAck (Week 9+)
        private uint   _cryptoId;
        private string _jwtToken;
        private string _reconnectToken;
        private ulong  _localPlayerId;
        private ulong  _currentRoomId;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current network state.</summary>
        public NetworkState State => _state;

        /// <summary>True when connected to the gateway (Connected or InRoom).</summary>
        public bool IsConnected => _state == NetworkState.Connected
                                || _state == NetworkState.InRoom;

        /// <summary>True when connected and inside a room.</summary>
        /// <remarks>
        /// State is the authoritative source for "are we in a room". <c>CurrentRoomId</c>
        /// is populated in Week 9 when <c>OnRoomJoinAck</c> parses the payload; do not
        /// gate this property on <c>CurrentRoomId != 0</c> or it will always return
        /// <see langword="false"/> until that parsing is implemented.
        /// </remarks>
        public bool IsInRoom => _state == NetworkState.InRoom;

        /// <summary>Settings asset in use (may be the built-in default).</summary>
        public NetworkSettings Settings => _settings;

        /// <summary>Local player ID — valid after SessionAck (Week 9+).</summary>
        public ulong LocalPlayerId => _localPlayerId;

        /// <summary>Current room ID — valid after RoomJoin.</summary>
        public ulong CurrentRoomId => _currentRoomId;

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
        public event Action<ulong> OnJoinedRoom;

        /// <summary>Fired when the local player leaves a room.</summary>
        public event Action<ulong> OnLeftRoom;

        /// <summary>
        /// Fired when a <see cref="PacketType.Data"/> or <see cref="PacketType.StateSync"/>
        /// packet is received. Argument is the full raw packet (header + payload).
        /// </summary>
        public event Action<byte[]> OnDataReceived;

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
        }

        private void Cleanup()
        {
            // Dispose stops the thread and disconnects the transport.
            _networkThread?.Dispose();
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

            _networkThread.Start();

            // TODO (Week 9): replace legacy stub with full ECDH 4-step flow:
            //   HandshakeInit → Challenge → HandshakeResponse → SessionAck
            SendHandshakeInit(apiKey);

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

            // Capture connected status BEFORE transitioning — IsConnected checks State,
            // and after we enter Disconnecting that check would return false.
            bool wasConnected = IsConnected;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }

            // Announce that teardown has begun.  Listeners can react (e.g. show UI).
            TransitionTo(NetworkState.Disconnecting);

            if (wasConnected)
                SendDisconnect();

            // NOTE: Stop() joins the background thread with a 2-second timeout.
            // In the worst case (thread stuck in a blocking socket call), this
            // blocks the Unity main thread for up to 2 seconds.
            // Week 10 will replace this with cooperative CancellationToken shutdown.
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ClientRequest);
        }

        /// <summary>
        /// Enqueue a raw packet for transmission. Thread-safe.
        /// </summary>
        /// <param name="data">Raw bytes to send (header + payload).</param>
        /// <param name="reliable">
        /// When <see langword="true"/>, the packet is marked with
        /// <see cref="PacketFlags.Reliable"/> — used by KCP transport (Week 10+).
        /// </param>
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

        // ── Receive path (raised on network thread → marshalled to main thread) ─

        private void HandlePacketReceived(byte[] data)
        {
            // Marshal to main thread — Unity APIs are not safe to call here.
            _dispatcher?.Enqueue(() => ProcessPacket(data));
        }

        private void ProcessPacket(byte[] data)
        {
            // ─ Minimum length check ─────────────────────────────────────────────
            if (data == null || data.Length < PacketProtocol.HEADER_SIZE)
            {
                Debug.LogWarning("[RTMPE] Dropped packet: too short to contain a valid header.");
                return;
            }

            // ─ Magic validation (bytes 0-1, little-endian u16) ──────────────────
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

            // ─ Version validation (byte 2) ───────────────────────────────────────
            if (data[PacketProtocol.OFFSET_VERSION] != PacketProtocol.VERSION)
            {
                Debug.LogWarning(
                    $"[RTMPE] Dropped packet: unsupported protocol version " +
                    $"{data[PacketProtocol.OFFSET_VERSION]} (expected {PacketProtocol.VERSION}).");
                return;
            }

            var packetType = (PacketType)data[PacketProtocol.OFFSET_TYPE];
            // var flags   = (PacketFlags)data[PacketProtocol.OFFSET_FLAGS];  // Week 9+

            LogDebug($"Received {packetType} ({data.Length} B).");

            switch (packetType)
            {
                case PacketType.HandshakeAck:    OnHandshakeAck(data);    break;
                case PacketType.SessionAck:      OnSessionAck(data);      break;
                case PacketType.HeartbeatAck:    OnHeartbeatAck(data);    break;
                case PacketType.RoomJoin:        OnRoomJoinAck(data);     break;
                case PacketType.Disconnect:      OnServerDisconnect(data); break;
                case PacketType.Data:
                case PacketType.StateSync:       OnDataReceived?.Invoke(data); break;

                // TODO (Week 9): add Challenge + HandshakeResponse handlers for the
                // full ECDH 4-step flow. Challenge carries [ephemeral:32][static:32][sig:64]
                // and must be verified with Ed25519 before proceeding.
                case PacketType.Challenge:
                case PacketType.HandshakeResponse:
                    LogDebug($"Packet type {packetType} requires Week 9 ECDH handler — stub.");
                    break;

                default:
                    LogDebug($"No handler registered for packet type 0x{(byte)packetType:X2}.");
                    break;
            }
        }

        // ── Packet handlers (main thread) ──────────────────────────────────────

        // Legacy Handshake (Week 3 compat path — kept for backward compatibility)
        private void OnHandshakeAck(byte[] _)
        {
            // Guard: ignore stale ACKs that arrive after a timeout has already
            // transitioned us away from Connecting (U-C1 fix — no ghost Connected).
            if (_state != NetworkState.Connecting) return;

            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            TransitionTo(NetworkState.Connected);
        }

        // Production 4-step ECDH path (Week 9+ parse of JWT / reconnect token)
        private void OnSessionAck(byte[] _)
        {
            // Guard: ignore stale ACKs that arrive after timeout/disconnect (U-C1 fix).
            if (_state != NetworkState.Connecting) return;

            // TODO (Week 9): deserialise [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][rc:N]
            if (_timeoutCoroutine != null)
            {
                StopCoroutine(_timeoutCoroutine);
                _timeoutCoroutine = null;
            }
            TransitionTo(NetworkState.Connected);
        }

        private void OnHeartbeatAck(byte[] _)
        {
            // TODO (Week 9): record last-seen timestamp; compute RTT.
        }

        private void OnRoomJoinAck(byte[] _)
        {
            // Guard: only transition from Connected; ignore stale packets (U-C1 fix).
            if (_state != NetworkState.Connected) return;

            // TODO (Week 9): parse room ID from payload.
            TransitionTo(NetworkState.InRoom);
            OnJoinedRoom?.Invoke(_currentRoomId);
        }

        private void OnServerDisconnect(byte[] _)
        {
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, DisconnectReason.ServerRequest);
        }

        // ── Transport error path (raised on network thread) ────────────────────

        private void HandleTransportError(Exception ex)
        {
            Debug.LogError($"[RTMPE] Transport error: {ex.Message}");

            _dispatcher?.Enqueue(() =>
            {
                // Capture state BEFORE any transition so event semantics are correct:
                // OnConnectionFailed is for failed *connection attempts* (Connecting state).
                // A transport error while Connected/InRoom is a connection-loss — only
                // OnDisconnected (fired by TransitionTo) is appropriate in that case.
                bool wasConnecting = _state == NetworkState.Connecting;

                if (_timeoutCoroutine != null)
                {
                    StopCoroutine(_timeoutCoroutine);
                    _timeoutCoroutine = null;
                }

                // Fire OnConnectionFailed BEFORE transitioning state — matching the
                // ordering in ConnectionTimeoutRoutine so that handlers always see
                // NetworkState.Connecting when OnConnectionFailed fires, regardless
                // of whether the failure was a timeout or a transport error.
                if (wasConnecting)
                    OnConnectionFailed?.Invoke(ex.Message);

                // Join the background thread (Stop() is a no-op if already exited).
                // OnServerDisconnect and Disconnect() both call Stop() before state
                // transitions; HandleTransportError must be consistent.
                _networkThread?.Stop();

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
                    // Fire with the reason supplied by the caller — never silently
                    // default to Unknown when the actual cause is known.
                    OnDisconnected?.Invoke(reason);
                    break;
            }
        }

        // ── Outbound stubs (Week 8 — packet serialisation added in Week 9) ──────

        private void SendHandshakeInit(string apiKey)
        {
            // Week 9: serialise PacketType.HandshakeInit + [api_key_len:2 LE][api_key:N].
            LogDebug("SendHandshakeInit — stub (Week 9 will serialise real packet).");
        }

        private void SendDisconnect()
        {
            // Week 9: serialise PacketType.Disconnect packet and flush before Stop().
            LogDebug("SendDisconnect — stub (Week 9 will serialise real packet).");
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private IEnumerator ConnectionTimeoutRoutine()
        {
            yield return new WaitForSeconds(_settings.connectionTimeoutMs / 1_000f);

            if (_state == NetworkState.Connecting)
            {
                OnConnectionFailed?.Invoke("Connection timeout.");
                _networkThread?.Stop();
                ClearSessionData();
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }

            _timeoutCoroutine = null;
        }

        private void ClearSessionData()
        {
            _cryptoId       = 0;
            _jwtToken       = null;
            _reconnectToken = null;
            _localPlayerId  = 0;
            _currentRoomId  = 0;
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
