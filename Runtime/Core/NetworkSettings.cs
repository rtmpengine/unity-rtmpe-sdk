// RTMPE SDK — Runtime/Core/NetworkSettings.cs
//
// Project-wide RTMPE connection settings stored as a ScriptableObject asset.
//
// Create via: Assets > Create > RTMPE > Settings
// Then assign the created asset to the NetworkManager's "Settings" field in the Inspector.
//
// If no asset is assigned at runtime, NetworkManager.Awake() calls
// NetworkSettings.CreateDefault() to produce a runtime-only instance with the
// defaults defined here — zero configuration needed for local development.

using UnityEngine;
using RTMPE.Crypto;

namespace RTMPE.Core
{
    /// <summary>
    /// Project-wide RTMPE connection settings.
    /// Store as a <see cref="ScriptableObject"/> asset. You can maintain multiple
    /// profiles (e.g. <c>RTMPESettings_Dev.asset</c>, <c>RTMPESettings_Prod.asset</c>)
    /// and swap them in the <see cref="NetworkManager"/> Inspector field.
    /// </summary>
    [CreateAssetMenu(
        fileName = "RTMPESettings",
        menuName  = "RTMPE/Settings",
        order     = 1)]
    public sealed class NetworkSettings : ScriptableObject
    {
        // ── Server ──────────────────────────────────────────────────────────────

        [Header("Server")]
        [Tooltip("RTMPE gateway hostname or IP address.")]
        public string serverHost = "127.0.0.1";

        [Tooltip("UDP port the RTMPE gateway listens on (default: 7777).")]
        [Range(1, 65535)]
        public int serverPort = 7777;

        // ── Timing ──────────────────────────────────────────────────────────────

        [Header("Timing")]
        [Tooltip("Interval in milliseconds between keepalive heartbeat packets.")]
        [Range(100, 60_000)]
        public int heartbeatIntervalMs = 5_000;

        [Tooltip("Maximum time in milliseconds to wait for the initial handshake to complete.")]
        [Range(1_000, 60_000)]
        public int connectionTimeoutMs = 10_000;

        [Tooltip("Expected server tick rate in Hz. Must match the room-service configuration.")]
        [Range(1, 128)]
        public int tickRate = 30;

        // ── Reconnect behaviour ─────────────────────────────────────────────────

        [Header("Reconnect")]
        [Tooltip(
            "When true (default), after a successful token-based Reconnect() the SDK automatically " +
            "rejoins the last room (by ID) that was active immediately before the disconnect. " +
            "Fires RoomManager.OnRoomJoined as usual. " +
            "Set to false if your app wants to prompt the user or run room-selection UI on reconnect.")]
        public bool autoRejoinLastRoomOnReconnect = true;

        [Tooltip(
            "Upper bound on bounded-retry reconnect attempts driven by Reconnect(). " +
            "When the internal coroutine exhausts this budget without reaching " +
            "Connected, the manager transitions to Disconnected, clears session " +
            "state and fires OnReconnectFailed so the application can surface the " +
            "failure to the user instead of staying frozen in the Reconnecting " +
            "state. Spacing between attempts uses ReconnectBackoff (full-jitter " +
            "capped exponential).")]
        [Range(1, 50)]
        public int maxReconnectAttempts = 5;

        // ── Buffers ─────────────────────────────────────────────────────────────

        [Header("Buffers")]
        // Defaults sized for a typical 30 Hz, 16-player burst
        // (~480 datagrams/second).  The previous 4 KiB cap held only ~3
        // datagrams worth of kernel buffer and produced silent receive-side
        // drops on every tick boundary; 256 KiB absorbs the worst burst
        // with headroom while staying inside the per-socket rmem_max on
        // unmodified Linux and Windows hosts.
        [Tooltip("UDP socket SO_SNDBUF size in bytes. Default 256 KiB sized " +
                 "for 30 Hz 16-player sessions (~480 datagrams/second).")]
        [Range(4_096, 4_194_304)]
        public int sendBufferBytes = 262_144;

        [Tooltip("UDP socket SO_RCVBUF size in bytes. Default 256 KiB sized " +
                 "for 30 Hz 16-player sessions (~480 datagrams/second). " +
                 "Smaller values cause silent kernel-side datagram drops.")]
        [Range(4_096, 4_194_304)]
        public int receiveBufferBytes = 262_144;

        [Tooltip("Size of the scratch buffer used by the network thread for reading incoming datagrams.")]
        [Range(1_024, 65_536)]
        public int networkThreadBufferBytes = 8_192;

