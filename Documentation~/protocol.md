# RTMPE SDK — Protocol Reference

> SDK Version: `com.rtmpe.sdk 1.1.0`
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
| Bit 0  | `0x01` | `Compressed`  | Payload is LZ4-compressed (applied before AEAD when `CompressIfBeneficial` shrinks the payload) |
| Bit 1  | `0x02` | `Encrypted`   | Payload is ChaCha20-Poly1305 AEAD encrypted |
| Bit 2  | `0x04` | `Reliable`    | Packet is sent over the reliable KCP channel (port 7778) |

```csharp
// C# constants
PacketFlags.Compressed = 0x01
PacketFlags.Encrypted  = 0x02
PacketFlags.Reliable   = 0x04
```

**Compression order:** `Lz4Compressor.CompressIfBeneficial` is applied to the
plaintext payload *before* AEAD sealing. The `Compressed` flag is set in both
the header and the AAD so the gateway verifies it was not tampered with in
transit. The SDK decompresses transparently; handlers always receive
uncompressed plaintext.

---

## 3. Packet Types

| Hex    | Name               | Direction | Channel   | Payload format |
|--------|--------------------|-----------|-----------|----------------|
| `0x01` | `Handshake`        | C↔S       | KCP       | FlatBuffers *(legacy, pre-v3 only)* |
| `0x02` | `HandshakeAck`     | S→C       | KCP       | FlatBuffers *(legacy, pre-v3 only)* |
| `0x03` | `Heartbeat`        | C→S       | UDP       | empty — see §13 |
| `0x04` | `HeartbeatAck`     | S→C       | UDP       | empty — see §13 |
| `0x05` | `HandshakeInit`    | C→S       | KCP       | **Raw binary** — see §5 |
| `0x06` | `Challenge`        | S→C       | KCP       | **Raw binary** — see §5 |
| `0x07` | `HandshakeResponse`| C→S       | KCP       | **Raw binary** — see §5 |
| `0x08` | `SessionAck`       | S→C       | KCP       | **Raw binary** — see §5 |
| `0x09` | `ReconnectInit`    | C→S       | KCP       | **Raw binary** — see §5 (token + optional HMAC proof) |
| `0x0A` | `ReconnectAck`     | reserved  | KCP       | reserved — future single-round-trip variant |
| `0x10` | `Data`             | C↔S       | UDP       | application-defined |
| `0x11` | `DataAck`          | S→C       | UDP       | empty |
| `0x20` | `RoomCreate`       | C↔S       | KCP       | Custom binary — see §8 |
| `0x21` | `RoomJoin`         | C↔S       | KCP       | Custom binary — see §8 |
| `0x22` | `RoomLeave`        | C↔S       | KCP       | Custom binary — see §8 |
| `0x23` | `RoomList`         | C↔S       | KCP       | Custom binary — see §8 |
| `0x30` | `Spawn`            | C↔S       | KCP       | Custom binary — see §9 |
| `0x31` | `Despawn`          | C↔S       | KCP       | Custom binary — see §9 |
| `0x40` | `StateSync`        | S→C       | UDP       | Custom binary — see §10 |
| `0x41` | `VariableUpdate`   | C→S→C     | KCP       | Custom binary — see §12 (client → server relays to room) |
| `0x50` | `Rpc`              | C→S       | KCP       | Custom binary — see §11 |
| `0x51` | `RpcResponse`      | S→C       | KCP       | Custom binary — see §11 |
| `0xFF` | `Disconnect`       | C↔S       | KCP       | empty — see §14 |

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
payload   = [nonce:12][ChaCha20-Poly1305.Seal(PSK, nonce, AAD, plaintext)]
```

The AAD binds the encrypted API key to the client's source endpoint, so a
relayed or spoofed `HandshakeInit` is rejected by the gateway:

| Family | AAD layout (binary)                                          | Length  |
|--------|--------------------------------------------------------------|---------|
| IPv4   | `[0x04][ip:4 octets, network order][port:2 LE u16]`          | 7 bytes |
| IPv6   | `[0x06][ip:16 octets, network order][port:2 LE u16]`         | 19 bytes |

The first byte (`0x04` / `0x06`) is the IP-family discriminator and matches
the gateway's AAD parser. The `UdpTransport` routing probe (see
[Architecture §3](architecture.md#3-transport-layer)) supplies the real
outgoing interface address; a failed probe falls back to loopback and the
gateway will reject the handshake with an AEAD tag mismatch.

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
- `reconnect_token` — opaque single-use token that resumes the session via
  `ReconnectInit` (0x09). Used by `NetworkManager.Reconnect()`. Also used by
  the HKDF N-8 path to derive an IP-migration HMAC key.

After Step 4, both sides have derived two directional HKDF-SHA256 keys plus
a third HKDF expansion for the IP-migration HMAC key (see
[Architecture §4](architecture.md#4-crypto-layer)). All subsequent packets use
AEAD encryption.

### Reconnect shortcut — ReconnectInit (0x09)

After a transient drop (heartbeat timeout, transport error, IP migration), a
client holding a valid `reconnect_token` may call `NetworkManager.Reconnect()`
and skip the PSK path. The server replies with a normal `Challenge` (0x06),
and the full ECDH exchange resumes from that point.

```
payload = [token_len:2 LE u16][token:N UTF-8]
          [proof:32]?    (optional HMAC-SHA256 of token, for IP migration)
