// RTMPE SDK — Tests/Runtime/Performance/PerformanceBaselineTests.cs
//
// Baseline performance measurements for the RTMPE SDK hot paths, using the
// Unity.PerformanceTesting framework (com.unity.test-framework.performance).
//
// Unlike the NUnit-based MemoryAllocationTests — which make *allocation-free*
// assertions — these tests emit timing / byte-count samples so that the Unity
// performance database can track regressions over release history.  They do
// NOT fail on absolute thresholds: drift is visualised in the performance
// dashboard rather than gated via assertions, because CI hardware timing is
// noisy and absolute numbers are of low value.  CI-gating thresholds should
// be added in a follow-up once we have baseline data from at least 5 runs on
// the same runner hardware.
//
// Activation: the ENTIRE assembly is gated at the asmdef level by
// `defineConstraints: ["RTMPE_PERFORMANCE_TESTING"]`.  The define itself is
// emitted by `versionDefines` when the consuming project has
// `com.unity.test-framework.performance >= 3.0.0` installed.  If the
// performance framework is absent the assembly simply does not compile —
// the reference to `Unity.PerformanceTesting` is never resolved and
// the base SDK package retains its zero-runtime-dependency guarantee.
// No `#if RTMPE_PERFORMANCE_TESTING` source-level guard is needed because
// the asmdef-level gate is authoritative.

using System;
using System.IO;
using NUnit.Framework;
using Unity.PerformanceTesting;
using RTMPE.Protocol;
using RTMPE.Sync;

namespace RTMPE.Tests.Performance
{
    /// <summary>
    /// Baseline throughput + allocation measurements for the wire-protocol
    /// path.  Each method follows the Unity.PerformanceTesting idiom:
    ///
    ///   Measure.Method(() => { /* hot-path call */ })
    ///       .WarmupCount(N)
    ///       .MeasurementCount(M)
    ///       .Run();
    ///
    /// Iteration counts are chosen to bring each measurement batch above the
    /// scheduler quantum (~1 ms on modern CPUs) so GC jitter + cache-miss
    /// variance have a chance to reveal regressions.
    /// </summary>
    [TestFixture]
    [Category("Performance")]
    public class PerformanceBaselineTests
    {
        // Shared fixtures that a benchmark must NOT recreate per measurement
        // pass (we want to isolate the hot-path cost, not object construction).
        private PacketBuilder _builder;
        private byte[] _payload256;
        private byte[] _payload1024;
        private byte[] _heartbeatPacket;

        [SetUp]
        public void SetUp()
        {
            _builder = new PacketBuilder();

            _payload256 = new byte[256];
            for (int i = 0; i < _payload256.Length; i++) _payload256[i] = (byte)(i & 0xFF);

            _payload1024 = new byte[1024];
            for (int i = 0; i < _payload1024.Length; i++) _payload1024[i] = (byte)(i & 0xFF);

            // Build the heartbeat sample on a SEPARATE builder so the
            // per-test `_builder` starts its sequence counter at 0 — this
            // keeps the Build_Heartbeat benchmark honest (no pre-warmed
            // counter state from a prior call inside SetUp).
            _heartbeatPacket = new PacketBuilder().BuildHeartbeat();
        }

        // ── Packet building ─────────────────────────────────────────────────

        [Test, Performance]
        public void Perf_Build_Heartbeat_NoPayload()
        {
            Measure.Method(() => _builder.BuildHeartbeat())
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(5000)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Perf_Build_Data_256B()
        {
            Measure.Method(() => _builder.BuildData(_payload256))
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(2000)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Perf_Build_Data_1KB()
        {
            Measure.Method(() => _builder.BuildData(_payload1024))
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(1000)
                .GC()
                .Run();
        }

        // ── Packet parsing ──────────────────────────────────────────────────

        [Test, Performance]
        public void Perf_ExtractPayload_Heartbeat()
        {
            Measure.Method(() =>
                {
                    var p = PacketParser.ExtractPayload(_heartbeatPacket);
                    // Guard the compiler against dead-code elimination of the
                    // parse call (the returned array is observed — the call
                    // site therefore cannot be removed).
                    if (p == null) throw new InvalidOperationException("unreachable");
                })
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(5000)
                .GC()
                .Run();
        }

        // ── NetworkVariable hot path (covers the ArrayPool adoption M-5) ────

        [Test, Performance]
        public void Perf_NetworkVariable_SerializeWithId_Int()
        {
            var v = new NetworkVariableInt(null, variableId: 1, defaultValue: 1234);
            // `using` ensures BinaryWriter / MemoryStream are deterministically
            // disposed when the method exits.  Without it, BinaryWriter's
            // finalizer would eventually call Flush() against a Stream that
            // may already be garbage — surfacing a finalizer-thread exception
            // in the test log.  Capacity 32 is generous for the 8-byte
            // framed int payload; no per-iteration resize.
            using var ms = new MemoryStream(capacity: 32);
            using var bw = new BinaryWriter(ms);

            Measure.Method(() =>
                {
                    ms.Position = 0;
                    v.SerializeWithId(bw);
                })
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(5000)
                .GC()
                .Run();
        }

        [Test, Performance]
        public void Perf_NetworkVariable_SerializeWithId_String()
        {
            var v = new NetworkVariableString(null, variableId: 2, defaultValue: "baseline-sample");
            using var ms = new MemoryStream(capacity: 64);
            using var bw = new BinaryWriter(ms);

            Measure.Method(() =>
                {
                    ms.Position = 0;
                    v.SerializeWithId(bw);
                })
                .WarmupCount(10)
                .MeasurementCount(20)
                .IterationsPerMeasurement(3000)
                .GC()
                .Run();
        }
    }
}