        /// <summary>
        /// Hard cap on the depth of <c>NetworkThread</c>'s outbound send queue.
        /// When the queue is at this depth, additional <c>Send</c> /
        /// <c>SendOwned</c> calls drop the newest packet and increment
        /// <c>NetworkThread.SendQueueDroppedCount</c> instead of enqueueing
        /// (drop-newest preserves arrival ordering of already-queued traffic).
        /// Bounds heap usage when producers outpace the drain — primarily
        /// under sustained ENOBUFS, where drain throughput falls below the
        /// 30 Hz × 16-player producer rate.  Default 4096 ≈ 4 MB at 1200 B
        /// per item, sized for mobile.  Integrators MUST monitor
        /// <c>SendQueueDroppedCount</c> to detect saturation.
        /// </summary>
        [Tooltip("Hard cap on the NetworkThread outbound send-queue depth. " +
                 "When at the cap, additional packets are dropped (newest) " +
                 "and SendQueueDroppedCount is incremented; OnError is NOT " +
                 "raised. Bounds heap usage when the producer outpaces the " +
                 "drain (sustained ENOBUFS). Default 4096 ≈ 4 MB at 1200 B " +
                 "per item — sized for mobile. Monitor " +
                 "NetworkThread.SendQueueDroppedCount for saturation.")]
        [Range(64, 65_536)]
        public int sendQueueMaxItems = 4_096;

        // ── Interest management ─────────────────────────────────────────────────

        [Header("Interest Management")]
        [Tooltip(
            "Hysteresis margin (world units) added to InterestManager.ReceiveFilterRadius " +
            "when deciding whether a currently-visible object should leave the interest set.\n\n" +
            "Objects ENTER visibility at ReceiveFilterRadius (strict); they LEAVE only after " +
            "they exceed ReceiveFilterRadius + this margin. Eliminates the per-tick flap that " +
            "occurs when an object loiters at the radius boundary.\n\n" +
            "When >= 0 this value overrides the per-component InterestManager.HysteresisMargin " +
            "field at runtime so a project-wide tuning change does not require touching every " +
            "prefab. Set to -1 to opt out of the global override and use the per-component value.")]
        [Range(-1f, 50f)]
        public float interestHysteresisMargin = 1f;

        // ── Sync hardening (physics & transform plausibility) ──────────────────

        [Header("Sync Hardening")]
        [Tooltip("Reject incoming physics-state packets whose linear-velocity " +
                 "magnitude exceeds this threshold (units/second). Defends against " +
                 "a hostile peer or compromised server attempting to launch a " +
                 "remote Rigidbody at unbounded speed. 0 disables the cap.")]
        [Range(0f, 100_000f)]
        public float maxLinearVelocity = 1_000f;

        [Tooltip("Reject incoming physics-state packets whose angular-velocity " +
                 "magnitude exceeds this threshold (rad/s for 3-D, deg/s for 2-D). " +
                 "0 disables the cap.")]
        [Range(0f, 100_000f)]
        public float maxAngularVelocity = 1_000f;

        [Tooltip("Reject incoming physics-state packets whose position differs " +
                 "from the last accepted position by more than this many world " +
                 "units. Prevents single-tick teleportation. 0 disables the cap.")]
        [Range(0f, 100_000f)]
        public float maxPositionDeltaPerTick = 50f;

        [Tooltip("Maximum number of physics-state packets accepted per second " +
                 "per object. Excess packets are dropped before any state is " +
                 "applied. 0 disables the rate limit.")]
        [Range(0f, 1_000f)]
        public float maxPhysicsPacketsPerSecond = 240f;

        [Tooltip("When false (default), incoming ConstraintMask updates are " +
                 "ignored: constraints set at spawn cannot be mutated by a " +
                 "remote sender. Set true if your design requires runtime " +
                 "constraint propagation across the network.")]
        public bool allowDynamicConstraints = false;

        [Tooltip("When AllowDynamicConstraints is true, only bits set in this " +
                 "allowlist are honoured on the receiving Rigidbody. Default " +
                 "0xFF accepts all bits; lower bits restrict the writable set.")]
        [Range(0, 255)]
        public int dynamicConstraintsAllowMask = 0xFF;

        [Tooltip("Maximum world-space distance the server may correct the local " +
                 "transform per reconciliation. Corrections beyond this distance " +
                 "are rejected (with a warning) so a hostile or compromised server " +
                 "cannot teleport the client to an arbitrary position. 0 disables " +
                 "the cap (back-compat mode).")]
        [Range(0f, 100_000f)]
        public float maxServerCorrectionDistance = 50f;

        [Tooltip("When true, reconciliation positions outside WorldBounds are " +
                 "rejected. Defines hard world-space limits the server cannot " +
                 "corrupt the local view past.")]
        public bool worldBoundsEnabled = false;

        [Tooltip("Centre of the world-bounds AABB (world units). Effective only " +
                 "when WorldBoundsEnabled is true.")]
        public Vector3 worldBoundsCenter = Vector3.zero;

