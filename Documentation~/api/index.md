# RTMPE SDK — C# API Reference

> SDK Version: `com.rtmpe.sdk 1.0.0`  
> Namespace: `RTMPE.Core` · `RTMPE.Rooms` · `RTMPE.Rpc` · `RTMPE.Sync` · `RTMPE.Transport`

---

## Table of Contents

- [NetworkManager](#networkmanager)
- [RoomManager](#roommanager)
- [SpawnManager](#spawnmanager)
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
- [UdpTransport](#udptransport)

---

## NetworkManager

**Namespace:** `RTMPE.Core`  
**Inherits:** `MonoBehaviour`  
**Pattern:** Singleton — access via `NetworkManager.Instance`

`NetworkManager` is the central coordinator of the SDK. It owns the connection
lifecycle, crypto session, heartbeat, and all sub-managers. It persists across
scenes via `DontDestroyOnLoad`. Place it on a GameObject **only in the boot scene**.

### Static members

```csharp
// Returns the singleton instance, or null after OnApplicationQuit.
static NetworkManager Instance { get; }

// Thread-safe null check.
static bool HasInstance { get; }
```

### Connection

```csharp
// Begin the handshake with the RTMPE gateway.
// Must be called from the Disconnected state.
// apiKey — your API key from the RTMPE developer dashboard.
void Connect(string apiKey)

// Gracefully close the connection.
// Sends a Disconnect packet, drains the socket for up to DrainDurationSecs,
// then closes the UDP socket.
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

// Numeric session ID assigned by the gateway. Valid after Connect fires OnConnected.
ulong LocalPlayerId { get; }

// Room-scoped player UUID (e.g. "a1b2c3d4-…"). Valid after CreateRoom/JoinRoom.
string LocalPlayerStringId { get; }

// Round-trip time in milliseconds. -1.0f before the first HeartbeatAck is received.
float LastRttMs { get; }
```

### Sub-managers

```csharp
// Room CRUD operations and events.
RoomManager Rooms { get; }

// Spawn / Despawn networked GameObjects.
SpawnManager Spawner { get; }
```

### Events

All events are dispatched on the **Unity main thread** via `MainThreadDispatcher`.

```csharp
// Fired when the AEAD session is fully established (after SessionAck).
event Action OnConnected

// Fired when the connection closes for any reason.
event Action<DisconnectReason> OnDisconnected

// Fired when the handshake fails before OnConnected.
event Action<string> OnConnectionFailed   // string = human-readable reason

// Fired on every state transition.
event Action<NetworkState, NetworkState> OnStateChanged   // (previous, current)

// Fired after each successful HeartbeatAck. RTT in milliseconds.
event Action<float> OnRttUpdated
```

### Inspector fields

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `Settings` | `NetworkSettings` | null | Assign your `RTMPESettings` asset here |

---

## RoomManager

**Namespace:** `RTMPE.Rooms`  
**Access:** `NetworkManager.Instance.Rooms`

Handles room creation, joining, leaving, and listing. All operations are
sent over the reliable KCP channel and produce an event callback.

### Operations

```csharp
// Create a new room. Fires OnRoomCreated on success, OnRoomError on failure.
void CreateRoom(CreateRoomOptions options = null)

// Join an existing room by its GUID.
// Fires OnRoomJoined on success, OnRoomError on failure.
void JoinRoom(string roomId, JoinRoomOptions options = null)

// Join an existing room by its short code (e.g. "XKQT").
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

// Another player joined the same room.
event Action<PlayerInfo> OnPlayerJoined

// Another player left the room. Receives that player's UUID string.
event Action<string> OnPlayerLeft

// Room list received after a call to ListRooms().
event Action<RoomInfo[]> OnRoomListReceived

// A room operation failed. The string contains a description.
event Action<string> OnRoomError
```

---

## SpawnManager

**Namespace:** `RTMPE.Core`  
**Access:** `NetworkManager.Instance.Spawner`

Manages the lifecycle of networked GameObjects. All methods must be called
from the **Unity main thread**.

### Prefab registration

```csharp
// Map a numeric prefab ID to a Unity prefab.
// IMPORTANT: Call this inside OnConnected(), not before Connect().
//            Connect() creates a fresh SpawnManager — registrations before
//            Connect() are wiped.
void RegisterPrefab(uint prefabId, GameObject prefab)

// Remove a prefab mapping. Returns true if the ID was registered.
bool UnregisterPrefab(uint prefabId)

// Returns true if a prefab is registered for this ID.
bool HasPrefab(uint prefabId)
```

### Spawn / Despawn

```csharp
// Instantiate the prefab registered as prefabId, register it on the network,
// send a SpawnRequest to the gateway, and broadcast to all room peers.
// Returns the NetworkBehaviour component of the new GameObject,
// or null if prefabId is not registered.
// Call only after OnRoomCreated / OnRoomJoined fires.
NetworkBehaviour Spawn(uint prefabId, Vector3 position, Quaternion rotation)

// Unregister, broadcast a despawn to all peers, and Destroy the GameObject.
// objectId — the NetworkBehaviour.NetworkObjectId value, not the component itself.
void Despawn(ulong networkObjectId)
```

### Object ID generation

```csharp
// Internal: objectId = (LocalPlayerId & 0xFFFF_FFFF) << 32 | (localCounter++)
// This guarantees uniqueness across up to 16 concurrent players without a server round-trip.
```

---

## OwnershipManager

**Namespace:** `RTMPE.Core`  
**Access:** via `NetworkBehaviour.IsOwner` and `RequestOwnershipTransfer()`

Manages object ownership. Ownership is **server-authoritative** — the local client
cannot self-assign ownership; it sends a request and waits for a server grant.

```csharp
// Request the server to transfer ownership of objectId to newOwnerId.
// The server validates the request and fires an OwnershipTransfer RPC to all clients.
void RequestOwnershipTransfer(ulong objectId, string newOwnerId)

// Called internally when the server grants an OwnershipTransfer.
// Updates local ownership records and fires IsOwner change on the affected object.
void ApplyOwnershipGrant(ulong objectId, string newOwnerId)
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
```

> Use `protected override void OnNetworkSpawn()` — not `protected new` or `public override`.

---

## NetworkTransform

**Namespace:** `RTMPE.Sync`  
**Inherits:** `MonoBehaviour`  
**Attach to:** any prefab that should sync its position/rotation/scale.

`NetworkTransform` reads the `Transform` of its `GameObject` each frame, compares
against the last-sent values, and sends a `StateDelta` (0x40) packet when the delta
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
new NetworkVariableXxx(NetworkBehaviour owner, int variableId, T initialValue)

// Read (any client, any time after OnNetworkSpawn)
T Value { get; }

// Write (owner only, after OnNetworkSpawn)
T Value { set; }

// Subscribe to replicated changes (runs on Unity main thread, all clients)
event Action<T, T> OnValueChanged   // (previousValue, newValue)
```

**Rules:**
1. Create inside `OnNetworkSpawn()` — never in `Awake()` / `Start()`.
2. `variableId` must be **unique within each component** (0, 1, 2 …).
   Different components on the same GameObject have separate ID namespaces.
3. Only the owner writes `Value`. All clients read and react via `OnValueChanged`.
4. Store delegate references before subscribing so you can unsubscribe in `OnNetworkDespawn()`.

### Available types

| Class | T | Wire size |
|-------|----|-----------|
| `NetworkVariableInt` | `int` | 4 bytes (LE i32) |
| `NetworkVariableFloat` | `float` | 4 bytes (LE f32) |
| `NetworkVariableBool` | `bool` | 1 byte |
| `NetworkVariableVector3` | `Vector3` | 12 bytes (3 × LE f32) |
| `NetworkVariableQuaternion` | `Quaternion` | 16 bytes (4 × LE f32) |
| `NetworkVariableString` | `string` | variable (UTF-8) |

### Example — correct subscribe / unsubscribe pattern

```csharp
public class Fighter : NetworkBehaviour
{
    private NetworkVariableInt _health;
    private Action<int, int>   _onHealthChanged;    // ← stored reference

    protected override void OnNetworkSpawn()
    {
        _health = new NetworkVariableInt(this, variableId: 0, initialValue: 100);

        _onHealthChanged = (prev, next) => UpdateHealthBar(next);
        _health.OnValueChanged += _onHealthChanged; // ← subscribe
    }

    protected override void OnNetworkDespawn()
    {
        if (_health != null)
            _health.OnValueChanged -= _onHealthChanged; // ← unsubscribe with same reference
    }
}
```

---

## NetworkObjectRegistry

**Namespace:** `RTMPE.Core`

Thread-safe map of `ulong objectId → NetworkBehaviour`. Managed internally by
`SpawnManager`. Provides query and enumeration access.

```csharp
// Returns true and sets nb if objectId is registered and the GameObject is alive.
bool TryGet(ulong objectId, out NetworkBehaviour nb)

// Returns a snapshot array of all currently registered objects.
// Safe to iterate — will not throw if objects are despawned mid-iteration.
NetworkBehaviour[] GetAll()

// Returns true if objectId is registered.
bool Contains(ulong objectId)
```

---

## NetworkSettings

**Namespace:** `RTMPE.Core`  
**Inherits:** `ScriptableObject`

Create via **right-click → Create → RTMPE → Settings** in the Project panel.
Assign to `NetworkManager.Settings` in the Inspector.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ServerHost` | `string` | `"127.0.0.1"` | RTMPE Gateway hostname or IP |
| `ServerPort` | `int` | `7777` | UDP port |
| `HeartbeatIntervalMs` | `int` | `5000` | Milliseconds between Heartbeat packets |
| `ConnectionTimeoutMs` | `int` | `10000` | Milliseconds before handshake times out |
| `TickRate` | `int` | `30` | Must match the server room-service config |
| `SendBufferBytes` | `int` | `4096` | UDP socket SO_SNDBUF |
| `ReceiveBufferBytes` | `int` | `4096` | UDP socket SO_RCVBUF |
| `NetworkThreadBufferBytes` | `int` | `8192` | Background thread read buffer |
| `EnableDebugLogs` | `bool` | `true` | Unity Console tracing — set false in production |
| `ApiKeyPskHex` | `string` | `""` | 64-char hex PSK — copy from the RTMPE dashboard |
| `PinnedServerPublicKeyHex` | `string` | `""` | 64-char hex — optional server cert pinning |

---

## CreateRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class CreateRoomOptions
{
    // Display name shown in room lists. Max 64 bytes UTF-8. Default: "Room".
    public string Name { get; set; }

    // Max players allowed. Range: 1–16. 0 = server default (16).
    public int MaxPlayers { get; set; }

    // Whether the room appears in public room listings.
    public bool IsPublic { get; set; }
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
    public string RoomId      { get; }   // GUID — use for JoinRoom()
    public string RoomCode    { get; }   // Short code, e.g. "XKQT" — use for JoinRoomByCode()
    public string Name        { get; }   // Display name
    public int    PlayerCount { get; }   // Current number of players
    public int    MaxPlayers  { get; }   // Maximum capacity
    public bool   IsPublic    { get; }   // Appears in public room lists
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
}
```

---

## NetworkState enum

**Namespace:** `RTMPE.Core`

```csharp
public enum NetworkState
{
    Disconnected,    // Not connected. Call Connect() from this state.
    Connecting,      // Handshake in progress.
    Connected,       // Session established. Can call CreateRoom / JoinRoom.
    InRoom,          // Inside a room. Can call Spawn.
    Disconnecting,   // Disconnect() called, draining socket.
}
```

---

## DisconnectReason enum

**Namespace:** `RTMPE.Core`

Received in `NetworkManager.OnDisconnected`.

```csharp
public enum DisconnectReason
{
    ClientRequest,   // You called Disconnect().
    ConnectionLost,  // Network was interrupted.
    ServerRequest,   // Server initiated the disconnect.
    Timeout,         // Heartbeat or handshake timed out.
    Kicked,          // Server forcibly removed the player.
    Unknown,         // Unclassified reason.
}
```

---

## IDamageable interface

**Namespace:** `RTMPE.Core`

Implement on any `NetworkBehaviour` that can receive damage via the built-in
`ApplyDamage` RPC. The gateway dispatches this RPC by looking for this interface
via `GetComponentInParent<IDamageable>()`.

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

## UdpTransport

**Namespace:** `RTMPE.Transport`

Low-level UDP socket wrapper. Normally used internally by `NetworkThread`.
Exposed for testing and custom transport implementations.

```csharp
// Constructor
UdpTransport(string host, int port)

// Open the UDP socket and connect to the remote endpoint.
// Discovers the local outgoing IP via a routing probe.
void Connect()

// Close the socket.
void Disconnect()

// Send raw bytes. Returns false if the socket is not connected.
bool Send(byte[] data, int length)

// Receive up to buffer.Length bytes. Returns the number of bytes read, or 0.
int Receive(byte[] buffer)

// The local endpoint assigned by the OS after Connect(). Null before Connect().
IPEndPoint LocalEndPoint { get; }

// True after Connect(), false after Disconnect().
bool IsConnected { get; }
```

---

*RTMPE SDK 1.0.0 — [Getting Started](../getting-started.md) — [Architecture](../architecture.md) — [Protocol Reference](../protocol.md)*
