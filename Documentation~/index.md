# RTMPE SDK — Documentation

Welcome to the RTMPE SDK documentation.

## Sections

- [Quick Start](getting-started.md) — step-by-step guide: install, configure, connect, spawn, sync
- [Architecture](architecture.md) — SDK layers, threading model, crypto flow, data flow diagrams
- [Protocol Reference](protocol.md) — wire format, PacketType values, flag bits, payload layouts
- [Troubleshooting](troubleshooting.md) — common issues and diagnostic checklists
- [Performance Tuning](performance-tuning.md) — tick rate, memory budget, IL2CPP tips
- [API Reference](api/index.md) — complete C# class and method reference
- [Samples](../Samples~/BasicConnection/README.md) — runnable example projects
- [Changelog](../CHANGELOG.md) — version history

## Protocol Version

This SDK targets **RTMPE protocol v3**.

```
Header layout (13 bytes, little-endian):
  [0..1]  magic       = 0x5254 ("RT")
  [2]     version     = 3
  [3]     packet_type (see PacketType enum)
  [4]     flags       (Compressed=0x01, Encrypted=0x02, Reliable=0x04)
  [5..8]  sequence    (u32, monotonic per connection)
  [9..12] payload_len (u32)
```
