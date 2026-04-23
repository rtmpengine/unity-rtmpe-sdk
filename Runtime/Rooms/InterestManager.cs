// RTMPE SDK — Runtime/Rooms/InterestManager.cs
//
// Feature #6: Interest Management — client-side position reporting.
//
// InterestManager is a MonoBehaviour that periodically sends the local player's
// 2-D world position to the gateway via PositionUpdate (0x42) packets.  The
// gateway uses the position to apply spatial-grid culling so that room-wide
// broadcasts from the Sync Engine only reach clients whose 3×3 cell
// neighbourhood (default: 150 m × 150 m with a 50 m cell size) overlaps the
// source position embedded in the broadcast.
//
// Clients that never attach InterestManager (or that call StopTracking())
// receive every room-wide broadcast unchanged — opt-in semantics preserve
// full backwards compatibility.
//
// Usage:
//   Add InterestManager to the local player's GameObject, or to any persistent
//   manager object, and assign the tracked Transform.  The component sends
//   positions automatically while the player is in a room.
//
// Protocol:
//   Packet 0x42 payload — [x: f32 LE][y: f32 LE] (8 bytes)
//   The x/y values are the tracked transform's world position projected onto
//   the XZ plane (Y is vertical in Unity 3D games; for 2-D games, use Y).
//
// Gateway counterpart:
//   modules/gateway/src/interest/spatial_grid.rs
//   modules/gateway/src/session/store.rs  (AppSession.position)
//   modules/gateway/src/nats/broadcast.rs (zone-filtered dispatch)

using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Rooms
{
    /// <summary>
    /// Periodically reports the local player's 2-D world position to the RTMPE
    /// gateway so it can apply spatial interest filtering to room-wide broadcasts.
    ///
    /// <para>Attach to any persistent GameObject while the player is in a room.
    /// Assign <see cref="TrackedTransform"/> to the player's Transform (or any
    /// Transform whose position represents the player's world location).</para>
    ///
    /// <para>If <see cref="TrackedTransform"/> is <see langword="null"/> when a
    /// send tick fires, the last successfully sent position is re-sent so the
    /// gateway retains a valid interest zone.</para>
    /// </summary>
    public sealed class InterestManager : MonoBehaviour
    {
        // ── Inspector / public fields ──────────────────────────────────────────

        /// <summary>
        /// Transform whose world position is reported to the gateway.
        /// Typically the local player's root Transform.
        /// When <see langword="null"/>, the last known position is re-sent.
        /// </summary>
        [Tooltip("The Transform whose world-space position is sent to the gateway. " +
                 "Assign the local player's root Transform.")]
        public Transform TrackedTransform;

        /// <summary>
        /// How often the position is sent to the gateway (seconds).
        /// Lower values reduce latency but increase uplink bandwidth.
        /// Default: 0.1 s (10 Hz) — suitable for most action games.
        /// </summary>
        [Tooltip("Position update interval in seconds (default 0.1 s = 10 Hz).")]
        [Range(0.05f, 5f)]
        public float UpdateInterval = 0.1f;

        /// <summary>
        /// When enabled, positions are projected onto the XZ plane (Y = vertical).
        /// Disable for top-down or 2-D games where Y is the horizontal axis.
        /// Default: true (standard Unity 3-D convention).
        /// </summary>
        [Tooltip("Project position onto XZ plane (Y is vertical). " +
                 "Disable for 2-D or top-down games that use X/Y coordinates.")]
        public bool UseXzPlane = true;

        // ── Private state ──────────────────────────────────────────────────────

        private float _accumulator;
        private float _lastSentX;
        private float _lastSentY;
        private bool  _tracking = true;

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Pause position reporting.  The gateway retains the last position
        /// and continues applying the interest zone until tracking resumes
        /// or the session ends.
        /// </summary>
        public void StopTracking()  => _tracking = false;

        /// <summary>Resume position reporting after <see cref="StopTracking"/>.</summary>
        public void StartTracking() => _tracking = true;

        /// <summary>
        /// <see langword="true"/> while the component is actively sending position
        /// updates to the gateway.
        /// </summary>
        public bool IsTracking => _tracking && enabled;

        // ── Unity lifecycle ────────────────────────────────────────────────────

        private void Update()
        {
            if (!_tracking) return;

            var nm = NetworkManager.Instance;
            if (nm == null || !nm.IsInRoom) return;

            _accumulator += Time.deltaTime;
            if (_accumulator < UpdateInterval) return;

            _accumulator -= UpdateInterval;
            SendCurrentPosition(nm);
        }

        // ── Internal ───────────────────────────────────────────────────────────

        private void SendCurrentPosition(NetworkManager nm)
        {
            float x, y;

            if (TrackedTransform != null)
            {
                var pos = TrackedTransform.position;
                if (UseXzPlane)
                {
                    x = pos.x;
                    y = pos.z;
                }
                else
                {
                    x = pos.x;
                    y = pos.y;
                }
                _lastSentX = x;
                _lastSentY = y;
            }
            else
            {
                // No transform assigned — re-send the last known position so
                // the gateway keeps a valid interest zone for this client.
                x = _lastSentX;
                y = _lastSentY;
            }

            nm.SendPositionUpdate(x, y);
        }
    }
}
