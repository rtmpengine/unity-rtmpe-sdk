// OwnershipReassignmentPolicy — pure decision logic for NEW-OWNERSHIP-1.
//
// When the owner of a NetworkObject leaves the room, objects flagged
// DestroyWithOwner=false used to freeze: the SDK left them owned by the
// departed player awaiting a "server ownership grant" that no server ever
// emitted.  The fix reassigns those surviving objects to the current room host
// (MasterClient) deterministically on every client — the canonical
// host-migration behaviour for this class of SDK.
//
// The *when-to-reassign* decision is isolated here, free of UnityEngine, so it
// is unit-testable under the dotnet/xunit harness.  The MonoBehaviour-coupled
// OwnershipManager/SpawnManager that perform the actual reassignment cannot be
// compiled outside Unity, so keeping this guard pure is what makes the
// load-bearing correctness rule (notably: do NOT steal objects on a voluntary
// in-room master transfer) testable without the Unity runtime.

namespace RTMPE.Core
{
    /// <summary>
    /// Pure, Unity-free guard deciding whether a departed/replaced owner's
    /// surviving objects should be reassigned to a new owner (the room host).
    /// </summary>
    public static class OwnershipReassignmentPolicy
    {
        /// <summary>
        /// Returns <see langword="true"/> iff <paramref name="formerOwnerId"/>'s
        /// surviving (DestroyWithOwner=false) objects should be reassigned to
        /// <paramref name="newOwnerId"/>.
        ///
        /// <para>Rejects when:</para>
        /// <list type="bullet">
        ///   <item>either id is null/empty (no valid party);</item>
        ///   <item>the two ids are equal (reassigning to self is a no-op); or</item>
        ///   <item><paramref name="formerStillInRoom"/> is true — the former
        ///   owner is STILL a room member, i.e. this is a voluntary in-room
        ///   master transfer, not a departure, and the still-present owner keeps
        ///   their objects.</item>
        /// </list>
        ///
        /// <para>On a player-leave the caller passes
        /// <paramref name="formerStillInRoom"/> = false (a leaver is, by
        /// definition, gone).  On a master-client change the caller passes the
        /// live roster-membership of the previous master, so a host that merely
        /// handed off the master role while remaining in the room is not
        /// stripped of its objects.</para>
        /// </summary>
        public static bool ShouldReassign(string formerOwnerId, string newOwnerId, bool formerStillInRoom)
        {
            if (string.IsNullOrEmpty(formerOwnerId)) return false;
            if (string.IsNullOrEmpty(newOwnerId)) return false;
            if (formerOwnerId == newOwnerId) return false;
            if (formerStillInRoom) return false;
            return true;
        }
    }
}
