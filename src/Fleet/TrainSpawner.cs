using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV.MultipleUnit;
using DV.Simulation.Cars;
using DV.ThingTypes;
using AITraffic.Compat;
using AITraffic.Driver;

namespace AITraffic.Fleet
{
    /// <summary>
    /// Handles procedural instantiation, coupling, mechanical configuration,
    /// engine startup, and AI engineer attachment for AI train consists.
    /// </summary>
    public static class TrainSpawner
    {
        /// <summary>
        /// True while an ambient AI consist is being instantiated and configured by CarSpawner.
        /// Checked by debt, penalty, and orphan-cleanup routines to suppress operations during spawning.
        /// </summary>
        public static bool IsSpawningAmbientConsist { get; private set; }

        private static readonly HashSet<TrainCar> s_spawningCars = new HashSet<TrainCar>();
        private static readonly HashSet<string> s_spawningCarIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object s_spawningCarsLock = new object();

        /// <summary>
        /// Checks whether a specific car is currently being spawned, configured, or coupled into an AI consist.
        /// </summary>
        public static bool IsCarSpawning(TrainCar car)
        {
            if (car == null) return false;
            lock (s_spawningCarsLock)
            {
                return s_spawningCars.Contains(car);
            }
        }

        public static bool IsCarSpawning(string carId)
        {
            if (string.IsNullOrEmpty(carId)) return false;
            lock (s_spawningCarsLock)
            {
                return s_spawningCarIds.Contains(carId);
            }
        }

        public static void RegisterSpawningCar(TrainCar car)
        {
            if (car == null) return;
            lock (s_spawningCarsLock)
            {
                s_spawningCars.Add(car);
                string cId = ModCompatManager.GetCarId(car);
                if (!string.IsNullOrEmpty(cId))
                {
                    s_spawningCarIds.Add(cId);
                }
            }
        }

        public static void UnregisterSpawningCars(IEnumerable<TrainCar> cars)
        {
            if (cars == null) return;
            lock (s_spawningCarsLock)
            {
                foreach (var c in cars)
                {
                    if (c != null)
                    {
                        s_spawningCars.Remove(c);
                        string cId = ModCompatManager.GetCarId(c);
                        if (!string.IsNullOrEmpty(cId))
                        {
                            s_spawningCarIds.Remove(cId);
                        }
                    }
                }
            }
        }

        public static void ClearSpawningRegistries()
        {
            lock (s_spawningCarsLock)
            {
                s_spawningCars.Clear();
                s_spawningCarIds.Clear();
            }
        }

        public static void ResetSpawningState()
        {
            IsSpawningAmbientConsist = false;
            ClearSpawningRegistries();
        }

        private static Type s_couplerBreakerType;
        private static Type GetCouplerBreakerType()
        {
            if (s_couplerBreakerType != null) return s_couplerBreakerType;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                s_couplerBreakerType = asm.GetType("DvMod.ZCouplers.Physics.CouplerBreaker");
                if (s_couplerBreakerType != null) break;
            }
            return s_couplerBreakerType;
        }

