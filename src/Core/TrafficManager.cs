using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Config;
using AITraffic.Fleet;
using AITraffic.Driver;
using AITraffic.Compat;
using AITraffic.Workers;

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
        private float _orphanCheckTimer = 0f;
        private const float OrphanCheckInterval = 30.0f;
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
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded += OnSceneUnloaded;
            }
            catch {}

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                Main.ModEntry.Logger.Log("[TrafficManager] Initialized singleton instance with quit and scene lifecycle hooks.");
        }

        private void OnSceneUnloaded(UnityEngine.SceneManagement.Scene scene)
        {
            try
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Scene '{0}' unloaded. Aborting active spawn coroutines and resetting spawner state.", scene.name));

                StopAllCoroutines();
                TrainSpawner.ResetSpawningState();
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error handling OnSceneUnloaded: {0}", ex));
            }
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
                UnityEngine.SceneManagement.SceneManager.sceneUnloaded -= OnSceneUnloaded;
            }
            catch {}

            DespawnAllAITrains();

            try
            {
                for (int i = 0; i < _visualizerCaches.Count; i++)
                {
                    if (_visualizerCaches[i] != null && _visualizerCaches[i].ConeMesh != null)
                    {
                        Destroy(_visualizerCaches[i].ConeMesh);
                    }
                }
                _visualizerCaches.Clear();
                _routeLineRenderers.Clear();
                _routeConeFilters.Clear();
                _routeConeRenderers.Clear();

                if (s_outlineMaterial != null)
                {
                    Destroy(s_outlineMaterial);
                    s_outlineMaterial = null;
                }
            }
            catch {}

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

            // 2.5 Periodic orphan AI car cleanup sweep
            _orphanCheckTimer += deltaTime;
            if (_orphanCheckTimer >= OrphanCheckInterval)
            {
                _orphanCheckTimer = 0f;
                CheckOrphanedAICars();
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
                UnregisterStalledAmbientEngineer(engineer);
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Unregistered AI Engineer for loco '{0}'. Active AI trains: {1}",
                        engineer.TrainCar != null ? engineer.TrainCar.ID : "Unknown", _activeEngineers.Count));
            }
        }

        private readonly List<AIEngineer> _stalledAmbientEngineers = new List<AIEngineer>();
        public List<AIEngineer> StalledAmbientEngineers { get { return _stalledAmbientEngineers; } }

        /// <summary>
        /// Registers an ambient AI train that has permanently stalled on a grade.
        /// </summary>
        public void RegisterStalledAmbientEngineer(AIEngineer engineer)
        {
            if (engineer != null && !_stalledAmbientEngineers.Contains(engineer))
            {
                _stalledAmbientEngineers.Add(engineer);
            }
        }

        /// <summary>
        /// Unregisters a stalled ambient AI train upon clearing or despawn.
        /// </summary>
        public void UnregisterStalledAmbientEngineer(AIEngineer engineer)
        {
            if (engineer != null)
            {
                _stalledAmbientEngineers.Remove(engineer);
            }
        }

        /// <summary>
        /// Despawns the oldest permanently stalled ambient AI train from the world.
        /// </summary>
        public void DespawnFirstStalledAmbientTrain()
        {
            for (int i = _stalledAmbientEngineers.Count - 1; i >= 0; i--)
            {
                var eng = _stalledAmbientEngineers[i];
                _stalledAmbientEngineers.RemoveAt(i);
                if (eng != null && eng.TrainCar != null)
                {
                    string id = eng.TrainCar.ID;
                    DespawnAITrain(eng);
                    if (AITraffic.Workers.WorkerManager.Instance != null)
                    {
                        AITraffic.Workers.WorkerManager.Instance.ShowToast(string.Format("Stalled AI Train '{0}' cleared from track.", id), "[AI Traffic]");
                    }
                    break;
                }
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

                float distToPlayer = playerPos != Vector3.zero ? Vector3.Distance(engineer.TrainCar.transform.position, playerPos) : 0f;

                // 1. Terminus / Completed Route Despawning:
                // A train stopped at terminus with engine shut down is ready to be cleared ONLY after:
                // 1. It has genuinely entered final parking (State == EngineState.TerminusStop and CurrentSpeedKmh < 0.2f)
                // 2. It has dwelled at terminus for a realistic duration (>= 120s)
                // 3. The player is far enough away: AT LEAST the distance set for despawn in settings (configuredDespawnDist)
                bool isStoppedAtTerminus = (engineer.State == EngineState.TerminusStop && engineer.CurrentSpeedKmh < 0.2f);

                if (isStoppedAtTerminus)
                {
                    bool hasDwelledAtTerminus = (engineer.TerminusArrivalTime > 0f && (Time.time - engineer.TerminusArrivalTime > 120f));

                    if (hasDwelledAtTerminus)
                    {
                        float minClearDist = configuredDespawnDist;
                        float frustumDist = configuredDespawnDist;

                        if (TrainDespawner.CanDespawnSafely(engineer, minDistance: minClearDist, frustumDistance: frustumDist))
                        {
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning completed terminus train '{0}' (player distance: {1:F0}m >= setting {2:F0}m).",
                                    engineer.TrainCar.ID, distToPlayer, minClearDist));

                            _activeEngineers.RemoveAt(i);
                            TrainDespawner.DespawnTrain(engineer, forceInstant: true);
                            continue;
                        }
                    }
                    continue;
                }

                // 2. Emergency Recovery: Stuck / Deadlocked Ambient Train Despawning
                // If an ambient train is stationary for >= emergency timeout (default 5 min / 300s),
                // emergency delete it with NO regard for distance to player or camera view frustum.
                // The only exceptions:
                // - AI worker trains (checked above)
                // - Terminus trains arrived at destination track (checked above)
                // - Trains where the player is currently aboard / riding in any car
                bool emergencyDespawnEnabled = _settings == null || _settings.EmergencyDespawnStuckTrains;
                float emergencyTimeoutSeconds = (_settings != null && _settings.EmergencyDespawnMinutes > 0f)
                    ? _settings.EmergencyDespawnMinutes * 60f
                    : 300f;

                if (emergencyDespawnEnabled && engineer.State != EngineState.TerminusStop && engineer.StationaryTimer >= emergencyTimeoutSeconds)
                {
                    // Check if player is currently aboard any car of this consist
                    bool isPlayerAboard = false;
                    TrainCar playerCar = PlayerManager.Car;
                    if (playerCar != null)
                    {
                        if (engineer.TrainCar == playerCar)
                        {
                            isPlayerAboard = true;
                        }
                        else if (engineer.RegisteredConsistCars != null && engineer.RegisteredConsistCars.Contains(playerCar))
                        {
                            isPlayerAboard = true;
                        }
                        else if (engineer.TrainCar != null && engineer.TrainCar.trainset != null && engineer.TrainCar.trainset.cars != null && engineer.TrainCar.trainset.cars.Contains(playerCar))
                        {
                            isPlayerAboard = true;
                        }
                    }

                    if (!isPlayerAboard)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        {
                            Main.ModEntry.Logger.Warning(string.Format("[TrafficManager] Emergency despawning stuck AI train '{0}' (stationary for {1:F0}s >= timeout {2:F0}s, player distance: {3:F0}m).",
                                engineer.TrainCar.ID, engineer.StationaryTimer, emergencyTimeoutSeconds, distToPlayer));
                        }

                        _activeEngineers.RemoveAt(i);
                        UnregisterStalledAmbientEngineer(engineer);
                        TrainDespawner.DespawnTrain(engineer, forceInstant: true);
                        continue;
                    }
                }

                // 3. Derailed / Crashed Consist Despawning
                // If an AI train has suffered a derailment/collision across any car in its consist, has come to a stop, and dwelled for >= 60s outside player range
                if (engineer.HasConsistDerailed || engineer.TrainCar.derailed)
                {
                    if (engineer.StationaryTimer >= 60f && distToPlayer > configuredDespawnDist)
                    {
                        if (TrainDespawner.CanDespawnSafely(engineer, minDistance: configuredDespawnDist, frustumDistance: configuredDespawnDist))
                        {
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning derailed AI consist '{0}' ({1} cars, player distance: {2:F0}m >= setting {3:F0}m).",
                                    engineer.TrainCar.ID, engineer.RegisteredConsistCars.Count, distToPlayer, configuredDespawnDist));

                            _activeEngineers.RemoveAt(i);
                            TrainDespawner.DespawnTrain(engineer, forceInstant: true);
                            continue;
                        }
                    }
                    continue;
                }

                // 4. Active En-Route Train Rules:

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
                // Despawn once the train has passed the player or moved out of encounter range (> configuredDespawnDist) AND is outside camera view
                float despawnThreshold = configuredDespawnDist;

                if (distToPlayer > despawnThreshold)
                {
                    if (TrainDespawner.CanDespawnSafely(engineer, minDistance: despawnThreshold, frustumDistance: despawnThreshold))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Despawning AI train '{0}' that passed player or moved out of encounter range ({1:F0}m from player >= setting {2:F0}m, moving away).",
                                engineer.TrainCar.ID, distToPlayer, despawnThreshold));

                        _activeEngineers.RemoveAt(i);
                        TrainDespawner.DespawnTrain(engineer, forceInstant: true);
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Scans for orphaned ambient AI cars (e.g. cars separated in derailments or crashes where the lead
        /// locomotive or engineer was destroyed) and cleans them up outside player range.
        /// </summary>
        private void CheckOrphanedAICars()
        {
            if (CarSpawner.Instance == null || CarSpawner.Instance.AllCars == null)
                return;

            // Never run orphan cleanup while an ambient consist is actively being spawned or coupled
            if (AITraffic.Fleet.TrainSpawner.IsSpawningAmbientConsist)
                return;

            float configuredDespawnDist = _settings != null ? _settings.DespawnDistance : TrainDespawner.DefaultSafeDespawnDistance;
            Transform playerTransform = PlayerManager.PlayerTransform;
            Vector3 playerPos = playerTransform != null ? playerTransform.position : Vector3.zero;
            TrainCar playerCar = PlayerManager.Car;

            var allCars = CarSpawner.Instance.AllCars;
            List<TrainCar> orphansToDelete = null;

            for (int i = allCars.Count - 1; i >= 0; i--)
            {
                if (i >= allCars.Count) continue;
                var car = allCars[i];
                if (car == null) continue;

                // Never touch cars that are currently being spawned or configured
                if (AITraffic.Fleet.TrainSpawner.IsCarSpawning(car))
                    continue;

                // Protect world/derelict locomotives (e.g. S060 at restoration shed) which have playerSpawnedCar == false
                if (!car.playerSpawnedCar || car.logicCar == null)
                    continue;

                // Never touch player worker trains
                if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                    continue;

                // Never touch player's current trainset or car
                if (playerCar != null && (car == playerCar || (car.trainset != null && car.trainset.cars != null && car.trainset.cars.Contains(playerCar))))
                    continue;

                // Check marker spawn age: give newly spawned cars at least 60 seconds grace period before considering them orphaned
                var marker = car.gameObject != null ? car.gameObject.GetComponent<AITrafficCarMarker>() : null;
                if (marker != null && Time.time - marker.SpawnTime < 60f)
                    continue;

                // Check if this car is an ambient AI car
                bool isAiCar = marker != null ||
                               ModCompatManager.IsAmbientAITrain(car) ||
                               ModCompatManager.IsAmbientAITrainId(car.ID);

                if (!isAiCar) continue;

                // Check if this car has an active engineer
                bool hasActiveEngineer = false;
                if (car.trainset != null && car.trainset.cars != null)
                {
                    for (int c = 0; c < car.trainset.cars.Count; c++)
                    {
                        var tc = car.trainset.cars[c];
                        if (tc != null)
                        {
                            var eng = tc.GetComponent<AIEngineer>();
                            if (eng != null && _activeEngineers.Contains(eng))
                            {
                                hasActiveEngineer = true;
                                break;
                            }
                        }
                    }
                }

                if (hasActiveEngineer) continue;

                // Check if an active engineer's registered consist still tracks this car
                for (int e = 0; e < _activeEngineers.Count; e++)
                {
                    var eng = _activeEngineers[e];
                    if (eng != null && eng.RegisteredConsistCars != null && eng.RegisteredConsistCars.Contains(car))
                    {
                        hasActiveEngineer = true;
                        break;
                    }
                }

                if (hasActiveEngineer) continue;

                // This is an uncrewed, orphaned ambient AI car!
                // Verify safety distance from player before despawning
                if (playerPos != Vector3.zero)
                {
                    float distSq = (car.transform.position - playerPos).sqrMagnitude;
                    if (distSq < configuredDespawnDist * configuredDespawnDist)
                    {
                        continue; // Still within player range
                    }
                }

                // Check line of sight / camera frustum
                Camera playerCam = PlayerManager.PlayerCamera ?? Camera.main;
                if (playerCam != null)
                {
                    Vector3 viewportPoint = playerCam.WorldToViewportPoint(car.transform.position);
                    bool inViewFrustum = viewportPoint.z > 0f &&
                                         viewportPoint.x >= -0.05f && viewportPoint.x <= 1.05f &&
                                         viewportPoint.y >= -0.05f && viewportPoint.y <= 1.05f;
                    if (inViewFrustum)
                    {
                        continue;
                    }
                }

                if (orphansToDelete == null) orphansToDelete = new List<TrainCar>();
                orphansToDelete.Add(car);
            }

            if (orphansToDelete != null && orphansToDelete.Count > 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Cleaning up {0} orphaned ambient AI car(s) outside player range.", orphansToDelete.Count));

                for (int i = 0; i < orphansToDelete.Count; i++)
                {
                    var car = orphansToDelete[i];
                    if (car == null) continue;

                    try
                    {
                        if (car.trainset != null)
                        {
                            ModCompatManager.UntagTrain(car.trainset);
                        }
                        if (car.gameObject != null)
                        {
                            var marker = car.gameObject.GetComponent<AITrafficCarMarker>();
                            if (marker != null) UnityEngine.Object.Destroy(marker);
                        }
                    }
                    catch { }
                }

                CarSpawner.Instance.DeleteTrainCars(orphansToDelete, forceInstantDestroy: true);
            }
        }

        /// <summary>
        /// Despawns and deletes a specific AI train and its entire consist from the world.
        /// </summary>
        public void DespawnAITrain(AIEngineer engineer)
        {
            if (engineer == null) return;
            try
            {
                _activeEngineers.Remove(engineer);
                TrainDespawner.DespawnTrain(engineer, forceInstant: true);

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
        /// Despawns and deletes all currently active AI trainsets and orphaned ambient cars in the world.
        /// </summary>
        public void DespawnAllAITrains()
        {
            PurgeAllWorldAICars(forceAll: false);
        }

        /// <summary>
        /// Purges and deletes all ambient AI trains and orphaned AI cars from the world.
        /// Strictly preserves player rolling stock, jobs, and worker-driven trains.
        /// </summary>
        /// <returns>The total number of cars deleted.</returns>
        public int PurgeAllWorldAICars(bool forceAll = false)
        {
            int purgedCount = 0;
            try
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log("[TrafficManager] Purging all ambient AI trains and orphaned AI cars from world...");

                // 1. Despawn all active engineers and their registered consists
                for (int i = _activeEngineers.Count - 1; i >= 0; i--)
                {
                    var engineer = _activeEngineers[i];
                    if (engineer == null) continue;

                    // Skip player worker trains unless forceAll is requested
                    if (engineer.IsWorkerDriven && !forceAll) continue;

                    _activeEngineers.RemoveAt(i);
                    TrainDespawner.DespawnTrain(engineer, forceInstant: true);
                }
                _activeEngineers.Clear();

                // 2. Scan CarSpawner for any remaining ambient AI cars or orphaned markers
                if (CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
                {
                    TrainCar playerCar = PlayerManager.Car;
                    var allCars = CarSpawner.Instance.AllCars;
                    List<TrainCar> carsToDelete = new List<TrainCar>();

                    for (int i = 0; i < allCars.Count; i++)
                    {
                        var car = allCars[i];
                        if (car == null) continue;

                        // Never purge player trains or worker trains
                        if (!forceAll)
                        {
                            if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                                continue;

                            if (playerCar != null && (car == playerCar || (car.trainset != null && car.trainset.cars != null && car.trainset.cars.Contains(playerCar))))
                                continue;
                        }

                        bool isAi = (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                                    ModCompatManager.IsAmbientAITrain(car) ||
                                    ModCompatManager.IsAmbientAITrainId(car.ID) ||
                                    car.GetComponent<AIEngineer>() != null ||
                                    TrainSpawner.IsCarSpawning(car) ||
                                    TrainSpawner.IsCarSpawning(car.ID);

                        if (isAi)
                        {
                            carsToDelete.Add(car);
                        }
                    }

                    if (carsToDelete.Count > 0)
                    {
                        purgedCount += carsToDelete.Count;
                        for (int i = 0; i < carsToDelete.Count; i++)
                        {
                            var c = carsToDelete[i];
                            if (c == null) continue;

                            var eng = c.GetComponent<AIEngineer>();
                            if (eng != null)
                            {
                                eng.EmergencyBrake();
                                eng.ReleaseAllSignalReservations();
                                if (AITraffic.Navigation.JunctionController.Instance != null)
                                    AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(eng);
                                if (AITraffic.Navigation.RailGraph.Instance != null)
                                    AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(eng);
                                UnityEngine.Object.Destroy(eng);
                            }

                            if (c.trainset != null)
                            {
                                ModCompatManager.UntagTrain(c.trainset);
                            }
                            if (c.gameObject != null)
                            {
                                var marker = c.gameObject.GetComponent<AITrafficCarMarker>();
                                if (marker != null) UnityEngine.Object.Destroy(marker);
                            }
                        }

                        CarSpawner.Instance.DeleteTrainCars(carsToDelete, forceInstantDestroy: true);
                    }
                }

                // 3. Clear traffic scheduler caches and debts
                TrafficScheduler.ClearCaches();
                try
                {
                    AIDebtPatches.ScrubActiveAIDebts();
                }
                catch { }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] World AI car purge complete. Cleaned up {0} cars.", purgedCount));

                return purgedCount;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in PurgeAllWorldAICars: {0}", ex));
                return purgedCount;
            }
        }

        /// <summary>
        /// Explicit manual debug tool: deletes physically derailed rolling stock that has no active job
        /// and is not occupied by or coupled to the player. Never called automatically.
        /// </summary>
        public int PurgeDerailedGhostCars()
        {
            int purgedCount = 0;
            try
            {
                if (CarSpawner.Instance == null || CarSpawner.Instance.AllCars == null) return 0;
                TrainCar playerCar = PlayerManager.Car;
                var allCars = CarSpawner.Instance.AllCars;
                List<TrainCar> toDelete = new List<TrainCar>();

                for (int i = 0; i < allCars.Count; i++)
                {
                    var car = allCars[i];
                    if (car == null) continue;

                    // Strictly protect player's locomotive and coupled consist
                    if (playerCar != null && (car == playerCar || (car.trainset != null && car.trainset.cars != null && car.trainset.cars.Contains(playerCar))))
                        continue;

                    // Strictly protect any car involved in a player worker task
                    if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                        continue;

                    // Strictly protect cars with an active or pending station job
                    if (HasActiveOrPendingJob(car))
                        continue;

                    // Only target AI-related ghost cars that derailed:
                    // (has AI marker, registered as ambient, has AIEngineer, active spawning, or has preventDebtDisplay
                    // which is exclusively set on AI traffic cars, never on player/station/SelfShunt equipment)
                    bool isAiGhost = (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                                     ModCompatManager.IsAmbientAITrain(car) ||
                                     ModCompatManager.IsAmbientAITrainId(car.ID) ||
                                     car.GetComponent<AIEngineer>() != null ||
                                     TrainSpawner.IsCarSpawning(car) ||
                                     TrainSpawner.IsCarSpawning(car.ID) ||
                                     (car.playerSpawnedCar && car.preventDebtDisplay);

                    // Target cars that are physically derailed and identified as AI ghost cars
                    if (car.derailed && isAiGhost)
                    {
                        toDelete.Add(car);
                    }
                }

                if (toDelete.Count > 0)
                {
                    purgedCount = toDelete.Count;
                    for (int i = 0; i < toDelete.Count; i++)
                    {
                        var c = toDelete[i];
                        if (c == null) continue;
                        if (c.trainset != null) ModCompatManager.UntagTrain(c.trainset);
                        if (c.gameObject != null)
                        {
                            var marker = c.gameObject.GetComponent<AITrafficCarMarker>();
                            if (marker != null) UnityEngine.Object.Destroy(marker);
                        }
                    }
                    CarSpawner.Instance.DeleteTrainCars(toDelete, forceInstantDestroy: true);
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Manual derailed ghost car purge complete: Cleaned up {0} derailed unassigned cars.", purgedCount));

                return purgedCount;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in PurgeDerailedGhostCars: {0}", ex));
                return purgedCount;
            }
        }

        private static bool HasActiveOrPendingJob(TrainCar car)
        {
            if (car == null || car.logicCar == null) return false;
            try
            {
                if (DV.Logic.Job.JobsManager.Instance != null)
                {
                    return DV.Logic.Job.JobsManager.Instance.GetJobOfCar(car.logicCar, false) != null;
                }
            }
            catch {}
            return false;
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

        private static Material s_outlineMaterial;
        private static bool s_isNightOutline = false;
        private static float s_dayNightCheckTimer = 0f;

        private static bool CheckIsNightTime()
        {
            try
            {
                if (DV.WeatherSystem.WeatherDriver.Instance != null)
                {
                    return !DV.WeatherSystem.WeatherDriver.Instance.IsDay;
                }
            }
            catch {}

            try
            {
                var sun = RenderSettings.sun;
                if (sun != null)
                {
                    if (sun.transform.forward.y >= -0.05f || sun.intensity < 0.15f)
                        return true;
                }
                else if (RenderSettings.ambientLight.grayscale < 0.20f)
                {
                    return true;
                }
            }
            catch {}

            return false;
        }

        private static void UpdateOutlineMaterialColor()
        {
            if (s_outlineMaterial != null && s_outlineMaterial.HasProperty("_Color"))
            {
                s_outlineMaterial.color = s_isNightOutline
                    ? new Color(0.96f, 0.96f, 0.96f, 0.95f)
                    : new Color(0.04f, 0.04f, 0.04f, 0.95f);
            }
        }

        private static Material GetOutlineMaterial()
        {
            if (s_outlineMaterial != null)
                return s_outlineMaterial;

            if (s_routeShader == null)
            {
                GetPathMaterial(0);
            }

            if (s_routeShader != null)
            {
                s_outlineMaterial = new Material(s_routeShader);
                s_isNightOutline = CheckIsNightTime();
                UpdateOutlineMaterialColor();
                return s_outlineMaterial;
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
            public readonly Mesh ConeMesh = new Mesh();
            public readonly List<Vector3> ConeVertices = new List<Vector3>();
            public readonly List<int> ColoredTriangles = new List<int>();
            public readonly List<int> BlackTriangles = new List<int>();
            public readonly List<Color> ConeColors = new List<Color>();
        }

        private readonly List<VisualizerTrackCache> _visualizerCaches = new List<VisualizerTrackCache>();
        private readonly List<LineRenderer> _routeLineRenderers = new List<LineRenderer>();
        private readonly List<MeshFilter> _routeConeFilters = new List<MeshFilter>();
        private readonly List<MeshRenderer> _routeConeRenderers = new List<MeshRenderer>();

        private void LateUpdate()
        {
            CheckFloatingOriginShift();
            Update3DRouteVisualizer();

            bool ctrlPressed = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            bool shiftPressed = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // Toggle Master Debug Monitor HUD with Ctrl + Shift + D
            if (ctrlPressed && shiftPressed && Input.GetKeyDown(KeyCode.D))
            {
                if (_settings != null)
                {
                    _settings.DebugVisuals = !_settings.DebugVisuals;
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    {
                        Main.ModEntry.Logger.Log(string.Format("[TrafficManager] Debug HUD toggled via hotkey (Ctrl+Shift+D): {0}",
                            _settings.DebugVisuals ? "Enabled" : "Disabled"));
                    }
                }
            }

            // Hotkey 'K' to despawn stalled ambient AI train
            if (Input.GetKeyDown(KeyCode.K) && _stalledAmbientEngineers.Count > 0)
            {
                DespawnFirstStalledAmbientTrain();
            }

#if DEBUG
            if (ctrlPressed && shiftPressed && Input.GetKeyDown(KeyCode.M))
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
                    if (i < _routeConeRenderers.Count && _routeConeRenderers[i] != null)
                        _routeConeRenderers[i].enabled = false;
                }
                return;
            }

            // Check day/night transition periodically (twice per second) to update outline color (black by day, white by night)
            s_dayNightCheckTimer += Time.deltaTime;
            if (s_dayNightCheckTimer >= 0.5f)
            {
                s_dayNightCheckTimer = 0f;
                bool isNight = CheckIsNightTime();
                if (isNight != s_isNightOutline)
                {
                    s_isNightOutline = isNight;
                    UpdateOutlineMaterialColor();
                }
            }

            // Maintain LineRenderer, cone mesh, and cache pool for active engineers
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

                var coneObj = new GameObject("Cones");
                coneObj.transform.SetParent(lineObj.transform, false);
                coneObj.layer = 0;
                var mf = coneObj.AddComponent<MeshFilter>();
                var mr = coneObj.AddComponent<MeshRenderer>();
                var outlineMatInit = GetOutlineMaterial();
                if (mat != null && outlineMatInit != null)
                {
                    mr.sharedMaterials = new Material[] { mat, outlineMatInit };
                }
                else if (mat != null)
                {
                    mr.sharedMaterial = mat;
                }
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                _routeConeFilters.Add(mf);
                _routeConeRenderers.Add(mr);

                var cache = new VisualizerTrackCache();
                mf.sharedMesh = cache.ConeMesh;
                _visualizerCaches.Add(cache);
            }

            for (int i = 0; i < _routeLineRenderers.Count; i++)
            {
                var lr = _routeLineRenderers[i];
                var mr = i < _routeConeRenderers.Count ? _routeConeRenderers[i] : null;
                var cache = _visualizerCaches[i];
                if (i >= _activeEngineers.Count)
                {
                    if (lr != null) lr.enabled = false;
                    if (mr != null) mr.enabled = false;
                    cache.Engineer = null;
                    cache.TrackIndex = -1;
                    cache.Path = null;
                    continue;
                }

                var eng = _activeEngineers[i];
                if (eng == null || eng.TrainCar == null || eng.CurrentPath == null || eng.CurrentPath.Tracks == null || eng.CurrentPath.Tracks.Count == 0)
                {
                    if (lr != null) lr.enabled = false;
                    if (mr != null) mr.enabled = false;
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
                Material outlineMat = GetOutlineMaterial();
                if (pathMat != null)
                {
                    if (lr.sharedMaterial != pathMat) lr.sharedMaterial = pathMat;
                    if (mr != null)
                    {
                        var curMats = mr.sharedMaterials;
                        if (curMats == null || curMats.Length != 2 || curMats[0] != pathMat || curMats[1] != outlineMat)
                        {
                            if (outlineMat != null)
                                mr.sharedMaterials = new Material[] { pathMat, outlineMat };
                            else
                                mr.sharedMaterial = pathMat;
                        }
                    }
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

                    GeneratePathCones(cache, i < _routeConeFilters.Count ? _routeConeFilters[i] : null, pathColor);
                }

                // Crucial visibility fix: Ensure this runs ALWAYS, so toggling visuals or caching never leaves lr.enabled == false
                if (cache.PointsArray != null && cache.PointsArray.Length > 1)
                {
                    if (!lr.gameObject.activeSelf) lr.gameObject.SetActive(true);
                    if (!lr.enabled) lr.enabled = true;
                    if (mr != null && !mr.enabled) mr.enabled = true;
                }
                else
                {
                    if (lr.enabled) lr.enabled = false;
                    if (mr != null && mr.enabled) mr.enabled = false;
                }
            }
        }

        private static void GeneratePathCones(VisualizerTrackCache cache, MeshFilter mf, Color pathColor)
        {
            if (cache == null || cache.ConeMesh == null) return;

            cache.ConeMesh.Clear();
            cache.ConeVertices.Clear();
            cache.ColoredTriangles.Clear();
            cache.BlackTriangles.Clear();
            cache.ConeColors.Clear();

            if (cache.Points == null || cache.Points.Count < 2) return;

            Transform filterTransform = mf != null ? mf.transform : null;

            float totalDistance = 0f;
            for (int p = 0; p < cache.Points.Count - 1; p++)
            {
                totalDistance += Vector3.Distance(cache.Points[p], cache.Points[p + 1]);
            }

            if (totalDistance < 5f) return;

            const float coneInterval = 40f;
            const float coneLength = 2.4f;
            const float coneRadius = 0.55f;
            const float ridgeWidth = 0.08f;
            const int sides = 6;

            float nextConeDist = totalDistance < 40f ? totalDistance * 0.5f : 20f;
            float accumulatedDist = 0f;

            for (int p = 0; p < cache.Points.Count - 1; p++)
            {
                Vector3 p0 = cache.Points[p];
                Vector3 p1 = cache.Points[p + 1];
                float segLen = Vector3.Distance(p0, p1);
                if (segLen < 0.001f) continue;

                while (accumulatedDist + segLen >= nextConeDist && nextConeDist <= totalDistance)
                {
                    float t = Mathf.Clamp01((nextConeDist - accumulatedDist) / segLen);
                    Vector3 worldPos = Vector3.Lerp(p0, p1, t);
                    Vector3 worldDir = (p1 - p0).normalized;

                    Vector3 localPos = filterTransform != null ? filterTransform.InverseTransformPoint(worldPos) : worldPos;
                    Vector3 localDir = filterTransform != null ? filterTransform.InverseTransformDirection(worldDir).normalized : worldDir;

                    Vector3 up = Vector3.up;
                    if (filterTransform != null) up = filterTransform.InverseTransformDirection(Vector3.up).normalized;
                    if (Mathf.Abs(Vector3.Dot(localDir, up)) > 0.90f)
                    {
                        up = filterTransform != null ? filterTransform.InverseTransformDirection(Vector3.forward).normalized : Vector3.forward;
                    }

                    Vector3 right = Vector3.Cross(up, localDir).normalized;
                    up = Vector3.Cross(localDir, right).normalized;

                    float progress = Mathf.Clamp01(nextConeDist / totalDistance);
                    Color coneCol = new Color(pathColor.r, pathColor.g, pathColor.b, Mathf.Lerp(0.95f, 0.40f, progress));
                    Color outlineCol = new Color(1.0f, 1.0f, 1.0f, 0.95f);

                    float halfLen = coneLength * 0.5f;
                    Vector3 tip = localPos + localDir * halfLen;
                    Vector3 baseCenter = localPos - localDir * halfLen;

                    // Perimeter ring points on base circle
                    Vector3[] ringPts = new Vector3[sides];
                    Vector3[] outVecs = new Vector3[sides];
                    for (int s = 0; s < sides; s++)
                    {
                        float angle = (s * Mathf.PI * 2f) / sides;
                        Vector3 radDir = (right * Mathf.Cos(angle) + up * Mathf.Sin(angle)).normalized;
                        ringPts[s] = baseCenter + radDir * coneRadius;
                        outVecs[s] = radDir;
                    }

                    // 1. Submesh 0: Colored Mantle Facets (6 facets)
                    for (int s = 0; s < sides; s++)
                    {
                        int next = (s + 1) % sides;
                        Vector3 pCurr = ringPts[s];
                        Vector3 pNext = ringPts[next];

                        int vStart = cache.ConeVertices.Count;
                        cache.ConeVertices.Add(tip);
                        cache.ConeVertices.Add(pCurr);
                        cache.ConeVertices.Add(pNext);

                        cache.ConeColors.Add(coneCol);
                        cache.ConeColors.Add(coneCol);
                        cache.ConeColors.Add(coneCol);

                        // Double-sided mantle facet
                        cache.ColoredTriangles.Add(vStart);
                        cache.ColoredTriangles.Add(vStart + 1);
                        cache.ColoredTriangles.Add(vStart + 2);

                        cache.ColoredTriangles.Add(vStart);
                        cache.ColoredTriangles.Add(vStart + 2);
                        cache.ColoredTriangles.Add(vStart + 1);
                    }

                    // 2. Submesh 1: Black Outlines, Base Cap, and Ridge Ribs
                    // (a) Base Cap (solid black rear face)
                    int baseCenterIdx = cache.ConeVertices.Count;
                    cache.ConeVertices.Add(baseCenter);
                    cache.ConeColors.Add(outlineCol);

                    int ringStartIdx = cache.ConeVertices.Count;
                    for (int s = 0; s < sides; s++)
                    {
                        cache.ConeVertices.Add(ringPts[s]);
                        cache.ConeColors.Add(outlineCol);
                    }

                    for (int s = 0; s < sides; s++)
                    {
                        int curr = ringStartIdx + s;
                        int next = ringStartIdx + ((s + 1) % sides);

                        // Double-sided base cap triangle
                        cache.BlackTriangles.Add(baseCenterIdx);
                        cache.BlackTriangles.Add(curr);
                        cache.BlackTriangles.Add(next);

                        cache.BlackTriangles.Add(baseCenterIdx);
                        cache.BlackTriangles.Add(next);
                        cache.BlackTriangles.Add(curr);
                    }

                    // (b) 6 Black Ridge Ribs (from tip to each base vertex)
                    const float halfRibW = ridgeWidth * 0.5f;
                    for (int s = 0; s < sides; s++)
                    {
                        Vector3 edgeVec = ringPts[s] - tip;
                        Vector3 outNorm = outVecs[s];
                        Vector3 binorm = Vector3.Cross(edgeVec, outNorm).normalized;

                        Vector3 eTip = tip + outNorm * 0.015f;
                        Vector3 eBase = ringPts[s] + outNorm * 0.015f;

                        Vector3 r0 = eTip - binorm * halfRibW;
                        Vector3 r1 = eTip + binorm * halfRibW;
                        Vector3 r2 = eBase + binorm * halfRibW;
                        Vector3 r3 = eBase - binorm * halfRibW;

                        int ribStart = cache.ConeVertices.Count;
                        cache.ConeVertices.Add(r0);
                        cache.ConeVertices.Add(r1);
                        cache.ConeVertices.Add(r2);
                        cache.ConeVertices.Add(r3);

                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);

                        // Double-sided quad
                        cache.BlackTriangles.Add(ribStart);
                        cache.BlackTriangles.Add(ribStart + 1);
                        cache.BlackTriangles.Add(ribStart + 2);

                        cache.BlackTriangles.Add(ribStart);
                        cache.BlackTriangles.Add(ribStart + 2);
                        cache.BlackTriangles.Add(ribStart + 1);

                        cache.BlackTriangles.Add(ribStart);
                        cache.BlackTriangles.Add(ribStart + 2);
                        cache.BlackTriangles.Add(ribStart + 3);

                        cache.BlackTriangles.Add(ribStart);
                        cache.BlackTriangles.Add(ribStart + 3);
                        cache.BlackTriangles.Add(ribStart + 2);
                    }

                    // (c) Black Base Rim Collar (wrapping around the base)
                    for (int s = 0; s < sides; s++)
                    {
                        int next = (s + 1) % sides;
                        Vector3 pCurr = ringPts[s];
                        Vector3 pNext = ringPts[next];

                        Vector3 pCurrFwd = Vector3.Lerp(pCurr, tip, 0.18f) + outVecs[s] * 0.012f;
                        Vector3 pNextFwd = Vector3.Lerp(pNext, tip, 0.18f) + outVecs[next] * 0.012f;
                        Vector3 pCurrElev = pCurr + outVecs[s] * 0.012f;
                        Vector3 pNextElev = pNext + outVecs[next] * 0.012f;

                        int rimStart = cache.ConeVertices.Count;
                        cache.ConeVertices.Add(pCurrElev);
                        cache.ConeVertices.Add(pNextElev);
                        cache.ConeVertices.Add(pNextFwd);
                        cache.ConeVertices.Add(pCurrFwd);

                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);
                        cache.ConeColors.Add(outlineCol);

                        // Double-sided quad
                        cache.BlackTriangles.Add(rimStart);
                        cache.BlackTriangles.Add(rimStart + 1);
                        cache.BlackTriangles.Add(rimStart + 2);

                        cache.BlackTriangles.Add(rimStart);
                        cache.BlackTriangles.Add(rimStart + 2);
                        cache.BlackTriangles.Add(rimStart + 1);

                        cache.BlackTriangles.Add(rimStart);
                        cache.BlackTriangles.Add(rimStart + 2);
                        cache.BlackTriangles.Add(rimStart + 3);

                        cache.BlackTriangles.Add(rimStart);
                        cache.BlackTriangles.Add(rimStart + 3);
                        cache.BlackTriangles.Add(rimStart + 2);
                    }

                    nextConeDist += coneInterval;
                }

                accumulatedDist += segLen;
            }

            if (cache.ConeVertices.Count > 0)
            {
                cache.ConeMesh.subMeshCount = 2;
                cache.ConeMesh.SetVertices(cache.ConeVertices);
                cache.ConeMesh.SetColors(cache.ConeColors);
                cache.ConeMesh.SetTriangles(cache.ColoredTriangles, 0);
                cache.ConeMesh.SetTriangles(cache.BlackTriangles, 1);
                cache.ConeMesh.RecalculateNormals();
                cache.ConeMesh.RecalculateBounds();
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

            if (_settings != null && _settings.Density == TrafficDensity.Off && !_settings.DebugVisuals)
                return;

            InitStyles();

            // 1. Draw Master Debug Monitor (HUD)
            bool showHud = _settings != null && _settings.DebugVisuals;
            if (showHud)
            {
                float screenW = Screen.width;
                float screenH = Screen.height;
                float boxWidth = _showWorkerDispatcher ? 760f : 740f;
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
                if (_settings != null && GUILayout.Button("✕", GUILayout.Width(22)))
                {
                    _settings.DebugVisuals = false;
                }
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
                    int purged = PurgeAllWorldAICars(forceAll: false);
                    _lastDispatchStatus = string.Format("<color=#00FF88>Despawned all AI trains ({0} cars cleared).</color>", purged);
                }
                if (GUILayout.Button("🧹 Purge AI Cars", GUILayout.Height(22)))
                {
                    int purged = PurgeAllWorldAICars(forceAll: false);
                    _lastDispatchStatus = string.Format("<color=#00FF88>Purged {0} AI cars from world.</color>", purged);
                }
                if (GUILayout.Button("💥 Purge Derailed", GUILayout.Height(22)))
                {
                    int purged = PurgeDerailedGhostCars();
                    _lastDispatchStatus = string.Format("<color=#FF5555>Purged {0} derailed unassigned ghost cars.</color>", purged);
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
                        string hillStr = "";
                        if (eng.HillStage == AIEngineer.HillLaunchStage.Stage1_Clean)
                            hillStr = string.Format(" | <color=#FFD700><b>[HILL: Clean (Eq:{0:F0}%)]</b></color>", eng.HillEquilibriumThrottle * 100f);
                        else if (eng.HillStage == AIEngineer.HillLaunchStage.Stage2_Sanded)
                            hillStr = string.Format(" | <color=#FFA500><b>[HILL: Sanded (Eq:{0:F0}%)]</b></color>", eng.HillEquilibriumThrottle * 100f);
                        else if (eng.HillStage == AIEngineer.HillLaunchStage.RollbackDescent)
                            hillStr = " | <color=#FF6600><b>[HILL: Rollback Descent]</b></color>";
                        else if (eng.HillStage == AIEngineer.HillLaunchStage.MomentumRunUp)
                            hillStr = " | <color=#00FFFF><b>[HILL: Momentum Run-Up]</b></color>";
                        else if (eng.HillStage == AIEngineer.HillLaunchStage.PermanentlyStalled)
                            hillStr = " | <color=#FF0000><b>[PERMANENTLY STALLED]</b></color>";
                        else if (eng.IsHillStarting)
                            hillStr = " | <color=#FFD700><b>[HILL START]</b></color>";

                        if (!string.IsNullOrEmpty(thermalStr) || !string.IsNullOrEmpty(ampsStr) || eng.IsWheelSlipping || eng.IsRollbackDetected || eng.IsHillStarting || eng.HillStage != AIEngineer.HillLaunchStage.None)
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
