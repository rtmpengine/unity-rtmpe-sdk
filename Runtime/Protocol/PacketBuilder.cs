// RTMPE SDK — Runtime/Protocol/PacketBuilder.cs
//
// Builds outbound RTMPE packets with the correct 13-byte header.
//
// Header layout (all little-endian):
//  [0..1]  magic       : u16  = 0x5254 ("RT")
//  [2]     version     : u8   = 3
//  [3]     packet_type : u8
//  [4]     flags       : u8
//  [5..8]  sequence    : u32  (monotonically increasing, per-connection)
//  [9..12] payload_len : u32
//
// The sequence counter is an instance field (NOT static) so each connection
// has its own independent counter. Sharing a PacketBuilder across connections
// is a protocol error.

using System;
using System.Threading;
using RTMPE.Core;

namespace RTMPE.Protocol
{
    /// <summary>
    /// Builds RTMPE wire-format packets.
    /// One instance per connection; never share across connections.
    /// All methods are safe to call from any thread.
    /// </summary>
    public sealed class PacketBuilder
    {
        // The sequence counter wraps naturally at uint.MaxValue (2^32-1).
        // To avoid the Unsafe.As IL2CPP issue, we store as int and cast to uint on write.
        // Initialised to -1 so the first Interlocked.Increment returns 0, matching the
        // gateway's expectation that the first packet carries sequence 0.
        private int _sequenceCounter = -1;

        // Midpoint observability: increments by 1 every time the int
        // counter crosses from int.MaxValue → int.MinValue, which
        // corresponds to the wire-domain u32 sequence transitioning from
        // 0x7FFF_FFFF → 0x8000_0000 — the MIDPOINT of u32 space, not the
        // full wrap.  This is the operationally-useful early signal: at
        // the midpoint there are still ~2 billion sends of headroom
        // before the gateway's replay-window logic begins observing
        // duplicate sequences after the actual u32 wrap, giving operators
        // a generous window to plan a re-handshake.  The TRUE u32 wrap
        // (0xFFFF_FFFF → 0x0000_0000) corresponds to the int counter
        // going from -1 to 0 — but at that point the warning is already
        // too late, so we deliberately fire the alert at the midpoint
        // crossing.
        private long _sequenceMidpointCrossingCount;

        /// <summary>
        /// Total number of times the wire sequence counter has crossed
        /// the u32 midpoint (0x7FFF_FFFF → 0x8000_0000) since this builder
        /// was constructed.  This is the operational early-warning signal
        /// for sequence wrap: at midpoint there are still ~2 billion
        /// sends of headroom before the actual u32 wrap, giving
        /// dashboards time to plan a re-handshake.  Operators that
        /// observe a non-zero count should age the connection.
        /// </summary>
        public long SequenceMidpointCrossingCount =>
            System.Threading.Interlocked.Read(ref _sequenceMidpointCrossingCount);

        /// <summary>
        /// Last assigned wire sequence (uint) — useful for dashboards that
        /// want to estimate time-to-wrap.  Reads the int counter and casts
        /// to uint via the same convention as the wire encoding.
        /// </summary>
        public uint CurrentSequence => (uint)Volatile.Read(ref _sequenceCounter);

        // ── Public factory methods ────────────────────────────────────────────

        /// <summary>
        /// Build a <c>HandshakeInit</c> packet (type 0x05).
        /// Payload is the pre-encrypted API key blob from <see cref="Crypto.ApiKeyCipher"/>.
        /// </summary>
        public byte[] BuildHandshakeInit(byte[] encryptedApiKeyPayload)
            => Build(PacketType.HandshakeInit, PacketFlags.None, encryptedApiKeyPayload);

        /// <summary>
        /// Build a <c>HandshakeResponse</c> packet (type 0x07).
        /// Payload is exactly 32 bytes: the client's X25519 ephemeral public key.
        /// </summary>
        public byte[] BuildHandshakeResponse(byte[] clientPublicKey)
        {
            if (clientPublicKey == null || clientPublicKey.Length != 32)
                throw new ArgumentException("clientPublicKey must be exactly 32 bytes.", nameof(clientPublicKey));
            return Build(PacketType.HandshakeResponse, PacketFlags.None, clientPublicKey);
        }

