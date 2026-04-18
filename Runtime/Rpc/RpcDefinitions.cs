// RTMPE SDK — Runtime/Rpc/RpcDefinitions.cs
//
// Wire-protocol constants for the RPC subsystem.
//
// Wire-protocol constants for RPC messages are defined here so both
// RpcPacketBuilder and RpcPacketParser share a single source of truth.
// Method IDs are permanent — never reuse a retired ID.

namespace RTMPE.Rpc
{
    /// <summary>
    /// RPC method identifiers.
    /// Values are permanent: never reuse a retired method ID.
    /// </summary>
    public static class RpcMethodId
    {
        /// <summary>Simple echo; used for latency measurement.</summary>
        public const uint Ping               = 100;

        /// <summary>Request ownership transfer (server-authoritative).</summary>
        public const uint TransferOwnership  = 200;

        /// <summary>Request damage application (ServerRpc).</summary>
        public const uint RequestDamage      = 300;

        /// <summary>Broadcast damage result to all room clients (ClientRpc).</summary>
        public const uint ApplyDamage        = 301;

        /// <summary>Host-only game state change (ServerRpc).</summary>
        public const uint GameStateChange    = 400;

        /// <summary>Broadcast current game state to all clients (ClientRpc).</summary>
        public const uint SyncGameState      = 401;
    }

    /// <summary>
    /// RPC response error codes.
    /// </summary>
    public enum RpcErrorCode : ushort
    {
        /// <summary>No error; handler succeeded.</summary>
        OK              = 0,

        /// <summary>Sender failed authorization (not owner, not in room).</summary>
        Unauthorized    = 1,

        /// <summary>Method ID not registered on the server.</summary>
        UnknownMethod   = 2,

        /// <summary>Handler returned an error during execution.</summary>
        HandlerError    = 3,

        /// <summary>Payload exceeds <see cref="RpcLimits.MaxPayloadBytes"/>.</summary>
        OversizedPayload = 4,
    }

    /// <summary>
    /// RPC system limits and wire-format sizes.
    /// </summary>
    public static class RpcLimits
    {
        /// <summary>Maximum RPC payload size in bytes (4 KiB).</summary>
        public const int MaxPayloadBytes = 4096;

        /// <summary>
        /// RPC request header size (bytes) — excludes the 13-byte standard packet header.
        /// Layout: method_id(4) + sender_id(8) + request_id(4) + payload_len(2) = 18.
        /// </summary>
        public const int RequestHeaderSize = 18;

        /// <summary>
        /// RPC response header size (bytes) — excludes the 13-byte standard packet header.
        /// Layout: request_id(4) + method_id(4) + sender_id(8) + success(1) + error_code(2) + payload_len(2) = 21.
        /// </summary>
        public const int ResponseHeaderSize = 21;
    }
}
