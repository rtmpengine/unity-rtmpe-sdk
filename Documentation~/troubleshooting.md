# Troubleshooting Guide

> SDK Version: `com.rtmpe.sdk 1.1.0`

Common issues encountered when integrating the RTMPE SDK, with diagnostic
steps and fixes. Each section leads with the symptom followed by a checklist.

---

## Connection issues

### Symptom: `OnConnectionFailed` fires with "Connection timeout"

The handshake did not complete within `NetworkSettings.connectionTimeoutMs`
(default 10 000 ms).

**Diagnostic checklist:**

- [ ] Is the gateway reachable? On a shell:
      `nc -u <host> 7777` (Linux/macOS) or equivalent UDP test.
- [ ] Is outbound UDP 7777 allowed through the user's firewall and router?
      RTMPE uses UDP only — TCP rules do not apply.
- [ ] Is `Settings.pinnedServerPublicKeyHex` set to the correct 64-char hex
      key for the target environment? A key mismatch causes the Ed25519
      signature check on the `Challenge` to fail; the SDK aborts with
      "Ed25519 signature invalid" and the timeout coroutine fires
      `OnConnectionFailed` shortly after.
- [ ] Is `Settings.apiKeyPskHex` the 64-char hex value from the dashboard for
      the target environment? A wrong PSK means the gateway can't decrypt the
      API key in `HandshakeInit` and silently drops the packet.
- [ ] Is the client system clock within 5 minutes of UTC? JWT `nbf` / `exp`
      claims are validated server-side; a skewed clock causes silent token
      rejection after a successful handshake.

**Common causes and fixes:**

| Cause                                   | Fix |
|-----------------------------------------|-----|
| Wrong environment PSK                   | Verify `Settings.apiKeyPskHex` matches the server's `GATEWAY_API_KEY_ENCRYPTION_KEY_HEX`. |
| Wrong pinned public key                 | Verify `Settings.pinnedServerPublicKeyHex` matches the server's `GATEWAY_PUBLIC_KEY`. |
| Corporate NAT drops unsolicited UDP     | Consider a WebSocket transport via `NetworkManager.SetTransportFactory` + a WebSocket-to-UDP bridge on the server side. |
| Routing probe fell back to loopback     | Check the Unity Console for `[RTMPE] UdpTransport: routing probe failed …` — common in isolated test containers. The AEAD AAD of `HandshakeInit` will contain the loopback IP instead of the real outgoing interface, and the gateway will reject it. |

---

### Symptom: Connection drops after ~15–30 seconds of idle

The SDK sends a `Heartbeat` every `heartbeatIntervalMs` (default 5 000 ms).
Three consecutive missed `HeartbeatAck` responses trigger
`DisconnectReason.ConnectionLost`.

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

- [ ] Is the JWT valid? Log `NetworkManager.Instance.JwtToken` and decode the
      `exp` claim (`jwt.io` or equivalent). A `401` from the Room Service
      REST API indicates expiration or a signing-key mismatch between the
      gateway and Room Service.
- [ ] Is the system clock synchronised? Token TTLs default to 5 minutes. A
      clock drift greater than 5 minutes causes `exp` rejection.
- [ ] Are you using the correct environment's token? Dev tokens are signed
      with a different HMAC key than production tokens and are rejected by
      the production Room Service.

---

### Symptom: Auth token expires during a long session

The SDK does not auto-refresh tokens. Listen for
`OnDisconnected(DisconnectReason.Kicked)` and re-authenticate from scratch via
`Connect(apiKey)` — the reconnect token path is not sufficient for a stale
auth context.

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
      level even reliable (KCP) packets observe significant latency. Consider
      reducing tick rate or switching to a closer region.

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

Unity AOT code stripping removes unused methods. Add the SDK assemblies to
your project's `link.xml`:

```xml
<linker>
    <assembly fullname="RTMPE.SDK.Runtime" preserve="all" />
</linker>
```

Place `link.xml` in the `Assets/` folder. If you use a stripping level higher
than **Low**, also add any custom `NetworkVariable<T>` types where `T` is not
one of the pre-referenced types (`int`, `float`, `bool`, `Vector3`,
`Quaternion`, `string`):

```xml
<assembly fullname="Assembly-CSharp">
    <type fullname="MyGame.MyCustomState" preserve="all" />
</assembly>
```

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
<https://github.com/Faisalzz1/unity-rtmpe-sdk/issues> and include:

1. SDK version — found in `Packages/com.rtmpe.sdk/package.json`.
2. Unity version (e.g. `6000.1.0f1`) and scripting backend (Mono / IL2CPP).
3. Target platform (Windows / macOS / iOS / Android / WebGL).
4. The full `NetworkManager` log with `NetworkSettings.enableDebugLogs = true`.
5. A minimal reproduction project if possible.

---

*RTMPE SDK 1.1.0*
