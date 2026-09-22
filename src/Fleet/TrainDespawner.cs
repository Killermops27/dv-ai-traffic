using System;
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
        public static bool CanDespawnSafely(AIEngineer engineer, float minDistance = DefaultTerminusSafeDespawnDistance, float frustumDistance = DefaultTerminusFrustumDistance)
        {
            if (engineer == null)
                return true;

            List<TrainCar> allCars = new List<TrainCar>();
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
                        Main.ModEntry.Logger.Warning(string.Format("[TrainDespawner] Aborting despawn of trainset: car '{0}' is part of an AI worker task or player consist!", car.ID));
                    return false;
                }
            }

            try
            {
                int carCount = trainset.cars.Count;
                string leadId = trainset.firstCar != null ? trainset.firstCar.ID : "Unknown";

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
                        AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(engineer);
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

                // 2. Untag cars from AI registry to prevent save leaks
                ModCompatManager.UntagTrain(trainset);

                // 3. Create a snapshot copy of cars list for deletion
                List<TrainCar> carsToDelete = new List<TrainCar>(trainset.cars);

                // 4. Delete cars using CarSpawner
                CarSpawner.Instance.DeleteTrainCars(carsToDelete, forceInstantDestroy: forceInstant);

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
        public static bool DespawnTrain(AIEngineer engineer, bool forceInstant = true)
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
                            Main.ModEntry.Logger.Warning(string.Format("[TrainDespawner] Aborting despawn of train: car '{0}' is part of an AI worker task or player consist!", car.ID));
                        return false;
                    }
                }

                string leadId = engineer.TrainCar != null ? engineer.TrainCar.ID : "Unknown";
                int carCount = carsToDelete.Count;

                // 2. Untag all affected Trainsets and cleanup markers/reservations
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

                    if (car.gameObject != null)
                    {
                        var marker = car.gameObject.GetComponent<AITrafficCarMarker>();
                        if (marker != null)
                        {
                            UnityEngine.Object.Destroy(marker);
                        }
                    }
                }

                foreach (var ts in affectedTrainsets)
                {
                    if (ts != null)
                    {
                        ModCompatManager.UntagTrain(ts);
                    }
                }

                // 3. Delete cars using CarSpawner
                CarSpawner.Instance.DeleteTrainCars(carsToDelete, forceInstantDestroy: forceInstant);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[TrainDespawner] Despawned entire AI consist (Lead: {0}, Total Cars: {1}).", leadId, carCount));

                return true;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrainDespawner.DespawnTrain(AIEngineer): {0}", ex));
                return false;
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
