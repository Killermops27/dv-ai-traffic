#if DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;

namespace AITraffic.Diagnostics
{
    /// <summary>
    /// Debug-only performance profiler and live telemetry monitor for AI Traffic.
    /// Tracks pathfinding search times, node counts, car spawn timings, and scheduler state.
    /// Excluded from Release builds.
    /// </summary>
    public static class PerformanceProfiler
    {
        public struct SearchRecord
        {
            public long ElapsedMs;
            public int ExploredNodes;
            public string FromTrack;
            public string ToTrack;
            public bool Success;
            public float Timestamp;
        }

        private static string _schedulerState = "Idle";
        private static int _totalSearches = 0;
        private static int _slowSearches = 0;
        private static long _maxSearchMs = 0;
        private static long _totalSearchMs = 0;
        private static SearchRecord _lastSearch;
        private static readonly Queue<SearchRecord> _recentSearches = new Queue<SearchRecord>(10);

        // Spawn metrics
        private static int _totalSpawns = 0;
        private static string _lastSpawnTrack = "";
        private static int _lastSpawnCarCount = 0;
        private static float _lastSpawnDuration = 0f;
        private static string _currentSpawningStatus = "";

        // FPS tracking
        private static float _fpsAccumulator = 0f;
        private static int _fpsFrames = 0;
        private static float _currentFps = 60f;
        private static float _lastFpsUpdate = 0f;

        public static void SetSchedulerState(string state)
        {
            _schedulerState = state ?? "Idle";
        }

        public static void RecordPathSearch(long elapsedMs, int nodesExplored, string fromTrack, string toTrack, bool success)
        {
            _totalSearches++;
            _totalSearchMs += elapsedMs;
            if (elapsedMs > _maxSearchMs) _maxSearchMs = elapsedMs;
            if (elapsedMs > 15) _slowSearches++;

            var rec = new SearchRecord
            {
                ElapsedMs = elapsedMs,
                ExploredNodes = nodesExplored,
                FromTrack = fromTrack,
                ToTrack = toTrack,
                Success = success,
                Timestamp = Time.realtimeSinceStartup
            };

            _lastSearch = rec;

            lock (_recentSearches)
            {
                if (_recentSearches.Count >= 8)
                {
                    _recentSearches.Dequeue();
                }
                _recentSearches.Enqueue(rec);
            }
        }

        public static void RecordSpawnStart(string trackName, int carCount)
        {
            _currentSpawningStatus = string.Format("Spawning {0} cars on '{1}'...", carCount, trackName);
        }

        public static void RecordSpawnCar(int currentCar, int totalCars)
        {
            _currentSpawningStatus = string.Format("Spawning car {0} of {1}...", currentCar, totalCars);
        }

        public static void RecordSpawnEnd(string trackName, int carCount, float durationSeconds)
        {
            _totalSpawns++;
            _lastSpawnTrack = trackName;
            _lastSpawnCarCount = carCount;
            _lastSpawnDuration = durationSeconds;
            _currentSpawningStatus = "";

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
            {
                Main.ModEntry.Logger.Log(string.Format("[AITraffic-Perf] Spawn Complete: {0} cars on '{1}' took {2:F2}s ({3:F0}ms/car).",
                    carCount, trackName, durationSeconds, (durationSeconds * 1000f) / Math.Max(1, carCount)));
            }
        }

        public static void DrawProfilerGUI(Rect rect)
        {
            // Calculate FPS
            _fpsFrames++;
            _fpsAccumulator += Time.unscaledDeltaTime;
            if (Time.realtimeSinceStartup - _lastFpsUpdate >= 0.5f)
            {
                _currentFps = _fpsAccumulator > 0f ? (_fpsFrames / _fpsAccumulator) : 60f;
                _fpsFrames = 0;
                _fpsAccumulator = 0f;
                _lastFpsUpdate = Time.realtimeSinceStartup;
            }

            Color prevColor = GUI.color;
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.95f);
            GUI.Box(rect, GUIContent.none);
            GUI.color = prevColor;

