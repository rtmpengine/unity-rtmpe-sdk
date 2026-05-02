// RTMPE SDK — Runtime/Infrastructure/Serialization/VerifiedFlatBuffer.cs
//
// Centralized hardening gate for every FlatBuffers root accessor on the
// receive path. The vendored Google.FlatBuffers runtime ships a structural
// `Verifier` (see ./FlatBuffers/FlatBufferVerify.cs) and the schema compiler
// emits a per-table `*Verify.Verify(Verifier, uint)` static helper alongside
// each generated table. Neither of those is invoked by `GetRootAsXxx` —
// those accessors blindly trust the bytes they are handed, which is unsafe
// when the bytes originate from a UDP socket, a peer relay, or any other
// untrusted source.
//
// This file plugs that gap with a single `TryGetRoot` entry point that:
//
//  1. Rejects empty / undersized buffers before touching the verifier so a
//     degenerate packet cannot cost more than a length check.
//  2. Runs the structural verifier with limits sized for our 16 KB MTU
//     (deeply-nested or table-bombed payloads are dropped, not unwound at
//     decode time via NRE / IndexOutOfRange).
//  3. Defers to the schema-generated table verifier so vtable offsets,
//     vector lengths, and required fields are all validated before any
//     consumer reads a single field.
//  4. Returns a bool plus an out-root, so call sites stay branch-friendly
//     and can drop the packet without unwinding through an exception.
//
// Design notes:
//
//  • The verifier is O(n) over the buffer. At our 16 KB MTU and the small
//    table count of our schema this is comfortably under the per-packet
//    budget on every supported platform; the cost is dominated by the
//    existing AEAD on the same packet.
//  • Failures are rate-limited because a hostile peer can otherwise pin
//    the main thread on `Debug.LogWarning` formatting alone — see the
//    same pattern in UdpTransport's drop logging.
//  • `max_depth=32` and `max_tables=16384` are chosen to comfortably
//    cover every legitimate schema in Generated/ (none nest beyond ~4
//    deep or carry more than a few hundred tables) while still bounding
//    adversarial blow-up.

using System;
using System.Text;
using System.Threading;
using Google.FlatBuffers;
using RTMPE.Messages;
using RTMPE.States;
// RTMPE.States.ValueType collides with System.ValueType; alias to the
// schema enum so unqualified references below resolve unambiguously.
using ValueType = RTMPE.States.ValueType;

namespace RTMPE.Infrastructure.Serialization
{
    /// <summary>
    /// Structural-validation gate for FlatBuffers payloads received from an
    /// untrusted source (network socket, relay, file). Call sites must use
    /// <see cref="TryGetRoot{TRoot}"/> instead of the generated
    /// <c>GetRootAsXxx</c> accessors directly.
    /// </summary>
    public static class VerifiedFlatBuffer
    {
        /// <summary>
        /// Maximum nested-table depth accepted by the verifier. The generated
        /// schemas in this SDK do not exceed depth 4; 32 leaves a comfortable
        /// margin while still rejecting pathological inputs.
        /// </summary>
        public const int MaxNestedDepth = 32;

        /// <summary>
        /// Maximum total tables accepted by the verifier inside a single
        /// 16 KB packet. With minimum table overhead of ~6 bytes a hostile
        /// payload cannot legitimately exceed this even at full MTU; the
        /// cap exists as defence in depth against quadratic walk patterns.
        /// </summary>
        public const int MaxTablesPerBuffer = 16384;

        /// <summary>
        /// Smallest legal FlatBuffer: a 4-byte root offset is the absolute
        /// minimum any well-formed buffer can occupy. Anything shorter is
        /// rejected without engaging the verifier.
        /// </summary>
        private const int MinimumBufferLength = 4;

        /// <summary>
        /// Maximum UTF-8 byte length allowed for the <c>OwnerId</c> string field
        /// in <see cref="SpawnPayload"/>. Player/session IDs longer than 128 bytes
        /// indicate a protocol violation or a hostile server.
        /// </summary>
        public const int MaxOwnerIdBytes = 128;

