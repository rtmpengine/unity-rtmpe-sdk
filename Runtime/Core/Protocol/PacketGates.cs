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
        /// <para><c>SessionAck</c> is a special case — its bootstrap
        /// encryption is opt-in via <c>NetworkSettings.ExpectEncryptedSessionAck</c>,
        /// so the live caller must override this verdict against the runtime
        /// setting.  This function reflects the static, settings-blind
        /// answer.</para>
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

            type         = (PacketType)data[PacketProtocol.OFFSET_TYPE];
            wasEncrypted = (data[PacketProtocol.OFFSET_FLAGS]
                            & (byte)PacketFlags.Encrypted) != 0;
            return HeaderValidationResult.Ok;
        }
    }
}
