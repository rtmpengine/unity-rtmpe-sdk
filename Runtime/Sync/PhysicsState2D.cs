// RTMPE SDK — Runtime/Sync/PhysicsState2D.cs
//
// Plain value struct holding the 2-D physics fields synchronised over the
// network by NetworkRigidbody2D.
//
// Design decisions:
//   • Uses Vector2 for position/velocity (XY plane only).
//   • Rotation is a single float (Z-axis angle in degrees).
//     Matches Rigidbody2D.rotation which Unity exposes in degrees.
//   • AngularVelocity is a single float (degrees/second).
//     Matches Rigidbody2D.angularVelocity which Unity also exposes in deg/s.
//   • IsSleeping mirrors the 3-D convention; Rigidbody2D.IsSleeping() is the
//     corresponding Unity API call.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Snapshot of a 2-D <see cref="UnityEngine.Rigidbody2D"/> physics state.
    /// Produced by <see cref="NetworkRigidbody2D.GetState"/> and consumed by
    /// <see cref="NetworkRigidbody2D.ApplyRemoteState"/>.
    /// </summary>
    public struct PhysicsState2D
    {
        /// <summary>World-space 2-D position.</summary>
        public Vector2 Position;

        /// <summary>Z-axis rotation in degrees.</summary>
        public float Rotation;

        /// <summary>Linear velocity in units/second.</summary>
        public Vector2 Velocity;

        /// <summary>Angular velocity in degrees/second.</summary>
        public float AngularVelocity;

        /// <summary>True when the physics body is in the sleep state.</summary>
        public bool IsSleeping;
    }
}
