using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV.Logic.Job;
using DV.ThingTypes;
using DV.Simulation.Cars;
using AITraffic.Fleet;
using AITraffic.Driver;
using AITraffic.Compat;

namespace AITraffic.Core
{
    /// <summary>
    /// Represents an active job assignment being executed by an AI locomotive engineer.
    /// </summary>
    public class AIJobAssignment
    {
        public Job Job { get; private set; }
        public StationController OriginStation { get; private set; }
        public StationController DestinationStation { get; private set; }
        public AIEngineer Engineer { get; private set; }
        public List<TrainCar> Cars { get; private set; }
        public RailTrack DestinationTrack { get; private set; }
        public float StartTime { get; private set; }
        public bool IsCompleted { get; internal set; }

        public AIJobAssignment(Job job, StationController origin, StationController dest, AIEngineer engineer, List<TrainCar> cars, RailTrack destTrack)
        {
            Job = job;
            OriginStation = origin;
            DestinationStation = dest;
            Engineer = engineer;
            Cars = cars ?? new List<TrainCar>();
            DestinationTrack = destTrack;
            StartTime = Time.time;
            IsCompleted = false;
        }
    }

    /// <summary>
    /// Tier 2 autonomous real-job operator:
    /// Scans StationController.allStations for available jobs, claims them, locates rolling stock,
    /// couples locomotives, executes mainline hauls, and triggers task completion within the persistent economy.
    /// </summary>
    public class JobOperator
    {
        private static JobOperator s_instance;
        public static JobOperator Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new JobOperator();
                }
                return s_instance;
            }
        }

        private readonly List<AIJobAssignment> _activeAssignments = new List<AIJobAssignment>();
        public List<AIJobAssignment> ActiveAssignments
        {
            get { return _activeAssignments; }
        }

        private readonly HashSet<string> _claimedJobIds = new HashSet<string>();

        public JobOperator()
        {
        }

        /// <summary>
        /// Scans all stations for available unreserved jobs suitable for AI execution.
        /// </summary>
        /// <param name="stationFilter">Optional specific station to scan.</param>
        /// <returns>A list of eligible Job instances.</returns>
        public List<Job> ScanAvailableJobs(StationController stationFilter = null)
        {
            List<Job> eligibleJobs = new List<Job>();

            if (StationController.allStations == null || StationController.allStations.Count == 0)
                return eligibleJobs;

            try
            {
                List<StationController> stationsToScan = stationFilter != null
                    ? new List<StationController> { stationFilter }
                    : StationController.allStations;

                for (int sIdx = 0; sIdx < stationsToScan.Count; sIdx++)
                {
                    var station = stationsToScan[sIdx];
                    if (station == null || station.logicStation == null || station.logicStation.availableJobs == null)
                        continue;

                    var availableJobs = station.logicStation.availableJobs;
                    for (int jIdx = 0; jIdx < availableJobs.Count; jIdx++)
                    {
                        var job = availableJobs[jIdx];
                        if (job == null) continue;

                        // Check job state
                        if (job.State != JobState.Available)
                            continue;

                        // Check if already claimed by AI
                        if (!string.IsNullOrEmpty(job.ID) && _claimedJobIds.Contains(job.ID))
                            continue;

                        // Supported Job types for autonomous hauling
                        if (job.jobType == JobType.Transport ||
                            job.jobType == JobType.EmptyHaul ||
                            job.jobType == JobType.ShuntingLoad ||
                            job.jobType == JobType.ShuntingUnload)
                        {
                            eligibleJobs.Add(job);
                        }
                    }
                }

                // Shuffle and sort by player proximity
                if (eligibleJobs.Count > 1)
                {
                    var rng = new System.Random();
                    for (int i = eligibleJobs.Count - 1; i > 0; i--)
                    {
                        int swapIdx = rng.Next(0, i + 1);
                        var temp = eligibleJobs[i];
                        eligibleJobs[i] = eligibleJobs[swapIdx];
                        eligibleJobs[swapIdx] = temp;
                    }

                    Vector3 playerPos = PlayerManager.PlayerTransform != null ? PlayerManager.PlayerTransform.position : Vector3.zero;
                    if (playerPos != Vector3.zero)
                    {
                        eligibleJobs.Sort((jA, jB) =>
                        {
                            StationController stA = FindStationControllerForYardId(jA.chainData != null ? jA.chainData.chainOriginYardId : null);
                            StationController stB = FindStationControllerForYardId(jB.chainData != null ? jB.chainData.chainOriginYardId : null);

                            float dA = stA != null ? Vector3.Distance(stA.transform.position, playerPos) : 99999f;
                            float dB = stB != null ? Vector3.Distance(stB.transform.position, playerPos) : 99999f;

                            float scoreA = (dA < 350f) ? 99999f : (dA <= 2500f ? dA : 2500f + (dA - 2500f) * 2f);
                            float scoreB = (dB < 350f) ? 99999f : (dB <= 2500f ? dB : 2500f + (dB - 2500f) * 2f);
                            return scoreA.CompareTo(scoreB);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error scanning available jobs: {0}", ex));
            }

            return eligibleJobs;
        }

        /// <summary>
        /// Claims an available job, locates its rolling stock in the yard, couples an AI locomotive,
        /// and dispatches the train toward the destination.
        /// </summary>
        /// <param name="job">The job to execute.</param>
        /// <returns>The created AIJobAssignment, or null if claiming/dispatch failed.</returns>
        public AIJobAssignment ClaimAndDispatchJob(Job job)
        {
            if (job == null || job.State != JobState.Available)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning("ClaimAndDispatchJob: Job is null or not in Available state.");
                return null;
            }

            try
            {
                // 1. Locate stations and task parameters
                StationController originStation = FindStationControllerForYardId(job.chainData != null ? job.chainData.chainOriginYardId : null);
                StationController destStation = FindStationControllerForYardId(job.chainData != null ? job.chainData.chainDestinationYardId : null);

                // Find primary transport/shunting task data
                TaskData primaryTask = FindPrimaryTaskData(job);

                // 2. Locate TrainCars matching the job's logic cars
                List<TrainCar> jobCars = new List<TrainCar>();
                if (primaryTask != null && primaryTask.cars != null && CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
                {
                    var allCars = CarSpawner.Instance.AllCars;
                    for (int cIdx = 0; cIdx < primaryTask.cars.Count; cIdx++)
                    {
                        var logicCar = primaryTask.cars[cIdx];
                        if (logicCar == null) continue;

                        for (int aIdx = 0; aIdx < allCars.Count; aIdx++)
                        {
                            var worldCar = allCars[aIdx];
                            if (worldCar != null && (worldCar.logicCar == logicCar || worldCar.ID == logicCar.ID))
                            {
                                jobCars.Add(worldCar);
                                break;
                            }
                        }
                    }
                }

                if (jobCars.Count == 0)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("ClaimAndDispatchJob: Could not locate rolling stock in world for job '{0}'.", job.ID));
                    return null;
                }

                // 3. Claim job within game logic
                job.TakeJob(false);
                if (!string.IsNullOrEmpty(job.ID))
                {
                    _claimedJobIds.Add(job.ID);
                }

                // 4. Resolve destination RailTrack
                RailTrack destRailTrack = null;
                if (primaryTask != null && primaryTask.destinationTrack != null)
                {
                    destRailTrack = MapLogicTrackToRailTrack(primaryTask.destinationTrack);
                }

                // Fallback destination track if null
                if (destRailTrack == null && destStation != null && destStation.AllStationTracks != null && destStation.AllStationTracks.Count > 0)
                {
                    destRailTrack = destStation.AllStationTracks[0];
                }

                // 5. Check if consist already has a supported locomotive, or attach/spawn one
                TrainCar leadLoco = null;
                for (int i = 0; i < jobCars.Count; i++)
                {
                    if (jobCars[i] != null && TrainSpawner.IsSupportedAILocomotive(jobCars[i]))
                    {
                        leadLoco = jobCars[i];
                        break;
                    }
                }

                if (leadLoco == null)
                {
                    // Select appropriate locomotive based on car count
                    TrainCarType locoType = jobCars.Count > 10 ? TrainCarType.LocoDiesel : (jobCars.Count > 5 ? TrainCarType.LocoDH4 : TrainCarType.LocoShunter);
                    TrainCarLivery locoLivery = ConsistDefinitions.GetLivery(locoType);

                    RailTrack startTrack = null;
                    if (primaryTask != null && primaryTask.startTrack != null)
                    {
                        startTrack = MapLogicTrackToRailTrack(primaryTask.startTrack);
                    }
                    if (startTrack == null && jobCars[0].FrontBogie != null)
                    {
                        startTrack = jobCars[0].FrontBogie.track;
                    }

                    if (startTrack != null && locoLivery != null)
                    {
                        leadLoco = CarSpawner.Instance.SpawnCar(
                            carToSpawn: locoLivery.prefab,
                            track: startTrack,
                            position: jobCars[0].transform.position + jobCars[0].transform.forward * 12f,
                            forward: jobCars[0].transform.forward,
                            playerSpawnedCar: false,
                            uniqueCar: false
                        );
                    }
                }

                if (leadLoco == null)
                {
                    leadLoco = jobCars[0];
                }

                // Connect couplers, air hoses, release brakes
                List<TrainCar> fullConsist = new List<TrainCar>();
                if (!jobCars.Contains(leadLoco))
                {
                    fullConsist.Add(leadLoco);
                }
                fullConsist.AddRange(jobCars);

                TrainSpawner.ConfigureConsistCouplers(fullConsist);

                for (int i = 0; i < fullConsist.Count; i++)
                {
                    var car = fullConsist[i];
                    if (car == null) continue;
                    var controls = car.GetComponent<BaseControlsOverrider>();
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(0f);
                    }
                }

                // 6. Tag trainset for AI traffic
                if (leadLoco.trainset != null)
                {
                    ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
                }

                // 7. Attach and configure AIEngineer
                AIEngineer engineer = leadLoco.gameObject.GetComponent<AIEngineer>();
                if (engineer == null)
                {
                    engineer = leadLoco.gameObject.AddComponent<AIEngineer>();
                }

                string jobOrig = (originStation != null && originStation.stationInfo != null && !string.IsNullOrEmpty(originStation.stationInfo.Name)) 
                    ? originStation.stationInfo.Name 
                    : (originStation != null && originStation.stationInfo != null ? originStation.stationInfo.YardID : "Yard");
                string jobDest = (destStation != null && destStation.stationInfo != null && !string.IsNullOrEmpty(destStation.stationInfo.Name)) 
                    ? destStation.stationInfo.Name 
                    : (destStation != null && destStation.stationInfo != null ? destStation.stationInfo.YardID : "Yard");
                string jobYardId = destStation != null && destStation.stationInfo != null ? destStation.stationInfo.YardID : "";

                engineer.OriginStationName = jobOrig;
                engineer.DestinationStationName = string.Format("{0} [{1}]", jobDest, jobYardId);
                engineer.DestinationTrackName = destRailTrack != null ? destRailTrack.name : string.Empty;

                // 8. Create assignment record
                AIJobAssignment assignment = new AIJobAssignment(job, originStation, destStation, engineer, fullConsist, destRailTrack);
                _activeAssignments.Add(assignment);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[JobOperator] Dispatched AI Job '{0}' ({1} -> {2}, Cars: {3}, Lead Loco: {4}).",
                        job.ID,
                        originStation != null ? originStation.stationInfo.YardID : "Unknown",
                        destStation != null ? destStation.stationInfo.YardID : "Unknown",
                        fullConsist.Count,
                        leadLoco.ID));

                return assignment;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error claiming and dispatching job '{0}': {1}", job != null ? job.ID : "null", ex));
                return null;
            }
        }

        /// <summary>
        /// Updates active job runs, monitors arrival at destination tracks, and completes tasks/jobs.
        /// </summary>
        public void UpdateActiveAssignments(float deltaTime)
        {
            for (int i = _activeAssignments.Count - 1; i >= 0; i--)
            {
                var assignment = _activeAssignments[i];
                if (assignment == null || assignment.IsCompleted)
                {
                    _activeAssignments.RemoveAt(i);
                    continue;
                }

                var job = assignment.Job;
                var engineer = assignment.Engineer;

                if (job == null || engineer == null || engineer.TrainCar == null)
                {
                    _activeAssignments.RemoveAt(i);
                    continue;
                }

                // Check if train has arrived at destination
                if (assignment.DestinationTrack != null)
                {
                    RailTrack currentTrack = null;
                    if (engineer.TrainCar.FrontBogie != null) currentTrack = engineer.TrainCar.FrontBogie.track;
                    else if (engineer.TrainCar.RearBogie != null) currentTrack = engineer.TrainCar.RearBogie.track;

                    bool onDestinationTrack = (currentTrack == assignment.DestinationTrack);
                    bool isStopped = engineer.CurrentSpeedKmh < 1.0f;

                    if (onDestinationTrack && isStopped)
                    {
                        CompleteAssignment(assignment);
                        _activeAssignments.RemoveAt(i);
                    }
                }
            }
        }

        private void CompleteAssignment(AIJobAssignment assignment)
        {
            if (assignment == null || assignment.Job == null) return;

            try
            {
                var job = assignment.Job;

                // Complete tasks
                if (job.tasks != null)
                {
                    for (int i = 0; i < job.tasks.Count; i++)
                    {
                        var task = job.tasks[i];
                        if (task != null && task.state != TaskState.Done)
                        {
                            task.state = TaskState.Done;
                        }
                    }
                }

                // Complete the job
                job.CompleteJob();
                assignment.IsCompleted = true;
                if (!string.IsNullOrEmpty(job.ID))
                {
                    _claimedJobIds.Remove(job.ID);
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[JobOperator] AI completed job '{0}'. Payout registered in persistent economy.", job.ID));
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error completing assignment for job '{0}': {1}", assignment.Job.ID, ex));
            }
        }

        #region Helper Methods

        private static StationController FindStationControllerForYardId(string yardId)
        {
            if (string.IsNullOrEmpty(yardId) || StationController.allStations == null)
                return null;

            for (int i = 0; i < StationController.allStations.Count; i++)
            {
                var sc = StationController.allStations[i];
                if (sc != null && sc.stationInfo != null &&
                    string.Equals(sc.stationInfo.YardID, yardId, StringComparison.OrdinalIgnoreCase))
                {
                    return sc;
                }
            }

            return null;
        }

        private static RailTrack MapLogicTrackToRailTrack(DV.Logic.Job.Track logicTrack)
        {
            if (logicTrack == null) return null;

            if (RailTrackRegistry.LogicToRailTrack != null)
            {
                RailTrack railTrack;
                if (RailTrackRegistry.LogicToRailTrack.TryGetValue(logicTrack, out railTrack))
                {
                    return railTrack;
                }
            }

            return null;
        }

        private static TaskData FindPrimaryTaskData(Job job)
        {
            if (job == null || job.tasks == null) return null;

            for (int i = 0; i < job.tasks.Count; i++)
            {
                var task = job.tasks[i];
                if (task == null) continue;

                TaskData data = task.GetTaskData();
                if (data == null) continue;

                if (data.type == TaskType.Transport)
                {
                    return data;
                }

                if (data.nestedTasks != null)
                {
                    for (int j = 0; j < data.nestedTasks.Count; j++)
                    {
                        var subTask = data.nestedTasks[j];
                        if (subTask == null) continue;

                        TaskData subData = subTask.GetTaskData();
                        if (subData != null && subData.type == TaskType.Transport)
                        {
                            return subData;
                        }
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
