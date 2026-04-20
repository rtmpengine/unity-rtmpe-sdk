# Troubleshooting Guide

Common issues encountered when integrating the RTMPE SDK, with diagnostic
steps and fixes. Each section leads with the symptom followed by a checklist.

---

## Connection issues

### Symptom: `Connect` returns and `OnConnectionFailed` fires with `HandshakeTimeout`

**Diagnostic checklist:**

- [ ] Is the gateway reachable? `nc -u <host> 7777` should connect without error.
- [ ] Is UDP port 7777 open in the firewall? RTMPE uses UDP only — TCP rules
      do not apply.
- [ ] Is `NetworkManager.Settings.pinnedServerPublicKeyHex` set to the correct
      64-char hex key for the target environment? A key mismatch causes the server
      to silently drop the handshake packet; no error is returned within the
      handshake window.
- [ ] Is the client system clock within 5 minutes of UTC? JWT `nbf` and `exp`
      claims are validated server-side; a skewed clock causes silent token rejection.

**Common causes and fixes:**

| Cause | Fix |
|-------|-----|
| Wrong environment key | Verify `Settings.pinnedServerPublicKeyHex` matches the server's `GATEWAY_PUBLIC_KEY` environment variable. |
| Wrong API key PSK | Verify `Settings.apiKeyPskHex` is the 64-char hex value from the RTMPE developer dashboard for the target environment. |
| Corporate NAT drops unsolicited UDP | Use KCP transport (roadmap Q2 2026; not available in v1.0). |

---

### Symptom: Connection drops after ~30 seconds of idle

The client sends a `Heartbeat` packet every 5 seconds by default. The server
closes the session if no heartbeat is received within 30 seconds.

- [ ] Is the Unity editor paused? The SDK's socket thread uses `ThreadSafeQueue`
      which does not advance while the editor is paused.
- [ ] Is `NetworkManager.Settings.heartbeatIntervalMs` at its default value of
      `5_000`? Increasing it beyond `30_000` causes the server to time out the
      session before the next heartbeat arrives.
- [ ] Is the application going to the background on mobile? Call
      `NetworkManager.Suspend()` when the app enters background to stop the
      socket thread cleanly instead of letting heartbeats time out.

---

### Symptom: Reconnection loop — connects then immediately disconnects

- [ ] Check the `DisconnectReason` in the `OnDisconnected` callback. A reason of
      `ProtocolMismatch` indicates a version mismatch between the SDK and the
      server; upgrade both to matching protocol versions (see
      [`../../../shared/contracts/COMPATIBILITY.md`]).
- [ ] Check `SessionRejected` below if the disconnect occurs right after handshake.

---

## Authentication issues

### Symptom: `SessionRejected` immediately after handshake

- [ ] Is the auth token valid? Test it independently:
      ```
      curl -H "Authorization: Bearer <token>" https://<portal>/api/v1/auth/me
      ```
      A `401` response means the token is invalid or expired.
- [ ] Is the system clock synchronised? Session TTLs default to 5 minutes. A
      clock drift greater than 5 minutes results in `exp` rejection.
- [ ] Are you using the correct environment's token? Dev tokens contain a
      different HKDF salt than production tokens and are rejected by the
      production gateway.

---

### Symptom: Auth token expires during a long session

The SDK does not auto-refresh tokens. Implement `ITokenProvider` and return a
fresh token from `GetTokenAsync` — the SDK calls this before every reconnect
attempt.

---

## State synchronisation issues

### Symptom: `NetworkVariable` values never update on remote clients

- [ ] Is the `NetworkBehaviour` registered via `SpawnManager.Spawn()`? Objects
      instantiated with Unity's `Instantiate` are not tracked by the SDK.
- [ ] Is `NetworkBehaviour.NetworkObjectId` unique and consistent across server
      and client? Log it on both sides with
      `Debug.Log($"id={obj.NetworkObjectId}")`.
- [ ] Is `NetworkVariable<T>.IsDirty` being cleared manually? Call
      `MarkClean()` only after serialisation completes; premature clearing
      suppresses the next sync tick.

---

### Symptom: Silent packet loss — state lags behind without error

- [ ] Is the server tick rate lower than the client's expected update rate?
      The server drives all state updates; the client only receives what the
      server sends.
- [ ] Is the connection operating under heavy packet loss (> 20%)? At this level,
      even reliable packets (KCP) may observe significant latency. Monitor
      `NetworkManager.Instance.LastRttMs` — sustained spikes above 200 ms on
      a LAN indicate significant loss. Use the Unity Profiler's Network view for
      deeper analysis.

---

## Performance symptoms

### Symptom: GC spikes every 1–2 seconds

Allocations in hot paths are the most common cause. Profile with
`Profiler.GetTotalAllocatedMemoryLong()` delta per tick.

- [ ] Are you caching the payload buffer from `OnPacketReceived`? The SDK owns
      that buffer and reuses it — copy only the bytes you need.
- [ ] Are `NetworkVariable<T>` values reference types? `T` must be a struct or
      a pooled class to avoid per-change heap allocation.
- [ ] Are you constructing `new PacketBuilder()` per tick? `PacketBuilder` is
      stateless and thread-safe; keep one instance and reuse it.

**Expected GC budget (v1.0):** < 120 B/tick at 30 Hz with 10 `NetworkVariable`
instances. See [performance-tuning.md](performance-tuning.md) for details.

---

### Symptom: CPU spikes on reconnection

`NetworkManager.Connect` already retries with exponential backoff capped at
30 seconds. Adding a custom retry loop on top doubles the connect pressure.

- [ ] Remove any `while (!connected) { Connect(apiKey); }` loops from
      application code — the SDK handles transient failures internally.

---

## Unity-specific issues

### IL2CPP: `MissingMethodException` at runtime

Unity AOT code stripping removes unused methods. Add the SDK assemblies to
your project's `link.xml`:

```xml
<linker>
    <assembly fullname="RTMPE.SDK" preserve="all" />
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

### WebGL: not supported in v1.0

WebGL browsers do not provide UDP socket access. WebRTC transport is on the
roadmap for v1.2.

---

### Mobile: excessive battery drain

- Lower `NetworkManager.Settings.tickRate` from the default `30` to `10`–`15`
  for games that do not require sub-100 ms input latency.
- Call `NetworkManager.Suspend()` when `OnApplicationPause(true)` fires to
  stop the socket thread and suppress heartbeats.

---

## Reporting bugs

If none of the above resolves the issue, open an issue at
<https://github.com/Faisalzz1/unity-rtmpe-sdk/issues> and include:

1. SDK version — found in `Packages/com.rtmpe.sdk/package.json`.
2. Unity version (e.g. `6000.1.0f1`) and scripting backend (Mono / IL2CPP).
3. Target platform (Windows / macOS / iOS / Android).
4. The full `NetworkManager` log at verbosity `Debug`.
5. A minimal reproduction project if possible.
