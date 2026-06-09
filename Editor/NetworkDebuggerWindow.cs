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
        private bool _foldConnection, _foldTraffic, _foldVariables, _foldRooms;

        // Vertical scroll position for the variables panel — the only panel
        // that can grow unboundedly large.
        private Vector2 _variablesScroll;

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
            _foldConnection = SessionState.GetBool(PrefPrefix + "Connection", true);
            _foldTraffic    = SessionState.GetBool(PrefPrefix + "Traffic",    true);
            _foldVariables  = SessionState.GetBool(PrefPrefix + "Variables",  true);
            _foldRooms      = SessionState.GetBool(PrefPrefix + "Rooms",      true);

            EditorApplication.update += OnEditorUpdate;
            ResetSamplerBaseline();
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            SessionState.SetBool(PrefPrefix + "Connection", _foldConnection);
            SessionState.SetBool(PrefPrefix + "Traffic",    _foldTraffic);
            SessionState.SetBool(PrefPrefix + "Variables",  _foldVariables);
            SessionState.SetBool(PrefPrefix + "Rooms",      _foldRooms);
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
                if (_rateSamples.Count > 0) ResetSamplerBaseline();
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

            // First sample after Play entered: only seed the baseline so the
            // first reported rate is computed against a real interval.
            if (_rateSamples.Count == 0)
            {
                _lastPacketsOut = pOut;
                _lastBytesOut   = bOut;
                _lastPacketsIn  = pIn;
                _lastBytesIn    = bIn;
                _lastSampleTime = now;
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
            DrawVariablesPanel(nm);
            EditorGUILayout.Space(4);
            DrawRoomsPanel(nm);
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
                        nm.Settings != null
                            ? $"{nm.Settings.serverHost}:{nm.Settings.serverPort}"
                            : "—");

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
                        $"{_ratePacketsOut:F1} pkt/s   {FormatBytesPerSecond(_rateBytesOut)}");

                    EditorGUILayout.LabelField(
                        new GUIContent("In", "Inbound wire-level rate."),
                        $"{_ratePacketsIn:F1} pkt/s   {FormatBytesPerSecond(_rateBytesIn)}");

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
                        $"{nm.SendQueueCount:N0} queued   {nm.SendQueueDroppedCount:N0} dropped   {nm.EnobufsCount:N0} ENOBUFS");
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
            if (ts.TotalSeconds < 60.0)  return $"{ts.Seconds}s";
            if (ts.TotalMinutes < 60.0)  return $"{ts.Minutes}m {ts.Seconds:D2}s";
            if (ts.TotalHours   < 24.0)  return $"{(int)ts.TotalHours}h {ts.Minutes:D2}m";
            return $"{(int)ts.TotalDays}d {ts.Hours:D2}h";
        }
    }
}
#endif
