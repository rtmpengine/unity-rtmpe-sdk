# Troubleshooting Guide

> SDK Version: `com.rtmpe.sdk 1.9.6`

Common issues encountered when integrating the RTMPE SDK, with diagnostic
steps and fixes. Each section leads with the symptom followed by a checklist.

---

## Connection issues

### Symptom: `OnConnectionFailed` fires with "Connection timeout." or "Reconnect timeout."

The handshake did not complete within `NetworkSettings.connectionTimeoutMs`
(default 10 000 ms).

**Diagnostic checklist:**

- [ ] Is the gateway reachable? On a shell:
      `nc -u <host> 7777` (Linux/macOS) or equivalent UDP test.
- [ ] Is outbound UDP 7777 allowed through the user's firewall and router?
      RTMPE uses UDP only — TCP rules do not apply.
- [ ] Is `Settings.pinnedServerPublicKeyHex` set to the correct 64-char hex
      key for the target environment? A key mismatch causes the Ed25519
      signature check on the `Challenge` to fail; the SDK aborts the
      handshake and the timeout coroutine fires `OnConnectionFailed`
      shortly after.
- [ ] Is `Settings.apiKeyPskHex` the 64-char hex secret your gateway operator
      provided out of band (it matches `GATEWAY_API_KEY_ENCRYPTION_KEY_HEX` and
      is **not** shown on the dashboard, and is **not** the gateway public key)?
      A wrong PSK means the gateway can't decrypt the API key in `HandshakeInit`
      and silently drops the packet.
- [ ] Is the client system clock within 5 minutes of UTC? JWT `nbf` / `exp`
      claims are validated server-side; a skewed clock causes silent token
      rejection after a successful handshake.

**Common causes and fixes:**

| Cause                                   | Fix |
|-----------------------------------------|-----|
| Wrong environment PSK                   | Verify `Settings.apiKeyPskHex` matches the API-key PSK configured on the gateway. |
| Wrong pinned public key                 | Verify `Settings.pinnedServerPublicKeyHex` matches the gateway's Ed25519 public key for the target environment. |
| Corporate NAT drops unsolicited UDP     | Consider a WebSocket transport via `NetworkManager.SetTransportFactory` + a WebSocket-to-UDP bridge on the server side. |
| Routing probe fell back to loopback     | Check the Unity Console for `[RTMPE] UdpTransport: routing probe failed …` — common in isolated test containers. The AEAD AAD of `HandshakeInit` will contain the loopback IP instead of the real outgoing interface, and the gateway will reject it. |

---

### Symptom: Connection drops after ~15–30 seconds of idle

The SDK sends a `Heartbeat` every `heartbeatIntervalMs` (default 5 000 ms).
`DisconnectReason.ConnectionLost` is raised only when **both** conditions hold:
three consecutive `HeartbeatAck` responses are missed **and** no
AEAD-authenticated `HeartbeatAck` has arrived within the liveness-grace window
(default ≈ 30 s, twice the three-miss span). The grace window forgives a brief
stall that recovers with a real ack; only a `HeartbeatAck` refreshes it (other
inbound traffic does not), so keep `heartbeatIntervalMs` low enough that the ack
cadence stays inside the window.

- [ ] Is the Unity Editor paused? The network thread continues running, but
      `MainThreadDispatcher` does not drain actions while paused — callbacks
      appear to stop.
- [ ] Is `heartbeatIntervalMs` above `15_000`? With 3-miss tolerance the
      server-side session TTL is exceeded before the next heartbeat arrives.
- [ ] Is the application going to the background on mobile? Handle
      `OnApplicationPause(true)` by calling `Disconnect()` and `Reconnect()`
      on resume — the stored reconnect token makes this cheap.

---

### Symptom: Reconnect loop — connects then immediately disconnects

