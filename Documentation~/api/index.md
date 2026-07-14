# RTMPE SDK — C# API Reference

> SDK Version: `com.rtmpe.sdk 1.9.6`
> Namespaces: `RTMPE.Core` · `RTMPE.Rooms` · `RTMPE.Rpc` · `RTMPE.Sync` · `RTMPE.Transport`

---

## Table of Contents

- [NetworkManager](#networkmanager)
- [RoomManager](#roommanager)
- [LobbyManager](#lobbymanager)
- [MatchmakingManager](#matchmakingmanager)
- [Networked scenes](#networked-scenes)
- [Interest management](#interest-management)
- [SpawnManager](#spawnmanager)
- [INetworkObjectPool](#inetworkobjectpool)
- [OwnershipManager](#ownershipmanager)
- [NetworkBehaviour](#networkbehaviour)
- [Remote Procedure Calls](#remote-procedure-calls)
- [NetworkTransform](#networktransform)
- [NetworkTransformInterpolator](#networktransforminterpolator)
- [NetworkRigidbody / NetworkRigidbody2D](#networkrigidbody--networkrigidbody2d)
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
// Returns the NetworkManager placed in the scene. Returns null — with a
// one-time warning — when the scene contains none, and after OnApplicationQuit.
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

// EdDSA (Ed25519) JWT bearer token issued at SessionAck. Use with the Room
// Service REST API. Returned as an opaque RedactedString so a stray log line
// cannot leak it — call .Reveal() at the point of use to get the raw string,
// or .IsEmpty to test for presence without revealing.
RedactedString JwtToken { get; }

// Reconnect token issued at SessionAck. Non-empty whenever a token is held and
// not yet consumed. Same RedactedString wrapper as JwtToken — call .Reveal()
// for the raw value.
RedactedString ReconnectToken { get; }

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

// Lobby browser — list / filter rooms without joining one.
LobbyManager Lobby { get; }

// Matchmaking — AutoJoinOrCreate by mode / lobby.
MatchmakingManager Matchmaking { get; }

// Networked scene-loading façade — the master client drives room-wide scene
// changes; every client receives OnSceneLoadStarted. See Networked scenes.
NetworkSceneManager Scene { get; }   // null until connected (LoadScene still requires being the room's master)

// Local-player property helper: NetworkManager.LocalPlayer.SetProperty(key, value).
LocalPlayerContext LocalPlayer { get; }
```

### Master client

```csharp
// True when the local player is the current master client (host) of
// Rooms.CurrentRoom. Derived from the cached room snapshot, so it updates
// automatically on master_client_changed / host_changed. False when not in a
// room, when no host is set, or before the local player ID is known.
bool IsMasterClient { get; }
```

### Custom data channel

```csharp
// Send an application-defined message to the room. Thread-safe; AEAD-encrypted
// once the session is established. A defensive copy is taken, so the caller may
// reuse its buffer immediately. Must be called from the Unity main thread.
//
// reliable: true opts the packet into the ARQ retransmit channel — effective
// only when NetworkSettings.EmitArqSequence is enabled AND the peer negotiated
// the ArqAck capability; otherwise the packet is sent unreliably (a one-time
// advisory is logged). No-ops with a warning when not connected.
void Send(byte[] data, bool reliable = false);
```

> The receive side is the `OnDataReceived` event below — it is raised for `Data`
> (0x10) and `StateSync` (0x40) packets with the full decrypted frame.

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

// Fired when the bounded reconnect loop kicked off by Reconnect() exhausts
// NetworkSettings.maxReconnectAttempts without reaching Connected. Argument
// is the number of attempts actually made. When this fires the manager has
// transitioned back to Disconnected and cleared all session state — the
// application MUST fall back to Connect(apiKey) to recover.
event Action<int> OnReconnectFailed
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

Handles room creation, joining, leaving, listing, custom properties,
master-client transfer, and scene coordination. All operations are sent with
`FLAG_RELIABLE` (reliable delivery), AEAD-encrypted once the session is
established, and produce an event callback.

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

// Replace the current room's custom properties. Fires OnRoomPropertiesChanged
// once the server accepts and broadcasts the update.
void SetRoomProperties(IReadOnlyDictionary<string, PropertyValue> properties)

// Set a single room property by key. Convenience wrapper over SetRoomProperties.
void SetRoomProperty(string key, PropertyValue value)

// Set custom properties for a player in the room. Fires OnPlayerPropertiesChanged.
void SetPlayerProperties(string playerId, IReadOnlyDictionary<string, PropertyValue> properties)

// Request that the master-client role be transferred to targetPlayerId.
// On acceptance every client in the room receives OnMasterClientChanged.
void TransferMasterClient(string targetPlayerId)

// Request the server remove targetPlayerId from the room.
// On acceptance every client receives OnPlayerKicked.
void KickPlayer(string targetPlayerId)

// Report that the local client finished loading sceneName. When every player
// has reported the same scene, all clients receive OnAllPlayersSceneLoaded.
void ReportSceneLoaded(string sceneName)
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

// Fired after the server accepts a RoomPropertyUpdate and broadcasts the
// new state to all clients. Argument is the post-update RoomInfo snapshot;
// RoomInfo.Properties reflects the authoritative map. Subscribers that need
// a delta should diff against CurrentRoom captured BEFORE the event fires —
// RoomManager swaps CurrentRoom to the new snapshot BEFORE invoking this event.
event Action<RoomInfo> OnRoomPropertiesChanged

// Fired after the server accepts a PlayerPropertyUpdate and broadcasts the
// new state to all clients. Arguments are (playerId, updatedPlayerInfo).
event Action<string, PlayerInfo> OnPlayerPropertiesChanged

// Fired when the room's master client changes — either automatically (FIFO
// promotion after the previous master disconnected) or manually (a
// TransferMasterClient request was accepted). Arguments are
// (previousMasterId, newMasterId); either may be empty when unknown
// (e.g. initial assignment).
event Action<string, string> OnMasterClientChanged

// Fired when the host removes a player from the room via KickPlayer.
// Arguments are (kickerId, targetPlayerId). Every client in the room
// receives this event — the kicked client observes their own ID as the
// target and should treat it as an authoritative disconnect.
event Action<string, string> OnPlayerKicked

// Fired when every player in the room has reported scene-loaded readiness
// for the authoritative scene (stored in the reserved Scene property).
// Argument is the scene name that just finished loading for everyone.
event Action<string> OnAllPlayersSceneLoaded
```

---

## LobbyManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Lobby`

Browse and filter the rooms in a lobby without joining one. A fresh
`LobbyManager` is created on every `Connect()` / `Reconnect()`.

### Properties

```csharp
// Name of the lobby currently joined; "" means the Default lobby.
string CurrentLobbyName { get; }

// True while the local client is in the lobby browser.
bool IsInLobby { get; }

// Most recent room list received from the server.
IReadOnlyList<LobbyRoomInfo> Rooms { get; }
```

### Methods

```csharp
// Enter the lobby browser. "" selects the Default lobby.
// The server replies with the current room list (OnRoomListUpdated).
void JoinLobby(string lobbyName = "")

// Leave the lobby browser (fire-and-forget).
void LeaveLobby()

// Request a filtered / sorted one-shot room list (see LobbyQueryOptions).
void ListRooms(LobbyQueryOptions opts = null)
```

### Events

```csharp
// Fired when the server pushes an updated room list — after JoinLobby,
// ListRooms, or a server-side lobby change.
event Action<IReadOnlyList<LobbyRoomInfo>> OnRoomListUpdated
```

### LobbyRoomInfo (immutable)

```csharp
string RoomId      { get; }
string RoomCode    { get; }
string Name        { get; }
int    PlayerCount { get; }
int    MaxPlayers  { get; }
bool   IsPublic    { get; }
string LobbyName   { get; }
```

### LobbyQueryOptions

```csharp
string            LobbyName  { get; set; } = "";   // "" = Default lobby
int               MaxResults { get; set; } = 0;    // 1–100; 0 = server default (100)
LobbySort         SortBy     { get; set; } = LobbySort.PlayerCount;
List<LobbyFilter> Filters    { get; set; }         // null = no filter
```

- `LobbySort` — `PlayerCount` (0), `Age` (1), `Name` (2).
- `LobbyFilter` — `{ string Key; LobbyFilterOp Op; object Value; }`.
- `LobbyFilterOp` — `Eq` (0), `NotEq` (1), `Lt` (2), `Gt` (3), `LtEq` (4), `GtEq` (5).

---

## MatchmakingManager

**Namespace:** `RTMPE.Rooms`
**Access:** `NetworkManager.Instance.Matchmaking`

AutoJoinOrCreate matchmaking: the server atomically finds an open room
matching the requested mode / lobby or creates one, then joins the player.
A fresh `MatchmakingManager` is created on every `Connect()` / `Reconnect()`.

### Property

```csharp
// True while a matchmaking request is in flight.
bool IsMatchmaking { get; }
```

### Methods

```csharp
// Start matchmaking (default 30 s timeout).
// Throws InvalidOperationException if not Connected / InRoom or a request
// is already in flight; ArgumentException on an invalid Mode.
void StartMatchmaking(MatchmakingOptions options)

// Same, with an explicit timeout — clamped to (0, 300] seconds;
// double.PositiveInfinity disables the timeout.
void StartMatchmaking(MatchmakingOptions options, double timeoutSeconds)

// Abort an in-flight request. Idempotent.
void CancelFindMatch()
```

> `Tick(double)` drives the timeout clock but is called automatically by
> `NetworkManager.Update()` — applications do not call it.

### Events

```csharp
event Action<MatchmakingResult> OnMatchmakingComplete   // matched / created room
event Action<string>            OnMatchmakingFailed     // server-side failure
event Action                    OnMatchmakingCancelled  // CancelFindMatch()
event Action                    OnMatchmakingTimedOut   // timeout elapsed
```

### MatchmakingOptions

```csharp
string Mode        { get; set; } = "";   // required, non-empty
string LobbyName   { get; set; } = "";
int    MinPlayers  { get; set; } = 0;    // <= 0 → server default (2)
int    MaxPlayers  { get; set; } = 0;    // <= 0 → server default
string DisplayName { get; set; } = "";
```

### MatchmakingResult (immutable)

```csharp
string RoomId   { get; }
string RoomCode { get; }
bool   Created  { get; }   // true = a new room was created for this match
```

---

## Networked scenes

**Namespace:** `RTMPE.Rooms` (`NetworkSceneManager`)
**Access via:** `NetworkManager.Instance.Scene` (`null` until connected)

Coordinates a room-wide scene change. The **master client** calls `LoadScene`;
every client (including the master) receives `OnSceneLoadStarted` and is
responsible for actually loading the scene with Unity's own
`SceneManager.LoadSceneAsync`. Each client calls `ReportReady` when its local
load finishes; once **all** clients report, every client receives
`OnAllPlayersSceneLoaded`. A late joiner whose room already has a scene set
receives `OnSceneLoadStarted` immediately on join, so it catches up for free.

```csharp
// Master-client only. Instruct the room to switch scene. Throws
// InvalidOperationException if the caller is not in a room or not the master
// client, and ArgumentException if sceneName is null/empty.
void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single);

// Report that THIS client finished loading the current scene. No-op when not in
// a room or no scene is set.
void ReportReady();

// The room's authoritative scene name (empty until one is set).
string CurrentScene { get; }

event Action<string> OnSceneLoadStarted;       // (sceneName) — begin loading
event Action<string> OnAllPlayersSceneLoaded;  // (sceneName) — everyone is ready

enum NetworkSceneLoadMode { Single = 0, Additive = 1 }
```

The scene name is carried on the reserved `__scene` room custom property, so it
also appears as `RoomInfo.CurrentScene`. The manager never touches Unity's
`SceneManagement` itself — you drive the actual load in response to the event.

---

## Interest management

**Namespace:** `RTMPE.Rooms` (`InterestManager`)
**Inherits:** `MonoBehaviour`

Opt-in spatial culling. Attach `InterestManager` to a persistent object and
assign `TrackedTransform` to the local player; while in a room it reports the
player's position to the gateway (10 Hz by default), and the gateway restricts
room-wide broadcasts to clients whose spatial-grid neighbourhood overlaps the
source. Clients without an `InterestManager` receive every broadcast unchanged.

```csharp
// The Transform whose world position is reported. When null at a send tick, the
// last position is re-sent. (public field / Inspector)
Transform TrackedTransform;

float UpdateInterval = 0.1f;   // seconds between position sends (10 Hz)
bool  UseXzPlane     = true;   // project on XZ (3-D). Set false for 2-D / top-down (XY)

// Optional secondary receive-side filter. When > 0, inbound state for objects
// farther than this radius is discarded locally (in addition to gateway culling).
// 0 disables it. HysteresisMargin widens the leave-radius to stop boundary flap.
float ReceiveFilterRadius = 0f;
float HysteresisMargin    = 1f;

void StartTracking();          // resume reporting
void StopTracking();           // pause; gateway keeps the last interest zone
bool IsTracking { get; }
```

> Interest management is optional. Leave it off for small rooms; enable it for
> large open worlds where most objects are irrelevant to any given player.

---

## SpawnManager

**Namespace:** `RTMPE.Core`
**Access:** `NetworkManager.Instance.Spawner`

Manages the lifecycle of networked GameObjects. All methods must be called
from the **Unity main thread**. A fresh `SpawnManager` is created on every
`Connect()` / `Reconnect()`; prefab registrations are carried across the
rebuild, but pool installs are not — call `SetObjectPool()` inside
`OnConnected()`.

### Prefab registration

```csharp
// Map a numeric prefab ID to a Unity prefab.
// Registrations persist across reconnects; call once before the first Spawn().
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
// Internal (ObjectIdMath.Compose):
//   high 32 bits = MixSessionId(gatewaySessionId) — xor-fold + SplitMix64 avalanche
//   low  32 bits = per-session spawn counter
//   objectId     = (high << 32) | (counter & 0xFFFFFFFF)
// The avalanche mixing spreads ids across the 64-bit space so two players'
// object ids do not collide without a server round-trip.
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
**Access:** `NetworkManager.Instance.Spawner.Ownership` (the `SpawnManager.Ownership` property). `RequestOwnershipTransfer()` is a method on this `OwnershipManager`, not on `NetworkBehaviour`.

Manages object ownership. Ownership is **server-authoritative** — the local client
cannot self-assign ownership; it sends a request and waits for a server grant.

```csharp
// Request the server to transfer ownership of objectId to newOwnerPlayerId.
// The server validates the request and broadcasts an OwnershipTransfer RPC
// to all clients.
void RequestOwnershipTransfer(ulong objectId, string newOwnerPlayerId)

// Apply a server-decided ownership grant. The three-argument form is the
// primary entry point: serverAttested must be true for a grant that did not
// originate from a local RequestOwnershipTransfer call (an unattested grant
// with no matching outstanding request is rejected). The two-argument
// overload forwards with serverAttested: false and is kept for back-compat.
void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId, bool serverAttested)
void ApplyOwnershipGrant(ulong objectId, string newOwnerPlayerId)

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

// Called once per simulated tick on every owned, spawned NetworkBehaviour by
// the central 30 Hz tick driver — exactly one call per tick regardless of frame
// rate (a long frame fires it once per integrated tick; a short frame, zero).
// Use for fixed-cadence work: input sampling, deterministic timers.
protected virtual void OnFixedTick(float deltaTime) { }

// Client-side prediction (optional; owner only). GatherInput supplies this
// frame's input; ApplyInput deterministically re-applies one input during a
// reconciliation replay. Both are no-ops until overridden. ApplyInput MUST be
// deterministic and MUST use the passed deltaTime — never Time.deltaTime.
protected virtual InputPayload GatherInput() => default;
protected virtual void ApplyInput(InputPayload input, float deltaTime) { }
```

> Use `protected override void OnNetworkSpawn()` — not `protected new` or `public override`.

---

## Remote Procedure Calls

**Namespace:** `RTMPE.Rpc` (attribute, `RpcTarget`) · `RTMPE.Core` (the `RPC` call)

An RPC is a method one client invokes so it runs on other clients (or the
server). Declare the method with `[RtmpeRpc]` and invoke it by name with `RPC`.

```csharp
using RTMPE.Core;
using RTMPE.Rpc;

public class Weapon : NetworkBehaviour
{
    // Sender: only the owner should trigger the shot.
    public void Fire()
    {
        if (!IsOwner) return;
        RPC(nameof(FireRpc), transform.position);   // invoke by name
    }

    // Receiver: runs on every client (All includes the sender).
    [RtmpeRpc(RpcTarget.All)]
    public void FireRpc(Vector3 origin) => SpawnMuzzleFlash(origin);
}
```

### `RpcTarget`

| Value | Audience |
|---|---|
| `RpcTarget.All` | every client in the room, **including** the sender |
| `RpcTarget.Others` | every client **except** the sender |
| `RpcTarget.Server` | the server only (ServerRpc pattern); may re-broadcast |
| `RpcTarget.AllBuffered` | like `All`, and replayed to clients that join later |

### The `RPC` call

```csharp
// On any NetworkBehaviour. methodName must name a [RtmpeRpc] method on the
// same object; use nameof(...) so a rename is a compile error, not a runtime miss.
void RPC(string methodName, params object[] args);
```

Supported argument types: `int`, `float`, `bool`, `string`, `byte[]`, `ulong`,
`Vector3`, `Color`, `Quaternion`, or any type implementing `INetworkSerializable`.

The gateway stamps the authenticated sender on every RPC; read it inside the
handler via `NetworkManager.CurrentRpcSenderId` to authorize the call (e.g.
compare against `NetworkManager.Instance.LocalPlayerId`).

### Server RPC with a reply — `SendEnhancedRpcAsync`

For a `RpcTarget.Server` method that returns a result, await the request instead
of using fire-and-forget `RPC`:

```csharp
Task<RpcResponse> SendEnhancedRpcAsync(
    NetworkBehaviour  sender,
    string            methodName,
    object[]          args,
    TimeSpan?         timeout            = null,   // defaults to DefaultServerRpcTimeout
    CancellationToken cancellationToken  = default);

// RpcResponse (readonly struct)
uint         RequestId;   // correlates the reply with the request
uint         MethodId;    // FNV-1a("TypeName.MethodName")
ulong        SenderId;    // authenticated originator
bool         Success;     // false → inspect ErrorCode
RpcErrorCode ErrorCode;
byte[]       Payload;     // server-returned bytes (empty when none)
```

The awaited call throws `InvalidOperationException` **synchronously** on a
pre-condition failure — not connected/in a room, unknown method name, `null`
sender, or a method whose `[RtmpeRpc]` target is not `RpcTarget.Server` (use the
fire-and-forget `RPC` for `All`/`Others`/`AllBuffered`). The task completes on
the server reply, the timeout, cancellation, or session teardown.

### Where to put an `[RtmpeRpc]` method (placement)

> **As of v1.9.6** an `[RtmpeRpc]` method may live on **any** `NetworkBehaviour`
> on the object — the inbound call resolves to the component that declares it.
> Place RPCs on the script that uses them, as you would in other SDKs.

Mechanics worth knowing:

- **Anchor precedence.** Each object routes through its *anchor* — the **first**
  `NetworkBehaviour` on the GameObject. If the anchor declares the method it wins;
  otherwise the SDK resolves to the sibling component that declares it.
- **Older than v1.9.6.** Before v1.9.6 an inbound RPC reached the anchor only, so
  a method on a non-anchor component was dropped with
  `no [RtmpeRpc] method with id 0x… on <AnchorType>`. If you cannot update the
  package, move the method to the object's first `NetworkBehaviour`.
- **Ambiguity.** If two components on one object declare a same-named `[RtmpeRpc]`
  method whose types share an unqualified name, the wire id cannot tell them
  apart and the call is deliberately not dispatched — rename one.
- **State is different from RPCs.** A `NetworkVariable` still replicates **only**
  from the anchor (see [NetworkVariable types](#networkvariable-types)). RPCs
  follow siblings; networked *state* does not — keep `NetworkVariable` members on
  the object's first `NetworkBehaviour`.

### Author-time checks

The `[RtmpeRpc]` method must be a `public` instance method on a `NetworkBehaviour`
subclass, with supported parameter types and no overloads. Its FNV-1a id (derived
from `"TypeName.MethodName"`) must not collide with a reserved id
(`100, 200, 300, 301, 400, 401`) or with another `[RtmpeRpc]` method on the same
type; a collision is a startup error from `RpcRegistry.Validate()`. The shipped
Roslyn analyzers surface these at compile time.

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

> The names above are the Unity Inspector labels for the component's private
> `[SerializeField]` fields (e.g. `_syncPosition`, `_positionThreshold`) — set
> them in the Inspector; they are not public code identifiers.

### Methods

```csharp
// Owner-only. Reposition the object without the move being treated as a
// high-speed slide by the anti-cheat velocity cap — use for respawns, fast
// travel, and scripted cinematics. Sets the transform immediately and resets
// the velocity baseline; it does not itself send a packet (the next change-
// detection update emits the new pose). Ignored (with a warning) on objects
// this client does not own.
void OwnerTeleportTo(Vector3 worldPosition);
```

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

> The names above are the Unity Inspector labels for private `[SerializeField]`
> fields (e.g. `_bufferSize`, `_interpolationDelay`) — configured in the
> Inspector; they are not public code identifiers.

### Notes

- Position uses `Vector3.Lerp`.
- Rotation uses `Quaternion.Slerp`.
- Snapshots with `timestamp ≤ latestTimestamp` are discarded (monotonic guard).
- `TryInterpolate()` is a no-op when fewer than 2 snapshots are available.

---

## NetworkRigidbody / NetworkRigidbody2D

**Namespace:** `RTMPE.Sync`
**Inherits:** `NetworkBehaviour`
**Attach to:** a prefab with a `Rigidbody` (3-D) or `Rigidbody2D` (2-D). The
component menu entries are **RTMPE → Network Rigidbody** and **RTMPE → Network
Rigidbody 2D**.

Use these instead of `NetworkTransform` when the object is driven by Unity
physics. The **owner** simulates physics and sends state on `FixedUpdate`;
**remote** clients smoothly correct their local body with velocity blending,
dead reckoning (extrapolation between packets), and position lerp — so bodies
keep simulating between ticks rather than rubber-banding.

### Inspector fields (3-D; 2-D is analogous)

| Field (Inspector label)     | Default | Description |
|-----------------------------|---------|-------------|
| Sync Position / Rotation    | `true`  | Replicate position / rotation |
| Sync Velocity               | `true`  | Replicate linear velocity so remote bodies keep moving between packets |
| Sync Angular Velocity       | `true`  | Replicate angular velocity |
| Sync Sleep State            | `true`  | Idle the remote body when the owner's body sleeps |
| Sync Constraints            | `true`  | Propagate runtime `RigidbodyConstraints` changes (gated by settings) |
| Position / Rotation Threshold | `0.01` / `0.1` | Minimum change before a field is sent |
| Make Remote Kinematic       | `false` | Set remote copies kinematic and apply pose directly — for fully owner-authoritative bodies (player characters) |
| Snap Threshold              | `3.0`   | Position error (m) above which the remote body teleports instead of lerping |
| Enable Dead Reckoning       | `true`  | Extrapolate remote position with the last velocity between packets |
| Send Rate Hz                | `20`    | Owner send rate (clamped to the 30 Hz tick) |
| Enable Owner Reconciliation | `false` | Owner snaps to server-confirmed pose on large divergence — enable only with an authoritative physics server |

### Methods

```csharp
// Capture the current Rigidbody state (position, rotation, velocity, angular
// velocity, sleep, constraint mask) — the snapshot the owner sends each tick.
PhysicsState GetState();
```

> **Receive-side plausibility caps.** On each receiver the component validates
> inbound physics packets against the local `NetworkSettings`
> (`maxLinearVelocity`, `maxAngularVelocity`, `maxPositionDeltaPerTick`,
> `allowDynamicConstraints`, world bounds) plus a per-object rate limit;
> non-finite (NaN/Inf) values are always rejected. This is client-side
> defence-in-depth that keeps one peer's corrupt or hostile stream from
> poisoning another peer's PhysX state — not server-enforced anti-cheat. Leave
> the values at their defaults unless you are tuning those tolerances.

---

## NetworkVariable types

**Namespace:** `RTMPE.Sync`

> **Declare `NetworkVariable` members on the object's anchor** — its **first**
> `NetworkBehaviour`. Unlike `[RtmpeRpc]` methods (which resolve to any component,
> see [Remote Procedure Calls](#remote-procedure-calls)), networked
> state replicates only from the anchor. A `NetworkVariable` created by a
> non-anchor sibling is neither sent by its owner nor applied on receivers — it
> silently never syncs.

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

### Synchronised collections — `NetworkVariableList<T>`

For replicated lists (inventory, active buffs, a kill feed), use a
`NetworkVariableList`. It rides the same 30 Hz flush as scalar variables and the
same anchor rule (declare it on the object's **first** `NetworkBehaviour`). The
owner mutates it; receivers observe each change. Concrete types:
`NetworkVariableListInt`, `NetworkVariableListFloat`, `NetworkVariableListVector3`,
`NetworkVariableListString`.

```csharp
// Constructor (create in OnNetworkSpawn, like any NetworkVariable)
new NetworkVariableListInt(NetworkBehaviour owner, ushort variableId);

// List API — owner writes; every client reads.
int  Count { get; }
T    this[int index] { get; set; }
void Add(T item);
void Insert(int index, T item);
void RemoveAt(int index);
bool Remove(T item);
void Clear();
bool Contains(T item);
int  IndexOf(T item);

// Per-op change notification (fires on owner and receivers, after the op applies).
event Action<NetworkVariableListChangeEvent<T>> OnListChanged;
//   .Kind (Add/Insert/RemoveAt/Set/Clear/FullSync), .Index, .NewValue, .PreviousValue

// When the pending op log passes this many ops, the next flush collapses to a
// single full-sync (bounds per-tick bandwidth). Default 32; hard cap 255.
int FullSyncOpThreshold { get; set; }
```

Steady-state edits ship as a compact delta log; a periodic full-sync (and the
late-join snapshot) ships the whole list. Receivers apply each payload
atomically — a truncated or malformed payload reverts to the pre-payload
contents, so owner and receiver never diverge. The maximum list size is bounded
by `NetworkSettings.maxNetworkVariableListSize` (default 1024).

### Per-variable send rate — `[NetworkVariable(SendRateHz)]`

By default every dirty variable flushes at the global 30 Hz tick. Annotate a
`NetworkVariable` field to cap **that variable's** outbound rate independently —
useful for values that change often but only need slow sync (health, ammo):

```csharp
public sealed class PlayerStats : NetworkBehaviour
{
    public NetworkVariableVector3 velocity;              // 30 Hz default

    [NetworkVariable(SendRateHz = 10f)]                  // ~66% less bandwidth
    public NetworkVariableInt health;

    [NetworkVariable(SendRateHz = 2f)]                   // cosmetic counter
    public NetworkVariableInt killStreak;
}
```

`SendRateHz = 0` (default) means "use the global tick". Intermediate changes are
coalesced into the next eligible send window — the latest value is never lost,
only rate-limited. Values above 30 Hz cannot exceed the tick. The scan is
reflection-cached per type on first spawn (no per-flush cost).

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
| `sendBufferBytes`              | `int`    | `262144`    | UDP socket SO_SNDBUF (256 KiB) |
| `receiveBufferBytes`           | `int`    | `262144`    | UDP socket SO_RCVBUF (256 KiB) |
| `networkThreadBufferBytes`     | `int`    | `8192`      | Background thread read buffer |
| `enableDebugLogs`              | `bool`   | `false`     | Unity Console tracing — set true only in development |
| `apiKeyPskHex`                 | `string` | `""`        | 64-char hex PSK — operator-supplied secret (matches `GATEWAY_API_KEY_ENCRYPTION_KEY_HEX`); not on the dashboard, not the public key |
| `pinnedServerPublicKeyHex`     | `string` | `""`        | 64-char hex — optional server cert pinning |

> The table above lists the **core connection fields**. `NetworkSettings` also
> carries advanced tuning fields (interest management, client-side prediction
> thresholds, spawn-rate limits, variable batching, JWT verification). Inspect
> the `NetworkSettings` asset in the Unity Inspector for the full, current set.

---

## CreateRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class CreateRoomOptions
{
    // Display name shown in room lists. Max 64 bytes UTF-8. Default: "" (server assigns a name).
    public string Name { get; set; } = string.Empty;

    // Max players allowed. Range: 1–100. 0 = server default (100).
    // Values outside [1, 100] are silently clamped to 100 by the room service.
    public int MaxPlayers { get; set; } = 0;

    // Whether the room appears in public room listings. Default: true.
    public bool IsPublic { get; set; } = true;

    // When true (default), a successful CreateRoom automatically issues the
    // JoinRoom that seats the creator as host, so you end up inside the room
    // and gameplay starts from OnRoomJoined. Set false only for the deliberate
    // two-step flow where you drive JoinRoom yourself — otherwise the room
    // stays empty ("waiting", zero players) and the host is never seated.
    public bool AutoJoinAsHost { get; set; } = true;
}
```

---

## JoinRoomOptions

**Namespace:** `RTMPE.Rooms`

```csharp
public sealed class JoinRoomOptions
{
    // Name displayed to other players in the room (max 32 characters).
    // Default: "" (empty) — the server assigns a fallback name when blank.
    public string DisplayName { get; set; } = string.Empty;
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

    public string      MasterId    { get; }   // PlayerId of the current host, or "" if none (derived from Players)
    public string      CurrentScene{ get; }   // Authoritative scene name (reserved __scene property), or "" if unset

    // Room custom properties (read-only; always non-null) and the server's
    // monotonic version counter for optimistic-concurrency updates.
    public IReadOnlyDictionary<string, PropertyValue> Properties { get; }
    public int         PropertiesVersion { get; }
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
    NonceExhausted,  // The outbound AEAD nonce counter reached 2^32 packets;
                     // the session must be fully re-established.
    ProtocolError,   // The gateway sent a packet violating the expected protocol
                     // sequence; the connection can no longer be trusted.
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
| Outbound AEAD nonce counter exhausted (2³² packets)                               | `NonceExhausted` | No               |
| Gateway sent a packet violating the expected protocol sequence                    | `ProtocolError`  | No               |

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
`ApplyDamage` RPC (method_id `301`). The SDK's NetworkManager (client-side) dispatches this RPC by looking
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
    int    sendBufferBytes    = 262144,   // 256 KiB
    int    receiveBufferBytes = 262144)   // 256 KiB

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

*RTMPE SDK 1.9.6 — [Getting Started](../getting-started.md) — [Architecture](../architecture.md) — [Protocol Reference](../protocol.md)*
