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
        private readonly RoomManager _rooms;
        private string _lastObservedScene = string.Empty;

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
        public string CurrentScene =>
            _rooms?.CurrentRoom?.CurrentScene ?? string.Empty;

        internal NetworkSceneManager(RoomManager rooms)
        {
            _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _rooms.OnRoomPropertiesChanged      += HandleRoomPropertiesChanged;
            _rooms.OnRoomJoined                 += HandleRoomJoined;
            _rooms.OnRoomLeft                   += HandleRoomLeft;
            _rooms.OnAllPlayersSceneLoaded      += HandleAllReady;
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
            if (_rooms == null || !_rooms.IsInRoom)
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

            var updates = new System.Collections.Generic.Dictionary<string, PropertyValue>
            {
                { ReservedPropertyKeys.Scene,         PropertyValue.OfString(sceneName) },
                { ReservedPropertyKeys.SceneAdditive, PropertyValue.OfBool(mode == NetworkSceneLoadMode.Additive) },
            };
            _rooms.SetRoomProperties(updates);
        }

        /// <summary>
        /// Report to the server that the local client has finished loading
        /// the scene identified by <see cref="CurrentScene"/>.  No-op when
        /// not in a room or when no scene has been set.
        /// </summary>
        public void ReportReady()
        {
            var scene = CurrentScene;
            if (string.IsNullOrEmpty(scene)) return;
            _rooms.ReportSceneLoaded(scene);
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
            if (_rooms == null) return;
            _rooms.OnRoomPropertiesChanged -= HandleRoomPropertiesChanged;
            _rooms.OnRoomJoined            -= HandleRoomJoined;
            _rooms.OnRoomLeft              -= HandleRoomLeft;
            _rooms.OnAllPlayersSceneLoaded -= HandleAllReady;
        }
    }
}
