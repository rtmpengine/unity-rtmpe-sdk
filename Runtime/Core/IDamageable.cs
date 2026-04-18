// RTMPE SDK — Runtime/Core/IDamageable.cs
//
// Interface for objects that can receive server-authorised damage via ApplyDamage RPC.
// Lives in the SDK Runtime assembly so NetworkManager can dispatch damage RPCs
// without a compile-time dependency on game-specific types (e.g. HealthController
// in the Samples assembly).
//
// Game code implements this on any component that should receive ApplyDamage (301) RPCs.

namespace RTMPE.Core
{
    /// <summary>
    /// Implement on a <see cref="UnityEngine.Component"/> attached to the same
    /// <c>GameObject</c> (or a parent) as a <see cref="NetworkBehaviour"/> to
    /// receive server-authorised damage via the <c>ApplyDamage</c> RPC (method_id=301).
    ///
    /// <see cref="NetworkManager"/> looks up this interface using
    /// <c>GetComponentInParent&lt;IDamageable&gt;</c> when an ApplyDamage RPC arrives.
    /// </summary>
    public interface IDamageable
    {
        /// <summary>
        /// Apply validated, server-authorised damage.
        /// Called on the Unity main thread by <c>NetworkManager.HandleApplyDamageRpc</c>.
        /// </summary>
        /// <param name="damage">Positive damage amount (server-validated).</param>
        void ReceiveApplyDamage(int damage);
    }
}
