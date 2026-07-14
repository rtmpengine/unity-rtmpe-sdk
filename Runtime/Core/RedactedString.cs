// RTMPE SDK — Runtime/Core/RedactedString.cs
//
// Opaque wrapper for session-bearer credentials exposed on the SDK's
// public surface (JWT bearer, reconnect token).  The type defangs the
// dominant credential-leak vector: a stray `Debug.Log(token)` or a
// string-interpolated analytics breadcrumb that ends up in a crash
// report or a third-party SDK's log shipper.
//
// Wire-format / persistence behaviour is unchanged — the underlying
// string travels through the SDK exactly as before; the only difference
// is what the public accessor returns and how it renders in a log line.
//
// No UnityEngine dependency, so this file compiles into both Unity
// builds and the xunit projects under tests/unit/ without stubs.

using System;

namespace RTMPE.Core
{
    /// <summary>
    /// Opaque wrapper for a sensitive string (JWT bearer token, reconnect
    /// token) whose accidental disclosure would compromise the current
    /// session.  The struct renders as the literal <c>&lt;redacted&gt;</c>
    /// from <see cref="ToString"/> — so a careless
    /// <c>UnityEngine.Debug.Log(networkManager.JwtToken)</c>, a
    /// string-interpolated analytics breadcrumb, or a default
    /// JSON serialiser will not exfiltrate the underlying credential.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers that legitimately need the raw value (for example, to
    /// attach it as a bearer to an HTTPS request against the operator's
    /// own backend) must call <see cref="Reveal"/> explicitly.  No
    /// implicit conversion to <see cref="string"/> exists, so the
    /// reveal step is forced at compile time and is grep-discoverable
    /// during security review.
    /// </para>
    /// <para>
    /// The struct is a value type with a single reference field, so
    /// passing it through event payloads costs the same as passing a
    /// bare <see cref="string"/> — no allocation on the hot path.
    /// </para>
    /// </remarks>
    [Serializable]
    public readonly struct RedactedString : IEquatable<RedactedString>
    {
        /// <summary>The literal substituted for the underlying value
        /// in <see cref="ToString"/>.  Pinned to a constant so log
        /// pipelines can grep for it as a tripwire.</summary>
        public const string Placeholder = "<redacted>";

        /// <summary>
        /// Backing field for the wrapped secret.  Marked
        /// <see cref="NonSerializedAttribute"/> so that
        /// <c>BinaryFormatter</c>, <c>DataContractSerializer</c>,
        /// <c>XmlSerializer</c>, and any other reflection-based wire
        /// serialiser that walks private state cannot exfiltrate the
        /// underlying value.  Consumers that legitimately need to persist
        /// a token must do so through their own typed path (storing the
        /// result of <see cref="Reveal"/> after a conscious decision),
        /// not by serialising the wrapper as opaque data.
        /// </summary>
        [NonSerialized]
        private readonly string _value;

        /// <summary>
        /// Wrap a sensitive string.  Internal so the SDK retains sole
        /// authority over which strings are tagged as sensitive — app
        /// code cannot construct one to bypass the wrapper.
        /// </summary>
        internal RedactedString(string value)
        {
            _value = value;
        }

        /// <summary>
        /// <see langword="true"/> when the wrapped value is null or
        /// empty.  Use this for state checks ("does the SDK currently
        /// hold a token?") so that no reveal of the underlying value is
        /// required for the common branching case.
        /// </summary>
        public bool IsEmpty => string.IsNullOrEmpty(_value);

        /// <summary>
        /// Return the underlying string.  Call this only at the point
        /// of use — for example, immediately before attaching the value
        /// as a bearer header on an outbound HTTPS request — and never
        /// store the revealed value in a long-lived field or log line.
        /// </summary>
        public string Reveal() => _value;

        /// <summary>
        /// Always returns the literal <c>&lt;redacted&gt;</c> for a
        /// non-empty value, or the empty string for an empty wrapper.
        /// Overridden so that <c>Debug.Log(token)</c>,
        /// <c>$"token={token}"</c>, and default JSON / object dumps
        /// cannot exfiltrate the underlying credential.
        /// </summary>
        public override string ToString() => IsEmpty ? string.Empty : Placeholder;

        /// <summary>
        /// Explicit conversion to <see cref="string"/>.  Equivalent to
        /// <see cref="Reveal"/> — kept available so callers can use the
        /// idiomatic <c>(string)</c> cast when revealing — but no
        /// implicit conversion is provided, so the reveal step is
        /// always visible at the call site.
        /// </summary>
        public static explicit operator string(RedactedString s) => s._value;

        /// <inheritdoc/>
        public bool Equals(RedactedString other) => string.Equals(_value, other._value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is RedactedString other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _value == null ? 0 : StringComparer.Ordinal.GetHashCode(_value);

        /// <summary>Two wrappers compare equal when their underlying values are ordinal-equal.</summary>
        public static bool operator ==(RedactedString a, RedactedString b) => a.Equals(b);

        /// <summary>Two wrappers compare unequal when their underlying values differ.</summary>
        public static bool operator !=(RedactedString a, RedactedString b) => !a.Equals(b);
    }
}
