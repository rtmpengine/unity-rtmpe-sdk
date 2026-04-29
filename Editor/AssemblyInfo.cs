// RTMPE SDK — Editor/AssemblyInfo.cs
//
// Exposes Editor-only internals to the Editor test assembly so that
// EditorApiKeyStore and other internal Editor utilities can be unit
// tested without making them part of the package's public surface.

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("RTMPE.SDK.Editor.Tests")]
