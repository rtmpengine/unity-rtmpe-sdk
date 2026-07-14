// RTMPE SDK — Runtime/Core/Sync/VariableBatchManager.cs
//
// Per-tick accumulator + batched-emitter for NetworkVariable updates.
//
// Why this is its own class:
//   FlushDirtyNetworkVariables() walks every owned, spawned NetworkBehaviour
//   on each tick and calls FlushDirtyVariables(sender) — when batching is
//   enabled, the sender callback diverts each per-object payload into the
//   batch instead of emitting it immediately. Bundling the diversion logic
//   into one class makes the cap-eager-flush invariant ("never let a batch
//   exceed _activeCap entries within a tick") testable in isolation, and
//   isolates the per-instance buffer reuse pattern from the Update loop.
//
// Threading:
//   Main-thread only. Update() runs on the Unity main thread and is the
//   only path that reaches CollectIntoBatch / FlushPending. The internal
//   buffers (_pending, _scratch) are not synchronised; they assume the
//   single-thread invariant of the Update path.
//
// GC Round 2 (2026-05-02):
//   • Collector signature is now Action<byte[], int> so callers can pass a
//     pooled / cached buffer that is larger than the logical payload —
//     CollectIntoBatch reads only the leading `length` bytes.
//   • Pending entries are still per-payload byte[] allocations because they
//     must persist across CollectIntoBatch calls until FlushPending sends
//     the combined batch.  This per-entry allocation is the same cost the
//     pre-Round-2 ms.ToArray() path paid; the win is on the non-batching
//     path which now bypasses the copy entirely (NetworkBehaviour hands
//     ms.GetBuffer() + length straight to SendVariableUpdate).
//   • The combined batch byte[] (built by VariableBatchBuilder) is rented
//     from ArrayPool<byte>.Shared and returned after SendVariableBatchUpdate
//     wraps it.  Eliminates the per-tick batch allocation that scaled with
//     pending count.

using System;
using System.Buffers;
using System.Collections.Generic;

using UnityEngine;

using RTMPE.Sync;

namespace RTMPE.Core.Sync
{
    internal sealed class VariableBatchManager
    {
        private readonly List<byte[]> _pending = new List<byte[]>(32);

        // Reusable scratch sized to the active cap. Resized lazily when the
        // configured cap grows; never shrunk because shrinking would create
        // allocation pressure for a setting that is only ever raised as
        // the project's variable count grows.
        private byte[][] _scratch = new byte[32][];

        private int _activeCap;

        // Maximum wire size, in bytes, of a single batch payload — the datagram
        // application-payload ceiling the PacketBuilder enforces.  The
        // accumulator flushes before a pending batch would cross this, so a
        // batch is never built larger than one datagram and then rejected at
        // the send boundary.  Zero disables byte-aware splitting (entry-count
        // cap only), preserving the legacy behaviour for callers that do not
        // supply a budget.
        private readonly int _byteBudget;

        // Running wire size of the batch currently accumulating in _pending,
        // including the leading count byte; zero whenever _pending is empty.
        private int _pendingBytes;

        private readonly Action<byte[], int> _sendBatch;        // → NetworkManager.SendVariableBatchUpdate(byte[], int)
        private readonly Action<byte[], int> _sendSingleFallback; // → NetworkManager.SendVariableUpdate(byte[], int) (used on builder failure)

        // Cached delegate for the batch collector. Method-group conversion
        // would allocate a new closure on every CollectIntoBatch read; one
        // allocation amortised across the lifetime of the manager keeps the
        // hot flush path allocation-free.
        private Action<byte[], int> _collectorCache;

        public VariableBatchManager(
            Action<byte[], int> sendBatch,
            Action<byte[], int> sendSingleFallback,
            int byteBudget)
        {
            _sendBatch          = sendBatch          ?? throw new ArgumentNullException(nameof(sendBatch));
            _sendSingleFallback = sendSingleFallback ?? throw new ArgumentNullException(nameof(sendSingleFallback));
            _byteBudget         = byteBudget > 0 ? byteBudget : 0;
        }

        /// <summary>True iff <see cref="CollectIntoBatch"/> has stashed at least one entry awaiting flush.</summary>
        public bool HasPending => _pending.Count > 0;

        /// <summary>
        /// Apply the per-tick cap (clamped externally by
        /// <see cref="RTMPE.Sync.VariableBatchBuilder.ClampBatchCap"/> to
        /// [1, GatewayEntryCap] so an over-cap batch the gateway would drop is
        /// never produced).  Must be set before <see cref="CollectIntoBatch"/>
        /// is used; the collector consults the cap to decide when to eagerly
        /// flush.
        /// </summary>
        public void SetActiveCap(int cap) => _activeCap = cap;

        /// <summary>
        /// The cached <see cref="Action{Byte[], Int32}"/> form of <see cref="CollectIntoBatch"/>.
        /// Hot-path: returned once and reused on every tick to keep
        /// FlushDirtyVariables's per-NetworkBehaviour callback allocation-free.
        /// </summary>
        public Action<byte[], int> Collector => _collectorCache ??= CollectIntoBatch;

