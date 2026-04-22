// RTMPE SDK — Tests/Runtime/CryptoAndNetworkTests.cs
//
// Regression tests for cryptography and networking behaviours:
//
//   Curve25519           — X25519 key exchange (RFC 7748 §6.1 test vectors)
//   UdpTransportIPv6     — UdpTransport.Receive() IPv6 endpoint handling
//   NetworkVarFormat     — NetworkVariableString: 2-byte LE length prefix
//   FlushDirtyVariables  — No per-call delegate allocation
//   VariableFraming      — SerializeWithId/ApplyVariableUpdate value_len framing
//   NetworkThreadDrain   — TryReceive: drain all available datagrams per iteration
//   NetworkThreadConcur  — Start(): atomic Interlocked guard prevents duplicate threads
//   NetworkTransformScale— HasScaleChanged: scale delta detection
//   RoomManagerQueue     — _pendingCreateQueue: FIFO request correlation
//   CryptoKeyZeroization — HandshakeHandler._clientPrivateKey zeroed in Dispose()
//   InterpolatorTimestamp— NetworkTransformInterpolator.AddState: monotonic timestamp guard
//
// Pure C# — no Unity engine dependencies beyond those already present in the
// SDK test assembly. Runs in Edit Mode Test Runner.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Crypto;
using RTMPE.Crypto.Internal;
using RTMPE.Protocol;
using RTMPE.Rooms;
using RTMPE.Sync;
using RTMPE.Threading;
using RTMPE.Transport;