            GUILayout.BeginArea(new Rect(rect.x + 10f, rect.y + 10f, rect.width - 20f, rect.height - 20f));

            GUILayout.Label(string.Format("<size=13><b>⚡ AI Traffic Performance Telemetry</b></size> | <color=#{0}>{1:F1} FPS</color>",
                _currentFps < 30f ? "FF4444" : (_currentFps < 50f ? "FFAA00" : "00FF88"), _currentFps));

            GUILayout.Space(4f);
            GUILayout.Label(string.Format("<b>Scheduler State:</b> <color=#00FFFF>{0}</color>", _schedulerState));
            if (!string.IsNullOrEmpty(_currentSpawningStatus))
            {
                GUILayout.Label(string.Format("<b>Active Spawn:</b> <color=#FFD700>{0}</color>", _currentSpawningStatus));
            }

            GUILayout.Space(6f);
            GUILayout.Label("<b>--- Pathfinding Engine ---</b>");
            float avgMs = _totalSearches > 0 ? (float)_totalSearchMs / _totalSearches : 0f;
            GUILayout.Label(string.Format("Searches: <b>{0}</b> | Avg: <b>{1:F1}ms</b> | Max: <b>{2}ms</b> | Slow (>15ms): <color=#{3}><b>{4}</b></color>",
                _totalSearches, avgMs, _maxSearchMs, _slowSearches > 0 ? "FF5555" : "00FF88", _slowSearches));

            if (_totalSearches > 0)
            {
                GUILayout.Label(string.Format("Last: <color={0}><b>{1}ms</b> ({2} nodes)</color> '{3}' ➔ '{4}'",
                    _lastSearch.ElapsedMs > 15 ? "#FFA500" : "#A0D8EF",
                    _lastSearch.ElapsedMs,
                    _lastSearch.ExploredNodes,
                    _lastSearch.FromTrack,
                    _lastSearch.ToTrack));
            }

            GUILayout.Space(6f);
            GUILayout.Label("<b>--- Consist Spawning ---</b>");
            GUILayout.Label(string.Format("Total Spawns: <b>{0}</b>", _totalSpawns));
            if (_totalSpawns > 0)
            {
                float msPerCar = (_lastSpawnDuration * 1000f) / Math.Max(1, _lastSpawnCarCount);
                GUILayout.Label(string.Format("Last: <b>{0} cars</b> in <b>{1:F2}s</b> ({2:F0}ms/car) on '{3}'",
                    _lastSpawnCarCount, _lastSpawnDuration, msPerCar, _lastSpawnTrack));
            }

            GUILayout.Space(6f);
            GUILayout.Label("<b>--- Recent Path Searches (Last 5) ---</b>");
            lock (_recentSearches)
            {
                var list = new List<SearchRecord>(_recentSearches);
                int start = Math.Max(0, list.Count - 5);
                for (int i = list.Count - 1; i >= start; i--)
                {
                    var r = list[i];
                    string color = r.ElapsedMs > 15 ? "#FFA500" : (r.Success ? "#C0C0C0" : "#FF5555");
                    string icon = r.Success ? "✓" : "✗";
                    GUILayout.Label(string.Format(" <color={0}>{1} {2}ms ({3}n) '{4}' ➔ '{5}'</color>",
                        color, icon, r.ElapsedMs, r.ExploredNodes, r.FromTrack, r.ToTrack));
                }
            }

            GUILayout.EndArea();
        }
    }
}
#else
using UnityEngine;

namespace AITraffic.Diagnostics
{
    public static class PerformanceProfiler
    {
        public static void SetSchedulerState(string state) { }
        public static void RecordPathSearch(long elapsedMs, int nodesExplored, string fromTrack, string toTrack, bool success) { }
        public static void RecordSpawnStart(string trackName, int carCount) { }
        public static void RecordSpawnCar(int currentCar, int totalCars) { }
        public static void RecordSpawnEnd(string trackName, int carCount, float durationSeconds) { }
        public static void DrawProfilerGUI(Rect rect) { }
    }
}
#endif
