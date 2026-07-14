// RTMPE SDK — Runtime/Sync/TransformState.cs
//
// Plain value struct holding the transform fields that are synchronised over
// the network by NetworkTransform.
//
// Design decisions:
//  • Uses UnityEngine.Vector3 and UnityEngine.Quaternion directly so that
//    NetworkTransform can assign/read Unity transform fields without an
//    intermediate conversion type.
//  • No UnityEngine behaviour or MonoBehaviour dependencies — pure data.
//  • TransformPacketBuilder and TransformPacketParser operate on this type,
//    making both serialisation and deserialisation paths type-safe.
//  • Identity static property gives a canonical "at rest" value useful for
//    initialisation and test default construction.

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
        /// The highest client input tick the server has incorporated into this
        /// transform (SDKS-01).  Meaningful only when
        /// <see cref="HasConfirmedInputTick"/> is true — tick 0 is a legitimate
        /// value, so presence is tracked by the flag rather than a zero
        /// sentinel.  On a server-authoritative <c>StateDelta</c> this is the
        /// watermark the owning client passes to the replay-aware
        /// <c>NetworkTransform.ApplyReconciliation</c> overload so it can trim
        /// its input buffer and replay only still-in-flight inputs.
        /// </summary>
        public uint ConfirmedInputTick;

        /// <summary>
        /// Whether <see cref="ConfirmedInputTick"/> carries a server-supplied
        /// value.  False for transforms that arrived without the SDKS-01
        /// input-tick field (legacy server, quantized relay, or local state),
        /// in which case reconciliation falls back to its local watermark.
        /// </summary>
        public bool HasConfirmedInputTick;

        /// <summary>
        /// The room's authoritative broadcast sequence at the tick this state
        /// was emitted — a per-room monotone 30 Hz counter.  Meaningful only
        /// when <see cref="HasServerTick"/> is true.  The non-owner receive path
        /// feeds it to <c>NetworkTransformInterpolator.AddStateFromSenderTick</c>
        /// so a remote object's render timeline tracks the broadcast cadence
        /// instead of the jittery local arrival clock.  Distinct from
        /// <see cref="ConfirmedInputTick"/>, which is the owner's reconciliation
        /// watermark; the two may be present together or apart.
        /// </summary>
        public uint ServerTick;

        /// <summary>
        /// Whether <see cref="ServerTick"/> carries a server-supplied value.
        /// False for transforms that arrived without the broadcast-clock field
        /// (a server with stamping disabled, the quantized relay, or local
        /// state), in which case interpolation falls back to the receiver-clock
        /// <c>AddState</c> path.
        /// </summary>
        public bool HasServerTick;

        /// <summary>
        /// A <see cref="TransformState"/> at the world origin with identity
        /// rotation and unit scale.  Useful as a safe default.
        /// </summary>
        public static TransformState Identity => new TransformState
        {
            Position              = Vector3.zero,
            Rotation              = Quaternion.identity,
            Scale                 = Vector3.one,
            ConfirmedInputTick    = 0u,
            HasConfirmedInputTick = false,
            ServerTick            = 0u,
            HasServerTick         = false,
        };
    }
}
