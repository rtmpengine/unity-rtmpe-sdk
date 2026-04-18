# RTMPE SDK — Changelog

All notable changes to this Unity package are documented here.
Follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) format and
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

## [1.0.0] — 2026-04-17

**Production release — RTMPE v3.0 MVP complete.**

### Added — Room System (Week 20)
- `Runtime/Rooms/RoomManager.cs` — `CreateRoom()`, `JoinRoom(string roomId)`, `JoinRoomByCode(string code)`, `LeaveRoom()`, `ListRooms(bool publicOnly)`.
- `Runtime/Rooms/RoomInfo.cs` — room metadata (ID, name, code, maxPlayers, playerCount, isPublic, state).
- `Runtime/Rooms/PlayerInfo.cs` — player snapshot (playerId, displayName, isLocal).
- `Runtime/Rooms/CreateRoomOptions.cs` — typed options for room creation (Name, MaxPlayers 1–16, IsPublic).
- `Runtime/Rooms/JoinRoomOptions.cs` — typed options for join (DisplayName).
- `Runtime/Rooms/RoomPacketBuilder.cs` — binary payloads for RoomCreate/RoomJoin/RoomLeave/RoomList packets.
- `Runtime/Rooms/RoomPacketParser.cs` — response parser for all four room packet types.
- `NetworkManager.Rooms` property — access point for the full room API.
- Events: `OnRoomCreated`, `OnRoomJoined`, `OnRoomLeft`, `OnPlayerJoined`, `OnPlayerLeft`, `OnRoomListReceived`, `OnRoomError`.

### Added — Spawn & Ownership System (Week 19)
- `Runtime/Core/SpawnManager.cs` — `RegisterPrefab()`, `Spawn()`, `Despawn()`, `ClearAll()`, `OnPlayerLeftRoom()`.
- `Runtime/Core/OwnershipManager.cs` — server-authoritative ownership: `RequestOwnershipTransfer()`, `ApplyOwnershipGrant()`, `GetObjectsOwnedBy()`.
- `Runtime/Core/NetworkObjectRegistry.cs` — thread-safe map of `ulong → NetworkBehaviour` for all live objects.
- `Runtime/Spawn/SpawnPacketBuilder.cs` / `SpawnPacketParser.cs` — spawn/despawn binary wire format.
- `NetworkBehaviour.DestroyWithOwner` — automatically despawns objects when owner leaves the room.

### Added — RPC System (Week 17–18)
- `Runtime/Rpc/RpcPacketBuilder.cs` — `BuildRequest()`, `BuildTransferOwnership()`, `BuildPing()`.
- `Runtime/Rpc/RpcPacketParser.cs` — parse incoming RPC payloads.
- `Runtime/Rpc/RpcDefinitions.cs` — `RpcMethodId` constants: Ping=100, TransferOwnership=200, RequestDamage=300, ApplyDamage=301, GameStateChange=400, SyncGameState=401.
- `NetworkManager.SendRpc(uint methodId, byte[] payload)` — high-level RPC dispatch over reliable KCP channel.

### Added — State Sync (Week 15–16)
- `Runtime/Sync/NetworkTransform.cs` — 30 Hz position/rotation/(optional) scale sync with interpolation. Inspector fields: SyncPosition, SyncRotation, SyncScale, PositionThreshold (0.01 m), RotationThreshold (0.1°), ScaleThreshold (0.001).
- `Runtime/Sync/NetworkVariable.cs` / `NetworkVariableBase.cs` — dirty-flag replication with `OnValueChanged` event.
- Concrete variable types: `NetworkVariableInt`, `NetworkVariableFloat`, `NetworkVariableBool`, `NetworkVariableVector3`, `NetworkVariableQuaternion`, `NetworkVariableString`.
- `Runtime/Sync/StateSync.cs` — incoming `StateSync` (0x40) packet dispatcher; routes variable updates to registered objects.
- `NetworkBehaviour.FlushDirtyVariables()` — gathers all dirty variables and enqueues a `Data` packet; uses growable `MemoryStream` (bug fix: replaced fixed-capacity stream).

### Added — Full Crypto Stack (Week 9)
- `Runtime/Crypto/ChaCha20Poly1305.cs` — pure C# ChaCha20-Poly1305 AEAD (RFC 8439).
- `Runtime/Crypto/X25519.cs` — Montgomery ladder scalar multiplication (RFC 7748).
- `Runtime/Crypto/Ed25519.cs` — signature verification (RFC 8032); `ScalarMult(n=0)` crash fixed.
- `Runtime/Crypto/HkdfSha256.cs` — HKDF Extract + Expand (RFC 5869).
- `Runtime/Crypto/HandshakeManager.cs` — 4-step W6 handshake state machine: HandshakeInit (PSK) → Challenge → HandshakeResponse → SessionAck.
- G-H1 fix: two directional session keys via HKDF-Expand with `\x00`/`\x01` suffix.
- H4 fix: Ed25519 server signature verified **before** ECDH proceeds.
- M-12 fix: real source IP via routing probe used in AAD `[0x04][ip:4][port:2 LE]`.
- `Runtime/Crypto/ApiKeyCipher.cs` — PSK-based API key encryption for HandshakeInit.

