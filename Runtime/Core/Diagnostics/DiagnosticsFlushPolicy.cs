// RTMPE SDK — Runtime/Core/Diagnostics/DiagnosticsFlushPolicy.cs
//
// Pure, UnityEngine-free decision seam for the diagnostics uplink's send
// cadence. It is separated from the Unity wrapper so the timing rule — release
// crash-grade signals promptly while batching routine ones — is verifiable in
// isolation, without a live session or the Unity main loop.

namespace RTMPE.Core.Diagnostics
{
    /// <summary>
    /// Decides when the uplink should drain its buffer to the wire.
    /// </summary>
    internal static class DiagnosticsFlushPolicy
    {
        /// <summary>
        /// Returns whether buffered diagnostics should be sent now.
        /// <para>
        /// A crash-grade entry (<paramref name="highSeverityPending"/>) is
        /// released after only <paramref name="promptSpacingMs"/> so it escapes a
        /// degrading connection before the transport tears down, while routine
        /// entries wait the configured <paramref name="flushIntervalMs"/> to
        /// coalesce. With nothing buffered the uplink stays silent.
        /// </para>
        /// </summary>
        /// <param name="pendingCount">Entries buffered and awaiting a flush.</param>
        /// <param name="highSeverityPending">A crash-grade entry is among them.</param>
        /// <param name="msSinceLastFlush">Elapsed time since the previous flush.</param>
        /// <param name="flushIntervalMs">Routine coalescing interval.</param>
        /// <param name="promptSpacingMs">Minimum spacing for a crash-grade release.</param>
        public static bool ShouldFlush(
            int pendingCount,
            bool highSeverityPending,
            long msSinceLastFlush,
            int flushIntervalMs,
            int promptSpacingMs)
        {
            if (pendingCount <= 0) return false;
            if (highSeverityPending && msSinceLastFlush >= promptSpacingMs) return true;
            return msSinceLastFlush >= flushIntervalMs;
        }
    }
}
