// RTMPE SDK — Runtime/Rpc/IRtmpeReader.cs
//
// Narrow read API exposed to INetworkSerializable implementations.
// Symmetric with IRtmpeWriter — every write method has a corresponding read.
//
// All multi-byte primitives are little-endian.  On truncated input the reader
// returns a default-valued primitive AND raises a sticky failure flag so the
// outer dispatch (RpcSerializer.ReadParam) can short-circuit and discard the
// rest of the parameter stream cleanly.

using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Primitive read surface passed to
    /// <see cref="INetworkSerializable.NetworkDeserialize"/>.
    /// All multi-byte values are decoded little-endian.
    ///
   /// <para>Failure handling: if the underlying buffer is truncated mid-read
    /// the reader returns the natural default for the requested type
    /// (<c>0</c>, <c>false</c>, <see cref="string.Empty"/>, …) and sets the
    /// <see cref="HasFailed"/> flag.  Implementers may continue calling read
    /// methods — they will all return defaults — but the outer dispatch
    /// guarantees the resulting object is treated as a deserialization
    /// failure.</para>
    /// </summary>
    public interface IRtmpeReader
    {
        /// <summary>
        /// True after any read failed due to buffer truncation.  Once raised
        /// the reader stays failed for the remainder of the current
        /// deserialization; subsequent reads return their type's default
        /// without advancing the cursor.
        /// </summary>
        bool HasFailed { get; }

        /// <summary>Read a 32-bit signed integer (4 bytes LE).</summary>
        int ReadInt32();

        /// <summary>Read a 32-bit IEEE-754 float (4 bytes LE).</summary>
        float ReadFloat();

        /// <summary>Read a single 0x00 / 0x01 byte as a bool.</summary>
        bool ReadBool();

        /// <summary>Read a 64-bit unsigned integer (8 bytes LE).</summary>
        ulong ReadUInt64();

        /// <summary>Read a 16-bit unsigned integer (2 bytes LE).</summary>
        ushort ReadUInt16();

        /// <summary>Read a single byte.</summary>
        byte ReadByte();

        /// <summary>
        /// Read a UTF-8 string framed as <c>[len:2 LE ushort][bytes…]</c>.
        /// Returns <see cref="string.Empty"/> on truncation.
        /// </summary>
        string ReadString();

        /// <summary>
        /// Read a length-prefixed byte buffer as <c>[len:2 LE ushort][bytes…]</c>.
        /// Returns <see cref="System.Array.Empty{T}()"/> on truncation.
        /// </summary>
        byte[] ReadBytes();

        /// <summary>Read a <see cref="Vector3"/> (12 bytes LE: x, y, z).</summary>
        Vector3 ReadVector3();

        /// <summary>Read a <see cref="Quaternion"/> (16 bytes LE: x, y, z, w).</summary>
        Quaternion ReadQuaternion();

        /// <summary>Read a <see cref="Color"/> (16 bytes LE: r, g, b, a).</summary>
        Color ReadColor();
    }
}
