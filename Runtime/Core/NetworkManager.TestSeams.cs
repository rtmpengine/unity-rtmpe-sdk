// RTMPE SDK — Runtime/Core/NetworkManager.TestSeams.cs
//
// Internal test seams + SafeRaise event multicast helpers.
// Part of the NetworkManager partial class — see NetworkManager.cs for the
// canonical class declaration, base type, and Unity attributes.
//
// Compilation gate:
//   The internal `*ForTests` methods below are wrapped in
//   `#if UNITY_INCLUDE_TESTS` so they compile only inside test-runner builds
//   (Editor + Test Framework) and are STRIPPED from Player builds shipped to
//   subscribers.  This keeps the production assembly free of any code path
//   that exists solely to reach internal state from a fixture — reflection
//   probes against the shipped DLL cannot land on a seam that does not exist
//   in the published IL.
//
//   The Test Framework defines `UNITY_INCLUDE_TESTS` whenever the Test Runner
//   compiles the runtime assembly to satisfy a test assembly that references
//   it.  The test asmdef at Tests/Runtime/RTMPE.SDK.Tests.asmdef already
//   carries the matching `defineConstraints` entry, so the symbol is set
//   atomically with the test assembly compilation — there is no scenario in
//   which the tests load while the seams are absent.

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using RTMPE.Threading;
using RTMPE.Transport;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Rpc;
using RTMPE.Sync;
using RTMPE.Infrastructure.Compression;

namespace RTMPE.Core
{
    public sealed partial class NetworkManager
    {
#if UNITY_INCLUDE_TESTS
        // ── Protocol-error rejection test seam ────────────────────────────────

        /// <summary>
        /// Drives <see cref="OnHandshakeAck"/> from a test after forcing the
        /// state machine into <see cref="NetworkState.Connecting"/> via
        /// <see cref="ForceConnectingStateForTests"/>.
        /// Do NOT call from production code.
        /// </summary>
        internal void SimulateLegacyHandshakeAckForTests()
            => OnHandshakeAck(null);

        /// <summary>
        /// Forces the state machine into <see cref="NetworkState.Connecting"/>
        /// so a unit test can observe how the SDK handles packets received in
        /// that state without opening a real UDP socket.
        /// Do NOT call from production code.
        /// </summary>
        internal void ForceConnectingStateForTests()
            => TransitionTo(NetworkState.Connecting);

        internal static void ResetJwtAudienceUnconfiguredWarningForTests()
            => JwtValidator.ResetAudienceUnconfiguredWarningForTests();

        // ── Issue-5 test hooks: _applicationIsQuitting Volatile semantics ────────

        /// <summary>
        /// Simulates OnApplicationQuit without invoking Cleanup — lets tests
        /// verify that all singleton accessors return null once the quitting
        /// flag is set.  Do NOT call from production code.
        /// </summary>
        internal static void SimulateApplicationQuitForTests()
            => System.Threading.Volatile.Write(ref _applicationIsQuitting, true);

        /// <summary>
        /// Clears the quitting flag after a SimulateApplicationQuitForTests call
        /// so subsequent tests start from a clean state.  Do NOT call from
        /// production code.
        /// </summary>
        internal static void ResetApplicationQuittingForTests()
            => System.Threading.Volatile.Write(ref _applicationIsQuitting, false);

        // ── Issue-6 test hooks: RoomManager subscription lifecycle ───────────────

        /// <summary>
        /// Exposes the current RoomManager instance for subscription-leak
        /// assertions in tests.  Do NOT call from production code.
        /// </summary>
        internal RoomManager GetRoomManagerForTests() => _roomManager;

        /// <summary>
        /// Creates a PacketBuilder and calls RecreateRoomAndSpawnManagers so
        /// tests can reach a state where _roomManager has live subscriptions
        /// without opening a real transport.  Do NOT call from production code.
        /// </summary>
        internal void SetupRoomManagerForTests()
        {
            _packetBuilder = new PacketBuilder();
            RecreateRoomAndSpawnManagers();
        }

        /// <summary>
        /// Invokes the internal Cleanup method so tests can verify teardown
        /// without going through DestroyImmediate or OnApplicationQuit.
        /// Do NOT call from production code.
        /// </summary>
        internal void InvokeCleanupForTests() => Cleanup();

        // ── Backend-integration test hooks (HeartbeatAck backpressure + Disconnect reason) ──

        /// <summary>
        /// Drives <see cref="OnHeartbeatAck"/> directly from a test.  The seam
        /// exists so unit tests can assert that the gateway-supplied
        /// backpressure byte is correctly extracted from the HeartbeatAck
        /// payload without standing up a live UDP socket.  Do NOT call from
        /// production code.
        /// </summary>
        internal void SimulateHeartbeatAckForTests(byte[] payload)
            => OnHeartbeatAck(BuildWirePacketForTests(PacketType.HeartbeatAck, payload ?? System.Array.Empty<byte>()));

