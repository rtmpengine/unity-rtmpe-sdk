// RTMPE SDK — Samples~/BasicConnection/Scripts/ConnectionTest.cs
//
// Minimal demo MonoBehaviour: connect to the RTMPE gateway on Start, display
// live connection status with OnGUI, and disconnect cleanly on Destroy.
//
// Quick Start:
//   1. Attach this script to any GameObject in your test scene.
//   2. Set apiKey in the Inspector (or leave blank to use the value from
//      NetworkSettings assigned to NetworkManager).
//   3. Fill in apiKeyPskHex inside your NetworkSettings asset to match the
//      PSK configured on your gateway (required for the encrypted handshake).
//   4. Press Play.

using System.Collections;
using System.Text;
using UnityEngine;
using RTMPE.Core;

namespace RTMPE.Samples.BasicConnection
{
    /// <summary>
    /// Minimal RTMPE connection demo.
    /// Attach to a scene GameObject — <see cref="NetworkManager"/> will be
    /// auto-created as a persistent singleton if not already present.
    /// </summary>
    public sealed class ConnectionTest : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────────

        [Header("Connection")]
        [SerializeField]
        [Tooltip("API key issued by the RTMPE dashboard. "
               + "Leave empty to read from NetworkSettings.")]
        private string apiKey = "test-api-key-replace-me";

        [SerializeField]
        [Tooltip("Connect automatically on Start.")]
        private bool connectOnStart = true;

        [SerializeField]
        [Tooltip("Seconds between automatic reconnect attempts (0 = disabled).")]
        private float reconnectDelay = 5f;

        // ── Runtime state ─────────────────────────────────────────────────────

        private string _statusLine     = "Idle";
        private string _rttLine        = "";
        private bool   _shouldReconnect;
        private Coroutine _reconnectCoroutine;

        // ── Unity Lifecycle ───────────────────────────────────────────────────

        private void Awake()
        {
            // Subscribe to events before Start() so we never miss an early edge.
            var nm = NetworkManager.Instance;
            if (nm == null) { _statusLine = "ERROR: NetworkManager unavailable."; return; }

            nm.OnStateChanged   += OnStateChanged;
            nm.OnConnected      += OnConnected;
            nm.OnDisconnected   += OnDisconnected;
            nm.OnConnectionFailed += OnConnectionFailed;
            nm.OnRttUpdated     += OnRttUpdated;
        }

        private void Start()
        {
            if (connectOnStart)
                TryConnect();
        }

        private void OnDestroy()
        {
            // Clean up so listeners are not called after this object is gone.
            if (_reconnectCoroutine != null)
                StopCoroutine(_reconnectCoroutine);

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            nm.OnStateChanged   -= OnStateChanged;
            nm.OnConnected      -= OnConnected;
            nm.OnDisconnected   -= OnDisconnected;
            nm.OnConnectionFailed -= OnConnectionFailed;
            nm.OnRttUpdated     -= OnRttUpdated;

            if (nm.IsConnected)
                nm.Disconnect();
        }

        // ── Public actions ────────────────────────────────────────────────────

        /// <summary>Connect with the configured API key.</summary>
        public void TryConnect()
        {
            var nm = NetworkManager.Instance;
            if (nm == null) { _statusLine = "ERROR: no NetworkManager."; return; }

            if (string.IsNullOrEmpty(apiKey))
            {
                _statusLine = "apiKey not set — fill it in the Inspector.";
                Debug.LogWarning("[ConnectionTest] Cannot connect: apiKey is empty. " +
                                 "Set it in the Inspector or configure it in NetworkSettings.");
                return;
            }

            _shouldReconnect = reconnectDelay > 0f;
            _statusLine = "Connecting…";
            nm.Connect(apiKey);
        }

        /// <summary>Disconnect and stop auto-reconnect.</summary>
        public void TryDisconnect()
        {
            _shouldReconnect = false;
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
            NetworkManager.Instance?.Disconnect();
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void OnStateChanged(NetworkState prev, NetworkState next)
        {
            _statusLine = $"State: {next}  (was {prev})";
        }

        private void OnConnected()
        {
            _statusLine = "Connected!";
            Debug.Log("[ConnectionTest] Connected to RTMPE gateway.");
        }

        private void OnDisconnected(DisconnectReason reason)
        {
            _statusLine = $"Disconnected ({reason})";
            Debug.Log($"[ConnectionTest] Disconnected — reason: {reason}");

            if (_shouldReconnect && reconnectDelay > 0f && this != null)
            {
                _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
            }
        }

        private void OnConnectionFailed(string error)
        {
            _statusLine = $"Connection failed: {error}";
            Debug.LogError($"[ConnectionTest] Connection failed — {error}");

            if (_shouldReconnect && reconnectDelay > 0f && this != null)
            {
                _reconnectCoroutine = StartCoroutine(ReconnectAfterDelay());
            }
        }

        private void OnRttUpdated(float rttMs)
        {
            _rttLine = $"RTT: {rttMs:F1} ms";
        }

        // ── Auto-reconnect ────────────────────────────────────────────────────

        private IEnumerator ReconnectAfterDelay()
        {
            _statusLine = $"Reconnecting in {reconnectDelay:F0} s…";
            yield return new WaitForSeconds(reconnectDelay);
            _reconnectCoroutine = null;

            var nm = NetworkManager.Instance;
            if (nm == null || nm.State != NetworkState.Disconnected) yield break;

            TryConnect();
        }

        // ── HUD (visible in both Game view and Editor) ────────────────────────

        private readonly GUIStyle _labelStyle  = new GUIStyle();
        private bool              _styleReady;

        private void OnGUI()
        {
            if (!_styleReady)
            {
                _labelStyle.fontSize  = 16;
                _labelStyle.fontStyle = FontStyle.Bold;
                _labelStyle.normal.textColor = Color.white;
                _styleReady = true;
            }

            const int pad = 12;
            const int lineHeight = 24;

            // Background box
            GUI.Box(new Rect(pad, pad, 420, 130), GUIContent.none);

            GUI.Label(new Rect(pad + 8, pad + 6,              400, lineHeight), "[RTMPE] BasicConnection Demo", _labelStyle);
            GUI.Label(new Rect(pad + 8, pad + 6 + lineHeight, 400, lineHeight), _statusLine,  _labelStyle);
            GUI.Label(new Rect(pad + 8, pad + 6 + lineHeight * 2, 400, lineHeight), _rttLine, _labelStyle);

            var nm = NetworkManager.Instance;

            float btnY = pad + 6 + lineHeight * 3 + 4;
            if (!string.IsNullOrEmpty(apiKey) && nm != null && nm.State == NetworkState.Disconnected)
            {
                if (GUI.Button(new Rect(pad + 8, btnY, 120, 28), "Connect"))
                    TryConnect();
            }
            else if (nm != null && nm.IsConnected)
            {
                if (GUI.Button(new Rect(pad + 8, btnY, 120, 28), "Disconnect"))
                    TryDisconnect();
            }
        }
    }
}
