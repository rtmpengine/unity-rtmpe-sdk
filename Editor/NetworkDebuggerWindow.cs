// RTMPE SDK — Editor/NetworkDebuggerWindow.cs
//
// Editor-only diagnostic window that surfaces real-time network state for the
// running Play-Mode session.  Open via: Window > RTMPE > Network Debugger.
//
// Design constraints:
//  • READ-ONLY.  No buttons that mutate state — this is a debugger, not a
//    console.  Modifying live network state from the Editor would silently
//    desync clients.
//  • Polls NetworkManager.Instance via EditorApplication.update at ~250 ms
//    so the visible refresh rate is independent of Unity's frame rate.
//    We compute traffic rates by sampling counter deltas across the polling
//    interval; this is cheaper and more stable than per-frame integration.
//  • Allocation-budget conscious — IMGUI is already chatty; we keep our own
//    code free of per-repaint heap allocations beyond what the panels need.
//  • Telemetry counters are read via Volatile/Interlocked accessors on
//    NetworkManager — no new fields are exposed for the window's benefit.
//
// Layout:
//  Connection panel  : state, endpoint, in-room flag, local IDs, room ID,
//                      master client flag.
//  Traffic panel     : packets/s, bytes/s for both directions (rolling 1 s).
//  Variables panel   : per-NetworkObject list with dirty/clean status, send
//                      rate cap and last-flush age.
//  Rooms panel       : current room ID, master, player list (best-effort).

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using RTMPE.Core;
using RTMPE.Sync;

namespace RTMPE.Editor
{
    /// <summary>
    /// Editor window showing live network telemetry for the running session.
    /// </summary>
    public sealed class NetworkDebuggerWindow : EditorWindow
    {
        // ── Sampling cadence ────────────────────────────────────────────────────

        // 250 ms gives a perceptibly live readout without flooding the editor
        // event queue.  Lower values inflate IMGUI cost; higher values lag the
        // user's perception of network changes (e.g. heartbeat misses).
        private const double SampleIntervalSeconds = 0.25;

        // Rolling 1-second smoothing for traffic rates: keep four 250 ms
        // samples and average over the four-sample window.  Larger windows
        // smooth more but lag spikes; this matches what most live-ops dashboards
        // ship (1 s rate at sub-second cadence).
        private const int RateWindowSamples = 4;

        // ── Sampler state ───────────────────────────────────────────────────────

        private double _lastSampleTime;
        private long _lastPacketsOut, _lastBytesOut, _lastPacketsIn, _lastBytesIn;

        // Rolling samples (newest at the back).
        private readonly Queue<RateSample> _rateSamples = new Queue<RateSample>();
        private readonly struct RateSample
        {
            public readonly double DeltaTime;
            public readonly long   PacketsOut, BytesOut, PacketsIn, BytesIn;

            public RateSample(double dt, long po, long bo, long pi, long bi)
            { DeltaTime = dt; PacketsOut = po; BytesOut = bo; PacketsIn = pi; BytesIn = bi; }
        }

        // Computed averages (refreshed each sample).
        private float _ratePacketsOut, _rateBytesOut, _ratePacketsIn, _rateBytesIn;

        // Session-uptime starting point, captured the first time we observe
        // an active connection.  Reset when state returns to Disconnected.
        private DateTime _sessionStartUtc;
        private bool     _sessionStartValid;

        // Foldout state for each panel.  Persisted across domain reloads via
        // SessionState so the user's expansion choices survive script
        // recompilation in the same play session.
        private const string PrefPrefix = "RTMPE.Debugger.Foldout.";
        private bool _foldConnection, _foldTraffic, _foldDiagnostics, _foldVariables, _foldRooms, _foldThreadHealth;

        // Vertical scroll position for the variables panel — the only panel
        // that can grow unboundedly large.
        private Vector2 _variablesScroll;

