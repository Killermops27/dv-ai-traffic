using System;
using System.Collections.Generic;
using HarmonyLib;
using AITraffic.Workers;
using AITraffic.Navigation;
using AITraffic.Core;
using Signals.Game.Controllers;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches that dynamically wake up station yard generation when player-employed AI workers
    /// or AI trains approach a destination station, and inhibit premature job/car deletion.
    /// Fully compatible with vanilla Derail Valley, PersistentJobsMod, and SelfShunt.
    /// </summary>
    public static class StationWakeUpPatches
    {
        [HarmonyPatch(typeof(StationJobGenerationRange), "IsPlayerInJobGenerationZone")]
        public static class IsPlayerInJobGenerationZone_Patch
        {
            public static void Postfix(StationJobGenerationRange __instance, ref bool __result)
            {
                if (__result) return; // Player is already in range, nothing to override

                try
                {
                    if (StationWakeUpManager.Instance.ShouldWakeStation(__instance))
                    {
                        __result = true;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("Error in IsPlayerInJobGenerationZone_Patch: {0}", ex));
                }
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange), "IsPlayerOutOfJobDestroyZone")]
        public static class IsPlayerOutOfJobDestroyZone_Patch
        {
            public static void Postfix(StationJobGenerationRange __instance, ref bool __result)
            {
                if (!__result) return; // Not out of zone, nothing to override

#if DEBUG
                long __wdStart = AITraffic.Diagnostics.HitchWatchdog.GetTimestamp();
                long __wdMem = AITraffic.Diagnostics.HitchWatchdog.GetCurrentMemory();
                try {
#endif

                try
                {
                    // Direct distance calculation eliminates re-entrant Harmony postfix recursion on IsPlayerInJobGenerationZone.
                    // In Derail Valley, generateJobsSqrDistance represents JobGenerationZoneRadius squared.
                    bool inGenZone = __instance.PlayerSqrDistanceFromStationCenter <= __instance.generateJobsSqrDistance;

                    // Track-specific protection: Only inhibit destruction if the station yard contains tracks currently
                    // occupied or assigned to active AI/worker rolling stock. This allows vanilla Derail Valley to freely
                    // clear and refresh unoccupied tracks after its normal 30-minute timer without triggering overlapping collisions.
                    bool shouldInhibit = false;
                    if (StationWakeUpManager.Instance.ShouldInhibitJobDestroy(__instance))
                    {
                        StationController station = StationWakeUpManager.Instance.GetStationFromRange(__instance);
                        if (station != null && HasYardTracksOccupiedOrAssigned(station))
                        {
                            shouldInhibit = true;
                        }
                    }

                    // Strict Mutex Invariant: A station that is actively waking up, has an active inhibition on occupied/assigned yard tracks,
                    // or is already within the job generation zone can NEVER be considered out of the job destroy zone.
                    // This permanently eliminates the 2-frame infinite start/stop flip-flop loop.
                    if (StationWakeUpManager.Instance.ShouldWakeStation(__instance) ||
                        shouldInhibit ||
                        inGenZone)
                    {
                        __result = false;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("Error in IsPlayerOutOfJobDestroyZone_Patch: {0}", ex));
                }
#if DEBUG
                } finally {
                    AITraffic.Diagnostics.HitchWatchdog.RecordStationPatch(__wdStart, __wdMem);
                }
#endif
            }

            /// <summary>
            /// Determines whether a station yard contains tracks currently occupied or assigned to active AI/worker rolling stock.
            /// </summary>
            private static bool HasYardTracksOccupiedOrAssigned(StationController station)
            {
                if (station == null) return false;

                var stationTracks = station.AllStationTracks;
                if (stationTracks == null || stationTracks.Count == 0) return false;

                var trackSet = new HashSet<RailTrack>(stationTracks);

                // 1. Check worker task track assignments (destination and origin tracks)
                if (WorkerManager.Instance != null)
                {
                    var activeTasks = WorkerManager.Instance.ActiveTasks;
                    if (activeTasks != null)
                    {
                        for (int i = 0; i < activeTasks.Count; i++)
                        {
                            var task = activeTasks[i];
                            if (task == null) continue;
                            if (task.DestinationTrack != null && trackSet.Contains(task.DestinationTrack))
                                return true;
                            if (task.OriginTrack != null && trackSet.Contains(task.OriginTrack))
                                return true;
                        }
                    }

                    var completedTasks = WorkerManager.Instance.CompletedTasks;
                    if (completedTasks != null)
                    {
                        for (int i = 0; i < completedTasks.Count; i++)
                        {
                            var task = completedTasks[i];
                            if (task != null && task.DestinationTrack != null && trackSet.Contains(task.DestinationTrack))
                                return true;
                        }
                    }
                }

                // 2. Check ambient AI train route assignments (destination track or route path through station yard)
                if (TrafficManager.Instance != null)
                {
                    var engineers = TrafficManager.Instance.ActiveEngineers;
                    if (engineers != null)
                    {
                        for (int i = 0; i < engineers.Count; i++)
                        {
                            var eng = engineers[i];
                            if (eng == null) continue;

                            if (eng.CurrentPath != null && eng.CurrentPath.Tracks != null)
                            {
                                var pathTracks = eng.CurrentPath.Tracks;
                                for (int p = 0; p < pathTracks.Count; p++)
                                {
                                    if (pathTracks[p] != null && trackSet.Contains(pathTracks[p]))
                                        return true;
                                }
                            }
                        }
                    }
                }

                // 3. Check physical occupancy across station tracks: are any bogies of active AI or worker rolling stock on these tracks?
                for (int i = 0; i < stationTracks.Count; i++)
                {
                    var trk = stationTracks[i];
                    if (trk == null) continue;

                    try
                    {
                        var bogies = trk.BogiesOnTrack();
                        if (bogies != null && bogies.Count > 0)
                        {
                            foreach (var bogie in bogies)
                            {
                                if (bogie != null && bogie.Car != null)
                                {
                                    if (ModCompatManager.IsAITrain(bogie.Car) || ModCompatManager.IsWorkerTrain(bogie.Car))
                                    {
                                        return true;
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // Guard against potential transient physics errors during scene transitions
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Harmony patch ensuring DVSignals controllers stay awake and actively evaluate
        /// block occupancy and aspects when AI trains are within approach distance (1500m),
        /// even when the player is far away across the map.
        /// Eliminates dormant signals displaying false green aspects to opposing traffic.
        /// </summary>
        [HarmonyPatch(typeof(BasicSignalController), "ShouldUpdate")]
        public static class BasicSignalController_ShouldUpdate_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(BasicSignalController __instance, ref bool __result)
            {
                if (__result) return; // Player camera is already within 1500m

#if DEBUG
                long __wdStart = AITraffic.Diagnostics.HitchWatchdog.GetTimestamp();
                long __wdMem = AITraffic.Diagnostics.HitchWatchdog.GetCurrentMemory();
                bool __wokeUp = false;
                try {
#endif

                try
                {
                    if (SignalRegistry.IsSignalNearAnyAITrain(__instance, 1500f))
                    {
                        __result = true;
#if DEBUG
                        __wokeUp = true;
#endif
                    }
                }
                catch (Exception)
                {
                    // Guard against potential null refs during scene transitions
                }
#if DEBUG
                } finally {
                    AITraffic.Diagnostics.HitchWatchdog.RecordSignalPatch(__wdStart, __wdMem, __wokeUp);
                }
#endif
            }
        }
    }
}
