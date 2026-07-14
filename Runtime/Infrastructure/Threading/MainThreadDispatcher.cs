// RTMPE SDK — Runtime/Infrastructure/Threading/MainThreadDispatcher.cs
//
// Bridges the gap between the RTMPE background network thread and Unity's main thread.
// Unity APIs (Debug.Log, MonoBehaviour callbacks, scene queries) are only safe to call
// from the main thread. This dispatcher queues lambdas on the network thread and
// drains them inside Unity's Update() loop.
//
// Usage from any thread:
//  MainThreadDispatcher.Instance.Enqueue(() => { /* any Unity-safe code */ });
//
// UNITY MAIN THREAD RULE: MainThreadDispatcher.Instance must be accessed
// from the main thread only — it may create a new GameObject on first call,
// and AddComponent/DontDestroyOnLoad are not safe off-main-thread.  To guard
// against a misuse where a background thread is the first to touch the
// singleton, the main-thread id is captured at static init via
// RuntimeInitializeOnLoadMethod.  An off-main-thread access throws
// InvalidOperationException with a clear message instead of letting Unity
// surface a confusing UnityException later inside AddComponent.
//
// Pre-warming: callers that want to be explicit can call Prewarm() during
// their own Awake/Start to materialise the singleton before any background
// thread has reason to touch it.

using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

namespace RTMPE.Threading
{
    /// <summary>
    /// Policy applied when <see cref="MainThreadDispatcher.Enqueue"/> is called
    /// while the queue already contains <see cref="MainThreadDispatcher.MaxQueueDepth"/>
    /// items.
    /// </summary>
    public enum DispatcherFullPolicy
    {
        /// <summary>Drop the newly-enqueued action.  Default — preserves FIFO of in-flight work.</summary>
        DropTail,

        /// <summary>Drop the oldest pending action and accept the new one.</summary>
        DropHead,

        /// <summary>Throw <see cref="InvalidOperationException"/>.</summary>
        Throw,
    }

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

