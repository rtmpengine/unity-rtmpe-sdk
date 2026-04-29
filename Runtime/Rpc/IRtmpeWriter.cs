// RTMPE SDK — Runtime/Rpc/IRtmpeWriter.cs
//
// Narrow write API exposed to INetworkSerializable implementations.
//
// Why a dedicated interface (and not BinaryWriter):
//  • BinaryWriter writes strings with a 7-bit-encoded length prefix that is
//    incompatible with the rest of the RTMPE wire format (Go server expects
//    2-byte LE ushort length).  Forcing implementers to use BinaryWriter
//    would either invite that mismatch or require a manual byte-by-byte
//    encoding everywhere.
//  • Allows the SDK to swap the backing store (byte[] / Span<byte> /
//    pre-rented pool buffer) without breaking author-written serializers.
//
// All multi-byte primitives are little-endian to match the rest of RpcSerializer.

using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Primitive write surface passed to
    /// <see cref="INetworkSerializable.NetworkSerialize"/>.
    /// All multi-byte values are encoded little-endian.
    /// </summary>
    public interface IRtmpeWriter
    {
        /// <summary>Write a 32-bit signed integer (4 bytes LE).</summary>
        void WriteInt32(int value);

        /// <summary>Write a 32-bit IEEE-754 float (4 bytes LE).</summary>
        void WriteFloat(float value);

        /// <summary>Write a single byte 0x00 / 0x01.</summary>
        void WriteBool(bool value);

        /// <summary>Write a 64-bit unsigned integer (8 bytes LE).</summary>
        void WriteUInt64(ulong value);

        /// <summary>Write a 16-bit unsigned integer (2 bytes LE).</summary>
        void WriteUInt16(ushort value);

        /// <summary>Write a single byte verbatim.</summary>
        void WriteByte(byte value);

        /// <summary>
        /// Write a UTF-8 string framed as <c>[len:2 LE ushort][bytes…]</c>.
        /// Null is normalised to <see cref="string.Empty"/>.
        /// Throws <see cref="System.ArgumentException"/> when the encoded
        /// length exceeds <see cref="ushort.MaxValue"/>.
        /// </summary>
        void WriteString(string value);

        /// <summary>
        /// Write a length-prefixed byte buffer as <c>[len:2 LE ushort][bytes…]</c>.
        /// Null is treated as an empty buffer.
        /// Throws <see cref="System.ArgumentException"/> when
        /// <paramref name="value"/>.Length exceeds <see cref="ushort.MaxValue"/>.
        /// </summary>
        void WriteBytes(byte[] value);

        /// <summary>Write a <see cref="Vector3"/> (12 bytes LE: x, y, z).</summary>
        void WriteVector3(Vector3 value);

        /// <summary>Write a <see cref="Quaternion"/> (16 bytes LE: x, y, z, w).</summary>
        void WriteQuaternion(Quaternion value);

        /// <summary>Write a <see cref="Color"/> (16 bytes LE: r, g, b, a).</summary>
        void WriteColor(Color value);
    }
}
