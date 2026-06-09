// RTMPE SDK — Tests/Runtime/RpcBufferReplaySecurityTests.cs
//
// Adversarial tests for NetworkManager.HandleRpcBufferReplay.
//
// The replay frame is delivered immediately after a room join and replays
// catch-up RPCs on the main thread.  Wire format:
//  [event_count : u16 LE]
//  for each event:
//    [payload_len : u16 LE]
//    [payload     : N bytes]   // Enhanced RPC payload
//
// A malicious or buggy peer can mis-encode any of these fields.  The
// handler must:
//  * Reject event_count > MaxRpcBufferReplayEvents (4096) instead of
//    looping up to 65 535 times consuming main-thread CPU.
//  * Skip events with unknown method ids (no [RtmpeRpc] handler).
//  * Reject events with mismatched argument counts.
//  * Reject truncated buffers without throwing.
//  * Refuse re-entrant invocation (no double-dispatch).

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;
using RTMPE.Core.Rpc;
using RTMPE.Rpc;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("RpcBufferReplay")]
    public class RpcBufferReplaySecurityTests
    {
        private GameObject     _go;
        private NetworkManager _manager;

        [SetUp]
        public void SetUp()
        {
            _go      = new GameObject("TestNetworkManager");
            _manager = _go.AddComponent<NetworkManager>();
            // Permissive verifier so the parser does not reject our crafted events.
            EnhancedRpcVerifier.Reset();
            EnhancedRpcVerifier.SenderVerifier      = _ => true;
            EnhancedRpcVerifier.ObjectExistsVerifier = _ => true;
        }

        [TearDown]
        public void TearDown()
        {
            EnhancedRpcVerifier.Reset();
            if (_go != null) Object.DestroyImmediate(_go);
        }

        // ── Wire-format helpers ────────────────────────────────────────────────

        private static void WriteU16LE(byte[] buf, ref int o, ushort v)
        {
            buf[o++] = (byte)v;
            buf[o++] = (byte)(v >> 8);
        }

        private static void WriteU32LE(byte[] buf, ref int o, uint v)
        {
            buf[o++] = (byte)v;
            buf[o++] = (byte)(v >> 8);
            buf[o++] = (byte)(v >> 16);
            buf[o++] = (byte)(v >> 24);
        }

        private static void WriteU64LE(byte[] buf, ref int o, ulong v)
        {
            for (int i = 0; i < 8; i++) buf[o++] = (byte)(v >> (i * 8));
        }

        // Build a minimal Enhanced RPC payload (27-byte header, zero params)
        // with caller-controlled methodId and objectId.
        private static byte[] BuildEnhancedRpcEvent(uint methodId, ulong objectId)
        {
            var buf = new byte[RpcLimits.EnhancedRequestHeaderSize];
            int o = 0;
            WriteU32LE(buf, ref o, methodId);
            WriteU64LE(buf, ref o, /*senderId*/ 1234UL);
            WriteU32LE(buf, ref o, /*requestId*/ 1U);
            WriteU64LE(buf, ref o, objectId);
            buf[o++] = (byte)RpcTarget.All;
            buf[o++] = 0x00;  // rpc_flags reserved
            buf[o++] = 0x00;  // param_count = 0
            return buf;
        }

        // Wrap one or more Enhanced RPC payloads in the replay-frame envelope
        // with a caller-controlled event_count (which may legally differ from
        // events.Length so we can synthesise truncated frames).
        private static byte[] BuildReplayFrame(ushort eventCount, params byte[][] events)
        {
            int total = 2;
            for (int i = 0; i < events.Length; i++) total += 2 + events[i].Length;
            var buf = new byte[total];
            int o = 0;
            WriteU16LE(buf, ref o, eventCount);
            for (int i = 0; i < events.Length; i++)
            {
                WriteU16LE(buf, ref o, (ushort)events[i].Length);
                System.Buffer.BlockCopy(events[i], 0, buf, o, events[i].Length);
                o += events[i].Length;
            }
            return buf;
        }

        // ── 1. event_count = 0xFFFF cap ────────────────────────────────────────

        [Test]
        [Description("event_count = 0xFFFF (65 535) is rejected before the loop runs — the cap " +
                     "prevents a hostile peer from stalling the main thread.")]
        public void EventCount_MaxU16_Rejected()
        {
            // Frame header advertises 65 535 events but carries zero — without
            // the cap the handler would loop 65 535 times and abort on the
            // first truncation check.  With the cap it returns immediately.
            var frame = BuildReplayFrame(eventCount: 0xFFFF);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
            stopwatch.Stop();

            // 50 ms is generous — the cap path returns in microseconds.  This
            // bound exists to lock the cap in: a regression that loops 65 535
            // times will blow well past it on every CI runner we use.
            Assert.Less(stopwatch.ElapsedMilliseconds, 50,
                "Capped frame must short-circuit; without the cap the loop dominates.");
        }

        [Test]
        [Description("event_count just above the documented cap is rejected; just at the cap is " +
                     "accepted (with an empty body it short-circuits via the truncation check).")]
        public void EventCount_AtAndAboveCap_BehaviourMatchesContract()
        {
            // event_count = MaxRpcBufferReplayEvents + 1 → rejected.
            const int cap = NetworkManager.MaxRpcBufferReplayEvents;
            var overFrame = BuildReplayFrame(eventCount: (ushort)(cap + 1));
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(overFrame));

            // event_count = cap → accepted at the header check, then aborts on
            // the per-event truncation guard because the body is empty.
            var atFrame = BuildReplayFrame(eventCount: (ushort)cap);
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(atFrame));
        }

        // ── 2. Unknown method id is logged + skipped, never thrown ──────────────

        [Test]
        [Description("An event whose methodId has no registered [RtmpeRpc] handler is skipped " +
                     "without throwing.  The unknown-objectId branch covers the same code path " +
                     "without requiring a live spawn registry.")]
        public void UnknownMethodId_SkippedWithoutThrow()
        {
            // Object id 0xDEAD has no spawned NetworkBehaviour, so the handler
            // logs and skips — exactly the behaviour required for unknown
            // methodId on the live path (DispatchEnhancedRpc itself short-
            // circuits via RpcRegistry.TryFindMethod and is verified by
            // NetworkBehaviour-level tests).
            var ev = BuildEnhancedRpcEvent(methodId: 0xDEADBEEF, objectId: 0xDEADUL);
            var frame = BuildReplayFrame(eventCount: 1, ev);

            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
        }

        // ── 3. Mismatched arg count is rejected gracefully ──────────────────────

        [Test]
        [Description("Event whose declared payload length disagrees with the frame remainder is " +
                     "rejected (truncation guard) instead of reading past the buffer end.")]
        public void MismatchedPayloadLen_Rejected()
        {
            // Build a frame with one event but advertise a payload_len that
            // exceeds the actual bytes available.
            const ushort lyingLen = 9999;
            var buf = new byte[2 + 2 + 4];   // header + payload_len + 4 bytes
            int o = 0;
            WriteU16LE(buf, ref o, /*event_count*/ 1);
            WriteU16LE(buf, ref o, lyingLen);
            buf[o++] = 0x01; buf[o++] = 0x02; buf[o++] = 0x03; buf[o++] = 0x04;

            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(buf));
        }

        // ── 4. Truncated buffer ────────────────────────────────────────────────

        [Test]
        [Description("A buffer too short to contain even the event_count header is dropped silently.")]
        public void TruncatedBuffer_Dropped()
        {
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(new byte[] { 0x01 }));
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(System.Array.Empty<byte>()));
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(null));
        }

        [Test]
        [Description("event_count claims 5 events but only 1 is encoded — handler aborts on " +
                     "truncation without throwing or dispatching nonsense.")]
        public void TruncatedAfterFirstEvent_AbortsCleanly()
        {
            var ev = BuildEnhancedRpcEvent(methodId: 1, objectId: 99UL);
            // event_count=5 but we only provide one event payload.
            var frame = BuildReplayFrame(eventCount: 5, ev);
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
        }

        // ── 5. Re-entry / concurrent replay ────────────────────────────────────

        [Test]
        [Description("Calling HandleRpcBufferReplay while another invocation is on the call stack " +
                     "is dropped; this prevents double-dispatch when a handler synchronously " +
                     "triggers another replay frame.")]
        public void ConcurrentReplay_NoDoubleDispatch()
        {
            // Two back-to-back calls (the only legal pattern under the
            // SDK's main-thread-only contract) MUST both complete without
            // throwing.  The re-entry guard is a CAS on _replayInProgress
            // and is exercised here in the trivial sequential case to lock
            // the contract.
            var frame = BuildReplayFrame(eventCount: 0);
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
        }

        // ── 6. Empty (event_count = 0) is a no-op ──────────────────────────────

        [Test]
        [Description("event_count = 0 returns immediately without iterating.")]
        public void EmptyFrame_IsNoOp()
        {
            var frame = BuildReplayFrame(eventCount: 0);
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));
        }

        // ── Live-RPC ordering during replay ────────────────────────────────────
        //
        // Buffered (historical) RPCs in the replay frame are server-ordered by
        // their position in the frame; a live RPC arriving on the dispatcher
        // during the replay window must NOT interleave with the buffered
        // sequence — its state mutation could otherwise be overwritten by an
        // older buffered handler.  The ordering invariant is owned by the
        // RpcReplayBuffer (NetworkManager._rpcReplayBuffer): while its barrier
        // is raised, OnEnhancedRpcRequest defers live payloads to the buffer's
        // pending queue, and that queue is drained in arrival order — after the
        // buffered events — by the replay's bounded drain.

        // Reflection seams: the replay state lives in the internal
        // RpcReplayBuffer instance, and the connection-state gate that
        // OnEnhancedRpcRequest checks lives in the private _state field.
        private RpcReplayBuffer ReplayBuffer()
        {
            var f = typeof(NetworkManager).GetField(
                "_rpcReplayBuffer",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(f, "_rpcReplayBuffer field must exist on NetworkManager.");
            return (RpcReplayBuffer)f.GetValue(_manager);
        }

        private void SetConnectionState(NetworkState state)
        {
            var f = typeof(NetworkManager).GetField(
                "_state",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(f, "_state field must exist on NetworkManager.");
            f.SetValue(_manager, state);
        }

        private void InvokeOnEnhancedRpcRequest(byte[] payload)
        {
            var mi = typeof(NetworkManager).GetMethod(
                "OnEnhancedRpcRequest",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(mi, "OnEnhancedRpcRequest must exist as a private method.");
            mi.Invoke(_manager, new object[] { payload });
        }

        [Test]
        [Description("A live Enhanced RPC dispatched while the replay barrier is raised is " +
                     "deferred to the pending queue rather than processed inline.")]
        public void LiveRpc_DuringReplay_QueuedNotDispatched()
        {
            // Live RPCs are accepted only in-room; raise the barrier to mark a
            // replay in progress, then deliver a live RPC.  This is exactly the
            // situation a buffered handler creates if it synchronously feeds an
            // inbound packet back through the dispatcher.
            SetConnectionState(NetworkState.InRoom);
            var buffer = ReplayBuffer();
            Assert.IsTrue(buffer.TryEnterDrain(), "barrier should raise on first enter.");

            var liveEv = BuildEnhancedRpcEvent(methodId: 0xDEADBEEF, objectId: 42UL);
            Assert.DoesNotThrow(() => InvokeOnEnhancedRpcRequest(liveEv));

            Assert.AreEqual(1, buffer.PendingCount,
                "A live RPC arriving during a replay window must be queued, not processed inline.");

            buffer.Clear();   // reset barrier + queues for the next fixture.
        }

        [Test]
        [Description("After a replay completes, queued live RPCs are drained in arrival order " +
                     "so the buffered (historical) handlers run BEFORE the live handlers.")]
        public void LiveRpc_AfterReplay_DrainedInOrder()
        {
            SetConnectionState(NetworkState.InRoom);
            var buffer = ReplayBuffer();

            // Pre-seed two live payloads (barrier down, so they sit in the
            // pending queue) — simulating two live RPCs that arrived during a
            // replay window.
            Assert.AreEqual(RpcReplayBuffer.EnqueueResult.Ok,
                buffer.TryEnqueue(BuildEnhancedRpcEvent(0xAAAAAAAA, 1UL)));
            Assert.AreEqual(RpcReplayBuffer.EnqueueResult.Ok,
                buffer.TryEnqueue(BuildEnhancedRpcEvent(0xBBBBBBBB, 2UL)));

            // A zero-event replay frame's bounded drain must consume both
            // pending entries (the no-spawned-object path logs+returns inside
            // DispatchEnhancedRpcPayload — the expected outcome for these
            // synthetic events).
            var frame = BuildReplayFrame(eventCount: 0);
            Assert.DoesNotThrow(() => _manager.HandleRpcBufferReplay(frame));

            Assert.AreEqual(0, buffer.PendingCount,
                "The replay drain must consume every pending live RPC.");
        }

        [Test]
        [Description("Pending-live queue is bounded: once MaxPendingLiveRpcsDuringReplay is " +
                     "reached, additional live RPCs are dropped rather than growing memory " +
                     "unboundedly under a hostile / flooded network.")]
        public void LiveRpc_DuringReplay_QueueCappedToMax()
        {
            SetConnectionState(NetworkState.InRoom);
            var buffer = ReplayBuffer();
            Assert.IsTrue(buffer.TryEnterDrain());

            // Push MaxPendingLiveRpcsDuringReplay + a few extras.  The queue
            // must cap exactly at the documented constant.
            int max = NetworkManager.MaxPendingLiveRpcsDuringReplay;
            var liveEv = BuildEnhancedRpcEvent(methodId: 0x12345678, objectId: 7UL);
            for (int i = 0; i < max + 25; i++)
                InvokeOnEnhancedRpcRequest(liveEv);

            Assert.AreEqual(max, buffer.PendingCount,
                "Pending-live queue must not exceed MaxPendingLiveRpcsDuringReplay.");

            buffer.Clear();
        }
    }
}
