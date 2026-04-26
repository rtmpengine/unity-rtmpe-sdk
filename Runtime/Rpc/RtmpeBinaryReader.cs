// RTMPE SDK — Runtime/Rpc/RtmpeBinaryReader.cs
//
// Concrete IRtmpeReader that reads from a byte[] at a caller-controlled offset.
// Reuses RpcSerializer's LE primitive helpers for bit-identical decoding.
//
// Truncation policy:
//   • Each read first checks whether enough bytes remain.
//   • On under-flow the reader sets HasFailed = true and returns the natural
//     default for the requested type without advancing the cursor.
//   • Subsequent reads short-circuit on HasFailed so the implementer's loop
//     terminates cleanly and the outer dispatch (RpcSerializer.ReadParam) can
//     abandon the parameter.
//
// All multi-byte values are little-endian, matching the rest of the wire format.

using System;
using System.Text;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Internal concrete reader used by
    /// <see cref="RpcSerializer"/> to decode
    /// <see cref="INetworkSerializable"/> payloads from inbound RPC packets.
    /// </summary>
    internal sealed class RtmpeBinaryReader : IRtmpeReader
    {
        private static readonly UTF8Encoding Utf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

        private readonly byte[] _data;
        private          int    _pos;
        private readonly int    _end;
        private          bool   _failed;

        public RtmpeBinaryReader(byte[] data, int startOffset, int byteCount)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
            if ((uint)startOffset > (uint)data.Length)
                throw new ArgumentOutOfRangeException(nameof(startOffset));
            if (byteCount < 0 || startOffset + byteCount > data.Length)
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            _pos    = startOffset;
            _end    = startOffset + byteCount;
            _failed = false;
        }

        public bool HasFailed => _failed;

        /// <summary>Current read offset within the backing buffer.</summary>
        public int Position => _pos;

        private bool Require(int n)
        {
            if (_failed) return false;
            if (_end - _pos < n)
            {
                _failed = true;
                return false;
            }
            return true;
        }

        public byte ReadByte()
        {
            if (!Require(1)) return 0;
            return _data[_pos++];
        }

        public bool ReadBool()
        {
            if (!Require(1)) return false;
            return _data[_pos++] != 0;
        }

        public ushort ReadUInt16()
        {
            if (!Require(2)) return 0;
            ushort v = RpcSerializer.ReadU16LE(_data, _pos);
            _pos += 2;
            return v;
        }

        public int ReadInt32()
        {
            if (!Require(4)) return 0;
            int v = RpcSerializer.ReadI32LE(_data, _pos);
            _pos += 4;
            return v;
        }

        public float ReadFloat()
        {
            if (!Require(4)) return 0f;
            float v = RpcSerializer.ReadF32LE(_data, _pos);
            _pos += 4;
            return v;
        }

        public ulong ReadUInt64()
        {
            if (!Require(8)) return 0UL;
            ulong v = RpcSerializer.ReadU64LE(_data, _pos);
            _pos += 8;
            return v;
        }

        public string ReadString()
        {
            if (!Require(2)) return string.Empty;
            ushort len = RpcSerializer.ReadU16LE(_data, _pos);
            _pos += 2;
            if (len == 0) return string.Empty;
            if (!Require(len)) return string.Empty;
            string s = Utf8.GetString(_data, _pos, len);
            _pos += len;
            return s;
        }

        public byte[] ReadBytes()
        {
            if (!Require(2)) return Array.Empty<byte>();
            ushort len = RpcSerializer.ReadU16LE(_data, _pos);
            _pos += 2;
            if (len == 0) return Array.Empty<byte>();
            if (!Require(len)) return Array.Empty<byte>();
            var copy = new byte[len];
            Buffer.BlockCopy(_data, _pos, copy, 0, len);
            _pos += len;
            return copy;
        }

        public Vector3 ReadVector3()
        {
            if (!Require(12)) return Vector3.zero;
            float x = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float y = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float z = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            return new Vector3(x, y, z);
        }

        public Quaternion ReadQuaternion()
        {
            if (!Require(16)) return Quaternion.identity;
            float x = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float y = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float z = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float w = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            return new Quaternion(x, y, z, w);
        }

        public Color ReadColor()
        {
            if (!Require(16)) return default;
            float r = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float g = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float b = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            float a = RpcSerializer.ReadF32LE(_data, _pos); _pos += 4;
            return new Color(r, g, b, a);
        }
    }
}
