// RTMPE SDK — Runtime/Rpc/RtmpeRpcSerializableAttribute.cs
//
// Trust model:
//  The Enhanced RPC wire format embeds a UTF-8 type name for every
//  INetworkSerializable parameter and the receiver instantiates the
//  resolved Type via Activator.CreateInstance.  That instantiation is a
//  reflection-driven gadget primitive: any process-loaded type that
//  satisfies the discovery filter (public, parameterless ctor,
//  INetworkSerializable) becomes reachable from a hostile peer or relay.
//
//  To collapse that surface to a known-good set we require explicit
//  author opt-in.  A type only becomes resolvable when one of the
//  following is true:
//    • it carries [RtmpeRpcSerializable], OR
//    • the application called RpcTypeRegistry.Register<T>() at startup.
//
//  Untagged / unregistered types are silently invisible to the inbound
//  resolver — even when RpcTypeRegistry.AllowAppDomainScan is enabled
//  (the scan only picks up attributed types).  This is the intended
//  defence; do not relax it without a corresponding threat model update.

using System;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Marks an <see cref="INetworkSerializable"/> type as eligible to be
    /// instantiated from an inbound RPC parameter stream.  Apply to every
    /// custom RPC payload struct/class the application wants to round-trip
    /// over the wire.
    ///
   /// <para>Without this attribute (and absent an explicit
    /// <see cref="RpcTypeRegistry.Register{T}"/> call), the type is NOT
    /// resolvable by the inbound parser and any RPC carrying its type name
    /// is dropped with a warning.</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct,
                    AllowMultiple = false, Inherited = false)]
    public sealed class RtmpeRpcSerializableAttribute : Attribute
    {
    }
}