        // Captured on the very first managed-thread to run user code in the
        // Unity domain — i.e. the main thread.  Compared in Instance to detect
        // off-main-thread access before AddComponent/DontDestroyOnLoad blow up.
        private static int _mainThreadId;

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            // SubsystemRegistration runs on the main thread before any user
            // script.  Capturing the id here means later off-main-thread
            // accesses can be detected deterministically.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            lock (_instLock) { _instance = null; }
        }

        // ── Queue ──────────────────────────────────────────────────────────────
        // Concurrent queue + explicit Interlocked counter.  ConcurrentQueue.Count
        // is an O(N) walk on some implementations and only an *approximate*
        // snapshot — using it for back-pressure decisions allowed transient
        // over- or under-counts.  An explicit counter gives a precise depth
        // value for the policy check below.
        //
        // The work item carries both an Action (for the legacy zero-arg
        // overload) and an Action<object>+state pair (for the generic
        // Enqueue<TArg> overload).  Carrying both inside one struct keeps
        // a single FIFO for execution order; producers populate exactly
        // one of the two execution shapes.
        private readonly ConcurrentQueue<WorkItem> _queue = new ConcurrentQueue<WorkItem>();
        private int _depth;

        private readonly struct WorkItem
        {
            // One of:
            //   • Action               — legacy zero-arg overload.
            //   • StateAction + State  — generic Enqueue<TArg> overload (boxes
            //                            value-type args once per call; static
            //                            method refs pass through unboxed).
            //   • BufferAction + Buffer + Length — per-packet receive overload
            //                            that avoids any per-call allocation
            //                            by carrying the byte[]+int pair
            //                            inline in the queue node.
            public readonly Action Action;
            public readonly Action<object> StateAction;
            public readonly object State;
            public readonly Action<byte[], int> BufferAction;
            public readonly byte[] Buffer;
            public readonly int Length;

            public WorkItem(Action action)
            {
                Action       = action;
                StateAction  = null;
                State        = null;
                BufferAction = null;
                Buffer       = null;
                Length       = 0;
            }

            public WorkItem(Action<object> stateAction, object state)
            {
                Action       = null;
                StateAction  = stateAction;
                State        = state;
                BufferAction = null;
                Buffer       = null;
                Length       = 0;
            }

            public WorkItem(Action<byte[], int> bufferAction, byte[] buffer, int length)
            {
                Action       = null;
                StateAction  = null;
                State        = null;
                BufferAction = bufferAction;
                Buffer       = buffer;
                Length       = length;
            }

            public void Invoke()
            {
                if (Action != null)             Action();
                else if (BufferAction != null)  BufferAction(Buffer, Length);
                else if (StateAction != null)   StateAction(State);
            }
        }

        // Serialises the read-check → optional-dequeue → enqueue → increment
        // sequence so that concurrent producers cannot race past the cap.
        // Update()'s drain path (TryDequeue + Interlocked.Decrement) never
        // takes this lock, so there is no contention on the hot read path.
        private readonly object _enqueueLock = new object();

        /// <summary>
        /// Policy applied when the queue is full.  Defaults to
        /// <see cref="DispatcherFullPolicy.DropTail"/>.  Settable from the main
        /// thread before the dispatcher is used.
        /// </summary>
        public DispatcherFullPolicy FullPolicy { get; set; } = DispatcherFullPolicy.DropTail;

        // Limit callbacks executed per frame to bound worst-case stall time.
        // 200 × ~1 µs = ~200 µs — well inside a 33 ms frame budget at 30 Hz.
        private const int MaxActionsPerFrame = 200;

        /// <summary>
        /// Soft cap on the number of pending callbacks in the queue.  At ~1 µs
        /// per action this represents ~10 ms of work — far more than a single
        /// frame can drain.
        /// </summary>
        public const int MaxQueueDepth = 10_000;

        // Track overflow events so operators can detect producer/consumer mismatch
        // without spamming the log.  We log the FIRST overflow and then every
        // power-of-two-th overflow (1, 2, 4, 8, 16, …) to retain visibility of
        // ongoing degradation without flooding the console at ~60 FPS.
        private long _overflowCount;

        // Counts buffer-pair Enqueue calls rejected by backpressure.  The
        // caller of that overload owns a pool rental that must be returned
        // when the dispatch never runs, so a separate counter (rather than a
        // share of _overflowCount) lets operators size the receive pool
        // against the precise drop rate of rented packets.
        private long _droppedRentedPacketCount;

        /// <summary>Total number of dropped or rejected actions since construction.</summary>
        public long OverflowCount => Interlocked.Read(ref _overflowCount);

        /// <summary>
        /// Total number of buffer-pair <see cref="Enqueue(Action{byte[],int}, byte[], int)"/>
        /// calls rejected by backpressure since construction.  The caller is
        /// expected to return the pool rental on every false return.
        /// </summary>
        public long DroppedRentedPacketCount => Interlocked.Read(ref _droppedRentedPacketCount);

        /// <summary>
        /// Raised on the producer thread when the rented-buffer drop count
        /// crosses the next power-of-two boundary (1, 2, 4, 8, …) — the same
        /// log-cadence used by <see cref="RecordOverflow"/>, lifted into an
        /// observable event so production code can wire up dashboards or
        /// circuit breakers without polling
        /// <see cref="DroppedRentedPacketCount"/> on a timer.  The argument
        /// is the new total drop count.
        /// </summary>
        /// <remarks>
        /// Fires from arbitrary producer threads and is invoked OUTSIDE the
        /// internal enqueue lock.  Subscribers MUST be thread-safe and MUST
        /// NOT re-enter the dispatcher synchronously — schedule any
        /// follow-up work via <see cref="Enqueue(System.Action)"/> if the
        /// reaction must run on the main thread.
        /// </remarks>
        public event System.Action<long> OnRentedPacketDropped;

        // Hook used to return a rented buffer to its origin pool when the
        // DropHead policy evicts an in-flight work item.  The original
        // network-thread caller already saw a true return for that item, so
        // it will never call Return itself — without this hook the rental
        // leaks for the lifetime of the process under sustained backpressure.
        // Defaults to the shared ArrayPool to match production callers; tests
        // override it to assert the eviction path returns through a known sink.
        private Action<byte[]> _bufferReturnHandler =
            static b => { try { System.Buffers.ArrayPool<byte>.Shared.Return(b, clearArray: true); } catch { /* foreign array; pool may reject */ } };

        /// <summary>
        /// Sink invoked when the <see cref="DispatcherFullPolicy.DropHead"/>
        /// policy evicts a work item that carries a rented byte buffer.  The
        /// sink owns the rental from the moment it is invoked.  Defaults to
        /// returning the buffer to <see cref="System.Buffers.ArrayPool{T}.Shared"/>.
        /// Set to a custom delegate when a non-shared pool is in use, or to
        /// observe evictions from a test harness.
        ///
        /// <para><b>Threading:</b> the handler is invoked from arbitrary
        /// producer threads OUTSIDE the dispatcher's internal enqueue lock.
        /// It MUST be thread-safe and MUST NOT re-enter the dispatcher
        /// (calling <c>MainThreadDispatcher.Instance.Enqueue</c> from inside
        /// the handler is supported because the dispatcher lock is no longer
        /// held, but a handler that re-rents a buffer and re-enqueues it can
        /// trivially deadlock the producer if the queue is still full —
        /// design the handler as a pure pool-return / observer with no
        /// further dispatch work).</para>
        /// </summary>
        public Action<byte[]> BufferReturnHandler
        {
            get => _bufferReturnHandler;
            set => _bufferReturnHandler = value ?? (static _ => { });
        }

        /// <summary>Current pending-action depth (precise, not approximate).</summary>
        public int Depth => Volatile.Read(ref _depth);

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Singleton accessor. <b>MUST be called from the Unity main thread.</b>
        /// Creates the dispatcher GameObject on first access.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the first access happens on a non-main thread.  Call
        /// <see cref="Prewarm"/> from a main-thread Awake/Start to avoid this.
        /// </exception>
        public static MainThreadDispatcher Instance
        {
            get
            {
                // Hot-path Instance reads must be lock-free — the Awake-driven
                // publication is the only writer in steady state, and the
                // monitor-protected writes below provide the matching release
                // barrier.  Volatile.Read makes the dependent load a true
                // acquire so a background thread sees a fully-constructed
                // instance once Awake has published it.  AddComponent is the
                // only branch that needs synchronisation, and only on the
                // first-ever access before any GameObject exists.
                var cached = Volatile.Read(ref _instance);
                if (cached != null) return cached;

                lock (_instLock)
                {
                    // Re-check inside the lock — another main-thread caller
                    // may have raced us through the fast path and already
                    // constructed the GameObject.
                    if (_instance != null) return _instance;

                    // Refuse to create the GameObject from a background thread.
                    // Without this guard Unity raises UnityException from inside
                    // AddComponent, which is harder to diagnose than a precise
                    // managed exception thrown at the call site.
                    int currentId = Thread.CurrentThread.ManagedThreadId;
                    if (_mainThreadId != 0 && currentId != _mainThreadId)
                    {
                        throw new InvalidOperationException(
                            "MainThreadDispatcher.Instance must be accessed from the Unity main thread. " +
                            $"Current thread id = {currentId}, main thread id = {_mainThreadId}. " +
                            "Call MainThreadDispatcher.Prewarm() from a MonoBehaviour Awake() before any " +
                            "background thread calls Enqueue().");
                    }

                    var go = new GameObject("[RTMPE] MainThreadDispatcher");
                    DontDestroyOnLoad(go);
                    // Awake() runs synchronously inside AddComponent, setting _instance.
                    go.AddComponent<MainThreadDispatcher>();
                    return _instance;
                }
            }
        }

        /// <summary>
        /// Materialise the singleton on the main thread.  Idempotent.  Safe to
        /// call from any MonoBehaviour Awake/Start so background-thread
        /// producers can rely on <see cref="Instance"/> being non-null.
        /// </summary>
        public static MainThreadDispatcher Prewarm() => Instance;

        /// <summary>
        /// <see langword="true"/> when the calling code is executing on the
        /// Unity main thread.  The id captured at <c>SubsystemRegistration</c>
        /// is the only authoritative comparison point — the runtime exposes it
        /// here so other SDK subsystems (singleton getters, transport
        /// callbacks) can route Unity-API access without re-implementing the
        /// capture.  Returns <see langword="false"/> when the id has not yet
        /// been captured (defensive — should be impossible after the first
        /// frame because <c>SubsystemRegistration</c> always fires on the main
        /// thread before any user script).
        /// </summary>
        public static bool IsMainThread =>
            _mainThreadId != 0
            && Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        /// <summary>
        /// Enqueue <paramref name="action"/> for execution on the Unity main thread.
        /// Thread-safe; returns immediately without blocking.
        /// Null actions are silently ignored.
        ///
       /// Back-pressure: when the pending queue already contains
        /// <see cref="MaxQueueDepth"/> items, <see cref="FullPolicy"/> determines
        /// the outcome — DropTail (default) discards the new action, DropHead
        /// removes the oldest pending action and admits the new one, and Throw
        /// raises <see cref="InvalidOperationException"/>.
        /// <para>
        /// Use <see cref="TryEnqueue(Action)"/> when the caller needs to know
        /// whether the work was admitted (e.g. RPC-response continuations
        /// whose dropped invocation would silently strand the request).
        /// </para>
        /// </summary>
        public void Enqueue(Action action)
        {
            if (action == null) return;
            EnqueueCore(new WorkItem(action), RejectionKind.Generic);
        }

        /// <summary>
        /// Enqueue <paramref name="action"/> and report whether it was
        /// admitted.  Returns <see langword="false"/> when the queue is at
        /// capacity under DropTail (the action will never run), or when the
        /// action was admitted at the cost of evicting an older work item
        /// under DropHead (the caller is informed so observability paths
        /// can surface the loss); the Throw policy still raises
        /// <see cref="InvalidOperationException"/>.  Subscribe to
        /// <see cref="OnGenericActionDropped"/> for an observability hook
        /// equivalent to <see cref="OnRentedPacketDropped"/>.
        /// </summary>
        public bool TryEnqueue(Action action)
        {
            if (action == null) return false;
            return EnqueueCore(new WorkItem(action), RejectionKind.Generic);
        }

        /// <summary>
        /// Same accept/reject semantics as <see cref="TryEnqueue(Action)"/>
        /// but for the closure-free state-pair overload.  Use this for RPC
        /// timeout callbacks, ownership-grant continuations, scene-load
        /// resolutions, and any other state-bearing main-thread dispatch
        /// whose silent drop would corrupt application state.
        /// </summary>
        public bool TryEnqueue<TArg>(Action<TArg> action, TArg arg)
        {
            if (action == null) return false;
            return EnqueueCore(new WorkItem(static state =>
            {
                var pair = ((Action<TArg>, TArg))state;
                pair.Item1(pair.Item2);
            }, (action, arg)), RejectionKind.Generic);
        }

        // Producer threads invoke this when a generic-shape work item
        // (Action / Action<TArg>) is rejected by backpressure.  Symmetric
        // with OnRentedPacketDropped — sized & gated identically (logged at
        // power-of-two boundaries inside RecordOverflow) so dashboards can
        // sum the two streams without policy drift.
        private long _droppedGenericActionCount;

        /// <summary>
        /// Total number of <see cref="Enqueue(Action)"/> /
        /// <see cref="TryEnqueue(Action)"/> / <see cref="TryEnqueue{TArg}(Action{TArg}, TArg)"/>
        /// calls rejected by backpressure since construction.
        /// </summary>
        public long DroppedGenericActionCount => Interlocked.Read(ref _droppedGenericActionCount);

        /// <summary>
        /// Raised on the producer thread when the generic-action drop count
        /// crosses the next power-of-two boundary.  Fires from arbitrary
        /// producer threads OUTSIDE the dispatcher's internal enqueue lock.
        /// Subscribers MUST be thread-safe and MUST NOT re-enter the
        /// dispatcher synchronously.
        /// </summary>
        public event System.Action<long> OnGenericActionDropped;

        // Delegate slot used by the byte[] overload to record buffer-pair
        // rejections.  Carried through EnqueueCore so the rejection branch
        // can tick the dedicated counter without inspecting the WorkItem
        // shape on every call.
        private enum RejectionKind { Generic, RentedBuffer }

        /// <summary>
        /// Enqueue <paramref name="action"/> with a caller-supplied state object
        /// for execution on the Unity main thread.  The state pair lets callers
        /// pass a static method reference + arg without capturing locals,
        /// eliminating the per-call closure that the parameterless
        /// <see cref="Enqueue(Action)"/> overload otherwise allocates.
        /// </summary>
        /// <typeparam name="TArg">Caller-provided argument type.</typeparam>
        /// <param name="action">Static or cached delegate that consumes <paramref name="arg"/>.</param>
        /// <param name="arg">Value or reference passed verbatim to <paramref name="action"/>.</param>
        public void Enqueue<TArg>(Action<TArg> action, TArg arg)
        {
            if (action == null) return;
            // Re-shape Action<TArg> + TArg into Action<object> + object.  When
            // TArg is a reference type the boxing is a no-op cast; when it is
            // a value type the runtime boxes once per call (still cheaper than
            // a closure capturing a local plus the captured "this" pointer).
            EnqueueCore(new WorkItem(static state =>
            {
                var pair = ((Action<TArg>, TArg))state;
                pair.Item1(pair.Item2);
            }, (action, arg)), RejectionKind.Generic);
        }

        /// <summary>
        /// Hot-path receive overload.  Carries the (buffer, length) pair
        /// inline in the queue node so the per-packet enqueue allocates
        /// nothing on the managed heap — the caller passes a cached
        /// (typically static) <see cref="Action{T1, T2}"/> reference.
        /// </summary>
        /// <param name="action">Cached static delegate.</param>
        /// <param name="buffer">Caller-owned (typically pool-rented) byte buffer.</param>
        /// <param name="length">Meaningful prefix length inside <paramref name="buffer"/>.</param>
        /// <returns>
        /// <see langword="true"/> when the work item was queued and
        /// <paramref name="action"/> is guaranteed to run on the main thread;
        /// <see langword="false"/> when backpressure (DropTail / DropHead)
        /// rejected the call.  A <see langword="false"/> return means the
        /// dispatcher will NEVER invoke <paramref name="action"/>, so the
        /// caller MUST return <paramref name="buffer"/> to its pool to keep
        /// the rental account balanced.  The Throw policy still raises
        /// <see cref="InvalidOperationException"/> as before.
        /// </returns>
        /// <remarks>
        /// The dispatcher does NOT take ownership of <paramref name="buffer"/>.
        /// The receiver of the dispatched call is responsible for whatever
        /// pool-return / lifetime contract the buffer carries.
        /// </remarks>
        public bool Enqueue(Action<byte[], int> action, byte[] buffer, int length)
        {
            if (action == null || buffer == null) return false;
            return EnqueueCore(new WorkItem(action, buffer, length), RejectionKind.RentedBuffer);
        }

        private bool EnqueueCore(WorkItem item, RejectionKind kind)
        {
            // Lifted out of the locked region: a DropHead eviction that carries
            // a rented buffer is captured here and serviced AFTER the lock is
            // released.  Two reasons to do this:
            //   • A user-installed BufferReturnHandler that re-enters the
            //     dispatcher (or any code path that takes another lock that
            //     might be held by a thread waiting on _enqueueLock) would
            //     otherwise deadlock the producer.
            //   • ArrayPool.Return takes its own lock and zero-initialises
            //     the buffer; serialising that work behind _enqueueLock
            //     unnecessarily widens the producer-side critical section
            //     under backpressure.
            byte[] evictedBuffer = null;
            bool   evictedRented = false;

            bool result;
            lock (_enqueueLock)
            {
                // Read depth inside the lock so the check-policy-enqueue-increment
                // sequence is atomic with respect to other producers.  Update()'s
                // drain (TryDequeue + Interlocked.Decrement) never takes this lock,
                // so it can only make room — it cannot cause the depth to rise.
                int current = Volatile.Read(ref _depth);
                if (current >= MaxQueueDepth)
                {
                    switch (FullPolicy)
                    {
                        case DispatcherFullPolicy.DropHead:
                            // Remove the oldest pending action to make room for
                            // the new one.  Decrement only when we actually popped
                            // (Update may have already drained the queue).  When
                            // the popped item carries a rented buffer, the original
                            // network-thread caller already received an accepted=true
                            // and will never invoke Return itself.  Route the buffer
                            // through BufferReturnHandler so the rental is balanced
                            // and ArrayPool is not silently starved under sustained
                            // backpressure.
                            if (_queue.TryDequeue(out var evicted))
                            {
                                Interlocked.Decrement(ref _depth);
                                if (evicted.BufferAction != null)
                                {
                                    evictedRented = true;
                                    if (evicted.Buffer != null)
                                        evictedBuffer = evicted.Buffer;
                                }
                            }
                            RecordOverflow();
                            break;

                        case DispatcherFullPolicy.Throw:
                            RecordOverflow();
                            if (kind == RejectionKind.RentedBuffer)
                                BumpRentedDropAndNotify();
                            else
                                BumpGenericDropAndNotify();
                            throw new InvalidOperationException(
                                $"MainThreadDispatcher queue is full ({MaxQueueDepth} items). " +
                                "FullPolicy=Throw rejected the new action.");

                        case DispatcherFullPolicy.DropTail:
                        default:
                            RecordOverflow();
                            if (kind == RejectionKind.RentedBuffer)
                                BumpRentedDropAndNotify();
                            else
                                BumpGenericDropAndNotify();
                            return false;
                    }
                }

                _queue.Enqueue(item);
                Interlocked.Increment(ref _depth);
                result = true;
            }

            // Outside the lock: bump the dropped-rented counter and invoke the
            // (potentially user-supplied) BufferReturnHandler.  See the field
            // comments above for the deadlock scenario this avoids.
            if (evictedRented)
            {
                BumpRentedDropAndNotify();
                if (evictedBuffer != null)
                {
                    try { _bufferReturnHandler(evictedBuffer); }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw on DropHead eviction.\n{ex}");
                    }
                }
            }

            return result;
        }

        // Increments the rented-drop counter and raises the observability
        // event on every power-of-two transition (1, 2, 4, 8, …).  Mirrors
        // RecordOverflow's cadence so dashboards can correlate the two
        // signals 1:1 without polling.  Subscriber exceptions are isolated
        // so a buggy listener cannot starve the producer thread.
        private void BumpRentedDropAndNotify()
        {
            long total = Interlocked.Increment(ref _droppedRentedPacketCount);
            var handler = OnRentedPacketDropped;
            if (handler == null) return;
            if (total != 1 && (total & (total - 1)) != 0) return;
            try { handler(total); }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher.OnRentedPacketDropped subscriber threw.\n{ex}");
            }
        }

        // Symmetric counter + observability event for generic-shape work
        // items (Action, Action<TArg>).  A dropped main-thread continuation
        // is functionally indistinguishable from a network outage to the
        // application code that posted it — without this signal,
        // pending-RPC continuations, ownership-grant follow-ups, and
        // scene-load resolutions vanish without operator-visible
        // telemetry.
        private void BumpGenericDropAndNotify()
        {
            long total = Interlocked.Increment(ref _droppedGenericActionCount);
            var handler = OnGenericActionDropped;
            if (handler == null) return;
            if (total != 1 && (total & (total - 1)) != 0) return;
            try { handler(total); }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher.OnGenericActionDropped subscriber threw.\n{ex}");
            }
        }

        private void RecordOverflow()
        {
            // Saturate at long.MaxValue / 2 so the counter cannot wrap.  An
            // unchecked Interlocked.Increment past long.MaxValue rolls over
            // to long.MinValue, after which the (count & (count - 1)) == 0
            // power-of-two test fires every increment — the log cadence
            // contract breaks open into a flood.  Capping at half the range
            // gives effectively unlimited headroom (4.6 × 10^18 events) while
            // keeping the high bit clear, so the bitwise check stays sound.
            long count = Volatile.Read(ref _overflowCount);
            if (count < long.MaxValue / 2)
                count = Interlocked.Increment(ref _overflowCount);

            // Log on the first overflow and at every power-of-two overflow
            // afterwards (1, 2, 4, 8, 16, …).
            if (count == 1 || (count & (count - 1)) == 0)
            {
                Debug.LogError(
                    $"[RTMPE] MainThreadDispatcher: queue full ({MaxQueueDepth}); " +
                    $"policy={FullPolicy}; total overflow events={count}. " +
                    "This usually means the main thread is stalled or a background producer is misconfigured.");
            }
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Initialise the singleton reference for this MonoBehaviour instance.
        ///
       /// <para><b>Threading invariant:</b> Unity guarantees that
        /// <c>Awake</c> runs exclusively on the main thread.  The unsynchronised
        /// reads of <see cref="_instance"/> and <see cref="_mainThreadId"/> are
        /// therefore safe — no other thread can run <c>Awake</c> concurrently,
        /// and the only other writers (the static <see cref="Instance"/>
        /// accessor and <see cref="OnDestroy"/>) are themselves serialised
        /// under <see cref="_instLock"/>.  The lock around the assignment to
        /// <see cref="_instance"/> is preserved so the publication of the new
        /// reference happens under the same monitor that other threads acquire
        /// when they read it via <see cref="Instance"/>, providing the
        /// required release/acquire memory barrier.</para>
        /// </summary>
        private void Awake()
        {
            // Handle the edge case where Unity instantiates a second dispatcher
            // (e.g. scene has a prefab with this component).  This read is
            // safe without the lock because Awake is main-thread-only — see
            // the XML doc above for the full invariant.
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            // First Awake on the main thread always re-captures the id — the
            // SubsystemRegistration hook above already did this, but we belt-
            // and-brace in case a custom domain-reload sequence skipped it.
            if (_mainThreadId == 0)
                _mainThreadId = Thread.CurrentThread.ManagedThreadId;

            lock (_instLock)
            {
                _instance = this;
            }
        }

        private void Update()
        {
            int processed = 0;
            while (processed < MaxActionsPerFrame && _queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _depth);
                try
                {
                    item.Invoke();
                }
                catch (Exception ex)
                {
                    // Never swallow silently in production — log and continue.
                    Debug.LogError($"[RTMPE] MainThreadDispatcher: unhandled exception in dispatched action.\n{ex}");

                    // Buffer-ownership contract on the successful path is
                    // "the BufferAction returns the rental in its own
                    // finally" (see NetworkManager.ProcessPacketAndReturn).
                    // When the user's code throws BEFORE reaching that
                    // finally, no one returns the buffer — the producer
                    // already saw accepted=true and the consumer never
                    // completed.  Without this catch-side return every
                    // dispatched throw permanently drains a slot from the
                    // ArrayPool, slowly starving the receive path.  We
                    // route through the registered handler so non-default
                    // pool wiring (tests, custom rentals) sees the same
                    // path the eviction / drain branches already exercise.
                    if (item.BufferAction != null && item.Buffer != null)
                    {
                        try { _bufferReturnHandler(item.Buffer); }
                        catch (Exception bex)
                        {
                            Debug.LogError(
                                $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw " +
                                $"during in-flight return: {bex.Message}");
                        }
                    }
                }
                processed++;
            }
        }

        private void OnDestroy()
        {
            // Drain pending rented buffers before destruction so ArrayPool
            // retains accurate accounting across scene transitions and domain
            // reloads.  The queue may hold up to MaxQueueDepth WorkItems,
            // each potentially carrying a pool-rented byte[] whose original
            // producer already received accepted=true and will never call
            // Return itself — without this drain the rentals leak.
            DrainPendingBuffers();

            lock (_instLock)
            {
                if (_instance == this)
                    _instance = null;
            }
        }

        // Pull every queued WorkItem and route any rented buffer through the
        // installed BufferReturnHandler.  Runs OUTSIDE _instLock — the lock
        // guards _instance only and the handler is documented as safe to
        // invoke from arbitrary threads without dispatcher locks held.
        private void DrainPendingBuffers()
        {
            while (_queue.TryDequeue(out var item))
            {
                Interlocked.Decrement(ref _depth);
                if (item.BufferAction != null && item.Buffer != null)
                {
                    try { _bufferReturnHandler(item.Buffer); }
                    catch (Exception ex)
                    {
                        Debug.LogError(
                            $"[RTMPE] MainThreadDispatcher: BufferReturnHandler threw during shutdown drain: {ex.Message}");
                    }
                }
            }
        }

