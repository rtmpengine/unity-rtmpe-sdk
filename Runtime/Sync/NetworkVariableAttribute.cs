// RTMPE SDK — Runtime/Sync/NetworkVariableAttribute.cs
//
// Declarative metadata for NetworkVariable fields and properties.
//
// Primary use case: per-variable bandwidth throttling.  By default every dirty
// NetworkVariable is flushed at the global tick cadence (NetworkManager
// VariableFlushInterval = 30 Hz).  Marking a field with
//   [NetworkVariable(SendRateHz = 10f)]
// caps that variable's outbound rate at 10 Hz independently of its siblings,
// reducing bandwidth for values that change frequently but only need slow
// synchronisation (health bars, ammo counts, kill streaks, …).
//
// Discovery: NetworkBehaviour scans its own type once (via reflection) the
// first time TrackVariable() is called for that type.  Found attributes are
// matched to their corresponding NetworkVariableBase instance by the field /
// property reference (object identity), not by name, so the developer is free
// to rename either side without invalidating the binding.
//
// Performance: the reflection scan happens once per concrete subclass, on a
// single thread (Unity main thread, during OnNetworkSpawn).  The result is
// cached in a per-type dictionary keyed by Type, so subsequent spawns of the
// same NetworkBehaviour subclass do not re-scan.  Per-variable lookup at
// flush time is O(1) — the SendRateHz is copied directly onto each variable
// instance during registration.

using System;

namespace RTMPE.Sync
{
    /// <summary>
    /// Optional metadata for a <see cref="NetworkVariableBase"/>-typed field or
    /// property declared on a <see cref="RTMPE.Core.NetworkBehaviour"/>.
    ///
    /// <para>Currently supports per-variable send-rate throttling via
    /// <see cref="SendRateHz"/>.  Future extensions (e.g. relevancy filters,
    /// reliability hints) will live on this same attribute to avoid stacking
    /// multiple custom attributes on a single declaration.</para>
    ///
    /// <example>
    /// <code>
    /// public sealed class PlayerStats : NetworkBehaviour
    /// {
    ///     // 30 Hz default — used by transform-style values that need smooth interpolation.
    ///     public NetworkVariableVector3 velocity;
    ///
    ///     // 10 Hz — health changes are visually discrete; saves ~66 % bandwidth.
    ///     [NetworkVariable(SendRateHz = 10f)]
    ///     public NetworkVariableInt health;
    ///
    ///     // 2 Hz — cosmetic counter; minimal bandwidth.
    ///     [NetworkVariable(SendRateHz = 2f)]
    ///     public NetworkVariableInt killStreak;
    /// }
    /// </code>
    /// </example>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property,
                    AllowMultiple = false,
                    Inherited     = true)]
    public sealed class NetworkVariableAttribute : Attribute
    {
        /// <summary>
        /// Maximum send rate in updates per second for this variable.
        ///
        /// <para>A value of <c>0</c> (the default) means "use the global flush
        /// cadence" — currently 30 Hz, configured by
        /// <c>NetworkManager.VariableFlushInterval</c>.  Any positive value
        /// independently throttles this variable to at most one send per
        /// <c>1 / SendRateHz</c> seconds; intermediate value changes still
        /// update local state and remain queued (the <c>IsDirty</c> flag is
        /// preserved when a flush is skipped) but they are coalesced into the
        /// next eligible send window.</para>
        ///
        /// <para>Negative values are clamped to <c>0</c> (treated as "use
        /// default") with a one-line warning at registration time.  Values
        /// above <c>NetworkManager.VariableFlushInterval</c>'s implied ceiling
        /// (30 Hz) are accepted but cannot exceed the global tick rate — the
        /// flush loop runs at 30 Hz and only emits one update per tick per
        /// variable.</para>
        /// </summary>
        public float SendRateHz { get; set; } = 0f;
    }
}