```

- `token` — the UTF-8 `reconnect_token` issued at the last `SessionAck`.
- `proof` — HMAC-SHA256(ip_migration_key, token_bytes). Only present when the
  SDK holds a non-null `ip_migration_key` — included so the gateway accepts a
  reconnect from a new source IP (WiFi ↔ 4G handoff).

The token is **single-use**: the gateway consumes it atomically on receipt. On
timeout the SDK clears its stored token so repeated attempts do not feed stale
tokens that always fail.

---

## 5. Raw Binary Packets (0x05–0x09)

> ⚠ **Critical:** Packets `0x05` through `0x09` use a custom binary layout, **NOT FlatBuffers**.
> Attempting to parse them as FlatBuffers tables will produce garbage or a crash.

| Packet           | Exact byte layout |
|------------------|-------------------|
| `HandshakeInit`  | `[nonce:12][ct+tag:N]` — N = plaintext + 16 (Poly1305 tag) |
| `Challenge`      | `[eph_pub:32][static_pub:32][ed25519_sig:64]` = 128 bytes exactly |
| `HandshakeResponse` | `[client_pub:32]` = 32 bytes exactly |
| `SessionAck`     | `[crypto_id:4][jwt_len:2][jwt:N][rc_len:2][rc:R]` (first AEAD-encrypted) |
| `ReconnectInit`  | `[token_len:2][token:N][proof:32]?` — see §4 reconnect shortcut |

---

## 6. AEAD Encryption on the Wire

After the handshake, **every packet is AEAD-encrypted**. The `FLAG_ENCRYPTED` bit (`0x02`)
is set in the header flags.

### Encryption (outbound)

```
1. Optionally compress the application payload:
     compressed = Lz4Compressor.CompressIfBeneficial(payload, out didCompress)
     if didCompress:
         application_payload = compressed
         flags |= 0x01                  // set FLAG_COMPRESSED BEFORE AAD
     else:
         application_payload = payload   // unchanged

2. Build plaintext (sealed under AEAD):
     plaintext = [origSeq:4 LE u32][application_payload]

3. Compute AAD (2 bytes) — using flags AFTER compression step but BEFORE step 5:
     aad[0] = packetType
     aad[1] = flags & ~0x02             // EXCLUDES Encrypted;
                                        // INCLUDES Compressed (0x01) when set;
                                        // INCLUDES Reliable (0x04) when set.

4. Build nonce (12 bytes):
     nonce[0..7]  = outboundNonceCounter (u64 LE, starts at 0, increments per packet)
     nonce[8..11] = cryptoId             (u32 LE, from SessionAck)

5. Encrypt:
     ciphertext = ChaCha20-Poly1305.Seal(
         key   = SessionKeys.EncryptKey,
         nonce = nonce,
         aad   = aad,
         input = plaintext
     )
     // ciphertext length = plaintext length + 16 (Poly1305 tag appended)

6. Update header:
     header.sequence    = nonce counter   (receiver reconstructs nonce from this)
     header.flags      |= 0x02            (set Encrypted flag — AFTER AAD is fixed)
     header.payload_len = len(ciphertext)
```

> **Why `FLAG_COMPRESSED` is covered by AAD:** a MITM that strips the flag
> would cause the receiver to skip decompression and treat raw compressed
> bytes as the application payload. Including the flag in AAD causes Poly1305
> to reject any such tampering.

### Decryption (inbound)

```
1. Reconstruct nonce:
     nonce[0..7]  = header.sequence (treated as u64, zero-extended from u32)
     nonce[8..11] = cryptoId        (u32 LE, stored after SessionAck)

2. Compute AAD (2 bytes):
     aad[0] = packetType
     aad[1] = header.flags & ~0x02   // strip Encrypted bit only.
                                     // Preserves FLAG_COMPRESSED (0x01) and
                                     // FLAG_RELIABLE (0x04) exactly as sent.

3. Decrypt:
     plaintext = ChaCha20-Poly1305.Open(
         key   = SessionKeys.DecryptKey,
         nonce = nonce,
         aad   = aad,
         input = ciphertext
     )
     // Poly1305 tag verification failure → drop packet silently

4. Recover original sequence and payload:
     origSeq          = plaintext[0..3]  (u32 LE)
     header.sequence  = origSeq          (restored)
     inner_payload    = plaintext[4..]

5. Decompress if flagged:
     if (header.flags & 0x01) != 0:          // FLAG_COMPRESSED was set
         payload = Lz4Compressor.Decompress(inner_payload)
     else:
         payload = inner_payload

6. Clear flags and restore payload_len:
     header.flags      &= ~(0x01 | 0x02)   // strip Encrypted + Compressed;
                                           // downstream handlers always see plaintext.
     header.payload_len = len(payload)
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
> session — sufficient for continuous 30 Hz game traffic for ~4.5 years.