#if UNITY_INCLUDE_TESTS
        // Test seam: drives the same drain path the OnDestroy hook uses so
        // edit-mode tests can assert the buffer-return handler is invoked
        // for every queued rental.  Compiled only when
        // UNITY_INCLUDE_TESTS is defined so the shipped Player assembly
        // exposes only the OnDestroy lifetime path, never a manual drain
        // entry point.
        internal void DrainAndDisposeForTest() => DrainPendingBuffers();
#endif // UNITY_INCLUDE_TESTS

        /// <summary>
        /// Discards all callbacks queued by the background network thread and
        /// returns any rented buffers to the pool.  Call at session-boundary
        /// teardown — on the Unity main thread — to prevent closures captured
        /// during a now-defunct session from executing against the reconnected
        /// session's state.
        /// </summary>
        /// <remarks>
        /// Every <c>BufferAction</c> work item has its rented <c>byte[]</c>
        /// routed through the installed <see cref="BufferReturnHandler"/> so
        /// the pool retains accurate accounting.  Plain <c>Action</c> and
        /// <c>Action&lt;object&gt;</c> items are silently dropped — their
        /// callbacks are stale session-bound closures the caller has decided
        /// to suppress.
        /// </remarks>
        public void DiscardPendingCallbacks()
        {
            Debug.Assert(
                IsMainThread,
                "[RTMPE] MainThreadDispatcher.DiscardPendingCallbacks must be called from the Unity main thread.");
            DrainPendingBuffers();
        }
    }
}
