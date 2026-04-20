# RTMPE SDK — Architecture

> SDK Version: `com.rtmpe.sdk 1.0.0`  
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
8. [Data Flow — Outbound](#8-data-flow--outbound)
9. [Data Flow — Inbound](#9-data-flow--inbound)

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
└──────────────────────────┬─────────────────────────────────────┘
                           │ encrypted byte[]
┌──────────────────────────▼─────────────────────────────────────┐
│                    INFRASTRUCTURE LAYER                        │
│   UdpTransport (socket I/O)                                    │
│   NetworkThread (background I/O loop)                          │
│   MainThreadDispatcher (Unity-thread callbacks)                │
│   ThreadSafeQueue<T>                                           │
└──────────────────────────┬─────────────────────────────────────┘
                           │ UDP datagrams
                  Network / Internet
                           │
                  RTMPE Gateway (Rust)
```

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
│  SpawnManager.Spawn/Despawn (Instantiate/Destroy)       │
│  MainThreadDispatcher.Update() — drains action queue    │
│                                                         │
│  ⚠ Never block this thread with socket calls           │
└────────────────┬────────────────────────────────────────┘
                 │  enqueues Action via MainThreadDispatcher
                 │  (up to 200 actions per frame)
┌────────────────▼────────────────────────────────────────┐
│                 BACKGROUND NETWORK THREAD               │
│  Priority: AboveNormal                                  │
│                                                         │
│  Loop:                                                  │
│    1. socket.Receive() — read up to 100 packets         │
│    2. Decrypt each packet (ChaCha20-Poly1305)           │
│    3. Parse packet header                               │
│    4. Enqueue callback to MainThreadDispatcher          │
│    5. Send any outbound packets (heartbeat, ACK, …)     │
│                                                         │
│  Started by: NetworkThread.Start()                      │
│  Stopped by: NetworkThread.Stop()                       │
│  Error handling: resets atomic flags before OnError     │
└─────────────────────────────────────────────────────────┘
```

### Thread-safety rules

| Component | Thread-safe? | Notes |
|-----------|-------------|-------|
| `NetworkManager.Connect()` | ✅ Main thread only | Call from Unity main thread |
| `NetworkManager.Disconnect()` | ✅ Main thread only | |
| `NetworkVariable.Value` (write) | ✅ Main thread only | Never from background threads |
| `NetworkVariable.Value` (read) | ✅ Any thread | Volatile read |
| `ThreadSafeQueue<T>` | ✅ Any thread | Uses `ConcurrentQueue<T>` |
| `NetworkObjectRegistry` | ✅ Any thread | `Dictionary<ulong,NetworkBehaviour>` protected by explicit `lock` |
| `PacketBuilder` sequence counter | ✅ Any thread | `Interlocked.Increment` |

---

## 3. Transport Layer

### UdpTransport

All game traffic travels over a single **UDP socket** (`System.Net.Sockets.Socket`).

| Property | Value |
|----------|-------|
| Protocol | UDP (unreliable, unordered) |
| Default port | 7777 |
| Reliable path | KCP over UDP — port 7778 |
| Send buffer | configurable — default 4 096 bytes |
| Receive buffer | configurable — default 4 096 bytes |

**Local IP discovery** — `UdpTransport` opens a temporary routing probe UDP socket
(no data sent) to discover the OS-assigned outgoing interface IP. This ensures
the correct source IP is included in the AEAD Additional Authenticated Data (AAD)
before the real socket is opened.

**Error handling:**
- `SocketError.WouldBlock` — silently ignored (non-blocking receive returned no data)
- `SocketError.ConnectionReset` — silently ignored (ICMP port-unreachable on Windows)
- All other socket errors — propagated to `NetworkThread.OnError`

### Reliable path (KCP)

Room management packets (CreateRoom, JoinRoom, LeaveRoom, ListRooms),
handshake packets, RPC calls, and spawn/despawn notifications travel over
**KCP** (port 7778) — a reliable, ordered, congestion-controlled protocol
built on top of UDP.

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
  │   [crypto_id:4][jwt_len:2][jwt][rc_len:2][rc]│  HKDF derives two keys
  │   First AEAD-encrypted packet             │
  │                                           │
  │  Both sides derive two HKDF-SHA256 keys:  │
  │    EncryptKey  (client→server direction)  │
  │    DecryptKey  (server→client direction)  │
  │                                           │
  ▼  All subsequent packets AEAD-encrypted    ▼
```

### HKDF-SHA256 key derivation

```
IKM   = X25519(clientPriv, serverEphPub)                  // shared secret
Salt  = "RTMPE-v3-hkdf-salt-2026"                        // ASCII
Info  = "RTMPE-v3-session-key"
        + min(clientPub, serverEphPub)                    // lexicographic
        + max(clientPub, serverEphPub)

initiatorKey = HKDF-Expand(PRK, info + 0x00, 32 bytes)
responderKey = HKDF-Expand(PRK, info + 0x01, 32 bytes)

iAmInitiator = (clientPub ≤ serverEphPub)  // lexicographic comparison

If iAmInitiator:
    EncryptKey = initiatorKey    // used when sending
    DecryptKey = responderKey    // used when receiving

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
  [1] = flags  (WITHOUT the Encrypted bit 0x02)

Plaintext on encrypt:
  [0..3] = originalSequence  (u32 LE — the original header.sequence)
  [4..]  = application payload

Ciphertext = ChaCha20-Poly1305.Seal(key=EncryptKey, nonce, AAD, plaintext)
header.sequence = nonce counter (for receiver to reconstruct nonce)
header.flags   |= 0x02          (Encrypted flag set)

On decrypt:
  nonce_counter = header.sequence
  aad_flags     = header.flags & ~0x02   (strip Encrypted flag)
  plaintext     = ChaCha20-Poly1305.Open(key=DecryptKey, nonce, aad, ciphertext)
  origSeq       = plaintext[0..3]         (restore header.sequence)
  payload       = plaintext[4..]
```

### Anti-replay

A **128-bit sliding window** per session (`REPLAY_WINDOW_SIZE = 128`) is maintained
on the gateway. The SDK does not maintain a client-side window — it relies on the
gateway to reject replayed packets. AEAD authentication failure causes silent packet
drop.

---

## 5. Protocol Layer

All application-level packets use the **fixed 13-byte binary header** followed
by a variable-length payload. See [Protocol Reference](protocol.md) for full details.

### Sequence counter

`PacketBuilder` maintains a per-session sequence counter (`_sequenceCounter`):

```csharp
// Internal: starts at -1; first sent packet gets sequence = 0
uint seq = (uint)Interlocked.Increment(ref _sequenceCounter);
```

The counter is reset to `-1` on reconnect via `Interlocked.Exchange`.

### Heartbeat

`HeartbeatManager` sends a `Heartbeat (0x03)` every `HeartbeatIntervalMs`
(default 5 000 ms) and expects a `HeartbeatAck (0x04)` within `ConnectionTimeoutMs`
(default 10 000 ms). Three consecutive missed ACKs trigger a disconnect with reason
`Timeout`. Round-trip time (RTT) is measured with a monotonic `Stopwatch` per heartbeat.

---

## 6. Domain Layer

### NetworkManager (singleton)

The central coordinator. Persists across scenes via `DontDestroyOnLoad`.
Owns all other managers and the connection lifecycle state machine.

**State machine:**

```
Disconnected → Connecting → Connected → InRoom → Disconnecting → Disconnected
```

### RoomManager

Handles room CRUD over the reliable KCP channel. Exposes C# events for all
room lifecycle outcomes. Internally maintains a FIFO `Queue<CreateRoomOptions>`
capped at 16 pending creates.

### SpawnManager

Manages the lifecycle of networked GameObjects.

- `RegisterPrefab(id, prefab)` — maps a numeric ID to a Unity prefab.
- `Spawn(prefabId, position, rotation)` — calls `Instantiate`, assigns `NetworkObjectId`,
  broadcasts the spawn to all players in the room.
- `Despawn(objectId)` — destroys locally and broadcasts to all peers.
- `GenerateObjectId` — `(playerId & 0xFFFFFFFF) << 32 | localCounter` — guarantees
  uniqueness across players.
- `OnPlayerLeftRoom` — despawns all objects with `DestroyWithOwner = true` for
  the disconnected player.

### OwnershipManager

Server-authoritative. Ownership can only change via a server-granted `OwnershipTransfer`
RPC. The local player cannot self-assign ownership — they send a request and wait for
the server grant.

### NetworkObjectRegistry

Thread-safe registry (`Dictionary<ulong, NetworkBehaviour>` protected by an explicit `lock`)
mapping `ulong objectId → NetworkBehaviour`.
- `GetAll()` returns a defensive `IReadOnlyList<NetworkBehaviour>` snapshot taken under lock.
- `Clear()` despawns all registered objects before clearing.
- Null-checks against Unity's destroyed-object sentinel on every lookup.

### NetworkBehaviour (base class)

Every networked GameObject script must extend `NetworkBehaviour` instead of `MonoBehaviour`.

Key lifecycle hooks (use `protected override`):

| Method | Called when |
|--------|------------|
| `OnNetworkSpawn()` | Object registered on the network — initialize `NetworkVariable`s here |
| `OnNetworkDespawn()` | Object about to be removed — unsubscribe events here |

### NetworkVariable

Replicated value that automatically syncs from owner → all clients at 30 Hz.
Only the owner writes `Value`; all clients receive `OnValueChanged` callbacks
dispatched on the Unity main thread.

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
       │
  GameManager.OnRoomEntered()
       │
       │  Spawner.Spawn(id, pos, rot)
       │       │
       │       ├── Instantiate(prefab, pos, rot)
       │       ├── Assign NetworkObjectId
       │       ├── NetworkObjectRegistry.Register(objectId, nb)
       │       ├── nb.SetSpawned(true)
       │       ├── nb.OnNetworkSpawn()          ← initialize NetworkVariables here
       │       └── Send SpawnRequest to gateway → broadcast to all peers
       │
  [Remote peers receive Spawn notification]
       │
       ├── Instantiate(prefab, pos, rot)
       ├── NetworkObjectRegistry.Register(objectId, nb)
       ├── nb.SetSpawned(true)
       └── nb.OnNetworkSpawn()
  
  [Player disconnects]
       │
       └── For each object with DestroyWithOwner = true:
               nb.OnNetworkDespawn()
               Destroy(gameObject)
               NetworkObjectRegistry.Unregister(objectId)
```

---

## 8. Data Flow — Outbound

Example: owner moves the player → `NetworkTransform` sends a position update.

```
1. NetworkTransform.LateUpdate()
     position delta > threshold?  yes → send
     
2. TransformPacketBuilder.BuildStateDelta(objectId, changedMask, pos, rot, scale)
     → byte[] payload  (48 bytes max, little-endian floats)

3. PacketBuilder.Build(PacketType.Data, payload)
     → byte[] rawPacket  [header:13][payload:N]

4. NetworkManager.EncryptAndSend(rawPacket, PacketType.Data, flags)
     AAD          = [packetType, flags & ~0x02]
     nonce        = [counter:8 LE][cryptoId:4 LE]
     plaintext    = [origSeq:4 LE][payload]
     ciphertext   = ChaCha20-Poly1305.Seal(EncryptKey, nonce, AAD, plaintext)
     header.seq   = nonce counter
     header.flags |= 0x02
     → encrypted byte[]

5. UdpTransport.Send(encrypted)
     → kernel UDP socket → wire → Gateway
```

---

## 9. Data Flow — Inbound

Example: remote player moves → SDK receives a StateSync (0x40) packet.

```
1. NetworkThread (background)
     socket.Receive() → raw bytes

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

4. Route by PacketType
     0x40 (StateSync) → HandleStateSyncPacket(payload)
     
5. TransformPacketParser.TryParseStateDelta(payload)
     → objectId, changedMask, position, rotation, scale

6. Enqueue to MainThreadDispatcher

7. [Unity main thread] MainThreadDispatcher.Update()
     NetworkObjectRegistry.TryGet(objectId) → nb
     NetworkTransformInterpolator.AddState(timestamp, pos, rot, scale)

8. NetworkTransformInterpolator.TryInterpolate()  [next Update()]
     Slerp between buffered states → smooth movement applied to Transform
```

---

*RTMPE SDK 1.0.0 — [Getting Started](getting-started.md) — [Protocol Reference](protocol.md) — [API Reference](api/index.md)*
