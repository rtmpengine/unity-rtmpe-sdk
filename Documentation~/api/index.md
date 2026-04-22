# RTMPE SDK — C# API Reference

> SDK Version: `com.rtmpe.sdk 1.1.0`
> Namespaces: `RTMPE.Core` · `RTMPE.Rooms` · `RTMPE.Rpc` · `RTMPE.Sync` · `RTMPE.Transport`

---

## Table of Contents

- [NetworkManager](#networkmanager)
- [RoomManager](#roommanager)
- [SpawnManager](#spawnmanager)
- [INetworkObjectPool](#inetworkobjectpool)
- [OwnershipManager](#ownershipmanager)
- [NetworkBehaviour](#networkbehaviour)
- [NetworkTransform](#networktransform)
- [NetworkTransformInterpolator](#networktransforminterpolator)
- [NetworkVariable types](#networkvariable-types)
- [NetworkObjectRegistry](#networkobjectregistry)
- [NetworkSettings](#networksettings)
- [CreateRoomOptions](#createroomoptions)
- [JoinRoomOptions](#joinroomoptions)
- [RoomInfo](#roominfo)
- [PlayerInfo](#playerinfo)
- [NetworkState enum](#networkstate-enum)
- [DisconnectReason enum](#disconnectreason-enum)
- [IDamageable interface](#idamageable-interface)
- [NetworkTransport (abstract)](#networktransport-abstract)
- [UdpTransport](#udptransport)

---

## NetworkManager

**Namespace:** `RTMPE.Core`
**Inherits:** `MonoBehaviour`
**Pattern:** Singleton — access via `NetworkManager.Instance`

`NetworkManager` is the central coordinator of the SDK. It owns the connection
lifecycle, crypto session, heartbeat, and all sub-managers. It persists across
scenes via `DontDestroyOnLoad` and subscribes to `SceneManager.sceneUnloaded` /
`sceneLoaded` to prune the `NetworkObjectRegistry` after a scene transition.
Place it on a GameObject **only in the boot scene**.

### Static members

```csharp
// Returns the singleton instance, or null after OnApplicationQuit.
static NetworkManager Instance { get; }

// Thread-safe null check — no side effects.
static bool HasInstance { get; }
```

### Transport factory (pluggable)

A static hook that lets apps replace the built-in `UdpTransport` — used by WebGL
builds (WebSocket transport) and integration tests (mock transport). Install
before the first `Connect()` call. A `null` return or a thrown exception from
the factory logs a warning and falls back to `UdpTransport`.

```csharp
// Delegate signature. Receives the active NetworkSettings.
delegate NetworkTransport TransportFactoryFn(NetworkSettings settings);

// Install / clear. Install before Connect(); changing mid-session does not
// re-create the live transport — Disconnect() first.
static void SetTransportFactory(TransportFactoryFn factory)
static void ClearTransportFactory()

// True when a custom factory is installed.
static bool HasCustomTransportFactory { get; }
```

### Connection

```csharp
// Begin the handshake with the RTMPE gateway.
// Must be called from the Disconnected state.
// apiKey — your API key from the RTMPE developer dashboard.
void Connect(string apiKey)

// Shortcut reconnect using a previously-issued reconnect token.
// Returns true if a reconnect attempt was scheduled; false if no token is
// held or the manager is not in the Disconnected state.
// On a successful SessionAck, if LastRoomId is populated and
// NetworkSettings.autoRejoinLastRoomOnReconnect is true, the SDK auto-calls
// Rooms.JoinRoom(LastRoomId).
bool Reconnect()

// Gracefully close the connection.
// Sends a Disconnect packet, drains the socket, then closes the UDP socket.
// Clears the reconnect token and last-room snapshot.
void Disconnect()
```

### State

```csharp
// Current connection state (see NetworkState enum).
NetworkState State { get; }

// True when State is Connected or InRoom.
bool IsConnected { get; }

// True when State is InRoom.
bool IsInRoom { get; }
```

### Identity & tokens

```csharp
// Gateway session ID (numeric) extracted from the JWT sub claim.
// Valid after SessionAck — fire OnConnected.
ulong LocalPlayerId { get; }

// Room-scoped player UUID (e.g. "a1b2c3d4-…"). Populated when RoomManager
// receives a successful Create/Join response. Used by NetworkBehaviour.IsOwner.
string LocalPlayerStringId { get; }

// HS256 JWT bearer token issued at SessionAck. Use with Room Service REST API.
string JwtToken { get; }

// Reconnect token issued at SessionAck. Non-null whenever a token is held and
// not yet consumed.
string ReconnectToken { get; }

// True when the SDK holds a valid reconnect token.
bool CanReconnect { get; }
```

### Last-room snapshot (v1.1)

```csharp
// RoomInfo.RoomId of the most recently active room. Preserved across a
// token-preserving ClearSessionData so Reconnect() can auto-rejoin.
// Null when no room has been joined, after an explicit Disconnect(), or
// after LeaveRoom().
string LastRoomId { get; }

// RoomInfo.RoomCode (human-readable) paired with LastRoomId.
// Same lifetime as LastRoomId.
string LastRoomCode { get; }
```

### Round-trip time

```csharp
// Round-trip time in milliseconds, measured per HeartbeatAck.
// -1.0f before the first HeartbeatAck arrives.
float LastRttMs { get; }
```

### Sub-managers

```csharp
// Room CRUD operations and events.
RoomManager Rooms { get; }

// Spawn / Despawn networked GameObjects (+ optional INetworkObjectPool).
SpawnManager Spawner { get; }
```

### Events

All events are dispatched on the **Unity main thread** via `MainThreadDispatcher`.

```csharp
// Fired when the AEAD session is fully established (after SessionAck).
event Action OnConnected

// Fired on every state transition, including Connecting / Reconnecting /
// Disconnecting. Arguments are (previous, current).
event Action<NetworkState, NetworkState> OnStateChanged

// Fired when the connection closes for any reason.
event Action<DisconnectReason> OnDisconnected

// Fired when the handshake fails before OnConnected.
event Action<string> OnConnectionFailed   // string = human-readable reason

// Fired after each successful HeartbeatAck. RTT in milliseconds.
event Action<float> OnRttUpdated

// Fired when a Data (0x10) or StateSync (0x40) packet is received.
// The argument is the full decrypted packet (header + payload).
event Action<byte[]> OnDataReceived

// Fired when the server acknowledges a reliable Data packet.
// Reserved for retransmit-suppression hooks.
event Action OnDataAcknowledged

// v1.1 — fired when, after a successful Reconnect(), the SDK begins an
// automatic rejoin of LastRoomId. Outcome observable via the usual
// Rooms.OnRoomJoined / Rooms.OnRoomError events.
// Not fired when autoRejoinLastRoomOnReconnect is false or LastRoomId is null.
event Action<string> OnAutoRejoinAttempt
```

### Obsolete events (v1.0 compatibility shims)

```csharp
// [Obsolete] Use Rooms.OnRoomJoined / Rooms.OnRoomCreated instead.
event Action<ulong> OnJoinedRoom

// [Obsolete] Use Rooms.OnRoomLeft instead.
event Action<ulong> OnLeftRoom
```

### Inspector fields

| Field      | Type              | Default | Description                                   |
|------------|-------------------|---------|-----------------------------------------------|
| `Settings` | `NetworkSettings` | `null`  | Assign your `RTMPESettings` asset here        |

---

## RoomManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Rooms`

Handles room creation, joining, leaving, and listing. All operations travel
over the reliable KCP channel (AEAD-encrypted once the session is established)
and produce an event callback.

### Operations

```csharp
// Create a new room. Fires OnRoomCreated on success, OnRoomError on failure.
// The create queue is capped at 16 in-flight requests.
void CreateRoom(CreateRoomOptions options = null)

// Join an existing room by its UUID.
// Fires OnRoomJoined on success, OnRoomError on failure.
void JoinRoom(string roomId, JoinRoomOptions options = null)

// Join an existing room by its 6-character join code (e.g. "XKCD42").
// Fires OnRoomJoined on success, OnRoomError on failure.
void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)

// Leave the current room. Fires OnRoomLeft.
void LeaveRoom()

// Request a list of rooms. Fires OnRoomListReceived with the results.
// publicOnly — when true, returns only rooms marked as public.
void ListRooms(bool publicOnly = true)
```

### State

```csharp
// The current room, or null when not in a room.
RoomInfo CurrentRoom { get; }

// True when the local player is currently in a room.
bool IsInRoom { get; }
```

### Events

```csharp
// Room created successfully. RoomInfo contains id, code, name, playerCount.
event Action<RoomInfo> OnRoomCreated

// Room joined successfully.
event Action<RoomInfo> OnRoomJoined

// Local player left the room.
event Action OnRoomLeft

// Another player joined the current room.
// The SDK auto-calls SpawnManager.MarkAllVariablesDirtyForResync() on this event
// so the joiner receives a full NetworkVariable snapshot within one 30 Hz tick.
event Action<PlayerInfo> OnPlayerJoined

// Another player left the room. Receives the player UUID string.
event Action<string> OnPlayerLeft

// Room list received after a call to ListRooms().
event Action<RoomInfo[]> OnRoomListReceived

// A room operation failed. The string contains a diagnostic description.
event Action<string> OnRoomError
```

---

## SpawnManager

**Namespace:** `RTMPE.Core`
**Access:** `NetworkManager.Instance.Spawner`

Manages the lifecycle of networked GameObjects. All methods must be called
from the **Unity main thread**. A fresh `SpawnManager` is created on every
`Connect()` / `Reconnect()` — prefab registrations and pool installs made
before `Connect()` are therefore wiped; register them inside `OnConnected()`.

### Prefab registration

```csharp
// Map a numeric prefab ID to a Unity prefab.
// IMPORTANT: Call this inside OnConnected(), not before Connect().
void RegisterPrefab(uint prefabId, GameObject prefab)

// Remove a prefab mapping. Returns true if the ID was registered.
bool UnregisterPrefab(uint prefabId)

// Returns true if a prefab is registered for this ID.
bool HasPrefab(uint prefabId)
```

### Spawn / Despawn

```csharp
// Instantiate the prefab registered as prefabId (via pool if installed),
// register it on the network, send a SpawnRequest, and broadcast to all peers.
// Returns the NetworkBehaviour of the new GameObject, or null if prefabId is
// not registered or the prefab has no NetworkBehaviour component.
// Call only after OnRoomCreated / OnRoomJoined fires.
// ownerPlayerId: optional override; defaults to NetworkManager.LocalPlayerStringId.
NetworkBehaviour Spawn(
    uint prefabId,
    Vector3 position,
    Quaternion rotation,
    string ownerPlayerId = null)

// Broadcast a despawn to all peers, unregister, and either:
//   - call INetworkObjectPool.Release(prefabId, gameObject) when a pool is installed
//   - call UnityEngine.Object.Destroy(gameObject) otherwise.
void Despawn(ulong networkObjectId)
```

### Object pool (v1.1)

```csharp
// Install a pluggable pool. From the next spawn onwards all Instantiate /
// Destroy calls route through the pool. Pass null to revert.
void SetObjectPool(INetworkObjectPool pool)

// Remove any installed pool.
void ClearObjectPool()

// The currently-installed pool, or null when none is set.
INetworkObjectPool ObjectPool { get; }
```

### Late-join resync (v1.1)

```csharp
// Mark every NetworkVariable on every locally-owned, spawned object as dirty
// so the next 30 Hz flush retransmits its current value. Auto-wired to
// RoomManager.OnPlayerJoined — apps rarely call this directly.
public void MarkAllVariablesDirtyForResync()
```

### Teardown

```csharp
// Destroy (or release to pool) all spawned objects. Called on room leave
// and on disconnect. Fires NetworkBehaviour.OnNetworkDespawn for each.
public void ClearAll()
```

### Object ID generation

```csharp
// Internal: objectId = (LocalPlayerId & 0xFFFFFFFF) << 32 | (localCounter++)
// Guarantees uniqueness across up to 16 concurrent players without a server round-trip.
```

---

## INetworkObjectPool

**Namespace:** `RTMPE.Core`
**Since:** v1.1.0

Contract for plugging a custom object pool into `SpawnManager`. Install via
`SpawnManager.SetObjectPool(pool)`. When no pool is installed, `SpawnManager`
falls back to `UnityEngine.Object.Instantiate` / `UnityEngine.Object.Destroy`
(zero overhead, v1.0-compatible behaviour).

```csharp
public interface INetworkObjectPool
{
    // Acquire a live, active GameObject for the requested prefab.
    // MUST NOT return null on success — SpawnManager treats null as a
    // contract violation (logs an error and falls back to Instantiate).
    GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation);

    // Release the instance back to the pool on despawn. The pool should
    // typically deactivate the GameObject and keep it for reuse.
    // prefabId may be uint.MaxValue if SpawnManager could not recover the
    // original prefab id — implementations should then Destroy the instance.
    void Release(uint prefabId, GameObject instance);
}
```

### Implementation contract

- All calls happen on the Unity main thread. Implementations need not be thread-safe.
- `Acquire` should reactivate the GameObject (`SetActive(true)`) if needed —
  `SpawnManager` also does this defensively after a successful acquire.
- Exceptions thrown from `Release` are caught by `SpawnManager`, logged, and
  the instance is destroyed as a fallback.

---

## OwnershipManager

**Namespace:** `RTMPE.Core`
**Access:** via `NetworkBehaviour.IsOwner` and `RequestOwnershipTransfer()`

Manages object ownership. Ownership is **server-authoritative** — the local client
cannot self-assign ownership; it sends a request and waits for a server grant.

```csharp
// Request the server to transfer ownership of objectId to newOwnerId.
// The server validates the request and broadcasts an OwnershipTransfer RPC
// to all clients.
void RequestOwnershipTransfer(ulong objectId, string newOwnerId)

// Called internally when the server grants an OwnershipTransfer.
// Updates local ownership records and fires IsOwner change on the affected object.
void ApplyOwnershipGrant(ulong objectId, string newOwnerId)

// Snapshot of live objects owned by a given player UUID.
IReadOnlyList<NetworkBehaviour> GetObjectsOwnedBy(string playerId)
```

---

## NetworkBehaviour

**Namespace:** `RTMPE.Core`
**Inherits:** `MonoBehaviour`

Base class for every script on a networked GameObject. Extend this instead of
`MonoBehaviour` for any script that must sync state across players.

### Properties

```csharp
// Server-assigned unique ID for this network object.
ulong NetworkObjectId { get; }

// UUID string of the owning player.
string OwnerPlayerId { get; }

// True only on the client that owns this object.
// Guard all Input.* reads and NetworkVariable.Value writes with:  if (!IsOwner) return;
bool IsOwner { get; }

// True after the object is registered on the network (after OnNetworkSpawn).
bool IsSpawned { get; }

// When true (default), this object is automatically despawned on all clients
// when the owning player disconnects.
// Set to false to keep the object alive after the owner leaves.
bool DestroyWithOwner { get; set; }   // settable property — do NOT use 'override'
```

### Override points

```csharp
// Called after the object is registered on the network.
// Initialize all NetworkVariable instances here — NOT in Awake() or Start().
protected virtual void OnNetworkSpawn() { }

// Called before the object is removed from the network.
// Unsubscribe all NetworkVariable.OnValueChanged events here.
protected virtual void OnNetworkDespawn() { }

// Called when ownership changes. Fires only on actual owner change.
protected virtual void OnOwnershipChanged(string previousOwner, string newOwner) { }
```

> Use `protected override void OnNetworkSpawn()` — not `protected new` or `public override`.

---

## NetworkTransform

**Namespace:** `RTMPE.Sync`
**Inherits:** `NetworkBehaviour`
**Attach to:** any prefab that should sync its position / rotation / scale.

`NetworkTransform` reads the `Transform` of its `GameObject` each frame, compares
against the last-sent values, and sends a `StateSync` (0x40) packet when the delta
exceeds the configured threshold.

Only the **owner** sends updates. Remote clients receive updates and feed them to
`NetworkTransformInterpolator`.

### Inspector fields

| Field                | Type    | Default | Description |
|----------------------|---------|---------|-------------|
| `SyncPosition`       | `bool`  | `true`  | Send position updates |
| `SyncRotation`       | `bool`  | `true`  | Send rotation updates |
| `SyncScale`          | `bool`  | `false` | Send scale updates (enable only if scale changes) |
| `PositionThreshold`  | `float` | `0.01`  | Minimum metres delta before sending position |
| `RotationThreshold`  | `float` | `0.1`   | Minimum degrees delta before sending rotation |

---

## NetworkTransformInterpolator

**Namespace:** `RTMPE.Sync`
**Inherits:** `MonoBehaviour`
**Attach to:** same prefab as `NetworkTransform`.

Maintains a ring buffer of received `TransformState` snapshots and smoothly
interpolates between them each frame.

### Inspector fields

| Field               | Type    | Default | Description |
|---------------------|---------|---------|-------------|
| `BufferSize`        | `int`   | `10`    | Number of snapshots to retain |
| `InterpolationDelay`| `float` | `0.1`   | Seconds behind latest snapshot — absorbs jitter |
| `InterpolateScale`  | `bool`  | `false` | Match your `NetworkTransform.SyncScale` setting |

### Notes

- Position uses `Vector3.Lerp`.
- Rotation uses `Quaternion.Slerp`.
- Snapshots with `timestamp ≤ latestTimestamp` are discarded (monotonic guard).
- `TryInterpolate()` is a no-op when fewer than 2 snapshots are available.

---

## NetworkVariable types

**Namespace:** `RTMPE.Sync`

All `NetworkVariable<T>` types share the same contract:

```csharp
// Constructor
new NetworkVariableXxx(NetworkBehaviour owner, ushort variableId, T initialValue)

// Read (any client, any time after OnNetworkSpawn)
T Value { get; }

// Write (owner only, after OnNetworkSpawn)
T Value { set; }

// Subscribe to replicated changes (runs on Unity main thread, all clients)
event Action<T, T> OnValueChanged   // (previousValue, newValue)

// Clear the dirty flag — called internally after flush. Apps rarely call this.
void MarkClean()
```

**Rules:**
1. Create inside `OnNetworkSpawn()` — never in `Awake()` / `Start()`.
2. `variableId` must be **unique within each component** (0, 1, 2 …).
   Different components on the same GameObject have separate ID namespaces.
3. Only the owner writes `Value`. All clients read and react via `OnValueChanged`.
4. Store delegate references before subscribing so you can unsubscribe in `OnNetworkDespawn()`.

### Available types

| Class                        | T             | Wire size          |
|------------------------------|---------------|--------------------|
| `NetworkVariableInt`         | `int`         | 4 bytes (LE i32)   |
| `NetworkVariableFloat`       | `float`       | 4 bytes (LE f32)   |
| `NetworkVariableBool`        | `bool`        | 1 byte             |
| `NetworkVariableVector3`     | `Vector3`     | 12 bytes (3 × LE f32) |
| `NetworkVariableQuaternion`  | `Quaternion`  | 16 bytes (4 × LE f32) |
| `NetworkVariableString`      | `string`      | 2 B length + UTF-8 |

### Late-join snapshot behaviour (v1.1)

When another player joins the current room, `SpawnManager` automatically
re-flags every `NetworkVariable` on every locally-owned, spawned object so
the next 30 Hz flush transmits its current value. The joiner therefore sees
the correct variable values within ~33 ms instead of waiting for the next
value change. `OnValueChanged` does **not** fire on the owner during a
resync — only the dirty flag flips.

### Example — correct subscribe / unsubscribe pattern

```csharp
public class Fighter : NetworkBehaviour
{
    private NetworkVariableInt _health;
    private Action<int, int>   _onHealthChanged;    // stored reference

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, variableId: (ushort)0, initialValue: 100);

        _onHealthChanged = (prev, next) => UpdateHealthBar(next);
        _health.OnValueChanged += _onHealthChanged; // subscribe
    }

    protected override void OnNetworkDespawn()
    {
        if (_health != null)
            _health.OnValueChanged -= _onHealthChanged; // unsubscribe with same reference
    }
}
```

---

## NetworkObjectRegistry

**Namespace:** `RTMPE.Core`

Thread-safe map of `ulong objectId → NetworkBehaviour`, protected by an
explicit `lock`. Managed internally by `SpawnManager`. Provides query,
enumeration, and eviction access.

```csharp
// Look up by object ID. Auto-evicts the entry if the GameObject has been
// Unity-destroyed (returns null in that case).
NetworkBehaviour Get(ulong objectId)

// Returns a read-only snapshot of all currently registered live objects.
// Safe to iterate — the snapshot is taken under lock and excludes destroyed entries.
IReadOnlyList<NetworkBehaviour> GetAll()

// Register an object. If a different object is already registered under the
// same NetworkObjectId, it is despawned (SetSpawned(false) fires) before
// being evicted.
void Register(NetworkBehaviour obj)

// Remove the entry for the given object ID, if present.
void Unregister(ulong objectId)

// v1.1 — Sweep the dictionary in one pass and evict every entry whose
// GameObject was Unity-destroyed. Returns the number of evicted entries.
// Does NOT fire OnNetworkDespawn (the managed reference is unusable once
// Unity has destroyed the GameObject).
// NetworkManager calls this automatically after sceneUnloaded / sceneLoaded.
int PruneDestroyed()

// Destroy every registered object (fires SetSpawned(false) on each).
// Called on room leave and disconnect.
void Clear()
```

---

## NetworkSettings

**Namespace:** `RTMPE.Core`
**Inherits:** `ScriptableObject`

Create via **right-click → Create → RTMPE → Settings** in the Project panel.
Assign to `NetworkManager.Settings` in the Inspector.

These are **serialized public fields** (not C# properties) on a `ScriptableObject`.
Set them in the Unity Inspector or assign them in code by field name.

| Field (camelCase)              | Type     | Default     | Description |
|--------------------------------|----------|-------------|-------------|
| `serverHost`                   | `string` | `"127.0.0.1"` | RTMPE Gateway hostname or IP |
| `serverPort`                   | `int`    | `7777`      | UDP port |
| `heartbeatIntervalMs`          | `int`    | `5000`      | Milliseconds between Heartbeat packets |
| `connectionTimeoutMs`          | `int`    | `10000`     | Milliseconds before handshake times out |
| `tickRate`                     | `int`    | `30`        | Must match the server room-service config |
| `autoRejoinLastRoomOnReconnect`| `bool`   | `true`      | v1.1 — auto-call `Rooms.JoinRoom(LastRoomId)` after a successful token-based Reconnect() |
| `sendBufferBytes`              | `int`    | `4096`      | UDP socket SO_SNDBUF |
| `receiveBufferBytes`           | `int`    | `4096`      | UDP socket SO_RCVBUF |
| `networkThreadBufferBytes`     | `int`    | `8192`      | Background thread read buffer |
| `enableDebugLogs`              | `bool`   | `false`     | Unity Console tracing — set true only in development |
| `apiKeyPskHex`                 | `string` | `""`        | 64-char hex PSK — copy from the RTMPE dashboard |
| `pinnedServerPublicKeyHex`     | `string` | `""`        | 64-char hex — optional server cert pinning |

---

## CreateRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class CreateRoomOptions
{
    // Display name shown in room lists. Max 64 bytes UTF-8. Default: "" (server assigns a name).
    public string Name { get; set; } = string.Empty;

    // Max players allowed. Range: 1–16. 0 = server default (16).
    public int MaxPlayers { get; set; } = 0;

    // Whether the room appears in public room listings. Default: true.
    public bool IsPublic { get; set; } = true;
}
```

---

## JoinRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class JoinRoomOptions
{
    // Name displayed to other players in the room. Max 32 bytes UTF-8. Default: "Player".
    public string DisplayName { get; set; }
}
```

---

## RoomInfo

**Namespace:** `RTMPE.Rooms`

Received in `OnRoomCreated`, `OnRoomJoined`, and `OnRoomListReceived`.

```csharp
public sealed class RoomInfo
{
    public string      RoomId      { get; }   // UUID — use for JoinRoom()
    public string      RoomCode    { get; }   // 6-char join code, e.g. "XKCD42" — use for JoinRoomByCode()
    public string      Name        { get; }   // Display name
    public string      State       { get; }   // "waiting" | "playing" | "finished"
    public int         PlayerCount { get; }   // Current number of players
    public int         MaxPlayers  { get; }   // Maximum capacity
    public bool        IsPublic    { get; }   // Appears in public room lists
    public PlayerInfo[] Players    { get; }   // Player roster snapshot (may be empty for list responses)
}
```

---

## PlayerInfo

**Namespace:** `RTMPE.Rooms`

Received in `RoomManager.OnPlayerJoined`.

```csharp
public sealed class PlayerInfo
{
    public string PlayerId    { get; }   // UUID string
    public string DisplayName { get; }   // Name set in JoinRoomOptions
    public bool   IsHost      { get; }   // True if this player created the room
    public bool   IsReady     { get; }   // True if the player has signalled ready state
}
```

---

## NetworkState enum

**Namespace:** `RTMPE.Core`

```csharp
public enum NetworkState
{
    Disconnected,    // Not connected. Call Connect() or Reconnect() from this state.
    Connecting,      // Initial handshake in progress.
    Connected,       // Session established. Can call CreateRoom / JoinRoom.
    InRoom,          // Inside a room. Can call Spawn.
    Disconnecting,   // Disconnect() called, draining socket.

    // v1.1 — token-based reconnect in progress. Transitions directly to
    // Connected on success, or Disconnected on timeout / failure.
    Reconnecting,
}
```

---

## DisconnectReason enum

**Namespace:** `RTMPE.Core`

Received in `NetworkManager.OnDisconnected`.

```csharp
public enum DisconnectReason
{
    Unknown,         // Unclassified reason.
    ClientRequest,   // You called Disconnect().
    ServerRequest,   // Server initiated the disconnect (received a Disconnect packet).
    Timeout,         // Initial handshake OR token reconnect did not complete within
                     // NetworkSettings.connectionTimeoutMs.
    ConnectionLost,  // Three consecutive missed HeartbeatAck responses OR a
                     // non-recoverable transport error (SocketException propagated
                     // from the network thread).
    Kicked,          // Server forcibly removed the player.
}
```

### Which reason fires when? Which preserves the reconnect token?

| Scenario                                                                         | Reason           | Token preserved? |
|----------------------------------------------------------------------------------|------------------|------------------|
| 3 consecutive missed `HeartbeatAck` responses                                    | `ConnectionLost` | **Yes — recoverable** |
| App calls `NetworkManager.Disconnect()`                                          | `ClientRequest`  | No               |
| Server sends a `Disconnect (0xFF)` packet                                        | `ServerRequest`  | No               |
| `Connect(apiKey)` or `Reconnect()` does not reach `SessionAck` within `connectionTimeoutMs` | `Timeout`        | No               |
| Background thread raises `SocketException`                                       | `ConnectionLost` | No               |
| Server kicks the player (game logic)                                             | `Kicked`         | No               |

> **Reconnect pattern.** Only the heartbeat-miss path preserves the token,
> because it is the only case where the client has strong evidence that the
> session is still server-side valid (no `Disconnect` packet was received, no
> socket error was raised). Check `NetworkManager.CanReconnect` in your
> `OnDisconnected` handler — if it returns `true`, call `Reconnect()`;
> otherwise call `Connect(apiKey)` with fresh credentials.

---

## IDamageable interface

**Namespace:** `RTMPE.Core`

Implement on any `NetworkBehaviour` that can receive damage via the built-in
`ApplyDamage` RPC (method_id `301`). The gateway dispatches this RPC by looking
for this interface via `GetComponentInParent<IDamageable>()`.

```csharp
public interface IDamageable
{
    // Called on all clients when an ApplyDamage RPC is received.
    // damage — always a positive integer (the gateway validates and discards damage ≤ 0).
    void ReceiveApplyDamage(int damage);
}
```

### Example

```csharp
public class PlayerHealth : NetworkBehaviour, IDamageable
{
    private NetworkVariableInt _health;

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, 0, 100);
    }

    // Called on all clients via the ApplyDamage RPC.
    public void ReceiveApplyDamage(int damage)
    {
        if (!IsOwner) return;
        _health.Value = Mathf.Max(0, _health.Value - damage);
    }
}
```

---

## NetworkTransport (abstract)

**Namespace:** `RTMPE.Transport`
**Inherits:** `IDisposable`

Abstract base for all network transports. The built-in `UdpTransport` derives
from this. Custom transports (WebSocket for WebGL, mock for tests) also derive
from it and are installed via `NetworkManager.SetTransportFactory(factory)`.

```csharp
public abstract class NetworkTransport : IDisposable
{
    // True while the underlying socket is open and ready for I/O.
    public abstract bool IsConnected { get; }

    // Local endpoint the OS assigned after Connect().
    // Null before Connect(). Used by the SDK for the HandshakeInit AAD.
    public virtual System.Net.IPEndPoint LocalEndPoint { get; }

    // Open the socket / WebSocket / mock transport.
    public abstract void Connect();

    // Close the socket. Safe to call multiple times.
    public abstract void Disconnect();

    // Send all bytes. The array is owned by the caller; implementations
    // must not retain a reference.
    public abstract void Send(byte[] data);

    // Non-blocking receive. Returns bytes written to buffer, or 0 if nothing is ready.
    // Implementations MUST return 0 immediately when no data is available.
    public abstract int Receive(byte[] buffer);

    // Non-blocking readability poll. Returns true if at least one datagram
    // is available to read.
    public abstract bool Poll(int microSeconds);

    public abstract void Dispose();
}
```

---

## UdpTransport

**Namespace:** `RTMPE.Transport`
**Inherits:** `NetworkTransport`

Built-in non-blocking UDP transport. Used by default unless a custom transport
is installed via `NetworkManager.SetTransportFactory`.

```csharp
// Constructor
UdpTransport(
    string host,
    int    port,
    int    sendBufferBytes    = 4096,
    int    receiveBufferBytes = 4096)

// Also exposed for zero-copy hot paths (e.g. ArrayPool-rented buffers).
public void Send(byte[] buffer, int offset, int count)
```

Inherits all abstract members from `NetworkTransport`. Notable behaviour:

- **IPv4-then-IPv6 fallback** on DNS resolution. IPv6-only hosts are supported.
- **Routing probe** during `Connect()` discovers the actual outgoing interface IP
  and stores it in `LocalEndPoint`. On failure (e.g. isolated test containers
  with no default route) the probe falls back to loopback and logs a warning —
  a real-server handshake will fail the AEAD AAD check as expected.
- `SocketError.WouldBlock` (no data ready) and `SocketError.ConnectionReset`
  (ICMP port-unreachable on Windows) are silently swallowed per RFC 1122.

---

*RTMPE SDK 1.1.0 — [Getting Started](../getting-started.md) — [Architecture](../architecture.md) — [Protocol Reference](../protocol.md)*
