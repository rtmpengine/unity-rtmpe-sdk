// RTMPE SDK — Runtime/Core/NetworkConstants.cs
//
// Wire-protocol constants shared between:
//   • Rust gateway  — modules/gateway/src/packet/header.rs   (source of truth)
//   • Unity SDK     — this file                               (C# mirror)
//
// ⚠  SYNC RULE: Any change to PacketType values, flag bits, MAGIC, VERSION, or
//    HEADER_SIZE in the Rust gateway MUST be mirrored here immediately, and
//    vice versa. Mismatched values will cause silent protocol failures at runtime.
//
// Header wire layout (13 bytes, all little-endian):
//   [0..1]  magic       : u16  = 0x5254
//   [2]     version     : u8   = 3
//   [3]     packet_type : u8   (see PacketType enum)
//   [4]     flags       : u8   (see PacketFlags enum)
//   [5..8]  sequence    : u32  (monotonic, per-connection)
//   [9..12] payload_len : u32  (byte count of payload following header)

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Wire-protocol framing constants.
    /// Values are authoritative from <c>modules/gateway/src/packet/header.rs — PacketHeader</c>.
    /// </summary>
    public static class PacketProtocol
    {
        /// <summary>
        /// Protocol framing magic: 2-byte little-endian value <c>0x5254</c> = ASCII "RT".
        /// On the wire: byte[0]=0x54 ('T'), byte[1]=0x52 ('R').
        /// </summary>
        public const ushort MAGIC = 0x5254;

        /// <summary>
        /// Current protocol version byte. Must match gateway constant <c>VERSION = 3</c>.
        /// A mismatch causes the gateway to reject the packet with a version error.
        /// </summary>
        public const byte VERSION = 3;

        /// <summary>
        /// Fixed size of every packet header in bytes (13).
        /// Layout: magic(2) + version(1) + type(1) + flags(1) + sequence(4) + payload_len(4).
        /// </summary>
        public const int HEADER_SIZE = 13;

        // ── Header field byte offsets ──────────────────────────────────────────
        // Offsets are useful for zero-copy parsing with Span<byte> (Week 11+).
        internal const int OFFSET_MAGIC       = 0;   // 2 bytes LE
        internal const int OFFSET_VERSION     = 2;   // 1 byte
        internal const int OFFSET_TYPE        = 3;   // 1 byte
        internal const int OFFSET_FLAGS       = 4;   // 1 byte
        internal const int OFFSET_SEQUENCE    = 5;   // 4 bytes LE
        internal const int OFFSET_PAYLOAD_LEN = 9;   // 4 bytes LE
    }

    /// <summary>
    /// Packet type discriminator (header byte offset 3).
    /// Values MUST match <c>enum PacketType</c> in <c>modules/gateway/src/packet/header.rs</c>.
    /// </summary>
    public enum PacketType : byte
    {
        // ── Legacy handshake (Week 3 / backward compatibility) ───────────────
        Handshake         = 0x01,   // Client → Server: initial connection request
        HandshakeAck      = 0x02,   // Server → Client: handshake accepted

        // ── ECDH 4-step mutual authentication (Week 6+, production) ──────────
        // Flow: HandshakeInit → Challenge → HandshakeResponse → SessionAck
        HandshakeInit     = 0x05,   // Client → Server: [api_key_len:2 LE][api_key:N]
        Challenge         = 0x06,   // Server → Client: [ephemeral_pub:32][static_pub:32][ed25519_sig:64] = 128 B (H4 security fix)
        HandshakeResponse = 0x07,   // Client → Server: [client_pub_key:32]
        SessionAck        = 0x08,   // Server → Client: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:N]

        // ── Keep-alive ────────────────────────────────────────────────────────
        Heartbeat         = 0x03,   // Client → Server: periodic keepalive
        HeartbeatAck      = 0x04,   // Server → Client: keepalive acknowledged

        // ── Generic data ──────────────────────────────────────────────────────
        Data              = 0x10,   // Client ↔ Server: arbitrary serialised payload
        DataAck           = 0x11,   // Server → Client: data acknowledged

        // ── Room lifecycle ────────────────────────────────────────────────────
        RoomCreate        = 0x20,   // Client → Server: create new room
        RoomJoin          = 0x21,   // Client → Server / Server → Client: join room / join ack
        RoomLeave         = 0x22,   // Client → Server: leave current room
        RoomList          = 0x23,   // Client → Server: request room list

        // ── Networked object lifecycle ────────────────────────────────────────
        Spawn             = 0x30,   // Server → Client: spawn networked object
        Despawn           = 0x31,   // Server → Client: remove networked object

        // ── State synchronisation ─────────────────────────────────────────────
        StateSync         = 0x40,   // Server → Client: authoritative full snapshot

        // ── RPC system (Week 17) ─────────────────────────────────────────────
        Rpc               = 0x50,   // Client → Server: RPC request (method_id dispatch)
        RpcResponse       = 0x51,   // Server → Client: RPC response (or broadcast)

        // ── Session termination ───────────────────────────────────────────────
        Disconnect        = 0xFF,   // Client-initiated graceful disconnect
    }

    /// <summary>
    /// Packet header flags bitfield (header byte offset 4).
    /// Values MUST match <c>FLAG_*</c> constants in <c>modules/gateway/src/packet/header.rs</c>.
    /// Only three flags exist; no fragmentation bits are defined in this protocol version.
    /// </summary>
    [Flags]
    public enum PacketFlags : byte
    {
        None       = 0x00,
        Compressed = 0x01,   // FLAG_COMPRESSED — payload is LZ4-compressed
        Encrypted  = 0x02,   // FLAG_ENCRYPTED  — payload is ChaCha20-Poly1305 AEAD-encrypted
        Reliable   = 0x04,   // FLAG_RELIABLE   — packet requires KCP acknowledgement
    }
}
