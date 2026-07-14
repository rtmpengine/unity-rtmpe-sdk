// RTMPE SDK — Runtime/Core/GameplaySequencePrefix.cs
//
// Helpers for the 4-byte monotonic gameplay-sequence prefix that gameplay
// packets carry when NetworkSettings.enableGameplayOrdering is true.  The
// flag bit PacketFlags.GameplayOrdered is set on the packet header so the
// receiver can dispatch on the prefix without pre-coordinating with the
// payload type.
//
// Layout when present (always at byte 0 of the application payload, BEFORE
// any per-packet-type fields):
//
//   [0..3]  gameplay_sequence : u32 LE   — strictly monotonic, wraparound
//                                          handled by RFC 1982 modular
//                                          comparison in
//                                          GameplayOrderingBuffer.
//
// Allocation discipline: TryStrip never copies; it returns offsets into the
// caller's buffer.  Wrap allocates a new array (the only zero-copy way to
// prepend bytes to an externally-owned payload is unsafe pointer juggling
// or pinned arrays — neither is appropriate at this layer).

using System.Threading;

namespace RTMPE.Core
{
    /// <summary>
    /// Pure-static helpers for prefixing / consuming the 4-byte gameplay
    /// sequence used by the gameplay-ordering scaffold (RPC ↔ StateSync ordering).
    /// </summary>
    public static class GameplaySequencePrefix
    {
        /// <summary>Bytes occupied by the gameplay-sequence prefix.</summary>
        public const int PrefixSize = 4;

        // Process-wide monotonic counter.  Interlocked-incremented per outbound
        // gameplay packet so multiple sender threads cannot collide; cast to
        // uint at write time so wraparound matches RFC 1982 semantics.
        private static int _counter;

        /// <summary>
        /// Acquire the next gameplay sequence.  Thread-safe via Interlocked.
        /// </summary>
        public static uint NextSequence()
        {
            return unchecked((uint)Interlocked.Increment(ref _counter));
        }

#if UNITY_INCLUDE_TESTS
        /// <summary>Reset the counter (test-only seam).  Compiled only when
        /// <c>UNITY_INCLUDE_TESTS</c> is defined so the shipped Player
        /// assembly carries no public mutator on the gameplay-ordering
        /// counter.</summary>
        public static void ResetForTest()
        {
            Interlocked.Exchange(ref _counter, 0);
        }
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Wrap <paramref name="payload"/> with the supplied gameplay sequence
        /// prepended in little-endian.  Returns a new array; the caller must
        /// also set <c>PacketFlags.GameplayOrdered</c> on the wrapping
        /// packet.
        /// </summary>
        public static byte[] Wrap(uint sequence, byte[] payload)
        {
            int payloadLen = payload != null ? payload.Length : 0;
            var wrapped = new byte[PrefixSize + payloadLen];
            wrapped[0] = (byte)(sequence);
            wrapped[1] = (byte)(sequence >> 8);
            wrapped[2] = (byte)(sequence >> 16);
            wrapped[3] = (byte)(sequence >> 24);
            if (payloadLen > 0)
                System.Buffer.BlockCopy(payload, 0, wrapped, PrefixSize, payloadLen);
            return wrapped;
        }

        /// <summary>
        /// Try to strip the 4-byte sequence prefix from <paramref name="payload"/>.
        /// Returns <c>true</c> on success and writes the parsed
        /// <paramref name="sequence"/> plus the offset/length of the
        /// remaining payload bytes.  Returns <c>false</c> when the buffer is
        /// shorter than 4 bytes.
        /// </summary>
        public static bool TryStrip(byte[] payload, out uint sequence, out int innerOffset, out int innerLength)
        {
            sequence    = 0;
            innerOffset = 0;
            innerLength = 0;
            if (payload == null || payload.Length < PrefixSize) return false;

            sequence =
                  (uint)payload[0]
                | ((uint)payload[1] << 8)
                | ((uint)payload[2] << 16)
                | ((uint)payload[3] << 24);
            innerOffset = PrefixSize;
            innerLength = payload.Length - PrefixSize;
            return true;
        }
    }
}