        // Rate limit warning spam so a hostile peer cannot pin the main
        // thread on UnityEngine.Debug.LogWarning string formatting alone.
        // Uses Environment.TickCount64 (milliseconds, monotonic) — faster than
        // DateTime.UtcNow on Windows and immune to NTP backward steps.
        private const long WarningCooldownMs = 1000L;
        private static long s_lastWarningMs;

        /// <summary>
        /// Delegate matching the static <c>Verify</c> method emitted by the
        /// FlatBuffers compiler on every generated table type. The compiler
        /// emits, for a table <c>Foo</c>, a class <c>FooVerify</c> with a
        /// static <c>Verify(Verifier, uint)</c> method that walks the table.
        /// </summary>
        public delegate bool TableVerifier(Verifier verifier, uint tablePos);

        /// <summary>
        /// Delegate matching the generated <c>GetRootAsXxx(ByteBuffer)</c>
        /// factory. Invoked only after the buffer has cleared verification.
        /// </summary>
        public delegate TRoot RootFactory<out TRoot>(ByteBuffer byteBuffer);

        /// <summary>
        /// Verify <paramref name="buffer"/> against the schema-generated
        /// <paramref name="tableVerifier"/> and, on success, materialise the
        /// root via <paramref name="rootFactory"/>. Returns <c>false</c> for
        /// any structural defect — the caller should drop the packet and
        /// continue; throwing on the receive path would let a single
        /// malformed datagram terminate the run loop.
        /// </summary>
        /// <typeparam name="TRoot">The generated root table struct type.</typeparam>
        /// <param name="buffer">Raw bytes received from the untrusted source.</param>
        /// <param name="tableVerifier">
        /// The schema-generated <c>{TableName}Verify.Verify</c> method.
        /// </param>
        /// <param name="rootFactory">
        /// The schema-generated <c>GetRootAs{TableName}(ByteBuffer)</c> method.
        /// </param>
        /// <param name="root">Materialised root on success; default on failure.</param>
        /// <param name="diagnosticTag">
        /// Short human-readable tag (typically the table name) included in
        /// rate-limited warning logs to aid triage. Optional.
        /// </param>
        /// <returns><c>true</c> if the buffer is structurally valid.</returns>
        public static bool TryGetRoot<TRoot>(
            byte[] buffer,
            TableVerifier tableVerifier,
            RootFactory<TRoot> rootFactory,
            out TRoot root,
            string diagnosticTag = null)
        {
            root = default;

            if (buffer == null || buffer.Length < MinimumBufferLength)
            {
                LogVerificationFailure(diagnosticTag, "buffer null or too small");
                return false;
            }

            if (tableVerifier == null)
            {
                throw new ArgumentNullException(nameof(tableVerifier));
            }

            if (rootFactory == null)
            {
                throw new ArgumentNullException(nameof(rootFactory));
            }

            // Boundary discriminant: the FlatBuffers schemas in
            // shared/contracts/flatbuffers/*.fbs are intentionally not stamped
            // with a 4-char file_identifier — adding one would be a wire-
            // breaking change for the already-deployed Rust gateway and Go
            // services that emit identifier-less buffers. In its place we
            // require that the (tableVerifier, TRoot) pair the caller passes
            // is one of the known good combinations. A mismatched pair (e.g.
            // verifying as InputPayload but materialising as StateSyncPayload)
            // would otherwise produce a "verified" but type-confused root and
            // surface garbage values to game-state code.
            if (!IsRegisteredRootBinding<TRoot>(tableVerifier))
            {
                LogVerificationFailure(diagnosticTag,
                    "table verifier does not match root type " + typeof(TRoot).Name);
                return false;
            }

            var byteBuffer = new ByteBuffer(buffer);

            // Catch any defensive throw from inside the verifier itself —
            // e.g. an arithmetic conversion overflow inside Convert.ToInt32
            // on hostile offsets. The verifier *should* return false for
            // every malformed shape, but we treat any escape as a hard fail.
            bool ok;
            try
            {
                var options = new Options(
                    maxDepth: MaxNestedDepth,
                    maxTables: MaxTablesPerBuffer,
                    stringEndCheck: true,
                    alignmentCheck: true);
                var verifier = new Verifier(byteBuffer, options);
                // Bridge our public delegate type onto the FlatBuffers
                // runtime delegate type. Method-group conversion would also
                // work, but going through a captured local makes the
                // adaptation explicit and avoids accidental binding to the
                // wrong overload if future runtime updates add one.
                VerifyTableAction adapter = (v, pos) => tableVerifier(v, pos);
                ok = verifier.VerifyBuffer(
                    identifier: null,
                    sizePrefixed: false,
                    verifyAction: adapter);
            }
            catch (Exception ex)
            {
                LogVerificationFailure(diagnosticTag, "verifier threw: " + ex.GetType().Name);
                return false;
            }

            if (!ok)
            {
                LogVerificationFailure(diagnosticTag, "structural verification failed");
                return false;
            }

            // Verifier consumed Position; rewind a fresh ByteBuffer so the
            // root accessor reads the prefix offset from byte 0 as expected.
            try
            {
                root = rootFactory(new ByteBuffer(buffer));
            }
            catch (Exception ex)
            {
                // A throw here would mean the verifier missed something —
                // log loud (still rate-limited) and drop the packet.
                LogVerificationFailure(diagnosticTag, "post-verify accessor threw: " + ex.GetType().Name);
                root = default;
                return false;
            }

            // Semantic post-verify pass: the structural verifier guarantees
            // wire integrity, but it does not enforce the SDK's payload-level
            // limits (cumulative vector size, finite floats, defined union
            // tags). Run those here so each receive site obtains a payload
            // that is safe for direct game-state consumption.
            try
            {
                ValidatePayloadSemantics(root);
                return true;
            }
            catch (Exception ex)
            {
                LogVerificationFailure(diagnosticTag, "semantic validation rejected: " + ex.GetType().Name);
                root = default;
                return false;
            }
        }

