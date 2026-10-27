using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityModManagerNet;

namespace AITraffic.Compat
{
    /// <summary>
    /// Decoupled runtime compatibility shield for Persistent Jobs (tested against PJ 4.0.0).
    /// Dynamically hooks Persistent Jobs methods via reflection to isolate AI rolling stock,
    /// prevent debt generation/job hijacking, and enable empty yard auto-recovery.
    /// Fully safe and dormant when Persistent Jobs is not present.
    /// </summary>
    public static class PersistentJobsPatches
    {
        private const string HarmonyInstanceId = "com.killermops27.aitraffic.pjcompat";
        private static bool s_initialized;

        // Cached reflection elements for Shield 4
        private static Type s_reassignStatusEnumType;

        // Cached reflection elements for Shield 5
        private static Type s_stationPersistenceType;
        private static FieldInfo s_stationPersistenceInstanceField;
        private static PropertyInfo s_stationPersistenceInstanceProp;
        private static MethodInfo s_getHasStationSpawnedCarsMethod;
        private static MethodInfo s_setHasStationSpawnedCarsMethod;
        private static FieldInfo s_farCarOptStationDictField;
        private static FieldInfo s_farCarOptTracksDictField;

        /// <summary>
        /// Initializes and applies the decoupled compatibility shield hooks for Persistent Jobs.
        /// </summary>
        /// <param name="modEntry">The AI Traffic ModEntry instance for logging.</param>
        public static void Initialize(UnityModManager.ModEntry modEntry)
        {
            if (s_initialized) return;

            if (!ModCompatManager.IsPersistentJobsLoaded)
            {
                if (Main.IsDebugLoggingEnabled)
                    Main.LogDebug("[PersistentJobsPatches] Persistent Jobs not detected. Compatibility shield dormant.");
                return;
            }

            s_initialized = true;
            if (Main.IsDebugLoggingEnabled)
                Main.LogDebug("[PersistentJobsPatches] Initializing decoupled compatibility shield for Persistent Jobs 4.0.0...");

            try
            {
                var harmony = new Harmony(HarmonyInstanceId);

                ApplyShield1(harmony, modEntry);
                ApplyShield2(harmony, modEntry);
                ApplyShield4(harmony, modEntry);
                ApplyShield5(harmony, modEntry);

                if (Main.IsDebugLoggingEnabled)
                    Main.LogDebug("[PersistentJobsPatches] Compatibility shield initialization complete.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Error(string.Format("[PersistentJobsPatches] Failed to initialize Persistent Jobs shield: {0}", ex));
            }
        }

        #region Shield 1 - Debt & Non-Player Conversion Shield

        private static void ApplyShield1(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var targetType = AccessTools.TypeByName("PersistentJobsMod.Utilities.PlayerSpawnedCarUtilities");
                if (targetType == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 1: PlayerSpawnedCarUtilities type not found.");
                    return;
                }

                var targetMethod = AccessTools.Method(targetType, "ConvertPlayerSpawnedTrainCar", new Type[] { typeof(TrainCar) });
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 1: ConvertPlayerSpawnedTrainCar method not found.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(PersistentJobsPatches), "ConvertPlayerSpawnedTrainCar_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[PersistentJobsPatches] Shield 1 applied: Prevent Player Debt & Non-Player Conversion on AI Cars.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 1 hook failed: {0}", ex));
            }
        }

        private static bool ConvertPlayerSpawnedTrainCar_Prefix(TrainCar trainCar)
        {
            try
            {
                if (trainCar != null && (ModCompatManager.IsAITrain(trainCar) ||
                    (trainCar.gameObject != null && trainCar.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                    trainCar.preventDebtDisplay))
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 1 prefix error: {0}", ex));
            }
            return true;
        }

        #endregion

        #region Shield 2 - Despawn & JSON Suspension Shield

        private static void ApplyShield2(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var targetType = AccessTools.TypeByName("PersistentJobsMod.Optimization.FarCarOpt");
                if (targetType == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 2: FarCarOpt type not found.");
                    return;
                }

                // Upstream Filter 1: Invalidate AI trainsets so they are never added to PJ's suspension candidate queue
                var isValidMethod = AccessTools.Method(targetType, "IsTrainsetValid", new Type[] { typeof(IEnumerable<TrainCar>) });
                if (isValidMethod != null)
                {
                    var isValidPrefix = AccessTools.Method(typeof(PersistentJobsPatches), "IsTrainsetValid_Prefix");
                    harmony.Patch(isValidMethod, prefix: new HarmonyMethod(isValidPrefix));
                }
                else if (modEntry != null && modEntry.Logger != null)
                {
                    modEntry.Logger.Warning("[PersistentJobsPatches] Shield 2: IsTrainsetValid method not found.");
                }

                // Upstream Filter 2: Purge loose AI cars from batch list prior to iteration
                var suspendCarsMethod = AccessTools.Method(targetType, "SuspendCars", new Type[] { typeof(List<TrainCar>) });
                if (suspendCarsMethod != null)
                {
                    var suspendCarsPrefix = AccessTools.Method(typeof(PersistentJobsPatches), "SuspendCars_Prefix");
                    harmony.Patch(suspendCarsMethod, prefix: new HarmonyMethod(suspendCarsPrefix));
                }
                else if (modEntry != null && modEntry.Logger != null)
                {
                    modEntry.Logger.Warning("[PersistentJobsPatches] Shield 2: SuspendCars method not found.");
                }

                // Downstream Safety Net: Veto direct SuspendCar calls if any AI car ever reaches it
                var targetMethod = AccessTools.Method(targetType, "SuspendCar", new Type[] { typeof(TrainCar), typeof(JObject) });
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 2: SuspendCar method not found.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(PersistentJobsPatches), "SuspendCar_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[PersistentJobsPatches] Shield 2 applied: Prevent AI Cars from Despawning/Suspending into JSON (Upstream Filter Active).");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 2 hook failed: {0}", ex));
            }
        }

        private static bool IsTrainsetValid_Prefix(IEnumerable<TrainCar> trainset, ref bool __result)
        {
            try
            {
                if (trainset != null)
                {
                    foreach (var car in trainset)
                    {
                        if (car != null && (ModCompatManager.IsAITrain(car) ||
                            (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                            car.preventDebtDisplay))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 2 IsTrainsetValid prefix error: {0}", ex));
            }
            return true;
        }

        private static void SuspendCars_Prefix(List<TrainCar> trainCars)
        {
            try
            {
                if (trainCars != null)
                {
                    trainCars.RemoveAll(car => car != null && (ModCompatManager.IsAITrain(car) ||
                        (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                        car.preventDebtDisplay));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 2 SuspendCars prefix error: {0}", ex));
            }
        }

        private static bool SuspendCar_Prefix(TrainCar trainCar, ref bool __result)
        {
            try
            {
                if (trainCar != null && (ModCompatManager.IsAITrain(trainCar) ||
                    (trainCar.gameObject != null && trainCar.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                    trainCar.preventDebtDisplay))
                {
                    __result = false;
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 2 prefix error: {0}", ex));
            }
            return true;
        }

        #endregion

        #region Shield 3/4 - Procedural Job Booklet & Deletion Shield

        private static void ApplyShield4(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var targetType = AccessTools.TypeByName("PersistentJobsMod.HarmonyPatches.JobGeneration.UnusedTrainCarDeleter_Patches");
                if (targetType == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 4: UnusedTrainCarDeleter_Patches type not found.");
                    return;
                }

                var targetMethod = AccessTools.Method(targetType, "GetTrainCarReassignStatus");
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 4: GetTrainCarReassignStatus method not found.");
                    return;
                }

                s_reassignStatusEnumType = targetMethod.ReturnType;
                var prefix = AccessTools.Method(typeof(PersistentJobsPatches), "GetTrainCarReassignStatus_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[PersistentJobsPatches] Shield 4 applied: Prevent AI Cars from Being Hijacked into Procedural Job Booklets.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 4 hook failed: {0}", ex));
            }
        }

        private static bool GetTrainCarReassignStatus_Prefix(TrainCar trainCar, ref object __result)
        {
            try
            {
                if (trainCar != null && (ModCompatManager.IsAITrain(trainCar) ||
                    (trainCar.gameObject != null && trainCar.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                    trainCar.preventDebtDisplay))
                {
                    if (s_reassignStatusEnumType != null)
                    {
                        try
                        {
                            if (Enum.IsDefined(s_reassignStatusEnumType, "NonRegularTrainCar"))
                            {
                                __result = Enum.Parse(s_reassignStatusEnumType, "NonRegularTrainCar");
                            }
                            else
                            {
                                __result = Enum.ToObject(s_reassignStatusEnumType, 3);
                            }
                        }
                        catch
                        {
                            __result = Enum.ToObject(s_reassignStatusEnumType, 3);
                        }
                    }
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 4 prefix error: {0}", ex));
            }
            return true;
        }

        #endregion

        #region Shield 5 - Empty Yard Auto-Recovery Shield

        private static void ApplyShield5(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                // CRITICAL INVARIANT [RGR-04]: Visibility of FarCarOpt Suspended Cars in Occupancy Snapshot (See GRAVEYARD.md)
                s_stationPersistenceType = AccessTools.TypeByName("PersistentJobsMod.Persistence.StationIdCarSpawningPersistence");
                if (s_stationPersistenceType != null)
                {
                    s_stationPersistenceInstanceField = AccessTools.Field(s_stationPersistenceType, "Instance");
                    s_stationPersistenceInstanceProp = AccessTools.Property(s_stationPersistenceType, "Instance");
                    s_getHasStationSpawnedCarsMethod = AccessTools.Method(s_stationPersistenceType, "GetHasStationSpawnedCarsFlag", new Type[] { typeof(StationController) })
                        ?? AccessTools.Method(s_stationPersistenceType, "GetHasStationSpawnedCarsFlag", new Type[] { typeof(string) });
                    s_setHasStationSpawnedCarsMethod = AccessTools.Method(s_stationPersistenceType, "SetHasStationSpawnedCarsFlag", new Type[] { typeof(StationController), typeof(bool) })
                        ?? AccessTools.Method(s_stationPersistenceType, "SetHasStationSpawnedCarsFlag", new Type[] { typeof(string), typeof(bool) });
                }

                var farCarOptType = AccessTools.TypeByName("PersistentJobsMod.Optimization.FarCarOpt");
                if (farCarOptType != null)
                {
                    s_farCarOptStationDictField = AccessTools.Field(farCarOptType, "StationIDtoSuspendedCarGUID");
                    s_farCarOptTracksDictField = AccessTools.Field(farCarOptType, "TracksToSpaceOccupiedBySuspendedCars");
                    s_farCarOptResumeCarsMethod = AccessTools.Method(farCarOptType, "ResumeCarsInStation", new Type[] { typeof(string), typeof(Action<bool>) });
                }

                var targetType = AccessTools.TypeByName("PersistentJobsMod.HarmonyPatches.CarSpawningJobGeneration.StationProceduralJobsController_Patch");
                if (targetType == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 5: StationProceduralJobsController_Patch type not found.");
                    return;
                }

                var targetMethod = AccessTools.Method(targetType, "TryToGenerateJobs_Prefix");
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[PersistentJobsPatches] Shield 5: TryToGenerateJobs_Prefix method not found.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(PersistentJobsPatches), "TryToGenerateJobs_Prefix_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[PersistentJobsPatches] Shield 5 applied: Empty Yard Auto-Recovery active.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 5 hook failed: {0}", ex));
            }
        }

        private static MethodInfo s_farCarOptResumeCarsMethod;

        private static bool TryToGenerateJobs_Prefix_Prefix(StationProceduralJobsController __instance)
        {
            try
            {
                var station = __instance != null ? __instance.stationController : null;
                if (station == null || station.logicStation == null)
                    return true;

                if (s_stationPersistenceType == null)
                    return true;

                object persistenceInstance = GetStationPersistenceInstance();
                if (persistenceInstance == null)
                    return true;

                // When Self-Shunt (YardMaster) is active, delegate car management to Self-Shunt,
                // resume FarCarOpt suspended cars, and suppress PJ's procedural cars to prevent double-spawning.
                if (ModCompatManager.IsYardMasterLoaded)
                {
                    string yardId = station.stationInfo != null ? station.stationInfo.YardID : station.logicStation.ID;
                    if (!string.IsNullOrEmpty(yardId) && s_farCarOptResumeCarsMethod != null)
                    {
                        try
                        {
                            s_farCarOptResumeCarsMethod.Invoke(null, new object[] { yardId, null });
                        }
                        catch { }
                    }

                    try
                    {
                        var carSpawnerType = AccessTools.TypeByName("SelfShunt.SSCarSpawner");
                        if (carSpawnerType != null)
                        {
                            var popMethod = AccessTools.Method(carSpawnerType, "PopulateMapWithCars");
                            if (popMethod != null)
                            {
                                popMethod.Invoke(null, null);
                            }
                        }
                    }
                    catch { }

                    SetStationSpawnedCarsFlag(persistenceInstance, station, true);
                    __instance.StopJobGeneration();
                    return false;
                }

                bool hasSpawned = CheckHasStationSpawnedCarsFlag(persistenceInstance, station);
                if (!hasSpawned)
                    return true;

                int availableJobs = station.logicStation.availableJobs != null ? station.logicStation.availableJobs.Count : 0;
                int takenJobs = station.logicStation.takenJobs != null ? station.logicStation.takenJobs.Count : 0;
                if (availableJobs > 0 || takenJobs > 0)
                    return true;

                if (HasRegularCarsOnStationTracks(station))
                    return true;

                if (HasSuspendedCarsInStation(station))
                    return true;

                // Empty yard detected: reset flag to false so Persistent Jobs repopulates the yard for the player
                SetStationSpawnedCarsFlag(persistenceInstance, station, false);
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    string yardName = station.stationInfo != null ? station.stationInfo.YardID : station.name;
                    Main.ModEntry.Logger.Log(string.Format("[PersistentJobsPatches] Shield 5: Empty yard detected at {0}. Reset HasStationSpawnedCarsFlag to allow PJ repopulation.", yardName));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Shield 5 prefix error: {0}", ex));
            }

            return true;
        }

        private static object GetStationPersistenceInstance()
        {
            if (s_stationPersistenceType == null) return null;
            if (s_stationPersistenceInstanceField != null)
            {
                return s_stationPersistenceInstanceField.GetValue(null);
            }
            if (s_stationPersistenceInstanceProp != null)
            {
                return s_stationPersistenceInstanceProp.GetValue(null, null);
            }
            return null;
        }

        private static bool CheckHasStationSpawnedCarsFlag(object instance, StationController station)
        {
            if (s_getHasStationSpawnedCarsMethod == null || instance == null || station == null) return false;
            var parameters = s_getHasStationSpawnedCarsMethod.GetParameters();
            if (parameters.Length == 0) return false;
            object arg = parameters[0].ParameterType == typeof(string)
                ? (object)(station.stationInfo != null ? station.stationInfo.YardID : station.name)
                : station;
            return (bool)s_getHasStationSpawnedCarsMethod.Invoke(instance, new object[] { arg });
        }

        private static void SetStationSpawnedCarsFlag(object instance, StationController station, bool value)
        {
            if (s_setHasStationSpawnedCarsMethod == null || instance == null || station == null) return;
            var parameters = s_setHasStationSpawnedCarsMethod.GetParameters();
            if (parameters.Length < 2) return;
            object arg = parameters[0].ParameterType == typeof(string)
                ? (object)(station.stationInfo != null ? station.stationInfo.YardID : station.name)
                : station;
            s_setHasStationSpawnedCarsMethod.Invoke(instance, new object[] { arg, value });
        }

        private static bool HasRegularCarsOnStationTracks(StationController station)
        {
            if (station == null || station.AllStationTracks == null) return false;

            var checkedCars = new HashSet<TrainCar>();
            for (int i = 0; i < station.AllStationTracks.Count; i++)
            {
                var railTrack = station.AllStationTracks[i];
                if (railTrack == null) continue;

                var logicTrack = ModCompatManager.GetLogicTrack(railTrack);
                if (logicTrack == null) continue;

                var fully = logicTrack.GetCarsFullyOnTrack();
                if (fully != null)
                {
                    for (int j = 0; j < fully.Count; j++)
                    {
                        var logicCar = fully[j];
                        if (logicCar == null) continue;
                        var tc = logicCar.TrainCar();
                        if (tc != null && checkedCars.Add(tc))
                        {
                            if (!IsAITrainOrPlayerCar(tc))
                            {
                                return true;
                            }
                        }
                    }
                }

                var partly = logicTrack.GetCarsPartiallyOnTrack();
                if (partly != null)
                {
                    for (int j = 0; j < partly.Count; j++)
                    {
                        var logicCar = partly[j];
                        if (logicCar == null) continue;
                        var tc = logicCar.TrainCar();
                        if (tc != null && checkedCars.Add(tc))
                        {
                            if (!IsAITrainOrPlayerCar(tc))
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private static bool HasSuspendedCarsInStation(StationController station)
        {
            try
            {
                if (s_farCarOptStationDictField != null && station != null && station.stationInfo != null && !string.IsNullOrEmpty(station.stationInfo.YardID))
                {
                    var dict = s_farCarOptStationDictField.GetValue(null) as IDictionary;
                    if (dict != null && dict.Contains(station.stationInfo.YardID))
                    {
                        var collection = dict[station.stationInfo.YardID] as ICollection;
                        if (collection != null && collection.Count > 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool IsAITrainOrPlayerCar(TrainCar car)
        {
            if (car == null) return true;
            if (ModCompatManager.IsAITrain(car)) return true;
            if (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) return true;
            if (car.preventDebtDisplay) return true;
            if (car.playerSpawnedCar) return true;
            return false;
        }

        /// <summary>
        /// Queries PersistentJobsMod.Optimization.FarCarOpt.TracksToSpaceOccupiedBySuspendedCars
        /// and adds all RailTracks currently holding suspended/despawned cars to the occupied set.
        /// This prevents pathfinding, reservations, and dispatching from routing through or spawning on
        /// tracks containing suspended cars.
        /// </summary>
        public static void AddSuspendedOccupiedTracks(HashSet<RailTrack> occupiedSet)
        {
            if (occupiedSet == null || !ModCompatManager.IsPersistentJobsLoaded || s_farCarOptTracksDictField == null)
                return;

            try
            {
                var dict = s_farCarOptTracksDictField.GetValue(null) as IDictionary;
                if (dict == null || dict.Count == 0) return;

                foreach (object key in dict.Keys)
                {
                    var logicTrack = key as DV.Logic.Job.Track;
                    if (logicTrack != null)
                    {
                        var railTrack = ModCompatManager.GetRailTrack(logicTrack);
                        if (railTrack != null)
                        {
                            occupiedSet.Add(railTrack);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[PersistentJobsPatches] Error reading TracksToSpaceOccupiedBySuspendedCars: {0}", ex));
            }
        }

        #endregion
    }
}
