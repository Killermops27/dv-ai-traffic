using System;
using System.Collections.Generic;
using UnityEngine;
using AITraffic.Driver;
using AITraffic.Navigation;

namespace AITraffic.Workers
{
    public enum HaulTaskStatus
    {
        Preparing,
        EnRoute,
        WaitingAtIntermediate,
        Arrived,
        Cancelled,
        Failed
    }

    public enum ItineraryLegStatus
    {
        Pending,
        EnRoute,
        Completed,
        Cancelled,
        Failed
    }

    /// <summary>
    /// Represents a single station-to-station leg within a worker haul itinerary.
    /// </summary>
    public class ItineraryLeg
    {
        public int LegIndex { get; internal set; }
        public StationController OriginStation { get; internal set; }
        public RailTrack OriginTrack { get; internal set; }
        public StationController DestinationStation { get; internal set; }
        public RailTrack DestinationTrack { get; internal set; }
        public bool IsAutoSelectedTrack { get; internal set; }
        public float RouteDistance { get; internal set; }
        public double LegFee { get; internal set; }
        public float DwellSeconds { get; internal set; }
        public ItineraryLegStatus Status { get; internal set; }

        public ItineraryLeg(
            int legIndex,
            StationController originStation,
            RailTrack originTrack,
            StationController destStation,
            RailTrack destTrack,
            bool isAutoTrack,
            float routeDistance,
            double legFee,
            float dwellSeconds = 5.0f,
            ItineraryLegStatus status = ItineraryLegStatus.Pending)
        {
            LegIndex = legIndex;
            OriginStation = originStation;
            OriginTrack = originTrack;
            DestinationStation = destStation;
            DestinationTrack = destTrack;
            IsAutoSelectedTrack = isAutoTrack;
            RouteDistance = routeDistance;
            LegFee = legFee;
            DwellSeconds = dwellSeconds;
            Status = status;
        }
    }

    /// <summary>
    /// Represents an active or completed single-leg or multi-leg mainline haul executed by an employed AI driver.
    /// </summary>
    // CRITICAL INVARIANT [WRK-07]: Unified Multi-Leg Itinerary & Pro-Rated Refund Ledger (See GRAVEYARD.md)
    public class AtoBHaulTask
    {
        private readonly List<ItineraryLeg> _legs = new List<ItineraryLeg>();

        public string Id { get; private set; }
        public TrainCar LeadLocomotive { get; private set; }
        public List<TrainCar> Consist { get; private set; }
        public List<ItineraryLeg> Legs { get { return _legs; } }
        public int CurrentLegIndex { get; internal set; }

        public ItineraryLeg CurrentLeg
        {
            get
            {
                if (_legs == null || _legs.Count == 0) return null;
                int idx = Mathf.Clamp(CurrentLegIndex, 0, _legs.Count - 1);
                return _legs[idx];
            }
        }

        public bool IsMultiLeg
        {
            get { return _legs != null && _legs.Count > 1; }
        }

        public bool HasRemainingLegs
        {
            get { return _legs != null && CurrentLegIndex < _legs.Count - 1; }
        }

        public StationController OriginStation
        {
            get { return CurrentLeg != null ? CurrentLeg.OriginStation : null; }
        }

        public RailTrack OriginTrack
        {
            get { return CurrentLeg != null ? CurrentLeg.OriginTrack : null; }
        }

        public StationController DestinationStation
        {
            get { return CurrentLeg != null ? CurrentLeg.DestinationStation : null; }
        }

        public RailTrack DestinationTrack
        {
            get { return CurrentLeg != null ? CurrentLeg.DestinationTrack : null; }
        }

        public bool IsAutoSelectedTrack
        {
            get { return CurrentLeg != null && CurrentLeg.IsAutoSelectedTrack; }
        }

        public float RouteDistance
        {
            get { return CurrentLeg != null ? CurrentLeg.RouteDistance : 0f; }
        }

        public double HiringFee { get; private set; }
        public double TotalHiringFee { get { return HiringFee; } }
        public double BaseFeeCharged { get; private set; }
        public double RefundedAmount { get; internal set; }
        public float TotalRouteDistance { get; private set; }
        public float StartTime { get; private set; }
        public float CompletedTime { get; internal set; }
        public float IntermediateDwellExpireTime { get; internal set; }

