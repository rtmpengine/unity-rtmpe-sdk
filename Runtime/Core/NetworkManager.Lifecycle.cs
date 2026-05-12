// RTMPE SDK — Runtime/Core/NetworkManager.Lifecycle.cs
//
// Unity MonoBehaviour lifecycle (Awake/Start/Update/OnDestroy) + scene handlers.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
        // ── Unity lifecycle ────────────────────────────────────────────────────

        private void Awake()
        {
            lock (_instLock)
            {
                if (_instance != null && _instance != this)
                {
                    Destroy(gameObject);
                    return;
                }
                _instance = this;
            }

            DontDestroyOnLoad(gameObject);

            if (_settings == null)
                _settings = NetworkSettings.CreateDefault();

            // Hand the active settings to the gated logger so transport-level
            // errors honour the verbose-logs preference instead of being
            // ingested as crashes by every reporter installed in the host app.
            RtmpeLog.SetActiveSettings(_settings);

            // Mirror the deployment's lobby/room string-length cap into the
            // packet parser's static budget so the parser rejects oversize
            // strings consistently with the inspector contract — without
            // this hand-off the parser's hard 4 KiB ceiling silently
            // overrides a stricter value the operator configured.
            RoomPacketParser.ConfigureMaxStringBytes(_settings.maxLobbyStringBytes);

            // Coerce any non-finite world-bounds vectors back to a sane
            // default.  An OnValidate hook covers the Editor path; this
            // call covers runtime asset loads (Addressables, AssetBundles)
            // and code-built NetworkSettings instances that would otherwise
            // bypass the Editor-only validation.
            _settings.EnsureFiniteWorldBoundsForRuntime();

            // Resolve the runtime tick interval from the settings' configured
            // tickRate.  TickInterval is guarded against division by zero by
            // NetworkSettings (Mathf.Max(1, tickRate)); the extra positive
            // check below catches any future regression where a hand-built
            // settings instance bypasses the inspector range.
            ResolveTickInterval();

            // Subscribe to scene-load events BEFORE InitialiseNetwork so that
            // a scene load that races with the first frame doesn't leak stale
            // NetworkObject registry entries.  sceneUnloaded fires AFTER Unity
            // has destroyed all GameObjects in the unloaded scene — the
            // registry may then hold a bunch of managed references that
            // compare equal to null.  PruneDestroyed() sweeps them out so the
            // registry count matches the number of live objects.
            SceneManager.sceneUnloaded += HandleSceneUnloaded;
            SceneManager.sceneLoaded   += HandleSceneLoaded;

            InitialiseNetwork();
        }

        // Resolves _variableFlushInterval from the active NetworkSettings'
        // configured tickRate.  Falls back to the historical 1/30 s default
        // when no settings are configured or when a hand-built instance
        // somehow surfaces a non-positive interval.  Centralising the
        // resolution lets a future hot-reload path (settings asset
        // re-imported in the editor) refresh the cadence with one call.
        private void ResolveTickInterval()
        {
            float interval = (_settings != null && _settings.TickInterval > 0f)
                ? _settings.TickInterval
                : DefaultVariableFlushInterval;
            _variableFlushInterval = interval;
        }

        private void Start()
        {
            // Warm the MainThreadDispatcher singleton HERE (main thread, before any
            // background threads are started) so the first Enqueue() call is free
            // of the one-time GameObject allocation cost.
            _dispatcher = MainThreadDispatcher.Instance;

            // Cache the heartbeat send callback once so Update() does
            // not allocate a new closure object every frame.
            // Route through EncryptAndSend so heartbeat packets are AEAD-encrypted
            // once the session is established (i.e. _sessionKeyStore.IsReady).
            _sendPacketCallback = packet => EncryptAndSend(packet);

            // Cache the SendVariableUpdate delegate once to eliminate
            // per-tick allocation on the 30 Hz flush path.
            _sendVariableUpdateDelegate = SendVariableUpdate;

            // VariableBatchManager owns the per-tick accumulator, scratch buffer,
            // active-cap setting, and cached collector delegate.
            // Initialised here so the SendVariableBatchUpdate / SendVariableUpdate
            // method-group references resolve against this NetworkManager.
            _variableBatchManager = new RTMPE.Core.Sync.VariableBatchManager(
                SendVariableBatchUpdate, SendVariableUpdate);
        }

        private void Update()
        {
            // Drive the heartbeat tick each frame using the pre-cached callback.
            _heartbeatManager?.Tick(_sendPacketCallback);

            // Drive the outbound ARQ retransmit ladder once per frame.  The
            // resend callback re-runs the AEAD pipeline with the same
            // arq_seq the entry was registered under so the receiver's
            // cumulative-ACK clearing logic stays coherent across retries.
            // `onDropped` keeps the cap-exhaustion event observable in the
            // Unity console without coupling production callers to the
            // ReliableChannel API surface.
            if (_settings != null && _settings.EmitArqSequence)
            {
                _outboundReliableChannel.Tick(
                    (float)Time.unscaledTimeAsDouble,
                    resend: (seq, payload) =>
                        EncryptAndSendInternal(payload, hasFixedArqSeq: true, fixedArqSeq: seq),
                    onDropped: seq =>
                    {
                        if (IsDebugLogEnabled)
                            LogDebug($"ReliableChannel: arq_seq={seq} dropped after MaxAttempts.");
                    });
            }

            // Drive matchmaking timeout/cancel state machine in main-thread time
            // so callers don't need to wire app-level Tick plumbing.
            _matchmakingManager?.Tick(Time.unscaledTimeAsDouble);

            // Flush dirty NetworkVariables at 30 Hz for all owned objects.
            //
            // The catch-up loop drains the accumulator with a `while` rather
            // than a single `if` so a frame that ran long (Editor breakpoint,
            // GC stall, dropped-foreground on mobile) advances every tick it
            // owes instead of leaking the surplus into wall-clock drift.
            // MaxTicksPerFrame caps the worst-case work to keep a long pause
            // from turning into a multi-second hitch on resume.
            _variableFlushAccum += Time.deltaTime;
            int ticksThisFrame = 0;
            // Snapshot the resolved interval to a local so a settings change
            // mid-loop (e.g. on a settings-asset reimport in the editor)
            // cannot cause divide-by-zero or infinite-loop pathologies if it
            // races to zero between catch-up iterations.
            float flushInterval = _variableFlushInterval;
            if (flushInterval <= 0f) flushInterval = DefaultVariableFlushInterval;
            while (_variableFlushAccum >= flushInterval
                   && ticksThisFrame < MaxTicksPerFrame)
            {
                _variableFlushAccum -= flushInterval;
                // Advance the CSP tick in lock-step with the variable flush
                // so that InputPayload.Tick and NetworkVariable deltas share
                // the same configured cadence.  Only advance while in a room
                // — ticks outside a room are meaningless for CSP.
                if (IsInRoom) _localTick++;
                // Driving per-tick collectors (input sampling, etc.) from a
                // single while-loop tick driver guarantees exactly one sample
                // per simulated tick, even on long frames; otherwise inputs
                // are silently lost on stutters or double-collected at
                // sub-tick frame rates.  Dispatched BEFORE FlushDirtyNetworkVariables
                // so any input-driven NetworkVariable mutation produced inside
                // OnFixedTick is published in the same tick it was sampled.
                DispatchFixedTick(flushInterval);
                FlushDirtyNetworkVariables();
                ticksThisFrame++;
            }
            if (_variableFlushAccum >= flushInterval)
            {
                // The cap above prevents the simulation from chasing an
                // arbitrarily-large hitch all in one frame; surrender the
                // residual time to wall-clock to avoid permanent backlog.
                _variableFlushAccum = 0f;
            }

            // Sweep expired RPC callbacks every 5 seconds so pending entries
            // from unanswered or timed-out requests do not accumulate indefinitely.
            _rpcPurgeAccum += Time.deltaTime;
            if (_rpcPurgeAccum >= RpcPurgeInterval)
            {
                _rpcPurgeAccum = 0f;
                RequestIdAllocator.PurgeExpired();
            }

            // Drive a periodic prune of the SpawnManager's pending-despawn
            // tracker.  CreateLocal / DestroyLocal are the only other prune
            // paths and they are not guaranteed to fire after the last
            // out-of-order Despawn of a session — so without this driver
            // any leftover entries linger until the next room or process
            // restart.  Cadence matches the tracker TTL window so an entry
            // is never more than one period late on its expiry.
            _pendingDespawnPruneAccum += Time.deltaTime;
            if (_pendingDespawnPruneAccum >= PendingDespawnPruneInterval)
            {
                _pendingDespawnPruneAccum = 0f;
                if (_spawnManager != null)
                    _spawnManager.PruneIfStale(_spawnManager.PendingDespawnNowMillis());
            }
        }

        /// <summary>
        /// Flush dirty NetworkVariables for all objects owned by the local player.
        /// Called at 30 Hz from Update().
        /// </summary>
        private void FlushDirtyNetworkVariables()
        {
            if (_spawnManager == null || !IsInRoom) return;
            // Walk via a private snapshot list — flushing a NetworkVariable
            // can fire user value-change callbacks that may legally re-enter
            // the registry (e.g. spawn a follower object), and the shared
            // GetAll buffer must not be parked across that re-entry.
            _spawnManager.Registry.GetAllSnapshot(_flushScratch);

            // When variable batching is enabled, every per-object payload is
            // diverted into the VariableBatchManager instead of being emitted
            // as a standalone 0x41 packet.  After the iteration the
            // accumulated payloads are encoded into one or more 0x44 batch
            // packets.  At the cap the partial batch is flushed eagerly so
            // the receiver never blocks waiting for more entries inside the
            // same tick — that invariant is enforced inside
            // VariableBatchManager.CollectIntoBatch.
            bool batching = _settings != null && _settings.enableVariableBatching;
            int  batchCap = batching && _settings != null
                ? UnityEngine.Mathf.Clamp(_settings.maxVariablesPerBatch, 1, VariableBatchBuilder.MaxEntries)
                : 0;

            // Snapshot the cap onto the manager so its cached collector
            // delegate consults a stable value across the iteration —
            // avoids torn reads if the NetworkSettings asset is
            // hot-reloaded mid-flush.
            _variableBatchManager.SetActiveCap(batchCap);
            System.Action<byte[], int> sender = batching
                ? _variableBatchManager.Collector
                : _sendVariableUpdateDelegate;

            for (int i = 0; i < _flushScratch.Count; i++)
            {
                var nb = _flushScratch[i];
                if (nb == null || !nb.IsOwner || !nb.IsSpawned) continue;
                nb.FlushDirtyVariables(sender);
            }
            _flushScratch.Clear();

            if (batching && _variableBatchManager.HasPending)
            {
                _variableBatchManager.FlushPending();
            }
        }

        // VariableBatchManager owns the per-tick accumulator, scratch buffer,
        // and the cached collector delegate. See
        // Runtime/Core/Sync/VariableBatchManager.cs for the cap-eager-flush
        // contract and the on-builder-failure fallback path.
        private RTMPE.Core.Sync.VariableBatchManager _variableBatchManager;

        // Pre-allocated buffers for the per-frame and per-tick dispatch loops.
        // Kept on this NetworkManager so a user callback that triggers another
        // GetAll cannot stomp the in-flight iteration.
        private readonly System.Collections.Generic.List<NetworkBehaviour> _flushScratch =
            new System.Collections.Generic.List<NetworkBehaviour>(64);
        private readonly System.Collections.Generic.List<NetworkBehaviour> _fixedTickScratch =
            new System.Collections.Generic.List<NetworkBehaviour>(64);

        /// <summary>
        /// Invoke <see cref="NetworkBehaviour.InvokeOnFixedTick"/> on every
        /// owned, spawned NetworkBehaviour exactly once per simulated tick.
        /// Centralising the call here lets a long frame (Editor breakpoint,
        /// GC stall, dropped-foreground on mobile) run every tick it owes
        /// rather than dropping accumulated input — Update()-hosted
        /// collectors observe one frame's deltaTime regardless of how many
        /// ticks the accumulator advances.
        /// </summary>
        private void DispatchFixedTick(float dt)
        {
            if (_spawnManager == null || !IsInRoom) return;
            // Snapshot via a private list so a user OnFixedTick implementation
            // that re-enters the registry cannot perturb the iteration order.
            _spawnManager.Registry.GetAllSnapshot(_fixedTickScratch);
            for (int i = 0; i < _fixedTickScratch.Count; i++)
            {
                var nb = _fixedTickScratch[i];
                if (nb == null || !nb.IsOwner || !nb.IsSpawned) continue;
                try
                {
                    nb.InvokeOnFixedTick(dt);
                }
                catch (Exception ex)
                {
                    // A single misbehaving subclass must not abort the rest of
                    // the per-tick dispatch — log and move on.
                    Debug.LogError(
                        $"[RTMPE] NetworkBehaviour.OnFixedTick threw on " +
                        $"{nb.GetType().Name}: {ex.GetType().Name}: {ex.Message}", nb);
                }
            }
            _fixedTickScratch.Clear();
        }

        private void OnDestroy()
        {
            lock (_instLock)
            {
                if (_instance == this) _instance = null;
            }

            // Drop the gated logger's reference so a stale settings asset is
            // not retained across scene reloads.
            RtmpeLog.SetActiveSettings(null);

            // Unsubscribe BEFORE Cleanup so that a scene-unload fired as part
            // of shutdown doesn't re-enter our handler after fields are nulled.
            SceneManager.sceneUnloaded -= HandleSceneUnloaded;
            SceneManager.sceneLoaded   -= HandleSceneLoaded;

            StopAllCoroutines();
            Cleanup();
        }

        // ── Scene lifecycle ────────────────────────────────────────────────────

        /// <summary>
        /// Unity raises this AFTER a scene is unloaded — the GameObjects in
        /// that scene have already been destroyed.  Sweep the NetworkObject
        /// registry to evict any entry whose GameObject compares equal to
        /// null, preventing a slow leak when apps use additive scene loading.
        /// </summary>
        /// <remarks>
        /// We do NOT attempt to send despawn packets for the pruned objects —
        /// the authoritative side (gateway + room service) still tracks them.
        /// Apps that want server-side cleanup should call <c>Despawn(objectId)</c>
        /// explicitly before unloading the scene, or use <c>DestroyWithOwner</c>
        /// in combination with a room leave.
        /// </remarks>
        private void HandleSceneUnloaded(Scene scene)
        {
            // Serialize against HandleSceneLoaded and RecreateRoomAndSpawnManagers.
            // The try / finally would be load-bearing if Monitor.Enter could
            // throw on its own — using a lock statement is equivalent and the
            // exit is guaranteed even if PruneDestroyed throws (the inner
            // try/catch swallows it before the lock unwinds).
            lock (_sceneTransitionLock)
            {
                if (_spawnManager?.Registry == null) return;

                int pruned;
                try { pruned = _spawnManager.Registry.PruneDestroyed(); }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] NetworkObjectRegistry.PruneDestroyed threw " +
                        $"{ex.GetType().Name}: {ex.Message}. Skipping prune this cycle.");
                    return;
                }

                if (pruned > 0)
                    LogDebug(
                        $"Scene \"{scene.name}\" unloaded — pruned {pruned} stale NetworkObject entr" +
                        (pruned == 1 ? "y" : "ies") + " from registry.");
            }
        }

        /// <summary>
        /// Unity raises this when a new scene finishes loading.  Single-scene
        /// loads (<see cref="LoadSceneMode.Single"/>) destroy all scene
        /// objects that were not <c>DontDestroyOnLoad</c>, so we prune here
        /// too — <see cref="HandleSceneUnloaded"/> alone does not cover the
        /// case where the PREVIOUS scene was unloaded by the Single mode
        /// before the new load completed.
        /// </summary>
        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;

            // Serialize with HandleSceneUnloaded so a Single-mode load that
            // raises both events back-to-back observes a consistent registry.
            lock (_sceneTransitionLock)
            {
                if (_spawnManager?.Registry == null) return;

                try { _spawnManager.Registry.PruneDestroyed(); }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[RTMPE] NetworkObjectRegistry.PruneDestroyed threw " +
                        $"{ex.GetType().Name}: {ex.Message}. Skipping prune this cycle.");
                }
            }
        }

        private void OnApplicationQuit()
        {
            System.Threading.Volatile.Write(ref _applicationIsQuitting, true);
            Cleanup();
        }

    }
}
