// RTMPE SDK — Runtime/Crypto/EncryptedFilePinStore.cs
//
// Defense-in-depth IServerKeyPinStore implementation that persists
// pinned server keys to a binary file under
// UnityEngine.Application.persistentDataPath (an app-private directory
// on every supported platform) with per-record HMAC-SHA256 integrity
// protection.
//
// THREAT-MODEL COMPARISON vs PlayerPrefsPinStore
// ---------------------------------------------
// PlayerPrefsPinStore writes to the platform's preferences store
// (Android SharedPreferences, iOS user defaults, Windows registry).
// On Android the prefs file is readable+writable to anyone with adb
// shell access OR an attacker holding the cloud-backup blob — the
// stored hex pin can be swapped silently for an attacker's key,
// enabling MITM on the next handshake.
//
// EncryptedFilePinStore raises the bar by:
//   1. Storing pins in Application.persistentDataPath (not exposed via
//      the standard adb-backup path on modern Android, and never via
//      cloud sync on iOS) so casual `adb pull` / restore-from-backup
//      cannot read or rewrite the pin file.
//   2. Binding each pin to an HMAC-SHA256 tag derived from
//      SystemInfo.deviceUniqueIdentifier — pins lifted from one
//      device's file and dropped onto another fail integrity check
//      and read as "no pin", forcing fresh TOFU on the second device
//      instead of trusting the lifted value.
//   3. Detecting per-record bitflips, swap-with-attacker-key, and
//      add-a-forged-record at Load time; any tampered or unverified
//      record degrades to "no pin" rather than yielding a value that
//      would silently authenticate an attacker's gateway.
//
// LIMITATIONS (documented honestly)
//   - On a rooted Android device with read access to both the file
//     AND the binary, an attacker can re-derive the MAC key from
//     SystemInfo.deviceUniqueIdentifier and forge valid records.
//     Hardware-backed key storage (Android Keystore / iOS Keychain)
//     is the proper mitigation against that threat; this class is the
//     pure-managed step on the way there.
//   - SystemInfo.deviceUniqueIdentifier may be empty or unstable on
//     some platforms (WebGL, headless Linux); the derivation falls
//     through to the HKDF default-salt path so the file is still
//     readable, but cross-device transplant detection is lost.
//
// FILE FORMAT (binary, little-endian)
//   header:
//     4 bytes magic   = 0x49 0x50 0x54 0x52  ("IPTR" LE → "RTPI")
//     1 byte  version = 0x01
//     4 bytes record_count (LE u32)
//   record:
//     2 bytes endpoint_len (LE u16, max 1024)
//     N bytes endpoint     (UTF-8, N = endpoint_len)
//    32 bytes pin
//    32 bytes hmac        = HMAC-SHA256(macKey, hmacInput)
//   hmacInput:
//     [version][endpoint_len_LE_2B][endpoint][pin]
//   (version is included so a format upgrade that re-uses the same
//   record layout cannot be silently downgraded; endpoint_len is
//   prefixed so a splice attack that shifts endpoint bytes into the
//   pin region is detected.)

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using RTMPE.Crypto.Internal;
using UnityEngine;

namespace RTMPE.Crypto
{
    /// <summary>
    /// Hardened <see cref="IServerKeyPinStore"/> implementation that persists
    /// pins to <see cref="Application.persistentDataPath"/> with per-record
    /// HMAC-SHA256 integrity protection.  Suitable for deployments that need
    /// stronger tamper resistance than <see cref="PlayerPrefsPinStore"/>
    /// against an attacker with adb access on a non-rooted device.
    /// </summary>
    public sealed class EncryptedFilePinStore : IServerKeyPinStore
    {
        // ── Wire-format constants (treat as load-bearing) ─────────────────
        // Bytes are stored as a little-endian 32-bit word so the on-disk
        // sequence is 'R' 'T' 'P' 'I' regardless of host endianness.
        internal const uint MagicHeader   = 0x49_50_54_52u;
        internal const byte FormatVersion = 0x01;
        internal const int  PinLength     = 32;
        internal const int  HmacLength    = 32;
        internal const int  MaxEndpointLength = 1024;

