using System;
using System.Collections.Generic;
using UnityEngine;
using CommsRadioAPI;
using DV;
using DV.InventorySystem;
using AITraffic.Core;
using AITraffic.Fleet;
using AITraffic.Navigation;

namespace AITraffic.Workers.Radio
{
    /// <summary>
    /// Initial scan/targeting state: player aims Comms Radio at any locomotive or consist wagon
    /// to inspect train parameters or dismiss an existing worker.
    /// </summary>
    public class WorkerRadioScanState : AStateBehaviour
    {
        private readonly TrainCar _hoveredLoco;
        private readonly AtoBHaulTask _hoveredTask;
        private readonly int _raycastMask;

        public WorkerRadioScanState()
            : this(null, null)
        {
        }

        public WorkerRadioScanState(TrainCar hoveredLoco, AtoBHaulTask hoveredTask)
            : base(BuildState(hoveredLoco, hoveredTask))
        {
            _hoveredLoco = hoveredLoco;
            _hoveredTask = hoveredTask;
            _raycastMask = LayerMask.GetMask("Train_Big_Collider");
            if (_raycastMask == 0)
            {
                int layer = LayerMask.NameToLayer("Train_Big_Collider");
                _raycastMask = (layer != -1) ? (1 << layer) : ~0;
            }
        }

        private static CommsRadioState BuildState(TrainCar loco, AtoBHaulTask task)
        {
            if (loco == null)
            {
                return new CommsRadioState(
                    "AI WORKER",
                    "Point at train to\ninspect consist or\nhire an AI driver.",
                    "",
                    LCDArrowState.Off,
                    LEDState.Off,
                    ButtonBehaviourType.Regular
                );
            }

            if (task != null)
            {
                string destName = (task.DestinationStation != null && task.DestinationStation.stationInfo != null)
                    ? task.DestinationStation.stationInfo.Name
                    : "Destination";

                return new CommsRadioState(
                    "AI WORKER: ACTIVE",
                    string.Format("{0} -> {1}\nSpeed: {2:F0} km/h\nDist Rem: {3:F1} km",
                        loco.ID, destName, task.CurrentSpeedKmh, task.RemainingDistance / 1000f),
                    "DISMISS DRIVER",
                    LCDArrowState.Off,
                    LEDState.On,
                    ButtonBehaviourType.Regular
                );
            }

            List<TrainCar> consist;
            float totalLen;
            float totalMass;
            WorkerManager.Instance.GetConsistMetrics(loco, out consist, out totalLen, out totalMass);

            return new CommsRadioState(
                "AI WORKER: READY",
                string.Format("{0} ({1})\nCars: {2} | Len: {3:F0}m\nMass: {4:F0}t",
                    loco.ID, loco.carType, consist.Count, totalLen, totalMass),
                "SELECT TRAIN",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Regular
            );
        }

