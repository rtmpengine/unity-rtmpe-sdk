// RTMPE SDK — Runtime/Sync/PhysicsState.cs
//
// Plain value struct holding the 3-D physics fields synchronised over the
// network by NetworkRigidbody.
//
// Design decisions:
//   • Mirrors TransformState but adds Velocity, AngularVelocity, and IsSleeping
//     for Rigidbody-driven objects.  Position and Rotation are included so the
//     physics component is self-contained and does not require a co-located
//     NetworkTransform.
//   • Uses UnityEngine types directly — no intermediate conversion type.
//   • IsSleeping enables remote Rigidbodies to enter sleep state when the owner
//     physics engine idles the body, eliminating micro-movement from floating-
//     point noise on stationary objects.
//   • AngularVelocity is in radians/second (Unity's native Rigidbody unit).

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Snapshot of a 3-D <see cref="UnityEngine.Rigidbody"/> physics state.
    /// Produced by <see cref="NetworkRigidbody.GetState"/> and consumed by
    /// <see cref="NetworkRigidbody.ApplyRemoteState"/>.
    /// </summary>
    public struct PhysicsState
    {
        /// <summary>World-space position.</summary>
        public Vector3 Position;

        /// <summary>World-space rotation as a unit quaternion.</summary>
        public Quaternion Rotation;

        /// <summary>World-space linear velocity in units/second.</summary>
        public Vector3 Velocity;

        /// <summary>World-space angular velocity in radians/second.</summary>
        public Vector3 AngularVelocity;

        /// <summary>True when the physics body is in the sleep state.</summary>
        public bool IsSleeping;
    }
}
