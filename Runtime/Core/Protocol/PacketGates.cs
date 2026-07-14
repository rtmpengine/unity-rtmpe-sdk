// RTMPE SDK — Runtime/Core/Protocol/PacketGates.cs
//
// Static, pure decision tables for inbound packet admission.  Each function
// answers a single yes/no question against a <see cref="PacketType"/>; no
// instance state, no Unity dependencies, no logging side effects.
//
// Why this lives here (rather than as private statics on NetworkManager):
//   • The two predicates encode the SDK's wire-format security contract —
//     "every type that depends on session-bound state must arrive AEAD-
//     authenticated", "every game-data type requires SessionAck before
//     it is dispatched".  An audit of which types fall into either bucket
//     is a security review; isolating the tables makes that review
//     independently scope-able.
//   • Both functions are exercised directly from tests (Tier0SecurityTests,
//     Tier1, …) — keeping them in a free-standing static class lets the
//     test fixture compile against this single file rather than dragging
//     in NetworkManager's transitive dependency surface.
//   • Any routing layer that consumes these predicates does not own their
//     definitions, keeping routing logic focused on dispatch rather than
//     policy.
//
// Backward compatibility:
//   NetworkManager retains <c>RequiresEncryption</c> and
//   <c>RequiresActiveSession</c> as thin passthroughs so existing test
//   fixtures (e.g. Tier0SecurityTests) keep working without modification.

using RTMPE.Protocol;

namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Outcome of <see cref="PacketGates.ValidateHeader"/>. The caller maps
    /// each non-<see cref="Ok"/> verdict onto its own rate-limited warning
    /// latch (one-second backoff per failure mode) so a flood of malformed
    /// packets cannot saturate the log pipeline.
    /// </summary>
    internal enum HeaderValidationResult
    {
        /// <summary>Header is well-formed; out-parameters are populated.</summary>
        Ok,
        /// <summary><paramref name="data"/> is null OR <paramref name="length"/> &lt; <see cref="PacketProtocol.HEADER_SIZE"/>.</summary>
        TooShort,
        /// <summary>Two-byte magic at <see cref="PacketProtocol.OFFSET_MAGIC"/> does not equal <see cref="PacketProtocol.MAGIC"/>.</summary>
        BadMagic,
        /// <summary>Version byte at <see cref="PacketProtocol.OFFSET_VERSION"/> does not equal <see cref="PacketProtocol.VERSION"/>.</summary>
        UnsupportedVersion,
        /// <summary>Flags byte at <see cref="PacketProtocol.OFFSET_FLAGS"/> carries a bit outside <see cref="PacketProtocol.KNOWN_FLAGS"/>.</summary>
        MalformedFlags,
        /// <summary>Type byte at <see cref="PacketProtocol.OFFSET_TYPE"/> does not match any defined <see cref="PacketType"/> opcode.  Mirrors the Rust gateway's <c>PacketType::try_from</c> strict reject so the SDK and gateway agree on the set of acceptable bytes.</summary>
        UnknownType,
    }

    internal static class PacketGates
    {
        /// <summary>
        /// Allow-list of inbound packet types that MUST arrive AEAD-encrypted
        /// once the session has reached SessionAck.  The handshake exchange
        /// itself cannot be encrypted (no keys yet); everything that depends
        /// on session-bound state (SessionAck's crypto_id / JWT, room
        /// lifecycle, RPCs, spawn / despawn, server-pushed state, the
        /// graceful Disconnect signal) must be authenticated under the
        /// derived ChaCha20-Poly1305 key or it could be forged by an
        /// off-path attacker who only sees the wire.
        ///
        /// <para><c>SessionAck</c> is excluded from the mandatory-encryption
        /// set: it is the bootstrap envelope and never travels under the
        /// session AEAD key (it carries the <c>crypto_id</c> that key
        /// depends on).  Its confidentiality is instead a negotiated
        /// property of the handshake — <see cref="CapabilityFlags.EncryptedSessionAck"/>
        /// — sealed under a one-time ECDH-derived key and verified by the
        /// SessionAck handler against the gateway's advertised caps.</para>
        /// </summary>
        public static bool RequiresEncryption(PacketType type)
        {
            switch (type)
            {
                // Pre-handshake — keys do not exist yet.
                case PacketType.Handshake:
                case PacketType.HandshakeAck:
                case PacketType.HandshakeInit:
                case PacketType.Challenge:
                case PacketType.HandshakeResponse:
                case PacketType.ReconnectInit:
                case PacketType.ReconnectAck:
                // Handshake rejection — emitted before any key exists, so it
                // travels in cleartext like the rest of the pre-session exchange.
                case PacketType.HandshakeError:
                // SessionAck — bootstrap envelope; negotiated encryption,
                // never the session AEAD key.
                case PacketType.SessionAck:
                    return false;

                // Everything else carries session-bound semantics.
                default:
                    return true;
            }
        }

        /// <summary>
        /// Game-data packet types — payloads that mutate session-bound state
        /// and therefore require the session-established gate to have closed
        /// before they are dispatched.  Pre-session traffic is dropped at
        /// the dispatcher gate; post-session-but-pre-room traffic is dropped
        /// at each handler's existing InRoom check (defence-in-depth).
        /// </summary>
        public static bool RequiresActiveSession(PacketType type)
        {
            switch (type)
            {
                case PacketType.Spawn:
                case PacketType.Despawn:
                case PacketType.VariableUpdate:
                case PacketType.VariableBatchUpdate:
                case PacketType.Rpc:
                case PacketType.RpcResponse:
                case PacketType.RpcBufferReplay:
                case PacketType.RoomPropertyUpdate:
                case PacketType.PlayerPropertyUpdate:
                case PacketType.StateSync:
                case PacketType.Data:
                case PacketType.DataAck:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Decide whether a cleartext inbound buffer must be copied into an
        /// exact frame-length array before dispatch.
        ///
        /// <para>An as-received frame may sit inside an oversized buffer rented
        /// from <see cref="System.Buffers.ArrayPool{T}"/>, so handlers that read
        /// <c>data.Length</c> need an exact-sized copy cut to the on-wire frame
        /// length.  A frame that a decrypt stage has already replaced, however,
        /// is a freshly-allocated plaintext buffer whose length IS the decrypted
        /// size — strictly smaller than the on-wire frame length, because the
        /// AEAD tag (and any sub-header) has been removed.  Copying that buffer
        /// at the stale on-wire length would read past its end; a decrypted
        /// payload is therefore never re-sliced.</para>
        /// </summary>
        /// <param name="payloadAlreadyExact">
        /// True when a decrypt stage has already produced an exact-sized
        /// plaintext frame (e.g. the bootstrap-encrypted <c>SessionAck</c>
        /// path), so the buffer length is authoritative and the on-wire
        /// <paramref name="frameLength"/> no longer describes it.</param>
        /// <param name="bufferLength">Current <c>data.Length</c>.</param>
        /// <param name="frameLength">On-wire frame length in bytes.</param>
        public static bool RequiresExactFrameCopy(
            bool payloadAlreadyExact, int bufferLength, int frameLength)
            => !payloadAlreadyExact && bufferLength != frameLength;

        // ── Header validation ─────────────────────────────────────────────
        /// <summary>
        /// Validate the 13-byte wire header in <paramref name="data"/> and,
        /// on success, surface the packet type and the FLAG_ENCRYPTED bit
        /// for the caller. Pure / side-effect-free: no logging, no Unity
        /// dependency — every diagnostic decision is left to the caller so
        /// the per-failure-mode warning rate-limit (one-second backoff
        /// latches on <c>NetworkManager</c>) stays at the call site where
        /// the contextual data lives.
        /// </summary>
        /// <param name="data">Raw inbound buffer (may exceed packet length).</param>
        /// <param name="length">Authoritative packet length in bytes.</param>
        /// <param name="type">On <see cref="HeaderValidationResult.Ok"/>: packet type byte at <see cref="PacketProtocol.OFFSET_TYPE"/>.</param>
        /// <param name="wasEncrypted">On <see cref="HeaderValidationResult.Ok"/>: true iff <see cref="PacketFlags.Encrypted"/> is set in the flags byte.</param>
        public static HeaderValidationResult ValidateHeader(
            byte[] data,
            int length,
            out PacketType type,
            out bool wasEncrypted)
        {
            type = default;
            wasEncrypted = false;

            if (data == null || length < PacketProtocol.HEADER_SIZE)
                return HeaderValidationResult.TooShort;

            var magic = (ushort)(
                  data[PacketProtocol.OFFSET_MAGIC]
                | (data[PacketProtocol.OFFSET_MAGIC + 1] << 8));
            if (magic != PacketProtocol.MAGIC)
                return HeaderValidationResult.BadMagic;

            if (data[PacketProtocol.OFFSET_VERSION] != PacketProtocol.VERSION)
                return HeaderValidationResult.UnsupportedVersion;

            // Refuse a header whose flags byte carries an undefined bit.  The
            // gateway only ever emits bits within KNOWN_FLAGS, so a frame with
            // an out-of-set bit is corrupt, tampered, or from a protocol
            // version this build does not implement — rejecting it before
            // dispatch keeps an undefined bit from steering any later branch.
            if ((data[PacketProtocol.OFFSET_FLAGS] & ~PacketProtocol.KNOWN_FLAGS) != 0)
                return HeaderValidationResult.MalformedFlags;

            // Refuse a type byte that does not match a defined opcode.  C# enum
            // casts are unchecked, so without this guard `(PacketType)0x99`
            // would surface as a valid-looking value and fall through every
            // downstream switch's default arm.  The Rust gateway's
            // `PacketType::try_from(u8)` strict-rejects unknown bytes at
            // parse time; mirroring that policy here keeps the two sides in
            // lockstep on what counts as a wire-acceptable frame.
            type = (PacketType)data[PacketProtocol.OFFSET_TYPE];
            if (!IsKnownPacketType(type))
                return HeaderValidationResult.UnknownType;

            wasEncrypted = (data[PacketProtocol.OFFSET_FLAGS]
                            & (byte)PacketFlags.Encrypted) != 0;
            return HeaderValidationResult.Ok;
        }

        /// <summary>
        /// Strict allow-list of every defined <see cref="PacketType"/> opcode.
        /// MUST stay in lockstep with the enum declaration in
        /// <c>NetworkConstants.cs</c> and with
        /// <c>PacketType::try_from</c> in
        /// <c>modules/gateway/src/packet/header.rs</c> — protocol-sync invariant.
        /// A new wire-format opcode must be added here in the same commit so
        /// the receiver does not treat the new frame as <c>UnknownType</c>.
        /// </summary>
        internal static bool IsKnownPacketType(PacketType type)
        {
            switch (type)
            {
                // Legacy handshake
                case PacketType.Handshake:
                case PacketType.HandshakeAck:
                // Keep-alive
                case PacketType.Heartbeat:
                case PacketType.HeartbeatAck:
                // ECDH 4-step handshake
                case PacketType.HandshakeInit:
                case PacketType.Challenge:
                case PacketType.HandshakeResponse:
                case PacketType.SessionAck:
                // Reconnect
                case PacketType.ReconnectInit:
                case PacketType.ReconnectAck:
                // Handshake rejection
                case PacketType.HandshakeError:
                // Generic data
                case PacketType.Data:
                case PacketType.DataAck:
                // Room lifecycle
                case PacketType.RoomCreate:
                case PacketType.RoomJoin:
                case PacketType.RoomLeave:
                case PacketType.RoomList:
                // Custom properties
                case PacketType.RoomPropertyUpdate:
                case PacketType.PlayerPropertyUpdate:
                // Matchmaking
                case PacketType.MatchmakingRequest:
                case PacketType.MatchmakingResponse:
                // Lobby
                case PacketType.LobbyJoin:
                case PacketType.LobbyLeave:
                case PacketType.LobbyList:
                case PacketType.LobbyRoomListUpdate:
                // Room management
                case PacketType.MasterClientChanged:
                case PacketType.MasterClientTransfer:
                case PacketType.KickPlayer:
                case PacketType.SceneLoaded:
                // Networked-object lifecycle
                case PacketType.Spawn:
                case PacketType.Despawn:
                // State sync + variable updates
                case PacketType.StateSync:
                case PacketType.VariableUpdate:
                case PacketType.PositionUpdate:
                case PacketType.InputPayload:
                case PacketType.VariableBatchUpdate:
                // RPC
                case PacketType.Rpc:
                case PacketType.RpcResponse:
                case PacketType.RpcBufferReplay:
                // Session termination
                case PacketType.Disconnect:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// True when an inbound <c>SessionAck</c> must be refused as a
        /// bootstrap downgrade: this client advertised
        /// <see cref="CapabilityFlags.EncryptedSessionAck"/> in its
        /// HandshakeResponse, yet the envelope arrived without
        /// <c>PacketFlags.Encrypted</c>.
        ///
        /// The LOCAL advertisement is the only tamper-proof input to this
        /// decision.  Capability bytes travel outside the signed Ed25519
        /// transcript and outside any AEAD, so on-path tampering can clear
        /// both the <c>client_caps</c> the gateway saw (forcing a cleartext
        /// envelope) and the <c>gateway_caps</c> echo inside that cleartext
        /// envelope (erasing the evidence) — any refusal keyed on packet
        /// contents can be disarmed by the same hand that forced the
        /// downgrade.  Keying on what this client itself advertised cannot
        /// be: the attacker does not control local state.  A gateway unable
        /// to seal the bootstrap is rejected by design; the JWT and the
        /// reconnect token it would deliver in the clear are the session's
        /// bearer credentials.
        /// </summary>
        internal static bool IsSessionAckDowngrade(
            CapabilityFlags localCaps, bool wasEncrypted)
        {
            return (localCaps & CapabilityFlags.EncryptedSessionAck) != 0
                && !wasEncrypted;
        }

        // ── Handshake rejection (0x0B) decode ─────────────────────────────────
        //
        // Category bytes mirror the gateway's encoder in
        // modules/gateway/src/session/handshake.rs — protocol-sync invariant: a
        // new category must be added on both sides in the same commit.

        /// <summary>Opaque category: the diagnostics-off default, an unknown
        /// future code, and (distinctly) the internal-fault family. Rendered with
        /// the receiver's own generic text.</summary>
        internal const byte HandshakeErrorGeneric = 0xFF;
        internal const byte HandshakeErrorInvalidApiKey = 0x01;
        internal const byte HandshakeErrorProjectLimit = 0x02;
        internal const byte HandshakeErrorInvalidFormat = 0x03;
        internal const byte HandshakeErrorReconnect = 0x04;
        internal const byte HandshakeErrorUnavailable = 0x05;
        internal const byte HandshakeErrorInternal = 0xFE;

        /// <summary>
        /// Decode a <c>HandshakeError</c> payload —
        /// <c>[code:1][reason_len:2 LE][reason:reason_len UTF-8]</c> — into its
        /// category byte and the server-supplied reason text. Returns
        /// <c>false</c> for a truncated or length-inconsistent payload so a
        /// malformed frame yields no partial value. The decoded reason is
        /// untrusted: the frame is pre-session and unauthenticated, so callers
        /// surface <see cref="DescribeHandshakeError"/> rather than the raw text.
        /// </summary>
        internal static bool TryParseHandshakeError(byte[] payload, out byte code, out string reason)
        {
            code = HandshakeErrorGeneric;
            reason = string.Empty;
            if (payload == null || payload.Length < 3)
                return false;
            code = payload[0];
            int len = payload[1] | (payload[2] << 8);
            if (payload.Length < 3 + len)
                return false;
            reason = len > 0
                ? System.Text.Encoding.UTF8.GetString(payload, 3, len)
                : string.Empty;
            return true;
        }

        /// <summary>
        /// The receiver's own trusted account of a handshake-rejection category.
        /// Keyed on the category byte, never the server-supplied text, so a forged
        /// pre-session frame can only pick among these fixed messages instead of
        /// injecting arbitrary display text into the client.
        /// </summary>
        internal static string DescribeHandshakeError(byte code)
        {
            switch (code)
            {
                case HandshakeErrorInvalidApiKey:
                    return "the server rejected the API key (invalid, deactivated, or unrecognised)";
                case HandshakeErrorProjectLimit:
                    return "the project has reached its concurrent-connection limit";
                case HandshakeErrorInvalidFormat:
                    return "the handshake was malformed — verify the API-key path (sealed-box key vs PSK) and the pinned server key";
                case HandshakeErrorReconnect:
                    return "the reconnect token was invalid or expired — reconnect with a full handshake";
                case HandshakeErrorUnavailable:
                    return "the gateway is temporarily unavailable — retry shortly";
                case HandshakeErrorInternal:
                    return "the server encountered an internal error during the handshake";
                default:
                    return "the server declined the handshake — verify the API key, the sealed-box or pinned server key, and the project connection limit";
            }
        }
    }
}
