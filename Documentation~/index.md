# RTMPE SDK — Documentation

Welcome to the RTMPE SDK documentation.

## Sections

- [Quick Start](getting-started.md) — connect your first game in under 15 minutes
- [Architecture](architecture.md) — SDK layers, threading model, transport options
- [Protocol Reference](protocol.md) — wire format, PacketType values, flag bits
- [API Reference](api/index.md) — C# class and method reference
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
