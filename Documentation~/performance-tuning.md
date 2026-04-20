# Performance Tuning Guide

How to configure the SDK for different game types and target devices.

Reference measurements are taken on a desktop PC (Intel i7-10700K, Unity 2022 LTS,
Mono backend, 30 Hz tick rate, 10 `NetworkVariable` instances per object).

---

## Tick rate selection

The tick rate controls how often the server flushes state to clients. Lower
values reduce CPU and bandwidth at the cost of update latency.

| Game type | Recommended tick rate | Rationale |
|-----------|-----------------------|-----------|
| FPS / action | 30 Hz (default) | Lowest perceptible latency |
| Platformer / MOBA | 20 Hz | Good balance of responsiveness and load |
| MMO / many rooms | 10 Hz | Scales to large room counts |
| Turn-based | 1–2 Hz | Sync only on turn change |
| Mobile (battery-sensitive) | 10–15 Hz | Reduces CPU and radio wake cycles |

Change via:

```csharp
NetworkManager.Settings.tickRate = 10;
```

Set this **before** calling `Connect`. Changing it after connection has no
effect until the next reconnect.

---

## Network variable budget

Each `NetworkVariable<T>` adds to the per-tick payload. Measured serialisation
costs per variable (dirty + non-dirty path):

| Type | Bytes per dirty flush |
|------|-----------------------|
| `NetworkVariable<int>` | 5 B (4 B value + 1 B dirty flag) |
| `NetworkVariable<float>` | 5 B |
| `NetworkVariable<bool>` | 2 B |
| `NetworkVariable<Vector3>` | 13 B |
| `NetworkVariable<Quaternion>` | 17 B |
| `NetworkVariable<string>` (≤ 64 chars) | 66 B |

**Target budget:** ≤ 50 KB/s per player at 30 Hz.

At 30 Hz with a 50 KB/s budget, that is approximately:

```
50_000 bytes/s ÷ 30 ticks/s ÷ 8 bytes/variable ≈ 208 variables per tick
```

If you exceed this, consider:

- Using `NetworkTransform` instead of raw `NetworkVariable<Vector3>` —
  it applies delta compression and suppresses sub-threshold moves.
- Splitting high-frequency objects (position) from low-frequency ones
  (inventory, stats) and ticking them at different rates.
- Reducing tick rate for objects that change infrequently.

---

## Position and rotation thresholds

`NetworkTransform` suppresses updates that fall below a movement threshold,
saving bandwidth for stationary objects.

| Property | Default threshold | Description |
|----------|-------------------|-------------|
| Position | 0.01 m | Sub-centimetre moves are suppressed |
| Rotation | 0.1° | Micro-rotations are suppressed |

Configure these thresholds in the **Inspector** on the `NetworkTransform`
component — `_positionThreshold` and `_rotationThreshold` are serialized
private fields and are not exposed as public C# properties.

Increasing thresholds reduces bandwidth at the cost of visible snapping for
fast-moving objects.

---

## Memory allocation profile

### v1.0 baseline (30 Hz, 10 variables, Mono)

| Source | Allocations/tick | GC pressure |
|--------|------------------|-------------|
| `NetworkVariable.SerializeWithId` (`MemoryStream`) | ~90 | ~60 B |
| `new IPEndPoint` per UDP receive | ~30 | ~48 B |
| JSON encoding (sync broadcaster) | 0 (pooled) | 0 |
| **Total** | **~120** | **~108 B/tick** |

### Reducing allocations

- **Do not cache packet payload buffers** passed to `OnPacketReceived`. The
  SDK recycles these immediately after the callback returns.
- **Pre-size `List<T>` collections** used inside network event handlers to
  avoid `List.Add` resizing.
- **Reuse `PacketBuilder` instances** — the builder is stateless and
  thread-safe.

### v1.1 roadmap

`ArrayPool<byte>` integration targets < 20 allocations per tick by eliminating
`MemoryStream` and `IPEndPoint` allocations from hot paths.

---

## Scripting backend comparison

| Backend | Build time | Runtime CPU | Memory | Recommendation |
|---------|-----------|-------------|--------|----------------|
| Mono | Fast | +5–10 % vs IL2CPP | +10 % | Development only |
| IL2CPP | Slower | Baseline | Baseline | Required for iOS; recommended for Android release |

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

| Operation | Cost |
|-----------|------|
| Packet parsing (per packet) | ~0.2 ms |
| `NetworkVariable` serialisation (10 variables) | ~0.5 ms |
| ChaCha20-Poly1305 encryption (1 KB payload) | ~0.3 ms |
| **Total at 10 variables + 2 KB/s** | **~1 ms/tick (3 % of frame)** |

If the SDK consistently exceeds **10 % of your tick budget**, reduce the tick
rate or the number of variables per object.

Profile with the Unity Profiler deep-profile mode; filter by the
`RTMPE.SDK.Runtime` assembly to isolate SDK cost.

---

## Mobile-specific tuning

| Setting | Default | Mobile recommendation |
|---------|---------|----------------------|
| `tickRate` | 30 Hz | 10–15 Hz |
| `heartbeatIntervalMs` | 5 000 ms | 5 000 ms (do not increase above 30 000) |
| `connectionTimeoutMs` | 10 000 ms | 15 000 ms (weaker radio links) |
| Background behaviour | Active | Call `NetworkManager.Suspend()` on `OnApplicationPause(true)` |

Halving the tick rate from 30 to 15 Hz reduces both CPU usage and radio
wake cycles by approximately 50 %, which has a measurable impact on battery
life during extended play sessions.

---

## Related documentation

- [Architecture](architecture.md) — where allocations happen in the call stack
- [Protocol Reference](protocol.md) — wire-format details for bandwidth calculation
- [API Reference](api/index.md) — `NetworkSettings` field reference
- [Troubleshooting](troubleshooting.md) — GC spike and CPU spike diagnostics