        [Tooltip("Half-extents of the world-bounds AABB (world units). Effective " +
                 "only when WorldBoundsEnabled is true.")]
        public Vector3 worldBoundsExtents = new Vector3(10_000f, 10_000f, 10_000f);

        // ── Lobby / matchmaking JSON hardening ─────────────────────────────────

        [Header("Lobby Hardening")]
        [Tooltip("Maximum number of room entries the lobby parser will accept " +
                 "from a server-pushed lobby room list. Prevents unbounded " +
                 "allocation from a malicious or buggy server. Matches the " +
                 "256-entry cap used by RoomPacketParser.")]
        [Range(1, 100_000)]
        public int maxLobbyRoomEntries = 256;

        [Tooltip("Maximum byte length of any single string field parsed out of " +
                 "a server-supplied lobby/matchmaking JSON payload (room codes, " +
                 "lobby names, error strings).")]
        [Range(16, 65_536)]
        public int maxLobbyStringBytes = 256;

        // ── Client-side prediction ─────────────────────────────────────────────

        [Header("Client-Side Prediction")]
        [Tooltip("Default position-error threshold (world units) below which a server " +
                 "reconciliation is accepted as-is — no visible correction is applied. " +
                 "Used by NetworkTransform when its per-instance Inspector value is left " +
                 "at the sentinel ReconcileUseProjectDefault. Tune higher for fast-moving, " +
                 "low-precision games (TPS, racing) and lower for tactical / fighting titles " +
                 "where 10 cm of drift is visible. The CSP test suite exercises 0.05–0.25 m.")]
        [Range(0f, 10f)]
        public float reconcileLerpThreshold = 0.1f;

        [Tooltip("Default position-error threshold (world units) above which the local " +
                 "predicted transform snaps directly to the server pose rather than lerping. " +
                 "Set high enough that a typical RTT-induced error does NOT snap (which would " +
                 "look like teleportation), but low enough that a genuine cheat or desync is " +
                 "corrected within one tick. The 2 m default suits 30 Hz character movement; " +
                 "raise for vehicles / projectiles, lower for first-person aim where any pop " +
                 "is jarring. MUST exceed reconcileLerpThreshold; values below it are clamped " +
                 "at runtime.")]
        [Range(0f, 1_000f)]
        public float reconcileSnapThreshold = 2.0f;

        // ── Bandwidth optimisation ────────────────────────────────────────────

        [Header("Bandwidth Optimisation")]
        [Tooltip("When true, NetworkTransform packets encode position and scale " +
                 "as 16-bit half-precision floats and rotation as a 32-bit " +
                 "smallest-three packed quaternion. Halves the position/scale " +
                 "wire size and quarters the rotation wire size at the cost of " +
                 "approximately 0.1% relative position error and 0.1° angular " +
                 "error. Default OFF — the gateway must understand the " +
                 "FLAG_QUANTIZED bit before clients can negotiate this " +
                 "encoding; the decoder accepts both formats so flipping the " +
                 "toggle on a single peer is safe.")]
        public bool quantizeTransforms = false;

        [Tooltip("Forward-compatible scaffold for gameplay packet ordering. " +
                 "The buffer logic (GameplayOrderingBuffer) and the sequence " +
                 "prefix helpers (GameplaySequencePrefix) are implemented and " +
                 "unit-tested, but the dispatcher wiring activates only once " +
                 "the gateway negotiates the FLAG_GAMEPLAY_ORDERED protocol " +
                 "bit; until that negotiation lands, no Enqueue path is hooked " +
                 "up and toggling this flag has no observable runtime effect. " +
                 "Default OFF.  When the wiring is enabled, gameplay packets " +
                 "(transform, RPC, variable update) will carry a shared 4-byte " +
                 "monotonic gameplay sequence and the receiver will buffer up " +
                 "to GameplayOrderingBufferSize out-of-order packets so an RPC " +
                 "and a subsequent state update cannot invert under UDP reorder.")]
        public bool enableGameplayOrdering = false;

        [Tooltip("Bound on the per-session reorder buffer used by " +
                 "EnableGameplayOrdering.  Eight slots cover a 250-ms reorder " +
                 "window at 30 Hz tick rate; raising the cap defeats memory-" +
                 "amplification by an attacker who replays a flood of low-" +
                 "sequence packets to pin the buffer at maximum occupancy.")]
        [Range(2, 64)]
        public int gameplayOrderingBufferSize = 8;

        [Tooltip("When true, every encrypted packet additionally binds a " +
                 "4-byte little-endian application-level monotonic sequence " +
                 "into the AEAD AAD and sets FLAG_APP_SEQUENCE on the wire " +
                 "header.  The wire Sequence field carries the AEAD nonce " +
                 "counter once a session is up, so the application sequence " +
                 "would otherwise survive only inside the encrypted plaintext " +
                 "and be unavailable for cheap deduplication or ordering at " +
                 "the receiver.  Layering it through the AAD lets the gateway " +
                 "or peer drop duplicates and order packets without decrypting " +
                 "first, while AEAD authentication prevents an on-path " +
                 "attacker from forging the sequence value.  Default OFF — " +
                 "the gateway must opt in to the new flag bit.")]
        public bool preserveApplicationSequence = false;

