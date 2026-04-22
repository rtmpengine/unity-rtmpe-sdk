# RTMPE SDK — Changelog

> 📦 **Repository:** https://github.com/Faisalzz1/unity-rtmpe-sdk

All notable changes to this Unity package are documented here.
Follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.1.0] — 2026-04-22

**Gameplay-readiness release — five targeted fixes that close the remaining
"day-of-launch" gaps in v1.0. All changes are additive; no breaking API
changes. Defaults preserve v1.0 behaviour.**

### Late-join state snapshot (Critical)
- `Runtime/Core/SpawnManager.cs` — `MarkAllVariablesDirtyForResync()` re-flags every
  `NetworkVariable` on every locally-owned, spawned object so the next 30 Hz flush
  retransmits full state.
- `Runtime/Core/NetworkBehaviour.cs` — `MarkAllVariablesDirty()` internal hook.
- `Runtime/Sync/NetworkVariable.cs` — `NetworkVariableBase.MarkDirtyForResync()`
  forces the dirty flag without firing `OnValueChanged`.
- Auto-wired to `RoomManager.OnPlayerJoined` — new joiners see correct
  `NetworkVariable` values within ~33 ms instead of waiting for the next
  value change.

### Pluggable transport factory (High)
- `Runtime/Core/NetworkManager.cs` — `SetTransportFactory(TransportFactoryFn)`,
  `ClearTransportFactory()`, `HasCustomTransportFactory` static API.
- `TransportFactoryFn` delegate receives the active `NetworkSettings`.
- Opens the door to WebGL builds via user-provided WebSocket transport and
  enables clean mock-transport injection for integration tests.
- Factory exceptions and null returns fall back to the built-in
  `UdpTransport` with a diagnostic log.

### Auto room re-join after reconnect (High)
- `Runtime/Core/NetworkManager.cs` — new properties `LastRoomId`, `LastRoomCode`
  preserved across token-preserving session clears.
- `OnAutoRejoinAttempt(string roomId)` event.
- `Runtime/Core/NetworkSettings.cs` — new `autoRejoinLastRoomOnReconnect` bool
  (default `true`).
- After a successful `Reconnect()` → `SessionAck`, the SDK automatically calls
  `Rooms.JoinRoom(LastRoomId)` when the setting is enabled. The snapshot is
  wiped on explicit `Disconnect()` or `LeaveRoom()` so those never auto-rejoin.

### Scene transition handling (Medium)
- `Runtime/Core/NetworkObjectRegistry.cs` — `PruneDestroyed()` sweeps the
  dictionary in one pass and evicts entries whose Unity `GameObject` has been
  destroyed. Returns the number of evictions.
- `Runtime/Core/NetworkManager.cs` — subscribes to
  `UnityEngine.SceneManagement.SceneManager.sceneUnloaded` and `sceneLoaded`,
  calling `PruneDestroyed()` to prevent slow leaks under additive /
  single-mode scene loads.

### Object pool interface (Medium)
- `Runtime/Core/INetworkObjectPool.cs` — new interface (`Acquire`, `Release`).
- `Runtime/Core/SpawnManager.cs` — `SetObjectPool(INetworkObjectPool)`,
  `ClearObjectPool()`, `ObjectPool` property. Both `CreateLocal` and
  `DestroyLocal` route through the pool when installed; fall back to
  `Object.Instantiate`/`Object.Destroy` otherwise.
- `SpawnManager` now tracks the prefab ID of every live object so the pool
  receives a stable `prefabId` at `Release` time.

### Refactor
- `NetworkManager` consolidates `RoomManager` / `SpawnManager` wiring into a
  single private `RecreateRoomAndSpawnManagers()` helper, eliminating three
  near-identical wiring blocks in `InitialiseNetwork`, `Connect`, and
  `Reconnect`. Guarantees identical event topology on all three paths.

