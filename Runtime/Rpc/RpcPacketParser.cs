// RTMPE SDK — Runtime/Rpc/RpcPacketParser.cs
//
// Parses incoming RPC response packets (PacketType.RpcResponse = 0x51).
// The standard 13-byte packet header has already been stripped by the
// transport layer — this parser operates on the RPC payload portion only.
//
// Wire format (all little-endian):
//   [request_id  : 4 LE u32]   — echoed from request for correlation
//   [method_id   : 4 LE u32]   — method that produced this response
//   [sender_id   : 8 LE u64]   — server-verified sender ID
//   [success     : 1 u8]       — 1=success, 0=failure
//   [error_code  : 2 LE u16]   — 0=OK, 1-4=error (see RpcErrorCode)
//   [payload_len : 2 LE u16]   — length of optional response payload
//   [payload     : N bytes]    — optional method-specific response data
//
// Total header: 21 bytes + N.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Parsed RPC response data.
    /// </summary>
    public readonly struct RpcResponse
    {
        public readonly uint RequestId;
        public readonly uint MethodId;
        public readonly ulong SenderId;
        public readonly bool Success;
        public readonly RpcErrorCode ErrorCode;
        public readonly byte[] Payload;

        public RpcResponse(
            uint requestId,
            uint methodId,
            ulong senderId,
            bool success,
            RpcErrorCode errorCode,
            byte[] payload)
        {
            RequestId = requestId;
            MethodId  = methodId;
            SenderId  = senderId;
            Success   = success;
            ErrorCode = errorCode;
            Payload   = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Parsed incoming RPC request data (for server-side or test use).
    /// </summary>
    public readonly struct RpcRequest
    {
        public readonly uint MethodId;
        public readonly ulong SenderId;
        public readonly uint RequestId;
        public readonly byte[] Payload;

        public RpcRequest(uint methodId, ulong senderId, uint requestId, byte[] payload)
        {
            MethodId  = methodId;
            SenderId  = senderId;
            RequestId = requestId;
            Payload   = payload ?? Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Parses RPC payload bytes into structured data.
    /// All methods return false on malformed input (no exceptions).
    /// </summary>
    public static class RpcPacketParser
    {
        /// <summary>
        /// Parse an RPC response payload (received from server).
        /// </summary>
        /// <param name="data">The RPC payload bytes (after the 13-byte standard header).</param>
        /// <param name="response">The parsed response if successful.</param>
        /// <returns>True if parsing succeeded; false for truncated or malformed data.</returns>
        public static bool TryParseResponse(byte[] data, out RpcResponse response)
        {
            response = default;

            if (data == null || data.Length < RpcLimits.ResponseHeaderSize)
                return false;

            uint requestId = ReadU32LE(data, 0);
            uint methodId  = ReadU32LE(data, 4);
            ulong senderId = ReadU64LE(data, 8);
            bool success   = data[16] != 0;
            ushort errorCode  = ReadU16LE(data, 17);
            ushort payloadLen = ReadU16LE(data, 19);

            // Reject oversized payloads first (defense-in-depth: adversarial payloadLen)
            if (payloadLen > RpcLimits.MaxPayloadBytes)
                return false;

            // Validate payload length against remaining data
            if (data.Length < RpcLimits.ResponseHeaderSize + payloadLen)
                return false;

            byte[] payload;
            if (payloadLen > 0)
            {
                payload = new byte[payloadLen];
                Buffer.BlockCopy(data, RpcLimits.ResponseHeaderSize, payload, 0, payloadLen);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            response = new RpcResponse(
                requestId, methodId, senderId,
                success, (RpcErrorCode)errorCode, payload);
            return true;
        }

        /// <summary>
        /// Parse an RPC request payload (for server-side dispatch or test verification).
        /// </summary>
        /// <param name="data">The RPC payload bytes (after the 13-byte standard header).</param>
        /// <param name="request">The parsed request if successful.</param>
        /// <returns>True if parsing succeeded; false for truncated or malformed data.</returns>
        public static bool TryParseRequest(byte[] data, out RpcRequest request)
        {
            request = default;

            if (data == null || data.Length < RpcLimits.RequestHeaderSize)
                return false;

            uint methodId  = ReadU32LE(data, 0);
            ulong senderId = ReadU64LE(data, 4);
            uint requestId = ReadU32LE(data, 12);
            ushort payloadLen = ReadU16LE(data, 16);

            // Reject oversized payloads first (defense-in-depth: adversarial payloadLen)
            if (payloadLen > RpcLimits.MaxPayloadBytes)
                return false;

            // Validate payload length against remaining data
            if (data.Length < RpcLimits.RequestHeaderSize + payloadLen)
                return false;

            byte[] payload;
            if (payloadLen > 0)
            {
                payload = new byte[payloadLen];
                Buffer.BlockCopy(data, RpcLimits.RequestHeaderSize, payload, 0, payloadLen);
            }
            else
            {
                payload = Array.Empty<byte>();
            }

            request = new RpcRequest(methodId, senderId, requestId, payload);
            return true;
        }

        /// <summary>
        /// Build an RPC response payload (for server-side or test use).
        /// </summary>
        public static byte[] BuildResponse(
            uint requestId,
            uint methodId,
            ulong senderId,
            bool success,
            RpcErrorCode errorCode,
            byte[] payload = null)
        {
            if (payload == null) payload = Array.Empty<byte>();

            if (payload.Length > RpcLimits.MaxPayloadBytes)
                throw new ArgumentException(
                    $"RPC response payload exceeds maximum size ({payload.Length} > {RpcLimits.MaxPayloadBytes}).",
                    nameof(payload));

            ushort payloadLen = (ushort)payload.Length;
            var result = new byte[RpcLimits.ResponseHeaderSize + payload.Length];

            // [0..3] request_id (LE u32)
            result[0] = (byte)(requestId);
            result[1] = (byte)(requestId >> 8);
            result[2] = (byte)(requestId >> 16);
            result[3] = (byte)(requestId >> 24);

            // [4..7] method_id (LE u32)
            result[4] = (byte)(methodId);
            result[5] = (byte)(methodId >> 8);
            result[6] = (byte)(methodId >> 16);
            result[7] = (byte)(methodId >> 24);

            // [8..15] sender_id (LE u64)
            result[8]  = (byte)(senderId);
            result[9]  = (byte)(senderId >> 8);
            result[10] = (byte)(senderId >> 16);
            result[11] = (byte)(senderId >> 24);
            result[12] = (byte)(senderId >> 32);
            result[13] = (byte)(senderId >> 40);
            result[14] = (byte)(senderId >> 48);
            result[15] = (byte)(senderId >> 56);

            // [16] success (u8)
            result[16] = success ? (byte)1 : (byte)0;

            // [17..18] error_code (LE u16)
            ushort ec = (ushort)errorCode;
            result[17] = (byte)(ec);
            result[18] = (byte)(ec >> 8);

            // [19..20] payload_len (LE u16)
            result[19] = (byte)(payloadLen);
            result[20] = (byte)(payloadLen >> 8);

            // [21..] payload
            if (payload.Length > 0)
                Buffer.BlockCopy(payload, 0, result, RpcLimits.ResponseHeaderSize, payload.Length);

            return result;
        }

        // ── Little-endian readers ──────────────────────────────────────────────

        private static ushort ReadU16LE(byte[] data, int offset)
            => (ushort)(data[offset] | (data[offset + 1] << 8));

        private static uint ReadU32LE(byte[] data, int offset)
            => (uint)(data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));

        private static ulong ReadU64LE(byte[] data, int offset)
            => (ulong)data[offset]
                | ((ulong)data[offset + 1] << 8)
                | ((ulong)data[offset + 2] << 16)
                | ((ulong)data[offset + 3] << 24)
                | ((ulong)data[offset + 4] << 32)
                | ((ulong)data[offset + 5] << 40)
                | ((ulong)data[offset + 6] << 48)
                | ((ulong)data[offset + 7] << 56);
    }
}
