// RTMPE SDK — Runtime/Sync/VariableBatchBuilder.cs
//
// Coalesces variable updates across multiple owned objects into a single
// per-tick batch packet.  Eliminates the per-packet ~61-byte tax (IP+UDP 28B
// + RTMPE header 13B + AEAD 20B) that dominates traffic when many small
// variable deltas leave the client each tick.
//
// Wire format (all fields little-endian):
//
//   [0]      count : u8                — number of entries in this batch
//   [1..]    entries × count where each entry is:
//     [0..1] entry_len : u16           — total bytes of the entry payload
//     [2..]  entry_payload : N bytes   — the bytes produced by the legacy
//                                        per-object VariableUpdate builder
//                                        (object_id + tick + var_count + N
//                                        × {var_id, value_len, value})
//
// Each entry is a legacy 0x41 payload verbatim, so a gateway that
// understands batching can split the batch and replay the inner payloads
// through the existing 0x41 dispatcher.  The batch packet uses a new
// PacketType (VariableBatchUpdate, 0x44) so an old gateway sees an
// unrecognised packet and drops it; clients pre-filter on
// NetworkSettings.enableVariableBatching so a gateway that has not opted
// in never receives the new type.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Coalesces multiple per-object NetworkVariable update payloads into a
    /// single batch payload suitable for one AEAD-encrypted UDP datagram.
    /// </summary>
    public static class VariableBatchBuilder
    {
        /// <summary>Maximum entries the count byte can express.</summary>
        public const int MaxEntries = byte.MaxValue;

        /// <summary>
        /// Build a batch payload over <paramref name="count"/> entries from
        /// <paramref name="payloads"/>.  Returns the batch bytes.  Throws when
        /// any entry exceeds <see cref="ushort.MaxValue"/> bytes (the per-
        /// entry length prefix limit).
        /// </summary>
        public static byte[] Build(byte[][] payloads, int count)
        {
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (count < 0 || count > payloads.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count > MaxEntries)
                throw new ArgumentException(
                    $"variable batch holds at most {MaxEntries} entries (got {count}); " +
                    "split into multiple batches at the call site.",
                    nameof(count));

            // Pre-compute total length so the result is allocated exactly once.
            int total = 1; // count byte
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                if (len > ushort.MaxValue)
                    throw new ArgumentException(
                        $"variable batch entry {i} is {len} bytes — wire-format " +
                        "supports up to 65535 per entry.",
                        nameof(payloads));
                total += 2 + len; // length prefix + payload
            }

            var result = new byte[total];
            result[0] = (byte)count;
            int off = 1;
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                result[off]     = (byte)(len);
                result[off + 1] = (byte)(len >> 8);
                off += 2;
                if (len > 0)
                {
                    Buffer.BlockCopy(payloads[i], 0, result, off, len);
                    off += len;
                }
            }
            return result;
        }

        /// <summary>
        /// Decode a batch payload.  Walks each entry, invoking
        /// <paramref name="dispatch"/> with a freshly allocated copy of every
        /// inner payload (the gateway side and tests use this path; the
        /// runtime SDK does not currently receive batch packets — gateways
        /// fan them back out as legacy 0x41 packets).  Returns the number of
        /// entries successfully dispatched; returns -1 on a malformed batch.
        /// </summary>
        public static int TryParse(byte[] batch, Action<byte[]> dispatch)
        {
            if (batch == null || batch.Length < 1) return -1;
            if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));

            int count = batch[0];
            int off = 1;
            for (int i = 0; i < count; i++)
            {
                if (off + 2 > batch.Length) return -1;
                int len = batch[off] | (batch[off + 1] << 8);
                off += 2;
                if (off + len > batch.Length) return -1;

                var inner = new byte[len];
                if (len > 0)
                    Buffer.BlockCopy(batch, off, inner, 0, len);
                off += len;
                dispatch(inner);
            }
            return count;
        }
    }
}
