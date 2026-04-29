// RTMPE SDK — Runtime/Rooms/NetworkSceneManager.cs
//
// High-level scene-synchronisation façade that piggybacks on the Custom
// Properties channel introduced in Phase 1.  The authoritative scene name
// lives in the room's `custom_properties["__scene"]` reserved key; the host
// drives changes via RoomPropertyUpdate (0x24) and every other client
// receives the change as a normal property-update broadcast — which gives
// late-joiners free synchronisation for no extra migration cost.
//
// Scene-load readiness is reported separately via the SceneLoaded (0x2F)
// packet.  The Room Service aggregates reports and emits
// `all_players_scene_loaded` once the last player is ready; the SDK
// surfaces that as OnAllPlayersSceneLoaded.

using System;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// How a networked scene should be loaded on every client.  Mirrors
    /// <c>UnityEngine.SceneManagement.LoadSceneMode</c> so application code
    /// does not need to translate values.  Kept as a separate enum so this
    /// file never takes a hard dependency on <c>UnityEngine.SceneManagement</c>.
    /// </summary>
    public enum NetworkSceneLoadMode
    {
        /// <summary>Replace any currently loaded scene with the new one.</summary>
        Single = 0,

        /// <summary>Load the new scene alongside the currently loaded scene(s).</summary>
        Additive = 1,
    }

    /// <summary>
    /// Façade that drives networked scene loading from the master client and
    /// surfaces scene-transition events to every client in the room.
    ///
    /// The manager is stateless with respect to Unity's
    /// <c>SceneManagement</c> API — it only signals what scene should be
    /// active.  The application is responsible for calling
    /// <c>SceneManager.LoadSceneAsync</c> (or equivalent) in response to
    /// <see cref="OnSceneLoadStarted"/> and for calling
    /// <see cref="ReportReady"/> once the local scene has finished loading.
    ///
    /// Access via <see cref="NetworkManager.Scene"/>.
    /// </summary>
    public sealed class NetworkSceneManager
    {
        // Reconnect-safe: resolved each time the RoomManager identity is
        // checked (room-event bridging, property writes, scene-prune
        // requests) so a Reconnect-driven swap of the underlying RoomManager
        // is observed without dropping the long-lived public façade
        // reference held by the application.
        private readonly Func<RoomManager> _roomsProvider;

        // Most recently observed RoomManager.  Used to detect identity
        // changes so subscriptions are migrated atomically: unsubscribe from
        // the old, subscribe to the new, all under the main-thread contract.
        // Holds the dead instance across the very brief window between
        // Reconnect's call to RecreateRoomAndSpawnManagers and the next
        // method invocation that triggers EnsureBound().
        private RoomManager _bound;

        private string _lastObservedScene = string.Empty;
        private bool   _disposed;

        /// <summary>
        /// Fired when the server has accepted a new scene name and every
        /// client in the room should begin loading it.  Argument is the
        /// scene name carried by the <see cref="ReservedPropertyKeys.Scene"/>
        /// property.  Fires on every client, including the master that
        /// initiated the change.
        /// </summary>
        public event Action<string> OnSceneLoadStarted;

        /// <summary>
        /// Fired when every client has reported local scene-load completion
        /// for the same scene.  Argument is the scene name.  Application
        /// code typically waits for this event before starting the match.
        /// </summary>
        public event Action<string> OnAllPlayersSceneLoaded;

        /// <summary>
        /// The authoritative scene name currently loaded by the room, or
        /// empty string when no scene has been set yet.  Mirrors
        /// <see cref="RoomInfo.CurrentScene"/> for convenience.
        /// </summary>
        public string CurrentScene
        {
            get
            {
                EnsureBound();
                return _bound?.CurrentRoom?.CurrentScene ?? string.Empty;
            }
        }

        // Provider-based ctor (preferred, reconnect-safe).
        internal NetworkSceneManager(Func<RoomManager> roomsProvider)
        {
            _roomsProvider = roomsProvider ?? throw new ArgumentNullException(nameof(roomsProvider));
            EnsureBound();
        }

        // Legacy ctor retained for back-compat with tests / out-of-tree
        // callers that constructed with a fixed RoomManager.  The fixed
        // reference is wrapped in a constant provider so the rest of the
        // class can route exclusively through EnsureBound().
        internal NetworkSceneManager(RoomManager rooms)
        {
            if (rooms == null) throw new ArgumentNullException(nameof(rooms));
            _roomsProvider = () => rooms;
            EnsureBound();
        }

        /// <summary>
        /// Instruct the server (via the Custom Properties pipeline) that the
        /// room should transition to <paramref name="sceneName"/>.  Only the
        /// master client may call this; non-master callers log an error and
        /// return immediately so the bug is surfaced to the developer rather
        /// than producing a silent server-side no-op.
        /// </summary>
        /// <param name="sceneName">Scene name or path, as passed to
        /// <c>SceneManager.LoadSceneAsync</c>.  Must not be null or empty.</param>
        /// <param name="mode">Load mode.  <see cref="NetworkSceneLoadMode.Single"/>
        /// is the default and the most common choice.</param>
        public void LoadScene(string sceneName, NetworkSceneLoadMode mode = NetworkSceneLoadMode.Single)
        {
            if (string.IsNullOrEmpty(sceneName))
                throw new ArgumentException("sceneName must not be null or empty.", nameof(sceneName));

            EnsureBound();
            var rooms = _bound;
            if (rooms == null || !rooms.IsInRoom)
            {
                Debug.LogWarning("[RTMPE] NetworkSceneManager.LoadScene: must be in a room.");
                return;
            }
            // Only the master client may instruct the room to change scene.
            // Non-master callers are rejected here rather than letting the server
            // silently discard the property update, giving developers immediate
            // feedback instead of a silent no-op that is hard to diagnose.
            if (NetworkManager.Instance == null || !NetworkManager.Instance.IsMasterClient)
            {
                Debug.LogError("[RTMPE] NetworkSceneManager.LoadScene: only the master client may change the scene.");
                return;
            }

            // Robustness: prune any NetworkObjects whose GameObjects were
            // destroyed by an out-of-band scene unload BEFORE the server
            // broadcast lands.  Without this, a transitional gap can leave
            // the registry holding entries that compare equal to null when
            // the new scene's RegisterPrefab/Spawn cycle runs, producing
            // silent ID collisions if the gateway re-uses an id near the
            // wrap.  The host's sceneUnloaded handler already prunes; this
            // is a second, defensive sweep tied to the network-driven
            // transition rather than the engine's local unload event.
            var nm = NetworkManager.Instance;
            try { nm?.Spawner?.Registry?.PruneDestroyed(); }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    $"[RTMPE] NetworkSceneManager.LoadScene: registry prune threw " +
                    $"{ex.GetType().Name}: {ex.Message}.  Continuing — gateway broadcast still proceeds.");
            }

            var updates = new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene,         PropertyValue.OfString(sceneName) },
                { ReservedPropertyKeys.SceneAdditive, PropertyValue.OfBool(mode == NetworkSceneLoadMode.Additive) },
            };
            rooms.SetRoomProperties(updates);
        }

        /// <summary>
        /// Report to the server that the local client has finished loading
        /// the scene identified by <see cref="CurrentScene"/>.  No-op when
        /// not in a room or when no scene has been set.
        /// </summary>
        public void ReportReady()
        {
            EnsureBound();
            var scene = CurrentScene;
            if (string.IsNullOrEmpty(scene)) return;
            _bound?.ReportSceneLoaded(scene);
        }

        // ── Subscription management ───────────────────────────────────────

        // Re-bind subscriptions if the live RoomManager is no longer the one
        // we last subscribed to.  Called from every public/event entry
        // point so a Reconnect that swaps the manager between calls is
        // observed at the next interaction.  Idempotent — when the live
        // instance equals _bound (steady state) the method returns
        // immediately without touching the event delegates.
        private void EnsureBound()
        {
            if (_disposed) return;
            var live = _roomsProvider();
            if (ReferenceEquals(live, _bound)) return;

            // Detach from the previous instance — even if it has been
            // discarded by NetworkManager, our delegates are still rooted
            // in its event invocation list.  Without explicit detach the
            // dead instance leaks until full GC sweeps both objects.
            if (_bound != null)
            {
                _bound.OnRoomPropertiesChanged -= HandleRoomPropertiesChanged;
                _bound.OnRoomJoined            -= HandleRoomJoined;
                _bound.OnRoomLeft              -= HandleRoomLeft;
                _bound.OnAllPlayersSceneLoaded -= HandleAllReady;
            }

            _bound = live;
            // Reset the per-session scene-watcher: the new RoomManager has
            // a clean CurrentRoom history, so the first OnRoomJoined or
            // OnRoomPropertiesChanged it raises must be honoured even if
            // the scene name happens to match the one we observed on the
            // previous (defunct) instance.
            _lastObservedScene = string.Empty;

            if (_bound != null)
            {
                _bound.OnRoomPropertiesChanged += HandleRoomPropertiesChanged;
                _bound.OnRoomJoined            += HandleRoomJoined;
                _bound.OnRoomLeft              += HandleRoomLeft;
                _bound.OnAllPlayersSceneLoaded += HandleAllReady;
            }
        }

        // ── Room-event bridging ──────────────────────────────────────────

        private void HandleRoomPropertiesChanged(RoomInfo room)
        {
            if (room == null) return;
            var scene = room.CurrentScene;
            if (string.IsNullOrEmpty(scene) || scene == _lastObservedScene) return;
            _lastObservedScene = scene;
            OnSceneLoadStarted?.Invoke(scene);
        }

        private void HandleRoomJoined(RoomInfo room)
        {
            if (room == null) return;
            // Late-join path: if the room already has an authoritative scene,
            // fire OnSceneLoadStarted immediately so the client catches up.
            var scene = room.CurrentScene;
            if (string.IsNullOrEmpty(scene))
            {
                _lastObservedScene = string.Empty;
                return;
            }
            _lastObservedScene = scene;
            OnSceneLoadStarted?.Invoke(scene);
        }

        private void HandleRoomLeft()
        {
            _lastObservedScene = string.Empty;
        }

        private void HandleAllReady(string sceneName)
        {
            OnAllPlayersSceneLoaded?.Invoke(sceneName);
        }

        /// <summary>
        /// Detach from the underlying <see cref="RoomManager"/> events.
        /// Called by <see cref="NetworkManager"/> during cleanup so the
        /// manager does not keep a stale room reference alive after the
        /// socket has been torn down.
        /// </summary>
        internal void Dispose()
        {
            _disposed = true;
            if (_bound == null) return;
            _bound.OnRoomPropertiesChanged -= HandleRoomPropertiesChanged;
            _bound.OnRoomJoined            -= HandleRoomJoined;
            _bound.OnRoomLeft              -= HandleRoomLeft;
            _bound.OnAllPlayersSceneLoaded -= HandleAllReady;
            _bound = null;
        }
    }
}