### Nonce exhaustion (hard stop)

The SDK tracks its outbound counter as a `long` starting at `-1`. The first
`Interlocked.Increment` returns `0`. Two thresholds mirror the Rust gateway's
`SEQUENCE_EXHAUSTION_THRESHOLD` / `NEAR_EXHAUSTION_MARGIN`:

| Threshold                          | Value (constant)              | Effect |
|------------------------------------|-------------------------------|--------|
| `OutboundNonceExhaustionThreshold` | `(long)uint.MaxValue + 1`     | Hard stop — SDK calls `Disconnect()` and logs an error |
| Near-exhaustion warning            | threshold − `1_048_576`       | `Debug.LogWarning` — ~9.7 h remaining at 30 Hz |

Any packet that would use a counter value ≥ the hard stop is refused; the
session must be fully re-established.

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

A single layout is always sent. Supply either `room_id` or `room_code`; leave the
other field empty (zero-length).

```
[room_id_len:2 LE u16][room_id:room_id_len UTF-8]
[room_code_len:2 LE u16][room_code:room_code_len UTF-8]
[display_name_len:2 LE u16][display_name:display_name_len UTF-8]
```

- **Join by room ID:** set `room_id` to the UUID; `room_code` is empty (`room_code_len = 0`).
- **Join by room code:** set `room_code` to the 6-char code; `room_id` is empty (`room_id_len = 0`).

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

## 12. NetworkVariable Wire Format (VariableUpdate — 0x41)

`VariableUpdate` (0x41) is the dedicated packet type for replicating dirty
`NetworkVariable` values. It travels over the reliable KCP channel and is
flushed at 30 Hz by the SDK for every owned, spawned object that has at least
one dirty variable. On the server it is relayed to all other clients in the room.

### Top-level payload

```
[object_id:8 LE u64]
[var_count:1 u8]
per variable:
  [var_id:2 LE u16]
  [value_len:2 LE u16]
  [value_bytes:value_len]
```

The 4-byte `[var_id][value_len]` prefix lets receivers skip entries with
unknown variable IDs without losing alignment for the rest of the packet.

### Per-type value encoding

| Type                          | Encoding |
|-------------------------------|----------|
| `NetworkVariableInt`          | 4 bytes LE i32 (`BinaryWriter.Write(int)`) |
| `NetworkVariableFloat`        | 4 bytes LE f32 (`BinaryWriter.Write(float)`) |
| `NetworkVariableBool`         | 1 byte — `0x01` true, `0x00` false |
| `NetworkVariableVector3`      | 12 bytes — `[x:4][y:4][z:4]` LE f32 |
| `NetworkVariableQuaternion`   | 16 bytes — `[x:4][y:4][z:4][w:4]` LE f32 |
| `NetworkVariableString`       | `[utf8_len:2 LE u16][utf8_bytes:N]` — the 2-byte length prefix is part of `value_bytes` |

> `BinaryWriter.Write(float)` and `BinaryWriter.Write(int)` produce little-endian output
> on all Unity platforms (x86-64, ARM LE, iOS, Android). Spawn/transform floats use the
> explicit `SingleToInt32Bits` pattern for additional clarity and big-endian portability.

### Late-join snapshot (v1.1)

When another player joins the current room, the SDK automatically re-flags
every `NetworkVariable` on every locally-owned, spawned object as dirty. The
next 30 Hz flush emits a `VariableUpdate` that carries the full current value
of every tracked variable, so the newly-joined client sees correct state
within ~33 ms instead of waiting for the next application-level value change.

The wire format is identical to a regular flush — there is no separate
"snapshot" packet type.

---

## 13. Heartbeat Packets

Heartbeat packets carry **no payload** (empty body). The 13-byte standard header is
sufficient for keep-alive purposes; the gateway identifies them by `packet_type = 0x03`.

| Packet | Hex | Direction | Interval |
|--------|-----|-----------|----------|
| `Heartbeat`    | 0x03 | C→S | Every `HeartbeatIntervalMs` (default 5 000 ms) |
| `HeartbeatAck` | 0x04 | S→C | Immediately on receipt of Heartbeat |

Three consecutive missed `HeartbeatAck` responses trigger an `OnDisconnected`
event with `DisconnectReason.ConnectionLost`. (The `Timeout` reason is reserved
for a failed **initial** handshake or **reconnect** that exceeds
`NetworkSettings.connectionTimeoutMs` — see `ConnectionTimeoutRoutine` in
`NetworkManager.cs`.)

---

## 14. Disconnect Packet

```
Hex:  0xFF
Dir:  C↔S (either side may send)
Enc:  Encrypted (FLAG_ENCRYPTED set if session is established)
Body: empty (no payload)
```

The SDK sends a `Disconnect` packet on `NetworkManager.Disconnect()` before closing
the socket, giving the gateway a chance to clean up the session synchronously.
The packet carries no payload; the reason is implicit (connection closure).

---

*RTMPE SDK 1.1.0 — [Getting Started](getting-started.md) — [Architecture](architecture.md) — [API Reference](api/index.md)*