### Added — Transport & Protocol (Week 8)
- `Runtime/Infrastructure/Transport/UdpTransport.cs` — non-blocking UDP socket (Blocking=false, Poll(0)).
- `Runtime/Infrastructure/Transport/KcpProtocol.cs` — KCP reliable transport layer over UDP.
- `Runtime/Protocol/PacketBuilder.cs` — builds 13-byte header + payload.
- `Runtime/Protocol/PacketParser.cs` — zero-copy header parser; 1 MiB payload cap (overflow fix).
- `Runtime/Core/NetworkBehaviour.cs` — base class with `IsOwner`, `IsSpawned`, `OnNetworkSpawn()`, `OnNetworkDespawn()`, `OnOwnershipChanged()`.
- Heartbeat: 3-miss timeout via `Stopwatch`, RTT measurement, `OnHeartbeatTimeout` → disconnect.
- IL2CPP-safe sequence counter: `Interlocked.Increment(ref int)` cast to `uint`.

### Added — Samples
- `Samples/BasicMovement/` — WASD movement with `NetworkTransform` + `NetworkVariableInt` score sync.
- `Samples/SimpleFPS/` — hitscan FPS with `RequestDamage`/`ApplyDamage` RPC pattern, `NetworkVariableInt` health.

### Added — Tests
- **103 unit tests** total: 31 crypto · 20 PacketBuilder · 16 PacketParser · 17 NetworkManager · 11 ThreadSafeQueue · 8 protocol constants.

### Fixed
- `NetworkVariable.SerializeWithId()` — replaced fixed-capacity `MemoryStream(4)` with growable `MemoryStream(64)` (overflow on strings/Vector3).
- `NetworkBehaviour.FlushDirtyVariables()` — replaced fixed-capacity `MemoryStream(256)` with growable `MemoryStream(256)`.
- `RoomPacketBuilder` — added missing `using UnityEngine;`.
- `Ed25519.ScalarMult(n=0)` — `BigInteger.Log(0,2)` crash fixed with early identity return.
- `PacketParser.ExtractPayload` — integer overflow on adversarial `payload_len ≥ 2^31` fixed with 1 MiB cap.
- `Ed25519.PointDouble` — dead/incorrect variables `E_inner`/`E` removed.

---

## [0.2.0-preview] — 2026-03-09

### Added
- `Runtime/Core/NetworkConstants.cs` — wire-protocol constants mirroring the Rust gateway
  (`PacketProtocol`, `PacketType`, `PacketFlags` with all 18 discriminator values).
- `Runtime/Core/NetworkSettings.cs` — `ScriptableObject` asset for per-project configuration.
- `Runtime/Core/NetworkManager.cs` — singleton `MonoBehaviour`; state machine
  (`Disconnected → Connecting → Connected → InRoom → Disconnecting`);
  `OnStateChanged / OnConnected / OnDisconnected / OnConnectionFailed / OnDataReceived` events.
- `Runtime/Infrastructure/Transport/NetworkTransport.cs` — abstract transport base.
- `Runtime/Infrastructure/Transport/UdpTransport.cs` — non-blocking UDP socket scaffold.
- `Runtime/Infrastructure/Threading/ThreadSafeQueue.cs` — `ConcurrentQueue<T>` wrapper.
- `Runtime/Infrastructure/Threading/MainThreadDispatcher.cs` — network thread → Unity main thread marshaller.
- `Runtime/Infrastructure/Threading/NetworkThread.cs` — background I/O thread (`ThreadPriority.AboveNormal`).
- `Editor/NetworkManagerEditor.cs` — custom inspector with Play Mode runtime diagnostics.
- `Tests/Runtime/ThreadSafeQueueTests.cs` — 11 NUnit tests.
- `Tests/Runtime/NetworkManagerTests.cs` — 17 NUnit tests.
- `Tests/Runtime/ThreadingTests.cs` — 6 protocol-constant contract-guard tests.
- `Samples~/BasicConnection/README.md` — UPM sample scaffold.
- `Documentation~/index.md` — documentation root.

### Changed
- `package.json` bumped to `0.2.0-preview`; `samples` array registered.

### Fixed
- `Challenge` packet comment: payload is `[ephemeral_pub:32][static_pub:32][ed25519_sig:64]` = 128 B.
- `README.md` Quick Start: `Connect(apiKey)` replaces the non-existent `ConnectAsync`.

---

## [0.1.0-preview] — 2026-03-07

### Added
- Initial UPM package scaffold (`package.json`, `Runtime/`, `Editor/`).
- `Runtime/com.rtmpe.sdk.runtime.asmdef` (`RTMPE.SDK.Runtime`).
- `Editor/com.rtmpe.sdk.editor.asmdef` (`RTMPE.SDK.Editor`).
- Empty `Infrastructure/` sub-directories (Transport, Serialization, Threading).
