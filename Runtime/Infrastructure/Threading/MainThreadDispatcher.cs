// RTMPE SDK — Runtime/Infrastructure/Threading/MainThreadDispatcher.cs
//
// Bridges the gap between the RTMPE background network thread and Unity's main thread.
// Unity APIs (Debug.Log, MonoBehaviour callbacks, scene queries) are only safe to call
// from the main thread. This dispatcher queues lambdas on the network thread and
// drains them inside Unity's Update() loop.
//
// Usage from any thread:
//   MainThreadDispatcher.Instance.Enqueue(() => { /* any Unity-safe code */ });
//
// ⚠  UNITY MAIN THREAD RULE: MainThreadDispatcher.Instance must be accessed
//    from the main thread only (it may create a new GameObject on first call).
//    Call it once during Awake/Start to warm the singleton before background threads
//    begin sending dispatch commands.

using System;
using UnityEngine;

namespace RTMPE.Threading
{
    /// <summary>
    /// Singleton MonoBehaviour that marshals callbacks from background threads
    /// to the Unity main thread. Survives scene loads via <c>DontDestroyOnLoad</c>.
    /// </summary>
    [DefaultExecutionOrder(-999)]
    public sealed class MainThreadDispatcher : MonoBehaviour
    {
        // ── Singleton ──────────────────────────────────────────────────────────
        private static MainThreadDispatcher _instance;
        private static readonly object _instLock = new object();

        // Reset static state on Play-Mode entry so a second Play in the same
        // Editor session gets a clean singleton (same domain-reload pattern as
        // NetworkManager).
        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            lock (_instLock) { _instance = null; }
        }

        // ── Queue ──────────────────────────────────────────────────────────────
        private readonly ThreadSafeQueue<Action> _queue = new ThreadSafeQueue<Action>();

        // Limit callbacks executed per frame to bound worst-case stall time.
        // 200 × ~1 µs = ~200 µs — well inside a 33 ms frame budget at 30 Hz.
        private const int MaxActionsPerFrame = 200;

        // Soft cap on the number of pending callbacks in the queue.  At ~1 µs
        // per action this represents ~10 ms of work — far more than a single
        // frame can drain.  A backlog larger than this almost certainly means
        // the main thread is stalled or a producer is running wild; new
        // enqueues past this threshold are DROPPED rather than allowed to
        // grow the queue to OOM on long-running mobile sessions.
        private const int MaxQueueDepth = 10_000;

        // Track overflow events so operators can detect producer/consumer mismatch
        // without spamming the log.  We log the FIRST overflow and then every
        // power-of-two-th overflow (1, 2, 4, 8, 16, …) to retain visibility of
        // ongoing degradation without flooding the console at ~60 FPS.
        private long _overflowCount;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Singleton accessor. <b>MUST be called from the Unity main thread.</b>
        /// Creates the dispatcher GameObject on first access.
        /// </summary>
        public static MainThreadDispatcher Instance
        {
            get
            {
                lock (_instLock)
                {
                    if (_instance != null) return _instance;

                    var go = new GameObject("[RTMPE] MainThreadDispatcher");
                    DontDestroyOnLoad(go);
                    // Awake() runs synchronously inside AddComponent, setting _instance.
                    go.AddComponent<MainThreadDispatcher>();
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Enqueue <paramref name="action"/> for execution on the Unity main thread.
        /// Thread-safe; returns immediately without blocking.
        /// Null actions are silently ignored.
        ///
        /// Back-pressure: if the pending queue already contains
        /// <see cref="MaxQueueDepth"/> (10 000) items the new action is DROPPED
        /// and an error is logged at power-of-two overflow counts.  Dropping
        /// is preferred over unbounded growth because a 10 000-item backlog
        /// already indicates the main thread is not draining — adding more
        /// work only compounds the stall and risks OOM on mobile.
        /// </summary>
        public void Enqueue(Action action)
        {
            if (action == null) return;

            // ThreadSafeQueue.Count is an approximate (fast) snapshot — good
            // enough for a soft cap.  A brief racing over-count is fine; the
            // goal is to *bound* growth, not to implement a precise semaphore.
            if (_queue.Count >= MaxQueueDepth)
            {
                // Interlocked.Increment on long is supported on .NET Standard 2.1.
                long count = System.Threading.Interlocked.Increment(ref _overflowCount);
                // Log on the first overflow and at every power-of-two overflow
                // afterwards (1, 2, 4, 8, 16, …).  `(n & (n-1)) == 0` is a
                // standard power-of-two check.
                if (count == 1 || (count & (count - 1)) == 0)
                {
                    // Use LogError — an overflowing dispatcher queue almost
                    // always indicates a real application-level bug.
                    Debug.LogError(
                        $"[RTMPE] MainThreadDispatcher: queue full ({MaxQueueDepth}); " +
                        $"dropped action (total dropped: {count}).  This usually means the " +
                        "main thread is stalled or a background producer is misconfigured.");
                }
                return;
            }
            _queue.Enqueue(action);
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            // Handle the edge case where Unity instantiates a second dispatcher
            // (e.g. scene has a prefab with this component).
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            lock (_instLock)
            {
                _instance = this;
            }
        }

        private void Update()
        {
            int processed = 0;
            while (processed < MaxActionsPerFrame && _queue.TryDequeue(out var action))
            {
                try
                {
                    action.Invoke();
                }
                catch (Exception ex)
                {
                    // Never swallow silently in production — log and continue.
                    Debug.LogError($"[RTMPE] MainThreadDispatcher: unhandled exception in dispatched action.\n{ex}");
                }
                processed++;
            }
        }

        private void OnDestroy()
        {
            lock (_instLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }
    }
}