        public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
        {
            TrainCar detectedLoco = null;

            if (utility != null && utility.SignalOrigin != null)
            {
                RaycastHit hit;
                if (Physics.Raycast(utility.SignalOrigin.position, utility.SignalOrigin.forward, out hit, 100f, _raycastMask))
                {
                    TrainCar car = TrainCar.Resolve(hit.transform.root);
                    if (car == null) car = hit.collider.GetComponentInParent<TrainCar>();

                    if (car != null)
                    {
                        if (car.IsLoco && TrainSpawner.IsSupportedAILocomotive(car))
                        {
                            detectedLoco = car;
                        }
                        else if (car.trainset != null && car.trainset.cars != null)
                        {
                            for (int i = 0; i < car.trainset.cars.Count; i++)
                            {
                                var c = car.trainset.cars[i];
                                if (c != null && c.IsLoco && TrainSpawner.IsSupportedAILocomotive(c))
                                {
                                    detectedLoco = c;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Fallback: If player is standing inside a locomotive cab
            if (detectedLoco == null && PlayerManager.Car != null && PlayerManager.Car.IsLoco && TrainSpawner.IsSupportedAILocomotive(PlayerManager.Car))
            {
                detectedLoco = PlayerManager.Car;
            }

            if (detectedLoco != _hoveredLoco)
            {
                AtoBHaulTask newTask = detectedLoco != null ? WorkerManager.Instance.GetActiveTask(detectedLoco) : null;
                if (detectedLoco != null && utility != null)
                {
                    utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                }
                return new WorkerRadioScanState(detectedLoco, newTask);
            }

            return this;
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Activate)
            {
                if (_hoveredLoco == null)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Warning, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }

                if (_hoveredTask != null)
                {
                    WorkerManager.Instance.CancelTask(_hoveredTask);
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }

                if (utility != null)
                {
                    utility.PlayVehicleSound(VanillaSoundVehicle.SelectVehicle, _hoveredLoco, false);
                    utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                }

                return new WorkerRadioSelectStationState(_hoveredLoco, 0);
            }

            return this;
        }
    }

    /// <summary>
    /// Second configuration step: player scrolls to select the destination station.
    /// </summary>
    public class WorkerRadioSelectStationState : AStateBehaviour
    {
        private readonly TrainCar _loco;
        private readonly List<StationController> _stations;
        private readonly int _selectedIndex;

        public WorkerRadioSelectStationState(TrainCar loco, int initialIndex = 0)
            : this(loco, GetValidStations(), initialIndex)
        {
        }

        private WorkerRadioSelectStationState(TrainCar loco, List<StationController> stations, int selectedIndex)
            : base(BuildState(loco, stations, selectedIndex))
        {
            _loco = loco;
            _stations = stations;
            _selectedIndex = (stations != null && stations.Count > 0) ? Mathf.Clamp(selectedIndex, 0, stations.Count - 1) : 0;
        }

        private static List<StationController> GetValidStations()
        {
            var list = new List<StationController>();
            if (StationController.allStations != null)
            {
                for (int i = 0; i < StationController.allStations.Count; i++)
                {
                    var s = StationController.allStations[i];
                    if (s != null && s.stationInfo != null && !string.IsNullOrEmpty(s.stationInfo.Name))
                    {
                        list.Add(s);
                    }
                }
                list.Sort((a, b) => string.Compare(a.stationInfo.Name, b.stationInfo.Name, StringComparison.OrdinalIgnoreCase));
            }
            return list;
        }

        private static CommsRadioState BuildState(TrainCar loco, List<StationController> stations, int index)
        {
            if (stations == null || stations.Count == 0)
            {
                return new CommsRadioState(
                    "DEST. STATION",
                    "No stations found!\nWorld not ready.",
                    "CANCEL",
                    LCDArrowState.Off,
                    LEDState.Off,
                    ButtonBehaviourType.Override
                );
            }

            int safeIndex = Mathf.Clamp(index, 0, stations.Count - 1);
            var station = stations[safeIndex];
            float distKm = (loco != null && station != null)
                ? Vector3.Distance(loco.transform.position, station.transform.position) / 1000f
                : 0f;

            return new CommsRadioState(
                "DEST. STATION",
                string.Format("<b>{0}</b>\nDist: ~{1:F1} km\n[{2}/{3}] (Scroll +/-)",
                    station.stationInfo.Name, distKm, safeIndex + 1, stations.Count),
                "SELECT STATION",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Override
            );
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (_loco == null)
            {
                return new WorkerRadioScanState();
            }

            if (action == InputAction.Up)
            {
                if (_stations != null && _stations.Count > 0)
                {
                    int nextIndex = (_selectedIndex + 1) % _stations.Count;
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Switch, utility.SignalOrigin);
                    return new WorkerRadioSelectStationState(_loco, _stations, nextIndex);
                }
                return this;
            }

            if (action == InputAction.Down)
            {
                if (_stations != null && _stations.Count > 0)
                {
                    int prevIndex = (_selectedIndex - 1 + _stations.Count) % _stations.Count;
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Switch, utility.SignalOrigin);
                    return new WorkerRadioSelectStationState(_loco, _stations, prevIndex);
                }
                return this;
            }

            if (action == InputAction.Activate)
            {
                if (_stations == null || _stations.Count == 0)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }

                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                return new WorkerRadioSelectTrackState(_loco, _stations[_selectedIndex], 0);
            }

            return this;
        }
    }

