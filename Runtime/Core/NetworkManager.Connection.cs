// RTMPE SDK — Runtime/Core/NetworkManager.Connection.cs
//
// Connect/Reconnect/Disconnect, Cleanup, InitialiseNetwork, public API.
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
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
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
                    RtmpeLog.Error(
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

            _networkThread = new NetworkThread(
                _transport,
                _settings.networkThreadBufferBytes,
                _settings.sendQueueMaxItems);
            _networkThread.OnPacketReceivedRented += HandlePacketReceivedRented;
            _networkThread.OnError                += HandleTransportError;

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
            _sessionKeyStore.DisposeKeys();  // Zero session keys before GC can observe it
            // OnDestroy / OnApplicationQuit reach Cleanup directly without
            // routing through ClearSessionData, so the HKDF-derived auxiliary
            // keys must be zeroed here too — otherwise mid-session app quit
            // or scene unload leaves the key bytes on the managed heap until
            // the next GC cycle.  Match the explicit Array.Clear pattern
            // used by ClearSessionData.
            if (_ipMigrationKey != null)
            {
                Array.Clear(_ipMigrationKey, 0, _ipMigrationKey.Length);
                _ipMigrationKey = null;
            }
            if (_sessionAckKey != null)
            {
                Array.Clear(_sessionAckKey, 0, _sessionAckKey.Length);
                _sessionAckKey = null;
            }
            _sessionKeyStore.ResetReplayWindow();
            // Detach scene manager BEFORE tearing down the network thread so
            // any in-flight SceneLoaded callbacks don't fire into a disposed manager.
            _sceneManager?.Dispose();
            _sceneManager = null;
            // Unsubscribe before dispose to break delegate references.
            if (_networkThread != null)
            {
                _networkThread.OnPacketReceivedRented -= HandlePacketReceivedRented;
                _networkThread.OnError                -= HandleTransportError;
                _networkThread.Dispose();
            }
            _networkThread = null;
            _transport     = null;

            // Symmetric unsubscribe to defend against future re-init paths
            // that would otherwise accumulate handlers.  InitialiseNetwork
            // attaches HandleStateSyncPacket; the historical Cleanup path did
            // not detach it because the manager was assumed to be re-created
            // (not re-initialised) per session.  A future refactor that calls
            // InitialiseNetwork twice on the same instance would silently
            // double-fire StateSync without this line.
            OnDataReceived -= HandleStateSyncPacket;

            // Break the circular delegate reference: RoomManager holds delegate
            // instances that capture `this` (the NetworkManager).  Without this
            // unsubscription the two objects form a reference cycle that survives
            // until GC finalisation — preventing timely collection after teardown
            // and keeping room-state alive in memory across scene reloads.
            // This is the only place outside RecreateRoomAndSpawnManagers that
            // modifies _roomManager; Disconnect() → ClearSessionData() does NOT
            // unsubscribe, so the subscriptions would leak for the remainder of
            // the component's lifetime if we did not clean them up here.
            if (_roomManager != null)
            {
                _roomManager.OnRoomJoined   -= OnRoomManagerJoined;
                _roomManager.OnRoomLeft     -= OnRoomManagerLeft;
                _roomManager.OnRoomCreated  -= OnRoomManagerCreated;
                _roomManager.OnPlayerLeft   -= OnRoomManagerPlayerLeft;
                _roomManager.OnPlayerJoined -= OnRoomManagerPlayerJoined;
                _roomManager = null;
            }
            _spawnManager       = null;
            _lobbyManager       = null;
            _matchmakingManager = null;
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
            _sessionKeyStore.ResetOutboundNonceCounter();
            // Mirror the reset for the application-level sequence so the first
            // FLAG_APP_SEQUENCE packet of a fresh session starts at 0.
            System.Threading.Interlocked.Exchange(ref _outboundAppSequenceCounter, -1L);
            _sessionKeyStore.ResetLastInboundAppSequence();

            // Recreate Room/Spawn managers with the fresh PacketBuilder.
            RecreateRoomAndSpawnManagers();

            // Defensive disposal: the state machine guarantees we are in
            // Disconnected (which always traverses Cleanup), but a future
            // refactor could legitimately call Connect from a different
            // path.  Disposing here is a no-op when the field is already
            // null, and prevents an X25519 ephemeral private key from
            // surviving in heap memory across reconnect attempts.
            _handshakeHandler?.Dispose();
            _sessionKeyStore.DisposeKeys();

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
            // Serialize against scene-transition handlers so a Recreate
            // triggered by reconnect cannot interleave with a PruneDestroyed
            // from sceneUnloaded.  Documented ordering when both fire:
            //  sceneUnloaded → Prune → sceneLoaded → Prune
            //  (Recreate runs atomically with respect to the above.)
            lock (_sceneTransitionLock)
            {
                // Symmetric detach against the prior instance.  Replacing
                // _roomManager with a new instance leaves the old instance
                // unreferenced from this field, but a transport callback
                // already in-flight on a worker thread may still reach the
                // old delegate list and invoke our handler against
                // already-replaced state.  Detaching first guarantees the
                // old instance dispatches no further callbacks regardless
                // of the GC schedule.
                if (_roomManager != null)
                {
                    _roomManager.OnRoomJoined          -= OnRoomManagerJoined;
                    _roomManager.OnRoomLeft            -= OnRoomManagerLeft;
                    _roomManager.OnRoomCreated         -= OnRoomManagerCreated;
                    _roomManager.OnPlayerLeft          -= OnRoomManagerPlayerLeft;
                    _roomManager.OnPlayerJoined        -= OnRoomManagerPlayerJoined;
                }

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

                // Wire the static EnhancedRpcVerifier hooks to the live
                // session state.
                //
                // The roster-anchored sender verifier admits the local session
                // id and (when in a room) defers to IsRosterMemberSession for
                // peer admission.  The current room wire format (see
                // RoomPacketParser — RoomJoin response and PlayerJoined
                // notification) does NOT carry the gateway session id per
                // roster member; only player UUIDs are exposed.  Without a
                // session-id keyed roster the SDK cannot distinguish a
                // legitimate peer from an arbitrary in-room sender, so the
                // membership predicate accepts any non-zero session id and a
                // one-time advisory is emitted on first peer admission.  This
                // preserves cross-player RPC delivery while keeping the zero
                // sentinel guard (impersonation of the pre-authenticated
                // session) and the self-only path active outside any room.
                //
                // The object verifier requires the inbound objectId to resolve
                // in the spawn registry.  Both hooks are torn down on Cleanup /
                // ClearSessionData so a stale closure cannot outlive the
                // manager that captured it.
                RTMPE.Rpc.EnhancedRpcVerifier.SelfSessionIdProvider =
                    () => _localPlayerId;
                RTMPE.Rpc.EnhancedRpcVerifier.IsRoomJoined =
                    () => _roomManager?.CurrentRoom != null;
                RTMPE.Rpc.EnhancedRpcVerifier.LocalSessionIdProvider =
                    () => _localPlayerId;
                RTMPE.Rpc.EnhancedRpcVerifier.IsRosterMemberSession =
                    AdmitNonZeroPeerWithOneTimeAdvisory;
                RTMPE.Rpc.EnhancedRpcVerifier.SenderVerifier =
                    RTMPE.Rpc.EnhancedRpcVerifier.RoomAnchoredSenderVerifier;
                RTMPE.Rpc.EnhancedRpcVerifier.ObjectExistsVerifier =
                    objectId => objectId != 0UL && registry.Get(objectId) != null;

                // Use named-method delegates (not inline lambdas) so the
                // delegate instances are not re-allocated per call to
                // RecreateRoomAndSpawnManagers, which fires on every
                // reconnect / scene load.  Inline lambdas would also
                // capture `this` implicitly and prevent the JIT from
                // caching the delegate; method-group references hit the
                // delegate cache and are emitted as a static field by Roslyn.
                _roomManager.OnPlayerLeft   += OnRoomManagerPlayerLeft;
                _roomManager.OnPlayerJoined += OnRoomManagerPlayerJoined;
            }
        }

        // Event handlers wired in RecreateRoomAndSpawnManagers — named here
        // to avoid per-subscription delegate allocation on every room join.
        private void OnRoomManagerPlayerLeft(string playerId)
            => _spawnManager?.OnPlayerLeftRoom(playerId);

        private void OnRoomManagerPlayerJoined(PlayerInfo _)
            => _spawnManager?.MarkAllVariablesDirtyForResync();

        // One-time advisory state for the in-room peer-admission fallback.
        // The roster-anchored sender verifier consults this predicate when a
        // non-self senderId arrives while the local SDK is in a room.  The
        // current open-source room wire format does not expose gateway session
        // ids per roster member, so client-side roster anchoring is not yet
        // possible; non-zero peers are admitted with a one-time advisory so
        // legitimate cross-player RPC traffic flows while the architectural
        // limitation remains visible to integrators auditing logs.
        private static int _peerAdmissionAdvisoryEmitted;

        private static bool AdmitNonZeroPeerWithOneTimeAdvisory(ulong senderId)
        {
            if (senderId == 0UL) return false;
            if (System.Threading.Interlocked.CompareExchange(
                    ref _peerAdmissionAdvisoryEmitted, 1, 0) == 0)
            {
                UnityEngine.Debug.LogWarning(
                    "[RTMPE] EnhancedRpcVerifier admitting peer RPCs without a " +
                    "session-id-keyed roster anchor.  The room wire format does " +
                    "not expose per-member gateway session ids, so peer senderIds " +
                    "cannot be cross-checked against the roster on the client.  " +
                    "Wire EnhancedRpcVerifier.IsRosterMemberSession or call " +
                    "SetServerAttestedSenderVerifier for stricter admission.");
            }
            return true;
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
        /// <b>Single-attempt API.</b>  This method schedules ONE reconnect
        /// attempt and returns immediately.  The SDK does NOT auto-retry on
        /// failure — the application owns the retry policy so it can be tied
        /// to UI state (e.g. show a "reconnecting…" toast, expose a Cancel
        /// button, time out after N attempts).
        /// </para>
        /// <para>
        /// The recommended retry helper is <see cref="ReconnectBackoff"/>
        /// (Full-Jitter capped exponential — industry-standard).  Drive it
        /// from your reconnect coroutine:
        /// <code>
        /// var backoff = new ReconnectBackoff();
        /// while (!nm.IsConnected &amp;&amp; backoff.Attempt &lt; 10)
        /// {
        ///    yield return new WaitForSeconds((float)backoff.NextDelay().TotalSeconds);
        ///    if (!nm.Reconnect()) break; // CanReconnect went false → fall back to Connect()
        /// }
        /// if (nm.IsConnected) backoff.Reset();
        /// </code>
        /// On exhaustion the application MUST fall back to a full
        /// <see cref="Connect(string)"/> with credentials.
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

            // Reconnect drives a bounded retry loop internally so a single
            // bad attempt cannot leave the SDK frozen in Reconnecting.  The
            // public method still returns immediately — observers see an
            // immediate transition to Reconnecting and the loop coroutine
            // owns the subsequent state moves.
            TransitionTo(NetworkState.Reconnecting);
            int budget = (_settings != null && _settings.maxReconnectAttempts > 0)
                ? _settings.maxReconnectAttempts
                : 1;
            _reconnectLoopCoroutine = StartCoroutine(ReconnectLoopCoroutine(budget));
            return true;
        }

        // Coroutine handle for the bounded retry loop.  Held so a user-driven
        // Disconnect() or a successful SessionAck can stop the loop deterministic-
        // ally without leaving an orphaned coroutine that would later try to
        // mutate state on a torn-down session.
        private Coroutine _reconnectLoopCoroutine;

        /// <summary>
        /// Bounded retry loop driven by <see cref="Reconnect"/>.
        /// Performs up to <paramref name="maxAttempts"/> single-attempt
        /// reconnects, spaced by <see cref="ReconnectBackoff"/>.  Exits early
        /// on the first attempt that reaches <see cref="NetworkState.Connected"/>;
        /// on exhaustion transitions to <see cref="NetworkState.Disconnected"/>,
        /// clears session data, and fires <see cref="OnReconnectFailed"/>.
        /// </summary>
        private IEnumerator ReconnectLoopCoroutine(int maxAttempts)
        {
            var backoff = new ReconnectBackoff();
            int attempts = 0;

            while (attempts < maxAttempts)
            {
                attempts++;

                // CanReconnect can flip between attempts (token cleared by
                // a racing Disconnect, or session torn down externally).
                // Bail out quietly if the precondition no longer holds.
                if (!CanReconnect) break;

                // Each attempt starts a fresh per-connection state.  We
                // re-enter Reconnecting here in case a previous failed
                // attempt transitioned us to Disconnected via the timeout
                // coroutine — the loop owns the state lifecycle, not the
                // individual attempt coroutines.
                if (_state == NetworkState.Disconnected)
                    TransitionTo(NetworkState.Reconnecting);

                StartReconnectAttempt();

                // Wait for the attempt to resolve: either Connected (success)
                // or Disconnected (per-attempt timeout / transport error).
                while (_state == NetworkState.Reconnecting)
                    yield return null;

                if (_state == NetworkState.Connected)
                {
                    _reconnectLoopCoroutine = null;
                    yield break;
                }

                // Attempt failed.  Sleep with full-jitter backoff before the
                // next attempt — prevents reconnect storms on a flapping
                // gateway and de-correlates retry timing across clients.
                if (attempts < maxAttempts)
                {
                    var delay = backoff.NextDelay();
                    yield return new WaitForSeconds((float)delay.TotalSeconds);
                }
            }

            // All attempts consumed.  Make the failure visible to the app —
            // the previous behaviour silently left the manager in
            // Reconnecting with no event surfaced to game UI.
            if (_state != NetworkState.Disconnected)
            {
                _networkThread?.Stop();
                _heartbeatManager?.Stop();
                ClearSessionData(preserveReconnectToken: false);
                TransitionTo(NetworkState.Disconnected, DisconnectReason.Timeout);
            }
            SafeRaise(OnReconnectFailed, attempts, nameof(OnReconnectFailed));
            _reconnectLoopCoroutine = null;
        }

        /// <summary>
        /// Drive the per-attempt portion of a reconnect: reset protocol
        /// state, recreate managers, restart the network thread, and start
        /// the ReconnectInit + timeout coroutines.  Called once per
        /// iteration of <see cref="ReconnectLoopCoroutine"/>.
        /// </summary>
        private void StartReconnectAttempt()
        {
            // Reset per-connection protocol state — same pattern as Connect().
            _packetBuilder = new PacketBuilder();
            _sessionKeyStore.ResetOutboundNonceCounter();
            System.Threading.Interlocked.Exchange(ref _outboundAppSequenceCounter, -1L);
            _sessionKeyStore.ResetLastInboundAppSequence();

            // Recreate Room/Spawn managers with identical event wiring to Connect().
            RecreateRoomAndSpawnManagers();

            // Defensive disposal — same rationale as Connect(): the state
            // machine guarantees Cleanup ran before reaching Disconnected,
            // but disposing here is a no-op when the fields are null and
            // protects ephemeral key material against a future state-flow
            // refactor that misses Cleanup on one of the paths.
            _handshakeHandler?.Dispose();
            _sessionKeyStore.DisposeKeys();

            // Drop the previous session's replay-window bitmap.  The new
            // session derives fresh keys whose nonce stream restarts at zero,
            // so a stale bitmap from the previous session would block
            // legitimate low-counter packets after the new SessionAck.
            // OnChallenge re-allocates the window after key derivation; we
            // null the field here so the receive path's strict null-reject
            // remains the only way an AEAD frame can be observed before the
            // new keys are in place.
            _sessionKeyStore.DropReplayWindow();

            _handshakeHandler = new HandshakeHandler();

            // Re-arm the network thread.  If a previous attempt's timeout or
            // transport error stopped and nulled the thread, recreate it.
            EnsureNetworkThreadReady();
            _networkThread.Start();

            // Kick off the reconnect coroutine — waits for transport bind, sends
            // ReconnectInit, then the existing Challenge/HandshakeResponse/SessionAck
            // handlers complete the flow exactly as for a fresh Connect().
            _connectCoroutine = StartCoroutine(ReconnectInitCoroutine());
            _timeoutCoroutine = StartCoroutine(ConnectionTimeoutRoutine());
        }

        /// <summary>
        /// Lazily reconstruct the network thread when a previous attempt's
        /// timeout tore it down.  The transport instance survives across
        /// attempts (UdpTransport.Connect is re-callable; Disconnect closes
        /// the socket without disposing the wrapper) and is re-bound by the
        /// new RunLoop.  Manager-lifetime subscriptions such as the
        /// state-sync data handler are NOT touched here — they were wired
        /// once in InitialiseNetwork and must not be re-attached per
        /// attempt or every inbound packet would multi-dispatch.
        /// </summary>
        private void EnsureNetworkThreadReady()
        {
            if (_networkThread != null) return;
            if (_transport == null)
            {
                // Should not happen on the reconnect path because
                // InitialiseNetwork ran in Awake; guard for the case where
                // a user-installed factory or manual Disposal cleared the
                // field, so a retry can still recover.
                _transport = new UdpTransport(
                    _settings.serverHost,
                    _settings.serverPort,
                    _settings.sendBufferBytes,
                    _settings.receiveBufferBytes);
            }
            _networkThread = new NetworkThread(
                _transport,
                _settings.networkThreadBufferBytes,
                _settings.sendQueueMaxItems);
            _networkThread.OnPacketReceivedRented += HandlePacketReceivedRented;
            _networkThread.OnError                += HandleTransportError;
        }

        /// <summary>
        /// Gracefully disconnect from the gateway and reset all session state.
        /// No-op when already disconnected or a disconnect is already in progress.
        /// </summary>
        public void Disconnect() => DisconnectWithReason(DisconnectReason.ClientRequest);

        // Shared teardown path used by both user-initiated and internal disconnects.
        // reason is forwarded to OnDisconnected so callers can distinguish nonce
        // exhaustion from a user-initiated or server-initiated disconnect.
        private void DisconnectWithReason(DisconnectReason reason)
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
            // A user-initiated disconnect must cancel any pending bounded
            // reconnect retry so we don't see Reconnecting → Disconnected
            // → Reconnecting flap after the user has explicitly given up.
            if (_reconnectLoopCoroutine != null)
            {
                StopCoroutine(_reconnectLoopCoroutine);
                _reconnectLoopCoroutine = null;
            }

            TransitionTo(NetworkState.Disconnecting);

            if (wasConnected)
                SendDisconnect();

            _heartbeatManager?.Stop();
            _networkThread?.Stop();
            ClearSessionData();
            TransitionTo(NetworkState.Disconnected, reason);
        }

        /// <summary>
        /// Enqueue a raw packet for transmission. Thread-safe.
        /// The packet is AEAD-encrypted when the session is established.
        /// A defensive copy is made internally so the caller can safely reuse its buffer.
        ///
        /// <para>When <paramref name="reliable"/> is `true` and the deployment
        /// has opted into <see cref="NetworkSettings.EmitArqSequence"/>, the
        /// packet is registered with the outbound
        /// <see cref="ReliableChannel"/>: an ARQ sequence is allocated, the
        /// packet bytes are kept in a retransmit table, and any unacknowledged
        /// entry is re-emitted on the configured RTO schedule until either a
        /// matching <see cref="PacketType.DataAck"/> arrives or
        /// <see cref="ReliableChannel.MaxAttempts"/> is exhausted.  The
        /// retransmit driver is the per-frame <see cref="Update"/> tick on the
        /// Unity main thread, so retransmits are interleaved with regular
        /// sends without a dedicated worker thread.</para>
        ///
        /// <para>When <paramref name="reliable"/> is `false`, or
        /// <see cref="NetworkSettings.EmitArqSequence"/> is `false`, the
        /// packet is sent unreliably (no retransmit, no ACK) — the historical
        /// behaviour for legacy callers and projects that have not opted into
        /// the ARQ wire extension.</para>
        /// </summary>
        public void Send(byte[] data, bool reliable = false)
        {
            if (!IsConnected)
            {
                Debug.LogWarning("[RTMPE] NetworkManager.Send: cannot send while not connected.");
                return;
            }

            if (data == null || data.Length == 0) return;

            // _negotiatedPeerCaps is read below without synchronisation — the
            // field is written only on the main thread (inside OnSessionAck,
            // dispatched by MainThreadDispatcher) and all callers of Send are
            // expected to be main-thread code.  This assertion catches
            // accidental cross-thread use before it produces a data race.
            Debug.Assert(
                RTMPE.Threading.MainThreadDispatcher.IsMainThread,
                "[RTMPE] NetworkManager.Send must be called from the Unity main thread — " +
                "_negotiatedPeerCaps is read without synchronisation.");

            // Copy so the caller can safely reuse or discard its buffer after
            // this call, which matches the original NetworkThread.Send(copy)
            // contract.  When reliable=true the same copy is parked in the
            // retransmit table for re-emission on RTO expiry.
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);

            // Application-layer ARQ requires three predicates to line up:
            //   • caller intent (`reliable: true`)
            //   • deployment opt-in (`NetworkSettings.EmitArqSequence`) —
            //     instructs the SDK to emit the 4-byte ARQ sub-header on
            //     the wire so the gateway can address its DataAck
            //   • negotiated peer capability (`CapabilityFlags.ArqAck` in
            //     `_negotiatedPeerCaps`) — the gateway has promised to
            //     emit DataAck for reliable frames
            // Each predicate has its own actionable when missing, so the
            // two deployment-level causes route to dedicated warn-once
            // advisories below.
            bool emitArqSequence = _settings != null && _settings.EmitArqSequence;
            bool peerSupportsArqAck =
                (_negotiatedPeerCaps & RTMPE.Core.Protocol.CapabilityFlags.ArqAck) != 0;

            // Local-side advisory: caller asked for reliability but the
            // SDK is configured not to emit the ARQ sub-header.  Fires
            // once regardless of peer cap state — fixing the local opt-in
            // is the prerequisite either way.
            RTMPE.Core.Diagnostics.ReliableSendAdvisory.NotifyIfDowngrading(
                emitArqSequence:   emitArqSequence,
                reliableRequested: reliable);

            // Peer-side advisory: local opt-in is on but the session's
            // negotiated cap set excludes ArqAck (legacy gateway, or
            // operator left RTMPE_ADVERTISE_ARQ_CAP off, or the active
            // transport's gateway path intentionally suppresses the cap
            // — KCP / WebSocket).  The advisory's internal predicate
            // requires `emitArqSequence == true`, so the two advisories
            // are mutually exclusive on first emission for a given root
            // cause.
            RTMPE.Core.Diagnostics.PeerCapabilityAdvisory.NotifyIfArqUnavailable(
                emitArqSequence:    emitArqSequence,
                peerSupportsArqAck: peerSupportsArqAck,
                reliableRequested:  reliable);

            bool reliableEnabled = reliable && emitArqSequence && peerSupportsArqAck;

            if (reliableEnabled
                && _outboundReliableChannel.TryRegisterOutbound(
                    copy,
                    (float)UnityEngine.Time.unscaledTimeAsDouble,
                    out uint arqSeq))
            {
                // The retransmit table holds the original packet bytes; a
                // resend re-runs EncryptAndSendInternal with the same arqSeq
                // so the receiver observes a stable sequence across retries.
                EncryptAndSendInternal(copy, hasFixedArqSeq: true, fixedArqSeq: arqSeq);
                return;
            }

            // Unreliable path (or saturated retransmit table) — caller has
            // already accepted "best effort" semantics.  When the table is
            // saturated the caller is implicitly back-pressured: the packet
            // still goes out once, but no retry will happen if it is lost.
            EncryptAndSend(copy);
        }

    }
}
