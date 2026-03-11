// RTMPE SDK — Runtime/Infrastructure/Threading/ThreadSafeQueue.cs
//
// Lock-free FIFO queue for cross-thread producer/consumer scenarios.
//
// Why ConcurrentQueue<T> instead of Queue<T> + lock?
//   ConcurrentQueue uses CAS (compare-and-swap) internally — no OS mutex is
//   acquired on the hot path. On .NET Standard 2.1 / Unity IL2CPP this eliminates
//   lock contention and priority inversion between the network background thread
//   and the Unity main thread, which is critical for sub-30 ms P99 latency.
//
// .NET Standard 2.1 note:
//   ConcurrentQueue<T>.Clear() was added in .NET 5 and is NOT available on
//   .NET Standard 2.1 / Unity IL2CPP. Use the drain-loop pattern (see Clear()).

using System.Collections.Concurrent;

namespace RTMPE.Threading
{
    /// <summary>
    /// Lock-free thread-safe FIFO queue backed by <see cref="ConcurrentQueue{T}"/>.
    /// Safe for concurrent single-producer / single-consumer use patterns.
    /// </summary>
    public sealed class ThreadSafeQueue<T>
    {
        private readonly ConcurrentQueue<T> _queue = new ConcurrentQueue<T>();

        /// <summary>Returns <see langword="true"/> when the queue contains no items.</summary>
        public bool IsEmpty => _queue.IsEmpty;

        /// <summary>
        /// Approximate item count. May be stale immediately after reading due to
        /// concurrent operations. Use only for diagnostics — never for correctness.
        /// </summary>
        public int Count => _queue.Count;

        /// <summary>
        /// Append <paramref name="item"/> to the tail of the queue. Thread-safe; lock-free.
        /// </summary>
        public void Enqueue(T item) => _queue.Enqueue(item);

        /// <summary>
        /// Attempt to remove and return the item at the head.
        /// Returns <see langword="false"/> (and <paramref name="item"/> = default) when empty.
        /// Thread-safe; lock-free.
        /// </summary>
        public bool TryDequeue(out T item) => _queue.TryDequeue(out item);

        /// <summary>
        /// Discard all pending items.
        /// Uses a drain-loop because <c>ConcurrentQueue.Clear()</c> is .NET 5+ only.
        /// </summary>
        public void Clear()
        {
            while (_queue.TryDequeue(out _)) { }
        }
    }
}