- [ ] Inspect the `DisconnectReason` argument in `OnDisconnected`. Compare
      against the enum in [API Reference §DisconnectReason](api/index.md#disconnectreason-enum).
- [ ] If the disconnect carries `ConnectionLost` immediately after
      `OnConnected`, the gateway rejected the first encrypted packet — typical
      causes are nonce-counter mismatch between SDK and gateway, or a server
      reboot that invalidated your `cryptoId`.
- [ ] If `OnAutoRejoinAttempt` fires followed immediately by an `OnRoomError`,
      the server has evicted the room UUID. Disable
      `autoRejoinLastRoomOnReconnect` and show a room-selection UI instead.

---

### Symptom: `CanReconnect` is false after a drop

The SDK wipes the reconnect token on:

- explicit `Disconnect()`;
- handshake or ACK timeouts (the token has been consumed or is unusable);
- server-initiated `Disconnect`.

If your use case needs guaranteed resumption after those events, you must
call `Connect(apiKey)` with fresh credentials.

---

## Authentication issues

### Symptom: Handshake succeeds but the first room call fails with "invalid token"

- [ ] Is the JWT valid? `NetworkManager.Instance.JwtToken` is a `RedactedString`
      that logs as `<redacted>`; call `.Reveal()` to obtain the raw token, then
      decode the `exp` claim (`jwt.io` or equivalent). A `401` from the Room Service
      REST API indicates expiration or a signing-key mismatch between the
      gateway and Room Service.
- [ ] Is the system clock synchronised? Token TTLs default to 5 minutes. A
      clock drift greater than 5 minutes causes `exp` rejection.
- [ ] Are you using the correct environment's token? Dev tokens are signed
      with a different HMAC key than production tokens and are rejected by
      the production Room Service.

---

### Symptom: Auth token expires during a long session

The SDK does not auto-refresh the session JWT. When a token expires the
gateway ends the session, which the SDK surfaces through `OnDisconnected` —
typically with `DisconnectReason.ServerRequest` (or `Timeout` if a subsequent
reconnect cannot complete). In your `OnDisconnected` handler, when
`CanReconnect` is `false`, re-authenticate from scratch via `Connect(apiKey)`;
the reconnect-token path does not refresh an expired auth context.

---

## State synchronisation issues

### Symptom: `NetworkVariable` values never update on remote clients

- [ ] Was the `NetworkBehaviour` registered via `SpawnManager.Spawn()`? Objects
      instantiated with Unity's `Instantiate` are not tracked by the SDK.
- [ ] Is `NetworkBehaviour.NetworkObjectId` consistent between sender and
      receiver? Log it on both sides with
      `Debug.Log($"id={obj.NetworkObjectId}")`.
- [ ] Is the `NetworkVariable` created in `OnNetworkSpawn()`? Creating it in
      `Awake()` / `Start()` leaves `IsOwner` undefined at construction time
      and the variable is not tracked by the send loop.
- [ ] Are you writing `Value` on a non-owner? The setter is ignored on
      non-owners — guard with `if (!IsOwner) return;` before any write.

---

### Symptom: Late-joiner sees default NetworkVariable values until the owner writes to them (v1.0 only)

Fixed in v1.1 — `SpawnManager.MarkAllVariablesDirtyForResync` is now auto-called
on `RoomManager.OnPlayerJoined`, and the joiner receives a full snapshot
within one 30 Hz tick. Upgrade to 1.1.0.

For v1.0, work around this by having the owner re-assign the variable's
current value on `OnPlayerJoined`:

```csharp
NetworkManager.Instance.Rooms.OnPlayerJoined += _ =>
{
    if (_health.IsOwner) _health.Value = _health.Value;
};
```

---

### Symptom: Silent packet loss — state lags behind without error

- [ ] Is `NetworkManager.LastRttMs` consistently high or spiking?
      `> 200 ms` on a LAN indicates significant packet loss. Open the Unity
      Profiler's Network view for deeper analysis.
- [ ] Is the connection operating under heavy packet loss (> 20 %)? At this
      level even packets marked reliable observe significant latency. Consider
      reducing tick rate or switching to a closer region.

---

### Symptom: a spawned object's `IsOwner` is always `false` (the player never moves)

`IsOwner` is `true` only for an object created through the SDK's spawn system
with the local player as its owner. If `Update()` keeps logging *"not the
owner"*, the object running that code is almost certainly not the one you
spawned:

- [ ] Is the object created with `NetworkManager.Instance.Spawner.Spawn(...)`?
      A scene-placed (or `Instantiate`d) `NetworkBehaviour` is never networked,
      so its `IsOwner` is permanently `false`. The networked player must come
      from `Spawn`.
- [ ] Did `Spawn` return non-`null`? When the prefab id is not registered,
      `Spawn` logs `[SpawnManager] Spawn: prefab {id} is not registered` and
      returns `null` — assign the result and check it. Register the prefab with
      `Spawner.RegisterPrefab(id, prefab)` (it persists across reconnects).
- [ ] Is a second copy of the script already in the scene? Its `Update()` logs
      *"not owner"* forever while the spawned copy is fine. Log
      `GetInstanceID()`, `IsSpawned`, and `OwnerPlayerId` to tell them apart.
- [ ] Spawn from `OnRoomJoined` (or later): the local player id arrives with the
      room, so spawning before then leaves the object unowned.

---

## RPC issues

### Symptom: `RPC request: malformed payload, dropped` on the receiver

The gateway must preserve the `EnhancedRpc` wire flag when it fans an
`[RtmpeRpc]` call out to peers; a build that drops it delivers your enhanced
payload to the legacy parser, which reports *malformed*. This was a server-side
defect fixed on 2026-07-13 — no SDK or code change is needed on your side. If you
see it against a self-hosted gateway, update the gateway build.

### Symptom: `no [RtmpeRpc] method with id 0x… on <SomeType>` — the RPC is dropped

The call reached the object but the SDK looked for the method on the wrong
component. An object routes RPCs through its **anchor** (the **first**
`NetworkBehaviour` on the GameObject), and `<SomeType>` in the message is that
anchor. The named id is hashed from the *declaring* type, so this fires when the
`[RtmpeRpc]` method lives on a different component than the anchor.

- [ ] **On v1.9.6 or newer:** the SDK resolves the call to whichever component
      declares the method, so this should not occur — confirm the package is
      actually updated (`Packages/com.rtmpe.sdk/package.json` → `"version"`), and
      that the *receiver's* object carries a component of the same type that the
      sender declared the method on (both clients run the same prefab).
- [ ] **On an older package:** move the `[RtmpeRpc]` method onto the object's
      **first** `NetworkBehaviour`. For a global event (e.g. "start game"), the
      clean home is a dedicated networked manager object whose first
      `NetworkBehaviour` declares the RPC — not a per-player object whose first
      component is something else.
- [ ] Coming from Photon Fusion (RPCs on the specific `NetworkBehaviour`)? That
      pattern is supported as of v1.9.6; on older builds the anchor rule above
      applies. See [API → Remote Procedure Calls](api/index.md#remote-procedure-calls).

### Symptom: an `[RtmpeRpc]` call never runs, no "malformed"/"no method" log

- [ ] Is the method `public`, non-`static`, on a `NetworkBehaviour` subclass, with
      only supported parameter types (`int`, `float`, `bool`, `string`, `byte[]`,
      `ulong`, `Vector3`, `Color`, `Quaternion`, or `INetworkSerializable`)? The
      shipped Roslyn analyzers flag violations at compile time.
- [ ] Does the wire audience match the declared one? A method declared
      `[RtmpeRpc(RpcTarget.Others)]` invoked as `All` (or vice-versa) is refused;
      declare the audience you actually send. A `RpcTarget.Server` method never
      runs on a client.
- [ ] Are you in a room? RPCs are dropped before `OnRoomJoined`.

---

## Performance symptoms

### Symptom: GC spikes every 1–2 seconds

Allocations in hot paths are the most common cause. Profile with
`Profiler.GetTotalAllocatedMemoryLong()` delta per tick.

- [ ] Are you caching the payload buffer from `OnDataReceived`? The SDK
      owns that buffer and reuses it — copy only the bytes you need.
- [ ] Are you spawning and despawning objects every frame? Install an
      [`INetworkObjectPool`](getting-started.md#step-12--object-pooling-optional)
      to eliminate `Instantiate` / `Destroy` allocations.
- [ ] Are you creating closures (anonymous lambdas) inside `Update`? Cache
      them as fields, as shown in the Getting Started guide.

**Expected GC budget (v1.1):** ≤ ~2 KiB / tick at 30 Hz with 10
`NetworkVariable` instances and no user-level allocations. See
[performance-tuning.md](performance-tuning.md) for details.

---

### Symptom: CPU spikes on reconnection

`NetworkManager.Reconnect()` uses the `ReconnectBackoff` (Full-Jitter capped
exponential) internally. Do not wrap `Reconnect()` in a `while` loop — it
takes care of retry cadence automatically.

---

## Unity-specific issues

### IL2CPP: `MissingMethodException` at runtime

Unity AOT code stripping removes members it cannot prove are reached from a
static call site. The SDK ships a `link.xml` that preserves its own runtime
assembly (`RTMPE.SDK.Runtime`), so the SDK's reflective RPC and variable paths
survive stripping with no action on your part.

Your game's `[RtmpeRpc]` methods and any custom `NetworkVariable<T>` closed
types live in your own assembly, which the SDK cannot preserve for you. When
the **Managed Stripping Level** is above **Low**, add a `link.xml` under your
project's `Assets/` folder that preserves them:

```xml
<linker>
    <assembly fullname="Assembly-CSharp">
        <type fullname="MyGame.PlayerController" preserve="all" />
        <type fullname="MyGame.MyCustomState" preserve="all" />
    </assembly>
</linker>
```

Replace `Assembly-CSharp` with your gameplay assembly name if you use an
`.asmdef`. The built-in `NetworkVariable<T>` closures for `int`, `float`,
`bool`, `Vector3`, `Quaternion`, and `string` are already preserved by the
SDK's own `link.xml`.

---

### IL2CPP: `ExecutionEngineException` on generic `NetworkVariable<T>`

The SDK pre-specialises generic paths for `int`, `float`, `bool`, `Vector3`,
`Quaternion`, and `string`. A custom `T` requires either:

- The `[Preserve]` attribute on the type definition, **or**
- A non-stripped code path that creates at least one `NetworkVariable<T>`
  instance, forcing AOT specialisation.

---

### WebGL: UDP is not available — use a custom transport

Unity WebGL runs in the browser sandbox, which has no access to raw UDP
sockets. Ship a WebGL build by:

1. Implementing a `WebSocketTransport : NetworkTransport` backed by
   `System.Net.WebSockets.ClientWebSocket` (desktop builds) or a
   `DllImport("__Internal")`-based JavaScript bridge (WebGL).
2. Installing it before `Connect()`:
   ```csharp
   NetworkManager.SetTransportFactory(settings => new WebSocketTransport(settings));
   ```
3. Deploying a WebSocket-to-UDP bridge in front of the RTMPE Gateway, or a
   dedicated WebSocket gateway build.

The default gateway image speaks UDP+KCP only — a WebSocket endpoint is not
part of the stock deployment. See
[Architecture §3 — Pluggable transport](architecture.md#3-transport-layer).

---

### Mobile: excessive battery drain

- Lower `NetworkManager.Settings.tickRate` from the default `30` to `10`–`15`
  for games that do not require sub-100 ms input latency.
- Handle `OnApplicationPause(true)` by calling `Disconnect()`. On resume,
  call `Reconnect()` — the stored reconnect token avoids a full re-auth.

---

### Scene transitions: stale registry entries

If you load a new scene with `SceneManager.LoadScene` without first calling
`SpawnManager.Despawn` on scene-specific networked objects, the registry
holds dead references until the next `ClearAll()` (room leave / disconnect).

v1.1 subscribes to `SceneManager.sceneUnloaded` / `sceneLoaded` and calls
`NetworkObjectRegistry.PruneDestroyed()` automatically to evict those dead
references. If you want server-side cleanup of those objects, despawn them
explicitly before loading the new scene:

```csharp
foreach (var obj in NetworkManager.Instance.Spawner.Registry.GetAll())
{
    if (obj.IsOwner)
        NetworkManager.Instance.Spawner.Despawn(obj.NetworkObjectId);
}
UnityEngine.SceneManagement.SceneManager.LoadScene("NextLevel");
```

---

## Reporting bugs

If none of the above resolves the issue, open an issue at
<https://github.com/rtmpengine/unity-rtmpe-sdk/issues> and include:

1. SDK version — found in `Packages/com.rtmpe.sdk/package.json`.
2. Unity version (e.g. `6000.1.0f1`) and scripting backend (Mono / IL2CPP).
3. Target platform (Windows / macOS / iOS / Android / WebGL).
4. The full `NetworkManager` log with `NetworkSettings.enableDebugLogs = true`.
5. A minimal reproduction project if possible.

---

*RTMPE SDK 1.9.6*
