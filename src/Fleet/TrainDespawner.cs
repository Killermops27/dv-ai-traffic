using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Compat;
using AITraffic.Driver;

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
        /// <param name="trainset">The trainset to evaluate.</param>
        /// <param name="minDistance">Minimum absolute distance required between all cars and the player (even when out of view).</param>
        /// <param name="frustumDistance">Distance required when the train is within the player's camera view frustum.</param>
        /// <returns>True if the trainset is safe to despawn.</returns>
        public static bool CanDespawnSafely(Trainset trainset, float minDistance = DefaultTerminusSafeDespawnDistance, float frustumDistance = DefaultTerminusFrustumDistance)
        {
            if (trainset == null || trainset.cars == null || trainset.cars.Count == 0)
                return true;

            Transform playerTransform = PlayerManager.PlayerTransform;
            if (playerTransform == null)
                return true; // No player active, safe to despawn

            Vector3 playerPos = playerTransform.position;
            TrainCar playerCar = PlayerManager.Car;

            // 1. Check if the player is riding, coupled to, or standing inside any car in this trainset
            if (playerCar != null && trainset.cars.Contains(playerCar))
            {
                return false;
            }

            // Also check proximity to any car (e.g. player standing on walkway/roof or immediately next to train)
            for (int i = 0; i < trainset.cars.Count; i++)
            {
                var car = trainset.cars[i];
                if (car == null) continue;

                // Absolute minimum safety distance: if player is within 30m of any car, never despawn
                if ((car.transform.position - playerPos).sqrMagnitude < 900f) // 30m
                {
                    return false;
                }
            }

            // 2. Check distance from player to every car in the trainset
            float minDistanceSq = minDistance * minDistance;
            for (int i = 0; i < trainset.cars.Count; i++)
            {
                var car = trainset.cars[i];
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
                for (int i = 0; i < trainset.cars.Count; i++)
                {
                    var car = trainset.cars[i];
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
        /// Despawns an AI train by its lead engineer controller.
        /// </summary>
        public static bool DespawnTrain(AIEngineer engineer, bool forceInstant = true)
        {
            if (engineer == null || engineer.TrainCar == null || engineer.TrainCar.trainset == null)
                return false;

            return DespawnTrain(engineer.TrainCar.trainset, forceInstant);
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

            // 1. If the train is already within 2500m of player, it is in active encounter range
            if (currentDistToPlayer <= 2500f)
            {
                return true;
            }

            // 2. Check if destination station / track is closer to the player than the train's current position
            if (engineer.CurrentPath != null && engineer.CurrentPath.Tracks != null && engineer.CurrentPath.Tracks.Count > 0)
            {
                var tracks = engineer.CurrentPath.Tracks;
                var destTrack = tracks[tracks.Count - 1];
                if (destTrack != null)
                {
                    float destDistToPlayer = Vector3.Distance(destTrack.transform.position, playerPos);
                    if (destDistToPlayer < currentDistToPlayer)
                    {
                        return true; // Journey is heading towards player
                    }
                }

                // 3. Check remaining route track waypoints
                int startIdx = Mathf.Clamp(engineer.CurrentPathTrackIndex, 0, tracks.Count - 1);
                for (int i = startIdx; i < tracks.Count; i++)
                {
                    var t = tracks[i];
                    if (t == null) continue;

                    float trackDist = Vector3.Distance(t.transform.position, playerPos);
                    // If route waypoint passes within 2200m of player or comes substantially closer than current position
                    if (trackDist <= 2200f || trackDist < currentDistToPlayer * 0.75f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