        [Tooltip("Hard cap on the apparent owner velocity (world units per " +
                 "second) NetworkTransform may broadcast.  Movement faster " +
                 "than this is clamped to the cap rate at the wire, defeating " +
                 "casual speed-hack overlays that overwrite transform.position " +
                 "directly. Legitimate teleports must call OwnerTeleportTo() " +
                 "to bypass the cap. Set 0 to disable (back-compat).")]
        [Range(0f, 1_000f)]
        public float maxOwnerVelocityMetersPerSecond = 50f;

        [Tooltip("When true, dirty NetworkVariables across every owned object " +
                 "are coalesced into a single VariableBatchUpdate packet per " +
                 "tick.  Eliminates the per-packet ~61-byte header tax that " +
                 "dominates traffic when many objects emit small deltas. " +
                 "Default OFF — the gateway must understand the " +
                 "VariableBatchUpdate (0x44) packet type; with the toggle off " +
                 "the legacy per-object 0x41 path is used.")]
        public bool enableVariableBatching = false;

        [Tooltip("Maximum number of variable updates packed into a single " +
                 "VariableBatchUpdate.  Excess updates split into a second " +
                 "batch packet rather than exceeding the per-datagram MTU.")]
        [Range(1, 256)]
        public int maxVariablesPerBatch = 32;

        // ── Spawn hardening ────────────────────────────────────────────────────

        [Header("Spawn Hardening")]
        [Tooltip("Maximum number of inbound or local Spawn requests honoured per " +
                 "second per session.  A hostile gateway emitting an Instantiate " +
                 "storm is bounded to this rate, preserving the main thread budget " +
                 "and the GameObject pool on mobile devices.  Excess spawns in the " +
                 "same one-second window are dropped (with a one-shot redacted log).")]
        [Range(1, 1_000)]
        public int maxSpawnsPerSecond = 100;

        [Tooltip("Maximum number of concurrently live spawned objects per session. " +
                 "Once reached, additional Spawn requests are rejected until existing " +
                 "objects despawn.  Bounds total memory regardless of arrival rate; a " +
                 "slow attacker spawning under the per-second cap still cannot exhaust " +
                 "the heap.")]
        [Range(100, 50_000)]
        public int maxSpawnsPerRoom = 5_000;

        // ── Replication hardening ──────────────────────────────────────────────

        [Header("Replication Hardening")]
        [Tooltip("Hard cap on the element count accepted in a NetworkVariableList " +
                 "FullSync payload.  A hostile or buggy sender could otherwise " +
                 "set the wire-level uint16 length to 65535 and force the " +
                 "receiver to preallocate up to ~512 KB per variable per tick. " +
                 "1024 covers any realistic gameplay list (inventories, kill " +
                 "feeds, active buffs); raise only with a measured need.")]
        [Range(1, 65_535)]
        public int maxNetworkVariableListSize = 1024;

        // ── Debug ────────────────────────────────────────────────────────────────

        [Header("Debug")]
        [Tooltip("Log verbose NetworkManager state transitions to the Unity Console.")]
        public bool enableDebugLogs;

        // ── RPC authorisation ──────────────────────────────────────────────────

        [Tooltip("When true (default), every legacy MethodId-dispatched RPC is " +
                 "passed through EnhancedRpcVerifier.IsSenderAcceptable before " +
                 "the per-method handler runs.  The Enhanced RPC path already " +
                 "applies this gate; mirroring it on the legacy path closes the " +
                 "spoof window where a peer claims senderId=0 (uninitialised " +
                 "sentinel) or a non-roster id and the receiver dispatches Ping " +
                 "/ ApplyDamage as if the gateway had verified the origin.  " +
                 "Set to false only when a customer architecture deliberately " +
                 "delivers MethodId 100/301 from a sender outside the active " +
                 "session roster (e.g. server-of-record relays).")]
        public bool requireLegacyRpcSender = true;

        // ── Forward-compat protocol toggles ────────────────────────────────────
        //
        // The three toggles below gate features whose gateway counterpart is
        // either in flight or behind an environment variable.  Each toggle is
        // OFF by default so existing deployments are unaffected; enabling any
        // of them on the SDK without the matching gateway support causes
        // handshake failure or AEAD authentication failure (a hostile or
        // mismatched gateway cannot abuse a customer who has not opted in).
        //
        // Customers MUST flip these in lock-step with the gateway operator —
        // there is no negotiation handshake; the wire format diverges silently
        // when only one side opts in.

