# RTMPE SDK — Changelog

> 📦 **Repository:** https://github.com/rtmpengine/unity-rtmpe-sdk

All notable changes to this Unity package are documented here.
Follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

### Security
- AEAD pipeline now enforces `ExpectEncryptedSessionAck` — a misconfigured
  client can no longer silently accept a plaintext SessionAck on a session
  that negotiated bootstrap encryption.
- `OwnershipManager` eviction scrubs the orphaned-request expectation tuple
  symmetrically across every tracking dictionary.
- `NetworkObjectRegistry.Register` rejects re-entrant calls issued from inside
  an `OnNetworkDespawn` handler via a `[ThreadStatic]` depth counter so the
  outer slot cannot be silently clobbered.
- `TransformQuantization.TryWriteHalf` saturating-clamps inputs to
  ±`HalfMaxFinite` (65504) before the bit-level conversion, making the encoder
  total over the finite float domain and preventing far-overflow position
  components from teleporting to the origin via an Inf round-trip.
- Broad hardening sweep: JWT revocation, TOCTOU fixes via transactional
  repository methods, `NetworkVariable.SetValueWithoutNotify` guards on
  `IsSpawned`, `SpawnManager.CreateLocal` reordered so `OnNetworkSpawn`
  observes a fully-initialised object, `ObjectExistsVerifier` defaults to
  registry membership, RPC sender verifier defaults to self-only with explicit
  opt-in.

### Performance & Threading
- `AeadNonce.BuildInto` writes the 12-byte nonce into a caller-provided buffer,
  eliminating a per-packet allocation on the AEAD hot path.
- `NetworkVariable` fast-path serializer caches `MemoryStream` + `BinaryWriter`
  wrappers per instance — `SerializeWithId` no longer allocates them on every
  call.
- `Lz4Compressor` rents both the output buffer and the int hash table from
  `ArrayPool`, returning with `clearArray:true`.
- `NetworkTransformInterpolator` uses a per-object cursor hint to reduce the
  bracket lookup from O(N) to amortised O(1) at large object counts (5 000+
  replicas).
- `MainThreadDispatcher`, `NetworkThread`, and `ReliableChannel` re-audited
  for race conditions, `Interlocked` discipline, and shutdown ordering.

### Tests
- 20 new Edit-Mode tests covering `OwnershipManager` eviction, re-entrant
  registry guards, and `TransformQuantization` IEEE 754 binary16 boundary
  cases (saturating clamp, encoder-totality property).
- Additional performance, threading, and security stress tests added.

### Compatibility
- `package.json` — `unity` floor lowered to `2022.3` so the package installs
  on Unity 2022.3 LTS, 2023 LTS, and Unity 6 (6000.0+).
- `Runtime/Sync/RigidbodyVelocityCompat.cs` — new compatibility helpers for
  `Rigidbody.linearVelocity` / `Rigidbody2D.linearVelocity` so
  `NetworkRigidbody` and `NetworkRigidbody2D` compile against both
  Unity 2022.3 / 2023 LTS (`velocity`) and Unity 6 (`linearVelocity`).

### Editor
- `Editor/EditorApiKeyStore.cs` — replaced `System.Security.Cryptography.AesGcm`
  with the package's managed `ChaCha20Poly1305Impl`. `AesGcm` is unavailable on
  Unity Editor Mono; the old code threw `TypeInitializationException` on first
  API key entry.
- `Editor/EditorApiKeyStore.cs` — KEK derivation no longer mixes
  `Application.dataPath` into the HKDF input, so renaming or moving the project
  folder no longer silently invalidates the saved API key.
- `Editor/SetupWizard.cs` — added a Cancel button with an unsaved-changes
  confirmation dialog; `ApiKeyStore.Save()` failures are now surfaced via
  `EditorUtility.DisplayDialog` so a storage failure no longer silently advances
  the wizard past the API-key step.

### Documentation
- Quick Start updated to spell out the four-step `NetworkSettings` asset
  prerequisite required before the first `Connect()` call.
- Unity version requirement updated throughout to reflect 2022.3 LTS / 2023 LTS
  support.
