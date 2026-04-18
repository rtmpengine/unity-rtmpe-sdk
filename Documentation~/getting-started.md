# RTMPE SDK — Getting Started

> **SDK Version:** `com.rtmpe.sdk 1.0.0`  
> **Unity Version Required:** Unity 6000.0 LTS or later  
> **Target Platform:** PC, Mac, Linux, Android, iOS

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [Prerequisites](#2-prerequisites)
3. [Step 1 — Install the SDK](#step-1--install-the-sdk)
4. [Step 2 — Create the NetworkSettings Asset](#step-2--create-the-networksettings-asset)
5. [Step 3 — Add NetworkManager to the Scene](#step-3--add-networkmanager-to-the-scene)
6. [Step 4 — Convert Scripts to NetworkBehaviour](#step-4--convert-scripts-to-networkbehaviour)
7. [Step 5 — Set Up the Networked Prefab](#step-5--set-up-the-networked-prefab)
8. [Step 6 — Synchronize State with NetworkVariables](#step-6--synchronize-state-with-networkvariables)
9. [Step 7 — Create a GameManager](#step-7--create-a-gamemanager)
10. [Step 8 — Room List UI](#step-8--room-list-ui)
11. [Step 9 — Handle Player Join and Leave Events](#step-9--handle-player-join-and-leave-events)
12. [Step 10 — Disconnection and Cleanup](#step-10--disconnection-and-cleanup)
13. [Complete API Reference](#complete-api-reference)
14. [Connection State Machine](#connection-state-machine)
15. [Pre-Launch Checklist](#pre-launch-checklist)
16. [Common Errors and Fixes](#common-errors-and-fixes)
17. [Performance Notes](#performance-notes)

---

## 1. Architecture Overview

```
┌────────────────────────────────────────────────────────────┐
│                      GAME CLIENTS                          │
│                                                            │
│   ┌──────────────────┐       ┌──────────────────┐          │
│   │    Player 1      │       │    Player 2      │          │
│   │  Unity 6 LTS     │       │  Unity 6 LTS     │          │
│   │  RTMPE SDK       │       │  RTMPE SDK       │          │
│   └────────┬─────────┘       └────────┬─────────┘          │
│            │ UDP :7777                │ UDP :7777           │
└────────────┼─────────────────────────┼────────────────────-┘
             │                         │
             ▼                         ▼
┌────────────────────────────────────────────────────────────┐
│                    RTMPE Backend                           │
│                                                            │
│   ┌──────────────────────┐   ┌──────────────────────────┐  │
│   │  UDP Gateway (Rust)  │   │   Room Service (Go)      │  │
│   │  Port 7777 (UDP)     │──▶│   CreateRoom / JoinRoom  │  │
│   │  Port 7778 (KCP)     │   │   LeaveRoom / GetRoom    │  │
│   └──────────────────────┘   └──────────────────────────┘  │
│              │                          │                   │
│              └──────────┬───────────────┘                   │
│                         ▼                                   │
│                 ┌──────────────┐                            │
│                 │  NATS Bus    │   Event routing            │
│                 └──────────────┘                            │
│                                                             │
│   PostgreSQL — API Key validation + Room persistence        │
└────────────────────────────────────────────────────────────-┘
```

### Connection flow

1. Client calls `NetworkManager.Instance.Connect(apiKey)`.
2. Gateway validates the API key against the database and issues a session token.
3. Client enters `NetworkState.Connected`.
4. Client creates or joins a **Room** (1–16 players).
5. Each client spawns their player object via `SpawnManager.Spawn()`.
6. The server broadcasts state at **30 Hz** to every player in the room.
7. Players see each other moving in real time at P99 < 30 ms latency (within region).

---

## 2. Prerequisites

| Requirement     | Detail                                    |
| --------------- | ----------------------------------------- |
| Unity           | 6000.0 LTS (Unity 6) or later             |
| .NET Standard   | 2.1                                       |
| Build targets   | PC, Mac, Linux, Android, iOS — all supported |
| RTMPE Gateway   | ≥ 3.0.0 — obtain from the RTMPE dashboard |
| API Key         | Issued via the RTMPE developer dashboard  |
| Outbound UDP    | Your firewall must allow **outbound** UDP on port 7777 |

---

## Step 1 — Install the SDK

### Option A — Unity Package Manager (Git URL) — recommended

Open your game's `Packages/manifest.json` and add:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "https://github.com/Faisalzz1/unity-rtmpe-sdk.git"
  }
}
```

Or in Unity: **Window → Package Manager → + → Add package from git URL**, paste:

```
https://github.com/Faisalzz1/unity-rtmpe-sdk.git
```

### Option B — Local copy

1. Download or clone the SDK repository.
2. Copy the `com.rtmpe.sdk` folder into your project's `Packages/` directory.
3. Unity auto-detects it on the next Editor refresh.

Or reference it by path in `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.rtmpe.sdk": "file:../path/to/com.rtmpe.sdk"
  }
}
```

### Verify installation

Open **Window → Package Manager**. You should see:

```
RTMPE SDK   1.0.0   ✓
```

The `RTMPE` namespace is now available in all scripts.

---

## Step 2 — Create the NetworkSettings Asset

The `NetworkSettings` asset stores all connection configuration for a deployment target.
You can maintain multiple profiles (e.g. `RTMPESettings_Dev.asset`, `RTMPESettings_Prod.asset`).

1. In the **Project** panel: right-click → **Create → RTMPE → Settings**.
2. Name the asset (e.g. `RTMPESettings_Prod.asset`).
3. Configure the fields in the **Inspector**:

| Field                      | Value                                      | Notes                                         |
| -------------------------- | ------------------------------------------ | --------------------------------------------- |
| `Server Host`              | Your RTMPE gateway hostname or IP          | Obtain from the RTMPE dashboard               |
| `Server Port`              | `7777`                                     | Default UDP port                              |
| `Heartbeat Interval Ms`    | `5000`                                     | 5-second keepalive interval                   |
| `Connection Timeout Ms`    | `10000`                                    | 10-second handshake timeout                   |
| `Tick Rate`                | `30`                                       | Must match the server room-service config     |
| `Send Buffer Bytes`        | `4096`                                     | UDP socket SO_SNDBUF                         |
| `Receive Buffer Bytes`     | `4096`                                     | UDP socket SO_RCVBUF                         |
| `Network Thread Buffer Bytes` | `8192`                                  | Background thread read buffer                 |
| `Enable Debug Logs`        | `true` during development, `false` in production | Unity Console connection traces        |
| `Api Key Psk Hex`          | 64-char hex — copy from the RTMPE dashboard | Encrypts the API key in transit; leave blank for local dev only |
| `Pinned Server Public Key Hex` | 64-char hex — copy from the RTMPE dashboard | Optional server certificate pinning   |

> **Security note:** Never commit your production `RTMPESettings` asset to a public
> repository. Add `RTMPESettings_Prod.asset` to your `.gitignore`, or store the API key
> in a separate secret file loaded at runtime.

---

## Step 3 — Add NetworkManager to the Scene

1. Create an empty **GameObject** in your **first / boot scene**.
2. Name it `[RTMPE] NetworkManager`.
3. Add the `NetworkManager` component (**Component → RTMPE → NetworkManager**).
4. In the Inspector, drag your `RTMPESettings_Prod.asset` into the **Settings** field.

```
Hierarchy (boot scene):
  ├── [RTMPE] NetworkManager   ← add here only
  └── ... (other boot objects)
```

> **Important:** `NetworkManager` calls `DontDestroyOnLoad()` automatically — it persists
> across all scene loads. **Do not add a second NetworkManager in any other scene** or
> you will get duplicate-singleton warnings.

---

## Step 4 — Convert Scripts to NetworkBehaviour

Every GameObject whose state must be visible to all players must derive from
`NetworkBehaviour` instead of `MonoBehaviour`.

### Before (single-player)

```csharp
using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private void Update()
    {
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.position += new Vector3(h, 0f, v) * _moveSpeed * Time.deltaTime;
    }
}
```

### After (multiplayer)

```csharp
using System;
using UnityEngine;
using RTMPE.Core;   // NetworkBehaviour, NetworkManager
using RTMPE.Sync;   // NetworkVariable types

[RequireComponent(typeof(NetworkTransform))]          // ← required
public class PlayerController : NetworkBehaviour      // ← changed from MonoBehaviour
{
    [SerializeField] private float _moveSpeed = 5f;

    private NetworkVariableInt   _health;
    private NetworkVariableFloat _score;

    // Store handler references for reliable unsubscription in OnNetworkDespawn.
    // Anonymous lambdas create a new delegate each call — -= (o,n)=>{} removes nothing.
    private Action<int, int> _onHealthChanged;

    // Called by RTMPE when this object is registered on the network.
    // Initialize all NetworkVariables here — NOT in Awake/Start.
    // Use 'protected override' to match the base class modifier (avoids CS0507).
    protected override void OnNetworkSpawn()
    {
        // variableId must be unique within this component (0, 1, 2, …).
        _health = new NetworkVariableInt(this, variableId: 0, initialValue: 100);
        _score  = new NetworkVariableFloat(this, variableId: 1, initialValue: 0f);

        // Store the reference BEFORE subscribing so OnNetworkDespawn can remove it.
        _onHealthChanged = (oldHp, newHp) =>
        {
            Debug.Log($"[{name}] HP: {oldHp} → {newHp}");
            if (newHp <= 0) HandleDeath();
        };
        _health.OnValueChanged += _onHealthChanged;
    }

    // Called before this network object is removed from the network.
    protected override void OnNetworkDespawn()
    {
        if (_health != null) _health.OnValueChanged -= _onHealthChanged;
    }

    private void Update()
    {
        // ──────────────────────────────────────────────────────────────────────
        // CRITICAL RULE: Only the INPUT owner moves the character.
        // Other clients receive the position automatically via NetworkTransform.
        // ──────────────────────────────────────────────────────────────────────
        if (!IsOwner) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        transform.position += new Vector3(h, 0f, v) * _moveSpeed * Time.deltaTime;
        // NetworkTransform sends the position update to the server at 30 Hz automatically.
    }

    // Only the owner sets this value; all other clients receive it via OnValueChanged.
    public void TakeDamage(int amount)
    {
        if (!IsOwner) return;
        if (_health == null) return;
        _health.Value = Mathf.Max(0, _health.Value - amount);
    }

    private void HandleDeath()
    {
        // Runs on ALL clients because OnValueChanged replicates everywhere.
        Debug.Log($"[{name}] eliminated.");
    }
}
```

### The `IsOwner` rule

| Context | `IsOwner` |
| ------- | --------- |
| The player on their own machine | `true` |
| The same player viewed on any other machine | `false` |

**Only the owner should:**
- Read `Input.*`
- Move the character
- Write `NetworkVariable.Value`

**All clients receive automatically:**
- Position and rotation via `NetworkTransform`
- Variable changes via `NetworkVariable.OnValueChanged`

---

## Step 5 — Set Up the Networked Prefab

Attach these components to every prefab that needs to be visible across the network:

```
PlayerPrefab (GameObject)
  ├── PlayerController.cs              ← your script (extends NetworkBehaviour)
  ├── NetworkTransform.cs              ← Runtime/Sync/
  ├── NetworkTransformInterpolator.cs  ← Runtime/Sync/
  └── (any other existing components)
```

### NetworkTransform Inspector settings

| Field                | Recommended | Notes                                      |
| -------------------- | ----------- | ------------------------------------------ |
| `Sync Position`      | ✅ true     | Sync world-space position                  |
| `Sync Rotation`      | ✅ true     | Sync world-space rotation                  |
| `Sync Scale`         | ❌ false    | Enable only if the object changes scale    |
| `Position Threshold` | `0.01`      | Minimum movement in metres before sending  |
| `Rotation Threshold` | `0.1`       | Minimum rotation in degrees before sending |

### NetworkTransformInterpolator Inspector settings

| Field                | Recommended | Notes                                           |
| -------------------- | ----------- | ----------------------------------------------- |
| `Buffer Size`        | `10`        | Number of state snapshots to buffer             |
| `Interpolation Delay`| `0.1`       | 100 ms lag buffer — smooths jitter              |
| `Interpolate Scale`  | ❌ false    | Match your `Sync Scale` setting                 |

> The interpolator runs on **all clients** to smooth the movement of remote players.

---

## Step 6 — Synchronize State with NetworkVariables

Use `NetworkVariable<T>` for any value that all players must see simultaneously.

### Available types

| Class                        | Type         | Size      |
| ---------------------------- | ------------ | --------- |
| `NetworkVariableInt`         | `int`        | 4 bytes   |
| `NetworkVariableFloat`       | `float`      | 4 bytes   |
| `NetworkVariableBool`        | `bool`       | 1 byte    |
| `NetworkVariableVector3`     | `Vector3`    | 12 bytes  |
| `NetworkVariableQuaternion`  | `Quaternion` | 16 bytes  |
| `NetworkVariableString`      | `string`     | variable (UTF-8) |

### Rules

1. **Initialize in `OnNetworkSpawn()`** — never in `Awake()` or `Start()`.
2. **`variableId` must be unique within each component** (0, 1, 2 … per component).
   Different components on the same prefab have independent ID namespaces.
3. **Only the owner writes `Value`**. All clients read and react via `OnValueChanged`.
4. Variables are flushed to the server at **30 Hz** automatically.

### Example

```csharp
using System;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

public class MyCharacter : NetworkBehaviour
{
    private NetworkVariableInt    _health;
    private NetworkVariableInt    _score;
    private NetworkVariableString _displayName;
    private NetworkVariableBool   _isAlive;

    private Action<int, int>       _onHealthChanged;
    private Action<bool, bool>     _onAliveChanged;
    private Action<string, string> _onNameChanged;

    protected override void OnNetworkSpawn()
    {
        _health      = new NetworkVariableInt(this,    variableId: 0, initialValue: 100);
        _score       = new NetworkVariableInt(this,    variableId: 1, initialValue: 0);
        _displayName = new NetworkVariableString(this, variableId: 2, initialValue: "Player");
        _isAlive     = new NetworkVariableBool(this,   variableId: 3, initialValue: true);

        _onHealthChanged = (old, next) => UpdateHealthBar(next);
        _onAliveChanged  = (old, next) => OnAliveStateChanged(next);
        _onNameChanged   = (old, next) => UpdateNameTag(next);

        _health.OnValueChanged      += _onHealthChanged;
        _isAlive.OnValueChanged     += _onAliveChanged;
        _displayName.OnValueChanged += _onNameChanged;
    }

    protected override void OnNetworkDespawn()
    {
        if (_health      != null) _health.OnValueChanged      -= _onHealthChanged;
        if (_isAlive     != null) _isAlive.OnValueChanged     -= _onAliveChanged;
        if (_displayName != null) _displayName.OnValueChanged -= _onNameChanged;
    }

    private void UpdateHealthBar(int hp)       { /* update UI */ }
    private void OnAliveStateChanged(bool alive) { /* play animation */ }
    private void UpdateNameTag(string name)    { /* update label */ }
}
```

---

## Step 7 — Create a GameManager

The `GameManager` orchestrates the full lifecycle: connect → create/join room → spawn player.
Place it on a persistent GameObject in your boot scene.

```csharp
using System;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Rooms;

public class GameManager : MonoBehaviour
{
    // ── Inspector ────────────────────────────────────────────────────────────

    [Header("Connection")]
    [Tooltip("API key issued from the RTMPE developer dashboard.")]
    [SerializeField] private string _apiKey = "";   // fill in the Inspector; do NOT hardcode here

    [Header("Room")]
    [SerializeField] private string _roomName   = "My Game Room";
    [SerializeField] private int    _maxPlayers = 4;
    [SerializeField] private bool   _autoCreate = true;  // true = auto-create; false = show lobby list

    [Header("Spawn")]
    [SerializeField] private GameObject _playerPrefab;
    [SerializeField] private uint       _playerPrefabId = 1;   // must be identical on every client
    [SerializeField] private Vector3    _spawnPosition  = new Vector3(0f, 1f, 0f);

    // ── Private ──────────────────────────────────────────────────────────────

    private NetworkBehaviour _localPlayer;

    // Store references so we can unsubscribe precisely in OnDestroy.
    private Action                   _onConnectedHandler;
    private Action<DisconnectReason> _onDisconnectedHandler;
    private Action<string>           _onConnectionFailedHandler;
    private Action<RoomInfo>         _onRoomCreatedHandler;
    private Action<RoomInfo>         _onRoomJoinedHandler;
    private Action                   _onRoomLeftHandler;
    private Action<string>           _onRoomErrorHandler;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    private void Start()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            Debug.LogError("[GameManager] API key is not set in the Inspector.");
            return;
        }

        if (_playerPrefab == null)
        {
            Debug.LogError("[GameManager] Player prefab is not assigned.");
            return;
        }

        var net = NetworkManager.Instance;

        // Assign stored references before subscribing.
        _onConnectedHandler        = OnConnected;
        _onDisconnectedHandler     = OnDisconnected;
        _onConnectionFailedHandler = OnConnectionFailed;
        _onRoomCreatedHandler      = room => OnRoomEntered(room);
        _onRoomJoinedHandler       = room => OnRoomEntered(room);
        _onRoomLeftHandler         = OnRoomLeft;
        _onRoomErrorHandler        = OnRoomError;

        net.OnConnected          += _onConnectedHandler;
        net.OnDisconnected       += _onDisconnectedHandler;
        net.OnConnectionFailed   += _onConnectionFailedHandler;
        net.Rooms.OnRoomCreated  += _onRoomCreatedHandler;
        net.Rooms.OnRoomJoined   += _onRoomJoinedHandler;
        net.Rooms.OnRoomLeft     += _onRoomLeftHandler;
        net.Rooms.OnRoomError    += _onRoomErrorHandler;

        net.Connect(_apiKey);
    }

    private void OnDestroy()
    {
        var net = NetworkManager.Instance;
        if (net == null) return;

        net.OnConnected          -= _onConnectedHandler;
        net.OnDisconnected       -= _onDisconnectedHandler;
        net.OnConnectionFailed   -= _onConnectionFailedHandler;
        net.Rooms.OnRoomCreated  -= _onRoomCreatedHandler;
        net.Rooms.OnRoomJoined   -= _onRoomJoinedHandler;
        net.Rooms.OnRoomLeft     -= _onRoomLeftHandler;
        net.Rooms.OnRoomError    -= _onRoomErrorHandler;
    }

    // ── Handlers ─────────────────────────────────────────────────────────────

    private void OnConnected()
    {
        Debug.Log("[GameManager] Connected.");

        // Register the prefab INSIDE OnConnected.
        // Connect() creates a fresh SpawnManager — any registration made before
        // Connect() is silently wiped.
        NetworkManager.Instance.Spawner.RegisterPrefab(_playerPrefabId, _playerPrefab);

        if (_autoCreate)
        {
            NetworkManager.Instance.Rooms.CreateRoom(new CreateRoomOptions
            {
                Name       = _roomName,
                MaxPlayers = _maxPlayers,
                IsPublic   = true,
            });
        }
        else
        {
            // Populate a room-list UI instead.
            NetworkManager.Instance.Rooms.ListRooms(publicOnly: true);
        }
    }

    private void OnRoomEntered(RoomInfo room)
    {
        Debug.Log($"[GameManager] Room: {room.Name}  code: {room.RoomCode}  " +
                  $"{room.PlayerCount}/{room.MaxPlayers} players");

        _localPlayer = NetworkManager.Instance.Spawner.Spawn(
            _playerPrefabId,
            _spawnPosition,
            Quaternion.identity);

        if (_localPlayer == null)
            Debug.LogError("[GameManager] Spawn returned null — verify prefab registration.");
    }

    private void OnRoomLeft()
    {
        Debug.Log("[GameManager] Left room.");
        _localPlayer = null;
    }

    private void OnDisconnected(DisconnectReason reason)
    {
        Debug.Log($"[GameManager] Disconnected — {reason}");
        _localPlayer = null;

        UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
    }

    private void OnConnectionFailed(string reason)
    {
        Debug.LogError($"[GameManager] Connection failed: {reason}");
    }

    private void OnRoomError(string error)
    {
        Debug.LogError($"[GameManager] Room error: {error}");
    }

    // ── Public (call from UI buttons) ────────────────────────────────────────

    public void JoinRoom(string roomId)
    {
        NetworkManager.Instance.Rooms.JoinRoom(roomId, new JoinRoomOptions
        {
            DisplayName = "Player",
        });
    }

    public void JoinRoomByCode(string roomCode)
    {
        NetworkManager.Instance.Rooms.JoinRoomByCode(roomCode, new JoinRoomOptions
        {
            DisplayName = "Player",
        });
    }

    public void LeaveRoom()   => NetworkManager.Instance.Rooms.LeaveRoom();
    public void Disconnect()  => NetworkManager.Instance.Disconnect();
}
```

---

## Step 8 — Room List UI

When `_autoCreate = false`, call `ListRooms()` and show the results in a UI panel.

```csharp
using UnityEngine;
using UnityEngine.UI;
using RTMPE.Core;
using RTMPE.Rooms;

public class RoomListUI : MonoBehaviour
{
    [SerializeField] private Transform  _container;       // parent for room entry prefabs
    [SerializeField] private GameObject _entryPrefab;     // prefab with Text + Join Button
    [SerializeField] private InputField _codeInput;       // optional: direct join by code

    private void OnEnable()
    {
        NetworkManager.Instance.Rooms.OnRoomListReceived += Populate;
    }

    private void OnDisable()
    {
        if (NetworkManager.HasInstance)
            NetworkManager.Instance.Rooms.OnRoomListReceived -= Populate;
    }

    private void Populate(RoomInfo[] rooms)
    {
        foreach (Transform child in _container)
            Destroy(child.gameObject);

        foreach (var room in rooms)
        {
            var entry = Instantiate(_entryPrefab, _container);

            // Use TMP_Text (add 'using TMPro;') instead of Text for Unity 6 TMP projects.
            entry.GetComponentInChildren<Text>().text =
                $"{room.Name}  [{room.PlayerCount}/{room.MaxPlayers}]  #{room.RoomCode}";

            var roomIdCopy = room.RoomId;
            entry.GetComponentInChildren<Button>().onClick.AddListener(() =>
                NetworkManager.Instance.Rooms.JoinRoom(roomIdCopy));
        }
    }

    public void JoinByCode()
    {
        var code = _codeInput?.text?.Trim();
        if (!string.IsNullOrEmpty(code))
            NetworkManager.Instance.Rooms.JoinRoomByCode(code);
    }

    public void Refresh() => NetworkManager.Instance.Rooms.ListRooms(publicOnly: true);
}
```

---

## Step 9 — Handle Player Join and Leave Events

```csharp
private void SubscribeToPlayerEvents()
{
    NetworkManager.Instance.Rooms.OnPlayerJoined += OnPlayerJoined;
    NetworkManager.Instance.Rooms.OnPlayerLeft   += OnPlayerLeft;
}

private void OnPlayerJoined(PlayerInfo player)
{
    Debug.Log($"Player joined: {player.DisplayName} (id={player.PlayerId})");
    // Update head-count UI, play join sound, etc.
}

private void OnPlayerLeft(string playerId)
{
    Debug.Log($"Player left: {playerId}");
    // Objects spawned by that player with DestroyWithOwner = true
    // are destroyed automatically on all remaining clients.
}
```

### DestroyWithOwner behaviour

When a player disconnects, any networked object they spawned with
`DestroyWithOwner = true` (the default) is automatically despawned on all clients.
No extra code is required.

```csharp
// Inside OnNetworkSpawn() or Awake() on your NetworkBehaviour:
// DestroyWithOwner is a settable property (not virtual) — do NOT use override.
DestroyWithOwner = false;   // keep object alive after the owner disconnects

// The default is true — if you want the default behaviour, do nothing.
```

---

## Step 10 — Disconnection and Cleanup

```csharp
private void OnDisconnected(DisconnectReason reason)
{
    _localPlayer = null;
    UnityEngine.SceneManagement.SceneManager.LoadScene("MainMenu");
}

// From a Quit button:
public void QuitToMainMenu()
{
    NetworkManager.Instance.Disconnect();
    // OnDisconnected will fire — handle the scene transition there.
}
```

---

## Complete API Reference

### NetworkManager (singleton)

```csharp
// Access
NetworkManager.Instance          // returns null after OnApplicationQuit
NetworkManager.HasInstance       // thread-safe null check

// Connection
void Connect(string apiKey)
void Disconnect()

// State
NetworkState State               // Disconnected / Connecting / Connected / InRoom / Disconnecting
bool IsConnected                 // true when Connected or InRoom
bool IsInRoom                    // true when inside a room

// Identity
ulong  LocalPlayerId             // numeric session ID (valid after Connect)
string LocalPlayerStringId       // room player UUID (valid after JoinRoom/CreateRoom)
float  LastRttMs                 // round-trip time in ms (-1 before first heartbeat)

// Sub-managers
RoomManager   Rooms
SpawnManager  Spawner

// Events
event Action                              OnConnected
event Action<DisconnectReason>            OnDisconnected
event Action<string>                      OnConnectionFailed
event Action<NetworkState, NetworkState>  OnStateChanged
event Action<float>                       OnRttUpdated
```

### RoomManager (`NetworkManager.Rooms`)

```csharp
// Operations
void CreateRoom(CreateRoomOptions options = null)
void JoinRoom(string roomId, JoinRoomOptions options = null)
void JoinRoomByCode(string roomCode, JoinRoomOptions options = null)
void LeaveRoom()
void ListRooms(bool publicOnly = true)

// State
RoomInfo CurrentRoom             // null when not in a room
bool IsInRoom

// Events
event Action<RoomInfo>    OnRoomCreated
event Action<RoomInfo>    OnRoomJoined
event Action              OnRoomLeft
event Action<PlayerInfo>  OnPlayerJoined
event Action<string>      OnPlayerLeft          // receives playerId
event Action<RoomInfo[]>  OnRoomListReceived
event Action<string>      OnRoomError
```

### CreateRoomOptions

```csharp
new CreateRoomOptions
{
    Name       = "My Room",   // display name (max 64 chars)
    MaxPlayers = 4,            // 1–16; 0 = server default (16)
    IsPublic   = true,         // visible in ListRooms results
}
```

### JoinRoomOptions

```csharp
new JoinRoomOptions
{
    DisplayName = "Alice",   // visible name in the room (max 32 chars)
}
```

### SpawnManager (`NetworkManager.Spawner`)

```csharp
// Call RegisterPrefab inside OnConnected — Connect() creates a fresh SpawnManager;
// any registration made before Connect() is silently wiped.
void RegisterPrefab(uint prefabId, GameObject prefab)
bool UnregisterPrefab(uint prefabId)
bool HasPrefab(uint prefabId)

// Call Spawn after OnRoomCreated / OnRoomJoined fires.
NetworkBehaviour Spawn(uint prefabId, Vector3 position, Quaternion rotation)

// Pass the NetworkObjectId (ulong), not the component reference.
void Despawn(ulong networkObjectId)
```

> **Prefab ID rule:** The same `prefabId` (e.g. `1`) **must** map to the same prefab
> on every client. Register identically across all clients.

### NetworkBehaviour (base class)

```csharp
ulong  NetworkObjectId       // server-assigned unique object ID
string OwnerPlayerId         // UUID of the owning player
bool   IsOwner               // true only on the owning client
bool   IsSpawned             // true after the object is spawned
bool   DestroyWithOwner      // settable property (not virtual); default: true

// Override with 'protected override':
protected virtual void OnNetworkSpawn()
protected virtual void OnNetworkDespawn()
```

### NetworkVariable types

```csharp
// Constructor signature: (NetworkBehaviour owner, int variableId, T initialValue)
var hp       = new NetworkVariableInt(this,    0, 100);
var speed    = new NetworkVariableFloat(this,  1, 0f);
var alive    = new NetworkVariableBool(this,   2, true);
var vel      = new NetworkVariableVector3(this, 3, Vector3.zero);
var lookDir  = new NetworkVariableQuaternion(this, 4, Quaternion.identity);
var name     = new NetworkVariableString(this, 5, "Player");

// Read (any client)
int currentHp = hp.Value;

// Write (owner only)
hp.Value = 50;

// React (all clients)
hp.OnValueChanged += (oldVal, newVal) => UpdateUI(newVal);
```

### DisconnectReason enum

| Value            | Meaning                                   |
| ---------------- | ----------------------------------------- |
| `ClientRequest`  | You called `Disconnect()`                 |
| `ConnectionLost` | Network interrupted                       |
| `ServerRequest`  | Server closed the connection              |
| `Timeout`        | Handshake or heartbeat timed out          |
| `Kicked`         | Server forcibly removed the player        |
| `Unknown`        | Unclassified reason                       |

---

## Connection State Machine

```
              Connect(apiKey)
Disconnected ──────────────────▶ Connecting
                                      │
                                      │ Handshake + SessionAck ✅
                                      ▼
                                  Connected ◀── CreateRoom / JoinRoom available
                                      │
                                      │ CreateRoom / JoinRoom ✅
                                      ▼
                                   InRoom ◀──── Spawn objects here
                                      │
                                      │ LeaveRoom()
                                      ▼
                                  Connected
                                      │
                                      │ Disconnect()
                                      ▼
                               Disconnecting ──▶ Disconnected
```

**Key rules:**
- Call `Connect()` only from `Disconnected` state.
- Call `CreateRoom()` / `JoinRoom()` only after `OnConnected` fires.
- Call `Spawner.Spawn()` only after `OnRoomCreated` / `OnRoomJoined` fires.
- Call `RegisterPrefab()` inside `OnConnected()` — never before `Connect()`.

---

## Pre-Launch Checklist

- [ ] SDK installed — `com.rtmpe.sdk` appears in Package Manager
- [ ] `RTMPESettings` asset created with the correct `serverHost` and `serverPort`
- [ ] `NetworkManager` GameObject exists **only in the boot scene** with the Settings asset assigned
- [ ] `serverHost` and API key values come from environment / secure storage — not hardcoded in source
- [ ] Player prefab has a `NetworkBehaviour` subclass as its main script
- [ ] Player prefab has `NetworkTransform` component attached
- [ ] Player prefab has `NetworkTransformInterpolator` component attached
- [ ] `GameManager._playerPrefabId` is consistent across all clients
- [ ] `RegisterPrefab()` is called **inside `OnConnected()`**, not before `Connect()`
- [ ] All `NetworkVariable` IDs are unique within each component
- [ ] All `NetworkVariable` types are initialized inside `OnNetworkSpawn()`, not `Awake()`/`Start()`
- [ ] Every `Input.*` call is guarded with `if (!IsOwner) return;`
- [ ] All event subscriptions use stored delegate references (not anonymous lambdas)
- [ ] All events are unsubscribed in `OnDestroy()`
- [ ] `enableDebugLogs = false` in the production Settings asset

---

## Common Errors and Fixes

### `[RTMPE] NetworkManager.Connect: apiKey must not be null or empty`
**Cause:** The `_apiKey` field is empty.  
**Fix:** Enter your API key in the Inspector field on `GameManager`.

---

### `[RTMPE] NetworkManager.Connect ignored — already in state <X>`
**Cause:** `Connect()` was called when the manager was not in `Disconnected` state.  
**Fix:**
```csharp
if (NetworkManager.Instance.State == NetworkState.Disconnected)
    NetworkManager.Instance.Connect(_apiKey);
```

---

### `OnRoomError` fires after a successful `OnConnected`
**Cause:** The API key connected successfully (UDP layer is fine) but is not authorised in the server database.  
**Fix:** Verify the API key is registered and active in the RTMPE developer dashboard.

---

### Players do not see each other moving
**Cause 1:** `NetworkTransform` is missing from the player prefab.  
**Cause 2:** `NetworkTransformInterpolator` is missing or is inside an `if (!IsOwner)` block.  
**Fix:** Confirm both `NetworkTransform` and `NetworkTransformInterpolator` are attached as separate components in the Inspector.

---

### `Spawn returned null`
**Cause:** `RegisterPrefab()` was not called before `Spawn()`, or was called before `Connect()` (which resets the SpawnManager).  
**Fix:** Call `RegisterPrefab()` inside `OnConnected()`, before `CreateRoom()` / `JoinRoom()`.

---

### `NetworkVariable.OnValueChanged` always fires with the default value
**Cause:** `NetworkVariable` was created in `Awake()` or `Start()` instead of `OnNetworkSpawn()`.  
**Fix:** Move all `new NetworkVariableXxx(...)` calls into `OnNetworkSpawn()`.

---

### `Duplicate NetworkManager instance` warning
**Cause:** Two scenes both contain a `NetworkManager` GameObject.  
**Fix:** Keep `NetworkManager` only in the boot scene. It persists via `DontDestroyOnLoad`.

---

### Connection times out after 10 seconds
**Cause 1:** Outbound UDP on port 7777 is blocked by a firewall or router.  
**Cause 2:** The RTMPE server is unreachable.  
**Fix 1:** Test on a different network. Ensure outbound UDP 7777 is allowed.  
**Fix 2:** Verify the server is running via the RTMPE dashboard health endpoint.

---

## Performance Notes

| Parameter              | Value / Note                                                  |
| ---------------------- | ------------------------------------------------------------- |
| Tick rate              | 30 Hz — state updates every 33.3 ms                          |
| Latency P99            | < 30 ms within region                                        |
| Max players per room   | 16 (configurable per project in the dashboard)               |
| Position threshold     | 0.01 m — sub-centimetre moves are suppressed to save bandwidth |
| Rotation threshold     | 0.1° — tiny rotations are suppressed                         |
| NetworkVariable flush  | 30 Hz — no manual flush needed                               |
| Thread safety          | Never write `NetworkVariable.Value` from a background thread  |
| Interpolation delay    | 100 ms default — smoother movement at the cost of slight visual delay |

---

*RTMPE SDK 1.0.0 — [Changelog](../CHANGELOG.md) — [Protocol Reference](protocol.md) — [API Reference](api/index.md)*