        private void CollectIntoBatch(byte[] payload, int length)
        {
            if (payload == null || length <= 0) return;

            // Flush the current batch before this entry would push its wire
            // size past one datagram, so a batch splits on cumulative byte size
            // as well as on entry count and is never built larger than the MTU.
            // Guarded on a non-empty pending set: a lone entry that exceeds the
            // budget on its own cannot be split further and is contained by the
            // send guard, not an endless flush loop.
            int entryWire = VariableBatchBuilder.EntryWireSize(length);
            if (_byteBudget > 0 && _pending.Count > 0
                && _pendingBytes + entryWire > _byteBudget)
            {
                FlushPending();
            }

            // The pending entry must persist across CollectIntoBatch calls
            // until FlushPending fires.  The caller's buffer (NetworkBehaviour's
            // cached _flushMs backing array, or another pooled buffer) will
            // be reused on the next tick, so we MUST copy out into our own
            // exact-sized byte[] here.  This per-payload allocation is
            // unavoidable at the batching boundary without a deeper redesign
            // (e.g. a per-tick parallel-lengths array + pooled mega-buffer);
            // it matches the cost of the pre-Round-2 ms.ToArray() call, so
            // batching is no worse than before, and non-batching is cheaper.
            var entry = new byte[length];
            Buffer.BlockCopy(payload, 0, entry, 0, length);

            if (_pending.Count == 0) _pendingBytes = VariableBatchBuilder.BatchHeaderBytes;
            _pending.Add(entry);
            _pendingBytes += entryWire;

            if (_pending.Count >= _activeCap)
            {
                FlushPending();
            }
        }

        /// <summary>
        /// Drop every pending payload without sending. Used at session
        /// boundary (ClearSessionData) so a reconnect does not flush
        /// stale variable updates onto the new session's nonce stream
        /// (the receiver's PacketBuilder counter restarts at zero).
        /// </summary>
        public void Clear()
        {
            _pending.Clear();
            _pendingBytes = 0;
            // _scratch is left alone — it is reused in place; the trailing
            // slots are always cleared at the start of FlushPending.
        }

        /// <summary>
        /// Encode every pending payload into a single VariableBatchUpdate
        /// packet and dispatch via the batch sender.  No-op when no payload
        /// is pending.  On builder exception, falls back to per-object
        /// VariableUpdate packets so a single corrupt payload cannot stall
        /// the entire frame's variable updates.
        /// </summary>
        public void FlushPending()
        {
            int count = _pending.Count;
            if (count == 0) return;
            if (_scratch.Length < count)
            {
                _scratch = new byte[count][];
            }
            for (int i = 0; i < count; i++) _scratch[i] = _pending[i];
            // Null the trailing slots so the array does not keep stale
            // references alive across the next batch.
            for (int i = count; i < _scratch.Length; i++) _scratch[i] = null;
            _pending.Clear();
            _pendingBytes = 0;

            // GC Round 2 (2026-05-02): rent the batch byte[] from ArrayPool
            // and use the BuildInto overload to write the wire bytes
            // directly into the rented buffer.  ComputeTotalSize gives the
            // exact size; ArrayPool.Rent may return a buffer larger than
            // the requested size, so we explicitly pass `total` to
            // SendVariableBatchUpdate(byte[], int) so the wire frame's
            // payload_len matches the bytes we actually wrote.
            int total;
            try
            {
                total = VariableBatchBuilder.ComputeTotalSize(_scratch, count);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] VariableBatchBuilder.ComputeTotalSize threw {ex.GetType().Name}: {ex.Message}. " +
                    "Falling back to per-object VariableUpdate packets for this batch.");
                for (int i = 0; i < count; i++) SafeSendSingle(_scratch[i], _scratch[i].Length);
                return;
            }

            var pool   = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(total);
            try
            {
                int written;
                try
                {
                    written = VariableBatchBuilder.BuildInto(buffer, 0, _scratch, count);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] VariableBatchBuilder.BuildInto threw {ex.GetType().Name}: {ex.Message}. " +
                        "Falling back to per-object VariableUpdate packets for this batch.");
                    for (int i = 0; i < count; i++) SafeSendSingle(_scratch[i], _scratch[i].Length);
                    return;
                }

                SafeSendBatch(buffer, written);
            }
            finally
            {
                pool.Return(buffer);
            }
        }

        // FlushPending must never let a send or builder exception propagate
        // into the per-tick Update loop: the dirtied variables are MarkClean()'d
        // before they reach the batcher, so an escaping throw would both abort
        // the remainder of the frame and lose the update with no retry.  These
        // wrappers keep a single un-sendable payload — e.g. one object whose
        // own update exceeds a datagram even after byte-aware splitting —
        // contained to a logged drop rather than a frame-wide abort.
        private void SafeSendBatch(byte[] buffer, int written)
        {
            try
            {
                _sendBatch(buffer, written);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] VariableBatch send failed ({ex.GetType().Name}): {ex.Message}. " +
                    "Batch dropped this tick.");
            }
        }

        private void SafeSendSingle(byte[] payload, int length)
        {
            try
            {
                _sendSingleFallback(payload, length);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] Variable update send failed ({ex.GetType().Name}): {ex.Message}. " +
                    "Update dropped this tick.");
            }
        }
    }
}
