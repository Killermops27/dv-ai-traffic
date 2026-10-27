using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DV;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.ThingTypes.TransitionHelpers;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace AITraffic.Compat
{
    /// <summary>
    /// Decoupled runtime stability and compatibility guardian for the official, unmodified Self-Shunt mod (YardMaster).
    /// Transparently fixes official Self-Shunt internal engine bugs at runtime (spawner lockup, dictionary crashes, cargo bloat)
    /// and shields AI Traffic rolling stock from being hijacked into player shunting tasks.
    /// Strictly soft-bound via dynamic reflection with zero compile-time references to SelfShunt.dll.
    /// </summary>
    public static class SelfShuntPatches
    {
        private const string HarmonyInstanceId = "com.killermops27.aitraffic.selfshuntcompat";
        private static bool s_initialized;
        private static bool s_cargoExpanderInitialized;
        private static readonly System.Random s_random = new System.Random();

        // Cached reflection elements for SelfShunt
        private static Type s_jobMechanicsType;
        private static FieldInfo s_trackToStationControllerField;
        private static Type s_carSpawnerType;
        private static FieldInfo s_spawingField;

        /// <summary>
        /// Initializes and applies the runtime stability and protection hooks for official Self-Shunt.
        /// </summary>
        /// <param name="modEntry">The AI Traffic ModEntry instance for logging.</param>
        public static void Initialize(UnityModManager.ModEntry modEntry)
        {
            if (s_initialized) return;

            if (!ModCompatManager.IsYardMasterLoaded)
            {
                if (Main.IsDebugLoggingEnabled)
                    Main.LogDebug("[SelfShuntPatches] Self-Shunt (YardMaster) not detected. Guardian dormant.");
                return;
            }

            s_initialized = true;
            if (Main.IsDebugLoggingEnabled)
                Main.LogDebug("[SelfShuntPatches] Initializing runtime stability guardian for official Self-Shunt (YardMaster)...");

            try
            {
                var harmony = new Harmony(HarmonyInstanceId);

                ApplyAIProtectionShield(harmony, modEntry);
                ApplyTrackDictionaryFix(harmony, modEntry);
                ApplyOptimizerBypass(harmony, modEntry);
                ApplySafeCarSpawner(harmony, modEntry);
                ApplyCargoExpanderFix(harmony, modEntry);
                ApplyJobValidatorExpireGuard(harmony, modEntry);
                ApplyAIAnchorImmunity(harmony, modEntry);

                if (Main.IsDebugLoggingEnabled)
                    Main.LogDebug("[SelfShuntPatches] Self-Shunt runtime guardian successfully initialized.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Error(string.Format("[SelfShuntPatches] Failed to initialize Self-Shunt guardian: {0}", ex));
            }
        }

        #region Shield 1 - AI Traffic Car Theft Protection

        private static void ApplyAIProtectionShield(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                s_jobMechanicsType = AccessTools.TypeByName("SelfShunt.JobMechanics");
                if (s_jobMechanicsType == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[SelfShuntPatches] JobMechanics type not found.");
                    return;
                }

                var targetMethod = AccessTools.Method(s_jobMechanicsType, "AddCarsToJob", new Type[] { typeof(List<Car>), typeof(Job) });
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[SelfShuntPatches] AddCarsToJob method not found.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "AddCarsToJob_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 1 applied: Protect AI rolling stock from being hijacked into Self-Shunt jobs.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 1 hook failed: {0}", ex));
            }
        }

        private static bool AddCarsToJob_Prefix(List<Car> validCars)
        {
            try
            {
                if (validCars != null)
                {
                    for (int i = validCars.Count - 1; i >= 0; i--)
                    {
                        var car = validCars[i];
                        if (car == null) continue;

                        var tc = car.TrainCar();
                        if (tc != null && (ModCompatManager.IsAITrain(tc) ||
                            (tc.gameObject != null && tc.gameObject.GetComponent<AITrafficCarMarker>() != null) ||
                            tc.preventDebtDisplay))
                        {
                            validCars.RemoveAt(i);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SelfShuntPatches] AddCarsToJob prefix error: {0}", ex));
            }
            return true;
        }

        #endregion

        #region Shield 2 - Fix Track Dictionary Duplicate Key Crash

        private static void ApplyTrackDictionaryFix(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                if (s_jobMechanicsType == null) s_jobMechanicsType = AccessTools.TypeByName("SelfShunt.JobMechanics");
                if (s_jobMechanicsType == null) return;

                s_trackToStationControllerField = AccessTools.Field(s_jobMechanicsType, "trackToStationController");

                var targetMethod = AccessTools.Method(s_jobMechanicsType, "PopulateTracks");
                if (targetMethod == null)
                {
                    if (modEntry != null && modEntry.Logger != null)
                        modEntry.Logger.Warning("[SelfShuntPatches] PopulateTracks method not found.");
                    return;
                }

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "PopulateTracks_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 2 applied: Fixed official Self-Shunt track dictionary duplicate key crash.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 2 hook failed: {0}", ex));
            }
        }

        private static bool PopulateTracks_Prefix()
        {
            try
            {
                if (s_trackToStationControllerField != null)
                {
                    var dict = s_trackToStationControllerField.GetValue(null) as Dictionary<string, StationController>;
                    if (dict != null)
                    {
                        dict.Clear();
                        if (StationController.allStations != null)
                        {
                            for (int i = 0; i < StationController.allStations.Count; i++)
                            {
                                var station = StationController.allStations[i];
                                if (station == null || station.AllStationTracks == null) continue;

                                for (int j = 0; j < station.AllStationTracks.Count; j++)
                                {
                                    var track = station.AllStationTracks[j];
                                    if (track == null) continue;

                                    var logicTrack = ModCompatManager.GetLogicTrack(track);
                                    if (logicTrack == null || logicTrack.ID == null || string.IsNullOrEmpty(logicTrack.ID.FullID)) continue;

                                    // Indexer assignment prevents ArgumentException on shared yard tracks
                                    dict[logicTrack.ID.FullID] = station;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SelfShuntPatches] PopulateTracks safe replacement error: {0}", ex));
            }
            return false; // Skip the buggy original PopulateTracks method!
        }

        #endregion

        #region Shield 3 - Disable Buggy Kinematic Optimizer

        private static void ApplyOptimizerBypass(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var ssMod = UnityModManager.FindMod("SelfShunt") ?? UnityModManager.FindMod("YardMaster");
                if (ssMod != null && ssMod.Info != null && !string.IsNullOrEmpty(ssMod.Info.Version))
                {
                    try
                    {
                        var ver = new Version(ssMod.Info.Version);
                        if (ver.Major >= 2)
                        {
                            if (modEntry != null && modEntry.Logger != null)
                                modEntry.Logger.Log("[SelfShuntPatches] Shield 3 skipped: Self-Shunt v2.0.0+ detected with safe non-destructive kinematic anchor.");
                            return;
                        }
                    }
                    catch { }
                }

                s_carSpawnerType = AccessTools.TypeByName("SelfShunt.SSCarSpawner");
                if (s_carSpawnerType == null) return;

                var targetMethod = AccessTools.Method(s_carSpawnerType, "CheckForOptimizableCars");
                if (targetMethod == null) return;

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "CheckForOptimizableCars_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 3 applied: Disabled official Self-Shunt collider/kinematic disabling loop.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 3 hook failed: {0}", ex));
            }
        }

        private static bool CheckForOptimizableCars_Prefix()
        {
            // Bypasses the buggy optimizer of legacy SelfShunt 1.x that disabled colliders on distant cars
            return false;
        }

        #endregion

        #region Shield 4 - Safe Car Spawner (Prevents Deadlock & Overspill)

        private static void ApplySafeCarSpawner(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var ssMod = UnityModManager.FindMod("SelfShunt") ?? UnityModManager.FindMod("YardMaster");
                if (ssMod != null && ssMod.Info != null && !string.IsNullOrEmpty(ssMod.Info.Version))
                {
                    try
                    {
                        var ver = new Version(ssMod.Info.Version);
                        if (ver.Major >= 2)
                        {
                            if (modEntry != null && modEntry.Logger != null)
                                modEntry.Logger.Log("[SelfShuntPatches] Shield 4 skipped: Self-Shunt v2.0.0+ detected with native dynamic car spawner and target settings.");
                            return;
                        }
                    }
                    catch { }
                }

                if (s_carSpawnerType == null) s_carSpawnerType = AccessTools.TypeByName("SelfShunt.SSCarSpawner");
                if (s_carSpawnerType == null) return;

                s_spawingField = AccessTools.Field(s_carSpawnerType, "Spawing");

                var targetMethod = AccessTools.Method(s_carSpawnerType, "PopulateMapWithCars");
                if (targetMethod == null) return;

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "PopulateMapWithCars_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 4 applied: Safe car spawner with deadlock recovery and track capacity safety active.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 4 hook failed: {0}", ex));
            }
        }

        private static bool PopulateMapWithCars_Prefix()
        {
            try
            {
                bool isSpawning = false;
                if (s_spawingField != null)
                {
                    isSpawning = (bool)s_spawingField.GetValue(null);
                }

                if (isSpawning) return false;
                if (CarSpawner.Instance == null) return false;

                int carSpawnGoal = 600;
                if (s_carSpawnerType != null)
                {
                    var prop = AccessTools.Property(s_carSpawnerType, "CAR_SPAWN_GOAL");
                    if (prop != null)
                    {
                        try { carSpawnGoal = (int)prop.GetValue(null); } catch { }
                    }
                }
                int remainingGoal = carSpawnGoal - CarSpawner.Instance.AllCars.Count;
                if (remainingGoal <= 0) return false;

                var spawnOnRailTracks = new Dictionary<RailTrack, List<TrainCarType_v2>>();
                if (StationController.allStations == null) return false;

                for (int i = 0; i < StationController.allStations.Count; i++)
                {
                    var station = StationController.allStations[i];
                    if (station == null) continue;

                    var carTypes = GetCarTypesForStationSafe(station);
                    if (carTypes == null || carTypes.Count == 0) continue;

                    var loadTracks = new HashSet<RailTrack>();
                    if (station.warehouseMachineControllers != null)
                    {
                        for (int w = 0; w < station.warehouseMachineControllers.Count; w++)
                        {
                            var wmc = station.warehouseMachineControllers[w];
                            if (wmc != null && wmc.warehouseTrack != null)
                            {
                                loadTracks.Add(wmc.warehouseTrack);
                            }
                        }
                    }

                    if (station.AllStationTracks != null)
                    {
                        for (int t = 0; t < station.AllStationTracks.Count; t++)
                        {
                            var track = station.AllStationTracks[t];
                            if (track != null && !loadTracks.Contains(track))
                            {
                                spawnOnRailTracks[track] = carTypes;
                            }
                        }
                    }
                }

                if (spawnOnRailTracks.Count > 0)
                {
                    // Run our safe coroutine on CoroutineManager to prevent async lockups
                    DV.Utils.SingletonBehaviour<CoroutineManager>.Instance.Run(SafeSpawnCarsOnTrackCoro(spawnOnRailTracks, remainingGoal, carSpawnGoal));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SelfShuntPatches] Safe PopulateMapWithCars error: {0}", ex));
            }
            return false; // Skip the official buggy spawner!
        }

        private static List<TrainCarType_v2> GetCarTypesForStationSafe(StationController stationController)
        {
            var carTypes = new List<TrainCarType_v2>();
            if (stationController == null || stationController.warehouseMachineControllers == null) return carTypes;

            for (int i = 0; i < stationController.warehouseMachineControllers.Count; i++)
            {
                var warehouse = stationController.warehouseMachineControllers[i];
                if (warehouse == null || warehouse.supportedCargoTypes == null) continue;

                for (int c = 0; c < warehouse.supportedCargoTypes.Count; c++)
                {
                    var cargo = warehouse.supportedCargoTypes[c];
                    var cargoV2 = cargo.ToV2();
                    if (cargoV2 == null) continue;

                    List<TrainCarType_v2> loadableCars;
                    // Safe TryGetValue avoids KeyNotFoundException present in official Self-Shunt
                    if (Globals.G.Types.CargoToLoadableCarTypes.TryGetValue(cargoV2, out loadableCars))
                    {
                        if (loadableCars != null)
                        {
                            carTypes.AddRange(loadableCars);
                        }
                    }
                }
            }

            return carTypes.Distinct().ToList();
        }

        private static IEnumerator SafeSpawnCarsOnTrackCoro(Dictionary<RailTrack, List<TrainCarType_v2>> railTracks, int remainingGoal, int carSpawnGoal)
        {
            SetSpawningFlag(true);

            try
            {
                var shuffledTracks = railTracks.Keys.OrderBy(delegate(RailTrack k) { return s_random.Next(); }).ToList();

                for (int i = 0; i < shuffledTracks.Count; i++)
                {
                    var track = shuffledTracks[i];
                    if (CarSpawner.Instance == null || CarSpawner.Instance.AllCars.Count >= carSpawnGoal || remainingGoal <= 0)
                        break;

                    yield return new WaitForSeconds(0.05f);

                    if (track == null) continue;
                    var logicTrack = ModCompatManager.GetLogicTrack(track);
                    if (logicTrack == null || !logicTrack.IsFree()) continue;

                    List<TrainCarType_v2> carTypes;
                    if (!railTracks.TryGetValue(track, out carTypes) || carTypes == null || carTypes.Count == 0)
                        continue;

                    var randomCarType = carTypes[s_random.Next(0, carTypes.Count)];
                    if (randomCarType == null || randomCarType.liveries == null || randomCarType.liveries.Count == 0)
                        continue;

                    var liveriesToSpawn = new List<TrainCarLivery>();
                    while (liveriesToSpawn.Count < remainingGoal && liveriesToSpawn.Count < 15)
                    {
                        var randomLivery = randomCarType.liveries[s_random.Next(0, randomCarType.liveries.Count)];
                        if (randomLivery == null || randomLivery.prefab == null) break;

                        var prefabTc = randomLivery.prefab.GetComponent<TrainCar>();
                        if (prefabTc == null) break;

                        float currentLength = CarSpawner.Instance.GetTotalCarLiveriesLength(liveriesToSpawn);
                        float nextCarLength = prefabTc.InterCouplerDistance;

                        // 15m safety margin prevents bumper/turnout fouling
                        if (currentLength + nextCarLength > logicTrack.length - 15f)
                        {
                            break;
                        }

                        liveriesToSpawn.Add(randomLivery);

                        int maxCutsRemaining = Math.Max(1, 15 - liveriesToSpawn.Count);
                        if (liveriesToSpawn.Count >= 3 && s_random.Next(0, maxCutsRemaining) == 0) break;
                    }

                    if (liveriesToSpawn.Count > 0)
                    {
                        CarSpawner.Instance.SpawnCarTypesOnTrackRandomOrientation(liveriesToSpawn, track, false, true);
                        remainingGoal -= liveriesToSpawn.Count;
                    }
                }
            }
            finally
            {
                // Guaranteed cleanup permanently prevents the spawner deadlock bug
                SetSpawningFlag(false);
            }
        }

        private static void SetSpawningFlag(bool value)
        {
            try
            {
                if (s_spawingField != null)
                {
                    s_spawingField.SetValue(null, value);
                }
            }
            catch { }
        }

        #endregion

        #region Shield 5 - CargoExpander Deduplication Fix

        private static void ApplyCargoExpanderFix(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var cargoExpanderType = AccessTools.TypeByName("SelfShunt.CargoExpander");
                if (cargoExpanderType == null) return;

                var targetMethod = AccessTools.Method(cargoExpanderType, "PreGameLoad");
                if (targetMethod == null) return;

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "PreGameLoad_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 5 applied: Prevented CargoExpander duplicate registrations and null crashes.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 5 hook failed: {0}", ex));
            }
        }

        private static bool PreGameLoad_Prefix()
        {
            if (s_cargoExpanderInitialized) return false;
            s_cargoExpanderInitialized = true;

            try
            {
                TrainCarLivery carFlat = null;
                TrainCarLivery carFlatShort = null;
                TrainCarLivery carFlatMilitary = null;

                if (Globals.G.Types.Liveries != null)
                {
                    for (int i = 0; i < Globals.G.Types.Liveries.Count; i++)
                    {
                        var livery = Globals.G.Types.Liveries[i];
                        if (livery == null || livery.prefab == null) continue;
                        if (livery.prefab.name == "CarFlatcarShort") carFlatShort = livery;
                        else if (livery.prefab.name == "CarFlatcar") carFlat = livery;
                        else if (livery.prefab.name == "CarFlatcarMilitary") carFlatMilitary = livery;
                    }
                }

                if (carFlat == null || carFlatMilitary == null || carFlatShort == null) return false;

                if (Globals.G.Types.cargos != null)
                {
                    for (int i = 0; i < Globals.G.Types.cargos.Count; i++)
                    {
                        var cargo = Globals.G.Types.cargos[i];
                        if (cargo == null) continue;

                        if (cargo.IsLoadableOnCarType(carFlat.parentType))
                        {
                            if (!cargo.IsLoadableOnCarType(carFlatMilitary.parentType))
                            {
                                AddToCargoListSafe(cargo, carFlat.parentType, carFlatMilitary);
                            }
                        }
                        else if (cargo.IsLoadableOnCarType(carFlatShort.parentType))
                        {
                            if (!cargo.IsLoadableOnCarType(carFlatMilitary.parentType))
                            {
                                AddToCargoListSafe(cargo, carFlatShort.parentType, carFlatMilitary);
                            }
                            if (!cargo.IsLoadableOnCarType(carFlat.parentType))
                            {
                                AddToCargoListSafe(cargo, carFlatShort.parentType, carFlat);
                            }
                        }
                    }
                }

                AccessTools.Field(typeof(DVObjectModel), "_carTypeToLoadableCargo").SetValue(Globals.G.Types, null);
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SelfShuntPatches] CargoExpander safe execution error: {0}", ex));
            }

            return false; // Skip the official un-guarded PreGameLoad!
        }

        private static void AddToCargoListSafe(CargoType_v2 cargo, TrainCarType_v2 reference, TrainCarLivery car)
        {
            if (cargo == null || car == null || car.parentType == null) return;

            if (cargo.loadableCarTypes != null)
            {
                for (int i = 0; i < cargo.loadableCarTypes.Length; i++)
                {
                    var info = cargo.loadableCarTypes[i];
                    if (info != null && info.carType == car.parentType) return;
                }
            }

            GameObject[] prefabs = cargo.GetCargoPrefabsForCarType(reference);
            if (prefabs == null || prefabs.Length == 0) return;

            var newInfo = new CargoType_v2.LoadableInfo(car.parentType, prefabs);
            var oldInfoArray = cargo.loadableCarTypes ?? new CargoType_v2.LoadableInfo[0];
            var newInfoArray = oldInfoArray.Concat(new CargoType_v2.LoadableInfo[] { newInfo }).ToArray();
            cargo.loadableCarTypes = newInfoArray;

            AccessTools.Field(typeof(CargoType_v2), "_trainCargoToCargoPrefabs").SetValue(cargo, null);
        }

        #endregion

        #region Shield 6 - ComplexTransport ExpireJob Guard

        private static void ApplyJobValidatorExpireGuard(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                var targetMethod = AccessTools.Method(typeof(Job), "ExpireJob");
                if (targetMethod == null) return;

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "ExpireJob_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 6 applied: Guarded ComplexTransport jobs against invalid JobValidator expiration.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 6 hook failed: {0}", ex));
            }
        }

        private static bool ExpireJob_Prefix(Job __instance)
        {
            try
            {
                if (__instance != null && __instance.jobType == JobType.ComplexTransport)
                {
                    var trace = new System.Diagnostics.StackTrace(false);
                    int count = trace.FrameCount;
                    if (count > 8) count = 8;

                    for (int i = 1; i < count; i++)
                    {
                        var frame = trace.GetFrame(i);
                        var method = frame != null ? frame.GetMethod() : null;
                        if (method != null && method.DeclaringType != null)
                        {
                            string typeName = method.DeclaringType.FullName ?? "";
                            if (typeName.IndexOf("JobValidator", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return false; // Block invalid expiration of SelfShunt job by JobValidator!
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SelfShuntPatches] ExpireJob prefix error: {0}", ex));
            }
            return true;
        }

        #endregion

        #region Shield 7 - AI Train Kinematic Anchor Immunity

        private static void ApplyAIAnchorImmunity(Harmony harmony, UnityModManager.ModEntry modEntry)
        {
            try
            {
                if (s_carSpawnerType == null) s_carSpawnerType = AccessTools.TypeByName("SelfShunt.SSCarSpawner");
                if (s_carSpawnerType == null) return;

                var targetMethod = AccessTools.Method(s_carSpawnerType, "FreezeTrainset", new Type[] { typeof(Trainset) });
                if (targetMethod == null) return;

                var prefix = AccessTools.Method(typeof(SelfShuntPatches), "FreezeTrainset_Prefix");
                harmony.Patch(targetMethod, prefix: new HarmonyMethod(prefix));
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Log("[SelfShuntPatches] Shield 7 applied: AI trains protected from kinematic anchor locking.");
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Warning(string.Format("[SelfShuntPatches] Shield 7 hook failed: {0}", ex));
            }
        }

        private static bool FreezeTrainset_Prefix(Trainset train)
        {
            if (train == null) return false;
            try
            {
                if (ModCompatManager.IsAITrain(train))
                {
                    // Ensure dynamic physics is restored if any car was previously frozen
                    if (train.cars != null)
                    {
                        for (int i = 0; i < train.cars.Count; i++)
                        {
                            var c = train.cars[i];
                            if (c != null && c.gameObject != null)
                            {
                                var rb = c.gameObject.GetComponent<Rigidbody>();
                                if (rb != null && rb.isKinematic)
                                {
                                    rb.isKinematic = false;
                                }
                            }
                        }
                    }
                    return false; // Intercept & abort: never freeze AI trains
                }

                // Protect starter locomotives with LocoZoneBlocker from bogie popping into terrain void
                if (train.cars != null)
                {
                    for (int i = 0; i < train.cars.Count; i++)
                    {
                        var c = train.cars[i];
                        if (c != null && c.gameObject != null && c.gameObject.GetComponentInChildren<LocoZoneBlocker>() != null)
                        {
                            var rb = c.gameObject.GetComponent<Rigidbody>();
                            if (rb != null && rb.isKinematic)
                            {
                                rb.isKinematic = false;
                            }
                            return false;
                        }
                    }
                }
            }
            catch { }
            return true;
        }

        #endregion
    }
}
