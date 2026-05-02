// RTMPE SDK — Runtime/Rpc/RequestIdAllocator.cs
//
// Centralised allocator for the 32-bit request_id field used in RPC requests
// (legacy RpcPacketBuilder and EnhancedRpcPacketBuilder share the same field).
//
// Why a dedicated allocator:
//  • The wire field is caller-supplied; without enforcement, application
//    code can recycle a small counter and let an attacker on the wire
//    race a forged response into the open correlation slot before the
//    real reply arrives.  Sourcing IDs from a CSPRNG raises the bid for
//    such an attack from "increment to N" to "predict 32 random bits".
//  • Pending callbacks need a TTL — without one, an unanswered request
//    leaks its slot indefinitely, and (worst case) a delayed forged
//    reply can correlate against a long-stale request_id.
//
// Wire field is 32 bits, so collision risk after ~2^16 outstanding
// requests reaches the birthday bound (~50 % chance).  In practice the
// pending map is in the tens, well below that bound; the allocator
// re-rolls if it ever picks zero (zero is reserved as "unused" by
// BuildPing fallbacks) or an in-flight ID.
//
// Threading: all members are thread-safe.  RegisterPending / Resolve /
// PurgeExpired use a single lock; ID generation uses RandomNumberGenerator
// which is already thread-safe.