        /// <summary>
        /// Estimates the physical length of a consist (in meters) including couplers for track suitability validation.
        /// </summary>
        public static float CalculateConsistLength(ConsistType consistType, string originYard = null, string destYard = null, System.Random rng = null)
        {
            if (rng == null) rng = new System.Random();
            List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(consistType, originYard, destYard, rng);
            if (specs == null || specs.Count == 0) return 100f;

            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null) liveries.Add(specs[i].Livery);
            }

            if (CarSpawner.Instance != null && liveries.Count > 0)
            {
                try
                {
                    float len = CarSpawner.Instance.GetTotalCarLiveriesLength(liveries);
                    if (len > 0f) return len;
                }
                catch { }
            }

            // Fallback estimation: average 18m per car
            return specs.Count * 18f;
        }

        /// <summary>
        /// Instantly verifies mathematically (&lt;0.05ms) if a consist of the given liveries can physically fit
        /// on the target track without colliding with track buffer limits.
        /// </summary>
        /// <summary>
        /// Checks if the player has acquired the Military 1 job license.
        /// </summary>
        public static bool IsMilitaryLicenseAcquired()
        {
            try
            {
                if (LicenseManager.Instance == null) return false;
                var acquired = LicenseManager.Instance.GetAcquiredJobLicenses();
                if (acquired != null)
                {
                    foreach (var lic in acquired)
                    {
                        if (lic != null && (lic.v1 == DV.ThingTypes.JobLicenses.Military1 || string.Equals(lic.id, "Military1", StringComparison.OrdinalIgnoreCase)))
                        {
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning("[TrainSpawner] Error checking military license: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Identifies whether a rail track belongs to a military installation:
        /// 1. Military Base (MB)
        /// 2. Harbor Military Base (HMB) / Harbor Town (HB) military spur
        /// 3. Machine Factory Military Base (MFMB) / Machine Factory (MF) gated military section
        /// </summary>
        // CRITICAL INVARIANT [FLT-11]: Gate all three military bases (MB, HMB, MFMB) and generic "Road ... MA/MB" tracks behind Military1 license (See GRAVEYARD.md)
        public static bool IsMilitaryTrack(RailTrack track)
        {
            if (track == null) return false;

            var logicTrack = ModCompatManager.GetLogicTrack(track);
            if (logicTrack != null && logicTrack.ID != null)
            {
                string yard = logicTrack.ID.yardId ?? string.Empty;
                string subYard = logicTrack.ID.SignIDSubYardPart ?? string.Empty;
                if (string.Equals(yard, "MB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(yard, "MA", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(yard, "HMB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(yard, "HBMB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(yard, "MFMB", StringComparison.OrdinalIgnoreCase) ||
                    yard.EndsWith("MB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(subYard, "MB", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(subYard, "MA", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string trackPart = logicTrack.ID.TrackPartOnly ?? string.Empty;
                string display = logicTrack.ID.FullDisplayID ?? string.Empty;

                if (string.Equals(yard, "HB", StringComparison.OrdinalIgnoreCase) &&
                    (trackPart.StartsWith("D", StringComparison.OrdinalIgnoreCase) || display.IndexOf("HB-D", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                if (string.Equals(yard, "MF", StringComparison.OrdinalIgnoreCase) &&
                    (trackPart.StartsWith("C", StringComparison.OrdinalIgnoreCase) || display.IndexOf("MF-C", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            string name = (track.name ?? string.Empty).Trim();
            if (name.IndexOf("Military", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.IndexOf("[#MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("[MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("_[MB]_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("[MA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("_[MA]_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MB-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("-MB-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("_MB_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("-MA-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("_MA_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.EndsWith(" MB", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-MB", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_MB", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(" MA", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("-MA", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_MA", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOf(" MB ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(" MB]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(" MA ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf(" MA]", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (name.IndexOf("HMB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HBMB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HB-MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HB_MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HB-D", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("HB_D", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
            if (name.IndexOf("MFMB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MF-MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MF_MB", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MF-C", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("MF_C", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Filters a collection of candidate tracks, excluding military tracks if the player lacks Military 1 license.
        /// </summary>
        public static List<RailTrack> FilterCandidateSpawnTracks(IEnumerable<RailTrack> tracks)
        {
            var result = new List<RailTrack>();
            if (tracks == null) return result;
            bool militaryAllowed = IsMilitaryLicenseAcquired();
            foreach (var t in tracks)
            {
                if (t == null) continue;
                if (!militaryAllowed && IsMilitaryTrack(t)) continue;
                result.Add(t);
            }
            return result;
        }

        public static bool CanConsistFitOnTrack(
            List<TrainCarLivery> liveries,
            RailTrack track,
            double startSpan = 15.0,
            bool flipTrainConsist = false)
        {
            if (track == null || track.curve == null || liveries == null || liveries.Count == 0)
                return false;

            // Military track gating: exclude military tracks if player lacks Military 1 license
            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
                return false;

            float totalLength = CarSpawner.Instance != null
                ? CarSpawner.Instance.GetTotalCarLiveriesLength(liveries)
                : 0f;

            if (track.curve.length < totalLength + 20f)
                return false;

            double checkSpan = startSpan > 0.0 ? startSpan : 15.0;
            if (startSpan <= 0.0 && track.curve.length >= totalLength + 30f)
            {
                checkSpan = Math.Max(15.0, (track.curve.length - totalLength) * 0.5);
            }

            var orientationList = new List<bool>(liveries.Count);
            for (int i = 0; i < liveries.Count; i++)
            {
                orientationList.Add(flipTrainConsist);
            }

            try
            {
                var spawnData = CarSpawner.GetTrackMiddleBasedSpawnData(
                    liveries,
                    orientationList,
                    track,
                    checkSpan,
                    flipTrainConsist);

                return (spawnData.result == CarSpawner.SpawnDataResult.OK &&
                        spawnData.carData != null &&
                        spawnData.carData.Length > 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Spawns an AI train consist of the specified type on the given track, matching origin and destination industrial chains.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="consistType">The type of consist to spawn.</param>
        /// <param name="originYard">Optional origin station yard ID.</param>
        /// <param name="destYard">Optional destination station yard ID.</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <param name="rng">Optional random number generator.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(
            RailTrack track,
            ConsistType consistType,
            string originYard = null,
            string destYard = null,
            double startSpan = 15.0,
            bool flipTrainConsist = false,
            System.Random rng = null)
        {
            if (track == null || track.curve == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Target track or track curve lookup is null.");
                return null;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner: Cannot spawn on military track '{0}' without Military 1 license.", track.name));
                return null;
            }

            List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(consistType, originYard, destYard, rng);
            if (specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner.SpawnAITrain failed: No specs generated for consist type {0}.", consistType));
                return null;
            }

            return SpawnAITrain(track, specs, startSpan, flipTrainConsist);
        }

        /// <summary>
        /// Asynchronously spawns an AI train consist, time-slicing car instantiations 1 car per frame
        /// to eliminate main-thread lag spikes.
        /// </summary>
        public static System.Collections.IEnumerator SpawnAITrainCoroutine(
            RailTrack track,
            ConsistType consistType,
            string originYard = null,
            string destYard = null,
            double startSpan = 15.0,
            bool flipTrainConsist = false,
            System.Random rng = null,
            Action<AIEngineer> onComplete = null)
        {
            if (track == null || track.curve == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrainCoroutine failed: Target track or track curve lookup is null.");
                if (onComplete != null) onComplete(null);
                yield break;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner.SpawnAITrainCoroutine: Target track '{0}' is in a military installation and player lacks Military 1 license. Aborting spawn.", track.name));
                if (onComplete != null) onComplete(null);
                yield break;
            }

            if (rng == null) rng = new System.Random();
            List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(consistType, originYard, destYard, rng);
            if (specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner.SpawnAITrainCoroutine failed: No specs generated for consist type {0}.", consistType));
                if (onComplete != null) onComplete(null);
                yield break;
            }

            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null)
                {
                    liveries.Add(specs[i].Livery);
                }
            }

            if (Core.TrafficManager.Instance != null)
            {
                yield return Core.TrafficManager.Instance.StartCoroutine(
                    SpawnAITrainInternalCoroutine(track, liveries, specs, startSpan, flipTrainConsist, onComplete, consistType));
            }
            else
            {
                var eng = SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
                if (onComplete != null) onComplete(eng);
            }
        }

        /// <summary>
        /// Asynchronously spawns an AI train consist with custom car specs, time-slicing across frames.
        /// </summary>
        public static System.Collections.IEnumerator SpawnAITrainCoroutine(
            RailTrack track,
            List<ConsistCarSpec> specs,
            double startSpan = 15.0,
            bool flipTrainConsist = false,
            Action<AIEngineer> onComplete = null,
            ConsistType consistType = ConsistType.RegionalFreight,
            bool startInShuntingMode = false)
        {
            if (track == null || track.curve == null || specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrainCoroutine failed: Target track or track curve lookup is null, or invalid arguments.");
                if (onComplete != null) onComplete(null);
                yield break;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner.SpawnAITrainCoroutine: Target track '{0}' is in a military installation and player lacks Military 1 license. Aborting spawn.", track.name));
                if (onComplete != null) onComplete(null);
                yield break;
            }

            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null)
                {
                    liveries.Add(specs[i].Livery);
                }
            }

            if (Core.TrafficManager.Instance != null)
            {
                yield return Core.TrafficManager.Instance.StartCoroutine(
                    SpawnAITrainInternalCoroutine(track, liveries, specs, startSpan, flipTrainConsist, onComplete, consistType, startInShuntingMode));
            }
            else
            {
                var eng = SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
                if (eng != null)
                {
                    eng.ConsistType = consistType;
                    if (startInShuntingMode) eng.StartInShuntingMode();
                }
                if (onComplete != null) onComplete(eng);
            }
        }

        /// <summary>
        /// Spawns an AI train consist of the specified type on the given track (overload for default origin/destination).
        /// </summary>
        public static AIEngineer SpawnAITrain(RailTrack track, ConsistType consistType, double startSpan = 15.0, bool flipTrainConsist = false, System.Random rng = null)
        {
            return SpawnAITrain(track, consistType, null, null, startSpan, flipTrainConsist, rng);
        }

        /// <summary>
        /// Spawns an AI train consist with the specified car specifications and loads matching cargo on rolling stock.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="specs">The ordered list of car specifications (starting with lead locomotive).</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(RailTrack track, List<ConsistCarSpec> specs, double startSpan = 15.0, bool flipTrainConsist = false)
        {
            if (track == null || track.curve == null || specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Target track or track curve lookup is null, or invalid arguments.");
                return null;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner: Cannot spawn on military track '{0}' without Military 1 license.", track.name));
                return null;
            }

            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null)
                {
                    liveries.Add(specs[i].Livery);
                }
            }

            return SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
        }

        /// <summary>
        /// Spawns an AI train consist with the specified liveries on the given track.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="liveries">The ordered list of car liveries (starting with lead locomotive).</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(RailTrack track, List<TrainCarLivery> liveries, double startSpan = 15.0, bool flipTrainConsist = false)
        {
            if (track == null || track.curve == null || liveries == null || liveries.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Target track or track curve lookup is null, or invalid arguments.");
                return null;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner: Cannot spawn on military track '{0}' without Military 1 license.", track.name));
                return null;
            }

            List<ConsistCarSpec> specs = new List<ConsistCarSpec>(liveries.Count);
            for (int i = 0; i < liveries.Count; i++)
            {
                specs.Add(new ConsistCarSpec(liveries[i], CargoType.None));
            }

            return SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
        }

        private static System.Collections.IEnumerator SpawnAITrainInternalCoroutine(
            RailTrack track,
            List<TrainCarLivery> liveries,
            List<ConsistCarSpec> specs,
            double startSpan,
            bool flipTrainConsist,
            Action<AIEngineer> onComplete,
            ConsistType consistType = ConsistType.RegionalFreight,
            bool startInShuntingMode = false)
        {
            if (track == null || track.curve == null || liveries == null || liveries.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrainInternalCoroutine failed: Target track or track curve lookup is null, or invalid arguments.");
                if (onComplete != null) onComplete(null);
                yield break;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner.SpawnAITrainInternalCoroutine: Target track '{0}' is in a military installation and player lacks Military 1 license. Aborting spawn.", track.name));
                if (onComplete != null) onComplete(null);
                yield break;
            }

            if (CarSpawner.Instance == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrainInternalCoroutine failed: CarSpawner instance not available.");
                if (onComplete != null) onComplete(null);
                yield break;
            }

            // Validate consist and track lengths for forward vs reverse span
            float totalConsistLength = CarSpawner.Instance.GetTotalCarLiveriesLength(liveries);
            float trackLength = track.curve != null ? track.curve.length : 0f;

            if (trackLength > 0f && startSpan <= 0.0)
            {
                if (totalConsistLength + 30f <= trackLength)
                {
                    startSpan = Math.Max(15.0, (trackLength - totalConsistLength) * 0.5);
                }
                else
                {
                    startSpan = 15.0;
                }
            }

            // Base game CarSpawner.PopulateSpawnData already negates worldForward when flipTrainConsist is true.
            // All car orientation entries must be false to avoid double-negation which would invert the locomotive backwards.
            List<bool> orientationList = new List<bool>(liveries.Count);
            for (int i = 0; i < liveries.Count; i++)
            {
                orientationList.Add(false);
            }

            // Precalculate track curve placements mathematically (instantaneous, < 1ms)
            CarSpawner.SpawnData spawnData = default(CarSpawner.SpawnData);
            bool dataGenerated = false;
            try
            {
                spawnData = CarSpawner.GetTrackMiddleBasedSpawnData(
                    liveries,
                    orientationList,
                    track,
                    startSpan,
                    flipTrainConsist);
                dataGenerated = (spawnData.result == CarSpawner.SpawnDataResult.OK && spawnData.carData != null && spawnData.carData.Length > 0);
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner: GetTrackMiddleBasedSpawnData exception: {0}", ex));
            }

            if (!dataGenerated || spawnData.carData == null || spawnData.carData.Length == 0 || spawnData.track == null || spawnData.track.curve == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Failed to compute valid spawn data (result: {0}) or track curve lookup is null on track '{1}'.", spawnData.result, track != null ? track.name : "null"));
                if (onComplete != null) onComplete(null);
                yield break;
            }

            // CRITICAL INVARIANT [FLT-03]: Sea-Level Ocean Spawning at Vector3.Zero (See GRAVEYARD.md)
            bool invalidPositionFound = false;
            for (int pIdx = 0; pIdx < spawnData.carData.Length; pIdx++)
            {
                Vector3 pos = spawnData.carData[pIdx].position;
                if (pos == Vector3.zero || pos.sqrMagnitude < 0.0001f)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Car {0} computed spawn position is Vector3.zero on track '{1}'. Aborting spawn cleanly to prevent spawning in ocean.", pIdx, track.name));
                    invalidPositionFound = true;
                    break;
                }
            }
            if (invalidPositionFound)
            {
                if (onComplete != null) onComplete(null);
                yield break;
            }

            List<TrainCar> spawnedCars = new List<TrainCar>(spawnData.carData.Length);
            IsSpawningAmbientConsist = true;
            float spawnStartTime = Time.realtimeSinceStartup;
            AITraffic.Diagnostics.PerformanceProfiler.RecordSpawnStart(track.name, spawnData.carData.Length);

            bool spawnFailed = false;
            for (int i = 0; i < spawnData.carData.Length; i++)
            {
                // Safety check: if scene is unloading or world tracks are destroyed, abort immediately
                if (!Main.IsWorldFullyLoaded || track == null || spawnData.track == null || spawnData.track.curve == null || CarSpawner.Instance == null)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("TrainSpawner: Aborting spawn loop at car {0}/{1} due to world unload or invalid track.", i, spawnData.carData.Length));
                    spawnFailed = true;
                    break;
                }

                var cData = spawnData.carData[i];
                if (cData.prefab == null) continue;

                // Validate position before calling CarSpawner
                if (cData.position == Vector3.zero || cData.position.sqrMagnitude < 0.0001f)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Aborting spawn: car {0} computed position is Vector3.zero on track '{1}'.", i, track.name));
                    spawnFailed = true;
                    break;
                }

                TrainCar car = null;
                try
                {
                    car = CarSpawner.Instance.SpawnCar(
                        cData.prefab,
                        spawnData.track,
                        cData.position,
                        cData.forward,
                        playerSpawnedCar: true,
                        uniqueCar: false
                    );
                }
                catch (Exception spawnEx)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("Error spawning car {0} in consist: {1}", i, spawnEx));
                    spawnFailed = true;
                    break;
                }

                if (car == null)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: CarSpawner returned null for car {0} on track '{1}'.", i, track.name));
                    spawnFailed = true;
                    break;
                }

                // Register in spawning registry immediately so subsequent tagging recognizes it as newly instantiated AI equipment
                RegisterSpawningCar(car);
                ModCompatManager.TagCarAsAITraffic(car);
                spawnedCars.Add(car);
                AITraffic.Diagnostics.PerformanceProfiler.RecordSpawnCar(spawnedCars.Count, spawnData.carData.Length);

                // Clamp handbrake immediately so newly spawned car stays rock-solid stationary on grades while consist assembles
                if (car.brakeSystem != null)
                {
                    car.brakeSystem.SetHandbrakePosition(1.0f, true);
                }

                car.playerSpawnedCar = true;
                car.preventDebtDisplay = true;
                car.preventAutoCouple = true;

                var cdc = car.GetComponent<DV.ServicePenalty.CarDebtController>();
                if (cdc != null) cdc.SetDummyDebtTracker();

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    ApplyAIDamageImmunity(car, true);
                }

                // Time-slice: yield 0.18s (~11 frames at 60 FPS) between car instantiations to guarantee smooth rendering headroom
                yield return new WaitForSeconds(0.18f);
            }

            // Atomic consist validation: if any car failed or consist is incomplete, roll back and clean up immediately
            if (spawnFailed || spawnedCars.Count == 0 || spawnedCars.Count != spawnData.carData.Length)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Consist spawn failed or incomplete ({0}/{1} cars spawned) on track '{2}'. Rolling back and cleaning up partial consist.",
                        spawnedCars.Count, spawnData.carData.Length, track != null ? track.name : "null"));

                // CRITICAL INVARIANT [FLT-04]: Atomic consist rollback via SafeDeleteCars (See GRAVEYARD.md)
                UnregisterSpawningCars(spawnedCars);
                if (CarSpawner.Instance != null && spawnedCars.Count > 0)
                {
                    try
                    {
                        TrainDespawner.SafeDeleteCars(spawnedCars, true);
                    }
                    catch (Exception delEx)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Error(string.Format("Error rolling back failed consist: {0}", delEx));
                    }
                }
                IsSpawningAmbientConsist = false;
                if (onComplete != null) onComplete(null);
                yield break;
            }

            // Yield 1 frame for physics / bogie registering and initial coupler settling
            yield return null;

            // Tag ALL spawned cars immediately as AI traffic so every car receives an AITrafficCarMarker,
            // is registered in AI car collections, and is omitted from savegames and player debt systems
            ModCompatManager.TagCarsAsAITraffic(spawnedCars);

            // Locate lead locomotive
            TrainCar leadLoco = null;
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                var car = spawnedCars[i];
                if (car != null && IsSupportedAILocomotive(car))
                {
                    leadLoco = car;
                    break;
                }
            }
            if (leadLoco == null) leadLoco = spawnedCars[0];

            // Tag trainset for AI save segregation
            if (leadLoco.trainset != null)
            {
                ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
            }

            // Pre-charge the air brake system across all individual cars and locomotives BEFORE coupling.
            // (Charging each car while uncoupled avoids base game "Attempt to SetBrakePipePressure on Brakeset with multiple cars" warnings)
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                var car = spawnedCars[i];
                if (car != null && car.brakeSystem != null)
                {
                    try
                    {
                        if (car.IsLoco)
                        {
                            car.brakeSystem.SetMainReservoirPressure(9.5f);
                        }
                        car.brakeSystem.SetBrakePipePressure(5.0f);
                        car.brakeSystem.SetAuxReservoirPressure(5.0f);
                        car.brakeSystem.SetControlReservoirPressure(5.0f);
                    }
                    catch { }
                }

                // Batch: yield a frame every 5 cars to spread brake calculations smoothly
                if ((i + 1) % 5 == 0)
                {
                    yield return null;
                }
            }

            yield return null;

            // Connect couplers, air hoses, open angle cocks, tighten chains, time-sliced across frames (1 car pair per frame)
            for (int i = 0; i < spawnedCars.Count - 1; i++)
            {
                var cA = spawnedCars[i];
                var cB = spawnedCars[i + 1];
                if (cA != null && cB != null && cA != cB)
                {
                    CoupleAdjacentCars(cA, cB);
                }
                yield return null;
            }

            // Consist coupling complete: re-enable auto-coupling on all cars
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                if (spawnedCars[i] != null)
                {
                    spawnedCars[i].preventAutoCouple = false;
                }
            }

            // Ensure outer end-most couplers have closed angle cocks so brake pipe holds air
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                var car = spawnedCars[i];
                if (car == null) continue;

                if (car.frontCoupler != null && !car.frontCoupler.IsCoupled())
                {
                    car.frontCoupler.IsCockOpen = false;
                }

                if (car.rearCoupler != null && !car.rearCoupler.IsCoupled())
                {
                    car.rearCoupler.IsCockOpen = false;
                }
            }

            // Yield 1 frame for coupler joint physics settling
            yield return null;

            // Retag full coupled trainset now that all cars are unified into a single consist
            if (leadLoco != null && leadLoco.trainset != null)
            {
                ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
            }

            // Release temporary holding handbrakes and clean up debt records
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                var car = spawnedCars[i];
                if (car == null) continue;

                if (car.brakeSystem != null)
                {
                    car.brakeSystem.SetHandbrakePosition(0f, true);
                }

                var controls = (car.SimController != null) ? car.SimController.controlsOverrider : car.GetComponent<BaseControlsOverrider>();
                if (controls != null && controls.Handbrake != null)
                {
                    controls.Handbrake.Set(0f);
                }

                if (car.IsLoco && DV.ServicePenalty.LocoDebtController.Instance != null && DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts != null)
                {
                    var existingDebt = DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Find(d => d != null && d.car == car);
                    if (existingDebt != null)
                    {
                        DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Remove(existingDebt);
                        AITraffic.Compat.AIDebtPatches.SafeUnregisterDebt(existingDebt);
                    }
                }

                // Batch: yield a frame every 5 cars to spread handbrake releasing smoothly
                if ((i + 1) % 5 == 0)
                {
                    yield return null;
                }
            }

            // Yield 1 frame before heavy loco init (SimController port discovery, brake system init, engine startup)
            yield return null;

            // Initialize locomotives (startup prime movers, reverser, lights), staggered 1 loco per frame
            for (int i = 0; i < spawnedCars.Count; i++)
            {
                var car = spawnedCars[i];
                if (car != null && car.IsLoco)
                {
                    bool isLead = (car == leadLoco);
                    InitializeLocomotive(car, isLead);
                    yield return null;
                }
            }

            // Yield 1 frame for SimController to settle before AIEngineer component initialization
            yield return null;

            // Attach AIEngineer component
            AIEngineer engineer = leadLoco.gameObject.GetComponent<AIEngineer>();
            if (engineer == null)
            {
                engineer = leadLoco.gameObject.AddComponent<AIEngineer>();
            }

            if (engineer != null)
            {
                engineer.ConsistType = consistType;
                engineer.RegisterConsistCars(spawnedCars);
                if (startInShuntingMode)
                {
                    engineer.StartInShuntingMode();
                }
            }

            // Consist setup complete and registered with active AIEngineer: release from spawning registry
            UnregisterSpawningCars(spawnedCars);
            IsSpawningAmbientConsist = false;

            // Stagger cargo loading across frames in background
            if (engineer != null && specs != null && specs.Count > 0)
            {
                engineer.StartCoroutine(PopulateConsistCargoAsync(spawnedCars, specs));
            }

            // Yield 1 final frame so AIEngineer Awake/Start complete before caller accesses the engineer
            yield return null;

            if (Main.IsDebugLoggingEnabled)
            {
                Main.LogDebug(string.Format("[TrainSpawner] Successfully time-sliced spawned AI train ({0} cars, Lead Loco: {1}) on track '{2}'.",
                    spawnedCars.Count, ModCompatManager.GetCarId(leadLoco), track.name));
            }

            float spawnDuration = Time.realtimeSinceStartup - spawnStartTime;
            AITraffic.Diagnostics.PerformanceProfiler.RecordSpawnEnd(track.name, spawnedCars.Count, spawnDuration);

            if (onComplete != null)
            {
                onComplete(engineer);
            }
        }

        private static AIEngineer SpawnAITrainInternal(
            RailTrack track,
            List<TrainCarLivery> liveries,
            List<ConsistCarSpec> specs,
            double startSpan,
            bool flipTrainConsist)
        {
            if (track == null || track.curve == null || liveries == null || liveries.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Target track or track curve lookup is null, or invalid arguments.");
                return null;
            }

            if (IsMilitaryTrack(track) && !IsMilitaryLicenseAcquired())
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("TrainSpawner: Cannot spawn on military track '{0}' without Military 1 license.", track.name));
                return null;
            }

            if (CarSpawner.Instance == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: CarSpawner instance not available.");
                return null;
            }

            List<TrainCar> spawnedCars = null;
            try
            {
                // Validate consist and track lengths for forward vs reverse span
                float totalConsistLength = CarSpawner.Instance.GetTotalCarLiveriesLength(liveries);
                float trackLength = track.curve != null ? track.curve.length : 0f;

                if (trackLength > 0f && startSpan <= 0.0)
                {
                    if (totalConsistLength + 30f <= trackLength)
                    {
                        startSpan = Math.Max(15.0, (trackLength - totalConsistLength) * 0.5);
                    }
                    else
                    {
                        startSpan = 15.0;
                    }
                }

                // Base game CarSpawner.PopulateSpawnData already negates worldForward when flipTrainConsist is true.
                // All car orientation entries must be false to avoid double-negation which would invert the locomotive backwards.
                List<bool> orientationList = new List<bool>(liveries.Count);
                for (int i = 0; i < liveries.Count; i++)
                {
                    orientationList.Add(false);
                }

                // CRITICAL INVARIANT [FLT-03]: Validate all computed car positions against Vector3.zero BEFORE calling CarSpawner (See GRAVEYARD.md)
                CarSpawner.SpawnData preCheckData = CarSpawner.GetTrackMiddleBasedSpawnData(
                    liveries,
                    orientationList,
                    track,
                    startSpan,
                    flipTrainConsist);

                if (preCheckData.result != CarSpawner.SpawnDataResult.OK || preCheckData.carData == null || preCheckData.carData.Length == 0)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Failed to compute valid spawn data (result: {0}) on track '{1}'. Aborting synchronous spawn.", preCheckData.result, track.name));
                    return null;
                }

                for (int pIdx = 0; pIdx < preCheckData.carData.Length; pIdx++)
                {
                    Vector3 pos = preCheckData.carData[pIdx].position;
                    if (pos == Vector3.zero || pos.sqrMagnitude < 0.0001f)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Car {0} computed spawn position is Vector3.zero on track '{1}'. Aborting synchronous spawn to prevent ocean spawning.", pIdx, track.name));
                        return null;
                    }
                }

                // 1. Spawn cars on track with playerSpawnedCars = true so SimController natively skips debt tracking
                IsSpawningAmbientConsist = true;
                spawnedCars = CarSpawner.Instance.SpawnCarTypesOnTrack(
                    trainCarTypes: liveries,
                    carsOrientationReversed: orientationList,
                    railTrack: track,
                    preventAutoCoupleOnLastCars: false,
                    applyHandbrakeOnLastCars: false,
                    startSpan: startSpan,
                    flipTrainConsist: flipTrainConsist,
                    playerSpawnedCars: true
                );

                if (spawnedCars == null || spawnedCars.Count == 0)
                {
                    IsSpawningAmbientConsist = false;
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: CarSpawner returned null or empty car list on track '{0}'.", track.name));
                    return null;
                }

                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var c = spawnedCars[i];
                    if (c != null)
                    {
                        if (c.transform.position == Vector3.zero || c.transform.position.sqrMagnitude < 0.0001f)
                        {
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                Main.ModEntry.Logger.Error(string.Format("TrainSpawner: Car {0} spawned at Vector3.zero on track '{1}'. Rolling back consist to prevent ocean spawning.", i, track.name));
                            // CRITICAL INVARIANT [FLT-04]: Roll back consist via SafeDeleteCars on invalid position (See GRAVEYARD.md)
                            TrainDespawner.SafeDeleteCars(spawnedCars, true);
                            IsSpawningAmbientConsist = false;
                            return null;
                        }
                        RegisterSpawningCar(c);
                        c.playerSpawnedCar = true;
                        c.preventDebtDisplay = true;
                    }
                }
                ModCompatManager.TagCarsAsAITraffic(spawnedCars);

                // 2. Locate lead locomotive (first supported diesel/mechanical/electric locomotive in consist)
                TrainCar leadLoco = null;
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car != null && IsSupportedAILocomotive(car))
                    {
                        leadLoco = car;
                        break;
                    }
                }

                if (leadLoco == null)
                {
                    leadLoco = spawnedCars[0];
                }

                // 3. Tag trainset immediately for AI save segregation, mod compatibility, and debt suppression
                if (leadLoco.trainset != null)
                {
                    ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
                }

                // 4. Connect couplers, air hoses, open cocks, tighten chains
                ConfigureConsistCouplers(spawnedCars);

                // 5. Release handbrakes, apply damage immunity, and guarantee debt immunity across all cars
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car == null) continue;

                    car.playerSpawnedCar = true;
                    car.preventDebtDisplay = true;

                    // Attach dummy debt tracker to eliminate any car/cargo damage debt
                    var cdc = car.GetComponent<DV.ServicePenalty.CarDebtController>();
                    if (cdc != null)
                    {
                        cdc.SetDummyDebtTracker();
                    }

                    // Purge any accidental registration in LocoDebtController
                    if (car.IsLoco && DV.ServicePenalty.LocoDebtController.Instance != null && DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts != null)
                    {
                        var existingDebt = DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Find(d => d != null && d.car == car);
                        if (existingDebt != null)
                        {
                            DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Remove(existingDebt);
                            AITraffic.Compat.AIDebtPatches.SafeUnregisterDebt(existingDebt);
                        }
                    }

                    if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                    {
                        ApplyAIDamageImmunity(car, true);
                    }

                    // Release handbrake across train car and controls
                    if (car.brakeSystem != null)
                    {
                        car.brakeSystem.SetHandbrakePosition(0f, true);
                    }
                    var controls = (car.SimController != null) ? car.SimController.controlsOverrider : car.GetComponent<BaseControlsOverrider>();
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(0f);
                    }
                }

                // 6. Initialize locomotives (start engine, headlights, reverser)
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car != null && car.IsLoco)
                    {
                        bool isLead = (car == leadLoco);
                        InitializeLocomotive(car, isLead);
                    }
                }

                // 6.5 Ensure Multiple Unit (MU) cables are connected between all adjacent locomotives/slugs
                for (int i = 0; i < spawnedCars.Count - 1; i++)
                {
                    var carA = spawnedCars[i];
                    var carB = spawnedCars[i + 1];
                    if (carA != null && carB != null && carA.IsMultipleUnit && carB.IsMultipleUnit)
                    {
                        ConnectMUCablesBetween(carA, carB);
                    }
                }

                // 7. Attach AIEngineer component to lead locomotive
                AIEngineer engineer = leadLoco.gameObject.GetComponent<AIEngineer>();
                if (engineer == null)
                {
                    engineer = leadLoco.gameObject.AddComponent<AIEngineer>();
                }
                if (engineer != null)
                {
                    engineer.ConsistType = ConsistType.RegionalFreight;
                    engineer.RegisterConsistCars(spawnedCars);
                }

                // Consist setup complete and registered with active AIEngineer: release from spawning registry
                UnregisterSpawningCars(spawnedCars);
                IsSpawningAmbientConsist = false;

                // 8. Time-slice procedural cargo loading across frames to eliminate spawn stutter while maintaining 100% visual/weight fidelity
                if (engineer != null && specs != null && specs.Count > 0)
                {
                    engineer.StartCoroutine(PopulateConsistCargoAsync(spawnedCars, specs));
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[TrainSpawner] Successfully spawned AI train ({0} cars, Lead Loco: {1}) on track '{2}'.",
                        spawnedCars.Count, ModCompatManager.GetCarId(leadLoco), track.name));
                }

                return engineer;
            }
            catch (Exception ex)
            {
                UnregisterSpawningCars(spawnedCars);
                IsSpawningAmbientConsist = false;
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrainSpawner.SpawnAITrain on track '{0}': {1}", track.name, ex));
                return null;
            }
        }

        private static System.Collections.IEnumerator PopulateConsistCargoAsync(List<TrainCar> spawnedCars, List<ConsistCarSpec> specs)
        {
            if (spawnedCars == null || specs == null) yield break;

            // Yield initial frame so car rigidbodies and bogies settle cleanly
            yield return null;

            int loadedCarCount = 0;
            string sampleCargoName = "None";

            // CRITICAL INVARIANT [FLT-08]: Base game CarSpawner.GetUninitializedSpawnData reverses car order when flipTrainConsist is false.
            // Detect whether spawnedCars is inverted relative to specs so each physical wagon receives its own matching cargo spec.
            int count = Mathf.Min(spawnedCars.Count, specs.Count);
            int forwardMatches = 0;
            int reverseMatches = 0;
            for (int m = 0; m < count; m++)
            {
                var c = spawnedCars[m];
                if (c == null) continue;
                if (c.carLivery == specs[m].Livery) forwardMatches++;
                if (c.carLivery == specs[count - 1 - m].Livery) reverseMatches++;
            }
            bool isReversed = reverseMatches > forwardMatches;
            if (forwardMatches == reverseMatches && count > 1 && spawnedCars[0] != null && spawnedCars[count - 1] != null)
            {
                isReversed = !spawnedCars[0].IsLoco && spawnedCars[count - 1].IsLoco;
            }

            for (int i = 0; i < count; i++)
            {
                var car = spawnedCars[i];
                if (car == null || car.IsLoco) continue;

                int specIndex = isReversed ? (count - 1 - i) : i;
                CargoType cargoToLoad = specs[specIndex].Cargo;
                if (cargoToLoad != CargoType.None && car.logicCar != null)
                {
                    try
                    {
                        float amount = car.logicCar.capacity > 0f ? car.logicCar.capacity : 1f;
                        car.logicCar.LoadCargo(amount, cargoToLoad, null);
                        loadedCarCount++;
                        sampleCargoName = cargoToLoad.ToString();
                    }
                    catch (Exception ex)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Warning(string.Format("Failed loading cargo '{0}' onto car '{1}': {2}", cargoToLoad, ModCompatManager.GetCarId(car), ex.Message));
                    }

                    // Stagger: yield 0.10s (~6 frames at 60 FPS) between loaded cars to distribute 3D mesh instantiation and texture uploads smoothly
                    yield return new WaitForSeconds(0.10f);
                }
            }

            if (Main.IsDebugLoggingEnabled && loadedCarCount > 0)
            {
                Main.LogDebug(string.Format("[TrainSpawner] Completed cargo loading for {0} cars ({1}).", loadedCarCount, sampleCargoName));
            }
        }

        #region Mechanical & Coupler Setup

        /// <summary>
        /// Automatically connects couplers, attaches air hoses, opens angle cocks,
        /// tightens screw chains between adjacent cars, and closes outer end cocks.
        /// </summary>
        public static void ConfigureConsistCouplers(List<TrainCar> cars)
        {
            if (cars == null || cars.Count < 2) return;

            // Connect adjacent cars in sequence
            for (int i = 0; i < cars.Count - 1; i++)
            {
                var cA = cars[i];
                var cB = cars[i + 1];
                if (cA != null && cB != null && cA != cB)
                {
                    CoupleAdjacentCars(cA, cB);
                }
            }

            // Ensure outer end-most couplers have closed angle cocks so brake pipe holds air
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                if (car.frontCoupler != null && !car.frontCoupler.IsCoupled())
                {
                    car.frontCoupler.IsCockOpen = false;
                }

                if (car.rearCoupler != null && !car.rearCoupler.IsCoupled())
                {
                    car.rearCoupler.IsCockOpen = false;
                }
            }
        }

        private static void CoupleAdjacentCars(TrainCar carA, TrainCar carB)
        {
            if (carA == null || carB == null || carA == carB) return;
            if (carA.gameObject == null || carB.gameObject == null) return;

            Coupler[] couplersA = new Coupler[] { carA.frontCoupler, carA.rearCoupler };
            Coupler[] couplersB = new Coupler[] { carB.frontCoupler, carB.rearCoupler };

            Coupler bestA = null;
            Coupler bestB = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < couplersA.Length; i++)
            {
                var cA = couplersA[i];
                if (cA == null || cA.gameObject == null || !cA.gameObject.activeInHierarchy) continue;

                for (int j = 0; j < couplersB.Length; j++)
                {
                    var cB = couplersB[j];
                    if (cB == null || cB.gameObject == null || !cB.gameObject.activeInHierarchy) continue;

                    // Strictly skip if both couplers belong to the same train car
                    if (cA == cB || cA.train == cB.train) continue;

                    float distSq = (cA.transform.position - cB.transform.position).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestA = cA;
                        bestB = cB;
                    }
                }
            }

            if (bestA != null && bestB != null && bestA != bestB && bestA.train != bestB.train && bestDistSq < 25.0f) // 5m squared
            {
                try
                {
                    if (!bestA.IsCoupled() || bestA.GetCoupled() != bestB)
                    {
                        bestA.CoupleTo(bestB, playAudio: false, viaChainInteraction: false);
                    }

                    if (bestA.GetAirHoseConnectedTo() == null)
                    {
                        bestA.ConnectAirHose(bestB, playAudio: false);
                    }

                    bestA.IsCockOpen = true;
                    bestB.IsCockOpen = true;
                    bestA.SetChainTight(true);
                    bestB.SetChainTight(true);

                    // Connect Multiple Unit (MU) cable if both adjacent units support it
                    ConnectMUCablesBetween(carA, carB);

                    // ZCouplers compatibility: remove any CouplerBreaker components attached during coupling
                    // to prevent initial frame settling impulses from tearing AI consist joints apart
                    var cbType = GetCouplerBreakerType();
                    if (cbType != null)
                    {
                        try
                        {
                            var cbA = bestA.GetComponent(cbType);
                            if (cbA != null) UnityEngine.Object.Destroy(cbA);
                            var cbB = bestB.GetComponent(cbType);
                            if (cbB != null) UnityEngine.Object.Destroy(cbB);
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("Warning coupling cars '{0}' and '{1}': {2}", ModCompatManager.GetCarId(carA), ModCompatManager.GetCarId(carB), ex.Message));
                }
            }
        }

        /// <summary>
        /// Connects the Multiple Unit (MU) cable between two adjacent locomotives/slugs if both support MU.
        /// </summary>
        public static void ConnectMUCablesBetween(TrainCar carA, TrainCar carB)
        {
            if (carA == null || carB == null) return;
            if (!carA.IsMultipleUnit || !carB.IsMultipleUnit) return;
            if (carA.muModule == null || carB.muModule == null) return;

            Coupler cA = null;
            Coupler cB = null;

            if (carA.frontCoupler != null && carA.frontCoupler.GetCoupled() != null && carA.frontCoupler.GetCoupled().train == carB)
            {
                cA = carA.frontCoupler;
                cB = carA.frontCoupler.GetCoupled();
            }
            else if (carA.rearCoupler != null && carA.rearCoupler.GetCoupled() != null && carA.rearCoupler.GetCoupled().train == carB)
            {
                cA = carA.rearCoupler;
                cB = carA.rearCoupler.GetCoupled();
            }
            else
            {
                Coupler[] couplersA = new Coupler[] { carA.frontCoupler, carA.rearCoupler };
                Coupler[] couplersB = new Coupler[] { carB.frontCoupler, carB.rearCoupler };
                float bestDistSq = float.MaxValue;
                for (int i = 0; i < couplersA.Length; i++)
                {
                    if (couplersA[i] == null) continue;
                    for (int j = 0; j < couplersB.Length; j++)
                    {
                        if (couplersB[j] == null) continue;
                        float d = (couplersA[i].transform.position - couplersB[j].transform.position).sqrMagnitude;
                        if (d < bestDistSq)
                        {
                            bestDistSq = d;
                            cA = couplersA[i];
                            cB = couplersB[j];
                        }
                    }
                }
                if (bestDistSq > 25.0f) return;
            }

            if (cA == null || cB == null) return;

            var cableA = cA.isFrontCoupler ? carA.muModule.FrontCable : carA.muModule.RearCable;
            var cableB = cB.isFrontCoupler ? carB.muModule.FrontCable : carB.muModule.RearCable;

            if (cableA != null && cableB != null && !cableA.IsConnected && !cableB.IsConnected)
            {
                try
                {
                    cableA.Connect(cableB, playAudio: false);
                    if (Main.IsDebugLoggingEnabled)
                    {
                        Main.LogDebug(string.Format("[TrainSpawner] Successfully connected MU cable between '{0}' ({1}) and '{2}' ({3}).",
                            ModCompatManager.GetCarId(carA), cA.isFrontCoupler ? "Front" : "Rear",
                            ModCompatManager.GetCarId(carB), cB.isFrontCoupler ? "Front" : "Rear"));
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    {
                        Main.ModEntry.Logger.Warning(string.Format("[TrainSpawner] Failed to connect MU cable between '{0}' and '{1}': {2}",
                            ModCompatManager.GetCarId(carA), ModCompatManager.GetCarId(carB), ex.Message));
                    }
                }
            }
        }

        public static void InitializeLocomotive(TrainCar loco, bool isLead = true)
        {
            if (loco == null) return;

            try
            {
                // Official Derail Valley locomotive startup (closes fuses, primes fuel, cranks engine)
                try
                {
                    DV.Simulation.Controllers.StartupHelper.Startup(loco);
                }
                catch (Exception stEx)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("StartupHelper for loco '{0}' warning: {1}", ModCompatManager.GetCarId(loco), stEx.Message));
                }

                var controls = loco.GetComponent<BaseControlsOverrider>();
                if (controls == null && loco.SimController != null)
                {
                    controls = loco.SimController.controlsOverrider;
                }

                if (loco.brakeSystem != null)
                {
                    try
                    {
                        loco.brakeSystem.SetMainReservoirPressure(9.5f);
                        loco.brakeSystem.SetAuxReservoirPressure(5.0f);
                        loco.brakeSystem.SetControlReservoirPressure(5.0f);
                    }
                    catch { }
                    loco.brakeSystem.SetHandbrakePosition(0f, true);
                }

                if (controls != null)
                {
                    if (isLead)
                    {
                        if (controls.Handbrake != null) controls.Handbrake.Set(0f);
                        if (controls.Brake != null) controls.Brake.Set(0.5f); // Hold service brake until AI takes control
                        if (controls.IndependentBrake != null) controls.IndependentBrake.Set(0f);
                        if (controls.DynamicBrake != null) controls.DynamicBrake.Set(0f);
                        if (controls.Reverser != null) controls.Reverser.Set(1f);
                        if (controls.HeadlightsFront != null) controls.HeadlightsFront.Set(1.0f); // White headlight
                        if (controls.HeadlightsRear != null) controls.HeadlightsRear.Set(0.4f); // Off (neutral)
                        if (controls.BrakeCutout != null) controls.BrakeCutout.Set(1f);
                    }
                    else
                    {
                        // Trailing helper units: cut out brake valve, release independent/train/hand brakes
                        if (controls.BrakeCutout != null) controls.BrakeCutout.Set(0f);
                        if (controls.IndependentBrake != null) controls.IndependentBrake.Set(0f);
                        if (controls.Brake != null) controls.Brake.Set(0f);
                        if (controls.Handbrake != null) controls.Handbrake.Set(0f);
                        if (controls.DynamicBrake != null) controls.DynamicBrake.Set(0f);
                        if (controls.Reverser != null) controls.Reverser.Set(1f);
                        if (controls.HeadlightsFront != null) controls.HeadlightsFront.Set(0.4f); // Off (neutral)
                        if (controls.HeadlightsRear != null) controls.HeadlightsRear.Set(0.4f); // Off (neutral)
                    }

                    // Ensure engine starter was primed if not already on
                    if (controls.EngineOnReader != null && !controls.EngineOnReader.IsOn && controls.Starter != null)
                    {
                        controls.Starter.Set(1f);
                    }
                }
                // Remove career license blockers and colliders so moving AI trains never trigger position-fix lag spikes
                RemoveLocoZoneBlockers(loco);
                ApplyAIDamageImmunity(loco, true);

                if (isLead)
                {
                    try
                    {
                        AITraffic.Driver.AILocomotiveLighting.GetOrCreate(loco, null);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Warning initializing locomotive '{0}': {1}", ModCompatManager.GetCarId(loco), ex.Message));
            }
        }

        /// <summary>
        /// Removes career license blockers (LocoZoneBlocker) and their colliders from AI-operated locomotives,
        /// preventing physics invalidation, log spam, and lag spikes when running at speed.
        /// </summary>
        public static void RemoveLocoZoneBlockers(TrainCar loco)
        {
            if (loco == null) return;

            try
            {
                var blockers = loco.GetComponentsInChildren<LocoZoneBlocker>(true);
                if (blockers != null && blockers.Length > 0)
                {
                    for (int i = 0; i < blockers.Length; i++)
                    {
                        if (blockers[i] != null)
                        {
                            AITraffic.Compat.LocoZoneBlockerPatches.Unblock(blockers[i]);
                        }
                    }
                }

                if (loco.interior != null)
                {
                    var interiorBlockers = loco.interior.GetComponentsInChildren<LocoZoneBlocker>(true);
                    if (interiorBlockers != null && interiorBlockers.Length > 0)
                    {
                        for (int i = 0; i < interiorBlockers.Length; i++)
                        {
                            if (interiorBlockers[i] != null)
                            {
                                AITraffic.Compat.LocoZoneBlockerPatches.Unblock(interiorBlockers[i]);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Warning clearing LocoZoneBlockers on '{0}': {1}", ModCompatManager.GetCarId(loco), ex.Message));
            }
        }

        /// <summary>
        /// Checks if a locomotive is fully supported by the AI driver (excludes steam locos: S060, S282).
        /// </summary>
        public static bool IsSupportedAILocomotive(TrainCar car)
        {
            if (car == null || !car.IsLoco) return false;
            string id = car.carLivery != null ? car.carLivery.id : "";
            if (id.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("S060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("S282", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Applies or clears damage immunity across an AI train car.
        /// </summary>
        public static void ApplyAIDamageImmunity(TrainCar car, bool immune = true)
        {
            if (car == null) return;

            try
            {
                // 1. Car body & collision damage
                if (car.CarDamage != null)
                {
                    car.CarDamage.IgnoreDamage(immune);
                }

                // 2. Powertrain, wheel, mechanical and electrical damage
                var dc = car.GetComponent<DV.Damage.DamageController>();
                if (dc != null)
                {
                    dc.IgnoreDamage(immune);
                }

                // 3. LocoSim drivetrain component immunity (breaker trips & traction motor blowouts)
                if (immune)
                {
                    AITraffic.Compat.AIDamageImmunityPatches.RegisterAISimComponents(car);
                }
                else
                {
                    AITraffic.Compat.AIDamageImmunityPatches.UnregisterAISimComponents(car);
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error setting AI damage immunity on '{0}': {1}", ModCompatManager.GetCarId(car), ex.Message));
            }
        }

        #endregion
    }
}
