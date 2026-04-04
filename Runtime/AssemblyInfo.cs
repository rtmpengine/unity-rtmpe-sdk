// RTMPE SDK — Runtime/AssemblyInfo.cs
//
// Assembly-level attributes for RTMPE.SDK.Runtime.
//
// InternalsVisibleTo allows the test assembly to access internal types such as
// Curve25519, HkdfSha256, ChaCha20Poly1305Impl, and Ed25519Verify for white-box
// unit testing with RFC test vectors.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RTMPE.SDK.Tests")]