        /// <summary>
        /// **N-1** — build a <c>ReconnectInit</c> packet (type 0x09).
        /// <para>
        /// Payload layout: <c>[token_len: u16 LE][token: N bytes UTF-8]</c>.
        /// </para>
        /// <para>
        /// The gateway consumes the token atomically (single-use), verifies
        /// the source IP matches the binding recorded at issue time, and
        /// responds with a <see cref="PacketType.Challenge"/> that the client
        /// answers with a normal <see cref="PacketType.HandshakeResponse"/>.
        /// </para>
        /// </summary>
        /// <param name="reconnectToken">
        /// The token obtained from the previous <c>SessionAck</c>.  Must be a
        /// non-empty UTF-8 string no longer than 128 bytes (the gateway caps
        /// token length at 128).
        /// </param>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="reconnectToken"/> is null, empty, or
        /// its UTF-8 encoding exceeds 128 bytes.
        /// </exception>
        public byte[] BuildReconnectInit(string reconnectToken, byte[] proof)
        {
            if (proof == null)
                throw new ArgumentNullException(nameof(proof),
                    "proof is required.  Compute it via " +
                    nameof(ComputeReconnectProof) +
                    "(token, ipMigrationKey), or call " +
                    nameof(BuildReconnectInitWithoutProof) +
                    " when no IP-migration key was negotiated.");
            if (proof.Length != 32)
                throw new ArgumentException("proof must be exactly 32 bytes.", nameof(proof));

            return BuildReconnectInitInternal(reconnectToken, proof);
        }

        /// <summary>
        /// Build a <c>ReconnectInit</c> packet without an HMAC proof.
        /// Use this only when no IP-migration key was negotiated for the
        /// previous session (older gateway, or session torn down before
        /// HKDF expansion completed).  In all other cases, prefer
        /// <see cref="BuildReconnectInit"/> with a proof so the gateway can
        /// accept a reconnect from a new IP address (WiFi → 4G migration).
        /// </summary>
        public byte[] BuildReconnectInitWithoutProof(string reconnectToken)
        {
            return BuildReconnectInitInternal(reconnectToken, null);
        }

        /// <summary>
        /// Compute the 32-byte HMAC-SHA256 proof bound to a reconnect token.
        /// Pair with <see cref="BuildReconnectInit"/>.
        /// </summary>
        /// <param name="reconnectToken">
        /// The token returned in the previous <c>SessionAck</c>.
        /// </param>
        /// <param name="ipMigrationKey">
        /// The 32-byte IP-migration key derived alongside the session keys
        /// (HKDF info suffix <c>\x02</c> — see <c>HandshakeHandler.DeriveSessionKeys</c>).
        /// </param>
        public static byte[] ComputeReconnectProof(string reconnectToken, byte[] ipMigrationKey)
        {
            if (string.IsNullOrEmpty(reconnectToken))
                throw new ArgumentException("reconnectToken must not be null or empty.", nameof(reconnectToken));
            if (ipMigrationKey == null || ipMigrationKey.Length != 32)
                throw new ArgumentException("ipMigrationKey must be exactly 32 bytes.", nameof(ipMigrationKey));

            var tokenBytes = System.Text.Encoding.UTF8.GetBytes(reconnectToken);
            using var hmac = new System.Security.Cryptography.HMACSHA256(ipMigrationKey);
            return hmac.ComputeHash(tokenBytes);
        }

        private byte[] BuildReconnectInitInternal(string reconnectToken, byte[] proof)
        {
            if (string.IsNullOrEmpty(reconnectToken))
                throw new ArgumentException("reconnectToken must not be null or empty.", nameof(reconnectToken));

            var tokenBytes = System.Text.Encoding.UTF8.GetBytes(reconnectToken);
            if (tokenBytes.Length > 128)
                throw new ArgumentException(
                    $"reconnectToken UTF-8 length {tokenBytes.Length} exceeds 128 bytes (gateway cap).",
                    nameof(reconnectToken));

            // Payload: [token_len: u16 LE][token: N][proof: 32 optional]
            // The gateway detects the proof by checking payload.len() > 2 + token_len.
            int proofLen = proof != null ? 32 : 0;
            var payload = new byte[2 + tokenBytes.Length + proofLen];
            payload[0] = (byte)(tokenBytes.Length & 0xFF);
            payload[1] = (byte)((tokenBytes.Length >> 8) & 0xFF);
            Buffer.BlockCopy(tokenBytes, 0, payload, 2, tokenBytes.Length);
            if (proof != null)
                Buffer.BlockCopy(proof, 0, payload, 2 + tokenBytes.Length, 32);

            return Build(PacketType.ReconnectInit, PacketFlags.None, payload);
        }

