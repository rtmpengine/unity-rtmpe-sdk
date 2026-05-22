// RTMPE SDK — Runtime/Core/NetworkConstants.cs
//
// Wire-protocol constants shared between:
//  • Rust gateway  — modules/gateway/src/packet/header.rs   (source of truth)
//  • Unity SDK     — this file                               (C# mirror)
//
// ⚠  SYNC RULE: Any change to PacketType values, flag bits, MAGIC, VERSION, or
//   HEADER_SIZE in the Rust gateway MUST be mirrored here immediately, and
//   vice versa. Mismatched values will cause silent protocol failures at runtime.
//
// Header wire layout (13 bytes, all little-endian):
//  [0..1]  magic       : u16  = 0x5254
//  [2]     version     : u8   = 3
//  [3]     packet_type : u8   (see PacketType enum)
//  [4]     flags       : u8   (see PacketFlags enum)
//  [5..8]  sequence    : u32  (monotonic, per-connection)
//  [9..12] payload_len : u32  (byte count of payload following header)

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
        internal const int OFFSET_MAGIC       = 0;   // 2 bytes LE
        internal const int OFFSET_VERSION     = 2;   // 1 byte
        internal const int OFFSET_TYPE        = 3;   // 1 byte
        internal const int OFFSET_FLAGS       = 4;   // 1 byte
        internal const int OFFSET_SEQUENCE    = 5;   // 4 bytes LE
        internal const int OFFSET_PAYLOAD_LEN = 9;   // 4 bytes LE

        // ── Flag-bit hygiene ───────────────────────────────────────────────────
        /// <summary>
        /// Bitwise union of every defined <see cref="PacketFlags"/> bit.
        /// Mirrors the gateway's <c>PacketHeader::KNOWN_FLAGS</c> in
        /// <c>modules/gateway/src/packet/header.rs</c>: a header whose flags
        /// byte carries any bit outside this mask is not a frame this protocol
        /// version defines.  Must be kept in lockstep with both the
        /// <see cref="PacketFlags"/> enum and the gateway constant whenever a
        /// flag bit is introduced.
        /// </summary>
        public const byte KNOWN_FLAGS = (byte)(
              PacketFlags.Compressed
            | PacketFlags.Encrypted
            | PacketFlags.Reliable
            | PacketFlags.EnhancedRpc
            | PacketFlags.GameplayOrdered
            | PacketFlags.AppSequence);
    }

    /// <summary>
    /// Packet type discriminator (header byte offset 3).
    /// Values MUST match <c>enum PacketType</c> in <c>modules/gateway/src/packet/header.rs</c>.
    /// </summary>
    public enum PacketType : byte
    {
        // ── Legacy handshake (backward compatibility) ───────────────────────────
        Handshake         = 0x01,   // Client → Server: initial connection request
        HandshakeAck      = 0x02,   // Server → Client: handshake accepted

        // ── ECDH 4-step mutual authentication (production) ───────────────────────
        // Flow: HandshakeInit → Challenge → HandshakeResponse → SessionAck
        HandshakeInit     = 0x05,   // Client → Server: [api_key_len:2 LE][api_key:N]
        Challenge         = 0x06,   // Server → Client: [ephemeral_pub:32][static_pub:32][ed25519_sig:64] = 128 B
        HandshakeResponse = 0x07,   // Client → Server: [client_pub_key:32]
        SessionAck        = 0x08,   // Server → Client: [crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:N][gateway_caps:4 LE?]

        // ── N-1: Reconnect flow ───────────────────────────────────────────────
        // Client presents a previously-issued reconnect token to resume a
        // session without a full PSK re-authentication.  The gateway responds
        // with a normal Challenge (0x06) and the standard 4-step ECDH flow
        // continues from there.
        //
        // ReconnectInit payload: [token_len:2 LE][token:N][proof:32 optional]
        //
        // MUST stay in sync with modules/gateway/src/packet/header.rs
        // (PacketType::ReconnectInit = 0x09, ReconnectAck = 0x0A).
        ReconnectInit     = 0x09,   // Client → Server: resume previous session via reconnect token
        ReconnectAck      = 0x0A,   // Reserved — gateway responds with Challenge (0x06), not this opcode

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

        // ── Custom properties ─────────────────────────────────────────────────
        RoomPropertyUpdate   = 0x24,   // Client → Server → all: room-level property update (JSON payload)
        PlayerPropertyUpdate = 0x25,   // Client → Server → all: per-player property update (JSON payload)

        // ── Matchmaking (AutoJoinOrCreate) ───────────────────────────────────
        // Flow: Client sends MatchmakingRequest; server atomically finds an open
        //      waiting room matching (mode + lobby_name + project_id) or creates
        //      a new one, joins the player, and replies with MatchmakingResponse.
        //
       // MatchmakingRequest payload  (JSON):
        //  { "project_id": int64, "mode": string, "lobby_name"?: string,
        //    "min_players"?: int, "max_players"?: int,
        //    "player_id": string, "display_name"?: string }
        //
       // MatchmakingResponse payload (JSON):
        //  { "ok": bool, "data"?: { "room_id": string, "room_code": string, "created": bool },
        //    "error"?: string }
        //
       // MUST stay in sync with:
        //  modules/gateway/src/packet/header.rs  (PacketType::MatchmakingRequest = 0x26)
        //  modules/room/infrastructure/messaging/nats_matchmaking_handler.go
        MatchmakingRequest  = 0x26,   // Client → Server: AutoJoinOrCreate request
        MatchmakingResponse = 0x2B,   // Server → Client: matchmaking result

        // ── Lobby system (Phase 1.3) ─────────────────────────────────────────
        // Flow: LobbyJoin → server responds with current room list (JSON array).
        //      LobbyLeave is fire-and-forget; no server reply is expected.
        //      LobbyList requests a one-shot room listing with filters / sort.
        //      LobbyRoomListUpdate is a server-push update to subscribed clients.
        LobbyJoin           = 0x27,   // Client → Server: enter lobby browser (reply = current room list)
        LobbyLeave          = 0x28,   // Client → Server: exit lobby browser (fire-and-forget)
        LobbyList           = 0x29,   // Client → Server: filtered room list request (reply = JSON array)
        LobbyRoomListUpdate = 0x2A,   // Server → Client: push update when lobby changes

        // ── Room management ───────────────────────────────────────────────────
        MasterClientChanged  = 0x2C,   // Server → All clients: master-client changed (auto or manual)
        MasterClientTransfer = 0x2D,   // Client → Server: request to transfer master-client role
        KickPlayer           = 0x2E,   // Client → Server / Server → All clients: kick request / broadcast
        SceneLoaded          = 0x2F,   // Client → Server / Server → All clients: scene-load readiness

        // ── Networked object lifecycle ────────────────────────────────────────
        Spawn             = 0x30,   // Server → Client: spawn networked object
        Despawn           = 0x31,   // Server → Client: remove networked object

        // ── State synchronisation ─────────────────────────────────────────────
        StateSync         = 0x40,   // Server → Client: authoritative full snapshot
        // ── Network variable delta synchronisation ───────────────────────
        // Payload: [object_id:8 LE][tick:4 LE][var_count:1][for each: [var_id:2 LE][value_len:2 LE][value bytes...]]
        VariableUpdate    = 0x41,   // Client → Server → all room clients: dirty variable delta
        // ── Interest Management (Feature #6) ─────────────────────────────
        // Payload: [x: float LE 4 B][y: float LE 4 B] — total 8 bytes.
        // Client → Server only; opts the session into zone-filtered delivery.
        // Clients that never send this packet receive every room-wide broadcast.
        // MUST stay in sync with PacketType::PositionUpdate = 0x42 in
        // modules/gateway/src/packet/header.rs
        PositionUpdate    = 0x42,   // Client → Server: 2-D world position for interest-zone filtering
        // ── Server-authoritative input batch (Phase 2.x — 2026-04-25) ─────
        // Carries a batch of <see cref="InputPayload"/> frames captured by
        // <see cref="NetworkBehaviour.GatherInput"/>.  The Sync Service consumes
        // these from `rtmpe.input.{room_id}` and applies them in
        // RoomTicker.tickRoom — the foundation for true server-side
        // simulation, lag compensation, and anti-cheat.
        //
       // Payload (raw binary, little-endian):
        //  [count: u16 LE][payload_1: 13 bytes]…[payload_N: 13 bytes]
        //
       // Per-frame layout matches InputPayload.WriteTo (13 B):
        //  [tick: u32 LE][move_x: f32 LE][move_y: f32 LE][flags: u8]
        //
       // Player identity is NOT carried in the payload — the Sync Service
        // resolves it from the gateway's NATS envelope (session_id →
        // authoritative player_id) so a client cannot stamp another
        // player's id on its own inputs.
        //
       // MUST stay in sync with PacketType::InputPayload = 0x43 in
        // modules/gateway/src/packet/header.rs
        InputPayload      = 0x43,   // Client → Server: server-authoritative input batch
        // ── Variable batch (multi-object coalesced delta) ────────────────
        // Payload: [count:1][count × {[entry_len:2 LE][entry:N]}] where each
        // entry is a legacy 0x41 VariableUpdate payload verbatim.  Reduces
        // per-packet wire overhead when many small deltas leave the same
        // sender in one tick.  Only emitted when
        // NetworkSettings.enableVariableBatching is true; gateways that do
        // not negotiate the new type drop unrecognised packets.
        VariableBatchUpdate = 0x44, // Client → Server: per-tick coalesced variable batch
        // ── RPC system ────────────────────────────────────────────────────────
        Rpc               = 0x50,   // Client → Server: RPC request (method_id dispatch)
        RpcResponse       = 0x51,   // Server → Client: RPC response (or broadcast)
        /// <summary>
        /// Server → Client: late-joiner RPC replay buffer.
        /// Payload: [event_count:2 LE u16][for each: [payload_len:2 LE u16][payload:N bytes]]
        /// Each payload entry is a raw Enhanced RPC payload (27+ bytes, same format as Rpc 0x50 with FLAG_ENHANCED_RPC).
        /// MUST stay in sync with PacketType::RpcBufferReplay in modules/gateway/src/packet/header.rs
        /// </summary>
        RpcBufferReplay   = 0x52,   // Server → Client: buffered RPC events for late joiners

        // ── Session termination ───────────────────────────────────────────────
        Disconnect        = 0xFF,   // Client-initiated graceful disconnect
    }

    /// <summary>
    /// Packet header flags bitfield (header byte offset 4).
    /// Values MUST match <c>FLAG_*</c> constants in <c>modules/gateway/src/packet/header.rs</c>.
    /// </summary>
    [Flags]
    public enum PacketFlags : byte
    {
        None        = 0x00,
        Compressed  = 0x01,   // FLAG_COMPRESSED  — payload is LZ4-compressed
        Encrypted   = 0x02,   // FLAG_ENCRYPTED   — payload is ChaCha20-Poly1305 AEAD-encrypted
        Reliable    = 0x04,   // FLAG_RELIABLE    — packet requires KCP acknowledgement
        EnhancedRpc = 0x08,   // FLAG_ENHANCED_RPC — Rpc(0x50) payload uses 27-byte Enhanced RPC header
        GameplayOrdered = 0x10, // FLAG_GAMEPLAY_ORDERED — payload begins with a 4-byte gameplay sequence (LE u32) used to order RPC and StateSync against each other
        // The wire sequence field is the AEAD nonce counter once a session is
        // established, so the original application-level sequence is preserved
        // only inside the encrypted plaintext.  When this flag is set, the AAD
        // additionally binds a 4-byte LE u32 application sequence, allowing the
        // gateway and receiver to deduplicate or order packets without first
        // peeking at decrypted bytes.  Off by default; gateway must opt in.
        AppSequence = 0x20, // FLAG_APP_SEQUENCE — application-level monotonic sequence layered into AAD
    }

    /// <summary>
    /// Encoding selected at handshake time for network-variable state-sync
    /// payloads.  Two FlatBuffers tables coexist in the schema; the gateway
    /// fans out the negotiated variant per session so peers using different
    /// formats can share a room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="V2"/> uses <c>RTMPE.States.NetworkVariableUpdate</c> — a
    /// <c>value_type: ValueType</c> enum tag plus a <c>data: [ubyte]</c> raw
    /// byte vector.  Tag-vs-payload consistency is enforced on the read path
    /// by application-level length checks.
    /// </para>
    /// <para>
    /// <see cref="V4"/> uses <c>RTMPE.States.NetworkVariableUpdateV2</c> —
    /// the value is a discriminated <c>NetworkVariableValue</c> union.  The
    /// structural verifier validates the (tag, table-offset) pair atomically;
    /// the consumer dispatches through type-safe variant accessors.
    /// </para>
    /// </remarks>
    public enum WireFormatVersion : byte
    {
        V2 = 2,
        V4 = 4,
    }

    /// <summary>
    /// Default state-sync wire format used when no explicit negotiation has
    /// taken place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="WireFormatVersion.V4"/> is the recommended default once the
    /// gateway has been upgraded to emit it, because V4 routes every variable
    /// update through the FlatBuffers structural verifier — a malformed table
    /// is rejected before any application accessor is invoked, closing the
    /// (tag, payload) inconsistency class that V2's raw-bytes shape leaves
    /// open to defensive parsing in the consumer.
    /// </para>
    /// <para>
    /// <see cref="LegacyDefault"/> is retained as a separate constant for
    /// deployments whose gateway has not yet been upgraded — flipping a
    /// single per-build constant flips the SDK's preferred shape without
    /// requiring per-call-site source edits.
    /// </para>
    /// </remarks>
    public static class WireFormat
    {
        /// <summary>
        /// Preferred negotiated value when the deployment's gateway speaks
        /// the structural-verifier shape.  Default for greenfield deployments.
        /// </summary>
        public const WireFormatVersion Default = WireFormatVersion.V4;

        /// <summary>
        /// Wire format used by gateways that have not been upgraded.  Kept as
        /// a named constant so a deployment that still depends on the legacy
        /// shape can opt in explicitly rather than silently inheriting it.
        /// </summary>
        public const WireFormatVersion LegacyDefault = WireFormatVersion.V2;
    }
}
