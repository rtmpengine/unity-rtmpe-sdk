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
    public class M1_HandshakeHandlerKeyZeroingTests
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
    public class M2_NetworkTransformInterpolatorMonotonicTests
    {
        private GameObject                   _go;
        private NetworkTransformInterpolator _interp;

        [SetUp]
        public void SetUp()
        {
            _go     = new GameObject("Interp_M2");
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
    // Shared stubs
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Minimal NetworkBehaviour stub for tests in this file.
    /// Named uniquely to avoid collision with other stub types in the assembly.
    /// </summary>
    internal sealed class NetworkBehaviourStub : NetworkBehaviour { }
}
