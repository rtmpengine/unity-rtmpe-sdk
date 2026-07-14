// RTMPE SDK — Runtime/Core/Protocol/CapabilityFlags.cs
//
// Per-session capability bitmask exchanged inside the optional tails of
// HandshakeResponse (SDK → Gateway: `client_caps:4 LE`) and SessionAck
// (Gateway → SDK: `gateway_caps:4 LE`).  Each side advertises the set of
// optional protocol features it is willing AND able to honour for the
// duration of the session; the negotiated value is the bitwise AND of the
// two advertisements.  A feature only engages when both peers carry the
// same bit, which keeps every cap individually opt-in on either side and
// safe under mixed-version mesh deployments.
//
// Why a separate enum
// -------------------
// The wire field is a `u32 LE` so additional caps can land without ever
// touching the existing layout — the enum acts as the single canonical
// place where bit positions are reserved and documented.  Adding a new
// capability is a code-only change: pick the next free bit, name it
// here, and wire the gate at the consumer.  Old peers that did not learn
// the new bit advertise it as 0 and the negotiation downgrades cleanly
// without coordination.
//
// Wire-format contract
// --------------------
// The bitmask is serialised little-endian on the wire.  The 32-bit width
// is enforced through the underlying `uint`; widening would be a wire-
// format break and must be done with a versioned successor field rather
// than by silently extending this one.

using System;

namespace RTMPE.Core.Protocol
{
    /// <summary>
    /// Per-session protocol capability bits negotiated during the
    /// handshake.  The SDK advertises a 32-bit bitmask of features it is
    /// willing to honour, the gateway advertises its own, and the
    /// effective session caps are the bitwise AND of the two.  A feature
    /// is active for the session only when its bit appears in the
    /// intersection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wire encoding is `u32 LE`.  Each bit position is a permanent
    /// allocation: once a value has appeared on the wire under a given
    /// name it must not be reused for a different feature, even after the
    /// original feature is retired — peers that still send the old bit
    /// will otherwise see a different protocol contract than they expect.
    /// </para>
    /// <para>
    /// The reserved-bit policy is: any bit not explicitly named here
    /// MUST be sent as zero and MUST be ignored on receipt.  This keeps
    /// the field forward-extensible without forcing a coordinated
    /// release; a peer that learns a new bit will simply observe its
    /// counterpart's `0` and never engage the feature.
    /// </para>
    /// </remarks>
    [Flags]
    public enum CapabilityFlags : uint
    {
        /// <summary>
        /// Empty intersection — no optional features active for this
        /// session.  Equivalent to a legacy peer that does not understand
        /// any cap byte.  This is the safe default when either side
        /// omits the cap field on the wire.
        /// </summary>
        None = 0u,

        /// <summary>
        /// Bit 0 — Application-layer ARQ with peer-emitted DataAck.
        /// When both peers advertise this bit the SDK enables its
        /// outbound retransmit ladder for packets carrying
        /// <see cref="PacketFlags.Reliable"/> and the gateway emits a
        /// <see cref="PacketType.DataAck"/> (0x11) frame back for every
        /// reliable inbound frame.  Without this bit the SDK still
        /// honours the local <c>NetworkSettings.EmitArqSequence</c>
        /// opt-in for the sub-header bytes but will not register
        /// retransmit entries — the wire bytes go out once and no ACK is
        /// expected.
        /// </summary>
        ArqAck = 1u << 0,

        /// <summary>
        /// Bit 1 — Encrypted bootstrap <see cref="PacketType.SessionAck"/>.
        /// When the SDK advertises this bit the gateway seals the
        /// SessionAck (0x08) payload under the ECDH-derived bootstrap key
        /// and stamps <see cref="PacketFlags.Encrypted"/> on the header;
        /// the SDK decrypts it with the same key before parsing
        /// <c>crypto_id</c>, the JWT, and the reconnect token.  The
        /// gateway advertises this bit unconditionally, so the feature
        /// engages whenever the SDK opts in.  A peer that does not
        /// advertise the bit receives the plaintext SessionAck,
        /// preserving the pre-capability wire shape for legacy gateways.
        /// </summary>
        EncryptedSessionAck = 1u << 1,

        /// <summary>
        /// Bit 2 — Session JWT signed by the gateway's Ed25519 static
        /// identity key.  The gateway advertises this bit when the
        /// SessionAck JWT is signed with the same key that signs the
        /// <see cref="PacketType.Challenge"/> transcript — the static
        /// identity key the SDK verifies before completing ECDH.  An SDK
        /// that observes the bit verifies the JWT's signature against that
        /// key with no out-of-band key distribution.  How much independent
        /// trust the check adds is bounded by the SDK's server-key pinning
        /// mode: under strict pinning the key is an operator-pinned anchor
        /// and the check is a genuinely independent integrity check on the
        /// token; under trust-on-first-use or no-pinning it is only as
        /// strong as that mode's guarantee about the Challenge key.  It is
        /// always greater than or equal to the prior structural-only
        /// validation.  This is a one-way gateway assertion carried in the
        /// SessionAck <c>gateway_caps</c> tail: the SDK reads it directly and
        /// does not advertise it back.  A gateway running an HMAC JWT keyring
        /// does not advertise the bit, and an SDK that does not see it falls
        /// back to structural/temporal JWT validation only — so the bit never
        /// regresses a legacy deployment.
        /// </summary>
        IdentitySignedJwt = 1u << 2,

