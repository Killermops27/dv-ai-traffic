using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Compat;
using AITraffic.Driver;
using AITraffic.Workers;

namespace AITraffic.Fleet
{
    /// <summary>
    /// Handles safe despawning, resource cleanup, and save untagging for AI train consists.
    /// Ensures trains are only cleaned up outside player view and beyond safety distances.
    /// </summary>
    public static class TrainDespawner
    {
        public const float DefaultSafeDespawnDistance = 2500f;
        public const float DefaultTerminusSafeDespawnDistance = 500f;
        public const float DefaultTerminusFrustumDistance = 750f;

        /// <summary>
        /// Checks whether a given AI trainset can be safely despawned without visible pop-in/pop-out
        /// or disrupting the player.
        /// </summary>
        public static bool CanDespawnSafely(Trainset trainset, float minDistance = DefaultTerminusSafeDespawnDistance, float frustumDistance = DefaultTerminusFrustumDistance)
        {
            if (trainset == null || trainset.cars == null || trainset.cars.Count == 0)
                return true;
            return CanDespawnCarsSafely(trainset.cars, minDistance, frustumDistance);
        }

        /// <summary>
        /// Checks whether all cars belonging to an AI train (including any separated/derailed consist cars)
        /// can be safely despawned without visible pop-in/pop-out or disrupting the player.
        /// </summary>
        private static readonly List<TrainCar> s_despawnCarsScratch = new List<TrainCar>(32);

        public static bool CanDespawnSafely(AIEngineer engineer, float minDistance = DefaultTerminusSafeDespawnDistance, float frustumDistance = DefaultTerminusFrustumDistance)
        {
            if (engineer == null)
                return true;

            var allCars = s_despawnCarsScratch;
            allCars.Clear();
            if (engineer.RegisteredConsistCars != null && engineer.RegisteredConsistCars.Count > 0)
            {
                for (int i = 0; i < engineer.RegisteredConsistCars.Count; i++)
                {
                    var c = engineer.RegisteredConsistCars[i];
                    if (c != null && !allCars.Contains(c))
                    {
                        allCars.Add(c);
                    }
                }
            }
            if (engineer.TrainCar != null && engineer.TrainCar.trainset != null && engineer.TrainCar.trainset.cars != null)
            {
                for (int i = 0; i < engineer.TrainCar.trainset.cars.Count; i++)
                {
                    var c = engineer.TrainCar.trainset.cars[i];
                    if (c != null && !allCars.Contains(c))
                    {
                        allCars.Add(c);
                    }
                }
            }
            else if (engineer.TrainCar != null && !allCars.Contains(engineer.TrainCar))
            {
                allCars.Add(engineer.TrainCar);
            }

            return CanDespawnCarsSafely(allCars, minDistance, frustumDistance);
        }

        /// <summary>
        /// Checks whether a list of cars can be safely despawned without visible pop-in/pop-out or disrupting the player.
        /// </summary>
        public static bool CanDespawnCarsSafely(List<TrainCar> cars, float minDistance = DefaultTerminusSafeDespawnDistance, float frustumDistance = DefaultTerminusFrustumDistance)
        {
            if (cars == null || cars.Count == 0)
                return true;

            // 0. Worker trains or player consists must NEVER be despawned!
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;
                if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                {
                    return false;
                }
            }

            Transform playerTransform = PlayerManager.PlayerTransform;
            if (playerTransform == null)
                return true; // No player active, safe to despawn

            Vector3 playerPos = playerTransform.position;
            TrainCar playerCar = PlayerManager.Car;

            // 1. Check if the player is riding, coupled to, or standing inside any car in this list
            if (playerCar != null && cars.Contains(playerCar))
            {
                return false;
            }

            // Also check proximity to any car (e.g. player standing on walkway/roof or immediately next to train)
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                // Absolute minimum safety distance: if player is within 30m of any car, never despawn
                if ((car.transform.position - playerPos).sqrMagnitude < 900f) // 30m
                {
                    return false;
                }
            }