        /// <summary>
        /// Drives <see cref="OnServerDisconnect"/> directly from a test.
        /// Used to verify that the typed-reason byte introduced by the
        /// gateway's <c>disconnect_reason</c> wire contract is mapped to the
        /// matching <see cref="DisconnectReason"/> enum value.  Do NOT call
        /// from production code.
        /// </summary>
        internal void SimulateServerDisconnectForTests(byte[] payload)
            => OnServerDisconnect(BuildWirePacketForTests(PacketType.Disconnect, payload ?? System.Array.Empty<byte>()));

        /// <summary>
        /// Forces the session-established gate to <paramref name="value"/> so
        /// <see cref="OnServerDisconnect"/> tests can reach the disconnect
        /// path without running a real handshake.  Do NOT call from
        /// production code.
        /// </summary>
        internal void SetSessionEstablishedForTests(bool value)
            => _sessionEstablished = value;

        // Build a minimal, valid wire packet (13-byte header + payload) so
        // PacketParser.ExtractPayload accepts the buffer and the test seam
        // exercises the same code path as a real receive.
        private static byte[] BuildWirePacketForTests(PacketType type, byte[] payload)
        {
            byte[] wire = new byte[PacketProtocol.HEADER_SIZE + payload.Length];
            wire[0] = (byte)(PacketProtocol.MAGIC & 0xFF);
            wire[1] = (byte)(PacketProtocol.MAGIC >> 8);
            wire[2] = PacketProtocol.VERSION;
            wire[3] = (byte)type;
            wire[4] = 0; // flags
            // sequence (4 bytes LE) at offsets 5–8 stays zero.
            uint len = (uint)payload.Length;
            wire[9]  = (byte)(len);
            wire[10] = (byte)(len >> 8);
            wire[11] = (byte)(len >> 16);
            wire[12] = (byte)(len >> 24);
            if (payload.Length > 0)
                System.Buffer.BlockCopy(payload, 0, wire, PacketProtocol.HEADER_SIZE, payload.Length);
            return wire;
        }
#endif // UNITY_INCLUDE_TESTS

        // JWT signature and base64url decoding route through JwtValidator;
        // see Runtime/Core/JwtValidator.cs for the full verification surface.

        /// <summary>
        /// Convenience accessor onto <see cref="RTMPE.Core.Protocol.PacketGates.RequiresEncryption"/>.
        /// Production call sites (for example
        /// <see cref="NetworkManager.HandshakeHandlers"/>) reach the gate
        /// through this member; the canonical decision table itself lives in
        /// the dedicated <c>PacketGates</c> static class.  Existing security
        /// fixtures depend on this signature, so it is retained as a stable
        /// surface even though the body is a one-line delegation.
        /// </summary>
        internal static bool RequiresEncryption(PacketType type)
            => RTMPE.Core.Protocol.PacketGates.RequiresEncryption(type);

        private void LogDebug(string message)
        {
            if (_settings != null && _settings.enableDebugLogs)
                Debug.Log($"[RTMPE] {message}");
        }

        // Hot-path gate.  Callers building an interpolated $"..." argument
        // for LogDebug must test this first — interpolation allocates a
        // formatted string before LogDebug can suppress it, so a per-packet
        // call site dominates steady-state GC even when verbose logs are
        // off.  Cheap field-read; safe to call from any thread that reads
        // _settings under the same publication invariants as Awake.
        private bool IsDebugLogEnabled =>
            _settings != null && _settings.enableDebugLogs;

        // ── Resilient event multicast ─────────────────────────────────────────
        //
       // Default C# delegate invocation (handler?.Invoke(...)) walks the
        // multicast chain in registration order and propagates the FIRST
        // subscriber exception, aborting all later subscribers.  In an SDK
        // event surface this is a denial-of-service primitive: a single
        // misbehaving listener silences every other listener for the rest
        // of the process lifetime.  The helpers below isolate each
        // subscriber inside try/catch so one buggy handler cannot deafen
        // the rest.  Exceptions are surfaced via Debug.LogException for
        // visibility but never re-thrown — the network thread / state
        // machine must stay live regardless of application-level bugs.
        //
       // Invariant: NEVER call `handler.Invoke(...)` directly on a public
        // event.  All raise-sites in this class go through SafeRaise.

        private static void SafeRaise(Action handler, string eventName)
        {
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action)d)(); }
                catch (Exception ex) { LogSubscriberException(eventName, ex); }
            }
        }

        private static void SafeRaise<T>(Action<T> handler, T arg, string eventName)
        {
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<T>)d)(arg); }
                catch (Exception ex) { LogSubscriberException(eventName, ex); }
            }
        }

        private static void SafeRaise<T1, T2>(Action<T1, T2> handler, T1 a1, T2 a2, string eventName)
        {
            if (handler == null) return;
            foreach (var d in handler.GetInvocationList())
            {
                try { ((Action<T1, T2>)d)(a1, a2); }
                catch (Exception ex) { LogSubscriberException(eventName, ex); }
            }
        }

        private static void LogSubscriberException(string eventName, Exception ex)
        {
            // We log via Debug.LogError with the inner exception attached so the
            // stack trace points at the throwing subscriber, not at SafeRaise.
            Debug.LogError($"[RTMPE] Subscriber threw in event '{eventName}': {ex.GetType().Name}: {ex.Message}");
            Debug.LogException(ex);
        }
    }
}