### Testing
- `Tests/Runtime/CryptoAndNetworkTests.cs` — 16 new tests across 5 fixtures:
  - `LateJoinResyncTests` (3) — dirty-reflag semantics, owner-only filter.
  - `PluggableTransportTests` (4) — factory install, fallback on null / throw.
  - `NetworkObjectRegistryPruneTests` (2) — stale-entry eviction.
  - `ObjectPoolTests` (2) — pool routing with and without installed pool.
  - `AutoRejoinTests` (5) — `LastRoomId` preservation + default semantics.

### Documentation
- Complete refresh of all seven guides in `Documentation~/` plus the top-level
  `README.md` and `CHANGELOG.md` to reflect v1.1 API surface.

---

## [1.0.0] — 2026-04-17

**Production release — RTMPE SDK v1.0.0 complete.**

### Room System
- `Runtime/Rooms/RoomManager.cs` — `CreateRoom()`, `JoinRoom(string roomId)`, `JoinRoomByCode(string code)`, `LeaveRoom()`, `ListRooms(bool publicOnly)`.
- `Runtime/Rooms/RoomInfo.cs` — room metadata (ID, name, code, maxPlayers, playerCount, isPublic, state).
- `Runtime/Rooms/PlayerInfo.cs` — player snapshot (playerId, displayName, isLocal).
- `Runtime/Rooms/CreateRoomOptions.cs` — room creation configuration (Name, MaxPlayers 1–16, IsPublic).
- `Runtime/Rooms/JoinRoomOptions.cs` — join configuration (DisplayName).
- `Runtime/Rooms/RoomPacketBuilder.cs` — room packet serialization (Create/Join/Leave/List).
- `Runtime/Rooms/RoomPacketParser.cs` — room packet deserialization.
- `NetworkManager.Rooms` — room API access point.
- Events: `OnRoomCreated`, `OnRoomJoined`, `OnRoomLeft`, `OnPlayerJoined`, `OnPlayerLeft`, `OnRoomListReceived`, `OnRoomError`.

### Spawn & Ownership System
- `Runtime/Core/SpawnManager.cs` — `RegisterPrefab()`, `Spawn()`, `Despawn()`, `ClearAll()`, `OnPlayerLeftRoom()`.
- `Runtime/Core/OwnershipManager.cs` — ownership management: `RequestOwnershipTransfer()`, `ApplyOwnershipGrant()`, `GetObjectsOwnedBy()`.
- `Runtime/Core/NetworkObjectRegistry.cs` — thread-safe object registry.
- `Runtime/Core/SpawnPacketBuilder.cs` / `Runtime/Core/SpawnPacketParser.cs` — spawn/despawn serialization.
- `NetworkBehaviour.DestroyWithOwner` — automatic despawn on owner disconnection.

### RPC System
- `Runtime/Rpc/RpcPacketBuilder.cs` — `BuildRequest()`, `BuildTransferOwnership()`, `BuildPing()`.
- `Runtime/Rpc/RpcPacketParser.cs` — RPC packet deserialization.
- `Runtime/Rpc/RpcDefinitions.cs` — `RpcMethodId` constants (Ping=100, TransferOwnership=200, RequestDamage=300, ApplyDamage=301, GameStateChange=400, SyncGameState=401).
- `NetworkManager.SendRpc(uint methodId, byte[] payload)` — RPC dispatch over reliable KCP channel.

### State Synchronization
- `Runtime/Sync/NetworkTransform.cs` — 30 Hz position/rotation/scale sync with interpolation. Inspector fields: SyncPosition, SyncRotation, SyncScale, PositionThreshold (0.01 m), RotationThreshold (0.1°), ScaleThreshold (0.001).
- `Runtime/Sync/NetworkVariable.cs` / `Runtime/Sync/NetworkVariableBase.cs` — dirty-flag replication with `OnValueChanged` event.
- Concrete types: `NetworkVariableInt`, `NetworkVariableFloat`, `NetworkVariableBool`, `NetworkVariableVector3`, `NetworkVariableQuaternion`, `NetworkVariableString`.
- `Runtime/Sync/StateSync.cs` — `StateSync` (0x40) packet dispatcher.
- `NetworkBehaviour.FlushDirtyVariables()` — variable serialization.

