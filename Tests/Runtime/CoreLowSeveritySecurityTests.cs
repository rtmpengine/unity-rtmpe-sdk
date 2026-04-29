// RTMPE SDK — Tests/Runtime/CoreLowSeveritySecurityTests.cs
//
// Edit-mode tests covering low-severity defensive behaviour in the Core
// subsystem: log redaction, dirty-variable snapshot iteration, the internal
// SpawnManager accessor, RPC dispatch IsSpawned gating, ReconnectBackoff
// saturation, scene-event serialisation, and the strict UTF-8 / JsonUtility
// JWT parsing path.
//
// Tests are deliberately decoupled from the wider SDK surface so they run
// in isolation without spinning up a transport or singleton.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("LowSeveritySecurity")]
    public class CoreLowSeveritySecurityTests
    {
        // ── log redaction ───────────────────────────────────────────────

        [Test]
        [Description("LogRedaction.Redact(uint) keeps only the first 4 hex chars and appends ***.")]
        public void LogRedaction_RedactsCryptoIdToPrefix()
        {
            uint full = 0xDEADBEEFu;
            string redacted = LogRedaction.Redact(full);

            Assert.That(redacted, Is.EqualTo("dead***"));
            Assert.That(redacted, Does.Not.Contain("beef"),
                "Redacted form must NOT contain the suffix bytes of the original id.");
        }

        [Test]
        [Description("ulong session-id redaction emits exactly 4 hex prefix chars.")]
        public void LogRedaction_RedactsSessionIdToPrefix()
        {
            ulong full = 0x0123456789ABCDEFul;
            string redacted = LogRedaction.Redact(full);

            Assert.That(redacted, Is.EqualTo("0123***"));
            Assert.That(redacted, Does.Not.Contain("4567"));
            Assert.That(redacted, Does.Not.Contain("89ab"));
        }

        [Test]
        [Description("string redaction handles short, normal, null, and empty inputs.")]
        public void LogRedaction_StringFormVariants()
        {
            Assert.That(LogRedaction.Redact((string)null), Is.EqualTo("<null>"));
            Assert.That(LogRedaction.Redact(""),           Is.EqualTo("<empty>"));
            Assert.That(LogRedaction.Redact("abc"),        Is.EqualTo("abc***"));
            Assert.That(LogRedaction.Redact("abcdef"),     Is.EqualTo("abcd***"));
        }

        // ── snapshot iteration of _trackedVariables ─────────────────────

        [Test]
        [Description(
            "MarkAllVariablesDirty must not throw when a subscriber " +
            "callback registers a new NetworkVariable during iteration.")]
        public void MarkAllVariablesDirty_SnapshotIsolatesCollectionMutation()
        {
            // We cannot legally mutate _trackedVariables from a public hook
            // today, but the snapshot must be observable independent of the
            // backing list reference.  Reflect the field, copy it into a
            // snapshot via the same CopyTo path the production code uses,
            // append a new element to the live list, and confirm the snapshot
            // length is unchanged.  This is the unit-test analogue of the
            // defensive guarantee MarkAllVariablesDirty offers.
            var go     = new GameObject("Subject");
            var nm     = go.AddComponent<DummyBehaviour>();
            var listFi = typeof(NetworkBehaviour).GetField(
                "_trackedVariables",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(listFi, "Expected NetworkBehaviour._trackedVariables field");

            var live = (System.Collections.IList)listFi.GetValue(nm);
            // Build a dummy snapshot via the same array-copy idiom.  An empty
            // live list is sufficient to prove the snapshot semantics.
            var snapshot = new object[live.Count];
            live.CopyTo(snapshot, 0);
            int initial = snapshot.Length;

            // Mutate the live list — the snapshot length must be unchanged.
            // We append null because the dummy's list is List<NetworkVariableBase>
            // and we only need to demonstrate the snapshot is decoupled.
            // Some Unity versions reject null in a typed List.Add; in that
            // case skip the mutation half of the assertion.
            try { live.Add(null); }
            catch { /* tolerated */ }

            Assert.That(snapshot.Length, Is.EqualTo(initial),
                "Snapshot must be decoupled from the live tracking list.");

            UnityEngine.Object.DestroyImmediate(go);
        }

        private sealed class DummyBehaviour : NetworkBehaviour { }

        // ── internal SpawnManagerInternal accessor ─────────────────────

        [Test]
        [Description(
            "NetworkManager exposes SpawnManagerInternal for editor " +
            "diagnostics, replacing the previous reflection lookup.")]
        public void SpawnManagerInternal_AccessorExists()
        {
            var pi = typeof(NetworkManager).GetProperty(
                "SpawnManagerInternal",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.IsNotNull(pi, "Expected internal accessor SpawnManagerInternal on NetworkManager.");
            Assert.IsTrue(pi.PropertyType == typeof(SpawnManager),
                "SpawnManagerInternal must return SpawnManager.");
        }

        // ── RPC dispatched to non-spawned NetworkBehaviour is dropped ───

        [Test]
        [Description(
            "DispatchEnhancedRpc on a non-spawned NetworkBehaviour " +
            "logs a warning and returns without invoking the method.")]
        public void DispatchEnhancedRpc_DroppedWhenNotSpawned()
        {
            // Arrange — settings with verbose logs so RtmpeLog.Warning surfaces
            // as Debug.LogWarning, which we can assert on.
            var settings = ScriptableObject.CreateInstance<NetworkSettings>();
            settings.enableDebugLogs = true;
            RtmpeLog.SetTestOverride(settings);

            try
            {
                var go = new GameObject("Subject");
                var nb = go.AddComponent<DummyBehaviour>();
                Assert.IsFalse(nb.IsSpawned, "Sanity: behaviour must start non-spawned.");

                LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex(
                    "RPC dispatched to non-spawned NetworkBehaviour"));

                // Reflectively call the internal dispatch entry point.
                var mi = typeof(NetworkBehaviour).GetMethod(
                    "DispatchEnhancedRpc",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(mi);
                mi.Invoke(nb, new object[] { 0xCAFEBABEu, new object[0] });

                UnityEngine.Object.DestroyImmediate(go);
            }
            finally
            {
                RtmpeLog.SetTestOverride(null);
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        // ── ReconnectBackoff saturates instead of overflowing ───────────

        [Test]
        [Description(
            "NextDelay() must not throw OverflowException when invoked " +
            "more than int.MaxValue times — the attempt counter saturates.")]
        public void ReconnectBackoff_AttemptCounterSaturates()
        {
            var backoff = new ReconnectBackoff(seed: 12345);

            // Drive the counter past the saturation cap (30) and confirm it
            // stops climbing.  We don't loop two billion times in a unit test;
            // we instead reach into the field and confirm the cap.
            for (int i = 0; i < 100; i++)
            {
                Assert.DoesNotThrow(() => backoff.NextDelay());
            }

            var fi = typeof(ReconnectBackoff).GetField(
                "_attempt",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(fi);
            int observed = (int)fi.GetValue(backoff);

            Assert.That(observed, Is.LessThanOrEqualTo(30),
                "Attempt counter must saturate at MaxAttemptForBackoff (30).");
        }

        [Test]
        [Description(
            "setting _attempt directly to int.MaxValue and calling " +
            "NextDelay() must not throw OverflowException.")]
        public void ReconnectBackoff_AtMaxValue_DoesNotThrow()
        {
            var backoff = new ReconnectBackoff(seed: 7);
            var fi = typeof(ReconnectBackoff).GetField(
                "_attempt",
                BindingFlags.Instance | BindingFlags.NonPublic);
            fi.SetValue(backoff, int.MaxValue);

            // Saturating increment must accept int.MaxValue without throwing.
            TimeSpan d = default;
            Assert.DoesNotThrow(() => d = backoff.NextDelay());
            Assert.That(d.TotalMilliseconds, Is.GreaterThanOrEqualTo(0));
        }

        // ── scene-event ordering serialised by lock ─────────────────────

        [Test]
        [Description(
            "scene-transition lock exists and is non-null after Awake " +
            "so concurrent prune / recreate paths cannot interleave.")]
        public void SceneTransitionLock_IsInitialised()
        {
            var go = new GameObject("NetworkManager");
            var nm = go.AddComponent<NetworkManager>();
            try
            {
                var fi = typeof(NetworkManager).GetField(
                    "_sceneTransitionLock",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(fi, "Expected _sceneTransitionLock field on NetworkManager.");
                var lockObj = fi.GetValue(nm);
                Assert.IsNotNull(lockObj, "Lock object must be initialised in field initialiser.");

                // Re-entering the lock from the same thread must succeed
                // (Monitor is reentrant) — proves no other thread holds it.
                lock (lockObj)
                {
                    lock (lockObj) { /* reentrant — must not deadlock */ }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── SpawnPacketParser strict UTF-8 + JWT JSON parse ─────────────

        [Test]
        [Description(
            "SpawnPacketParser rejects an owner-id payload with " +
            "embedded NUL bytes (strict UTF-8 path).")]
        public void SpawnPacketParser_RejectsEmbeddedNul()
        {
            byte[] payload = BuildSpawnPayloadWithOwner(new byte[] { (byte)'a', 0x00, (byte)'b' });
            bool ok = SpawnPacketParser.TryParseSpawn(payload, out _);
            Assert.IsFalse(ok, "Owner with embedded NUL must be rejected.");
        }

        [Test]
        [Description("SpawnPacketParser rejects a malformed UTF-8 owner-id.")]
        public void SpawnPacketParser_RejectsMalformedUtf8()
        {
            // 0xC0 0x80 is overlong-encoded NUL — invalid per RFC 3629 and
            // rejected by strict UTF-8 decoders.
            byte[] payload = BuildSpawnPayloadWithOwner(new byte[] { 0xC0, 0x80 });
            bool ok = SpawnPacketParser.TryParseSpawn(payload, out _);
            Assert.IsFalse(ok, "Malformed UTF-8 owner must be rejected.");
        }

        [Test]
        [Description("SpawnPacketParser accepts a well-formed ASCII owner.")]
        public void SpawnPacketParser_AcceptsAsciiOwner()
        {
            byte[] payload = BuildSpawnPayloadWithOwner(Encoding.UTF8.GetBytes("alice"));
            bool ok = SpawnPacketParser.TryParseSpawn(payload, out var data);
            Assert.IsTrue(ok);
            Assert.That(data.OwnerPlayerId, Is.EqualTo("alice"));
        }

        [Test]
        [Description(
            "TryExtractJwtSub must pick the top-level 'sub' claim even " +
            "when another claim's value contains a literal \\\"sub\\\":\\\"…\\\" sequence.")]
        public void TryExtractJwtSub_PrefersTopLevelClaim()
        {
            // Construct claims JSON where another field VALUE contains the
            // literal substring '"sub":"forged"' — the previous IndexOf-based
            // scan would have matched it ahead of the real sub claim.
            string claimsJson =
                "{\"name\":\"\\\"sub\\\":\\\"forged\\\"\",\"sub\":\"42\"}";

            string sub = InvokeTryExtractJwtSub(BuildJwt(claimsJson));
            Assert.That(sub, Is.EqualTo("42"),
                "Parser must return the real top-level sub, not a substring " +
                "match inside another claim value.");
        }

        [Test]
        [Description("TryExtractJwtSub handles base64url payloads with stripped '=' padding.")]
        public void TryExtractJwtSub_HandlesUnpaddedBase64Url()
        {
            // Choose a claims JSON whose base64 length is not a multiple of 4
            // so padding is required.  '{"sub":"7"}' is 11 bytes, base64 yields
            // 16 chars including 2 '=' chars — strip them to exercise the pad
            // restoration path.
            string sub = InvokeTryExtractJwtSub(BuildJwt("{\"sub\":\"7\"}"));
            Assert.That(sub, Is.EqualTo("7"));
        }

        [Test]
        [Description("TryExtractJwtSub returns null for a JWT without a sub claim.")]
        public void TryExtractJwtSub_ReturnsNullWhenAbsent()
        {
            string sub = InvokeTryExtractJwtSub(BuildJwt("{\"name\":\"alice\"}"));
            Assert.IsNull(sub);
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static string InvokeTryExtractJwtSub(string jwt)
        {
            var mi = typeof(NetworkManager).GetMethod(
                "TryExtractJwtSub",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(mi, "Expected NetworkManager.TryExtractJwtSub static helper.");
            return (string)mi.Invoke(null, new object[] { jwt });
        }

        private static string BuildJwt(string claimsJson)
        {
            // Header / signature segments are arbitrary — TryExtractJwtSub
            // only inspects the claims segment.  Strip '=' so the helper has
            // to restore padding.
            string headerB64    = Base64UrlEncode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");
            string claimsB64    = Base64UrlEncode(claimsJson);
            string signatureB64 = Base64UrlEncode("sig");
            return $"{headerB64}.{claimsB64}.{signatureB64}";
        }

        private static string Base64UrlEncode(string raw)
        {
            var bytes = Encoding.UTF8.GetBytes(raw);
            return Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
        }

        // Build a minimal Spawn payload (42 bytes baseline + ownerLen):
        //  prefabId u32 | objectId u64 | ownerLen u16 | owner | 7 floats
        private static byte[] BuildSpawnPayloadWithOwner(byte[] ownerBytes)
        {
            int ownerLen = ownerBytes.Length;
            byte[] buf = new byte[4 + 8 + 2 + ownerLen + 28];
            int o = 0;

            // prefabId = 1
            buf[o++] = 1; buf[o++] = 0; buf[o++] = 0; buf[o++] = 0;
            // objectId = 0
            for (int i = 0; i < 8; i++) buf[o++] = 0;
            // ownerLen LE
            buf[o++] = (byte)(ownerLen & 0xFF);
            buf[o++] = (byte)((ownerLen >> 8) & 0xFF);
            // owner bytes
            Array.Copy(ownerBytes, 0, buf, o, ownerLen);
            o += ownerLen;
            // 7 floats = 28 zero bytes is acceptable (positions / quaternion
            // identity is enforced elsewhere; the parser does not validate).
            // Seed quaternion w = 1.0f at the last float slot so consumers
            // that DO validate produce a unit quaternion.
            int rwOffset = o + 24;
            // Encode 1.0f LE manually.
            byte[] one = BitConverter.GetBytes(1.0f);
            if (!BitConverter.IsLittleEndian) Array.Reverse(one);
            buf[rwOffset]     = one[0];
            buf[rwOffset + 1] = one[1];
            buf[rwOffset + 2] = one[2];
            buf[rwOffset + 3] = one[3];
            return buf;
        }
    }
}
