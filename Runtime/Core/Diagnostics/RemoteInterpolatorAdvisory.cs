// RTMPE SDK — Runtime/Core/Diagnostics/RemoteInterpolatorAdvisory.cs
//
// A non-owner networked object is driven entirely by the transform records the
// receiver decodes from the network, and the receive path applies that motion
// through the object's NetworkTransformInterpolator.  When the prefab carries a
// NetworkTransform (so it is registered for transform replication) but no
// interpolator, the receive path has nowhere to hand the decoded state: the
// record is discarded and the replica stays frozen on this client even though
// its replication traffic continues to arrive normally.  Nothing about that
// outcome is observable from the console — the object simply never moves.
//
// This advisory turns that silent misconfiguration into a single, named,
// actionable console line.  Because the inbound records arrive at the room's
// 30 Hz broadcast cadence, the surfacing is latched per object id so a
// persistently-misconfigured replica produces one diagnostic rather than a
// continuous stream.  The decision and the message are pure managed code with
// no UnityEngine dependency, so the warn-once behaviour and the wording are
// exercised directly under the headless test runner; the single
// Debug.LogWarning sink lives at the receive-dispatch call site.

using System.Collections.Concurrent;

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Per-object warn-once gate for the "non-owner object without a
    /// <see cref="RTMPE.Sync.NetworkTransformInterpolator"/>" misconfiguration.
    /// The receive dispatch calls <see cref="ShouldWarn"/> on the drop path and,
    /// on the first occurrence for a given object, emits <see cref="Compose"/>.
    /// </summary>
    internal static class RemoteInterpolatorAdvisory
    {
        // Set of object ids already surfaced.  Only misconfigured replicas are
        // ever inserted, so the set stays small; a concurrent map keeps the gate
        // correct even if a future receive path runs off the main thread.
        private static readonly ConcurrentDictionary<ulong, byte> s_surfaced =
            new ConcurrentDictionary<ulong, byte>();

        /// <summary>
        /// Records <paramref name="objectId"/> as surfaced and reports whether
        /// this call is the first for that object.  Returns <see langword="true"/>
        /// exactly once per object id — the caller emits the advisory only on a
        /// <see langword="true"/> result, so a 30 Hz stream of records for the
        /// same misconfigured replica yields a single console line.
        /// </summary>
        public static bool ShouldWarn(ulong objectId) => s_surfaced.TryAdd(objectId, 0);

        /// <summary>
        /// Builds the actionable advisory text naming the offending object and
        /// the remedy.  Pure; centralised here so the test project asserts on a
        /// stable string and any wording change is a single reviewed edit.
        /// </summary>
        public static string Compose(ulong objectId, string objectName)
        {
            string named = string.IsNullOrEmpty(objectName) ? "<unnamed>" : objectName;
            return
                $"[RTMPE] Networked object {objectId} ('{named}') is receiving remote " +
                "transform updates but carries no NetworkTransformInterpolator, so the " +
                "receive path has nowhere to apply the motion: the object stays frozen on " +
                "this client while its replication traffic continues to arrive.  Add a " +
                "NetworkTransformInterpolator component to the prefab alongside " +
                "NetworkTransform.  Logged once per object.";
        }

        /// <summary>
        /// Whether <paramref name="objectId"/> has already been surfaced in this
        /// process.  Exposed for test fixtures and editor diagnostics; production
        /// code drives the gate through <see cref="ShouldWarn"/>.
        /// </summary>
        internal static bool WasSurfaced(ulong objectId) => s_surfaced.ContainsKey(objectId);

#if UNITY_INCLUDE_TESTS
        /// <summary>
        /// Drains the per-object latch so a fixture observes the first-surface
        /// path from a clean precondition.  Compiled only under
        /// <c>UNITY_INCLUDE_TESTS</c> so the shipped Player assembly exposes no
        /// mutator on the advisory state.
        /// </summary>
        internal static void ResetForTests() => s_surfaced.Clear();
#endif // UNITY_INCLUDE_TESTS
    }
}
