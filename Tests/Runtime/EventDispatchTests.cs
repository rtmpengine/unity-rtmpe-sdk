// RTMPE SDK — Tests/Runtime/EventDispatchTests.cs
//
// Verifies the resilient multicast contract for NetworkManager events:
// a single throwing subscriber must NOT prevent later subscribers from
// firing.  We exercise the SafeRaise<T> helpers indirectly by hooking
// real public events and observing dispatch order around a thrower.

using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;
using RTMPE.Core;

namespace RTMPE.Tests
{
    [TestFixture]
    [Category("EventDispatch")]
    public class EventDispatchTests
    {
        private GameObject     _go;
        private NetworkManager _manager;

        [SetUp]
        public void SetUp()
        {
            _go      = new GameObject("EventDispatchTests_NM");
            _manager = _go.AddComponent<NetworkManager>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_go != null) UnityEngine.Object.DestroyImmediate(_go);
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Force a state transition through the public API surface in a
        /// way that exercises OnStateChanged / OnConnected / OnDisconnected
        /// without standing up a real network thread.  We use Disconnect()
        /// while in Disconnected (no-op for transport, but the explicit
        /// Connect path is too heavy for an edit-mode test) — instead we
        /// invoke the internal transition by toggling state via Reflection.
        /// </summary>
        private void InvokeTransitionTo(NetworkState target)
        {
            var t = typeof(NetworkManager);
            var m = t.GetMethod("TransitionTo",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(m, "TransitionTo method not found via reflection.");
            m.Invoke(_manager, new object[] { target, DisconnectReason.Unknown });
        }

        // ── Tests ────────────────────────────────────────────────────────

        [Test]
        [Description("A throwing subscriber on OnStateChanged must not block later subscribers.")]
        public void OnStateChanged_ThrowingSubscriber_DoesNotAbortChain()
        {
            int beforeThrowCount = 0;
            int afterThrowCount  = 0;

            _manager.OnStateChanged += (prev, next) => { beforeThrowCount++; };
            _manager.OnStateChanged += (prev, next) =>
            {
                throw new InvalidOperationException("intentional test failure");
            };
            _manager.OnStateChanged += (prev, next) => { afterThrowCount++; };

            // Expect both an error log and an exception log from SafeRaise.
            LogAssert.Expect(LogType.Error, new Regex("Subscriber threw in event 'OnStateChanged'"));
            LogAssert.Expect(LogType.Exception, new Regex("InvalidOperationException"));

            InvokeTransitionTo(NetworkState.Connecting);

            Assert.AreEqual(1, beforeThrowCount, "Subscriber registered before the thrower must run.");
            Assert.AreEqual(1, afterThrowCount,  "Subscriber registered after the thrower must still run.");
        }

        [Test]
        [Description("Multiple throwing subscribers must each be logged and not stop the chain.")]
        public void OnConnected_MultipleThrowers_AllSurvived()
        {
            int counter = 0;

            _manager.OnConnected += () => { counter++; };
            _manager.OnConnected += () => throw new Exception("first");
            _manager.OnConnected += () => { counter++; };
            _manager.OnConnected += () => throw new Exception("second");
            _manager.OnConnected += () => { counter++; };

            LogAssert.Expect(LogType.Error,     new Regex("Subscriber threw in event 'OnConnected'"));
            LogAssert.Expect(LogType.Exception, new Regex("Exception"));
            LogAssert.Expect(LogType.Error,     new Regex("Subscriber threw in event 'OnConnected'"));
            LogAssert.Expect(LogType.Exception, new Regex("Exception"));

            InvokeTransitionTo(NetworkState.Connecting);
            InvokeTransitionTo(NetworkState.Connected);

            Assert.AreEqual(3, counter, "All non-throwing subscribers must have fired.");
        }

        [Test]
        [Description("With no subscribers, raising an event is a silent no-op (no NRE).")]
        public void NoSubscribers_RaiseIsNoOp()
        {
            Assert.DoesNotThrow(() => InvokeTransitionTo(NetworkState.Connecting));
        }
    }
}
