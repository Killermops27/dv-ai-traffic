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
                if (requester != null && requester is AITraffic.Driver.AIEngineer)
                {
                    if (SignalRegistry.IsJunctionOccupiedByPlayer(junction))
                    {
                        Log(string.Format("[JunctionController] Junction '{0}' is occupied by the player; AI alignment denied.", junction.name));
                        return false;
                    }
                }

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
                    if (junction.selectedBranch != desiredBranch)
                    {
                        junction.Switch(Junction.SwitchMode.REGULAR, desiredBranch);
                        Log(string.Format("[JunctionController] Junction '{0}' switched to branch {1} for requester '{2}'.", junction.name, desiredBranch, requester));
                    }

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
                    if (lockInfo.IsExpired || !IsRequesterAlive(lockInfo.Requester))
                    {
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

                    if (info.IsExpired || info.Junction == null)
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

                    if (onDestination && pastClearanceMargin)
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
                    ReleaseJunctionInternal(item.Junction, item.Requester);
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

        private float CalculateClearanceMargin(Junction junction)
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

        private bool IsTrackPartOfJunction(Junction junction, RailTrack track)
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
    }
}
