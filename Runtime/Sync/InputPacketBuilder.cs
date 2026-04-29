// RTMPE SDK — Runtime/Sync/InputPacketBuilder.cs
//
// Serialises a batch of unacknowledged InputPayload frames into the wire
// format consumed by gateway packet type 0x43 (InputPayload) — the
// server-authoritative input opcode added in Phase 2.x (2026-04-25).
//
// Wire format (all little-endian, raw binary):
//
//  [0..1]      count : u16  — number of InputPayload entries that follow
//  [2..2+13*N] payload entries, each 13 bytes:
//    [+0..3] tick   : u32
//    [+4..7] move_x : f32
//    [+8..11] move_y : f32
//    [+12]    flags  : u8 (bit 0 = Jump)
//
// Total wire size: 2 + 13 * count bytes.
//
// Player identity is NOT carried in the payload — the gateway resolves
// session_id → authoritative player_id and embeds both in the NATS envelope
// before the Sync Service ever sees the bytes.  This eliminates the
// client-spoofing surface that would exist if a client could stamp any
// player_id it liked on its own inputs.
//
// MUST stay in sync with:
//  - PacketType.InputPayload = 0x43 (NetworkConstants.cs)
//  - PacketType::InputPayload = 0x43 (modules/gateway/src/packet/header.rs)
//  - InputPayloadParser (Go side, modules/synchronization/.../input_payload.go)
//
// No UnityEngine dependency — testable from pure .NET xunit projects.

using System;
using RTMPE.Core;

namespace RTMPE.Sync
{
    /// <summary>
    /// Encodes a batch of <see cref="InputPayload"/> frames into the raw
    /// 0x43 wire payload consumed by the Sync Service.
    /// </summary>
    public static class InputPacketBuilder
    {
        // ── Wire constants ─────────────────────────────────────────────────────

        /// <summary>Wire size of the batch header (the leading u16 count).</summary>
        public const int BatchHeaderSize = 2;

        /// <summary>
        /// Maximum number of <see cref="InputPayload"/> entries that fit in a
        /// single 0x43 packet.  Bounded by:
        ///
       /// <list type="bullet">
        /// <item>The u16 count field (65 535 logical max).</item>
        /// <item><see cref="InputBuffer.Capacity"/> (= 64 today) — the SDK
        /// can never accumulate more than the ring buffer allows.</item>
        /// </list>
        ///
       /// Set to <see cref="InputBuffer.Capacity"/> so any full ring buffer
        /// fits in one packet without forcing the caller to loop or
        /// fragment.  At 30 Hz this caps wire size at 2 + 13×64 = 834 bytes.
        /// </summary>
        public const int MaxBatchSize = InputBuffer.Capacity;

        // ── Build ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Serialise the first <paramref name="count"/> entries of
        /// <paramref name="payloads"/> into a fresh byte array.
        /// </summary>
        /// <param name="payloads">Source array of input frames; only the
        /// range <c>[0, count)</c> is read.</param>
        /// <param name="count">Number of entries to serialise.  Must be in
        /// <c>[0, MaxBatchSize]</c>.  Zero produces a 2-byte payload (count=0).
        /// </param>
        /// <returns>Newly-allocated byte array of length
        /// <c>BatchHeaderSize + 13 * count</c>.</returns>
        /// <exception cref="ArgumentNullException"/>
        /// <exception cref="ArgumentOutOfRangeException"/>
        public static byte[] BuildBatchPayload(InputPayload[] payloads, int count)
        {
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (count < 0 || count > MaxBatchSize)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"count must be in [0, {MaxBatchSize}].");
            if (count > payloads.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"count ({count}) exceeds payloads.Length ({payloads.Length}).");

            var buf = new byte[BatchHeaderSize + count * InputPayload.WireSize];

            // [0..1] count : u16 LE
            buf[0] = (byte)(count       & 0xFF);
            buf[1] = (byte)((count >> 8) & 0xFF);

            // [2..2+13*N] InputPayload entries
            int offset = BatchHeaderSize;
            for (int i = 0; i < count; i++)
            {
                payloads[i].WriteTo(buf, offset);
                offset += InputPayload.WireSize;
            }

            return buf;
        }
    }
}