        /// <summary>
        /// Build a <c>Heartbeat</c> packet (type 0x03) with no payload.
        /// </summary>
        public byte[] BuildHeartbeat()
            => Build(PacketType.Heartbeat, PacketFlags.None, Array.Empty<byte>());

        /// <summary>
        /// Build a <c>Disconnect</c> packet (type 0xFF) with no payload.
        /// </summary>
        public byte[] BuildDisconnect()
            => Build(PacketType.Disconnect, PacketFlags.None, Array.Empty<byte>());

        /// <summary>
        /// Build a <c>Data</c> packet (type 0x10) with optional encryption/compression flags.
        /// </summary>
        public byte[] BuildData(byte[] payload, PacketFlags flags = PacketFlags.None)
            => Build(PacketType.Data, flags, payload ?? Array.Empty<byte>());

        // ── Core builder ──────────────────────────────────────────────────────

        /// <summary>
        /// Wire-validation upper bound on payload length.  Matches the
        /// parser-side cap in <see cref="PacketParser"/> (1 MiB) and protects
        /// the build path against an integer-class bug that would let a
        /// caller pass a 4 GiB array into <see cref="Build"/>.  Defense in
        /// depth only — every realistic call site is bounded much more
        /// tightly by <see cref="MaxApplicationPayloadBytes"/>, which is the
        /// limit a normal builder consumer should hit first.
        /// </summary>
        public const int MaxPayloadBytes = 1 * 1024 * 1024;

        // The transport-side datagram envelope is bounded so a legitimately
        // built packet survives every link in the path (PPPoE, IPsec, IPv6
        // minimum-MTU networks).  Constants below mirror UdpTransport's
        // DefaultMaxDatagramSize without taking a Transport assembly
        // dependency from Protocol — the literal 1200 is documented in
        // UdpTransport.DefaultMaxDatagramSize and the two values must move
        // together.  An automated guard against drift is provided by the
        // transport-suite test "DatagramAndApplicationCapsAreInSync".
        private const int DefaultDatagramSizeMirror = 1200;

        // 4-byte AEAD seq prefix + 16-byte Poly1305 tag are appended by the
        // EncryptAndSend pipeline; documented in NetworkManager.cs's encrypt
        // path.  An application caller hands plaintext to the builder, so the
        // cap below pre-deducts what AEAD will add later.
        private const int AeadOverheadBytes = 4 + 16;

        /// <summary>
        /// Largest application payload that, after the 13-byte RTMPE header
        /// and the 20-byte AEAD overhead, still fits inside one
        /// <see cref="DefaultDatagramSizeMirror"/>-byte UDP datagram.
        /// Exceeding this causes an <see cref="ArgumentException"/> at the
        /// builder so the caller diagnoses the size error at the call site
        /// instead of meeting it as a delayed transport failure or, worse,
        /// silently relying on IP fragmentation (poor on mobile / CGNAT).
        /// </summary>
        public const int MaxApplicationPayloadBytes =
            DefaultDatagramSizeMirror - PacketProtocol.HEADER_SIZE - AeadOverheadBytes;

