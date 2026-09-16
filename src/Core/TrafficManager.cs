using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Config;
using AITraffic.Fleet;
using AITraffic.Driver;
using AITraffic.Compat;

namespace AITraffic.Core
{
    /// <summary>
    /// Central MonoBehaviour singleton managing AI traffic lifecycle, physics and routing updates,
    /// floating origin shift synchronization, and density target enforcement.
    /// </summary>
    public class TrafficManager : MonoBehaviour
    {
        private static TrafficManager s_instance;
        public static TrafficManager Instance
        {
            get
            {
                if (s_instance == null)
                {
                    var go = new GameObject("[AITraffic_TrafficManager]");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    s_instance = go.AddComponent<TrafficManager>();
                }
                return s_instance;
            }
        }

        public static bool IsRunning
        {
            get { return s_instance != null && s_instance.enabled; }
        }

        private readonly List<AIEngineer> _activeEngineers = new List<AIEngineer>();
        public List<AIEngineer> ActiveEngineers
        {
            get { return _activeEngineers; }
        }

        public int ActiveTrainCount
        {
            get { return _activeEngineers.Count; }
        }

        private AITrafficSettings _settings;
        public AITrafficSettings Settings
        {
            get { return _settings; }
            set { _settings = value; }
        }

        private float _despawnCheckTimer = 0f;
        private const float DespawnCheckInterval = 5.0f;
        private bool _showWorkerDispatcher = false;

        #region Unity Lifecycle

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
            DontDestroyOnLoad(gameObject);

            try
            {
                _lastWorldMove = WorldMover.currentMove;
            }
            catch
            {
                _lastWorldMove = Vector3.zero;
            }

            try
            {
                Application.quitting += OnApplicationQuit;
            }
            catch {}

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                Main.ModEntry.Logger.Log("[TrafficManager] Initialized singleton instance with quit lifecycle hooks.");
        }

        private void OnApplicationQuit()
        {
            try
            {
                DespawnAllAITrains();
            }
            catch {}
        }

        private void OnDestroy()
        {
            try
            {
                Application.quitting -= OnApplicationQuit;
            }
            catch {}

            DespawnAllAITrains();

            if (s_instance == this)
            {
                s_instance = null;
            }

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                Main.ModEntry.Logger.Log("[TrafficManager] Destroyed and cleaned up AI traffic.");
        }

        private void FixedUpdate()
        {
            if (_settings != null && _settings.Density == TrafficDensity.Off)
                return;

            // Physics update loop
            for (int i = _activeEngineers.Count - 1; i >= 0; i--)
            {
                var engineer = _activeEngineers[i];
                if (engineer == null || engineer.TrainCar == null)
                {
                    _activeEngineers.RemoveAt(i);
                    continue;
                }

                // AI Engineer handles its own FixedUpdate internal regulation
            }
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;

            // Check floating origin shift
            CheckFloatingOriginShift();

            if (_settings != null && _settings.Density == TrafficDensity.Off)
            {
                if (_activeEngineers.Count > 0)
                {
                    DespawnAllAITrains();
                }
                return;
            }

            // 1. Synchronize active engineers list
            RefreshActiveEngineers();

            // 2. Periodic despawn safety checks for out-of-range trains
            _despawnCheckTimer += deltaTime;
            if (_despawnCheckTimer >= DespawnCheckInterval)
            {
                _despawnCheckTimer = 0f;
                CheckDespawnEligibleTrains();
            }

            // 3. Update player-employed AI worker tasks
            AITraffic.Workers.WorkerManager.Instance.Update(deltaTime);

            // 4. Update traffic scheduler to maintain active train density
            int maxAllowed = _settings != null ? (int)_settings.MaxActiveTrains : 4;
            TrafficScheduler.Instance.UpdateScheduler(deltaTime, _activeEngineers.Count, maxAllowed, _settings);
        }

        #endregion

        #region Active Trains Management

