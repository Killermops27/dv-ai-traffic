using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityModManagerNet;
using HarmonyLib;
using DV.Logic.Job;

namespace AITraffic.Compat
{
    /// <summary>
    /// Lightweight runtime marker attached to TrainCars managed by AI Traffic.
    /// Used for fast identification and exclusion by save systems and external mods.
    /// </summary>
    public class AITrafficCarMarker : MonoBehaviour
    {
        public string RouteId;
        public float SpawnTime;

        private void Awake()
        {
            SpawnTime = Time.time;
        }
    }

    /// <summary>
    /// Manages compatibility, presence detection, and inter-mod coordination
    /// for DVSignals, DoubleTrack, PersistentJobs, SelfShunt (YardMaster), and PassengerJobs.
    /// </summary>
    public static class ModCompatManager
    {
        private static readonly object s_aiCarsLock = new object();
        private static readonly HashSet<string> s_aiCarGuids = new HashSet<string>();
        private static readonly HashSet<TrainCar> s_aiCars = new HashSet<TrainCar>();

        // Mod ID Constants
        private const string ModIdDVSignals = "DVSignals";
        private const string ModIdDoubleTrack = "DoubleTrack";
        private const string ModIdPersistentJobsMod = "PersistentJobsMod";
        private const string ModIdPersistentJobs = "PersistentJobs";
        private const string ModIdSelfShunt = "SelfShunt";
        private const string ModIdYardMaster = "YardMaster";
        private const string ModIdPassengerJobs = "PassengerJobs";

        #region Presence Checks

        /// <summary>
        /// True if DVSignals is installed and active.
        /// </summary>
        public static bool IsDVSignalsLoaded
        {
            get
            {
                var mod = UnityModManager.FindMod(ModIdDVSignals);
                return mod != null && mod.Active;
            }
        }

        /// <summary>
        /// True if DoubleTrack is installed and active.
        /// </summary>
        public static bool IsDoubleTrackLoaded
        {
            get
            {
                var mod = UnityModManager.FindMod(ModIdDoubleTrack);
                return mod != null && mod.Active;
            }
        }

        /// <summary>
        /// True if PersistentJobs (or PersistentJobsMod) is installed and active.
        /// </summary>
        public static bool IsPersistentJobsLoaded
        {
            get
            {
                var mod = UnityModManager.FindMod(ModIdPersistentJobsMod) ?? UnityModManager.FindMod(ModIdPersistentJobs);
                return mod != null && mod.Active;
            }
        }

        /// <summary>
        /// True if SelfShunt (Yard Master) is installed and active.
        /// </summary>
        public static bool IsYardMasterLoaded
        {
            get
            {
                var mod = UnityModManager.FindMod(ModIdSelfShunt) ?? UnityModManager.FindMod(ModIdYardMaster);
                return mod != null && mod.Active;
            }
        }

        /// <summary>
        /// Alias for <see cref="IsYardMasterLoaded"/>.
        /// </summary>
        public static bool IsSelfShuntLoaded
        {
            get { return IsYardMasterLoaded; }
        }

        /// <summary>
        /// True if PassengerJobs is installed and active.
        /// </summary>
        public static bool IsPassengerJobsLoaded
        {
            get
            {
                var mod = UnityModManager.FindMod(ModIdPassengerJobs);
                return mod != null && mod.Active;
            }
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes compatibility adapters, logs detected integrations, and sets up save protection hooks.
        /// </summary>
        public static void Initialize()
        {
            try
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log("Initializing AI Traffic Mod Compatibility Adapters...");

                    Main.ModEntry.Logger.Log(string.Format(" • DVSignals: {0}", IsDVSignalsLoaded ? "Active [Signaling & Interlocking Enabled]" : "NOT DETECTED (Required)"));
                    Main.ModEntry.Logger.Log(string.Format(" • DoubleTrack: {0}", IsDoubleTrackLoaded ? "Active [Multi-Track Mainlines Enabled]" : "Not Detected"));
                    Main.ModEntry.Logger.Log(string.Format(" • PersistentJobs: {0}", IsPersistentJobsLoaded ? "Active [Dynamic Car Isolation Enabled]" : "Not Detected"));
                    Main.ModEntry.Logger.Log(string.Format(" • SelfShunt (YardMaster): {0}", IsYardMasterLoaded ? "Active [Active Yard Protection Enabled]" : "Not Detected"));
                    Main.ModEntry.Logger.Log(string.Format(" • PassengerJobs: {0}", IsPassengerJobsLoaded ? "Active [Passenger Platform Routing Enabled]" : "Not Detected"));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error during ModCompatManager initialization: {0}", ex));
            }
        }

