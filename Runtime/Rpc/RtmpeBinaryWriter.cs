// RTMPE SDK — Runtime/Rpc/RtmpeBinaryWriter.cs
//
// Concrete IRtmpeWriter that appends to a byte[] at a caller-controlled offset.
// Reuses RpcSerializer's LE primitive helpers to guarantee bit-identical
// encoding with the rest of the RPC pipeline.
//
// Usage pattern (from RpcSerializer):
//   var writer = new RtmpeBinaryWriter(buf, offset);
//   serializable.NetworkSerialize(writer);
//   bytesWritten = writer.Position - offset;
//
// The writer assumes buf is already large enough for the payload.  RpcSerializer
// computes the required size via INetworkSerializable.NetworkSerialize against
// a measure-only writer in MeasureParam, then allocates exactly that much.

using System;
using System.Text;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Internal concrete writer used by
    /// <see cref="RpcSerializer"/> to materialise
    /// <see cref="INetworkSerializable"/> payloads into the outbound RPC buffer.
    /// </summary>
    internal sealed class RtmpeBinaryWriter : IRtmpeWriter
    {
        private static readonly UTF8Encoding Utf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private readonly byte[] _buf;
        private          int    _pos;

        public RtmpeBinaryWriter(byte[] buf, int startOffset)
        {
            _buf = buf ?? throw new ArgumentNullException(nameof(buf));
            if ((uint)startOffset > (uint)buf.Length)
                throw new ArgumentOutOfRangeException(nameof(startOffset));
            _pos = startOffset;
        }

        /// <summary>Current write offset within the backing buffer.</summary>
        public int Position => _pos;

        public void WriteByte(byte value)
        {
            _buf[_pos++] = value;
        }

        public void WriteBool(bool value)
        {
            _buf[_pos++] = value ? (byte)1 : (byte)0;
        }

        public void WriteUInt16(ushort value)
        {
            RpcSerializer.WriteU16LE(_buf, _pos, value);
            _pos += 2;
        }

        public void WriteInt32(int value)
        {
            RpcSerializer.WriteI32LE(_buf, _pos, value);
            _pos += 4;
        }

        public void WriteFloat(float value)
        {
            RpcSerializer.WriteF32LE(_buf, _pos, value);
            _pos += 4;
        }

        public void WriteUInt64(ulong value)
        {
            RpcSerializer.WriteU64LE(_buf, _pos, value);
            _pos += 8;
        }

        public void WriteString(string value)
        {
            string s = value ?? string.Empty;
            byte[] bytes = Utf8.GetBytes(s);
            if (bytes.Length > ushort.MaxValue)
                throw new ArgumentException(
                    $"INetworkSerializable string field encodes to {bytes.Length} bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));

            RpcSerializer.WriteU16LE(_buf, _pos, (ushort)bytes.Length);
            _pos += 2;
            if (bytes.Length > 0)
            {
                Buffer.BlockCopy(bytes, 0, _buf, _pos, bytes.Length);
                _pos += bytes.Length;
            }
        }

        public void WriteBytes(byte[] value)
        {
            int len = value?.Length ?? 0;
            if (len > ushort.MaxValue)
                throw new ArgumentException(
                    $"INetworkSerializable byte[] field is {len} bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));

            RpcSerializer.WriteU16LE(_buf, _pos, (ushort)len);
            _pos += 2;
            if (len > 0)
            {
                Buffer.BlockCopy(value, 0, _buf, _pos, len);
                _pos += len;
            }
        }

        public void WriteVector3(Vector3 value)
        {
            RpcSerializer.WriteF32LE(_buf, _pos, value.x); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.y); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.z); _pos += 4;
        }

        public void WriteQuaternion(Quaternion value)
        {
            RpcSerializer.WriteF32LE(_buf, _pos, value.x); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.y); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.z); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.w); _pos += 4;
        }

        public void WriteColor(Color value)
        {
            RpcSerializer.WriteF32LE(_buf, _pos, value.r); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.g); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.b); _pos += 4;
            RpcSerializer.WriteF32LE(_buf, _pos, value.a); _pos += 4;
        }
    }

    /// <summary>
    /// Counts the bytes that would be written by an
    /// <see cref="INetworkSerializable.NetworkSerialize"/> call without
    /// touching any backing buffer.  Used by
    /// <see cref="RpcSerializer.MeasureParam"/> to size the outbound buffer
    /// before calling <see cref="RtmpeBinaryWriter"/>.
    /// </summary>
    internal sealed class RtmpeBinaryMeasurer : IRtmpeWriter
    {
        private static readonly UTF8Encoding Utf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        public int Bytes { get; private set; }

        public void WriteByte(byte value)              => Bytes += 1;
        public void WriteBool(bool value)              => Bytes += 1;
        public void WriteUInt16(ushort value)          => Bytes += 2;
        public void WriteInt32(int value)              => Bytes += 4;
        public void WriteFloat(float value)            => Bytes += 4;
        public void WriteUInt64(ulong value)           => Bytes += 8;
        public void WriteVector3(Vector3 value)        => Bytes += 12;
        public void WriteQuaternion(Quaternion value)  => Bytes += 16;
        public void WriteColor(Color value)            => Bytes += 16;

        public void WriteString(string value)
        {
            int n = Utf8.GetByteCount(value ?? string.Empty);
            if (n > ushort.MaxValue)
                throw new ArgumentException(
                    $"INetworkSerializable string field encodes to {n} bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));
            Bytes += 2 + n;
        }

        public void WriteBytes(byte[] value)
        {
            int n = value?.Length ?? 0;
            if (n > ushort.MaxValue)
                throw new ArgumentException(
                    $"INetworkSerializable byte[] field is {n} bytes — " +
                    $"exceeds {ushort.MaxValue}-byte wire limit.",
                    nameof(value));
            Bytes += 2 + n;
        }
    }
}