        /// <summary>
        /// Bit 3 — Client commits to including the 32-byte Round-1
        /// init-hash echo in every <see cref="PacketType.HandshakeResponse"/>
        /// (Round-2) payload that is not a reconnect.  The echo is the
        /// SHA-256 of the encrypted Round-1 <see cref="PacketType.HandshakeInit"/>
        /// wire payload and is written to <c>payload[37..69]</c>, immediately
        /// after the 4-byte capability tail at <c>payload[33..37]</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Unlike <see cref="ArqAck"/> and <see cref="EncryptedSessionAck"/>,
        /// this bit is NOT subject to a <c>min</c>-style negotiation.  When the
        /// SDK advertises it the gateway treats Round-2 without an echo as a
        /// protocol violation: the client has just committed to providing one,
        /// so its absence is contradictory and the upgrade is rejected.  When
        /// the SDK does NOT advertise it the gateway keeps accepting the
        /// echo-less wire shape for backward compatibility with pre-extension
        /// builds, but increments a deprecation counter so operators can see
        /// the legacy-bypass surface shrinking to zero before the transitional
        /// accept-path is removed.
        /// </para>
        /// <para>
        /// Reconnect <see cref="PacketType.ReconnectInit"/> flows are
        /// unaffected: the single-use reconnect token already binds Round-2
        /// to the originating Round-1, so the echo would be redundant and the
        /// gateway's <c>CLIENT_INIT_HASH_ABSENT</c> sentinel short-circuits
        /// the echo check on that path.
        /// </para>
        /// <para>
        /// Channel-binding rationale: an on-path attacker on a shared NAT
        /// segment who captures the victim's Round-1 packet can otherwise
        /// race Round-2 from the same <c>SocketAddr</c> with their own
        /// ephemeral key.  The echo proves that the Round-2 sender observed
        /// the same Round-1 packet the gateway holds in its pending-auth
        /// slot, binding the ECDH key exchange to the session that
        /// authenticated the API key.
        /// </para>
        /// </remarks>
        InitHashEcho = 1u << 3,
    }

    /// <summary>
    /// Helpers for serialising and inspecting <see cref="CapabilityFlags"/>
    /// values on the wire.  Centralised so the builder, the parser, the
    /// negotiator, and any future consumer share one canonical layout
    /// definition.
    /// </summary>
    public static class CapabilityFlagsWire
    {
        /// <summary>
        /// Number of bytes occupied by a capability bitmask on the wire.
        /// Read from this constant rather than hard-coding `4` at every
        /// call site so an evolutionary widening — done via a successor
        /// field, never by mutating the existing one — has one single
        /// place to revise the read/write loops that survive the change.
        /// </summary>
        public const int WireSize = 4;

        /// <summary>
        /// Encode <paramref name="caps"/> little-endian into the four
        /// bytes starting at <paramref name="destination"/>[<paramref name="offset"/>].
        /// The destination buffer must hold at least
        /// <see cref="WireSize"/> bytes after the supplied offset.
        /// </summary>
        public static void WriteLittleEndian(byte[] destination, int offset, CapabilityFlags caps)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (offset < 0 || offset > destination.Length - WireSize)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "Destination buffer cannot hold a 4-byte capability bitmask at the requested offset.");

            uint value = (uint)caps;
            destination[offset    ] = (byte) value;
            destination[offset + 1] = (byte)(value >>  8);
            destination[offset + 2] = (byte)(value >> 16);
            destination[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// Decode a capability bitmask from the four bytes starting at
        /// <paramref name="source"/>[<paramref name="offset"/>].  The
        /// span must contain at least <see cref="WireSize"/> bytes after
        /// the offset; out-of-range bytes are signalled to the caller via
        /// the return value of <see cref="TryReadLittleEndian"/>.
        /// </summary>
        public static CapabilityFlags ReadLittleEndian(ReadOnlySpan<byte> source, int offset)
        {
            if (offset < 0 || offset > source.Length - WireSize)
                throw new ArgumentOutOfRangeException(nameof(offset),
                    "Source span does not hold 4 bytes at the requested offset.");

            uint value = (uint)source[offset]
                       | ((uint)source[offset + 1] <<  8)
                       | ((uint)source[offset + 2] << 16)
                       | ((uint)source[offset + 3] << 24);
            return (CapabilityFlags)value;
        }

        /// <summary>
        /// Optional-tail variant of <see cref="ReadLittleEndian"/>.
        /// Returns <see langword="true"/> with the decoded value when the
        /// span carries at least four bytes from the offset; returns
        /// <see langword="false"/> with <see cref="CapabilityFlags.None"/>
        /// when the bytes are absent.  Mirrors the legacy-friendly
        /// behaviour of the wire schema where a missing cap field is
        /// semantically equivalent to a peer advertising no optional
        /// features.
        /// </summary>
        public static bool TryReadLittleEndian(
            ReadOnlySpan<byte> source, int offset, out CapabilityFlags caps)
        {
            if (offset < 0 || offset > source.Length - WireSize)
            {
                caps = CapabilityFlags.None;
                return false;
            }
            caps = ReadLittleEndian(source, offset);
            return true;
        }

        /// <summary>
        /// Bitwise-AND intersection of two capability advertisements.
        /// The session-effective cap set is exactly the features both
        /// peers committed to honouring; an advertiser that drops a bit
        /// later cannot regress an already-engaged feature within the
        /// session, so the intersection captured at handshake time is
        /// load-bearing for every subsequent gate.
        /// </summary>
        public static CapabilityFlags Negotiate(CapabilityFlags local, CapabilityFlags peer)
            => local & peer;
    }
}
