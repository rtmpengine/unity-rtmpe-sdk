// RTMPE SDK — Runtime/Protocol/PacketBuilder.cs
//
// Builds outbound RTMPE packets with the correct 13-byte header.
//
// Header layout (all little-endian):
//   [0..1]  magic       : u16  = 0x5254 ("RT")
//   [2]     version     : u8   = 3
//   [3]     packet_type : u8
//   [4]     flags       : u8
//   [5..8]  sequence    : u32  (monotonically increasing, per-connection)
//   [9..12] payload_len : u32
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
        /// Build a complete packet: 13-byte header + payload.
        /// The sequence number is atomically incremented per call.
        /// </summary>
        public byte[] Build(PacketType type, PacketFlags flags, byte[] payload)
        {
            if (payload == null) payload = Array.Empty<byte>();

            // Atomic increment — no Unsafe.As required; cast uint at write time.
            // Interlocked.Increment returns int; casting to uint handles wrap-around correctly.
            uint seq = (uint)Interlocked.Increment(ref _sequenceCounter);

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
    }
}