### Cryptography
- `Runtime/Crypto/ChaCha20Poly1305.cs` — ChaCha20-Poly1305 AEAD (RFC 8439).
- `Runtime/Crypto/X25519.cs` — X25519 key exchange (RFC 7748).
- `Runtime/Crypto/Ed25519.cs` — Ed25519 signature verification (RFC 8032).
- `Runtime/Crypto/HkdfSha256.cs` — HKDF-SHA256 key derivation (RFC 5869).
- `Runtime/Crypto/HandshakeHandler.cs` — 4-step W6 handshake: HandshakeInit (PSK) → Challenge → HandshakeResponse → SessionAck.
- `Runtime/Crypto/SessionKeys.cs` — directional session key derivation.
- `Runtime/Crypto/ApiKeyCipher.cs` — API key encryption for HandshakeInit.

### Transport & Protocol
- `Runtime/Infrastructure/Transport/UdpTransport.cs` — non-blocking UDP socket.
- `Runtime/Infrastructure/Transport/KcpProtocol.cs` — KCP reliable transport layer.
- `Runtime/Protocol/PacketBuilder.cs` — 13-byte header + payload serialization.
- `Runtime/Protocol/PacketParser.cs` — zero-copy header parsing.
- `Runtime/Core/NetworkBehaviour.cs` — base class with `IsOwner`, `IsSpawned`, `OnNetworkSpawn()`, `OnNetworkDespawn()`, `OnOwnershipChanged()`.
- Heartbeat: 3-miss timeout, RTT measurement, `OnHeartbeatTimeout` event.

### Infrastructure
- `Runtime/Infrastructure/Threading/NetworkThread.cs` — background I/O thread.
- `Runtime/Infrastructure/Threading/ThreadSafeQueue.cs` — thread-safe queue.
- `Runtime/Infrastructure/Transport/NetworkTransport.cs` — abstract transport base.

### Samples
- `Samples/BasicMovement/` — WASD movement with `NetworkTransform` and score sync.
- `Samples/SimpleFPS/` — hitscan FPS with RPC-based damage and health tracking.

### Testing
- 103 unit tests covering cryptography, serialization, protocol, and networking components.

---

## [0.2.0-preview] — 2026-03-09

### Core Features
- `Runtime/Core/NetworkConstants.cs` — wire-protocol constants.
- `Runtime/Core/NetworkSettings.cs` — `ScriptableObject` configuration.
- `Runtime/Core/NetworkManager.cs` — network lifecycle management.
- State machine: `Disconnected → Connecting → Connected → InRoom → Disconnecting`.
- Events: `OnStateChanged`, `OnConnected`, `OnDisconnected`, `OnConnectionFailed`, `OnDataReceived`.

### Transport Layer
- `Runtime/Infrastructure/Transport/NetworkTransport.cs` — abstract transport base.
- `Runtime/Infrastructure/Transport/UdpTransport.cs` — UDP socket implementation.

### Threading
- `Runtime/Infrastructure/Threading/ThreadSafeQueue.cs` — concurrent queue wrapper.
- `Runtime/Infrastructure/Threading/MainThreadDispatcher.cs` — thread marshalling.
- `Runtime/Infrastructure/Threading/NetworkThread.cs` — background I/O thread.

### Editor & Tests
- `Editor/NetworkManagerEditor.cs` — custom inspector.
- 34 NUnit tests for core systems.
- `Samples~/BasicConnection/README.md` — sample documentation.

---

## [0.1.0-preview] — 2026-03-07

### Initial Package
- UPM package structure (`package.json`, `Runtime/`, `Editor/`).
- `Runtime/com.rtmpe.sdk.runtime.asmdef` — runtime assembly definition.
- `Editor/com.rtmpe.sdk.editor.asmdef` — editor assembly definition.
- Foundation directory structure.