            // 2. Check distance from player to every car
            float minDistanceSq = minDistance * minDistance;
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                float distSq = (car.transform.position - playerPos).sqrMagnitude;
                if (distSq < minDistanceSq)
                {
                    return false;
                }
            }

            // 3. Check player camera line of sight / view frustum if within frustumDistance
            Camera playerCam = PlayerManager.PlayerCamera ?? Camera.main;
            if (playerCam != null)
            {
                float frustumDistSq = frustumDistance * frustumDistance;
                for (int i = 0; i < cars.Count; i++)
                {
                    var car = cars[i];
                    if (car == null) continue;

                    float distSq = (car.transform.position - playerPos).sqrMagnitude;
                    if (distSq > frustumDistSq)
                        continue; // Beyond frustum concern

                    Vector3 viewportPoint = playerCam.WorldToViewportPoint(car.transform.position);
                    bool inViewFrustum = viewportPoint.z > 0f &&
                                         viewportPoint.x >= -0.05f && viewportPoint.x <= 1.05f &&
                                         viewportPoint.y >= -0.05f && viewportPoint.y <= 1.05f;

                    if (inViewFrustum)
                    {
                        // In player's direct field of view and within frustumDistance
                        return false;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Despawns and deletes the specified AI trainset from the game world.
        /// </summary>
        /// <param name="trainset">The trainset to delete.</param>
        /// <param name="forceInstant">Whether to bypass pool return and force immediate destruction.</param>
        /// <returns>True if despawned successfully.</returns>
        public static bool DespawnTrain(Trainset trainset, bool forceInstant = true)
        {
            if (trainset == null || trainset.cars == null || trainset.cars.Count == 0)
                return false;

            if (CarSpawner.Instance == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainDespawner: CarSpawner instance is null.");
                return false;
            }

            // Safety guard: NEVER despawn a player consist or worker train!
            for (int i = 0; i < trainset.cars.Count; i++)
            {
                var car = trainset.cars[i];
                if (car == null) continue;
                if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("[TrainDespawner] Aborting despawn of trainset: car '{0}' is part of an AI worker task or player consist!", ModCompatManager.GetCarId(car)));
                    return false;
                }
            }

            try
            {
                int carCount = trainset.cars.Count;
                string leadId = trainset.firstCar != null ? ModCompatManager.GetCarId(trainset.firstCar) : "Unknown";

                // 1. Remove AIEngineer components, release locks/reservations, and stop sounds/coroutines
                for (int i = 0; i < trainset.cars.Count; i++)
                {
                    var car = trainset.cars[i];
                    if (car == null) continue;

                    var engineer = car.GetComponent<AIEngineer>();
                    if (engineer != null)
                    {
                        engineer.EmergencyBrake();
                        engineer.ReleaseAllSignalReservations();
                        if (AITraffic.Core.TrafficManager.Instance != null)
                        {
                            AITraffic.Core.TrafficManager.Instance.UnregisterEngineer(engineer);
                        }
                        if (AITraffic.Navigation.JunctionController.Instance != null)
                        {
                            AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(engineer);
                        }
                        if (AITraffic.Navigation.RailGraph.Instance != null)
                        {
                            AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(engineer);
                        }
                        UnityEngine.Object.Destroy(engineer);
                    }

                    AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(car);
                    if (AITraffic.Navigation.RailGraph.Instance != null)
                    {
                        AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(car);
                    }
                }

                // CRITICAL INVARIANT [FLT-06]: Delete cars BEFORE untagging so SimController.OnCarDestroyed and
                // PersistentJobsMod hooks still see AITrafficCarMarker and s_aiCars membership during PrepareForDestroy.
                List<TrainCar> carsToDelete = new List<TrainCar>(trainset.cars);
                SafeDeleteCars(carsToDelete, forceInstant);
                ModCompatManager.UntagTrain(trainset);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrainDespawner] Despawned AI trainset (Lead: {0}, Cars: {1}).", leadId, carCount));

                return true;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrainDespawner.DespawnTrain: {0}", ex));
                return false;
            }
        }

        /// <summary>
        /// Despawns an AI train and ALL of its registered consist cars (including any cars
        /// separated or derailed during a collision) from the game world.
        /// </summary>
        private const int TimeSlicedDespawnCarsPerFrame = 2;

        public static bool DespawnTrain(AIEngineer engineer, bool forceInstant = false)
        {
            if (engineer == null)
                return false;

            if (CarSpawner.Instance == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainDespawner: CarSpawner instance is null.");
                return false;
            }

            try
            {
                // 1. Gather all cars belonging to this engineer (loco, current trainset, and registered consist cars)
                List<TrainCar> carsToDelete = new List<TrainCar>();
                if (engineer.RegisteredConsistCars != null && engineer.RegisteredConsistCars.Count > 0)
                {
                    for (int i = 0; i < engineer.RegisteredConsistCars.Count; i++)
                    {
                        var c = engineer.RegisteredConsistCars[i];
                        if (c != null && !carsToDelete.Contains(c))
                        {
                            carsToDelete.Add(c);
                        }
                    }
                }
                if (engineer.TrainCar != null && engineer.TrainCar.trainset != null && engineer.TrainCar.trainset.cars != null)
                {
                    for (int i = 0; i < engineer.TrainCar.trainset.cars.Count; i++)
                    {
                        var c = engineer.TrainCar.trainset.cars[i];
                        if (c != null && !carsToDelete.Contains(c))
                        {
                            carsToDelete.Add(c);
                        }
                    }
                }
                else if (engineer.TrainCar != null && !carsToDelete.Contains(engineer.TrainCar))
                {
                    carsToDelete.Add(engineer.TrainCar);
                }

                if (carsToDelete.Count == 0)
                    return false;

                // Safety guard: NEVER despawn a player consist or worker train!
                for (int i = 0; i < carsToDelete.Count; i++)
                {
                    var car = carsToDelete[i];
                    if (car == null) continue;
                    if (ModCompatManager.IsWorkerTrain(car) || WorkerManager.IsTrainCarInAnyWorkerTask(car))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Warning(string.Format("[TrainDespawner] Aborting despawn of train: car '{0}' is part of an AI worker task or player consist!", ModCompatManager.GetCarId(car)));
                        return false;
                    }
                }

                string leadId = engineer.TrainCar != null ? ModCompatManager.GetCarId(engineer.TrainCar) : "Unknown";
                int carCount = carsToDelete.Count;

                // 2. Immediately shut down AIEngineer and release all signal/junction/corridor reservations upfront
                HashSet<Trainset> affectedTrainsets = new HashSet<Trainset>();
                for (int i = 0; i < carsToDelete.Count; i++)
                {
                    var car = carsToDelete[i];
                    if (car == null) continue;

                    if (car.trainset != null)
                    {
                        affectedTrainsets.Add(car.trainset);
                    }

                    var eng = car.GetComponent<AIEngineer>();
                    if (eng != null)
                    {
                        eng.EmergencyBrake();
                        eng.ReleaseAllSignalReservations();
                        if (AITraffic.Core.TrafficManager.Instance != null)
                        {
                            AITraffic.Core.TrafficManager.Instance.UnregisterEngineer(eng);
                        }
                        if (AITraffic.Navigation.JunctionController.Instance != null)
                        {
                            AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(eng);
                        }
                        if (AITraffic.Navigation.RailGraph.Instance != null)
                        {
                            AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(eng);
                        }
                        UnityEngine.Object.Destroy(eng);
                    }

                    if (AITraffic.Navigation.JunctionController.Instance != null)
                    {
                        AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(car);
                    }
                    if (AITraffic.Navigation.RailGraph.Instance != null)
                    {
                        AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(car);
                    }

                }

                // CRITICAL INVARIANT [FLT-06]: Execute SafeDeleteCars BEFORE untagging or destroying AITrafficCarMarker
                // so synchronous OnCarDestroyed debt hooks recognize the despawning AI cars.
                bool canTimeSlice = !forceInstant &&
                                    Main.IsWorldFullyLoaded &&
                                    carsToDelete.Count > TimeSlicedDespawnCarsPerFrame &&
                                    AITraffic.Core.TrafficManager.Instance != null &&
                                    AITraffic.Core.TrafficManager.Instance.isActiveAndEnabled;

                if (canTimeSlice)
                {
                    AITraffic.Core.TrafficManager.Instance.StartCoroutine(
                        TimeSlicedDespawnRoutine(carsToDelete, affectedTrainsets, leadId, carCount));
                }
                else
                {
                    SafeDeleteCars(carsToDelete, true);

                    foreach (var ts in affectedTrainsets)
                    {
                        if (ts != null)
                        {
                            ModCompatManager.UntagTrain(ts);
                        }
                    }

                    if (Main.IsDebugLoggingEnabled)
                        Main.LogDebug(string.Format("[TrainDespawner] Despawned entire AI consist (Lead: {0}, Total Cars: {1}).", leadId, carCount));
                }

                return true;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrainDespawner.DespawnTrain(AIEngineer): {0}", ex));
                return false;
            }
        }

        private static IEnumerator TimeSlicedDespawnRoutine(List<TrainCar> carsToDelete, HashSet<Trainset> affectedTrainsets, string leadId, int carCount)
        {
            List<TrainCar> slice = new List<TrainCar>(TimeSlicedDespawnCarsPerFrame);
            for (int i = 0; i < carsToDelete.Count; i += TimeSlicedDespawnCarsPerFrame)
            {
                if (!Main.IsWorldFullyLoaded || CarSpawner.Instance == null)
                {
                    // World unloading mid-coroutine: flush remaining cars immediately
                    slice.Clear();
                    for (int j = i; j < carsToDelete.Count; j++)
                    {
                        if (carsToDelete[j] != null) slice.Add(carsToDelete[j]);
                    }
                    if (slice.Count > 0)
                    {
                        SafeDeleteCars(slice, true);
                    }
                    break;
                }

                slice.Clear();
                int end = Mathf.Min(i + TimeSlicedDespawnCarsPerFrame, carsToDelete.Count);
                for (int j = i; j < end; j++)
                {
                    var c = carsToDelete[j];
                    if (c != null) slice.Add(c);
                }

                if (slice.Count > 0)
                {
                    SafeDeleteCars(slice, true);
                }

                if (end < carsToDelete.Count)
                {
                    yield return null;
                }
            }

            if (affectedTrainsets != null)
            {
                foreach (var ts in affectedTrainsets)
                {
                    if (ts != null)
                    {
                        ModCompatManager.UntagTrain(ts);
                    }
                }
            }

            if (Main.IsDebugLoggingEnabled)
                Main.LogDebug(string.Format("[TrainDespawner] Time-sliced despawn completed for AI consist (Lead: {0}, Total Cars: {1}).", leadId, carCount));
        }

        /// <summary>
        /// Multi-tier robust deletion of train cars.
        /// Filters null and destroyed objects, attempts batch DeleteTrainCars, and falls back to per-car
        /// deletion or direct GameObject destruction if CarSpawner encounters null logicCars.
        /// </summary>
        // CRITICAL INVARIANT [FLT-04]: Multi-tier safe deletion guarding against corrupted logicCar NREs (See GRAVEYARD.md)
        public static void SafeDeleteCars(List<TrainCar> carsToDelete, bool forceInstant = true)
        {
            if (carsToDelete == null || carsToDelete.Count == 0 || CarSpawner.Instance == null)
                return;

            // 1. Sanitize list and pre-capture GUIDs/IDs before CarSpawner nulls car.logicCar in PrepareForDestroy
            List<TrainCar> validCars = new List<TrainCar>();
            List<string> preCapturedGuids = new List<string>();
            List<string> preCapturedIds = new List<string>();
            for (int i = 0; i < carsToDelete.Count; i++)
            {
                var c = carsToDelete[i];
                if (c != null && c.gameObject != null && !validCars.Contains(c))
                {
                    validCars.Add(c);
                    preCapturedGuids.Add(ModCompatManager.GetCarGuid(c));
                    preCapturedIds.Add(ModCompatManager.GetCarId(c));
                }
            }

            if (validCars.Count == 0) return;

            // 2. Attempt batch deletion while AITrafficCarMarker and s_aiCars tags are still active
            bool batchSucceeded = false;
            try
            {
                CarSpawner.Instance.DeleteTrainCars(validCars, forceInstantDestroy: forceInstant);
                batchSucceeded = true;
            }
            catch (Exception ex)
            {
                if (Main.IsWorldFullyLoaded)
                {
                    Main.LogWarning(string.Format("[TrainDespawner] Batch DeleteTrainCars threw {0}: {1}. Falling back to per-car deletion.", ex.GetType().Name, ex.Message));
                }
                else if (Main.IsDebugLoggingEnabled)
                {
                    Main.LogDebug(string.Format("[TrainDespawner] Teardown batch deletion threw {0}: {1}. Falling back to per-car deletion.", ex.GetType().Name, ex.Message));
                }
            }

            if (batchSucceeded)
            {
                ModCompatManager.UntagCars(validCars, preCapturedGuids, preCapturedIds);
            }

            // 3. Fallback to per-car deletion if batch failed
            if (!batchSucceeded)
            {
                for (int i = 0; i < validCars.Count; i++)
                {
                    var car = validCars[i];
                    if (car == null || car.gameObject == null) continue;

                    try
                    {
                        if (car.logicCar != null)
                        {
                            CarSpawner.Instance.DeleteCar(car);
                        }
                        else
                        {
                            // Missing logicCar causes vanilla NRE; destroy GameObject directly
                            UnityEngine.Object.Destroy(car.gameObject);
                        }
                    }
                    catch (Exception singleEx)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        {
                            Main.ModEntry.Logger.Warning(string.Format("[TrainDespawner] Single CarSpawner.DeleteCar failed for car '{0}' ({1}). Destroying GameObject directly.", ModCompatManager.GetCarId(car), singleEx.Message));
                        }
                        if (car.gameObject != null)
                        {
                            UnityEngine.Object.Destroy(car.gameObject);
                        }
                    }
                }

                ModCompatManager.UntagCars(validCars, preCapturedGuids, preCapturedIds);
            }
        }

        /// <summary>
        /// Checks whether an AI train is actively traveling towards the player, or has an upcoming route
        /// segment / destination that brings it into the player's vicinity.
        /// </summary>
        public static bool IsTrainHeadingTowardsPlayer(AIEngineer engineer, Vector3 playerPos)
        {
            if (engineer == null || engineer.TrainCar == null || playerPos == Vector3.zero)
                return false;

            Vector3 trainPos = engineer.TrainCar.transform.position;
            float currentDistToPlayer = Vector3.Distance(trainPos, playerPos);

            // 1. If the train is within 1000m of the player, it is in immediate player proximity
            if (currentDistToPlayer <= 1000f)
            {
                return true;
            }

            // 2. Check instantaneous movement direction
            Vector3 toPlayer = (playerPos - trainPos).normalized;
            Vector3 locoForward = engineer.TrainCar.transform.forward;
            float speedKmh = engineer.CurrentSpeedKmh;
            Vector3 moveDir = (Mathf.Abs(speedKmh) > 1.0f)
                ? (speedKmh > 0 ? locoForward : -locoForward)
                : Vector3.zero;

            if (moveDir != Vector3.zero && Vector3.Dot(moveDir, toPlayer) > 0.2f)
            {
                return true;
            }

            // 3. Check if destination station / track is closer to the player than the train's current position
            if (engineer.CurrentPath != null && engineer.CurrentPath.Tracks != null && engineer.CurrentPath.Tracks.Count > 0)
            {
                var tracks = engineer.CurrentPath.Tracks;
                var destTrack = tracks[tracks.Count - 1];
                if (destTrack != null)
                {
                    Vector3 destPos = (destTrack.curve != null) ? destTrack.curve.GetPointAt(0.5f) : destTrack.transform.position;
                    float destDistToPlayer = Vector3.Distance(destPos, playerPos);
                    if (destDistToPlayer < currentDistToPlayer - 100f)
                    {
                        return true; // Journey is heading towards player
                    }
                }

                // 4. Check remaining route track waypoints from current track index forward
                int startIdx = Mathf.Clamp(engineer.CurrentPathTrackIndex, 0, tracks.Count - 1);
                for (int i = startIdx; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    if (t == null) continue;

                    Vector3 tPos = (t.curve != null) ? t.curve.GetPointAt(0.5f) : t.transform.position;
                    float trackDist = Vector3.Distance(tPos, playerPos);
                    // If route waypoint passes closer than current distance
                    if (trackDist < currentDistToPlayer - 150f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