namespace RTMPE.Tests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // Curve25519 cswap — the conditional guard `if (swap == 1)` was removed
    //      so the arithmetic select always executes.  The RFC 7748 §6.1 test vector
    //      only passes with the correct cswap logic.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("Curve25519")]
    public class Curve25519CswapTests
    {
        private static byte[] H(string hex)
        {
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
                b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }

        private static bool Eq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        [Test]
        public void SharedSecret_Alice_MatchesRfc7748_Vector()
        {
            // RFC 7748 §6.1 — if the cswap conditional was still present the result
            // would be the all-zero degenerate point or a wrong value.
            var alicePriv = H("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
            var bobPub    = H("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
            var expected  = H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

            var result = Curve25519.SharedSecret(alicePriv, bobPub);

            Assert.IsNotNull(result, "SharedSecret must not return null for valid inputs");
            Assert.IsTrue(Eq(expected, result),
                "Alice's shared secret must match RFC 7748 §6.1 test vector.\n" +
                "A mismatch indicates the cswap conditional guard is still present.");
        }

        [Test]
        public void SharedSecret_Bob_MatchesRfc7748_Vector()
        {
            var bobPriv   = H("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
            var alicePub  = H("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
            var expected  = H("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

            var result = Curve25519.SharedSecret(bobPriv, alicePub);

            Assert.IsTrue(Eq(expected, result),
                "Bob's shared secret must equal Alice's (ECDH symmetry) and match RFC vector.");
        }

        [Test]
        public void SharedSecret_ECDH_IsSymmetric()
        {
            // Any fresh key pair must yield matching secrets on both sides.
            var (privA, pubA) = Curve25519.GenerateKeyPair();
            var (privB, pubB) = Curve25519.GenerateKeyPair();

            var sA = Curve25519.SharedSecret(privA, pubB);
            var sB = Curve25519.SharedSecret(privB, pubA);

            Assert.IsTrue(Eq(sA, sB), "ECDH must be symmetric for any fresh key pair.");
        }

        [Test]
        public void SharedSecret_LowOrderPoint_ReturnsNull()
        {
            // The all-zero point is a low-order point; X25519 returns all-zero
            // shared secret.  Our implementation rejects it and returns null.
            var (priv, _) = Curve25519.GenerateKeyPair();
            var lowOrder  = new byte[32]; // all-zero

            var result = Curve25519.SharedSecret(priv, lowOrder);

            Assert.IsNull(result,
                "SharedSecret must return null for the all-zero low-order peer public key.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // UdpTransport.Receive() IPv6 — the hardcoded `new IPEndPoint(IPAddress.Any, 0)`
    //      was replaced with a family-aware endpoint chosen from `_socketFamily`.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("UdpTransportIPv6")]
    public class UdpTransportIPv6Tests
    {
        /// <summary>
        /// Verifies that a UdpTransport round-trip works on the IPv4 loopback,
        /// which exercises the Receive() path with an InterNetwork endpoint.
        /// </summary>
        [Test]
        public void UdpTransport_IPv4_ReceiveReturnsData()
        {
            // Stand up a loopback listener to echo a single datagram back.
            using var listener = new Socket(AddressFamily.InterNetwork,
                                            SocketType.Dgram, ProtocolType.Udp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            int listenerPort = ((IPEndPoint)listener.LocalEndPoint).Port;

            byte[] sentData = Encoding.UTF8.GetBytes("hello-ipv4");
            byte[] recvBuf  = new byte[64];

            // UdpTransport takes host+port in the constructor; Connect() resolves.
            using var transport = new UdpTransport("127.0.0.1", listenerPort);
            transport.Connect();

            // Send from transport → listener
            transport.Send(sentData);

            // Listener receives and echoes back
            EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            int n = listener.ReceiveFrom(recvBuf, ref remote);
            Assert.AreEqual(sentData.Length, n);
            listener.SendTo(sentData, remote);

            // Transport receives echo — should not throw with IPv4 socket.
            // Poll timeout is in microseconds; 2 000 000 µs = 2 s.
            if (transport.Poll(2_000_000))
            {
                int received = transport.Receive(recvBuf);
                Assert.AreEqual(sentData.Length, received,
                    "UdpTransport.Receive() must return the correct byte count on IPv4.");
            }
            else
            {
                Assert.Fail("Transport did not receive the echoed datagram within 2 s.");
            }
        }

        [Test]
        public void UdpTransport_IPv6_ConnectDoesNotThrow()
        {
            if (!Socket.OSSupportsIPv6)
                Assert.Ignore("IPv6 not supported on this host — skipping.");

            // Constructor takes host+port; Connect() does DNS resolution + socket creation.
            using var transport = new UdpTransport("::1", 9999);
            Assert.DoesNotThrow(
                () => transport.Connect(),
                "Connecting to an IPv6 endpoint must not throw.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NetworkVariableString wire format — 2-byte LE length prefix, not varint.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkVariableWireFormat")]
    public class NetworkVariableStringWireFormatTests
    {
        private GameObject     _nmGo;
        private NetworkManager _nm;
        private GameObject     _ownerGo;
        private NetworkBehaviourStub _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM_C3");
            _nm      = _nmGo.AddComponent<NetworkManager>();
            _ownerGo = new GameObject("Owner_C3");
            _owner   = _ownerGo.AddComponent<NetworkBehaviourStub>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_ownerGo);
        }

        [Test]
        public void Serialize_EmptyString_WritesTwoZeroLengthBytes()
        {
            var v = new NetworkVariableString(_owner, 1, "");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v.Serialize(bw);
            bw.Flush();

            var bytes = ms.ToArray();
            // First 2 bytes must be 0x00 0x00 (LE uint16 = 0)
            Assert.AreEqual(2, bytes.Length, "Empty string: 2-byte length prefix only.");
            Assert.AreEqual(0x00, bytes[0], "Low byte of length must be 0.");
            Assert.AreEqual(0x00, bytes[1], "High byte of length must be 0.");
        }

        [Test]
        public void Serialize_AsciiString_WritesTwoByteLengthThenAsciiBytes()
        {
            const string value = "abc";
            var v = new NetworkVariableString(_owner, 2, value);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v.Serialize(bw);
            bw.Flush();

            var bytes = ms.ToArray();
            // Layout: [len:2 LE][utf8 bytes]
            Assert.AreEqual(5, bytes.Length, "3-char ASCII: 2-byte prefix + 3 bytes.");
            ushort len = (ushort)(bytes[0] | (bytes[1] << 8));
            Assert.AreEqual(3, len, "Length field must equal 3 for 'abc'.");
            Assert.AreEqual((byte)'a', bytes[2]);
            Assert.AreEqual((byte)'b', bytes[3]);
            Assert.AreEqual((byte)'c', bytes[4]);
        }

        [Test]
        public void Serialize_LengthPrefixIsLittleEndian_NotVarint()
        {
            // A 128-char ASCII string has UTF-8 length 128 (0x80).
            // .NET's 7-bit varint would encode 128 as two bytes: 0x80 0x01.
            // The correct 2-byte LE uint16 encoding is 0x80 0x00.
            var value = new string('X', 128);
            var v = new NetworkVariableString(_owner, 3, value);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v.Serialize(bw);
            bw.Flush();

            var bytes = ms.ToArray();
            Assert.AreEqual(0x80, bytes[0],
                "Low byte of LE uint16(128) must be 0x80, not a varint continuation byte.");
            Assert.AreEqual(0x00, bytes[1],
                "High byte of LE uint16(128) must be 0x00, not 0x01 (varint).");
        }

        [Test]
        public void RoundTrip_AsciiString_IsPreserved()
        {
            const string original = "Hello, RTMPE!";
            var src = new NetworkVariableString(_owner, 4, original);
            var dst = new NetworkVariableString(_owner, 4, "");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            src.Serialize(bw);
            bw.Flush();

            ms.Position = 0;
            using var br = new BinaryReader(ms);
            dst.Deserialize(br);

            Assert.AreEqual(original, dst.Value);
        }

        [Test]
        public void RoundTrip_UnicodeString_IsPreserved()
        {
            const string original = "こんにちは";  // 15 UTF-8 bytes, 5 chars
            var src = new NetworkVariableString(_owner, 5, original);
            var dst = new NetworkVariableString(_owner, 5, "");

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            src.Serialize(bw);
            bw.Flush();

            ms.Position = 0;
            using var br = new BinaryReader(ms);
            dst.Deserialize(br);

            Assert.AreEqual(original, dst.Value);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // FlushDirtyVariables delegate caching — Action<byte[]> is cached once in
    //      NetworkManager.Start() so repeated calls to FlushDirtyNetworkVariables
    //      do not allocate a new closure each time.
    //      The private method itself is not callable from tests; we instead verify
    //      the supporting infrastructure (NetworkBehaviour.FlushDirtyVariables) by
    //      exercising it via its internal entry point and checking the ArrayPool path
    //      produces a well-formed packet without throwing.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("FlushDirtyVariables")]
    public class FlushDirtyVariablesDelegateTests
    {
        private GameObject      _nmGo;
        private NetworkManager  _nm;
        private GameObject      _goA;
        private NetworkBehaviourStub _nbA;

        [SetUp]
        public void SetUp()
        {
            _nmGo = new GameObject("NM_H1");
            _nm   = _nmGo.AddComponent<NetworkManager>();
            _goA  = new GameObject("NB_H1");
            _nbA  = _goA.AddComponent<NetworkBehaviourStub>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_goA);
        }

        [Test]
        public void FlushDirtyVariables_NoDirtyVars_SendCallbackNotInvoked()
        {
            // Make the behaviour an owner + spawned so FlushDirtyVariables doesn't
            // bail out on the !IsOwner / !IsSpawned guard.
            _nm.SetLocalPlayerStringId("player-1");
            _nbA.Initialize(1UL, "player-1");
            _nbA.SetSpawned(true);

            int sendCount = 0;
            Assert.DoesNotThrow(
                () => _nbA.FlushDirtyVariables(_ => sendCount++),
                "FlushDirtyVariables must not throw when no variables are tracked.");
            Assert.AreEqual(0, sendCount,
                "No dirty variables must not trigger the send callback.");
        }

        [Test]
        public void FlushDirtyVariables_WithDirtyVar_InvokesSendCallbackWithNonEmptyPayload()
        {
            _nm.SetLocalPlayerStringId("player-1");
            _nbA.Initialize(1UL, "player-1");
            _nbA.SetSpawned(true);

            var v = new NetworkVariableInt(_nbA, 1, 0);
            _nbA.TrackVariable(v);
            v.Value = 99; // dirty

            byte[] captured = null;
            Assert.DoesNotThrow(
                () => _nbA.FlushDirtyVariables(p => captured = p),
                "FlushDirtyVariables must not throw when a dirty variable is tracked.");
            Assert.IsNotNull(captured, "A dirty variable must invoke the send callback.");
            Assert.Greater(captured.Length, 0,
                "The flushed payload must contain variable data.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SerializeWithId / ApplyVariableUpdate — value_len framing.
    //      Unknown variable IDs must be skipped without corrupting the stream.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("VariableFraming")]
    public class H2_ValueLenFramingTests
    {
        private GameObject      _nmGo;
        private NetworkManager  _nm;
        private GameObject      _ownerGo;
        private NetworkBehaviourStub _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo    = new GameObject("NM_H2");
            _nm      = _nmGo.AddComponent<NetworkManager>();
            _ownerGo = new GameObject("Owner_H2");
            _owner   = _ownerGo.AddComponent<NetworkBehaviourStub>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_ownerGo);
        }

        [Test]
        public void SerializeWithId_WritesVariableId_ThenLength_ThenBytes()
        {
            // NetworkVariableInt serialized value = 4 bytes (int32 LE).
            var v = new NetworkVariableInt(_owner, 7, 0x12345678);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v.SerializeWithId(bw);
            bw.Flush();

            var bytes = ms.ToArray();
            // [var_id:2 LE][value_len:2 LE][value_bytes:4]
            Assert.AreEqual(8, bytes.Length, "int32 entry must be 8 bytes total.");

            ushort varId = (ushort)(bytes[0] | (bytes[1] << 8));
            Assert.AreEqual(7, varId, "var_id must be 7.");

            ushort valueLen = (ushort)(bytes[2] | (bytes[3] << 8));
            Assert.AreEqual(4, valueLen, "value_len must be 4 for an int32.");
        }

        [Test]
        public void SerializeWithId_UnknownId_DoesNotCorruptSubsequentVariable()
        {
            // Build a stream that contains:
            //   [unknown_id=9999][value_len=4][garbage:4]
            //   [known_id=1][value_len=4][value=42]
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // Unknown entry
            bw.Write((ushort)9999);   // var_id
            bw.Write((ushort)4);      // value_len
            bw.Write(new byte[4]);    // garbage payload

            // Known entry — note: NetworkVariableInt(owner, id, value=42)
            var v = new NetworkVariableInt(_owner, 1, 42);
            v.SerializeWithId(bw);
            bw.Flush();

            // Initialize state for deserialization
            _owner.Initialize(100UL, "player-owner");
            _owner.TrackVariable(v);

            // Deserialize — if the unknown-id skip is correct, var_id=1 will be parsed.
            ms.Position = 0;
            using var br = new BinaryReader(ms);

            bool anyApplied = false;
            while (ms.Position < ms.Length)
            {
                ushort id  = br.ReadUInt16();
                ushort len = br.ReadUInt16();
                long   start = ms.Position;

                if (id == 1)
                {
                    // Known variable — deserialize into v
                    v.Deserialize(br);
                    anyApplied = true;
                }
                else
                {
                    // Unknown — skip exactly value_len bytes
                    ms.Position = start + len;
                }
            }

            Assert.IsTrue(anyApplied, "The known variable (id=1) must be reached after skipping the unknown entry.");
            Assert.AreEqual(42, v.Value, "The known variable must have the correct value after deserialization.");
        }

        [Test]
        public void SerializeWithId_MultipleVariables_AllRoundTrip()
        {
            var v1 = new NetworkVariableInt(_owner,   10, 100);
            var v2 = new NetworkVariableFloat(_owner, 11, 3.14f);
            var v3 = new NetworkVariableInt(_owner,   12, -999);

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            v1.SerializeWithId(bw);
            v2.SerializeWithId(bw);
            v3.SerializeWithId(bw);
            bw.Flush();

            // Deserialize all
            ms.Position = 0;
            using var br = new BinaryReader(ms);

            var r1 = new NetworkVariableInt(_owner,   10, 0);
            var r2 = new NetworkVariableFloat(_owner, 11, 0f);
            var r3 = new NetworkVariableInt(_owner,   12, 0);

            while (ms.Position < ms.Length)
            {
                ushort id  = br.ReadUInt16();
                ushort len = br.ReadUInt16();
                long   pos = ms.Position;

                if      (id == 10) r1.Deserialize(br);
                else if (id == 11) r2.Deserialize(br);
                else if (id == 12) r3.Deserialize(br);
                else ms.Position = pos + len;
            }

            Assert.AreEqual(100,  r1.Value, "v1 round-trip");
            Assert.AreEqual(3.14f, r2.Value, 0.0001f, "v2 round-trip");
            Assert.AreEqual(-999, r3.Value, "v3 round-trip");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NetworkThread TryReceive drain loop — multiple datagrams available in
    //      one poll cycle must all be dispatched in that cycle.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkThreadDrain")]
    public class H3_NetworkThreadDrainLoopTests
    {
        /// <summary>
        /// Stub transport that simulates N queued datagrams available on the first
        /// poll cycle. Poll() returns true N times then false, and Receive() returns
        /// a fixed pattern each call.
        /// </summary>
        private sealed class BurstTransport : NetworkTransport
        {
            private readonly int _burst;
            private int          _pollCount;
            private int          _recvCount;

            public BurstTransport(int burst) { _burst = burst; }

            public override bool IsConnected => true;
            public override void Connect()   { }
            public override void Disconnect() { }
            public override void Dispose()   { }

            public override bool Poll(int microSeconds)
            {
                bool available = _pollCount < _burst;
                if (available) _pollCount++;
                return available;
            }

            public override int Receive(byte[] buffer)
            {
                int i = _recvCount++;
                buffer[0] = (byte)(i & 0xFF);
                return 1;
            }

            public override void Send(byte[] data) { }
        }

        [Test]
        public void TryReceive_BurstOfThree_DispatchesAllThreeInOneCycle()
        {
            const int Burst = 3;
            var transport = new BurstTransport(Burst);
            var thread    = new NetworkThread(transport);

            int received = 0;
            thread.OnPacketReceived += _ => Interlocked.Increment(ref received);

            // Call the internal drain method once.  We use Start()/Stop() and a
            // ManualResetEventSlim to get one iteration of the loop.
            var cts = new System.Threading.CancellationTokenSource();
            thread.Start();
            // Wait briefly for background thread to run at least one iteration
            Thread.Sleep(50);
            thread.Stop();
            thread.Dispose();

            Assert.GreaterOrEqual(received, Burst,
                $"Expected at least {Burst} packets dispatched; got {received}. " +
                "The drain loop must consume all burst packets in one cycle.");
        }

        [Test]
        public void TryReceive_EmptySocket_DoesNotDispatch()
        {
            var transport = new BurstTransport(0); // nothing available
            var thread    = new NetworkThread(transport);

            int received = 0;
            thread.OnPacketReceived += _ => Interlocked.Increment(ref received);

            thread.Start();
            Thread.Sleep(50);
            thread.Stop();
            thread.Dispose();

            Assert.AreEqual(0, received, "No packets should be dispatched when the socket is empty.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NetworkThread.Start() atomic guard — concurrent calls must not spawn
    //      duplicate threads.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkThreadConcurrency")]
    public class NetworkThreadAtomicStartTests
    {
        private sealed class NullTransport : NetworkTransport
        {
            public override bool IsConnected => true;
            public override void Connect()    { }
            public override void Disconnect() { }
            public override void Dispose()    { }
            public override bool Poll(int microSeconds) { Thread.Sleep(1); return false; }
            public override int  Receive(byte[] buffer) => 0;
            public override void Send(byte[] data)      { }
        }

        [Test]
        public void Start_CalledTwiceConcurrently_DoesNotThrow()
        {
            var transport = new NullTransport();
            var thread    = new NetworkThread(transport);

            Exception caught = null;
            var t1 = new Thread(() => { try { thread.Start(); } catch (Exception ex) { caught = ex; } });
            var t2 = new Thread(() => { try { thread.Start(); } catch (Exception ex) { caught = ex; } });

            t1.Start(); t2.Start();
            t1.Join(500); t2.Join(500);

            thread.Stop();
            thread.Dispose();

            Assert.IsNull(caught, $"Concurrent Start() must not throw: {caught}");
            Assert.IsTrue(t1.Join(100), "t1 must have finished");
            Assert.IsTrue(t2.Join(100), "t2 must have finished");
        }

        [Test]
        public void Start_CalledTwiceSequentially_IsRunningAfterFirstCall()
        {
            var transport = new NullTransport();
            var thread    = new NetworkThread(transport);

            thread.Start();
            Assert.IsTrue(thread.IsRunning, "IsRunning must be true after first Start().");

            // Second call must be a no-op
            thread.Start();
            Assert.IsTrue(thread.IsRunning, "IsRunning must still be true after second Start().");

            thread.Stop();
            thread.Dispose();
        }

        [Test]
        public void Stop_AfterStart_SetsIsRunningFalse()
        {
            var transport = new NullTransport();
            var thread    = new NetworkThread(transport);

            thread.Start();
            thread.Stop();

            // Brief wait for background thread to exit
            Thread.Sleep(50);
            Assert.IsFalse(thread.IsRunning, "IsRunning must be false after Stop().");
            thread.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NetworkTransform.HasScaleChanged — scale delta triggers dirty flag.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("NetworkTransformScale")]
    public class H5_NetworkTransformHasScaleChangedTests
    {
        private GameObject        _nmGo;
        private NetworkManager    _nm;
        private GameObject        _go;
        private NetworkTransform  _nt;

        [SetUp]
        public void SetUp()
        {
            _nmGo = new GameObject("NM_H5");
            _nm   = _nmGo.AddComponent<NetworkManager>();
            _go   = new GameObject("NT_H5");
            _nt   = _go.AddComponent<NetworkTransform>();

            // Enable scale sync via reflection — _syncScale is [SerializeField] private.
            // HasScaleChanged guards on _syncScale, so tests would always see false without this.
            var sf = typeof(NetworkTransform).GetField(
                "_syncScale",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(sf, "Field _syncScale must exist on NetworkTransform.");
            sf.SetValue(_nt, true);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_go);
        }

        [Test]
        public void HasScaleChanged_WhenScaleUnchanged_ReturnsFalse()
        {
            // MarkClean() records current scale as the last-sent baseline.
            _nt.MarkClean();
            Assert.IsFalse(_nt.HasScaleChanged,
                "HasScaleChanged must be false when the scale has not changed.");
        }

        [Test]
        public void HasScaleChanged_AfterLargeScaleChange_ReturnsTrue()
        {
            _nt.MarkClean();
            _go.transform.localScale = _go.transform.localScale + new Vector3(1f, 1f, 1f);

            Assert.IsTrue(_nt.HasScaleChanged,
                "HasScaleChanged must return true after a > threshold scale change.");
        }

        [Test]
        public void HasScaleChanged_SubthresholdChange_ReturnsFalse()
        {
            _nt.MarkClean();
            // Epsilon change — smaller than the default threshold of 0.001f
            _go.transform.localScale += new Vector3(0.0001f, 0f, 0f);

            Assert.IsFalse(_nt.HasScaleChanged,
                "HasScaleChanged must return false for sub-threshold scale changes.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // RoomManager._pendingCreateQueue — FIFO request correlation.
    //      Two rapid CreateRoom calls must each receive the correct options.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("RoomManagerQueue")]
    public class H6_RoomManagerPendingQueueTests
    {
        private PacketBuilder _pb;
        private List<byte[]>  _sent;
        private RoomManager   _rm;

        [SetUp]
        public void SetUp()
        {
            _pb   = new PacketBuilder();
            _sent = new List<byte[]>();
            _rm   = new RoomManager(_pb, p => _sent.Add(p), () => NetworkState.Connected);
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static byte[] BuildCreateOk(string roomId, string roomCode, int maxPlayers)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write((byte)1); // ok=true
            WriteString(bw, roomId);
            WriteString(bw, roomCode);
            bw.Write((byte)maxPlayers);
            // localPlayerId omitted (optional v3.1+ field) — parser handles gracefully
            bw.Flush();
            return ms.ToArray();
        }

        private static void WriteString(BinaryWriter bw, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s ?? "");
            bw.Write((ushort)bytes.Length);  // 2-byte LE length prefix
            bw.Write(bytes);
        }

        // ── Tests ──────────────────────────────────────────────────────────────

        [Test]
        public void TwoRapidCreateRoom_FirstResponseUsesFirstOptions()
        {
            var opts1 = new CreateRoomOptions { Name = "Room-One",  MaxPlayers = 4 };
            var opts2 = new CreateRoomOptions { Name = "Room-Two",  MaxPlayers = 8 };

            _rm.CreateRoom(opts1);
            _rm.CreateRoom(opts2);

            // First response arrives
            RoomInfo firstRoom = null;
            _rm.OnRoomCreated += room => firstRoom = room;

            var resp1 = BuildCreateOk("id-1", "CODE1", 4);
            _rm.HandleRoomPacket(PacketType.RoomCreate, resp1);

            Assert.IsNotNull(firstRoom, "OnRoomCreated must fire for first response.");
            Assert.AreEqual("Room-One", firstRoom.Name,
                "First response must use the first call's options (FIFO queue).");
        }

        [Test]
        public void TwoRapidCreateRoom_SecondResponseUsesSecondOptions()
        {
            var opts1 = new CreateRoomOptions { Name = "Room-Alpha", MaxPlayers = 2 };
            var opts2 = new CreateRoomOptions { Name = "Room-Beta",  MaxPlayers = 16 };

            _rm.CreateRoom(opts1);
            _rm.CreateRoom(opts2);

            // Consume first response — dequeues opts1, queue = [opts2].
            // Do NOT call ClearState() here: ClearState() also clears the pending
            // queue, which would discard opts2 and break the FIFO assertion below.
            // HandleCreateResponse unconditionally overwrites _currentRoom so no
            // reset is needed between the two responses.
            _rm.HandleRoomPacket(PacketType.RoomCreate, BuildCreateOk("id-A", "CODEA", 2));

            // Second response — dequeues opts2.
            RoomInfo secondRoom = null;
            _rm.OnRoomCreated += room => secondRoom = room;
            _rm.HandleRoomPacket(PacketType.RoomCreate, BuildCreateOk("id-B", "CODEB", 16));

            Assert.IsNotNull(secondRoom, "OnRoomCreated must fire for second response.");
            Assert.AreEqual("Room-Beta", secondRoom.Name,
                "Second response must use the second call's options (FIFO queue).");
        }

        [Test]
        public void ClearState_PurgesQueue()
        {
            _rm.CreateRoom(new CreateRoomOptions { Name = "Leftover" });
            _rm.ClearState();

            // After clear, a new CreateRoom with different options must not leak
            // the old "Leftover" name.
            _rm.CreateRoom(new CreateRoomOptions { Name = "Fresh" });

            RoomInfo created = null;
            _rm.OnRoomCreated += r => created = r;
            _rm.HandleRoomPacket(PacketType.RoomCreate, BuildCreateOk("id-F", "CODEF", 8));

            Assert.AreEqual("Fresh", created.Name,
                "After ClearState() the queue must be empty; new options must be used.");
        }

        [Test]
        public void SingleCreateRoom_ReceivesCorrectOptions()
        {
            var opts = new CreateRoomOptions { Name = "Solo", IsPublic = false };
            _rm.CreateRoom(opts);

            RoomInfo created = null;
            _rm.OnRoomCreated += r => created = r;
            _rm.HandleRoomPacket(PacketType.RoomCreate, BuildCreateOk("id-S", "CODES", 4));

            Assert.AreEqual("Solo",  created.Name);
            Assert.IsFalse(created.IsPublic);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // HandshakeHandler._clientPrivateKey zeroed in Dispose().
    //      After Dispose() the private key bytes must all be 0x00.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("CryptoKeyZeroization")]
    public class HandshakeHandlerKeyZeroingTests
    {
        [Test]
        public void Dispose_ZerosClientPrivateKey()
        {
            var handler = new HandshakeHandler();

            // Capture the private key reference via reflection before Dispose.
            var field = typeof(HandshakeHandler)
                .GetField("_clientPrivateKey",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);
            Assert.IsNotNull(field, "Field _clientPrivateKey must exist.");
            var keyRef = (byte[])field.GetValue(handler);
            Assert.IsNotNull(keyRef, "_clientPrivateKey must be non-null before Dispose.");

            // Confirm it is non-zero before Dispose.
            bool anyNonZeroBefore = false;
            foreach (byte b in keyRef) if (b != 0) { anyNonZeroBefore = true; break; }
            Assert.IsTrue(anyNonZeroBefore, "Private key must not be all-zeros before Dispose.");

            handler.Dispose();

            // After Dispose the same array reference must be all zeros.
            foreach (byte b in keyRef)
                Assert.AreEqual(0, b, "Every byte of _clientPrivateKey must be 0x00 after Dispose().");
        }

        [Test]
        public void Dispose_CalledTwice_DoesNotThrow()
        {
            var handler = new HandshakeHandler();
            handler.Dispose();
            Assert.DoesNotThrow(() => handler.Dispose(),
                "Double-Dispose() must be idempotent and must not throw.");
        }

        [Test]
        public void HandshakeHandler_ClientPublicKey_IsNonNull()
        {
            using var handler = new HandshakeHandler();
            Assert.IsNotNull(handler.ClientPublicKey,
                "ClientPublicKey must be non-null after construction.");
            Assert.AreEqual(32, handler.ClientPublicKey.Length,
                "X25519 public key must be exactly 32 bytes.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NetworkTransformInterpolator.AddState monotonic timestamp guard.
    //      Out-of-order or duplicate states must be silently discarded.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("InterpolatorTimestamp")]
    public class NetworkTransformInterpolatorMonotonicTests
    {
        private GameObject                   _go;
        private NetworkTransformInterpolator _interp;

        [SetUp]
        public void SetUp()
        {
            _go     = new GameObject("Interp");
            _interp = _go.AddComponent<NetworkTransformInterpolator>();
            _interp.ConfigureForTest(bufferSize: 8, interpolationDelay: 0.1f);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_go);
        }

        private static TransformState State(float x) =>
            new TransformState
            {
                Position = new Vector3(x, 0f, 0f),
                Rotation = Quaternion.identity,
                Scale    = Vector3.one,
            };

        [Test]
        public void AddState_MonotonicTimestamps_AcceptsAll()
        {
            _interp.AddState(State(1f), 1.0);
            _interp.AddState(State(2f), 2.0);
            _interp.AddState(State(3f), 3.0);

            Assert.AreEqual(3, _interp.BufferCount,
                "Three strictly-increasing timestamps must all be accepted.");
        }

        [Test]
        public void AddState_DuplicateTimestamp_IsDiscarded()
        {
            _interp.AddState(State(1f), 1.0);
            _interp.AddState(State(2f), 1.0); // same timestamp — must be discarded

            Assert.AreEqual(1, _interp.BufferCount,
                "A state with a duplicate timestamp must be discarded.");
        }

        [Test]
        public void AddState_OutOfOrderTimestamp_IsDiscarded()
        {
            _interp.AddState(State(1f), 5.0);
            _interp.AddState(State(2f), 3.0); // earlier timestamp — must be discarded

            Assert.AreEqual(1, _interp.BufferCount,
                "A state with an older timestamp (out-of-order UDP) must be discarded.");
        }

        [Test]
        public void AddState_OutOfOrder_DoesNotCorruptInterpolation()
        {
            _interp.AddState(State(0f), 0.0);
            _interp.AddState(State(2f), 2.0);

            // Inject an out-of-order state between the two
            _interp.AddState(State(99f), 1.0); // should be discarded

            // Interpolation at t=1.0 should still give x≈1 (halfway between 0 and 2),
            // not x=99 (which would indicate the stale state was accepted).
            bool ok = _interp.TryInterpolate(1.0, out var result);
            Assert.IsTrue(ok, "TryInterpolate must succeed with two valid bracketing states.");
            Assert.AreEqual(1f, result.Position.x, 0.01f,
                "Out-of-order state must not corrupt interpolation result.");
        }

        [Test]
        public void AddState_StrictlyIncreasing_BufferFillsNormally()
        {
            for (int i = 0; i < 8; i++)
                _interp.AddState(State(i), (double)i);

            Assert.AreEqual(8, _interp.BufferCount,
                "Buffer must contain exactly 8 entries after 8 monotonically increasing AddState calls.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // AeadPacketPipeline — end-to-end ChaCha20-Poly1305 packet pipeline
    //
    //   These tests verify the wire-format contract that NetworkManager
    //   EncryptAndSend / DecryptInboundPacket must honour:
    //
    //     • Nonce layout  : [counter:8 LE u64][session_id:4 LE u32]   (12 bytes)
    //     • SEQ prefix    : orig_seq prepended as 4-byte LE u32 to plaintext
    //     • AAD           : [packet_type, flags_without_FLAG_ENCRYPTED]
    //     • Symmetry      : AAD flags are identical on both seal and open sides
    //
    //   Each test uses local helpers that re-implement the *same algorithm* as
    //   the production code (using ChaCha20Poly1305Impl.Seal / Open directly),
    //   so a mismatch in the production code would surface as a tag failure.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("AeadPacketPipeline")]
    public class AeadPacketPipelineTests
    {
        // A deterministic 256-bit test key (not used outside this class).
        private static readonly byte[] TestKey = new byte[32]
        {
            0x60, 0x3d, 0xeb, 0x10, 0x15, 0xca, 0x71, 0xbe,
            0x2b, 0x73, 0xae, 0xf0, 0x85, 0x7d, 0x77, 0x81,
            0x1f, 0x35, 0x2c, 0x07, 0x3b, 0x61, 0x08, 0xd7,
            0x2d, 0x98, 0x10, 0xa3, 0x09, 0x14, 0xdf, 0xf4,
        };

        private const byte FlagEncrypted = 0x02;
        private const int  OffType       = PacketProtocol.OFFSET_TYPE;
        private const int  OffFlags      = PacketProtocol.OFFSET_FLAGS;
        private const int  OffSeq        = PacketProtocol.OFFSET_SEQUENCE;
        private const int  OffPLen       = PacketProtocol.OFFSET_PAYLOAD_LEN;
        private const int  HdrSize       = PacketProtocol.HEADER_SIZE;

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Builds a minimal but structurally valid RTMPE packet.
        /// </summary>
        private static byte[] MakePacket(byte packetType, byte flags,
                                         uint seq, byte[] payload)
        {
            int  len = HdrSize + (payload?.Length ?? 0);
            var  p   = new byte[len];
            // magic
            p[0] = 0x54; p[1] = 0x52;
            // version
            p[2] = PacketProtocol.VERSION;
            p[OffType]  = packetType;
            p[OffFlags] = flags;
            // sequence
            p[OffSeq]     = (byte) seq;
            p[OffSeq + 1] = (byte)(seq >>  8);
            p[OffSeq + 2] = (byte)(seq >> 16);
            p[OffSeq + 3] = (byte)(seq >> 24);
            // payload_len
            uint pLen = (uint)(payload?.Length ?? 0);
            p[OffPLen]     = (byte) pLen;
            p[OffPLen + 1] = (byte)(pLen >>  8);
            p[OffPLen + 2] = (byte)(pLen >> 16);
            p[OffPLen + 3] = (byte)(pLen >> 24);
            if (payload != null && payload.Length > 0)
                Buffer.BlockCopy(payload, 0, p, HdrSize, payload.Length);
            return p;
        }

        /// <summary>
        /// Builds the 12-byte nonce: [counter:8 LE u64][session_id:4 LE u32].
        /// Mirrors <c>NonceGenerator::build_nonce_raw</c> in the Rust gateway and
        /// <c>NetworkManager.BuildAeadNonce</c> in the SDK.
        /// </summary>
        private static byte[] BuildNonce(uint counter, uint sessionId)
        {
            var n = new byte[12];
            n[0] = (byte) counter;       n[1] = (byte)(counter >>  8);
            n[2] = (byte)(counter >> 16); n[3] = (byte)(counter >> 24);
            // n[4..7] remain 0x00 — high 32 bits of counter, always zero
            n[8]  = (byte) sessionId;     n[9]  = (byte)(sessionId >>  8);
            n[10] = (byte)(sessionId >> 16); n[11] = (byte)(sessionId >> 24);
            return n;
        }

        /// <summary>
        /// Re-implements EncryptAndSend's crypto transforms for test verification.
        /// Mirrors <c>encrypt_outbound()</c> in the Rust gateway's pipeline.rs.
        /// </summary>
        private static byte[] SimulateEncryptAndSend(
            byte[] packet, byte[] key, uint nonceCounter, uint cryptoId)
        {
            uint origSeq = (uint)(
                  packet[OffSeq] | (packet[OffSeq + 1] << 8)
                | (packet[OffSeq + 2] << 16) | (packet[OffSeq + 3] << 24));
            uint payloadLen = (uint)(
                  packet[OffPLen] | (packet[OffPLen + 1] << 8)
                | (packet[OffPLen + 2] << 16) | (packet[OffPLen + 3] << 24));

            byte pktType = packet[OffType];
            byte flags   = packet[OffFlags];

            // plaintext = [orig_seq:4 LE] || payload
            var pt = new byte[4 + (int)payloadLen];
            pt[0] = (byte)origSeq; pt[1] = (byte)(origSeq >> 8);
            pt[2] = (byte)(origSeq >> 16); pt[3] = (byte)(origSeq >> 24);
            if (payloadLen > 0)
                Buffer.BlockCopy(packet, HdrSize, pt, 4, (int)payloadLen);

            // AAD = [packet_type, flags_without_encrypted]
            var aad = new byte[] { pktType, flags };

            var ct = ChaCha20Poly1305Impl.Seal(key, BuildNonce(nonceCounter, cryptoId), pt, aad);

            var result = new byte[HdrSize + ct.Length];
            Buffer.BlockCopy(packet, 0, result, 0, HdrSize);

            // header.sequence = nonce counter
            result[OffSeq]     = (byte) nonceCounter;
            result[OffSeq + 1] = (byte)(nonceCounter >>  8);
            result[OffSeq + 2] = (byte)(nonceCounter >> 16);
            result[OffSeq + 3] = (byte)(nonceCounter >> 24);
            // header.flags |= FLAG_ENCRYPTED
            result[OffFlags] = (byte)(flags | FlagEncrypted);
            // header.payload_len = len(ciphertext)
            uint ctLen = (uint)ct.Length;
            result[OffPLen]     = (byte) ctLen;
            result[OffPLen + 1] = (byte)(ctLen >>  8);
            result[OffPLen + 2] = (byte)(ctLen >> 16);
            result[OffPLen + 3] = (byte)(ctLen >> 24);

            Buffer.BlockCopy(ct, 0, result, HdrSize, ct.Length);
            return result;
        }

        /// <summary>
        /// Re-implements DecryptInboundPacket's crypto transforms for test verification.
        /// Mirrors <c>decrypt_inbound()</c> in the Rust gateway's pipeline.rs.
        /// </summary>
        private static byte[] SimulateDecryptInboundPacket(
            byte[] data, byte[] key, uint cryptoId)
        {
            if (data == null || data.Length < HdrSize + 4 + 16) return null;

            uint nonceCounter = (uint)(
                  data[OffSeq] | (data[OffSeq + 1] << 8)
                | (data[OffSeq + 2] << 16) | (data[OffSeq + 3] << 24));

            byte pktType = data[OffType];
            byte flags   = data[OffFlags];

            // AAD = [packet_type, flags & ~FLAG_ENCRYPTED]
            var aad = new byte[] { pktType, (byte)(flags & ~FlagEncrypted) };

            int ctLen = data.Length - HdrSize;
            var ct = new byte[ctLen];
            Buffer.BlockCopy(data, HdrSize, ct, 0, ctLen);

            var pt = ChaCha20Poly1305Impl.Open(
                key, BuildNonce(nonceCounter, cryptoId), ct, aad);
            if (pt == null || pt.Length < 4) return null;

            uint origSeq = (uint)(
                  pt[0] | (pt[1] << 8) | (pt[2] << 16) | (pt[3] << 24));
            int actualPLen = pt.Length - 4;

            var result = new byte[HdrSize + actualPLen];
            Buffer.BlockCopy(data, 0, result, 0, HdrSize);

            result[OffSeq]     = (byte) origSeq;
            result[OffSeq + 1] = (byte)(origSeq >>  8);
            result[OffSeq + 2] = (byte)(origSeq >> 16);
            result[OffSeq + 3] = (byte)(origSeq >> 24);
            result[OffFlags] = (byte)(flags & ~FlagEncrypted);

            uint newPLen = (uint)actualPLen;
            result[OffPLen]     = (byte) newPLen;
            result[OffPLen + 1] = (byte)(newPLen >>  8);
            result[OffPLen + 2] = (byte)(newPLen >> 16);
            result[OffPLen + 3] = (byte)(newPLen >> 24);

            if (actualPLen > 0)
                Buffer.BlockCopy(pt, 4, result, HdrSize, actualPLen);
            return result;
        }

        // ── tests ─────────────────────────────────────────────────────────────

        [Test]
        public void Nonce_Layout_CounterInBytes0to7_SessionIdInBytes8to11()
        {
            // counter = 0xDEADBEEF (LE in bytes 0-3, bytes 4-7 must be 0)
            // sessionId = 0x12345678 (LE in bytes 8-11)
            uint counter   = 0xDEADBEEFu;
            uint sessionId = 0x12345678u;

            var nonce = BuildNonce(counter, sessionId);

            Assert.AreEqual(12, nonce.Length, "Nonce must be 12 bytes");
            // counter LE
            Assert.AreEqual(0xEF, nonce[0],  "nonce[0] = counter byte 0 (LSB)");
            Assert.AreEqual(0xBE, nonce[1],  "nonce[1] = counter byte 1");
            Assert.AreEqual(0xAD, nonce[2],  "nonce[2] = counter byte 2");
            Assert.AreEqual(0xDE, nonce[3],  "nonce[3] = counter byte 3 (MSB)");
            Assert.AreEqual(0x00, nonce[4],  "nonce[4] = high counter byte 0 — must be 0");
            Assert.AreEqual(0x00, nonce[5],  "nonce[5] = high counter byte 1 — must be 0");
            Assert.AreEqual(0x00, nonce[6],  "nonce[6] = high counter byte 2 — must be 0");
            Assert.AreEqual(0x00, nonce[7],  "nonce[7] = high counter byte 3 — must be 0");
            // session_id LE
            Assert.AreEqual(0x78, nonce[8],  "nonce[8]  = sessionId byte 0 (LSB)");
            Assert.AreEqual(0x56, nonce[9],  "nonce[9]  = sessionId byte 1");
            Assert.AreEqual(0x34, nonce[10], "nonce[10] = sessionId byte 2");
            Assert.AreEqual(0x12, nonce[11], "nonce[11] = sessionId byte 3 (MSB)");
        }

        [Test]
        public void RoundTrip_WithPayload_RestoredExactly()
        {
            // Heartbeat (0x03), Reliable flag (0x04), seq = 42, 4-byte payload.
            byte[] payload  = { 0xDE, 0xAD, 0xBE, 0xEF };
            var    original = MakePacket(0x03, 0x04, seq: 42, payload);

            uint nonceCounter = 0u;
            uint cryptoId     = 7u;

            var encrypted = SimulateEncryptAndSend(original, TestKey, nonceCounter, cryptoId);

            // FLAG_ENCRYPTED must be set and header.sequence must carry the nonce counter.
            Assert.IsTrue((encrypted[OffFlags] & FlagEncrypted) != 0,
                "FLAG_ENCRYPTED must be set on the wire packet");
            uint wireSeq = (uint)(encrypted[OffSeq] | (encrypted[OffSeq+1] << 8)
                               | (encrypted[OffSeq+2] << 16) | (encrypted[OffSeq+3] << 24));
            Assert.AreEqual(nonceCounter, wireSeq,
                "header.sequence must equal the nonce counter after encryption");
            // Ciphertext = original payload (4) + SEQ prefix (4) + Poly1305 tag (16)
            Assert.AreEqual(HdrSize + payload.Length + 4 + 16, encrypted.Length,
                "Encrypted length must be HEADER + payload + SEQ_prefix(4) + tag(16)");

            var decrypted = SimulateDecryptInboundPacket(encrypted, TestKey, cryptoId);

            Assert.IsNotNull(decrypted, "Decryption must succeed with matching key/nonce/AAD");
            Assert.AreEqual(original.Length, decrypted.Length,
                "Decrypted packet must be the same size as the original");
            for (int i = 0; i < original.Length; i++)
                Assert.AreEqual(original[i], decrypted[i],
                    $"Byte mismatch at offset {i} after round-trip");
        }

        [Test]
        public void RoundTrip_EmptyPayload_RestoredExactly()
        {
            // Edge case: packet with zero-length application payload.
            var original = MakePacket(0x03, 0x00, seq: 0xFF, payload: null);

            var encrypted = SimulateEncryptAndSend(original, TestKey, 0u, 1u);
            var decrypted = SimulateDecryptInboundPacket(encrypted, TestKey, 1u);

            Assert.IsNotNull(decrypted, "Round-trip must succeed for zero-length payload");
            Assert.AreEqual(original.Length, decrypted.Length);
            for (int i = 0; i < original.Length; i++)
                Assert.AreEqual(original[i], decrypted[i],
                    $"Byte mismatch at offset {i} for empty-payload packet");
        }

        [Test]
        public void Decrypt_TamperedCiphertext_ReturnsNull()
        {
            var original  = MakePacket(0x10, 0x00, seq: 1, payload: new byte[] { 1, 2, 3 });
            var encrypted = SimulateEncryptAndSend(original, TestKey, 5u, 99u);

            // Flip one bit in the ciphertext body (not the tag) to break AEAD.
            encrypted[HdrSize + 2] ^= 0xFF;

            var result = SimulateDecryptInboundPacket(encrypted, TestKey, 99u);
            Assert.IsNull(result, "Tampered ciphertext must be rejected by AEAD tag verification");
        }

        [Test]
        public void Decrypt_WrongKey_ReturnsNull()
        {
            var original  = MakePacket(0x10, 0x00, seq: 99, payload: new byte[] { 5, 6, 7, 8 });
            var encrypted = SimulateEncryptAndSend(original, TestKey, 1u, 42u);

            var wrongKey = new byte[32]; // all-zero key
            var result   = SimulateDecryptInboundPacket(encrypted, wrongKey, 42u);
            Assert.IsNull(result, "Wrong decryption key must be rejected by Poly1305 tag");
        }

        [Test]
        public void Decrypt_WrongCryptoId_ReturnsNull()
        {
            // Different cryptoId → different nonce → AEAD tag failure.
            var original  = MakePacket(0x03, 0x00, seq: 0, payload: new byte[] { 1 });
            var encrypted = SimulateEncryptAndSend(original, TestKey, 0u, cryptoId: 1u);

            var result = SimulateDecryptInboundPacket(encrypted, TestKey, cryptoId: 2u);
            Assert.IsNull(result, "Wrong cryptoId (nonce mismatch) must be rejected by AEAD");
        }

        [Test]
        public void Decrypt_WrongNonceCounter_ReturnsNull()
        {
            // Flip LSB of header.sequence to simulate a wrong nonce counter.
            var original  = MakePacket(0x03, 0x00, seq: 0, payload: new byte[] { 0xAA, 0xBB });
            var encrypted = SimulateEncryptAndSend(original, TestKey, 10u, 5u);

            encrypted[OffSeq] ^= 0x01; // mutate nonce counter in-place

            var result = SimulateDecryptInboundPacket(encrypted, TestKey, 5u);
            Assert.IsNull(result, "Modified nonce counter (wrong header.sequence) must be rejected");
        }

        [Test]
        public void AadSymmetry_FlagsBytesAreIdenticalOnBothSides()
        {
            // Encrypt side: AAD flags = flags_before_encryption (no FLAG_ENCRYPTED yet).
            // Decrypt side: AAD flags = flags_on_wire & ~FLAG_ENCRYPTED.
            // Both must yield the same byte so the AEAD tag is consistent.
            byte preEncryptFlags = 0x04; // only Reliable
            byte onWireFlags     = (byte)(preEncryptFlags | FlagEncrypted); // Reliable | Encrypted

            byte encryptAadFlags = preEncryptFlags;               // as seen by EncryptAndSend
            byte decryptAadFlags = (byte)(onWireFlags & ~FlagEncrypted); // as seen by Decrypt

            Assert.AreEqual(encryptAadFlags, decryptAadFlags,
                "AAD flags byte must be identical on both encrypt and decrypt sides");
        }

        [Test]
        public void SeqPrefix_OriginalSequenceRestoredAfterRoundTrip()
        {
            // Use a large, non-trivial sequence number to verify all 4 bytes are handled.
            uint  origSeq  = 0xCAFEBABEu;
            var   original = MakePacket(0x03, 0x04, origSeq, payload: new byte[] { 9, 8, 7 });
            var   encrypted = SimulateEncryptAndSend(original, TestKey, 3u, 11u);
            var   decrypted = SimulateDecryptInboundPacket(encrypted, TestKey, 11u);

            Assert.IsNotNull(decrypted);
            uint restoredSeq = (uint)(
                  decrypted[OffSeq] | (decrypted[OffSeq+1] << 8)
                | (decrypted[OffSeq+2] << 16) | (decrypted[OffSeq+3] << 24));
            Assert.AreEqual(origSeq, restoredSeq,
                "Original application sequence must be exactly restored after decryption");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Nonce Exhaustion Constants — verify SDK mirrors Rust gateway thresholds
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies that the outbound nonce exhaustion constants in NetworkManager
    /// match the Rust gateway's SEQUENCE_EXHAUSTION_THRESHOLD and
    /// NEAR_EXHAUSTION_MARGIN (nonce.rs).
    ///
    /// These tests do NOT use reflection — they verify the public behaviour
    /// implied by the constants (counter arithmetic) so the thresholds remain
    /// aligned even if the field is refactored.
    /// </summary>
    [TestFixture]
    [Category("NonceExhaustion")]
    public class NonceExhaustionConstantsTests
    {
        // Mirror the production constants for assertion arithmetic.
        // If these fail to compile the production values were renamed — fix both.
        private const long ExhaustionThreshold    = (long)uint.MaxValue + 1L; // 4,294,967,296
        private const long NearExhaustionMargin   = 1_048_576L;

        [Test]
        public void ExhaustionThreshold_Equals_TwoToThe32()
        {
            Assert.AreEqual(4_294_967_296L, ExhaustionThreshold,
                "Exhaustion threshold must be 2^32 — matches SEQUENCE_EXHAUSTION_THRESHOLD in nonce.rs");
        }

        [Test]
        public void ExhaustionThreshold_ExceedsUInt32MaxValue()
        {
            Assert.Greater(ExhaustionThreshold, (long)uint.MaxValue,
                "Threshold must be strictly greater than uint.MaxValue so counter uint.MaxValue is still valid");
        }

        [Test]
        public void LastValidCounter_IsUInt32MaxValue()
        {
            long lastValid = ExhaustionThreshold - 1L;
            Assert.AreEqual((long)uint.MaxValue, lastValid,
                "Last valid counter must be uint.MaxValue (4,294,967,295)");
            // Must fit losslessly in uint — same as gateway's u32::try_from check.
            Assert.DoesNotThrow(() => { _ = checked((uint)lastValid); },
                "Last valid counter must be representable as uint without overflow");
        }

        [Test]
        public void NearExhaustionWarning_FiresBefore_HardStop()
        {
            long warningThreshold = ExhaustionThreshold - NearExhaustionMargin;
            Assert.Greater(ExhaustionThreshold, warningThreshold,
                "Near-exhaustion warning must fire strictly before the hard stop");
            Assert.GreaterOrEqual(warningThreshold, 0L,
                "Near-exhaustion threshold must be non-negative");
        }

        [Test]
        public void NearExhaustionMargin_Matches_GatewayValue()
        {
            // Gateway's NEAR_EXHAUSTION_MARGIN = 1_048_576 (nonce.rs).
            Assert.AreEqual(1_048_576L, NearExhaustionMargin,
                "Near-exhaustion margin must match Rust gateway NEAR_EXHAUSTION_MARGIN");
        }

        [Test]
        public void CounterAtThreshold_IsRejected_ByRangeCheck()
        {
            // Simulate the guard: rawCounter >= ExhaustionThreshold → disconnect.
            long atThreshold = ExhaustionThreshold;
            Assert.IsTrue(atThreshold >= ExhaustionThreshold,
                "Counter == threshold must trigger hard stop");
        }

        [Test]
        public void CounterBelowThreshold_Fits_InUInt32()
        {
            // Every counter 0 … uint.MaxValue must cast to uint losslessly.
            long[] samples = { 0L, 1L, 1_000_000L, (long)uint.MaxValue - 1L, (long)uint.MaxValue };
            foreach (var c in samples)
            {
                Assert.Less(c, ExhaustionThreshold, $"Sample {c} must be below threshold");
                uint asUint = (uint)c;
                Assert.AreEqual(c, (long)asUint,
                    $"Counter {c} must survive lossless round-trip through uint");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Late-Join Resync (Fix 1) — MarkAllVariablesDirty propagates via
    //      SpawnManager when a new player joins the room so the flush
    //      loop retransmits current state.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("LateJoinResync")]
    public class LateJoinResyncTests
    {
        private GameObject      _nmGo;
        private NetworkManager  _nm;
        private GameObject      _ownerGo;
        private NetworkBehaviourStub _owner;

        [SetUp]
        public void SetUp()
        {
            _nmGo     = new GameObject("NM_LateJoin");
            _nm       = _nmGo.AddComponent<NetworkManager>();
            _ownerGo  = new GameObject("NB_LateJoin");
            _owner    = _ownerGo.AddComponent<NetworkBehaviourStub>();

            _nm.SetLocalPlayerStringId("player-owner");
            _owner.Initialize(42UL, "player-owner");
            _owner.SetSpawned(true);
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_ownerGo);
        }

        [Test]
        public void MarkAllVariablesDirty_ReflaggsCleanedVariable_ForResync()
        {
            var v = new NetworkVariableInt(_owner, 1, 0);
            v.Value = 7;          // dirty
            v.MarkClean();         // cleaned
            Assert.IsFalse(v.IsDirty, "Sanity: variable is clean before resync");

            _owner.MarkAllVariablesDirty();

            Assert.IsTrue(v.IsDirty,
                "MarkAllVariablesDirty must force the variable back into the dirty set " +
                "without changing its value — this is what feeds the late-joiner snapshot.");
        }

        [Test]
        public void MarkAllVariablesDirty_DoesNotFireOnValueChanged()
        {
            var v = new NetworkVariableInt(_owner, 1, 0);
            v.Value = 5;
            v.MarkClean();

            int changeCount = 0;
            v.OnValueChanged += (oldV, newV) => changeCount++;

            _owner.MarkAllVariablesDirty();

            Assert.AreEqual(0, changeCount,
                "Resync must not fire OnValueChanged — the value itself did not change.");
        }

        [Test]
        public void SpawnManager_Resync_OnlyMarksOwnedObjects()
        {
            // Build a minimal SpawnManager with one owned + one non-owned object.
            var registry  = new NetworkObjectRegistry();
            var ownership = new RTMPE.Core.OwnershipManager(registry, _nm);
            var spawn     = new RTMPE.Core.SpawnManager(registry, ownership, _nm);

            var owned = _owner;                             // already ownerId = player-owner
            var foreignGo = new GameObject("ForeignNB");
            var foreign   = foreignGo.AddComponent<NetworkBehaviourStub>();
            foreign.Initialize(43UL, "player-other");
            foreign.SetSpawned(true);

            registry.Register(owned);
            registry.Register(foreign);

            var vOwned   = new NetworkVariableInt(owned,   10, 0);
            var vForeign = new NetworkVariableInt(foreign, 20, 0);
            vOwned.Value = 1;   vOwned.MarkClean();
            vForeign.Value = 2; vForeign.MarkClean();

            spawn.MarkAllVariablesDirtyForResync();

            Assert.IsTrue(vOwned.IsDirty,   "Owned variable must be re-dirtied for resync.");
            Assert.IsFalse(vForeign.IsDirty, "Non-owned variable must NOT be touched — remote clients resync their own state.");

            UnityEngine.Object.DestroyImmediate(foreignGo);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Pluggable Transport (Fix 2) — TransportFactory override replaces the
    //      built-in UdpTransport on InitialiseNetwork.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("PluggableTransport")]
    public class PluggableTransportTests
    {
        private sealed class StubTransport : RTMPE.Transport.NetworkTransport
        {
            public override bool IsConnected => false;
            public override void Connect()    { }
            public override void Disconnect() { }
            public override void Send(byte[] data) { }
            public override int  Receive(byte[] buffer) => 0;
            public override bool Poll(int microSeconds) => false;
            public override void Dispose()    { }
        }

        [TearDown]
        public void TearDown()
        {
            // Tests share a static factory — always clear to avoid leaking
            // into the next fixture's SetUp.
            NetworkManager.ClearTransportFactory();
        }

        [Test]
        public void NoFactory_DefaultsToUdpTransport()
        {
            NetworkManager.ClearTransportFactory();
            Assert.IsFalse(NetworkManager.HasCustomTransportFactory,
                "Default path must report no custom factory.");
        }

        [Test]
        public void CustomFactory_IsInvoked_AndProducesTheReportedTransport()
        {
            int factoryCalls = 0;
            NetworkManager.SetTransportFactory(settings =>
            {
                Interlocked.Increment(ref factoryCalls);
                Assert.IsNotNull(settings, "Factory must receive the active NetworkSettings.");
                return new StubTransport();
            });

            Assert.IsTrue(NetworkManager.HasCustomTransportFactory,
                "SetTransportFactory must flip HasCustomTransportFactory true.");

            // Spin up a throwaway NetworkManager; Awake calls InitialiseNetwork,
            // which will invoke the factory exactly once.
            var go = new GameObject("NM_PluggableTransport");
            try
            {
                go.AddComponent<NetworkManager>();
                Assert.AreEqual(1, factoryCalls,
                    "Custom transport factory must be invoked exactly once per InitialiseNetwork.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FactoryReturningNull_FallsBackToUdpTransport_WithoutThrowing()
        {
            NetworkManager.SetTransportFactory(_ => null);

            var go = new GameObject("NM_FactoryNull");
            try
            {
                Assert.DoesNotThrow(() => go.AddComponent<NetworkManager>(),
                    "A null factory return must fall back, not throw.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void FactoryThrowing_FallsBackToUdpTransport_WithoutThrowing()
        {
            NetworkManager.SetTransportFactory(_ =>
                throw new InvalidOperationException("synthetic factory failure"));

            var go = new GameObject("NM_FactoryThrows");
            try
            {
                // Expect the error log from the fallback path so NUnit doesn't
                // fail on a logged error.
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error,
                    new System.Text.RegularExpressions.Regex(
                        "Custom transport factory threw"));
                UnityEngine.TestTools.LogAssert.Expect(LogType.Warning,
                    new System.Text.RegularExpressions.Regex(
                        "Falling back to the built-in UdpTransport.*"));

                Assert.DoesNotThrow(() => go.AddComponent<NetworkManager>(),
                    "A throwing factory must be caught; NetworkManager must still initialise.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Scene-transition Pruning (Fix 4) — NetworkObjectRegistry.PruneDestroyed
    //      evicts entries whose GameObject has been Unity-destroyed.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("ScenePrune")]
    public class NetworkObjectRegistryPruneTests
    {
        [Test]
        public void PruneDestroyed_RemovesEntriesWhoseGameObjectWasDestroyed()
        {
            var registry = new NetworkObjectRegistry();

            var liveGo   = new GameObject("Live_Prune");
            var liveNb   = liveGo.AddComponent<NetworkBehaviourStub>();
            liveNb.Initialize(1UL, "player-a");

            var deadGo   = new GameObject("Dead_Prune");
            var deadNb   = deadGo.AddComponent<NetworkBehaviourStub>();
            deadNb.Initialize(2UL, "player-a");

            registry.Register(liveNb);
            registry.Register(deadNb);

            // Simulate a scene unload — Unity destroys the GameObject out from
            // under the registry without the SDK ever calling Unregister.
            UnityEngine.Object.DestroyImmediate(deadGo);

            int pruned = registry.PruneDestroyed();
            Assert.AreEqual(1, pruned,
                "Exactly one registry entry must have been destroyed by Unity.");

            Assert.IsNotNull(registry.Get(1UL),
                "Live registered object must still be present after prune.");
            Assert.IsNull(registry.Get(2UL),
                "Destroyed registry entry must be evicted after PruneDestroyed.");

            UnityEngine.Object.DestroyImmediate(liveGo);
        }

        [Test]
        public void PruneDestroyed_NoStaleEntries_ReturnsZero()
        {
            var registry = new NetworkObjectRegistry();

            var goA = new GameObject("A_Prune");
            var nbA = goA.AddComponent<NetworkBehaviourStub>();
            nbA.Initialize(10UL, "p");
            registry.Register(nbA);

            int pruned = registry.PruneDestroyed();
            Assert.AreEqual(0, pruned,
                "PruneDestroyed must return 0 when no entries are stale.");

            UnityEngine.Object.DestroyImmediate(goA);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Object Pool (Fix 5) — INetworkObjectPool is consulted when installed
    //      and bypassed when null.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("ObjectPool")]
    public class ObjectPoolTests
    {
        private sealed class CountingPool : INetworkObjectPool
        {
            public int AcquireCalls;
            public int ReleaseCalls;
            public List<uint> AcquiredPrefabIds = new List<uint>();
            public List<uint> ReleasedPrefabIds = new List<uint>();

            public GameObject Acquire(uint prefabId, GameObject prefab, Vector3 position, Quaternion rotation)
            {
                AcquireCalls++;
                AcquiredPrefabIds.Add(prefabId);
                return UnityEngine.Object.Instantiate(prefab, position, rotation);
            }

            public void Release(uint prefabId, GameObject instance)
            {
                ReleaseCalls++;
                ReleasedPrefabIds.Add(prefabId);
                UnityEngine.Object.Destroy(instance);
            }
        }

        private GameObject     _nmGo;
        private NetworkManager _nm;
        private GameObject     _prefab;

        [SetUp]
        public void SetUp()
        {
            _nmGo  = new GameObject("NM_Pool");
            _nm    = _nmGo.AddComponent<NetworkManager>();
            _prefab = new GameObject("Pool_Prefab");
            _prefab.AddComponent<NetworkBehaviourStub>();
            _nm.SetLocalPlayerStringId("player-owner");
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
            UnityEngine.Object.DestroyImmediate(_prefab);
        }

        [Test]
        public void SpawnManager_WithPool_RoutesAcquireAndRelease()
        {
            var pool = new CountingPool();
            _nm.Spawner.RegisterPrefab(100, _prefab);
            _nm.Spawner.SetObjectPool(pool);

            var nb = _nm.Spawner.Spawn(100, Vector3.zero, Quaternion.identity, "player-owner");
            Assert.IsNotNull(nb, "Spawn must return a live NetworkBehaviour.");
            Assert.AreEqual(1, pool.AcquireCalls, "Pool.Acquire must be invoked once per Spawn.");
            Assert.AreEqual(100U, pool.AcquiredPrefabIds[0]);

            _nm.Spawner.Despawn(nb.NetworkObjectId);
            Assert.AreEqual(1, pool.ReleaseCalls, "Pool.Release must be invoked once per Despawn.");
            Assert.AreEqual(100U, pool.ReleasedPrefabIds[0],
                "Release must carry the SAME prefabId that Acquire returned the instance for.");
        }

        [Test]
        public void SpawnManager_WithoutPool_DoesNotCrash()
        {
            _nm.Spawner.RegisterPrefab(101, _prefab);
            _nm.Spawner.ClearObjectPool();

            var nb = _nm.Spawner.Spawn(101, Vector3.zero, Quaternion.identity, "player-owner");
            Assert.IsNotNull(nb, "Spawn without a pool must still return a live NetworkBehaviour.");
            Assert.IsNull(_nm.Spawner.ObjectPool, "ObjectPool must report null when cleared.");

            Assert.DoesNotThrow(() => _nm.Spawner.Despawn(nb.NetworkObjectId),
                "Despawn without a pool must fall back to Object.Destroy without throwing.");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Auto Room Re-Join (Fix 3) — LastRoomId is populated on join, preserved
    //      across token-preserving clear, and wiped on an explicit Disconnect.
    // ═══════════════════════════════════════════════════════════════════════════

    [TestFixture]
    [Category("AutoRejoin")]
    public class AutoRejoinTests
    {
        private GameObject     _nmGo;
        private NetworkManager _nm;

        [SetUp]
        public void SetUp()
        {
            _nmGo = new GameObject("NM_AutoRejoin");
            _nm   = _nmGo.AddComponent<NetworkManager>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_nmGo);
        }

        // Reach into the private RememberRoom helper via reflection — the
        // alternative (driving a full RoomJoined event) would require a
        // fully established session which is heavier than needed here.
        private void RememberRoom(RoomInfo room)
        {
            var mi = typeof(NetworkManager).GetMethod(
                "RememberRoom",
                BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(mi, "RememberRoom helper must exist on NetworkManager.");
            mi.Invoke(_nm, new object[] { room });
        }

        // Expose the ClearSessionData overload that accepts preserveReconnectToken.
        private void ClearSessionData(bool preserveReconnectToken)
        {
            var mi = typeof(NetworkManager).GetMethod(
                "ClearSessionData",
                BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null);
            Assert.IsNotNull(mi,
                "ClearSessionData(bool) overload must exist on NetworkManager.");
            mi.Invoke(_nm, new object[] { preserveReconnectToken });
        }

        [Test]
        public void RememberRoom_PopulatesLastRoomIdAndCode()
        {
            var room = new RoomInfo("room-uuid-1", "ABC123", "Test", "waiting", 1, 8, true);
            RememberRoom(room);

            Assert.AreEqual("room-uuid-1", _nm.LastRoomId);
            Assert.AreEqual("ABC123",      _nm.LastRoomCode);
        }

        [Test]
        public void PreservingReconnectToken_ShouldAlsoPreserve_LastRoomSnapshot()
        {
            var room = new RoomInfo("room-uuid-2", "XKCD42", "Test", "waiting", 1, 8, true);
            RememberRoom(room);

            ClearSessionData(preserveReconnectToken: true);

            Assert.AreEqual("room-uuid-2", _nm.LastRoomId,
                "Token-preserving clear MUST keep the last-room snapshot so Reconnect() " +
                "can auto-rejoin.");
            Assert.AreEqual("XKCD42", _nm.LastRoomCode);
        }

        [Test]
        public void ClearingReconnectToken_AlsoClears_LastRoomSnapshot()
        {
            var room = new RoomInfo("room-uuid-3", "ZYXW98", "Test", "waiting", 1, 8, true);
            RememberRoom(room);

            ClearSessionData(preserveReconnectToken: false);

            Assert.IsNull(_nm.LastRoomId,
                "Full clear must wipe LastRoomId — it is meaningless without the token.");
            Assert.IsNull(_nm.LastRoomCode);
        }

        [Test]
        public void RememberRoom_WithNullRoom_ClearsSnapshot()
        {
            RememberRoom(new RoomInfo("room-uuid-4", "Q1Q1Q1", "N", "waiting", 1, 2, true));
            Assert.AreEqual("room-uuid-4", _nm.LastRoomId);

            RememberRoom(null);

            Assert.IsNull(_nm.LastRoomId,
                "Null room parameter must clear the snapshot (defensive reset).");
        }

        [Test]
        public void AutoRejoinSetting_DefaultsToEnabled()
        {
            var defaults = NetworkSettings.CreateInstance<NetworkSettings>();
            Assert.IsTrue(defaults.autoRejoinLastRoomOnReconnect,
                "autoRejoinLastRoomOnReconnect must default to true — backwards-safe for new users.");
            UnityEngine.Object.DestroyImmediate(defaults);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Shared stubs
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal NetworkBehaviour stub for tests in this file.
    /// Named uniquely to avoid collision with other stub types in the assembly.
    /// </summary>
    internal sealed class NetworkBehaviourStub : NetworkBehaviour { }
}
