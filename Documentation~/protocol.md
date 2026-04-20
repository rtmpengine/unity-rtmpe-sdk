# RTMPE SDK — Protocol Reference

> SDK Version: `com.rtmpe.sdk 1.0.0`  
> Protocol Version: **v3** — Magic `0x5254` ("RT")

---

## Table of Contents

1. [Packet Header](#1-packet-header)
2. [Flag Bits](#2-flag-bits)
3. [Packet Types](#3-packet-types)
4. [Handshake Sequence](#4-handshake-sequence)
5. [Raw Binary Packets (0x05–0x08)](#5-raw-binary-packets-0x050x08)
6. [AEAD Encryption on the Wire](#6-aead-encryption-on-the-wire)
7. [Nonce Construction](#7-nonce-construction)
8. [Room Operation Payloads](#8-room-operation-payloads)
9. [Spawn / Despawn Payloads](#9-spawn--despawn-payloads)
10. [State Sync Payload](#10-state-sync-payload)
11. [RPC Payloads](#11-rpc-payloads)
12. [NetworkVariable Wire Format](#12-networkvariable-wire-format)
13. [Heartbeat Packets](#13-heartbeat-packets)
14. [Disconnect Packet](#14-disconnect-packet)

---

## 1. Packet Header

Every packet starts with a fixed **13-byte binary header** in **little-endian** byte order,
followed by a variable-length payload.

```
Offset  Size  Field        Notes
──────  ────  ───────────  ──────────────────────────────────────────────────
  0       2   magic        0x5254  ("RT" in ASCII, bytes: 0x54 0x52)
  2       1   version      3  (protocol version 3)
  3       1   packet_type  PacketType enum value
  4       1   flags        Bitset — see §2
  5       4   sequence     u32 LE — monotonic per connection; doubles as nonce counter
  9       4   payload_len  u32 LE — byte length of the payload that follows
 13      ..   payload      N bytes — format depends on packet_type
```

> **Size validation:** The SDK enforces `payload_len ≤ 1 MiB` (`PacketParser.ExtractPayload`).
> Packets exceeding this limit are dropped.

### C# constants (`PacketProtocol`)

```csharp
PacketProtocol.MAGIC        = 0x5254
PacketProtocol.VERSION      = 3
PacketProtocol.HEADER_SIZE  = 13
```

---

## 2. Flag Bits

The `flags` byte is a bitmask. Multiple flags may be set simultaneously.

| Bit    | Hex    | Name          | Meaning |
|--------|--------|---------------|---------|
| Bit 0  | `0x01` | `Compressed`  | Payload was compressed with LZ4 (reserved, not yet active) |
| Bit 1  | `0x02` | `Encrypted`   | Payload is ChaCha20-Poly1305 AEAD encrypted |
| Bit 2  | `0x04` | `Reliable`    | Packet is sent over the reliable KCP channel (port 7778) |

```csharp
// C# constants
PacketFlags.Compressed = 0x01
PacketFlags.Encrypted  = 0x02
PacketFlags.Reliable   = 0x04
```

---

## 3. Packet Types

| Hex    | Name               | Direction | Channel   | Payload format |
|--------|--------------------|-----------|-----------|----------------|
| `0x01` | `Handshake`        | C↔S       | KCP       | FlatBuffers *(legacy, W3–W5 only)* |
| `0x02` | `HandshakeAck`     | S→C       | KCP       | FlatBuffers *(legacy, W3–W5 only)* |
| `0x03` | `Heartbeat`        | C→S       | UDP       | FlatBuffers |
| `0x04` | `HeartbeatAck`     | S→C       | UDP       | FlatBuffers |
| `0x05` | `HandshakeInit`    | C→S       | KCP       | **Raw binary** — see §5 |
| `0x06` | `Challenge`        | S→C       | KCP       | **Raw binary** — see §5 |
| `0x07` | `HandshakeResponse`| C→S       | KCP       | **Raw binary** — see §5 |
| `0x08` | `SessionAck`       | S→C       | KCP       | **Raw binary** — see §5 |
| `0x10` | `Data`             | C→S       | UDP       | FlatBuffers |
| `0x11` | `DataAck`          | S→C       | UDP       | FlatBuffers |
| `0x20` | `RoomCreate`       | C→S       | KCP       | Custom binary — see §8 |
| `0x21` | `RoomJoin`         | C→S       | KCP       | Custom binary — see §8 |
| `0x22` | `RoomLeave`        | C→S       | KCP       | Custom binary — see §8 |
| `0x23` | `RoomList`         | C→S       | KCP       | Custom binary — see §8 |
| `0x30` | `Spawn`            | S→C       | KCP       | Custom binary — see §9 |
| `0x31` | `Despawn`          | S→C       | KCP       | Custom binary — see §9 |
| `0x40` | `StateSync`        | S→C       | UDP       | Custom binary — see §10 |
| `0x50` | `Rpc`              | C→S       | KCP       | Custom binary — see §11 |
| `0x51` | `RpcResponse`      | S→C       | KCP       | Custom binary — see §11 |
| `0xFF` | `Disconnect`       | C↔S       | KCP       | FlatBuffers — see §14 |

> **C↔S** = bidirectional. **C→S** = client to server. **S→C** = server to client.

---

## 4. Handshake Sequence

The connection is established with a 4-step exchange. All handshake packets are sent
over the reliable KCP channel (port 7778).

```
Step  Packet            Hex    Dir   Payload
────  ────────────────  ─────  ────  ──────────────────────────────────────────────
  1   HandshakeInit     0x05   C→S   [nonce:12][ChaCha20-Poly1305(PSK, apiKey)]
  2   Challenge         0x06   S→C   [eph_pub:32][static_pub:32][ed25519_sig:64]
  3   HandshakeResponse 0x07   C→S   [client_pub:32]
  4   SessionAck        0x08   S→C   [crypto_id:4 LE][jwt_len:2 LE][jwt:N]
                                      [rc_len:2 LE][reconnect_token:R]
                                      (first AEAD-encrypted packet)
```

### Step 1 — HandshakeInit

Client encrypts its API key with the PSK (`GATEWAY_API_KEY_ENCRYPTION_KEY_HEX`):

```
plaintext = [api_key_len:2 LE u16][api_key_bytes:N]
nonce     = random 12 bytes (from RandomNumberGenerator.GetBytes)
AAD       = [0x04][source_ip:4 bytes][source_port:2 LE u16]  (IPv4)
payload   = [nonce:12][ChaCha20-Poly1305.Seal(PSK, nonce, AAD, plaintext)]
```

### Step 2 — Challenge

Gateway proves its identity using an Ed25519 signature:

```
payload = [eph_pub:32][static_pub:32][ed25519_sig:64]
           X25519 key   pinned key    Sign(static_priv, eph_pub)

Total: 128 bytes — ParseChallenge() validates exactly 128 bytes
```

The SDK verifies the Ed25519 signature using RFC 8032 §5.1.7.
If pinning is configured (`PinnedServerPublicKeyHex`), the static public key
must match. Verification failure aborts the handshake.

### Step 3 — HandshakeResponse

```
payload = [client_pub:32]   // client's X25519 ephemeral public key
```

### Step 4 — SessionAck

```
payload = [crypto_id:4 LE][jwt_len:2 LE][jwt:jwt_len][rc_len:2 LE][reconnect_token:rc_len]
```

- `crypto_id` — a server-assigned `u32` used as the high 4 bytes of every subsequent AEAD nonce.
- `jwt` — HS256-signed JWT session token (UTF-8 encoded).
- `reconnect_token` — opaque token for reconnect (future use).

After Step 4, both sides have derived two directional HKDF-SHA256 keys (see
[Architecture §4](architecture.md#4-crypto-layer)). All subsequent packets use
AEAD encryption.

---

## 5. Raw Binary Packets (0x05–0x08)

> ⚠ **Critical:** Packets `0x05` through `0x08` use a custom binary layout, **NOT FlatBuffers**.
> Attempting to parse them as FlatBuffers tables will produce garbage or a crash.

| Packet         | Exact byte layout |
|----------------|-------------------|
| `HandshakeInit`| `[nonce:12][ct+tag:N]` — N = plaintext + 16 (Poly1305 tag) |
| `Challenge`    | `[eph_pub:32][static_pub:32][ed25519_sig:64]` = 128 bytes exactly |
| `HandshakeResponse` | `[client_pub:32]` = 32 bytes exactly |
| `SessionAck`   | `[crypto_id:4][jwt_len:2][jwt:N][rc_len:2][rc:R]` (first AEAD-encrypted) |

---

## 6. AEAD Encryption on the Wire

After the handshake, **every packet is AEAD-encrypted**. The `FLAG_ENCRYPTED` bit (`0x02`)
is set in the header flags.

### Encryption (outbound)

```
1. Build plaintext:
     plaintext = [origSeq:4 LE u32][application_payload]

2. Compute AAD (2 bytes):
     aad[0] = packetType
     aad[1] = flags WITHOUT 0x02 (before setting Encrypted flag)

3. Build nonce (12 bytes):
     nonce[0..7]  = outboundNonceCounter (u64 LE, starts at 0, increments per packet)
     nonce[8..11] = cryptoId             (u32 LE, from SessionAck)

4. Encrypt:
     ciphertext = ChaCha20-Poly1305.Seal(
         key   = SessionKeys.EncryptKey,
         nonce = nonce,
         aad   = aad,
         input = plaintext
     )
     // ciphertext length = plaintext length + 16 (Poly1305 tag appended)

5. Update header:
     header.sequence = nonce counter   (receiver reconstructs nonce from this)
     header.flags   |= 0x02            (set Encrypted flag)
     header.payload_len = len(ciphertext)
```

### Decryption (inbound)

```
1. Reconstruct nonce:
     nonce[0..7]  = header.sequence (treated as u64, zero-extended from u32)
     nonce[8..11] = cryptoId        (u32 LE, stored after SessionAck)

2. Compute AAD (2 bytes):
     aad[0] = packetType
     aad[1] = header.flags & ~0x02   (strip Encrypted bit)

3. Decrypt:
     plaintext = ChaCha20-Poly1305.Open(
         key   = SessionKeys.DecryptKey,
         nonce = nonce,
         aad   = aad,
         input = ciphertext
     )
     // Poly1305 tag verification failure → drop packet silently

4. Recover original sequence:
     origSeq          = plaintext[0..3]  (u32 LE)
     header.sequence  = origSeq          (restored)
     payload          = plaintext[4..]
```

---

## 7. Nonce Construction

The 12-byte AEAD nonce encodes the per-packet counter and the server-assigned session ID:

```
Offset  Size  Content
──────  ────  ─────────────────────────────────────────────────────
  0       8   outboundNonceCounter  (u64 LE — monotonic, starts at 0)
  8       4   cryptoId              (u32 LE — received in SessionAck)
```

> The counter is a `u64` on the gateway (Rust) but the SDK header `sequence` field
> is `u32`. The SDK stores the counter in bytes `[0..3]` LE with bytes `[4..7]` = `0x00`
> (zero-extended from u32 to u64). This provides 2³² ≈ 4 billion unique nonces per
> session — sufficient for continuous 30 Hz game traffic for 16+ hours.

**Anti-replay:** The gateway maintains a 128-bit sliding window per session. A packet
whose nonce counter falls outside the window (replayed or too old) is dropped without
decryption. AEAD tag failure always causes a silent drop.

---

## 8. Room Operation Payloads

Room packets are sent over KCP (reliable), encrypted with AEAD.

### RoomCreate (0x20) — client → server

```
[name_len:2 LE u16][name:name_len UTF-8]
[max_players:1 u8]
[is_public:1 bool]
```

### RoomJoin (0x21) — client → server

```
// Join by room ID:
[msg_kind:1 = 0x01][room_id_len:2 LE u16][room_id:N UTF-8]
[display_name_len:2 LE u16][display_name:N UTF-8]

// Join by room code:
[msg_kind:1 = 0x02][room_code_len:2 LE u16][room_code:N UTF-8]
[display_name_len:2 LE u16][display_name:N UTF-8]
```

### RoomLeave (0x22) — client → server

```
(empty payload)
```

### RoomList (0x23) — client → server

```
[public_only:1 bool]
```

### Server → Client room responses

```
// Room created / joined response:
[result_code:1]   0x00 = success, 0x01 = error
[room_id_len:2 LE][room_id:N UTF-8]
[room_code_len:2 LE][room_code:N UTF-8]
[room_name_len:2 LE][room_name:N UTF-8]
[player_count:1][max_players:1]
[local_player_id_len:2 LE][local_player_id:N UTF-8]

// Room list response:
[count:2 LE u16]
  per room:
  [room_id_len:2 LE][room_id:N]
  [room_name_len:2 LE][room_name:N]
  [room_code_len:2 LE][room_code:N]
  [player_count:1][max_players:1][is_public:1]

// Player joined event:
[player_id_len:2 LE][player_id:N UTF-8]
[display_name_len:2 LE][display_name:N UTF-8]
[is_host:1 bool]

// Player left event:
[player_id_len:2 LE][player_id:N UTF-8]
```

> **String encoding:** All strings are UTF-8. `RoomPacketBuilder.SafeEncodeUtf8`
> truncates strings to a maximum byte count while respecting multi-byte character
> boundaries (no split code points).

---

## 9. Spawn / Despawn Payloads

Spawn and despawn packets travel over KCP, encrypted with AEAD.

### SpawnRequest / Spawn (0x30) payload

```
[prefab_id:4 LE u32]
[object_id:8 LE u64]
[owner_len:2 LE u16][owner_player_id:owner_len UTF-8]
[pos_x:4 LE f32][pos_y:4 LE f32][pos_z:4 LE f32]
[rot_x:4 LE f32][rot_y:4 LE f32][rot_z:4 LE f32][rot_w:4 LE f32]
```

Minimum size: 4 + 8 + 2 + 0 + 12 + 16 = **42 bytes** (empty owner string).

> **Float encoding:** All floats are encoded little-endian using
> `BitConverter.SingleToInt32Bits` + explicit byte extraction
> (`SpawnPacketBuilder.WriteF32LE`). This is endian-safe on all Unity target platforms.

### DespawnRequest / Despawn (0x31) payload

```
[object_id:8 LE u64]
```

---

## 10. State Sync Payload

State sync packets (0x40) travel over the **unreliable UDP channel** for minimum latency.
They are AEAD-encrypted but marked as unreliable — loss is acceptable since a fresh
snapshot arrives at 30 Hz.

### StateDelta payload

```
[object_id:8 LE u64]
[changed_mask:1 u8]   bit 0 = position, bit 1 = rotation, bit 2 = scale
[if changed_mask & 0x01 (position)]:
    [pos_x:4 LE f32][pos_y:4 LE f32][pos_z:4 LE f32]
[if changed_mask & 0x02 (rotation)]:
    [rot_x:4 LE f32][rot_y:4 LE f32][rot_z:4 LE f32][rot_w:4 LE f32]
[if changed_mask & 0x04 (scale)]:
    [scl_x:4 LE f32][scl_y:4 LE f32][scl_z:4 LE f32]
```

Maximum size: 8 + 1 + 12 + 16 + 12 = **49 bytes** (all components present).

### NetworkVariable update payload (within Data 0x10)

```
[object_id:8 LE u64]
[var_count:1 u8]
per variable:
  [var_id:2 LE u16]
  [value_len:2 LE u16]
  [value_bytes:value_len]
```

Variable value encodings by type:

| Type                    | Encoding |
|-------------------------|----------|
| `NetworkVariableInt`    | 4 bytes LE i32 (`BinaryWriter.Write(int)`) |
| `NetworkVariableFloat`  | 4 bytes LE f32 (`BinaryWriter.Write(float)`) |
| `NetworkVariableBool`   | 1 byte — `0x01` true, `0x00` false |
| `NetworkVariableVector3`| 12 bytes — `[x:4][y:4][z:4]` LE f32 |
| `NetworkVariableQuaternion`| 16 bytes — `[x:4][y:4][z:4][w:4]` LE f32 |
| `NetworkVariableString` | N bytes — UTF-8, no length prefix (use `value_len`) |

> `BinaryWriter.Write(float)` and `BinaryWriter.Write(int)` produce little-endian output
> on all Unity platforms (x86-64, ARM LE, iOS, Android). These types use the BCL writer
> directly and are safe. Spawn/transform floats use the explicit `SingleToInt32Bits`
> pattern for additional clarity and big-endian portability.

---

## 11. RPC Payloads

RPC packets travel over KCP (reliable), encrypted with AEAD.

### Rpc (0x50) — client → server

```
[method_id:4 LE u32]
[sender_id:8 LE u64]
[request_id:4 LE u32]
[payload_len:2 LE u16]
[payload:payload_len bytes]
```

### RpcResponse (0x51) — server → client

```
[request_id:4 LE u32]
[method_id:4 LE u32]
[sender_id:8 LE u64]
[success:1 bool]
[error_code:2 LE u16]
[payload_len:2 LE u16]
[payload:payload_len bytes]
```

### Built-in RPC: ApplyDamage

| Field     | Type    | Value |
|-----------|---------|-------|
| method_id | u32     | `RpcDefinitions.METHOD_APPLY_DAMAGE` |
| payload   | 12 bytes | `[object_id:8 LE u64][damage:4 LE i32]` |

---

## 12. NetworkVariable Wire Format

Standalone variable-only updates (not bundled with a StateDelta) use the framing:

```
[var_id:2 LE u16][value_len:2 LE u16][value_bytes:value_len]
```

This framing allows incremental decoding: the reader advances by `value_len` per
variable without needing to know the type ahead of time.

---

## 13. Heartbeat Packets

Heartbeat uses FlatBuffers encoding for cross-language compatibility with the gateway.

| Packet | Hex | Direction | Interval |
|--------|-----|-----------|----------|
| `Heartbeat`    | 0x03 | C→S | Every `HeartbeatIntervalMs` (default 5 000 ms) |
| `HeartbeatAck` | 0x04 | S→C | Immediately on receipt of Heartbeat |

Three consecutive missed `HeartbeatAck` responses trigger `DisconnectReason.Timeout`.

---

## 14. Disconnect Packet

```
Hex:  0xFF
Dir:  C↔S (either side may send)
Enc:  Encrypted (FLAG_ENCRYPTED set if session is established)
Body: FlatBuffers — contains a reason code string
```

The SDK sends a `Disconnect` packet on `NetworkManager.Disconnect()` before closing
the socket, giving the gateway a chance to clean up the session synchronously.

---

*RTMPE SDK 1.0.0 — [Getting Started](getting-started.md) — [Architecture](architecture.md) — [API Reference](api/index.md)*
