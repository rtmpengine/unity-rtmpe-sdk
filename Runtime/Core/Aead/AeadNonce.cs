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

namespace RTMPE.Core.Aead
{
    internal static class AeadNonce
    {
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
            var nonce = new byte[12];
            // counter — 8 bytes LE (high 32 bits are always 0)
            nonce[0] = (byte) counter;
            nonce[1] = (byte)(counter >>  8);
            nonce[2] = (byte)(counter >> 16);
            nonce[3] = (byte)(counter >> 24);
            // nonce[4..7] remain 0x00 (C# zero-initialises arrays)

            // session_id — 4 bytes LE
            nonce[8]  = (byte) sessionId;
            nonce[9]  = (byte)(sessionId >>  8);
            nonce[10] = (byte)(sessionId >> 16);
            nonce[11] = (byte)(sessionId >> 24);
            return nonce;
        }
    }
}
