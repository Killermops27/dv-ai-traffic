#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using AITraffic.Driver;

namespace AITraffic.Diagnostics
{
    /// <summary>
    /// Debug-only high-resolution frame hitch and Mono Garbage Collection (GC) watchdog.
    /// Pinpoints exact culprits of 1% and 0.1% framerate lows by measuring per-frame delta time,
    /// tracking Mono GC collections and heap allocations, and aggregating subsystem execution times.
    /// Excluded from Release builds.
    /// </summary>
    public static class HitchWatchdog
    {
        public struct HitchRecord
        {
            public float Timestamp;
            public float FrameTimeMs;
            public float Fps;
            public string Verdict;
            public float AiTrafficTotalMs;
            public float ExternalMs;
            public float PhysicsMs;
            public int PhysicsTicks;
            public float RenderAndGcMs;
            public float TmMs;
            public float SchedMs;
            public float WorkerMs;
            public float EngineersTotalMs;
            public int EngineerCount;
            public string SlowestLocoId;
            public float SlowestLocoMs;
            public float LightingMs;
            public float JunctionMs;
            public float SignalPatchMs;
            public int SignalsAwakeCount;
            public float StationPatchMs;
            public int Gen0Delta;
            public int Gen1Delta;
            public float HeapMb;
            public float AllocRateMbs;
            public float AiAllocRateMbs;
            public float ExtAllocRateMbs;
            public long FrameAllocBytes;
            public long AiAllocBytes;
            public int ActiveTrains;
            public int ActiveCars;
        }

        // Configuration
        public static float HitchThresholdMs = 25.0f; // Frames >= 25ms (< 40 FPS) qualify as hitches
        private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;
        private static string _logFilePath = null;
        private static bool _initialized = false;

        // Phase timestamps
        private static long _lastLateUpdateEndTicks = 0;
        private static long _physicsPhaseStartTicks = 0;
        private static long _physicsPhaseEndTicks = 0;
        private static int _currPhysicsTicks = 0;
        private static float _currPhysicsMs = 0f;
        private static float _currRenderAndGcMs = 0f;

        // Subsystem accumulators for the CURRENT frame
        private static float _currTmMs = 0f;
        private static float _currSchedMs = 0f;
        private static float _currWorkerMs = 0f;
        private static float _currEngineersMs = 0f;
        private static int _currEngineerCount = 0;
        private static float _currSlowestEngineerMs = 0f;
        private static string _currSlowestEngineerId = "None";
        private static float _currLightingMs = 0f;
        private static float _currJunctionMs = 0f;
        private static float _currSignalPatchMs = 0f;
        private static int _currSignalsAwakeCount = 0;
        private static float _currStationPatchMs = 0f;

        // Allocation tracking for the CURRENT frame
        private static long _currAiAllocBytes = 0;

        // Previous frame values for deltas
        private static int _lastGen0 = 0;
        private static int _lastGen1 = 0;
        private static int _lastGen2 = 0;
        private static long _lastFrameEndMemory = 0;

        // Rolling memory allocation rate tracking
        private static float _allocSampleTimer = 0f;
        private static long _allocBytesAccumulator = 0;
        private static long _aiAllocBytesAccumulator = 0;
        private static float _currentAllocRateMbs = 0f;
        private static float _currentAiAllocRateMbs = 0f;
        private static float _currentExtAllocRateMbs = 0f;
        private static float _periodicAuditTimer = 0f;

        // Lifetime Telemetry
        private static int _totalFrames = 0;
        private static int _totalHitches = 0;
        private static int _totalGcHitches = 0;
        private static int _totalAiHitches = 0;
        private static int _totalExternalHitches = 0;
        private static int _totalPhysicsHitches = 0;
        private static HitchRecord _lastHitch;
        private static bool _hasLastHitch = false;
        private static readonly Queue<HitchRecord> _recentHitches = new Queue<HitchRecord>(10);

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _lastGen0 = GC.CollectionCount(0);
            _lastGen1 = GC.CollectionCount(1);
            _lastGen2 = GC.CollectionCount(2);
            _lastFrameEndMemory = GC.GetTotalMemory(false);
            _lastLateUpdateEndTicks = Stopwatch.GetTimestamp();

