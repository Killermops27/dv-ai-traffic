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
    /// Dispatches Tier 1 ambient runs according to user settings and traffic density.
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
            new TrafficCorridor("SW", "CSW", ConsistType.PassengerCommuter),
            new TrafficCorridor("CSW", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "CSW", ConsistType.MainlineHeavy),
            new TrafficCorridor("CSW", "MB", ConsistType.RegionalFreight),
            new TrafficCorridor("MB", "CSW", ConsistType.RegionalFreight),

            // Coal Power Plant Corridors
            new TrafficCorridor("CM", "CP", ConsistType.MainlineHeavy),
            new TrafficCorridor("CP", "CM", ConsistType.MainlineHeavy),
            new TrafficCorridor("HB", "CP", ConsistType.MainlineHeavy),
            new TrafficCorridor("CP", "HB", ConsistType.MainlineHeavy),
            new TrafficCorridor("CP", "SM", ConsistType.MainlineHeavy),
            new TrafficCorridor("SM", "CP", ConsistType.MainlineHeavy),

            // Additional Forest Meadow & Farm Routes
            new TrafficCorridor("FR", "SW", ConsistType.ShunterFreight),
            new TrafficCorridor("SW", "FR", ConsistType.ShunterFreight),
            new TrafficCorridor("FR", "FF", ConsistType.ShunterFreight),
            new TrafficCorridor("FF", "FR", ConsistType.ShunterFreight),

            // City South (CS) Corridors
            new TrafficCorridor("CS", "HB", ConsistType.PassengerCommuter),
            new TrafficCorridor("HB", "CS", ConsistType.PassengerCommuter),
            new TrafficCorridor("CS", "SM", ConsistType.RegionalFreight),
            new TrafficCorridor("SM", "CS", ConsistType.RegionalFreight),
            new TrafficCorridor("CS", "SW", ConsistType.RegionalFreight),
            new TrafficCorridor("SW", "CS", ConsistType.RegionalFreight),
            new TrafficCorridor("CS", "MF", ConsistType.RegionalFreight),
            new TrafficCorridor("MF", "CS", ConsistType.RegionalFreight),

            // Forest South (FS / FRS) & Logging Corridors
            new TrafficCorridor("FS", "SW", ConsistType.ShunterFreight),
            new TrafficCorridor("SW", "FS", ConsistType.ShunterFreight),
            new TrafficCorridor("FS", "CS", ConsistType.ShunterFreight),
            new TrafficCorridor("CS", "FS", ConsistType.ShunterFreight),
            new TrafficCorridor("FS", "HB", ConsistType.RegionalFreight),
            new TrafficCorridor("HB", "FS", ConsistType.RegionalFreight)
        };

        private float _lastDispatchTime = -9999f;
        private float _dispatchIntervalSeconds = 180f; // 3 minutes default

        public float LastDispatchTime { get { return _lastDispatchTime; } }
        public float DispatchIntervalSeconds { get { return _dispatchIntervalSeconds; } }

        private readonly System.Random _rng = new System.Random();
        private readonly List<string> _recentDestinations = new List<string>();
        private readonly List<string> _recentOrigins = new List<string>();

        private struct ScoredCorridor
        {
            public TrafficCorridor Corridor;
            public float Score;
        }

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

        private void RecordOrigin(string yardId)
        {
            if (string.IsNullOrEmpty(yardId)) return;
            _recentOrigins.Remove(yardId);
            _recentOrigins.Add(yardId);
            while (_recentOrigins.Count > 4)
            {
                _recentOrigins.RemoveAt(0);
            }
        }

        private float CalculateCorridorScore(TrafficCorridor corridor, Vector3 playerPos)
        {
            StationController origin = FindStation(corridor.OriginYardId);
            StationController dest = FindStation(corridor.DestinationYardId);
            if (origin == null || dest == null) return 99999f;

            float distOrigin = Vector3.Distance(origin.transform.position, playerPos);
            float distDest = Vector3.Distance(dest.transform.position, playerPos);

            // Avoid spawning within 1000m of the player
            if (distOrigin < 1000f)
                return 80000f;

            float score = 0f;

            // Origin distance: Optimal spawn distance is between 1000m and 3000m from the player
            if (distOrigin >= 1000f && distOrigin <= 3000f)
            {
                score += distOrigin;
            }
            else // distOrigin > 3000f
            {
                score += 3000f + (distOrigin - 3000f) * 1.5f;
            }

            // Destination bonus: prioritize trains heading towards or past the player's general sector
            if (distDest >= 1000f && distDest <= 3500f)
            {
                score -= 600f;
            }

            // Anti-repetition penalty for recent destinations
            if (_recentDestinations.Contains(corridor.DestinationYardId))
            {
                int recencyIdx = _recentDestinations.IndexOf(corridor.DestinationYardId);
                score += 3000f * (recencyIdx + 1);
            }

            // Anti-repetition penalty for recent origins
            if (_recentOrigins.Contains(corridor.OriginYardId))
            {
                int recencyIdx = _recentOrigins.IndexOf(corridor.OriginYardId);
                score += 3000f * (recencyIdx + 1);
            }

            // Random jitter (+-300m) to ensure rich variety among nearby candidate corridors
            score += (float)(_rng.NextDouble() * 600.0 - 300.0);

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
            return DispatchTier1Ambient();
        }

        private void TryDispatchTrain(AITrafficSettings settings, int currentCount, int maxCount)
        {
            try
            {
                if (currentCount < maxCount)
                {
                    DispatchTier1Ambient();
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrafficScheduler.TryDispatchTrain: {0}", ex));
            }
        }

        private bool _isDispatching = false;

        /// <summary>
        /// Dispatches a Tier 1 ambient AI train along a major corridor asynchronously, time-slicing
        /// pathfinding and consist instantiation across multiple frames to eliminate main-thread lag spikes.
        /// </summary>
        public bool DispatchTier1Ambient(Action<bool> onComplete = null)
        {
            if (_isDispatching)
            {
                if (onComplete != null) onComplete(false);
                return false;
            }

            if (TrafficManager.Instance != null)
            {
                TrafficManager.Instance.StartCoroutine(DispatchTier1AmbientCoroutine(onComplete));
                return true;
            }

            return false;
        }

        public System.Collections.IEnumerator DispatchTier1AmbientCoroutine(Action<bool> onComplete = null)
        {
            if (_isDispatching)
            {
                if (onComplete != null) onComplete(false);
                yield break;
            }

            _isDispatching = true;
            bool success = false;

            try
            {
                if (StationController.allStations == null || StationController.allStations.Count == 0)
                    yield break;

                Vector3 playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;

                var occupiedSnapshot = (AITraffic.Navigation.RailGraph.Instance != null)
                    ? AITraffic.Navigation.RailGraph.BuildOccupiedTracksSnapshot()
                    : new HashSet<RailTrack>();

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
                if (playerPos != Vector3.zero && corridorList.Count > 1)
                {
                    var scoredCorridors = new List<ScoredCorridor>(corridorList.Count);
                    for (int i = 0; i < corridorList.Count; i++)
                    {
                        scoredCorridors.Add(new ScoredCorridor
                        {
                            Corridor = corridorList[i],
                            Score = CalculateCorridorScore(corridorList[i], playerPos)
                        });
                    }
                    scoredCorridors.Sort((a, b) => a.Score.CompareTo(b.Score));
                    for (int i = 0; i < scoredCorridors.Count; i++)
                    {
                        corridorList[i] = scoredCorridors[i].Corridor;
                    }
                }

                for (int c = 0; c < corridorList.Count; c++)
                {
                    // Yield a frame between candidate evaluations so heavy A* route searches don't freeze the frame
                    yield return null;

                    TrafficCorridor corridor = corridorList[c];
                    StationController originStation = FindStation(corridor.OriginYardId);
                    StationController destStation = FindStation(corridor.DestinationYardId);

                    if (originStation == null || destStation == null) continue;

                    // Avoid spawning within 1000m of the player
                    if (playerPos != Vector3.zero)
                    {
                        float distToPlayer = Vector3.Distance(originStation.transform.position, playerPos);
                        if (distToPlayer < 1000f)
                            continue;
                    }

                    RailPath routePath;
                    RailTrack spawnTrack = FindClearDepartureTrack(originStation, destStation, 100f, out routePath, corridor.PreferredConsist, occupiedSnapshot);
                    if (spawnTrack == null || routePath == null || !routePath.IsValid)
                        continue;

                    if (playerPos != Vector3.zero && IsDepartureHeadingTowardsPlayer(spawnTrack, routePath, playerPos))
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

                    // Yield a frame right before consist instantiation so frame starts with clean GPU/CPU budget
                    // Spawn ambient consist asynchronously, time-slicing 1 car per frame across multiple frames
                    AIEngineer engineer = null;
                    bool spawnComplete = false;

                    yield return TrafficManager.Instance.StartCoroutine(TrainSpawner.SpawnAITrainCoroutine(
                        spawnTrack,
                        corridor.PreferredConsist,
                        originYard: corridor.OriginYardId,
                        destYard: corridor.DestinationYardId,
                        startSpan: startSpan,
                        flipTrainConsist: flipConsist,
                        rng: _rng,
                        onComplete: delegate(AIEngineer eng)
                        {
                            engineer = eng;
                            spawnComplete = true;
                        }));

                    while (!spawnComplete)
                    {
                        yield return null;
                    }

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
                    RecordOrigin(corridor.OriginYardId);

                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Dispatched Tier 1 Ambient Train ({0} -> {1}, Consist: {2}) on track '{3}' (Route: {4:F0}m).",
                            corridor.OriginYardId, corridor.DestinationYardId, corridor.PreferredConsist, spawnTrack.name, routePath.TotalDistance));

                    success = true;
                    yield break;
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

                        float scoreA = (dA < 1000f) ? 99999f : (dA <= 3000f ? dA : 3000f + (dA - 3000f) * 2f);
                        float scoreB = (dB < 1000f) ? 99999f : (dB <= 3000f ? dB : 3000f + (dB - 3000f) * 2f);

                        string yardA = (stA.stationInfo != null) ? (stA.stationInfo.YardID ?? "") : "";
                        string yardB = (stB.stationInfo != null) ? (stB.stationInfo.YardID ?? "") : "";
                        int recA = _recentOrigins.Contains(yardA) ? _recentOrigins.IndexOf(yardA) + 1 : 0;
                        int recB = _recentOrigins.Contains(yardB) ? _recentOrigins.IndexOf(yardB) + 1 : 0;
                        scoreA += 3000f * recA;
                        scoreB += 3000f * recB;

                        return scoreA.CompareTo(scoreB);
                    });
                }

                for (int s = 0; s < allStationList.Count; s++)
                {
                    yield return null;

                    var station = allStationList[s];
                    if (station == null) continue;

                    // Avoid spawning within 1000m of the player
                    if (playerPos != Vector3.zero && Vector3.Distance(station.transform.position, playerPos) < 1000f)
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
                        yield return null;

                        var candidateDest = destCandidates[d];
                        if (candidateDest == null) continue;

                        float distBetween = Vector3.Distance(station.transform.position, candidateDest.transform.position);
                        if (distBetween < 1000f) continue;

                        ConsistType inferredConsist = InferConsistType(station, candidateDest, _rng);

                        RailPath fallbackPath;
                        RailTrack spawnTrack = FindClearDepartureTrack(station, candidateDest, 80f, out fallbackPath, inferredConsist, occupiedSnapshot);
                        if (spawnTrack != null && fallbackPath != null && fallbackPath.IsValid)
                        {
                            if (playerPos != Vector3.zero && IsDepartureHeadingTowardsPlayer(spawnTrack, fallbackPath, playerPos))
                                continue;

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

                            string origYard = (station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.YardID)) ? station.stationInfo.YardID : "ORIG";
                            string destYard = (candidateDest.stationInfo != null && !string.IsNullOrEmpty(candidateDest.stationInfo.YardID)) ? candidateDest.stationInfo.YardID : "DEST";

                            // Spawn ambient consist asynchronously, time-slicing 1 car per frame across multiple frames
                            AIEngineer engineer = null;
                            bool spawnComplete = false;

                            yield return TrafficManager.Instance.StartCoroutine(TrainSpawner.SpawnAITrainCoroutine(
                                spawnTrack,
                                inferredConsist,
                                originYard: origYard,
                                destYard: destYard,
                                startSpan: startSpan,
                                flipTrainConsist: flipConsist,
                                rng: _rng,
                                onComplete: delegate(AIEngineer eng)
                                {
                                    engineer = eng;
                                    spawnComplete = true;
                                }));

                            while (!spawnComplete)
                            {
                                yield return null;
                            }

                            if (engineer != null)
                            {
                                engineer.CurrentPath = fallbackPath;
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
                                RecordOrigin(origYard);

                                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                    Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Dispatched Dynamic Ambient Train ({0} -> {1}, Consist: {2}) on track '{3}' (Route: {4:F0}m).",
                                        origName, destName, inferredConsist, spawnTrack.name, fallbackPath.TotalDistance));

                                success = true;
                                yield break;
                            }
                        }
                    }
                }
            }
            finally
            {
                _isDispatching = false;
                if (onComplete != null) onComplete(success);
            }
        }

        #region Helper Methods

        /// <summary>
        /// Verifies that candidate spawn track and initial departure route are safely distanced from the player.
        /// Prevents dangerous situations where an AI train spawns on a long yard track (e.g. Harbor D/G)
        /// heading straight toward an oncoming player within 1200m.
        /// </summary>
        private static bool IsDepartureHeadingTowardsPlayer(RailTrack spawnTrack, RailPath routePath, Vector3 playerPos)
        {
            if (spawnTrack == null || routePath == null || playerPos == Vector3.zero) return false;

            // Direct track distance to player (must be at least 800m away from player)
            float spawnDistToPlayer = Vector3.Distance(spawnTrack.transform.position, playerPos);
            if (spawnDistToPlayer < 800f) return true;

            // Check if initial departure route heads directly toward a player who is within 1400m
            if (spawnDistToPlayer < 1400f && routePath.Tracks != null && routePath.Tracks.Count > 1)
            {
                int checkLimit = Math.Min(routePath.Tracks.Count, 6);
                for (int i = 1; i < checkLimit; i++)
                {
                    var t = routePath.Tracks[i];
                    if (t != null && Vector3.Distance(t.transform.position, playerPos) < 350f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a rail track is currently occupied by any rolling stock or logic track cars.
        /// <summary>
        /// Checks whether a given RailTrack is physically occupied by any rolling stock or train cars.
        /// Supports passing an optional precomputed occupiedTracksSnapshot for O(1) performance.
        /// </summary>
        public static bool IsTrackOccupied(RailTrack track, HashSet<RailTrack> occupiedSnapshot = null)
        {
            if (track == null) return true;

            // 0. Direct physical bogie registry on track (O(1))
            try
            {
                var bogies = track.BogiesOnTrack();
                if (bogies != null && bogies.Count > 0)
                {
                    return true;
                }
            }
            catch { }

            // 1. Check snapshot or RailGraph (O(1))
            if (occupiedSnapshot != null)
            {
                return occupiedSnapshot.Contains(track);
            }

            if (AITraffic.Navigation.RailGraph.Instance != null)
            {
                return AITraffic.Navigation.RailGraph.Instance.IsTrackOccupied(track);
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

        private static readonly Dictionary<string, StationController> s_stationIndex = new Dictionary<string, StationController>(StringComparer.OrdinalIgnoreCase);

        private static void EnsureStationIndexBuilt()
        {
            if (s_stationIndex.Count > 0) return;
            if (StationController.allStations == null || StationController.allStations.Count == 0) return;

            for (int i = 0; i < StationController.allStations.Count; i++)
            {
                var sc = StationController.allStations[i];
                if (sc == null || sc.stationInfo == null) continue;

                string sYard = sc.stationInfo.YardID;
                string sName = sc.stationInfo.Name;

                if (!string.IsNullOrEmpty(sYard) && !s_stationIndex.ContainsKey(sYard))
                {
                    s_stationIndex[sYard] = sc;
                }
                if (!string.IsNullOrEmpty(sName) && !s_stationIndex.ContainsKey(sName))
                {
                    s_stationIndex[sName] = sc;
                }

                // Handle City South: CS
                if (string.Equals(sYard, "CS", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && sName.IndexOf("City South", StringComparison.OrdinalIgnoreCase) >= 0 && sName.IndexOf("West", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    if (!s_stationIndex.ContainsKey("CS")) s_stationIndex["CS"] = sc;
                }

                // Handle City South West / City West: CW / CSW
                if (string.Equals(sYard, "CSW", StringComparison.OrdinalIgnoreCase) || string.Equals(sYard, "CW", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && (sName.IndexOf("City South West", StringComparison.OrdinalIgnoreCase) >= 0 || sName.IndexOf("City West", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    if (!s_stationIndex.ContainsKey("CW")) s_stationIndex["CW"] = sc;
                    if (!s_stationIndex.ContainsKey("CSW")) s_stationIndex["CSW"] = sc;
                }

                // Handle Forest South: FS / FRS
                if (string.Equals(sYard, "FS", StringComparison.OrdinalIgnoreCase) || string.Equals(sYard, "FRS", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && sName.IndexOf("Forest South", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    if (!s_stationIndex.ContainsKey("FS")) s_stationIndex["FS"] = sc;
                    if (!s_stationIndex.ContainsKey("FRS")) s_stationIndex["FRS"] = sc;
                }

                // Handle Farm / Forest Meadow aliases: FM / FR
                if (string.Equals(sYard, "FM", StringComparison.OrdinalIgnoreCase) || string.Equals(sYard, "FR", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && (sName.IndexOf("Farm", StringComparison.OrdinalIgnoreCase) >= 0 || sName.IndexOf("Forest Meadow", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    if (!s_stationIndex.ContainsKey("FM")) s_stationIndex["FM"] = sc;
                    if (!s_stationIndex.ContainsKey("FR")) s_stationIndex["FR"] = sc;
                }

                // Handle Coal Power Plant aliases: CP / CPP
                if (string.Equals(sYard, "CP", StringComparison.OrdinalIgnoreCase) || string.Equals(sYard, "CPP", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && (sName.IndexOf("Power", StringComparison.OrdinalIgnoreCase) >= 0 || sName.IndexOf("Coal Power", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    if (!s_stationIndex.ContainsKey("CP")) s_stationIndex["CP"] = sc;
                    if (!s_stationIndex.ContainsKey("CPP")) s_stationIndex["CPP"] = sc;
                }
            }
        }

        private static StationController FindStation(string yardId)
        {
            if (string.IsNullOrEmpty(yardId))
                return null;

            EnsureStationIndexBuilt();

            StationController sc;
            if (s_stationIndex.TryGetValue(yardId, out sc))
                return sc;

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

        private static List<RailTrack> GetCandidateDestinationTracks(StationController destStation, ConsistType consistType = ConsistType.RegionalFreight, HashSet<RailTrack> occupiedSnapshot = null)
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
                        if (t == null || IsTrackOccupied(t, occupiedSnapshot)) continue;
                        if (IsPlatformTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Passing loops / Station sidings
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsTrackOccupied(t, occupiedSnapshot)) continue;
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
                        if (t == null || IsDeadEndTrack(t) || IsTrackOccupied(t, occupiedSnapshot)) continue;
                        // STRICT GUARD: Never route freight to a passenger platform
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsInboundTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Yard storage / classification tracks [S] (excluding dead-end sidings and active shunting zones)
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t) || IsTrackOccupied(t, occupiedSnapshot)) continue;
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsYardStorageTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 3: Passing loops / mainline station loops
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsDeadEndTrack(t) || IsTrackOccupied(t, occupiedSnapshot)) continue;
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
                    if (t.curve == null || (!isPax && IsDeadEndTrack(t))) continue;

                    string name = t.name ?? "";
                    if (!string.IsNullOrEmpty(yardId) && name.IndexOf(yardId, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (IsTrackOccupied(t, occupiedSnapshot)) continue;

                        if (isPax)
                        {
                            if (IsPlatformTrack(t, destStation) && !results.Contains(t))
                                results.Add(t);
                        }
                        else
                        {
                            if (IsPlatformTrack(t, destStation)) continue; // STRICT GUARD
                            if (ModCompatManager.IsTrackActiveYardZone(t)) continue;

                            if ((IsInboundTrack(t, destStation) || IsYardStorageTrack(t, destStation) || IsPassingOrLoopTrack(t)) && !results.Contains(t))
                                results.Add(t);
                        }
                    }
                }
            }

            // 3. Fallback: Non-occupied station tracks respecting passenger/freight separation and non-dead-end
            if (results.Count == 0 && destStation.AllStationTracks != null)
            {
                for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                {
                    var t = destStation.AllStationTracks[i];
                    if (t == null || (!isPax && IsDeadEndTrack(t)) || IsTrackOccupied(t, occupiedSnapshot)) continue;

                    if (isPax)
                    {
                        if (!IsInboundTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }
                    else
                    {
                        if (!IsPlatformTrack(t, destStation) && !ModCompatManager.IsTrackActiveYardZone(t) && !results.Contains(t))
                            results.Add(t);
                    }
                }
            }

            return results;
        }

        /// <summary>
        /// Maximum allowable uphill incline grade (in %) along the departure route when spawning ambient trains.
        /// Declines (downhill) and level tracks are allowed; steep inclines (> 0.40%) are strictly avoided
        /// to prevent initial spawn hill start failures before the air brake system is fully pressurized.
        /// </summary>
        public const float MaxSpawnInclineGrade = 0.40f;

        /// <summary>
        /// Computes the effective departure grade (in %) along the route's travel direction.
        /// Positive values indicate an uphill climb (incline). Negative values indicate a downhill descent (decline).
        /// Inspects both the initial spawn track and the consist footprint (~250m) to prevent spawning on steep inclines.
        /// </summary>
        public static float CalculateDepartureGrade(RailPath path, float maxSampleDistance = 250f)
        {
            if (path == null || path.Tracks == null || path.Tracks.Count == 0) return 0f;

            var t0 = path.Tracks[0];
            if (t0 == null || t0.curve == null) return 0f;

            if (path.Tracks.Count == 1)
            {
                Vector3 p0 = t0.curve.GetPointAt(0f);
                Vector3 p1 = t0.curve.GetPointAt(1f);
                float dY = p1.y - p0.y;
                float hD = Mathf.Sqrt(Mathf.Pow(p1.x - p0.x, 2) + Mathf.Pow(p1.z - p0.z, 2));
                return hD > 0.1f ? (Mathf.Abs(dY) / hD) * 100f : 0f;
            }

            var t1 = path.Tracks[1];
            if (t1 == null || t1.curve == null) return 0f;

            Vector3 curStart = t0.curve.GetPointAt(0.0f);
            Vector3 curEnd = t0.curve.GetPointAt(1.0f);
            Vector3 nextMid = t1.curve.GetPointAt(0.5f);

            bool forward = Vector3.Distance(curEnd, nextMid) <= Vector3.Distance(curStart, nextMid);
            Vector3 departureStartPos = forward ? curStart : curEnd;
            Vector3 departureEndPos = forward ? curEnd : curStart;

            float deltaY0 = departureEndPos.y - departureStartPos.y;
            float hDist0 = Mathf.Sqrt(Mathf.Pow(departureEndPos.x - departureStartPos.x, 2) + Mathf.Pow(departureEndPos.z - departureStartPos.z, 2));
            float track0Grade = hDist0 > 0.1f ? (deltaY0 / hDist0) * 100f : 0f;

            // Also inspect the cumulative elevation change across the consist footprint (~250m)
            float accumulatedDist = t0.curve.length;
            Vector3 lastPos = departureEndPos;

            for (int i = 1; i < path.Tracks.Count && accumulatedDist < maxSampleDistance; i++)
            {
                var trk = path.Tracks[i];
                if (trk == null || trk.curve == null) break;

                Vector3 pStart = trk.curve.GetPointAt(0.0f);
                Vector3 pEnd = trk.curve.GetPointAt(1.0f);

                bool entryIsStart = Vector3.Distance(pStart, lastPos) <= Vector3.Distance(pEnd, lastPos);
                Vector3 trkExit = entryIsStart ? pEnd : pStart;

                lastPos = trkExit;
                accumulatedDist += trk.curve.length;
            }

            float totalDeltaY = lastPos.y - departureStartPos.y;
            float totalHDist = Mathf.Sqrt(Mathf.Pow(lastPos.x - departureStartPos.x, 2) + Mathf.Pow(lastPos.z - departureStartPos.z, 2));
            float cumulativeGrade = totalHDist > 0.1f ? (totalDeltaY / totalHDist) * 100f : 0f;

            // Return the most uphill incline between the initial track and the extended consist footprint
            return Mathf.Max(track0Grade, cumulativeGrade);
        }

        private static List<RailTrack> GetCandidateDepartureTracks(StationController originStation, float minLength, HashSet<RailTrack> occupiedSnapshot = null)
        {
            var results = new List<RailTrack>();
            if (originStation == null) return results;

            // 1. Designated through tracks in station (prefer flatter station yard tracks)
            if (originStation.AllStationTracks != null)
            {
                for (int i = 0; i < originStation.AllStationTracks.Count; i++)
                {
                    var t = originStation.AllStationTracks[i];
                    if (t == null || t.curve == null || t.curve.length < minLength) continue;
                    if (IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t) || IsTrackOccupied(t, occupiedSnapshot)) continue;

                    if (IsThroughOrMainlineTrack(t))
                    {
                        results.Add(t);
                    }
                }
            }

            // 2. Mainline edges in RailGraph within 1500m of origin station
            // Filter out steep mainline grades (> 0.5% grade) so trains don't spawn on mountain inclines
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

                    // Spatial distance check FIRST to skip ~98% of tracks immediately
                    if (Vector3.Distance(t.transform.position, origPos) > 1500f) continue;

                    // Exclude steep mainline edges (> 0.5% grade)
                    if (Mathf.Abs(edge.Grade) > 0.5f) continue;

                    if (IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t) || IsTrackOccupied(t, occupiedSnapshot)) continue;

                    if (IsThroughOrMainlineTrack(t) && !results.Contains(t))
                    {
                        results.Add(t);
                    }
                }
            }

            // Separate candidate tracks into flat departure tracks (grade <= MaxSpawnInclineGrade) and others
            var flatTracks = new List<RailTrack>();
            var otherTracks = new List<RailTrack>();

            for (int i = 0; i < results.Count; i++)
            {
                var t = results[i];
                float g = (t.curve != null && t.curve.length > 1f) ? Mathf.Abs(t.curve.GetPointAt(1f).y - t.curve.GetPointAt(0f).y) / t.curve.length : 0f;
                if (g <= MaxSpawnInclineGrade)
                {
                    flatTracks.Add(t);
                }
                else
                {
                    otherTracks.Add(t);
                }
            }

            // Shuffle flat tracks using UnityEngine.Random so different candidate tracks are selected across spawns
            for (int i = flatTracks.Count - 1; i > 0; i--)
            {
                int r = UnityEngine.Random.Range(0, i + 1);
                var temp = flatTracks[i];
                flatTracks[i] = flatTracks[r];
                flatTracks[r] = temp;
            }

            flatTracks.AddRange(otherTracks);
            return flatTracks;
        }

        private static RailTrack FindClearDepartureTrack(
            StationController station,
            StationController destStation,
            float minLength,
            out RailPath routePath,
            ConsistType consistType = ConsistType.RegionalFreight,
            HashSet<RailTrack> occupiedSnapshot = null)
        {
            routePath = null;
            if (station == null) return null;

            if (occupiedSnapshot == null)
            {
                occupiedSnapshot = (AITraffic.Navigation.RailGraph.Instance != null)
                    ? AITraffic.Navigation.RailGraph.BuildOccupiedTracksSnapshot()
                    : new HashSet<RailTrack>();
            }

            var depTracks = GetCandidateDepartureTracks(station, minLength, occupiedSnapshot);
            if (depTracks == null || depTracks.Count == 0) return null;

            if (destStation == null)
            {
                return depTracks[0];
            }

            var destTracks = GetCandidateDestinationTracks(destStation, consistType, occupiedSnapshot);
            if (destTracks == null || destTracks.Count == 0) return null;

            // Randomize destination tracks so arrivals don't always target the identical siding
            for (int i = destTracks.Count - 1; i > 0; i--)
            {
                int r = UnityEngine.Random.Range(0, i + 1);
                var temp = destTracks[i];
                destTracks[i] = destTracks[r];
                destTracks[r] = temp;
            }

            float directStationDist = Vector3.Distance(station.transform.position, destStation.transform.position);
            // Multi-station corridors MUST span a realistic distance between stations (at least 45% of direct Euclidean distance)
            float minCorridorDist = Mathf.Max(350f, directStationDist * 0.45f);

            var pathfinder = new AITraffic.Navigation.Pathfinder();
            var pathOptions = new AITraffic.Navigation.PathfinderOptions
            {
                StrictlyAvoidOccupied = true,
                OccupiedTracksSnapshot = occupiedSnapshot
            };

            // Evaluate up to top 3 departure tracks and top 2 destination tracks to eliminate lag spikes while giving rich track variety
            int maxDep = Math.Min(3, depTracks.Count);
            int maxDest = Math.Min(2, destTracks.Count);

            for (int i = 0; i < maxDep; i++)
            {
                var depTrack = depTracks[i];
                for (int d = 0; d < maxDest; d++)
                {
                    var dt = destTracks[d];
                    if (depTrack == dt) continue;

                    var path = pathfinder.FindPath(depTrack, dt, pathOptions);
                    if (path != null && path.IsValid && path.Tracks.Count > 0 && path.TotalDistance >= minCorridorDist)
                    {
                        // Check departure incline: Declines and flat tracks are okay, but NO steep inclines!
                        float depGrade = CalculateDepartureGrade(path);
                        if (depGrade > MaxSpawnInclineGrade)
                        {
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            {
                                Main.ModEntry.Logger.Log(string.Format("Skipping spawn track '{0}' to '{1}': Departure has steep incline (+{2:F2}% grade > {3:F2}% threshold).",
                                    depTrack.name, dt.name, depGrade, MaxSpawnInclineGrade));
                            }
                            continue; // Skip this track, search for a flat track or decline!
                        }

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
