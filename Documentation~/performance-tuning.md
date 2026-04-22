# Performance Tuning Guide

> SDK Version: `com.rtmpe.sdk 1.1.0`

How to configure the SDK for different game types and target devices.

Reference measurements are taken on a desktop PC (Intel i7-10700K, Unity 6000.0 LTS,
Mono backend, 30 Hz tick rate, 10 `NetworkVariable` instances per object).

---

## Tick rate selection

The tick rate controls how often the server flushes state to clients. Lower
values reduce CPU and bandwidth at the cost of update latency.

| Game type                    | Recommended tick rate | Rationale                              |
|------------------------------|-----------------------|----------------------------------------|
| FPS / action                 | 30 Hz (default)       | Lowest perceptible latency             |
| Platformer / MOBA            | 20 Hz                 | Good balance of responsiveness and load |
| MMO / many rooms             | 10 Hz                 | Scales to large room counts            |
| Turn-based                   | 1–2 Hz                | Sync only on turn change               |
| Mobile (battery-sensitive)   | 10–15 Hz              | Reduces CPU and radio wake cycles      |

Set `NetworkSettings.tickRate` on the `RTMPESettings` asset **before** calling
`Connect`. Changing it mid-session has no effect until the next reconnect.

---

## Network variable budget

Each `NetworkVariable<T>` adds to the per-tick payload. Measured wire sizes
per variable (value-only; the outer `VariableUpdate` packet adds 8 bytes for
`object_id` + 1 byte for `var_count` + 4 bytes of `[var_id][value_len]` per entry):

| Type                         | Value bytes         |
|------------------------------|---------------------|
| `NetworkVariableInt`         | 4                   |
| `NetworkVariableFloat`       | 4                   |
| `NetworkVariableBool`        | 1                   |
| `NetworkVariableVector3`     | 12                  |
| `NetworkVariableQuaternion`  | 16                  |
| `NetworkVariableString`      | 2 + UTF-8 byte count |

**Target budget:** ≤ 50 KB/s per player at 30 Hz.

At 30 Hz with a 50 KB/s budget and an average of 8 wire bytes per variable:

```
50_000 B/s ÷ 30 ticks/s ÷ 8 B/var ≈ 208 variables / tick / player
```

If you exceed this, consider:

- Using `NetworkTransform` instead of raw `NetworkVariable<Vector3>` — it
  applies delta compression and suppresses sub-threshold moves.
- Splitting high-frequency objects (position) from low-frequency ones
  (inventory, stats) and ticking them at different rates.
- Enabling LZ4 compression implicitly — the SDK calls
  `Lz4Compressor.CompressIfBeneficial` on every outbound payload and sets
  `FLAG_COMPRESSED` only when the compressed form is smaller. No
  configuration is required.

### Late-join snapshot cost (v1.1)

When another player joins the current room, `SpawnManager.MarkAllVariablesDirtyForResync`
re-flags every tracked `NetworkVariable` on every owned, spawned object. The
next 30 Hz flush transmits one extra `VariableUpdate` per owned object
carrying **every** tracked variable (not just the ones that changed recently).
Budget ~8 KB extra one-shot per join if you have ~64 tracked variables; at
30 Hz this is a single-frame blip, not a sustained cost.

---

## Position and rotation thresholds

`NetworkTransform` suppresses updates that fall below a movement threshold,
saving bandwidth for stationary objects.

| Property             | Default threshold | Description                               |
|----------------------|-------------------|-------------------------------------------|
| `PositionThreshold`  | 0.01 m            | Sub-centimetre moves are suppressed       |
| `RotationThreshold`  | 0.1°              | Micro-rotations are suppressed            |

Configure these thresholds in the **Inspector** on the `NetworkTransform`
component. Increasing thresholds reduces bandwidth at the cost of visible
snapping for fast-moving objects.

---

## Memory allocation profile

### v1.1 hot-path allocations (30 Hz, 10 variables, Mono)

| Source                                                  | Allocations / tick | GC pressure |
|---------------------------------------------------------|--------------------|-------------|
| `NetworkVariable.SerializeWithId` — pool path (≤ 1 KiB) | 0                  | 0           |
| `NetworkBehaviour.FlushDirtyVariables` (growable stream fallback, rare) | 1 | ~256 B      |
| Inbound packet copy (`NetworkThread.TryReceive`)        | 1                  | up to 8 KiB per received packet |
| `PacketBuilder.Build` result array                      | 1 per send         | sized to payload |
| **Typical steady-state**                                | **~10 / tick**     | **~1–2 KiB / tick** |

Key optimisations already in place:

- `NetworkVariable.SerializeWithId` renteds a pool-backed `byte[1024]` from
  `ArrayPool<byte>.Shared`. Falls back to a growable `MemoryStream` only for
  unusually large string values.
- `NetworkManager` caches the heartbeat and variable-update delegates (no
  closure allocation per frame).
- `NetworkThread` drains up to 100 packets per iteration to prevent receive
  queuing under burst load.

### Reducing further

- Do not cache the payload buffer passed to `OnDataReceived` — the SDK owns
  the buffer and reuses it immediately after your callback returns.