        /// <summary>
        /// Registers an AIEngineer controller with the TrafficManager.
        /// </summary>
        public void RegisterEngineer(AIEngineer engineer)
        {
            if (engineer != null && !_activeEngineers.Contains(engineer))
            {
                _activeEngineers.Add(engineer);
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Registered AI Engineer for loco '{0}'. Active AI trains: {1}",
                        engineer.TrainCar != null ? engineer.TrainCar.ID : "Unknown", _activeEngineers.Count));
            }
        }

        /// <summary>
        /// Unregisters an AIEngineer controller from the TrafficManager.
        /// </summary>
        public void UnregisterEngineer(AIEngineer engineer)
        {
            if (engineer != null && _activeEngineers.Remove(engineer))
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Unregistered AI Engineer for loco '{0}'. Active AI trains: {1}",
                        engineer.TrainCar != null ? engineer.TrainCar.ID : "Unknown", _activeEngineers.Count));
            }
        }

        private void RefreshActiveEngineers()
        {
            // Clean up null or destroyed engineers efficiently without FindObjectsOfType
            for (int i = _activeEngineers.Count - 1; i >= 0; i--)
            {
                if (_activeEngineers[i] == null || _activeEngineers[i].TrainCar == null)
                {
                    _activeEngineers.RemoveAt(i);
                }
            }
        }

        private struct StationLocationEntry
        {
            public float LastUpdateTime;
            public string Description;
        }

        private static readonly Dictionary<TrainCar, StationLocationEntry> s_stationLocationCache = new Dictionary<TrainCar, StationLocationEntry>();

        /// <summary>
        /// Checks whether a given TrainCar is operated by the AI Traffic mod.
        /// </summary>
        public static bool IsAITrain(TrainCar car)
        {
            return AITraffic.Compat.ModCompatManager.IsAITrain(car);
        }

        /// <summary>
        /// Gets a descriptive string of the train's current world location and nearest station.
        /// Cached per locomotive on a 2-second timer to eliminate per-frame station iteration in OnGUI.
        /// </summary>
        public static string GetTrainLocationDescription(TrainCar trainCar)
        {
            if (trainCar == null) return "Unknown";

            StationLocationEntry cached;
            if (s_stationLocationCache.TryGetValue(trainCar, out cached) && (Time.time - cached.LastUpdateTime < 2.0f))
            {
                return cached.Description;
            }

            string trackName = "Mainline";
            if (trainCar.FrontBogie != null && trainCar.FrontBogie.track != null)
            {
                trackName = trainCar.FrontBogie.track.name;
            }
            else if (trainCar.RearBogie != null && trainCar.RearBogie.track != null)
            {
                trackName = trainCar.RearBogie.track.name;
            }

            string nearestStationStr = "Open Line";
            float minStationDist = float.MaxValue;
            if (StationController.allStations != null)
            {
                Vector3 trainPos = trainCar.transform.position;
                for (int i = 0; i < StationController.allStations.Count; i++)
                {
                    var st = StationController.allStations[i];
                    if (st == null) continue;

                    float dist = Vector3.Distance(trainPos, st.transform.position);
                    if (dist < minStationDist)
                    {
                        minStationDist = dist;
                        string stName = (st.stationInfo != null && !string.IsNullOrEmpty(st.stationInfo.Name)) 
                            ? st.stationInfo.Name 
                            : (st.stationInfo != null ? st.stationInfo.YardID : "Station");
                        string yardId = st.stationInfo != null ? st.stationInfo.YardID : "";
                        nearestStationStr = string.Format("{0} [{1}] ({2:F0}m)", stName, yardId, dist);
                    }
                }
            }

            string result = string.Format("{0} | Track: {1}", nearestStationStr, trackName);
            s_stationLocationCache[trainCar] = new StationLocationEntry { LastUpdateTime = Time.time, Description = result };
            return result;
        }

        /// <summary>
        /// Gets a descriptive string of the train's target destination station and remaining distance.
        /// </summary>
        public static string GetTrainDestinationDescription(AIEngineer eng)
        {
            if (eng == null) return "None";

            string destName = !string.IsNullOrEmpty(eng.DestinationStationName) ? eng.DestinationStationName : "Open Corridor";
            if (!string.IsNullOrEmpty(eng.DestinationTrackName))
            {
                destName += string.Format(" (Track: {0})", eng.DestinationTrackName);
            }

            if (!float.IsInfinity(eng.DistanceToDestination) && eng.DistanceToDestination > 0f)
            {
                return string.Format("{0} — {1:F0}m", destName, eng.DistanceToDestination);
            }

            return destName;
        }

        /// <summary>
        /// Teleports the player to the specified AI locomotive cab or position.
        /// </summary>
        public static void TeleportPlayerToTrain(TrainCar car)
        {
            if (car == null) return;

            try
            {
                PlayerManager.TeleportPlayerToCar(car);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Teleported player to AI train '{0}'.", car.ID));
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error teleporting player to train '{0}': {1}", car.ID, ex.Message));
            }
        }

        private void CheckDespawnEligibleTrains()
        {
            float configuredDespawnDist = _settings != null ? _settings.DespawnDistance : TrainDespawner.DefaultSafeDespawnDistance;
            Vector3 playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;

            for (int i = _activeEngineers.Count - 1; i >= 0; i--)
            {
                var engineer = _activeEngineers[i];
                if (engineer == null || engineer.TrainCar == null || engineer.TrainCar.trainset == null)
                {
                    _activeEngineers.RemoveAt(i);
                    continue;
                }

                // AI Worker trains are player property/consists and must NEVER be despawned!
                if (engineer.IsWorkerDriven)
                {
                    continue;
                }

                // 1. Terminus / Completed Route Despawning:
                // A train stopped at terminus with engine shut down is ready to be cleared as soon as player moves away (> 500m out of view, or > 750m)
                bool isStoppedAtTerminus = (engineer.State == EngineState.TerminusStop) ||
                                           ((engineer.IsTerminusDestination || engineer.IsStationDestination) &&
                                            engineer.CurrentSpeedKmh < 0.5f &&
                                            (engineer.DistanceToDestination < 40.0f || engineer.State == EngineState.TerminusStop));

                if (isStoppedAtTerminus)
                {
                    // Terminus safe despawn distance:
                    // Standard: 500m (outside camera view) or 750m absolute
                    // After resting 120s at terminus: 150m (outside camera view) or 500m absolute to prevent yard throat congestion
                    bool hasDwelledAtTerminus = (engineer.TerminusArrivalTime > 0f && (Time.time - engineer.TerminusArrivalTime > 120f));
                    float minClearDist = hasDwelledAtTerminus ? 150f : 500f;
                    float frustumDist = hasDwelledAtTerminus ? 500f : 750f;

                    if (TrainDespawner.CanDespawnSafely(engineer.TrainCar.trainset, minDistance: minClearDist, frustumDistance: frustumDist))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning completed terminus train '{0}' (player distance cleared: {1:F0}m).",
                                engineer.TrainCar.ID, minClearDist));

                        _activeEngineers.RemoveAt(i);
                        TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                        continue;
                    }
                    continue;
                }

                // 2. Active En-Route Train Rules:

                // Rule A: Spawn Grace Period - Never despawn an active train within 90s of creation
                if (Time.time - engineer.SpawnTime < 90f)
                {
                    continue;
                }

                // Rule B: Directional Protection - Never despawn an active train that is routed towards or passing the player
                if (playerPos != Vector3.zero && TrainDespawner.IsTrainHeadingTowardsPlayer(engineer, playerPos))
                {
                    continue;
                }

                // Rule C: Out-of-Range or Passed-the-Player Moving Away Despawning
                // Despawn promptly once the train has passed the player or moved out of encounter range (> 1000m) AND is outside camera view
                float distToPlayer = playerPos != Vector3.zero ? Vector3.Distance(engineer.TrainCar.transform.position, playerPos) : 0f;
                float despawnThreshold = 1000f;

                if (distToPlayer > despawnThreshold)
                {
                    if (TrainDespawner.CanDespawnSafely(engineer.TrainCar.trainset, minDistance: despawnThreshold, frustumDistance: despawnThreshold))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning AI train '{0}' that passed player or moved out of encounter range ({1:F0}m from player, moving away).",
                                engineer.TrainCar.ID, distToPlayer));

                        _activeEngineers.RemoveAt(i);
                        TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                        continue;
                    }
                }

                // Rule D: Deadlock / Permanently Stuck Recovery
                // If an active train has been completely halted (> 300s / 5 min) outside player view (> 600m)
                if (engineer.StationaryTimer > 300f && distToPlayer > 600f)
                {
                    if (TrainDespawner.CanDespawnSafely(engineer.TrainCar.trainset, minDistance: 600f, frustumDistance: 900f))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Warning(string.Format("[TrafficManager] Despawning stuck AI train '{0}' (stationary for {1:F0}s).",
                                engineer.TrainCar.ID, engineer.StationaryTimer));

                        _activeEngineers.RemoveAt(i);
                        TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Despawns and deletes a specific AI trainset from the world.
        /// </summary>
        public void DespawnAITrain(AIEngineer engineer)
        {
            if (engineer == null) return;
            try
            {
                _activeEngineers.Remove(engineer);
                if (engineer.TrainCar != null && engineer.TrainCar.trainset != null)
                {
                    TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                }
                else if (engineer.TrainCar != null && CarSpawner.Instance != null)
                {
                    CarSpawner.Instance.DeleteCar(engineer.TrainCar);
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawned selected AI train '{0}'.", engineer.TrainCar != null ? engineer.TrainCar.ID : "unknown"));
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error despawning selected AI train: {0}", ex));
            }
        }

        /// <summary>
        /// Despawns and deletes all currently active AI trainsets in the world.
        /// </summary>
        public void DespawnAllAITrains()
        {
            try
            {
                for (int i = _activeEngineers.Count - 1; i >= 0; i--)
                {
                    var engineer = _activeEngineers[i];
                    if (engineer != null && engineer.TrainCar != null && engineer.TrainCar.trainset != null)
                    {
                        TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                    }
                }
                _activeEngineers.Clear();

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log("[TrafficManager] Despawned all AI trains.");
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error despawning all AI trains: {0}", ex));
            }
        }

        #endregion

        #region Floating Origin (WorldMover)

        private Vector3 _lastWorldMove = Vector3.zero;

        private void CheckFloatingOriginShift()
        {
            try
            {
                Vector3 current = WorldMover.currentMove;
                if (current != _lastWorldMove)
                {
                    Vector3 delta = current - _lastWorldMove;
                    _lastWorldMove = current;
                    ApplyOriginShift(delta);
                }
            }
            catch
            {
            }
        }

        private void ApplyOriginShift(Vector3 offset)
        {
            try
            {
                // When origin shifts, notify active engineers and update world-relative crossing positions
                for (int i = 0; i < _activeEngineers.Count; i++)
                {
                    var engineer = _activeEngineers[i];
                    if (engineer == null) continue;

                    if (engineer.LevelCrossings != null && engineer.LevelCrossings.Count > 0)
                    {
                        for (int j = 0; j < engineer.LevelCrossings.Count; j++)
                        {
                            engineer.LevelCrossings[j] += offset;
                        }
                    }
                }

                // Shift existing visualizer points and mark caches dirty to resample from shifted track curves
                for (int i = 0; i < _visualizerCaches.Count; i++)
                {
                    var cache = _visualizerCaches[i];
                    if (cache == null) continue;

                    if (cache.Points != null && cache.Points.Count > 0)
                    {
                        for (int p = 0; p < cache.Points.Count; p++)
                        {
                            cache.Points[p] += offset;
                        }
                    }

                    if (cache.PointsArray != null && cache.PointsArray.Length > 0)
                    {
                        for (int p = 0; p < cache.PointsArray.Length; p++)
                        {
                            cache.PointsArray[p] += offset;
                        }

                        if (i < _routeLineRenderers.Count)
                        {
                            var lr = _routeLineRenderers[i];
                            if (lr != null)
                            {
                                lr.positionCount = cache.PointsArray.Length;
                                lr.SetPositions(cache.PointsArray);
                            }
                        }
                    }

                    cache.TrackIndex = -1;
                    cache.Path = null;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error updating floating origin offset: {0}", ex.Message));
            }
        }

        #endregion

        #region 3D In-World Route Visualizer

        public static readonly Color[] TrainPathColors = new Color[]
        {
            new Color(0.0f, 1.0f, 0.55f),  // Spring / Emerald Green
            new Color(0.15f, 0.85f, 1.0f), // Cyan / Sky Blue
            new Color(1.0f, 0.85f, 0.1f),  // Gold / Yellow
            new Color(1.0f, 0.35f, 0.95f), // Magenta / Neon Pink
            new Color(1.0f, 0.45f, 0.1f),  // Bright Orange
            new Color(0.35f, 0.55f, 1.0f), // Royal / Azure Blue
            new Color(0.65f, 1.0f, 0.2f),  // Lime Green
            new Color(0.85f, 0.4f, 1.0f),  // Purple / Violet
            new Color(1.0f, 0.25f, 0.25f), // Bright Crimson / Red
            new Color(0.1f, 1.0f, 0.85f),  // Turquoise / Aquamarine
            new Color(1.0f, 0.6f, 0.8f),   // Rose Pink
            new Color(0.5f, 0.9f, 1.0f),   // Ice Blue
            new Color(1.0f, 0.7f, 0.3f),   // Coral / Amber
            new Color(0.3f, 1.0f, 0.4f),   // Mint Green
            new Color(0.8f, 0.9f, 0.2f),   // Chartreuse
            new Color(0.9f, 0.6f, 1.0f)    // Lavender / Orchid
        };

        private static readonly Material[] s_pathMaterials = new Material[TrainPathColors.Length];
        private static Shader s_routeShader;

        private static Material GetPathMaterial(int colorIndex)
        {
            int idx = Mathf.Abs(colorIndex) % TrainPathColors.Length;
            if (s_pathMaterials[idx] != null)
                return s_pathMaterials[idx];

            if (s_routeShader == null)
            {
                try
                {
                    var existingLr = UnityEngine.Object.FindObjectOfType<LineRenderer>();
                    if (existingLr != null && existingLr.sharedMaterial != null && existingLr.sharedMaterial.shader != null)
                    {
                        s_routeShader = existingLr.sharedMaterial.shader;
                    }
                }
                catch { }

                if (s_routeShader == null)
                {
                    s_routeShader = Shader.Find("Sprites/Default") ??
                                    Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ??
                                    Shader.Find("Unlit/Color") ??
                                    Shader.Find("UI/Default") ??
                                    Shader.Find("Standard") ??
                                    Shader.Find("Hidden/Internal-Colored");
                }
            }

            if (s_routeShader != null)
            {
                var mat = new Material(s_routeShader);
                Color c = TrainPathColors[idx];
                if (mat.HasProperty("_Color"))
                {
                    mat.color = c;
                }
                s_pathMaterials[idx] = mat;
                return mat;
            }

            return null;
        }

        private class VisualizerTrackCache
        {
            public AIEngineer Engineer;
            public int TrackIndex = -1;
            public Navigation.RailPath Path = null;
            public float Direction;
            public double LastSpan;
            public readonly List<Vector3> Points = new List<Vector3>();
            public Vector3[] PointsArray = new Vector3[0];
        }

        private readonly List<VisualizerTrackCache> _visualizerCaches = new List<VisualizerTrackCache>();
        private readonly List<LineRenderer> _routeLineRenderers = new List<LineRenderer>();

        private void LateUpdate()
        {
            CheckFloatingOriginShift();
            Update3DRouteVisualizer();
#if DEBUG
            if (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.M))
            {
                AITraffic.Diagnostics.TopologyMapExporter.ExportToDesktop();
            }
#endif
        }

        private void Update3DRouteVisualizer()
        {
            bool showVisuals = _settings != null && _settings.ShowRouteVisualizer;
            if (!showVisuals)
            {
                for (int i = 0; i < _routeLineRenderers.Count; i++)
                {
                    if (_routeLineRenderers[i] != null)
                        _routeLineRenderers[i].enabled = false;
                }
                return;
            }

            // Maintain LineRenderer and cache pool for active engineers
            while (_routeLineRenderers.Count < _activeEngineers.Count)
            {
                int newIdx = _routeLineRenderers.Count;
                var lineObj = new GameObject(string.Format("[AI_RouteVisualizer_{0}]", newIdx));
                lineObj.transform.SetParent(transform);
                lineObj.layer = 0; // Default layer (always rendered)
                var lr = lineObj.AddComponent<LineRenderer>();
                var mat = GetPathMaterial(newIdx);
                if (mat != null) lr.sharedMaterial = mat;
                lr.startWidth = 0.50f;
                lr.endWidth = 0.35f;
                lr.useWorldSpace = true;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 4;
                lr.numCornerVertices = 4;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                _routeLineRenderers.Add(lr);
                _visualizerCaches.Add(new VisualizerTrackCache());
            }

            for (int i = 0; i < _routeLineRenderers.Count; i++)
            {
                var lr = _routeLineRenderers[i];
                var cache = _visualizerCaches[i];
                if (i >= _activeEngineers.Count)
                {
                    if (lr != null) lr.enabled = false;
                    cache.Engineer = null;
                    cache.TrackIndex = -1;
                    cache.Path = null;
                    continue;
                }

                var eng = _activeEngineers[i];
                if (eng == null || eng.TrainCar == null || eng.CurrentPath == null || eng.CurrentPath.Tracks == null || eng.CurrentPath.Tracks.Count == 0)
                {
                    if (lr != null) lr.enabled = false;
                    cache.Engineer = null;
                    cache.TrackIndex = -1;
                    cache.Path = null;
                    continue;
                }

                if (cache.Engineer != eng)
                {
                    cache.Engineer = eng;
                    cache.TrackIndex = -1;
                    cache.Path = null;
                    cache.Points.Clear();
                }

                Material pathMat = GetPathMaterial(i % TrainPathColors.Length);
                if (pathMat != null && lr.sharedMaterial != pathMat)
                {
                    lr.sharedMaterial = pathMat;
                }

                Color pathColor = TrainPathColors[i % TrainPathColors.Length];
                lr.startColor = new Color(pathColor.r, pathColor.g, pathColor.b, 0.90f);
                lr.endColor = new Color(pathColor.r, pathColor.g, pathColor.b, 0.25f);

                int startIdx = Mathf.Clamp(eng.CurrentPathTrackIndex, 0, Mathf.Max(0, eng.CurrentPath.Tracks.Count - 1));

                // Determine current span of locomotive on start track
                double curSpan = 0.0;
                if (eng.TrainCar != null && eng.TrainCar.FrontBogie != null && eng.TrainCar.FrontBogie.traveller != null)
                {
                    curSpan = eng.TrainCar.FrontBogie.traveller.Span;
                }

                bool dirChanged = (cache.Direction != eng.TargetDirection);
                bool spanMovedFar = (Math.Abs(curSpan - cache.LastSpan) > 120.0);
                if (cache.TrackIndex != startIdx || cache.Path != eng.CurrentPath || dirChanged || spanMovedFar)
                {
                    cache.TrackIndex = startIdx;
                    cache.Path = eng.CurrentPath;
                    cache.Direction = eng.TargetDirection;
                    cache.LastSpan = curSpan;
                    cache.Points.Clear();

                    float accumulatedMeters = 0f;
                    const float maxRenderDistance = 3000f;

                    Vector3 lastPoint = Vector3.zero;

                    for (int t = startIdx; t < eng.CurrentPath.Tracks.Count && accumulatedMeters < maxRenderDistance; t++)
                    {
                        var track = eng.CurrentPath.Tracks[t];
                        if (track == null || track.curve == null) continue;

                        float len = track.curve.length;
                        if (len <= 0.1f) continue;

                        Vector3 p0 = track.curve.GetPointAt(0.0f) + Vector3.up * 0.65f;
                        Vector3 p1 = track.curve.GetPointAt(1.0f) + Vector3.up * 0.65f;

                        bool isForward;
                        if (t == startIdx)
                        {
                            isForward = (eng.TargetDirection >= 0.0f);
                        }
                        else
                        {
                            isForward = (Vector3.SqrMagnitude(lastPoint - p0) <= Vector3.SqrMagnitude(lastPoint - p1));
                        }

                        float startFrac;
                        float endFrac;

                        if (t == startIdx)
                        {
                            float spanFrac = Mathf.Clamp01((float)(curSpan / len));
                            startFrac = spanFrac;
                            endFrac = isForward ? 1.0f : 0.0f;
                        }
                        else
                        {
                            startFrac = isForward ? 0.0f : 1.0f;
                            endFrac = isForward ? 1.0f : 0.0f;
                        }

                        int samples = Mathf.Max(2, Mathf.RoundToInt(Mathf.Abs(endFrac - startFrac) * len / 4f));

                        for (int s = 0; s <= samples; s++)
                        {
                            float frac = Mathf.Lerp(startFrac, endFrac, (float)s / samples);
                            Vector3 pt = track.curve.GetPointAt(frac) + Vector3.up * 0.65f;

                            if (cache.Points.Count == 0 || Vector3.Distance(cache.Points[cache.Points.Count - 1], pt) > 0.1f)
                            {
                                cache.Points.Add(pt);
                                lastPoint = pt;
                            }
                        }

                        accumulatedMeters += len;
                    }

                    cache.PointsArray = cache.Points.ToArray();
                    lr.positionCount = cache.PointsArray.Length;
                    lr.SetPositions(cache.PointsArray);
                }

                // Crucial visibility fix: Ensure this runs ALWAYS, so toggling visuals or caching never leaves lr.enabled == false
                if (cache.PointsArray != null && cache.PointsArray.Length > 1)
                {
                    if (!lr.gameObject.activeSelf) lr.gameObject.SetActive(true);
                    if (!lr.enabled) lr.enabled = true;
                }
                else
                {
                    if (lr.enabled) lr.enabled = false;
                }
            }
        }

        #endregion

        #region Debug Visuals & On-Screen Overlay

        private readonly HashSet<string> _expandedRoutes = new HashSet<string>();
        private readonly HashSet<string> _expandedSignalBlocks = new HashSet<string>();
        private Vector2 _hudScrollPos = Vector2.zero;
        private float _lastHeaderHeight = 180f;
        private GUIStyle _nameTagStyle;
        private GUIStyle _signalTagStyle;
        private GUIStyle _signalTagBoxStyle;
        private string _lastDispatchStatus = "";
        private bool _stylesInitialized = false;
#if DEBUG
        private bool _showPerformanceProfiler = true;
#endif
        private Camera GetActiveCamera()
        {
            // 1. Derail Valley PlayerManager: ActiveCamera returns PlayerCameraOverride when PhotoMode/Freecam/OrbitCam is engaged
            try
            {
                Camera activeCam = PlayerManager.ActiveCamera;
                if (activeCam != null && activeCam.isActiveAndEnabled)
                    return activeCam;
            }
            catch { }

            // 2. Fallback to Camera.main if valid and enabled
            if (Camera.main != null && Camera.main.isActiveAndEnabled)
                return Camera.main;

            // 3. Fallback to Camera.current during GUI rendering events
            if (Camera.current != null && Camera.current.isActiveAndEnabled)
                return Camera.current;

            return null;
        }

        private void InitStyles()
        {
            if (_stylesInitialized && _nameTagStyle != null) return;

            _nameTagStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            _nameTagStyle.normal.textColor = Color.white;

            _signalTagStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 11,
                fontStyle = FontStyle.Bold,
                wordWrap = false
            };
            _signalTagStyle.normal.textColor = Color.white;

            _signalTagBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(6, 6, 4, 4)
            };

            _stylesInitialized = true;
        }

        private void OnGUI()
        {
            // Draw floating toast notifications for worker hiring and arrival (always shown even if ambient traffic is Off)
            AITraffic.Workers.WorkerManager.Instance.DrawToastGUI();

            if (_settings != null && _settings.Density == TrafficDensity.Off)
                return;

            InitStyles();

            // 1. Draw Master Debug Monitor (HUD)
            bool showHud = _settings != null && _settings.DebugVisuals;
            if (showHud)
            {
                float screenW = Screen.width;
                float screenH = Screen.height;
                float boxWidth = _showWorkerDispatcher ? 740f : 720f;
                float boxHeight = _showWorkerDispatcher ? Mathf.Min(880f, screenH - 70f) : Mathf.Min(780f, screenH - 70f);

                Rect hudRect = new Rect(20f, 50f, boxWidth, boxHeight);

                // Dark semi-transparent background box
                Color prevColor = GUI.color;
                GUI.color = new Color(0.05f, 0.05f, 0.08f, 0.94f);
                GUI.Box(hudRect, GUIContent.none);
                GUI.color = prevColor;

                GUILayout.BeginArea(new Rect(hudRect.x + 10f, hudRect.y + 10f, hudRect.width - 20f, hudRect.height - 20f));

                GUILayout.BeginHorizontal();
                GUILayout.Label("<size=13><b>[AI Traffic Debug Monitor]</b></size>");
                if (_settings != null)
                {
                    _settings.ShowLocoTags = GUILayout.Toggle(_settings.ShowLocoTags, " 3D Locos", GUILayout.Width(78));
                    _settings.ShowRouteVisualizer = GUILayout.Toggle(_settings.ShowRouteVisualizer, " 3D Path", GUILayout.Width(72));
                    _settings.ShowSignalTags = GUILayout.Toggle(_settings.ShowSignalTags, " 3D Signals", GUILayout.Width(86));
                    _settings.RideAlongMode = GUILayout.Toggle(_settings.RideAlongMode, " Ride Along", GUILayout.Width(90));
                }
                bool newShowWorker = GUILayout.Toggle(_showWorkerDispatcher, " 👷 Workers", GUILayout.Width(92));
                if (newShowWorker != _showWorkerDispatcher)
                {
                    _showWorkerDispatcher = newShowWorker;
                    _lastHeaderHeight = _showWorkerDispatcher ? 480f : 180f;
                }
#if DEBUG
                _showPerformanceProfiler = GUILayout.Toggle(_showPerformanceProfiler, " ⚡ Perf", GUILayout.Width(68));
#endif
                GUILayout.EndHorizontal();
                GUILayout.Space(2f);

                if (_showWorkerDispatcher)
                {
                    AITraffic.Workers.WorkerManager.Instance.DrawWorkerDispatcherGUI();
                    GUILayout.Space(6f);
                }

                string rideStatus = (_settings != null && _settings.RideAlongMode) ? " | <color=#00FF88><b>Ride-Along: Active</b></color>" : "";
                GUILayout.Label(string.Format("<b>Mode:</b> {0} | <b>Density:</b> {1} | <b>Max Trains:</b> {2}{3}",
                    _settings.Mode, _settings.Density, _settings.MaxActiveTrains, rideStatus));

                float timeToNext = Mathf.Max(0f, TrafficScheduler.Instance.DispatchIntervalSeconds - (Time.time - TrafficScheduler.Instance.LastDispatchTime));
                GUILayout.Label(string.Format("<b>Active AI Trains:</b> {0} / {1} | <b>Next Dispatch:</b> {2:F0}s",
                    _activeEngineers.Count, _settings.MaxActiveTrains, timeToNext));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Spawn Ambient", GUILayout.Height(22)))
                {
                    _lastDispatchStatus = "<color=#FFFF00>Evaluating clear corridor & dispatching...</color>";
                    bool started = TrafficScheduler.Instance.DispatchTier1Ambient((ok) =>
                    {
                        _lastDispatchStatus = ok ? "<color=#00FF88>Ambient train dispatched successfully!</color>" : "<color=#FF4444>No clear corridor / departure track available.</color>";
                    });
                    if (!started)
                    {
                        _lastDispatchStatus = "<color=#FFAA00>Dispatch already in progress or max active trains reached.</color>";
                    }
                }
                if (GUILayout.Button("Despawn All", GUILayout.Height(22)))
                {
                    DespawnAllAITrains();
                    _lastDispatchStatus = "<color=#FFFFFF>All AI trains despawned.</color>";
                }
#if DEBUG
                if (GUILayout.Button("🗺️ Export Map", GUILayout.Height(22)))
                {
                    AITraffic.Diagnostics.TopologyMapExporter.ExportToDesktop();
                }
#endif
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_lastDispatchStatus))
                {
                    GUILayout.Label(_lastDispatchStatus);
                }

                GUILayout.Space(6f);
                GUILayout.Label("<b>--- Active Locomotives & Locations ---</b>");

                if (Event.current.type == EventType.Repaint)
                {
                    Rect lastRect = GUILayoutUtility.GetLastRect();
                    if (lastRect.yMax > 20f)
                    {
                        _lastHeaderHeight = lastRect.yMax;
                    }
                }

                if (_activeEngineers.Count == 0)
                {
                    GUILayout.Label("<i>No active AI trains currently on track. Click 'Spawn Ambient' to launch a train.</i>");
                }
                else
                {
                    float availableHeight = (hudRect.height - 20f) - _lastHeaderHeight;
                    float scrollHeight = Mathf.Max(120f, availableHeight - 8f);
                    _hudScrollPos = GUILayout.BeginScrollView(_hudScrollPos, GUILayout.Height(scrollHeight));
                    for (int i = 0; i < _activeEngineers.Count; i++)
                    {
                        var eng = _activeEngineers[i];
                        if (eng == null || eng.TrainCar == null) continue;

                        string locoId = eng.TrainCar.ID ?? "Unknown";
                        string state = eng.State.ToString();
                        float speed = eng.CurrentSpeedKmh;
                        float targetSpeed = eng.TargetSpeedKmh;
                        float throttle = eng.CommandedThrottle * 100f;
                        float trainBrake = eng.CommandedTrainBrake * 100f;
                        float dynBrake = eng.CommandedDynamicBrake * 100f;
                        float distDest = eng.DistanceToDestination;
                        float distSig = eng.DistanceToSignal;

                        string locationStr = GetTrainLocationDescription(eng.TrainCar);
                        string destStr = GetTrainDestinationDescription(eng);
                        string sigStr = float.IsInfinity(distSig) ? "Clear" : string.Format("{0:F0}m", distSig);

                        GUILayout.BeginVertical(GUI.skin.box);
                        
                        string reasonStr = string.Format(" <color=#FFD700>[{0} (Limit:{1:F0}km/h)]</color>", 
                            eng.CurrentSpeedProfile.LimitingReason,
                            eng.CurrentSpeedProfile.TrackLimitKmh);

                        string locoColorHex = ColorUtility.ToHtmlStringRGB(TrainPathColors[i % TrainPathColors.Length]);
                        string encounterTag = eng.IsEncounterTrain ? " <color=#00FFFF><b>[Encounter]</b></color>" : "";

                        GUILayout.BeginHorizontal();
                        GUILayout.Label(string.Format("<color=#{0}>■</color> <b>{1}</b>{6} [{2}]  Speed: <b>{3:F1}</b> / {4:F1} km/h{5}", locoColorHex, locoId, state, speed, targetSpeed, reasonStr, encounterTag));
                        
                        bool isRouteExpanded = _expandedRoutes.Contains(locoId);
                        if (GUILayout.Button(isRouteExpanded ? "▲ Route" : "▼ Route", GUILayout.Width(62), GUILayout.Height(19)))
                        {
                            if (isRouteExpanded) _expandedRoutes.Remove(locoId);
                            else _expandedRoutes.Add(locoId);
                        }

                        bool isBlocksExpanded = _expandedSignalBlocks.Contains(locoId);
                        if (GUILayout.Button(isBlocksExpanded ? "▲ Blocks" : "▼ Blocks", GUILayout.Width(68), GUILayout.Height(19)))
                        {
                            if (isBlocksExpanded) _expandedSignalBlocks.Remove(locoId);
                            else _expandedSignalBlocks.Add(locoId);
                        }

                        if (GUILayout.Button("Jump", GUILayout.Width(46), GUILayout.Height(19)))
                        {
                            if (_settings != null) _settings.RideAlongMode = true;
                            TeleportPlayerToTrain(eng.TrainCar);
                        }
                        if (GUILayout.Button("Del", GUILayout.Width(35), GUILayout.Height(19)))
                        {
                            DespawnAITrain(eng);
                            break;
                        }
                        GUILayout.EndHorizontal();

                        GUILayout.Label(string.Format("   <color=#A0D8EF>Loc:</color> {0}", locationStr));
                        GUILayout.Label(string.Format("   <color=#98FB98>Dest:</color> <b>{0}</b>", destStr));

                        if (eng.DM3Controller != null && eng.DM3Controller.IsDM3)
                        {
                            string dm3Status = eng.DM3Controller.IsShifting 
                                ? "<color=#FFA500><b>Shifting...</b></color>" 
                                : string.Format("<b>Gear {0}</b> (A: {1}, B: {2})", eng.DM3Controller.CurrentGearIndex, eng.DM3Controller.CurrentGearA, eng.DM3Controller.CurrentGearB);
                            GUILayout.Label(string.Format("   <color=#FFD700>DM3 Transmission:</color> {0}", dm3Status));
                        }
                        string obsStr = float.IsInfinity(eng.DistanceToObstacle) ? "Clear" : string.Format("<color=#FF5555>{0:F0}m</color>", eng.DistanceToObstacle);
                        GUILayout.Label(string.Format("   Thr: <b>{0:F0}%</b> | Brk: <b>{1:F0}%</b> | Dyn: <b>{2:F0}%</b> | Signal: {3} | Obstacle: {4}", throttle, trainBrake, dynBrake, sigStr, obsStr));

                        // Powertrain telemetry (Thermal, Traction Motor Amperage, Wheel Slip & Rollback)
                        string thermalStr = (eng.CurrentMaxTemperature > 0.1f) 
                            ? (eng.IsOverheated ? string.Format("<color=#FF4444><b>Temp: {0:F0}°C [OVERHEAT CUT]</b></color>", eng.CurrentMaxTemperature) : string.Format("Temp: <b>{0:F0}°C</b>", eng.CurrentMaxTemperature))
                            : "";
                        string ampsStr = (eng.CurrentAmpsPerTM > 1.0f)
                            ? (eng.OvercurrentThrottleLimit < 0.95f ? string.Format("<color=#FFA500><b>Amps: {0:F0}A [Limit {1:F0}%]</b></color>", eng.CurrentAmpsPerTM, eng.OvercurrentThrottleLimit * 100f) : string.Format("Amps: <b>{0:F0}A</b>", eng.CurrentAmpsPerTM))
                            : "";
                        string slipStr = eng.IsWheelSlipping ? " | <color=#FF5555><b>[SLIP]</b></color>" : "";
                        string rollbackStr = eng.IsRollbackDetected ? " | <color=#FF0000><b>[ROLLBACK CLAMP]</b></color>" : "";
                        string hillStr = eng.IsHillStarting ? " | <color=#FFD700><b>[HILL START]</b></color>" : "";

                        if (!string.IsNullOrEmpty(thermalStr) || !string.IsNullOrEmpty(ampsStr) || eng.IsWheelSlipping || eng.IsRollbackDetected || eng.IsHillStarting)
                        {
                            string telemetry = string.Format("   Powertrain: {0}{1}{2}{3}{4}{5}",
                                thermalStr,
                                (!string.IsNullOrEmpty(thermalStr) && !string.IsNullOrEmpty(ampsStr) ? " | " : ""),
                                ampsStr,
                                slipStr,
                                rollbackStr,
                                hillStr);
                            GUILayout.Label(telemetry);
                        }

                        if (eng.IsHoldingForCorridor)
                        {
                            GUILayout.Label(string.Format("   <color=#FFA500><b>[HOLD: Single Track ({0})]</b></color>", eng.CorridorHoldReason ?? "Corridor Conflict"));
                        }

                        // --- Signal Blocks Section (Collapsible) ---
                        if (isBlocksExpanded)
                        {
                            GUILayout.Space(3f);
                            GUILayout.Label("<color=#00FFFF><b>Active Signal Blocks (Interlocking & Clearance):</b></color>");
                            if (eng.UpcomingSignalBlocks == null || eng.UpcomingSignalBlocks.Count == 0)
                            {
                                GUILayout.Label("   <i>No signal blocks detected along current route.</i>");
                            }
                            else
                            {
                                for (int b = 0; b < eng.UpcomingSignalBlocks.Count; b++)
                                {
                                    var blk = eng.UpcomingSignalBlocks[b];
                                    if (blk == null) continue;

                                    string entryName = blk.EntrySignal != null ? AITraffic.Navigation.SignalRegistry.GetSignalName(blk.EntrySignal) : "Train Location";
                                    string exitName = blk.ExitSignal != null ? AITraffic.Navigation.SignalRegistry.GetSignalName(blk.ExitSignal) : "Open Corridor End";
                                    string aspectHex = blk.ExitSignal != null ? AITraffic.Navigation.SignalRegistry.GetAspectColorHex(blk.ExitSignal) : (blk.EntrySignal != null ? AITraffic.Navigation.SignalRegistry.GetAspectColorHex(blk.EntrySignal) : "#00FF88");
                                    string clearStatus = blk.IsClear ? "<color=#00FF88>Clear ✓</color>" : "<color=#FF4444>Occupied ⚠</color>";
                                    string switchStatus = blk.Switches.Count == 0 
                                        ? "<color=#AAAAAA>0 Switches (Straight line)</color>" 
                                        : (blk.AreSwitchesAligned ? string.Format("<color=#00FF88>{0}/{0} Aligned ✓</color>", blk.Switches.Count) : string.Format("<color=#FFA500>{0} Switches (Aligning...)</color>", blk.Switches.Count));

                                    GUILayout.BeginVertical(GUI.skin.box);
                                    GUILayout.Label(string.Format("<b>Block {0}:</b> [{1} ➜ {2}] | Span: <b>{3:F0}m - {4:F0}m</b> (Len: {5:F0}m)",
                                        blk.BlockIndex, entryName, exitName, blk.DistanceToEntry, blk.DistanceToExit, blk.BlockLength));
                                    GUILayout.Label(string.Format("   Aspect: <color={0}><b>{1}</b></color> | Tracks: <b>{2}</b> | Block State: {3}",
                                        aspectHex, blk.AspectName, blk.Tracks.Count, clearStatus));
                                    GUILayout.Label(string.Format("   Switches: {0}", switchStatus));

                                    if (blk.Switches.Count > 0)
                                    {
                                        for (int s = 0; s < blk.Switches.Count; s++)
                                        {
                                            var sw = blk.Switches[s];
                                            string jName = sw.Junction != null ? sw.Junction.name : "Turnout";
                                            string jStatus = sw.IsAligned 
                                                ? string.Format("<color=#00FF88>Branch {0} (Aligned ✓)</color>", sw.RequiredBranch)
                                                : string.Format("<color=#FFA500>Branch {0} (Current: {1})</color>", sw.RequiredBranch, sw.CurrentBranch);
                                            GUILayout.Label(string.Format("     • {0} ➜ {1}", jName, jStatus));
                                        }
                                    }
                                    GUILayout.EndVertical();
                                }
                            }
                        }

                        // --- Route Inspector Section (Collapsible) ---
                        if (isRouteExpanded && eng.CurrentPath != null && eng.CurrentPath.Tracks != null && eng.CurrentPath.Tracks.Count > 0)
                        {
                            GUILayout.Space(3f);
                            GUILayout.Label("<color=#FFD700><b>Planned Route Breakdown:</b></color>");
                            int startIdx = Mathf.Max(0, eng.CurrentPathTrackIndex);
                            int endIdx = Mathf.Min(eng.CurrentPath.Tracks.Count, startIdx + 8);

                            for (int t = startIdx; t < endIdx; t++)
                            {
                                var track = eng.CurrentPath.Tracks[t];
                                if (track == null) continue;
                                string tName = track.name ?? "Track";
                                float tLen = track.curve != null ? track.curve.length : 0f;
                                float tSpeed = eng.SpeedProfiler != null ? eng.SpeedProfiler.GetTrackSpeedLimit(track) : 60f;
                                bool isCurrent = (t == startIdx);

                                string prefix = isCurrent ? "  ▶ <color=#00FF88><b>NOW:</b></color> " : "  • ";
                                GUILayout.Label(string.Format("{0}<b>{1}</b> ({2:F0}m, {3:F0} km/h)", prefix, tName, tLen, tSpeed));
                            }

                            if (eng.CurrentPath.OrderedJunctionSwitches != null && eng.CurrentPath.OrderedJunctionSwitches.Count > 0)
                            {
                                GUILayout.Label(string.Format("  <color=#87CEEB>Upcoming Switches:</color> {0} turnouts in route", eng.CurrentPath.OrderedJunctionSwitches.Count));
                            }

                            if (eng.CurrentPath.Tracks.Count > endIdx)
                            {
                                GUILayout.Label(string.Format("  <i>... and {0} more track segments</i>", eng.CurrentPath.Tracks.Count - endIdx));
                            }
                        }

                        GUILayout.EndVertical();
                        GUILayout.Space(4f);
                    }
                    GUILayout.Space(16f);
                    GUILayout.EndScrollView();
                }

                GUILayout.EndArea();

