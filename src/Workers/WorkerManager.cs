using System;
using System.Collections.Generic;
using UnityEngine;
using DV.Simulation.Cars;
using DV.InventorySystem;
using AITraffic.Driver;
using AITraffic.Fleet;
using AITraffic.Navigation;
using AITraffic.Compat;
using AITraffic.Core;

namespace AITraffic.Workers
{
    /// <summary>
    /// Coordinates player-employed AI workers, handles hiring fees, route assignment,
    /// delivery monitoring, and UI notifications.
    /// </summary>
    public class WorkerManager
    {
        private static WorkerManager s_instance;
        public static WorkerManager Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new WorkerManager();
                }
                return s_instance;
            }
        }

        public List<AtoBHaulTask> ActiveTasks { get; private set; }
        public List<AtoBHaulTask> CompletedTasks { get; private set; }

        // On-screen toast notification state
        private string _toastMessage = string.Empty;
        private float _toastExpireTime = 0f;
        private const float ToastDuration = 7.0f;
        private GUIStyle _toastBoxStyle;
        private GUIStyle _toastTextStyle;
        private GUIStyle _toastHeaderStyle;
        private bool _toastStylesInitialized = false;

        public WorkerManager()
        {
            s_instance = this;
            ActiveTasks = new List<AtoBHaulTask>();
            CompletedTasks = new List<AtoBHaulTask>();
        }

        #region Consist & Train Inspection

        /// <summary>
        /// Detects the locomotive the player is currently operating, riding on, or standing nearest to.
        /// </summary>
        public TrainCar GetPlayerSelectedLocomotive()
        {
            // 1. Current car occupied by player
            if (PlayerManager.Car != null)
            {
                if (PlayerManager.Car.IsLoco && TrainSpawner.IsSupportedAILocomotive(PlayerManager.Car))
                {
                    return PlayerManager.Car;
                }

                // If player is on a wagon in a consist, find the locomotive in that trainset
                if (PlayerManager.Car.trainset != null && PlayerManager.Car.trainset.cars != null)
                {
                    for (int i = 0; i < PlayerManager.Car.trainset.cars.Count; i++)
                    {
                        var c = PlayerManager.Car.trainset.cars[i];
                        if (c != null && c.IsLoco && TrainSpawner.IsSupportedAILocomotive(c))
                        {
                            return c;
                        }
                    }
                }
            }

            // 2. Last operated locomotive
            if (PlayerManager.LastLoco != null && TrainSpawner.IsSupportedAILocomotive(PlayerManager.LastLoco))
            {
                return PlayerManager.LastLoco;
            }

            // 3. Proximity search: find nearest locomotive within 60m of player
            Vector3 playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;
            if (playerPos != Vector3.zero && CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
            {
                TrainCar nearestLoco = null;
                float nearestDistSq = 60f * 60f;

                var allCars = CarSpawner.Instance.AllCars;
                for (int i = 0; i < allCars.Count; i++)
                {
                    var car = allCars[i];
                    if (car != null && car.IsLoco && TrainSpawner.IsSupportedAILocomotive(car))
                    {
                        float distSq = (car.transform.position - playerPos).sqrMagnitude;
                        if (distSq < nearestDistSq)
                        {
                            nearestDistSq = distSq;
                            nearestLoco = car;
                        }
                    }
                }

                if (nearestLoco != null)
                {
                    return nearestLoco;
                }
            }

            return null;
        }

        /// <summary>
        /// Gathers full consist details, car count, total physical length, and total weight.
        /// </summary>
        public void GetConsistMetrics(TrainCar loco, out List<TrainCar> fullConsist, out float totalLengthMeters, out float totalMassTons)
        {
            fullConsist = new List<TrainCar>();
            totalLengthMeters = 0f;
            totalMassTons = 0f;

            if (loco == null) return;

            if (loco.trainset != null && loco.trainset.cars != null)
            {
                fullConsist.AddRange(loco.trainset.cars);
            }
            else
            {
                fullConsist.Add(loco);
            }

            for (int i = 0; i < fullConsist.Count; i++)
            {
                var car = fullConsist[i];
                if (car == null) continue;

                float carLen = car.InterCouplerDistance > 0f ? car.InterCouplerDistance : 15f;
                totalLengthMeters += carLen;

                float massKg = (car.massController != null && car.massController.TotalMass > 0f) ? car.massController.TotalMass : 40000f;
                totalMassTons += massKg / 1000f;
            }
        }

        /// <summary>
        /// Finds the RailTrack currently occupied by the train's lead bogie.
        /// </summary>
        public RailTrack GetCurrentTrack(TrainCar car)
        {
            if (car == null) return null;
            if (car.FrontBogie != null && car.FrontBogie.track != null) return car.FrontBogie.track;
            if (car.RearBogie != null && car.RearBogie.track != null) return car.RearBogie.track;
            return null;
        }

        /// <summary>
        /// Identifies the closest StationController to the given position.
        /// </summary>
        public StationController GetNearestStation(Vector3 position)
        {
            if (StationController.allStations == null || StationController.allStations.Count == 0)
                return null;

            StationController nearest = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < StationController.allStations.Count; i++)
            {
                var station = StationController.allStations[i];
                if (station == null || station.stationInfo == null) continue;

                float distSq = (station.transform.position - position).sqrMagnitude;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    nearest = station;
                }
            }

            return nearest;
        }

        #endregion

        #region Route & Destination Evaluation

        /// <summary>
        /// Automatically selects the best available arrival track at the destination station
        /// that has sufficient physical length to clear the entire consist and is currently unoccupied.
        /// </summary>
        public RailTrack SelectBestArrivalTrack(StationController station, float requiredLength, RailTrack originTrack, TrainCar leadLoco = null)
        {
            if (station == null || station.AllStationTracks == null || station.AllStationTracks.Count == 0)
                return null;

            List<RailTrack> candidates = new List<RailTrack>();

            // 1. Prefer designated transferIn (inbound receiving) tracks
            if (station.transferInRailtracksGONames != null && station.transferInRailtracksGONames.Count > 0)
            {
                for (int i = 0; i < station.AllStationTracks.Count; i++)
                {
                    var track = station.AllStationTracks[i];
                    if (track == null || track.curve == null) continue;

                    if (station.transferInRailtracksGONames.Contains(track.name))
                    {
                        candidates.Add(track);
                    }
                }
            }

            // 2. Fallback to storage sidings
            if (candidates.Count == 0 && station.storageRailtracksGONames != null)
            {
                for (int i = 0; i < station.AllStationTracks.Count; i++)
                {
                    var track = station.AllStationTracks[i];
                    if (track == null || track.curve == null) continue;

                    if (station.storageRailtracksGONames.Contains(track.name))
                    {
                        candidates.Add(track);
                    }
                }
            }

            // 3. Fallback to any station track
            if (candidates.Count == 0)
            {
                candidates.AddRange(station.AllStationTracks);
            }

            // Filter candidates: track length must accommodate consist + 25m buffer, and must not be occupied
            RailTrack bestCandidate = null;
            float bufferLength = requiredLength + 25f;
            var pathfinder = new Pathfinder(RailGraph.Instance);

            var pathOptions = new PathfinderOptions
            {
                Requester = leadLoco,
                RequesterTrainset = leadLoco != null ? leadLoco.trainset : null,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = false,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            // Pass 1: Length >= bufferLength and strictly unoccupied
            for (int i = 0; i < candidates.Count; i++)
            {
                var track = candidates[i];
                if (track == null || track.curve == null) continue;

                if (track.curve.length >= bufferLength && !TrafficScheduler.IsTrackOccupied(track))
                {
                    // Check if Pathfinder can find a route to this track
                    if (originTrack != null)
                    {
                        var testPath = pathfinder.FindPath(originTrack, track, pathOptions);
                        if (testPath != null && testPath.IsValid)
                        {
                            bestCandidate = track;
                            break;
                        }
                    }
                    else
                    {
                        bestCandidate = track;
                        break;
                    }
                }
            }

            // Pass 2: If no track met strict buffer length, pick any unoccupied track that has a path
            if (bestCandidate == null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var track = candidates[i];
                    if (track == null || track.curve == null) continue;

                    if (!TrafficScheduler.IsTrackOccupied(track))
                    {
                        if (originTrack != null)
                        {
                            var testPath = pathfinder.FindPath(originTrack, track, pathOptions);
                            if (testPath != null && testPath.IsValid)
                            {
                                bestCandidate = track;
                                break;
                            }
                        }
                    }
                }
            }

            // Pass 3: Fallback if all yard tracks have rolling stock/cars, pick any candidate with length >= bufferLength that has a path
            if (bestCandidate == null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var track = candidates[i];
                    if (track == null || track.curve == null) continue;

                    if (track.curve.length >= bufferLength)
                    {
                        if (originTrack != null)
                        {
                            var testPath = pathfinder.FindPath(originTrack, track, pathOptions);
                            if (testPath != null && testPath.IsValid)
                            {
                                bestCandidate = track;
                                break;
                            }
                        }
                    }
                }
            }

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
            {
                string origName = originTrack != null ? originTrack.name : "null";
                string stName = station != null && station.stationInfo != null ? station.stationInfo.Name : "null";
                Main.ModEntry.Logger.Log(string.Format("[WorkerManager] SelectBestArrivalTrack for {0} (ConsistLen: {1:F0}m, Orig: {2}, Candidates: {3}) -> Result: {4}.",
                    stName, requiredLength, origName, candidates.Count, bestCandidate != null ? bestCandidate.name : "NONE"));
            }

            return bestCandidate;
        }

        /// <summary>
        /// Calculates a realistic hiring wage based on distance and consist weight.
        /// </summary>
        public double CalculateHiringFee(float routeDistanceMeters, float consistMassTons)
        {
            double baseFee = 300.0;
            double distanceFee = (routeDistanceMeters / 1000.0) * 60.0; // $60 per km
            double massFee = consistMassTons * 0.35; // $35 per 100 tons

            double total = baseFee + distanceFee + massFee;
            return Math.Round(total / 10.0) * 10.0; // Round to nearest $10
        }

        #endregion

        #region Worker Dispatch & Task Management

        /// <summary>
        /// Employs an AI driver for the specified train and initiates an autonomous A-to-B mainline haul.
        /// </summary>
        public bool HireDriverForAtoB(TrainCar leadLoco, StationController destStation, RailTrack specificDestTrack, out string statusMessage)
        {
            statusMessage = string.Empty;

            if (leadLoco == null)
            {
                statusMessage = "No locomotive selected or nearby!";
                return false;
            }

            if (!leadLoco.IsLoco || !TrainSpawner.IsSupportedAILocomotive(leadLoco))
            {
                statusMessage = string.Format("Locomotive '{0}' is not supported for autonomous driving.", leadLoco.ID);
                return false;
            }

            if (destStation == null)
            {
                statusMessage = "Destination station not selected!";
                return false;
            }

            // Check if train is already under worker assignment
            for (int i = 0; i < ActiveTasks.Count; i++)
            {
                if (ActiveTasks[i].LeadLocomotive == leadLoco)
                {
                    statusMessage = "This locomotive already has an active AI driver assigned!";
                    return false;
                }
            }

            // 1. Determine consist metrics
            List<TrainCar> consist;
            float totalLength;
            float totalMass;
            GetConsistMetrics(leadLoco, out consist, out totalLength, out totalMass);

            // 2. Identify origin track & station
            RailTrack originTrack = GetCurrentTrack(leadLoco);
            if (originTrack == null)
            {
                statusMessage = "Locomotive is not sitting on a valid rail track!";
                return false;
            }

            StationController originStation = GetNearestStation(leadLoco.transform.position);

            // 3. Resolve destination track
            RailTrack destTrack = specificDestTrack;
            bool isAutoTrack = (destTrack == null);
            if (isAutoTrack)
            {
                destTrack = SelectBestArrivalTrack(destStation, totalLength, originTrack, leadLoco);
            }

            if (destTrack == null)
            {
                statusMessage = string.Format("No suitable arrival track found at {0} with length >= {1:F0}m!",
                    destStation.stationInfo != null ? destStation.stationInfo.Name : "destination", totalLength + 25f);
                return false;
            }

            if (destTrack == originTrack)
            {
                statusMessage = "Train is already sitting on the destination track!";
                return false;
            }

            // 4. Compute mainline route via Pathfinder
            var pathfinder = new Pathfinder(RailGraph.Instance);
            var pathOptions = new PathfinderOptions
            {
                Requester = leadLoco,
                RequesterTrainset = leadLoco != null ? leadLoco.trainset : null,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = false,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            var routePath = pathfinder.FindPath(originTrack, destTrack, pathOptions);
            if (routePath == null || !routePath.IsValid || routePath.Tracks == null || routePath.Tracks.Count < 2)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[WorkerManager] HireDriverForAtoB route calculation failed from '{0}' to '{1}'.",
                        originTrack != null ? originTrack.name : "null", destTrack != null ? destTrack.name : "null"));
                }
                statusMessage = string.Format("Could not calculate a clear route to {0} track '{1}'. Switches may be blocked.",
                    destStation.stationInfo != null ? destStation.stationInfo.Name : "destination", destTrack.name);
                return false;
            }

            // 5. Calculate fee & check player funds
            double fee = CalculateHiringFee(routePath.TotalDistance, totalMass);
            double playerMoney = Inventory.Instance != null ? Inventory.Instance.PlayerMoney : 0.0;

            if (playerMoney < fee)
            {
                statusMessage = string.Format("Insufficient funds! Cost: ${0:N0} (Wallet: ${1:N0})", fee, playerMoney);
                return false;
            }

            // 6. Deduct hiring payment
            if (Inventory.Instance != null)
            {
                Inventory.Instance.RemoveMoney(fee);
            }

            // 7. Mechanical configuration
            // Ensure couplers connected and angle cocks open across all consist cars
            TrainSpawner.ConfigureConsistCouplers(consist);

            // Release handbrakes across the entire consist
            for (int i = 0; i < consist.Count; i++)
            {
                var car = consist[i];
                if (car == null) continue;

                var controls = car.GetComponent<BaseControlsOverrider>();
                if (controls != null && controls.Handbrake != null)
                {
                    controls.Handbrake.Set(0f);
                }

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    TrainSpawner.ApplyAIDamageImmunity(car, true);
                }
            }

            // Start up engine prime mover and electronics
            TrainSpawner.InitializeLocomotive(leadLoco);

            // 8. Attach and initialize AIEngineer
            var engineer = leadLoco.gameObject.GetComponent<AIEngineer>();
            if (engineer == null)
            {
                engineer = leadLoco.gameObject.AddComponent<AIEngineer>();
            }

            engineer.IsWorkerDriven = true;
            engineer.CurrentPath = routePath;
            engineer.DistanceToDestination = routePath.TotalDistance;
            engineer.OriginStationName = originStation != null && originStation.stationInfo != null ? originStation.stationInfo.Name : "Yard";
            engineer.DestinationStationName = destStation.stationInfo != null ? destStation.stationInfo.Name : "Destination";
            engineer.DestinationTrackName = destTrack.name;
            engineer.IsStationDestination = false;
            engineer.IsTerminusDestination = true;

            // Hook terminus arrival event
            engineer.OnTerminusArrival += HandleTaskTerminusArrival;

            // Register with TrafficManager
            TrafficManager.Instance.RegisterEngineer(engineer);

            // Tag trainset for AI traffic
            if (leadLoco.trainset != null)
            {
                ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
            }

            // 9. Create task record
            var task = new AtoBHaulTask(
                null,
                leadLoco,
                consist,
                originStation,
                originTrack,
                destStation,
                destTrack,
                isAutoTrack,
                fee,
                routePath.TotalDistance,
                engineer
            );

            ActiveTasks.Add(task);

            string toastMsg = string.Format("AI Driver Hired (${0:N0})! Hauling to {1} on track '{2}'.",
                fee, engineer.DestinationStationName, destTrack.name);
            ShowToast(toastMsg);

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
            {
                Main.ModEntry.Logger.Log(string.Format("[WorkerManager] Dispatched Worker Haul ({0} -> {1} on '{2}', Cars: {3}, Fee: ${4:N0}, Dist: {5:F0}m).",
                    engineer.OriginStationName, engineer.DestinationStationName, destTrack.name, consist.Count, fee, routePath.TotalDistance));
            }

            statusMessage = "AI Driver successfully hired and dispatched!";
            return true;
        }

        /// <summary>
        /// Looks up an active task governing the specified locomotive or consist.
        /// </summary>
        public AtoBHaulTask GetActiveTask(TrainCar loco)
        {
            if (loco == null) return null;
            for (int i = 0; i < ActiveTasks.Count; i++)
            {
                var task = ActiveTasks[i];
                if (task != null && (task.LeadLocomotive == loco || (task.Consist != null && task.Consist.Contains(loco))))
                {
                    return task;
                }
            }
            return null;
        }

        /// <summary>
        /// Cancels an active worker haul, immediately halting the train and returning control to the player.
        /// </summary>
        public void CancelTask(AtoBHaulTask task)
        {
            if (task == null) return;

            try
            {
                if (task.Engineer != null)
                {
                    task.Engineer.OnTerminusArrival -= HandleTaskTerminusArrival;
                    task.Engineer.EmergencyStop();
                    TrafficManager.Instance.UnregisterEngineer(task.Engineer);
                    UnityEngine.Object.Destroy(task.Engineer);
                }

                // Apply handbrake to prevent rolling
                if (task.LeadLocomotive != null)
                {
                    var controls = task.LeadLocomotive.GetComponent<BaseControlsOverrider>();
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(1.0f);
                    }
                }

                task.Status = HaulTaskStatus.Cancelled;
                task.CompletedTime = Time.time;
                task.StatusMessage = "Cancelled by player";

                ActiveTasks.Remove(task);
                CompletedTasks.Add(task);

                ShowToast("AI Driver dismissed. Control returned to player.");
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[AITraffic] Error cancelling worker task: {0}", ex));
            }
        }

        /// <summary>
        /// Invoked when the AI engineer has brought the consist to a halt on the destination track.
        /// </summary>
        private void HandleTaskTerminusArrival(AIEngineer engineer)
        {
            if (engineer == null) return;

            AtoBHaulTask matchedTask = null;
            for (int i = 0; i < ActiveTasks.Count; i++)
            {
                if (ActiveTasks[i].Engineer == engineer || ActiveTasks[i].LeadLocomotive == engineer.TrainCar)
                {
                    matchedTask = ActiveTasks[i];
                    break;
                }
            }

            if (matchedTask == null) return;

            try
            {
                engineer.OnTerminusArrival -= HandleTaskTerminusArrival;

                // Secure locomotive with full parking handbrake
                if (matchedTask.LeadLocomotive != null)
                {
                    var controls = matchedTask.LeadLocomotive.GetComponent<BaseControlsOverrider>();
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(1.0f);
                    }
                }

                // Unregister and destroy AIEngineer component so control returns cleanly to player
                TrafficManager.Instance.UnregisterEngineer(engineer);
                UnityEngine.Object.Destroy(engineer);
                matchedTask.Engineer = null;

                matchedTask.Status = HaulTaskStatus.Arrived;
                matchedTask.CompletedTime = Time.time;
                matchedTask.StatusMessage = "Consist delivered successfully!";

                ActiveTasks.Remove(matchedTask);
                CompletedTasks.Add(matchedTask);

                string destName = matchedTask.DestinationStation != null && matchedTask.DestinationStation.stationInfo != null
                    ? matchedTask.DestinationStation.stationInfo.Name
                    : "Destination";

                string finishToast = string.Format("AI Worker completed! Consist delivered to {0} on track '{1}'!",
                    destName, matchedTask.DestinationTrack != null ? matchedTask.DestinationTrack.name : "Yard");

                ShowToast(finishToast);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[WorkerManager] Completed haul task '{0}' at {1}.", matchedTask.Id, destName));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[AITraffic] Error completing worker task: {0}", ex));
            }
        }

        #endregion

        #region Update & Toast Notifications

        /// <summary>
        /// Monitored on every tick from TrafficManager.
        /// </summary>
        public void Update(float deltaTime)
        {
            // Verify active tasks health
            for (int i = ActiveTasks.Count - 1; i >= 0; i--)
            {
                var task = ActiveTasks[i];
                if (task == null || task.LeadLocomotive == null)
                {
                    ActiveTasks.RemoveAt(i);
                    continue;
                }

                // Secondary arrival detection if engineer reached terminus stop
                if (task.Engineer != null && task.Engineer.State == EngineState.TerminusStop && task.Engineer.CurrentSpeedKmh < 0.2f)
                {
                    HandleTaskTerminusArrival(task.Engineer);
                }
            }
        }

        /// <summary>
        /// Displays an on-screen toast banner notification.
        /// </summary>
        public void ShowToast(string message)
        {
            _toastMessage = message;
            _toastExpireTime = Time.time + ToastDuration;
        }

        private void InitToastStyles()
        {
            if (_toastStylesInitialized && _toastBoxStyle != null) return;

            _toastBoxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(14, 14, 8, 8)
            };

            _toastHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.95f, 0.4f) }
            };

            _toastTextStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                wordWrap = true
            };

            _toastStylesInitialized = true;
        }

        /// <summary>
        /// Renders the floating on-screen toast banner in IMGUI.
        /// </summary>
        public void DrawToastGUI()
        {
            if (Time.time >= _toastExpireTime || string.IsNullOrEmpty(_toastMessage))
                return;

            InitToastStyles();

            float timeLeft = _toastExpireTime - Time.time;
            float alpha = Mathf.Clamp01(timeLeft); // Smooth fade-out in last second

            float bannerWidth = 520f;
            float bannerHeight = 65f;
            float screenX = (Screen.width - bannerWidth) * 0.5f;
            float screenY = 24f;

            Color prevColor = GUI.color;
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.95f * alpha);
            GUI.Box(new Rect(screenX, screenY, bannerWidth, bannerHeight), GUIContent.none);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUILayout.BeginArea(new Rect(screenX + 10f, screenY + 6f, bannerWidth - 20f, bannerHeight - 12f));
            GUILayout.Label("[AI Worker Dispatcher]", _toastHeaderStyle);
            GUILayout.Label(_toastMessage, _toastTextStyle);
            GUILayout.EndArea();

            GUI.color = prevColor;
        }

        private int _selectedStationIndex = 0;
        private int _selectedTrackIndex = -1; // -1 = Auto
        private string _lastHireStatus = string.Empty;

        /// <summary>
        /// Renders the interactive AI Worker Dispatcher menu in IMGUI (used by both UMM settings and in-game overlay).
        /// </summary>
        public void DrawWorkerDispatcherGUI()
        {
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label("<b><size=13>👷 AI Worker Dispatcher (Station-to-Station Hauls)</size></b>");
            GUILayout.Space(4f);

            double playerMoney = Inventory.Instance != null ? Inventory.Instance.PlayerMoney : 0.0;
            GUILayout.Label(string.Format("<b>Player Wallet:</b> <color=#00FF88>${0:N0}</color>", playerMoney));
            GUILayout.Space(4f);

            // 1. Identify targeted train/locomotive
            var loco = GetPlayerSelectedLocomotive();
            if (loco == null)
            {
                GUILayout.Label("<i>No locomotive detected. Board a locomotive or stand near one to hire an AI driver.</i>");
            }
            else
            {
                List<TrainCar> consist;
                float totalLen;
                float totalMass;
                GetConsistMetrics(loco, out consist, out totalLen, out totalMass);

                var curTrack = GetCurrentTrack(loco);
                var originStation = GetNearestStation(loco.transform.position);

                string origName = (originStation != null && originStation.stationInfo != null) ? originStation.stationInfo.Name : "Yard";
                string trackName = (curTrack != null) ? curTrack.name : "Unknown Track";

                GUILayout.Label(string.Format("<b>Target Train:</b> <color=#FFD700>{0}</color> ({1}) | <b>Cars:</b> {2} | <b>Length:</b> {3:F0}m | <b>Weight:</b> {4:F0}t",
                    loco.ID, loco.carType, consist.Count, totalLen, totalMass));
                GUILayout.Label(string.Format("<b>Current Location:</b> {0} [Track: {1}]", origName, trackName));
                GUILayout.Space(6f);

                // Check if already under active contract
                AtoBHaulTask existingTask = null;
                for (int i = 0; i < ActiveTasks.Count; i++)
                {
                    if (ActiveTasks[i].LeadLocomotive == loco)
                    {
                        existingTask = ActiveTasks[i];
                        break;
                    }
                }

                if (existingTask != null)
                {
                    GUILayout.Label(string.Format("<color=#00FF88><b>Active Mission:</b></color> Hauling to <b>{0}</b> on track '{1}'",
                        existingTask.DestinationStation != null && existingTask.DestinationStation.stationInfo != null ? existingTask.DestinationStation.stationInfo.Name : "Destination",
                        existingTask.DestinationTrack != null ? existingTask.DestinationTrack.name : "Yard"));
                    GUILayout.Label(string.Format("<b>Speed:</b> {0:F0} km/h | <b>Remaining:</b> {1:F1} km",
                        existingTask.CurrentSpeedKmh, existingTask.RemainingDistance / 1000f));

                    if (GUILayout.Button("Dismiss AI Driver (Take Manual Control)", GUILayout.Height(24)))
                    {
                        CancelTask(existingTask);
                    }
                }
                else
                {
                    // Destination Station Selector
                    var stations = StationController.allStations;
                    if (stations != null && stations.Count > 0)
                    {
                        // Filter out stations without stationInfo
                        List<StationController> validStations = new List<StationController>();
                        for (int i = 0; i < stations.Count; i++)
                        {
                            if (stations[i] != null && stations[i].stationInfo != null)
                                validStations.Add(stations[i]);
                        }

                        if (_selectedStationIndex >= validStations.Count) _selectedStationIndex = 0;
                        if (_selectedStationIndex < 0) _selectedStationIndex = validStations.Count - 1;

                        var destStation = validStations[_selectedStationIndex];
                        string destStationName = destStation.stationInfo.Name;
                        string destYardId = destStation.stationInfo.YardID;

                        GUILayout.BeginHorizontal();
                        GUILayout.Label("<b>Destination:</b>", GUILayout.Width(95));
                        if (GUILayout.Button("<", GUILayout.Width(28)))
                        {
                            _selectedStationIndex = (_selectedStationIndex - 1 + validStations.Count) % validStations.Count;
                            _selectedTrackIndex = -1; // Reset track selection on station change
                        }
                        GUILayout.Label(string.Format("<b>{0}</b> [{1}]", destStationName, destYardId), GUILayout.MinWidth(180));
                        if (GUILayout.Button(">", GUILayout.Width(28)))
                        {
                            _selectedStationIndex = (_selectedStationIndex + 1) % validStations.Count;
                            _selectedTrackIndex = -1;
                        }
                        GUILayout.EndHorizontal();

                        // Track Selector (Auto vs Specific Siding)
                        List<RailTrack> stationTracks = destStation.AllStationTracks ?? new List<RailTrack>();
                        string trackDesc = "Auto-Select Clear Siding";
                        RailTrack chosenTrack = null;

                        if (_selectedTrackIndex >= 0 && _selectedTrackIndex < stationTracks.Count)
                        {
                            chosenTrack = stationTracks[_selectedTrackIndex];
                            trackDesc = string.Format("{0} ({1:F0}m)", chosenTrack.name, chosenTrack.curve != null ? chosenTrack.curve.length : 0f);
                        }
                        else
                        {
                            _selectedTrackIndex = -1;
                        }

                        GUILayout.BeginHorizontal();
                        GUILayout.Label("<b>Arrival Track:</b>", GUILayout.Width(95));
                        if (GUILayout.Button("<", GUILayout.Width(28)))
                        {
                            _selectedTrackIndex--;
                            if (_selectedTrackIndex < -1) _selectedTrackIndex = stationTracks.Count - 1;
                        }
                        GUILayout.Label(string.Format("<b>{0}</b>", trackDesc), GUILayout.MinWidth(180));
                        if (GUILayout.Button(">", GUILayout.Width(28)))
                        {
                            _selectedTrackIndex++;
                            if (_selectedTrackIndex >= stationTracks.Count) _selectedTrackIndex = -1;
                        }
                        GUILayout.EndHorizontal();

                        // Route estimate & pricing
                        float estDistance = Vector3.Distance(loco.transform.position, destStation.transform.position) * 1.35f;
                        double estFee = CalculateHiringFee(estDistance, totalMass);
                        bool canAfford = playerMoney >= estFee;

                        GUILayout.Space(2f);
                        string feeColor = canAfford ? "#00FF88" : "#FF4444";
                        GUILayout.Label(string.Format("<b>Est. Distance:</b> ~{0:F1} km | <b>Driver Wage:</b> <color={1}>${2:N0}</color>",
                            estDistance / 1000f, feeColor, estFee));

                        // Hire Button
                        GUI.enabled = canAfford && (destStation != originStation);
                        string buttonText = (destStation == originStation)
                            ? "Cannot haul to current station"
                            : (canAfford ? string.Format("Hire AI Driver (${0:N0})", estFee) : "Insufficient Funds");

                        if (GUILayout.Button(buttonText, GUILayout.Height(26)))
                        {
                            string msg;
                            bool ok = HireDriverForAtoB(loco, destStation, chosenTrack, out msg);
                            _lastHireStatus = ok ? string.Format("<color=#00FF88>{0}</color>", msg) : string.Format("<color=#FF4444>{0}</color>", msg);
                        }
                        GUI.enabled = true;

                        if (!string.IsNullOrEmpty(_lastHireStatus))
                        {
                            GUILayout.Label(_lastHireStatus);
                        }
                    }
                    else
                    {
                        GUILayout.Label("<i>Stations are still loading...</i>");
                    }
                }
            }

            // 2. Active Worker Missions List
            GUILayout.Space(8f);
            GUILayout.Label(string.Format("<b>--- Active Worker Missions ({0}) ---</b>", ActiveTasks.Count));

            if (ActiveTasks.Count == 0)
            {
                GUILayout.Label("<i>No workers currently on duty.</i>");
            }
            else
            {
                for (int i = 0; i < ActiveTasks.Count; i++)
                {
                    var task = ActiveTasks[i];
                    if (task == null || task.LeadLocomotive == null) continue;

                    string dName = (task.DestinationStation != null && task.DestinationStation.stationInfo != null) ? task.DestinationStation.stationInfo.Name : "Destination";
                    string tName = (task.DestinationTrack != null) ? task.DestinationTrack.name : "Track";

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(string.Format("• <b>{0}</b> -> {1} [{2}] | <b>{3:F0} km/h</b> | {4:F1} km left",
                        task.LeadLocomotive.ID, dName, tName, task.CurrentSpeedKmh, task.RemainingDistance / 1000f));

                    if (GUILayout.Button("Dismiss", GUILayout.Width(70)))
                    {
                        CancelTask(task);
                        break;
                    }
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.EndVertical();
        }

        #endregion
    }
}