        /// <summary>
        /// Per-payload semantic checks layered on top of the structural
        /// verifier. The dispatch is centralised here so every receive
        /// callsite obtains the same guarantees regardless of which
        /// generated factory it invokes.
        /// </summary>
        private static void ValidatePayloadSemantics<TRoot>(TRoot root)
        {
            switch (root)
            {
                // ── Types requiring semantic validation beyond structural ──────

                case InputPayload input:
                    ValidateInputPayload(input);
                    break;
                case StateSyncPayload state:
                    ValidateStateSyncPayload(state);
                    break;
                case DeltaStateSyncPayload delta:
                    ValidateDeltaStateSyncPayload(delta);
                    break;
                case NetworkVariableUpdate update:
                    ValidateNetworkVariableUpdate(update);
                    break;
                case NetworkVariableUpdateV2 updateV2:
                    ValidateNetworkVariableUpdateV2(updateV2);
                    break;
                case SpawnPayload spawn:
                    ValidateSpawnPayload(spawn);
                    break;
                case RoomListResponse roomList:
                    ValidateRoomListResponse(roomList);
                    break;
                case JoinRoomResponse joinRoom:
                    ValidateJoinRoomResponse(joinRoom);
                    break;
                case NetworkTransformState transformState:
                    ValidateNetworkTransformState(transformState);
                    break;

                // ── Types for which structural verification is sufficient ──────
                //
                // Each entry below has been individually audited (2026-04-27):
                //
                // DespawnPayload, PlayerState:
                //   Contain only string identifiers and scalar fixed-size fields.
                //   FlatBuffers structural verifier bounds all string and field
                //   lengths within the 16 KB MTU; no float fields are present.
                //
                // HandshakePayload, HandshakeAckPayload, HandshakeInitPayload,
                // HandshakeResponsePayload, ChallengePayload, SessionAckPayload:
                //   Carry fixed-size cryptographic byte vectors (keys, signatures,
                //   nonces) and short string tokens. Crypto keys are bounded to
                //   32–64 bytes structurally; string tokens are short by protocol
                //   definition. No float fields; no unbounded vector amplification.
                //
                // HeartbeatPayload, HeartbeatAckPayload, DataAckPayload,
                // VariableUpdatePayload, LeaveRoomResponse, RoomListRequest:
                //   No vectors, no strings, no float fields. Fixed-width scalar
                //   types only; structural verification fully covers them.
                //
                // DataPayload:
                //   Single [ubyte] data vector bounded within the 16 KB MTU by
                //   the structural verifier. No secondary amplification path.
                //
                // CreateRoomRequest, CreateRoomResponse, JoinRoomRequest,
                // LeaveRoomRequest, DisconnectPayload, RoomClosedEvent,
                // RoomInfo, PlayerJoinedEvent, PlayerLeftEvent:
                //   String fields only (room IDs, player IDs, display names,
                //   reason strings). All are bounded by the structural verifier
                //   to fit within the 16 KB buffer; no vector-of-tables or float
                //   fields that require application-level caps.
            }
        }

