// RTMPE SDK — Runtime/Protocol/PacketParser.cs
//
// Parse inbound RTMPE packet payloads.
//
// This class only parses payloads that the CLIENT receives from the server:
//   - Challenge   (0x06): [ephemeral:32][static:32][sig:64] = 128 bytes
//   - SessionAck  (0x08): [crypto_id:4 LE][jwt_len:2 LE][jwt:N][rc_len:2 LE][rc:R]
//
// Header validation (magic, version) is done in NetworkManager.ProcessPacket.
// PacketParser only handles the *payload* (bytes after the 13-byte header).
//
// ReadOnlySpan<byte> overloads exist alongside the byte[] overloads so callers
// holding a pool-rented buffer can parse without an intermediate ExtractPayload
// allocation.  The byte[] overloads delegate to the span overloads to keep the
// two paths bit-for-bit identical and trivially auditable.

using System;
using System.Text;
using RTMPE.Core;

namespace RTMPE.Protocol
{
    /// <summary>
    /// Parses inbound RTMPE packet payloads into typed structures.
    /// All methods are static and allocation-minimal.
    /// </summary>
    public static class PacketParser
    {
        // ── Header extraction ─────────────────────────────────────────────────

        // Cumulative count of packets rejected by <see cref="ExtractPayloadSpan"/>
        // because the declared payload length exceeded the 1 MiB sanity cap.
        // Exposed for backpressure observability — every legitimate gateway
        // emits packets well under this limit, so any non-zero rate
        // surfaces either a hostile sender or a protocol-version mismatch.
        private static long _droppedOversizedCount;
        private static long _droppedTruncatedCount;
        private static long _droppedHeaderInvalidCount;

        /// <summary>
        /// Number of packets dropped with a declared payload length above the
        /// 1 MiB cap.  Polled by integration dashboards; never resets.
        /// </summary>
        public static long DroppedOversizedCount =>
            System.Threading.Interlocked.Read(ref _droppedOversizedCount);

        /// <summary>
        /// Number of packets dropped because the on-wire bytes were shorter
        /// than the declared payload length.
        /// </summary>
        public static long DroppedTruncatedCount =>
            System.Threading.Interlocked.Read(ref _droppedTruncatedCount);

        /// <summary>
        /// Number of packets dropped because the on-wire MAGIC or VERSION
        /// bytes did not match the RTMPE-v3 framing.  A non-zero rate
        /// indicates either non-RTMPE traffic on the receive socket or
        /// a protocol-version skew between the SDK and the gateway.
        /// </summary>
        public static long DroppedHeaderInvalidCount =>
            System.Threading.Interlocked.Read(ref _droppedHeaderInvalidCount);

        /// <summary>
        /// Extract the payload bytes from a full wire packet (header + payload).
        /// Returns an empty array if the packet is too short.
        /// </summary>
        public static byte[] ExtractPayload(byte[] rawPacket)
        {
            if (rawPacket == null) return Array.Empty<byte>();
            return ExtractPayloadCopy(new ReadOnlySpan<byte>(rawPacket));
        }

        /// <summary>
        /// Slice the payload bytes from a full wire packet without copying.
        /// Returns an empty span if the packet is malformed or too short.
        /// Prefer this overload when the caller is parsing a pool-rented buffer.
        /// </summary>
        public static ReadOnlySpan<byte> ExtractPayloadSpan(ReadOnlySpan<byte> rawPacket)
        {
            if (rawPacket.Length < PacketProtocol.HEADER_SIZE)
                return ReadOnlySpan<byte>.Empty;

            // Validate magic + version inside the parser, not at the caller.
            // The function is `public static` and any future caller (test
            // harness, alternate transport adapter, replay/diagnostic tool)
            // inherits the trust boundary; relying on out-of-band caller
            // discipline lets non-RTMPE noise be admitted as a "valid" empty-
            // payload slice and weakens the DroppedTruncated /
            // DroppedOversized counters as observability signals.  Same
            // defence-in-depth principle as the rest of this parser:
            // refuse non-RTMPE input independent of caller discipline.
            ushort magic = (ushort)(rawPacket[0] | (rawPacket[1] << 8));
            if (magic != PacketProtocol.MAGIC || rawPacket[2] != PacketProtocol.VERSION)
            {
                System.Threading.Interlocked.Increment(ref _droppedHeaderInvalidCount);
                return ReadOnlySpan<byte>.Empty;
            }

            uint payloadLen = (uint)(rawPacket[9]
                                   | (rawPacket[10] << 8)
                                   | (rawPacket[11] << 16)
                                   | (rawPacket[12] << 24));

            // Sanity cap: reject any payload claim larger than 1 MiB.
            // Without this guard, a crafted packet with payload_len ≥ 2^31 causes
            // the (int) cast below to go negative, bypasses the length check, and
            // then a downstream allocation throws OverflowException.
            const uint MaxPayload = 1 * 1024 * 1024;
            if (payloadLen > MaxPayload)
            {
                System.Threading.Interlocked.Increment(ref _droppedOversizedCount);
                return ReadOnlySpan<byte>.Empty;
            }

            int expectedTotal = PacketProtocol.HEADER_SIZE + (int)payloadLen;
            if (rawPacket.Length < expectedTotal)
            {
                System.Threading.Interlocked.Increment(ref _droppedTruncatedCount);
                return ReadOnlySpan<byte>.Empty;
            }
            if (payloadLen == 0) return ReadOnlySpan<byte>.Empty;

            return rawPacket.Slice(PacketProtocol.HEADER_SIZE, (int)payloadLen);
        }

