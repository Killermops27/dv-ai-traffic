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

        // CRITICAL INVARIANT [JNC-07]: Latched true only once the requester's own consist physically occupies the junction
        // or its monitored rear bogie comes within ClearanceDistanceMeters. Unentered monitors never release and never protect.
        public bool HasEnteredSwitch { get; set; }

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
        /// <summary>
        /// Physical movable switch blade / point zone in meters.
        /// Movable points in Derail Valley span 0..6.5m from junction.position.
        /// Wheels within this threshold are directly over movable switch blades.
        /// </summary>
        public static float SwitchBladeZoneMeters
        {
            get
            {
                return (AITraffic.Config.IniConfigManager.Instance != null && AITraffic.Config.IniConfigManager.Instance.Dispatcher != null)
                    ? AITraffic.Config.IniConfigManager.Instance.Dispatcher.SwitchBladeZoneMeters
                    : 7.0f;
            }
        }

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

        // CRITICAL INVARIANT [JNC-04]: Re-entrancy flag so AI-initiated switch calls are never blocked by PlayerSwitchLockPatches (See GRAVEYARD.md)
        public static bool IsInternalAISwitchCall { get; private set; }

        public static event Action<Junction, byte, object> OnJunctionSwitched;
        public static event Action<Junction, object> OnJunctionLocked;
        public static event Action<Junction, object> OnJunctionReleased;

        private readonly Dictionary<Junction, JunctionLockInfo> _activeLocks = new Dictionary<Junction, JunctionLockInfo>();
        private readonly List<JunctionLockInfo> _monitoredTrainPassings = new List<JunctionLockInfo>();
        private readonly object _lock = new object();

        public const float StationThroatDwellTimeoutSeconds = 30.0f;
        private readonly Dictionary<object, float> _haltedOutsideThroatStartTime = new Dictionary<object, float>();

        private float _lastCleanupTime = 0f;
        private const float CleanupIntervalSeconds = 2.0f;

        // Per-frame cache of tracks occupied by active AI engineers and player trainset to avoid O(J * E * C) scans in IsJunctionPhysicallyOccupied
        private static int s_cachedActiveTrackBogiesFrame = -1;
        private static readonly Dictionary<RailTrack, List<Bogie>> s_cachedActiveTrackBogies = new Dictionary<RailTrack, List<Bogie>>();
        private static readonly Stack<List<Bogie>> s_bogieListPool = new Stack<List<Bogie>>();
        private static readonly HashSet<Trainset> s_visitedTrainsetsScratch = new HashSet<Trainset>();

        private float _clearanceMonitorTimer = 0f;
        private const float ClearanceMonitorInterval = 0.10f;

        private static void Log(string msg)
        {
            if (Main.IsDebugLoggingEnabled)
            {
                Main.LogDebug("[AITraffic] " + msg);
            }
        }

        private static void LogWarning(string msg)
        {
            Main.LogWarning("[AITraffic] " + msg);
        }

        private static void LogError(string msg)
        {
            Main.LogError("[AITraffic] " + msg);
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
#if DEBUG
            long __wdStart = AITraffic.Diagnostics.HitchWatchdog.GetTimestamp();
            long __wdMem = AITraffic.Diagnostics.HitchWatchdog.GetCurrentMemory();
            try {
#endif
            if (Time.time - _lastCleanupTime >= CleanupIntervalSeconds)
            {
                _lastCleanupTime = Time.time;
                ClearExpiredLocks();
            }

            UpdateTrainClearanceMonitoring();
#if DEBUG
            } finally {
                AITraffic.Diagnostics.HitchWatchdog.RecordJunctionController(__wdStart, __wdMem);
            }
#endif
        }

        private readonly Dictionary<Junction, float> _lastOccupiedLogTime = new Dictionary<Junction, float>();

        private void OnDestroy()
        {
            lock (_lock)
            {
                UnsubscribeAllBogieListeners();
                _activeLocks.Clear();
                _monitoredTrainPassings.Clear();
                _haltedOutsideThroatStartTime.Clear();
                _lastOccupiedLogTime.Clear();
                s_cachedActiveTrackBogiesFrame = -1;
                s_cachedActiveTrackBogies.Clear();
            }
        }

        /// <summary>
        /// Requests switching a junction to a desired branch safely if not locked by another entity.
        /// </summary>
        public bool RequestJunctionAlignment(Junction junction, byte desiredBranch, object requester, bool lockAfterSwitch = true)
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
                    float now = Time.time;
                    float lastLog;
                    if (!_lastOccupiedLogTime.TryGetValue(junction, out lastLog) || (now - lastLog) >= 30.0f)
                    {
                        _lastOccupiedLogTime[junction] = now;
                        if (Main.IsDebugLoggingEnabled)
                        {
                            Log(string.Format("[JunctionController] Junction '{0}' is physically OCCUPIED by car '{1}'; alignment to branch {2} strictly DENIED for requester '{3}'.",
                                junction.name, occupyingCar != null ? AITraffic.Compat.ModCompatManager.GetCarId(occupyingCar) : "unknown", desiredBranch, requester));
                        }
                    }
                    return false;
                }

                // 2. Player occupancy / presence check (respects Ride-Along mode and ignores requester's train)
                if (requester != null && requester is AITraffic.Driver.AIEngineer)
                {
                    var eng = (AITraffic.Driver.AIEngineer)requester;
                    bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);
                    bool protectLock = eng.ShouldProtectJunctionLock(junction);
                    if (!rideAlong && !protectLock)
                    {
                        Trainset pSet;
                        Vector3 pPos;
                        float pSpeed;
                        bool isPlayerMoving = SignalRegistry.TryGetPlayerTrainInfo(out pSet, out pPos, out pSpeed) && pSpeed >= 1.0f;
                        if (isPlayerMoving && SignalRegistry.IsJunctionOccupiedByPlayer(junction, requesterTrainset))
                        {
                            Log(string.Format("[JunctionController] Junction '{0}' is occupied by moving player traffic; AI alignment denied.", junction.name));
                            return false;
                        }

                        if (SignalRegistry.IsJunctionReservedByPlayerSignal(junction))
                        {
                            Log(string.Format("[JunctionController] Junction '{0}' is reserved by player signal; AI alignment denied.", junction.name));
                            return false;
                        }
                    }
                }

                // 3. Lock check
                JunctionLockInfo existingLock;
                if (_activeLocks.TryGetValue(junction, out existingLock))
                {
                    if (ShouldReleaseThroatLockForDwell(junction, existingLock))
                    {
                        Log(string.Format("[JunctionController] Released turnout lock on '{0}' held by '{1}' after dwell timeout outside station throat (siding occupied) to allow alignment by '{2}'.",
                            junction.name, existingLock.Requester, requester));
                        ReleaseJunctionInternal(junction, existingLock.Requester);
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
                    // CRITICAL INVARIANT [JNC-04]: Forced switch mode and internal call flag bypass player switch locks (See GRAVEYARD.md)
                    IsInternalAISwitchCall = true;
                    try
                    {
                        junction.Switch(Junction.SwitchMode.FORCED, desiredBranch);
                    }
                    finally
                    {
                        IsInternalAISwitchCall = false;
                    }
                    Log(string.Format("[JunctionController] Junction '{0}' switched to branch {1} for requester '{2}'.", junction.name, desiredBranch, requester));

                    if (lockAfterSwitch)
                    {
                        // Immediately lock switch to protect newly aligned route
                        TryLockJunction(junction, requester, 60f);
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
                    var eng = (AITraffic.Driver.AIEngineer)requester;
                    bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);
                    bool protectLock = eng.ShouldProtectJunctionLock(junction);
                    if (!rideAlong && !protectLock)
                    {
                        Trainset requesterTrainset = GetTrainsetFromRequester(requester);
                        if (SignalRegistry.IsJunctionOccupiedByPlayer(junction, requesterTrainset))
                        {
                            return false;
                        }
                    }

                    if (!protectLock && SignalRegistry.IsJunctionReservedByPlayerSignal(junction))
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
        /// Checks whether a junction is currently registered and actively monitored for train clearance
        /// (e.g. rear bogie passing clearance monitoring).
        /// </summary>
        public bool IsJunctionMonitoredForClearance(Junction junction, object requester = null)
        {
            if (junction == null) return false;
            lock (_lock)
            {
                for (int i = 0; i < _monitoredTrainPassings.Count; i++)
                {
                    var info = _monitoredTrainPassings[i];
                    // CRITICAL INVARIANT [JNC-07]: Only monitors whose consist has physically entered the switch count as active clearance protection.
                    if (info != null && info.Junction == junction && info.HasEnteredSwitch)
                    {
                        if (requester == null || info.Requester == requester)
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Releases a junction lock held by the requester, unless the train is actively monitored traversing
        /// the turnout or rolling stock is physically occupying the switch mechanism.
        /// </summary>
        // CRITICAL INVARIANT [JNC-01] [QUIRK-02]: Active Rear Bogie Clearance Monitoring
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
                        // Absolute physical safety invariant 1: Never release if train is actively monitored traversing switch!
                        if (IsJunctionMonitoredForClearance(junction, lockInfo.Requester))
                        {
                            return; // Retain lock until rear bogie clears clearance margin
                        }

                        // Absolute physical safety invariant 2: Never release if rolling stock is physically occupying junction!
                        TrainCar occupyingCar;
                        if (IsJunctionPhysicallyOccupied(junction, out occupyingCar))
                        {
                            return; // Retain lock while wheels/cars are physically on the switch
                        }

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
                    if (ShouldReleaseThroatLockForDwell(junction, lockInfo))
                    {
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
                        return false;
                    }

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

        /// <summary>
        /// Checks whether a junction is currently locked by an active AI engineer that is moving at speed
        /// or protecting its route through an entered signal block.
        /// </summary>
        public bool IsJunctionLockedByAI(Junction junction)
        {
            if (junction == null) return false;

            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    if (lockInfo.IsExpired)
                    {
                        return false;
                    }

                    var eng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
                    if (eng != null)
                    {
                        if (ShouldReleaseThroatLockForDwell(junction, lockInfo))
                        {
                            Log(string.Format("[JunctionController] AI turnout lock on '{0}' released after dwell timeout outside throat (siding occupied).", junction.name));
                            ReleaseJunctionInternal(junction, lockInfo.Requester);
                            return false;
                        }

                        // Absolute physical safety: While train is actively monitored traversing switch or physically occupying it,
                        // lock is 100% protected against player or opposing train intervention!
                        if (IsJunctionMonitoredForClearance(junction, eng) || IsJunctionPhysicallyOccupied(junction))
                        {
                            return true;
                        }

                        // AI engineer holds lock: protected against player intervention only if engineer actively protects it
                        if (eng.ShouldProtectJunctionLock(junction))
                        {
                            return true;
                        }
                        else
                        {
                            // Lock is no longer protected (e.g. passed behind train or covered by red signal / player reserved); release it immediately
                            ReleaseJunctionInternal(junction, lockInfo.Requester);
                            return false;
                        }
                    }
                    else if (lockInfo.Requester != null)
                    {
                        // CRITICAL INVARIANT [WRK-04]: A destroyed Unity requester (e.g. AIEngineer destroyed on worker arrival/cancel)
                        // can never hold a turnout lock. Purge all its locks and monitors instead of reporting a "ghost AI" lock.
                        var unityRequester = lockInfo.Requester as UnityEngine.Object;
                        if (!ReferenceEquals(unityRequester, null) && unityRequester == null)
                        {
                            ReleaseAllLocksFor(lockInfo.Requester);
                            return false;
                        }
                        return true;
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
        /// Pure read-only check whether the locking AI engineer can safely yield a junction to the player
        /// by stopping at an intervening governing main signal. Safe to call on hover (no state mutation).
        /// </summary>
        // CRITICAL INVARIANT [JNC-08]: Non-Mutating Hover Preview vs Actuation Yield Execution
        public bool CanAIYieldJunctionToPlayer(Junction junction)
        {
            if (junction == null) return false;

            AITraffic.Driver.AIEngineer eng = null;
            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (!_activeLocks.TryGetValue(junction, out lockInfo) || lockInfo.IsExpired)
                {
                    return true;
                }

                if (IsJunctionMonitoredForClearance(junction, lockInfo.Requester) || IsJunctionPhysicallyOccupied(junction))
                {
                    return false;
                }

                eng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
            }

            return eng != null && eng.CanYieldToPlayerAtSwitch(junction);
        }

        /// <summary>
        /// Attempts to have the locking AI engineer yield the specified junction (and any downstream switches
        /// beyond its governing main signal) to a player switch command.
        /// </summary>
        // CRITICAL INVARIANT [JNC-08]: Main Signal Yield Protocol for Player Switch Contention
        public bool TryYieldJunctionToPlayer(Junction junction)
        {
            if (junction == null) return false;

            AITraffic.Driver.AIEngineer eng = null;
            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (!_activeLocks.TryGetValue(junction, out lockInfo) || lockInfo.IsExpired)
                {
                    return true;
                }

                if (IsJunctionMonitoredForClearance(junction, lockInfo.Requester) || IsJunctionPhysicallyOccupied(junction))
                {
                    return false;
                }

                eng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
            }

            return eng != null && eng.TryRequestPlayerSwitchYield(junction);
        }

        /// <summary>
        /// Releases the contended junction and all downstream junction locks held by the requester that are not in the
        /// train's immediate entered block, while strictly enforcing physical occupancy and clearance invariants.
        /// </summary>
        // CRITICAL INVARIANT [JNC-01] [JNC-08]: Safe Downstream Lock Release on Main Signal Yield
        public void ReleaseDownstreamLocksForYield(object requester, Junction targetJunction, IList<JunctionSwitchInfo> immediateBlockSwitches)
        {
            if (requester == null) return;

            lock (_lock)
            {
                var keys = _activeLocks.Where(kvp => kvp.Value.Requester == requester).Select(kvp => kvp.Key).ToList();
                for (int i = 0; i < keys.Count; i++)
                {
                    Junction junc = keys[i];
                    if (junc == null) continue;

                    if (junc != targetJunction && immediateBlockSwitches != null)
                    {
                        bool isImmediate = false;
                        for (int k = 0; k < immediateBlockSwitches.Count; k++)
                        {
                            if (immediateBlockSwitches[k].Junction == junc)
                            {
                                isImmediate = true;
                                break;
                            }
                        }
                        if (isImmediate) continue;
                    }

                    if (IsJunctionMonitoredForClearance(junc, requester)) continue;

                    TrainCar occupyingCar;
                    if (IsJunctionPhysicallyOccupied(junc, out occupyingCar)) continue;

                    ReleaseJunctionInternal(junc, requester);
                }
            }
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

                _haltedOutsideThroatStartTime.Remove(requester);
            }
        }

        /// <summary>
        /// Gets all junctions currently locked by the specified requester.
        /// </summary>
        public List<Junction> GetLockedJunctionsFor(object requester)
        {
            var result = new List<Junction>();
            GetLockedJunctionsFor(requester, result);
            return result;
        }

        public void GetLockedJunctionsFor(object requester, List<Junction> result)
        {
            if (result == null) return;
            result.Clear();
            if (requester == null) return;

            lock (_lock)
            {
                foreach (var kvp in _activeLocks)
                {
                    if (kvp.Value != null && kvp.Value.Requester == requester && !kvp.Value.IsExpired)
                    {
                        result.Add(kvp.Key);
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

                    // Player Signal Priority: If reserved by player signal, immediately purge AI lock and allow player alignment UNLESS AI is actively moving at speed in this block!
                    if (SignalRegistry.IsJunctionReservedByPlayerSignal(junction))
                    {
                        var eng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
                        if (eng != null && !eng.ShouldProtectJunctionLock(junction))
                        {
                            ReleaseJunctionInternal(junction, lockInfo.Requester);
                            return false;
                        }
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

                    // Station throat dwell release: If the lock holder is an AI train halted outside station throat
                    // because the assigned destination siding is occupied, release lock after dwell timeout to unblock player shunting
                    if (ShouldReleaseThroatLockForDwell(junction, lockInfo))
                    {
                        Log(string.Format("[JunctionController] Turnout lock on '{0}' released for halted train '{1}' after dwell timeout (destination siding occupied) to unblock player shunting.",
                            junction.name, lockInfo.Requester));
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
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
        /// Determines whether a turnout lock held by an AI train halted outside a station throat
        /// should be released after dwell timeout because the assigned destination siding is occupied,
        /// unblocking player shunting operations.
        /// </summary>
        // CRITICAL INVARIANT [JNC-06]: Station Throat Dwell Timeout Release (See GRAVEYARD.md)
        public bool ShouldReleaseThroatLockForDwell(Junction junction, JunctionLockInfo lockInfo, float dwellTimeout = StationThroatDwellTimeoutSeconds)
        {
            if (junction == null || lockInfo == null) return false;
            if (lockInfo.Requester == null) return true;

            var eng = lockInfo.Requester as AITraffic.Driver.AIEngineer;
            Trainset engTrainset = (eng != null && eng.TrainCar != null)
                ? eng.TrainCar.trainset
                : GetTrainsetFromRequester(lockInfo.Requester);

            // CRITICAL INVARIANT [JNC-06]: Pass requester trainset to avoid self-blocking in frog zone during dwell release (See GRAVEYARD.md)
            TrainCar occupyingCar;
            if (IsJunctionPhysicallyOccupied(junction, out occupyingCar, engTrainset))
            {
                return false;
            }

            if (eng == null) return false;

            // Train must be halted (stopped or creeping < 1.5 km/h) outside the station throat
            if (eng.CurrentSpeedKmh >= 1.5f)
            {
                _haltedOutsideThroatStartTime.Remove(eng);
                return false;
            }

            // Identify assigned destination siding / track
            RailTrack destTrack = null;
            if (eng.CurrentPath != null && eng.CurrentPath.Tracks != null && eng.CurrentPath.Tracks.Count > 0)
            {
                destTrack = eng.CurrentPath.Tracks[eng.CurrentPath.Tracks.Count - 1];
            }
            else if (lockInfo.DestinationTrack != null)
            {
                destTrack = lockInfo.DestinationTrack;
            }
            else if (!string.IsNullOrEmpty(eng.DestinationTrackName) && RailGraph.Instance != null)
            {
                destTrack = RailGraph.Instance.GetTrackByName(eng.DestinationTrackName);
            }

            // Check if the assigned destination siding is physically occupied by other rolling stock
            bool isDestOccupied = false;
            if (destTrack != null && RailGraph.Instance != null)
            {
                isDestOccupied = RailGraph.Instance.IsTrackOccupied(destTrack, null, engTrainset) ||
                                 SignalRegistry.IsTrackOccupiedByPlayer(destTrack, engTrainset);
            }
            else
            {
                // Fallback: train is stopped behind an obstacle near station (< 300m) while targeting station/siding
                if ((eng.IsStationDestination || eng.IsTerminusDestination) && eng.DistanceToObstacle < 300.0f)
                {
                    isDestOccupied = true;
                }
            }

            if (!isDestOccupied)
            {
                _haltedOutsideThroatStartTime.Remove(eng);
                return false;
            }

            // Track dwell time while halted outside throat waiting for occupied destination siding
            float startTime;
            if (!_haltedOutsideThroatStartTime.TryGetValue(eng, out startTime))
            {
                startTime = lockInfo.LockedTime > 0f ? Mathf.Min(lockInfo.LockedTime, Time.time) : Time.time;
                _haltedOutsideThroatStartTime[eng] = startTime;
            }

            float elapsedDwell = Time.time - startTime;
            float effectiveTimeout = dwellTimeout > 0f ? dwellTimeout : StationThroatDwellTimeoutSeconds;
            if (eng.StationDwellDuration > 0f)
            {
                effectiveTimeout = Mathf.Min(effectiveTimeout, eng.StationDwellDuration);
            }

            return elapsedDwell >= effectiveTimeout;
        }

        /// <summary>
        /// Attempts to release the turnout lock on the specified junction if held by an AI train
        /// that is halted outside a station throat because its assigned destination siding is occupied,
        /// after dwell timeout has elapsed.
        /// </summary>
        public bool TryReleaseThroatLockAfterDwell(Junction junction, float dwellTimeout = StationThroatDwellTimeoutSeconds)
        {
            if (junction == null) return false;
            lock (_lock)
            {
                JunctionLockInfo lockInfo;
                if (_activeLocks.TryGetValue(junction, out lockInfo))
                {
                    if (ShouldReleaseThroatLockForDwell(junction, lockInfo, dwellTimeout))
                    {
                        Log(string.Format("[JunctionController] Releasing turnout lock on '{0}' held by '{1}' after dwell timeout (destination siding occupied) to unblock player shunting.",
                            junction.name, lockInfo.Requester));
                        ReleaseJunctionInternal(junction, lockInfo.Requester);
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
                // CRITICAL INVARIANT [JNC-07]: Never attach a clearance monitor to a lock held by a different requester.
                if (_activeLocks.TryGetValue(junction, out lockInfo) && lockInfo.Requester == trainRequester)
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
                        // CRITICAL INVARIANT [JNC-07]: ...and only after the consist has actually entered the switch.
                        if (pastClearanceMargin && info.HasEnteredSwitch)
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
            if (_monitoredTrainPassings.Count == 0)
                return;

            _clearanceMonitorTimer -= Time.deltaTime;
            if (_clearanceMonitorTimer > 0f)
                return;
            _clearanceMonitorTimer = ClearanceMonitorInterval;

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

                    var rearBogie = info.MonitoredRearBogie;
                    if (rearBogie != null && info.Junction != null)
                    {
                        float distToJunction = Vector3.Distance(rearBogie.transform.position, info.Junction.position);
                        if (distToJunction < info.ClearanceDistanceMeters)
                        {
                            // [JNC-07]: Rear bogie inside clearance margin => consist has reached the switch
                            info.HasEnteredSwitch = true;
                            info.ExpirationTime = Mathf.Max(info.ExpirationTime, Time.time + 30f);
                            continue;
                        }
                    }

                    // Check if train cars are still physically on the junction
                    TrainCar occupyingCar;
                    bool isStillOnJunction = IsJunctionPhysicallyOccupied(info.Junction, out occupyingCar);

                    if (isStillOnJunction)
                    {
                        // [JNC-07]: Latch entry only when the occupant belongs to the requester's own consist
                        if (!info.HasEnteredSwitch && occupyingCar != null)
                        {
                            Trainset requesterTrainset = null;
                            var requesterEngineer = info.Requester as AITraffic.Driver.AIEngineer;
                            if (requesterEngineer != null && requesterEngineer.TrainCar != null)
                            {
                                requesterTrainset = requesterEngineer.TrainCar.trainset;
                            }
                            else
                            {
                                var requesterCar = info.Requester as TrainCar;
                                if (requesterCar != null) requesterTrainset = requesterCar.trainset;
                            }
                            if (requesterTrainset != null && occupyingCar.trainset == requesterTrainset)
                            {
                                info.HasEnteredSwitch = true;
                            }
                        }

                        // Still traversing switch: keep lock active and extend expiration!
                        info.ExpirationTime = Mathf.Max(info.ExpirationTime, Time.time + 30f);
                        continue;
                    }

                    // CRITICAL INVARIANT [JNC-07]: An armed-but-unentered monitor (train still approaching or halted before the switch)
                    // must NEVER release the lock. If the lock was dropped/replaced by another path ([QUIRK-05], [JNC-05], [JNC-06], expiry),
                    // discard this orphaned monitor WITHOUT calling ReleaseJunctionInternal.
                    if (!info.HasEnteredSwitch)
                    {
                        JunctionLockInfo currentLock;
                        if (!_activeLocks.TryGetValue(info.Junction, out currentLock) || currentLock != info)
                        {
                            if (info.MonitoredRearBogie != null)
                            {
                                info.MonitoredRearBogie.TrackChanged -= OnBogieTrackChanged;
                            }
                            _monitoredTrainPassings.RemoveAt(i);
                        }
                        continue;
                    }

                    var bogie = info.MonitoredRearBogie;
                    if (bogie == null || bogie.HasDerailed)
                    {
                        _monitoredTrainPassings.RemoveAt(i);
                        ReleaseJunctionInternal(info.Junction, info.Requester);
                        continue;
                    }

                    // CRITICAL INVARIANT [JNC-01]: Rear bogie has cleared info.ClearanceDistanceMeters (verified at line 873)
                    // and junction is physically unoccupied (verified at line 884); safe to release turnout lock.
                    bogie.TrackChanged -= OnBogieTrackChanged;
                    _monitoredTrainPassings.RemoveAt(i);
                    ReleaseJunctionInternal(info.Junction, info.Requester);
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
            if (Main.IsDebugLoggingEnabled)
            {
                Log(string.Format("[JunctionController] Junction '{0}' lock released for requester '{1}'.", junction.name, requester));
            }
        }

        private static readonly List<JunctionLockInfo> s_expiredLocksScratch = new List<JunctionLockInfo>();

        public void ClearExpiredLocks()
        {
            lock (_lock)
            {
                s_expiredLocksScratch.Clear();
                foreach (var lockItem in _activeLocks.Values)
                {
                    if (lockItem.IsExpired || ShouldReleaseThroatLockForDwell(lockItem.Junction, lockItem))
                        s_expiredLocksScratch.Add(lockItem);
                }

                for (int i = 0; i < s_expiredLocksScratch.Count; i++)
                {
                    var item = s_expiredLocksScratch[i];
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
                s_expiredLocksScratch.Clear();
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
            PopulateConnectedTracks(junction, tracks);
            return tracks;
        }

        private static void PopulateConnectedTracks(Junction junction, List<RailTrack> tracks)
        {
            tracks.Clear();
            if (junction == null) return;

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
        private static readonly List<RailTrack> s_connectedTracksScratch = new List<RailTrack>(8);
        private static readonly Dictionary<Trainset, HashSet<RailTrack>> s_trainsetToTracksScratch = new Dictionary<Trainset, HashSet<RailTrack>>();
        private static readonly Dictionary<Trainset, List<TrainCar>> s_trainsetToCarsScratch = new Dictionary<Trainset, List<TrainCar>>();
        private static readonly Stack<HashSet<RailTrack>> s_trackSetPool = new Stack<HashSet<RailTrack>>();
        private static readonly Stack<List<TrainCar>> s_carListPool = new Stack<List<TrainCar>>();

        private static void ClearAndRecycleOccupancyScratch()
        {
            foreach (var kvp in s_trainsetToTracksScratch)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.Clear();
                    s_trackSetPool.Push(kvp.Value);
                }
            }
            s_trainsetToTracksScratch.Clear();

            foreach (var kvp in s_trainsetToCarsScratch)
            {
                if (kvp.Value != null)
                {
                    kvp.Value.Clear();
                    s_carListPool.Push(kvp.Value);
                }
            }
            s_trainsetToCarsScratch.Clear();
        }

        private static void ProcessBogieOccupancy(
            Bogie bogie,
            RailTrack track,
            Junction junction,
            RailTrack inTrack,
            List<RailTrack> connectedTracks,
            Trainset requesterTrainset,
            ref TrainCar detectedCar,
            ref TrainCar externalCar,
            ref TrainCar requesterBladeCar)
        {
            if (bogie == null || bogie.Car == null) return;
            var car = bogie.Car;

            if (car.trainset != null)
            {
                HashSet<RailTrack> tSet;
                if (!s_trainsetToTracksScratch.TryGetValue(car.trainset, out tSet))
                {
                    tSet = s_trackSetPool.Count > 0 ? s_trackSetPool.Pop() : new HashSet<RailTrack>();
                    s_trainsetToTracksScratch[car.trainset] = tSet;
                }
                if (track != null) tSet.Add(track);

                List<TrainCar> cList;
                if (!s_trainsetToCarsScratch.TryGetValue(car.trainset, out cList))
                {
                    cList = s_carListPool.Count > 0 ? s_carListPool.Pop() : new List<TrainCar>(16);
                    s_trainsetToCarsScratch[car.trainset] = cList;
                }
                if (!cList.Contains(car)) cList.Add(car);
            }

            bool isRequester = (requesterTrainset != null && car.trainset == requesterTrainset);

            // 1. Check bogie 3D distance to movable switch points / blades (7.0m physical blade zone)
            // Wheels within this distance are directly on the movable switch rails.
            // Parked yard stock further down siding tracks (> 7m) does not foul the movable blades.
            float bogieDist = Vector3.Distance(bogie.transform.position, junction.position);
            if (bogieDist <= SwitchBladeZoneMeters)
            {
                if (detectedCar == null) detectedCar = car;
                if (!isRequester)
                {
                    if (externalCar == null) externalCar = car;
                }
                else
                {
                    // Requester's wheels are directly over movable switch blades/points
                    if (requesterBladeCar == null) requesterBladeCar = car;
                }
            }

            // 2. Active Route Fouling Zone (0..35.0m frog clearance distance):
            // If the bogie is on inBranch OR on the currently selected outBranch (junction.selectedBranch),
            // wheels within 35.0m are physically on the switch frog and turnout lead rails!
            // Any switch command while wheels are traversing the active route will split or derail the train.
            // Restrict to external trains (!isRequester): SwitchBladeZoneMeters (7m), single-car straddling,
            // and multi-branch spanning already protect requester, avoiding false denial when approaching 7m-35m out.
            if (!isRequester)
            {
                bool isOnActiveRoute = false;
                if (inTrack != null && track == inTrack)
                {
                    isOnActiveRoute = true;
                }
                else if (junction.outBranches != null && junction.selectedBranch < junction.outBranches.Count)
                {
                    var selBranch = junction.outBranches[junction.selectedBranch];
                    if (selBranch != null && selBranch.track == track)
                    {
                        isOnActiveRoute = true;
                    }
                }

                if (isOnActiveRoute && bogieDist <= 35.0f)
                {
                    if (detectedCar == null) detectedCar = car;
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
        }

        private static void EnsureActiveTrackBogiesFrameCache()
        {
            int frame = Time.frameCount;
            if (s_cachedActiveTrackBogiesFrame == frame)
                return;

            s_cachedActiveTrackBogiesFrame = frame;
            foreach (var kvp in s_cachedActiveTrackBogies)
            {
                kvp.Value.Clear();
                s_bogieListPool.Push(kvp.Value);
            }
            s_cachedActiveTrackBogies.Clear();
            s_visitedTrainsetsScratch.Clear();

            if (AITraffic.Core.TrafficManager.Instance != null && AITraffic.Core.TrafficManager.Instance.ActiveEngineers != null)
            {
                var engineers = AITraffic.Core.TrafficManager.Instance.ActiveEngineers;
                for (int i = 0; i < engineers.Count; i++)
                {
                    var eng = engineers[i];
                    if (eng == null || eng.TrainCar == null) continue;
                    var ts = eng.TrainCar.trainset;
                    if (ts != null && s_visitedTrainsetsScratch.Add(ts) && ts.cars != null)
                    {
                        IndexTrainsetBogies(ts.cars);
                    }
                }
            }

            var playerCar = PlayerManager.Car;
            if (playerCar != null && playerCar.trainset != null && s_visitedTrainsetsScratch.Add(playerCar.trainset) && playerCar.trainset.cars != null)
            {
                IndexTrainsetBogies(playerCar.trainset.cars);
            }
        }

        private static void IndexTrainsetBogies(List<TrainCar> cars)
        {
            for (int c = 0; c < cars.Count; c++)
            {
                var car = cars[c];
                if (car == null) continue;
                AddBogieToFrameCache(car.FrontBogie);
                AddBogieToFrameCache(car.RearBogie);
            }
        }

        private static void AddBogieToFrameCache(Bogie b)
        {
            if (b == null || b.Car == null || b.track == null)
                return;

            List<Bogie> list;
            if (!s_cachedActiveTrackBogies.TryGetValue(b.track, out list))
            {
                list = s_bogieListPool.Count > 0 ? s_bogieListPool.Pop() : new List<Bogie>(4);
                s_cachedActiveTrackBogies[b.track] = list;
            }
            list.Add(b);
        }

        public static bool IsJunctionPhysicallyOccupied(Junction junction, out TrainCar occupyingCar, Trainset requesterTrainset = null)
        {
            occupyingCar = null;
            if (junction == null) return false;

            var connectedTracks = s_connectedTracksScratch;
            PopulateConnectedTracks(junction, connectedTracks);
            if (connectedTracks.Count == 0) return false;

            var inTrack = junction.inBranch != null ? junction.inBranch.track : null;

            // Map each trainset detected on any connected track to the tracks its bogies are on
            ClearAndRecycleOccupancyScratch();
            TrainCar detectedCar = null;
            TrainCar externalCar = null;
            TrainCar requesterBladeCar = null;

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
                        ProcessBogieOccupancy(bogie, track, junction, inTrack, connectedTracks, requesterTrainset, ref detectedCar, ref externalCar, ref requesterBladeCar);
                    }
                }
            }

            // Also inspect live active AI trains and player train via per-frame bogie cache to guarantee zero misses from collider trigger lag
            EnsureActiveTrackBogiesFrameCache();
            for (int t = 0; t < connectedTracks.Count; t++)
            {
                var track = connectedTracks[t];
                if (track == null) continue;

                List<Bogie> cachedBogies;
                if (s_cachedActiveTrackBogies.TryGetValue(track, out cachedBogies) && cachedBogies != null)
                {
                    for (int b = 0; b < cachedBogies.Count; b++)
                    {
                        ProcessBogieOccupancy(cachedBogies[b], track, junction, inTrack, connectedTracks, requesterTrainset, ref detectedCar, ref externalCar, ref requesterBladeCar);
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
            foreach (var kvp in s_trainsetToTracksScratch)
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
                    s_trainsetToCarsScratch.TryGetValue(kvp.Key, out cList);
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
                        s_trainsetToCarsScratch.TryGetValue(kvp.Key, out cList);
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

        /// <summary>
        /// Checks whether a junction is physically occupied by an AI train (ambient or worker).
        /// Used by Harmony patches to protect AI traffic while preserving player agency to derail their own cars if desired.
        /// </summary>
        // CRITICAL INVARIANT [JNC-04]: Preserving Player Shunting Agency on Unlocked Switches (See GRAVEYARD.md)
        public static bool IsJunctionPhysicallyOccupiedByAI(Junction junction)
        {
            if (junction == null) return false;
            TrainCar occupyingCar;
            if (IsJunctionPhysicallyOccupied(junction, out occupyingCar))
            {
                if (occupyingCar != null)
                {
                    // CRITICAL INVARIANT [WRK-04]: Only ACTIVE worker hauls count as AI here; completed hauls are player consists ([JNC-04]).
                    if (AITraffic.Compat.ModCompatManager.IsAmbientAITrain(occupyingCar) ||
                        occupyingCar.GetComponent<AITraffic.Compat.AITrafficCarMarker>() != null)
                    {
                        return true;
                    }
                    if (AITraffic.Core.TrafficManager.Instance != null && AITraffic.Core.TrafficManager.Instance.ActiveEngineers != null)
                    {
                        var active = AITraffic.Core.TrafficManager.Instance.ActiveEngineers;
                        for (int i = 0; i < active.Count; i++)
                        {
                            var eng = active[i];
                            if (eng != null && eng.TrainCar != null && eng.TrainCar.trainset == occupyingCar.trainset)
                                return true;
                        }
                    }
                    if (AITraffic.Workers.WorkerManager.IsTrainCarInActiveWorkerTask(occupyingCar))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
    }
}