        #endregion

        #region Yard & Platform Track Helpers

        /// <summary>
        /// Gets the corresponding DV.Logic.Job.Track for a given RailTrack.
        /// </summary>
        public static DV.Logic.Job.Track GetLogicTrack(RailTrack track)
        {
            if (track == null) return null;
            if (RailTrackRegistry.RailTrackToLogicTrack != null)
            {
                DV.Logic.Job.Track logicTrack;
                if (RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out logicTrack))
                {
                    return logicTrack;
                }
            }
            return null;
        }

        /// <summary>
        /// Determines whether a given <see cref="RailTrack"/> belongs to an active yard, shunting zone,
        /// or warehouse loading track (avoiding interference with player shunting or SelfShunt jobs).
        /// </summary>
        /// <param name="track">The RailTrack to inspect.</param>
        /// <returns>True if the track is in an active yard zone.</returns>
        public static bool IsTrackActiveYardZone(RailTrack track)
        {
            if (track == null) return false;

            try
            {
                // Platforms [P] are always accessible
                if (IsPlatformTrack(track)) return false;

                DV.Logic.Job.Track logicTrack = GetLogicTrack(track);
                if (logicTrack != null && logicTrack.ID != null)
                {
                    string part = logicTrack.ID.TrackPartOnly;
                    string display = logicTrack.ID.FullDisplayID ?? "";

                    // Mainline [#], Inbound [I], Passenger [P], and Passing [S] are NEVER blocked as active yard zones
                    if (part == DV.Logic.Job.TrackID.MAIN_LINE_TYPE || display.Contains("[#]") ||
                        part == DV.Logic.Job.TrackID.REGULAR_IN_TYPE || display.Contains("[I]") ||
                        part == DV.Logic.Job.TrackID.STORAGE_PASSENGER_TYPE || display.Contains("[P]") ||
                        part == DV.Logic.Job.TrackID.STORAGE_TYPE || display.Contains("[S]"))
                    {
                        return false;
                    }

                    // Warehouse loading tracks [L] and internal classification tracks [Y] are active yard zones
                    if (part == DV.Logic.Job.TrackID.LOADING_TYPE || display.StartsWith("[Y]") || display.StartsWith("[L]"))
                    {
                        return true;
                    }
                }

                string trackName = track.name ?? string.Empty;
                if (trackName.Contains("[#]") || trackName.Contains("[I]") || trackName.Contains("[P]") || trackName.Contains("[S]"))
                {
                    return false;
                }

                if (trackName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                    trackName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error in IsTrackActiveYardZone for track '{0}': {1}", track.name, ex.Message));
            }

            return false;
        }

        /// <summary>
        /// Determines whether a given <see cref="RailTrack"/> is a passenger station platform track.
        /// Integrates with PassengerJobs station configs if loaded, and falls back to valley platform naming heuristics.
        /// </summary>
        /// <param name="track">The RailTrack to inspect.</param>
        /// <returns>True if the track is a passenger platform.</returns>
        public static bool IsPlatformTrack(RailTrack track)
        {
            if (track == null) return false;

            try
            {
                // 1. Query PassengerJobs RouteManager if available
                if (IsPassengerJobsLoaded)
                {
                    if (CheckPassengerJobsPlatformInternal(track))
                    {
                        return true;
                    }
                }

                // 2. Generic naming heuristics for platforms / passenger stops
                string trackName = track.name ?? string.Empty;
                if (trackName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trackName.IndexOf("[P]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trackName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    trackName.IndexOf("Passenger", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                // 3. Check logic track ID
                if (RailTrackRegistry.RailTrackToLogicTrack != null)
                {
                    DV.Logic.Job.Track logicTrack;
                    if (RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out logicTrack) && logicTrack != null && logicTrack.ID != null)
                    {
                        string fullId = logicTrack.ID.FullID ?? string.Empty;
                        if (fullId.IndexOf("LP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            fullId.IndexOf("SP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            fullId.IndexOf("-P", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error in IsPlatformTrack for track '{0}': {1}", track.name, ex.Message));
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool CheckPassengerJobsPlatformInternal(RailTrack track)
        {
            try
            {
                if (track == null) return false;

                if (RailTrackRegistry.RailTrackToLogicTrack == null) return false;

                DV.Logic.Job.Track logicTrack;
                if (!RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out logicTrack) || logicTrack == null || logicTrack.ID == null)
                    return false;

                string fullId = logicTrack.ID.FullID;
                if (string.IsNullOrEmpty(fullId)) return false;

                var routeTrack = PassengerJobs.Generation.RouteManager.GetRouteTrackById(fullId);
                return routeTrack.HasValue;
            }
            catch
            {
                // PassengerJobs internal data not ready or different version; fall back to heuristic
            }

            return false;
        }

        #endregion

        #region AI Train Tagging & Save Isolation

        /// <summary>
        /// Tags an entire <see cref="Trainset"/> and its constituent <see cref="TrainCar"/>s as dynamic AI traffic.
        /// Sets playerSpawnedCar=true so PersistentJobs ignores them during procedural job generation,
        /// attaches <see cref="AITrafficCarMarker"/>, and registers them to be skipped by save serializers.
        /// </summary>
        /// <param name="trainset">The trainset to tag.</param>
        public static void TagTrainAsAITraffic(Trainset trainset)
        {
            if (trainset == null || trainset.cars == null) return;

            try
            {
                lock (s_aiCarsLock)
                {
                    for (int i = 0; i < trainset.cars.Count; i++)
                    {
                        var car = trainset.cars[i];
                        if (car == null) continue;

                        // 1. Tag as playerSpawned so PersistentJobs treats it as a non-job car
                        car.playerSpawnedCar = true;

                        // 2. Prevent debt display / damage copay charging the player for AI movements
                        car.preventDebtDisplay = true;

                        // 3. Attach runtime marker component
                        if (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() == null)
                        {
                            car.gameObject.AddComponent<AITrafficCarMarker>();
                        }

                        // 4. Register GUID and instance for fast lookup
                        if (!string.IsNullOrEmpty(car.CarGUID))
                        {
                            s_aiCarGuids.Add(car.CarGUID);
                        }
                        s_aiCars.Add(car);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error tagging trainset '{0}' as AI traffic: {1}", trainset != null ? trainset.id.ToString() : "null", ex));
            }
        }

        /// <summary>
        /// Untags a <see cref="Trainset"/> when it is despawned or returned to normal pool.
        /// </summary>
        /// <param name="trainset">The trainset to untag.</param>
        public static void UntagTrain(Trainset trainset)
        {
            if (trainset == null || trainset.cars == null) return;

            try
            {
                lock (s_aiCarsLock)
                {
                    for (int i = 0; i < trainset.cars.Count; i++)
                    {
                        var car = trainset.cars[i];
                        if (car == null) continue;

                        if (car.gameObject != null)
                        {
                            var marker = car.gameObject.GetComponent<AITrafficCarMarker>();
                            if (marker != null)
                            {
                                UnityEngine.Object.Destroy(marker);
                            }
                        }

                        if (!string.IsNullOrEmpty(car.CarGUID))
                        {
                            s_aiCarGuids.Remove(car.CarGUID);
                        }
                        s_aiCars.Remove(car);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error untagging trainset '{0}': {1}", trainset != null ? trainset.id.ToString() : "null", ex));
            }
        }

        /// <summary>
        /// Checks if a <see cref="TrainCar"/> belongs to an AI Traffic consist.
        /// </summary>
        public static bool IsAITrain(TrainCar car)
        {
            if (car == null) return false;
            if (car.GetComponent<AITraffic.Driver.AIEngineer>() != null) return true;
            if (car.gameObject != null && car.gameObject.GetComponent<AITrafficCarMarker>() != null) return true;

            // Check if any car in the same trainset has an AIEngineer or AI marker
            if (car.trainset != null && car.trainset.cars != null)
            {
                for (int i = 0; i < car.trainset.cars.Count; i++)
                {
                    var c = car.trainset.cars[i];
                    if (c != null && (c.GetComponent<AITraffic.Driver.AIEngineer>() != null || (c.gameObject != null && c.gameObject.GetComponent<AITrafficCarMarker>() != null)))
                    {
                        return true;
                    }
                }
            }

            lock (s_aiCarsLock)
            {
                return s_aiCars.Contains(car) || (!string.IsNullOrEmpty(car.CarGUID) && s_aiCarGuids.Contains(car.CarGUID));
            }
        }

        /// <summary>
        /// Checks if a <see cref="Trainset"/> belongs to an AI Traffic consist.
        /// </summary>
        public static bool IsAITrain(Trainset trainset)
        {
            if (trainset == null || trainset.cars == null || trainset.cars.Count == 0) return false;

            for (int i = 0; i < trainset.cars.Count; i++)
            {
                if (IsAITrain(trainset.cars[i])) return true;
            }

            return false;
        }

        #endregion
    }

    #region Save Game Harmony Patches

    /// <summary>
    /// Ensures that dynamic transient AI cars are omitted from game save files,
    /// preventing save corruption or ghost trains on reload.
    /// </summary>
    [HarmonyPatch(typeof(CarsSaveManager), "GetCarSaveData")]
    internal static class CarsSaveManager_GetCarSaveData_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(TrainCar car, ref Newtonsoft.Json.Linq.JObject __result)
        {
            try
            {
                if (car != null && ModCompatManager.IsAITrain(car))
                {
                    // Return null to skip saving this AI car
                    __result = null;
                    return false;
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in CarsSaveManager.GetCarSaveData prefix patch: {0}", ex));
            }

            return true;
        }
    }

    /// <summary>
    /// Sanitizes the 'carsData' JArray in save data by removing any null elements produced
    /// by skipped AI cars, preventing NullReferenceExceptions and save recovery resets during load.
    /// </summary>
    [HarmonyPatch(typeof(CarsSaveManager), "GetCarsSaveData")]
    internal static class CarsSaveManager_GetCarsSaveData_Patch
    {
        [HarmonyPostfix]
        private static void Postfix(ref Newtonsoft.Json.Linq.JObject __result)
        {
            try
            {
                if (__result == null) return;
                var carsArray = __result["carsData"] as Newtonsoft.Json.Linq.JArray;
                if (carsArray != null)
                {
                    for (int i = carsArray.Count - 1; i >= 0; i--)
                    {
                        if (carsArray[i] == null || carsArray[i].Type == Newtonsoft.Json.Linq.JTokenType.Null)
                        {
                            carsArray.RemoveAt(i);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error cleaning AI cars from save data in GetCarsSaveData: {0}", ex));
            }
        }
    }

    /// <summary>
    /// Guard against null carData elements when loading legacy savegames.
    /// </summary>
    [HarmonyPatch(typeof(CarsSaveManager), "InstantiateCarFromSavegame")]
    internal static class CarsSaveManager_InstantiateCarFromSavegame_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(Newtonsoft.Json.Linq.JObject carData, ref TrainCar __result)
        {
            if (carData == null)
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Guard against null carData elements when restoring car coupler connections.
    /// </summary>
    [HarmonyPatch(typeof(CarsSaveManager), "RestoreCarConnections")]
    internal static class CarsSaveManager_RestoreCarConnections_Patch
    {
        [HarmonyPrefix]
        private static bool Prefix(Newtonsoft.Json.Linq.JObject carData)
        {
            return carData != null;
        }
    }

    /// <summary>
    /// Despawns all AI trains when returning to the main menu.
    /// </summary>
    [HarmonyPatch(typeof(DV.UI.MainMenu), "GoBackToMainMenu")]
    internal static class MainMenu_GoBackToMainMenu_Patch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                if (AITraffic.Core.TrafficManager.IsRunning && AITraffic.Core.TrafficManager.Instance != null)
                {
                    AITraffic.Core.TrafficManager.Instance.DespawnAllAITrains();
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in MainMenu.GoBackToMainMenu prefix patch: {0}", ex));
            }
        }
    }

    /// <summary>
    /// Despawns all AI trains when quit is requested from the main menu.
    /// </summary>
    [HarmonyPatch(typeof(DV.UI.MainMenu), "OnQuitRequested")]
    internal static class MainMenu_OnQuitRequested_Patch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                if (AITraffic.Core.TrafficManager.IsRunning && AITraffic.Core.TrafficManager.Instance != null)
                {
                    AITraffic.Core.TrafficManager.Instance.DespawnAllAITrains();
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in MainMenu.OnQuitRequested prefix patch: {0}", ex));
            }
        }
    }

    /// <summary>
    /// Despawns all AI trains when the application begins quitting to ensure zero orphaned cars on disk.
    /// </summary>
    [HarmonyPatch(typeof(SaveGameManager), "OnApplicationQuitting")]
    internal static class SaveGameManager_OnApplicationQuitting_Patch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            try
            {
                if (AITraffic.Core.TrafficManager.IsRunning && AITraffic.Core.TrafficManager.Instance != null)
                {
                    AITraffic.Core.TrafficManager.Instance.DespawnAllAITrains();
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in SaveGameManager.OnApplicationQuitting prefix patch: {0}", ex));
            }
        }
    }

    #endregion
}