        [Header("Forward-compat Protocol")]
        [SerializeField]
        [Tooltip("If true, the SDK expects the gateway to encrypt SessionAck under " +
                 "the ECDH-derived key. Must match the gateway's RTMPE_ENCRYPT_SESSION_ACK " +
                 "setting; mismatched configurations cause handshake failure.")]
        private bool _expectEncryptedSessionAck;

        /// <summary>
        /// When true, the SDK decrypts the inbound SessionAck payload under the
        /// ECDH-derived bootstrap key (HKDF info suffix <c>\x03</c>).  Must be
        /// flipped together with the gateway's <c>RTMPE_ENCRYPT_SESSION_ACK</c>
        /// environment variable; otherwise the handshake terminates with an
        /// AEAD-authentication failure.
        /// </summary>
        public bool ExpectEncryptedSessionAck => _expectEncryptedSessionAck;

        [Header("Reliable Delivery")]
        [SerializeField]
        [Tooltip(
            "If true, the SDK emits the 4-byte ARQ sequence sub-header when " +
            "FLAG_RELIABLE is set. Requires a matching gateway build that consumes " +
            "the sub-header — flip both sides together.\n\n" +
            "When this setting is FALSE and a caller invokes " +
            "NetworkManager.Send(reliable: true), the SDK silently downgrades to " +
            "best-effort delivery (no application-layer retransmit, no DataAck). " +
            "Lost packets on raw UDP are not recovered. A one-time Debug.LogWarning " +
            "names the silent downgrade the first time it happens per process; " +
            "subsequent reliable sends continue to downgrade without further " +
            "logging.\n\n" +
            "On KCP and WebSocket transports the underlying stream already provides " +
            "reliability at the segment/stream layer, so leaving this setting FALSE " +
            "is the expected configuration there — the application-layer ARQ would " +
            "duplicate work the transport already performs. The setting matters " +
            "most for raw-UDP deployments.")]
        private bool _emitArqSequence;

        /// <summary>
        /// When true, every outbound packet that carries
        /// <see cref="PacketFlags.Reliable"/> has its 4-byte ARQ sequence
        /// inserted between the 13-byte fixed header and the encrypted
        /// payload, in little-endian.  The gateway must consume the sub-header
        /// from the same offset; if only one side opts in, AEAD AAD will not
        /// match and packets will be dropped at the receiver.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The sub-header is independent of any reliability guarantee the
        /// underlying transport (KCP, WebSocket) already provides: KCP
        /// retransmits at the segment level and WebSocket runs over TCP, so
        /// neither needs this field to deliver bytes.  The ARQ sub-header
        /// instead carries the application-layer reliable sequence value
        /// the gateway uses to authenticate per-packet ordering inside the
        /// AEAD AAD and to feed the duplicate-detection window.  Leaving
        /// this flag <c>false</c> keeps both endpoints on the legacy wire
        /// shape that omits the field; flipping it on requires the gateway
        /// to be running a build that consumes the sub-header.
        /// </para>
        /// <para>
        /// When the flag is <c>false</c> and an application calls
        /// <see cref="NetworkManager.Send"/> with <c>reliable: true</c>, the
        /// SDK silently routes the packet through the best-effort path: no
        /// retransmit entry is registered, no DataAck is expected, and a
        /// loss on the wire is not recovered.  A one-time
        /// <see cref="UnityEngine.Debug.LogWarning"/> surfaces this
        /// configuration mismatch the first time it occurs per process;
        /// the silent downgrade then continues for subsequent sends without
        /// further log spam.  Routing the advisory through warn-once keeps
        /// it actionable for the integrator who flipped (or forgot to flip)
        /// the setting, while letting steady-state traffic stay quiet.
        /// </para>
        /// </remarks>
        public bool EmitArqSequence => _emitArqSequence;

        [SerializeField]
        [Tooltip("If true, the SDK emits the 4-byte gameplay_seq sub-header when " +
                 "FLAG_GAMEPLAY_ORDERED is set on an outbound packet. Requires gateway " +
                 "support; activate together.")]
        private bool _emitGameplaySequencePrefix;

        /// <summary>
        /// When true, every outbound packet carrying
        /// <see cref="PacketFlags.GameplayOrdered"/> has the 4-byte gameplay
        /// sequence (LE u32) emitted on the wire by
        /// <see cref="GameplaySequencePrefix"/> before the encrypted payload.
        /// The gateway side skips the same sub-header when the flag bit is
        /// observed.
        /// </summary>
        public bool EmitGameplaySequencePrefix => _emitGameplaySequencePrefix;

        // ── Crypto ─────────────────────────────────────────────────────────────

