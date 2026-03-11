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
        /// </summary>
        public void Enqueue(Action action)
        {
            if (action != null)
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
