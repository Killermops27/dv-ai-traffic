using System;
using System.Collections.Generic;
using UnityEngine;
using CommsRadioAPI;
using DV;
using DV.InventorySystem;
using AITraffic.Core;
using AITraffic.Fleet;
using AITraffic.Navigation;
using AITraffic.Compat;
using AITraffic.Driver;

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
            int activeCount = (WorkerManager.Instance != null && WorkerManager.Instance.ActiveTasks != null)
                ? WorkerManager.Instance.ActiveTasks.Count
                : 0;
            AtoBHaulTask alertTask = (WorkerManager.Instance != null) ? WorkerManager.Instance.GetFirstAlertingTask() : null;

            if (loco == null)
            {
                if (alertTask != null && alertTask.LeadLocomotive != null)
                {
                    return new CommsRadioState(
                        "AI WORKER: ALERT!",
                        string.Format("{0} BLOCKED >30s\n{1}\n[Trigger/Scroll: Orders]",
                            ModCompatManager.GetCarId(alertTask.LeadLocomotive),
                            alertTask.Engineer != null ? alertTask.Engineer.WorkerBlockedReason : "Awaiting orders"),
                        "RESOLVE ALERT",
                        LCDArrowState.Right,
                        LEDState.On,
                        ButtonBehaviourType.Regular
                    );
                }

                if (activeCount > 0)
                {
                    return new CommsRadioState(
                        "AI WORKER",
                        string.Format("Active Trains: {0}\nPoint at train to hire\nor press Trigger/Scroll\nfor Active Fleet.", activeCount),
                        "ACTIVE TRAINS",
                        LCDArrowState.Right,
                        LEDState.On,
                        ButtonBehaviourType.Regular
                    );
                }

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
                    task.HasPendingBlockedAlert ? "AI WORKER: BLOCKED" : "AI WORKER: ACTIVE",
                    string.Format("{0} -> {1}\nSpeed: {2:F0} km/h\nDist Rem: {3:F1} km",
                        ModCompatManager.GetCarId(loco), destName, task.CurrentSpeedKmh, task.RemainingDistance / 1000f),
                    "MANAGE TRAIN",
                    LCDArrowState.Right,
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
                    ModCompatManager.GetCarId(loco), loco.carType, consist.Count, totalLen, totalMass),
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
            int activeCount = (WorkerManager.Instance != null && WorkerManager.Instance.ActiveTasks != null)
                ? WorkerManager.Instance.ActiveTasks.Count
                : 0;

            if (action == InputAction.Up || action == InputAction.Down)
            {
                if (activeCount > 0)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                    return new WorkerRadioActiveListState(0);
                }
                return this;
            }

            if (action == InputAction.Activate)
            {
                if (_hoveredLoco == null)
                {
                    AtoBHaulTask alertTask = (WorkerManager.Instance != null) ? WorkerManager.Instance.GetFirstAlertingTask() : null;
                    if (alertTask != null)
                    {
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioBlockedMenuState(alertTask, 0, 0);
                    }
                    if (activeCount > 0)
                    {
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioActiveListState(0);
                    }
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Warning, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }

                if (_hoveredTask != null)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                    if (_hoveredTask.HasPendingBlockedAlert)
                    {
                        return new WorkerRadioBlockedMenuState(_hoveredTask, 0, 0);
                    }
                    return new WorkerRadioTrainDetailState(_hoveredTask, 0);
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
                // CRITICAL INVARIANT [FLT-11]: Gate all three military bases (MB, HMB, MFMB) behind Military1 license (See GRAVEYARD.md)
                bool hasMilitary1 = AITraffic.Core.TrafficScheduler.IsLicenseAcquired(DV.ThingTypes.JobLicenses.Military1);
                for (int i = 0; i < StationController.allStations.Count; i++)
                {
                    var s = StationController.allStations[i];
                    if (s != null && s.stationInfo != null && !string.IsNullOrEmpty(s.stationInfo.Name))
                    {
                        if (!hasMilitary1 && AITraffic.Core.TrafficScheduler.IsMilitaryYardId(s.stationInfo.YardID))
                            continue;
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

            if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
            return new WorkerRadioScanState();
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

            if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
            return new WorkerRadioScanState();
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
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f
            };

            // CRITICAL INVARIANT [WRK-02]: Enforce strict pulling-forward routing with single-switchback fallback only on dead-end spurs (See GRAVEYARD.md)
            Junction spurSwitch;
            RailTrack spurStemTrack;
            byte spurMainlineBranch;
            var routePath = WorkerManager.Instance.FindWorkerRouteWithSpurFallback(
                pathfinder, originTrack, destTrack, loco, pathOptions,
                out spurSwitch, out spurStemTrack, out spurMainlineBranch);
            if (routePath == null || !routePath.IsValid || routePath.Tracks == null || routePath.Tracks.Count < 2)
            {
                res.IsValid = false;
                res.State = CreateErrorState("No forward route found!\nLoco may face wrong way\nor switches blocked.");
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
                    ModCompatManager.GetCarId(loco), stationName, destTrack.name, fee, routeDist / 1000f),
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

    /// <summary>
    /// Valley-wide carousel of all active AI Worker trains plus a [+ Scan / New Haul] return entry.
    /// </summary>
    public class WorkerRadioActiveListState : AStateBehaviour
    {
        private readonly int _selectedIndex;
        private readonly int _totalEntries;
        private readonly AtoBHaulTask _selectedTask;
        private float _refreshTimer;

        public WorkerRadioActiveListState(int selectedIndex)
            : this(selectedIndex, (WorkerManager.Instance != null && WorkerManager.Instance.ActiveTasks != null)
                ? WorkerManager.Instance.ActiveTasks
                : new List<AtoBHaulTask>())
        {
        }

        private WorkerRadioActiveListState(int rawIndex, List<AtoBHaulTask> tasks)
            : this(tasks, tasks.Count + 1, ((rawIndex % (tasks.Count + 1) + (tasks.Count + 1)) % (tasks.Count + 1)))
        {
        }

        private WorkerRadioActiveListState(List<AtoBHaulTask> tasks, int totalEntries, int normalizedIndex)
            : base(BuildState(tasks, totalEntries, normalizedIndex))
        {
            _selectedIndex = normalizedIndex;
            _totalEntries = totalEntries;
            _selectedTask = (normalizedIndex < tasks.Count) ? tasks[normalizedIndex] : null;
            _refreshTimer = 0.5f;
        }

        private static CommsRadioState BuildState(List<AtoBHaulTask> tasks, int totalEntries, int idx)
        {
            if (idx == tasks.Count || tasks.Count == 0)
            {
                return new CommsRadioState(
                    string.Format("ACTIVE FLEET ({0})", tasks.Count),
                    "[+ Scan / New Haul]\nReturn to laser scan\nto hire a new worker\nor inspect cars.",
                    "SCAN MODE",
                    LCDArrowState.Right,
                    LEDState.Off,
                    ButtonBehaviourType.Override
                );
            }

            AtoBHaulTask selectedTask = tasks[idx];
            string locoId = (selectedTask != null && selectedTask.LeadLocomotive != null)
                ? ModCompatManager.GetCarId(selectedTask.LeadLocomotive)
                : "Consist";
            string origName = (selectedTask != null && selectedTask.OriginStation != null && selectedTask.OriginStation.stationInfo != null)
                ? selectedTask.OriginStation.stationInfo.YardID
                : "ORG";
            string destName = (selectedTask != null && selectedTask.DestinationStation != null && selectedTask.DestinationStation.stationInfo != null)
                ? selectedTask.DestinationStation.stationInfo.YardID
                : "DST";
            string stateTag = "RUN";
            if (selectedTask != null)
            {
                if (selectedTask.HasPendingBlockedAlert) stateTag = "ALERT!";
                else if (selectedTask.Engineer != null && selectedTask.Engineer.State == EngineState.PlayerCoPilotStandby) stateTag = "CO-PILOT";
                else if (selectedTask.Engineer != null && selectedTask.Engineer.IsEmergencyStopped) stateTag = "E-STOP";
                else if (selectedTask.Engineer != null && selectedTask.Engineer.IsHoldingAtLoop) stateTag = "HOLD-LOOP";
                else if (selectedTask.IsInfiniteIntermediateHold) stateTag = "HOLD-WPT";
                else if (selectedTask.Status == HaulTaskStatus.WaitingAtIntermediate) stateTag = "DWELL";
                else if (selectedTask.CurrentSpeedKmh < 0.5f) stateTag = "WAIT";
            }

            float speed = selectedTask != null ? selectedTask.CurrentSpeedKmh : 0f;
            float remKm = selectedTask != null ? (selectedTask.RemainingDistance / 1000f) : 0f;

            return new CommsRadioState(
                string.Format("TRAIN {0}/{1} [{2}]", idx + 1, tasks.Count, stateTag),
                string.Format("{0}: {1} -> {2}\nSpd: {3:F0} km/h | Rem: {4:F1} km\n{5}",
                    locoId, origName, destName, speed, remKm,
                    selectedTask != null ? selectedTask.StatusMessage : ""),
                selectedTask != null && selectedTask.HasPendingBlockedAlert ? "RESOLVE ALERT" : "INSPECT / CMD",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Override
            );
        }

        public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
        {
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f)
            {
                return new WorkerRadioActiveListState(_selectedIndex);
            }
            return this;
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Up)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioActiveListState(_selectedIndex - 1);
            }
            if (action == InputAction.Down)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioActiveListState(_selectedIndex + 1);
            }
            if (action == InputAction.Activate)
            {
                if (_selectedTask == null || _selectedTask.LeadLocomotive == null)
                {
                    if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                    return new WorkerRadioScanState();
                }
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                if (_selectedTask.HasPendingBlockedAlert)
                {
                    return new WorkerRadioBlockedMenuState(_selectedTask, 0, _selectedIndex);
                }
                return new WorkerRadioTrainDetailState(_selectedTask, _selectedIndex);
            }
            return this;
        }
    }

    /// <summary>
    /// Live telemetry view for a selected active worker train (loco ID, car count, current track,
    /// speed, remaining distance, governing signal aspect, and current driver state).
    /// </summary>
    public class WorkerRadioTrainDetailState : AStateBehaviour
    {
        private readonly AtoBHaulTask _task;
        private readonly int _returnListIndex;
        private float _refreshTimer;

        public WorkerRadioTrainDetailState(AtoBHaulTask task, int returnListIndex)
            : base(BuildState(task))
        {
            _task = task;
            _returnListIndex = returnListIndex;
            _refreshTimer = 0.5f;
        }

        private static CommsRadioState BuildState(AtoBHaulTask task)
        {
            if (task == null || task.LeadLocomotive == null)
            {
                return new CommsRadioState(
                    "AI WORKER",
                    "Task ended or train\nno longer active.",
                    "BACK",
                    LCDArrowState.Off,
                    LEDState.Off,
                    ButtonBehaviourType.Override
                );
            }

            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            int carCount = task.Consist != null ? task.Consist.Count : 1;
            string destName = (task.DestinationStation != null && task.DestinationStation.stationInfo != null)
                ? task.DestinationStation.stationInfo.YardID
                : "DST";
            string trackName = (task.Engineer != null && task.Engineer.CurrentTrack != null)
                ? task.Engineer.CurrentTrack.name
                : "Unknown";
            if (trackName.Length > 16) trackName = trackName.Substring(0, 16);

            string sigInfo = "Clear";
            if (task.Engineer != null)
            {
                var sig = task.Engineer.GoverningSignal ?? task.Engineer.ApproachingSignal;
                if (sig != null)
                {
                    sigInfo = SignalRegistry.GetAspectDisplayName(sig);
                }
            }

            string stateInfo = task.Engineer != null ? task.Engineer.State.ToString() : task.Status.ToString();
            if (task.HasPendingBlockedAlert) stateInfo = "BLOCKED >30s";
            else if (task.Engineer != null && task.Engineer.State == EngineState.PlayerCoPilotStandby) stateInfo = "CO-PILOT STANDBY";
            else if (task.Engineer != null && task.Engineer.IsEmergencyStopped) stateInfo = "E-STOP (MAX BRK)";
            else if (task.Engineer != null && task.Engineer.IsHoldingAtLoop) stateInfo = "HOLD AT LOOP";
            else if (task.IsInfiniteIntermediateHold) stateInfo = "HOLD AT WAYPOINT";

            return new CommsRadioState(
                string.Format("{0} ({1}c) -> {2}", locoId, carCount, destName),
                string.Format("Trk: {0}\n{1:F0} km/h | {2:F1}km | Sig:{3}\nState: {4}",
                    trackName, task.CurrentSpeedKmh, task.RemainingDistance / 1000f, sigInfo, stateInfo),
                "COMMANDS",
                LCDArrowState.Right,
                LEDState.On,
                ButtonBehaviourType.Override
            );
        }

        public override AStateBehaviour OnUpdate(CommsRadioUtility utility)
        {
            if (_task == null || _task.LeadLocomotive == null || (WorkerManager.Instance != null && !WorkerManager.Instance.ActiveTasks.Contains(_task)))
            {
                return new WorkerRadioActiveListState(0);
            }
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer <= 0f)
            {
                return new WorkerRadioTrainDetailState(_task, _returnListIndex);
            }
            return this;
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Up || action == InputAction.Down)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                return new WorkerRadioActiveListState(_returnListIndex);
            }
            if (action == InputAction.Activate)
            {
                if (_task == null || _task.LeadLocomotive == null)
                {
                    return new WorkerRadioActiveListState(0);
                }
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                if (_task.HasPendingBlockedAlert)
                {
                    return new WorkerRadioBlockedMenuState(_task, 0, _returnListIndex);
                }
                return new WorkerRadioInFlightCommandsState(_task, 0, _returnListIndex);
            }
            return this;
        }
    }

    /// <summary>
    /// Remote command menu for an active worker train:
    /// Resume Run, Hold at Next Loop, Emergency Stop, Blocked Menu (if blocked), Abort & Hand Over, Back.
    /// </summary>
    public class WorkerRadioInFlightCommandsState : AStateBehaviour
    {
        private enum CommandOption
        {
            ResumeRun,
            HoldAtNextLoop,
            EmergencyStop,
            BlockedMenu,
            AbortAndHandOver,
            Back
        }

        private readonly AtoBHaulTask _task;
        private readonly int _selectedIndex;
        private readonly int _returnListIndex;
        private readonly List<CommandOption> _options;

        public WorkerRadioInFlightCommandsState(AtoBHaulTask task, int selectedIndex, int returnListIndex)
            : this(task, selectedIndex, returnListIndex, GetCommandOptions(task))
        {
        }

        private WorkerRadioInFlightCommandsState(AtoBHaulTask task, int rawIndex, int returnListIndex, List<CommandOption> options)
            : this(task, (options.Count > 0) ? ((rawIndex % options.Count + options.Count) % options.Count) : 0, returnListIndex, options, true)
        {
        }

        private WorkerRadioInFlightCommandsState(AtoBHaulTask task, int selectedIndex, int returnListIndex, List<CommandOption> options, bool dummy)
            : base(BuildState(task, selectedIndex, options))
        {
            _task = task;
            _selectedIndex = selectedIndex;
            _returnListIndex = returnListIndex;
            _options = options;
        }

        private static List<CommandOption> GetCommandOptions(AtoBHaulTask task)
        {
            var options = new List<CommandOption>();
            if (task == null || task.LeadLocomotive == null)
            {
                options.Add(CommandOption.Back);
                return options;
            }

            bool isCoPilotStandby = task.Engineer != null && task.Engineer.State == EngineState.PlayerCoPilotStandby;
            bool needsResume = isCoPilotStandby
                || task.Status == HaulTaskStatus.WaitingAtIntermediate
                || (task.Engineer != null && (task.Engineer.IsEmergencyStopped || task.Engineer.IsHoldingAtLoop || task.Engineer.HoldAtNextLoopRequested));

            if (needsResume)
            {
                options.Add(CommandOption.ResumeRun);
            }
            if (task.HasPendingBlockedAlert)
            {
                options.Add(CommandOption.BlockedMenu);
            }
            options.Add(CommandOption.HoldAtNextLoop);
            options.Add(CommandOption.EmergencyStop);
            if (!needsResume)
            {
                options.Add(CommandOption.ResumeRun);
            }
            options.Add(CommandOption.AbortAndHandOver);
            options.Add(CommandOption.Back);
            return options;
        }

        private static CommsRadioState BuildState(AtoBHaulTask task, int idx, List<CommandOption> options)
        {
            if (task == null || task.LeadLocomotive == null || options == null || options.Count == 0)
            {
                return new CommsRadioState("COMMANDS", "Train no longer active.", "BACK", LCDArrowState.Off, LEDState.Off, ButtonBehaviourType.Override);
            }

            CommandOption cmd = options[idx];
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);

            string title = string.Format("CMD: {0} ({1}/{2})", locoId, idx + 1, options.Count);
            string body;
            string actionLabel;

            switch (cmd)
            {
                case CommandOption.ResumeRun:
                    if (task.Engineer != null && task.Engineer.State == EngineState.PlayerCoPilotStandby)
                    {
                        body = "Resume AI Driving:\nSmoothly blend cab levers\nover 2.0s and return\ncontrol to AI Co-Pilot.";
                        actionLabel = "RESUME AI DRIVING";
                    }
                    else
                    {
                        body = "Resume Run:\nRestart engine (if off),\nrelease brakes & proceed\nto destination.";
                        actionLabel = "RESUME RUN";
                    }
                    break;
                case CommandOption.HoldAtNextLoop:
                    body = "Hold at Next Loop:\nPull clear into the next\npassing siding/loop on\nroute and hold.";
                    actionLabel = "HOLD AT LOOP";
                    break;
                case CommandOption.EmergencyStop:
                    body = "Emergency Stop:\nApply full brakes and\nhold ALL brakes at MAX\nuntil Resumed.";
                    actionLabel = "E-STOP";
                    break;
                case CommandOption.BlockedMenu:
                    body = "Blocked-Track Orders:\nReview pre-calculated\nbypass, siding backup,\nor wait timers.";
                    actionLabel = "BLOCKED MENU";
                    break;
                case CommandOption.AbortAndHandOver:
                    double refund = WorkerManager.Instance != null ? WorkerManager.Instance.CalculateCancellationRefund(task) : 0.0;
                    body = string.Format("Abort & Hand Over:\nDismiss AI driver and\nreturn manual control.\nEst. Refund: ${0:F0}", refund);
                    actionLabel = "ABORT HAUL";
                    break;
                default:
                    body = "Return to train\ntelemetry overview.";
                    actionLabel = "BACK";
                    break;
            }

            return new CommsRadioState(title, body, actionLabel, LCDArrowState.Right, LEDState.On, ButtonBehaviourType.Override);
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Up)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioInFlightCommandsState(_task, _selectedIndex - 1, _returnListIndex);
            }
            if (action == InputAction.Down)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioInFlightCommandsState(_task, _selectedIndex + 1, _returnListIndex);
            }
            if (action == InputAction.Activate)
            {
                if (_task == null || _task.LeadLocomotive == null || _options == null || _options.Count == 0)
                {
                    return new WorkerRadioActiveListState(0);
                }
                CommandOption selected = _options[_selectedIndex];
                switch (selected)
                {
                    case CommandOption.ResumeRun:
                        WorkerManager.Instance.ResumeWorkerTask(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case CommandOption.HoldAtNextLoop:
                        WorkerManager.Instance.RequestHoldAtNextLoop(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case CommandOption.EmergencyStop:
                        WorkerManager.Instance.EmergencyStopWorkerTask(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Warning, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case CommandOption.BlockedMenu:
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioBlockedMenuState(_task, 0, _returnListIndex);
                    case CommandOption.AbortAndHandOver:
                        WorkerManager.Instance.CancelTask(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                        return new WorkerRadioActiveListState(0);
                    default:
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                }
            }
            return this;
        }
    }

    /// <summary>
    /// Blocked-Track Escalation Menu (Goal 9):
    /// Presents pre-calculated bypass/siding options alongside multiple "Wait for X time" timers
    /// (Wait 1 Min, Wait 3 Min, Wait 5 Min) and Hand Over Control.
    /// </summary>
    public class WorkerRadioBlockedMenuState : AStateBehaviour
    {
        private enum BlockedAction
        {
            Wait1Min,
            Wait3Min,
            Wait5Min,
            RerouteBypass,
            BackupIntoSiding,
            HandOverControl,
            Back
        }

        private readonly AtoBHaulTask _task;
        private readonly int _selectedIndex;
        private readonly int _returnListIndex;
        private readonly List<BlockedAction> _actions;

        public WorkerRadioBlockedMenuState(AtoBHaulTask task, int selectedIndex, int returnListIndex)
            : this(task, selectedIndex, returnListIndex, GetBlockedActions(task))
        {
        }

        private WorkerRadioBlockedMenuState(AtoBHaulTask task, int rawIndex, int returnListIndex, List<BlockedAction> actions)
            : this(task, (actions.Count > 0) ? ((rawIndex % actions.Count + actions.Count) % actions.Count) : 0, returnListIndex, actions, true)
        {
        }

        private WorkerRadioBlockedMenuState(AtoBHaulTask task, int selectedIndex, int returnListIndex, List<BlockedAction> actions, bool dummy)
            : base(BuildState(task, selectedIndex, actions))
        {
            _task = task;
            _selectedIndex = selectedIndex;
            _returnListIndex = returnListIndex;
            _actions = actions;
        }

        private static List<BlockedAction> GetBlockedActions(AtoBHaulTask task)
        {
            var actions = new List<BlockedAction>();
            if (task == null || task.LeadLocomotive == null || task.Engineer == null)
            {
                actions.Add(BlockedAction.Back);
                return actions;
            }

            if (task.Engineer.HasPrecalculatedWorkerBypass)
            {
                actions.Add(BlockedAction.RerouteBypass);
            }
            if (task.Engineer.HasPrecalculatedWorkerTrailingSiding)
            {
                actions.Add(BlockedAction.BackupIntoSiding);
            }
            actions.Add(BlockedAction.Wait1Min);
            actions.Add(BlockedAction.Wait3Min);
            actions.Add(BlockedAction.Wait5Min);
            actions.Add(BlockedAction.HandOverControl);
            actions.Add(BlockedAction.Back);
            return actions;
        }

        private static CommsRadioState BuildState(AtoBHaulTask task, int idx, List<BlockedAction> actions)
        {
            if (task == null || task.LeadLocomotive == null || task.Engineer == null || actions == null || actions.Count == 0)
            {
                return new CommsRadioState("BLOCKED MENU", "Train no longer active.", "BACK", LCDArrowState.Off, LEDState.Off, ButtonBehaviourType.Override);
            }

            BlockedAction act = actions[idx];
            string locoId = ModCompatManager.GetCarId(task.LeadLocomotive);
            string reason = !string.IsNullOrEmpty(task.Engineer.WorkerBlockedReason)
                ? task.Engineer.WorkerBlockedReason
                : "Track obstructed";

            string title = string.Format("ALERT: {0} ({1}/{2})", locoId, idx + 1, actions.Count);
            string body;
            string buttonText;

            switch (act)
            {
                case BlockedAction.RerouteBypass:
                    body = string.Format("{0}\nOption: Reroute via\nclear {1} bypass.",
                        reason, task.Engineer.PrecalculatedWorkerBypassIsShunting ? "shunting" : "mainline");
                    buttonText = "REROUTE BYPASS";
                    break;
                case BlockedAction.BackupIntoSiding:
                    body = string.Format("{0}\nOption: Reverse into\nclear trailing siding\nto yield right-of-way.", reason);
                    buttonText = "BACKUP TO SIDING";
                    break;
                case BlockedAction.Wait1Min:
                    body = string.Format("{0}\nOption: Hold position\nfor 1 minute (60s)\nbefore re-checking.", reason);
                    buttonText = "WAIT 1 MIN";
                    break;
                case BlockedAction.Wait3Min:
                    body = string.Format("{0}\nOption: Hold position\nfor 3 minutes (180s)\nbefore re-checking.", reason);
                    buttonText = "WAIT 3 MIN";
                    break;
                case BlockedAction.Wait5Min:
                    body = string.Format("{0}\nOption: Hold position\nfor 5 minutes (300s)\nbefore re-checking.", reason);
                    buttonText = "WAIT 5 MIN";
                    break;
                case BlockedAction.HandOverControl:
                    double refund = WorkerManager.Instance != null ? WorkerManager.Instance.CalculateCancellationRefund(task) : 0.0;
                    body = string.Format("{0}\nOption: Dismiss driver &\ntake manual control.\nEst. Refund: ${1:F0}", reason, refund);
                    buttonText = "HAND OVER";
                    break;
                default:
                    body = "Return to train\ntelemetry screen.";
                    buttonText = "BACK";
                    break;
            }

            return new CommsRadioState(title, body, buttonText, LCDArrowState.Right, LEDState.On, ButtonBehaviourType.Override);
        }

        public override AStateBehaviour OnAction(CommsRadioUtility utility, InputAction action)
        {
            if (action == InputAction.Up)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioBlockedMenuState(_task, _selectedIndex - 1, _returnListIndex);
            }
            if (action == InputAction.Down)
            {
                if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.HoverOver, utility.SignalOrigin);
                return new WorkerRadioBlockedMenuState(_task, _selectedIndex + 1, _returnListIndex);
            }
            if (action == InputAction.Activate)
            {
                if (_task == null || _task.LeadLocomotive == null || _actions == null || _actions.Count == 0)
                {
                    return new WorkerRadioActiveListState(0);
                }
                BlockedAction act = _actions[_selectedIndex];
                switch (act)
                {
                    case BlockedAction.RerouteBypass:
                        WorkerManager.Instance.RerouteBlockedTask(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case BlockedAction.BackupIntoSiding:
                        WorkerManager.Instance.BackupBlockedTaskToSiding(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case BlockedAction.Wait1Min:
                        WorkerManager.Instance.WaitOnBlockedTask(_task, 60f);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case BlockedAction.Wait3Min:
                        WorkerManager.Instance.WaitOnBlockedTask(_task, 180f);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case BlockedAction.Wait5Min:
                        WorkerManager.Instance.WaitOnBlockedTask(_task, 300f);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Confirm, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                    case BlockedAction.HandOverControl:
                        WorkerManager.Instance.CancelTask(_task);
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                        return new WorkerRadioActiveListState(0);
                    default:
                        if (utility != null) utility.PlaySound(VanillaSoundCommsRadio.Cancel, utility.SignalOrigin);
                        return new WorkerRadioTrainDetailState(_task, _returnListIndex);
                }
            }
            return this;
        }
    }
}
