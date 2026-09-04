using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Config;
using AITraffic.Core;
using AITraffic.Driver;

namespace AITraffic.Workers
{
    /// <summary>
    /// Coordinates dynamic station wake-up (generating procedural jobs and yard cars)
    /// when player-employed AI workers or AI trains approach a destination station.
    /// Fully compatible with PersistentJobsMod, SelfShunt, PassengerJobs, and DVSignals.
    /// </summary>
    public class StationWakeUpManager
    {
        private static StationWakeUpManager s_instance;
        public static StationWakeUpManager Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new StationWakeUpManager();
                }
                return s_instance;
            }
        }

        private readonly HashSet<StationController> _wokenStations = new HashSet<StationController>();
        private readonly Dictionary<StationJobGenerationRange, StationController> _rangeToStation = new Dictionary<StationJobGenerationRange, StationController>();

        private StationWakeUpManager() { }

        /// <summary>
        /// Resolves the StationController associated with a given StationJobGenerationRange.
        /// </summary>
        public StationController GetStationFromRange(StationJobGenerationRange range)
        {
            if (range == null) return null;

            StationController station;
            if (_rangeToStation.TryGetValue(range, out station) && station != null)
            {
                return station;
            }

            station = range.GetComponentInParent<StationController>() ?? range.GetComponent<StationController>();
            if (station == null && StationController.allStations != null)
            {
                Vector3 center = range.stationCenterAnchor != null ? range.stationCenterAnchor.position : range.transform.position;
                float bestDistSq = float.MaxValue;
                for (int i = 0; i < StationController.allStations.Count; i++)
                {
                    var s = StationController.allStations[i];
                    if (s == null) continue;
                    float distSq = (s.transform.position - center).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        station = s;
                    }
                }
            }

            if (station != null)
            {
                _rangeToStation[range] = station;
            }

            return station;
        }

        /// <summary>
        /// Evaluates whether an approaching AI train or player-employed worker should trigger
        /// procedural job and yard car generation for this station.
        /// </summary>
        public bool ShouldWakeStation(StationJobGenerationRange range)
        {
            if (range == null) return false;
            if (Main.Settings == null || Main.Settings.StationWakeUp == StationWakeUpMode.Disabled) return false;

            StationController station = GetStationFromRange(range);
            if (station == null) return false;

            Vector3 center = range.stationCenterAnchor != null ? range.stationCenterAnchor.position : station.transform.position;
            float wakeSqrDist = Mathf.Max(range.generateJobsSqrDistance, 1440000f); // at least 1200m (1200^2 = 1,440,000)

            // 1. Check Player-Employed Workers
            if (Main.Settings.StationWakeUp == StationWakeUpMode.WorkerTrainsOnly || Main.Settings.StationWakeUp == StationWakeUpMode.AllAITrains)
            {
                var activeTasks = WorkerManager.Instance.ActiveTasks;
                if (activeTasks != null && activeTasks.Count > 0)
                {
                    for (int i = 0; i < activeTasks.Count; i++)
                    {
                        var task = activeTasks[i];
                        if (task == null || task.LeadLocomotive == null) continue;

                        // Check if this station is the worker's destination or origin
                        if (task.DestinationStation == station || task.OriginStation == station)
                        {
                            float distSq = (task.LeadLocomotive.transform.position - center).sqrMagnitude;
                            if (distSq <= wakeSqrDist)
                            {
                                if (!_wokenStations.Contains(station))
                                {
                                    _wokenStations.Add(station);
                                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                    {
                                        string sName = station.stationInfo != null ? station.stationInfo.Name : station.name;
                                        Main.ModEntry.Logger.Log(string.Format("[StationWakeUp] Waking station '{0}' for approaching worker train '{1}' (dist: {2:F0}m).",
                                            sName, task.LeadLocomotive.ID, Mathf.Sqrt(distSq)));
                                    }
                                }
                                return true;
                            }
                        }
                    }
                }
            }

            // 2. Check Ambient AI Trains (if AllAITrains mode is selected)
            if (Main.Settings.StationWakeUp == StationWakeUpMode.AllAITrains && TrafficManager.Instance != null)
            {
                var engineers = TrafficManager.Instance.ActiveEngineers;
                if (engineers != null && engineers.Count > 0)
                {
                    for (int i = 0; i < engineers.Count; i++)
                    {
                        var eng = engineers[i];
                        if (eng == null || eng.TrainCar == null) continue;

                        float distSq = (eng.TrainCar.transform.position - center).sqrMagnitude;
                        if (distSq <= wakeSqrDist)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Prevents premature destruction/despawning of un-taken jobs and yard cars
        /// while an AI worker train is operating in or approaching the station.
        /// </summary>
        public bool ShouldInhibitJobDestroy(StationJobGenerationRange range)
        {
            if (range == null) return false;
            if (Main.Settings == null || Main.Settings.StationWakeUp == StationWakeUpMode.Disabled) return false;

            StationController station = GetStationFromRange(range);
            if (station == null) return false;

            var activeTasks = WorkerManager.Instance.ActiveTasks;
            if (activeTasks != null && activeTasks.Count > 0)
            {
                Vector3 center = range.stationCenterAnchor != null ? range.stationCenterAnchor.position : station.transform.position;
                float destroySqrDist = Mathf.Max(range.destroyGeneratedJobsSqrDistanceRegular, 2560000f); // 1600m^2

                for (int i = 0; i < activeTasks.Count; i++)
                {
                    var task = activeTasks[i];
                    if (task == null || task.LeadLocomotive == null) continue;

                    if (task.DestinationStation == station || task.OriginStation == station)
                    {
                        float distSq = (task.LeadLocomotive.transform.position - center).sqrMagnitude;
                        if (distSq <= destroySqrDist)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks if a track is the reserved arrival track for an active worker task.
        /// Used by track reservation filters to direct procedural car generation to other sidings.
        /// </summary>
        public bool IsArrivalTrackReserved(RailTrack track)
        {
            if (track == null || Main.Settings == null || !Main.Settings.ReserveArrivalTrackFirst) return false;

            var activeTasks = WorkerManager.Instance.ActiveTasks;
            if (activeTasks == null || activeTasks.Count == 0) return false;

            for (int i = 0; i < activeTasks.Count; i++)
            {
                var task = activeTasks[i];
                if (task != null && task.DestinationTrack == track)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
