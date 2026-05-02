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

using System;
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

        private readonly Action<byte[]> _sendBatch;        // → NetworkManager.SendVariableBatchUpdate
        private readonly Action<byte[]> _sendSingleFallback; // → NetworkManager.SendVariableUpdate (used on builder failure)

        // Cached delegate for the batch collector. Method-group conversion
        // would allocate a new closure on every CollectIntoBatch read; one
        // allocation amortised across the lifetime of the manager keeps the
        // hot flush path allocation-free.
        private Action<byte[]> _collectorCache;

        public VariableBatchManager(Action<byte[]> sendBatch, Action<byte[]> sendSingleFallback)
        {
            _sendBatch         = sendBatch         ?? throw new ArgumentNullException(nameof(sendBatch));
            _sendSingleFallback = sendSingleFallback ?? throw new ArgumentNullException(nameof(sendSingleFallback));
        }

        /// <summary>True iff <see cref="CollectIntoBatch"/> has stashed at least one entry awaiting flush.</summary>
        public bool HasPending => _pending.Count > 0;

        /// <summary>
        /// Apply the per-tick cap (clamped externally to [1, MaxEntries]).
        /// Must be set before <see cref="CollectIntoBatch"/> is used; the
        /// collector consults the cap to decide when to eagerly flush.
        /// </summary>
        public void SetActiveCap(int cap) => _activeCap = cap;

        /// <summary>
        /// The cached <see cref="Action{Byte[]}"/> form of <see cref="CollectIntoBatch"/>.
        /// Hot-path: returned once and reused on every tick to keep
        /// FlushDirtyVariables's per-NetworkBehaviour callback allocation-free.
        /// </summary>
        public Action<byte[]> Collector => _collectorCache ??= CollectIntoBatch;

        private void CollectIntoBatch(byte[] payload)
        {
            if (payload == null || payload.Length == 0) return;
            _pending.Add(payload);
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

            byte[] batchPayload;
            try
            {
                batchPayload = VariableBatchBuilder.Build(_scratch, count);
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] VariableBatchBuilder.Build threw {ex.GetType().Name}: {ex.Message}. " +
                    "Falling back to per-object VariableUpdate packets for this batch.");
                for (int i = 0; i < count; i++) _sendSingleFallback(_scratch[i]);
                return;
            }

            _sendBatch(batchPayload);
        }
    }
}