#if DEBUG
                if (_showPerformanceProfiler)
                {
                    float profilerWidth = 380f;
                    float profilerHeight = 440f;
                    Rect profilerRect = new Rect(Screen.width - profilerWidth - 20f, 50f, profilerWidth, profilerHeight);
                    AITraffic.Diagnostics.PerformanceProfiler.DrawProfilerGUI(profilerRect);
                }
#endif
            }

            // 2. Draw World-Space Floating Nametags over Active AI Trains & 3D Signal Tags
            Camera cam = GetActiveCamera();
            if (cam != null)
            {
                // Active AI Train Nametags (rendered only when toggled on in debug monitor or settings)
                if (_settings != null && _settings.ShowLocoTags)
                {
                    for (int i = 0; i < _activeEngineers.Count; i++)
                    {
                        var eng = _activeEngineers[i];
                        if (eng == null || eng.TrainCar == null) continue;

                        Vector3 worldPos = eng.TrainCar.transform.position + Vector3.up * 3.2f;
                        Vector3 screenPos = cam.WorldToScreenPoint(worldPos);

                        if (screenPos.z > 0f && screenPos.z < 1500f)
                        {
                            float guiY = Screen.height - screenPos.y;
                            string destShort = !string.IsNullOrEmpty(eng.DestinationStationName) ? eng.DestinationStationName : "Open Line";
                            string locoColorHex = ColorUtility.ToHtmlStringRGB(TrainPathColors[i % TrainPathColors.Length]);
                            string encounterBadge = eng.IsEncounterTrain ? " <color=#00FFFF>[Encounter]</color>" : "";
                            string tag = string.Format("<color=#{0}><b>[AI: {1}]</b></color>{5}\n<color=white>{2:F0} km/h ({3})</color>\n<color=#98FB98>➜ {4}</color>",
                                locoColorHex, eng.TrainCar.ID, eng.CurrentSpeedKmh, eng.State, destShort, encounterBadge);
                            GUI.Label(new Rect(screenPos.x - 120f, guiY - 30f, 240f, 60f), tag, _nameTagStyle);
                        }
                    }
                }

                // 3. Draw 3D Floating Signal Tags at upcoming signal positions along active routes
                if (_settings != null && _settings.ShowSignalTags)
                {
                    var renderedSignals = new HashSet<Signals.Game.Signal>();

                    for (int i = 0; i < _activeEngineers.Count; i++)
                    {
                        var eng = _activeEngineers[i];
                        if (eng == null || eng.TrainCar == null || eng.UpcomingSignalBlocks == null) continue;

                        for (int b = 0; b < eng.UpcomingSignalBlocks.Count; b++)
                        {
                            var blk = eng.UpcomingSignalBlocks[b];
                            if (blk == null) continue;

                            Signals.Game.Signal[] blockSignals = new Signals.Game.Signal[] { blk.ExitSignal, blk.EntrySignal };
                            float[] signalDists = new float[] { blk.DistanceToExit, blk.DistanceToEntry };

                            for (int s = 0; s < blockSignals.Length; s++)
                            {
                                var sig = blockSignals[s];
                                float dist = signalDists[s];

                                if (sig == null || renderedSignals.Contains(sig)) continue;
                                if (float.IsInfinity(dist) || dist <= 0f || dist > 2000f) continue;

                                Vector3 sigWorldPos = AITraffic.Navigation.SignalRegistry.GetSignalPosition(sig) + Vector3.up * 4.0f;
                                if (sigWorldPos == Vector3.up * 4.0f && sig.Controller != null && sig.Controller.PlacementInfo.HasValue && sig.Controller.PlacementInfo.Value.Track != null && sig.Controller.PlacementInfo.Value.Track.curve != null)
                                {
                                    var p = sig.Controller.PlacementInfo.Value;
                                    float frac = Mathf.Clamp01((float)(p.Span / p.Track.curve.length));
                                    sigWorldPos = p.Track.curve.GetPointAt(frac) + Vector3.up * 4.0f;
                                }

                                if (sigWorldPos == Vector3.zero) continue;

                                Vector3 sigScreenPos = cam.WorldToScreenPoint(sigWorldPos);
                                if (sigScreenPos.z > 0f && sigScreenPos.z < 1500f)
                                {
                                    renderedSignals.Add(sig);

                                    float sigGuiY = Screen.height - sigScreenPos.y;
                                    string sigName = AITraffic.Navigation.SignalRegistry.GetSignalName(sig);
                                    string aspectName = AITraffic.Navigation.SignalRegistry.GetAspectDisplayName(sig);
                                    string aspectHex = AITraffic.Navigation.SignalRegistry.GetAspectColorHex(sig);

                                    string sigTag = string.Format("<color=#00FFFF><b>[{0}]</b></color>\n<color={1}><b>{2}</b></color>\n<color=white>{3:F0}m</color> <color=#FFD700>({4})</color>",
                                        sigName, aspectHex, aspectName, dist, eng.TrainCar.ID ?? "AI");

                                    Rect tagRect = new Rect(sigScreenPos.x - 110f, sigGuiY - 32f, 220f, 64f);
                                    Color prevC = GUI.color;
                                    GUI.color = new Color(0.08f, 0.08f, 0.12f, 0.88f);
                                    GUI.Box(tagRect, GUIContent.none);
                                    GUI.color = prevC;
                                    GUI.Label(tagRect, sigTag, _signalTagStyle);
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion
    }
}
