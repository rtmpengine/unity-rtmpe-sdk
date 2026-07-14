// RTMPE SDK — Runtime/Infrastructure/Threading/OptionalNativeCall.cs
//
// Guards a P/Invoke that is an OPTIONAL accelerator — a native call whose
// absence degrades performance but never correctness. Kept Unity-free so the
// network I/O thread stays independent of UnityEngine and the guard is
// exercised directly by the dispatcher test shard.

using System;

namespace RTMPE.Threading
{
    /// <summary>
    /// Runs a platform P/Invoke that the caller can do without.  A runtime that
    /// cannot resolve the native entry point — a trimmed, emulated, or
    /// non-desktop Windows build, for instance — throws at the call site; left
    /// unguarded, that exception would abort whatever critical path triggered
    /// the call (here, the connect that starts the network thread).  Swallowing
    /// only the two binding-failure exceptions lets the caller fall back to its
    /// default path while a genuine fault raised by an entry point that DID
    /// bind still propagates.
    /// </summary>
    internal static class OptionalNativeCall
    {
        /// <summary>
        /// Invokes <paramref name="nativeCall"/>.  Returns <see langword="true"/>
        /// when the entry point bound and the call returned; <see langword="false"/>
        /// when this platform could not resolve it.  Any exception other than a
        /// binding failure propagates unchanged — an unresolved import is
        /// recoverable, a fault inside an executed native call is not.
        /// </summary>
        /// <param name="nativeCall">The optional native invocation.</param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="nativeCall"/> is <see langword="null"/>.
        /// </exception>
        public static bool TryInvoke(Action nativeCall)
        {
            if (nativeCall == null) throw new ArgumentNullException(nameof(nativeCall));

            try
            {
                nativeCall();
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                return false;
            }
        }
    }
}