    /// <summary>
    /// Third configuration step: player scrolls to select the arrival track (Auto Siding vs Specific Siding).
    /// </summary>
    public class WorkerRadioSelectTrackState : AStateBehaviour
    {
        private readonly TrainCar _loco;
        private readonly StationController _station;
        private readonly List<RailTrack> _trackOptions;
        private readonly int _selectedIndex;

        public WorkerRadioSelectTrackState(TrainCar loco, StationController station, int initialIndex = 0)
            : this(loco, station, GetTrackOptions(station), initialIndex)
        {
        }

        private WorkerRadioSelectTrackState(TrainCar loco, StationController station, List<RailTrack> trackOptions, int selectedIndex)
            : base(BuildState(station, trackOptions, selectedIndex))
        {
            _loco = loco;
            _station = station;
            _trackOptions = trackOptions;
            _selectedIndex = (trackOptions != null && trackOptions.Count > 0) ? Mathf.Clamp(selectedIndex, 0, trackOptions.Count - 1) : 0;
        }

        private static List<RailTrack> GetTrackOptions(StationController station)
        {
            var options = new List<RailTrack> { null }; // Index 0 = [Auto Clear Siding]
            if (station != null && station.AllStationTracks != null)
            {
                for (int i = 0; i < station.AllStationTracks.Count; i++)
                {
                    var t = station.AllStationTracks[i];
                    if (t != null && t.curve != null)
                    {
                        options.Add(t);
                    }
                }
            }
            return options;
        }

        private static CommsRadioState BuildState(StationController station, List<RailTrack> options, int index)
        {
            if (options == null || options.Count == 0)
            {
                return new CommsRadioState(
                    "ARRIVAL TRACK",
                    "No tracks found!",
                    "CANCEL",
                    LCDArrowState.Off,
                    LEDState.Off,
                    ButtonBehaviourType.Override
                );
            }

            int safeIndex = Mathf.Clamp(index, 0, options.Count - 1);
            string trackName = (safeIndex == 0 || options[safeIndex] == null)
                ? "[Auto Clear Siding]"
                : options[safeIndex].name;

            string stationName = (station != null && station.stationInfo != null) ? station.stationInfo.Name : "Yard";

            return new CommsRadioState(
                "ARRIVAL TRACK",
                string.Format("Station: {0}\nTrack: <b>{1}</b>\n[{2}/{3}] (Scroll +/-)",
                    stationName, trackName, safeIndex + 1, options.Count),
                "SELECT TRACK",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Override
            );
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (_loco == null || _station == null)
            {
                return new WorkerRadioScanState();
            }

            if (action == InputAction.Up)
            {
                int nextIndex = (_selectedIndex + 1) % _trackOptions.Count;
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Switch, utility.SignalOrigin);
                return new WorkerRadioSelectTrackState(_loco, _station, _trackOptions, nextIndex);
            }

            if (action == InputAction.Down)
            {
                int prevIndex = (_selectedIndex - 1 + _trackOptions.Count) % _trackOptions.Count;
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Switch, utility.SignalOrigin);
                return new WorkerRadioSelectTrackState(_loco, _station, _trackOptions, prevIndex);
            }

            if (action == InputAction.Activate)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                RailTrack chosenTrack = _trackOptions[_selectedIndex];
                return new WorkerRadioConfirmState(_loco, _station, chosenTrack);
            }

