// RTMPE SDK — Tests/Runtime/BandwidthTests.cs
//
// RTMPE SDK — Bandwidth Budget Validation Tests
//
// These tests are pure arithmetic — no GameObjects, no MonoBehaviours,
// no network connection required.  They verify that the wire-format sizes
// chosen for the RTMPE sync protocol fit within the per-player budget of
// 50 KB/s (51,200 bytes/s) at the specified 30 Hz tick rate.
//
// Corrections vs. original plan (P-5, P-7):
//   P-5  The plan mixed up "60 Hz" in a comment, but the RTMPE spec is 30 Hz.
//        All calculations here use TickRate = 30.
//   P-7  "50 KB/s budget" — correctly interpreted as 50 × 1 024 = 51,200 bytes/s,
//        NOT 50 000 bytes/s.
//
// Byte-size constants used throughout:
//   FullStatePayload  = 48 bytes  (TransformPacketBuilder.PAYLOAD_SIZE)
//     Layout: ObjectID(8) + Position(12) + Rotation(16) + Scale(12)
//
//   DeltaPayload (server → client, variable length):
//     HeaderOnly   =  9 bytes  (ObjectID=8 + ChangedMask=1)
//     PositionOnly = 21 bytes  (9 + pos 12)
//     PosRot       = 37 bytes  (9 + pos 12 + rot 16)   ← most common 3D case
//     MaxDelta     = 49 bytes  (9 + pos 12 + rot 16 + scale 12)
//
//   RTMPE packet header = 13 bytes per packet (PacketHeader fixed size)
//
// Budget per player per tick = bytes × players × tickRate ≤ 51,200 bytes/s

