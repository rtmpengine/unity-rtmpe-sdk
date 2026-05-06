// RTMPE SDK — Runtime/Rpc/RpcSerializer.cs
//
// Typed parameter serialization for Enhanced RPC packets.
// All multi-byte values are little-endian.
//
// Wire format per parameter:
//  [type_id : 1 u8]
//  [value   : N bytes]  — size and layout depend on type_id (see table below)
//
// Type registry:
//  0x01  int32      4 bytes  signed LE
//  0x02  float32    4 bytes  IEEE-754 LE
//  0x03  bool       1 byte   1=true, 0=false
//  0x04  string     2-byte LE len + N UTF-8 bytes (max 65535 bytes encoded)
//  0x05  byte[]     2-byte LE len + N bytes       (max 65535 bytes)
//  0x06  Vector3   12 bytes  x:f32 y:f32 z:f32 LE
//  0x07  Color     16 bytes  r:f32 g:f32 b:f32 a:f32 LE
//  0x08  ulong      8 bytes  unsigned LE
//  0x09  Quaternion 16 bytes  x:f32 y:f32 z:f32 w:f32 LE
//  0x0A  INetworkSerializable
//                   [type_name_len:2 LE][type_name UTF-8][payload_len:2 LE][payload bytes]
//                   type_name is wire-supplied (hostile) and is resolved
//                   ONLY against the explicit RpcTypeRegistry — types
//                   without [RtmpeRpcSerializable] or an explicit
//                   Register<T>() are NOT instantiable, even if they
//                   implement INetworkSerializable elsewhere in the
//                   AppDomain.  Unresolved type names cause the
//                   parameter to be surfaced as null with a warning and
//                   the parser advances past payload_len so subsequent
//                   parameters still align.  See RpcTypeRegistry.cs
//                   for the trust model.

