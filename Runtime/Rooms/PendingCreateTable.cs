// RTMPE SDK — Runtime/Rooms/PendingCreateTable.cs
//
// In-flight CreateRoom request bookkeeping for RoomManager.  Carved out of
// RoomManager into its own type so the correlator's invariants (id-based
// matching with FIFO fallback, capacity cap, TTL-bounded growth) can be
// unit-tested without spinning up the full RoomManager surface (which
// depends on UnityEngine, PacketBuilder, RoomPacketBuilder, ...).
//
// Threading: every method must be invoked from the Unity main thread.
// RoomManager already enforces that contract; the table itself is
// not thread-safe by design.

using System;
using System.Collections.Generic;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Tracks outbound CreateRoom-style requests by client-generated GUID,
    /// preserving FIFO order for fallback matching when the server does not
    /// echo the request id.  Generic over the per-request payload type so
    /// the same primitive can be reused for any request/response correlator
    /// where the server cannot yet echo a correlator id.
    /// </summary>
    /// <typeparam name="T">Per-request payload (e.g. CreateRoomOptions).</typeparam>
    public sealed class PendingCreateTable<T>
    {
        private readonly int    _capacity;
        private readonly double _ttlSeconds;

        private readonly Dictionary<Guid, Entry> _byId    = new Dictionary<Guid, Entry>();
        private readonly Queue<Guid>             _byOrder = new Queue<Guid>();

        private readonly struct Entry
        {
            public readonly Guid     Id;
            public readonly T        Payload;
            public readonly DateTime SentAtUtc;
            public Entry(Guid id, T payload, DateTime sentAtUtc)
            {
                Id        = id;
                Payload   = payload;
                SentAtUtc = sentAtUtc;
            }
        }

        public PendingCreateTable(int capacity, double ttlSeconds)
        {
            if (capacity   <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (ttlSeconds <= 0) throw new ArgumentOutOfRangeException(nameof(ttlSeconds));
            _capacity   = capacity;
            _ttlSeconds = ttlSeconds;
        }

        public int  Count  => _byId.Count;
        public bool IsFull => _byId.Count >= _capacity;

        /// <summary>
        /// Allocate a fresh GUID, store the request payload, and return the
        /// allocated id.  Caller is responsible for calling <see cref="IsFull"/>
        /// first; calling Register on a full table throws.
        /// </summary>
        public Guid Register(T payload, DateTime sentAtUtc)
        {
            if (IsFull)
                throw new InvalidOperationException(
                    "PendingCreateTable: capacity exceeded — call IsFull first.");

            var id = Guid.NewGuid();
            _byId[id] = new Entry(id, payload, sentAtUtc);
            _byOrder.Enqueue(id);
            return id;
        }

        /// <summary>
        /// Resolve the request that this response answers.
        ///   • <paramref name="echoedRequestId"/> non-null and present → id match.
        ///   • Otherwise → oldest-not-yet-matched entry (FIFO fallback).
        /// Returns false when the table is empty (i.e. spurious response).
        /// </summary>
        /// <remarks>
        /// FIFO fallback exists because the current gateway protocol does NOT
        /// echo the request id — see the wire-protocol coordination note in
        /// RoomManager.  Once the gateway echoes the id, every match becomes
        /// id-based and FIFO becomes a defence-in-depth path.
        /// </remarks>
        public bool TryMatch(Guid? echoedRequestId, out T payload)
        {
            if (echoedRequestId.HasValue)
            {
                // Echoed-id path is strict: hit-or-miss.  Falling through to
                // FIFO when the id is unknown would let a duplicate response
                // (whose original was already matched) silently consume some
                // OTHER pending request's options — exactly the cross-talk
                // class this correlator was introduced to eliminate.  Treat
                // unknown echoed ids as spurious.
                if (_byId.TryGetValue(echoedRequestId.Value, out var byIdEntry))
                {
                    _byId.Remove(echoedRequestId.Value);
                    RemoveFromOrder(echoedRequestId.Value);
                    payload = byIdEntry.Payload;
                    return true;
                }
                payload = default;
                return false;
            }

            while (_byOrder.Count > 0)
            {
                var headId = _byOrder.Dequeue();
                if (_byId.TryGetValue(headId, out var entry))
                {
                    _byId.Remove(headId);
                    payload = entry.Payload;
                    return true;
                }
                // headId was already removed by id-match above — keep draining.
            }

            payload = default;
            return false;
        }

        /// <summary>
        /// Remove every entry whose <c>SentAtUtc</c> is older than
        /// <paramref name="now"/> minus the configured TTL.  Returns the
        /// (id, payload) tuples for the caller to surface to its observers
        /// (RoomManager fires OnRoomError so applications never silently lose
        /// an in-flight CreateRoom request).
        /// </summary>
        public IEnumerable<(Guid id, T payload)> SweepExpired(DateTime now)
        {
            if (_byId.Count == 0) yield break;

            DateTime  cutoff = now.AddSeconds(-_ttlSeconds);
            List<Guid> stale = null;
            foreach (var kv in _byId)
            {
                if (kv.Value.SentAtUtc <= cutoff)
                    (stale ??= new List<Guid>()).Add(kv.Key);
            }
            if (stale == null) yield break;

            foreach (var id in stale)
            {
                T payload = _byId[id].Payload;
                _byId.Remove(id);
                RemoveFromOrder(id);
                yield return (id, payload);
            }
        }

        public void Clear()
        {
            _byId.Clear();
            _byOrder.Clear();
        }

        private void RemoveFromOrder(Guid id)
        {
            // Linear scan is acceptable: queue length ≤ _capacity (default 16).
            int n = _byOrder.Count;
            for (int i = 0; i < n; i++)
            {
                var entry = _byOrder.Dequeue();
                if (entry != id) _byOrder.Enqueue(entry);
            }
        }
    }
}
