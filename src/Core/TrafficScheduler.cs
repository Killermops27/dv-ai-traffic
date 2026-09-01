using System;
using System.Collections.Generic;
using UnityEngine;
using DV.Logic.Job;
using AITraffic.Config;
using AITraffic.Fleet;
using AITraffic.Driver;
using AITraffic.Compat;
using AITraffic.Navigation;

namespace AITraffic.Core
{
    /// <summary>
    /// Represents a corridor between two stations for scheduled ambient or freight traffic.
    /// </summary>
    public struct TrafficCorridor
    {
        public string OriginYardId;
        public string DestinationYardId;
        public ConsistType PreferredConsist;

        public TrafficCorridor(string origin, string dest, ConsistType consist)
        {
            OriginYardId = origin;
            DestinationYardId = dest;
            PreferredConsist = consist;
        }
    }

    /// <summary>
    /// Schedules and coordinates periodic train departures across major stations in the valley.
    /// Dispatches Tier 1 ambient runs and Tier 2 job runs according to user settings and traffic density.
    /// </summary>
    public class TrafficScheduler
    {
        private static TrafficScheduler s_instance;
        public static TrafficScheduler Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new TrafficScheduler();
                }
                return s_instance;
            }
        }

        // Major valley corridors connecting industry hubs
        private static readonly TrafficCorridor[] s_corridors = new TrafficCorridor[]
        {
            // Harbor <-> Major Industry Corridors
            new TrafficCorridor("HB", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("MF", "HB", ConsistType.RegionalFreight),
            new TrafficCorridor("HB", "SW", ConsistType.PassengerCommuter),
            new TrafficCorridor("SW", "HB", ConsistType.PassengerCommuter),
            new TrafficCorridor("HB", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "HB", ConsistType.RegionalFreight),
            new TrafficCorridor("HB", "CSW", ConsistType.PassengerCommuter),
            new TrafficCorridor("CSW", "HB", ConsistType.PassengerCommuter),
            new TrafficCorridor("HB", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "HB", ConsistType.MainlineHeavy),
            new TrafficCorridor("HB", "FF", ConsistType.RegionalFreight),
            new TrafficCorridor("FF", "HB", ConsistType.RegionalFreight),
            new TrafficCorridor("HB", "MB", ConsistType.RegionalFreight),
            new TrafficCorridor("MB", "HB", ConsistType.RegionalFreight),
            new TrafficCorridor("HB", "OWN", ConsistType.MainlineHeavy),
            new TrafficCorridor("OWN", "HB", ConsistType.MainlineHeavy),

            // Steel Mill, Coal Mine & Iron Ore Corridors
            new TrafficCorridor("IME", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "IME", ConsistType.MainlineHeavy),
            new TrafficCorridor("IMW", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "IMW", ConsistType.MainlineHeavy),
            new TrafficCorridor("CM", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "CM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "SM", ConsistType.RegionalFreight),
            new TrafficCorridor("SM", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("MF", "SM", ConsistType.RegionalFreight),

            // Agriculture & Food Supply Chains
            new TrafficCorridor("FM", "FF", ConsistType.ShunterFreight),
            new TrafficCorridor("FF", "FM", ConsistType.ShunterFreight),
            new TrafficCorridor("FF", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "FF", ConsistType.RegionalFreight),
            new TrafficCorridor("FF", "CSW", ConsistType.RegionalFreight),
            new TrafficCorridor("CSW", "FF", ConsistType.RegionalFreight),
            new TrafficCorridor("FM", "CSW", ConsistType.PassengerCommuter),
            new TrafficCorridor("CSW", "FM", ConsistType.PassengerCommuter),

            // Machine Factory, Goods Factory, Military & Sawmill
            new TrafficCorridor("MF", "SW", ConsistType.ShunterFreight),
            new TrafficCorridor("SW", "MF", ConsistType.ShunterFreight),
            new TrafficCorridor("MF", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("MF", "MB", ConsistType.RegionalFreight),
            new TrafficCorridor("MB", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("SW", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "SW", ConsistType.RegionalFreight),
            new TrafficCorridor("SW", "FM", ConsistType.ShunterFreight),
            new TrafficCorridor("FM", "SW", ConsistType.ShunterFreight),

            // Oil Extraction & Refining
            new TrafficCorridor("OWN", "OWC", ConsistType.ShunterFreight),
            new TrafficCorridor("OWC", "OWN", ConsistType.ShunterFreight),
            new TrafficCorridor("OWC", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "OWC", ConsistType.MainlineHeavy),
            new TrafficCorridor("OWC", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "OWC", ConsistType.RegionalFreight),

            // City Cross-Valley Routes
            new TrafficCorridor("CSW", "GF", ConsistType.RegionalFreight),
            new TrafficCorridor("GF", "CSW", ConsistType.RegionalFreight),
            new TrafficCorridor("CSW", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("MF", "CSW", ConsistType.RegionalFreight),
            new TrafficCorridor("CSW", "SW", ConsistType.PassengerCommuter),
            new TrafficCorridor("SW", "CSW", ConsistType.PassengerCommuter)
        };

        private float _lastDispatchTime = -9999f;
        private float _dispatchIntervalSeconds = 180f; // 3 minutes default

        public float LastDispatchTime { get { return _lastDispatchTime; } }
        public float DispatchIntervalSeconds { get { return _dispatchIntervalSeconds; } }

        private readonly System.Random _rng = new System.Random();
        private readonly List<string> _recentDestinations = new List<string>();

        public TrafficScheduler()
        {
            _lastDispatchTime = -9999f;
        }

        private void RecordDestination(string yardId)
        {
            if (string.IsNullOrEmpty(yardId)) return;
            _recentDestinations.Remove(yardId);
            _recentDestinations.Add(yardId);
            while (_recentDestinations.Count > 4)
            {
                _recentDestinations.RemoveAt(0);
            }
        }

        private float CalculateCorridorScore(TrafficCorridor corridor, Vector3 playerPos)
        {
            StationController origin = FindStation(corridor.OriginYardId);
            StationController dest = FindStation(corridor.DestinationYardId);
            if (origin == null || dest == null) return 99999f;

            float distOrigin = Vector3.Distance(origin.transform.position, playerPos);
            float distDest = Vector3.Distance(dest.transform.position, playerPos);

            // Avoid spawning directly on top of the player (< 350m)
            if (distOrigin < 350f)
                return 80000f;

            float score = 0f;

            // Origin distance: Optimal spawn distance is between 500m and 2500m from the player
            if (distOrigin >= 500f && distOrigin <= 2500f)
            {
                score += distOrigin;
            }
            else if (distOrigin < 500f)
            {
                score += 3000f + (500f - distOrigin) * 5f;
            }
            else // distOrigin > 2500f
            {
                score += 2500f + (distOrigin - 2500f) * 1.5f;
            }

            // Destination bonus: prioritize trains heading towards or past the player's general sector
            if (distDest >= 600f && distDest <= 3500f)
            {
                score -= 400f;
            }

            // Anti-repetition penalty for recent destinations
            if (_recentDestinations.Contains(corridor.DestinationYardId))
            {
                int recencyIdx = _recentDestinations.IndexOf(corridor.DestinationYardId);
                score += 3000f * (recencyIdx + 1);
            }

            // Small random jitter (+-200m) to ensure rich variety among nearby candidate corridors
            score += (float)(_rng.NextDouble() * 400.0 - 200.0);

            return score;
        }

        private static ConsistType InferConsistType(StationController origin, StationController dest, System.Random rng)
        {
            string oYard = (origin != null && origin.stationInfo != null) ? (origin.stationInfo.YardID ?? "").ToUpperInvariant() : "";
            string dYard = (dest != null && dest.stationInfo != null) ? (dest.stationInfo.YardID ?? "").ToUpperInvariant() : "";

            // Heavy bulk materials: Mines, Steel Mill, Coal, Oil
            if (oYard.Contains("SM") || dYard.Contains("SM") ||
                oYard.Contains("CM") || dYard.Contains("CM") ||
                oYard.Contains("IM") || dYard.Contains("IM") ||
                oYard.Contains("OW") || dYard.Contains("OW"))
            {
                return (rng.NextDouble() < 0.65) ? ConsistType.MainlineHeavy : ConsistType.RegionalFreight;
            }

            // Passenger commuter between cities, sawmill, farm, harbor
            if (oYard.Contains("CW") || oYard.Contains("CSW") || dYard.Contains("CW") || dYard.Contains("CSW"))
            {
                if (rng.NextDouble() < 0.40)
                    return ConsistType.PassengerCommuter;
            }

            // Agriculture / Food
            if (oYard.Contains("FF") || dYard.Contains("FF") || oYard.Contains("FM") || dYard.Contains("FM") || oYard.Contains("FR") || dYard.Contains("FR"))
            {
                return (rng.NextDouble() < 0.50) ? ConsistType.ShunterFreight : ConsistType.RegionalFreight;
            }

            // Manufacturing & general freight
            if (rng.NextDouble() < 0.60)
                return ConsistType.RegionalFreight;
            if (rng.NextDouble() < 0.30)
                return ConsistType.ShunterFreight;

            return ConsistType.MainlineHeavy;
        }

        /// <summary>
        /// Periodic scheduler tick called by TrafficManager.
        /// </summary>
        /// <param name="deltaTime">Elapsed delta time.</param>
        /// <param name="activeTrainCount">Current count of active AI trains.</param>
        /// <param name="maxTrains">Target maximum trains allowed by density setting.</param>
        /// <param name="settings">User configuration settings.</param>
        public void UpdateScheduler(float deltaTime, int activeTrainCount, int maxTrains, AITrafficSettings settings)
        {
            if (settings == null || settings.Density == TrafficDensity.Off || maxTrains <= 0)
                return;

            if (activeTrainCount >= maxTrains)
                return;

            // Compute dynamic dispatch interval based on density
            _dispatchIntervalSeconds = TrafficDensityExtensions.switch_density(settings.Density);

            if (Time.time - _lastDispatchTime >= _dispatchIntervalSeconds)
            {
                _lastDispatchTime = Time.time;
                TryDispatchTrain(settings, activeTrainCount, maxTrains);
            }
        }

        /// <summary>
        /// Forces an immediate dispatch of an AI train regardless of interval.
        /// </summary>
        public bool ForceDispatch(AITrafficSettings settings)
        {
            _lastDispatchTime = Time.time;
            TrafficMode mode = settings != null ? settings.Mode : TrafficMode.Hybrid;
            if (mode == TrafficMode.RealJobsOnly)
                return DispatchTier2Job();
            return DispatchTier1Ambient();
        }

        private void TryDispatchTrain(AITrafficSettings settings, int currentCount, int maxCount)
        {
            try
            {
                TrafficMode mode = settings != null ? settings.Mode : TrafficMode.Hybrid;

                if (mode == TrafficMode.RealJobsOnly)
                {
                    DispatchTier2Job();
                }
                else if (mode == TrafficMode.AmbientOnly)
                {
                    DispatchTier1Ambient();
                }
                else // Hybrid
                {
                    // In Hybrid mode, try to dispatch a real job first; fallback to ambient if none available
                    bool jobDispatched = DispatchTier2Job();
                    if (!jobDispatched && currentCount < maxCount)
                    {
                        DispatchTier1Ambient();
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrafficScheduler.TryDispatchTrain: {0}", ex));
            }
        }

        /// <summary>
        /// Dispatches a Tier 1 ambient AI train along a major corridor, prioritizing corridors near the player with high destination variety.
        /// </summary>
        public bool DispatchTier1Ambient()
        {
            try
            {
                if (StationController.allStations == null || StationController.allStations.Count == 0)
                    return false;

                Vector3 playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;

                // 1. Shuffle all predefined corridors
                var corridorList = new List<TrafficCorridor>(s_corridors);
                for (int i = corridorList.Count - 1; i > 0; i--)
                {
                    int swapIdx = _rng.Next(0, i + 1);
                    var temp = corridorList[i];
                    corridorList[i] = corridorList[swapIdx];
                    corridorList[swapIdx] = temp;
                }

                // 2. Sort corridors by player proximity score and destination anti-repetition
                if (playerPos != Vector3.zero)
                {
                    corridorList.Sort((a, b) =>
                    {
                        float scoreA = CalculateCorridorScore(a, playerPos);
                        float scoreB = CalculateCorridorScore(b, playerPos);
                        return scoreA.CompareTo(scoreB);
                    });
                }

                for (int c = 0; c < corridorList.Count; c++)
                {
                    TrafficCorridor corridor = corridorList[c];
                    StationController originStation = FindStation(corridor.OriginYardId);
                    StationController destStation = FindStation(corridor.DestinationYardId);

                    if (originStation == null || destStation == null) continue;

                    // Avoid spawning directly on top of the player
                    if (playerPos != Vector3.zero)
                    {
                        float distToPlayer = Vector3.Distance(originStation.transform.position, playerPos);
                        if (distToPlayer < 350f)
                            continue;
                    }

                    RailPath routePath;
                    RailTrack spawnTrack = FindClearDepartureTrack(originStation, destStation, 100f, out routePath, corridor.PreferredConsist);
                    if (spawnTrack == null || routePath == null || !routePath.IsValid)
                        continue;

                    double startSpan = 15.0;
                    bool flipConsist = false;

                    if (routePath.Tracks != null && routePath.Tracks.Count > 1)
                    {
                        var track0 = routePath.Tracks[0];
                        var track1 = routePath.Tracks[1];
                        if (track0 != null && track0.curve != null && track1 != null && track1.curve != null)
                        {
                            Vector3 curStart = track0.curve.GetPointAt(0.0f);
                            Vector3 curEnd = track0.curve.GetPointAt(1.0f);
                            Vector3 nextMid = track1.curve.GetPointAt(0.5f);

                            bool forward = Vector3.Distance(curEnd, nextMid) <= Vector3.Distance(curStart, nextMid);
                            float trackLen = track0.curve.length;

                            if (forward)
                            {
                                startSpan = 15.0;
                                flipConsist = false;
                            }
                            else
                            {
                                startSpan = Math.Max(15.0, trackLen - 15.0);
                                flipConsist = true;
                            }
                        }
                    }
                    else if (routePath.Edges != null && routePath.Edges.Count > 0 && routePath.Nodes != null && routePath.Nodes.Count > 1)
                    {
                        var firstEdge = routePath.Edges[0];
                        var firstNode = routePath.Nodes[0];
                        bool forward = (firstNode == firstEdge.FromNode);
                        if (!forward)
                        {
                            float trackLen = spawnTrack.curve != null ? spawnTrack.curve.length : 100f;
                            startSpan = Math.Max(15.0, trackLen - 15.0);
                            flipConsist = true;
                        }
                    }

                    // Spawn ambient consist
                    AIEngineer engineer = TrainSpawner.SpawnAITrain(spawnTrack, corridor.PreferredConsist, startSpan: startSpan, flipTrainConsist: flipConsist, rng: _rng);
                    if (engineer == null)
                        continue;

                    // Set route and destination
                    engineer.CurrentPath = routePath;
                    string origName = (originStation.stationInfo != null && !string.IsNullOrEmpty(originStation.stationInfo.Name)) 
                        ? string.Format("{0} [{1}]", originStation.stationInfo.Name, corridor.OriginYardId)
                        : corridor.OriginYardId;
                    string destName = (destStation.stationInfo != null && !string.IsNullOrEmpty(destStation.stationInfo.Name)) 
                        ? string.Format("{0} [{1}]", destStation.stationInfo.Name, corridor.DestinationYardId)
                        : corridor.DestinationYardId;

                    engineer.OriginStationName = origName;
                    engineer.DestinationStationName = destName;
                    engineer.DestinationTrackName = (routePath.Tracks != null && routePath.Tracks.Count > 0) ? routePath.Tracks[routePath.Tracks.Count - 1].name : "";
                    engineer.DistanceToDestination = routePath.TotalDistance;
                    engineer.IsStationDestination = false;
                    engineer.IsTerminusDestination = true;

                    RecordDestination(corridor.DestinationYardId);

                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Dispatched Tier 1 Ambient Train ({0} -> {1}, Consist: {2}) on track '{3}' (Route: {4:F0}m).",
                            corridor.OriginYardId, corridor.DestinationYardId, corridor.PreferredConsist, spawnTrack.name, routePath.TotalDistance));

                    return true;
                }

                // 3. Dynamic Fallback: Query all stations in the valley, prioritizing origin stations near the player and randomized destinations
                var allStationList = new List<StationController>(StationController.allStations);
                for (int i = allStationList.Count - 1; i > 0; i--)
                {
                    int swapIdx = _rng.Next(0, i + 1);
                    var temp = allStationList[i];
                    allStationList[i] = allStationList[swapIdx];
                    allStationList[swapIdx] = temp;
                }

                if (playerPos != Vector3.zero)
                {
                    allStationList.Sort((stA, stB) =>
                    {
                        float dA = Vector3.Distance(stA.transform.position, playerPos);
                        float dB = Vector3.Distance(stB.transform.position, playerPos);

                        float scoreA = (dA < 350f) ? 99999f : (dA <= 2500f ? dA : 2500f + (dA - 2500f) * 2f);
                        float scoreB = (dB < 350f) ? 99999f : (dB <= 2500f ? dB : 2500f + (dB - 2500f) * 2f);
                        return scoreA.CompareTo(scoreB);
                    });
                }

                for (int s = 0; s < allStationList.Count; s++)
                {
                    var station = allStationList[s];
                    if (station == null) continue;

                    // Avoid spawning directly on top of the player
                    if (playerPos != Vector3.zero && Vector3.Distance(station.transform.position, playerPos) < 350f)
                        continue;

                    // Build shuffled destination candidates
                    var destCandidates = new List<StationController>(StationController.allStations);
                    destCandidates.Remove(station);

                    for (int i = destCandidates.Count - 1; i > 0; i--)
                    {
                        int swapIdx = _rng.Next(0, i + 1);
                        var temp = destCandidates[i];
                        destCandidates[i] = destCandidates[swapIdx];
                        destCandidates[swapIdx] = temp;
                    }

                    // Sort to avoid repeating recent destinations
                    destCandidates.Sort((dA, dB) =>
                    {
                        string yardA = (dA.stationInfo != null) ? (dA.stationInfo.YardID ?? "") : "";
                        string yardB = (dB.stationInfo != null) ? (dB.stationInfo.YardID ?? "") : "";
                        int recA = _recentDestinations.Contains(yardA) ? _recentDestinations.IndexOf(yardA) + 1 : 0;
                        int recB = _recentDestinations.Contains(yardB) ? _recentDestinations.IndexOf(yardB) + 1 : 0;
                        return recA.CompareTo(recB);
                    });

                    for (int d = 0; d < destCandidates.Count; d++)
                    {
                        var candidateDest = destCandidates[d];
                        if (candidateDest == null) continue;

                        float distBetween = Vector3.Distance(station.transform.position, candidateDest.transform.position);
                        if (distBetween < 1000f) continue;

                        ConsistType inferredConsist = InferConsistType(station, candidateDest, _rng);

                        RailPath fallbackPath;
                        RailTrack spawnTrack = FindClearDepartureTrack(station, candidateDest, 80f, out fallbackPath, inferredConsist);
                        if (spawnTrack != null && fallbackPath != null && fallbackPath.IsValid)
                        {
                            double startSpan = 15.0;
                            bool flipConsist = false;

                            if (fallbackPath.Tracks != null && fallbackPath.Tracks.Count > 1)
                            {
                                var track0 = fallbackPath.Tracks[0];
                                var track1 = fallbackPath.Tracks[1];
                                if (track0 != null && track0.curve != null && track1 != null && track1.curve != null)
                                {
                                    Vector3 curStart = track0.curve.GetPointAt(0.0f);
                                    Vector3 curEnd = track0.curve.GetPointAt(1.0f);
                                    Vector3 nextMid = track1.curve.GetPointAt(0.5f);

                                    bool forward = Vector3.Distance(curEnd, nextMid) <= Vector3.Distance(curStart, nextMid);
                                    float trackLen = track0.curve.length;

                                    if (forward)
                                    {
                                        startSpan = 15.0;
                                        flipConsist = false;
                                    }
                                    else
                                    {
                                        startSpan = Math.Max(15.0, trackLen - 15.0);
                                        flipConsist = true;
                                    }
                                }
                            }
                            else if (fallbackPath.Edges != null && fallbackPath.Edges.Count > 0 && fallbackPath.Nodes != null && fallbackPath.Nodes.Count > 1)
                            {
                                var firstEdge = fallbackPath.Edges[0];
                                var firstNode = fallbackPath.Nodes[0];
                                bool forward = (firstNode == firstEdge.FromNode);
                                if (!forward)
                                {
                                    float trackLen = spawnTrack.curve != null ? spawnTrack.curve.length : 100f;
                                    startSpan = Math.Max(15.0, trackLen - 15.0);
                                    flipConsist = true;
                                }
                            }

                            AIEngineer engineer = TrainSpawner.SpawnAITrain(spawnTrack, inferredConsist, startSpan: startSpan, flipTrainConsist: flipConsist, rng: _rng);
                            if (engineer != null)
                            {
                                engineer.CurrentPath = fallbackPath;
                                string origYard = (station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.YardID)) ? station.stationInfo.YardID : "ORIG";
                                string destYard = (candidateDest.stationInfo != null && !string.IsNullOrEmpty(candidateDest.stationInfo.YardID)) ? candidateDest.stationInfo.YardID : "DEST";
                                string origName = (station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.Name)) 
                                    ? string.Format("{0} [{1}]", station.stationInfo.Name, origYard)
                                    : origYard;
                                string destName = (candidateDest.stationInfo != null && !string.IsNullOrEmpty(candidateDest.stationInfo.Name))
                                    ? string.Format("{0} [{1}]", candidateDest.stationInfo.Name, destYard)
                                    : destYard;

                                engineer.OriginStationName = origName;
                                engineer.DestinationStationName = destName;
                                engineer.DestinationTrackName = (fallbackPath.Tracks != null && fallbackPath.Tracks.Count > 0) ? fallbackPath.Tracks[fallbackPath.Tracks.Count - 1].name : "";
                                engineer.DistanceToDestination = fallbackPath.TotalDistance;
                                engineer.IsStationDestination = false;
                                engineer.IsTerminusDestination = true;

                                RecordDestination(destYard);

                                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                    Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Dispatched Dynamic Ambient Train ({0} -> {1}, Consist: {2}) on track '{3}' (Route: {4:F0}m).",
                                        origName, destName, inferredConsist, spawnTrack.name, fallbackPath.TotalDistance));

                                return true;
                            }
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error dispatching Tier 1 ambient train: {0}", ex));
                return false;
            }
        }

        /// <summary>
        /// Dispatches a Tier 2 real-job AI train using JobOperator.
        /// </summary>
        public bool DispatchTier2Job()
        {
            try
            {
                var availableJobs = JobOperator.Instance.ScanAvailableJobs();
                if (availableJobs == null || availableJobs.Count == 0)
                    return false;

                // Pick first suitable job
                Job targetJob = availableJobs[0];
                var assignment = JobOperator.Instance.ClaimAndDispatchJob(targetJob);
                return assignment != null;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error dispatching Tier 2 job train: {0}", ex));
                return false;
            }
        }

        #region Helper Methods

        /// <summary>
        /// Checks whether a rail track is currently occupied by any rolling stock or logic track cars.
        /// </summary>
        public static bool IsTrackOccupied(RailTrack track)
        {
            if (track == null) return true;

            // 1. Check LogicTrack reservation / cars if available
            var logicTrack = ModCompatManager.GetLogicTrack(track);
            if (logicTrack != null && !logicTrack.IsFree())
            {
                return true;
            }

            // 2. Check all physical train cars currently in the world
            if (CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
            {
                var allCars = CarSpawner.Instance.AllCars;
                int count = allCars.Count;
                for (int i = 0; i < count; i++)
                {
                    var car = allCars[i];
                    if (car == null) continue;

                    if (car.FrontBogie != null && car.FrontBogie.track == track)
                        return true;
                    if (car.RearBogie != null && car.RearBogie.track == track)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a track ends in a buffer stop / dead-end bumper on EITHER side.
        /// </summary>
        public static bool IsDeadEndTrack(RailTrack track)
        {
            if (track == null || track.curve == null) return true;

            // 1. Physical connection check via DV RailTrack properties
            if (!track.inIsConnected || !track.outIsConnected)
            {
                return true;
            }

            // 2. RailGraph topological node check
            if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsInitialized)
            {
                var edge = AITraffic.Navigation.RailGraph.Instance.GetEdge(track);
                if (edge != null)
                {
                    if (edge.FromNode == null || edge.FromNode.IsDeadEnd || edge.FromNode.IncidentEdges == null || edge.FromNode.IncidentEdges.Count <= 1)
                        return true;

                    if (edge.ToNode == null || edge.ToNode.IsDeadEnd || edge.ToNode.IncidentEdges == null || edge.ToNode.IncidentEdges.Count <= 1)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines if a track is a dedicated through / mainline track (e.g. [#]HB-1, [#]MF-1, or connecting corridor)
        /// where persistent procedural wagons and yard jobs are NEVER spawned.
        /// </summary>
        public static bool IsThroughOrMainlineTrack(RailTrack track)
        {
            if (track == null || track.curve == null) return false;

            // Buffer stop / dead-end sidings are NEVER through tracks
            if (IsDeadEndTrack(track))
            {
                return false;
            }

            // 1. Check LogicTrack if registered in station
            var logicTrack = ModCompatManager.GetLogicTrack(track);
            if (logicTrack != null && logicTrack.ID != null)
            {
                string part = logicTrack.ID.TrackPartOnly;
                string display = logicTrack.ID.FullDisplayID ?? "";

                // Mainline through track ([#] or TrackID.MAIN_LINE_TYPE)
                if (part == DV.Logic.Job.TrackID.MAIN_LINE_TYPE || display.Contains("[#]"))
                {
                    return true;
                }

                // Explicitly reject yard storage, loading, inbound/outbound transfer, parking tracks
                if (part == DV.Logic.Job.TrackID.STORAGE_TYPE ||
                    part == DV.Logic.Job.TrackID.LOADING_TYPE ||
                    part == DV.Logic.Job.TrackID.REGULAR_IN_TYPE ||
                    part == DV.Logic.Job.TrackID.REGULAR_OUT_TYPE ||
                    part == DV.Logic.Job.TrackID.PARKING_TYPE ||
                    part == DV.Logic.Job.TrackID.STORAGE_PASSENGER_TYPE ||
                    display.StartsWith("[Y]") || display.StartsWith("[S]") || display.StartsWith("[L]") || 
                    display.StartsWith("[I]") || display.StartsWith("[O]") || display.StartsWith("[P]") ||
                    display.StartsWith("[C]") || display.StartsWith("[E]") || display.StartsWith("[T]"))
                {
                    return false;
                }
            }

            // 2. Name heuristics
            string name = track.name ?? "";
            if (name.Contains("[#]") || name.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (name.StartsWith("[Y]") || name.StartsWith("[S]") || name.StartsWith("[L]") || 
                name.StartsWith("[I]") || name.StartsWith("[O]") || name.StartsWith("[P]") ||
                name.StartsWith("[C]") || name.StartsWith("[E]") || name.StartsWith("[T]") ||
                name.IndexOf("Siding", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Spur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Stub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("Buffer", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return true;
        }

        private static StationController FindStation(string yardId)
        {
            if (string.IsNullOrEmpty(yardId) || StationController.allStations == null)
                return null;

            for (int i = 0; i < StationController.allStations.Count; i++)
            {
                var sc = StationController.allStations[i];
                if (sc != null && sc.stationInfo != null)
                {
                    string sYard = sc.stationInfo.YardID ?? "";
                    string sName = sc.stationInfo.Name ?? "";

                    if (string.Equals(sYard, yardId, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(sName, yardId, StringComparison.OrdinalIgnoreCase))
                    {
                        return sc;
                    }

                    // Handle common aliases: CW / CSW (City South West / City West)
                    if ((yardId.Equals("CW", StringComparison.OrdinalIgnoreCase) || yardId.Equals("CSW", StringComparison.OrdinalIgnoreCase)) &&
                        (sYard.Equals("CW", StringComparison.OrdinalIgnoreCase) || sYard.Equals("CSW", StringComparison.OrdinalIgnoreCase) ||
                         sName.IndexOf("City South", StringComparison.OrdinalIgnoreCase) >= 0 || sName.IndexOf("City West", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return sc;
                    }

                    // Handle Farm / Forest Meadow aliases
                    if ((yardId.Equals("FM", StringComparison.OrdinalIgnoreCase) || yardId.Equals("FR", StringComparison.OrdinalIgnoreCase)) &&
                        (sYard.Equals("FM", StringComparison.OrdinalIgnoreCase) || sYard.Equals("FR", StringComparison.OrdinalIgnoreCase) ||
                         sName.IndexOf("Farm", StringComparison.OrdinalIgnoreCase) >= 0 || sName.IndexOf("Forest", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return sc;
                    }
                }
            }

            return null;
        }

        private static bool IsInboundTrack(RailTrack t, StationController station)
        {
            if (t == null) return false;

            // 1. Direct StationController transferInRailtracksGONames match
            if (station != null && station.transferInRailtracksGONames != null)
            {
                string tName = t.name ?? "";
                string goName = (t.gameObject != null) ? t.gameObject.name : "";
                if (station.transferInRailtracksGONames.Contains(tName) || station.transferInRailtracksGONames.Contains(goName))
                    return true;
            }

            // 2. Logic Track Check
            var lt = ModCompatManager.GetLogicTrack(t);
            if (lt != null)
            {
                if (station != null && station.logicStation != null && station.logicStation.yard != null && station.logicStation.yard.TransferInTracks != null)
                {
                    if (station.logicStation.yard.TransferInTracks.Contains(lt))
                        return true;
                }

                if (lt.ID != null)
                {
                    if (lt.ID.TrackPartOnly == DV.Logic.Job.TrackID.REGULAR_IN_TYPE || lt.ID.TrackPartOnly == "I")
                        return true;

                    string disp = lt.ID.FullDisplayID ?? "";
                    if (disp.EndsWith("-I", StringComparison.OrdinalIgnoreCase) || disp.Contains("[I]") || disp.Contains("-I-"))
                        return true;
                }
            }

            // 3. Name Heuristics (e.g. "[Y]_[FF]_[C-04-I]", "[FF-C-4-I]", "Track C4I")
            string n = t.name ?? "";
            if (n.IndexOf("-I]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("_I]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("-I-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("_I_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("[I]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Inbound", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Input", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static bool IsPlatformTrack(RailTrack t, StationController station)
        {
            if (t == null) return false;
            if (ModCompatManager.IsPlatformTrack(t)) return true;

            var lt = ModCompatManager.GetLogicTrack(t);
            if (lt != null && lt.ID != null)
            {
                if (lt.ID.TrackPartOnly == DV.Logic.Job.TrackID.LOADING_PASSENGER_TYPE ||
                    lt.ID.TrackPartOnly == DV.Logic.Job.TrackID.STORAGE_PASSENGER_TYPE ||
                    lt.ID.TrackPartOnly == "LP" || lt.ID.TrackPartOnly == "SP")
                {
                    return true;
                }

                string disp = lt.ID.FullDisplayID ?? "";
                if (disp.Contains("LP") || disp.Contains("SP") || disp.Contains("[P]") || disp.Contains("-P"))
                    return true;
            }

            string n = t.name ?? "";
            if (n.IndexOf("LP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("SP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("[P]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("-P]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Passenger", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        private static bool IsPassingOrLoopTrack(RailTrack t)
        {
            if (t == null) return false;
            string n = t.name ?? "";
            return n.Contains("[S]") || n.Contains("[#]") ||
                   n.IndexOf("Loop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Pass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsYardStorageTrack(RailTrack t, StationController station)
        {
            if (t == null) return false;

            if (station != null && station.storageRailtracksGONames != null)
            {
                string tName = t.name ?? "";
                string goName = (t.gameObject != null) ? t.gameObject.name : "";
                if (station.storageRailtracksGONames.Contains(tName) || station.storageRailtracksGONames.Contains(goName))
                    return true;
            }

            var lt = ModCompatManager.GetLogicTrack(t);
            if (lt != null)
            {
                if (station != null && station.logicStation != null && station.logicStation.yard != null && station.logicStation.yard.StorageTracks != null)
                {
                    if (station.logicStation.yard.StorageTracks.Contains(lt))
                        return true;
                }

                if (lt.ID != null && (lt.ID.TrackPartOnly == DV.Logic.Job.TrackID.STORAGE_TYPE || lt.ID.TrackPartOnly == "S"))
                    return true;
            }

            string n = t.name ?? "";
            return n.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("_S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Siding", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<RailTrack> GetCandidateDestinationTracks(StationController destStation, ConsistType consistType = ConsistType.RegionalFreight)
        {
            var results = new List<RailTrack>();
            if (destStation == null) return results;

            string yardId = destStation.stationInfo != null ? destStation.stationInfo.YardID : "";
            bool isPax = (consistType == ConsistType.PassengerCommuter);

            // 1. Scan tracks assigned to destination station
            if (destStation.AllStationTracks != null)
            {
                if (isPax)
                {
                    // Pass 1: Passenger Platform tracks
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t)) continue;
                        if (IsPlatformTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Passing loops / Station sidings
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t)) continue;
                        if (!IsInboundTrack(t, destStation) && !IsYardStorageTrack(t, destStation) && IsPassingOrLoopTrack(t) && !results.Contains(t))
                            results.Add(t);
                    }
                }
                else
                {
                    // FREIGHT TRAIN
                    // Pass 1: Inbound [I] tracks (e.g. C-04-I)
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t)) continue;
                        // STRICT GUARD: Never route freight to a passenger platform
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsInboundTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Yard storage / classification tracks [S]
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t)) continue;
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsYardStorageTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 3: Passing loops / mainline station loops
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t)) continue;
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsPassingOrLoopTrack(t) && !results.Contains(t))
                            results.Add(t);
                    }
                }
            }

            // 2. Scan RailGraph for tracks matching destination yard ID
            if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.Edges != null)
            {
                var edges = AITraffic.Navigation.RailGraph.Instance.Edges;
                for (int i = 0; i < edges.Count; i++)
                {
                    var edge = edges[i];
                    if (edge == null || edge.Track == null) continue;
                    var t = edge.Track;
                    if (t.curve == null || IsTrackOccupied(t)) continue;

                    string name = t.name ?? "";
                    if (!string.IsNullOrEmpty(yardId) && name.IndexOf(yardId, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (isPax)
                        {
                            if (IsPlatformTrack(t, destStation) && !results.Contains(t))
                                results.Add(t);
                        }
                        else
                        {
                            if (IsPlatformTrack(t, destStation)) continue; // STRICT GUARD

                            if ((IsInboundTrack(t, destStation) || IsYardStorageTrack(t, destStation) || IsPassingOrLoopTrack(t)) && !results.Contains(t))
                                results.Add(t);
                        }
                    }
                }
            }

            // 3. Fallback: Non-occupied station tracks respecting passenger/freight separation
            if (results.Count == 0 && destStation.AllStationTracks != null)
            {
                for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                {
                    var t = destStation.AllStationTracks[i];
                    if (t == null || IsTrackOccupied(t)) continue;

                    if (isPax)
                    {
                        if (!IsInboundTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }
                    else
                    {
                        if (!IsPlatformTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }
                }
            }

            return results;
        }

        private static List<RailTrack> GetCandidateDepartureTracks(StationController originStation, float minLength)
        {
            var results = new List<RailTrack>();
            if (originStation == null) return results;

            // 1. Designated through tracks in station
            if (originStation.AllStationTracks != null)
            {
                for (int i = 0; i < originStation.AllStationTracks.Count; i++)
                {
                    var t = originStation.AllStationTracks[i];
                    if (t == null || t.curve == null || t.curve.length < minLength) continue;
                    if (IsDeadEndTrack(t) || IsTrackOccupied(t) || ModCompatManager.IsTrackActiveYardZone(t)) continue;

                    if (IsThroughOrMainlineTrack(t))
                    {
                        results.Add(t);
                    }
                }
            }

            // 2. Mainline edges in RailGraph within 1500m of origin station
            if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.Edges != null)
            {
                Vector3 origPos = originStation.transform.position;
                var edges = AITraffic.Navigation.RailGraph.Instance.Edges;
                for (int i = 0; i < edges.Count; i++)
                {
                    var edge = edges[i];
                    if (edge == null || edge.Track == null) continue;
                    var t = edge.Track;
                    if (t.curve == null || t.curve.length < minLength) continue;
                    if (IsDeadEndTrack(t) || IsTrackOccupied(t) || ModCompatManager.IsTrackActiveYardZone(t)) continue;

                    if (Vector3.Distance(t.transform.position, origPos) <= 1500f)
                    {
                        if (IsThroughOrMainlineTrack(t) && !results.Contains(t))
                        {
                            results.Add(t);
                        }
                    }
                }
            }

            return results;
        }

        private static RailTrack FindClearDepartureTrack(StationController station, StationController destStation, float minLength, out RailPath routePath, ConsistType consistType = ConsistType.RegionalFreight)
        {
            routePath = null;
            if (station == null) return null;

            var depTracks = GetCandidateDepartureTracks(station, minLength);
            if (depTracks == null || depTracks.Count == 0) return null;

            if (destStation == null)
            {
                return depTracks[0];
            }

            var destTracks = GetCandidateDestinationTracks(destStation, consistType);
            if (destTracks == null || destTracks.Count == 0) return null;

            float directStationDist = Vector3.Distance(station.transform.position, destStation.transform.position);
            // Multi-station corridors MUST span a realistic distance between stations (at least 45% of direct Euclidean distance)
            float minCorridorDist = Mathf.Max(350f, directStationDist * 0.45f);

            var pathfinder = new AITraffic.Navigation.Pathfinder();

            for (int i = 0; i < depTracks.Count; i++)
            {
                var depTrack = depTracks[i];
                for (int d = 0; d < destTracks.Count; d++)
                {
                    var dt = destTracks[d];
                    if (depTrack == dt) continue;

                    var path = pathfinder.FindPath(depTrack, dt, AITraffic.Navigation.PathfinderOptions.Default);
                    if (path != null && path.IsValid && path.Tracks.Count > 0 && path.TotalDistance >= minCorridorDist)
                    {
                        routePath = path;
                        return depTrack;
                    }
                }
            }

            return null;
        }

        #endregion
    }

    internal static class TrafficDensityExtensions
    {
        public static float switch_density(this TrafficDensity density)
        {
            switch (density)
            {
                case TrafficDensity.Light:
                    return 240f; // 4 min
                case TrafficDensity.Medium:
                    return 150f; // 2.5 min
                case TrafficDensity.Dense:
                    return 90f;  // 1.5 min
                default:
                    return 180f;
            }
        }
    }
}
