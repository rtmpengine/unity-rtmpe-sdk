// RTMPE SDK — Runtime/Sync/RemoteMotionGate.cs
//
// Plausibility gate for inbound position snapshots of non-owner networked
// objects.
//
// A non-owner object is driven entirely by snapshots the receiver decodes
// from the network; the receiver has no authority over the motion and no
// way to re-derive it.  Without a bound, a peer that streams arbitrary
// coordinates makes its replica appear to teleport across the world on every
// other client's screen.  This helper bounds the position step between two
// consecutive snapshots to the displacement reachable at a configured speed
// ceiling, mirroring the per-update displacement cap NetworkRigidbody already
// applies on its remote-state path.
//
// The logic is a pure function — no Unity scene, no component instance — so
// the bound can be exercised directly under the headless test runner.

using System;
using UnityEngine;

namespace RTMPE.Sync
{
    /// <summary>
    /// Pure helpers that bound a remote-object position step to a physically
    /// plausible displacement.
    /// </summary>
    internal static class RemoteMotionGate
    {
        /// <summary>
        /// Clamp <paramref name="candidate"/> to the displacement reachable
        /// from <paramref name="previous"/> within the elapsed interval.
        ///
        /// <para>The reachable displacement is
        /// <c>maxSpeed × dt + stepFloor</c>: the speed term scales with the
        /// real interval between snapshots, and the floor absorbs
        /// quantization noise and the degenerate case of two snapshots that
        /// resolve to the same timestamp (<c>dt ≤ 0</c>).  A candidate within
        /// that budget is returned unchanged; a candidate beyond it is pulled
        /// back along the line to <paramref name="previous"/> so the replica
        /// advances at the ceiling instead of snapping to the claimed point.</para>
        /// </summary>
        /// <param name="previous">Last accepted position.</param>
        /// <param name="candidate">Inbound position to validate.</param>
        /// <param name="dtSeconds">
        /// Seconds elapsed since <paramref name="previous"/> was accepted.
        /// A non-positive value contributes no speed budget; only
        /// <paramref name="stepFloor"/> applies.
        /// </param>
        /// <param name="maxSpeed">
        /// Speed ceiling in world units per second.  Callers gate on
        /// <c>maxSpeed &gt; 0</c> before invoking; a non-positive value here
        /// collapses the budget to <paramref name="stepFloor"/> alone.
        /// </param>
        /// <param name="stepFloor">
        /// Minimum permitted displacement regardless of the interval, in world
        /// units.  Must be non-negative.
        /// </param>
        /// <returns>
        /// <paramref name="candidate"/> when it is within budget; otherwise the
        /// point on the segment <c>previous → candidate</c> at the budget distance.
        /// </returns>
        public static Vector3 ClampPositionStep(
            Vector3 previous,
            Vector3 candidate,
            double  dtSeconds,
            float   maxSpeed,
            float   stepFloor)
        {
            // Componentwise delta — read only .x/.y/.z so the helper has no
            // dependency on Vector3 operator overloads.
            float dx = candidate.x - previous.x;
            float dy = candidate.y - previous.y;
            float dz = candidate.z - previous.z;

            // Accumulate the squared distance in double precision: a large
            // teleport delta squared can exceed the float mantissa's exact
            // range, and the comparison below must not lose the overflow.
            double distSq = (double)dx * dx + (double)dy * dy + (double)dz * dz;

            double dt      = dtSeconds  > 0.0 ? dtSeconds  : 0.0;
            double speed   = maxSpeed   > 0f  ? maxSpeed   : 0f;
            double floor   = stepFloor  > 0f  ? stepFloor  : 0f;
            double allowed = speed * dt + floor;

            // Within the displacement budget — accept verbatim.
            if (distSq <= allowed * allowed)
                return candidate;

            // Degenerate previous == candidate cannot reach here (distSq would
            // be 0); guard the normalisation anyway so a sub-epsilon delta
            // never divides by zero.
            double dist = Math.Sqrt(distSq);
            if (dist <= double.Epsilon)
                return candidate;

            double scale = allowed / dist;
            return new Vector3(
                previous.x + (float)(dx * scale),
                previous.y + (float)(dy * scale),
                previous.z + (float)(dz * scale));
        }
    }
}