        [Header("Crypto")]
        [Tooltip(
            "64-character lowercase hex string encoding the 32-byte pre-shared key used to " +
            "encrypt the API key in HandshakeInit packets.\n\n" +
            "Copy this value from the RTMPE developer dashboard. " +
            "It must match GATEWAY_API_KEY_ENCRYPTION_KEY_HEX on the server.\n\n" +
            "Leave blank to disable API-key encryption (insecure — dev/local only).")]
        public string apiKeyPskHex = "";

        [Tooltip(
            "Optional: 64-character lowercase hex string of the 32-byte Ed25519 static public key " +
            "of the gateway you expect to connect to (H4 server pinning).\n\n" +
            "Copy from the gateway startup log or developer dashboard. " +
            "Leaving blank skips pinning and trusts any valid Ed25519 signature.")]
        public string pinnedServerPublicKeyHex = "";

        [Tooltip(
            "Legacy boolean retained for back-compat with assets serialized before the " +
            "ServerPinningMode enum was introduced.  When TRUE, behaves identically to " +
            "ServerPinningMode.Strict regardless of the enum value below.  Leave FALSE in new " +
            "projects and use serverPinningMode instead.")]
        public bool requirePinnedServerPublicKey;

        [Tooltip(
            "Expected JWT issuer (`iss` claim) on tokens minted by the gateway.  When set, " +
            "SessionAck tokens whose `iss` differs are rejected and the session is " +
            "torn down.  Leave blank to skip the check (mismatched-deployment / dev).")]
        public string expectedJwtIssuer = "";

        [Tooltip(
            "Expected JWT audience (`aud` claim) for the local SDK build.  When set, " +
            "tokens whose `aud` does not contain this value are rejected.  Leave blank " +
            "to skip the check (mismatched-deployment / dev).")]
        public string expectedJwtAudience = "";

        [Tooltip(
            "Allowed clock skew, in seconds, when comparing the JWT `exp` and `nbf` " +
            "claims against the local wall clock.  Two minutes accommodates routine " +
            "drift on player devices without softening the exp check meaningfully.")]
        public int jwtClockSkewSeconds = 120;

        [Tooltip(
            "How to validate the gateway's Ed25519 static public key.\n\n" +
            "Strict (default, recommended for production): the embedded key MUST equal " +
            "pinnedServerPublicKeyHex; if no pin is configured, the handshake is refused.\n\n" +
            "TrustOnFirstUse: on first connect to each host:port, the server's static key is " +
            "captured and persisted (PlayerPrefs).  Subsequent connects to the same endpoint " +
            "must present the same key or the handshake is refused.  Useful when an embedded " +
            "pin is impractical but the first-flight risk is acceptable.\n\n" +
            "InsecureNoPinning: accept any valid Ed25519 signature (vulnerable to substituted-" +
            "key MITM).  Logs a warning each session.  ONLY for local-loop testing or when an " +
            "outer transport (e.g. mTLS) authenticates the server independently.")]
        public ServerPinningMode serverPinningMode = ServerPinningMode.Strict;

        [Tooltip(
            "Hardens TrustOnFirstUse against the documented first-flight MITM gap.\n\n" +
            "TOFU's threat model accepts that the very first connect to a new endpoint " +
            "trusts whatever Ed25519 key the network delivers — a network-positioned " +
            "attacker on that flight can substitute their own key and have it persisted " +
            "as the durable pin.  When this setting is TRUE, the SDK refuses to capture " +
            "an unseen endpoint: the pin MUST already exist in the pin store " +
            "(pre-provisioned via the IServerKeyPinStore API at first launch from a " +
            "trusted side-channel — staged install, MDM push, signed bootstrap config, " +
            "etc.) or the handshake is rejected.  Default is FALSE to preserve the " +
            "compatibility contract for projects already relying on first-flight TOFU.")]
        public bool requireFirstUseProvisioned = false;

        // ── Derived ──────────────────────────────────────────────────────────────

        /// <summary>Tick interval in seconds (<c>1 / tickRate</c>).</summary>
        public float TickInterval => 1f / Mathf.Max(1, tickRate);

        /// <summary>
        /// Decode <see cref="apiKeyPskHex"/> to a 32-byte array, or return
        /// <see langword="null"/> if the field is empty (insecure dev path).
        /// Throws <see cref="System.ArgumentException"/> if the value is non-empty but invalid.
        /// </summary>
        public byte[] ApiKeyPskBytes =>
            string.IsNullOrEmpty(apiKeyPskHex) ? null : Crypto.ApiKeyCipher.PskFromHex(apiKeyPskHex);

        /// <summary>
        /// Resolve the pinning mode that the SDK should actually enforce,
        /// after applying the legacy <see cref="requirePinnedServerPublicKey"/>
        /// override.  Strict ALWAYS wins: an old project that set the bool to
        /// true must continue to refuse unpinned handshakes even if a
        /// freshly-created enum field deserialises to its default value.
        /// </summary>
        public ServerPinningMode EffectivePinningMode
        {
            get
            {
                if (requirePinnedServerPublicKey) return ServerPinningMode.Strict;
                return serverPinningMode;
            }
        }