            try
            {
                string basePath = Main.ModEntry != null ? Main.ModEntry.Path : Path.Combine(Application.dataPath, "../Mods/AITraffic");
                _logFilePath = Path.Combine(basePath, "hitch_analysis.log");

                string header = string.Format(
                    "====================================================================================================\n" +
                    "   DV AI Traffic - High-Resolution Hitch & Memory Profiler Log\n" +
                    "   Session Started: {0:yyyy-MM-dd HH:mm:ss} | Hitch Threshold: {1:F1}ms\n" +
                    "====================================================================================================\n" +
                    "Timestamp        | FrameMs | FPS   | Verdict                         | AI Total | External | Slowest Loco        | GC Gen0/1 | Heap MB\n" +
                    "-----------------+---------+-------+---------------------------------+----------+----------+---------------------+-----------+---------\n",
                    DateTime.Now, HitchThresholdMs);

                File.AppendAllText(_logFilePath, header);
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[HitchWatchdog] Failed to initialize log file: {0}", ex.Message));
            }
        }

        #region Timing & Allocation Hooks

        public static long GetTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        public static long GetCurrentMemory()
        {
            return GC.GetTotalMemory(false);
        }

        public static void RecordFixedUpdateTick(long startTicks)
        {
            if (_physicsPhaseStartTicks == 0)
            {
                _physicsPhaseStartTicks = startTicks;
            }
            _physicsPhaseEndTicks = Stopwatch.GetTimestamp();
            _currPhysicsTicks++;
        }

        public static void OnUpdatePhaseStart()
        {
            long now = Stopwatch.GetTimestamp();
            if (_physicsPhaseStartTicks > 0)
            {
                _currPhysicsMs = (float)((_physicsPhaseEndTicks - _physicsPhaseStartTicks) * TicksToMs);
            }
            else
            {
                _currPhysicsMs = 0f;
            }

            if (_lastLateUpdateEndTicks > 0)
            {
                float betweenFramesMs = (float)((now - _lastLateUpdateEndTicks) * TicksToMs);
                _currRenderAndGcMs = Math.Max(0f, betweenFramesMs - _currPhysicsMs);
            }
            else
            {
                _currRenderAndGcMs = 0f;
            }
        }

