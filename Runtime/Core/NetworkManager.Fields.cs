// RTMPE SDK — Runtime/Core/NetworkManager.Fields.cs
//
// Inspector-serialised fields, runtime state, telemetry counters, public properties.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Core.Aead;
using RTMPE.Core.Rpc;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
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

        // Most recently observed gateway-side per-session backpressure (0–255).
        // Updated on every HeartbeatAck.  Read from any thread via Volatile.Read
        // because production code on the receive path writes from the network
        // thread and app code reads from the main thread.
        // 0 = bucket full (no throttling); 255 = bucket empty (max throttling).
        private int _serverBackpressure;

        // Session tokens populated on SessionAck
        // (crypto_id, session keys, replay window, and atomic counters now
        // live as a cohesive bundle in _sessionKeyStore — see further down.)
        private string _jwtToken;
        private string _reconnectToken;
        /// <summary>N-8: 32-byte HMAC key for IP-migration proofs, derived alongside session keys.</summary>
        private byte[] _ipMigrationKey;
        /// <summary>
        /// 32-byte AEAD key (HKDF info suffix <c>\x03</c>) used to decrypt the
        /// SessionAck payload when the handshake negotiated
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.EncryptedSessionAck"/>
        /// and the SessionAck therefore arrives carrying
        /// <see cref="PacketFlags.Encrypted"/>.
        /// </summary>
        private byte[] _sessionAckKey;

        /// <summary>
        /// The 32-byte Ed25519 server static identity public key, captured
        /// once the handshake <see cref="PacketType.Challenge"/> signature
        /// has been verified against it.  When the gateway advertises
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.IdentitySignedJwt"/>
        /// the SessionAck handler verifies the JWT signature against this
        /// key rather than relying on out-of-band NetworkSettings key
        /// configuration.  How strong an anchor it is depends on the
        /// server-key pinning mode that captured it.
        /// </summary>
        private byte[] _serverIdentityPublicKey;

        /// <summary>
        /// Owns the per-session ARQ sequence space.  Allocates the 4-byte
        /// sub-header value emitted on the wire when
        /// <see cref="NetworkSettings.EmitArqSequence"/> is enabled and the
        /// outbound packet carries <see cref="PacketFlags.Reliable"/>.  The
        /// retransmit table will be wired in once gateway-side ACK plumbing
        /// lands; until then this instance only services sequence allocation.
        /// </summary>
        private readonly ReliableChannel _outboundReliableChannel = new ReliableChannel();

        /// <summary>
        /// Session-effective capability bitmask negotiated against the
        /// gateway during the handshake.  Equal to
        /// <c>client_caps &amp; gateway_caps</c> where <c>client_caps</c>
        /// is the SDK's advertisement (currently mirrors
        /// <see cref="NetworkSettings.EmitArqSequence"/>) and
        /// <c>gateway_caps</c> is parsed from the <c>SessionAck</c> tail.
        /// Reset to <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/>
        /// alongside the rest of the session-bound AEAD state in
        /// <c>ClearSessionData</c>.
        /// </summary>
        /// <remarks>
        /// Single-threaded access by construction.  The write happens in
        /// <c>OnSessionAck</c>, which is dispatched onto the Unity main
        /// thread by <see cref="Infrastructure.Threading.MainThreadDispatcher"/>
        /// before any handler runs.  The three read sites — <c>Send</c>
        /// (<c>NetworkManager.Connection.cs</c>), the AEAD emit
        /// gate (<c>NetworkManager.AeadPipeline.cs</c>), and the
        /// retransmit tick (<c>NetworkManager.Lifecycle.cs</c>) —
        /// also run on the main thread (caller code, <c>Update</c>, and
        /// <c>Update</c> respectively), so no atomic / volatile read is
        /// required.  Mirrors the access pattern of the other
        /// session-installed fields (<c>_jwtToken</c>,
        /// <c>_reconnectToken</c>, <c>_localPlayerId</c>).
        /// </remarks>
        private RTMPE.Core.Protocol.CapabilityFlags _negotiatedPeerCaps =
            RTMPE.Core.Protocol.CapabilityFlags.None;

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

        // RPC request correlation IDs are sourced from the CSPRNG-backed
        // RequestIdAllocator so that reply spoofing requires guessing a
        // 32-bit cryptographically-random value rather than a predictable
        // monotonic counter.

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
        // _outboundNonceCounter is now part of _sessionKeyStore (see below).

        // Per-NetworkManager outbound gameplay-sequence counter.  Two
        // concurrent NetworkManager instances (e.g. a host + a spectator
        // co-resident in a single process) used to share a static counter
        // in GameplaySequencePrefix — their sequence streams interleaved
        // and the receiver's RFC-1982 ordering buffer rejected the alien
        // values as out-of-window.  The counter is now per-instance;
        // Interlocked.Increment keeps producer-thread safety inside a
        // single manager, and the unchecked cast preserves the original
        // RFC-1982 wraparound semantics.
        private int _outboundGameplaySequenceCounter;

        // Application-level monotonic sequence layered on top of the AEAD nonce
        // counter when NetworkSettings.preserveApplicationSequence is on.  The
        // wire header's Sequence field is overwritten by the nonce counter at
        // encryption time, so the application-level sequence travels in the
        // AAD instead — receivers can deduplicate or order without decrypting
        // first.  Starts at -1L so the first Increment yields 0 to match the
        // wire Sequence convention.  Reset alongside the nonce counter on every
        // session boundary.
        private long _outboundAppSequenceCounter = -1L;

        // _lastInboundAppSequence is now part of _sessionKeyStore (see below).
        // Exposed via the public LastInboundApplicationSequence property for
        // receivers that want to surface dedup / ordering metadata above the
        // AEAD layer.

        // ── Session crypto state ────────────────────────────────────────────
        //
        // _sessionKeyStore is the cohesive bundle of every per-session AEAD
        // input: the SessionKeys (encrypt + decrypt + N-8 + sessionAck), the
        // sliding-bitmap inbound replay window, the gateway-assigned crypto
        // identifier, the Interlocked outbound nonce counter, and the
        // monotonic Interlocked-CAS last-inbound-app-sequence.  Wrapping
        // these five pieces of state into one object makes the lifecycle
        // invariant explicit: either ALL of them are valid (active session)
        // or ALL are reset (no session) — mixing the two is the failure
        // mode that produces AEAD nonce reuse.  See
        // Runtime/Core/Aead/SessionKeyStore.cs for the API + threading
        // contract.
        private readonly SessionKeyStore _sessionKeyStore = new SessionKeyStore();

        // RpcReplayBuffer owns the CAS re-entry guard (_replayInProgress),
        // the pending-live-RPC queue (_pendingLiveRpcs), the running byte
        // counter, and the dropped-count atomic. Centralising these in one
        // class makes the ordering invariant (replay drains BEFORE live RPCs
        // from the same delivery window) reviewable in isolation. See
        // Runtime/Core/Rpc/RpcReplayBuffer.cs for the threading + caps.
        private readonly RpcReplayBuffer _rpcReplayBuffer = new RpcReplayBuffer();

        private HandshakeHandler _handshakeHandler;
        private PacketBuilder    _packetBuilder;

        // Persistent server-static-key pin store, used by the Trust-On-First-Use
        // mode of NetworkSettings.serverPinningMode.  Lazily initialised to a
        // PlayerPrefsPinStore on first read; tests inject a custom store via
        // SetPinStore() before Connect() to avoid touching the player's prefs.
        private IServerKeyPinStore _pinStore;

        // Channel-binding context for the current handshake.
        //
       // Holds the exact bytes the client emitted as the HandshakeInit
        // payload (the ChaCha20-Poly1305 envelope around the API key).  The
        // gateway hashes the same bytes and folds the SHA-256 into the
        // transcript it signs, so the SDK must keep them around between
        // Round 1 send and Round 1 reply receipt to recompute the matching
        // transcript.  Null in the reconnect flow (the absent-sentinel is
        // used instead — see HandshakeHandler.ValidateChallenge).
        private byte[]           _lastHandshakeInitCiphertext;
        private HeartbeatManager _heartbeatManager;

        // Room management
        private RoomManager  _roomManager;

        // Lobby management
        private LobbyManager _lobbyManager;

        // Matchmaking
        private MatchmakingManager _matchmakingManager;

        // Spawn management
        private SpawnManager _spawnManager;

        // Scene-event serialization.  Unity's SceneManager.sceneUnloaded and
        // sceneLoaded callbacks both run on the main thread today, so a true
        // data race is not the concern; the concern is reentrancy ordering.
        // A scripted single-mode load fires sceneUnloaded for the previous
        // scene immediately followed by sceneLoaded for the new scene, and a
        // long-running PruneDestroyed (e.g. behind a user-supplied
        // INetworkObjectPool destroy callback that yields back to coroutines)
        // could interleave with RecreateRoomAndSpawnManagers triggered by
        // reconnect logic.  Holding the lock around both handlers establishes
        // a documented ordering: sceneUnloaded → Prune → sceneLoaded → Prune
        // (and never overlapping with a Recreate from another code path).
        // The handlers are idempotent so the lock is correctness theatre, not
        // load-bearing — but documenting the invariant is the point.
        private readonly object _sceneTransitionLock = new object();

        // Cached heartbeat send callback — avoids per-frame closure allocation
        // at the call site in Update().
        private System.Action<byte[]> _sendPacketCallback;

        // Cached delegate for SendVariableUpdate — method-group-to-delegate
        // conversion allocates on every call unless stored in a field.
        // GC Round 2 (2026-05-02): now bound to the (byte[], int) overload
        // so callers can pass an oversized cached/pooled buffer + length.
        private System.Action<byte[], int> _sendVariableUpdateDelegate;

        // GC Round 3 (2026-05-02) — per-direction 12-byte AEAD nonce
        // scratch buffers reused across packets.  Replaces the per-packet
        // `new byte[12]` that AeadNonce.Build allocated.  Two separate
        // fields keep the inbound and outbound paths re-entrancy-safe even
        // if a future change runs them concurrently; both are written
        // before being read inside the same Seal/Open call so a stale-read
        // from the prior packet is never observable.  Sized exactly to
        // AeadNonce.Size — the wire-format invariant is enforced at every
        // call site that consumes them.
        private readonly byte[] _outboundNonceScratch = new byte[RTMPE.Core.Aead.AeadNonce.Size];
        private readonly byte[] _inboundNonceScratch  = new byte[RTMPE.Core.Aead.AeadNonce.Size];

        // Per-direction AAD scratch buffer reused across packets.  16 bytes
        // covers the maximum AAD size (2 baseline + 4 app_seq + 4
        // gameplay_seq = 10 bytes today) with headroom for future
        // sub-headers.  ChaCha20Poly1305Impl.Seal/OpenInto accept
        // (aad, aadOffset, aadLength) so the meaningful prefix is signalled
        // explicitly — the trailing slack bytes never participate in the
        // Poly1305 computation.
        //
        // Two separate fields keep the inbound and outbound paths
        // re-entrancy-safe even if a future change runs them concurrently;
        // both are fully overwritten before being passed to Seal/Open
        // inside the same call so a stale read from the prior packet is
        // never observable.  Mirrors the threading invariant of the
        // nonce scratch fields above.
        private const int AeadAadScratchCapacity = 16;
        private readonly byte[] _outboundAadScratch = new byte[AeadAadScratchCapacity];
        private readonly byte[] _inboundAadScratch  = new byte[AeadAadScratchCapacity];

        // Default-fallback tick interval used until NetworkSettings has been
        // resolved (e.g. very early Awake reentrancy, edit-mode tests with no
        // settings asset).  Matches the SDK's historical 30 Hz canonical rate.
        private const float DefaultVariableFlushInterval = 1f / 30f;

        // Accumulator for the NetworkVariable flush loop.  Cadence is driven
        // by _variableFlushInterval below, which is resolved from
        // NetworkSettings.tickRate at initialisation so tick-rate-sensitive
        // titles (FPS / fighting / mobile RPG) can change cadence without
        // forking the SDK.
        private float _variableFlushAccum;

        // Resolved per-tick interval (seconds).  Initialised to the default
        // so any access prior to InitialiseNetwork still sees a coherent
        // value (the field, not a const, is read by every tick site so a
        // later settings load propagates immediately).
        private float _variableFlushInterval = DefaultVariableFlushInterval;

        /// <summary>
        /// Fixed simulation step (seconds) used by the global tick loop and by
        /// CSP replay so the rollback path observes the same delta-time the
        /// original prediction did.  Resolved from
        /// <see cref="NetworkSettings.TickInterval"/>; defaults to 1/30 s
        /// when no settings are configured.
        /// </summary>
        public float FixedTickInterval => _variableFlushInterval;

        // Hard cap on the number of fixed ticks a single Update() may catch
        // up after a long hitch.  Without this guard a 30 s editor pause
        // would queue 900 ticks of work into one frame and produce a
        // multi-second stutter on resume.  The cap drops the "extra" time
        // and resyncs against wall-clock — the server is the source of truth
        // for late-binding state, so a few lost ticks are recoverable on
        // the next reconciliation.
        private const int MaxTicksPerFrame = 8;

        // 5-second accumulator for the periodic RPC callback purge sweep.
        private float _rpcPurgeAccum;
        private const float RpcPurgeInterval = 5f;

        // Accumulator for the periodic prune of SpawnManager's pending-
        // despawn tracker.  Cadence is at the same order as the tracker's
        // TTL (5 s) so a stale entry is never more than one period past
        // its expiry — bounding the worst-case occupancy without scheduling
        // overhead.
        private float _pendingDespawnPruneAccum;
        private const float PendingDespawnPruneInterval = 5f;

        // ── Telemetry counters (Feature: Network Debugger window) ──────────────
        //
       // Atomic 64-bit counters incremented on every wire-level send and receive.
        // Reads on 64-bit platforms (the only platforms Unity supports for
        // dedicated multiplayer titles in 2026) are atomic by construction; we
        // additionally use Interlocked.Read in the public accessors for ARM64
        // where some implementations historically had relaxed ordering on
        // unaligned long reads.  The increment cost is a single LOCK XADD —
        // measured under 5 ns on commodity x64, well below any per-packet
        // budget.  No allocations.
        //
       // The counters are read by the Editor-only Network Debugger window
        // and (optionally) by user telemetry sinks; they are not part of any
        // wire protocol.
        private long _packetsOut;
        private long _bytesOut;
        private long _packetsIn;
        private long _bytesIn;

        // ── Properties ─────────────────────────────────────────────────────────

        /// <summary>Current network state.</summary>
        public NetworkState State => _state;

        /// <summary>True when connected to the gateway (Connected or InRoom).</summary>
        public bool IsConnected => _state == NetworkState.Connected
                                || _state == NetworkState.InRoom;

        /// <summary>True when connected and inside a room.</summary>
        public bool IsInRoom => _state == NetworkState.InRoom;

        /// <summary>
        /// Gateway-reported per-session backpressure, taken from the most
        /// recently received <see cref="PacketType.HeartbeatAck"/> payload.
        /// <para>
        /// 0 = the gateway's per-session token bucket is full — no throttling.
        /// 255 = bucket empty — the gateway is at capacity and inbound packets
        /// from this session are at risk of being silently dropped.
        /// </para>
        /// Surfaced for adaptive send-rate logic and observability dashboards.
        /// Apps that send at a fixed cadence may ignore this; high-frequency
        /// senders should reduce their outbound rate as the value approaches
        /// 255 to avoid invisible packet loss at the gateway boundary.
        /// </summary>
        public byte ServerBackpressure
            => (byte)System.Threading.Volatile.Read(ref _serverBackpressure);

        /// <summary>Settings asset in use (may be the built-in default).</summary>
        public NetworkSettings Settings => _settings;

        /// <summary>
        /// Persistent server-static-key pin store used in Trust-On-First-Use
        /// mode.  Lazily initialised to a <see cref="PlayerPrefsPinStore"/>;
        /// tests and games with custom storage requirements may replace it
        /// via <see cref="SetPinStore"/> before calling <see cref="Connect"/>.
        /// </summary>
        public IServerKeyPinStore PinStore
        {
            get
            {
                if (_pinStore == null) _pinStore = new PlayerPrefsPinStore();
                return _pinStore;
            }
        }

        /// <summary>
        /// Inject a custom <see cref="IServerKeyPinStore"/>.  Must be called
        /// before <see cref="Connect"/> for the new store to be consulted on
        /// the upcoming Challenge.  Pass <see langword="null"/> to revert to
        /// the default <see cref="PlayerPrefsPinStore"/>.
        /// </summary>
        public void SetPinStore(IServerKeyPinStore store) => _pinStore = store;

        /// <summary>
        /// Forget the persisted Trust-On-First-Use pin for the configured
        /// server endpoint.  Intended for legitimate server-rotation flows:
        /// the operator clears the old pin, the next connect re-runs TOFU
        /// against the new key.
        /// </summary>
        public void ClearPinnedKey()
        {
            if (_settings == null) return;
            var endpoint = ServerKeyPinning.CanonicalEndpoint(
                _settings.serverHost, _settings.serverPort);
            PinStore.Clear(endpoint);
        }

        /// <summary>Local player ID (gateway session ID) — valid after SessionAck.</summary>
        public ulong LocalPlayerId => _localPlayerId;

        /// <summary>Current room ID — valid after RoomJoin.</summary>
        public ulong CurrentRoomId => _currentRoomId;

        // ── Telemetry counters (read-only) ─────────────────────────────────────
        //
       // Snapshot accessors return the current counter value.  Subtract two
        // sampled values across a known interval to compute a rate.  The
        // Network Debugger Editor window does this at ~250 ms cadence to
        // render packets-per-second / bytes-per-second.

        /// <summary>Total wire-level packets sent since process start.</summary>
        public long PacketsOutCounter =>
            System.Threading.Interlocked.Read(ref _packetsOut);

        /// <summary>Total wire-level bytes sent since process start.</summary>
        public long BytesOutCounter =>
            System.Threading.Interlocked.Read(ref _bytesOut);

        /// <summary>Total wire-level packets received since process start.</summary>
        public long PacketsInCounter =>
            System.Threading.Interlocked.Read(ref _packetsIn);

        /// <summary>Total wire-level bytes received since process start.</summary>
        public long BytesInCounter =>
            System.Threading.Interlocked.Read(ref _bytesIn);

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
        /// </summary>
        /// <remarks>
        /// Thread safety: <see cref="LocalPlayer"/> MUST be accessed from the
        /// Unity main thread.  Transport callbacks are marshalled onto the
        /// main thread by <see cref="Infrastructure.Threading.MainThreadDispatcher"/>
        /// before they reach this getter, and gameplay code is single-threaded
        /// by Unity convention.  Earlier revisions used
        /// <see cref="System.Threading.Interlocked.CompareExchange{T}(ref T,T,T)"/>
        /// to harden against an inadvertent off-thread caller; the CAS pattern
        /// allocated a throwaway <see cref="LocalPlayerContext"/> on every
        /// getter call until the field was populated and re-allocated again
        /// on every CAS-loser path — both costs incurred on the hot main
        /// thread for a contract the rest of the SDK does not actually
        /// permit being violated.
        /// </remarks>
        public LocalPlayerContext LocalPlayer
        {
            get
            {
                if (_localPlayer == null)
                    _localPlayer = new LocalPlayerContext(() => _roomManager, () => _localPlayerStringId);
                return _localPlayer;
            }
        }
        private LocalPlayerContext _localPlayer;

        /// <summary>Spawn management API — spawn, despawn, prefab registry, owner-leave handling.</summary>
        public SpawnManager Spawner => _spawnManager;

        /// <summary>
        /// Internal accessor for editor diagnostics tooling.  The
        /// NetworkDebuggerWindow uses this to enumerate the registry without
        /// reflecting on private fields, which is brittle across renames.
        /// Not part of the public API — gameplay code should use
        /// <see cref="Spawner"/>.
        /// </summary>
        internal SpawnManager SpawnManagerInternal => _spawnManager;

        /// <summary>
        /// Networked-scene management façade.  Drives room-wide scene
        /// transitions through the reserved <c>__scene</c> custom property
        /// and surfaces <c>SceneLoaded</c> (0x2F) readiness aggregation.
        /// </summary>
        /// <remarks>
        /// Thread safety: same main-thread-only contract as
        /// <see cref="LocalPlayer"/>; see that property's remarks for why
        /// the previous <see cref="System.Threading.Interlocked.CompareExchange{T}(ref T,T,T)"/>
        /// pattern was retired.
        /// </remarks>
        public NetworkSceneManager Scene
        {
            get
            {
                if (_roomManager == null) return null;
                if (_sceneManager == null)
                    _sceneManager = new NetworkSceneManager(() => _roomManager);
                return _sceneManager;
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

    }
}
