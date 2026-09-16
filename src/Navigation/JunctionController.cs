using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AITraffic.Navigation
{
    /// <summary>
    /// Holds active lock state and clearance tracking information for a junction switch.
    /// </summary>
    public class JunctionLockInfo
    {
        public Junction Junction { get; private set; }
        public object Requester { get; private set; }
        public float LockedTime { get; private set; }
        public float ExpirationTime { get; set; }

        public Bogie MonitoredRearBogie { get; set; }
        public RailTrack DestinationTrack { get; set; }
        public float ClearanceDistanceMeters { get; set; }

        public bool IsExpired
        {
            get { return ExpirationTime > 0f && Time.time > ExpirationTime; }
        }

        public JunctionLockInfo(Junction junction, object requester, float durationSeconds = 30f)
        {
            Junction = junction;
            Requester = requester;
            LockedTime = Time.time;
            ExpirationTime = durationSeconds > 0f ? Time.time + durationSeconds : float.MaxValue;
            ClearanceDistanceMeters = 15f;
        }
    }

    /// <summary>
    /// Manages turnout switching, safety locking, and automatic clearance tracking for AI trains.
    /// </summary>
    public class JunctionController : MonoBehaviour
    {
        private static JunctionController _instance;
        public static JunctionController Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("[AITraffic_JunctionController]");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    _instance = go.AddComponent<JunctionController>();
                }
                return _instance;
            }
        }

        public static event Action<Junction, byte, object> OnJunctionSwitched;
        public static event Action<Junction, object> OnJunctionLocked;
        public static event Action<Junction, object> OnJunctionReleased;

        private readonly Dictionary<Junction, JunctionLockInfo> _activeLocks = new Dictionary<Junction, JunctionLockInfo>();
        private readonly List<JunctionLockInfo> _monitoredTrainPassings = new List<JunctionLockInfo>();
        private readonly object _lock = new object();

        private float _lastCleanupTime = 0f;
        private const float CleanupIntervalSeconds = 2.0f;

        private static void Log(string msg)
        {
            Debug.Log("[AITraffic] " + msg);
        }

        private static void LogWarning(string msg)
        {
            Debug.LogWarning("[AITraffic] " + msg);
        }

        private static void LogError(string msg)
        {
            Debug.LogError("[AITraffic] " + msg);
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (Time.time - _lastCleanupTime >= CleanupIntervalSeconds)
            {
                _lastCleanupTime = Time.time;
                ClearExpiredLocks();
            }

            UpdateTrainClearanceMonitoring();
        }

        private void OnDestroy()
        {
            lock (_lock)
            {
                UnsubscribeAllBogieListeners();
                _activeLocks.Clear();
                _monitoredTrainPassings.Clear();
            }
        }

        /// <summary>
        /// Requests switching a junction to a desired branch safely if not locked by another entity.
        /// </summary>
        public bool RequestJunctionAlignment(Junction junction, byte desiredBranch, object requester)
        {
            if (junction == null)
            {
                LogWarning("[JunctionController] RequestJunctionAlignment called with null junction.");
                return false;
            }

            lock (_lock)
            {
                // If switch is already aligned to the desired branch, no throw is needed
                if (junction.selectedBranch == desiredBranch)
                {
                    return true;
                }

                // 1. Strict Physical Occupancy Safety Interlock: NEVER throw a switch under rolling stock!
                Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                TrainCar occupyingCar;
                if (IsJunctionPhysicallyOccupied(junction, out occupyingCar, requesterTrainset))
                {
                    Log(string.Format("[JunctionController] Junction '{0}' is physically OCCUPIED by car '{1}'; alignment to branch {2} strictly DENIED for requester '{3}'.",
                        junction.name, occupyingCar != null ? occupyingCar.ID : "unknown", desiredBranch, requester));
                    return false;
                }

                // 2. Player occupancy / presence check (respects Ride-Along mode and ignores requester's train)
                if (requester != null && requester is AITraffic.Driver.AIEngineer)
                {
                    bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);
                    if (!rideAlong)
                    {
                        if (SignalRegistry.IsJunctionOccupiedByPlayer(junction, requesterTrainset))
                        {
                            Log(string.Format("[JunctionController] Junction '{0}' is occupied by the player; AI alignment denied.", junction.name));
                            return false;
                        }
                    }
                }

                // 3. Lock check
                if (IsJunctionLockedByOther(junction, requester))
                {
                    Log(string.Format("[JunctionController] Junction '{0}' is locked by another entity; alignment denied.", junction.name));
                    return false;
                }

                if (junction.outBranches == null || desiredBranch >= junction.outBranches.Count)
                {
                    LogWarning(string.Format("[JunctionController] Invalid branch index {0} requested for junction '{1}' with {2} branches.",
                        desiredBranch, junction.name, junction.outBranches != null ? junction.outBranches.Count : 0));
                    return false;
                }

                try
                {
                    junction.Switch(Junction.SwitchMode.FORCED, desiredBranch);
                    Log(string.Format("[JunctionController] Junction '{0}' switched to branch {1} for requester '{2}'.", junction.name, desiredBranch, requester));

                    // Immediately lock switch to protect newly aligned route
                    TryLockJunction(junction, requester, 60f);

                    if (OnJunctionSwitched != null)
                    {
                        OnJunctionSwitched.Invoke(junction, desiredBranch, requester);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    LogError(string.Format("[JunctionController] Exception switching junction '{0}': {1}", junction.name, ex));
                    return false;
                }
            }
        }

        /// <summary>
        /// Attempts to acquire an exclusive lock on a junction for a requester.
        /// </summary>
        public bool TryLockJunction(Junction junction, object requester, float timeoutSeconds = 30f)
        {
            if (junction == null || requester == null) return false;

            lock (_lock)
            {
                TrainCar occupyingCar;
                if (IsJunctionPhysicallyOccupied(junction, out occupyingCar))
                {
                    Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                    // If the switch is physically occupied, ONLY the train that is occupying it can hold/refresh the lock!
                    if (requesterTrainset == null || occupyingCar == null || occupyingCar.trainset != requesterTrainset)
                    {
                        return false;
                    }
                }

                if (requester is AITraffic.Driver.AIEngineer)
                {
                    bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);
                    if (!rideAlong)
                    {
                        Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                        if (SignalRegistry.IsJunctionOccupiedByPlayer(junction, requesterTrainset))
                        {
                            return false;
                        }
                    }
                }

                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    if (lockInfo.Requester == requester)
                    {
                        // Refresh expiration timeout
                        lockInfo.ExpirationTime = timeoutSeconds > 0f ? Time.time + timeoutSeconds : float.MaxValue;
                        return true;
                    }

                    if (lockInfo.IsExpired)
                    {
                        if (IsJunctionPhysicallyOccupied(junction))
                        {
                            // Previous train is still physically occupying the switch; extend protection!
                            lockInfo.ExpirationTime = Time.time + 15f;
                            return false;
                        }
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                    }
                    else
                    {
                        return false;
                    }
                }

                var newLock = new JunctionLockInfo(junction, requester, timeoutSeconds);
                _activeLocks[junction] = newLock;

                if (OnJunctionLocked != null)
                {
                    OnJunctionLocked.Invoke(junction, requester);
                }
                return true;
            }
        }

        /// <summary>
        /// Releases a junction lock held by the requester.
        /// </summary>
        public void ReleaseJunction(Junction junction, object requester)
        {
            if (junction == null) return;

            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    if (requester == null || lockInfo.Requester == requester)
                    {
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether a junction is currently locked and returns the lock holder.
        /// </summary>
        public bool IsJunctionLocked(Junction junction, out object currentLockHolder)
        {
            currentLockHolder = null;
            if (junction == null) return false;

            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    if (!lockInfo.IsExpired)
                    {
                        currentLockHolder = lockInfo.Requester;
                        return true;
                    }
                    else
                    {
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                    }
                }
                return false;
            }
        }

        private bool IsRequesterAlive(object requester)
        {
            if (requester == null) return false;
            var eng = requester as AITraffic.Driver.AIEngineer;
            if (eng != null)
            {
                if (eng.TrainCar == null) return false;
            }
            var car = requester as TrainCar;
            if (car != null)
            {
                if (car == null) return false;
            }
            return true;
        }

        /// <summary>
        /// Releases all junction locks and passing monitors held by the specified requester.
        /// </summary>
        public void ReleaseAllLocksFor(object requester)
        {
            if (requester == null) return;

            lock (_lock)
            {
                var keys = _activeLocks.Where(kvp => kvp.Value.Requester == requester).Select(kvp => kvp.Key).ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    ReleaseJunctionInternal(keys[i], requester);
                }

                for (int i = _monitoredTrainPassings.Count - 1; i >= 0; i--)
                {
                    var info = _monitoredTrainPassings[i];
                    if (info.Requester == requester)
                    {
                        if (info.MonitoredRearBogie != null)
                        {
                            info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                        }
                        _monitoredTrainPassings.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether a junction is locked by an entity other than requester.
        /// </summary>
        public bool IsJunctionLockedByOther(Junction junction, object requester)
        {
            if (junction == null) return false;

            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                    Trainset lockHolderTrainset = GetTrainsetFromRequester(lockInfo.Requester);

                    // If requester belongs to the same trainset as the lock holder, it's not locked by "other"
                    if (requesterTrainset != null && lockHolderTrainset != null && requesterTrainset == lockHolderTrainset)
                    {
                        return false;
                    }

                    if (lockInfo.Requester == requester)
                    {
                        return false;
                    }

                    // If the switch is physically occupied, lock MUST be preserved and extended!
                    if (IsJunctionPhysicallyOccupied(junction))
                    {
                        lockInfo.ExpirationTime = Mathf.Max(lockInfo.ExpirationTime, Time.time + 30f);
                        return true;
                    }

                    if (lockInfo.IsExpired)
                    {
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                        return false;
                    }

                    if (!IsRequesterAlive(lockInfo.Requester))
                    {
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                        return false;
                    }

                    // Deadlock defense-in-depth: If the lock holder is an AIEngineer that is stopped and blocked by an obstacle ahead:
                    var holderEng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
                    if (holderEng != null && holderEng.CurrentSpeedKmh < 1.5f && holderEng.DistanceToObstacle < 150f)
                    {
                        // The lock holder is blocked and unable to move; do not block other agents from aligning switch to clear out
                        return false;
                    }

                    return true;
                }

                // Fallback: If not in _activeLocks, but is physically occupied by a train other than requester:
                TrainCar occupyingCar;
                if (IsJunctionPhysicallyOccupied(junction, out occupyingCar))
                {
                    Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                    if (occupyingCar != null && occupyingCar.trainset != null)
                    {
                        if (requesterTrainset == null || occupyingCar.trainset != requesterTrainset)
                        {
                            return true; // Physically occupied by another train!
                        }
                    }
                    else
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Automatically manages the junction switch lock until the rear bogie of the train clears the switch.
        /// </summary>
        public void RegisterTrainPassing(object trainRequester, Junction junction, Bogie rearBogie, RailTrack destinationTrack, float timeoutSeconds = 60f)
        {
            if (junction == null || trainRequester == null) return;

            lock (_lock)
            {
                TryLockJunction(junction, trainRequester, timeoutSeconds);

                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    lockInfo.MonitoredRearBogie = rearBogie;
                    lockInfo.DestinationTrack = destinationTrack;
                    lockInfo.ClearanceDistanceMeters = CalculateClearanceMargin(junction);

                    if (rearBogie != null)
                    {
                        rearBogie.TrackChanged -= OnBogieTrackChanged;
                        rearBogie.TrackChanged += OnBogieTrackChanged;
                    }

                    if (!_monitoredTrainPassings.Contains(lockInfo))
                    {
                        _monitoredTrainPassings.Add(lockInfo);
                    }
                }
            }
        }

        /// <summary>
        /// Convenience overload to register train passing using a TrainCar's rear bogie.
        /// </summary>
        public void RegisterTrainPassing(TrainCar rearCar, Junction junction, RailTrack destinationTrack, float timeoutSeconds = 60f)
        {
            if (rearCar == null || junction == null) return;
            var bogie = rearCar.RearBogie != null ? rearCar.RearBogie : rearCar.FrontBogie;
            RegisterTrainPassing(rearCar, junction, bogie, destinationTrack, timeoutSeconds);
        }

        public void CancelTrainPassing(object trainRequester, Junction junction)
        {
            if (junction == null) return;

            lock (_lock)
            {
                for (int i = _monitoredTrainPassings.Count - 1; i >= 0; i--)
                {
                    var info = _monitoredTrainPassings[i];
                    if (info.Junction == junction && (trainRequester == null || info.Requester == trainRequester))
                    {
                        if (info.MonitoredRearBogie != null)
                        {
                            info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                        }
                        _monitoredTrainPassings.RemoveAt(i);
                    }
                }

                ReleaseJunction(junction, trainRequester);
            }
        }

        private void OnBogieTrackChanged(RailTrack newTrack, Bogie bogie)
        {
            if (bogie == null) return;

            lock (_lock)
            {
                for (int i = _monitoredTrainPassings.Count - 1; i >= 0; i--)
                {
                    var info = _monitoredTrainPassings[i];
                    if (info.MonitoredRearBogie == bogie)
                    {
                        TrainCar occupyingCar;
                        if (IsJunctionPhysicallyOccupied(info.Junction, out occupyingCar))
                        {
                            // Train is still physically occupying the switch! Keep lock active and extend expiration!
                            info.ExpirationTime = Mathf.Max(info.ExpirationTime, Time.time + 30f);
                            continue;
                        }

                        float dist = (info.Junction != null) ? Vector3.Distance(bogie.transform.position, info.Junction.position) : 0f;
                        bool pastClearanceMargin = dist >= info.ClearanceDistanceMeters;

                        // Only release if rear bogie has fully passed beyond the clearance distance!
                        if (pastClearanceMargin)
                        {
                            bogie.TrackChanged -= OnBogieTrackChanged;
                            _monitoredTrainPassings.RemoveAt(i);
                            ReleaseJunctionInternal(info.Junction, info.Requester);
                        }
                    }
                }
            }
        }

        private void UpdateTrainClearanceMonitoring()
        {
            lock (_lock)
            {
                for (int i = _monitoredTrainPassings.Count - 1; i >= 0; i--)
                {
                    var info = _monitoredTrainPassings[i];

                    if (info.Junction == null)
                    {
                        if (info.MonitoredRearBogie != null)
                        {
                            info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                        }
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
                        continue;
                    }

                    // Check if train cars are still physically on the junction
                    TrainCar occupyingCar;
                    bool isStillOnJunction = IsJunctionPhysicallyOccupied(info.Junction, out occupyingCar);

                    if (isStillOnJunction)
                    {
                        // Still traversing switch: keep lock active and extend expiration!
                        info.ExpirationTime = Mathf.Max(info.ExpirationTime, Time.time + 30f);
                        continue;
                    }

                    var bogie = info.MonitoredRearBogie;
                    if (bogie == null || bogie.HasDerailed)
                    {
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
                        continue;
                    }

                    float dist = Vector3.Distance(bogie.transform.position, info.Junction.position);
                    bool pastClearanceMargin = dist >= info.ClearanceDistanceMeters;

                    if (!isStillOnJunction && pastClearanceMargin)
                    {
                        bogie.TrackChanged -= OnBogieTrackChanged;
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
                    }
                    else if (info.IsExpired && !isStillOnJunction)
                    {
                        bogie.TrackChanged -= OnBogieTrackChanged;
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
                    }
                }
            }
        }

        private void ReleaseJunctionInternal(Junction junction, object requester)
        {
            if (junction == null) return;

            _activeLocks.Remove(junction);
            if (OnJunctionReleased != null)
            {
                OnJunctionReleased.Invoke(junction, requester);
            }
            Log(string.Format("[JunctionController] Junction '{0}' lock released for requester '{1}'.", junction.name, requester));
        }

        public void ClearExpiredLocks()
        {
            lock (_lock)
            {
                var expiredList = _activeLocks.Values.Where(l => l.IsExpired).ToList();
                for (int i = 0; i < expiredList.Count; i++)
                {
                    var item = expiredList[i];
                    if (IsJunctionPhysicallyOccupied(item.Junction))
                    {
                        // Train is still physically traversing or sitting on the switch: extend lock!
                        item.ExpirationTime = Time.time + 30f;
                    }
                    else
                    {
                        ReleaseJunctionInternal(item.Junction, item.Requester);
                    }
                }
            }
        }

        private void UnsubscribeAllBogieListeners()
        {
            for (int i = 0; i < _monitoredTrainPassings.Count; i++)
            {
                var info = _monitoredTrainPassings[i];
                if (info.MonitoredRearBogie != null)
                {
                    info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                }
            }
        }

        public static float CalculateClearanceMargin(Junction junction)
        {
            if (junction == null) return 35f;

            float maxLen = 35f;
            if (junction.inBranch != null && junction.inBranch.track != null && junction.inBranch.track.curve != null)
            {
                maxLen = Mathf.Max(maxLen, Mathf.Min(junction.inBranch.track.curve.length, 50f));
            }

            if (junction.outBranches != null)
            {
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track != null && branch.track.curve != null)
                    {
                        maxLen = Mathf.Max(maxLen, Mathf.Min(branch.track.curve.length, 50f));
                    }
                }
            }

            return maxLen + 10f; // Generous safety clearance margin (at least 45m)
        }

        public static bool IsTrackPartOfJunction(Junction junction, RailTrack track)
        {
            if (junction == null || track == null) return false;
            if (junction.inBranch != null && junction.inBranch.track == track) return true;

            if (junction.outBranches != null)
            {
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    if (junction.outBranches[i] != null && junction.outBranches[i].track == track) return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns all rail tracks directly connected to the junction (inBranch and all outBranches).
        /// </summary>
        public static List<RailTrack> GetConnectedTracks(Junction junction)
        {
            var tracks = new List<RailTrack>();
            if (junction == null) return tracks;

            if (junction.inBranch != null && junction.inBranch.track != null)
            {
                tracks.Add(junction.inBranch.track);
            }

            if (junction.outBranches != null)
            {
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track != null && !tracks.Contains(branch.track))
                    {
                        tracks.Add(branch.track);
                    }
                }
            }

            return tracks;
        }

        /// <summary>
        /// Retrieves the Trainset associated with a requester object (either AIEngineer or TrainCar).
        /// </summary>
        public static Trainset GetTrainsetFromRequester(object requester)
        {
            if (requester == null) return null;
            var eng = requester as AITraffic.Driver.AIEngineer;
            if (eng != null && eng.TrainCar != null)
            {
                return eng.TrainCar.trainset;
            }
            var car = requester as TrainCar;
            if (car != null)
            {
                return car.trainset;
            }
            return null;
        }

        /// <summary>
        /// Checks whether any rolling stock (train cars, bogies) is physically occupying or straddling the junction.
        /// Evaluates whole-trainset spanning across in/out branches, individual car straddling,
        /// distance to switch points / frog clearance boundaries, and live AI/player trainsets.
        /// </summary>
        public static bool IsJunctionPhysicallyOccupied(Junction junction, out TrainCar occupyingCar, Trainset requesterTrainset = null)
        {
            occupyingCar = null;
            if (junction == null) return false;

            float clearanceMargin = CalculateClearanceMargin(junction);
            var connectedTracks = GetConnectedTracks(junction);
            if (connectedTracks.Count == 0) return false;

            var inTrack = junction.inBranch != null ? junction.inBranch.track : null;

            // Map each trainset detected on any connected track to the tracks its bogies are on
            var trainsetToTracks = new Dictionary<Trainset, HashSet<RailTrack>>();
            var trainsetToCars = new Dictionary<Trainset, List<TrainCar>>();
            TrainCar detectedCar = null;
            TrainCar externalCar = null;
            TrainCar requesterBladeCar = null;

            Action<Bogie, RailTrack> processBogie = (bogie, track) =>
            {
                if (bogie == null || bogie.Car == null) return;
                var car = bogie.Car;

                if (car.trainset != null)
                {
                    HashSet<RailTrack> tSet;
                    if (!trainsetToTracks.TryGetValue(car.trainset, out tSet))
                    {
                        tSet = new HashSet<RailTrack>();
                        trainsetToTracks[car.trainset] = tSet;
                    }
                    if (track != null) tSet.Add(track);

                    List<TrainCar> cList;
                    if (!trainsetToCars.TryGetValue(car.trainset, out cList))
                    {
                        cList = new List<TrainCar>();
                        trainsetToCars[car.trainset] = cList;
                    }
                    if (!cList.Contains(car)) cList.Add(car);
                }

                bool isRequester = (requesterTrainset != null && car.trainset == requesterTrainset);

                // 1. Check bogie 3D distance to switch points
                float bogieDist = Vector3.Distance(bogie.transform.position, junction.position);
                if (bogieDist <= clearanceMargin)
                {
                    if (detectedCar == null) detectedCar = car;
                    if (!isRequester)
                    {
                        if (externalCar == null) externalCar = car;
                    }
                    else if (bogieDist <= 6.5f)
                    {
                        // Requester's wheels are directly over movable switch blades/points
                        if (requesterBladeCar == null) requesterBladeCar = car;
                    }
                }

                // 2. Check car center 3D distance to switch points
                float carDist = Vector3.Distance(car.transform.position, junction.position);
                if (carDist <= clearanceMargin)
                {
                    if (detectedCar == null) detectedCar = car;
                    if (!isRequester)
                    {
                        if (externalCar == null) externalCar = car;
                    }
                }

                // 3. Check individual car straddling across branches
                if (car.FrontBogie != null && car.RearBogie != null && car.FrontBogie.track != null && car.RearBogie.track != null)
                {
                    var trackF = car.FrontBogie.track;
                    var trackR = car.RearBogie.track;

                    if (trackF != trackR)
                    {
                        bool fOnIn = (inTrack != null && trackF == inTrack);
                        bool rOnIn = (inTrack != null && trackR == inTrack);
                        bool fOnConn = connectedTracks.Contains(trackF);
                        bool rOnConn = connectedTracks.Contains(trackR);

                        if ((fOnIn && rOnConn) || (rOnIn && fOnConn) || (fOnConn && rOnConn))
                        {
                            if (detectedCar == null) detectedCar = car;
                            if (!isRequester)
                            {
                                if (externalCar == null) externalCar = car;
                            }
                            else
                            {
                                if (requesterBladeCar == null) requesterBladeCar = car;
                            }
                        }
                    }
                }
            };

            for (int t = 0; t < connectedTracks.Count; t++)
            {
                var track = connectedTracks[t];
                if (track == null) continue;

                HashSet<Bogie> bogies = null;
                try
                {
                    bogies = track.BogiesOnTrack();
                }
                catch { }

                if (bogies != null && bogies.Count > 0)
                {
                    foreach (var bogie in bogies)
                    {
                        processBogie(bogie, track);
                    }
                }
            }

            // Also inspect live active AI trains to guarantee zero misses from collider trigger lag
            if (AITraffic.Core.TrafficManager.Instance != null && AITraffic.Core.TrafficManager.Instance.ActiveEngineers != null)
            {
                var activeEngs = AITraffic.Core.TrafficManager.Instance.ActiveEngineers;
                for (int e = 0; e < activeEngs.Count; e++)
                {
                    var eng = activeEngs[e];
                    if (eng == null || eng.TrainCar == null || eng.TrainCar.trainset == null) continue;
                    var cars = eng.TrainCar.trainset.cars;
                    if (cars == null) continue;

                    for (int c = 0; c < cars.Count; c++)
                    {
                        var car = cars[c];
                        if (car == null) continue;

                        if (car.FrontBogie != null && car.FrontBogie.track != null && connectedTracks.Contains(car.FrontBogie.track))
                        {
                            processBogie(car.FrontBogie, car.FrontBogie.track);
                        }
                        if (car.RearBogie != null && car.RearBogie.track != null && connectedTracks.Contains(car.RearBogie.track))
                        {
                            processBogie(car.RearBogie, car.RearBogie.track);
                        }
                    }
                }
            }

            // Also inspect player train
            if (PlayerManager.Car != null && PlayerManager.Car.trainset != null && PlayerManager.Car.trainset.cars != null)
            {
                var pCars = PlayerManager.Car.trainset.cars;
                for (int c = 0; c < pCars.Count; c++)
                {
                    var car = pCars[c];
                    if (car == null) continue;

                    if (car.FrontBogie != null && car.FrontBogie.track != null && connectedTracks.Contains(car.FrontBogie.track))
                    {
                        processBogie(car.FrontBogie, car.FrontBogie.track);
                    }
                    if (car.RearBogie != null && car.RearBogie.track != null && connectedTracks.Contains(car.RearBogie.track))
                    {
                        processBogie(car.RearBogie, car.RearBogie.track);
                    }
                }
            }

            if (requesterTrainset != null)
            {
                if (externalCar != null)
                {
                    occupyingCar = externalCar;
                    return true;
                }
                if (requesterBladeCar != null)
                {
                    occupyingCar = requesterBladeCar;
                    return true;
                }
            }
            else if (detectedCar != null)
            {
                occupyingCar = detectedCar;
                return true;
            }

            // 4. Whole-Trainset Multi-Car Spanning Check:
            // If any trainset has cars/bogies spanning across inBranch and ANY outBranch (or between two outBranches),
            // the consist is physically over the switch points and actively traversing!
            foreach (var kvp in trainsetToTracks)
            {
                var tSet = kvp.Value;
                bool hasInTrack = inTrack != null && tSet.Contains(inTrack);
                bool hasOutTrack = false;

                if (junction.outBranches != null)
                {
                    for (int b = 0; b < junction.outBranches.Count; b++)
                    {
                        var outBr = junction.outBranches[b];
                        if (outBr != null && outBr.track != null && tSet.Contains(outBr.track))
                        {
                            hasOutTrack = true;
                            break;
                        }
                    }
                }

                if (hasInTrack && hasOutTrack)
                {
                    List<TrainCar> cList;
                    trainsetToCars.TryGetValue(kvp.Key, out cList);
                    occupyingCar = (cList != null && cList.Count > 0) ? cList[0] : null;
                    return true;
                }

                // If trainset has bogies on multiple out-branches (e.g. crossover switch spanning)
                if (junction.outBranches != null && junction.outBranches.Count > 1)
                {
                    int outBranchCount = 0;
                    for (int b = 0; b < junction.outBranches.Count; b++)
                    {
                        var outBr = junction.outBranches[b];
                        if (outBr != null && outBr.track != null && tSet.Contains(outBr.track))
                        {
                            outBranchCount++;
                        }
                    }
                    if (outBranchCount > 1)
                    {
                        List<TrainCar> cList;
                        trainsetToCars.TryGetValue(kvp.Key, out cList);
                        occupyingCar = (cList != null && cList.Count > 0) ? cList[0] : null;
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsJunctionPhysicallyOccupied(Junction junction)
        {
            TrainCar dummy;
            return IsJunctionPhysicallyOccupied(junction, out dummy);
        }
    }
}
