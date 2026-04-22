# RTMPE SDK — Architecture

> SDK Version: `com.rtmpe.sdk 1.1.0`
> Protocol Version: v3

---

## Table of Contents

1. [Layer Overview](#1-layer-overview)
2. [Threading Model](#2-threading-model)
3. [Transport Layer](#3-transport-layer)
4. [Crypto Layer](#4-crypto-layer)
5. [Protocol Layer](#5-protocol-layer)
6. [Domain Layer](#6-domain-layer)
7. [Object Lifecycle](#7-object-lifecycle)
8. [Late-Join Snapshot](#8-late-join-snapshot)
9. [Reconnect Flow](#9-reconnect-flow)
10. [Scene Transition Handling](#10-scene-transition-handling)
11. [Object Pooling](#11-object-pooling)
12. [Data Flow — Outbound](#12-data-flow--outbound)
13. [Data Flow — Inbound](#13-data-flow--inbound)

---

## 1. Layer Overview

```
┌────────────────────────────────────────────────────────────────┐
│                       YOUR GAME CODE                           │
│   MonoBehaviour / NetworkBehaviour subclasses                  │
│   PlayerController, GameManager, RoomListUI, …                 │
└──────────────────────────┬─────────────────────────────────────┘
                           │ C# events / method calls
┌──────────────────────────▼─────────────────────────────────────┐
│                       DOMAIN LAYER                             │
│   NetworkManager   RoomManager   SpawnManager                  │
│   OwnershipManager NetworkObjectRegistry                       │
│   NetworkBehaviour NetworkTransform NetworkVariable            │
│   INetworkObjectPool (pluggable)                               │
└──────────────────────────┬─────────────────────────────────────┘
                           │ byte[] packets
┌──────────────────────────▼─────────────────────────────────────┐
│                      PROTOCOL LAYER                            │
│   PacketBuilder   PacketParser   HeartbeatManager              │
│   RoomPacketBuilder/Parser   RpcPacketBuilder/Parser           │
│   SpawnPacketBuilder/Parser  TransformPacketBuilder/Parser     │
└──────────────────────────┬─────────────────────────────────────┘
                           │ byte[] + AAD
┌──────────────────────────▼─────────────────────────────────────┐
│                       CRYPTO LAYER                             │
│   HandshakeHandler (X25519 ECDH + Ed25519 verify)              │
│   SessionKeys (EncryptKey / DecryptKey)                        │
│   ChaCha20Poly1305Impl   HkdfSha256   ApiKeyCipher             │
│   Curve25519   Ed25519Verify                                   │
│   Lz4Compressor (payload compression pre-AEAD)                 │
└──────────────────────────┬─────────────────────────────────────┘
                           │ encrypted byte[]
┌──────────────────────────▼─────────────────────────────────────┐
│                    INFRASTRUCTURE LAYER                        │
│   NetworkTransport (abstract)      ← pluggable per platform    │
│   UdpTransport  (default; System.Net.Sockets UDP)              │
│   NetworkThread (background I/O loop, ~1 kHz)                  │
│   MainThreadDispatcher (Unity-thread callbacks, 200/frame cap) │
│   ThreadSafeQueue<T>                                           │
└──────────────────────────┬─────────────────────────────────────┘
                           │ UDP datagrams
                  Network / Internet
                           │
                  RTMPE Gateway (Rust)
```

### v1.1 additions

The shaded components below were added in v1.1 to close the remaining
gameplay-readiness gaps identified during the v1.0 audit:

| Component | Purpose |
|-----------|---------|
| `INetworkObjectPool` | Pluggable object pool for high-churn spawning |
| `NetworkManager.TransportFactoryFn` | Pluggable transport (WebGL / mock-transport integration tests) |
| `NetworkObjectRegistry.PruneDestroyed()` | Scene-transition sweep |
| `SpawnManager.MarkAllVariablesDirtyForResync()` | Late-join state snapshot |
| `NetworkManager.LastRoomId` / `OnAutoRejoinAttempt` | Auto room re-join after reconnect |

---

## 2. Threading Model

The SDK runs on two threads simultaneously to keep Unity's main thread free
from blocking socket I/O.

```
┌─────────────────────────────────────────────────────────┐
│                    UNITY MAIN THREAD                    │
│                                                         │
│  MonoBehaviour.Update()                                 │
│  MonoBehaviour.FixedUpdate()                            │
│  NetworkManager event handlers (OnConnected, …)         │
│  NetworkVariable.OnValueChanged callbacks               │
│  SpawnManager.Spawn/Despawn (Instantiate/Destroy/Pool)  │
│  MainThreadDispatcher.Update() — drains action queue    │
│    (up to 200 actions per frame, queue capped @ 4096)   │
│                                                         │
│  ⚠ Never block this thread with socket calls            │
└────────────────┬────────────────────────────────────────┘
                 │  enqueues Action via MainThreadDispatcher
                 │
┌────────────────▼────────────────────────────────────────┐
│                 BACKGROUND NETWORK THREAD               │
│  Name:     "RTMPE-NetworkThread"                        │
│  Priority: AboveNormal                                  │
│  Cadence:  ~1 kHz (Thread.Sleep(1); on Windows          │
│            timeBeginPeriod(1) raises resolution to 1 ms)│
│                                                         │
│  Loop:                                                  │
│    1. DrainSendQueue (up to 100 packets / iteration)    │
│    2. TryReceive (up to 100 packets / iteration)        │
│    3. Decrypt each packet (ChaCha20-Poly1305)           │
│    4. Enqueue callback to MainThreadDispatcher          │
│    5. Thread.Sleep(1)                                   │
│                                                         │
│  Started by: NetworkThread.Start()  (Interlocked guard) │
│  Stopped by: NetworkThread.Stop()                       │
│  Error handling: resets atomic flags before OnError     │
└─────────────────────────────────────────────────────────┘
```

### Thread-safety rules

| Component                           | Thread-safe?        | Notes |
|-------------------------------------|---------------------|-------|
| `NetworkManager.Connect()`          | Main thread only    | Call from Unity main thread |
| `NetworkManager.Disconnect()`       | Main thread only    | |
| `NetworkManager.Reconnect()`        | Main thread only    | |
| `NetworkVariable.Value` (write)     | Main thread only    | Never from background threads |
| `NetworkVariable.Value` (read)      | Any thread          | Volatile read |
| `ThreadSafeQueue<T>`                | Any thread          | Uses `ConcurrentQueue<T>` |
| `NetworkObjectRegistry`             | Any thread          | `Dictionary<ulong,NetworkBehaviour>` protected by explicit `lock` |
| `NetworkObjectRegistry.PruneDestroyed()` | Main thread only | Uses Unity's null-equality which is unsafe off-thread |
| `PacketBuilder` sequence counter    | Any thread          | `Interlocked.Increment` on a `long` |
| Outbound AEAD nonce counter         | Any thread          | `Interlocked.Increment` on a `long`; hard-stops at 2³² |

---

## 3. Transport Layer

### Pluggable transport (v1.1)

`NetworkManager` instantiates its transport through a static factory delegate
installed by the application:

```csharp
NetworkManager.SetTransportFactory(settings => new MyWebSocketTransport(settings));
```

When no factory is installed, `NetworkManager` instantiates the built-in
`UdpTransport`. A `null` return from the factory or a thrown exception falls
back to `UdpTransport` and logs a diagnostic (error / warning).

```
┌─────────────────────┐
│ NetworkManager      │
│  InitialiseNetwork()│
└──────────┬──────────┘
           │
           ▼
   TransportFactory
   installed?
           │
       ┌───┴───┐
       Yes     No
       │       │
       ▼       ▼
   factory(settings)   new UdpTransport(...)
       │
       ▼
   NetworkTransport abstract base
```

**Use cases:**

| Target | Factory | Notes |
|--------|---------|-------|
| Desktop / Mobile | (none) | Default `UdpTransport` is correct |
| Integration tests | `_ => new MockTransport()` | Deterministic loopback |
| WebGL | `_ => new WebSocketTransport(...)` | Requires a WebSocket-to-UDP bridge on the server side; not part of the default RTMPE image |

### UdpTransport (default)

All game traffic travels over a single **UDP socket** (`System.Net.Sockets.Socket`).

| Property       | Value                                     |
|----------------|-------------------------------------------|
| Protocol       | UDP (unreliable, unordered)               |
| Default port   | 7777                                      |
| Reliable path  | KCP over UDP — port 7778                  |
| Send buffer    | configurable — default 4 096 bytes        |
| Receive buffer | configurable — default 4 096 bytes        |
| IPv6           | IPv4-preferred; falls back to IPv6 for IPv6-only hosts |

**Local IP discovery** — `UdpTransport` opens a temporary routing probe UDP socket
(no data sent) to discover the OS-assigned outgoing interface IP. The real socket
is bound to `IPAddress.Any` (or `IPv6Any`) and the probe's source address becomes
the `LocalEndPoint`. This ensures the correct source IP is included in the AEAD
Additional Authenticated Data (AAD) of the `HandshakeInit` packet.

If the probe fails (isolated containers, no default route) the SDK logs a warning
and falls back to loopback. The subsequent handshake against a non-loopback server
will fail an AAD check — the warning makes the failure diagnosable rather than
a silent timeout.

**Error handling:**
- `SocketError.WouldBlock` — silently ignored (non-blocking receive returned no data)
- `SocketError.ConnectionReset` — silently ignored (ICMP port-unreachable on Windows)
- All other socket errors — propagated to `NetworkThread.OnError`

### Reliable path (KCP)

Room management packets (CreateRoom, JoinRoom, LeaveRoom, ListRooms),
handshake packets, RPC calls, variable updates, and spawn/despawn notifications
travel over **KCP** (port 7778) — a reliable, ordered, congestion-controlled
protocol built on top of UDP.

Fast-path game state (position/rotation at 30 Hz) travels over the plain
UDP socket (port 7777) for minimum latency.

---

## 4. Crypto Layer

### Handshake (one-time, at Connect)

```
Client                                    Gateway (Rust)
  │                                           │
  │── HandshakeInit (0x05) ──────────────────▶│
  │   PSK-encrypted API key                   │  DB lookup: SHA-256(key)
  │   [nonce:12][ciphertext+tag:N]            │
  │                                           │
  │◀─ Challenge (0x06) ──────────────────────│
  │   [eph_pub:32][static_pub:32][sig:64]     │  X25519 ephemeral key pair generated
  │   Client verifies Ed25519 signature       │  Ed25519 sign(eph_pub)
  │                                           │
  │── HandshakeResponse (0x07) ─────────────▶│
  │   [client_pub:32]                         │
  │                                           │
  │◀─ SessionAck (0x08) ─────────────────────│
  │   [crypto_id:4][jwt_len:2][jwt]           │  HKDF derives two session keys
  │   [rc_len:2][reconnect_token]             │  + ip_migration_key
  │   First AEAD-encrypted packet             │
  │                                           │
  │  Both sides derive via HKDF-SHA256:       │
  │    EncryptKey     (this-side→other)       │
  │    DecryptKey     (other→this-side)       │
  │    ip_migration_key (HMAC for N-8)        │
  │                                           │
  ▼  All subsequent packets AEAD-encrypted    ▼
```

### Reconnect (token shortcut, v1.1 documented)

```
Client                                    Gateway (Rust)
  │                                           │
  │── ReconnectInit (0x09) ──────────────────▶│  Token lookup (atomic)
  │   [token_len:2][token:N]                  │  Optional IP-migration HMAC
  │   [proof:32]?  (N-8 IP migration)         │    proof = HMAC(ip_key, token)
  │                                           │
  │◀─ Challenge (0x06) ──────────────────────│  Server re-uses the full handshake
  │   ... (same as fresh Connect) ...         │  from this point on.
```

Success → `OnSessionAck` → `OnConnected` → (optional) auto-rejoin of `LastRoomId`.

### HKDF-SHA256 key derivation

```
IKM   = X25519(clientPriv, serverEphPub)                  // shared secret
Salt  = "RTMPE-v3-hkdf-salt-2026"                         // ASCII
Info  = "RTMPE-v3-session-key"
        + min(clientPub, serverEphPub)                    // lexicographic
        + max(clientPub, serverEphPub)

initiatorKey     = HKDF-Expand(PRK, info + 0x00, 32 bytes)
responderKey     = HKDF-Expand(PRK, info + 0x01, 32 bytes)
ipMigrationKey   = HKDF-Expand(PRK, info + 0x02, 32 bytes)   // N-8

iAmInitiator = (clientPub ≤ serverEphPub)

If iAmInitiator:
    EncryptKey = initiatorKey
    DecryptKey = responderKey
Else:
    EncryptKey = responderKey
    DecryptKey = initiatorKey
```

### Per-packet AEAD (ChaCha20-Poly1305, RFC 8439)

```
Nonce (12 bytes):
  [0..7]  = outboundNonceCounter  (u64, little-endian, starts at 0)
  [8..11] = cryptoId              (u32, little-endian, from SessionAck)

AAD (2 bytes, on encrypt):
  [0] = packetType
  [1] = flags  (WITHOUT the Encrypted bit 0x02; WITH Compressed bit 0x01
                if LZ4 compression was applied)

Plaintext on encrypt:
  [0..3] = originalSequence        (u32 LE — the original header.sequence)
  [4..]  = application payload     (possibly LZ4-compressed — see FLAG_COMPRESSED)

Ciphertext = ChaCha20-Poly1305.Seal(key=EncryptKey, nonce, AAD, plaintext)
header.sequence = nonce counter (for receiver to reconstruct nonce)
header.flags   |= 0x02          (Encrypted flag set)

On decrypt:
  nonce_counter = header.sequence
  aad_flags     = header.flags & ~0x02   (strip Encrypted flag)
  plaintext     = ChaCha20-Poly1305.Open(key=DecryptKey, nonce, aad, ciphertext)
  origSeq       = plaintext[0..3]         (restore header.sequence)
  payload       = plaintext[4..]           (may need LZ4 decompression)
```

### Nonce exhaustion

The outbound nonce counter is a `long` starting at `-1`. The first
`Interlocked.Increment` returns `0`. On reaching 2³² (`uint.MaxValue + 1`) the
SDK calls `Disconnect()` and logs an error — 2³² packets at 30 Hz is ~4.5 years
of continuous traffic, so real games never hit this. At `2³² − 1_048_576`
(~9.7 h remaining) a warning is emitted so apps can schedule a clean re-auth.

### Anti-replay

A **128-bit sliding window** per session (`REPLAY_WINDOW_SIZE = 128`) is
maintained on the gateway. The SDK does not maintain a client-side window; it
relies on the gateway to reject replayed packets. AEAD authentication failure
causes a silent client-side packet drop.

### Payload compression

`Lz4Compressor.CompressIfBeneficial` is applied to the **plaintext** before the
AEAD seal. When compression shrinks the payload, `FLAG_COMPRESSED (0x01)` is set
in both the header flags and the AAD — the gateway verifies the flag was not
tampered with. Compression is transparent to application code; receivers always
see plaintext, uncompressed packets.

---

## 5. Protocol Layer

All application-level packets use the **fixed 13-byte binary header** followed
by a variable-length payload. See [Protocol Reference](protocol.md) for full details.

### Sequence counter

`PacketBuilder` maintains a per-session sequence counter:

```csharp
// Starts at -1; first sent packet gets sequence = 0.
uint seq = (uint)Interlocked.Increment(ref _sequenceCounter);
```

The counter is reset on every `Connect()` / `Reconnect()`.

### Heartbeat

`HeartbeatManager` sends a `Heartbeat (0x03)` every `heartbeatIntervalMs`
(default 5 000 ms) and expects a `HeartbeatAck (0x04)`. Three consecutive missed
ACKs trigger a disconnect with reason `ConnectionLost`. Round-trip time (RTT) is
measured with a monotonic `Stopwatch` per heartbeat and exposed via
`NetworkManager.LastRttMs` and `OnRttUpdated`.

---

## 6. Domain Layer

### NetworkManager (singleton)

The central coordinator. Persists across scenes via `DontDestroyOnLoad`.
Owns all other managers and the connection lifecycle state machine.

**State machine:**

```
Disconnected ─Connect()──▶ Connecting ─SessionAck──▶ Connected ─CreateRoom/JoinRoom──▶ InRoom
     ▲              ▲                                    │                                │
     │              │                                    │                                │
     │              │                                    │  LeaveRoom                     │
     │              │                                    ◀────────────────────────────────┘
     │              │                                    │
     │              │                            ┌───────┼───────┐
     │              │                            ▼       ▼       ▼
     │        ┌─ Reconnecting ◀─ (timeout) ──────┘       │       │
     │        │                                          │       │
     │   ReconnectInit                              Disconnect() │
     │        │                                          │       │
     │   Challenge / SessionAck ──────▶ Connected        │       │
     │                                                   ▼       ▼
     └──────────────────────────────────────────── Disconnecting ─▶ Disconnected
```

**v1.1 event wiring helper.** `InitialiseNetwork()`, `Connect()`, and
`Reconnect()` all create a fresh `RoomManager` + `SpawnManager` pair through
`RecreateRoomAndSpawnManagers()`, ensuring the following subscriptions are
always present regardless of entry path:

- `RoomManager.OnRoomJoined` → state transition to InRoom + `RememberRoom(room)`
- `RoomManager.OnRoomCreated` → state transition to InRoom + `RememberRoom(room)`
- `RoomManager.OnRoomLeft` → state transition to Connected + `ClearAll()` on spawn registry
- `RoomManager.OnPlayerLeft` → `SpawnManager.OnPlayerLeftRoom(playerId)` (DestroyWithOwner)
- `RoomManager.OnPlayerJoined` → `SpawnManager.MarkAllVariablesDirtyForResync()` (late-join snapshot)

### RoomManager

Handles room CRUD over the reliable KCP channel. Exposes C# events for all
room lifecycle outcomes. Internally maintains a FIFO `Queue<CreateRoomOptions>`
capped at 16 pending creates (`MaxPendingCreates`).

### SpawnManager

Manages the lifecycle of networked GameObjects.

- `RegisterPrefab(id, prefab)` — maps a numeric ID to a Unity prefab.
- `Spawn(prefabId, position, rotation)` — acquires an instance (via
  `INetworkObjectPool` if installed, else `Object.Instantiate`), assigns a
  `NetworkObjectId`, broadcasts the spawn to all players in the room.
- `Despawn(objectId)` — destroys locally (or releases to the pool) and
  broadcasts to all peers.
- `MarkAllVariablesDirtyForResync()` — (v1.1) re-flags every `NetworkVariable`
  on every owned, spawned object so the next 30 Hz flush transmits full state.
- `OnPlayerLeftRoom` — despawns all objects with `DestroyWithOwner = true` for
  the disconnected player; non-`DestroyWithOwner` objects wait for a server
  ownership grant.
- `GenerateObjectId` — `(playerId & 0xFFFFFFFF) << 32 | localCounter` —
  guarantees uniqueness across players.

### OwnershipManager

Server-authoritative. Ownership can only change via a server-granted `OwnershipTransfer`
RPC. The local player cannot self-assign ownership — they send a request and wait for
the server grant.

### NetworkObjectRegistry

Thread-safe registry (`Dictionary<ulong, NetworkBehaviour>` protected by an explicit `lock`)
mapping `ulong objectId → NetworkBehaviour`.

- `Get(objectId)` returns the live entry (auto-evicts Unity-destroyed entries).
- `GetAll()` returns a defensive `IReadOnlyList<NetworkBehaviour>` snapshot taken under lock.
- `Register` / `Unregister` mutate under lock.
- `Clear()` despawns all registered objects (fires `OnNetworkDespawn` outside the lock).
- `PruneDestroyed()` (v1.1) sweeps the dictionary in one pass and evicts every
  entry whose GameObject was Unity-destroyed. Returns the count.

### NetworkBehaviour (base class)

Every networked GameObject script must extend `NetworkBehaviour` instead of `MonoBehaviour`.

Key lifecycle hooks (use `protected override`):

| Method               | Called when                                                                 |
|----------------------|-----------------------------------------------------------------------------|
| `OnNetworkSpawn()`   | Object registered on the network — initialise `NetworkVariable`s here       |
| `OnNetworkDespawn()` | Object about to be removed — unsubscribe events here                        |
| `OnOwnershipChanged(prev, next)` | Fires only when ownership actually changes                      |

### NetworkVariable

Replicated value that automatically syncs from owner → all clients at 30 Hz.
Only the owner writes `Value`; all clients receive `OnValueChanged` callbacks
dispatched on the Unity main thread. Dirty-flag tracking suppresses redundant
network traffic.

### NetworkTransform

Owner-side component that sends position/rotation/scale deltas to the server
at 30 Hz. Configure thresholds to suppress sub-threshold moves (saves bandwidth).

### NetworkTransformInterpolator

Client-side ring buffer that smooths incoming position/rotation updates into
continuous movement. Maintains a configurable delay buffer (default 100 ms) to
absorb jitter. Uses `Vector3.Lerp` and `Quaternion.Slerp`.

---

## 7. Object Lifecycle

```
  GameManager.OnConnected()
       │
       │  RegisterPrefab(id, prefab)           ← MUST be inside OnConnected
       │  Spawner.SetObjectPool(pool)          ← optional; v1.1 only
       │
  GameManager.OnRoomEntered()
       │
       │  Spawner.Spawn(id, pos, rot)
       │       │
       │       ├── [pool?] Acquire(prefabId, prefab, pos, rot)
       │       │   [else]  Instantiate(prefab, pos, rot)
       │       ├── Assign NetworkObjectId
       │       ├── NetworkObjectRegistry.Register(objectId, nb)
       │       ├── nb.SetSpawned(true) → nb.OnNetworkSpawn()
       │       └── Send SpawnRequest to gateway → broadcast to all peers
       │
  [Remote peers receive Spawn notification]
       │
       ├── Instantiate / pool.Acquire
       ├── NetworkObjectRegistry.Register
       ├── nb.SetSpawned(true)
       └── nb.OnNetworkSpawn()

  [Player disconnects]
       │
       └── For each object with DestroyWithOwner = true:
               nb.OnNetworkDespawn()
               [pool?] Release(prefabId, gameObject)
               [else]  Destroy(gameObject)
               NetworkObjectRegistry.Unregister(objectId)
```

---

## 8. Late-Join Snapshot

**Problem:** `NetworkVariable` replication is delta-based. When a new player
joins a live room, their client sees the default value for every existing
variable until the owner writes `Value` again — which never happens for static
variables (display name, max HP, level of a prop).

**Solution (v1.1):** On every `RoomManager.OnPlayerJoined` event, every
already-joined client re-flags every `NetworkVariable` it owns as dirty. The
next 30 Hz flush transmits a full snapshot to the gateway, which broadcasts it
to all room peers — including the new joiner.

```
    Existing client A                Gateway              New client B
    ───────────────                 ──────                 ────────────
           │                           │                        │
           │  PlayerJoined(B) event ◀──┤ ◀── Joined room ───────┤
           │                           │                        │
     SpawnManager.                     │                        │
     MarkAllVariablesDirtyForResync() │                        │
           │                           │                        │
   (next 30 Hz tick)                   │                        │
           │                           │                        │
           │── VariableUpdate (0x41) ─▶│                        │
           │   all dirty vars          │── broadcast ─────────▶│
           │                           │                        │
           │                           │   [full state applied] │
```

**Properties:**
- No protocol change. Uses the existing `VariableUpdate` (0x41) packet.
- No `OnValueChanged` fires on the owner during resync — the value itself did
  not change, only the dirty flag flipped.
- Worst-case latency to snapshot: one flush cycle ≈ 33 ms at 30 Hz.
- Bandwidth cost: one extra flush per join (negligible compared to
  per-second per-player traffic).

---

## 9. Reconnect Flow

### When a drop is recoverable

After the first `SessionAck`, the SDK holds a `reconnect_token` and an
`ip_migration_key`. The token is preserved across **exactly one** disconnect
scenario: three consecutive missed `HeartbeatAck` responses (`OnHeartbeatTimeout`).
In that case `CanReconnect` returns `true` and the session can resume via a
`ReconnectInit` packet — which is why an IP change (WiFi → 4G) is typically
recoverable: the new interface simply starts missing ACKs on the old session
and the SDK rolls over into `Reconnecting` once `Reconnect()` is called.

Every other exit path — explicit `Disconnect()`, a handshake / reconnect
timeout, a transport `SocketException`, a server-initiated `Disconnect`, or a
`Kicked` event — wipes the token. Apps in those cases must call
`Connect(apiKey)` with fresh credentials.

See the [DisconnectReason table in the API reference](api/index.md#disconnectreason-enum)
for the full mapping of reason → token state.

### Auto room re-join (v1.1)

When `LastRoomId` is populated (set on every successful `OnRoomJoined` /
`OnRoomCreated`), a successful `Reconnect()` → `SessionAck` automatically calls
`Rooms.JoinRoom(LastRoomId)` — **only** when
`NetworkSettings.autoRejoinLastRoomOnReconnect` is `true` (the default).

```
    App calls        SDK drops → Reconnect() → SessionAck → auto-rejoin
    ─────────         ───────────────────────────────────────────────
        │                                │
      Connect(apiKey)                    │
        │                                │
    [ OnConnected ]                      │
        │                                │
      JoinRoom("room-uuid-…")            │
        │                                │
    [ OnRoomJoined ]                     │
        │                                │
    _lastRoomId = "room-uuid-…"          │
        │                                │
    (heartbeat timeout — drop)           │
                                         │
    [ OnDisconnected(ConnectionLost) ]   │
    (token + LastRoomId preserved)       │
                                         │
      Reconnect()                        │
        │                                │
    [ OnStateChanged → Reconnecting ]    │
        │                                │
      ReconnectInit → Challenge → SessionAck
        │                                │
    [ OnConnected ]                      │
        │                                │
    autoRejoinLastRoomOnReconnect ?      │
        │                                │
    [ OnAutoRejoinAttempt("room-uuid-…") ]
        │                                │
      Rooms.JoinRoom("room-uuid-…")       │
        │                                │
    [ OnRoomJoined ]                     │
```

Apps that want custom lobby UI can disable `autoRejoinLastRoomOnReconnect` and
use `LastRoomId` / `LastRoomCode` to build their own flow.

### When the snapshot is wiped

| Event                                                            | Snapshot state | Disconnect reason |
|------------------------------------------------------------------|----------------|-------------------|
| Successful `OnRoomJoined` / `OnRoomCreated`                      | **Set** to new room | n/a |
| 3 missed `HeartbeatAck` (`OnHeartbeatTimeout`)                   | **Preserved** — `preserveReconnectToken = true` | `ConnectionLost` |
| `Rooms.LeaveRoom()` succeeds                                     | **Cleared** — explicit user intent | n/a |
| `NetworkManager.Disconnect()`                                    | **Cleared** — explicit user intent | `ClientRequest` |
| Handshake or `Reconnect()` times out (`ConnectionTimeoutRoutine`)| **Cleared** — token is unusable | `Timeout` |
| Non-recoverable transport error (`HandleTransportError`)         | **Cleared** — socket layer collapsed | `ConnectionLost` |
| Server-initiated `Disconnect` packet                             | **Cleared** — session terminated by server | `ServerRequest` |

`ConnectionLost` is therefore the only reason that can appear with **either**
preserved or cleared snapshots — distinguish at runtime by checking
`NetworkManager.CanReconnect` inside your `OnDisconnected` handler.

---

## 10. Scene Transition Handling

`NetworkManager.Awake()` subscribes to `SceneManager.sceneUnloaded` and
`SceneManager.sceneLoaded`. Both handlers call
`NetworkObjectRegistry.PruneDestroyed()`, which sweeps the registry in one
pass and removes entries whose `GameObject` has been Unity-destroyed.

```
  User code: SceneManager.LoadScene("Level2")      ← additive or single mode
       │
       ▼
  Unity destroys all scene-specific GameObjects (non-DontDestroyOnLoad)
       │
       ▼
  SceneManager.sceneUnloaded event
       │
       ▼
  NetworkManager.HandleSceneUnloaded(scene)
       │
       ▼
  _spawnManager.Registry.PruneDestroyed()
       │
       └── removes entries where gameObject == null (Unity-destroyed)
           (does NOT fire OnNetworkDespawn — the reference is unusable)

  SceneManager.sceneLoaded event (single-mode only)
       │
       ▼
  Second PruneDestroyed() pass — covers the case where the NEW scene's
  Awake loads triggered further destruction.
```

**Apps that want server-side cleanup** of scene-specific networked objects
should call `Spawner.Despawn(objectId)` explicitly **before** loading the new
scene. The prune path only protects the client-side registry from holding dead
references; the gateway continues to track the objects until the next
`ClearAll()` (room leave or disconnect).

---

## 11. Object Pooling

The SDK ships with no built-in pool, matching a "pay for what you use"
philosophy — games with low spawn churn pay zero overhead. Games with heavy
churn (bullets, hit FX, short-lived props) install a custom
`INetworkObjectPool` via `SpawnManager.SetObjectPool(pool)`.

```csharp
public interface INetworkObjectPool
{
    GameObject Acquire(uint prefabId, GameObject prefab,
                       Vector3 position, Quaternion rotation);
    void Release(uint prefabId, GameObject instance);
}
```

### Routing

`SpawnManager` tracks the prefab id of every live object in a private
`Dictionary<ulong, uint>`. At despawn time the stored id is passed to
`pool.Release(prefabId, gameObject)` so the pool can sort the instance back
into the correct bucket. If the pool throws, `SpawnManager` falls back to
`Object.Destroy` to prevent a leak.

### Fallback

- When `ObjectPool == null`, `SpawnManager` uses `Object.Instantiate` /
  `Object.Destroy` (v1.0-compatible behaviour).
- When the pool's `Acquire` returns `null`, `SpawnManager` logs an error and
  falls back to `Instantiate` for that single spawn.
- When the pool's `Release` throws, `SpawnManager` logs the exception and
  destroys the instance.

### Swapping pools at runtime

Allowed. Already-live objects will be released to whichever pool is active at
despawn time. Apps that need to migrate live objects must `Despawn` then
`Spawn` explicitly.

---

## 12. Data Flow — Outbound

Example: owner moves the player → `NetworkTransform` sends a position update.

```
1. NetworkTransform.LateUpdate()
     position delta > threshold?  yes → send

2. TransformPacketBuilder.BuildStateDelta(objectId, changedMask, pos, rot, scale)
     → byte[] payload  (48 bytes max, little-endian floats)

3. PacketBuilder.Build(PacketType.StateSync, payload)
     → byte[] rawPacket  [header:13][payload:N]

4. NetworkManager.EncryptAndSend(rawPacket)
     Lz4Compressor.CompressIfBeneficial(payload) → maybe-compressed payload
     AAD          = [packetType, flags & ~0x02]    (flags may include 0x01 = Compressed)
     nonce        = [counter:8 LE][cryptoId:4 LE]
     plaintext    = [origSeq:4 LE][payload]
     ciphertext   = ChaCha20-Poly1305.Seal(EncryptKey, nonce, AAD, plaintext)
     header.seq   = nonce counter
     header.flags |= 0x02
     → encrypted byte[]

5. NetworkThread queues the packet; background thread calls
     NetworkTransport.Send(encrypted)
     → UdpTransport (or custom) → kernel socket / WebSocket → wire → Gateway
```

---

## 13. Data Flow — Inbound

Example: remote player moves → SDK receives a StateSync (0x40) packet.

```
1. NetworkThread (background)
     transport.Poll(0) + transport.Receive(buf) → raw bytes

2. PacketParser.ParseHeader(raw)
     → PacketHeader { type, flags, sequence, payloadLen }

3. NetworkManager.DecryptInboundPacket(header, raw)
     Is FLAG_ENCRYPTED set?  yes →
     nonce     = [header.sequence:8 LE][cryptoId:4 LE]
     aad_flags = flags & ~0x02
     AAD       = [packetType, aad_flags]
     plaintext = ChaCha20-Poly1305.Open(DecryptKey, nonce, AAD, ciphertext)
     origSeq   = plaintext[0..3]   → restore header.sequence
     payload   = plaintext[4..]
     Is FLAG_COMPRESSED set?  yes → Lz4Compressor.Decompress(payload)

4. MainThreadDispatcher.Enqueue — cross-thread hop to Unity main thread

5. NetworkManager.ProcessPacket(decryptedPacket)   [main thread]
     Route by PacketType:
       0x40 (StateSync)       → HandleStateSyncPacket(payload)
       0x41 (VariableUpdate)  → HandleVariableUpdatePacket(payload)
       0x30 (Spawn)           → OnSpawnPacket(payload)
       0x50 (Rpc)             → OnRpcRequest(payload)
       …

6. TransformPacketParser.TryParseStateDelta(payload)
     → objectId, changedMask, position, rotation, scale

7. NetworkObjectRegistry.Get(objectId) → nb
   NetworkTransformInterpolator.AddState(timestamp, pos, rot, scale)

8. [next Update()] NetworkTransformInterpolator.TryInterpolate()
     Slerp between buffered states → smooth movement applied to Transform
```

---

*RTMPE SDK 1.1.0 — [Getting Started](getting-started.md) — [Protocol Reference](protocol.md) — [API Reference](api/index.md)*