        // Thread Health panel sampler state.  Delta-based: computed from
        // counter differences between consecutive poll intervals so the
        // displayed rate reflects activity within the last SampleIntervalSeconds
        // window rather than since process start.
        private long  _lastPollHits;
        private long  _lastPollMisses;
        private float _pollHitRate;       // hits / (hits + misses) over the last interval [0..1]
        private float _pollWakeupsPerSec; // poll misses per second ≈ network-thread wakeup rate

        // Seed flag: true once the baseline counters have been captured for the
        // current play session.  Using a dedicated bool instead of
        // _rateSamples.Count == 0 avoids an infinite seed loop — the seed branch
        // returns early without enqueuing a sample, so Count would stay at 0 forever
        // and rate computation would never be reached.
        private bool _baselineSeeded;

        // Suppresses the thermal warning for one repaint frame immediately after a
        // NetworkThread replacement (reconnect).  The first counter delta from a
        // fresh thread reflects it starting from zero rather than sustained
        // busy-wait behaviour.
        private bool _pollJustReset;

        // ── Entry point ─────────────────────────────────────────────────────────

        [MenuItem("Window/RTMPE/Network Debugger")]
        public static void Open()
        {
            var win = GetWindow<NetworkDebuggerWindow>(false, "RTMPE Debugger", true);
            win.minSize = new Vector2(420, 360);
            win.Show();
        }

        // ── Lifecycle ───────────────────────────────────────────────────────────

        private void OnEnable()
        {
            _foldConnection  = SessionState.GetBool(PrefPrefix + "Connection",   true);
            _foldTraffic     = SessionState.GetBool(PrefPrefix + "Traffic",      true);
            _foldDiagnostics = SessionState.GetBool(PrefPrefix + "Diagnostics",  true);
            _foldVariables   = SessionState.GetBool(PrefPrefix + "Variables",    true);
            _foldRooms       = SessionState.GetBool(PrefPrefix + "Rooms",        true);
            _foldThreadHealth = SessionState.GetBool(PrefPrefix + "ThreadHealth", true);

            EditorApplication.update += OnEditorUpdate;
            ResetSamplerBaseline();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            SessionState.SetBool(PrefPrefix + "Connection",   _foldConnection);
            SessionState.SetBool(PrefPrefix + "Traffic",      _foldTraffic);
            SessionState.SetBool(PrefPrefix + "Diagnostics",  _foldDiagnostics);
            SessionState.SetBool(PrefPrefix + "Variables",    _foldVariables);
            SessionState.SetBool(PrefPrefix + "Rooms",        _foldRooms);
            SessionState.SetBool(PrefPrefix + "ThreadHealth", _foldThreadHealth);
        }

        private void ResetSamplerBaseline()
        {
            _lastSampleTime    = EditorApplication.timeSinceStartup;
            _lastPacketsOut    = 0;
            _lastBytesOut      = 0;
            _lastPacketsIn     = 0;
            _lastBytesIn       = 0;
            _rateSamples.Clear();
            _ratePacketsOut    = 0f;
            _rateBytesOut      = 0f;
            _ratePacketsIn     = 0f;
            _rateBytesIn       = 0f;
            _sessionStartValid = false;
            _lastPollHits      = 0;
            _lastPollMisses    = 0;
            _pollHitRate       = 0f;
            _pollWakeupsPerSec = 0f;
            _baselineSeeded    = false;
            _pollJustReset     = false;
        }