        // Allocation-bearing convenience used by the byte[] overload.  The span
        // overload is preferred everywhere else.
        private static byte[] ExtractPayloadCopy(ReadOnlySpan<byte> rawPacket)
        {
            var slice = ExtractPayloadSpan(rawPacket);
            if (slice.IsEmpty) return Array.Empty<byte>();
            return slice.ToArray();
        }

        // ── Challenge (0x06) ──────────────────────────────────────────────────

        /// <summary>
        /// Parse the 128-byte <c>Challenge</c> payload.
        ///
        /// Layout: [server_ephemeral_pub:32][server_static_pub:32][ed25519_sig:64]
        /// </summary>
        public static bool ParseChallenge(
            byte[] payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload == null) return false;
            return ParseChallenge(new ReadOnlySpan<byte>(payload),
                                  out serverEphemeralPub,
                                  out serverStaticPub,
                                  out ed25519Sig);
        }

        /// <summary>
        /// Span-based parse of the 128-byte <c>Challenge</c> payload.
        /// The output arrays are freshly-allocated so the caller may keep them
        /// independently of the source buffer's lifetime.
        /// </summary>
        public static bool ParseChallenge(
            ReadOnlySpan<byte> payload,
            out byte[] serverEphemeralPub,
            out byte[] serverStaticPub,
            out byte[] ed25519Sig)
        {
            serverEphemeralPub = null;
            serverStaticPub    = null;
            ed25519Sig         = null;

            if (payload.Length != 128) return false;

            // The three sub-fields are returned as independent arrays because
            // they outlive the enclosing packet — one is fed to Ed25519Verify,
            // another is stored as the server's static identity for pinning.
            // Allocating once on a successful Challenge is unavoidable; the
            // span path simply ensures we don't allocate the redundant
            // intermediate `payload` byte[] that the legacy ExtractPayload did.
            serverEphemeralPub = payload.Slice(  0, 32).ToArray();
            serverStaticPub    = payload.Slice( 32, 32).ToArray();
            ed25519Sig         = payload.Slice( 64, 64).ToArray();
            return true;
        }

        // ── SessionAck (0x08) ─────────────────────────────────────────────────

