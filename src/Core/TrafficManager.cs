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

        /// <summary>
        /// Checks whether a given TrainCar is operated by the AI Traffic mod.
        /// </summary>
        public static bool IsAITrain(TrainCar car)
        {
            if (car == null) return false;

            if (car.GetComponent<AIEngineer>() != null) return true;

            if (car.trainset != null && car.trainset.cars != null)
            {
                for (int i = 0; i < car.trainset.cars.Count; i++)
                {
                    var c = car.trainset.cars[i];
                    if (c != null && c.GetComponent<AIEngineer>() != null)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets a descriptive string of the train's current world location and nearest station.
        /// </summary>
        public static string GetTrainLocationDescription(TrainCar trainCar)
        {
            if (trainCar == null) return "Unknown";

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

            return string.Format("{0} | Track: {1}", nearestStationStr, trackName);
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

                // 1. Terminus / Completed Route Despawning:
                // A train stopped at terminus with engine shut down is ready to be cleared as soon as player moves away (> 500m out of view, or > 750m)
                bool isStoppedAtTerminus = (engineer.State == EngineState.TerminusStop) ||
                                           ((engineer.IsTerminusDestination || engineer.IsStationDestination) &&
                                            engineer.CurrentSpeedKmh < 0.5f &&
                                            (engineer.DistanceToDestination < 40.0f || engineer.State == EngineState.TerminusStop));

                if (isStoppedAtTerminus)
                {
                    // Terminus safe despawn distance: 500m (outside camera view) or 750m absolute
                    if (TrainDespawner.CanDespawnSafely(engineer.TrainCar.trainset, minDistance: 500f, frustumDistance: 750f))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning completed terminus train '{0}' (player distance cleared).",
                                engineer.TrainCar.ID));

                        _activeEngineers.RemoveAt(i);
                        TrainDespawner.DespawnTrain(engineer.TrainCar.trainset, forceInstant: true);
                        continue;
                    }
                    continue;
                }

                // 2. Active En-Route Train Rules:

                // Rule A: Spawn Grace Period - Never despawn an active train within 180s (3 min) of creation
                if (Time.time - engineer.SpawnTime < 180f)
                {
                    continue;
                }

                // Rule B: Directional Protection - Never despawn an active train that is routed towards or passing the player
                if (playerPos != Vector3.zero && TrainDespawner.IsTrainHeadingTowardsPlayer(engineer, playerPos))
                {
                    continue;
                }

                // Rule C: Out-of-Range Moving Away Despawning
                // Only despawn if the train has traveled far away (> 3500m) AND is moving further away
                float distToPlayer = playerPos != Vector3.zero ? Vector3.Distance(engineer.TrainCar.transform.position, playerPos) : 0f;
                float minOutDist = Mathf.Max(3500f, configuredDespawnDist);

                if (distToPlayer > minOutDist)
                {
                    if (TrainDespawner.CanDespawnSafely(engineer.TrainCar.trainset, minDistance: minOutDist, frustumDistance: minOutDist * 1.2f))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning out-of-range AI train '{0}' (> {1:F0}m from player and moving away).",
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
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error updating floating origin offset: {0}", ex.Message));
            }
        }

        #endregion

        #region 3D In-World Route Visualizer

        private readonly List<LineRenderer> _routeLineRenderers = new List<LineRenderer>();
        private Material _lineMaterial;

        private void LateUpdate()
        {
            Update3DRouteVisualizer();
        }

        private void Update3DRouteVisualizer()
        {
            bool showVisuals = _settings == null || _settings.ShowRouteVisualizer;
            if (!showVisuals)
            {
                for (int i = 0; i < _routeLineRenderers.Count; i++)
                {
                    if (_routeLineRenderers[i] != null)
                        _routeLineRenderers[i].enabled = false;
                }
                return;
            }

            if (_lineMaterial == null)
            {
                try
                {
                    var existingLr = UnityEngine.Object.FindObjectOfType<LineRenderer>();
                    if (existingLr != null && existingLr.sharedMaterial != null)
                    {
                        _lineMaterial = new Material(existingLr.sharedMaterial);
                    }
                }
                catch {}

                if (_lineMaterial == null)
                {
                    Shader shader = Shader.Find("Sprites/Default") ??
                                    Shader.Find("Legacy Shaders/Particles/Alpha Blended Premultiply") ??
                                    Shader.Find("Unlit/Color") ??
                                    Shader.Find("UI/Default") ??
                                    Shader.Find("Standard") ??
                                    Shader.Find("Hidden/Internal-Colored");

                    if (shader != null)
                    {
                        _lineMaterial = new Material(shader);
                    }
                }
            }

            // Maintain LineRenderer pool for active engineers
            while (_routeLineRenderers.Count < _activeEngineers.Count)
            {
                var lineObj = new GameObject(string.Format("[AI_RouteVisualizer_{0}]", _routeLineRenderers.Count));
                lineObj.transform.SetParent(transform);
                lineObj.layer = 0; // Default layer (always rendered)
                var lr = lineObj.AddComponent<LineRenderer>();
                if (_lineMaterial != null) lr.material = _lineMaterial;
                lr.startWidth = 0.50f;
                lr.endWidth = 0.35f;
                lr.useWorldSpace = true;
                lr.alignment = LineAlignment.View;
                lr.numCapVertices = 4;
                lr.numCornerVertices = 4;
                lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                lr.receiveShadows = false;
                _routeLineRenderers.Add(lr);
            }

            Color[] colors = new Color[] {
                new Color(0.0f, 1.0f, 0.55f, 0.90f), // Emerald Green
                new Color(0.2f, 0.85f, 1.0f, 0.90f), // Cyan
                new Color(1.0f, 0.82f, 0.1f, 0.90f), // Gold
                new Color(0.95f, 0.4f, 1.0f, 0.90f)  // Magenta
            };

            for (int i = 0; i < _routeLineRenderers.Count; i++)
            {
                var lr = _routeLineRenderers[i];
                if (i >= _activeEngineers.Count)
                {
                    if (lr != null) lr.enabled = false;
                    continue;
                }

                var eng = _activeEngineers[i];
                if (eng == null || eng.TrainCar == null || eng.CurrentPath == null || eng.CurrentPath.Tracks == null || eng.CurrentPath.Tracks.Count == 0)
                {
                    if (lr != null) lr.enabled = false;
                    continue;
                }

                if (_lineMaterial != null && lr.material != _lineMaterial)
                {
                    lr.material = _lineMaterial;
                }

                Color lineColor = colors[i % colors.Length];
                lr.startColor = lineColor;
                lr.endColor = new Color(lineColor.r, lineColor.g, lineColor.b, 0.20f);

                List<Vector3> pts = new List<Vector3>();

                // Determine current span of locomotive on start track
                double curSpan = 0.0;
                if (eng.TrainCar != null && eng.TrainCar.FrontBogie != null && eng.TrainCar.FrontBogie.traveller != null)
                {
                    curSpan = eng.TrainCar.FrontBogie.traveller.Span;
                }

                int startIdx = Mathf.Clamp(eng.CurrentPathTrackIndex, 0, Mathf.Max(0, eng.CurrentPath.Tracks.Count - 1));
                float accumulatedMeters = 0f;
                const float maxRenderDistance = 3000f;

                Vector3 lastPoint = Vector3.zero;

                for (int t = startIdx; t < eng.CurrentPath.Tracks.Count && accumulatedMeters < maxRenderDistance; t++)
                {
                    var track = eng.CurrentPath.Tracks[t];
                    if (track == null || track.curve == null) continue;

                    float len = track.curve.length;
                    if (len <= 0.1f) continue;

                    Vector3 p0 = track.curve.GetPointAt(0.0f) + Vector3.up * 0.45f;
                    Vector3 p1 = track.curve.GetPointAt(1.0f) + Vector3.up * 0.45f;

                    bool isForward;
                    if (t == startIdx)
                    {
                        // On current track, determine direction by looking at the next track in the path
                        if (t + 1 < eng.CurrentPath.Tracks.Count && eng.CurrentPath.Tracks[t + 1] != null && eng.CurrentPath.Tracks[t + 1].curve != null)
                        {
                            Vector3 nextMid = eng.CurrentPath.Tracks[t + 1].curve.GetPointAt(0.5f);
                            isForward = (Vector3.Distance(p1, nextMid) <= Vector3.Distance(p0, nextMid));
                        }
                        else
                        {
                            Vector3 trackTan = track.curve.GetTangentAt(0.5f);
                            isForward = (Vector3.Dot(eng.TrainCar.transform.forward, trackTan) >= 0f);
                        }
                    }
                    else
                    {
                        // Continuity from previous track's exit point
                        isForward = (Vector3.Distance(lastPoint, p0) <= Vector3.Distance(lastPoint, p1));
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
                        Vector3 pt = track.curve.GetPointAt(frac) + Vector3.up * 0.45f;

                        if (pts.Count == 0 || Vector3.Distance(pts[pts.Count - 1], pt) > 0.1f)
                        {
                            pts.Add(pt);
                            lastPoint = pt;
                        }
                    }

                    accumulatedMeters += len;
                }

                if (pts.Count > 1)
                {
                    lr.gameObject.SetActive(true);
                    lr.positionCount = pts.Count;
                    lr.SetPositions(pts.ToArray());
                    lr.enabled = true;
                }
                else
                {
                    if (lr != null) lr.enabled = false;
                }
            }
        }

        #endregion

        #region Debug Visuals & On-Screen Overlay

        private readonly HashSet<string> _expandedRoutes = new HashSet<string>();
        private readonly HashSet<string> _expandedSignalBlocks = new HashSet<string>();
        private Vector2 _hudScrollPos = Vector2.zero;
        private GUIStyle _nameTagStyle;
        private GUIStyle _signalTagStyle;
        private GUIStyle _signalTagBoxStyle;
        private string _lastDispatchStatus = "";
        private bool _stylesInitialized = false;
        private Camera _mainCamera;

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
            if (_settings != null && _settings.Density == TrafficDensity.Off)
                return;

            InitStyles();

            // 1. Draw Master Debug Monitor (HUD)
            bool showHud = _settings == null || _settings.DebugVisuals;
            if (showHud)
            {
                float screenW = Screen.width;
                float screenH = Screen.height;
                float boxWidth = 550f;
                float boxHeight = Mathf.Min(600f, screenH - 100f);

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
                    _settings.ShowRouteVisualizer = GUILayout.Toggle(_settings.ShowRouteVisualizer, " 3D Path", GUILayout.Width(72));
                    _settings.ShowSignalTags = GUILayout.Toggle(_settings.ShowSignalTags, " 3D Signals", GUILayout.Width(86));
                    _settings.RideAlongMode = GUILayout.Toggle(_settings.RideAlongMode, " Ride Along", GUILayout.Width(90));
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(2f);

                string rideStatus = (_settings != null && _settings.RideAlongMode) ? " | <color=#00FF88><b>Ride-Along: Active</b></color>" : "";
                GUILayout.Label(string.Format("<b>Mode:</b> {0} | <b>Density:</b> {1} | <b>Max Trains:</b> {2}{3}",
                    _settings.Mode, _settings.Density, _settings.MaxActiveTrains, rideStatus));

                float timeToNext = Mathf.Max(0f, TrafficScheduler.Instance.DispatchIntervalSeconds - (Time.time - TrafficScheduler.Instance.LastDispatchTime));
                GUILayout.Label(string.Format("<b>Active AI Trains:</b> {0} / {1} | <b>Next Dispatch:</b> {2:F0}s",
                    _activeEngineers.Count, _settings.MaxActiveTrains, timeToNext));

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Spawn Ambient", GUILayout.Height(22)))
                {
                    bool ok = TrafficScheduler.Instance.DispatchTier1Ambient();
                    _lastDispatchStatus = ok ? "<color=#00FF88>Ambient train dispatched successfully!</color>" : "<color=#FF4444>No clear corridor / departure track available.</color>";
                }
                if (GUILayout.Button("Despawn All", GUILayout.Height(22)))
                {
                    DespawnAllAITrains();
                    _lastDispatchStatus = "<color=#FFFFFF>All AI trains despawned.</color>";
                }
                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(_lastDispatchStatus))
                {
                    GUILayout.Label(_lastDispatchStatus);
                }

                GUILayout.Space(6f);
                GUILayout.Label("<b>--- Active Locomotives & Locations ---</b>");

                if (_activeEngineers.Count == 0)
                {
                    GUILayout.Label("<i>No active AI trains currently on track. Click 'Spawn Ambient' to launch a train.</i>");
                }
                else
                {
                    _hudScrollPos = GUILayout.BeginScrollView(_hudScrollPos, GUILayout.Height(boxHeight - 150f));
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

                        GUILayout.BeginHorizontal();
                        GUILayout.Label(string.Format("• <b>{0}</b> [{1}]  Speed: <b>{2:F1}</b> / {3:F1} km/h{4}", locoId, state, speed, targetSpeed, reasonStr));
                        
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
                    }
                    GUILayout.EndScrollView();
                }

                GUILayout.EndArea();
            }

            // 2. Draw World-Space Floating Nametags over Active AI Trains & 3D Signal Tags
            if (_mainCamera == null) _mainCamera = Camera.main;
            Camera cam = _mainCamera;
            if (cam != null)
            {
                // Active AI Train Nametags
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
                        string tag = string.Format("<color=#FFD700><b>[AI: {0}]</b></color>\n<color=white>{1:F0} km/h ({2})</color>\n<color=#98FB98>➜ {3}</color>",
                            eng.TrainCar.ID, eng.CurrentSpeedKmh, eng.State, destShort);
                        GUI.Label(new Rect(screenPos.x - 120f, guiY - 30f, 240f, 60f), tag, _nameTagStyle);
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
