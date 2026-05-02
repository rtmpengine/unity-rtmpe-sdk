// RTMPE SDK — Runtime/Core/NetworkManager.cs
//
// Central entry point for all RTMPE networking. Singleton MonoBehaviour that wires
// together NetworkSettings, NetworkThread, and MainThreadDispatcher.
//
// Threading model:
// • NetworkManager lives on the Unity main thread.
// • NetworkThread runs on a dedicated background thread.
// • Packets received on the background thread are delivered via MainThreadDispatcher
//   so that all state mutations and Unity API calls occur on the main thread.
//
// Singleton contract:
// • [DefaultExecutionOrder(-1000)] — Awake runs before all other components.
// • Instance getter returns the scene-placed NetworkManager if one exists,
//   otherwise null. It does NOT auto-create a stand-in with empty defaults —
//   silent auto-creation hid configuration bugs and produced sessions with
//   blank crypto material when an unrelated component touched Instance early.
// • _applicationIsQuitting flag guards against Unity's destroy-order issues.
//
// Protocol note:
// • All header field constants use PacketProtocol.* from NetworkConstants.cs.
//   Do NOT introduce magic numbers here — sync failures with the Rust gateway are
//   silent and extremely difficult to debug.

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
    /// <summary>
    /// Main entry point for RTMPE networking.
    /// Add to a persistent GameObject or let the singleton auto-create one.
    /// All public methods must be called from the Unity main thread.
    /// </summary>
    [DefaultExecutionOrder(-1000)]
    [AddComponentMenu("RTMPE/NetworkManager")]
    public sealed partial class NetworkManager : MonoBehaviour
    {
        // ── Implementation split across NetworkManager.*.cs partial files: ──
        //
        //   • NetworkManager.Singleton.cs        — singleton + transport factory
        //   • NetworkManager.Fields.cs           — fields, runtime state, properties
        //   • NetworkManager.Events.cs           — public event surface
        //   • NetworkManager.Lifecycle.cs        — Awake/Start/Update/OnDestroy + scene
        //   • NetworkManager.Connection.cs       — Connect/Reconnect/Disconnect + InitialiseNetwork
        //   • NetworkManager.HandshakeHandlers.cs — Challenge/SessionAck handlers
        //   • NetworkManager.ReceivePath.cs      — ProcessPacket dispatch + state machine
        //   • NetworkManager.AeadPipeline.cs     — EncryptAndSend / DecryptInbound
        //   • NetworkManager.GameData.cs         — StateSync / Variables / RPC handlers
        //   • NetworkManager.Jwt.cs              — JWT validation + auth helpers
        //   • NetworkManager.TestSeams.cs        — internal test hooks + SafeRaise
        //
        // The C# compiler merges these into a single sealed type at build time;
        // there is no runtime cost to the partial split.  All public API surface,
        // threading semantics, and AEAD wire format are maintained intact.
    }


    // ── Connection state ──────────────────────────────────────────────────────

    /// <summary>Connection lifecycle states for <see cref="NetworkManager"/>.</summary>
    public enum NetworkState
    {
        /// <summary>No active connection.</summary>
        Disconnected,

        /// <summary>Handshake in progress; waiting for gateway response.</summary>
        Connecting,

        /// <summary>Authenticated and connected; not yet in a room.</summary>
        Connected,

        /// <summary>Connected and inside an active room.</summary>
        InRoom,

        /// <summary>Graceful disconnect in progress.</summary>
        Disconnecting,

        /// <summary>
        /// **N-1** — heartbeat timed out or the transport dropped, but the
        /// previous session's reconnect token is still valid.  The SDK is
        /// backing off + retrying a shortcut <c>ReconnectInit</c> handshake.
        /// On success, the state transitions straight back to
        /// <see cref="Connected"/> (or <see cref="InRoom"/> once the SDK
        /// auto-rejoins the last room).  On token exhaustion or hard failure
        /// the state falls back to <see cref="Disconnected"/>.
        /// </summary>
        Reconnecting
    }

    /// <summary>Reason codes for <see cref="NetworkManager.OnDisconnected"/>.</summary>
    public enum DisconnectReason
    {
        Unknown,
        ClientRequest,
        ServerRequest,
        Timeout,
        ConnectionLost,
        Kicked,
        /// <summary>
        /// The outbound nonce counter was exhausted after 2^32 packets.
        /// The session must be re-established with a fresh handshake.
        /// </summary>
        NonceExhausted,
        /// <summary>
        /// The gateway sent a packet that violates the expected protocol
        /// sequence (e.g. a legacy handshake type incompatible with the
        /// current security model). The connection cannot be trusted.
        /// </summary>
        ProtocolError
    }
}
