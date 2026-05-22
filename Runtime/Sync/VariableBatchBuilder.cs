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
        /// Working ceiling on a single legacy 0x41 entry payload inside a
        /// batch, enforced symmetrically on both the build and parse paths.
        /// The wire-format length prefix permits up to 65 535 bytes per
        /// entry, but the SDK's largest legitimate per-object update sits an
        /// order of magnitude below this 16 KiB ceiling, and a batch packet
        /// must in any case fit within the local MTU plus per-packet AEAD
        /// overhead.
        ///
        /// <para>On the parse path the cap rejects an entry whose declared
        /// size, while structurally legal, is an allocation-amplification
        /// surface — a 255-entry batch each claiming 65 535 bytes would
        /// otherwise force ~16 MiB of per-entry allocation before any
        /// inner-payload validation runs.</para>
        ///
        /// <para>On the build path the same cap keeps the producer from
        /// emitting a batch that a conformant receiver would reject in
        /// full: an over-ceiling entry is the caller's signal to send that
        /// object's update as a standalone VariableUpdate instead.</para>
        /// </summary>
        public const int MaxEntryPayloadBytes = 16 * 1024;

        /// <summary>
        /// Build a batch payload over <paramref name="count"/> entries from
        /// <paramref name="payloads"/>.  Returns the batch bytes.  Throws
        /// <see cref="ArgumentException"/> when any entry exceeds
        /// <see cref="MaxEntryPayloadBytes"/>.
        /// </summary>
        public static byte[] Build(byte[][] payloads, int count)
        {
            int total = ComputeTotalSize(payloads, count);
            var result = new byte[total];
            BuildInto(result, 0, payloads, count);
            return result;
        }

        /// <summary>
        /// Returns the exact wire size in bytes that <see cref="BuildInto"/>
        /// will produce for the given payload array, after applying the same
        /// validation as <see cref="Build"/>.  Use to size a pooled buffer.
        /// </summary>
        public static int ComputeTotalSize(byte[][] payloads, int count)
        {
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (count < 0 || count > payloads.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            if (count > MaxEntries)
                throw new ArgumentException(
                    $"variable batch holds at most {MaxEntries} entries (got {count}); " +
                    "split into multiple batches at the call site.",
                    nameof(count));

            int total = 1; // count byte
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                if (len > MaxEntryPayloadBytes)
                    throw new ArgumentException(
                        $"variable batch entry {i} is {len} bytes — a batched entry " +
                        $"is capped at the {MaxEntryPayloadBytes}-byte working ceiling " +
                        "(see MaxEntryPayloadBytes); an entry above the ceiling must " +
                        "be sent as a standalone VariableUpdate.",
                        nameof(payloads));
                total += 2 + len; // length prefix + payload
            }
            return total;
        }

        /// <summary>
        /// Pooled-buffer variant: writes the batch payload into
        /// <paramref name="dest"/> starting at <paramref name="destOffset"/>.
        /// Returns the number of bytes written (= <see cref="ComputeTotalSize"/>).
        /// <paramref name="dest"/> may be a buffer rented from
        /// <c>ArrayPool&lt;byte&gt;.Shared</c>.
        /// </summary>
        public static int BuildInto(byte[] dest, int destOffset, byte[][] payloads, int count)
        {
            if (dest == null) throw new ArgumentNullException(nameof(dest));
            int total = ComputeTotalSize(payloads, count);
            if (destOffset < 0 || (long)destOffset + total > dest.Length)
                throw new ArgumentOutOfRangeException(nameof(destOffset),
                    "dest is too small for a variable batch payload at the given offset.");

            dest[destOffset] = (byte)count;
            int off = destOffset + 1;
            for (int i = 0; i < count; i++)
            {
                int len = payloads[i] != null ? payloads[i].Length : 0;
                dest[off]     = (byte)(len);
                dest[off + 1] = (byte)(len >> 8);
                off += 2;
                if (len > 0)
                {
                    Buffer.BlockCopy(payloads[i], 0, dest, off, len);
                    off += len;
                }
            }
            return total;
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

            // Pre-flight allocation guard.  A malicious sender setting
            // count = 255 with each entry claiming the maximum 65 535-byte
            // payload would otherwise force the dispatcher to attempt
            // ~16 MiB of `new byte[]` allocations — well above any
            // legitimate batch.  Reject early when the declared count
            // cannot possibly fit in the remaining bytes (each entry
            // requires at least the 2-byte length prefix).
            int minBytesNeeded = 1 + count * 2;
            if (minBytesNeeded > batch.Length) return -1;

            int off = 1;
            for (int i = 0; i < count; i++)
            {
                if (off > batch.Length - 2) return -1;
                int len = batch[off] | (batch[off + 1] << 8);
                off += 2;
                if (len > batch.Length - off) return -1;
                // Per-entry working ceiling.  See MaxEntryPayloadBytes
                // remarks: a structurally-legal 65 535-byte entry is
                // allocation-amplification when 255 of them appear in a
                // single batch.  Reject before the per-entry alloc.
                if (len > MaxEntryPayloadBytes) return -1;

                var inner = new byte[len];
                if (len > 0)
                    Buffer.BlockCopy(batch, off, inner, 0, len);
                off += len;
                dispatch(inner);
            }

            // Strict trailing-bytes check.  A well-formed batch ends
            // exactly at the last entry's payload; trailing residue is a
            // protocol-drift / smuggling signal.  Returning -1 surfaces
            // the anomaly to the caller (which the receive path logs)
            // instead of silently accepting the partial parse.
            if (off != batch.Length) return -1;
            return count;
        }
    }
}
