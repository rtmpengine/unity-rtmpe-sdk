// RTMPE SDK — Tests/Runtime/NetworkManagerInstanceTests.cs
//
// Locks the singleton contract for NetworkManager.Instance:
//  * Instance returns the existing scene-placed manager when one exists.
//  * Instance returns null (with a one-time warning) when no manager exists.
//    It does NOT create a hidden GameObject under any circumstances.
//  * TryGetInstance is the warning-free probe variant.
//
// The auto-create path that previously stood up a stand-in NetworkManager
// backed by NetworkSettings.CreateDefault() is intentionally removed: a
// subscriber accessing Instance from another component's Awake would have
// produced a permanent manager with empty crypto material and shadowed the
// scene-placed manager whose Awake had not yet run.

using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("NetworkManagerSingleton")]
    public class NetworkManagerInstanceTests
    {
        [SetUp]
        public void SetUp()
        {
            // Defensively destroy any stray manager left over from an earlier
            // fixture so each test starts from a clean singleton state.  This
            // is BELT-AND-BRACES on top of the per-test TearDown below — Unity
            // Test Runner does not guarantee fixture ordering.
            foreach (var existing in Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(existing.gameObject);
            }
            // Clear the one-shot warning latch so each test starts from
            // "no warning has been emitted yet for the missing-instance path".
            NetworkManager.ResetMissingInstanceWarningForTests();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var existing in Object.FindObjectsByType<NetworkManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                Object.DestroyImmediate(existing.gameObject);
            }
        }

        [Test]
        [Description("Instance returns null and emits a warning when no NetworkManager has been " +
                     "added to the scene; it must NOT auto-create a stand-in GameObject.")]
        public void Instance_NoSceneManager_ReturnsNullWithWarning()
        {
            // Confirm the precondition: there must be no NetworkManager in the
            // scene at the moment Instance is touched.
            Assert.AreEqual(0,
                Object.FindObjectsByType<NetworkManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                "Test precondition: no NetworkManager should exist before this assertion.");

            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("NetworkManager.*not initialized|not.*scene|Returning null",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));

            var nm = NetworkManager.Instance;

            Assert.IsNull(nm, "Instance must NOT auto-create a stand-in NetworkManager.");
            Assert.AreEqual(0,
                Object.FindObjectsByType<NetworkManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length,
                "No NetworkManager GameObject should have been spawned by the getter.");
        }

        [Test]
        [Description("Instance returns the scene-placed manager when one exists.")]
        public void Instance_WithSceneManager_ReturnsExisting()
        {
            var go = new GameObject("ScenePlacedManager");
            var nm = go.AddComponent<NetworkManager>();   // Awake runs → registers _instance.

            Assert.AreSame(nm, NetworkManager.Instance,
                "Instance must return the scene-placed manager (no warning, no stand-in).");

            Object.DestroyImmediate(go);
        }

        [Test]
        [Description("TryGetInstance returns false without emitting a warning when no manager exists.")]
        public void TryGetInstance_NoManager_ReturnsFalseSilently()
        {
            // No LogAssert.Expect — the probe variant must NOT log.
            bool found = NetworkManager.TryGetInstance(out var nm);

            Assert.IsFalse(found);
            Assert.IsNull(nm);
        }

        [Test]
        [Description("TryGetInstance returns true and assigns the existing manager when present.")]
        public void TryGetInstance_WithManager_ReturnsTrue()
        {
            var go = new GameObject("ScenePlacedManager");
            var nm = go.AddComponent<NetworkManager>();

            bool found = NetworkManager.TryGetInstance(out var got);
            Assert.IsTrue(found);
            Assert.AreSame(nm, got);

            Object.DestroyImmediate(go);
        }

        [Test]
        [Description("Repeated Instance access without a scene manager logs the warning at most " +
                     "once per missing-instance episode (no per-frame spam).")]
        public void Instance_RepeatedNullAccess_LogsAtMostOnce()
        {
            // First access: warning expected.
            LogAssert.Expect(LogType.Warning,
                new System.Text.RegularExpressions.Regex("Returning null|not.*scene",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase));

            _ = NetworkManager.Instance;

            // Second and third accesses: must NOT emit additional warnings.
            // (LogAssert.NoUnexpectedReceived runs at TearDown — any extra
            // warning would fail the test.)
            _ = NetworkManager.Instance;
            _ = NetworkManager.Instance;
        }

        [Test]
        [Description("Instance access from a background thread returns null cleanly when no manager " +
                     "has been published — it must NOT call Unity engine APIs (FindFirstObjectByType) " +
                     "off the main thread, which would raise UnityException.")]
        public void Instance_BackgroundThreadAccess_ReturnsNullWithoutThrowing()
        {
            // Precondition: no scene-placed manager.
            Assert.AreEqual(0,
                UnityEngine.Object.FindObjectsByType<NetworkManager>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None).Length);

            NetworkManager observed = null;
            System.Exception thrown = null;
            var t = new System.Threading.Thread(() =>
            {
                try { observed = NetworkManager.Instance; }
                catch (System.Exception ex) { thrown = ex; }
            });
            t.Start();
            // 2 s is generous — the call is a few field reads and a thread-id compare.
            Assert.IsTrue(t.Join(System.TimeSpan.FromSeconds(2)),
                "Instance getter should return promptly from a background thread.");

            Assert.IsNull(thrown, $"Background access must not throw; got {thrown}");
            Assert.IsNull(observed,
                "Background thread must observe null when no manager is published.");
        }

        [Test]
        [Description("Instance access from a background thread returns the cached singleton when " +
                     "Awake on the main thread has already published it — no Unity-API call is needed.")]
        public void Instance_BackgroundThreadAccessAfterPublication_ReturnsCached()
        {
            var go = new GameObject("ScenePlacedManager");
            var nm = go.AddComponent<NetworkManager>();   // Awake publishes _instance.

            NetworkManager observed = null;
            System.Exception thrown = null;
            var t = new System.Threading.Thread(() =>
            {
                try { observed = NetworkManager.Instance; }
                catch (System.Exception ex) { thrown = ex; }
            });
            t.Start();
            Assert.IsTrue(t.Join(System.TimeSpan.FromSeconds(2)));

            Assert.IsNull(thrown, $"Background access must not throw; got {thrown}");
            Assert.AreSame(nm, observed,
                "Background thread should see the cached singleton without a Unity-API call.");

            UnityEngine.Object.DestroyImmediate(go);
        }

        [Test]
        [Description("TryGetInstance from a background thread returns false cleanly when no manager " +
                     "is published; it must not throw or call Unity engine APIs off-thread.")]
        public void TryGetInstance_BackgroundThreadAccess_ReturnsFalseWithoutThrowing()
        {
            bool found = true;
            NetworkManager observed = null;
            System.Exception thrown = null;
            var t = new System.Threading.Thread(() =>
            {
                try { found = NetworkManager.TryGetInstance(out observed); }
                catch (System.Exception ex) { thrown = ex; }
            });
            t.Start();
            Assert.IsTrue(t.Join(System.TimeSpan.FromSeconds(2)));

            Assert.IsNull(thrown, $"Background access must not throw; got {thrown}");
            Assert.IsFalse(found);
            Assert.IsNull(observed);
        }
    }
}