        /// <summary>
        /// Decode <see cref="pinnedServerPublicKeyHex"/> to 32 bytes, or return
        /// <see langword="null"/> if pinning is not configured.
        /// Throws <see cref="System.ArgumentException"/> if the value is non-empty but invalid.
        /// </summary>
        public byte[] PinnedServerPublicKeyBytes
        {
            get
            {
                if (string.IsNullOrEmpty(pinnedServerPublicKeyHex)) return null;
                return Crypto.ApiKeyCipher.PskFromHex(pinnedServerPublicKeyHex);
            }
        }

        // ── JWT signature verification (JWKS pin) ──────────────────────────────
        //
        // When a pin is configured below, SessionAck JWTs whose signature does
        // not validate against the pinned key are rejected before any claim is
        // trusted. Without a pin the SDK validates structure + temporal claims
        // + iss/aud only and emits a one-time advisory warning so integrators
        // discover the gap before shipping. AEAD channel binding (RequiresEncryption)
        // is the second line of defence; signature verification closes the gap when
        // the channel keys themselves cannot be assumed trustworthy.

        /// <summary>
        /// Algorithm of the pinned JWS signing key. Must match the JWT header's
        /// <c>alg</c> claim or the token is rejected.
        /// </summary>
        public enum JwtSignatureAlgorithm
        {
            /// <summary>No signature verification (structure + temporal + iss/aud only).
            /// One-time warning logged at first SessionAck.</summary>
            None = 0,

            /// <summary>EdDSA over Ed25519 (RFC 8037). <c>alg=EdDSA</c>.
            /// <see cref="jwtSigningKeyHex"/> is a 64-character lowercase hex
            /// encoding of the 32-byte Ed25519 public key.</summary>
            Ed25519 = 1,

            /// <summary>RSA PKCS#1 v1.5 with SHA-256 (RFC 7518 §3.3).
            /// <c>alg=RS256</c>. <see cref="jwtSigningKeyPem"/> is a PEM-encoded
            /// SubjectPublicKeyInfo (the standard "BEGIN PUBLIC KEY" envelope)
            /// holding a ≥ 2048-bit RSA public key.</summary>
            RsaPkcs1Sha256 = 2,
        }

        [Header("PRODUCTION SECURITY — set this to a real algorithm before shipping")]
        [Tooltip(
            "JWS signing algorithm of the pinned key. The default value of None " +
            "is preserved for backwards compatibility with existing projects, but " +
            "production deployments MUST configure a real algorithm — leaving this " +
            "at None means the SessionAck JWT is accepted on structure + temporal + " +
            "iss/aud checks alone, which lets a hostile gateway install attacker-" +
            "chosen session_id / reconnect_token / crypto_id values into the client. " +
            "A LogError (not just a warning) is emitted at the first SessionAck so " +
            "the gap is visible in CI logs. Ed25519 (EdDSA) is recommended for new " +
            "deployments — 32-byte hex key, no PKI ceremony. RS256 is supported for " +
            "compatibility with existing IdP infrastructure.")]
        // Default value preserves backwards compatibility; production deployments
        // must set this to a real signature algorithm or accept the documented
        // risk.  The escalated LogError at first use makes the gap impossible to
        // miss in CI logs while keeping the field's serialised default unchanged.
        public JwtSignatureAlgorithm jwtSignatureAlgorithm = JwtSignatureAlgorithm.None;

        [Tooltip(
            "64-character lowercase hex string of the 32-byte Ed25519 public key " +
            "used to sign SessionAck JWTs. Used only when " +
            "jwtSignatureAlgorithm = Ed25519. Ignored otherwise.")]
        public string jwtSigningKeyHex = "";

        [Tooltip(
            "PEM-encoded RSA public key (`-----BEGIN PUBLIC KEY-----` envelope, " +
            "SubjectPublicKeyInfo) used to sign SessionAck JWTs with RS256. " +
            "Used only when jwtSignatureAlgorithm = RsaPkcs1Sha256. The decoded " +
            "modulus must be at least 2048 bits. Ignored otherwise.")]
        [TextArea(3, 12)]
        public string jwtSigningKeyPem = "";

        // ── Internal helpers ──────────────────────────────────────────────────────

        /// <summary>
        /// Create a runtime-only instance with factory-default values.
        /// Used by <see cref="NetworkManager"/> when no settings asset is assigned.
        /// Not saved to disk; garbage-collected when the manager is destroyed.
        /// </summary>
        internal static NetworkSettings CreateDefault()
        {
            var s = CreateInstance<NetworkSettings>();
            s.name = "RTMPESettings (runtime default)";
            return s;
        }

