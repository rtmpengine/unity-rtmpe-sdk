# RTMPE SDK — Documentation

> SDK Version: `com.rtmpe.sdk 1.1.0`
> Protocol Version: v3 — Magic `0x5254` ("RT")

Welcome to the RTMPE SDK documentation.

## Sections

- [Quick Start](getting-started.md) — step-by-step guide: install, configure, connect, spawn, sync, reconnect, pool
- [Architecture](architecture.md) — SDK layers, threading model, crypto flow, late-join, reconnect, scene transitions
- [Protocol Reference](protocol.md) — wire format, `PacketType` values, flag bits, payload layouts
- [Troubleshooting](troubleshooting.md) — common issues and diagnostic checklists
- [Performance Tuning](performance-tuning.md) — tick rate, memory budget, pooling, IL2CPP tips
- [API Reference](api/index.md) — complete C# class and method reference
- [Samples](../Samples~/BasicConnection/README.md) — runnable example projects
- [Changelog](../CHANGELOG.md) — version history

## Protocol framing

```
Header layout (13 bytes, little-endian):
  [0..1]  magic       = 0x5254 ("RT")
  [2]     version     = 3
  [3]     packet_type (see PacketType enum)
  [4]     flags       (Compressed=0x01, Encrypted=0x02, Reliable=0x04)
  [5..8]  sequence    (u32, monotonic per connection — doubles as AEAD nonce counter)
  [9..12] payload_len (u32)
```

## v1.1 highlights

- **Late-join state snapshot** — new joiners receive the full `NetworkVariable`
  state within one 30 Hz tick. Zero application-code changes required.
- **Pluggable transport** — `NetworkManager.SetTransportFactory(...)` opens
  the door to WebGL builds and mock-transport tests.
- **Auto room re-join** — `LastRoomId` / `LastRoomCode` survive a
  token-preserving clear; `Reconnect()` rejoins automatically when
  `NetworkSettings.autoRejoinLastRoomOnReconnect` is `true` (default).
- **Scene transition pruning** — `NetworkObjectRegistry.PruneDestroyed()` is
  auto-called on scene load/unload, preventing dead-reference leaks.
- **Object pooling** — `INetworkObjectPool` interface plus
  `SpawnManager.SetObjectPool()` eliminate `Instantiate`/`Destroy` GC
  pressure for high-churn objects.

Full detail in the [v1.1.0 CHANGELOG](../CHANGELOG.md).
