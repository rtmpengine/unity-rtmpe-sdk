/*
 * Copyright 2014 Google Inc. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Text;
using System.Runtime.InteropServices;

namespace Google.FlatBuffers
{
    /// <summary>
    /// All tables in the generated code derive from this struct, and add their own accessors.
    /// </summary>
    public struct Table
    {
        // Hardening limits applied at the parser boundary regardless of build
        // flags. Any payload containing a string longer than MaxStringBytes,
        // or a vector longer than MaxVectorElements, is treated as adversarial
        // and rejected. The caps are deliberately set well above the largest
        // legitimate value seen in any RTMPE schema (MTU is 16 KB) so that
        // benign traffic is never affected.
        internal const int MaxStringBytes = 4096;
        internal const int MaxVectorElements = 65536;

        // UTF-8 encoding configured to throw DecoderFallbackException on
        // malformed input rather than silently substituting U+FFFD. A
        // single instance is shared because UTF8Encoding.GetString is
        // thread-safe; only Decoder / Encoder objects (which we do not
        // use here) carry per-call state.
        private static readonly UTF8Encoding StrictUtf8 =
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        public int bb_pos { get; private set; }
        public ByteBuffer bb { get; private set; }

        public ByteBuffer ByteBuffer { get { return bb; } }

        // Re-init the internal state with an external buffer {@code ByteBuffer} and an offset within.
        public Table(int _i, ByteBuffer _bb) : this()
        {
            bb = _bb;
            bb_pos = _i;
        }

        // Look up a field in the vtable, return an offset into the object, or 0 if the field is not
        // present.
        public int __offset(int vtableOffset)
        {
            int vtable = bb_pos - bb.GetInt(bb_pos);
            return vtableOffset < bb.GetShort(vtable) ? (int)bb.GetShort(vtable + vtableOffset) : 0;
        }

        public static int __offset(int vtableOffset, int offset, ByteBuffer bb)
        {
            int vtable = bb.Length - offset;
            return (int)bb.GetShort(vtable + vtableOffset - bb.GetInt(vtable)) + vtable;
        }

        // Retrieve the relative offset stored at "offset"
        public int __indirect(int offset)
        {
            return offset + bb.GetInt(offset);
        }

        public static int __indirect(int offset, ByteBuffer bb)
        {
            return offset + bb.GetInt(offset);
        }

        // Create a .NET String from UTF-8 data stored inside the flatbuffer.
        // Local hardening over upstream: the wire length is validated to be
        // non-negative and bounded by MaxStringBytes, the decode goes through
        // a strict UTF-8 decoder so malformed sequences throw rather than
        // silently substituting U+FFFD, and embedded NUL bytes are rejected.
        public string __string(int offset)
        {
            int stringOffset = bb.GetInt(offset);
            if (stringOffset == 0)
                return null;

            offset += stringOffset;
            var len = bb.GetInt(offset);
            if (len < 0 || len > MaxStringBytes)
            {
                throw new InvalidOperationException(
                    "FlatBuffer string length out of bounds: " + len);
            }
            var startPos = offset + sizeof(int);
            return DecodeUtf8(bb, startPos, len);
        }

        internal static string DecodeUtf8(ByteBuffer bb, int startPos, int len)
        {
            // Reject embedded NUL: legitimate string fields in our schemas
            // are identifiers / display names / room IDs — none may contain
            // U+0000, and accepting them enables tag confusion in consumers
            // that treat strings as C-style.
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            var span = bb.ToReadOnlySpan(startPos, len);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == 0)
                {
                    throw new InvalidOperationException("FlatBuffer string contains embedded NUL");
                }
            }
            return StrictUtf8.GetString(span);
#elif ENABLE_SPAN_T
            var span = bb.ToReadOnlySpan(startPos, len);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i] == 0)
                {
                    throw new InvalidOperationException("FlatBuffer string contains embedded NUL");
                }
            }
            // .NET Standard 2.1 UTF8Encoding.GetString supports ReadOnlySpan.
            return StrictUtf8.GetString(span);
#else
            for (int i = 0; i < len; i++)
            {
                if (bb.Get(startPos + i) == 0)
                {
                    throw new InvalidOperationException("FlatBuffer string contains embedded NUL");
                }
            }
            return bb.GetStringUTF8Strict(startPos, len);
#endif
        }

        // Get the length of a vector whose offset is stored at "offset" in this object.
        // Local hardening over upstream: the decoded length must be non-negative
        // and capped at MaxVectorElements. A negative length on the wire would
        // otherwise propagate into MemoryMarshal.Cast and throw a much less
        // informative exception (and on some build flag combinations would
        // permit out-of-buffer reads).
        public int __vector_len(int offset)
        {
            offset += bb_pos;
            offset += bb.GetInt(offset);
            int len = bb.GetInt(offset);
            if (len < 0 || len > MaxVectorElements)
            {
                throw new InvalidOperationException(
                    "FlatBuffer vector length out of bounds: " + len);
            }
            return len;
        }

        // Get the start of data of a vector whose offset is stored at "offset" in this object.
        public int __vector(int offset)
        {
            offset += bb_pos;
            return offset + bb.GetInt(offset) + sizeof(int);  // data starts after the length
        }

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
        // Get the data of a vector whoses offset is stored at "offset" in this object as an
        // Spant&lt;byte&gt;. If the vector is not present in the ByteBuffer,
        // then an empty span will be returned.
        // Local hardening: on big-endian platforms the upstream code throws
        // NotSupportedException, which means a single inbound packet would
        // crash a BE-target build at runtime. Span casting requires a host
        // and wire endian match, so for non-byte element sizes on BE hosts we
        // surface a structured error to the verifier wrapper rather than
        // returning misaligned data — but we keep the byte-vector fast path
        // working since byte ordering is irrelevant for it.
        public Span<T> __vector_as_span<T>(int offset, int elementSize) where T : struct
        {
            var o = this.__offset(offset);
            if (0 == o)
            {
                return new Span<T>();
            }

            var pos = this.__vector(o);
            var len = this.__vector_len(o);
            if (!BitConverter.IsLittleEndian && elementSize > 1)
            {
                throw new InvalidOperationException(
                    "FlatBuffer typed-span access on big-endian host requires explicit byte swap");
            }
            // Local hardening over upstream: __vector_len already caps at
            // MaxVectorElements (65536) which combined with the largest legal
            // elementSize cannot overflow Int32 — but defence in depth: a
            // `checked` multiply rejects any future caller that bypasses the
            // hardened length accessor and supplies an unbounded len.
            int byteCount = checked(len * elementSize);
            return MemoryMarshal.Cast<byte, T>(bb.ToSpan(pos, byteCount));
        }
#else
        // Get the data of a vector whoses offset is stored at "offset" in this object as an
        // ArraySegment&lt;byte&gt;. If the vector is not present in the ByteBuffer,
        // then a null value will be returned.
        public ArraySegment<byte>? __vector_as_arraysegment(int offset)
        {
            var o = this.__offset(offset);
            if (0 == o)
            {
                return null;
            }

            var pos = this.__vector(o);
            var len = this.__vector_len(o);
            return bb.ToArraySegment(pos, len);
        }
#endif

        // Get the data of a vector whoses offset is stored at "offset" in this object as an
        // T[]. If the vector is not present in the ByteBuffer, then a null value will be
        // returned.
        // Local hardening: on big-endian hosts the upstream throws
        // NotSupportedException — we instead reroute through ByteBuffer's
        // existing endian-aware scalar accessors so a single received packet
        // cannot terminate the run loop. Element types whose accessors are
        // unavailable still surface a structured error.
        public T[] __vector_as_array<T>(int offset)
            where T : struct
        {
            var o = this.__offset(offset);
            if (0 == o)
            {
                return null;
            }

            var pos = this.__vector(o);
            var len = this.__vector_len(o);
            if (!BitConverter.IsLittleEndian)
            {
                return ReadTypedArrayBigEndian<T>(pos, len);
            }
            return bb.ToArray<T>(pos, len);
        }

        private T[] ReadTypedArrayBigEndian<T>(int pos, int len) where T : struct
        {
            var sizeOfT = ByteBuffer.SizeOf<T>();
            var arr = new T[len];
            // Use the per-scalar accessors which already handle endianness.
            // Boxing here is unavoidable for a generic scalar reader; the BE
            // path is rare (Unity ships LE on every shipping platform) so the
            // overhead is acceptable in exchange for not crashing the build.
            object boxed;
            for (int i = 0; i < len; i++)
            {
                int off = pos + i * sizeOfT;
                if (typeof(T) == typeof(byte))      boxed = bb.Get(off);
                else if (typeof(T) == typeof(sbyte)) boxed = bb.GetSbyte(off);
                else if (typeof(T) == typeof(short)) boxed = bb.GetShort(off);
                else if (typeof(T) == typeof(ushort)) boxed = bb.GetUshort(off);
                else if (typeof(T) == typeof(int))  boxed = bb.GetInt(off);
                else if (typeof(T) == typeof(uint)) boxed = bb.GetUint(off);
                else if (typeof(T) == typeof(long)) boxed = bb.GetLong(off);
                else if (typeof(T) == typeof(ulong)) boxed = bb.GetUlong(off);
                else if (typeof(T) == typeof(float)) boxed = bb.GetFloat(off);
                else if (typeof(T) == typeof(double)) boxed = bb.GetDouble(off);
                else
                {
                    throw new InvalidOperationException(
                        "FlatBuffer typed-array access on big-endian host: unsupported element type " + typeof(T).Name);
                }
                arr[i] = (T)boxed;
            }
            return arr;
        }

        // Initialize any Table-derived type to point to the union at the given offset.
        public T __union<T>(int offset) where T : struct, IFlatbufferObject
        {
            T t = new T();
            t.__init(__indirect(offset), bb);
            return t;
        }

        public static bool __has_identifier(ByteBuffer bb, string ident)
        {
            if (ident.Length != FlatBufferConstants.FileIdentifierLength)
                throw new ArgumentException("FlatBuffers: file identifier must be length " + FlatBufferConstants.FileIdentifierLength, "ident");

            for (var i = 0; i < FlatBufferConstants.FileIdentifierLength; i++)
            {
                if (ident[i] != (char)bb.Get(bb.Position + sizeof(int) + i)) return false;
            }

            return true;
        }

        // Compare strings in the ByteBuffer.
        public static int CompareStrings(int offset_1, int offset_2, ByteBuffer bb)
        {
            offset_1 += bb.GetInt(offset_1);
            offset_2 += bb.GetInt(offset_2);
            var len_1 = bb.GetInt(offset_1);
            var len_2 = bb.GetInt(offset_2);
            var startPos_1 = offset_1 + sizeof(int);
            var startPos_2 = offset_2 + sizeof(int);

#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            var span_1 = bb.ToReadOnlySpan(startPos_1, len_1);
            var span_2 = bb.ToReadOnlySpan(startPos_2, len_2);
            return span_1.SequenceCompareTo(span_2);
#else
            var len = Math.Min(len_1, len_2);
            for(int i = 0; i < len; i++) {
                byte b1 = bb.Get(i + startPos_1);
                byte b2 = bb.Get(i + startPos_2);
                if (b1 != b2)
                    return b1 - b2;
            }
            return len_1 - len_2;
#endif
        }

        // Compare string from the ByteBuffer with the string object
        public static int CompareStrings(int offset_1, byte[] key, ByteBuffer bb)
        {
            offset_1 += bb.GetInt(offset_1);
            var len_1 = bb.GetInt(offset_1);
            var len_2 = key.Length;
            var startPos_1 = offset_1 + sizeof(int);
#if ENABLE_SPAN_T && UNSAFE_BYTEBUFFER
            ReadOnlySpan<byte> span = bb.ToReadOnlySpan(startPos_1, len_1);
            ReadOnlySpan<byte> keySpan = key;
            return span.SequenceCompareTo(keySpan);
#else
            var len = Math.Min(len_1, len_2);
            for (int i = 0; i < len; i++) {
                byte b = bb.Get(i + startPos_1);
                if (b != key[i])
                    return b - key[i];
            }
            return len_1 - len_2;
#endif
        }
    }
}