        // Ceiling on total file size when reading.  A pathologically large
        // file (corrupt or hostile) would otherwise force an unbounded
        // allocation on Load.  1 MiB comfortably fits ~10 k records.
        internal const int  MaxFileBytes  = 1 << 20;

        internal const string DefaultFileName = "rtmpe-pins.bin";

        // HKDF salt / info bind the derived MAC key to the SDK identity and
        // a version tag so a future crypto rotation can move the namespace
        // without colliding on existing files.
        private static readonly byte[] HkdfSalt = Encoding.UTF8.GetBytes("RTMPE-pin-mac-v1");
        private static readonly byte[] HkdfInfo = Encoding.UTF8.GetBytes("RTMPE-pin-integrity");

        private readonly string _filePath;
        private readonly byte[] _macKey;
        private readonly object _lock = new object();
        private readonly bool _deviceIdEmpty;
        private int _deviceBindingWarned;   // 0 until the degradation warning fires once

        /// <summary>
        /// Construct the default file-backed pin store.  The file lives at
        /// <see cref="Application.persistentDataPath"/> /
        /// <c>rtmpe-pins.bin</c>, and the MAC key is derived from
        /// <see cref="SystemInfo.deviceUniqueIdentifier"/>.
        /// </summary>
        public EncryptedFilePinStore()
            : this(DefaultFilePath(), SystemInfo.deviceUniqueIdentifier ?? string.Empty)
        {
        }

        // Test seam.  Internal so production code cannot accidentally bypass
        // the platform-appropriate path/device-ID providers.  Exposed via
        // [InternalsVisibleTo("RTMPE.PinStore.Tests")] in AssemblyInfo.
        internal EncryptedFilePinStore(string filePath, string deviceId)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("filePath must be non-empty", nameof(filePath));
            _filePath      = filePath;
            _deviceIdEmpty = string.IsNullOrEmpty(deviceId);
            _macKey        = DeriveMacKey(deviceId ?? string.Empty);
        }

        // When the platform exposes no device identifier (empty deviceId — e.g.
        // WebGL or some privacy modes), the record MAC key derives from public
        // constants only: it still detects file corruption but no longer binds
        // the pin file to this device (a transplanted file would pass the MAC).
        // Surface that degradation once, lazily on first use, so it is never
        // emitted in Strict-with-configured-pin where the store is constructed
        // but never consulted.
        private void WarnDeviceBindingDegradedOnce()
        {
            if (!_deviceIdEmpty) return;
            if (System.Threading.Interlocked.Exchange(ref _deviceBindingWarned, 1) != 0) return;
            Debug.LogWarning(
                "[RTMPE] EncryptedFilePinStore: device identifier unavailable on this " +
                "platform — the pin file's cross-device transplant protection is reduced " +
                "to corruption detection only. Pins remain functional.");
        }

        private static string DefaultFilePath()
        {
            return Path.Combine(Application.persistentDataPath, DefaultFileName);
        }

        // ── Public IServerKeyPinStore surface ─────────────────────────────

        public byte[] Load(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return null;
            WarnDeviceBindingDegradedOnce();
            lock (_lock)
            {
                var records = ReadAllRecords();
                if (records == null) return null;
                return records.TryGetValue(endpoint, out var pin)
                    ? (byte[])pin.Clone()
                    : null;
            }
        }

        public void Save(string endpoint, byte[] pin)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            if (pin == null || pin.Length != PinLength) return;
            var endpointBytes = Encoding.UTF8.GetBytes(endpoint);
            if (endpointBytes.Length > MaxEndpointLength) return;
            WarnDeviceBindingDegradedOnce();

