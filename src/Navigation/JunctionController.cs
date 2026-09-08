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
                TrainCar occupyingCar;
                if (IsJunctionPhysicallyOccupied(junction, out occupyingCar))
                {
                    Log(string.Format("[JunctionController] Junction '{0}' is physically occupied by car '{1}'; alignment to branch {2} DENIED for requester '{3}'.",
                        junction.name, occupyingCar != null ? occupyingCar.ID : "unknown", desiredBranch, requester));
                    return false;
                }

                // 2. Player occupancy / presence check
                if (requester != null && requester is AITraffic.Driver.AIEngineer)
                {
                    if (SignalRegistry.IsJunctionOccupiedByPlayer(junction))
                    {
                        Log(string.Format("[JunctionController] Junction '{0}' is occupied by the player; AI alignment denied.", junction.name));
                        return false;
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
                    junction.Switch(Junction.SwitchMode.REGULAR, desiredBranch);
                    Log(string.Format("[JunctionController] Junction '{0}' switched to branch {1} for requester '{2}'.", junction.name, desiredBranch, requester));

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
                    if (occupyingCar != null && (requesterTrainset == null || occupyingCar.trainset != requesterTrainset))
                    {
                        return false; // Junction is physically occupied by a different train
                    }
                }

                if (requester is AITraffic.Driver.AIEngineer)
                {
                    if (SignalRegistry.IsJunctionOccupiedByPlayer(junction))
                    {
                        return false;
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
                    if (lockInfo.IsExpired)
                    {
                        if (IsJunctionPhysicallyOccupied(junction))
                        {
                            // Train is still occupying the switch: keep lock active!
                            lockInfo.ExpirationTime = Time.time + 15f;
                            return lockInfo.Requester != requester;
                        }

                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                        return false;
                    }

                    if (!IsRequesterAlive(lockInfo.Requester))
                    {
                        if (IsJunctionPhysicallyOccupied(junction))
                        {
                            return true; // Still occupied
                        }
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                        return false;
                    }

                    return lockInfo.Requester != requester;
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
                            // A car is still physically occupying the switch! Do not release!
                            continue;
                        }

                        bool clearedDestination = (info.DestinationTrack != null && newTrack == info.DestinationTrack);
                        bool isPastJunction = false;

                        if (info.Junction != null)
                        {
                            float dist = Vector3.Distance(bogie.transform.position, info.Junction.position);
                            if (dist >= info.ClearanceDistanceMeters)
                            {
                                isPastJunction = true;
                            }
                        }

                        // Only release if rear bogie is on destination track OR is past junction on a track outside the junction
                        if (clearedDestination || (isPastJunction && newTrack != null && !IsTrackPartOfJunction(info.Junction, newTrack)))
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
                    Trainset requesterTrainset = GetTrainsetFromRequester(info.Requester);
                    TrainCar occupyingCar;
                    bool isStillOnJunction = IsJunctionPhysicallyOccupied(info.Junction, out occupyingCar);
                    bool isOccupiedByMyTrain = isStillOnJunction && (requesterTrainset == null || (occupyingCar != null && occupyingCar.trainset == requesterTrainset));

                    if (isOccupiedByMyTrain)
                    {
                        // Still traversing switch: keep lock active
                        if (info.IsExpired)
                        {
                            info.ExpirationTime = Time.time + 20f;
                        }
                        continue;
                    }

                    if (info.IsExpired)
                    {
                        if (info.MonitoredRearBogie != null)
                        {
                            info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                        }
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
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
                    bool onDestination = (info.DestinationTrack != null && bogie.track == info.DestinationTrack);
                    bool pastClearanceMargin = dist >= info.ClearanceDistanceMeters;

                    if (!isStillOnJunction && onDestination && pastClearanceMargin)
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
                        item.ExpirationTime = Time.time + 15f;
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
            if (junction == null) return 15f;

            float maxLen = 15f;
            if (junction.inBranch != null && junction.inBranch.track != null && junction.inBranch.track.curve != null)
            {
                maxLen = Mathf.Max(maxLen, Mathf.Min(junction.inBranch.track.curve.length, 30f));
            }

            if (junction.outBranches != null)
            {
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track != null && branch.track.curve != null)
                    {
                        maxLen = Mathf.Max(maxLen, Mathf.Min(branch.track.curve.length, 30f));
                    }
                }
            }

            return maxLen + 5f; // Extra safety distance buffer
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
        /// </summary>
        public static bool IsJunctionPhysicallyOccupied(Junction junction, out TrainCar occupyingCar)
        {
            occupyingCar = null;
            if (junction == null) return false;

            float clearanceMargin = CalculateClearanceMargin(junction);
            var connectedTracks = GetConnectedTracks(junction);
            if (connectedTracks.Count == 0) return false;

            var inTrack = junction.inBranch != null ? junction.inBranch.track : null;

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

                if (bogies == null || bogies.Count == 0) continue;

                foreach (var bogie in bogies)
                {
                    if (bogie == null || bogie.Car == null) continue;

                    // 1. Check bogie 3D distance to switch points
                    float bogieDist = Vector3.Distance(bogie.transform.position, junction.position);
                    if (bogieDist <= clearanceMargin)
                    {
                        occupyingCar = bogie.Car;
                        return true;
                    }

                    // 2. Check car center 3D distance to switch points
                    float carDist = Vector3.Distance(bogie.Car.transform.position, junction.position);
                    if (carDist <= clearanceMargin)
                    {
                        occupyingCar = bogie.Car;
                        return true;
                    }

                    // 3. Check car straddling across branches
                    var car = bogie.Car;
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
                                occupyingCar = car;
                                return true;
                            }
                        }
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
