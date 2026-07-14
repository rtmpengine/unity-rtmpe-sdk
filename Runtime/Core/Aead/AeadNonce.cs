// RTMPE SDK — Runtime/Core/Aead/AeadNonce.cs
//
// Pure static helper that builds the 12-byte ChaCha20-Poly1305 nonce used
// on both the encrypt (outbound) and decrypt (inbound) AEAD paths. Matches
// the Rust gateway's <c>NonceGenerator::build_nonce_raw</c> in
// <c>modules/gateway/src/crypto/nonce.rs</c> — any drift here would break
// every encrypted frame on the wire and is silently catastrophic (Poly1305
// would mismatch and the gateway would drop the packet without a log
// signal). Kept as a free static so unit tests can exercise it without
// instantiating <c>NetworkManager</c> or any Unity context.
//
// GC Round 3 (2026-05-02):
//   • Added BuildInto(counter, sessionId, dest) overload that writes the
//     12 bytes into a caller-provided byte[] (typically rented from
//     ArrayPool<byte>.Shared or a stack-allocated buffer).  The legacy
//     allocating Build() now delegates to BuildInto, preserving wire
//     bytes bit-for-bit and keeping the existing test surface green.
//   • Eliminating the per-packet `new byte[12]` removes ~12 bytes per
//     outbound and per inbound packet from the AEAD pipeline's allocation
//     ledger; at 30 Hz across 32 connections that is ~23 KB/sec recovered.

using System;

namespace RTMPE.Core.Aead
{
    internal static class AeadNonce
    {
        /// <summary>
        /// Wire size of an RTMPE AEAD nonce in bytes.  Exposed as a public
        /// constant so callers can size pooled / stack buffers correctly
        /// without re-deriving the value from the file header comment.
        /// </summary>
        public const int Size = 12;

        /// <summary>
        /// Build the 12-byte ChaCha20-Poly1305 nonce shared by both directions.
        ///
        /// <para>Layout (matches gateway <c>build_nonce_raw(counter, session_id)</c>):</para>
        /// <code>
        ///  [counter : 8 bytes LE u64] [session_id : 4 bytes LE u32]
        /// </code>
        ///
        /// <para><paramref name="counter"/> is a <see cref="uint"/> (zero-extended
        /// to 8 bytes); the high 4 bytes are therefore always <c>0x00</c> for
        /// any session within its practical lifetime (2^32 packets ≈ 4 G).</para>
        /// </summary>
        public static byte[] Build(uint counter, uint sessionId)
        {
            var nonce = new byte[Size];
            BuildInto(counter, sessionId, nonce, 0);
            return nonce;
        }

        /// <summary>
        /// Pooled / stack-buffer overload of <see cref="Build"/>.  Writes the
        /// 12 nonce bytes into <paramref name="dest"/> starting at
        /// <paramref name="destOffset"/>.  Use this in hot-path encrypt /
        /// decrypt loops to avoid the per-packet 12-byte heap allocation
        /// that <see cref="Build"/> incurs.
        /// </summary>
        /// <param name="counter">Outbound nonce counter (uint, LE-extended to 8 bytes).</param>
        /// <param name="sessionId">RTMPE crypto-id (LE u32).</param>
        /// <param name="dest">Destination buffer; must have at least 12 bytes
        /// available from <paramref name="destOffset"/>.</param>
        /// <param name="destOffset">Byte offset within <paramref name="dest"/>
        /// at which to start writing.</param>
        public static void BuildInto(uint counter, uint sessionId, byte[] dest, int destOffset = 0)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            if (destOffset < 0 || (long)destOffset + Size > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for an AEAD nonce at the given offset.");

            // counter — 8 bytes LE (high 32 bits are always 0 because the
            // SDK's outbound counter is a uint).
            dest[destOffset + 0] = (byte) counter;
            dest[destOffset + 1] = (byte)(counter >>  8);
            dest[destOffset + 2] = (byte)(counter >> 16);
            dest[destOffset + 3] = (byte)(counter >> 24);
            dest[destOffset + 4] = 0;
            dest[destOffset + 5] = 0;
            dest[destOffset + 6] = 0;
            dest[destOffset + 7] = 0;

            // session_id — 4 bytes LE
            dest[destOffset +  8] = (byte) sessionId;
            dest[destOffset +  9] = (byte)(sessionId >>  8);
            dest[destOffset + 10] = (byte)(sessionId >> 16);
            dest[destOffset + 11] = (byte)(sessionId >> 24);
        }
    }
}