        private static void ValidateInputPayload(InputPayload payload)
        {
            if (!SafeFlatBufferAccessors.IsFinite(payload.MoveX))
            {
                throw new InvalidOperationException("InputPayload.MoveX is not finite");
            }
            if (!SafeFlatBufferAccessors.IsFinite(payload.MoveY))
            {
                throw new InvalidOperationException("InputPayload.MoveY is not finite");
            }
        }

        private static void ValidateStateSyncPayload(StateSyncPayload payload)
        {
            // Reject before allocating the per-element managed objects in
            // UnPackTo — that loop is what makes a high vector-count packet
            // expensive (~4000 elements per 16 KB MTU). A single int sum
            // guards against a hostile producer pushing the consumer into
            // GC pressure on the hot receive path.
            int total = payload.PlayersLength + payload.TransformsLength + payload.VariablesLength;
            if (total > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "StateSyncPayload total vector elements exceeds cap: " + total);
            }

            // Each child variable update carries its own union discriminator
            // and data vector. Validate each up-front so an undefined tag in
            // an array element does not surface deep inside game-state code.
            for (int i = 0; i < payload.VariablesLength; i++)
            {
                var v = payload.Variables(i);
                if (v.HasValue)
                {
                    ValidateNetworkVariableUpdate(v.Value);
                }
            }
        }