        public static void RecordTrafficManager(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currTmMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordScheduler(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currSchedMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordWorker(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currWorkerMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordEngineer(AIEngineer engineer, long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currEngineersMs += ms;
            _currEngineerCount++;

            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;

            if (ms > _currSlowestEngineerMs)
            {
                _currSlowestEngineerMs = ms;
                if (engineer != null && !string.IsNullOrEmpty(engineer.LocoId))
                {
                    _currSlowestEngineerId = engineer.LocoId;
                }
                else if (engineer != null && engineer.TrainCar != null)
                {
                    _currSlowestEngineerId = engineer.TrainCar.ID;
                }
            }
        }

        public static void RecordLighting(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currLightingMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordJunctionController(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currJunctionMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordSignalPatch(long startTicks, long startMem, bool wokeUp)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currSignalPatchMs += ms;
            if (wokeUp) _currSignalsAwakeCount++;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        public static void RecordStationPatch(long startTicks, long startMem)
        {
            float ms = (float)((Stopwatch.GetTimestamp() - startTicks) * TicksToMs);
            _currStationPatchMs += ms;
            long deltaMem = Math.Max(0, GC.GetTotalMemory(false) - startMem);
            _currAiAllocBytes += deltaMem;
        }

        #endregion

        #region Frame Boundary & Analysis

        public static void OnFrameEnd()
        {
            if (!_initialized)
            {
                Initialize();
            }

            _totalFrames++;
            float dt = Time.unscaledDeltaTime;
            float frameMs = dt * 1000f;
            float fps = dt > 0.0001f ? (1f / dt) : 60f;

            // 1. Mono GC & Heap Telemetry
            int gen0 = GC.CollectionCount(0);
            int gen1 = GC.CollectionCount(1);
            int gen2 = GC.CollectionCount(2);
            long totalMem = GC.GetTotalMemory(false);

            int dGen0 = gen0 - _lastGen0;
            int dGen1 = gen1 - _lastGen1;
            long dMem = totalMem - _lastFrameEndMemory;
            long frameAllocBytes = dMem > 0 ? dMem : 0;
            float reclaimedMb = dMem < 0 ? (-dMem / (1024f * 1024f)) : 0f;

            if (dMem > 0)
            {
                _allocBytesAccumulator += dMem;
            }
            _aiAllocBytesAccumulator += _currAiAllocBytes;

            _allocSampleTimer += dt;
            if (_allocSampleTimer >= 1.0f)
            {
                _currentAllocRateMbs = (_allocBytesAccumulator / _allocSampleTimer) / (1024f * 1024f);
                _currentAiAllocRateMbs = (_aiAllocBytesAccumulator / _allocSampleTimer) / (1024f * 1024f);
                _currentExtAllocRateMbs = Math.Max(0f, _currentAllocRateMbs - _currentAiAllocRateMbs);
                _allocBytesAccumulator = 0;
                _aiAllocBytesAccumulator = 0;
                _allocSampleTimer = 0f;
            }

            // Periodic 5-Second Memory Audit Log
            _periodicAuditTimer += dt;
            if (_periodicAuditTimer >= 5.0f)
            {
                _periodicAuditTimer = 0f;
                WriteMemoryAudit(totalMem, gen0, gen1, gen2);
            }

            // 2. Subsystem timings
            float aiTotalMs = _currTmMs + _currSchedMs + _currWorkerMs + _currEngineersMs + _currLightingMs + _currJunctionMs + _currSignalPatchMs + _currStationPatchMs;
            float externalMs = Math.Max(0f, frameMs - aiTotalMs);
            float heapMb = totalMem / (1024f * 1024f);

            int activeTrains = 0;
            int activeCars = 0;
            if (Core.TrafficManager.Instance != null && Core.TrafficManager.Instance.ActiveEngineers != null)
            {
                var engineers = Core.TrafficManager.Instance.ActiveEngineers;
                activeTrains = engineers.Count;
                for (int i = 0; i < engineers.Count; i++)
                {
                    var eng = engineers[i];
                    if (eng != null && eng.RegisteredConsistCars != null)
                    {
                        activeCars += eng.RegisteredConsistCars.Count;
                    }
                }
            }

            // 3. Hitch Evaluation
            if (frameMs >= HitchThresholdMs)
            {
                _totalHitches++;

                string verdict;
                if (dGen0 > 0 && (_currRenderAndGcMs >= 12.0f || externalMs >= 15.0f))
                {
                    verdict = reclaimedMb > 0f ?
                        string.Format("Mono GC Pause (Gen0:+{0}, -{1:F1}MB)", dGen0, reclaimedMb) :
                        string.Format("Mono GC Pause (Gen0:+{0})", dGen0);
                    _totalGcHitches++;
                }
                else if (_currPhysicsMs >= 15.0f && _currPhysicsTicks > 1)
                {
                    verdict = string.Format("Physics Tick Spike ({0:F1}ms, {1}t)", _currPhysicsMs, _currPhysicsTicks);
                    _totalPhysicsHitches++;
                }
                else if (aiTotalMs >= 15.0f && aiTotalMs >= externalMs)
                {
                    verdict = string.Format("AI Traffic Heavy ({0:F1}ms)", aiTotalMs);
                    _totalAiHitches++;
                }
                else if (externalMs >= 20.0f)
                {
                    verdict = string.Format("Engine/Terrain/Mod ({0:F1}ms)", externalMs);
                    _totalExternalHitches++;
                }
                else
                {
                    verdict = "Combined CPU Load";
                }

                var record = new HitchRecord
                {
                    Timestamp = Time.realtimeSinceStartup,
                    FrameTimeMs = frameMs,
                    Fps = fps,
                    Verdict = verdict,
                    AiTrafficTotalMs = aiTotalMs,
                    ExternalMs = externalMs,
                    PhysicsMs = _currPhysicsMs,
                    PhysicsTicks = _currPhysicsTicks,
                    RenderAndGcMs = _currRenderAndGcMs,
                    TmMs = _currTmMs,
                    SchedMs = _currSchedMs,
                    WorkerMs = _currWorkerMs,
                    EngineersTotalMs = _currEngineersMs,
                    EngineerCount = _currEngineerCount,
                    SlowestLocoId = _currSlowestEngineerId,
                    SlowestLocoMs = _currSlowestEngineerMs,
                    LightingMs = _currLightingMs,
                    JunctionMs = _currJunctionMs,
                    SignalPatchMs = _currSignalPatchMs,
                    SignalsAwakeCount = _currSignalsAwakeCount,
                    StationPatchMs = _currStationPatchMs,
                    Gen0Delta = dGen0,
                    Gen1Delta = dGen1,
                    HeapMb = heapMb,
                    AllocRateMbs = _currentAllocRateMbs,
                    AiAllocRateMbs = _currentAiAllocRateMbs,
                    ExtAllocRateMbs = _currentExtAllocRateMbs,
                    FrameAllocBytes = frameAllocBytes,
                    AiAllocBytes = _currAiAllocBytes,
                    ActiveTrains = activeTrains,
                    ActiveCars = activeCars
                };

                _lastHitch = record;
                _hasLastHitch = true;

                lock (_recentHitches)
                {
                    if (_recentHitches.Count >= 8)
                    {
                        _recentHitches.Dequeue();
                    }
                    _recentHitches.Enqueue(record);
                }

                WriteHitchToFile(record);

                if (frameMs >= 50.0f && Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Warning(string.Format(
                        "[HitchWatchdog] Severe Hitch: {0:F1}ms ({1:F0} FPS) | Verdict: {2} | AI: {3:F1}ms | Physics: {4:F1}ms ({5}t) | External: {6:F1}ms | Slowest Loco: '{7}' ({8:F1}ms)",
                        frameMs, fps, verdict, aiTotalMs, _currPhysicsMs, _currPhysicsTicks, externalMs, _currSlowestEngineerId, _currSlowestEngineerMs));
                }
            }

            // Update previous frame states
            _lastGen0 = gen0;
            _lastGen1 = gen1;
            _lastGen2 = gen2;
            _lastFrameEndMemory = totalMem;

            // Reset current frame accumulators
            _currPhysicsTicks = 0;
            _currPhysicsMs = 0f;
            _currRenderAndGcMs = 0f;
            _physicsPhaseStartTicks = 0;
            _physicsPhaseEndTicks = 0;

            _currTmMs = 0f;
            _currSchedMs = 0f;
            _currWorkerMs = 0f;
            _currEngineersMs = 0f;
            _currEngineerCount = 0;
            _currSlowestEngineerMs = 0f;
            _currSlowestEngineerId = "None";
            _currLightingMs = 0f;
            _currJunctionMs = 0f;
            _currSignalPatchMs = 0f;
            _currSignalsAwakeCount = 0;
            _currStationPatchMs = 0f;
            _currAiAllocBytes = 0;

            _lastLateUpdateEndTicks = Stopwatch.GetTimestamp();
        }

        private static void WriteHitchToFile(HitchRecord r)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                string slowestInfo = r.EngineerCount > 0 ? string.Format("{0} ({1:F1}ms)", r.SlowestLocoId, r.SlowestLocoMs) : "None";
                long extAllocBytes = Math.Max(0, r.FrameAllocBytes - r.AiAllocBytes);

                // Line 1: Standard tabular overview
                string line1 = string.Format(
                    "{0:HH:mm:ss.fff} | {1,5:F1}ms | {2,5:F1} | {3,-33} | {4,6:F1}ms | {5,6:F1}ms | {6,-19} | +{7}/+{8}    | {9,6:F1}MB\n",
                    DateTime.Now, r.FrameTimeMs, r.Fps, r.Verdict, r.AiTrafficTotalMs, r.ExternalMs, slowestInfo, r.Gen0Delta, r.Gen1Delta, r.HeapMb);

                // Line 2: Deep Diagnostics details breakdown
                string line2 = string.Format(
                    "  ➔ DETAILS: Physics={0:F1}ms ({1}t) | Render/GC={2:F1}ms | AI: TM={3:F1}ms, Sched={4:F1}ms, Eng={5:F1}ms, Light={6:F1}ms, Jnc={7:F1}ms, SigPatch={8:F1}ms ({9} awake) | Alloc: Frame=+{10:F0}KB (AI=+{11:F0}KB, Ext=+{12:F0}KB) | World: {13} trains ({14} cars)\n",
                    r.PhysicsMs, r.PhysicsTicks, r.RenderAndGcMs,
                    r.TmMs, r.SchedMs, r.EngineersTotalMs, r.LightingMs, r.JunctionMs, r.SignalPatchMs, r.SignalsAwakeCount,
                    r.FrameAllocBytes / 1024f, r.AiAllocBytes / 1024f, extAllocBytes / 1024f,
                    r.ActiveTrains, r.ActiveCars);

                File.AppendAllText(_logFilePath, line1 + line2);
            }
            catch {}
        }

        private static void WriteMemoryAudit(long totalMem, int gen0, int gen1, int gen2)
        {
            if (string.IsNullOrEmpty(_logFilePath)) return;

            try
            {
                float heapMb = totalMem / (1024f * 1024f);
                float aiPct = _currentAllocRateMbs > 0.01f ? (_currentAiAllocRateMbs / _currentAllocRateMbs * 100f) : 0f;
                float extPct = Math.Max(0f, 100f - aiPct);

                string auditLine = string.Format(
                    "[{0:HH:mm:ss}] [MEM-AUDIT] Heap: {1:F1}MB | Alloc Rate: {2:F2} MB/s | AI Traffic: {3:F2} MB/s ({4:F1}%) | External: {5:F2} MB/s ({6:F1}%) | GC Gen0/1/2: {7}/{8}/{9}\n",
                    DateTime.Now, heapMb, _currentAllocRateMbs, _currentAiAllocRateMbs, aiPct, _currentExtAllocRateMbs, extPct, gen0, gen1, gen2);

                File.AppendAllText(_logFilePath, auditLine);
            }
            catch {}
        }

        #endregion

        #region Profiler GUI Integration

        public static void DrawHitchGUI()
        {
            GUILayout.Space(6f);
            GUILayout.Label("<b>--- Frame Hitch & GC Watchdog ---</b>");

            long currentMem = GC.GetTotalMemory(false);
            float heapMb = currentMem / (1024f * 1024f);
            float aiPct = _currentAllocRateMbs > 0.01f ? (_currentAiAllocRateMbs / _currentAllocRateMbs * 100f) : 0f;

            GUILayout.Label(string.Format("Heap: <b>{0:F1} MB</b> | Alloc: <b>{1:F2} MB/s</b> (AI: <b>{2:F1}%</b> | Ext: <b>{3:F1}%</b>)",
                heapMb, _currentAllocRateMbs, aiPct, Math.Max(0f, 100f - aiPct)));
            GUILayout.Label(string.Format("Collections Gen0/1/2: <b>{0} / {1} / {2}</b>",
                GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));

            GUILayout.Label(string.Format("Hitches (>{0:F0}ms): <color=#{1}><b>{2}</b></color> (GC: <b>{3}</b> | Phys: <b>{4}</b> | AI: <b>{5}</b> | Ext: <b>{6}</b>)",
                HitchThresholdMs,
                _totalHitches > 0 ? "FFA500" : "00FF88",
                _totalHitches, _totalGcHitches, _totalPhysicsHitches, _totalAiHitches, _totalExternalHitches));

            if (_hasLastHitch)
            {
                GUILayout.Label(string.Format("Last Hitch: <color=#FF5555><b>{0:F1}ms</b> ({1:F0} FPS)</color> - <b>{2}</b>",
                    _lastHitch.FrameTimeMs, _lastHitch.Fps, _lastHitch.Verdict));
                GUILayout.Label(string.Format("  ➔ Phys: <b>{0:F1}ms</b> | Render/GC: <b>{1:F1}ms</b> | AI: <b>{2:F1}ms</b> (Slow: '{3}')",
                    _lastHitch.PhysicsMs, _lastHitch.RenderAndGcMs, _lastHitch.AiTrafficTotalMs, _lastHitch.SlowestLocoId));
            }
            else
            {
                GUILayout.Label("<color=#00FF88>✓ Zero frame hitches detected this session.</color>");
            }
        }

        #endregion
    }
}
#else
namespace AITraffic.Diagnostics
{
    public static class HitchWatchdog
    {
        public static float HitchThresholdMs = 25.0f;
        public static void Initialize() { }
        public static long GetTimestamp() { return 0; }
        public static long GetCurrentMemory() { return 0; }
        public static void RecordFixedUpdateTick(long startTicks) { }
        public static void OnUpdatePhaseStart() { }
        public static void RecordTrafficManager(long startTicks, long startMem) { }
        public static void RecordScheduler(long startTicks, long startMem) { }
        public static void RecordWorker(long startTicks, long startMem) { }
        public static void RecordEngineer(object engineer, long startTicks, long startMem) { }
        public static void RecordLighting(long startTicks, long startMem) { }
        public static void RecordJunctionController(long startTicks, long startMem) { }
        public static void RecordSignalPatch(long startTicks, long startMem, bool wokeUp) { }
        public static void RecordStationPatch(long startTicks, long startMem) { }
        public static void OnFrameEnd() { }
        public static void DrawHitchGUI() { }
    }
}
#endif