using System;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace RTMPE.Rpc
{
    /// <summary>
    /// Generates cryptographically random RPC request IDs and tracks
    /// pending request → callback associations with a configurable TTL.
    ///
   /// Static so legacy and Enhanced builders share one ID space.
    /// </summary>
    public static class RequestIdAllocator
    {
        /// <summary>
        /// Default time-to-live for a registered pending callback.  After this
        /// duration <see cref="PurgeExpired"/> will drop the entry and invoke
        /// the timeout callback (if supplied).
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

        // RNG instance is thread-safe per .NET docs and reused across calls
        // to avoid the per-allocation overhead of CreateInstance.
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        // Pending registry: id → (deadlineMs, optional timeout callback).
        // Deadline stored as Environment.TickCount64 milliseconds — monotonic,
        // immune to NTP clock adjustments, and ~100x cheaper than DateTime.UtcNow
        // on Windows (HPET read vs. kernel syscall).
        private struct Entry
        {
            public long DeadlineMs;
            public Action OnTimeout;
        }

        private static readonly Dictionary<uint, Entry> Pending = new Dictionary<uint, Entry>(64);
        private static readonly object Lock = new object();

        /// <summary>
        /// Allocate a non-zero request ID drawn from a CSPRNG.  Re-rolls if
        /// the random value is zero or already present in the pending map.
        /// The returned ID is NOT yet registered — call
        /// <see cref="RegisterPending"/> if a timeout is wanted.
        /// </summary>
        public static uint Next()
        {
            Span<byte> buf = stackalloc byte[4];
            for (int attempt = 0; attempt < 8; attempt++)
            {
                Rng.GetBytes(buf);
                uint candidate = (uint)(buf[0]
                                      | (buf[1] << 8)
                                      | (buf[2] << 16)
                                      | (buf[3] << 24));
                if (candidate == 0) continue;

                lock (Lock)
                {
                    if (!Pending.ContainsKey(candidate))
                        return candidate;
                }
            }
            // Extreme bad luck (or a saturated map) — fall through with a
            // non-zero best-effort value.  RNG already gave us something
            // unpredictable; we accept the rare collision over an infinite
            // loop.  PurgeExpired() invocation by the caller is recommended.
            Span<byte> fallback = stackalloc byte[4];
            Rng.GetBytes(fallback);
            uint v = (uint)(fallback[0]
                          | (fallback[1] << 8)
                          | (fallback[2] << 16)
                          | (fallback[3] << 24));
            return v == 0 ? 1u : v;
        }

        /// <summary>
        /// Allocate an ID and register it in the pending map with the
        /// supplied TTL.  <paramref name="onTimeout"/> is invoked by the
        /// next <see cref="PurgeExpired"/> call once the deadline passes.
        /// </summary>
        public static uint AllocateAndRegister(TimeSpan? timeout = null, Action onTimeout = null)
        {
            uint id = Next();
            RegisterPending(id, timeout ?? DefaultTimeout, onTimeout);
            return id;
        }

        /// <summary>
        /// Associate a previously-allocated ID with a deadline.  Used when
        /// the caller already chose an ID via <see cref="Next"/>.
        /// </summary>
        public static void RegisterPending(uint id, TimeSpan timeout, Action onTimeout = null)
        {
            if (id == 0) return;
            // Environment.TickCount64 is a monotonic millisecond counter — immune
            // to NTP corrections that can cause DateTime.UtcNow to go backward,
            // and significantly cheaper on Windows (HPET read vs. syscall).
            long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
            lock (Lock)
            {
                Pending[id] = new Entry { DeadlineMs = deadline, OnTimeout = onTimeout };
            }
        }

        /// <summary>
        /// Mark a request as resolved (response received).  Removes the
        /// pending entry so its ID may be reused.  Returns true when the
        /// entry was present.
        /// </summary>
        public static bool Resolve(uint id)
        {
            lock (Lock)
            {
                return Pending.Remove(id);
            }
        }

        /// <summary>
        /// Sweep entries past their deadline.  Caller (typically a periodic
        /// timer in NetworkManager) is expected to invoke this every
        /// 1–5 seconds.  Returns the number of entries purged.
        /// </summary>
        public static int PurgeExpired()
        {
            long nowMs = Environment.TickCount64;
            List<Action> callbacks = null;
            int purged = 0;

            lock (Lock)
            {
                if (Pending.Count == 0) return 0;

                List<uint> toRemove = null;
                foreach (var kv in Pending)
                {
                    if (kv.Value.DeadlineMs <= nowMs)
                    {
                        if (toRemove == null) toRemove = new List<uint>();
                        toRemove.Add(kv.Key);
                        if (kv.Value.OnTimeout != null)
                        {
                            if (callbacks == null) callbacks = new List<Action>();
                            callbacks.Add(kv.Value.OnTimeout);
                        }
                    }
                }
                if (toRemove != null)
                {
                    purged = toRemove.Count;
                    foreach (uint id in toRemove) Pending.Remove(id);
                }
            }

            // Fire callbacks outside the lock to avoid reentrancy hazards.
            //
            // Subscriber-isolation discipline: a buggy timeout callback must
            // not abort the sweep across siblings, but it MUST surface a
            // diagnostic so operator dashboards observe the regression.  A
            // bare `catch {}` makes the failure invisible — a callback that
            // throws every invocation looks identical to one that simply
            // does nothing.  Symmetric with the M19-CORE-07 / M19-SYNC-01
            // isolation already adopted across the SDK's hot paths.
            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            "[RTMPE] RequestIdAllocator: timeout callback threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Sweep continues " +
                            "with remaining timeouts.");
                    }
                }
            }
            return purged;
        }

        /// <summary>
        /// Number of currently-pending registered callbacks.  For diagnostics.
        /// </summary>
        public static int PendingCount
        {
            get { lock (Lock) return Pending.Count; }
        }

        /// <summary>
        /// Drop every pending entry and surface a synthetic timeout to each
        /// registered callback — call from <c>NetworkManager.Cleanup</c>,
        /// <c>ClearSessionData</c>, and any other session-boundary hook so a
        /// previous session's <c>OnTimeout</c> closures do not fire later
        /// against torn-down NetworkManager state, and a forged-reply window
        /// on a previously-allocated request_id cannot be correlated into
        /// the next session.  Callbacks fire OUTSIDE the internal lock to
        /// match <see cref="PurgeExpired"/>'s reentrancy contract; exception
        /// isolation is symmetric with that path.
        /// </summary>
        public static int DropPending()
        {
            List<Action> callbacks = null;
            int dropped;
            lock (Lock)
            {
                dropped = Pending.Count;
                if (dropped == 0) return 0;
                foreach (var kv in Pending)
                {
                    if (kv.Value.OnTimeout != null)
                    {
                        if (callbacks == null) callbacks = new List<Action>();
                        callbacks.Add(kv.Value.OnTimeout);
                    }
                }
                Pending.Clear();
            }

            if (callbacks != null)
            {
                foreach (var cb in callbacks)
                {
                    try { cb(); }
                    catch (Exception ex)
                    {
                        UnityEngine.Debug.LogError(
                            "[RTMPE] RequestIdAllocator.DropPending: timeout callback threw " +
                            $"{ex.GetType().Name}: {ex.Message}.  Drain continues.");
                    }
                }
            }
            return dropped;
        }

        /// <summary>
        /// Test seam: clear all pending entries without firing callbacks.
        /// </summary>
        internal static void ResetForTest()
        {
            lock (Lock) Pending.Clear();
        }
    }
}