- `sendBufferBytes` / `receiveBufferBytes` default values corrected (`4096` →
  `262144`) across API reference and Getting Started guide.
- `CreateRoomOptions.MaxPlayers` range corrected (`1–16` → `1–100`).
- Added documentation for `NetworkManager.OnReconnectFailed` and five
  `RoomManager` events (`OnRoomPropertiesChanged`, `OnPlayerPropertiesChanged`,
  `OnMasterClientChanged`, `OnPlayerKicked`, `OnAllPlayersSceneLoaded`).
- `MainThreadDispatcher` queue cap corrected from `4096` to `10000` in the
  architecture diagram.

### CI/CD
- New workflow automatically publishes the package directory as a flat tree to
  `rtmpengine/unity-rtmpe-sdk` on every push to `main` that touches the
  package, with a `dry-run` toggle on `workflow_dispatch`.

---

## [1.1.0] — 2026-04-22

**Gameplay-readiness release — five targeted fixes that close the remaining
day-of-launch gaps in v1.0. All changes are additive; no breaking API
changes. Defaults preserve v1.0 behaviour.**

### Late-join state snapshot
- `Runtime/Core/SpawnManager.cs` — `MarkAllVariablesDirtyForResync()` re-flags
  every `NetworkVariable` on every locally-owned, spawned object so the next
  30 Hz flush retransmits full state.
- `Runtime/Core/NetworkBehaviour.cs` — `MarkAllVariablesDirty()` internal hook.
- `Runtime/Sync/NetworkVariable.cs` — `NetworkVariableBase.MarkDirtyForResync()`
  forces the dirty flag without firing `OnValueChanged`.
- Auto-wired to `RoomManager.OnPlayerJoined` — new joiners see correct
  `NetworkVariable` values within ~33 ms instead of waiting for the next value
  change.

### Pluggable transport factory
- `Runtime/Core/NetworkManager.cs` — `SetTransportFactory(TransportFactoryFn)`,
  `ClearTransportFactory()`, `HasCustomTransportFactory` static API.
- `TransportFactoryFn` delegate receives the active `NetworkSettings`.
- Enables WebGL builds via user-provided WebSocket transport and clean
  mock-transport injection for integration tests.
- Factory exceptions and null returns fall back to the built-in `UdpTransport`
  with a diagnostic log.

### Auto room re-join after reconnect
- `Runtime/Core/NetworkManager.cs` — new properties `LastRoomId`, `LastRoomCode`
  preserved across token-preserving session clears.
- `OnAutoRejoinAttempt(string roomId)` event.
- `Runtime/Core/NetworkSettings.cs` — new `autoRejoinLastRoomOnReconnect` bool
  (default `true`).
- After a successful `Reconnect()` → `SessionAck`, the SDK automatically calls
  `Rooms.JoinRoom(LastRoomId)` when the setting is enabled. The snapshot is
  wiped on explicit `Disconnect()` or `LeaveRoom()`.

### Scene transition handling
- `Runtime/Core/NetworkObjectRegistry.cs` — `PruneDestroyed()` sweeps the
  registry in one pass and evicts entries whose `GameObject` has been destroyed.
- `Runtime/Core/NetworkManager.cs` — subscribes to `SceneManager.sceneUnloaded`
  and `sceneLoaded`, calling `PruneDestroyed()` to prevent leaks under additive
  / single-mode scene loads.

### Object pool interface
- `Runtime/Core/INetworkObjectPool.cs` — new interface (`Acquire`, `Release`).
- `Runtime/Core/SpawnManager.cs` — `SetObjectPool(INetworkObjectPool)`,
  `ClearObjectPool()`, `ObjectPool` property. Both `CreateLocal` and
  `DestroyLocal` route through the pool when installed; fall back to
  `Object.Instantiate` / `Object.Destroy` otherwise.
- `SpawnManager` tracks the prefab ID of every live object so the pool receives
  a stable `prefabId` at `Release` time.