        /// <summary>
        /// Editor tick.  Sample telemetry counters at a fixed cadence and
        /// trigger a Repaint so the window updates regardless of which view
        /// has Editor focus.
        /// </summary>
        private void OnEditorUpdate()
        {
            // Outside Play-Mode the singleton is null; nothing to sample.
            if (!Application.isPlaying || !NetworkManager.HasInstance)
            {
                if (_baselineSeeded || _rateSamples.Count > 0) ResetSamplerBaseline();
                return;
            }

            double now = EditorApplication.timeSinceStartup;
            double dt  = now - _lastSampleTime;
            if (dt < SampleIntervalSeconds) return;

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            long pOut = nm.PacketsOutCounter;
            long bOut = nm.BytesOutCounter;
            long pIn  = nm.PacketsInCounter;
            long bIn  = nm.BytesInCounter;

            // First sample after Play entered: seed the baseline counters and
            // return without computing a delta.  The second call computes the
            // first real rate against this seeded baseline.
            //
            // Guard on _baselineSeeded, NOT _rateSamples.Count == 0: the seed
            // branch returns early without enqueuing a sample, so
            // _rateSamples.Count stays at 0 and the delta block below would
            // never be reached if we tested the queue count.
            if (!_baselineSeeded)
            {
                _lastPacketsOut = pOut;
                _lastBytesOut   = bOut;
                _lastPacketsIn  = pIn;
                _lastBytesIn    = bIn;
                _lastPollHits   = nm.NetworkThreadPollHitCount;
                _lastPollMisses = nm.NetworkThreadPollMissCount;
                _lastSampleTime = now;
                _baselineSeeded = true;
                return;
            }

            var sample = new RateSample(
                dt,
                pOut - _lastPacketsOut,
                bOut - _lastBytesOut,
                pIn  - _lastPacketsIn,
                bIn  - _lastBytesIn);

            _lastPacketsOut = pOut;
            _lastBytesOut   = bOut;
            _lastPacketsIn  = pIn;
            _lastBytesIn    = bIn;
            _lastSampleTime = now;

            _rateSamples.Enqueue(sample);
            while (_rateSamples.Count > RateWindowSamples) _rateSamples.Dequeue();

            // Average over the rolling window.
            double sumDt = 0, sumPo = 0, sumBo = 0, sumPi = 0, sumBi = 0;
            foreach (var s in _rateSamples)
            {
                sumDt += s.DeltaTime;
                sumPo += s.PacketsOut;
                sumBo += s.BytesOut;
                sumPi += s.PacketsIn;
                sumBi += s.BytesIn;
            }
            if (sumDt > 0.0)
            {
                _ratePacketsOut = (float)(sumPo / sumDt);
                _rateBytesOut   = (float)(sumBo / sumDt);
                _ratePacketsIn  = (float)(sumPi / sumDt);
                _rateBytesIn    = (float)(sumBi / sumDt);
            }

            // Thread health: compute poll hit rate and wakeup rate from counter
            // deltas since the last sample.  A negative delta means the
            // NetworkThread was replaced (new Connect()); reset the computed
            // metrics so stale values are not displayed after reconnect.
            {
                long curHits   = nm.NetworkThreadPollHitCount;
                long curMisses = nm.NetworkThreadPollMissCount;
                long dHits     = curHits   - _lastPollHits;
                long dMisses   = curMisses - _lastPollMisses;
                if (dHits >= 0 && dMisses >= 0)
                {
                    long pollTotal     = dHits + dMisses;
                    _pollHitRate       = pollTotal > 0 ? (float)dHits / pollTotal : 0f;
                    _pollWakeupsPerSec = dt > 0.0 ? (float)(dMisses / dt) : 0f;
                }
                else
                {
                    // Counter reset after NetworkThread replacement — seed baseline.
                    _pollHitRate       = 0f;
                    _pollWakeupsPerSec = 0f;
                    _pollJustReset     = true;
                }
                _lastPollHits   = curHits;
                _lastPollMisses = curMisses;
            }

            // Track session uptime: capture the first moment we observe a
            // non-Disconnected state, clear when we return to Disconnected.
            if (nm.IsConnected || nm.State == NetworkState.Connecting)
            {
                if (!_sessionStartValid)
                {
                    _sessionStartUtc   = DateTime.UtcNow;
                    _sessionStartValid = true;
                }
            }
            else
            {
                _sessionStartValid = false;
            }

            Repaint();
        }

