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

        /// <summary>
        /// Radius (world units) used for receive-side interest filtering in
        /// <see cref="RTMPE.Core.NetworkManager"/>.  State-sync packets for
        /// objects whose last known position is further away than this radius
        /// are silently discarded before being applied to the scene.
        ///
        /// <para>Set to 0 (default) to disable receive-side filtering entirely.
        /// The gateway already performs server-side culling; this filter is a
        /// secondary defence for games that need tighter client-side control,
        /// e.g. a large open world where many objects are technically
        /// "in the room" but irrelevant to nearby players.</para>
        ///
        /// <para>Typical value: 75 m (1.5× the default 50 m cell size), which
        /// matches the 3×3 neighbourhood covered by the spatial grid.</para>
        /// </summary>
        [Tooltip("Receive-side interest radius in world units. " +
                 "0 = disabled (gateway culling only).  Typical: 75 m.")]
        [Min(0f)]
        public float ReceiveFilterRadius = 0f;

        // ── Static local-position accessor (used by NetworkManager) ───────────

        // Active component instance (singleton invariant enforced via OnEnable /
        // OnDisable below).  Exposed so NetworkManager can read the live
        // ReceiveFilterRadius and last-sent position without a component
        // reference on the hot path.  null when no manager is active.
        private static InterestManager s_active;

        /// <summary>
        /// The last world-space position reported to the gateway by the
        /// active <see cref="InterestManager"/>, expressed as a pair of
        /// horizontal coordinates.  In XZ mode (3-D, default) the tuple is
        /// (worldX, worldZ); in XY mode (2-D / top-down) it is (worldX,
        /// worldY).  Callers that need to compare against an object's
        /// position must consult <see cref="LocalUsesXzPlane"/> to pick the
        /// matching axis on the remote object.
        /// Returns (0, 0) when no manager is active.
        /// </summary>
        internal static (float h1, float h2) LocalPosition
            => s_active == null ? (0f, 0f) : (s_active._lastSentX, s_active._lastSentY);

        /// <summary>
        /// True when the active <see cref="InterestManager"/> reports
        /// positions on the XZ plane (Y vertical — 3-D default), false when
        /// it reports on the XY plane (top-down / 2-D games).  Returns true
        /// when no manager is active so callers default to the 3-D
        /// interpretation, matching the historical receive-filter behavior.
        /// </summary>
        internal static bool LocalUsesXzPlane
            => s_active == null ? true : s_active.UseXzPlane;

        /// <summary>
        /// Receive-side interest radius exposed by the active
        /// <see cref="InterestManager"/>.  Zero when filtering is disabled or
        /// no manager is active.  Read fresh from the Inspector field every
        /// call so a runtime toggle of <see cref="ReceiveFilterRadius"/> takes
        /// effect on the very next packet — no 100 ms hysteresis.
        /// </summary>
        internal static float LocalReceiveRadius
            => s_active == null ? 0f : Mathf.Max(0f, s_active.ReceiveFilterRadius);

        /// <summary>
        /// True while an <see cref="InterestManager"/> instance is active,
        /// <see cref="ReceiveFilterRadius"/> is greater than zero, AND a
        /// position has actually been reported (so the (0, 0) origin is not
        /// used as a default that would silently reject every remote object).
        /// </summary>
        internal static bool IsReceiveFilterActive
            => s_active != null
               && s_active._hasSentOnce
               && s_active.ReceiveFilterRadius > 0f;

        // ── Private state ──────────────────────────────────────────────────────

        private float _accumulator;
        private float _lastSentX;
        private float _lastSentY;
        private bool  _hasSentOnce;
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

        private void OnEnable()
        {
            // Singleton invariant: the most recently enabled InterestManager
            // wins.  A second leftover instance from a scene transition will
            // overwrite the previous one, but never disturb it via an out-of-
            // order OnDisable (see below).
            s_active = this;
        }

        private void OnDisable()
        {
            // Only clear if WE are the active instance.  Without this guard,
            // disabling a second manager (left over from a scene transition)
            // would wipe coordinates owned by the live one.
            if (s_active == this)
                s_active = null;
        }

        private void SendCurrentPosition(NetworkManager nm)
        {
            // Without a tracked transform there is no authoritative source for
            // the local player's position.  Skip the send — and skip recording
            // _hasSentOnce — so the receive filter stays inactive (defaulting
            // to "deliver everything") instead of using (0, 0) as a trap that
            // would silently discard every remote object far from world origin.
            if (TrackedTransform == null) return;

            var pos = TrackedTransform.position;
            float x, y;
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

            _lastSentX   = x;
            _lastSentY   = y;
            _hasSentOnce = true;

            nm.SendPositionUpdate(x, y);
        }
    }
}
