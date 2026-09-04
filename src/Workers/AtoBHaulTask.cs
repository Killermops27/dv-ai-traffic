using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Driver;

namespace AITraffic.Workers
{
    public enum HaulTaskStatus
    {
        Preparing,
        EnRoute,
        Arrived,
        Cancelled,
        Failed
    }

    /// <summary>
    /// Represents an active or completed station-to-station mainline haul executed by an employed AI driver.
    /// </summary>
    public class AtoBHaulTask
    {
        public string Id { get; private set; }
        public TrainCar LeadLocomotive { get; private set; }
        public List<TrainCar> Consist { get; private set; }
        public StationController OriginStation { get; private set; }
        public RailTrack OriginTrack { get; private set; }
        public StationController DestinationStation { get; private set; }
        public RailTrack DestinationTrack { get; private set; }
        public bool IsAutoSelectedTrack { get; private set; }
        public double HiringFee { get; private set; }
        public float StartTime { get; private set; }
        public float CompletedTime { get; internal set; }
        public float RouteDistance { get; private set; }
        public AIEngineer Engineer { get; internal set; }
        public HaulTaskStatus Status { get; internal set; }
        public string StatusMessage { get; internal set; }

        public float ElapsedSeconds
        {
            get { return Status == HaulTaskStatus.EnRoute ? (Time.time - StartTime) : (CompletedTime - StartTime); }
        }

        public float RemainingDistance
        {
            get { return Engineer != null ? Engineer.DistanceToDestination : 0f; }
        }

        public float CurrentSpeedKmh
        {
            get { return Engineer != null ? Engineer.CurrentSpeedKmh : 0f; }
        }

        public AtoBHaulTask(
            string id,
            TrainCar leadLoco,
            List<TrainCar> consist,
            StationController originStation,
            RailTrack originTrack,
            StationController destStation,
            RailTrack destTrack,
            bool isAutoTrack,
            double fee,
            float routeDistance,
            AIEngineer engineer)
        {
            Id = id ?? Guid.NewGuid().ToString().Substring(0, 8);
            LeadLocomotive = leadLoco;
            Consist = consist ?? new List<TrainCar>();
            OriginStation = originStation;
            OriginTrack = originTrack;
            DestinationStation = destStation;
            DestinationTrack = destTrack;
            IsAutoSelectedTrack = isAutoTrack;
            HiringFee = fee;
            StartTime = Time.time;
            RouteDistance = routeDistance;
            Engineer = engineer;
            Status = HaulTaskStatus.EnRoute;
            StatusMessage = "En route to " + (destStation != null && destStation.stationInfo != null ? destStation.stationInfo.Name : "destination");
        }
    }
}
