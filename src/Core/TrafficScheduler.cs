using System;
using System.Collections.Generic;
using UnityEngine;
using DV.Logic.Job;
using AITraffic.Config;
using AITraffic.Fleet;
using AITraffic.Driver;
using AITraffic.Compat;
using AITraffic.Navigation;
using DV.ThingTypes;

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
        internal static readonly TrafficCorridor[] s_corridors = new TrafficCorridor[]
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
            new TrafficCorridor("IME", "HB", ConsistType.MainlineHeavy),
            new TrafficCorridor("HB", "IME", ConsistType.MainlineHeavy),
            new TrafficCorridor("CME", "HB", ConsistType.MainlineHeavy),
            new TrafficCorridor("HB", "CME", ConsistType.MainlineHeavy),
            new TrafficCorridor("CM", "HB", ConsistType.MainlineHeavy),
            new TrafficCorridor("HB", "CM", ConsistType.MainlineHeavy),
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
        private bool _isFirstSchedulerTick = true;

        public float LastDispatchTime { get { return _lastDispatchTime; } }
        public float DispatchIntervalSeconds { get { return _dispatchIntervalSeconds; } }

        private readonly System.Random _rng = new System.Random();
        private readonly List<string> _recentDestinations = new List<string>();
        private readonly List<string> _recentOrigins = new List<string>();
        private bool _nextDispatchIsEncounter = true;

        private struct ScoredCorridor
        {
            public TrafficCorridor Corridor;
            public float Score;
        }

        public TrafficScheduler()
        {
            _lastDispatchTime = -9999f;
            _isFirstSchedulerTick = true;
        }

        public static void ClearCaches()
        {
            s_stationIndex.Clear();
            s_staticDepartureTracksCache.Clear();
            s_staticPaxDestTracksCache.Clear();
            s_staticFreightDestTracksCache.Clear();
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
            if (settings != null && settings.SpawnIntervalMinutes > 0.1f)
            {
                _dispatchIntervalSeconds = settings.SpawnIntervalMinutes * 60f;
            }
            else
            {
                _dispatchIntervalSeconds = TrafficDensityExtensions.switch_density(settings.Density);
            }

            if (_isFirstSchedulerTick)
            {
                _isFirstSchedulerTick = false;
                // Grace period: allow player and world 15 seconds to settle before first scheduled ambient dispatch
                _lastDispatchTime = Time.time - _dispatchIntervalSeconds + 15f;
            }

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

        private static float GetMinDistanceToTrack(RailTrack trk, Vector3 point)
        {
            if (trk == null || trk.curve == null) return float.MaxValue;
            if (PlayerManager.Car != null)
            {
                var car = PlayerManager.Car;
                if ((car.FrontBogie != null && car.FrontBogie.track == trk) ||
                    (car.RearBogie != null && car.RearBogie.track == trk))
                {
                    return 0f;
                }
            }

            Vector3 p0 = trk.curve.GetPointAt(0.0f);
            Vector3 p1 = trk.curve.GetPointAt(0.25f);
            Vector3 p2 = trk.curve.GetPointAt(0.5f);
            Vector3 p3 = trk.curve.GetPointAt(0.75f);
            Vector3 p4 = trk.curve.GetPointAt(1.0f);

            float d0 = Vector3.Distance(p0, point);
            float d1 = Vector3.Distance(p1, point);
            float d2 = Vector3.Distance(p2, point);
            float d3 = Vector3.Distance(p3, point);
            float d4 = Vector3.Distance(p4, point);

            return Mathf.Min(d0, Mathf.Min(d1, Mathf.Min(d2, Mathf.Min(d3, d4))));
        }

        private System.Collections.IEnumerator DispatchPassingTrainCoroutine(
            HashSet<RailTrack> occupiedSnapshot,
            Vector3 playerPos,
            Action<bool> onComplete)
        {
            if (AITraffic.Navigation.RailGraph.Instance == null || !AITraffic.Navigation.RailGraph.Instance.IsInitialized || playerPos == Vector3.zero)
            {
                if (onComplete != null) onComplete(false);
                yield break;
            }

            float minSpawnDist = (Main.Settings != null) ? Mathf.Clamp(Main.Settings.SpawnDistanceMin, 400f, 800f) : 500f;
            float maxSpawnDist = (Main.Settings != null) ? Mathf.Clamp(Main.Settings.SpawnDistanceMax, 1000f, 1800f) : 1400f;

            var rawTracks = new List<RailTrack>();
            AITraffic.Navigation.RailGraph.Instance.GetTracksInRadius(playerPos, minSpawnDist, maxSpawnDist, rawTracks);

            if (rawTracks.Count == 0)
            {
                if (onComplete != null) onComplete(false);
                yield break;
            }

            // O(1) Fast-fail candidate filtration
            var candidateTracks = new List<RailTrack>();
            for (int i = 0; i < rawTracks.Count; i++)
            {
                var trk = rawTracks[i];
                if (trk == null || trk.curve == null) continue;

                // 1. Length gate (must fit minimum viable consist)
                if (trk.curve.length < 80f) continue;

                // 2. Physical occupancy gate (O(1) snapshot lookup)
                if (IsTrackOccupied(trk, occupiedSnapshot)) continue;

                // 3. Player occupancy gate
                if (AITraffic.Navigation.SignalRegistry.IsTrackOccupiedByPlayer(trk, null)) continue;

                Signals.Game.Signal _;
                if (ModCompatManager.IsDVSignalsLoaded && Signals.Game.Railway.TrackReserver.IsTrackReserved(trk, out _))
                {
                    continue;
                }

                // 4. Grade & dead-end gate
                var edge = AITraffic.Navigation.RailGraph.Instance.GetEdge(trk);
                if (edge == null || Mathf.Abs(edge.Grade) > MaxSpawnInclineGrade) continue;

                bool isDeadEnd = (trk.inJunction == null && (edge.FromNode == null || edge.FromNode.IncidentEdges.Count <= 1)) ||
                                 (trk.outJunction == null && (edge.ToNode == null || edge.ToNode.IncidentEdges.Count <= 1));
                if (isDeadEnd) continue;

                // 5. Yard storage, industrial loading, and stub track gate:
                // Passing trains must spawn on through-running sidings, passing loops, or mainline corridors,
                // never trapped inside dead-end yard storage ladders or loading bays.
                string tName = trk.name ?? "";
                bool isDoubleTrackOrMain = edge.IsDoubleTrackMainline || 
                                           tName.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           tName.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isDoubleTrackOrMain)
                {
                    if (edge.IsYardTrack) continue;
                    if (tName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                        tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                        tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                        tName.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        tName.IndexOf("-L]", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }
                }

                candidateTracks.Add(trk);
            }

            if (candidateTracks.Count == 0)
            {
                if (onComplete != null) onComplete(false);
                yield break;
            }

            // Sort candidate tracks prioritizing the optimal 600m - 1200m sweet spot
            candidateTracks.Sort((a, b) =>
            {
                float da = Vector3.Distance(a.curve.GetPointAt(0.5f), playerPos);
                float db = Vector3.Distance(b.curve.GetPointAt(0.5f), playerPos);
                float scoreA = Mathf.Abs(da - 850f);
                float scoreB = Mathf.Abs(db - 850f);
                return scoreA.CompareTo(scoreB);
            });

            if (StationController.allStations == null || StationController.allStations.Count == 0)
            {
                if (onComplete != null) onComplete(false);
                yield break;
            }

            var destStationCandidates = new List<StationController>(StationController.allStations);
            for (int i = destStationCandidates.Count - 1; i > 0; i--)
            {
                int swapIdx = _rng.Next(0, i + 1);
                var temp = destStationCandidates[i];
                destStationCandidates[i] = destStationCandidates[swapIdx];
                destStationCandidates[swapIdx] = temp;
            }

            var pathfinder = new AITraffic.Navigation.Pathfinder();
            var pathOptions = new AITraffic.Navigation.PathfinderOptions
            {
                StrictlyAvoidOccupied = true,
                OccupiedTracksSnapshot = occupiedSnapshot,
                RequesterTrainset = (PlayerManager.Car != null) ? PlayerManager.Car.trainset : null
            };

            int maxTracksToCheck = Math.Min(4, candidateTracks.Count);
            int pathSearches = 0;
            bool stopSearches = false;

            for (int tIdx = 0; tIdx < maxTracksToCheck; tIdx++)
            {
                if (stopSearches) break;
                var candTrack = candidateTracks[tIdx];
                Vector3 candPos = candTrack.curve.GetPointAt(0.5f);
                Vector3 trackToPlayer = (playerPos - candPos).normalized;

                float trackLen = candTrack.curve.length;
                ConsistType consistType = (trackLen < 220f)
                    ? (_rng.NextDouble() < 0.5 ? ConsistType.ShunterFreight : ConsistType.PassengerCommuter)
                    : (_rng.NextDouble() < 0.65 ? ConsistType.RegionalFreight : ConsistType.PassengerCommuter);

                List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(consistType, rng: _rng);
                if (specs == null || specs.Count == 0) continue;

                List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
                for (int i = 0; i < specs.Count; i++)
                {
                    if (specs[i].Livery != null) liveries.Add(specs[i].Livery);
                }

                if (!TrainSpawner.CanConsistFitOnTrack(liveries, candTrack, 15.0, false))
                {
                    consistType = ConsistType.PassengerCommuter;
                    specs = ConsistDefinitions.GetConsistSpecs(consistType, rng: _rng);
                    if (specs == null || specs.Count == 0) continue;
                    liveries.Clear();
                    for (int i = 0; i < specs.Count; i++)
                    {
                        if (specs[i].Livery != null) liveries.Add(specs[i].Livery);
                    }
                    if (!TrainSpawner.CanConsistFitOnTrack(liveries, candTrack, 15.0, false))
                    {
                        continue;
                    }
                }

                var viableDestStations = new List<StationController>();
                for (int s = 0; s < destStationCandidates.Count; s++)
                {
                    var destStation = destStationCandidates[s];
                    if (destStation == null) continue;

                    float distTrackToDest = Vector3.Distance(destStation.transform.position, candPos);
                    if (distTrackToDest < 1200f || distTrackToDest > 8000f) continue;

                    // Prioritize destinations lying towards or past the player
                    Vector3 trackToDest = (destStation.transform.position - candPos).normalized;
                    if (Vector3.Dot(trackToPlayer, trackToDest) < 0.10f)
                        continue;

                    viableDestStations.Add(destStation);
                }

                viableDestStations.Sort((a, b) => Vector3.Distance(a.transform.position, candPos).CompareTo(Vector3.Distance(b.transform.position, candPos)));

                for (int s = 0; s < viableDestStations.Count; s++)
                {
                    if (stopSearches) break;
                    var destStation = viableDestStations[s];

                    var destTracks = GetCandidateDestinationTracks(destStation, consistType, occupiedSnapshot);
                    if (destTracks == null || destTracks.Count == 0) continue;

                    int maxDestTracks = Math.Min(2, destTracks.Count);
                    for (int d = 0; d < maxDestTracks; d++)
                    {
                        var destTrack = destTracks[d];
                        if (destTrack == null || destTrack == candTrack) continue;

                        pathSearches++;
                        // Strictly yield 1 frame before each path search to prevent frame hitching
                        yield return null;

                        if (pathSearches >= 4)
                        {
                            stopSearches = true;
                            break;
                        }

                        var path = pathfinder.FindPath(candTrack, destTrack, pathOptions);
                        if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count < 3)
                            continue;

                        // PREDICTIVE DEADLOCK & CONFLICT ANALYSIS
                        if (!PredictDeadlockConflict(candTrack, path, playerPos, occupiedSnapshot))
                        {
                            continue; // Deadlock or conflict predicted! Skip this candidate.
                        }

                        // Hairpin Route Sanity Check: Ensure no sharp 180° kinks in the route
                        bool hasHairpin = false;
                        for (int k = 0; k < path.Tracks.Count - 1 && k < 5; k++)
                        {
                            var tA = path.Tracks[k];
                            var tB = path.Tracks[k + 1];
                            if (tA != null && tA.curve != null && tB != null && tB.curve != null)
                            {
                                var edgeA = AITraffic.Navigation.RailGraph.Instance.GetEdge(tA);
                                var edgeB = AITraffic.Navigation.RailGraph.Instance.GetEdge(tB);
                                if (edgeA != null && edgeB != null)
                                {
                                    var sharedNode = AITraffic.Navigation.RailGraph.Instance.GetConnectingNode(edgeA, edgeB);
                                    if (sharedNode != null)
                                    {
                                        Vector3 vIn = AITraffic.Navigation.RailGraph.GetIncomingVector(sharedNode, edgeA);
                                        Vector3 vOut = AITraffic.Navigation.RailGraph.GetDepartingVector(sharedNode, edgeB);
                                        if (Vector3.Dot(vIn, vOut) < 0.20f)
                                        {
                                            hasHairpin = true;
                                            break;
                                        }
                                    }
                                }
                            }
                        }
                        if (hasHairpin) continue;

                        // Determine startSpan and flipConsist
                        double startSpan = 15.0;
                        bool flipConsist = false;
                        if (path.Tracks.Count > 1)
                        {
                            var track0 = path.Tracks[0];
                            var track1 = path.Tracks[1];
                            if (track0 != null && track0.curve != null && track1 != null && track1.curve != null)
                            {
                                Vector3 curStart = track0.curve.GetPointAt(0.0f);
                                Vector3 curEnd = track0.curve.GetPointAt(1.0f);
                                Vector3 nextMid = track1.curve.GetPointAt(0.5f);

                                bool forward = Vector3.Distance(curEnd, nextMid) <= Vector3.Distance(curStart, nextMid);
                                if (forward)
                                {
                                    startSpan = 15.0;
                                    flipConsist = false;
                                }
                                else
                                {
                                    startSpan = 15.0;
                                    flipConsist = true;
                                }
                            }
                        }

                        // Spawn passing train asynchronously with time-slicing (1 car/frame)
                        AIEngineer engineer = null;
                        bool spawnComplete = false;

                        string destYard = destStation.stationInfo != null ? (destStation.stationInfo.YardID ?? "") : "";
                        string origYard = "";
                        var nearestStation = FindNearestStation(candPos);
                        if (nearestStation != null && nearestStation.stationInfo != null)
                        {
                            origYard = nearestStation.stationInfo.YardID ?? "";
                        }

                        AITraffic.Diagnostics.PerformanceProfiler.SetSchedulerState(string.Format("Spawning Passing Train on '{0}'", candTrack.name));
                        yield return TrafficManager.Instance.StartCoroutine(TrainSpawner.SpawnAITrainCoroutine(
                            candTrack,
                            specs,
                            startSpan: startSpan,
                            flipTrainConsist: flipConsist,
                            onComplete: delegate(AIEngineer eng)
                            {
                                engineer = eng;
                                spawnComplete = true;
                            },
                            consistType: consistType));

                        while (!spawnComplete)
                        {
                            yield return null;
                        }

                        if (engineer == null)
                            continue;

                        engineer.CurrentPath = path;
                        engineer.IsEncounterTrain = true;

                        string origName = (nearestStation != null && nearestStation.stationInfo != null && !string.IsNullOrEmpty(nearestStation.stationInfo.Name))
                            ? string.Format("Line near {0}", nearestStation.stationInfo.Name)
                            : string.Format("Line [{0}]", candTrack.name);
                        string destName = (destStation.stationInfo != null && !string.IsNullOrEmpty(destStation.stationInfo.Name))
                            ? string.Format("{0} [{1}]", destStation.stationInfo.Name, destYard)
                            : destYard;

                        engineer.OriginStationName = origName;
                        engineer.DestinationStationName = destName;
                        engineer.DestinationTrackName = (path.Tracks != null && path.Tracks.Count > 0) ? path.Tracks[path.Tracks.Count - 1].name : "";
                        engineer.DistanceToDestination = path.TotalDistance;
                        engineer.IsStationDestination = false;
                        engineer.IsTerminusDestination = true;

                        RecordDestination(destYard);

                        float closestDist = float.MaxValue;
                        for (int t = 0; t < path.Tracks.Count; t++)
                        {
                            float distT = GetMinDistanceToTrack(path.Tracks[t], playerPos);
                            if (distT < closestDist) closestDist = distT;
                        }

                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        {
                            Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Dispatched Omnipresent Passing Train ({0} -> {1}, Consist: {2}) on track '{3}' (Route: {4:F0}m, Closest to Player: {5:F0}m).",
                                origName, destName, consistType, candTrack.name, path.TotalDistance, closestDist));
                        }

                        if (onComplete != null) onComplete(true);
                        yield break;
                    }
                    if (stopSearches) break;
                }
                if (stopSearches) break;
            }

            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
            {
                Main.ModEntry.Logger.Log(string.Format("[TrafficScheduler] Passing train check: Evaluated {0} tracks ({1} path searches) near player but found no viable deadlock-free route.",
                    maxTracksToCheck, pathSearches));
            }

            if (onComplete != null) onComplete(false);
        }

        /// <summary>
        /// Mathematically predicts and validates whether a proposed route is 100% free of head-on traps
        /// or unresolvable single-track bottlenecks with the player and active AI trains.
        /// </summary>
        public static bool PredictDeadlockConflict(
            RailTrack spawnTrack,
            RailPath routePath,
            Vector3 playerPos,
            HashSet<RailTrack> occupiedSnapshot)
        {
            if (spawnTrack == null || routePath == null || routePath.Tracks == null || routePath.Tracks.Count == 0)
                return false;

            // 1. Initial departure incline check
            float depGrade = CalculateDepartureGrade(routePath);
            if (depGrade > MaxEncounterInclineGrade)
                return false;

            // 2. Physical player car & track conflict check
            RailTrack playerTrack = null;
            float playerSpeedMs = 0f;
            Vector3 playerMoveDir = Vector3.zero;

            if (PlayerManager.Car != null)
            {
                if (PlayerManager.Car.FrontBogie != null && PlayerManager.Car.FrontBogie.track != null)
                    playerTrack = PlayerManager.Car.FrontBogie.track;
                else if (PlayerManager.Car.RearBogie != null && PlayerManager.Car.RearBogie.track != null)
                    playerTrack = PlayerManager.Car.RearBogie.track;

                playerSpeedMs = Mathf.Abs(PlayerManager.Car.GetForwardSpeed());
                if (playerSpeedMs > 0.6f && PlayerManager.Car.transform != null)
                {
                    playerMoveDir = PlayerManager.Car.transform.forward * (PlayerManager.Car.GetForwardSpeed() >= 0f ? 1f : -1f);
                }
            }

            if (ModCompatManager.IsDVSignalsLoaded)
            {
                Signals.Game.Signal _;
                for (int i = 0; i < routePath.Tracks.Count; i++)
                {
                    if (Signals.Game.Railway.TrackReserver.IsTrackReserved(routePath.Tracks[i], out _))
                    {
                        return false;
                    }
                }
            }

            // Rule A: Never route through the player's occupied track
            if (playerTrack != null && routePath.Tracks.Contains(playerTrack))
            {
                return false;
            }

            // Rule B: Head-On Conflict on Single Track with moving player
            Vector3 heading = (playerSpeedMs > 0.6f && playerMoveDir != Vector3.zero)
                ? playerMoveDir
                : (PlayerManager.Car != null && PlayerManager.Car.transform != null ? PlayerManager.Car.transform.forward : Vector3.zero);
            if (playerTrack != null && heading != Vector3.zero)
            {
                var playerProjected = ProjectTracksAhead(playerTrack, heading, 25);
                for (int p = 0; p < playerProjected.Count; p++)
                {
                    var projTrack = playerProjected[p];
                    if (routePath.Tracks.Contains(projTrack))
                    {
                        var edge = (AITraffic.Navigation.RailGraph.Instance != null) ? AITraffic.Navigation.RailGraph.Instance.GetEdge(projTrack) : null;
                        bool isDouble = (edge != null && edge.IsDoubleTrackMainline);
                        if (!isDouble)
                        {
                            // Opposing traffic on single track! Ensure a passing siding or double-track section exists between them
                            int meetIdx = routePath.Tracks.IndexOf(projTrack);
                            bool hasPassingLoopBetween = false;
                            for (int i = 0; i < meetIdx; i++)
                            {
                                var intermediate = routePath.Tracks[i];
                                if (AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(intermediate))
                                {
                                    hasPassingLoopBetween = true;
                                    break;
                                }
                                var iEdge = (AITraffic.Navigation.RailGraph.Instance != null) ? AITraffic.Navigation.RailGraph.Instance.GetEdge(intermediate) : null;
                                if (iEdge != null && iEdge.IsDoubleTrackMainline)
                                {
                                    hasPassingLoopBetween = true;
                                    break;
                                }
                            }

                            if (!hasPassingLoopBetween)
                            {
                                return false; // Deadlock: Opposing traffic on single track without passing loop!
                            }
                        }
                    }
                }
            }

            // Rule C: Active AI Train Interlock
            if (TrafficManager.Instance != null && TrafficManager.Instance.ActiveEngineers != null)
            {
                var engineers = TrafficManager.Instance.ActiveEngineers;
                for (int a = 0; a < engineers.Count; a++)
                {
                    var eng = engineers[a];
                    if (eng == null || eng.CurrentPath == null || eng.CurrentPath.Tracks == null) continue;

                    int curIdx = eng.CurrentPathTrackIndex;
                    var engTracks = eng.CurrentPath.Tracks;
                    for (int i = curIdx; i < engTracks.Count; i++)
                    {
                        var eTrk = engTracks[i];
                        if (eTrk == null) continue;

                        if (routePath.Tracks.Contains(eTrk))
                        {
                            var edge = (AITraffic.Navigation.RailGraph.Instance != null) ? AITraffic.Navigation.RailGraph.Instance.GetEdge(eTrk) : null;
                            bool isDouble = (edge != null && edge.IsDoubleTrackMainline);
                            if (!isDouble)
                            {
                                int meetIdx = routePath.Tracks.IndexOf(eTrk);

                                // Check if active train has already reserved this single track
                                if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsTrackReservedByOther(eTrk, null))
                                {
                                    return false; // Track is already reserved by active traffic!
                                }

                                // Check direction of movement along eTrk
                                bool isOpposing = false;
                                if (i < engTracks.Count - 1 && meetIdx > 0 && routePath.Tracks[meetIdx - 1] == engTracks[i + 1])
                                {
                                    isOpposing = true;
                                }
                                else if (i > curIdx && meetIdx < routePath.Tracks.Count - 1 && routePath.Tracks[meetIdx + 1] == engTracks[i - 1])
                                {
                                    isOpposing = true;
                                }
                                else if (eTrk.curve != null)
                                {
                                    Vector3 engVec = (i < engTracks.Count - 1 && engTracks[i + 1].curve != null)
                                        ? (engTracks[i + 1].curve.GetPointAt(0.5f) - eTrk.curve.GetPointAt(0.5f)).normalized
                                        : Vector3.zero;
                                    Vector3 candVec = (meetIdx < routePath.Tracks.Count - 1 && routePath.Tracks[meetIdx + 1].curve != null)
                                        ? (routePath.Tracks[meetIdx + 1].curve.GetPointAt(0.5f) - eTrk.curve.GetPointAt(0.5f)).normalized
                                        : Vector3.zero;
                                    if (engVec != Vector3.zero && candVec != Vector3.zero && Vector3.Dot(engVec, candVec) < -0.2f)
                                    {
                                        isOpposing = true;
                                    }
                                }

                                if (isOpposing)
                                {
                                    bool hasIntermediateLoop = false;
                                    // Intermediate passing loop must be along the line between station exit (m >= 2) and meeting point
                                    for (int m = 2; m < meetIdx - 1; m++)
                                    {
                                        var intermediate = routePath.Tracks[m];
                                        if (AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(intermediate))
                                        {
                                            hasIntermediateLoop = true;
                                            break;
                                        }
                                        var iEdge = (AITraffic.Navigation.RailGraph.Instance != null) ? AITraffic.Navigation.RailGraph.Instance.GetEdge(intermediate) : null;
                                        if (iEdge != null && iEdge.IsDoubleTrackMainline)
                                        {
                                            hasIntermediateLoop = true;
                                            break;
                                        }
                                    }

                                    if (!hasIntermediateLoop)
                                    {
                                        return false; // Deadlock conflict: Opposing active AI train on single track without passing loop!
                                    }
                                }
                            }
                        }
                    }
                }
            }

            return true;
        }

        private static List<RailTrack> ProjectTracksAhead(RailTrack startTrack, Vector3 heading, int maxTracks)
        {
            var list = new List<RailTrack>(maxTracks);
            if (startTrack == null || maxTracks <= 0) return list;

            RailTrack cur = startTrack;
            Vector3 curHeading = heading;

            for (int i = 0; i < maxTracks; i++)
            {
                if (cur.curve == null) break;
                Vector3 curStart = cur.curve.GetPointAt(0f);
                Vector3 curEnd = cur.curve.GetPointAt(1f);

                bool forward = Vector3.Dot(curHeading, curEnd - curStart) >= 0f;
                Junction nextJunction = forward ? cur.outJunction : cur.inJunction;
                if (nextJunction == null) break;

                RailTrack nextTrack = null;
                if (nextJunction.outBranches != null && nextJunction.selectedBranch < nextJunction.outBranches.Count)
                {
                    var branch = nextJunction.outBranches[nextJunction.selectedBranch];
                    if (branch != null && branch.track != null && branch.track != cur)
                    {
                        nextTrack = branch.track;
                    }
                }
                else if (nextJunction.inBranch != null && nextJunction.inBranch.track != cur)
                {
                    nextTrack = nextJunction.inBranch.track;
                }

                if (nextTrack == null || list.Contains(nextTrack)) break;
                list.Add(nextTrack);
                cur = nextTrack;
                if (cur.curve != null)
                {
                    curHeading = (cur.curve.GetPointAt(1f) - cur.curve.GetPointAt(0f)).normalized;
                    if (!forward) curHeading = -curHeading;
                }
            }

            return list;
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
                if (playerPos == Vector3.zero && Camera.main != null)
                {
                    playerPos = Camera.main.transform.position;
                }

                var occupiedSnapshot = (AITraffic.Navigation.RailGraph.Instance != null)
                    ? AITraffic.Navigation.RailGraph.BuildOccupiedTracksSnapshot()
                    : new HashSet<RailTrack>();

                // --- PASSING TRAIN DISPATCH ---
                // Spawns omnipresent passing trains on any valid track within 500m-1400m of the player,
                // backed by the Predictive Deadlock Engine to guarantee zero head-on traps or blockages.
                bool shouldTryPassing = (playerPos != Vector3.zero && (Main.Settings == null || Main.Settings.MoreTrainEncounters || _nextDispatchIsEncounter));
                if (shouldTryPassing)
                {
                    bool passingSuccess = false;
                    yield return DispatchPassingTrainCoroutine(occupiedSnapshot, playerPos, (ok) =>
                    {
                        passingSuccess = ok;
                    });

                    if (passingSuccess)
                    {
                        success = true;
                        _nextDispatchIsEncounter = false; // Next train will be standard corridor
                        _lastDispatchTime = Time.time;
                        yield break;
                    }
                }

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

                    RailPath routePath = null;
                    RailTrack spawnTrack = null;

                    // Deterministically generate consist specs once so length check and spawn are 100% identical
                    List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(corridor.PreferredConsist, corridor.OriginYardId, corridor.DestinationYardId, _rng);
                    if (specs == null || specs.Count == 0) continue;

                    List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
                    for (int i = 0; i < specs.Count; i++)
                    {
                        if (specs[i].Livery != null) liveries.Add(specs[i].Livery);
                    }

                    float consistLen = CarSpawner.Instance != null
                        ? CarSpawner.Instance.GetTotalCarLiveriesLength(liveries)
                        : specs.Count * 18f;
                    float requiredTrackLength = consistLen + 35f;
                    yield return FindClearDepartureTrackCoroutine(originStation, destStation, requiredTrackLength, corridor.PreferredConsist, occupiedSnapshot, (t, p) =>
                    {
                        spawnTrack = t;
                        routePath = p;
                    });
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
                                startSpan = 15.0;
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
                            startSpan = 15.0;
                            flipConsist = true;
                        }
                    }

                    // 0.05ms instant mathematical feasibility check before spawning
                    if (!TrainSpawner.CanConsistFitOnTrack(liveries, spawnTrack, startSpan, flipConsist))
                    {
                        continue;
                    }

                    // Yield a frame right before consist instantiation so frame starts with clean GPU/CPU budget
                    // Spawn ambient consist asynchronously, time-slicing 1 car per frame across multiple frames
                    AIEngineer engineer = null;
                    bool spawnComplete = false;

                    AITraffic.Diagnostics.PerformanceProfiler.SetSchedulerState(string.Format("Spawning Corridor on '{0}'", spawnTrack.name));
                    yield return TrafficManager.Instance.StartCoroutine(TrainSpawner.SpawnAITrainCoroutine(
                        spawnTrack,
                        specs,
                        startSpan: startSpan,
                        flipTrainConsist: flipConsist,
                        onComplete: delegate(AIEngineer eng)
                        {
                            engineer = eng;
                            spawnComplete = true;
                        },
                        consistType: corridor.PreferredConsist));

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

                    if (Main.Settings != null && Main.Settings.MoreTrainEncounters)
                    {
                        _nextDispatchIsEncounter = true;
                    }

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

                        RailPath fallbackPath = null;
                        RailTrack spawnTrack = null;
                        string fOrigYard = (station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.YardID)) ? station.stationInfo.YardID : "ORIG";
                        string fDestYard = (candidateDest.stationInfo != null && !string.IsNullOrEmpty(candidateDest.stationInfo.YardID)) ? candidateDest.stationInfo.YardID : "DEST";

                        // Deterministically generate consist specs once so length check and spawn are 100% identical
                        List<ConsistCarSpec> fallbackSpecs = ConsistDefinitions.GetConsistSpecs(inferredConsist, fOrigYard, fDestYard, _rng);
                        if (fallbackSpecs == null || fallbackSpecs.Count == 0) continue;

                        List<TrainCarLivery> fallbackLiveries = new List<TrainCarLivery>(fallbackSpecs.Count);
                        for (int i = 0; i < fallbackSpecs.Count; i++)
                        {
                            if (fallbackSpecs[i].Livery != null) fallbackLiveries.Add(fallbackSpecs[i].Livery);
                        }

                        float fallbackConsistLen = CarSpawner.Instance != null
                            ? CarSpawner.Instance.GetTotalCarLiveriesLength(fallbackLiveries)
                            : fallbackSpecs.Count * 18f;
                        float fallbackRequiredTrackLength = fallbackConsistLen + 35f;
                        yield return FindClearDepartureTrackCoroutine(station, candidateDest, fallbackRequiredTrackLength, inferredConsist, occupiedSnapshot, (t, p) =>
                        {
                            spawnTrack = t;
                            fallbackPath = p;
                        });
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
                                        startSpan = 15.0;
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
                                    startSpan = 15.0;
                                    flipConsist = true;
                                }
                            }

                            // 0.05ms instant mathematical feasibility check before spawning
                            if (!TrainSpawner.CanConsistFitOnTrack(fallbackLiveries, spawnTrack, startSpan, flipConsist))
                                continue;

                            string origYard = (station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.YardID)) ? station.stationInfo.YardID : "ORIG";
                            string destYard = (candidateDest.stationInfo != null && !string.IsNullOrEmpty(candidateDest.stationInfo.YardID)) ? candidateDest.stationInfo.YardID : "DEST";

                            // Spawn ambient consist asynchronously, time-slicing 1 car per frame across multiple frames
                            AIEngineer engineer = null;
                            bool spawnComplete = false;

                            AITraffic.Diagnostics.PerformanceProfiler.SetSchedulerState(string.Format("Spawning Fallback on '{0}'", spawnTrack.name));
                            yield return TrafficManager.Instance.StartCoroutine(TrainSpawner.SpawnAITrainCoroutine(
                                spawnTrack,
                                fallbackSpecs,
                                startSpan: startSpan,
                                flipTrainConsist: flipConsist,
                                onComplete: delegate(AIEngineer eng)
                                {
                                    engineer = eng;
                                    spawnComplete = true;
                                },
                                consistType: inferredConsist));

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

                                if (Main.Settings != null && Main.Settings.MoreTrainEncounters)
                                {
                                    _nextDispatchIsEncounter = true;
                                }

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
                AITraffic.Diagnostics.PerformanceProfiler.SetSchedulerState("Idle");
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

            // Direct track distance to player (must be at least 450m away from player)
            Vector3 spawnPos = (spawnTrack.curve != null) ? spawnTrack.curve.GetPointAt(0.5f) : spawnTrack.transform.position;
            float spawnDistToPlayer = Vector3.Distance(spawnPos, playerPos);
            if (spawnDistToPlayer < 450f) return true;

            // Predictive Deadlock Engine: If route is verified deadlock-free with player and active AI, allow it!
            if (!PredictDeadlockConflict(spawnTrack, routePath, playerPos, null))
            {
                return true; // Unresolvable conflict or deadlock predicted, reject departure!
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

                // Handle Coal Mine aliases: CM / CME
                if (string.Equals(sYard, "CM", StringComparison.OrdinalIgnoreCase) || string.Equals(sYard, "CME", StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrEmpty(sName) && (sName.IndexOf("Coal Mine", StringComparison.OrdinalIgnoreCase) >= 0)))
                {
                    if (!s_stationIndex.ContainsKey("CM")) s_stationIndex["CM"] = sc;
                    if (!s_stationIndex.ContainsKey("CME")) s_stationIndex["CME"] = sc;
                }
            }
        }

        internal static StationController FindStation(string yardId)
        {
            if (string.IsNullOrEmpty(yardId))
                return null;

            EnsureStationIndexBuilt();

            StationController sc;
            if (s_stationIndex.TryGetValue(yardId, out sc))
                return sc;

            return null;
        }

        internal static bool IsInboundTrack(RailTrack t, StationController station)
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

        internal static bool IsPlatformTrack(RailTrack t, StationController station)
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

        internal static StationController FindNearestStation(Vector3 pos)
        {
            if (StationController.allStations == null || StationController.allStations.Count == 0)
                return null;

            StationController nearest = null;
            float minDist = float.MaxValue;
            for (int i = 0; i < StationController.allStations.Count; i++)
            {
                var st = StationController.allStations[i];
                if (st == null) continue;
                float d = Vector3.Distance(st.transform.position, pos);
                if (d < minDist)
                {
                    minDist = d;
                    nearest = st;
                }
            }
            return nearest;
        }

        internal static bool IsDedicatedPassingLoopTrack(RailTrack t)
        {
            if (t == null || t.curve == null) return false;
            if (IsDeadEndTrack(t)) return false;

            string n = t.name ?? "";
            string lower = n.ToLowerInvariant();

            // 1. Dedicated DoubleTrack mod passing sidings
            // e.g. "[y]_[doubletrack]_[Siding-14-D]", "[y]_[doubletrack]_[Siding-DT-Donut-Upper-D] 41"
            if (lower.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 &&
                lower.IndexOf("siding", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 2. Base-game / mod dedicated mainline bypass loops
            // e.g. "JS-BypassAA 24", "[#] HB_BypassA 24", "[#] HB_BypassB 24"
            if (lower.IndexOf("bypass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 3. Station passing loops and sidings ending in "-S]" or containing "[S]"
            // e.g. "[Y]_[GF]_[B-01-S]", "[Y]_[HB]_[D-01-S]", "Siding-01-S]"
            if (lower.EndsWith("-s]") || lower.EndsWith("_s]") || lower.IndexOf("_[s]", StringComparison.OrdinalIgnoreCase) >= 0 || lower.IndexOf("[s]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 4. Generic siding keywords
            if (lower.IndexOf("siding", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("pass", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            // 5. Strictly reject active mainline running tracks, high-speed lines, and passenger platform tracks
            if (n.Contains("[#]") ||
                lower.IndexOf("main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("ml", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("dt-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.StartsWith("[p]") ||
                lower.IndexOf("platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("pax", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // 6. Strictly reject industrial loading/unloading, inbound, outbound, caboose, and active yard shunting tracks
            if (lower.StartsWith("[l]") || lower.StartsWith("[i]") || lower.StartsWith("[o]") || lower.StartsWith("[c]") ||
                lower.IndexOf("-l]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("-i]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("-o]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("-c]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                ModCompatManager.IsTrackActiveYardZone(t))
            {
                return false;
            }

            return false;
        }

        internal static bool IsPassingOrLoopTrack(RailTrack t)
        {
            if (t == null) return false;
            string n = t.name ?? "";
            return n.Contains("[S]") || n.Contains("[#]") ||
                   n.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("_S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Loop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Pass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool IsYardStorageTrack(RailTrack t, StationController station)
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

        private static readonly Dictionary<StationController, List<RailTrack>> s_staticPaxDestTracksCache = new Dictionary<StationController, List<RailTrack>>();
        private static readonly Dictionary<StationController, List<RailTrack>> s_staticFreightDestTracksCache = new Dictionary<StationController, List<RailTrack>>();

        private static List<RailTrack> GetStaticDestinationTracks(StationController destStation, bool isPax)
        {
            if (destStation == null) return null;
            var cache = isPax ? s_staticPaxDestTracksCache : s_staticFreightDestTracksCache;
            List<RailTrack> cached;
            if (cache.TryGetValue(destStation, out cached))
            {
                return cached;
            }

            var results = new List<RailTrack>();
            string yardId = destStation.stationInfo != null ? destStation.stationInfo.YardID : "";

            // 1. Scan tracks assigned to destination station
            if (destStation.AllStationTracks != null)
            {
                if (isPax)
                {
                    // Pass 1: Passenger Platform tracks
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null) continue;
                        if (IsPlatformTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Passing loops / Station sidings
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null) continue;
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
                        if (t == null || IsDeadEndTrack(t)) continue;
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsInboundTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 2: Yard storage / classification tracks [S] (excluding dead-end sidings and active shunting zones)
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t)) continue;
                        if (IsPlatformTrack(t, destStation)) continue;

                        if (IsYardStorageTrack(t, destStation) && !results.Contains(t))
                            results.Add(t);
                    }

                    // Pass 3: Passing loops / mainline station loops
                    for (int i = 0; i < destStation.AllStationTracks.Count; i++)
                    {
                        var t = destStation.AllStationTracks[i];
                        if (t == null || IsDeadEndTrack(t)) continue;
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
                    if (t == null || (!isPax && IsDeadEndTrack(t))) continue;

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

            cache[destStation] = results;
            return results;
        }

        internal static List<RailTrack> GetCandidateDestinationTracks(StationController destStation, ConsistType consistType = ConsistType.RegionalFreight, HashSet<RailTrack> occupiedSnapshot = null)
        {
            var results = new List<RailTrack>();
            if (destStation == null) return results;

            bool isPax = (consistType == ConsistType.PassengerCommuter);
            var staticTracks = GetStaticDestinationTracks(destStation, isPax);
            if (staticTracks != null)
            {
                for (int i = 0; i < staticTracks.Count; i++)
                {
                    var t = staticTracks[i];
                    if (!IsTrackOccupied(t, occupiedSnapshot))
                    {
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
        public const float MaxEncounterInclineGrade = 1.20f;

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

        private static readonly Dictionary<StationController, List<RailTrack>> s_staticDepartureTracksCache = new Dictionary<StationController, List<RailTrack>>();

        private static List<RailTrack> GetStaticDepartureTracks(StationController originStation)
        {
            if (originStation == null) return null;
            List<RailTrack> cached;
            if (s_staticDepartureTracksCache.TryGetValue(originStation, out cached))
            {
                return cached;
            }

            var list = new List<RailTrack>();

            // 1. Designated through tracks in station (prefer flatter station yard tracks)
            if (originStation.AllStationTracks != null)
            {
                for (int i = 0; i < originStation.AllStationTracks.Count; i++)
                {
                    var t = originStation.AllStationTracks[i];
                    if (t == null || t.curve == null || t.curve.length < 75f) continue;
                    if (IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t)) continue;

                    if (IsThroughOrMainlineTrack(t))
                    {
                        list.Add(t);
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
                    if (t.curve == null || t.curve.length < 75f) continue;

                    // Spatial distance check using physical curve midpoint FIRST to skip ~98% of tracks immediately
                    Vector3 trackMid = t.curve.GetPointAt(0.5f);
                    if (Vector3.Distance(trackMid, origPos) > 1500f) continue;

                    // Exclude steep mainline edges (> 0.5% grade)
                    if (Mathf.Abs(edge.Grade) > 0.5f) continue;

                    if (IsDeadEndTrack(t) || ModCompatManager.IsTrackActiveYardZone(t)) continue;

                    if (IsThroughOrMainlineTrack(t) && !list.Contains(t))
                    {
                        list.Add(t);
                    }
                }
            }

            s_staticDepartureTracksCache[originStation] = list;
            return list;
        }

        internal static List<RailTrack> GetCandidateDepartureTracks(StationController originStation, float minLength, HashSet<RailTrack> occupiedSnapshot = null)
        {
            var results = new List<RailTrack>();
            if (originStation == null) return results;

            var staticTracks = GetStaticDepartureTracks(originStation);
            if (staticTracks != null)
            {
                for (int i = 0; i < staticTracks.Count; i++)
                {
                    var t = staticTracks[i];
                    if (t.curve != null && t.curve.length >= minLength && !IsTrackOccupied(t, occupiedSnapshot))
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
                if (depTrack == null || depTrack.curve == null || depTrack.curve.length < minLength) continue;

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

        private static System.Collections.IEnumerator FindClearDepartureTrackCoroutine(
            StationController station,
            StationController destStation,
            float minLength,
            ConsistType consistType,
            HashSet<RailTrack> occupiedSnapshot,
            Action<RailTrack, RailPath> onFound)
        {
            if (station == null)
            {
                if (onFound != null) onFound(null, null);
                yield break;
            }

            if (occupiedSnapshot == null)
            {
                occupiedSnapshot = (AITraffic.Navigation.RailGraph.Instance != null)
                    ? AITraffic.Navigation.RailGraph.BuildOccupiedTracksSnapshot()
                    : new HashSet<RailTrack>();
            }

            var depTracks = GetCandidateDepartureTracks(station, minLength, occupiedSnapshot);
            if (depTracks == null || depTracks.Count == 0)
            {
                if (onFound != null) onFound(null, null);
                yield break;
            }

            if (destStation == null)
            {
                if (onFound != null) onFound(depTracks[0], null);
                yield break;
            }

            var destTracks = GetCandidateDestinationTracks(destStation, consistType, occupiedSnapshot);
            if (destTracks == null || destTracks.Count == 0)
            {
                if (onFound != null) onFound(null, null);
                yield break;
            }

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

            // Evaluate up to top 3 departure tracks and top 2 destination tracks, yielding a frame between searches
            int maxDep = Math.Min(3, depTracks.Count);
            int maxDest = Math.Min(2, destTracks.Count);
            int pathSearches = 0;

            for (int i = 0; i < maxDep; i++)
            {
                var depTrack = depTracks[i];
                if (depTrack == null || depTrack.curve == null || depTrack.curve.length < minLength) continue;

                for (int d = 0; d < maxDest; d++)
                {
                    var dt = destTracks[d];
                    if (depTrack == dt) continue;

                    pathSearches++;
                    yield return null;

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

                        if (onFound != null) onFound(depTrack, path);
                        yield break;
                    }
                }
            }

            if (onFound != null) onFound(null, null);
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
