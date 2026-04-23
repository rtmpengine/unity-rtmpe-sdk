// RTMPE SDK — Runtime/Rpc/RpcTarget.cs
//
// Determines which clients receive an Enhanced RPC call.
// Used by the [RtmpeRpc] attribute and EnhancedRpcPacketBuilder.
//
// Phase 1 targets only.  AllBuffered and OthersBuffered require Buffered Events
// (Phase 2.2) and MasterClient requires Master Client infrastructure (Phase 2.1)
// — those are intentionally absent here.

namespace RTMPE.Rpc
{
    /// <summary>
    /// Specifies the delivery audience for an <see cref="RtmpeRpcAttribute"/>-decorated method.
    /// Values are serialised as a single byte in the Enhanced RPC wire format.
    /// </summary>
    public enum RpcTarget : byte
    {
        /// <summary>
        /// All clients in the room receive the RPC, including the sender.
        /// </summary>
        All    = 0x00,

        /// <summary>
        /// All clients in the room receive the RPC, excluding the sender.
        /// </summary>
        Others = 0x01,

        /// <summary>
        /// The RPC is delivered to the server only (ServerRpc pattern).
        /// The server validates, then may re-broadcast with <see cref="All"/>.
        /// </summary>
        Server = 0x02,

        /// <summary>
        /// All clients in the room receive the RPC AND the event is stored in the
        /// server-side buffer for late joiners.  Late joiners receive all buffered
        /// events in order via <see cref="RTMPE.Core.PacketType.RpcBufferReplay"/> (0x52)
        /// immediately after joining the room.
        /// Requires Buffered Events infrastructure (Phase 5).
        /// </summary>
        AllBuffered = 0x03,
    }
}
