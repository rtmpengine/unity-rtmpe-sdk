// RTMPE SDK — Runtime/Sync/TransformState.cs
//
// Plain value struct holding the transform fields that are synchronised over
// the network by NetworkTransform.
//
// Design decisions:
//   • Uses UnityEngine.Vector3 and UnityEngine.Quaternion directly so that
//     NetworkTransform can assign/read Unity transform fields without an
//     intermediate conversion type.
//   • No UnityEngine behaviour or MonoBehaviour dependencies — pure data.
//   • TransformPacketBuilder and TransformPacketParser operate on this type,
//     making both serialisation and deserialisation paths type-safe.
//   • Identity static property gives a canonical "at rest" value useful for
//     initialisation and test default construction.

using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Snapshot of a networked object's transform at one point in time.
    /// Produced by <see cref="NetworkTransform.GetState"/> and consumed by
    /// <see cref="NetworkTransform.ApplyState"/>.
    /// </summary>
    public struct TransformState
    {
        /// <summary>World-space position.</summary>
        public Vector3 Position;

        /// <summary>World-space rotation as a unit quaternion.</summary>
        public Quaternion Rotation;

        /// <summary>Local (object-space) scale.</summary>
        public Vector3 Scale;

        /// <summary>
        /// A <see cref="TransformState"/> at the world origin with identity
        /// rotation and unit scale.  Useful as a safe default.
        /// </summary>
        public static TransformState Identity => new TransformState
        {
            Position = Vector3.zero,
            Rotation = Quaternion.identity,
            Scale    = Vector3.one,
        };
    }
}
