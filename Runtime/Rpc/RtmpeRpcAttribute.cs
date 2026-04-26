// RTMPE SDK — Runtime/Rpc/RtmpeRpcAttribute.cs
//
// Marks a method on a NetworkBehaviour as callable over the network.
// The RpcRegistry discovers [RtmpeRpc] methods via reflection on first access
// and assigns each a stable FNV-1a method ID derived from "TypeName.MethodName".
//
// Rules for [RtmpeRpc] methods:
//   • Must be declared on a class that inherits NetworkBehaviour.
//   • Must be public instance methods (not static, not abstract).
//   • Parameters must be types supported by RpcSerializer:
//       int, float, bool, string, byte[], ulong, Vector3, Color, Quaternion,
//       or any user-defined type implementing INetworkSerializable
//       (recommended: small structs with a public parameterless constructor).
//   • The FNV-1a hash of "DeclaredTypeName.MethodName" must not collide with
//     any reserved manual method ID listed in RpcMethodId (100, 200, 300, 301,
//     400, 401).  A collision causes a startup error via RpcRegistry.Validate().

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Marks a <c>NetworkBehaviour</c> method as a networked RPC endpoint.
    /// Invoke via <c>NetworkBehaviour.RPC("MethodName", args…)</c> from the owner client.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RtmpeRpcAttribute : Attribute
    {
        /// <summary>
        /// Which clients receive the RPC.
        /// Defaults to <see cref="RpcTarget.All"/> (including sender).
        /// </summary>
        public RpcTarget Target { get; }

        /// <param name="target">Delivery audience. Defaults to <see cref="RpcTarget.All"/>.</param>
        public RtmpeRpcAttribute(RpcTarget target = RpcTarget.All)
        {
            Target = target;
        }
    }
}