using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace RTMPE.Rpc
{
    /// <summary>Type discriminator constants for the Enhanced RPC wire format.</summary>
    internal static class RpcTypeId
    {
        public const byte Int32                = 0x01;
        public const byte Float32              = 0x02;
        public const byte Bool                 = 0x03;
        public const byte String               = 0x04;
        public const byte Bytes                = 0x05;
        public const byte Vector3              = 0x06;
        public const byte Color                = 0x07;
        public const byte UInt64               = 0x08;
        public const byte Quaternion           = 0x09;
        public const byte INetworkSerializable = 0x0A;
    }

    /// <summary>
    /// Serializes and deserializes typed RPC parameters.
    /// All write operations operate on a pre-allocated buffer at a given offset
    /// to avoid per-call heap allocations on the hot send path.
    /// </summary>
    public static class RpcSerializer
    {
        // Allocation-free float ↔ int32 reinterpretation via StructLayout union.
        // No unsafe code required; works on all Unity-supported platforms.
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatInt32Union
        {
            [FieldOffset(0)] public float Float;
            [FieldOffset(0)] public int   Int;
        }

        // Strict UTF-8 codec.  Decoding paths route every malformed sequence
        // into a DecoderFallbackException, which the ReadParam call sites
        // translate into the standard `offset = -1; return null` failure
        // contract.  Encoding paths are unaffected — every valid C# string
        // round-trips through the strict encoder identically to the lax
        // form, so production callers see no behavioural difference for
        // well-formed inputs.
        private static readonly UTF8Encoding Utf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        // ── Measurement ───────────────────────────────────────────────────────

        /// <summary>
        /// Returns the number of bytes needed to encode <paramref name="value"/>,
        /// including the 1-byte type_id prefix.
        /// </summary>
        /// <exception cref="ArgumentException">Thrown for unsupported parameter types.</exception>
        public static int MeasureParam(object value)
        {
            if (value is int)        return 1 + 4;
            if (value is float)      return 1 + 4;
            if (value is bool)       return 1 + 1;
            if (value is ulong)      return 1 + 8;
            if (value is string s)   return 1 + 2 + Utf8.GetByteCount(s);
            if (value is byte[] b)   return 1 + 2 + b.Length;
            if (value is Vector3)    return 1 + 12;
            if (value is Color)      return 1 + 16;
            if (value is Quaternion) return 1 + 16;
            if (value is INetworkSerializable ns)
            {
                // tag(1) + type_name_len(2) + type_name(N UTF-8) + payload_len(2) + payload(M)
                string typeName = ns.GetType().FullName ?? string.Empty;
                int typeNameLen = Utf8.GetByteCount(typeName);
                if (typeNameLen > ushort.MaxValue)
                    throw new ArgumentException(
                        $"INetworkSerializable type name '{typeName}' is {typeNameLen} bytes — " +
                        $"exceeds {ushort.MaxValue}-byte wire limit.",
                        nameof(value));

                var measurer = new RtmpeBinaryMeasurer();
                ns.NetworkSerialize(measurer);
                int payloadLen = measurer.Bytes;
                if (payloadLen > ushort.MaxValue)
                    throw new ArgumentException(
                        $"INetworkSerializable '{typeName}' serializes to {payloadLen} bytes — " +
                        $"exceeds {ushort.MaxValue}-byte wire limit.",
                        nameof(value));

                return 1 + 2 + typeNameLen + 2 + payloadLen;
            }
            var type = value?.GetType();
            throw new ArgumentException(
                $"Unsupported RPC parameter type: {(type != null ? type.Name : "null")}",
                nameof(value));
        }

        // ── Write ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Encode a single typed parameter into <paramref name="buf"/> starting
        /// at <paramref name="offset"/>.
        /// Returns the total bytes written (type_id byte + value bytes).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown for unsupported types or values that exceed the wire limits
        /// (string/byte[] &gt; 65535 bytes).
        /// </exception>
        public static int WriteParam(object value, byte[] buf, int offset)
        {
            if (value is int i)
            {
                buf[offset++] = RpcTypeId.Int32;
                WriteI32LE(buf, offset, i);
                return 1 + 4;
            }
            if (value is float f)
            {
                buf[offset++] = RpcTypeId.Float32;
                WriteF32LE(buf, offset, f);
                return 1 + 4;
            }
            if (value is bool bl)
            {
                buf[offset++] = RpcTypeId.Bool;
                buf[offset]   = bl ? (byte)1 : (byte)0;
                return 1 + 1;
            }
            if (value is ulong ul)
            {
                buf[offset++] = RpcTypeId.UInt64;
                WriteU64LE(buf, offset, ul);
                return 1 + 8;
            }
            if (value is string str)
            {
                // Pre-check the encoded byte count BEFORE materialising the
                // UTF-8 byte array.  GetByteCount is non-allocating per BCL
                // contract, so a 100 MiB string is rejected without paying
                // 100 MiB of heap allocation.  Same alloc-amplification
                // surface closed for ApiKeyCipher in M19-CRYPTO-01.
                int strByteCount = Utf8.GetByteCount(str);
                if (strByteCount > ushort.MaxValue)
                    throw new ArgumentException(
                        $"String param encodes to {strByteCount} bytes — exceeds 65535-byte wire limit.",
                        nameof(value));
                byte[] strBytes = Utf8.GetBytes(str);
                buf[offset++] = RpcTypeId.String;
                WriteU16LE(buf, offset, (ushort)strBytes.Length); offset += 2;
                Buffer.BlockCopy(strBytes, 0, buf, offset, strBytes.Length);
                return 1 + 2 + strBytes.Length;
            }
            if (value is byte[] bytes)
            {
                if (bytes.Length > ushort.MaxValue)
                    throw new ArgumentException(
                        $"byte[] param is {bytes.Length} bytes — exceeds 65535-byte wire limit.",
                        nameof(value));
                buf[offset++] = RpcTypeId.Bytes;
                WriteU16LE(buf, offset, (ushort)bytes.Length); offset += 2;
                Buffer.BlockCopy(bytes, 0, buf, offset, bytes.Length);
                return 1 + 2 + bytes.Length;
            }
            if (value is Vector3 v3)
            {
                buf[offset++] = RpcTypeId.Vector3;
                WriteF32LE(buf, offset, v3.x); offset += 4;
                WriteF32LE(buf, offset, v3.y); offset += 4;
                WriteF32LE(buf, offset, v3.z);
                return 1 + 12;
            }
            if (value is Color c)
            {
                buf[offset++] = RpcTypeId.Color;
                WriteF32LE(buf, offset, c.r); offset += 4;
                WriteF32LE(buf, offset, c.g); offset += 4;
                WriteF32LE(buf, offset, c.b); offset += 4;
                WriteF32LE(buf, offset, c.a);
                return 1 + 16;
            }
            if (value is Quaternion q)
            {
                buf[offset++] = RpcTypeId.Quaternion;
                WriteF32LE(buf, offset, q.x); offset += 4;
                WriteF32LE(buf, offset, q.y); offset += 4;
                WriteF32LE(buf, offset, q.z); offset += 4;
                WriteF32LE(buf, offset, q.w);
                return 1 + 16;
            }
            if (value is INetworkSerializable ns)
            {
                // [tag:1][type_name_len:2 LE][type_name UTF-8][payload_len:2 LE][payload]
                int startOffset = offset;
                buf[offset++] = RpcTypeId.INetworkSerializable;

                string typeName = ns.GetType().FullName ?? string.Empty;
                // Pre-check before allocating the encoded byte array — same
                // alloc-amplification discipline as the String branch above.
                int nameByteCount = Utf8.GetByteCount(typeName);
                if (nameByteCount > ushort.MaxValue)
                    throw new ArgumentException(
                        $"INetworkSerializable type name '{typeName}' is {nameByteCount} bytes — " +
                        $"exceeds {ushort.MaxValue}-byte wire limit.",
                        nameof(value));
                byte[] nameBytes = Utf8.GetBytes(typeName);

                WriteU16LE(buf, offset, (ushort)nameBytes.Length); offset += 2;
                if (nameBytes.Length > 0)
                {
                    Buffer.BlockCopy(nameBytes, 0, buf, offset, nameBytes.Length);
                    offset += nameBytes.Length;
                }

                // Reserve 2 bytes for payload_len; back-patched after writing.
                int payloadLenOffset = offset;
                offset += 2;

                int payloadStart = offset;
                var writer = new RtmpeBinaryWriter(buf, offset);
                ns.NetworkSerialize(writer);
                int payloadLen = writer.Position - payloadStart;
                if (payloadLen > ushort.MaxValue)
                    throw new ArgumentException(
                        $"INetworkSerializable '{typeName}' serializes to {payloadLen} bytes — " +
                        $"exceeds {ushort.MaxValue}-byte wire limit.",
                        nameof(value));

                WriteU16LE(buf, payloadLenOffset, (ushort)payloadLen);
                offset = writer.Position;

                return offset - startOffset;
            }

            throw new ArgumentException(
                $"RpcSerializer: unsupported parameter type '{value?.GetType().FullName ?? "null"}'.",
                nameof(value));
        }

        // ── Read ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Decode a single typed parameter from <paramref name="data"/> at
        /// <paramref name="offset"/>.  Advances <paramref name="offset"/> by
        /// the number of bytes consumed.
        ///
       /// Returns <see langword="null"/> and sets <paramref name="offset"/> to -1
        /// on truncated or unrecognised input — the caller must check for -1
        /// before further reads.
        /// </summary>
        public static object ReadParam(byte[] data, ref int offset)
        {
            if (data == null || offset < 0 || offset >= data.Length)
            {
                offset = -1;
                return null;
            }

            byte typeId = data[offset++];

            // All bounds checks below use the form `data.Length - offset < N`
            // rather than `offset + N > data.Length`.  The latter overflows in
            // signed-int arithmetic when offset is near int.MaxValue and N is
            // attacker-controlled (e.g. ushort strLen up to 65535), bypassing
            // the bounds check.  Subtracting from the known-non-negative
            // (offset >= 0 here, data.Length >= 0 always) cannot overflow.
            switch (typeId)
            {
                case RpcTypeId.Int32:
                    if (data.Length - offset < 4) { offset = -1; return null; }
                    int i32 = ReadI32LE(data, offset); offset += 4;
                    return i32;

                case RpcTypeId.Float32:
                    if (data.Length - offset < 4) { offset = -1; return null; }
                    float f32 = ReadF32LE(data, offset); offset += 4;
                    return f32;

                case RpcTypeId.Bool:
                    if (data.Length - offset < 1) { offset = -1; return null; }
                    return data[offset++] != 0;

                case RpcTypeId.UInt64:
                    if (data.Length - offset < 8) { offset = -1; return null; }
                    ulong u64 = ReadU64LE(data, offset); offset += 8;
                    return u64;

                case RpcTypeId.String:
                {
                    if (data.Length - offset < 2) { offset = -1; return null; }
                    ushort strLen = ReadU16LE(data, offset); offset += 2;
                    if (data.Length - offset < strLen) { offset = -1; return null; }
                    string str;
                    try { str = Utf8.GetString(data, offset, strLen); }
                    catch (DecoderFallbackException) { offset = -1; return null; }
                    offset += strLen;
                    return str;
                }

                case RpcTypeId.Bytes:
                {
                    if (data.Length - offset < 2) { offset = -1; return null; }
                    ushort bLen = ReadU16LE(data, offset); offset += 2;
                    if (data.Length - offset < bLen) { offset = -1; return null; }
                    byte[] byteArr = new byte[bLen];
                    Buffer.BlockCopy(data, offset, byteArr, 0, bLen);
                    offset += bLen;
                    return byteArr;
                }

                case RpcTypeId.Vector3:
                {
                    if (data.Length - offset < 12) { offset = -1; return null; }
                    float vx = ReadF32LE(data, offset); offset += 4;
                    float vy = ReadF32LE(data, offset); offset += 4;
                    float vz = ReadF32LE(data, offset); offset += 4;
                    return new Vector3(vx, vy, vz);
                }

                case RpcTypeId.Color:
                {
                    if (data.Length - offset < 16) { offset = -1; return null; }
                    float cr = ReadF32LE(data, offset); offset += 4;
                    float cg = ReadF32LE(data, offset); offset += 4;
                    float cb = ReadF32LE(data, offset); offset += 4;
                    float ca = ReadF32LE(data, offset); offset += 4;
                    return new Color(cr, cg, cb, ca);
                }

                case RpcTypeId.Quaternion:
                {
                    if (data.Length - offset < 16) { offset = -1; return null; }
                    float qx = ReadF32LE(data, offset); offset += 4;
                    float qy = ReadF32LE(data, offset); offset += 4;
                    float qz = ReadF32LE(data, offset); offset += 4;
                    float qw = ReadF32LE(data, offset); offset += 4;
                    return new Quaternion(qx, qy, qz, qw);
                }

                case RpcTypeId.INetworkSerializable:
                {
                    // [type_name_len:2 LE][type_name][payload_len:2 LE][payload]
                    if (data.Length - offset < 2) { offset = -1; return null; }
                    ushort nameLen = ReadU16LE(data, offset); offset += 2;
                    if (data.Length - offset < nameLen) { offset = -1; return null; }
                    string typeName;
                    if (nameLen == 0) typeName = string.Empty;
                    else
                    {
                        try { typeName = Utf8.GetString(data, offset, nameLen); }
                        catch (DecoderFallbackException) { offset = -1; return null; }
                    }
                    offset += nameLen;

                    if (data.Length - offset < 2) { offset = -1; return null; }
                    ushort payloadLen = ReadU16LE(data, offset); offset += 2;
                    if (data.Length - offset < payloadLen) { offset = -1; return null; }

                    int payloadStart = offset;
                    offset += payloadLen; // ALWAYS advance past payload, even on failure

                    // SECURITY: typeName is wire-supplied and must NEVER be
                    // resolved via Type.GetType / AppDomain probing.  The
                    // registry is a closed, author-attested allow-list:
                    // names not in the registry are dropped with a warning
                    // so an attacker cannot reach arbitrary parameterless-
                    // ctor INetworkSerializable types loaded in the
                    // AppDomain.
                    Type concrete = RpcTypeRegistry.Resolve(typeName);
                    if (concrete == null)
                    {
                        Debug.LogWarning(
                            $"[RTMPE] RpcSerializer: rejected unregistered " +
                            $"INetworkSerializable type '{typeName}' on inbound RPC. " +
                            "Register it explicitly via RpcTypeRegistry.Register<T>() " +
                            "or annotate the type with [RtmpeRpcSerializable] and " +
                            "enable RpcTypeRegistry.AllowAppDomainScan.  Parameter " +
                            "surfaced as null; downstream dispatch will reject " +
                            "non-nullable bindings.");
                        return null;
                    }

                    // Preserve type identity even on failure so the eventual
                    // MethodInfo.Invoke does not throw a value-type-binding
                    // error on a null argument when the receiver's parameter
                    // is a struct.  For reference types we still return a
                    // default-constructed instance — the caller can detect
                    // that the deserialize was partial via the warning log.
                    object instance;
                    try
                    {
                        // IL2CPP-safe path: Register<T>() stores () => new T().
                        // Falls back to Activator only for types registered via
                        // Register(Type) without a factory (Mono-only path).
                        instance = RpcTypeRegistry.CreateInstance(typeName);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning(
                            $"[RTMPE] RpcSerializer: failed to instantiate '{typeName}' " +
                            $"({ex.GetType().Name}: {ex.Message}).  Returning null parameter — " +
                            "RPC dispatch may fail if the receiver's parameter is a value type. " +
                            "Ensure the type has a public parameterless constructor.");
                        return null;
                    }

                    if (!(instance is INetworkSerializable ns))
                    {
                        Debug.LogWarning(
                            $"[RTMPE] RpcSerializer: type '{typeName}' resolved but does not " +
                            "implement INetworkSerializable.  Returning null parameter.");
                        return null;
                    }

                    var reader = new RtmpeBinaryReader(data, payloadStart, payloadLen);
                    try
                    {
                        ns.NetworkDeserialize(reader);
                    }
                    catch (RpcDeserializationException)
                    {
                        // Re-throw without wrapping so the dispatcher sees the
                        // original type-name context.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // A throwing NetworkDeserialize cannot leave a coherent
                        // instance behind — escalate so the dispatcher drops
                        // the entire RPC rather than invoking the receiver
                        // with a partial-state argument.
                        throw new RpcDeserializationException(
                            typeName,
                            $"NetworkDeserialize for '{typeName}' threw " +
                            $"{ex.GetType().Name}: {ex.Message}.",
                            ex);
                    }

                    if (reader.HasFailed)
                    {
                        // Truncated payload — same policy: refuse to surface a
                        // partially-populated instance to user code.
                        throw new RpcDeserializationException(
                            typeName,
                            $"Payload for '{typeName}' was truncated during NetworkDeserialize.");
                    }

                    return ns;
                }

                default:
                    offset = -1;
                    return null;
            }
        }

        // ── LE write helpers ──────────────────────────────────────────────────

        internal static void WriteU16LE(byte[] buf, int offset, ushort v)
        {
            buf[offset]     = (byte)(v);
            buf[offset + 1] = (byte)(v >> 8);
        }

        internal static void WriteI32LE(byte[] buf, int offset, int v)
        {
            uint u = (uint)v;
            buf[offset]     = (byte)(u);
            buf[offset + 1] = (byte)(u >> 8);
            buf[offset + 2] = (byte)(u >> 16);
            buf[offset + 3] = (byte)(u >> 24);
        }

        internal static void WriteU32LE(byte[] buf, int offset, uint v)
        {
            buf[offset]     = (byte)(v);
            buf[offset + 1] = (byte)(v >> 8);
            buf[offset + 2] = (byte)(v >> 16);
            buf[offset + 3] = (byte)(v >> 24);
        }

        internal static void WriteU64LE(byte[] buf, int offset, ulong v)
        {
            buf[offset]     = (byte)(v);
            buf[offset + 1] = (byte)(v >> 8);
            buf[offset + 2] = (byte)(v >> 16);
            buf[offset + 3] = (byte)(v >> 24);
            buf[offset + 4] = (byte)(v >> 32);
            buf[offset + 5] = (byte)(v >> 40);
            buf[offset + 6] = (byte)(v >> 48);
            buf[offset + 7] = (byte)(v >> 56);
        }

        internal static void WriteF32LE(byte[] buf, int offset, float v)
        {
            var u = new FloatInt32Union { Float = v };
            // unchecked: float bits may produce a negative int (e.g. negative floats have
            // the sign bit set); reinterpreting as uint must not throw in checked contexts.
            uint bits = unchecked((uint)u.Int);
            buf[offset]     = (byte)(bits);
            buf[offset + 1] = (byte)(bits >> 8);
            buf[offset + 2] = (byte)(bits >> 16);
            buf[offset + 3] = (byte)(bits >> 24);
        }

        // ── LE read helpers ───────────────────────────────────────────────────

        internal static ushort ReadU16LE(byte[] buf, int offset)
            => (ushort)(buf[offset] | (buf[offset + 1] << 8));

        internal static int ReadI32LE(byte[] buf, int offset)
        {
            // Build via uint to avoid signed-int overflow in checked contexts when
            // the high byte is >= 0x80 (buf[3] << 24 would overflow a signed int).
            uint bits = (uint)buf[offset]
                      | ((uint)buf[offset + 1] << 8)
                      | ((uint)buf[offset + 2] << 16)
                      | ((uint)buf[offset + 3] << 24);
            return unchecked((int)bits);
        }

        internal static uint ReadU32LE(byte[] buf, int offset)
            => (uint)(buf[offset]
             | (buf[offset + 1] << 8)
             | (buf[offset + 2] << 16)
             | (buf[offset + 3] << 24));

        internal static ulong ReadU64LE(byte[] buf, int offset)
            => (ulong)buf[offset]
             | ((ulong)buf[offset + 1] << 8)
             | ((ulong)buf[offset + 2] << 16)
             | ((ulong)buf[offset + 3] << 24)
             | ((ulong)buf[offset + 4] << 32)
             | ((ulong)buf[offset + 5] << 40)
             | ((ulong)buf[offset + 6] << 48)
             | ((ulong)buf[offset + 7] << 56);

        internal static float ReadF32LE(byte[] buf, int offset)
        {
            // Build via uint to avoid signed-int overflow in checked contexts;
            // then reinterpret bits as float through the union.
            uint bits = (uint)buf[offset]
                      | ((uint)buf[offset + 1] << 8)
                      | ((uint)buf[offset + 2] << 16)
                      | ((uint)buf[offset + 3] << 24);
            return new FloatInt32Union { Int = unchecked((int)bits) }.Float;
        }
    }
}
