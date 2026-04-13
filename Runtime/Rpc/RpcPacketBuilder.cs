// RTMPE SDK — Runtime/Rpc/RpcPacketBuilder.cs
//
// Builds payload bytes for RPC request packets (PacketType.Rpc = 0x50).
// The caller wraps the returned payload with PacketBuilder.Build() to produce
// the full wire packet (13-byte standard header + RPC payload).
//
// Wire format (all little-endian):
//   [method_id  : 4 LE u32]   — identifies the registered handler
//   [sender_id  : 8 LE u64]   — player/session ID (verified by server JWT)
//   [request_id : 4 LE u32]   — client correlation ID for async response matching
//   [payload_len: 2 LE u16]   — length of the variable method-specific payload
//   [payload    : N bytes]    — method-specific data (max 4096 bytes)
//
// Total overhead: 18 bytes + N.
//
// See Plan/week-17-rpc.md for the full architecture specification.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Builds payload byte arrays for RPC request packets.
    /// The returned bytes are the RPC payload — pass to
    /// <c>PacketBuilder.Build(PacketType.Rpc, flags, payload)</c>
    /// to produce the complete wire packet.
    /// </summary>
    public static class RpcPacketBuilder
    {
        /// <summary>
        /// Build the payload for an RPC request.
        /// </summary>
        /// <param name="methodId">Registered method identifier (see <see cref="RpcMethodId"/>).</param>
        /// <param name="senderId">Sender's session ID (gateway-assigned).</param>
        /// <param name="requestId">Client correlation ID for response matching.</param>
        /// <param name="payload">Method-specific payload bytes (may be null or empty).</param>
        /// <returns>Complete RPC payload ready for <c>PacketBuilder.Build()</c>.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="payload"/> exceeds <see cref="RpcLimits.MaxPayloadBytes"/>.
        /// </exception>
        public static byte[] BuildRequest(
            uint methodId,
            ulong senderId,
            uint requestId,
            byte[] payload = null)
        {
            if (payload == null) payload = Array.Empty<byte>();

            if (payload.Length > RpcLimits.MaxPayloadBytes)
                throw new ArgumentException(
                    $"RPC payload exceeds maximum size ({payload.Length} > {RpcLimits.MaxPayloadBytes}).",
                    nameof(payload));

            ushort payloadLen = (ushort)payload.Length;
            var result = new byte[RpcLimits.RequestHeaderSize + payload.Length];

            // [0..3] method_id (LE u32)
            result[0] = (byte)(methodId);
            result[1] = (byte)(methodId >> 8);
            result[2] = (byte)(methodId >> 16);
            result[3] = (byte)(methodId >> 24);

            // [4..11] sender_id (LE u64)
            result[4]  = (byte)(senderId);
            result[5]  = (byte)(senderId >> 8);
            result[6]  = (byte)(senderId >> 16);
            result[7]  = (byte)(senderId >> 24);
            result[8]  = (byte)(senderId >> 32);
            result[9]  = (byte)(senderId >> 40);
            result[10] = (byte)(senderId >> 48);
            result[11] = (byte)(senderId >> 56);

            // [12..15] request_id (LE u32)
            result[12] = (byte)(requestId);
            result[13] = (byte)(requestId >> 8);
            result[14] = (byte)(requestId >> 16);
            result[15] = (byte)(requestId >> 24);

            // [16..17] payload_len (LE u16)
            result[16] = (byte)(payloadLen);
            result[17] = (byte)(payloadLen >> 8);

            // [18..] payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, result, RpcLimits.RequestHeaderSize, payload.Length);

            return result;
        }

        /// <summary>
        /// Build a Ping request payload (method_id = 100, no payload).
        /// </summary>
        public static byte[] BuildPing(ulong senderId, uint requestId)
            => BuildRequest(RpcMethodId.Ping, senderId, requestId);

        /// <summary>
        /// Build a TransferOwnership request payload (method_id = 200).
        /// Payload: [object_id:8 LE u64][new_owner_len:2 LE u16][new_owner:N UTF-8].
        /// </summary>
        public static byte[] BuildTransferOwnership(
            ulong senderId,
            uint requestId,
            ulong objectId,
            string newOwnerPlayerId)
        {
            if (string.IsNullOrEmpty(newOwnerPlayerId))
                throw new ArgumentException(
                    "newOwnerPlayerId must not be null or empty.",
                    nameof(newOwnerPlayerId));

            byte[] ownerBytes = System.Text.Encoding.UTF8.GetBytes(newOwnerPlayerId);
            if (ownerBytes.Length > 256)
                throw new ArgumentException(
                    "newOwnerPlayerId UTF-8 encoding exceeds 256 bytes.",
                    nameof(newOwnerPlayerId));

            var payload = new byte[8 + 2 + ownerBytes.Length];

            // [0..7] object_id (LE u64)
            payload[0] = (byte)(objectId);
            payload[1] = (byte)(objectId >> 8);
            payload[2] = (byte)(objectId >> 16);
            payload[3] = (byte)(objectId >> 24);
            payload[4] = (byte)(objectId >> 32);
            payload[5] = (byte)(objectId >> 40);
            payload[6] = (byte)(objectId >> 48);
            payload[7] = (byte)(objectId >> 56);

            // [8..9] new_owner_len (LE u16)
            ushort ownerLen = (ushort)ownerBytes.Length;
            payload[8] = (byte)(ownerLen);
            payload[9] = (byte)(ownerLen >> 8);

            // [10..] new_owner UTF-8
            Buffer.BlockCopy(ownerBytes, 0, payload, 10, ownerBytes.Length);

            return BuildRequest(RpcMethodId.TransferOwnership, senderId, requestId, payload);
        }
    }
}