        // ── GUI ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EditorGUILayout.LabelField("RTMPE Network Debugger", EditorStyles.boldLabel);
            EditorGUILayout.Space(2);

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox(
                    "Network Debugger is only active during Play Mode.  Press Play to see live telemetry.",
                    MessageType.Info);
                return;
            }

            if (!NetworkManager.HasInstance)
            {
                EditorGUILayout.HelpBox(
                    "No NetworkManager instance found.  The SDK auto-creates one on first access; " +
                    "ensure your scene calls NetworkManager.Instance during startup.",
                    MessageType.Info);
                return;
            }

            var nm = NetworkManager.Instance;
            if (nm == null) return;

            DrawConnectionPanel(nm);
            EditorGUILayout.Space(4);
            DrawTrafficPanel(nm);
            EditorGUILayout.Space(4);
            DrawDiagnosticsPanel(nm);
            EditorGUILayout.Space(4);
            DrawVariablesPanel(nm);
            EditorGUILayout.Space(4);
            DrawRoomsPanel(nm);
            EditorGUILayout.Space(4);
            DrawThreadHealthPanel(nm);
        }

        // ── Panels ─────────────────────────────────────────────────────────────

        private void DrawConnectionPanel(NetworkManager nm)
        {
            _foldConnection = EditorGUILayout.BeginFoldoutHeaderGroup(_foldConnection, "Connection");
            if (_foldConnection)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.EnumPopup(
                        new GUIContent("State", "Connection lifecycle state."),
                        nm.State);
                    EditorGUILayout.Toggle("Is Connected", nm.IsConnected);
                    EditorGUILayout.Toggle("Is In Room",   nm.IsInRoom);
                    EditorGUILayout.Toggle("Is Master Client", nm.IsMasterClient);

                    EditorGUILayout.LabelField(
                        new GUIContent("Server Endpoint", "Configured gateway host:port."),
                        new GUIContent(nm.Settings != null
                            ? $"{nm.Settings.serverHost}:{nm.Settings.serverPort}"
                            : "—"));

                    EditorGUILayout.LabelField(
                        "Local Player Id (u64)",
                        nm.LocalPlayerId == 0 ? "—" : nm.LocalPlayerId.ToString());

                    EditorGUILayout.LabelField(
                        "Local Player Id (room UUID)",
                        string.IsNullOrEmpty(nm.LocalPlayerStringId) ? "—" : nm.LocalPlayerStringId);

                    EditorGUILayout.LabelField(
                        "Local Tick",
                        nm.LocalTick.ToString());

                    string uptime = _sessionStartValid
                        ? FormatDuration(DateTime.UtcNow - _sessionStartUtc)
                        : "—";
                    EditorGUILayout.LabelField("Session Uptime", uptime);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawTrafficPanel(NetworkManager nm)
        {
            _foldTraffic = EditorGUILayout.BeginFoldoutHeaderGroup(_foldTraffic, "Traffic (rolling 1 s)");
            if (_foldTraffic)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.LabelField(
                        new GUIContent("Out", "Outbound wire-level rate."),
                        new GUIContent($"{_ratePacketsOut:F1} pkt/s   {FormatBytesPerSecond(_rateBytesOut)}"));

                    EditorGUILayout.LabelField(
                        new GUIContent("In", "Inbound wire-level rate."),
                        new GUIContent($"{_ratePacketsIn:F1} pkt/s   {FormatBytesPerSecond(_rateBytesIn)}"));

                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("Total Out",
                        $"{nm.PacketsOutCounter:N0} pkt   {FormatBytes(nm.BytesOutCounter)}");
                    EditorGUILayout.LabelField("Total In ",
                        $"{nm.PacketsInCounter:N0} pkt   {FormatBytes(nm.BytesInCounter)}");

                    EditorGUILayout.Space(2);
                    // Saturation / back-pressure: a non-zero drop or ENOBUFS
                    // count means the producer is outpacing the uplink — the
                    // signal an integrator must watch under sustained load.
                    EditorGUILayout.LabelField(
                        new GUIContent("Send Queue",
                            "Outbound queue depth · packets dropped at the cap · ENOBUFS events."),
                        new GUIContent($"{nm.SendQueueCount:N0} queued   {nm.SendQueueDroppedCount:N0} dropped   {nm.EnobufsCount:N0} ENOBUFS"));
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawDiagnosticsPanel(NetworkManager nm)
        {
            _foldDiagnostics = EditorGUILayout.BeginFoldoutHeaderGroup(_foldDiagnostics, "Diagnostics");
            if (_foldDiagnostics)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    // Round-trip time — -1 until the first HeartbeatAck arrives.
                    float rtt = nm.LastRttMs;
                    EditorGUILayout.LabelField(
                        new GUIContent("Last RTT",
                            "Round-trip time in milliseconds (last HeartbeatAck).  " +
                            "-1 = not yet measured."),
                        new GUIContent(rtt < 0f ? "— (not yet measured)" : $"{rtt:F1} ms"));

                    // Server backpressure — 0 = no throttle, 255 = bucket empty.
                    byte bp = nm.ServerBackpressure;
                    EditorGUILayout.LabelField(
                        new GUIContent("Server Backpressure",
                            "Gateway per-session token bucket (0 = no throttle, " +
                            "255 = bucket empty; reduce send rate as this approaches 255)."),
                        new GUIContent($"{bp}  {(bp == 0 ? "(none)" : bp > 200 ? "[HIGH]" : "ok")}"));

                    // Local endpoint — null before Connect succeeds.
                    var ep = nm.TransportLocalEndPoint;
                    EditorGUILayout.LabelField(
                        new GUIContent("Local Endpoint",
                            "OS-assigned local UDP address:port (real outgoing interface, " +
                            "not 0.0.0.0).  Available after Connect; null before."),
                        new GUIContent(ep != null ? ep.ToString() : "— (not bound)"));

                    // Source mismatch drops — normally 0; a non-zero value may
                    // indicate an off-path attacker or routing anomaly.
                    long mismatch = nm.TransportDroppedSourceMismatchCount;
                    EditorGUILayout.LabelField(
                        new GUIContent("Src Mismatch Drops",
                            "Inbound datagrams rejected by the source-IP pin.  " +
                            "Non-zero may indicate an off-path sender or routing anomaly."),
                        new GUIContent(mismatch == 0 ? "0" : $"{mismatch:N0}  [non-zero]"));

                    // ENOBUFS count — normally 0; sustained non-zero = uplink saturation.
                    long enobufs = nm.TransportSendBufferExhaustedCount;
                    EditorGUILayout.LabelField(
                        new GUIContent("SendBuf Exhausted",
                            "Send calls that surfaced ENOBUFS (kernel send-buffer full).  " +
                            "Sustained non-zero indicates uplink saturation."),
                        new GUIContent(enobufs == 0 ? "0" : $"{enobufs:N0}  [non-zero]"));
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawVariablesPanel(NetworkManager nm)
        {
            _foldVariables = EditorGUILayout.BeginFoldoutHeaderGroup(
                _foldVariables, "Network Variables");

            if (!_foldVariables) { EditorGUILayout.EndFoldoutHeaderGroup(); return; }

            var spawnRegistry = SafeGetRegistry(nm);
            if (spawnRegistry == null)
            {
                EditorGUILayout.LabelField("No spawn manager active.");
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            var all = spawnRegistry.GetAll();
            if (all.Count == 0)
            {
                EditorGUILayout.LabelField("No registered NetworkObjects.");
                EditorGUILayout.EndFoldoutHeaderGroup();
                return;
            }

            _variablesScroll = EditorGUILayout.BeginScrollView(_variablesScroll,
                GUILayout.MaxHeight(220f));

            float now = Application.isPlaying ? Time.unscaledTime : 0f;
            for (int i = 0; i < all.Count; i++)
            {
                var nb = all[i];
                if (nb == null) continue;

                EditorGUILayout.LabelField(
                    $"{nb.name}  (id {nb.NetworkObjectId})  owner={(string.IsNullOrEmpty(nb.OwnerPlayerId) ? "—" : nb.OwnerPlayerId)}"
                    + (nb.IsOwner ? "  [LOCAL]" : ""),
                    EditorStyles.miniBoldLabel);

                // Defensive snapshot: TrackedVariables is the live underlying
                // list; if the network thread spawns/despawns while OnGUI
                // iterates we'd hit IndexOutOfRange.  Copying the count once
                // and clamping the index loop protects against this race.
                var vars = nb.TrackedVariables;
                int varCount = vars.Count;
                if (varCount == 0)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.LabelField("(no NetworkVariables registered)");
                    EditorGUI.indentLevel--;
                    continue;
                }

                EditorGUI.indentLevel++;
                for (int j = 0; j < varCount; j++)
                {
                    if (j >= vars.Count) break;   // list shrunk mid-iteration
                    var v = vars[j];
                    if (v == null) continue;
                    string rateStr = v.SendRateHz <= 0f
                        ? "default"
                        : $"{v.SendRateHz:F1} Hz";
                    float age = v.LastFlushTimeUnscaled <= 0f ? -1f : (now - v.LastFlushTimeUnscaled);
                    string ageStr = age < 0f ? "—" : $"{age * 1000f:F0} ms";

                    EditorGUILayout.LabelField(
                        $"#{v.VariableId}  {v.GetType().Name}",
                        $"dirty={v.IsDirty}   rate={rateStr}   last-flush={ageStr}");
                }
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawRoomsPanel(NetworkManager nm)
        {
            _foldRooms = EditorGUILayout.BeginFoldoutHeaderGroup(_foldRooms, "Room");
            if (_foldRooms)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    var room = nm.Rooms?.CurrentRoom;
                    if (room == null)
                    {
                        EditorGUILayout.LabelField("Not in a room.");
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Room ID",     room.RoomId ?? "—");
                        EditorGUILayout.LabelField("Master ID",   string.IsNullOrEmpty(room.MasterId) ? "—" : room.MasterId);
                        var players = room.Players;
                        EditorGUILayout.LabelField("Player Count",
                            players != null ? players.Length.ToString() : "0");

                        if (players != null && players.Length > 0)
                        {
                            EditorGUI.indentLevel++;
                            for (int i = 0; i < players.Length; i++)
                            {
                                var p = players[i];
                                if (p == null) continue;
                                EditorGUILayout.LabelField(
                                    $"#{i}  {p.PlayerId}",
                                    p.PlayerId == room.MasterId ? "MASTER" : "");
                            }
                            EditorGUI.indentLevel--;
                        }
                    }

                    // Room timeline — snapshot once per repaint to avoid
                    // collection-modified exceptions if an event fires mid-paint.
                    var timeline = System.Linq.Enumerable.ToArray(nm.RoomTimeline);
                    if (timeline.Length > 0)
                    {
                        EditorGUILayout.Space(4f);
                        EditorGUILayout.LabelField("Recent Events", EditorStyles.boldLabel);
                        EditorGUI.indentLevel++;
                        // Newest first: iterate the snapshot in reverse.
                        for (int i = timeline.Length - 1; i >= 0; i--)
                        {
                            var entry = timeline[i];
                            EditorGUILayout.LabelField(
                                entry.Timestamp.ToString("HH:mm:ss.fff"),
                                entry.Description);
                        }
                        EditorGUI.indentLevel--;
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        private void DrawThreadHealthPanel(NetworkManager nm)
        {
            _foldThreadHealth = EditorGUILayout.BeginFoldoutHeaderGroup(_foldThreadHealth, "Thread Health");
            if (_foldThreadHealth)
            {
                // Compute the warning condition before the DisabledScope so the
                // HelpBox can be placed outside it — DisabledScope grays out
                // interactive controls, which makes a Warning HelpBox harder to read.
                float hitPct = _pollHitRate * 100f;
                bool threadActive = nm.NetworkThreadPollHitCount > 0 || nm.NetworkThreadPollMissCount > 0;
                bool thermalWarning = threadActive && !_pollJustReset && hitPct < 5f && _pollWakeupsPerSec > 900f;
                _pollJustReset = false;

                using (new EditorGUI.DisabledScope(true))
                {
                    if (!threadActive)
                    {
                        EditorGUILayout.LabelField("Thread not started.");
                    }
                    else
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent("Poll Hit Rate",
                                "Fraction of Poll(0) calls that found a datagram waiting. " +
                                "Below ~5% at 1 kHz cadence the thread wakes almost " +
                                "exclusively for nothing — the primary cause of elevated " +
                                "thermal load on Apple Silicon."),
                            new GUIContent($"{hitPct:F1}%  (hits / (hits + misses))"));

                        EditorGUILayout.LabelField(
                            new GUIContent("Wakeups / sec",
                                "Poll misses per second. Each miss is a thread wakeup that " +
                                "found no data. At 1 kHz cadence the idle baseline is ~1000/s. " +
                                "High wakeups combined with low hit rate confirm busy-wait mode."),
                            new GUIContent($"{_pollWakeupsPerSec:F0} / s"));
                    }
                }

                if (thermalWarning)
                {
                    EditorGUILayout.HelpBox(
                        "Hit Rate < 5% with > 900 wakeups/s: NetworkThread is in busy-wait " +
                        "mode. High thermal load expected on Apple Silicon.\n" +
                        "Fix: see §6 of RTMPE_SDK_MACOS_M1_THREADING_ISSUE_2026-06-30.md",
                        MessageType.Warning);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        /// <summary>
        /// Defensive accessor for the spawn-manager registry.  Goes through the
        /// internal <see cref="NetworkManager.SpawnManagerInternal"/> accessor
        /// (visible to RTMPE.SDK.Editor via InternalsVisibleTo) instead of
        /// reflecting on private fields — reflection silently rots across
        /// renames, an internal accessor breaks compilation immediately.
        /// </summary>
        private static RTMPE.Core.NetworkObjectRegistry SafeGetRegistry(NetworkManager nm)
        {
            if (nm == null) return null;
            try
            {
                return nm.SpawnManagerInternal?.Registry;
            }
            catch
            {
                return null;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024L) return bytes + " B";
            double v = bytes;
            string[] units = { "KiB", "MiB", "GiB", "TiB" };
            int unit = -1;
            do { v /= 1024.0; unit++; } while (v >= 1024.0 && unit < units.Length - 1);
            return v.ToString("F2") + " " + units[unit];
        }

        private static string FormatBytesPerSecond(float bytesPerSec)
        {
            if (bytesPerSec < 1024f) return $"{bytesPerSec:F0} B/s";
            float v = bytesPerSec;
            string[] units = { "KiB/s", "MiB/s", "GiB/s" };
            int unit = -1;
            do { v /= 1024f; unit++; } while (v >= 1024f && unit < units.Length - 1);
            return v.ToString("F2") + " " + units[unit];
        }

        private static string FormatDuration(TimeSpan ts)
        {
            if (ts < TimeSpan.Zero)      return "—";
            if (ts.TotalSeconds < 60.0)  return $"{ts.Seconds}s";
            if (ts.TotalMinutes < 60.0)  return $"{ts.Minutes}m {ts.Seconds:D2}s";
            if (ts.TotalHours   < 24.0)  return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m {ts.Seconds:D2}s";
            return $"{(int)ts.TotalDays}d {ts.Hours:D2}h {ts.Minutes:D2}m";
        }
    }
}
#endif
