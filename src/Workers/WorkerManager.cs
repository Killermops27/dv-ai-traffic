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
        private string _toastHeaderTitle = "[AI Worker Dispatcher]";
        private string _toastActionLabel = string.Empty;
        private Action _toastAction = null;
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

        // CRITICAL INVARIANT [WRK-02]: "Forward" for AI Workers means pulling the consist out the uncoupled end of the lead locomotive (or +transform.forward for light engines).
        public bool IsPullingAlongLocoForward(TrainCar leadLoco)
        {
            if (leadLoco == null) return true;

            bool frontCoupled = leadLoco.frontCoupler != null && leadLoco.frontCoupler.IsCoupled();
            bool rearCoupled = leadLoco.rearCoupler != null && leadLoco.rearCoupler.IsCoupled();

            if (frontCoupled && !rearCoupled)
            {
                return false;
            }
            if (!frontCoupled && rearCoupled)
            {
                return true;
            }

            if (leadLoco.trainset != null && leadLoco.trainset.cars != null && leadLoco.trainset.cars.Count > 1)
            {
                TrainCar otherCar = (leadLoco.trainset.cars[0] == leadLoco)
                    ? leadLoco.trainset.cars[leadLoco.trainset.cars.Count - 1]
                    : leadLoco.trainset.cars[0];
                if (otherCar != null && otherCar != leadLoco)
                {
                    Vector3 awayFromConsist = leadLoco.transform.position - otherCar.transform.position;
                    if (awayFromConsist.sqrMagnitude > 0.01f)
                    {
                        return Vector3.Dot(leadLoco.transform.forward, awayFromConsist) >= 0f;
                    }
                }
            }

            return true;
        }

        public bool GetPullingForwardOnTrack(TrainCar leadLoco, RailTrack track)
        {
            if (leadLoco == null || track == null) return true;

            bool pullAlongLocoForward = IsPullingAlongLocoForward(leadLoco);
            bool locoForwardOnTrack = true;

            if (leadLoco.FrontBogie != null && leadLoco.RearBogie != null &&
                leadLoco.FrontBogie.traveller != null && leadLoco.RearBogie.traveller != null &&
                leadLoco.FrontBogie.track == track && leadLoco.RearBogie.track == track)
            {
                locoForwardOnTrack = (leadLoco.FrontBogie.traveller.Span >= leadLoco.RearBogie.traveller.Span);
            }
            else if (RailGraph.Instance != null)
            {
                if (!RailGraph.Instance.IsInitialized) RailGraph.Instance.Initialize();
                var edge = RailGraph.Instance.GetEdge(track);
                if (edge != null && edge.FromNode != null && edge.ToNode != null)
                {
                    Vector3 edgeDir = (edge.ToNode.Position - edge.FromNode.Position).normalized;
                    locoForwardOnTrack = Vector3.Dot(leadLoco.transform.forward, edgeDir) >= 0f;
                }
            }

            return pullAlongLocoForward ? locoForwardOnTrack : !locoForwardOnTrack;
        }

        // CRITICAL INVARIANT [WRK-02]: Worker trains must strictly search forward (pulling); reversing is only allowed to escape a dead-end spur across the first trailing switch onto a stem track with a valid forward route.
        public RailPath FindWorkerRouteWithSpurFallback(
            Pathfinder pathfinder,
            RailTrack originTrack,
            RailTrack destTrack,
            TrainCar leadLoco,
            PathfinderOptions pathOptions,
            out Junction spurSwitch,
            out RailTrack spurStemTrack,
            out byte spurMainlineBranch)
        {
            spurSwitch = null;
            spurStemTrack = null;
            spurMainlineBranch = 0;

            if (pathfinder == null || originTrack == null || destTrack == null) return null;

            bool forwardOnTrack = GetPullingForwardOnTrack(leadLoco, originTrack);

            // 1. Primary search: strictly in the forward pulling direction
            var forwardPath = pathfinder.FindPath(originTrack, destTrack, forwardOnTrack, pathOptions);
            if (forwardPath != null && forwardPath.IsValid && forwardPath.Tracks != null && forwardPath.Tracks.Count >= 2)
            {
                return forwardPath;
            }

            // 2. Dead-end spur check: only allow backing out if the forward direction from originTrack faces a dead-end buffer stop
            if (RailGraph.Instance == null) return null;
            if (!RailGraph.Instance.IsInitialized) RailGraph.Instance.Initialize();

            var originEdge = RailGraph.Instance.GetEdge(originTrack);
            if (originEdge == null) return null;

            RailNode forwardNode = forwardOnTrack ? originEdge.ToNode : originEdge.FromNode;
            RailNode rearNode = forwardOnTrack ? originEdge.FromNode : originEdge.ToNode;
            if (forwardNode == null || rearNode == null || !forwardNode.IsDeadEnd)
            {
                return null;
            }

            // 3. Locate the first trailing switch behind the dead-end spur where the train can back onto a stem track and pull forward
            // CRITICAL INVARIANT [WRK-06]: Evaluate originTrack's own end junctions first (O(1)); the full-scene
            // FindObjectsOfType<Junction>() scan is only a fallback when neither end junction qualifies.
            Junction RearSwitch = null;
            Junction[] allJunctions = new Junction[] { originTrack.inJunction, originTrack.outJunction };
            for (int pass = 0; pass < 2 && RearSwitch == null; pass++)
            {
                if (pass == 1)
                {
                    allJunctions = UnityEngine.Object.FindObjectsOfType<Junction>();
                    if (allJunctions == null) break;
                }

                for (int i = 0; i < allJunctions.Length; i++)
                {
                    var j = allJunctions[i];
                    if (j == null || j.inBranch == null || j.inBranch.track == null || j.outBranches == null || j.outBranches.Count < 2)
                        continue;

                    bool matchesOutBranch = false;
                    for (int b = 0; b < j.outBranches.Count; b++)
                    {
                        if (j.outBranches[b] != null && j.outBranches[b].track == originTrack)
                        {
                            matchesOutBranch = true;
                            break;
                        }
                    }

                    if (matchesOutBranch)
                    {
                        float distToRear = Vector3.Distance(j.transform.position, rearNode.Position);
                        float distToFwd = Vector3.Distance(j.transform.position, forwardNode.Position);
                        if (distToRear <= distToFwd)
                        {
                            RearSwitch = j;
                            break;
                        }
                    }
                }
            }

            if (RearSwitch == null) return null;

            RailTrack candidateStem = RearSwitch.inBranch.track;
            var stemEdge = RailGraph.Instance.GetEdge(candidateStem);
            if (stemEdge == null || stemEdge.FromNode == null || stemEdge.ToNode == null) return null;

            Trainset reqTs = (leadLoco != null) ? leadLoco.trainset : null;
            if (RailGraph.Instance.IsTrackOccupied(candidateStem, null, reqTs) ||
                SignalRegistry.IsTrackOccupiedByPlayer(candidateStem, reqTs))
            {
                return null;
            }

            // Pulling forward from candidateStem across RearSwitch means heading toward RearSwitch
            float dToNode = Vector3.Distance(stemEdge.ToNode.Position, RearSwitch.transform.position);
            float dFromNode = Vector3.Distance(stemEdge.FromNode.Position, RearSwitch.transform.position);
            bool stemForwardTowardSwitch = (dToNode <= dFromNode);

            PathfinderOptions switchbackOptions = (pathOptions != null) ? pathOptions.Clone() : new PathfinderOptions();
            if (switchbackOptions.ExcludedTracks == null)
            {
                switchbackOptions.ExcludedTracks = new HashSet<RailTrack>();
            }
            switchbackOptions.ExcludedTracks.Add(originTrack);

            var switchbackPath = pathfinder.FindPath(candidateStem, destTrack, stemForwardTowardSwitch, switchbackOptions);
            if (switchbackPath != null && switchbackPath.IsValid && switchbackPath.Tracks != null && switchbackPath.Tracks.Count >= 2)
            {
                Junction verifyJunc;
                byte mainBr = 0;
                if (SignalRegistry.TryGetJunctionBetweenTracks(candidateStem, switchbackPath.Tracks[1], out verifyJunc, out mainBr))
                {
                    spurMainlineBranch = mainBr;
                }
                spurSwitch = RearSwitch;
                spurStemTrack = candidateStem;
                return switchbackPath;
            }

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

            // CRITICAL INVARIANT [WRK-05]: Arrival candidates are tiered strictly by station track designation
            // (Tier 1 transferIn, Tier 2 storage, Tier 3 any station track only if 1+2 are empty), and each tier is
            // sorted by proximity to the station office (station.transform.position). Never pick in scene-hierarchy order.
            List<RailTrack> tierTransferIn = new List<RailTrack>();
            List<RailTrack> tierStorage = new List<RailTrack>();
            for (int i = 0; i < station.AllStationTracks.Count; i++)
            {
                var track = station.AllStationTracks[i];
                if (track == null || track.curve == null) continue;

                if (station.transferInRailtracksGONames != null && station.transferInRailtracksGONames.Contains(track.name))
                {
                    tierTransferIn.Add(track);
                }
                else if (station.storageRailtracksGONames != null && station.storageRailtracksGONames.Contains(track.name))
                {
                    tierStorage.Add(track);
                }
            }

            Vector3 stationOfficePos = station.transform.position;
            System.Comparison<RailTrack> byStationProximity = (a, b) =>
                (a.curve.GetPointAt(0.5f) - stationOfficePos).sqrMagnitude.CompareTo((b.curve.GetPointAt(0.5f) - stationOfficePos).sqrMagnitude);
            tierTransferIn.Sort(byStationProximity);
            tierStorage.Sort(byStationProximity);

            // 1. Designated transferIn (inbound receiving) tracks, nearest first
            candidates.AddRange(tierTransferIn);
            // 2. Storage sidings, nearest first (always evaluated after transferIn)
            candidates.AddRange(tierStorage);

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
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            // CRITICAL INVARIANT [WRK-06]: Shared budget of synchronous route checks across Pass 1 and Pass 2 (nearest-first per [WRK-05]);
            // a track route-checked in Pass 1 is never re-checked in Pass 2 (same origin/options => same result).
            const int MaxArrivalRouteChecks = 6;
            int arrivalRouteChecks = 0;
            var routeTestedTracks = new HashSet<RailTrack>();

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
                        // [WRK-06]: bounded synchronous route checks
                        if (arrivalRouteChecks >= MaxArrivalRouteChecks) break;
                        arrivalRouteChecks++;
                        routeTestedTracks.Add(track);

                        Junction dummySwitch;
                        RailTrack dummyStem;
                        byte dummyBranch;
                        var testPath = FindWorkerRouteWithSpurFallback(
                            pathfinder, originTrack, track, leadLoco, pathOptions,
                            out dummySwitch, out dummyStem, out dummyBranch);
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
                            // [WRK-06]: never re-test a Pass-1 track; respect the shared check budget
                            if (routeTestedTracks.Contains(track)) continue;
                            if (arrivalRouteChecks >= MaxArrivalRouteChecks) break;
                            arrivalRouteChecks++;
                            routeTestedTracks.Add(track);

                            Junction dummySwitch;
                            RailTrack dummyStem;
                            byte dummyBranch;
                            var testPath = FindWorkerRouteWithSpurFallback(
                                pathfinder, originTrack, track, leadLoco, pathOptions,
                                out dummySwitch, out dummyStem, out dummyBranch);
                            if (testPath != null && testPath.IsValid)
                            {
                                bestCandidate = track;
                                break;
                            }
                        }
                    }
                }
            }

            // Note: Pass 3 (fallback to occupied tracks) was removed in v0.2.1 to prevent AI trains
            // from routing into sidings and colliding with/pushing parked rolling stock.


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
            var econ = (AITraffic.Config.IniConfigManager.Instance != null && AITraffic.Config.IniConfigManager.Instance.Economy != null)
                ? AITraffic.Config.IniConfigManager.Instance.Economy
                : new AITraffic.Config.EconomyConfig();

            double baseFee = econ.DriverBaseFee;
            double distanceFee = routeDistanceMeters * econ.PerMeterRate;
            double massFee = consistMassTons * econ.PerTonRate;

            double total = baseFee + distanceFee + massFee;
            return Math.Round(total / 10.0) * 10.0; // Round to nearest $10
        }

        /// <summary>
        /// Calculates the variable leg fee (distance + tonnage) without the flat dispatch base fee.
        /// Used for Leg 1..N-1 in multi-leg itineraries where DriverBaseFee is charged once on Leg 0.
        /// </summary>
        public double CalculateLegVariableFee(float routeDistanceMeters, float consistMassTons)
        {
            var econ = (AITraffic.Config.IniConfigManager.Instance != null && AITraffic.Config.IniConfigManager.Instance.Economy != null)
                ? AITraffic.Config.IniConfigManager.Instance.Economy
                : new AITraffic.Config.EconomyConfig();

            double distanceFee = Math.Max(0f, routeDistanceMeters) * econ.PerMeterRate;
            double massFee = Math.Max(0f, consistMassTons) * econ.PerTonRate;
            double total = distanceFee + massFee;
            return Math.Round(total / 10.0) * 10.0;
        }

        // CRITICAL INVARIANT [WRK-07]: Compute cancellation refund BEFORE destroying task.Engineer (which zeroes RemainingDistance).
        // 100% full refund if cancelled within 10s grace window AND < 25m moved on Leg 0; otherwise DriverBaseFee and completed legs
        // are non-refundable, Pending future legs refund 100%, and the active EnRoute leg refunds its variable portion pro-rata.
        public double CalculateCancellationRefund(AtoBHaulTask task)
        {
            if (task == null || task.HiringFee <= 0.0) return 0.0;
            if (task.Status == HaulTaskStatus.Arrived || task.Status == HaulTaskStatus.Cancelled) return 0.0;

            float elapsed = Time.time - task.StartTime;
            float movedMeters = (task.LeadLocomotive != null && task.InitialWorldPosition != Vector3.zero)
                ? Vector3.Distance(task.LeadLocomotive.transform.position, task.InitialWorldPosition)
                : 999f;

            if (task.CurrentLegIndex == 0 && elapsed <= 10.0f && movedMeters < 25.0f)
            {
                return task.HiringFee;
            }

            if (task.Legs == null || task.Legs.Count == 0)
            {
                double varPortion = Math.Max(0.0, task.HiringFee - task.BaseFeeCharged);
                float ratio = (task.RouteDistance > 1f) ? Mathf.Clamp01(task.RemainingDistance / task.RouteDistance) : 0f;
                double rawRefund = Math.Round((varPortion * ratio) / 10.0) * 10.0;
                return Math.Max(0.0, Math.Min(varPortion, rawRefund));
            }

            double totalRefund = 0.0;
            for (int i = 0; i < task.Legs.Count; i++)
            {
                var leg = task.Legs[i];
                if (leg == null) continue;

                if (i < task.CurrentLegIndex || leg.Status == ItineraryLegStatus.Completed)
                {
                    continue;
                }
                else if (i > task.CurrentLegIndex || leg.Status == ItineraryLegStatus.Pending)
                {
                    totalRefund += Math.Max(0.0, leg.LegFee);
                }
                else
                {
                    double refundableLegPool = (i == 0)
                        ? Math.Max(0.0, leg.LegFee - task.BaseFeeCharged)
                        : Math.Max(0.0, leg.LegFee);

                    float legDist = leg.RouteDistance;
                    float remDist = (task.Status == HaulTaskStatus.WaitingAtIntermediate) ? 0f : task.RemainingDistance;
                    float remainingRatio = (legDist > 1f) ? Mathf.Clamp01(remDist / legDist) : 0f;
                    totalRefund += refundableLegPool * remainingRatio;
                }
            }

            double rounded = Math.Round(totalRefund / 10.0) * 10.0;
            double maxRefundable = Math.Max(0.0, task.HiringFee - task.BaseFeeCharged);
            return Math.Max(0.0, Math.Min(maxRefundable, rounded));
        }

        public static bool ShouldKeepEngineRunningOnArrival(AIEngineer engineer)
        {
            if (engineer == null || Instance == null || Instance.ActiveTasks == null) return false;
            for (int i = 0; i < Instance.ActiveTasks.Count; i++)
            {
                var t = Instance.ActiveTasks[i];
                if (t != null && (t.Engineer == engineer || t.LeadLocomotive == engineer.TrainCar))
                {
                    if (!t.HasRemainingLegs) return false;
                    // Infinite hold at intermediate waypoint (DwellSeconds < 0f) shuts down the prime mover until Radio Resume
                    if (t.CurrentLeg != null && t.CurrentLeg.DwellSeconds < 0f) return false;
                    return true;
                }
            }
            return false;
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
                statusMessage = string.Format("Locomotive '{0}' is not supported for autonomous driving.", ModCompatManager.GetCarId(leadLoco));
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

            if (!isAutoTrack && TrafficScheduler.IsTrackOccupied(destTrack))
            {
                statusMessage = string.Format("Destination track '{0}' is currently occupied by other cars!", destTrack.name);
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
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            // CRITICAL INVARIANT [WRK-02]: Enforce strict pulling-forward routing with single-switchback fallback only on dead-end spurs (See GRAVEYARD.md)
            Junction spurSwitch;
            RailTrack spurStemTrack;
            byte spurMainlineBranch;
            var routePath = FindWorkerRouteWithSpurFallback(
                pathfinder, originTrack, destTrack, leadLoco, pathOptions,
                out spurSwitch, out spurStemTrack, out spurMainlineBranch);
            if (routePath == null || !routePath.IsValid || routePath.Tracks == null || routePath.Tracks.Count < 2)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[WorkerManager] HireDriverForAtoB forward route calculation failed from '{0}' to '{1}'.",
                        originTrack != null ? originTrack.name : "null", destTrack != null ? destTrack.name : "null"));
                }
                statusMessage = string.Format("Could not calculate a forward route to {0} track '{1}'. Loco may face wrong way or switches are blocked.",
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

            // Connect Multiple Unit (MU) cables between all adjacent MU-capable locomotives/slugs
            for (int i = 0; i < consist.Count - 1; i++)
            {
                var carA = consist[i];
                var carB = consist[i + 1];
                if (carA != null && carB != null && carA.IsMultipleUnit && carB.IsMultipleUnit)
                {
                    TrainSpawner.ConnectMUCablesBetween(carA, carB);
                }
            }

            // Release handbrakes across the entire consist
            for (int i = 0; i < consist.Count; i++)
            {
                var car = consist[i];
                if (car == null) continue;

                if (car.brakeSystem != null)
                {
                    car.brakeSystem.SetHandbrakePosition(0f, true);
                }

                BaseControlsOverrider controls = null;
                if (car.SimController != null) controls = car.SimController.controlsOverrider;
                if (controls == null) controls = car.GetComponent<BaseControlsOverrider>();
                if (controls != null && controls.Handbrake != null)
                {
                    controls.Handbrake.Set(0f);
                }

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    TrainSpawner.ApplyAIDamageImmunity(car, true);
                }

                if (car.IsLoco)
                {
                    TrainSpawner.RemoveLocoZoneBlockers(car);
                }
            }

            // Traverse all locomotives in the consist:
            // Start trailing engines and ensure trailing locomotives have BrakeCutout.Set(0f) and IndependentBrake.Set(0f)
            for (int i = 0; i < consist.Count; i++)
            {
                var car = consist[i];
                if (car != null && car.IsLoco)
                {
                    bool isLead = (car == leadLoco);
                    TrainSpawner.InitializeLocomotive(car, isLead);

                    if (!isLead)
                    {
                        var locoControls = (car.SimController != null) ? car.SimController.controlsOverrider : car.GetComponent<BaseControlsOverrider>();
                        if (locoControls != null)
                        {
                            if (locoControls.BrakeCutout != null) locoControls.BrakeCutout.Set(0f);
                            if (locoControls.IndependentBrake != null) locoControls.IndependentBrake.Set(0f);
                            if (locoControls.Brake != null) locoControls.Brake.Set(0f);
                            if (locoControls.Handbrake != null) locoControls.Handbrake.Set(0f);
                        }
                    }
                }
            }

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

            // CRITICAL INVARIANT [WRK-02]: Lock worker pulling reverser and trigger dead-end spur switchback escape if departing from a buffer spur.
            float pullingReverser = IsPullingAlongLocoForward(leadLoco) ? 1.0f : -1.0f;
            if (spurSwitch != null && spurStemTrack != null)
            {
                engineer.InitiateSpurReverseEscape(spurSwitch, spurStemTrack, spurMainlineBranch, pullingReverser);
            }
            else
            {
                engineer.SetLockedPullingReverser(pullingReverser);
            }

            // Hook terminus arrival event
            engineer.OnTerminusArrival += HandleTaskTerminusArrival;

            // Register with TrafficManager
            TrafficManager.Instance.RegisterEngineer(engineer);

            // Note: In v0.2.1, worker-driven consists are no longer tagged as transient AI traffic,
            // allowing them to retain proper player save serialization, job car linkages, and career tracking.


            // 9. Create task record
            var econ = (AITraffic.Config.IniConfigManager.Instance != null && AITraffic.Config.IniConfigManager.Instance.Economy != null)
                ? AITraffic.Config.IniConfigManager.Instance.Economy
                : new AITraffic.Config.EconomyConfig();

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
                engineer,
                econ.DriverBaseFee
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
        /// Employs an AI driver to execute a multi-leg station-to-station itinerary, billing all legs upfront.
        /// </summary>
        public bool HireDriverForItinerary(TrainCar leadLoco, List<ItineraryLeg> requestedLegs, out string statusMessage)
        {
            statusMessage = string.Empty;

            if (requestedLegs == null || requestedLegs.Count == 0)
            {
                statusMessage = "Itinerary contains no legs!";
                return false;
            }

            if (requestedLegs.Count == 1)
            {
                var single = requestedLegs[0];
                return HireDriverForAtoB(
                    leadLoco,
                    single != null ? single.DestinationStation : null,
                    single != null && !single.IsAutoSelectedTrack ? single.DestinationTrack : null,
                    out statusMessage);
            }

            if (leadLoco == null || !leadLoco.IsLoco || !TrainSpawner.IsSupportedAILocomotive(leadLoco))
            {
                statusMessage = "No valid AI-supported lead locomotive selected!";
                return false;
            }

            for (int i = 0; i < ActiveTasks.Count; i++)
            {
                if (ActiveTasks[i].LeadLocomotive == leadLoco)
                {
                    statusMessage = "This locomotive already has an active AI driver assigned!";
                    return false;
                }
            }

            List<TrainCar> consist;
            float totalLength;
            float totalMass;
            GetConsistMetrics(leadLoco, out consist, out totalLength, out totalMass);

            RailTrack initialOriginTrack = GetCurrentTrack(leadLoco);
            if (initialOriginTrack == null)
            {
                statusMessage = "Locomotive is not sitting on a valid rail track!";
                return false;
            }

            StationController initialOriginStation = GetNearestStation(leadLoco.transform.position);
            var pathfinder = new Pathfinder(RailGraph.Instance);
            var pathOptions = new PathfinderOptions
            {
                Requester = leadLoco,
                RequesterTrainset = leadLoco.trainset,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            var econ = (AITraffic.Config.IniConfigManager.Instance != null && AITraffic.Config.IniConfigManager.Instance.Economy != null)
                ? AITraffic.Config.IniConfigManager.Instance.Economy
                : new AITraffic.Config.EconomyConfig();

            var validatedLegs = new List<ItineraryLeg>(requestedLegs.Count);
            RailPath firstLegPath = null;
            Junction firstSpurSwitch = null;
            RailTrack firstSpurStem = null;
            byte firstSpurBranch = 0;
            double totalFee = 0.0;

            RailTrack currentLegOriginTrack = initialOriginTrack;
            StationController currentLegOriginStation = initialOriginStation;

            for (int i = 0; i < requestedLegs.Count; i++)
            {
                var req = requestedLegs[i];
                if (req == null || req.DestinationStation == null)
                {
                    statusMessage = string.Format("Leg {0} is missing a destination station!", i + 1);
                    return false;
                }

                RailTrack legDestTrack = req.IsAutoSelectedTrack ? null : req.DestinationTrack;
                bool isAuto = (legDestTrack == null);
                if (isAuto)
                {
                    legDestTrack = SelectBestArrivalTrack(req.DestinationStation, totalLength, currentLegOriginTrack, i == 0 ? leadLoco : null);
                }

                if (legDestTrack == null)
                {
                    statusMessage = string.Format("Leg {0}: No suitable arrival track found at {1}!",
                        i + 1, req.DestinationStation.stationInfo != null ? req.DestinationStation.stationInfo.Name : "destination");
                    return false;
                }

                RailPath legPath;
                if (i == 0)
                {
                    legPath = FindWorkerRouteWithSpurFallback(
                        pathfinder, currentLegOriginTrack, legDestTrack, leadLoco, pathOptions,
                        out firstSpurSwitch, out firstSpurStem, out firstSpurBranch);
                    firstLegPath = legPath;
                }
                else
                {
                    legPath = pathfinder.FindPath(currentLegOriginTrack, legDestTrack, true, pathOptions)
                              ?? pathfinder.FindPath(currentLegOriginTrack, legDestTrack, false, pathOptions);
                }

                if (legPath == null || !legPath.IsValid || legPath.Tracks == null || legPath.Tracks.Count < 2)
                {
                    statusMessage = string.Format("Leg {0}: Could not calculate a valid route to {1}!",
                        i + 1, req.DestinationStation.stationInfo != null ? req.DestinationStation.stationInfo.Name : "destination");
                    return false;
                }

                double legFee = (i == 0)
                    ? CalculateHiringFee(legPath.TotalDistance, totalMass)
                    : CalculateLegVariableFee(legPath.TotalDistance, totalMass);

                totalFee += legFee;

                validatedLegs.Add(new ItineraryLeg(
                    i,
                    currentLegOriginStation,
                    currentLegOriginTrack,
                    req.DestinationStation,
                    legDestTrack,
                    isAuto,
                    legPath.TotalDistance,
                    legFee,
                    req.DwellSeconds > 0f ? req.DwellSeconds : 5.0f,
                    i == 0 ? ItineraryLegStatus.EnRoute : ItineraryLegStatus.Pending));

                currentLegOriginStation = req.DestinationStation;
                currentLegOriginTrack = legDestTrack;
            }

            double playerMoney = Inventory.Instance != null ? Inventory.Instance.PlayerMoney : 0.0;
            if (playerMoney < totalFee)
            {
                statusMessage = string.Format("Insufficient funds for {0}-leg itinerary! Cost: ${1:N0} (Wallet: ${2:N0})",
                    validatedLegs.Count, totalFee, playerMoney);
                return false;
            }

            if (Inventory.Instance != null)
            {
                Inventory.Instance.RemoveMoney(totalFee);
            }

            TrainSpawner.ConfigureConsistCouplers(consist);
            for (int i = 0; i < consist.Count - 1; i++)
            {
                var carA = consist[i];
                var carB = consist[i + 1];
                if (carA != null && carB != null && carA.IsMultipleUnit && carB.IsMultipleUnit)
                {
                    TrainSpawner.ConnectMUCablesBetween(carA, carB);
                }
            }

            for (int i = 0; i < consist.Count; i++)
            {
                var car = consist[i];
                if (car == null) continue;
                if (car.brakeSystem != null) car.brakeSystem.SetHandbrakePosition(0f, true);
                BaseControlsOverrider controls = (car.SimController != null) ? car.SimController.controlsOverrider : car.GetComponent<BaseControlsOverrider>();
                if (controls != null && controls.Handbrake != null) controls.Handbrake.Set(0f);
                if (Main.Settings != null && Main.Settings.AIDamageImmunity) TrainSpawner.ApplyAIDamageImmunity(car, true);
                if (car.IsLoco) TrainSpawner.RemoveLocoZoneBlockers(car);
            }

            for (int i = 0; i < consist.Count; i++)
            {
                var car = consist[i];
                if (car != null && car.IsLoco)
                {
                    bool isLead = (car == leadLoco);
                    TrainSpawner.InitializeLocomotive(car, isLead);
                    if (!isLead)
                    {
                        var locoControls = (car.SimController != null) ? car.SimController.controlsOverrider : car.GetComponent<BaseControlsOverrider>();
                        if (locoControls != null)
                        {
                            if (locoControls.BrakeCutout != null) locoControls.BrakeCutout.Set(0f);
                            if (locoControls.IndependentBrake != null) locoControls.IndependentBrake.Set(0f);
                            if (locoControls.Brake != null) locoControls.Brake.Set(0f);
                            if (locoControls.Handbrake != null) locoControls.Handbrake.Set(0f);
                        }
                    }
                }
            }

            var firstLeg = validatedLegs[0];
            var engineer = leadLoco.gameObject.GetComponent<AIEngineer>() ?? leadLoco.gameObject.AddComponent<AIEngineer>();
            engineer.IsWorkerDriven = true;
            engineer.CurrentPath = firstLegPath;
            engineer.DistanceToDestination = firstLegPath.TotalDistance;
            engineer.OriginStationName = firstLeg.OriginStation != null && firstLeg.OriginStation.stationInfo != null ? firstLeg.OriginStation.stationInfo.Name : "Yard";
            engineer.DestinationStationName = firstLeg.DestinationStation != null && firstLeg.DestinationStation.stationInfo != null ? firstLeg.DestinationStation.stationInfo.Name : "Destination";
            engineer.DestinationTrackName = firstLeg.DestinationTrack.name;
            engineer.IsStationDestination = false;
            engineer.IsTerminusDestination = true;

            float pullingReverser = IsPullingAlongLocoForward(leadLoco) ? 1.0f : -1.0f;
            if (firstSpurSwitch != null && firstSpurStem != null)
            {
                engineer.InitiateSpurReverseEscape(firstSpurSwitch, firstSpurStem, firstSpurBranch, pullingReverser);
            }
            else
            {
                engineer.SetLockedPullingReverser(pullingReverser);
            }

            engineer.OnTerminusArrival += HandleTaskTerminusArrival;
            TrafficManager.Instance.RegisterEngineer(engineer);

            var multiTask = new AtoBHaulTask(
                null,
                leadLoco,
                consist,
                validatedLegs,
                totalFee,
                econ.DriverBaseFee,
                engineer);

            ActiveTasks.Add(multiTask);

            ShowToast(string.Format("AI Driver Hired (${0:N0}) for {1}-leg itinerary! Leg 1 -> {2}.",
                totalFee, validatedLegs.Count, engineer.DestinationStationName));

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
            {
                Main.ModEntry.Logger.Log(string.Format("[WorkerManager] Dispatched Multi-Leg Worker Itinerary ({0} legs, Cars: {1}, TotalFee: ${2:N0}, TotalDist: {3:F0}m).",
                    validatedLegs.Count, consist.Count, totalFee, multiTask.TotalRouteDistance));
            }

            statusMessage = "AI Driver successfully hired for multi-leg itinerary!";
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
        // CRITICAL INVARIANT [COMPAT]: Cleanly unregister AIDamageImmunity and LocoSim components upon worker handover/cancellation (See GRAVEYARD.md)
        private static void RestorePlayerConsistDamageVulnerability(AtoBHaulTask task)
        {
            if (task == null) return;

            if (task.LeadLocomotive != null)
            {
                TrainSpawner.ApplyAIDamageImmunity(task.LeadLocomotive, false);
                AITraffic.Compat.AIDamageImmunityPatches.UnregisterAISimComponents(task.LeadLocomotive);

                if (task.LeadLocomotive.trainset != null && task.LeadLocomotive.trainset.cars != null)
                {
                    var trainsetCars = task.LeadLocomotive.trainset.cars;
                    for (int i = 0; i < trainsetCars.Count; i++)
                    {
                        var c = trainsetCars[i];
                        if (c != null)
                        {
                            TrainSpawner.ApplyAIDamageImmunity(c, false);
                            AITraffic.Compat.AIDamageImmunityPatches.UnregisterAISimComponents(c);
                        }
                    }
                }
            }

            if (task.Consist != null)
            {
                for (int i = 0; i < task.Consist.Count; i++)
                {
                    var c = task.Consist[i];
                    if (c != null)
                    {
                        TrainSpawner.ApplyAIDamageImmunity(c, false);
                        AITraffic.Compat.AIDamageImmunityPatches.UnregisterAISimComponents(c);
                    }
                }
            }
        }

        // CRITICAL INVARIANT [WRK-03]: Completely destroy AILocomotiveLighting, remove ConsistEOTMarker, and restore trailing MU BrakeCutout upon worker cancellation or arrival handover (See GRAVEYARD.md)
        private static void CleanupWorkerConsistComponents(AtoBHaulTask task)
        {
            if (task == null) return;

            if (task.LeadLocomotive != null)
            {
                var leadLighting = task.LeadLocomotive.GetComponent<AILocomotiveLighting>();
                if (leadLighting != null)
                {
                    leadLighting.Shutdown();
                    UnityEngine.Object.Destroy(leadLighting);
                }
                ConsistEOTMarker.RemoveFromCar(task.LeadLocomotive);

                if (task.LeadLocomotive.trainset != null && task.LeadLocomotive.trainset.cars != null)
                {
                    var tsCars = task.LeadLocomotive.trainset.cars;
                    for (int i = 0; i < tsCars.Count; i++)
                    {
                        var c = tsCars[i];
                        if (c == null) continue;
                        var l = c.GetComponent<AILocomotiveLighting>();
                        if (l != null)
                        {
                            l.Shutdown();
                            UnityEngine.Object.Destroy(l);
                        }
                        ConsistEOTMarker.RemoveFromCar(c);
                    }
                }
            }

            if (task.Consist != null)
            {
                for (int i = 0; i < task.Consist.Count; i++)
                {
                    var c = task.Consist[i];
                    if (c == null) continue;
                    var l = c.GetComponent<AILocomotiveLighting>();
                    if (l != null)
                    {
                        l.Shutdown();
                        UnityEngine.Object.Destroy(l);
                    }
                    ConsistEOTMarker.RemoveFromCar(c);
                }
            }
        }

        public void CancelTask(AtoBHaulTask task)
        {
            if (task == null) return;

            try
            {
                // CRITICAL INVARIANT [WRK-07]: Compute pro-rated cancellation refund BEFORE destroying task.Engineer
                double refund = CalculateCancellationRefund(task);
                task.RefundedAmount = refund;
                if (refund > 0.0 && Inventory.Instance != null)
                {
                    Inventory.Instance.AddMoney(refund);
                }

                if (task.Legs != null)
                {
                    for (int i = task.CurrentLegIndex; i < task.Legs.Count; i++)
                    {
                        if (task.Legs[i] != null && task.Legs[i].Status != ItineraryLegStatus.Completed)
                        {
                            task.Legs[i].Status = ItineraryLegStatus.Cancelled;
                        }
                    }
                }

                // CRITICAL INVARIANT [WRK-12]: Drop any in-flight waypoint exit search so a late background result can never be adopted after cancellation.
                task.ClearAdvanceSearch();
                _exitRetryToastedTasks.Remove(task);

                if (task.Engineer != null)
                {
                    task.Engineer.OnTerminusArrival -= HandleTaskTerminusArrival;
                    task.Engineer.EmergencyStop();
                    TrafficManager.Instance.UnregisterEngineer(task.Engineer);
                    UnityEngine.Object.Destroy(task.Engineer);
                    task.Engineer = null;
                }
                else if (task.LeadLocomotive != null)
                {
                    var remEng = task.LeadLocomotive.GetComponent<AIEngineer>();
                    if (remEng != null)
                    {
                        TrafficManager.Instance.UnregisterEngineer(remEng);
                        UnityEngine.Object.Destroy(remEng);
                    }
                }

                CleanupWorkerConsistComponents(task);
                RestorePlayerConsistDamageVulnerability(task);

                // Apply handbrake to prevent rolling
                if (task.LeadLocomotive != null)
                {
                    var controls = task.LeadLocomotive.GetComponent<BaseControlsOverrider>();
                    if (controls == null && task.LeadLocomotive.SimController != null)
                    {
                        controls = task.LeadLocomotive.SimController.controlsOverrider;
                    }
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(1.0f);
                    }
                }

                task.Status = HaulTaskStatus.Cancelled;
                task.CompletedTime = Time.time;
                task.StatusMessage = refund > 0.0
                    ? string.Format("Cancelled by player (Refunded ${0:N0})", refund)
                    : "Cancelled by player";

                ActiveTasks.Remove(task);
                CompletedTasks.Add(task);

                if (refund > 0.0)
                {
                    ShowToast(string.Format("AI Driver dismissed. Refunded ${0:N0} to wallet. Control returned to player.", refund));
                }
                else
                {
                    ShowToast("AI Driver dismissed. Control returned to player.");
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[WorkerManager] Cancelled worker task '{0}' (Paid: ${1:N0}, Refunded: ${2:N0}).",
                        task.Id, task.HiringFee, refund));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[AITraffic] Error cancelling worker task: {0}", ex));
            }
        }

        /// <summary>
        /// Checks if a train car belongs to a currently ACTIVE AI worker haul task (completed tasks are player consists).
        /// Used by switch interlocking. Despawn/purge protection must keep using IsTrainCarInAnyWorkerTask.
        /// </summary>
        public static bool IsTrainCarInActiveWorkerTask(TrainCar car)
        {
            if (car == null || Instance == null) return false;

            if (Instance.ActiveTasks != null)
            {
                for (int i = 0; i < Instance.ActiveTasks.Count; i++)
                {
                    var task = Instance.ActiveTasks[i];
                    if (task == null) continue;
                    if (task.LeadLocomotive == car) return true;
                    if (task.Consist != null && task.Consist.Contains(car)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a train car belongs to any active or completed AI worker haul task.
        /// Used by despawn systems to strictly preserve player-owned trains and consists.
        /// </summary>
        public static bool IsTrainCarInAnyWorkerTask(TrainCar car)
        {
            if (car == null || Instance == null) return false;

            if (Instance.ActiveTasks != null)
            {
                for (int i = 0; i < Instance.ActiveTasks.Count; i++)
                {
                    var task = Instance.ActiveTasks[i];
                    if (task == null) continue;
                    if (task.LeadLocomotive == car) return true;
                    if (task.Consist != null && task.Consist.Contains(car)) return true;
                }
            }

            if (Instance.CompletedTasks != null)
            {
                for (int i = 0; i < Instance.CompletedTasks.Count; i++)
                {
                    var task = Instance.CompletedTasks[i];
                    if (task == null) continue;
                    if (task.LeadLocomotive == car) return true;
                    if (task.Consist != null && task.Consist.Contains(car)) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Invoked when the AI engineer has brought the consist to a halt on the destination track.
        /// </summary>
        private void HandleTaskTerminusArrival(AIEngineer engineer)
        {
            HandleTaskTerminusArrival(engineer, null);
        }

        /// <summary>
        /// Shuts down the engine, secures the train with brakes, releases control to player, and shows a toast banner.
        /// </summary>
        private void HandleTaskTerminusArrival(AIEngineer engineer, AtoBHaulTask task)
        {
            AtoBHaulTask matchedTask = task;
            if (matchedTask == null)
            {
                if (engineer == null) return;
                for (int i = 0; i < ActiveTasks.Count; i++)
                {
                    if (ActiveTasks[i].Engineer == engineer || ActiveTasks[i].LeadLocomotive == engineer.TrainCar)
                    {
                        matchedTask = ActiveTasks[i];
                        break;
                    }
                }
            }

            if (matchedTask == null) return;

            try
            {
                if (matchedTask.CurrentLeg != null)
                {
                    matchedTask.CurrentLeg.Status = ItineraryLegStatus.Completed;
                }

                // CRITICAL INVARIANT [WRK-07]: Intermediate leg completion transitions to WaitingAtIntermediate without destroying AIEngineer or shutting down engine (unless DwellSeconds < 0f infinite hold)
                if (matchedTask.HasRemainingLegs && matchedTask.Status != HaulTaskStatus.WaitingAtIntermediate)
                {
                    bool isInfiniteHold = matchedTask.CurrentLeg != null && matchedTask.CurrentLeg.DwellSeconds < 0f;
                    float dwell = (matchedTask.CurrentLeg != null && matchedTask.CurrentLeg.DwellSeconds > 0f)
                        ? matchedTask.CurrentLeg.DwellSeconds
                        : 5.0f;
                    matchedTask.Status = HaulTaskStatus.WaitingAtIntermediate;
                    matchedTask.IntermediateDwellExpireTime = isInfiniteHold ? float.PositiveInfinity : (Time.time + dwell);

                    string stopName = matchedTask.DestinationStation != null && matchedTask.DestinationStation.stationInfo != null
                        ? matchedTask.DestinationStation.stationInfo.Name
                        : "Waypoint";
                    string locoName = matchedTask.LeadLocomotive != null ? ModCompatManager.GetCarId(matchedTask.LeadLocomotive) : "Consist";

                    if (isInfiniteHold)
                    {
                        if (engineer != null)
                        {
                            engineer.ShutdownEngine();
                        }
                        matchedTask.StatusMessage = string.Format("Holding at {0} (Leg {1}/{2}) - Engine Off (Radio Resume)",
                            stopName, matchedTask.CurrentLegIndex + 1, matchedTask.Legs.Count);
                        ShowToast(string.Format("AI Worker ({0}) reached waypoint {1} (Leg {2}/{3}). Engine shut down; awaiting Comms Radio Resume.",
                            locoName, stopName, matchedTask.CurrentLegIndex + 1, matchedTask.Legs.Count), "[AI Worker Dispatcher]", 10f);
                    }
                    else
                    {
                        matchedTask.StatusMessage = string.Format("Completed Leg {0}/{1} at {2} (dwelling {3:F0}s)",
                            matchedTask.CurrentLegIndex + 1, matchedTask.Legs.Count, stopName, dwell);

                        ShowToast(string.Format("AI Worker ({0}) reached waypoint {1} (Leg {2}/{3}). Proceeding in {4:F0}s...",
                            locoName, stopName, matchedTask.CurrentLegIndex + 1, matchedTask.Legs.Count, dwell));
                    }
                    return;
                }

                if (engineer != null)
                {
                    engineer.OnTerminusArrival -= HandleTaskTerminusArrival;
                    engineer.ShutdownEngine();
                }

                // Extra safety: make sure lead locomotive engine is shut down and brakes secured
                if (matchedTask.LeadLocomotive != null)
                {
                    var lead = matchedTask.LeadLocomotive;
                    var controls = lead.GetComponent<BaseControlsOverrider>();
                    if (controls == null && lead.SimController != null)
                    {
                        controls = lead.SimController.controlsOverrider;
                    }
                    if (controls != null)
                    {
                        if (controls.Throttle != null) controls.Throttle.Set(0.0f);
                        if (controls.DynamicBrake != null) controls.DynamicBrake.Set(0.0f);
                        if (controls.Brake != null) controls.Brake.Set(1.0f);
                        if (controls.IndependentBrake != null) controls.IndependentBrake.Set(1.0f);
                        if (controls.Handbrake != null) controls.Handbrake.Set(1.0f);
                        if (controls.Reverser != null) controls.Reverser.Set(0.0f);
                        if (controls.Starter != null) controls.Starter.Set(0.0f);
                        if (controls.PowerOff != null) controls.PowerOff.Set(1.0f);
                        if (controls.HeadlightsFront != null) controls.HeadlightsFront.Set(0.4f);
                        if (controls.HeadlightsRear != null) controls.HeadlightsRear.Set(0.4f);
                        if (controls.CabLight != null) controls.CabLight.Set(0.0f);
                        if (controls.Wipers != null) controls.Wipers.Set(0.0f);
                    }
                    if (lead.brakeSystem != null)
                    {
                        lead.brakeSystem.SetHandbrakePosition(1.0f, true);
                    }
                }

                // Also shut down helper locomotives in consist and clamp handbrakes
                if (matchedTask.Consist != null)
                {
                    foreach (var car in matchedTask.Consist)
                    {
                        if (car != null && car != matchedTask.LeadLocomotive && car.IsLoco)
                        {
                            var hControls = car.GetComponent<BaseControlsOverrider>();
                            if (hControls == null && car.SimController != null)
                            {
                                hControls = car.SimController.controlsOverrider;
                            }
                            if (hControls != null)
                            {
                                if (hControls.Throttle != null) hControls.Throttle.Set(0.0f);
                                if (hControls.DynamicBrake != null) hControls.DynamicBrake.Set(0.0f);
                                if (hControls.Brake != null) hControls.Brake.Set(1.0f);
                                if (hControls.IndependentBrake != null) hControls.IndependentBrake.Set(1.0f);
                                if (hControls.Handbrake != null) hControls.Handbrake.Set(1.0f);
                                if (hControls.Reverser != null) hControls.Reverser.Set(0.0f);
                                if (hControls.Starter != null) hControls.Starter.Set(0.0f);
                                if (hControls.PowerOff != null) hControls.PowerOff.Set(1.0f);
                                if (hControls.HeadlightsFront != null) hControls.HeadlightsFront.Set(0.4f);
                                if (hControls.HeadlightsRear != null) hControls.HeadlightsRear.Set(0.4f);
                                if (hControls.CabLight != null) hControls.CabLight.Set(0.0f);
                            }
                            if (car.brakeSystem != null)
                            {
                                car.brakeSystem.SetHandbrakePosition(1.0f, true);
                            }
                        }
                    }
                }

                // Unregister and destroy AIEngineer component so control returns cleanly to player
                if (engineer != null)
                {
                    TrafficManager.Instance.UnregisterEngineer(engineer);
                    UnityEngine.Object.Destroy(engineer);
                    matchedTask.Engineer = null;
                }
                else if (matchedTask.LeadLocomotive != null)
                {
                    var remEng = matchedTask.LeadLocomotive.GetComponent<AIEngineer>();
                    if (remEng != null)
                    {
                        TrafficManager.Instance.UnregisterEngineer(remEng);
                        UnityEngine.Object.Destroy(remEng);
                    }
                }

                CleanupWorkerConsistComponents(matchedTask);
                RestorePlayerConsistDamageVulnerability(matchedTask);

                matchedTask.Status = HaulTaskStatus.Arrived;
                matchedTask.CompletedTime = Time.time;
                matchedTask.StatusMessage = "Consist delivered successfully!";

                ActiveTasks.Remove(matchedTask);
                CompletedTasks.Add(matchedTask);

                string destName = matchedTask.DestinationStation != null && matchedTask.DestinationStation.stationInfo != null
                    ? matchedTask.DestinationStation.stationInfo.Name
                    : "Destination";
                string trackName = matchedTask.DestinationTrack != null ? matchedTask.DestinationTrack.name : "Yard";
                string locoId = matchedTask.LeadLocomotive != null ? ModCompatManager.GetCarId(matchedTask.LeadLocomotive) : "Consist";

                string finishToast = string.Format("AI Worker completed! Train {0} delivered to {1} on track '{2}'. Engine shut down, control returned to player.",
                    locoId, destName, trackName);

                ShowToast(finishToast);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[WorkerManager] Completed haul task '{0}' ({1}) at {2} on track '{3}'.",
                        matchedTask.Id, locoId, destName, trackName));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[AITraffic] Error completing worker task: {0}", ex));
            }
        }

        private const int MaxAdvanceRouteSearches = 6;

        // CRITICAL INVARIANT [WRK-12]: Intermediate waypoint departure is fully non-blocking (Pathfinder.FindPathAsync only, never a synchronous FindPath on the main thread),
        // never gates on or swaps the destination track by occupancy (explicit tracks immutable [WRK-10]; destination occupancy is handled by arrival staging, the dest edge is exempt in Pathfinder),
        // preserves dead-end spur switchback ([WRK-02]) as an async second phase, and may re-latch ONLY auto-selected legs to a free [WRK-05] candidate after the stored track proved unroutable.
        // Returns true only when a departure was adopted this call. isPending == true means a background search is in flight or deliberately waiting (co-pilot standby / back-off): the caller must do nothing.
        private bool TryAdvanceToNextItineraryLeg(AtoBHaulTask task, out bool isPending)
        {
            isPending = false;
            if (task == null) return false;

            if (IsIntermediateAdvanceStructurallyImpossible(task))
            {
                task.ClearAdvanceSearch();
                return false;
            }

            int nextIdx = task.CurrentLegIndex + 1;
            var nextLeg = task.Legs[nextIdx];

            if (task.AdvancePathTask != null)
            {
                if (task.AdvanceLegIndex != nextIdx)
                {
                    task.ClearAdvanceSearch();
                    isPending = true;
                    return false;
                }
                if (!task.AdvancePathTask.IsCompleted)
                {
                    isPending = true;
                    return false;
                }
                return CompleteIntermediateAdvanceSearch(task, nextIdx, nextLeg, out isPending);
            }

            return BeginIntermediateAdvanceSearch(task, nextIdx, nextLeg, out isPending);
        }

        private bool BeginIntermediateAdvanceSearch(AtoBHaulTask task, int nextIdx, ItineraryLeg nextLeg, out bool isPending)
        {
            isPending = false;

            // Co-pilot standby: wait silently for the player to re-engage (no search, no toast, no spin).
            if (task.Engineer.State == EngineState.PlayerCoPilotStandby)
            {
                isPending = true;
                return false;
            }

            RailTrack originTrack = GetCurrentTrack(task.LeadLocomotive) ?? nextLeg.OriginTrack;
            if (originTrack == null) return false;

            List<TrainCar> consist;
            float totalLen;
            float totalMass;
            GetConsistMetrics(task.LeadLocomotive, out consist, out totalLen, out totalMass);

            var candidates = new List<RailTrack>();
            RailTrack storedTrack = nextLeg.DestinationTrack;
            if (storedTrack != null)
            {
                candidates.Add(storedTrack);
            }
            if (storedTrack == null || nextLeg.IsAutoSelectedTrack)
            {
                // [WRK-05] tiered/proximity-sorted, zero pathfinding; the output list MUST be allocated here (Collect returns early on null).
                var alternates = new List<RailTrack>();
                CollectWorkerArrivalCandidates(nextLeg.DestinationStation, totalLen, storedTrack, alternates);
                candidates.AddRange(alternates);
            }
            candidates.RemoveAll(c => c == null || c == originTrack);
            if (candidates.Count > MaxAdvanceRouteSearches)
            {
                candidates.RemoveRange(MaxAdvanceRouteSearches, candidates.Count - MaxAdvanceRouteSearches);
            }
            if (candidates.Count == 0) return false;

            task.ClearAdvanceSearch();
            task.AdvanceCandidates = candidates;
            task.AdvanceCandidateIndex = 0;
            task.AdvanceLegIndex = nextIdx;
            task.AdvanceOriginTrack = originTrack;
            task.AdvanceForward = GetPullingForwardOnTrack(task.LeadLocomotive, originTrack);
            LaunchAdvanceSearch(task, false);
            isPending = true;
            return false;
        }

        private void LaunchAdvanceSearch(AtoBHaulTask task, bool spurPhase)
        {
            RailTrack target = task.AdvanceCandidates[task.AdvanceCandidateIndex];
            task.AdvanceTargetTrack = target;
            task.AdvanceSpurPhase = spurPhase;

            var options = new PathfinderOptions
            {
                Requester = task.LeadLocomotive,
                RequesterTrainset = task.LeadLocomotive.trainset,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            RailTrack startTrack = task.AdvanceOriginTrack;
            bool startForward = task.AdvanceForward;
            if (spurPhase)
            {
                options.ExcludedTracks = new HashSet<RailTrack> { task.AdvanceOriginTrack };
                startTrack = task.AdvanceSpurStem;
                startForward = task.AdvanceSpurStemForward;
            }

            // [RGR-03]/[AIE-16]: FindPathAsync captures all main-thread snapshots (EnsureOptionsSnapshots) before Task.Run.
            task.AdvancePathTask = Pathfinder.FindPathAsync(startTrack, target, startForward, options);
        }

        private bool CompleteIntermediateAdvanceSearch(AtoBHaulTask task, int nextIdx, ItineraryLeg nextLeg, out bool isPending)
        {
            isPending = false;

            var searchTask = task.AdvancePathTask;
            RailPath path = null;
            if (searchTask.Status == System.Threading.Tasks.TaskStatus.RanToCompletion)
            {
                path = searchTask.Result;
            }
            else if (Main.IsDebugLoggingEnabled)
            {
                Main.LogDebug(string.Format("[WorkerManager] Async waypoint exit search ended with status {0}.", searchTask.Status));
            }
            task.AdvancePathTask = null;

            if (task.Engineer.State == EngineState.PlayerCoPilotStandby)
            {
                task.ClearAdvanceSearch();
                isPending = true;
                return false;
            }

            RailTrack target = task.AdvanceTargetTrack;
            RailTrack currentTrack = GetCurrentTrack(task.LeadLocomotive);
            bool stateValid = task.Status == HaulTaskStatus.WaitingAtIntermediate
                && task.AdvanceLegIndex == nextIdx
                && target != null
                && (currentTrack == null || currentTrack == task.AdvanceOriginTrack)
                && GetPullingForwardOnTrack(task.LeadLocomotive, task.AdvanceOriginTrack) == task.AdvanceForward;
            if (!stateValid)
            {
                task.ClearAdvanceSearch();
                task.IntermediateDwellExpireTime = Time.time + 1f;
                isPending = true;
                return false;
            }

            bool pathOk = path != null && path.IsValid && path.Tracks != null && path.Tracks.Count >= 2
                && path.Tracks[path.Tracks.Count - 1] == target;
            if (pathOk)
            {
                return AdoptIntermediateAdvancePath(task, nextIdx, nextLeg, target, path);
            }

            // Failure of the forward (pulling) search: try the dead-end spur switchback once per episode, asynchronously ([WRK-02]).
            if (!task.AdvanceSpurPhase)
            {
                if (!task.AdvanceSpurResolved)
                {
                    task.AdvanceSpurResolved = true;
                    Junction rearSwitch;
                    RailTrack stemTrack;
                    bool stemForward;
                    task.AdvanceSpurAvailable = TryResolveWorkerSpurSwitchback(
                        task.AdvanceOriginTrack, task.AdvanceForward, task.LeadLocomotive,
                        out rearSwitch, out stemTrack, out stemForward);
                    if (task.AdvanceSpurAvailable)
                    {
                        task.AdvanceSpurSwitch = rearSwitch;
                        task.AdvanceSpurStem = stemTrack;
                        task.AdvanceSpurStemForward = stemForward;
                    }
                }
                if (task.AdvanceSpurAvailable)
                {
                    LaunchAdvanceSearch(task, true);
                    isPending = true;
                    return false;
                }
            }

            // Next candidate immediately (no 15s wait between candidate attempts).
            task.AdvanceCandidateIndex++;
            if (task.AdvanceCandidates != null && task.AdvanceCandidateIndex < task.AdvanceCandidates.Count)
            {
                LaunchAdvanceSearch(task, false);
                isPending = true;
                return false;
            }

            // All candidates exhausted: transient failure; caller applies [WRK-11] 15s hold.
            task.ClearAdvanceSearch();
            return false;
        }

        private bool AdoptIntermediateAdvancePath(AtoBHaulTask task, int nextIdx, ItineraryLeg nextLeg, RailTrack destTrack, RailPath routePath)
        {
            RailTrack originTrack = task.AdvanceOriginTrack;

            Junction spurSwitch = null;
            RailTrack spurStemTrack = null;
            byte spurMainlineBranch = 0;
            if (task.AdvanceSpurPhase)
            {
                spurSwitch = task.AdvanceSpurSwitch;
                spurStemTrack = task.AdvanceSpurStem;
                Junction verifyJunc;
                byte mainBr = 0;
                if (SignalRegistry.TryGetJunctionBetweenTracks(spurStemTrack, routePath.Tracks[1], out verifyJunc, out mainBr))
                {
                    spurMainlineBranch = mainBr;
                }
            }
            task.ClearAdvanceSearch();

            nextLeg.OriginTrack = originTrack;
            if (nextLeg.OriginStation == null)
            {
                nextLeg.OriginStation = GetNearestStation(task.LeadLocomotive.transform.position);
            }

            // [WRK-10]: explicit tracks are never mutated; only null / auto-selected legs may latch the track that was actually routed.
            if (nextLeg.DestinationTrack == null || (nextLeg.IsAutoSelectedTrack && nextLeg.DestinationTrack != destTrack))
            {
                nextLeg.DestinationTrack = destTrack;
            }

            task.CurrentLegIndex = nextIdx;
            nextLeg.RouteDistance = routePath.TotalDistance;
            nextLeg.Status = ItineraryLegStatus.EnRoute;
            task.Status = HaulTaskStatus.EnRoute;
            task.IntermediateDwellExpireTime = 0f;

            string origName = nextLeg.OriginStation != null && nextLeg.OriginStation.stationInfo != null
                ? nextLeg.OriginStation.stationInfo.Name : "Waypoint";
            string destName = nextLeg.DestinationStation.stationInfo != null
                ? nextLeg.DestinationStation.stationInfo.Name : "Destination";
            task.StatusMessage = string.Format("Leg {0}/{1}: En route to {2}",
                nextIdx + 1, task.Legs.Count, destName);

            float pullingReverser = IsPullingAlongLocoForward(task.LeadLocomotive) ? 1.0f : -1.0f;
            task.Engineer.AssignNextItineraryLeg(
                routePath, origName, destName, destTrack.name, pullingReverser,
                spurSwitch, spurStemTrack, spurMainlineBranch);

            ShowToast(string.Format("AI Worker departing {0} -> {1} (Leg {2}/{3}) on track '{4}'.",
                origName, destName, nextIdx + 1, task.Legs.Count, destTrack.name));
            return true;
        }

        // Verbatim steps 2-3 of FindWorkerRouteWithSpurFallback ([WRK-02], O(1) originTrack junction check first per [WRK-06]); pathfinding-free, so it is safe on the main thread.
        private bool TryResolveWorkerSpurSwitchback(
            RailTrack originTrack, bool forwardOnTrack, TrainCar leadLoco,
            out Junction rearSwitchOut, out RailTrack stemTrackOut, out bool stemForwardTowardSwitch)
        {
            rearSwitchOut = null;
            stemTrackOut = null;
            stemForwardTowardSwitch = true;
            if (originTrack == null || RailGraph.Instance == null) return false;
            if (!RailGraph.Instance.IsInitialized) RailGraph.Instance.Initialize();

            var originEdge = RailGraph.Instance.GetEdge(originTrack);
            if (originEdge == null) return false;

            RailNode forwardNode = forwardOnTrack ? originEdge.ToNode : originEdge.FromNode;
            RailNode rearNode = forwardOnTrack ? originEdge.FromNode : originEdge.ToNode;
            if (forwardNode == null || rearNode == null || !forwardNode.IsDeadEnd) return false;

            Junction RearSwitch = null;
            Junction[] allJunctions = new Junction[] { originTrack.inJunction, originTrack.outJunction };
            for (int pass = 0; pass < 2 && RearSwitch == null; pass++)
            {
                if (pass == 1)
                {
                    allJunctions = UnityEngine.Object.FindObjectsOfType<Junction>();
                    if (allJunctions == null) break;
                }

                for (int i = 0; i < allJunctions.Length; i++)
                {
                    var j = allJunctions[i];
                    if (j == null || j.inBranch == null || j.inBranch.track == null || j.outBranches == null || j.outBranches.Count < 2)
                        continue;

                    bool matchesOutBranch = false;
                    for (int b = 0; b < j.outBranches.Count; b++)
                    {
                        if (j.outBranches[b] != null && j.outBranches[b].track == originTrack)
                        {
                            matchesOutBranch = true;
                            break;
                        }
                    }

                    if (matchesOutBranch)
                    {
                        float distToRear = Vector3.Distance(j.transform.position, rearNode.Position);
                        float distToFwd = Vector3.Distance(j.transform.position, forwardNode.Position);
                        if (distToRear <= distToFwd)
                        {
                            RearSwitch = j;
                            break;
                        }
                    }
                }
            }

            if (RearSwitch == null) return false;

            RailTrack candidateStem = RearSwitch.inBranch.track;
            var stemEdge = RailGraph.Instance.GetEdge(candidateStem);
            if (stemEdge == null || stemEdge.FromNode == null || stemEdge.ToNode == null) return false;

            Trainset reqTs = (leadLoco != null) ? leadLoco.trainset : null;
            if (RailGraph.Instance.IsTrackOccupied(candidateStem, null, reqTs) ||
                SignalRegistry.IsTrackOccupiedByPlayer(candidateStem, reqTs))
            {
                return false;
            }

            float dToNode = Vector3.Distance(stemEdge.ToNode.Position, RearSwitch.transform.position);
            float dFromNode = Vector3.Distance(stemEdge.FromNode.Position, RearSwitch.transform.position);
            stemForwardTowardSwitch = (dToNode <= dFromNode);
            rearSwitchOut = RearSwitch;
            stemTrackOut = candidateStem;
            return true;
        }

        /// <summary>
        /// Returns the first active worker task that currently has a pre-evaluated 30s blocked-track alert pending.
        /// </summary>
        public AtoBHaulTask GetFirstAlertingTask()
        {
            for (int i = 0; i < ActiveTasks.Count; i++)
            {
                var t = ActiveTasks[i];
                if (t != null && t.HasPendingBlockedAlert)
                {
                    return t;
                }
            }
            return null;
        }

        public bool ResumeWorkerTask(AtoBHaulTask task)
        {
            if (task == null || task.LeadLocomotive == null) return false;
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);

            if (task.Status == HaulTaskStatus.WaitingAtIntermediate)
            {
                // CRITICAL INVARIANT [WRK-11]: Manual resume failure keeps the task alive in a 15s exit retry hold
                if (IsIntermediateAdvanceStructurallyImpossible(task))
                {
                    _exitRetryToastedTasks.Remove(task);
                    CancelTask(task);
                    return false;
                }
                // CRITICAL INVARIANT [WRK-12]: Departure kickoff is gated while in co-pilot standby, so a radio resume must re-engage the AI driver first.
                if (task.Engineer != null && task.Engineer.State == EngineState.PlayerCoPilotStandby)
                {
                    task.Engineer.ResumeAIDriving(true);
                }
                _exitRetryToastedTasks.Remove(task); // fresh toast: explicit feedback on manual resume
                task.IntermediateDwellExpireTime = 0f;
                bool resumeAdvancePending;
                if (TryAdvanceToNextItineraryLeg(task, out resumeAdvancePending))
                {
                    _exitRetryToastedTasks.Remove(task);
                    return true;
                }
                if (resumeAdvancePending)
                {
                    return true; // search in flight (non-blocking); Update() adopts or holds on completion
                }
                if (IsIntermediateAdvanceStructurallyImpossible(task))
                {
                    _exitRetryToastedTasks.Remove(task);
                    CancelTask(task);
                    return false;
                }
                HoldIntermediateForExitRetry(task);
                return false;
            }

            if (task.Engineer != null)
            {
                task.HasBroadcastBlockedAlert = false;
                if (task.Engineer.State == EngineState.PlayerCoPilotStandby)
                {
                    task.Engineer.ResumeAIDriving(true);
                }
                else
                {
                    task.Engineer.Resume();
                }
                string destName = (task.DestinationStation != null && task.DestinationStation.stationInfo != null)
                    ? task.DestinationStation.stationInfo.Name
                    : "Destination";
                task.StatusMessage = "En route to " + destName;
                if (task.Engineer.State != EngineState.PlayerCoPilotStandby)
                {
                    ShowToast(string.Format("AI Worker ({0}) resuming run to {1}.", locoId, destName));
                }
                return true;
            }
            return false;
        }

        // CRITICAL INVARIANT [WRK-11]: Non-destructive intermediate exit retry (15s), one toast per hold episode, structural failures still cancel
        private readonly HashSet<AtoBHaulTask> _exitRetryToastedTasks = new HashSet<AtoBHaulTask>();

        private static bool IsIntermediateAdvanceStructurallyImpossible(AtoBHaulTask task)
        {
            if (task == null || task.LeadLocomotive == null || task.Engineer == null || !task.HasRemainingLegs)
                return true;
            int nextIdx = task.CurrentLegIndex + 1;
            if (task.Legs == null || nextIdx < 0 || nextIdx >= task.Legs.Count)
                return true;
            var nextLeg = task.Legs[nextIdx];
            return nextLeg == null || nextLeg.DestinationStation == null;
        }

        private void HoldIntermediateForExitRetry(AtoBHaulTask task)
        {
            if (task == null) return;
            const float ExitRetrySeconds = 15f;
            task.Status = HaulTaskStatus.WaitingAtIntermediate;
            task.IntermediateDwellExpireTime = Time.time + ExitRetrySeconds;
            string stopName = (task.CurrentLeg != null && task.CurrentLeg.DestinationStation != null && task.CurrentLeg.DestinationStation.stationInfo != null)
                ? task.CurrentLeg.DestinationStation.stationInfo.Name
                : "Waypoint";
            task.StatusMessage = string.Format("Holding at {0}: Exit route obstructed (retrying in {1:F0}s)", stopName, ExitRetrySeconds);
            if (_exitRetryToastedTasks.Add(task))
            {
                string locoName = task.LeadLocomotive != null ? ModCompatManager.GetCarId(task.LeadLocomotive) : "Consist";
                ShowToast(string.Format("AI Worker ({0}) holding at {1}: exit route or next arrival track blocked. Retrying every {2:F0}s.",
                    locoName, stopName, ExitRetrySeconds), "[AI Worker Dispatcher]", 10f, playWarningSound: true);
            }
        }

        // CRITICAL INVARIANT [WRK-10]: Approach staging context — explicit player track selection (IsAutoSelectedTrack == false) is never repathed
        private static AtoBHaulTask FindActiveTaskForEngineer(AIEngineer engineer)
        {
            if (engineer == null || Instance == null || Instance.ActiveTasks == null) return null;
            for (int i = 0; i < Instance.ActiveTasks.Count; i++)
            {
                var task = Instance.ActiveTasks[i];
                if (task != null && task.Engineer == engineer) return task;
            }
            return null;
        }

        public static bool TryGetWorkerApproachContext(AIEngineer engineer, out StationController station, out bool isAutoSelectedTrack)
        {
            station = null;
            isAutoSelectedTrack = false;
            var task = FindActiveTaskForEngineer(engineer);
            if (task == null || task.Status != HaulTaskStatus.EnRoute) return false;
            var leg = task.CurrentLeg;
            if (leg != null && leg.DestinationStation != null)
            {
                station = leg.DestinationStation;
                isAutoSelectedTrack = leg.IsAutoSelectedTrack;
            }
            else
            {
                station = task.DestinationStation;
                isAutoSelectedTrack = false;
            }
            return station != null;
        }

        // [WRK-05] tiering (transferIn -> storage -> all only if both empty), nearest-first to station office; Pass-1 filter only (length >= consist + 25m, unoccupied).
        // Pure candidate listing: performs NO pathfinding (routing is async in AIEngineer, [AIE-16]).
        public static void CollectWorkerArrivalCandidates(StationController station, float requiredLength, RailTrack excludeTrack, List<RailTrack> output)
        {
            if (output == null) return;
            output.Clear();
            if (station == null || station.AllStationTracks == null || station.AllStationTracks.Count == 0) return;

            List<RailTrack> tierTransferIn = new List<RailTrack>();
            List<RailTrack> tierStorage = new List<RailTrack>();
            for (int i = 0; i < station.AllStationTracks.Count; i++)
            {
                var track = station.AllStationTracks[i];
                if (track == null || track.curve == null) continue;
                if (station.transferInRailtracksGONames != null && station.transferInRailtracksGONames.Contains(track.name))
                    tierTransferIn.Add(track);
                else if (station.storageRailtracksGONames != null && station.storageRailtracksGONames.Contains(track.name))
                    tierStorage.Add(track);
            }
            Vector3 stationOfficePos = station.transform.position;
            System.Comparison<RailTrack> byStationProximity = (a, b) =>
                (a.curve.GetPointAt(0.5f) - stationOfficePos).sqrMagnitude.CompareTo((b.curve.GetPointAt(0.5f) - stationOfficePos).sqrMagnitude);
            tierTransferIn.Sort(byStationProximity);
            tierStorage.Sort(byStationProximity);
            output.AddRange(tierTransferIn);
            output.AddRange(tierStorage);
            if (output.Count == 0)
                output.AddRange(station.AllStationTracks);

            float bufferLength = requiredLength + 25f;
            for (int i = output.Count - 1; i >= 0; i--)
            {
                var t = output[i];
                if (t == null || t.curve == null || t == excludeTrack || t.curve.length < bufferLength || TrafficScheduler.IsTrackOccupied(t))
                    output.RemoveAt(i);
            }
        }

        public static void SetWorkerTaskStatusMessage(AIEngineer engineer, string message)
        {
            var task = FindActiveTaskForEngineer(engineer);
            if (task != null && !string.IsNullOrEmpty(message))
                task.StatusMessage = message;
        }

        public static void NotifyWorkerArrivalTrackAdopted(AIEngineer engineer, RailTrack newTrack)
        {
            var task = FindActiveTaskForEngineer(engineer);
            if (task == null || newTrack == null) return;
            if (task.CurrentLeg != null)
                task.CurrentLeg.DestinationTrack = newTrack;
            string stName = (task.CurrentLeg != null && task.CurrentLeg.DestinationStation != null && task.CurrentLeg.DestinationStation.stationInfo != null)
                ? task.CurrentLeg.DestinationStation.stationInfo.Name
                : "Destination";
            task.StatusMessage = string.Format("En route to {0} (auto-adopted track '{1}')", stName, newTrack.name);
            string locoName = task.LeadLocomotive != null ? ModCompatManager.GetCarId(task.LeadLocomotive) : "Consist";
            Instance.ShowToast(string.Format("AI Worker ({0}): arrival track at {1} occupied - rerouted to free track '{2}'.", locoName, stName, newTrack.name));
        }

        public bool RequestHoldAtNextLoop(AtoBHaulTask task)
        {
            if (task == null || task.Engineer == null || task.LeadLocomotive == null) return false;
            task.Engineer.RequestHoldAtNextLoop();
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            task.StatusMessage = "Ordered to hold at next loop";
            ShowToast(string.Format("AI Worker ({0}) will hold inside the next passing loop on route.", locoId));
            return true;
        }

        public bool EmergencyStopWorkerTask(AtoBHaulTask task)
        {
            if (task == null || task.Engineer == null || task.LeadLocomotive == null) return false;
            task.Engineer.EmergencyStop();
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            task.StatusMessage = "Emergency Stop (All Brakes Max)";
            ShowToast(string.Format("AI Worker ({0}) executing Emergency Stop! Holding max brakes until resumed.", locoId));
            return true;
        }

        public bool WaitOnBlockedTask(AtoBHaulTask task, float waitSeconds)
        {
            if (task == null || task.Engineer == null || task.LeadLocomotive == null) return false;
            task.Engineer.AcknowledgeWorkerBlockedWait(waitSeconds);
            task.HasBroadcastBlockedAlert = false;
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            int mins = Mathf.Max(1, Mathf.RoundToInt(waitSeconds / 60f));
            task.StatusMessage = string.Format("Holding in place ({0} min)", mins);
            ShowToast(string.Format("AI Worker ({0}) holding position for {1} min before re-evaluating.", locoId, mins));
            return true;
        }

        public bool RerouteBlockedTask(AtoBHaulTask task)
        {
            if (task == null || task.Engineer == null || task.LeadLocomotive == null) return false;
            if (!task.Engineer.ExecutePrecalculatedWorkerBypass()) return false;
            task.HasBroadcastBlockedAlert = false;
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            task.StatusMessage = "Rerouting via clear bypass";
            ShowToast(string.Format("AI Worker ({0}) rerouted via clear bypass track.", locoId));
            return true;
        }

        public bool BackupBlockedTaskToSiding(AtoBHaulTask task)
        {
            if (task == null || task.Engineer == null || task.LeadLocomotive == null) return false;
            if (!task.Engineer.ExecuteWorkerTrailingSidingBackup()) return false;
            task.HasBroadcastBlockedAlert = false;
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            task.StatusMessage = "Backing into trailing siding";
            ShowToast(string.Format("AI Worker ({0}) reversing into trailing siding to clear mainline.", locoId));
            return true;
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

                if (task.Status == HaulTaskStatus.WaitingAtIntermediate)
                {
                    // [WRK-12]: an in-flight background search is polled every frame regardless of the (already expired) dwell timer.
                    if (task.AdvancePathTask != null || Time.time >= task.IntermediateDwellExpireTime)
                    {
                        // CRITICAL INVARIANT [WRK-11]: Transient leg-advance failure (blocked exit route / no free arrival track) never cancels the task
                        bool waypointAdvancePending;
                        if (TryAdvanceToNextItineraryLeg(task, out waypointAdvancePending))
                        {
                            _exitRetryToastedTasks.Remove(task);
                        }
                        else if (waypointAdvancePending)
                        {
                            // Search in flight / co-pilot standby / back-off: no hold, no toast, no cancel.
                        }
                        else if (IsIntermediateAdvanceStructurallyImpossible(task))
                        {
                            // Structural failure (no engineer / no loco / no next leg): refund remaining legs and hand back control cleanly
                            _exitRetryToastedTasks.Remove(task);
                            CancelTask(task);
                        }
                        else
                        {
                            HoldIntermediateForExitRetry(task);
                        }
                    }
                    continue;
                }

                // CRITICAL INVARIANT [WRK-08]: Only broadcast 30s blocked-track toast after AIEngineer finishes async option pre-calculation
                if (task.Engineer != null)
                {
                    if (task.Engineer.HasPendingWorkerBlockedAlert)
                    {
                        if (!task.HasBroadcastBlockedAlert)
                        {
                            task.HasBroadcastBlockedAlert = true;
                            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
                            string destName = (task.DestinationStation != null && task.DestinationStation.stationInfo != null)
                                ? task.DestinationStation.stationInfo.Name
                                : "Destination";
                            string reason = !string.IsNullOrEmpty(task.Engineer.WorkerBlockedReason)
                                ? task.Engineer.WorkerBlockedReason
                                : "Track ahead obstructed";
                            task.StatusMessage = "Blocked >30s: " + reason;
                            ShowToast(
                                string.Format("Worker {0} (-> {1}) waiting >30s: {2}. Open Comms Radio [AI WORKER] to give orders.", locoId, destName, reason),
                                "[AI Worker Alert]",
                                12f,
                                playWarningSound: true);
                        }
                    }
                    else
                    {
                        task.HasBroadcastBlockedAlert = false;
                    }
                }

                // CRITICAL INVARIANT [NEW v0.2.8]: Strict Terminus Arrival Verification
                // Rely strictly on AIEngineer.HasArrivedAtDestination (2.0s stationary + rear bogie on destination track)
                // so worker trains never shut down prematurely while trailing cars still foul station throat switches.
                if (task.Engineer != null && task.Engineer.HasArrivedAtDestination)
                {
                    HandleTaskTerminusArrival(task.Engineer, task);
                }
            }
        }

        /// <summary>
        /// Displays an on-screen toast banner notification.
        /// </summary>
        public void ShowToast(string message, string headerTitle = "[AI Worker Dispatcher]", float duration = ToastDuration, string actionLabel = null, Action onAction = null, bool playWarningSound = false)
        {
            _toastMessage = message;
            _toastHeaderTitle = !string.IsNullOrEmpty(headerTitle) ? headerTitle : "[AI Worker Dispatcher]";
            _toastActionLabel = actionLabel ?? string.Empty;
            _toastAction = onAction;
            _toastExpireTime = Time.time + duration;
            if (playWarningSound)
            {
                WorkerAudio.PlayWarningSound();
            }
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

            bool hasAction = (_toastAction != null && !string.IsNullOrEmpty(_toastActionLabel));
            float bannerWidth = 560f;
            float bannerHeight = hasAction ? 92f : 68f;
            float screenX = (Screen.width - bannerWidth) * 0.5f;
            float screenY = 24f;

            Color prevColor = GUI.color;
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.95f * alpha);
            GUI.Box(new Rect(screenX, screenY, bannerWidth, bannerHeight), GUIContent.none);

            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUILayout.BeginArea(new Rect(screenX + 10f, screenY + 6f, bannerWidth - 20f, bannerHeight - 12f));
            GUILayout.Label(_toastHeaderTitle, _toastHeaderStyle);
            GUILayout.Label(_toastMessage, _toastTextStyle);

            if (hasAction)
            {
                GUILayout.Space(3f);
                if (GUILayout.Button(_toastActionLabel, GUILayout.Height(22)))
                {
                    var act = _toastAction;
                    _toastAction = null;
                    _toastMessage = string.Empty;
                    try
                    {
                        if (act != null) act.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[WorkerManager] Error invoking toast action: " + ex);
                    }
                }
            }
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
                    ModCompatManager.GetCarId(loco), loco.carType, consist.Count, totalLen, totalMass));
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
                        ModCompatManager.GetCarId(task.LeadLocomotive), dName, tName, task.CurrentSpeedKmh, task.RemainingDistance / 1000f));

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
