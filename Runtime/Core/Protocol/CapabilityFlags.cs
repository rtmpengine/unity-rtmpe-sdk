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