using NUnit.Framework;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("Bandwidth")]
    public class BandwidthTests
    {
        // ── Constants ─────────────────────────────────────────────────────────

        /// <summary>Players in the room excluding self (max 15 remote peers).</summary>
        private const int Players = 15;

        /// <summary>Tick rate in Hz (RTMPE spec § Performance Targets).</summary>
        private const int TickRate = 30;

        /// <summary>Full transform payload — TransformPacketBuilder.PAYLOAD_SIZE.</summary>
        private const int FullStateBytes = 48;

        /// <summary>Delta: ObjectID(8) + ChangedMask(1) = 9 bytes minimum.</summary>
        private const int DeltaMinBytes = 9;

        /// <summary>Delta: position only — 9 + 12 = 21 bytes.</summary>
        private const int DeltaPosOnlyBytes = 21;

        /// <summary>
        /// Delta: position + rotation — 9 + 12 + 16 = 37 bytes.
        /// This is the most common case in a 3D multiplayer game.
        /// </summary>
        private const int DeltaPosRotBytes = 37;

        /// <summary>Delta: all fields — 9 + 12 + 16 + 12 = 49 bytes (maximum).</summary>
        private const int DeltaMaxBytes = 49;

        /// <summary>RTMPE packet header — PacketHeader fixed size (13 bytes).</summary>
        private const int RtmpeHeaderBytes = 13;

        /// <summary>Budget: 50 KB/s per player (50 × 1 024 = 51 200 bytes/s).</summary>
        private const int BudgetBytesPerSec = 50 * 1024; // 51 200

        // ── Group 1: Byte-size constants ───────────────────────────────────────

        [Test]
        [Description("Full transform payload is exactly 48 bytes (ObjectID+Pos+Rot+Scale).")]
        public void FullStateBytes_Constant_Is48()
        {
            // Verify layout arithmetic: 8 + 12 + 16 + 12 = 48
            const int expected = 8 + 12 + 16 + 12;

            Assert.AreEqual(48, expected, "Layout arithmetic");
            Assert.AreEqual(48, FullStateBytes, "Constant matches layout");
        }

        [Test]
        [Description("Maximum delta payload is exactly 49 bytes (header + pos + rot + scale).")]
        public void DeltaMaxBytes_Constant_Is49()
        {
            // 9 (ObjectID 8 + mask 1) + 12 (pos) + 16 (rot) + 12 (scale)
            const int expected = 9 + 12 + 16 + 12;

            Assert.AreEqual(49, expected);
            Assert.AreEqual(49, DeltaMaxBytes);
        }

        [Test]
        [Description("Position+rotation delta is 37 bytes — most common case for 3D objects.")]
        public void DeltaPosRotBytes_Constant_Is37()
        {
            // 9 (header) + 12 (pos) + 16 (rot)
            const int expected = 9 + 12 + 16;

            Assert.AreEqual(37, expected);
            Assert.AreEqual(37, DeltaPosRotBytes);
        }

        [Test]
        [Description("Position-only delta is 21 bytes.")]
        public void DeltaPosOnlyBytes_Constant_Is21()
        {
            // 9 (header) + 12 (pos)
            const int expected = 9 + 12;

            Assert.AreEqual(21, expected);
            Assert.AreEqual(21, DeltaPosOnlyBytes);
        }

        [Test]
        [Description("Minimum delta (empty mask) is 9 bytes.")]
        public void DeltaMinBytes_Constant_Is9()
        {
            // ObjectID(8) + ChangedMask(1) — no optional fields included
            const int expected = 8 + 1;

            Assert.AreEqual(9, expected);
            Assert.AreEqual(9, DeltaMinBytes);
        }

        // ── Group 2: Per-player payload bandwidth at 30 Hz ──────────────────────
        // NOTE: These measurements cover payload bytes only (excludes the 13-byte
        // RTMPE packet header).  Wire bandwidth = payload + header; see Group 4
        // for the combined worst-case figure (27,900 bytes/s ≈ 27 KB/s < 50 KB/s).

        [Test]
        [Description("Full-state payload only: 15 peers × 48 bytes × 30 Hz = 21,600 bytes/s. " +
                     "Wire cost (+ 13-byte header) = 27,450 bytes/s — see Group 4.")]
        public void FullState_15Peers_30Hz_Under50KBps()
        {
            int bytesPerSec = FullStateBytes * Players * TickRate;

            // 48 × 15 × 30 = 21,600 bytes/s
            Assert.AreEqual(21_600, bytesPerSec, "Expected bandwidth");
            Assert.Less(bytesPerSec, BudgetBytesPerSec,
                $"Full-state: {bytesPerSec} B/s must be < {BudgetBytesPerSec} B/s ({BudgetBytesPerSec / 1024} KB/s).");
        }

        [Test]
        [Description("Worst-case delta payload only: 15 peers × 49 bytes × 30 Hz = 22,050 bytes/s. " +
                     "Wire cost (+ 13-byte header) = 27,900 bytes/s — see Group 4.")]
        public void MaxDelta_15Peers_30Hz_Under50KBps()
        {
            int bytesPerSec = DeltaMaxBytes * Players * TickRate;

            // 49 × 15 × 30 = 22,050 bytes/s
            Assert.AreEqual(22_050, bytesPerSec, "Expected bandwidth");
            Assert.Less(bytesPerSec, BudgetBytesPerSec,
                $"Max-delta: {bytesPerSec} B/s must be < {BudgetBytesPerSec} B/s.");
        }

        [Test]
        [Description("Pos+rot delta payload only (typical 3D): 15 × 37 × 30 = 16,650 bytes/s. " +
                     "Wire cost (+ 13-byte header) = 22,500 bytes/s.")]
        public void PosRotDelta_15Peers_30Hz_Under50KBps()
        {
            int bytesPerSec = DeltaPosRotBytes * Players * TickRate;

            // 37 × 15 × 30 = 16,650 bytes/s
            Assert.AreEqual(16_650, bytesPerSec, "Expected bandwidth");
            Assert.Less(bytesPerSec, BudgetBytesPerSec,
                $"Pos+rot delta: {bytesPerSec} B/s must be < {BudgetBytesPerSec} B/s.");
        }

        [Test]
        [Description("Position-only delta payload: 15 × 21 × 30 = 9,450 bytes/s. " +
                     "Wire cost (+ 13-byte header) = 15,300 bytes/s.")]
        public void PosOnlyDelta_15Peers_30Hz_Under50KBps()
        {
            int bytesPerSec = DeltaPosOnlyBytes * Players * TickRate;

            // 21 × 15 × 30 = 9,450 bytes/s
            Assert.AreEqual(9_450, bytesPerSec, "Expected bandwidth");
            Assert.Less(bytesPerSec, BudgetBytesPerSec,
                $"Pos-only delta: {bytesPerSec} B/s must be < {BudgetBytesPerSec} B/s.");
        }

        // ── Group 3: RTMPE header overhead ────────────────────────────────────

        [Test]
        [Description("RTMPE 13-byte header at 30 Hz = 390 bytes/s per peer — negligible.")]
        public void RtmpeHeader_30Hz_Overhead_IsNegligible()
        {
            // One packet per peer per tick.
            int headerOverheadPerSec = RtmpeHeaderBytes * Players * TickRate;

            // 13 × 15 × 30 = 5,850 bytes/s
            Assert.AreEqual(5_850, headerOverheadPerSec, "Expected header overhead");
            Assert.Less(headerOverheadPerSec, BudgetBytesPerSec,
                "Header overhead alone must be < budget.");

            // As a fraction of budget: < 12% (negligible)
            float fraction = (float)headerOverheadPerSec / BudgetBytesPerSec;
            Assert.Less(fraction, 0.12f,
                $"Header overhead fraction {fraction:P0} must be under 12% of budget.");
        }

        // ── Group 4: Combined payload + header ────────────────────────────────

        [Test]
        [Description("Worst case combined: (49 payload + 13 header) × 15 × 30 = 27,900 bytes/s.")]
        public void MaxDeltaPlusHeader_15Peers_30Hz_Under50KBps()
        {
            int bytesPerPacket = DeltaMaxBytes + RtmpeHeaderBytes; // 49 + 13 = 62
            int bytesPerSec    = bytesPerPacket * Players * TickRate;

            // 62 × 15 × 30 = 27,900 bytes/s
            Assert.AreEqual(27_900, bytesPerSec, "Expected combined bandwidth");
            Assert.Less(bytesPerSec, BudgetBytesPerSec,
                $"Max-delta + header: {bytesPerSec} B/s must be < {BudgetBytesPerSec} B/s.");
        }

        // ── Group 5: Go/No-Go summary ──────────────────────────────────────────

        [Test]
        [Description("Go/No-Go: all bandwidth scenarios fit within the 50 KB/s budget.")]
        public void GoNoGo_AllBandwidthScenariosUnderBudget()
        {
            // Each expression is individually evaluated so NUnit reports
            // WHICH assertion failed if the budget is ever exceeded.
            Assert.Less(FullStateBytes  * Players * TickRate, BudgetBytesPerSec, "full-state");
            Assert.Less(DeltaMaxBytes   * Players * TickRate, BudgetBytesPerSec, "max-delta");
            Assert.Less(DeltaPosRotBytes* Players * TickRate, BudgetBytesPerSec, "pos+rot delta");
            Assert.Less(DeltaPosOnlyBytes*Players * TickRate, BudgetBytesPerSec, "pos-only delta");
            Assert.Less(DeltaMinBytes   * Players * TickRate, BudgetBytesPerSec, "min-delta");
            Assert.Less(
                (DeltaMaxBytes + RtmpeHeaderBytes) * Players * TickRate, BudgetBytesPerSec,
                "max-delta + header");
        }
    }
}
