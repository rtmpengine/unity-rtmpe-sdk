// RTMPE SDK — Tests/Runtime/Tier0NetworkingTests.cs
//
// NUnit Edit-Mode tests for the Tier-0 game-networking primitives:
//
//  • InputBuffer: uint32 sequence-wrap on AcknowledgeUpTo, plus the new
//    CopyUnacknowledgedAfter primitive used by the CSP replay path.
//  • NetworkManager tick loop: a long hitch advances every owed tick in a
//    single Update() instead of leaking residual time.
//  • CSP replay: NetworkTransform snaps to server pose at confirmedTick,
//    then re-simulates remaining unacked inputs via NetworkBehaviour.ApplyInput.
//  • NetworkVariable inbound tick gate: a re-ordered older delta is rejected.
//  • ReliableChannel: outbound retransmit + exponential backoff + dedup
//    window; out-of-order arrival yields exactly-once delivery.
//
// All tests are pure-managed (no Play-Mode required) where possible; the
// CSP and tick-loop tests use a real NetworkManager component because the
// behaviour under test is its Update() integration.

using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Tests
{
    // ────────────────────────────────────────────────────────────────────────
    // InputBuffer — sequence-wrap & replay primitives
    // ────────────────────────────────────────────────────────────────────────

    [TestFixture]
    [Category("CSP")]
    public class InputBufferWrapTests
    {
        [Test]
        [Description("AcknowledgeUpTo respects modular sequence comparison: an ack near " +
                     "uint.MaxValue followed by an ack just past wrap clears entries that " +
                     "straddle the boundary.")]
        public void AcknowledgeUpTo_AcrossUintWrap_ClearsAllUpToAck()
        {
            var buf = new InputBuffer();

            // Seed 50 ticks straddling the wrap boundary.  Pre-wrap ticks
            // come first (largest uint values), post-wrap come second
            // (small values from 0 upward).
            uint preWrapStart = uint.MaxValue - 19u;
            for (int i = 0; i < 20; i++)
                buf.Push(new InputPayload { Tick = preWrapStart + (uint)i });
            for (uint t = 0u; t < 30u; t++)
                buf.Push(new InputPayload { Tick = t });
            // Ring capacity is 64 so all 50 fit.
            Assert.AreEqual(50, buf.Count);

            // Ack everything up to a post-wrap tick; modular comparison
            // must recognise that pre-wrap ticks (e.g. uint.MaxValue - 5)
            // are LESS THAN post-wrap tick 10 in sequence-number space.
            buf.AcknowledgeUpTo(10u);

            // Remaining should be ticks 11..29 — i.e. 19 entries.
            var dest = new InputPayload[InputBuffer.Capacity];
            int n = buf.CopyUnacknowledgedTo(dest);
            Assert.AreEqual(19, n);
            Assert.AreEqual(11u, dest[0].Tick);
            Assert.AreEqual(29u, dest[18].Tick);
        }

        [Test]
        [Description("AcknowledgeUpTo with a numerically-smaller-but-modularly-greater " +
                     "value advances the gate (e.g. last ack = uint.MaxValue, new ack = 5).")]
        public void AcknowledgeUpTo_LastAckNearMaxValue_NewAckPostWrap_Advances()
        {
            var buf = new InputBuffer();
            buf.Push(new InputPayload { Tick = uint.MaxValue - 1u });
            buf.Push(new InputPayload { Tick = uint.MaxValue });
            buf.Push(new InputPayload { Tick = 0u });
            buf.Push(new InputPayload { Tick = 5u });

            buf.AcknowledgeUpTo(uint.MaxValue);
            Assert.AreEqual(2, buf.Count); // remaining: 0, 5

            buf.AcknowledgeUpTo(5u);  // numerically smaller, modularly greater
            Assert.AreEqual(0, buf.Count);
        }

        [Test]
        [Description("CopyUnacknowledgedAfter returns only entries strictly greater than the " +
                     "supplied watermark in oldest-first order.")]
        public void CopyUnacknowledgedAfter_ReturnsTicksAboveWatermark()
        {
            var buf  = new InputBuffer();
            var dest = new InputPayload[InputBuffer.Capacity];
            for (uint t = 1u; t <= 10u; t++)
                buf.Push(new InputPayload { Tick = t });

            int n = buf.CopyUnacknowledgedAfter(7u, dest);
            Assert.AreEqual(3, n);
            Assert.AreEqual(8u,  dest[0].Tick);
            Assert.AreEqual(9u,  dest[1].Tick);
            Assert.AreEqual(10u, dest[2].Tick);
        }

        [Test]
        [Description("Sequence helpers handle wrap correctly.")]
        public void SeqHelpers_WrapAware()
        {
            Assert.IsTrue (InputBuffer.SeqGreater(5u, uint.MaxValue));
            Assert.IsFalse(InputBuffer.SeqGreater(uint.MaxValue, 5u));
            Assert.IsTrue (InputBuffer.SeqLessOrEqual(uint.MaxValue, 5u));
            Assert.IsTrue (InputBuffer.SeqLessOrEqual(5u, 5u));
            Assert.IsFalse(InputBuffer.SeqLessOrEqual(5u, uint.MaxValue));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // CSP replay — NetworkTransform.ApplyReconciliation re-simulates inputs
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test stub that records replayed inputs so the test can assert the
    /// rollback walked the buffer in the expected oldest-first order.
    /// </summary>
    public class ReplayProbeTransform : NetworkTransform
    {
        public readonly List<InputPayload> Replayed = new List<InputPayload>();

        protected override void ApplyInput(InputPayload input, float deltaTime)
        {
            Replayed.Add(input);
            // Simulate forward-stepping a player at 1 m/s along x per move=1.
            var p = transform.position;
            p.x += input.MoveX * deltaTime;
            transform.position = p;
        }

        public void EnablePredictionForTest()
        {
            // Use reflection to flip the private serialised field so the
            // test does not require Inspector access.
            var f = typeof(NetworkTransform).GetField(
                "_enablePrediction",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            f.SetValue(this, true);
        }

        public void PushPredictedInput(InputPayload payload)
        {
            var f = typeof(NetworkTransform).GetField(
                "_inputBuffer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var buf = (InputBuffer)f.GetValue(this);
            buf.Push(payload);
        }
    }

    [TestFixture]
    [Category("CSP")]
    public class CspReplayTests
    {
        private GameObject           _nmGo;
        private NetworkManager       _manager;
        private GameObject           _go;
        private ReplayProbeTransform _nt;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();

            _go = new GameObject("ReplayProbe");
            _nt = _go.AddComponent<ReplayProbeTransform>();
            _go.transform.position = Vector3.zero;
            _nt.MarkClean();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go   != null) { Object.DestroyImmediate(_go);   _go   = null; }
            if (_nmGo != null) { Object.DestroyImmediate(_nmGo); _nmGo = null; }
        }

        [Test]
        [Description("On a snap-magnitude correction, the transform is set to the server pose " +
                     "and every input above confirmedInputTick is replayed in oldest-first order.")]
        public void ApplyReconciliation_SnapPath_ReplaysUnackedInputs()
        {
            _manager.SetLocalPlayerStringId("player-1");
            _nt.Initialize(1, "player-1");
            _nt.SetSpawned(true);
            _nt.EnablePredictionForTest();

            // Build a buffer of 5 inputs at ticks 96..100, all moving +x.
            for (uint t = 96u; t <= 100u; t++)
                _nt.PushPredictedInput(new InputPayload { Tick = t, MoveX = 1f });

            // Local prediction has drifted to position (5, 0, 0).  Server
            // says "at confirmedTick=98 you were at (10, 0, 0)".  Snap +
            // replay should place us at (10 + 2 * 1/30, 0, 0) — two ticks
            // (99, 100) of +x movement applied on top of the snapped pose.
            _go.transform.position = new Vector3(5f, 0f, 0f);

            _nt.ApplyReconciliation(
                new TransformState { Position = new Vector3(10f, 0f, 0f),
                                     Rotation = Quaternion.identity,
                                     Scale    = Vector3.one },
                confirmedInputTick: 98u,
                hasConfirmedTick:   true);

            // Two replay invocations, in order 99 then 100.
            Assert.AreEqual(2, _nt.Replayed.Count);
            Assert.AreEqual(99u,  _nt.Replayed[0].Tick);
            Assert.AreEqual(100u, _nt.Replayed[1].Tick);

            // Snapped to 10 plus two replay steps of dt = 1/30 each = 10 + 2/30.
            Assert.AreEqual(10f + 2f / 30f, _go.transform.position.x, 1e-4f);
        }

        [Test]
        [Description("Replay does not run when prediction is disabled, even if the server " +
                     "delta would otherwise trigger a snap.")]
        public void ApplyReconciliation_PredictionDisabled_NoReplay()
        {
            _manager.SetLocalPlayerStringId("player-1");
            _nt.Initialize(1, "player-1");
            _nt.SetSpawned(true);
            // Prediction NOT enabled.

            _nt.PushPredictedInput(new InputPayload { Tick = 5u, MoveX = 1f });
            _go.transform.position = new Vector3(5f, 0f, 0f);

            _nt.ApplyReconciliation(
                new TransformState { Position = new Vector3(10f, 0f, 0f),
                                     Rotation = Quaternion.identity,
                                     Scale    = Vector3.one },
                confirmedInputTick: 0u,
                hasConfirmedTick:   true);

            Assert.AreEqual(0, _nt.Replayed.Count);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tick loop — a long hitch drains every owed tick in one Update
    // ────────────────────────────────────────────────────────────────────────

    [TestFixture]
    [Category("Tick")]
    public class TickAdvanceTests
    {
        // The tick loop is private and driven by Time.deltaTime.  Rather than
        // hijack Unity's clock, this fixture exercises the equivalent state
        // machine directly — a small helper that mirrors the production
        // accumulator + while-loop semantics.  When the production code
        // changes, this helper must change in lock-step or the test stops
        // protecting the invariant.

        private const float TickInterval     = 1f / 30f;
        private const int   MaxTicksPerFrame = 8;

        private static int Drain(ref float accumulator, float deltaTime)
        {
            accumulator += deltaTime;
            int ticks = 0;
            while (accumulator >= TickInterval && ticks < MaxTicksPerFrame)
            {
                accumulator -= TickInterval;
                ticks++;
            }
            if (accumulator >= TickInterval) accumulator = 0f;
            return ticks;
        }

        [Test]
        [Description("A 200 ms hitch advances every tick that became due (clamped at the " +
                     "MaxTicksPerFrame ceiling) — never the original single-step `if`.")]
        public void Drain_200msHitch_AdvancesAllOwedTicks()
        {
            float acc = 0f;
            int ticks = Drain(ref acc, 0.200f);

            // 200 ms / (1/30 s) = 6 ticks owed; under the cap.
            Assert.AreEqual(6, ticks);
        }

        [Test]
        [Description("A pause longer than MaxTicksPerFrame * tickInterval is clamped at the " +
                     "ceiling and surrenders the residual time so a 30 s pause does not " +
                     "produce a multi-second stutter on resume.")]
        public void Drain_30sPause_ClampedAtMaxTicksPerFrame()
        {
            float acc = 0f;
            int ticks = Drain(ref acc, 30f);
            Assert.AreEqual(MaxTicksPerFrame, ticks);
            Assert.Less(acc, TickInterval, "Residual must be drained below the interval.");
        }

        [Test]
        [Description("Nominal frame at 60 fps advances at most one tick.")]
        public void Drain_NormalFrame_AdvancesAtMostOneTick()
        {
            float acc = 0f;
            // 60 fps → 16.67 ms per frame; below 1/30 ≈ 33.33 ms so first
            // frame produces 0 ticks, second frame produces 1.
            Assert.AreEqual(0, Drain(ref acc, 1f / 60f));
            Assert.AreEqual(1, Drain(ref acc, 1f / 60f));
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // NetworkVariable timestamp gate
    // ────────────────────────────────────────────────────────────────────────

    [TestFixture]
    [Category("NetworkVariable")]
    public class NetworkVariableTimestampTests
    {
        private GameObject       _nmGo;
        private NetworkManager   _manager;
        private GameObject       _go;
        private NetworkBehaviourTickTestStub _nb;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("TestNetworkManager");
            _manager = _nmGo.AddComponent<NetworkManager>();
            _manager.SetLocalPlayerStringId("player-1");

            _go  = new GameObject("VarOwner");
            _nb  = _go.AddComponent<NetworkBehaviourTickTestStub>();
            _nb.Initialize(7UL, "player-1");
            _nb.SetSpawned(true);
        }

        [TearDown]
        public void TearDown()
        {
            if (_go   != null) { Object.DestroyImmediate(_go);   _go   = null; }
            if (_nmGo != null) { Object.DestroyImmediate(_nmGo); _nmGo = null; }
        }

        [Test]
        [Description("An out-of-order older NetworkVariable update is rejected; the value " +
                     "from the newer tick remains in effect.")]
        public void OutOfOrderUpdate_OlderTick_Rejected()
        {
            var v = new NetworkVariableInt(_nb, variableId: 0, initialValue: 0);

            // Apply tick 100 = 99
            ApplyInt(v, packetTick: 100u, value: 99);
            Assert.AreEqual(99, v.Value);

            // Apply older tick 50 with a different value — should be dropped.
            ApplyInt(v, packetTick: 50u, value: 1);
            Assert.AreEqual(99, v.Value);

            // Same tick 100 must also be rejected (strictly-greater rule).
            ApplyInt(v, packetTick: 100u, value: 7);
            Assert.AreEqual(99, v.Value);

            // Newer tick advances the gate.
            ApplyInt(v, packetTick: 101u, value: 11);
            Assert.AreEqual(11, v.Value);
        }

        [Test]
        [Description("Tick gate uses modular comparison so wrap is handled.")]
        public void TickGate_AcrossUintWrap_NewerWins()
        {
            var v = new NetworkVariableInt(_nb, variableId: 0, initialValue: 0);
            ApplyInt(v, packetTick: uint.MaxValue, value: 42);
            Assert.AreEqual(42, v.Value);

            // Post-wrap tick 1 must be accepted as newer.
            ApplyInt(v, packetTick: 1u, value: 7);
            Assert.AreEqual(7, v.Value);
        }

        private void ApplyInt(NetworkVariableInt v, uint packetTick, int value)
        {
            using var ms     = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(value);
            writer.Flush();
            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            _nb.ApplyVariableUpdate(v.VariableId, reader, valueLen: 4,
                                    packetTick: packetTick, hasPacketTick: true);
        }
    }

    /// <summary>
    /// Minimal NetworkBehaviour subclass for the timestamp tests; the stub
    /// exists because <see cref="NetworkBehaviour"/> itself is abstract in
    /// the runtime assembly.
    /// </summary>
    public class NetworkBehaviourTickTestStub : NetworkBehaviour { }

    // ────────────────────────────────────────────────────────────────────────
    // ReliableChannel — ARQ + dedup
    // ────────────────────────────────────────────────────────────────────────

    [TestFixture]
    [Category("Reliable")]
    public class ReliableChannelTests
    {
        [Test]
        [Description("Outbound register allocates monotonically increasing sequences and " +
                     "tracks the in-flight count.")]
        public void RegisterOutbound_AllocatesMonotonicSequences()
        {
            var ch = new ReliableChannel();
            Assert.IsTrue(ch.TryRegisterOutbound(new byte[]{1}, 0f, out uint s0));
            Assert.IsTrue(ch.TryRegisterOutbound(new byte[]{2}, 0f, out uint s1));
            Assert.AreEqual(0u, s0);
            Assert.AreEqual(1u, s1);
            Assert.AreEqual(2, ch.InFlightCount);
        }

        [Test]
        [Description("Tick retransmits any frame whose RTO has expired and applies " +
                     "exponential backoff to the next attempt.")]
        public void Tick_ExpiredRto_RetransmitsWithBackoff()
        {
            var ch = new ReliableChannel { InitialRtoSeconds = 0.1f, MaxRtoSeconds = 1f };
            ch.TryRegisterOutbound(new byte[]{42}, nowSeconds: 0f, out uint seq);

            var resends = new List<uint>();
            // Just under RTO — no retransmit.
            ch.Tick(0.05f, (s, p) => resends.Add(s));
            Assert.AreEqual(0, resends.Count);

            // Past RTO — first retransmit.
            ch.Tick(0.20f, (s, p) => resends.Add(s));
            Assert.AreEqual(1, resends.Count);
            Assert.AreEqual(seq, resends[0]);

            // Backoff doubled to 0.2 s; retransmit only fires after 0.4 s
            // total wall-clock (0.2 first send + 0.2 backoff).
            ch.Tick(0.30f, (s, p) => resends.Add(s));
            Assert.AreEqual(1, resends.Count);

            ch.Tick(0.45f, (s, p) => resends.Add(s));
            Assert.AreEqual(2, resends.Count);
        }

        [Test]
        [Description("AcknowledgeUpTo drains every entry whose sequence is <= acked.")]
        public void AcknowledgeUpTo_ClearsCumulative()
        {
            var ch = new ReliableChannel();
            ch.TryRegisterOutbound(new byte[]{1}, 0f, out _);
            ch.TryRegisterOutbound(new byte[]{2}, 0f, out _);
            ch.TryRegisterOutbound(new byte[]{3}, 0f, out _);

            int cleared = ch.AcknowledgeUpTo(1u);
            Assert.AreEqual(2, cleared);
            Assert.AreEqual(1, ch.InFlightCount);
        }

        [Test]
        [Description("Inbound dedup window accepts the first occurrence and rejects every " +
                     "subsequent re-arrival, regardless of arrival order.")]
        public void Inbound_OutOfOrderThenDuplicate_DeliversOnce()
        {
            var ch = new ReliableChannel();
            // Out-of-order arrivals: 5, 3, 4, 7, 5(dup), 3(dup).
            Assert.IsTrue (ch.TryAcceptInbound(5u));
            Assert.IsTrue (ch.TryAcceptInbound(3u));
            Assert.IsTrue (ch.TryAcceptInbound(4u));
            Assert.IsTrue (ch.TryAcceptInbound(7u));
            Assert.IsFalse(ch.TryAcceptInbound(5u));
            Assert.IsFalse(ch.TryAcceptInbound(3u));
            Assert.IsFalse(ch.TryAcceptInbound(7u));
        }

        [Test]
        [Description("A sequence that falls below the dedup window is rejected as stale.")]
        public void Inbound_FarBelowWindow_Rejected()
        {
            var ch = new ReliableChannel();
            ch.TryAcceptInbound(0u);
            ch.TryAcceptInbound(2000u);  // window slides forward
            // 0 is now far below the trailing edge (window size = 1024).
            Assert.IsFalse(ch.TryAcceptInbound(0u));
        }

        [Test]
        [Description("Saturated in-flight table refuses new registrations until an ACK drains it.")]
        public void RegisterOutbound_Saturated_ReturnsFalseUntilAcked()
        {
            var ch = new ReliableChannel();
            for (int i = 0; i < ReliableChannel.MaxInFlight; i++)
                Assert.IsTrue(ch.TryRegisterOutbound(new byte[]{(byte)i}, 0f, out _));

            Assert.IsFalse(ch.TryRegisterOutbound(new byte[]{0}, 0f, out _));

            ch.AcknowledgeUpTo(0u);
            Assert.IsTrue(ch.TryRegisterOutbound(new byte[]{0}, 0f, out _));
        }

        [Test]
        [Description("After MaxAttempts retransmits the entry is dropped and the dropped " +
                     "callback is invoked with its sequence.")]
        public void Tick_ExhaustedAttempts_DropsAndNotifies()
        {
            var ch = new ReliableChannel
            {
                InitialRtoSeconds = 0.01f,
                MaxRtoSeconds     = 0.01f,
                MaxAttempts       = 3,
            };
            ch.TryRegisterOutbound(new byte[]{1}, 0f, out uint seq);

            var dropped = new List<uint>();
            // Drive the clock forward in big steps so the RTO timer expires
            // every Tick.  Register established attempts=1 (initial send by
            // caller); each subsequent Tick either retransmits or drops.
            ch.Tick(0.10f, (_, __) => { }, dropped.Add);  // attempts: 1 → 2 (retransmit)
            ch.Tick(0.20f, (_, __) => { }, dropped.Add);  // attempts: 2 → 3 (retransmit)
            ch.Tick(0.30f, (_, __) => { }, dropped.Add);  // 3 >= MaxAttempts → drop

            Assert.AreEqual(1, dropped.Count);
            Assert.AreEqual(seq, dropped[0]);
            Assert.AreEqual(0, ch.InFlightCount);
        }
    }
}
