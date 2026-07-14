// RTMPE SDK — Runtime/Sync/RigidbodyVelocityCompat.cs
//
// Cross-Unity-version shim for the Rigidbody / Rigidbody2D linear-velocity
// property.  Unity 6 (6000.0) renamed `Rigidbody.velocity` to
// `Rigidbody.linearVelocity` and `Rigidbody2D.velocity` to
// `Rigidbody2D.linearVelocity`; the older spelling is still available via
// the `[Obsolete(error: false)]` shim, but the new spelling does not exist
// on Unity 2022.3 LTS or 2023.1.  Without this shim NetworkRigidbody and
// NetworkRigidbody2D fail to compile against the dominant commercial LTS
// branches, locking studios out of the SDK.
//
// The shim is internal and inlined at every call site, so it costs nothing
// at runtime — the JIT folds it into a direct property load/store.

using UnityEngine;

namespace RTMPE.Sync.Internal
{
    internal static class RigidbodyVelocityCompat
    {
        public static Vector3 GetLinearVelocity(this Rigidbody rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetLinearVelocity(this Rigidbody rb, Vector3 value)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = value;
#else
            rb.velocity = value;
#endif
        }

        public static Vector2 GetLinearVelocity(this Rigidbody2D rb)
        {
#if UNITY_6000_0_OR_NEWER
            return rb.linearVelocity;
#else
            return rb.velocity;
#endif
        }

        public static void SetLinearVelocity(this Rigidbody2D rb, Vector2 value)
        {
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = value;
#else
            rb.velocity = value;
#endif
        }
    }
}