- Pre-size `List<T>` collections used inside network event handlers to avoid
  `List.Add` resizing.
- Install an [`INetworkObjectPool`](getting-started.md#step-12--object-pooling-optional)
  for any prefab that spawns and despawns frequently (bullets, hit FX,
  transient props). See the next section.

---

## Object pooling (v1.1)

Without pooling, every `Spawner.Spawn` allocates a new GameObject and every
`Spawner.Despawn` destroys one, producing GC pressure and frame-time hitches
during combat-heavy moments.

Install a pool once in `OnConnected`:

```csharp
private void OnConnected()
{
    NetworkManager.Instance.Spawner.SetObjectPool(new SimplePool());
    // …
}
```

The SDK routes every `Spawn`/`Despawn` through the pool when one is installed,
and falls back to `Object.Instantiate` / `Object.Destroy` when no pool is set.
See [Getting Started — Step 12](getting-started.md#step-12--object-pooling-optional)
for a minimal pool implementation.

### Expected impact

| Scenario                          | Without pool       | With pool          |
|-----------------------------------|--------------------|--------------------|
| 20 bullets/sec spawn + despawn    | ~40 GC allocs/sec  | ~0 GC allocs/sec after warm-up |
| Frame-time variance under combat  | 1–3 ms spikes from GC | steady             |

The pool MUST reactivate the GameObject on `Acquire` and should deactivate it
on `Release`. `SpawnManager` defensively calls `SetActive(true)` after a pool
`Acquire` returns, so pool implementations that forget this still work.

---

## Scripting backend comparison

| Backend | Build time | Runtime CPU       | Memory    | Recommendation |
|---------|-----------|-------------------|-----------|----------------|
| Mono    | Fast      | +5–10 % vs IL2CPP | +10 %     | Development only |
| IL2CPP  | Slower    | Baseline          | Baseline  | Required for iOS; recommended for Android / PC release |

### IL2CPP considerations

1. **Code stripping** — add the SDK to `link.xml` (see
   [troubleshooting.md § IL2CPP](troubleshooting.md#il2cpp-missingmethodexception-at-runtime)).
2. **Generic specialisation** — custom `NetworkVariable<T>` types need the
   `[Preserve]` attribute or a reachable instantiation site to avoid
   `ExecutionEngineException` at runtime.
3. **No `DynamicMethod`** — the SDK does not use `DynamicMethod`; full AOT
   compatibility is maintained.

---

## CPU budget

At 30 Hz each tick has 33.3 ms. Typical SDK CPU cost on the reference device:

| Operation                                    | Cost      |
|----------------------------------------------|-----------|
| Packet parsing (per packet)                  | ~0.2 ms   |
| `NetworkVariable` serialisation (10 vars)    | ~0.5 ms   |
| ChaCha20-Poly1305 encryption (1 KB payload)  | ~0.3 ms   |
| LZ4 compression (when beneficial, 1 KB)      | ~0.1 ms   |
| **Total at 10 variables + 2 KB/s**           | **~1 ms / tick (3 % of frame)** |

If the SDK consistently exceeds **10 % of your tick budget**, reduce the tick
rate or the number of variables per object.

Profile with the Unity Profiler deep-profile mode; filter by the
`RTMPE.SDK.Runtime` assembly to isolate SDK cost.

---

## Transport selection

The default `UdpTransport` is correct for every standalone platform (Windows,
macOS, Linux, Android, iOS). For WebGL, Unity's sandbox blocks raw UDP; install
a custom WebSocket transport via `NetworkManager.SetTransportFactory` and
deploy a matching WebSocket-to-UDP bridge on the server side.

```csharp
NetworkManager.SetTransportFactory(settings => new MyWebSocketTransport(settings));
NetworkManager.Instance.Connect(apiKey);
```

See [Architecture §3](architecture.md#3-transport-layer) for the complete
transport contract.

---

## Mobile-specific tuning

| Setting              | Default       | Mobile recommendation |
|----------------------|---------------|-----------------------|
| `tickRate`           | 30 Hz         | 10–15 Hz              |
| `heartbeatIntervalMs`| 5 000 ms      | 5 000 ms (do not increase above 15 000; 3 missed heartbeats = timeout) |
| `connectionTimeoutMs`| 10 000 ms     | 15 000 ms (weaker radio links)                                  |
| Background behaviour | Active        | Call `NetworkManager.Instance.Disconnect()` on `OnApplicationPause(true)` and `Reconnect()` on resume — the stored reconnect token makes this cheap |

Halving the tick rate from 30 to 15 Hz reduces both CPU usage and radio
wake cycles by approximately 50 %, which has a measurable impact on battery
life during extended play sessions.

---

## Related documentation

- [Architecture](architecture.md) — where allocations happen in the call stack
- [Protocol Reference](protocol.md) — wire-format details for bandwidth calculation
- [API Reference](api/index.md) — `NetworkSettings` field reference
- [Troubleshooting](troubleshooting.md) — GC spike and CPU spike diagnostics

---

*RTMPE SDK 1.1.0*
