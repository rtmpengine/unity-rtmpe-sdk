// RTMPE SDK — Runtime/Core/DepartedPlayerTracker.cs
//
// Bookkeeping for the leave-before-Spawn race on UDP transport.
//
// A player's `player_left` room event and a Spawn (0x30) that player owns are
// fanned out over different relay paths with no mutual ordering, so a Spawn can
// arrive AFTER its owner has left.  OnPlayerLeftRoom only tears down objects that
// are already registered, so a late Spawn would instantiate an object owned by a
// departed player that no live session can update or despawn — a permanent ghost
// in the local view.  This tracker remembers each departed player under a short
// TTL; a Spawn whose owner is still tombstoned is dropped.
//
// The tombstone keys on the server-derived player_id
// (deriveDeterministicPlayerID(session_id)).  A session-preserving rejoin reuses
// that id, so a returning player would otherwise stay tombstoned and have their
// own legitimate re-Spawn dropped; the id is therefore cleared explicitly on
// rejoin (see Remove, driven by SpawnManager.OnPlayerJoinedRoom).  The TTL
// remains the sole bound for the departed-and-not-returning case.

using System.Collections.Generic;

namespace RTMPE.Core
{
    /// <summary>
    /// Tracks players who have left the room so a Spawn that raced behind its
    /// owner's departure can be dropped instead of forming a ghost.
    /// Single-thread; the surrounding <see cref="SpawnManager"/> guarantees every
    /// entry point runs on the Unity main thread.
    /// </summary>
    internal sealed class DepartedPlayerTracker
    {
        // Five seconds matches PendingDespawnTracker: generous against the UDP
        // reorder window (well under 200 ms even on poor links) while bounding
        // how long a departed player suppresses stray spawns.
        public const long TtlMs = 5_000;

        // Bounds memory under pathological room churn; the live set is otherwise
        // at most the room's peak membership within one TTL, far below this cap.
        public const int MaxEntries = 256;

        private readonly Dictionary<string, long> _expiryByPlayerId =
            new Dictionary<string, long>(16);

        /// <summary>Live tombstone count.</summary>
        public int Count => _expiryByPlayerId.Count;

        /// <summary>Drop every tombstone (room leave / disconnect).</summary>
        public void Clear() => _expiryByPlayerId.Clear();

        /// <summary>
        /// Sweep entries at or past <paramref name="nowMs"/>.  Called from both
        /// mutation paths so a stale tombstone never suppresses a spawn or holds
        /// a cap slot.
        /// </summary>
        public void Prune(long nowMs)
        {
            if (_expiryByPlayerId.Count == 0) return;
            List<string> stale = null;
            foreach (var kv in _expiryByPlayerId)
            {
                if (kv.Value <= nowMs)
                {
                    if (stale == null) stale = new List<string>();
                    stale.Add(kv.Key);
                }
            }
            if (stale != null)
                foreach (var id in stale) _expiryByPlayerId.Remove(id);
        }

        /// <summary>
        /// Arm (or renew) <paramref name="playerId"/>'s tombstone for
        /// <see cref="TtlMs"/>.  At capacity the entry expiring soonest is
        /// evicted first.  Empty ids are ignored.
        /// </summary>
        public void Record(string playerId, long nowMs)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            Prune(nowMs);
            if (!_expiryByPlayerId.ContainsKey(playerId)
                && _expiryByPlayerId.Count >= MaxEntries)
                EvictSoonestExpiring();
            _expiryByPlayerId[playerId] = nowMs + TtlMs;
        }

        /// <summary>
        /// Lift <paramref name="playerId"/>'s tombstone.  A player present in the
        /// room again — the reused-id case where the same server-derived id
        /// returns on a rejoin — owns legitimate spawns that must no longer be
        /// suppressed; a departure never followed by a rejoin instead expires on
        /// its own via <see cref="TtlMs"/>.  Absent or empty ids are a no-op.
        /// </summary>
        public void Remove(string playerId)
        {
            if (string.IsNullOrEmpty(playerId)) return;
            _expiryByPlayerId.Remove(playerId);
        }

        /// <summary>
        /// True when <paramref name="playerId"/> departed within the last
        /// <see cref="TtlMs"/>.  Non-consuming: a departed player may own several
        /// late spawns, and every one is dropped until the tombstone expires.
        /// </summary>
        public bool IsDeparted(string playerId, long nowMs)
        {
            if (string.IsNullOrEmpty(playerId)) return false;
            Prune(nowMs);
            return _expiryByPlayerId.ContainsKey(playerId);
        }

        private void EvictSoonestExpiring()
        {
            string evict = null;
            long soonest = long.MaxValue;
            foreach (var kv in _expiryByPlayerId)
            {
                if (kv.Value < soonest)
                {
                    soonest = kv.Value;
                    evict = kv.Key;
                }
            }
            if (evict != null) _expiryByPlayerId.Remove(evict);
        }
    }
}