        /// <summary>
        /// Build a complete packet: 13-byte header + payload.
        /// The sequence number is atomically incremented per call.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="payload"/> exceeds
        /// <see cref="MaxApplicationPayloadBytes"/> (the layered, MTU-aware
        /// cap that fires first) or <see cref="MaxPayloadBytes"/> (the
        /// defense-in-depth wire-validation cap).
        /// </exception>
        public byte[] Build(PacketType type, PacketFlags flags, byte[] payload)
        {
            if (payload == null) payload = Array.Empty<byte>();

            // Application-layer cap fires first.  Application payloads
            // exceeding the transport MTU envelope silently rely on IP
            // fragmentation (poor on mobile / CGNAT); rejecting at the
            // builder makes the failure diagnosable at the call site instead
            // of late in the pipeline as an opaque SocketException.
            EnsureFitsInDatagram(payload.Length);

            if (payload.Length > MaxPayloadBytes)
                throw new ArgumentException(
                    $"payload length {payload.Length} exceeds PacketBuilder.MaxPayloadBytes ({MaxPayloadBytes})",
                    nameof(payload));

            // Atomic increment — no Unsafe.As required; cast uint at write time.
            // Interlocked.Increment returns int; casting to uint handles wrap-around correctly.
            int rawSeq = Interlocked.Increment(ref _sequenceCounter);
            uint seq   = (uint)rawSeq;
            // Midpoint detection: when the int counter increments from
            // int.MaxValue (= u32 0x7FFFFFFF) to int.MinValue (= u32
            // 0x80000000), the wire-domain u32 has crossed the midpoint
            // of its space.  At this point ~2 billion further sends remain
            // before the actual wrap (u32 0xFFFFFFFF → 0x00000000); the
            // alert fires here precisely so operators have time to plan a
            // re-handshake well before the gateway's replay-window dedup
            // begins observing duplicate sequences.  Log exactly once
            // per builder lifetime.
            if (rawSeq == int.MinValue)
            {
                long count = Interlocked.Increment(ref _sequenceMidpointCrossingCount);
                if (count == 1)
                {
                    UnityEngine.Debug.LogWarning(
                        "[RTMPE] PacketBuilder: wire sequence counter just crossed " +
                        "the u32 midpoint.  A full u32 wrap will occur after another " +
                        "~2 billion sends; plan a re-handshake before the gateway's " +
                        "replay-window dedup begins observing duplicate sequences.");
                }
            }

            var packet = new byte[PacketProtocol.HEADER_SIZE + payload.Length];

            // [0..1] magic (LE u16 = 0x5254)
            packet[0] = (byte)(PacketProtocol.MAGIC & 0xFF);
            packet[1] = (byte)(PacketProtocol.MAGIC >> 8);

            // [2] version
            packet[2] = PacketProtocol.VERSION;

            // [3] type
            packet[3] = (byte)type;

            // [4] flags
            packet[4] = (byte)flags;

            // [5..8] sequence (LE u32)
            packet[5] = (byte)(seq);
            packet[6] = (byte)(seq >> 8);
            packet[7] = (byte)(seq >> 16);
            packet[8] = (byte)(seq >> 24);

            // [9..12] payload_len (LE u32)
            uint payloadLen = (uint)payload.Length;
            packet[9]  = (byte)(payloadLen);
            packet[10] = (byte)(payloadLen >> 8);
            packet[11] = (byte)(payloadLen >> 16);
            packet[12] = (byte)(payloadLen >> 24);

            // Payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, packet, PacketProtocol.HEADER_SIZE, payload.Length);

            return packet;
        }

        /// <summary>
        /// Enforce that <paramref name="payloadLength"/> can be wrapped in
        /// header + AEAD overhead and still fit one transport datagram.
        /// Throws <see cref="ArgumentException"/> when the payload exceeds
        /// <see cref="MaxApplicationPayloadBytes"/>.
        /// </summary>
        /// <remarks>
        /// Public for unit-test introspection and for callers that want to
        /// pre-validate before constructing a payload buffer.  The check is
        /// embedded in <see cref="Build"/> so every builder consumer benefits
        /// without an extra call.
        /// </remarks>
        public static void EnsureFitsInDatagram(int payloadLength)
        {
            if (payloadLength < 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLength),
                    "payloadLength must be non-negative.");
            if (payloadLength > MaxApplicationPayloadBytes)
                throw new ArgumentException(
                    $"payload length {payloadLength} exceeds " +
                    $"PacketBuilder.MaxApplicationPayloadBytes ({MaxApplicationPayloadBytes}). " +
                    "Fragment the message at the application layer; do not rely on IP fragmentation.",
                    nameof(payloadLength));
        }
    }
}