### Refactor
- `NetworkManager` consolidates `RoomManager` / `SpawnManager` wiring into a
  single private `RecreateRoomAndSpawnManagers()` helper, eliminating three
  near-identical wiring blocks in `InitialiseNetwork`, `Connect`, and
  `Reconnect`.

### Testing
- 16 new tests across 5 fixtures: late-join resync, pluggable transport,
  object registry pruning, object pool routing, and auto-rejoin semantics.

### Documentation
- Complete refresh of all guides in `Documentation~/` plus `README.md` and
  `CHANGELOG.md` to reflect v1.1 API surface.

---

## [1.0.0] — 2026-04-17

**Production release.**

### Room System
- `RoomManager` — `CreateRoom()`, `JoinRoom()`, `JoinRoomByCode()`,
  `LeaveRoom()`, `ListRooms()`.
- `RoomInfo`, `PlayerInfo`, `CreateRoomOptions`, `JoinRoomOptions`.
- Events: `OnRoomCreated`, `OnRoomJoined`, `OnRoomLeft`, `OnPlayerJoined`,
  `OnPlayerLeft`, `OnRoomListReceived`, `OnRoomError`.

### Spawn & Ownership
- `SpawnManager` — `RegisterPrefab()`, `Spawn()`, `Despawn()`, `ClearAll()`.
- `OwnershipManager` — `RequestOwnershipTransfer()`, `ApplyOwnershipGrant()`,
  `GetObjectsOwnedBy()`.
- `NetworkObjectRegistry` — thread-safe object registry.
- `NetworkBehaviour.DestroyWithOwner` — automatic despawn on owner
  disconnection.

### RPC System
- `RpcPacketBuilder` / `RpcPacketParser`.
- `RpcDefinitions` — method ID constants.
- `NetworkManager.SendRpc(uint methodId, byte[] payload)` — RPC dispatch over
  reliable KCP channel.

### State Synchronization
- `NetworkTransform` — 30 Hz position / rotation / scale sync with
  interpolation.
- `NetworkVariable<T>` — dirty-flag replication with `OnValueChanged`.
- Concrete types: `NetworkVariableInt`, `NetworkVariableFloat`,
  `NetworkVariableBool`, `NetworkVariableVector3`, `NetworkVariableQuaternion`,
  `NetworkVariableString`.

### Cryptography
- ChaCha20-Poly1305 AEAD (RFC 8439), X25519 key exchange (RFC 7748),
  Ed25519 signature verification (RFC 8032), HKDF-SHA256 (RFC 5869).
- 4-step W6 handshake: HandshakeInit (PSK) → Challenge → HandshakeResponse →
  SessionAck.

### Transport & Protocol
- Non-blocking UDP socket with KCP reliable transport layer.
- 13-byte packet header + payload serialisation / zero-copy parsing.
- Heartbeat: 3-miss timeout, RTT measurement, `OnHeartbeatTimeout` event.

### Infrastructure
- Background I/O thread, thread-safe queue, abstract transport base.

### Samples
- `BasicMovement` — WASD movement with `NetworkTransform` and score sync.
- `SimpleFPS` — hitscan FPS with RPC-based damage and health tracking.

### Testing
- 103 unit tests covering cryptography, serialisation, protocol, and networking.

---

## [0.2.0-preview] — 2026-03-09

### Core Features
- `NetworkSettings` (`ScriptableObject` configuration).
- `NetworkManager` — network lifecycle and state machine:
  `Disconnected → Connecting → Connected → InRoom → Disconnecting`.
- Events: `OnStateChanged`, `OnConnected`, `OnDisconnected`,
  `OnConnectionFailed`, `OnDataReceived`.

### Transport & Threading
- `UdpTransport` — UDP socket implementation.
- `ThreadSafeQueue`, `MainThreadDispatcher`, `NetworkThread`.

### Editor & Tests
- `NetworkManagerEditor` — custom inspector.
- 34 NUnit tests for core systems.

---

## [0.1.0-preview] — 2026-03-07

### Initial Package
- UPM package structure (`package.json`, `Runtime/`, `Editor/`).
- Runtime and Editor assembly definitions.
- Foundation directory structure.
