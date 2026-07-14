// RTMPE SDK — Runtime/Rpc/RpcDeserializationException.cs
//
// Thrown by RpcSerializer when an INetworkSerializable parameter fails to
// deserialise.  An earlier design returned a partial-state instance with
// only a Debug.LogWarning, leaving the dispatcher to decide whether to
// invoke the user method with a corrupt argument.  That leaks the policy
// decision into game code; throwing forces the caller (RPC dispatcher) to
// pick a single explicit policy.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Raised when an inbound RPC parameter cannot be safely deserialised —
    /// either the implementing type's <c>NetworkDeserialize</c> threw, or
    /// the wire payload was truncated mid-read.  The RPC dispatcher catches
    /// this exception and drops the entire RPC call rather than invoking the
    /// receiver with a half-populated argument.
    /// </summary>
    public sealed class RpcDeserializationException : Exception
    {
        /// <summary>Wire-supplied type name reported by the inbound packet.</summary>
        public string TypeName { get; }

        /// <inheritdoc/>
        public RpcDeserializationException(string typeName, string message)
            : base(message)
        {
            TypeName = typeName ?? string.Empty;
        }

        /// <inheritdoc/>
        public RpcDeserializationException(string typeName, string message, Exception inner)
            : base(message, inner)
        {
            TypeName = typeName ?? string.Empty;
        }
    }
}
