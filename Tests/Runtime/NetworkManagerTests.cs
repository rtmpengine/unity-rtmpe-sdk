// RTMPE SDK — Tests/Runtime/NetworkManagerTests.cs
//
// NUnit Edit-Mode tests for NetworkManager.
//
// Tests run in Unity Test Runner (Edit Mode) — MonoBehaviour lifecycle methods
// (Awake, Start, Update) are NOT called automatically. Tests use
// GameObject.AddComponent<NetworkManager>() which DOES trigger Awake.
//
// Important: each test creates and destroys its own GameObject to ensure a
// clean singleton state. Object.DestroyImmediate triggers OnDestroy which
// sets NetworkManager._instance = null via the lock.

using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("NetworkManager")]
    public class NetworkManagerTests
    {
        private GameObject      _go;
        private NetworkManager  _manager;

        [SetUp]
        public void SetUp()
        {
            // Create a fresh NetworkManager per test.
            // Awake() runs immediately via AddComponent.
            _go      = new GameObject("TestNetworkManager");
            _manager = _go.AddComponent<NetworkManager>();
        }

        [TearDown]
        public void TearDown()
        {
            // DestroyImmediate triggers OnDestroy → sets _instance = null.
            if (_go != null)
                Object.DestroyImmediate(_go);
        }

        // ── Initial state ──────────────────────────────────────────────────────

        [Test]
        [Description("NetworkManager.State must be Disconnected immediately after Awake.")]
        public void InitialState_IsDisconnected()
        {
            Assert.AreEqual(NetworkState.Disconnected, _manager.State);
        }

        [Test]
        [Description("IsConnected must return false before any Connect() call.")]
        public void IsConnected_BeforeConnect_IsFalse()
        {
            Assert.IsFalse(_manager.IsConnected);
        }

        [Test]
        [Description("IsInRoom must return false before any RoomJoin.")]
        public void IsInRoom_BeforeJoin_IsFalse()
        {
            Assert.IsFalse(_manager.IsInRoom);
        }

        [Test]
        [Description("LocalPlayerId must be 0 before authentication.")]
        public void LocalPlayerId_BeforeConnect_IsZero()
        {
            Assert.AreEqual(0UL, _manager.LocalPlayerId);
        }

        [Test]
        [Description("CurrentRoomId must be 0 before room join.")]
        public void CurrentRoomId_BeforeJoin_IsZero()
        {
            Assert.AreEqual(0UL, _manager.CurrentRoomId);
        }

        // ── Settings ───────────────────────────────────────────────────────────

        [Test]
        [Description("Awake must create default settings when none are assigned.")]
        public void Settings_NotNull_AfterAwake()
        {
            Assert.IsNotNull(_manager.Settings,
                "NetworkManager should create a default NetworkSettings if none is assigned.");
        }

        [Test]
        [Description("Default settings must use the expected gateway address.")]
        public void DefaultSettings_ServerHost_Is_Localhost()
        {
            Assert.AreEqual("127.0.0.1", _manager.Settings.serverHost);
        }

        [Test]
        [Description("Default settings must use port 7777.")]
        public void DefaultSettings_ServerPort_Is_7777()
        {
            Assert.AreEqual(7777, _manager.Settings.serverPort);
        }

        [Test]
        [Description("Default settings must use tickRate 30 Hz.")]
        public void DefaultSettings_TickRate_Is_30()
        {
            Assert.AreEqual(30, _manager.Settings.tickRate);
        }

        [Test]
        [Description("TickInterval must equal 1 / tickRate.")]
        public void DefaultSettings_TickInterval_EqualsOneOverTickRate()
        {
            float expected = 1f / _manager.Settings.tickRate;
            Assert.AreEqual(expected, _manager.Settings.TickInterval, 0.0001f);
        }

        // ── Singleton ──────────────────────────────────────────────────────────

        [Test]
        [Description("HasInstance must return true while the instance is alive.")]
        public void HasInstance_AfterAwake_IsTrue()
        {
            Assert.IsTrue(NetworkManager.HasInstance);
        }

        [Test]
        [Description("HasInstance must return false after the instance is destroyed.")]
        public void HasInstance_AfterDestroy_IsFalse()
        {
            Object.DestroyImmediate(_go);
            _go = null;  // prevent double-destroy in TearDown

            Assert.IsFalse(NetworkManager.HasInstance);
        }

        // ── Input validation ───────────────────────────────────────────────────

        [Test]
        [Description("Connect with null API key must log an error and not change state.")]
        public void Connect_NullApiKey_DoesNotChangeState()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("apiKey"));

            _manager.Connect(null);

            Assert.AreEqual(NetworkState.Disconnected, _manager.State,
                "State must remain Disconnected when an invalid apiKey is supplied.");
        }

        [Test]
        [Description("Connect with empty API key must log an error and not change state.")]
        public void Connect_EmptyApiKey_DoesNotChangeState()
        {
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("apiKey"));

            _manager.Connect("");

            Assert.AreEqual(NetworkState.Disconnected, _manager.State);
        }

        // ── Disconnect guard ────────────────────────────────────────────────────

        [Test]
        [Description("Disconnect while already Disconnected must be a safe no-op.")]
        public void Disconnect_WhenAlreadyDisconnected_IsNoOp()
        {
            Assert.DoesNotThrow(() => _manager.Disconnect());
            Assert.AreEqual(NetworkState.Disconnected, _manager.State);
        }

        [Test]
        [Description("Disconnect() from the initial Disconnected state must be a silent no-op: " +
                     "OnDisconnected must NOT fire and state must remain Disconnected. " +
                     "(Full ClientRequest reason test requires reaching Connected state; " +
                     "added in Week 9 with MockTransport test seam.)")]
        public void Disconnect_WhenAlreadyDisconnected_DoesNotFireOnDisconnected()
        {
            int firedCount = 0;
            _manager.OnDisconnected += _ => firedCount++;

            _manager.Disconnect();

            Assert.AreEqual(NetworkState.Disconnected, _manager.State,
                "State must stay Disconnected.");
            Assert.AreEqual(0, firedCount,
                "OnDisconnected must NOT fire when already in Disconnected state.");
        }

        // ── Send guard ─────────────────────────────────────────────────────────

        [Test]
        [Description("Send while not connected must log a warning and not throw.")]
        public void Send_WhenNotConnected_LogsWarning()
        {
            LogAssert.Expect(LogType.Warning, new System.Text.RegularExpressions.Regex("not connected"));

            Assert.DoesNotThrow(() => _manager.Send(new byte[] { 0x01, 0x02 }));
        }
    }
}