        /// <summary>
        /// Parse the <c>SessionAck</c> payload.  Preserved for callers that
        /// have not opted into capability negotiation; see the five-argument
        /// overload to also extract the optional <c>gateway_caps</c> tail.
        ///
        /// Layout: <c>[crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R][gateway_caps:4 LE?]</c>
        /// </summary>
        [System.Obsolete(
            "Use the five-argument overload that surfaces gatewayCaps — " +
            "this overload silently discards the capability-negotiation tail " +
            "and will be removed in a future SDK version.")]
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out _);

        /// <summary>
        /// Parse the <c>SessionAck</c> payload and also extract the
        /// optional <c>gateway_caps</c> tail introduced by the capability-
        /// negotiation extension.  Missing tail yields
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/>, preserving
        /// the legacy-gateway contract.
        ///
        /// Layout: <c>[crypto_id:4 LE][jwt_len:2 LE][jwt:N][reconnect_len:2 LE][reconnect:R][gateway_caps:4 LE?]</c>
        /// </summary>
        public static bool ParseSessionAck(
            byte[] payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps)
        {
            cryptoId       = 0;
            jwtToken       = null;
            reconnectToken = null;
            gatewayCaps    = RTMPE.Core.Protocol.CapabilityFlags.None;

            if (payload == null) return false;
            return ParseSessionAck(new ReadOnlySpan<byte>(payload),
                                   out cryptoId,
                                   out jwtToken,
                                   out reconnectToken,
                                   out gatewayCaps);
        }

        /// <summary>
        /// Span-based parse of <c>SessionAck</c> with the legacy three-out
        /// shape.  String fields are decoded directly from the source span —
        /// no intermediate byte[] is allocated.  Discards the optional
        /// <c>gateway_caps</c> tail; callers that need it use the
        /// five-argument overload.
        /// </summary>
        [System.Obsolete(
            "Use the five-argument Span overload that surfaces gatewayCaps — " +
            "this overload silently discards the capability-negotiation tail " +
            "and will be removed in a future SDK version.")]
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken)
            => ParseSessionAck(
                payload,
                out cryptoId,
                out jwtToken,
                out reconnectToken,
                out _);

        /// <summary>
        /// Span-based parse of <c>SessionAck</c>.  String fields are decoded
        /// directly from the source span — no intermediate byte[] is allocated.
        /// The optional <c>gateway_caps</c> tail is surfaced through
        /// <paramref name="gatewayCaps"/>; when the payload ends before the
        /// tail (a legacy gateway response) the output is set to
        /// <see cref="RTMPE.Core.Protocol.CapabilityFlags.None"/>.  Bytes
        /// trailing the cap field are ignored by design — future protocol
        /// extensions may append further sub-fields without breaking this
        /// parser.
        /// </summary>
        public static bool ParseSessionAck(
            ReadOnlySpan<byte> payload,
            out uint   cryptoId,
            out string jwtToken,
            out string reconnectToken,
            out RTMPE.Core.Protocol.CapabilityFlags gatewayCaps)
        {
            cryptoId       = 0;
            jwtToken       = null;
            reconnectToken = null;
            gatewayCaps    = RTMPE.Core.Protocol.CapabilityFlags.None;

            if (payload.Length < 8) return false; // 4 + 2 + 0 + 2 minimum

            int offset = 0;

            cryptoId = (uint)(payload[offset]
                            | (payload[offset + 1] << 8)
                            | (payload[offset + 2] << 16)
                            | (payload[offset + 3] << 24));
            offset += 4;

            int jwtLen = payload[offset] | (payload[offset + 1] << 8);
            offset += 2;
            // Subtraction-form bounds check: the additive form
            // (offset + jwtLen > payload.Length) can overflow int when
            // jwtLen is near ushort.MaxValue and offset is large, admitting
            // the read.  Subtracting from payload.Length (always non-
            // negative, bounded by the receive ceiling) cannot wrap.
            if (jwtLen > payload.Length - offset) return false;

            try
            {
                jwtToken = jwtLen > 0
                    ? DecodeUtf8(payload.Slice(offset, jwtLen))
                    : string.Empty;
                offset += jwtLen;

                if (offset > payload.Length - 2) return false;
                int rcLen = payload[offset] | (payload[offset + 1] << 8);
                offset += 2;
                if (rcLen > payload.Length - offset) return false;

                reconnectToken = rcLen > 0
                    ? DecodeUtf8(payload.Slice(offset, rcLen))
                    : string.Empty;
                offset += rcLen;
            }
            catch (System.Text.DecoderFallbackException)
            {
                // Malformed UTF-8 in either token.  The strict decoder has
                // already discarded its partial state; reset the outputs and
                // surface a clean parse failure to the caller.
                jwtToken       = null;
                reconnectToken = null;
                return false;
            }

            // Optional `gateway_caps:4 LE` tail.  Absent on legacy gateways
            // that pre-date capability negotiation — the parser treats the
            // missing field as `CapabilityFlags.None`, which the negotiator
            // intersects with the SDK's advertised caps to disable every
            // optional feature for the session.  Any bytes beyond the cap
            // field are ignored so the wire stays forward-extensible.
            RTMPE.Core.Protocol.CapabilityFlagsWire.TryReadLittleEndian(
                payload, offset, out gatewayCaps);

            return true;
        }

        // Strict UTF-8 decoder.  Encoding.UTF8 silently substitutes U+FFFD
        // for any malformed sequence; that lets a hostile gateway smuggle
        // bytes that survive the parse but mutate downstream string-equality
        // invariants (host comparisons, reconnect-token equality with the
        // server's view).  A throwOnInvalidBytes encoder converts the same
        // input into a clean DecoderFallbackException that the caller maps
        // to a parse failure.
        private static readonly Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // Encoding.UTF8 (and its strict cousin built above) accept
        // ReadOnlySpan<byte> on .NET Standard 2.1 (Unity 2021.2+) and .NET
        // 5+.  Wrapped so call sites stay focused on parsing logic; profile
        // shows the span overload is genuinely alloc-free for ASCII tokens
        // (which JWT and reconnect tokens are).  The strict decode adds no
        // ASCII-path overhead since the validation is integrated into the
        // existing UTF-8 state machine.
        private static string DecodeUtf8(ReadOnlySpan<byte> bytes)
            => StrictUtf8.GetString(bytes);
    }
}