        /// <summary>
        /// Coerce any non-finite (NaN / Infinity) values configured on
        /// world-bounds Vector3 fields back to a sane default.  Reachable
        /// from the Inspector when an artist accidentally drags the value
        /// into a degenerate state, and from runtime callers that build a
        /// settings object via <see cref="ScriptableObject.CreateInstance{T}()"/>
        /// and assign Vector3.PositiveInfinity.  Without this guard the
        /// reconciliation bounds-check at <c>NetworkTransform.ApplyReconciliation</c>
        /// short-circuits to false on every comparison, silently disabling
        /// the bound entirely.
        /// </summary>
        /// <remarks>
        /// Called from <see cref="OnValidate"/> in the Editor and from
        /// <see cref="EnsureFiniteWorldBoundsForRuntime"/> by the runtime
        /// after asset load — both are idempotent and safe to invoke
        /// repeatedly.
        /// </remarks>
        internal void EnsureFiniteWorldBoundsForRuntime()
        {
            worldBoundsCenter  = ClampVector3Finite(worldBoundsCenter,  Vector3.zero);
            worldBoundsExtents = ClampVector3Finite(
                worldBoundsExtents,
                new Vector3(10_000f, 10_000f, 10_000f));
            // Extents must be non-negative; a negative half-extent reverses
            // the inside-out test and accepts every server position as
            // out-of-bounds.  Clamp to zero rather than abs() so an
            // accidentally-negative configuration surfaces as an obviously-
            // empty box rather than a silently-mirrored one.
            if (worldBoundsExtents.x < 0f) worldBoundsExtents.x = 0f;
            if (worldBoundsExtents.y < 0f) worldBoundsExtents.y = 0f;
            if (worldBoundsExtents.z < 0f) worldBoundsExtents.z = 0f;

            // Range attributes only fire from the Inspector — assets loaded
            // through Addressables / AssetBundle / direct deserialisation
            // bypass that path.  Mirror the Inspector floors at runtime so
            // a degenerate setting (zero or negative) cannot silently
            // disable the spawn-rate gate or the room-wide spawn cap.
            if (maxSpawnsPerSecond           < 1)     maxSpawnsPerSecond           = 1;
            if (maxSpawnsPerSecond           > 1000)  maxSpawnsPerSecond           = 1000;
            if (maxSpawnsPerRoom             < 100)   maxSpawnsPerRoom             = 100;
            if (maxSpawnsPerRoom             > 50000) maxSpawnsPerRoom             = 50000;
            if (maxNetworkVariableListSize   < 1)     maxNetworkVariableListSize   = 1;
            if (maxNetworkVariableListSize   > 65535) maxNetworkVariableListSize   = 65535;
        }

        private static Vector3 ClampVector3Finite(Vector3 candidate, Vector3 fallback)
        {
            if (!IsFiniteFloat(candidate.x)
             || !IsFiniteFloat(candidate.y)
             || !IsFiniteFloat(candidate.z))
                return fallback;
            return candidate;
        }

        private static bool IsFiniteFloat(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v);

#if UNITY_EDITOR
        private void OnValidate()
        {
            // Inspector-time hardening.  Range attributes already clamp the
            // numeric scalars; the world-bounds Vector3 fields have no
            // Range support, so the finiteness guard runs here.
            EnsureFiniteWorldBoundsForRuntime();

            // Pinning declared but no key supplied — every connection will fail
            // at runtime because the SDK has nowhere to compare the server's
            // key against.  Surface the conflict at edit time so it is
            // caught before a device build.
            if (requirePinnedServerPublicKey
                && string.IsNullOrWhiteSpace(pinnedServerPublicKeyHex))
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: requirePinnedServerPublicKey is true but " +
                    "pinnedServerPublicKeyHex is empty — every connection attempt " +
                    "will be rejected.  Supply the 64-char hex key or disable pinning.",
                    this);
            }

            // Ordering buffer below the architectural minimum: a single-slot
            // buffer cannot resolve a two-packet reorder and will stall delivery.
            if (enableGameplayOrdering && gameplayOrderingBufferSize < 2)
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: enableGameplayOrdering requires " +
                    $"gameplayOrderingBufferSize ≥ 2 (current: {gameplayOrderingBufferSize}).",
                    this);
            }

            // Variable batching requires at least one variable per batch;
            // zero or negative values would produce malformed wire packets.
            if (enableVariableBatching && maxVariablesPerBatch < 1)
            {
                UnityEngine.Debug.LogError(
                    $"[RTMPE] {name}: enableVariableBatching requires " +
                    $"maxVariablesPerBatch ≥ 1 (current: {maxVariablesPerBatch}).",
                    this);
            }
        }
#endif
    }
}