        // CRITICAL INVARIANT [WRK-12]: Non-blocking waypoint exit search state. Only WorkerManager mutates it; CancelTask and every terminal path must call ClearAdvanceSearch().
        public System.Threading.Tasks.Task<RailPath> AdvancePathTask { get; internal set; }
        public List<RailTrack> AdvanceCandidates { get; internal set; }
        public int AdvanceCandidateIndex { get; internal set; }
        internal int AdvanceLegIndex { get; set; }
        internal RailTrack AdvanceOriginTrack { get; set; }
        internal RailTrack AdvanceTargetTrack { get; set; }
        internal bool AdvanceForward { get; set; }
        internal bool AdvanceSpurPhase { get; set; }
        internal bool AdvanceSpurResolved { get; set; }
        internal bool AdvanceSpurAvailable { get; set; }
        internal Junction AdvanceSpurSwitch { get; set; }
        internal RailTrack AdvanceSpurStem { get; set; }
        internal bool AdvanceSpurStemForward { get; set; }

        internal void ClearAdvanceSearch()
        {
            AdvancePathTask = null;
            AdvanceCandidates = null;
            AdvanceCandidateIndex = 0;
            AdvanceLegIndex = -1;
            AdvanceOriginTrack = null;
            AdvanceTargetTrack = null;
            AdvanceForward = true;
            AdvanceSpurPhase = false;
            AdvanceSpurResolved = false;
            AdvanceSpurAvailable = false;
            AdvanceSpurSwitch = null;
            AdvanceSpurStem = null;
            AdvanceSpurStemForward = true;
        }
        public Vector3 InitialWorldPosition { get; private set; }
        public AIEngineer Engineer { get; internal set; }
        public HaulTaskStatus Status { get; internal set; }
        public string StatusMessage { get; internal set; }

        public float ElapsedSeconds
        {
            get
            {
                return (Status == HaulTaskStatus.EnRoute || Status == HaulTaskStatus.WaitingAtIntermediate)
                    ? (Time.time - StartTime)
                    : (CompletedTime - StartTime);
            }
        }

        /// <summary>
        /// True when a 30s blocked-track toast has been broadcast for the current hold event,
        /// preventing duplicate toast spam until the hold clears or the player selects an action.
        /// </summary>
        public bool HasBroadcastBlockedAlert { get; set; }

        public float CurrentSpeedKmh
        {
            get { return Engineer != null ? Engineer.CurrentSpeedKmh : 0f; }
        }

        public float RemainingDistance
        {
            get { return Engineer != null ? Engineer.DistanceToDestination : 0f; }
        }

        public bool HasPendingBlockedAlert
        {
            get { return Engineer != null && Engineer.HasPendingWorkerBlockedAlert; }
        }

        public bool IsInfiniteIntermediateHold
        {
            get
            {
                return Status == HaulTaskStatus.WaitingAtIntermediate && float.IsPositiveInfinity(IntermediateDwellExpireTime);
            }
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
            AIEngineer engineer,
            double baseFeeCharged = 300.0)
            : this(
                id,
                leadLoco,
                consist,
                new List<ItineraryLeg>
                {
                    new ItineraryLeg(
                        0,
                        originStation,
                        originTrack,
                        destStation,
                        destTrack,
                        isAutoTrack,
                        routeDistance,
                        fee,
                        5.0f,
                        ItineraryLegStatus.EnRoute)
                },
                fee,
                baseFeeCharged,
                engineer)
        {
        }

        public AtoBHaulTask(
            string id,
            TrainCar leadLoco,
            List<TrainCar> consist,
            List<ItineraryLeg> legs,
            double totalFee,
            double baseFeeCharged,
            AIEngineer engineer)
        {
            Id = id ?? Guid.NewGuid().ToString().Substring(0, 8);
            LeadLocomotive = leadLoco;
            Consist = consist ?? new List<TrainCar>();
            if (legs != null)
            {
                for (int i = 0; i < legs.Count; i++)
                {
                    var leg = legs[i];
                    if (leg != null)
                    {
                        leg.LegIndex = _legs.Count;
                        _legs.Add(leg);
                    }
                }
            }

            CurrentLegIndex = 0;
            if (_legs.Count > 0)
            {
                _legs[0].Status = ItineraryLegStatus.EnRoute;
            }

            HiringFee = totalFee;
            BaseFeeCharged = baseFeeCharged;
            RefundedAmount = 0.0;

            float sumDist = 0f;
            for (int i = 0; i < _legs.Count; i++)
            {
                sumDist += Mathf.Max(0f, _legs[i].RouteDistance);
            }
            TotalRouteDistance = sumDist;

            StartTime = Time.time;
            IntermediateDwellExpireTime = 0f;
            InitialWorldPosition = leadLoco != null ? leadLoco.transform.position : Vector3.zero;
            Engineer = engineer;
            Status = HaulTaskStatus.EnRoute;

            var firstDest = DestinationStation;
            StatusMessage = "En route to " + (firstDest != null && firstDest.stationInfo != null ? firstDest.stationInfo.Name : "destination");
        }
    }
}