            return this;
        }
    }

    /// <summary>
    /// Final confirmation step: validates route, checks player wallet, calculates fee,
    /// and dispatches the AI worker upon trigger pull.
    /// </summary>
    public class WorkerRadioConfirmState : AStateBehaviour
    {
        private readonly TrainCar _loco;
        private readonly StationController _station;
        private readonly RailTrack _specificTrack;
        private readonly bool _isValid;

        public WorkerRadioConfirmState(TrainCar loco, StationController station, RailTrack specificTrack)
            : this(loco, station, specificTrack, Evaluate(loco, station, specificTrack))
        {
        }

        private WorkerRadioConfirmState(TrainCar loco, StationController station, RailTrack specificTrack, EvaluationResult result)
            : base(result.State)
        {
            _loco = loco;
            _station = station;
            _specificTrack = specificTrack;
            _isValid = result.IsValid;
        }

        private struct EvaluationResult
        {
            public bool IsValid;
            public CommsRadioState State;
        }

        private static EvaluationResult Evaluate(TrainCar loco, StationController station, RailTrack specificTrack)
        {
            var res = new EvaluationResult();

            if (loco == null)
            {
                res.IsValid = false;
                res.State = CreateErrorState("Locomotive is missing!");
                return res;
            }

            if (station == null)
            {
                res.IsValid = false;
                res.State = CreateErrorState("Destination station missing!");
                return res;
            }

            RailTrack originTrack = WorkerManager.Instance.GetCurrentTrack(loco);
            if (originTrack == null)
            {
                res.IsValid = false;
                res.State = CreateErrorState("Locomotive not on a track!");
                return res;
            }

            List<TrainCar> consist;
            float totalLen, totalMass;
            WorkerManager.Instance.GetConsistMetrics(loco, out consist, out totalLen, out totalMass);

            RailTrack destTrack = specificTrack;
            if (destTrack == null)
            {
                destTrack = WorkerManager.Instance.SelectBestArrivalTrack(station, totalLen, originTrack, loco);
            }

            if (destTrack == null)
            {
                res.IsValid = false;
                res.State = CreateErrorState(string.Format("No siding with len >= {0:F0}m", totalLen + 25f));
                return res;
            }

            if (destTrack == originTrack)
            {
                res.IsValid = false;
                res.State = CreateErrorState("Already at destination track!");
                return res;
            }

            var pathfinder = new Pathfinder(RailGraph.Instance);
            var pathOptions = new PathfinderOptions
            {
                Requester = loco,
                RequesterTrainset = loco.trainset,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = false,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            var routePath = pathfinder.FindPath(originTrack, destTrack, pathOptions);
            if (routePath == null || !routePath.IsValid || routePath.Tracks == null || routePath.Tracks.Count < 2)
            {
                res.IsValid = false;
                res.State = CreateErrorState("No clear route found!\nSwitches may be blocked.");
                return res;
            }

            float routeDist = routePath.TotalDistance;
            double fee = WorkerManager.Instance.CalculateHiringFee(routeDist, totalMass);
            double playerMoney = Inventory.Instance != null ? Inventory.Instance.PlayerMoney : 0.0;

            if (playerMoney < fee)
            {
                res.IsValid = false;
                res.State = CreateErrorState(string.Format("Funds: ${0:N0}\nCost: ${1:N0}", playerMoney, fee));
                return res;
            }

            res.IsValid = true;
            string stationName = station.stationInfo != null ? station.stationInfo.Name : "Dest";
            res.State = new CommsRadioState(
                "DISPATCH DRIVER?",
                string.Format("<b>{0} -> {1}</b>\nTrack: {2}\nFee: ${3:N0} (Dist: {4:F1}km)\n[Scroll: Cancel/Back]",
                    loco.ID, stationName, destTrack.name, fee, routeDist / 1000f),
                "CONFIRM & PAY",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Override
            );
            return res;
        }

        private static CommsRadioState CreateErrorState(string message)
        {
            return new CommsRadioState(
                "CANNOT DISPATCH",
                string.Format("{0}\n\n[Trigger: Return to scan]", message),
                "RETURN",
                LCDArrowState.Off,
                LEDState.Off,
                ButtonBehaviourType.Override
            );
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Up || action == InputAction.Down)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                return new WorkerRadioSelectStationState(_loco, 0);
            }

            if (action == InputAction.Activate)
            {
                if (!_isValid)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Warning, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }

                string hireStatus;
                bool success = WorkerManager.Instance.HireDriverForAtoB(_loco, _station, _specificTrack, out hireStatus);
                if (success)
                {
                    if (utility != null)
                    {
                        utility.PlaySound(VanillaSoundCommsRadio.MoneyRemoved, utility.SignalOrigin);
                        utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                    }
                }
                else
                {
                    if (utility != null)
                    {
                        utility.PlaySound(VanillaSoundCommsRadio.Warning, utility.SignalOrigin);
                    }
                }

                return new WorkerRadioScanState();
            }

            return this;
        }
    }
}
