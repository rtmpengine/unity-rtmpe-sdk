// RTMPE SDK — Runtime/Rpc/EnhancedRpcPacketBuilder.cs
//
// Builds payload bytes for Enhanced RPC packets.
// The caller wraps the result with PacketBuilder.Build(PacketType.Rpc,
// PacketFlags.Reliable | PacketFlags.EnhancedRpc, payload) to produce the
// full wire packet (13-byte standard header + Enhanced RPC payload).
//
// Enhanced RPC payload layout (all little-endian):
//  [method_id   :  4 LE u32]  FNV-1a("TypeName.MethodName")
//  [sender_id   :  8 LE u64]  gateway session ID
//  [request_id  :  4 LE u32]  client-assigned correlation ID
//  [object_id   :  8 LE u64]  NetworkBehaviour.NetworkObjectId
//  [target      :  1 u8]      RpcTarget (All=0x00, Others=0x01, Server=0x02)
//  [rpc_flags   :  1 u8]      reserved, set to 0x00
//  [param_count :  1 u8]      number of typed parameters that follow
//  [params…]                  typed param stream (RpcSerializer format)
//
// Total fixed header: 27 bytes.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Builds payload byte arrays for Enhanced RPC packets.
    /// The returned bytes are the RPC payload — wrap with
    /// <c>PacketBuilder.Build(PacketType.Rpc, PacketFlags.Reliable | PacketFlags.EnhancedRpc, payload)</c>
    /// to produce the complete wire packet.
    /// </summary>
    public static class EnhancedRpcPacketBuilder
    {
        /// <summary>
        /// Build an Enhanced RPC request payload.
        /// </summary>
        /// <param name="methodId">FNV-1a hash of "TypeName.MethodName" (see <see cref="RpcRegistry"/>).</param>
        /// <param name="senderId">Gateway-assigned session ID of the sender.</param>
        /// <param name="requestId">Client-assigned correlation ID for response matching.</param>
        /// <param name="objectId"><see cref="RTMPE.Core.NetworkBehaviour.NetworkObjectId"/> of the sending object.</param>
        /// <param name="target">Delivery audience.</param>
        /// <param name="args">Typed parameters (int, float, bool, string, byte[], ulong, Vector3, Color, Quaternion).</param>
        /// <returns>Complete Enhanced RPC payload ready for <c>PacketBuilder.Build()</c>.</returns>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="args"/> contains an unsupported type, any single
        /// string/byte[] arg exceeds 65535 encoded bytes, the total param data
        /// exceeds <see cref="RpcLimits.MaxPayloadBytes"/>, or
        /// <paramref name="args"/> has more than 255 elements.
        /// </exception>
        public static byte[] Build(
            uint      methodId,
            ulong     senderId,
            uint      requestId,
            ulong     objectId,
            RpcTarget target,
            object[]  args = null)
        {
            // Zero is the gateway's "unset" sentinel for the session id field;
            // building a packet with senderId=0 produces a frame the gateway
            // and every receiving peer silently drop as spoofed.  Failing
            // loudly at the builder turns a hard-to-trace silent drop into
            // an immediate, attributable programmer error.
            if (senderId == 0)
                throw new ArgumentException(
                    "senderId must be non-zero; gateway and peers reject senderId=0 as spoofed.",
                    nameof(senderId));

            if (args == null) args = Array.Empty<object>();

            if (args.Length > byte.MaxValue)
                throw new ArgumentException(
                    $"Enhanced RPC supports at most 255 parameters; got {args.Length}.",
                    nameof(args));

            // Measure total param bytes first to allocate exactly once.
            int paramBytes = 0;
            for (int i = 0; i < args.Length; i++)
            {
                int sz = RpcSerializer.MeasureParam(args[i]);
                if (sz == 0)
                    throw new ArgumentException(
                        $"Enhanced RPC: unsupported parameter type at index {i}: " +
                        $"'{args[i]?.GetType().FullName ?? "null"}'.",
                        nameof(args));
                paramBytes += sz;
            }

            if (paramBytes > RpcLimits.MaxPayloadBytes)
                throw new ArgumentException(
                    $"Enhanced RPC parameter data ({paramBytes} bytes) exceeds " +
                    $"the {RpcLimits.MaxPayloadBytes}-byte payload limit.",
                    nameof(args));

            int totalLen = RpcLimits.EnhancedRequestHeaderSize + paramBytes;
            var buf = new byte[totalLen];

            int offset = 0;

            // [0..3] method_id (LE u32)
            RpcSerializer.WriteU32LE(buf, offset, methodId);     offset += 4;

            // [4..11] sender_id (LE u64)
            RpcSerializer.WriteU64LE(buf, offset, senderId);     offset += 8;

            // [12..15] request_id (LE u32)
            RpcSerializer.WriteU32LE(buf, offset, requestId);    offset += 4;

            // [16..23] object_id (LE u64)
            RpcSerializer.WriteU64LE(buf, offset, objectId);     offset += 8;

            // [24] target (u8)
            buf[offset++] = (byte)target;

            // [25] rpc_flags (u8) — reserved
            buf[offset++] = 0x00;

            // [26] param_count (u8)
            buf[offset++] = (byte)args.Length;

            // [27…] typed parameter stream
            foreach (var arg in args)
                offset += RpcSerializer.WriteParam(arg, buf, offset);

            return buf;
        }
    }
}