            lock (_lock)
            {
                var records = ReadAllRecords()
                              ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

                // TOFU pin replacement detection mirrors PlayerPrefsPinStore:
                // surface every overwrite as a structured warning so ops have
                // a forensic trail of when (and which endpoint) the pin
                // rotated under them — silently accepting a swap would hide
                // the dominant detection signal for an attacker who already
                // has file-write access.
                if (records.TryGetValue(endpoint, out var existing)
                    && !BytesEqual(existing, pin))
                {
                    Debug.LogWarning(
                        $"[RTMPE] EncryptedFilePinStore: replacing existing pin for endpoint {endpoint}. " +
                        "This is expected after an operator-driven key rotation; an unexpected change " +
                        "indicates either a re-installation or unauthorised modification of the pin " +
                        "file.  Verify the new pin against the server's published key out of band.");
                }

                var stored = new byte[PinLength];
                Buffer.BlockCopy(pin, 0, stored, 0, PinLength);
                records[endpoint] = stored;
                WriteAllRecordsAtomic(records);
            }
        }

        public void Clear(string endpoint)
        {
            if (string.IsNullOrEmpty(endpoint)) return;
            lock (_lock)
            {
                var records = ReadAllRecords();
                if (records == null || !records.Remove(endpoint)) return;
                WriteAllRecordsAtomic(records);
            }
        }

        // ── Key derivation ────────────────────────────────────────────────

        private static byte[] DeriveMacKey(string deviceId)
        {
            // HKDF-SHA256(IKM=device_id, salt=HkdfSalt, info=HkdfInfo) → 32 B.
            // Empty deviceId is permitted (RFC 5869 admits empty IKM) — the
            // resulting MAC key is still well-defined and stable across
            // launches on the same device.  Cross-device transplant
            // protection degrades when both endpoints have empty deviceIds,
            // which is documented at the class header.
            var ikm = Encoding.UTF8.GetBytes(deviceId);
            var prk = HkdfSha256.Extract(HkdfSalt, ikm);
            try
            {
                return HkdfSha256.Expand(prk, HkdfInfo, 32);
            }
            finally
            {
                Array.Clear(prk, 0, prk.Length);
            }
        }

        // ── File IO ───────────────────────────────────────────────────────

        // Returns the decoded record map or null if the file does not exist
        // or is unreadable.  Per-record HMAC mismatches drop the offending
        // entry but preserve the rest, so a tampered record cannot poison
        // unrelated endpoints.
        private Dictionary<string, byte[]> ReadAllRecords()
        {
            byte[] bytes;
            try
            {
                if (!File.Exists(_filePath)) return null;
                var info = new FileInfo(_filePath);
                if (info.Length == 0 || info.Length > MaxFileBytes) return null;
                bytes = File.ReadAllBytes(_filePath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }

            return DecodeRecords(bytes);
        }

        // Atomic write: stage into a sibling .tmp file, then atomically swap
        // it over the live file.  A process crash mid-write loses the
        // staging file but leaves the previous live file untouched, so the
        // pin store can never end up in a half-written state.
        private void WriteAllRecordsAtomic(Dictionary<string, byte[]> records)
        {
            var bytes  = EncodeRecords(records);
            var dir    = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmpPath = _filePath + ".tmp";
            try
            {
                File.WriteAllBytes(tmpPath, bytes);
                if (File.Exists(_filePath))
                {
                    // File.Replace handles the rename + replace as one
                    // operation on POSIX and uses MoveFileEx on Windows;
                    // both provide crash-safe semantics.
                    File.Replace(tmpPath, _filePath, destinationBackupFileName: null);
                }
                else
                {
                    File.Move(tmpPath, _filePath);
                }
            }
            catch (IOException)
            {
                // Clean up the staging file so a failed write leaves no
                // orphaned bytes behind, then propagate.  Callers — notably
                // MigratingPinStore — distinguish a durable write from a failed
                // one solely by whether Save throws: swallowing the failure
                // here would report a non-durable write as success, scrubbing
                // the legacy fallback pin and silently downgrading pinning to a
                // fresh trust-on-first-use capture on the next connect.
                try { if (File.Exists(tmpPath)) File.Delete(tmpPath); }
                catch (IOException) { }
                throw;
            }
        }

        // ── Encode / decode ───────────────────────────────────────────────

        private byte[] EncodeRecords(Dictionary<string, byte[]> records)
        {
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(MagicHeader);
            bw.Write(FormatVersion);
            bw.Write((uint)records.Count);

            foreach (var kv in records)
            {
                var endpointBytes = Encoding.UTF8.GetBytes(kv.Key);
                if (endpointBytes.Length > MaxEndpointLength) continue;
                if (kv.Value == null || kv.Value.Length != PinLength) continue;

                bw.Write((ushort)endpointBytes.Length);
                bw.Write(endpointBytes);
                bw.Write(kv.Value);

                var hmac = ComputeRecordHmac(endpointBytes, kv.Value);
                bw.Write(hmac);
                Array.Clear(hmac, 0, hmac.Length);
            }

            bw.Flush();
            return ms.ToArray();
        }

        private Dictionary<string, byte[]> DecodeRecords(byte[] bytes)
        {
            // Defensive: every length check below treats truncation /
            // malformed input as a soft failure that returns the partial
            // map up to the failure point.  Throwing on a malformed file
            // would break the SDK's connect path on an otherwise-recoverable
            // corruption (disk error, half-written backup, etc.) — the
            // caller already handles "no pin" gracefully via TOFU re-capture.
            const int HeaderLength = 9;  // 4 magic + 1 version + 4 count
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

            if (bytes.Length < HeaderLength) return null;

            using var ms = new MemoryStream(bytes, writable: false);
            using var br = new BinaryReader(ms);

            try
            {
                uint magic = br.ReadUInt32();
                if (magic != MagicHeader) return null;
                byte version = br.ReadByte();
                if (version != FormatVersion) return null;
                uint count = br.ReadUInt32();
                // Sanity-cap the count against the file size so a corrupt
                // record_count cannot drive an outsized loop.
                if (count > (uint)(bytes.Length / (2 + PinLength + HmacLength)))
                    return result;

                for (uint i = 0; i < count; i++)
                {
                    if (ms.Position + 2 > ms.Length) return result;
                    ushort endpointLen = br.ReadUInt16();
                    if (endpointLen == 0 || endpointLen > MaxEndpointLength) return result;

                    if (ms.Position + endpointLen + PinLength + HmacLength > ms.Length) return result;
                    var endpointBytes = br.ReadBytes(endpointLen);
                    var pin           = br.ReadBytes(PinLength);
                    var storedHmac    = br.ReadBytes(HmacLength);

                    var expectedHmac = ComputeRecordHmac(endpointBytes, pin);
                    bool ok = BytesEqual(storedHmac, expectedHmac);
                    Array.Clear(expectedHmac, 0, expectedHmac.Length);

                    if (!ok)
                    {
                        // Drop the tampered / forged record but keep
                        // decoding subsequent ones — a single bitflip
                        // should not nullify the whole store.
                        continue;
                    }

                    var endpoint = Encoding.UTF8.GetString(endpointBytes);
                    result[endpoint] = pin;
                }
            }
            catch (EndOfStreamException)
            {
                // Truncated tail — return what we have so far.
            }

            return result;
        }

        // ── HMAC + constant-time compare ──────────────────────────────────

        private byte[] ComputeRecordHmac(byte[] endpointBytes, byte[] pin)
        {
            // Canonical HMAC input:
            //   [version : 1B]
            //   [endpoint_len : 2B LE]
            //   [endpoint    : N B]
            //   [pin         : 32B]
            // Including the format version blocks downgrade attacks where a
            // future v2 record is reformatted as a v1 one with the same
            // bytes; the endpoint length prefix blocks splice attacks that
            // shift endpoint bytes across the endpoint↔pin boundary.
            var input = new byte[1 + 2 + endpointBytes.Length + PinLength];
            int off = 0;
            input[off++] = FormatVersion;
            input[off++] = (byte)(endpointBytes.Length & 0xff);
            input[off++] = (byte)((endpointBytes.Length >> 8) & 0xff);
            Buffer.BlockCopy(endpointBytes, 0, input, off, endpointBytes.Length);
            off += endpointBytes.Length;
            Buffer.BlockCopy(pin, 0, input, off, PinLength);

            using var hmac = new HMACSHA256(_macKey);
            var tag = hmac.ComputeHash(input);
            Array.Clear(input, 0, input.Length);
            return tag;
        }

        // Constant-time equality.  HMAC verification compares
        // attacker-controlled bytes to a derived value; an early-exit
        // comparison would leak the matched-prefix length as a timing
        // side-channel.  Always walks the full buffer.
        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
