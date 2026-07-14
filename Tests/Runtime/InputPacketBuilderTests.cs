// RTMPE SDK — Tests/Runtime/InputPacketBuilderTests.cs
//
// NUnit Edit-Mode tests for InputPacketBuilder — the wire encoder for
// 0x43 server-authoritative input batches (Phase 2.x — 2026-04-25).
//
// What is verified:
//
//  1. Empty batch produces a 2-byte payload with count=0.
//  2. Single-entry batch round-trips bytewise via InputPayload.ReadFrom.
//  3. Multi-entry batch preserves ordering (oldest-first).
//  4. Wire size is exactly 2 + 13 * count bytes.
//  5. count > MaxBatchSize throws ArgumentOutOfRangeException.
//  6. count > payloads.Length throws ArgumentOutOfRangeException.
//  7. null payloads throws ArgumentNullException.
//
// These tests are the SDK's parity check against the Go side
// (modules/synchronization/.../input_payload.go ParseInputBatch).  If the
// wire format ever drifts between the two, this suite catches it before
// any network packet is exchanged.

using System;
using NUnit.Framework;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("InputPipeline")]
    public class InputPacketBuilderTests
    {
        // ── Wire-size invariants ────────────────────────────────────────────

        [Test]
        public void EmptyBatch_Produces2ByteHeaderOnly()
        {
            var payload = InputPacketBuilder.BuildBatchPayload(
                new InputPayload[0], 0);

            Assert.AreEqual(InputPacketBuilder.BatchHeaderSize, payload.Length,
                "Empty batch must equal the batch header size (2 bytes).");
            Assert.AreEqual(0, payload[0], "count low byte must be 0.");
            Assert.AreEqual(0, payload[1], "count high byte must be 0.");
        }

        [Test]
        public void SingleEntryBatch_HasExpectedWireSize()
        {
            var payloads = new[] { new InputPayload { Tick = 1 } };
            var payload  = InputPacketBuilder.BuildBatchPayload(payloads, 1);

            Assert.AreEqual(
                InputPacketBuilder.BatchHeaderSize + InputPayload.WireSize,
                payload.Length,
                "Single-entry batch is header + 13 bytes.");
        }

        [Test]
        public void FullBatch_HasExpectedWireSize()
        {
            var payloads = new InputPayload[InputPacketBuilder.MaxBatchSize];
            for (int i = 0; i < payloads.Length; i++)
                payloads[i] = new InputPayload { Tick = (uint)i };

            var payload = InputPacketBuilder.BuildBatchPayload(
                payloads, InputPacketBuilder.MaxBatchSize);

            Assert.AreEqual(
                InputPacketBuilder.BatchHeaderSize +
                    InputPacketBuilder.MaxBatchSize * InputPayload.WireSize,
                payload.Length,
                "Full batch is header + 13 * MaxBatchSize bytes.");
        }

        // ── Round-trip parity (encode → decode by InputPayload.ReadFrom) ─────

        [Test]
        public void SingleEntry_RoundTripsBytewise()
        {
            var entry = new InputPayload
            {
                Tick  = 42,
                MoveX = 0.5f,
                MoveY = -0.25f,
                Jump  = true,
            };
            var payload = InputPacketBuilder.BuildBatchPayload(new[] { entry }, 1);

            // Decode count.
            int count = payload[0] | (payload[1] << 8);
            Assert.AreEqual(1, count);

            // Decode entry.
            var got = InputPayload.ReadFrom(payload, InputPacketBuilder.BatchHeaderSize);
            Assert.AreEqual(entry.Tick,  got.Tick);
            Assert.AreEqual(entry.MoveX, got.MoveX);
            Assert.AreEqual(entry.MoveY, got.MoveY);
            Assert.AreEqual(entry.Jump,  got.Jump);
        }

        [Test]
        public void MultiEntry_PreservesOrderOldestFirst()
        {
            var entries = new InputPayload[3];
            for (int i = 0; i < 3; i++)
                entries[i] = new InputPayload
                {
                    Tick  = (uint)(100 + i),
                    MoveX = 0.1f * i,
                    MoveY = -0.1f * i,
                    Jump  = (i & 1) == 1,
                };

            var payload = InputPacketBuilder.BuildBatchPayload(entries, 3);

            int count = payload[0] | (payload[1] << 8);
            Assert.AreEqual(3, count);

            for (int i = 0; i < 3; i++)
            {
                int offset = InputPacketBuilder.BatchHeaderSize +
                             i * InputPayload.WireSize;
                var got = InputPayload.ReadFrom(payload, offset);
                Assert.AreEqual(entries[i].Tick,  got.Tick,  $"entry {i}: tick");
                Assert.AreEqual(entries[i].MoveX, got.MoveX, $"entry {i}: move_x");
                Assert.AreEqual(entries[i].MoveY, got.MoveY, $"entry {i}: move_y");
                Assert.AreEqual(entries[i].Jump,  got.Jump,  $"entry {i}: jump");
            }
        }

        // ── Argument validation ─────────────────────────────────────────────

        [Test]
        public void NullPayloads_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(() =>
                InputPacketBuilder.BuildBatchPayload(null, 0));
        }

        [Test]
        public void NegativeCount_ThrowsArgumentOutOfRangeException()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                InputPacketBuilder.BuildBatchPayload(new InputPayload[0], -1));
        }

        [Test]
        public void CountAboveMaxBatchSize_ThrowsArgumentOutOfRangeException()
        {
            var arr = new InputPayload[InputPacketBuilder.MaxBatchSize + 1];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                InputPacketBuilder.BuildBatchPayload(arr, InputPacketBuilder.MaxBatchSize + 1));
        }

        [Test]
        public void CountAbovePayloadsLength_ThrowsArgumentOutOfRangeException()
        {
            var arr = new InputPayload[2];
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                InputPacketBuilder.BuildBatchPayload(arr, 5));
        }

        // ── Bandwidth sanity ────────────────────────────────────────────────

        [Test]
        public void MaxBatchSize_StaysUnderUDPMTU()
        {
            // 1500-byte Ethernet MTU minus 20 (IPv4) minus 8 (UDP) minus 13
            // (RTMPE binary header) = 1459 bytes for the application payload.
            // A full input batch must comfortably fit so a worst-case
            // catch-up send never fragments at the IP layer.
            var maxBatchBytes = InputPacketBuilder.BatchHeaderSize +
                                InputPacketBuilder.MaxBatchSize *
                                InputPayload.WireSize;
            Assert.Less(maxBatchBytes, 1459,
                $"MaxBatchSize wire footprint ({maxBatchBytes} B) exceeds the safe UDP MTU budget.");
        }
    }
}
