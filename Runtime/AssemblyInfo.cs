// RTMPE SDK — Runtime/AssemblyInfo.cs
//
// Assembly-level attributes for RTMPE.SDK.Runtime.
//
// InternalsVisibleTo allows the test assembly to access internal types such as
// Curve25519, HkdfSha256, ChaCha20Poly1305Impl, and Ed25519Verify for white-box
// unit testing with RFC test vectors.
//
// ============================================================================
// CRYPTOGRAPHIC THREAT-MODEL DECLARATION (assembly-wide)
// ============================================================================
// The pure-managed crypto primitives in Runtime/Crypto/Internal use
// System.Numerics.BigInteger for field arithmetic in Poly1305, X25519, and
// Ed25519. BigInteger is variable-time by .NET specification — its
// reduction, multiplication, and modular exponentiation routines branch on
// operand magnitude.
//
// The RTMPE SDK threat model EXCLUDES side-channel attacks that require
// shared-hardware co-residency, fine-grained timing measurement of the
// game client process, or local cache/branch observation. Specifically:
//
//   • The client is presumed to run on the player's own device. A player
//     extracting their own session key (the only secret derivable from
//     timing of this client's crypto) gains nothing they don't already
//     possess.
//   • Server-side keys are derived from ephemeral X25519 secrets that
//     never live on the client. Client-process timing leakage cannot
//     reveal them.
//   • The protocol's per-session ephemeral X25519 keys make it
//     statistically infeasible to accumulate enough timing samples
//     across a single key's lifetime to mount a Lucky-Thirteen-class
//     attack.
//
// Operators deploying RTMPE under a stricter threat model (shared-hardware
// game-streaming services, timing-instrumented anti-cheat sandboxes) must
// substitute a constant-time crypto backend. The hooks for doing so live
// in Runtime/Crypto/Internal; replacement is a per-file change with no
// public-API surface change.
//
// This decision is documented per CWE-208 ("Observable Timing Discrepancy")
// and aligns with the IRTF CFRG guidance in RFC 7748 §6 and RFC 8439 §1.
// ============================================================================

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RTMPE.SDK.Tests")]
[assembly: InternalsVisibleTo("RTMPE.SDK.Editor")]
[assembly: InternalsVisibleTo("RTMPE.Crypto.Tests")]
[assembly: InternalsVisibleTo("RTMPE.PinStore.Tests")]
[assembly: InternalsVisibleTo("RTMPE.Registry.Tests")]
