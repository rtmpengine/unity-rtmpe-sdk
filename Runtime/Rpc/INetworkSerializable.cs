// RTMPE SDK — Runtime/Rpc/INetworkSerializable.cs
//
// Extension point that lets game code pass user-defined types (typically
// small structs) as RPC parameters.  Without this interface RPCs are limited
// to the nine built-in primitive/Unity types known to RpcSerializer.
//
// Wire integration:
//   • RpcSerializer adds type tag 0x0A "INetworkSerializable".
//   • Encoded record: [tag:1][type_name_len:2 LE][type_name UTF-8][payload …]
//     The type name is the assembly-qualified-free FullName (Namespace.Type).
//     This lets the receiver instantiate the correct concrete type without
//     a separate registry hand-shake — at the cost of ~name-length bytes per
//     parameter.  Apps that send the same type at >1 Hz can pre-register via
//     RpcTypeRegistry to swap the name for a 4-byte hash (Phase 2 — not yet
//     wired through; the on-the-wire fallback is always available).
//
// Failure modes (all surfaced as warnings, never thrown across the read loop):
//   • Unknown type name → null parameter is returned; caller handles a null
//     argument the same way it would handle a malformed packet.
//   • Constructor throws → null parameter is returned, warning logged.
//   • Deserialize() throws → null parameter is returned, warning logged.
//
// Why an interface and not [Serializable]:
//   • Explicit boundary: callers cannot accidentally serialize huge graphs.
//   • Versioning: authors choose how to handle field additions/removals.
//   • Determinism: [Serializable] varies across .NET runtimes; this is stable.

namespace RTMPE.Rpc
{
    /// <summary>
    /// Implement this interface on a struct or class to make it eligible as an
    /// RPC parameter.  The two methods are called by
    /// <see cref="RpcSerializer"/> during outbound and inbound RPC dispatch
    /// respectively.
    ///
    /// <para><b>Author contract:</b> the byte sequence written by
    /// <see cref="NetworkSerialize"/> on the sender MUST be readable in the
    /// same order by <see cref="NetworkDeserialize"/> on the receiver, and
    /// the implementation MUST tolerate a deserialize call on a freshly
    /// constructed instance (no field is pre-populated by the SDK).</para>
    ///
    /// <para><b>Default constructor required.</b>  The deserializer
    /// instantiates the concrete type via <see cref="System.Activator"/>; if
    /// the type cannot be instantiated parameterlessly the parameter arrives
    /// as <see langword="null"/> on the receiving side and a warning is
    /// logged.  Structs always satisfy this; classes must declare a public
    /// no-arg constructor (or rely on an implicit one).</para>
    ///
    /// <example>
    /// <code>
    /// public struct PlayerScore : INetworkSerializable
    /// {
    ///     public int   Score;
    ///     public float Accuracy;
    ///     public string PlayerName;
    ///
    ///     public void NetworkSerialize(IRtmpeWriter writer)
    ///     {
    ///         writer.WriteInt32(Score);
    ///         writer.WriteFloat(Accuracy);
    ///         writer.WriteString(PlayerName);
    ///     }
    ///
    ///     public void NetworkDeserialize(IRtmpeReader reader)
    ///     {
    ///         Score      = reader.ReadInt32();
    ///         Accuracy   = reader.ReadFloat();
    ///         PlayerName = reader.ReadString();
    ///     }
    /// }
    /// </code>
    /// </example>
    /// </summary>
    public interface INetworkSerializable
    {
        /// <summary>
        /// Write the value of this instance to <paramref name="writer"/>.
        /// Called once per outgoing RPC parameter on the sending side.
        /// </summary>
        void NetworkSerialize(IRtmpeWriter writer);

        /// <summary>
        /// Read the value of this instance from <paramref name="reader"/>.
        /// Called once per incoming RPC parameter on the receiving side
        /// against a freshly constructed instance.
        /// </summary>
        void NetworkDeserialize(IRtmpeReader reader);
    }
}