        private static void ValidateNetworkVariableUpdate(NetworkVariableUpdate update)
        {
            SafeFlatBufferAccessors.RequireValid(update.ValueType);
            if (update.DataLength > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "NetworkVariableUpdate.Data length exceeds cap: " + update.DataLength);
            }
        }

        // The structural verifier already validates the (tag, table-offset)
        // pair atomically and runs the variant table's own structural check;
        // only variable-length variants (string / bytes) need an additional
        // semantic cap because their length is not bounded at the schema
        // level. Scalar variants are fixed-size and require no further
        // validation beyond what the verifier performs.
        private static void ValidateNetworkVariableUpdateV2(NetworkVariableUpdateV2 update)
        {
            switch (update.ValueType)
            {
                case NetworkVariableValue.NetworkVariableString:
                {
                    var variant = update.Value<NetworkVariableString>();
                    if (variant.HasValue)
                    {
                        // Probe the on-wire UTF-8 byte length WITHOUT materialising the
                        // string.  The schema accessor's `Value` property allocates a
                        // fresh string for every access, which converts a one-byte
                        // attacker advantage into a multi-KiB heap allocation per
                        // datagram on the receive boundary.  GetValueBytes returns the
                        // backing ArraySegment whose Count is the structurally-validated
                        // byte length.
                        var bytes = variant.Value.GetValueBytes();
                        int byteLen = bytes.HasValue ? bytes.Value.Count : 0;
                        if (byteLen > SafeFlatBufferAccessors.MaxTotalVectorElements)
                        {
                            throw new InvalidOperationException(
                                "NetworkVariableUpdateV2.String UTF-8 length exceeds cap: " + byteLen);
                        }
                    }
                    break;
                }
                case NetworkVariableValue.NetworkVariableBytes:
                {
                    var variant = update.Value<NetworkVariableBytes>();
                    if (variant.HasValue
                        && variant.Value.ValueLength > SafeFlatBufferAccessors.MaxTotalVectorElements)
                    {
                        throw new InvalidOperationException(
                            "NetworkVariableUpdateV2.Bytes length exceeds cap: "
                            + variant.Value.ValueLength);
                    }
                    break;
                }
            }
        }

        private static void ValidateDeltaStateSyncPayload(DeltaStateSyncPayload payload)
        {
            int total = payload.ChangedPlayersLength
                      + payload.ChangedTransformsLength
                      + payload.ChangedVariablesLength;
            if (total > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "DeltaStateSyncPayload total vector elements exceeds cap: " + total);
            }

            for (int i = 0; i < payload.ChangedVariablesLength; i++)
            {
                var v = payload.ChangedVariables(i);
                if (v.HasValue)
                    ValidateNetworkVariableUpdate(v.Value);
            }
        }

        private static void ValidateSpawnPayload(SpawnPayload payload)
        {
            var ownerId = payload.OwnerId;
            if (ownerId != null && Encoding.UTF8.GetByteCount(ownerId) > MaxOwnerIdBytes)
            {
                throw new InvalidOperationException(
                    "SpawnPayload.OwnerId exceeds length cap: " + ownerId.Length + " chars");
            }

            if (payload.InitialStateLength > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "SpawnPayload.InitialState length exceeds cap: " + payload.InitialStateLength);
            }
        }

        private static void ValidateRoomListResponse(RoomListResponse payload)
        {
            if (payload.RoomsLength > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "RoomListResponse.Rooms length exceeds cap: " + payload.RoomsLength);
            }
        }

        private static void ValidateJoinRoomResponse(JoinRoomResponse payload)
        {
            int total = payload.PlayerIdsLength + payload.DisplayNamesLength;
            if (total > SafeFlatBufferAccessors.MaxTotalVectorElements)
            {
                throw new InvalidOperationException(
                    "JoinRoomResponse total vector elements exceeds cap: " + total);
            }
        }

        // NetworkTransformState carries Vec3 Position / Velocity (3 × f32 each)
        // and Quaternion Rotation (4 × f32) — twelve attacker-controlled IEEE 754
        // values.  A single NaN or Infinity assigned to a Unity transform or a
        // Rigidbody silently destabilises PhysX (body disappears, joints detach)
        // and every plausibility-cap further down the pipeline short-circuits
        // through the NaN-comparison loophole (`> bound` returns false for NaN).
        // Reject at the wire boundary, BEFORE any consumer reads the field.
        private static void ValidateNetworkTransformState(NetworkTransformState state)
        {
            var pos = state.Position;
            if (pos.HasValue)
            {
                var v = pos.Value;
                if (!SafeFlatBufferAccessors.IsFinite(v.X)
                 || !SafeFlatBufferAccessors.IsFinite(v.Y)
                 || !SafeFlatBufferAccessors.IsFinite(v.Z))
                {
                    throw new InvalidOperationException(
                        "NetworkTransformState.Position is not finite");
                }
            }

            var rot = state.Rotation;
            if (rot.HasValue)
            {
                var q = rot.Value;
                if (!SafeFlatBufferAccessors.IsFinite(q.X)
                 || !SafeFlatBufferAccessors.IsFinite(q.Y)
                 || !SafeFlatBufferAccessors.IsFinite(q.Z)
                 || !SafeFlatBufferAccessors.IsFinite(q.W))
                {
                    throw new InvalidOperationException(
                        "NetworkTransformState.Rotation is not finite");
                }
            }

            var vel = state.Velocity;
            if (vel.HasValue)
            {
                var v = vel.Value;
                if (!SafeFlatBufferAccessors.IsFinite(v.X)
                 || !SafeFlatBufferAccessors.IsFinite(v.Y)
                 || !SafeFlatBufferAccessors.IsFinite(v.Z))
                {
                    throw new InvalidOperationException(
                        "NetworkTransformState.Velocity is not finite");
                }
            }
        }

        // Registry binding generated table types to the schema-emitted
        // verifier class names. The compiler emits, for a table `Foo`, a
        // sibling class `FooVerify` containing the static `Verify` method.
        // Comparing the supplied delegate's declaring-type name against the
        // expected entry rejects (verifier, TRoot) pair mismatches at the
        // boundary — a layered substitute for the missing file_identifier.
        // Add new RTMPE root tables here when they are introduced.
        private static bool IsRegisteredRootBinding<TRoot>(TableVerifier tableVerifier)
        {
            var method = tableVerifier.Method;
            if (method == null || method.DeclaringType == null)
            {
                return false;
            }
            string expectedVerifier = typeof(TRoot).Name + "Verify";
            if (method.DeclaringType.Name != expectedVerifier)
            {
                return false;
            }
            // Whitelist of root tables this SDK accepts at the receive
            // boundary. Anything else is rejected even if the name pattern
            // happens to match — defence against a future refactor that
            // exposes a non-root table to the wrapper by mistake.
            switch (typeof(TRoot).FullName)
            {
                case "RTMPE.States.InputPayload":
                case "RTMPE.States.StateSyncPayload":
                case "RTMPE.States.DeltaStateSyncPayload":
                case "RTMPE.States.NetworkVariableUpdate":
                case "RTMPE.States.NetworkVariableUpdateV2":
                case "RTMPE.States.SpawnPayload":
                case "RTMPE.States.DespawnPayload":
                case "RTMPE.States.PlayerState":
                case "RTMPE.States.NetworkTransformState":
                case "RTMPE.Packets.HandshakePayload":
                case "RTMPE.Packets.HandshakeAckPayload":
                case "RTMPE.Packets.HandshakeInitPayload":
                case "RTMPE.Packets.HandshakeResponsePayload":
                case "RTMPE.Packets.ChallengePayload":
                case "RTMPE.Packets.SessionAckPayload":
                case "RTMPE.Packets.HeartbeatPayload":
                case "RTMPE.Packets.HeartbeatAckPayload":
                case "RTMPE.Packets.DataPayload":
                case "RTMPE.Packets.DataAckPayload":
                case "RTMPE.Packets.DisconnectPayload":
                case "RTMPE.Packets.VariableUpdatePayload":
                case "RTMPE.Messages.CreateRoomRequest":
                case "RTMPE.Messages.CreateRoomResponse":
                case "RTMPE.Messages.JoinRoomRequest":
                case "RTMPE.Messages.JoinRoomResponse":
                case "RTMPE.Messages.LeaveRoomRequest":
                case "RTMPE.Messages.LeaveRoomResponse":
                case "RTMPE.Messages.RoomListRequest":
                case "RTMPE.Messages.RoomListResponse":
                case "RTMPE.Messages.RoomInfo":
                case "RTMPE.Messages.PlayerJoinedEvent":
                case "RTMPE.Messages.PlayerLeftEvent":
                case "RTMPE.Messages.RoomClosedEvent":
                    return true;
                default:
                    return false;
            }
        }

        private static void LogVerificationFailure(string tag, string reason)
        {
            // Atomic compare-exchange so concurrent callers do not all log.
            var now  = Environment.TickCount64;
            var last = Interlocked.Read(ref s_lastWarningMs);
            if (now - last < WarningCooldownMs)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref s_lastWarningMs, now, last) != last)
            {
                return;
            }

            var label = string.IsNullOrEmpty(tag) ? "<unknown>" : tag;
            UnityEngine.Debug.LogWarning(
                "[RTMPE] FlatBuffer verification rejected payload (" + label + "): " + reason);
        }
    }
}
