// RTMPE SDK — Runtime/Rpc/EnhancedRpcPacketParser.cs
//
// Parses inbound Enhanced RPC payload bytes.
// The standard 13-byte packet header has already been stripped by PacketParser.ExtractPayload()
// before this parser is called.
//
// Enhanced RPC payload layout (all little-endian):
//   [method_id   :  4 LE u32]  FNV-1a("TypeName.MethodName")
//   [sender_id   :  8 LE u64]  gateway-verified session ID
//   [request_id  :  4 LE u32]  client correlation ID
//   [object_id   :  8 LE u64]  NetworkBehaviour.NetworkObjectId
//   [target      :  1 u8]      RpcTarget (All=0x00, Others=0x01, Server=0x02)
//   [rpc_flags   :  1 u8]      reserved
//   [param_count :  1 u8]      number of typed parameters
//   [params…]                  typed param stream (RpcSerializer format)
//
// Total fixed header: 27 bytes.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Parsed representation of an inbound Enhanced RPC request.
    /// </summary>
    public sealed class EnhancedRpcRequest
    {
        /// <summary>FNV-1a method ID (matches <c>RpcRegistry.ComputeMethodId</c>).</summary>
        public uint MethodId { get; }

        /// <summary>Gateway-verified sender session ID.</summary>
        public ulong SenderId { get; }

        /// <summary>Client-assigned correlation ID (round-tripped to response).</summary>
        public uint RequestId { get; }

        /// <summary>The <c>NetworkBehaviour.NetworkObjectId</c> that originated the call.</summary>
        public ulong ObjectId { get; }

        /// <summary>Delivery audience declared by the sender.</summary>
        public RpcTarget Target { get; }

        /// <summary>Decoded typed argument array (may be empty, never null).</summary>
        public object[] Args { get; }

        internal EnhancedRpcRequest(
            uint methodId, ulong senderId, uint requestId,
            ulong objectId, RpcTarget target, object[] args)
        {
            MethodId  = methodId;
            SenderId  = senderId;
            RequestId = requestId;
            ObjectId  = objectId;
            Target    = target;
            Args      = args ?? Array.Empty<object>();
        }
    }

    /// <summary>
    /// Parses Enhanced RPC payload bytes into a structured <see cref="EnhancedRpcRequest"/>.
    /// </summary>
    public static class EnhancedRpcPacketParser
    {
        /// <summary>
        /// Attempt to parse an Enhanced RPC payload.
        /// </summary>
        /// <param name="payload">
        /// The RPC payload bytes (after the 13-byte standard packet header has been removed
        /// via <c>PacketParser.ExtractPayload()</c>).
        /// </param>
        /// <param name="request">Populated on success; <see langword="null"/> on failure.</param>
        /// <returns>
        /// <see langword="true"/> when parsing succeeded;
        /// <see langword="false"/> for truncated or malformed data.
        /// </returns>
        public static bool TryParse(byte[] payload, out EnhancedRpcRequest request)
        {
            request = null;

            if (payload == null || payload.Length < RpcLimits.EnhancedRequestHeaderSize)
                return false;

            int offset = 0;

            uint  methodId  = RpcSerializer.ReadU32LE(payload, offset); offset += 4;
            ulong senderId  = RpcSerializer.ReadU64LE(payload, offset); offset += 8;
            uint  requestId = RpcSerializer.ReadU32LE(payload, offset); offset += 4;
            ulong objectId  = RpcSerializer.ReadU64LE(payload, offset); offset += 8;

            byte targetByte = payload[offset++];
            // rpc_flags — reserved, skip
            offset++;
            byte paramCount = payload[offset++];

            // offset is now 27 (= EnhancedRequestHeaderSize)

            RpcTarget target = (RpcTarget)targetByte;

            // Decode typed parameters.
            var args = new object[paramCount];
            for (int i = 0; i < paramCount; i++)
            {
                object val = RpcSerializer.ReadParam(payload, ref offset);
                if (offset == -1)
                    return false;   // truncated or unknown type
                args[i] = val;
            }

            request = new EnhancedRpcRequest(
                methodId, senderId, requestId, objectId, target, args);
            return true;
        }
    }
}
