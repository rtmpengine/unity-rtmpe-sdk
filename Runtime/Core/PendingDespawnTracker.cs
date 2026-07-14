// RTMPE SDK — Runtime/Core/PendingDespawnTracker.cs
//
// Bookkeeping for out-of-order Despawn-before-Spawn on UDP transport.
//
// UDP reorder can deliver Despawn(id) before Spawn(id) for the same object id
// when the gateway uses a different relay path for each (rare but real on
// roaming mobile networks).  Without tracking, the late Spawn would create a
// "ghost" object that should never have existed in the local view.  This
// tracker remembers each Despawn whose target is not yet known under a short
// TTL; the matching Spawn arriving inside that window is treated as
// already-despawned and dropped.
//
// Three coupled structures give O(1) operations on every axis:
//
//   • _expiryByObjectId : map id → absolute expiry timestamp.
//   • _order            : LinkedList preserving insertion order for FIFO
//                         eviction when the cap is reached.
//   • _nodes            : map id → LinkedListNode so normal consumption
//                         can unlink the order entry in O(1).
//
// All three move together under every state transition, so the order list
// can never accumulate ghost ids whose dictionary entry was already
// consumed by a different code path.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Tracks Despawn frames that arrived ahead of their matching Spawn.
    /// Single-thread; the surrounding <see cref="SpawnManager"/> guarantees
    /// every entry point runs on the Unity main thread.
    /// </summary>
    internal sealed class PendingDespawnTracker
    {
        // Five seconds is generous w.r.t. typical reorder windows (under
        // 200 ms even on poor links) and bounds the entry's memory cost for
        // a flooding attacker.
        public const long TtlMs = 5_000;

        // Capping at 1024 keeps the dictionary's footprint negligible
        // (~40 KB worst case) while still admitting every plausible volume of
        // UDP-reordered despawns on a real link.
        public const int MaxEntries = 1024;

        // Hysteresis threshold for re-arming the cap-warning latch.  When
        // the live count falls back below this watermark, the next
        // saturation episode is treated as a fresh event and emits a new
        // operator warning.  Half of MaxEntries gives generous headroom so
        // a flapping count near saturation does not produce log spam, but
        // a meaningful drainage between distinct flood episodes does
        // re-enable the diagnostic.
        private const int CapWarnRearmThreshold = MaxEntries / 2;

        private readonly Dictionary<ulong, long> _expiryByObjectId =
            new Dictionary<ulong, long>(16);
        private readonly LinkedList<ulong> _order =
            new LinkedList<ulong>();
        private readonly Dictionary<ulong, LinkedListNode<ulong>> _nodes =
            new Dictionary<ulong, LinkedListNode<ulong>>(16);

        private bool _capWarned;

        /// <summary>Live-entry count.  Mirrors <see cref="OrderCount"/> and <see cref="NodeCount"/>.</summary>
        public int Count => _expiryByObjectId.Count;

        /// <summary>Insertion-order list size.  Must equal <see cref="Count"/> in steady state.</summary>
        public int OrderCount => _order.Count;

        /// <summary>Node-map size.  Must equal <see cref="Count"/> in steady state.</summary>
        public int NodeCount => _nodes.Count;

        /// <summary>Reset all three structures and the cap-warned latch.</summary>
        public void Clear()
        {
            _expiryByObjectId.Clear();
            _order.Clear();
            _nodes.Clear();
            _capWarned = false;
        }

        /// <summary>
        /// Sweep entries past <paramref name="nowMs"/>; called from the
        /// admission paths so the live count is current before a cap test.
        /// </summary>
        public void Prune(long nowMs)
        {
            if (_expiryByObjectId.Count == 0) return;
            List<ulong> stale = null;
            foreach (var kv in _expiryByObjectId)
            {
                if (kv.Value <= nowMs)
                {
                    if (stale == null) stale = new List<ulong>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
            {
                foreach (var id in stale)
                    RemoveAll(id);
            }
            // Hysteresis re-arm: once the post-prune count has dropped
            // back below the watermark, the next cap-eviction is treated
            // as a fresh saturation episode.  Without this, the latch
            // fired exactly once per process — a flooding attacker who
            // tripped it early in a session could continue evicting
            // legitimate entries silently for the rest of the session.
            if (_capWarned && _expiryByObjectId.Count <= CapWarnRearmThreshold)
                _capWarned = false;
        }

        /// <summary>
        /// Record an out-of-order despawn intent for <paramref name="objectId"/>.
        /// A re-arriving despawn for the same id renews the TTL; an existing
        /// node is unlinked and re-inserted at the tail so insertion order
        /// reflects the latest activity.  When the cap is reached, the oldest
        /// entry is evicted to make room — keeping the most recent (and
        /// therefore most likely to match) entries.
        /// </summary>
        /// <param name="objectId">Object id whose Despawn arrived first.</param>
        /// <param name="nowMs">Monotonic-clock value used to compute the expiry.</param>
        /// <returns>
        /// True when a cap eviction occurred during this call so the caller
        /// can emit a redacted operator warning.
        /// </returns>
        public bool Record(ulong objectId, long nowMs)
        {
            bool evicted = false;

            // Refresh path: unlink the existing order node first so the list
            // stays strictly insertion-ordered with no duplicate node entries.
            if (_nodes.TryGetValue(objectId, out var existing))
            {
                _order.Remove(existing);
                _nodes.Remove(objectId);
                _expiryByObjectId.Remove(objectId);
            }
            else if (_expiryByObjectId.Count >= MaxEntries)
            {
                // Drain expired entries first; only fall through to FIFO
                // eviction when even the post-prune count is at the cap.
                Prune(nowMs);
                while (_expiryByObjectId.Count >= MaxEntries
                       && _order.Count > 0)
                {
                    var oldestNode = _order.First;
                    ulong oldest = oldestNode.Value;
                    _order.RemoveFirst();
                    _nodes.Remove(oldest);
                    _expiryByObjectId.Remove(oldest);
                    evicted = true;
                }
            }

            _expiryByObjectId[objectId] = nowMs + TtlMs;
            var node = _order.AddLast(objectId);
            _nodes[objectId] = node;

            // Latch the warning flag: the caller may wish to surface it once
            // per saturation episode rather than every eviction.
            if (evicted && !_capWarned)
            {
                _capWarned = true;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Consume an entry — unlink it from all three structures.  Returns
        /// true when an entry existed; the caller uses this to distinguish
        /// "matching despawn already arrived" from "no pending entry".
        /// </summary>
        public bool Consume(ulong objectId)
        {
            if (!_expiryByObjectId.Remove(objectId)) return false;
            if (_nodes.TryGetValue(objectId, out var node))
            {
                _order.Remove(node);
                _nodes.Remove(objectId);
            }
            // Same hysteresis re-arm as in Prune: a Consume that drops the
            // live count below the watermark closes the prior saturation
            // episode and re-enables the warning latch for the next.
            if (_capWarned && _expiryByObjectId.Count <= CapWarnRearmThreshold)
                _capWarned = false;
            return true;
        }

        // Internal helper for prune; kept private so the only public mutation
        // surface remains Record / Consume / Clear.
        private void RemoveAll(ulong objectId)
        {
            _expiryByObjectId.Remove(objectId);
            if (_nodes.TryGetValue(objectId, out var node))
            {
                _order.Remove(node);
                _nodes.Remove(objectId);
            }
        }
    }
}
