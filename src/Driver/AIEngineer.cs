using System;
using System.Collections;
using System.Collections.Generic;
using DV.Simulation.Cars;
using Signals.Game;
using UnityEngine;
using DVSignal = Signals.Game.Signal;

namespace AITraffic.Driver
{
    /// <summary>
    /// Operating state of the locomotive AI engineer.
    /// </summary>
    public enum EngineState
    {
        Idle,
        Starting,
        Accelerating,
        Cruising,
        Coasting,
        Braking,
        StationHold,
        TerminusStop
    }

    /// <summary>
    /// Autonomous driving AI agent that controls the locomotive through TrainCar.SimController.controlsOverrider.
    /// Manages throttle, train air brake, dynamic brake, independent brake, reverser, horn, and sander.
    /// Implements smooth PID regulation, traction anti-slip control, level crossing horn sequences,
    /// and station dwell timing.
    /// </summary>
    public class AIEngineer : MonoBehaviour
    {
        #region State Machine & Properties

        [SerializeField]
        private TrainCar _trainCar;
        public TrainCar TrainCar
        {
            get { return _trainCar; }
        }

        [SerializeField]
        private EngineState _state;
        public EngineState State
        {
            get { return _state; }
            private set
            {
                if (_state != value)
                {
                    EngineState prev = _state;
                    _state = value;
                    if (OnStateChanged != null)
                    {
                        OnStateChanged(prev, _state);
                    }
                }
            }
        }

        public event Action<EngineState, EngineState> OnStateChanged;

        public float CurrentSpeedKmh { get; private set; }
        public float CurrentSpeedMs { get; private set; }
        public float TargetSpeedKmh { get; private set; }
        public float TargetSpeedMs { get; private set; }
        public float TargetDirection { get; set; } // 1.0f for forward, -1.0f for reverse

        public PIDController ThrottlePID { get; private set; }
        public PIDController BrakePID { get; private set; }
        public SpeedProfileGenerator SpeedProfiler { get; private set; }

        private readonly List<RailTrack> _upcomingTracks = new List<RailTrack>();
        public List<RailTrack> UpcomingTracks
        {
            get { return _upcomingTracks; }
        }

        private readonly List<Vector3> _levelCrossings = new List<Vector3>();
        public List<Vector3> LevelCrossings
        {
            get { return _levelCrossings; }
        }

        public DVSignal ApproachingSignal { get; set; }
        public float DistanceToSignal { get; set; }
        public float DistanceToObstacle { get; set; }
        public float DistanceToDestination { get; set; }
        public bool IsStationDestination { get; set; }
        public bool IsTerminusDestination { get; set; }
        public List<AITraffic.Navigation.SignalBlockInfo> UpcomingSignalBlocks
        {
            get { return _upcomingSignalBlocks; }
        }
        private readonly List<AITraffic.Navigation.SignalBlockInfo> _upcomingSignalBlocks = new List<AITraffic.Navigation.SignalBlockInfo>();

        public string OriginStationName { get; set; }
        public string DestinationStationName { get; set; }
        public string DestinationTrackName { get; set; }

        public AITraffic.Navigation.RailPath CurrentPath { get; set; }
        public int CurrentPathTrackIndex { get; private set; }

        public float StationDwellDuration { get; set; } // 30-60s
        public float DwellTimeRemaining { get; private set; }

        public float SpeedToleranceKmh { get; set; }
        public float ThrottleSlewRate { get; set; }      // Max throttle change / sec
        public float BrakeSlewRate { get; set; }         // Max train brake change / sec
        public float DynamicBrakeSlewRate { get; set; }  // Max dynamic brake change / sec

        public float CommandedThrottle { get { return _commandedThrottle; } }
        public float CommandedTrainBrake { get { return _commandedTrainBrake; } }
        public float CommandedDynamicBrake { get { return _commandedDynamicBrake; } }
        public float CommandedIndependentBrake { get { return _commandedIndependentBrake; } }
        public float CommandedReverser { get { return _commandedReverser; } }

        public float CurrentThrottle { get { return _currentThrottle; } }
        public float CurrentTrainBrake { get { return _currentTrainBrake; } }
        public float CurrentDynamicBrake { get { return _currentDynamicBrake; } }
        public float CurrentIndependentBrake { get { return _currentIndependentBrake; } }
        public float CurrentReverser { get { return _currentReverser; } }

        public DM3TransmissionController DM3Controller { get { return _dm3Controller; } }
        public SpeedProfileResult CurrentSpeedProfile { get; private set; }
        public float SpawnTime { get; private set; }
        public float StationaryTimer { get; private set; }

        public bool IsWorkerDriven { get; set; }
        public event Action<AIEngineer> OnTerminusArrival;

        #endregion

        #region Internal State & Control Values

        private DM3TransmissionController _dm3Controller;
        private BaseControlsOverrider _controlsOverrider;
        private bool _hasDynamicBrake;

        private float _commandedThrottle;
        private float _commandedTrainBrake;
        private float _commandedDynamicBrake;
        private float _commandedIndependentBrake;
        private float _commandedReverser;
        private float _desiredReverser = 1.0f;

        private float _currentThrottle;
        private float _currentTrainBrake;
        private float _currentDynamicBrake;
        private float _currentIndependentBrake;
        private float _currentReverser;
        private float _brakeHoldTimer;

        // Wheel slip recovery & sander interval control
        private bool _isWheelSlipping;
        private float _sanderActiveTimer;
        private float _sanderRestTimer;
        private bool _sanderRequested;
        private float _slipThrottleReduction;

        // Acceleration feedback & soft-launch throttle
        public float CurrentAccelerationMs2 { get; private set; }
        private float _lastSpeedMs;
        private float _smoothAccMs2;
        private float _rampThrottle;
        private float _hillAssistBoost;
        private float _stallRestartCooldown;

        // Level crossing horn sequence
        private bool _isHornPatternActive;
        private float _hornStepTimer;
        private int _hornStepIndex;

        // Startup sequence
        private bool _isStartingEngine;

        // Emergency stop flag
        private bool _isEmergencyStop;

        // Upcoming signals, obstacle and track reservation cache
        private readonly List<AITraffic.Navigation.SignalRegistry.UpcomingSignal> _upcomingSignals = new List<AITraffic.Navigation.SignalRegistry.UpcomingSignal>();
        private readonly HashSet<RailTrack> _reservedTracks = new HashSet<RailTrack>();
        private readonly HashSet<DVSignal> _reservedDVSignals = new HashSet<DVSignal>();

        #endregion

        #region Unity Lifecycle

        private void Awake()
        {
            _state = EngineState.Idle;
            TargetDirection = 1.0f;
            DistanceToSignal = float.PositiveInfinity;
            DistanceToObstacle = float.PositiveInfinity;
            DistanceToDestination = float.PositiveInfinity;
            StationDwellDuration = 45.0f;
            SpeedToleranceKmh = 1.0f;
            ThrottleSlewRate = 0.08f;
            BrakeSlewRate = 0.85f;
            DynamicBrakeSlewRate = 0.85f;
            _slipThrottleReduction = 1.0f;
            _rampThrottle = 0.0f;
            SpawnTime = Time.time;
            StationaryTimer = 0.0f;

            if (_trainCar == null)
            {
                _trainCar = GetComponent<TrainCar>();
            }

            InitializeControllers();

            if (AITraffic.Core.TrafficManager.IsRunning && AITraffic.Core.TrafficManager.Instance != null)
            {
                AITraffic.Core.TrafficManager.Instance.RegisterEngineer(this);
            }
        }

        private void OnDestroy()
        {
            try
            {
                if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsInitialized)
                {
                    AITraffic.Navigation.RailGraph.Instance.ReleaseAllReservationsFor(this);
                }
            }
            catch {}

            _reservedTracks.Clear();
            ReleaseAllSignalReservations();

            if (AITraffic.Core.TrafficManager.IsRunning && AITraffic.Core.TrafficManager.Instance != null)
            {
                AITraffic.Core.TrafficManager.Instance.UnregisterEngineer(this);
            }
        }

        /// <summary>
        /// Releases all active DVSignals route reservations held by this train.
        /// </summary>
        public void ReleaseAllSignalReservations()
        {
            if (_reservedDVSignals.Count > 0)
            {
                foreach (var sig in _reservedDVSignals)
                {
                    if (sig != null)
                    {
                        AITraffic.Navigation.SignalRegistry.ClearDVSignalReservation(sig);
                    }
                }
                _reservedDVSignals.Clear();
            }
        }

        private void Start()
        {
            if (_trainCar != null)
            {
                InitializeControls();
            }
        }

        private void Update()
        {
            if (_trainCar == null) return;

            float dt = Time.deltaTime;
            if (dt <= 0.0f) return;

            if (CurrentSpeedKmh < 0.2f && State != EngineState.TerminusStop && State != EngineState.StationHold)
            {
                StationaryTimer += dt;
            }
            else
            {
                StationaryTimer = 0.0f;
            }

            UpdateSensors(dt);
            UpdatePathAndJunctions(dt);
            UpdateWheelSlipProtection(dt);
            UpdateLevelCrossingHorn(dt);
            UpdateSpeedProfile(dt);
            UpdateStateMachine(dt);
            if (_dm3Controller != null && _dm3Controller.IsDM3)
            {
                _dm3Controller.Update(dt);
            }
            ExecuteControlOutputs(dt);
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Explicitly attaches and initializes the AI Engineer for a specific locomotive.
        /// </summary>
        /// <param name="locomotive">The locomotive TrainCar instance.</param>
        public void Initialize(TrainCar locomotive)
        {
            _trainCar = locomotive;
            InitializeControllers();
            InitializeControls();
        }

        private void InitializeControllers()
        {
            // Throttle PID: responsive with anti-windup
            ThrottlePID = new PIDController(
                kp: 0.08f,
                ki: 0.02f,
                kd: 0.04f,
                minOutput: 0.0f,
                maxOutput: 1.0f,
                filterTimeConstant: 0.15f,
                derivativeOnMeasurement: true
            );

            // Brake PID: smooth proportional braking response with dynamic stopping urgency
            BrakePID = new PIDController(
                kp: 0.14f,
                ki: 0.02f,
                kd: 0.05f,
                minOutput: 0.0f,
                maxOutput: 1.0f,
                filterTimeConstant: 0.10f,
                derivativeOnMeasurement: true
            );

            SpeedProfiler = new SpeedProfileGenerator();
            _dm3Controller = new DM3TransmissionController(this, _trainCar);
        }

        private void InitializeControls()
        {
            if (_trainCar.SimController != null)
            {
                _controlsOverrider = _trainCar.SimController.controlsOverrider;
            }

            if (_controlsOverrider != null)
            {
                _hasDynamicBrake = _controlsOverrider.DynamicBrake != null;
                EnsureEngineRunning();
            }
        }

        #endregion

        #region Sensor & State Updates

        private float _heavySensorUpdateCooldown = 0.0f;

        private void UpdateSensors(float dt)
        {
            float forwardSpeedMs = _trainCar.GetForwardSpeed();
            CurrentSpeedMs = Mathf.Abs(forwardSpeedMs);
            CurrentSpeedKmh = CurrentSpeedMs * 3.6f;

            // Smoothed acceleration in m/s^2
            float rawAcc = dt > 0.001f ? (CurrentSpeedMs - _lastSpeedMs) / dt : 0f;
            _lastSpeedMs = CurrentSpeedMs;
            _smoothAccMs2 = Mathf.Lerp(_smoothAccMs2, rawAcc, Mathf.Clamp01(dt * 3.0f));
            CurrentAccelerationMs2 = _smoothAccMs2;

            if (_controlsOverrider == null && _trainCar.SimController != null)
            {
                _controlsOverrider = _trainCar.SimController.controlsOverrider;
                if (_controlsOverrider != null)
                {
                    _hasDynamicBrake = _controlsOverrider.DynamicBrake != null;
                }
            }

            // Continuously decrement distances based on movement for smooth PID tracking
            float distTraveled = CurrentSpeedMs * dt;
            if (!float.IsInfinity(DistanceToSignal)) DistanceToSignal = Mathf.Max(0f, DistanceToSignal - distTraveled);
            if (!float.IsInfinity(DistanceToObstacle)) DistanceToObstacle = Mathf.Max(0f, DistanceToObstacle - distTraveled);
            if (!float.IsInfinity(DistanceToDestination)) DistanceToDestination = Mathf.Max(0f, DistanceToDestination - distTraveled);

            _heavySensorUpdateCooldown -= dt;
            if (_heavySensorUpdateCooldown > 0.0f) return;
            
            // Stagger updates slightly across multiple trains
            _heavySensorUpdateCooldown = 0.25f + UnityEngine.Random.Range(0f, 0.05f);

            // Look up upcoming signal along active route
            RailTrack currentTrack = null;
            double currentSpan = 0.0;
            if (_trainCar != null)
            {
                if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
                {
                    currentTrack = _trainCar.FrontBogie.track;
                    currentSpan = _trainCar.FrontBogie.traveller != null ? _trainCar.FrontBogie.traveller.Span : 0.0;
                }
                else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
                {
                    currentTrack = _trainCar.RearBogie.track;
                    currentSpan = _trainCar.RearBogie.traveller != null ? _trainCar.RearBogie.traveller.Span : 0.0;
                }
            }

            // Dynamically determine TargetDirection along currentTrack
            if (currentTrack != null && currentTrack.curve != null && _trainCar != null)
            {
                float trackLen = currentTrack.curve.length;
                float frac = (trackLen > 0.1f) ? Mathf.Clamp01((float)(currentSpan / trackLen)) : 0.5f;
                Vector3 tangent = currentTrack.curve.GetTangentAt(frac);

                if (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex + 1)
                {
                    var nextTrack = CurrentPath.Tracks[CurrentPathTrackIndex + 1];
                    if (nextTrack != null && nextTrack.curve != null)
                    {
                        Vector3 curStart = currentTrack.curve.GetPointAt(0.0f);
                        Vector3 curEnd = currentTrack.curve.GetPointAt(1.0f);
                        Vector3 nextMid = nextTrack.curve.GetPointAt(0.5f);

                        TargetDirection = (Vector3.Distance(curEnd, nextMid) <= Vector3.Distance(curStart, nextMid)) ? 1.0f : -1.0f;
                    }
                }
                else if (CurrentPath != null && CurrentPath.Tracks != null && CurrentPathTrackIndex > 0 && CurrentPathTrackIndex == CurrentPath.Tracks.Count - 1)
                {
                    var prevTrack = CurrentPath.Tracks[CurrentPathTrackIndex - 1];
                    if (prevTrack != null && prevTrack.curve != null)
                    {
                        Vector3 curStart = currentTrack.curve.GetPointAt(0.0f);
                        Vector3 curEnd = currentTrack.curve.GetPointAt(1.0f);
                        Vector3 prevStart = prevTrack.curve.GetPointAt(0.0f);
                        Vector3 prevEnd = prevTrack.curve.GetPointAt(1.0f);

                        float distStartToPrev = Mathf.Min(Vector3.Distance(curStart, prevStart), Vector3.Distance(curStart, prevEnd));
                        float distEndToPrev = Mathf.Min(Vector3.Distance(curEnd, prevStart), Vector3.Distance(curEnd, prevEnd));

                        TargetDirection = (distStartToPrev <= distEndToPrev) ? 1.0f : -1.0f;
                    }
                }
                else
                {
                    // Fallback to locomotive heading along track tangent
                    Vector3 locoHeading = _trainCar.transform.forward;
                    if (_commandedReverser < 0f || _desiredReverser < 0f) locoHeading = -locoHeading;
                    TargetDirection = (Vector3.Dot(locoHeading, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }

                Vector3 desiredMoveVector = tangent * TargetDirection;
                float dot = Vector3.Dot(_trainCar.transform.forward, desiredMoveVector);
                _desiredReverser = (dot >= 0.0f) ? 1.0f : -1.0f;
            }
            else if (TargetDirection == 0.0f)
            {
                TargetDirection = 1.0f;
            }

            // Look up upcoming signals along active route facing train
            AITraffic.Navigation.SignalRegistry.TryFindUpcomingSignals(currentTrack, currentSpan, TargetDirection, UpcomingTracks, _upcomingSignals);
            if (_upcomingSignals.Count > 0)
            {
                ApproachingSignal = _upcomingSignals[0].Signal;
                DistanceToSignal = _upcomingSignals[0].Distance;
            }
            else
            {
                ApproachingSignal = null;
                DistanceToSignal = float.PositiveInfinity;
            }

            // Look up physical obstacles / other train cars on route ahead (double-layer collision prevention)
            float distObstacle;
            if (AITraffic.Navigation.SignalRegistry.TryFindUpcomingObstacle(currentTrack, currentSpan, TargetDirection, UpcomingTracks, _trainCar != null ? _trainCar.trainset : null, out distObstacle))
            {
                DistanceToObstacle = distObstacle;
            }
            else
            {
                DistanceToObstacle = float.PositiveInfinity;
            }

            // Calculate upcoming Signal Blocks along active route (Block 1: Train -> S1, Block 2: S1 -> S2)
            AITraffic.Navigation.SignalRegistry.CalculateUpcomingSignalBlocks(
                currentTrack, currentSpan, TargetDirection, UpcomingTracks, _trainCar != null ? _trainCar.trainset : null, _upcomingSignalBlocks, 1500f);
        }

        private float _pathUpdateCooldown = 0.0f;

        private void UpdatePathAndJunctions(float dt)
        {
            if (CurrentPath == null || !CurrentPath.IsValid) return;

            _pathUpdateCooldown -= dt;
            if (_pathUpdateCooldown > 0.0f) return;
            _pathUpdateCooldown = 0.4f; // Check twice per second

            // 1. Locate current track in planned path
            RailTrack curTrack = null;
            if (_trainCar != null && _trainCar.FrontBogie != null)
            {
                curTrack = _trainCar.FrontBogie.track;
            }
            if (curTrack == null && _trainCar != null && _trainCar.RearBogie != null)
            {
                curTrack = _trainCar.RearBogie.track;
            }

            if (curTrack != null && CurrentPath.Tracks != null)
            {
                int foundIdx = CurrentPath.Tracks.IndexOf(curTrack);
                if (foundIdx >= 0)
                {
                    CurrentPathTrackIndex = foundIdx;
                }
            }

            // 1b. Check if player has aligned station switches for an open through-track/passing route
            if (curTrack != null)
            {
                TryAdoptPlayerAlignedPassingRoute(curTrack);
            }

            // 2. Populate UpcomingTracks list for SignalRegistry & SpeedProfiler
            if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex)
            {
                _upcomingTracks.Clear();
                for (int i = CurrentPathTrackIndex; i < CurrentPath.Tracks.Count; i++)
                {
                    _upcomingTracks.Add(CurrentPath.Tracks[i]);
                }
            }

            // 2b. Compute dynamic TargetDirection along current track
            if (curTrack != null && curTrack.curve != null)
            {
                if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex + 1)
                {
                    var nextTrack = CurrentPath.Tracks[CurrentPathTrackIndex + 1];
                    if (nextTrack != null && nextTrack.curve != null)
                    {
                        Vector3 curStart = curTrack.curve.GetPointAt(0.0f);
                        Vector3 curEnd = curTrack.curve.GetPointAt(1.0f);
                        Vector3 nextStart = nextTrack.curve.GetPointAt(0.0f);
                        Vector3 nextEnd = nextTrack.curve.GetPointAt(1.0f);

                        float distEndToNext = Mathf.Min(Vector3.Distance(curEnd, nextStart), Vector3.Distance(curEnd, nextEnd));
                        float distStartToNext = Mathf.Min(Vector3.Distance(curStart, nextStart), Vector3.Distance(curStart, nextEnd));

                        TargetDirection = (distEndToNext <= distStartToNext) ? 1.0f : -1.0f;
                    }
                }
                else if (CurrentPath.Tracks != null && CurrentPathTrackIndex > 0 && CurrentPathTrackIndex == CurrentPath.Tracks.Count - 1)
                {
                    // Final track in route: determine direction from entry point of previous track
                    var prevTrack = CurrentPath.Tracks[CurrentPathTrackIndex - 1];
                    if (prevTrack != null && prevTrack.curve != null)
                    {
                        Vector3 curStart = curTrack.curve.GetPointAt(0.0f);
                        Vector3 curEnd = curTrack.curve.GetPointAt(1.0f);
                        Vector3 prevStart = prevTrack.curve.GetPointAt(0.0f);
                        Vector3 prevEnd = prevTrack.curve.GetPointAt(1.0f);

                        float distStartToPrev = Mathf.Min(Vector3.Distance(curStart, prevStart), Vector3.Distance(curStart, prevEnd));
                        float distEndToPrev = Mathf.Min(Vector3.Distance(curEnd, prevStart), Vector3.Distance(curEnd, prevEnd));

                        TargetDirection = (distStartToPrev <= distEndToPrev) ? 1.0f : -1.0f;
                    }
                }
                else
                {
                    // Fallback to locomotive heading along track tangent
                    double span = (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0;
                    float frac = (float)Mathf.Clamp01((float)(span / curTrack.curve.length));
                    Vector3 tangent = curTrack.curve.GetTangentAt(frac);
                    Vector3 locoHeading = (_trainCar != null) ? _trainCar.transform.forward : Vector3.forward;
                    if (_commandedReverser < 0f || _desiredReverser < 0f) locoHeading = -locoHeading;
                    TargetDirection = (Vector3.Dot(locoHeading, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }

                // 2c. Compute locomotive reverser requirement based on physical locomotive heading vs track travel direction
                double curLocoSpan = (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0;
                float locoFrac = (float)Mathf.Clamp01((float)(curLocoSpan / curTrack.curve.length));
                Vector3 curTangent = curTrack.curve.GetTangentAt(locoFrac);
                Vector3 desiredMoveVector = curTangent * TargetDirection;
                float dot = (_trainCar != null) ? Vector3.Dot(_trainCar.transform.forward, desiredMoveVector) : 1.0f;
                _desiredReverser = (dot >= 0.0f) ? 1.0f : -1.0f;
            }

            // 2d. Dynamically compute exact remaining distance along route based on TargetDirection
            if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex)
            {
                double curSpan = 0.0;
                if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
                {
                    curSpan = _trainCar.FrontBogie.traveller.Span;
                }
                else if (_trainCar != null && _trainCar.RearBogie != null && _trainCar.RearBogie.traveller != null)
                {
                    curSpan = _trainCar.RearBogie.traveller.Span;
                }

                float remainingDist = 0.0f;
                if (curTrack != null && curTrack.curve != null)
                {
                    float curLen = curTrack.curve.length;
                    remainingDist = (TargetDirection >= 0.0f) ? Mathf.Max(0.0f, curLen - (float)curSpan) : Mathf.Max(0.0f, (float)curSpan);
                }

                for (int i = CurrentPathTrackIndex + 1; i < CurrentPath.Tracks.Count; i++)
                {
                    var t = CurrentPath.Tracks[i];
                    if (t != null && t.curve != null)
                    {
                        remainingDist += t.curve.length;
                    }
                }

                if (IsStationDestination || IsTerminusDestination)
                {
                    DistanceToDestination = Mathf.Max(0.0f, remainingDist);
                }
                else
                {
                    DistanceToDestination = float.PositiveInfinity;
                }
            }

            // 3. Complete Route & Station Switch Setting & Safety Interlocking (up to 2500m ahead / 40 tracks)
            if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 1 && _trainCar != null)
            {
                Vector3 trainPos = _trainCar.transform.position;
                Bogie rearBogie = null;
                if (_trainCar.trainset != null && _trainCar.trainset.cars != null && _trainCar.trainset.cars.Count > 0)
                {
                    var rearCar = _trainCar.trainset.cars[_trainCar.trainset.cars.Count - 1];
                    if (rearCar != null)
                    {
                        rearBogie = rearCar.RearBogie ?? rearCar.FrontBogie;
                    }
                }
                if (rearBogie == null)
                {
                    rearBogie = _trainCar.RearBogie ?? _trainCar.FrontBogie;
                }

                double curSpan = 0.0;
                if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
                {
                    curSpan = _trainCar.FrontBogie.traveller.Span;
                }

                // 3a. Proactively align and lock ALL switches along the entire upcoming planned route (through station throat to mainline)
                float accumulatedSwitchDist = 0.0f;
                RailTrack prevSwitchTrack = curTrack;

                for (int i = 0; i < _upcomingTracks.Count; i++)
                {
                    var trackA = prevSwitchTrack;
                    var trackB = _upcomingTracks[i];

                    if (trackA != null && trackB != null && trackA != trackB)
                    {
                        Junction junction;
                        byte requiredBranch;
                        if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out requiredBranch))
                        {
                            float dist = Vector3.Distance(trainPos, junction.position);

                            // Check if junction is occupied by player or belongs to a player-occupied signal block
                            bool isJunctionInPlayerBlock = false;
                            for (int b = 0; b < _upcomingSignalBlocks.Count; b++)
                            {
                                var block = _upcomingSignalBlocks[b];
                                if (block != null && block.IsPlayerOccupied)
                                {
                                    for (int s = 0; s < block.Switches.Count; s++)
                                    {
                                        if (block.Switches[s].Junction == junction)
                                        {
                                            isJunctionInPlayerBlock = true;
                                            break;
                                        }
                                    }
                                }
                                if (isJunctionInPlayerBlock) break;
                            }

                            if (isJunctionInPlayerBlock || AITraffic.Navigation.SignalRegistry.IsJunctionOccupiedByPlayer(junction, _trainCar != null ? _trainCar.trainset : null))
                            {
                                // Do not throw or lock switches in a block occupied by the player!
                                prevSwitchTrack = trackB;
                                continue;
                            }

                            // Advance Alignment: set switch immediately so whole station exit/entry route is aligned
                            if (junction.selectedBranch != requiredBranch)
                            {
                                AITraffic.Navigation.JunctionController.Instance.RequestJunctionAlignment(junction, requiredBranch, this);
                            }

                            // Critical Approach Lock: within 250m ahead of train
                            if (dist < 250f)
                            {
                                AITraffic.Navigation.JunctionController.Instance.TryLockJunction(junction, this, 45f);

                                // Register passing clearance for rear bogie when entering switch zone
                                if (dist < 40f && rearBogie != null)
                                {
                                    AITraffic.Navigation.JunctionController.Instance.RegisterTrainPassing(this, junction, rearBogie, trackB, 45f);
                                }
                            }
                        }
                    }

                    prevSwitchTrack = trackB;
                    if (trackB != null && trackB.curve != null)
                    {
                        accumulatedSwitchDist += trackB.curve.length;
                    }
                    if (accumulatedSwitchDist > 2500f || i > 40)
                    {
                        break;
                    }
                }

                // 3b. Track reservations in RailGraph to prevent conflicting opposing routes
                var desiredReservations = new HashSet<RailTrack>();

                for (int b = 0; b < _upcomingSignalBlocks.Count; b++)
                {
                    var block = _upcomingSignalBlocks[b];
                    if (block == null) continue;

                    // Reserve tracks in network graph to prevent conflicting routes
                    if (block.Tracks != null)
                    {
                        for (int t = 0; t < block.Tracks.Count; t++)
                        {
                            var trk = block.Tracks[t];
                            if (trk == null) continue;

                            desiredReservations.Add(trk);
                            if (!_reservedTracks.Contains(trk))
                            {
                                if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsInitialized)
                                {
                                    AITraffic.Navigation.RailGraph.Instance.TryReserveTrack(trk, this);
                                }
                                _reservedTracks.Add(trk);
                            }
                        }
                    }
                }

                // 3c. DVSignals Interlocking Route Reservation (Hp 0 -> Hp 1 / Hp 2 transition)
                // When switches along upcoming signal blocks are aligned, reserve the signals so DVSignals SpecialRequireReservationAspect clears to Hp 1/Hp 2!
                var desiredSignalReservations = new HashSet<DVSignal>();

                for (int b = 0; b < _upcomingSignalBlocks.Count; b++)
                {
                    var block = _upcomingSignalBlocks[b];
                    if (block == null) continue;

                    // If switches are aligned and block is clear:
                    if (block.AreSwitchesAligned && block.IsClear)
                    {
                        if (block.EntrySignal != null && block.DistanceToEntry < 1500f)
                        {
                            desiredSignalReservations.Add(block.EntrySignal);
                            if (!_reservedDVSignals.Contains(block.EntrySignal))
                            {
                                if (AITraffic.Navigation.SignalRegistry.TryReserveDVSignal(block.EntrySignal))
                                {
                                    _reservedDVSignals.Add(block.EntrySignal);
                                }
                            }
                        }

                        if (block.ExitSignal != null && block.DistanceToExit < 1500f)
                        {
                            desiredSignalReservations.Add(block.ExitSignal);
                            if (!_reservedDVSignals.Contains(block.ExitSignal))
                            {
                                if (AITraffic.Navigation.SignalRegistry.TryReserveDVSignal(block.ExitSignal))
                                {
                                    _reservedDVSignals.Add(block.ExitSignal);
                                }
                            }
                        }
                    }
                }

                // Also reserve immediate ApproachingSignal if within lookahead and all intermediate switches are aligned
                if (ApproachingSignal != null && DistanceToSignal < 1500f)
                {
                    desiredSignalReservations.Add(ApproachingSignal);
                    if (!_reservedDVSignals.Contains(ApproachingSignal))
                    {
                        if (AITraffic.Navigation.SignalRegistry.TryReserveDVSignal(ApproachingSignal))
                        {
                            _reservedDVSignals.Add(ApproachingSignal);
                        }
                    }
                }

                // Progressive release of passed signals
                List<DVSignal> sigsToRelease = null;
                foreach (var sig in _reservedDVSignals)
                {
                    if (!desiredSignalReservations.Contains(sig))
                    {
                        if (sigsToRelease == null) sigsToRelease = new List<DVSignal>();
                        sigsToRelease.Add(sig);
                    }
                }
                if (sigsToRelease != null)
                {
                    for (int s = 0; s < sigsToRelease.Count; s++)
                    {
                        AITraffic.Navigation.SignalRegistry.ClearDVSignalReservation(sigsToRelease[s]);
                        _reservedDVSignals.Remove(sigsToRelease[s]);
                    }
                }

                // Progressive release of tracks behind the train that are no longer in the active block
                List<RailTrack> toRelease = null;
                foreach (var trk in _reservedTracks)
                {
                    if (!desiredReservations.Contains(trk))
                    {
                        if (toRelease == null) toRelease = new List<RailTrack>();
                        toRelease.Add(trk);
                    }
                }
                if (toRelease != null)
                {
                    for (int r = 0; r < toRelease.Count; r++)
                    {
                        if (AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsInitialized)
                        {
                            AITraffic.Navigation.RailGraph.Instance.ReleaseTrackReservation(toRelease[r], this);
                        }
                        _reservedTracks.Remove(toRelease[r]);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if the player has manually aligned station throat switches for an open through-track/bypass route
        /// with a non-Hp0 clear signal aspect. If all targets and destinations can still be reached, dynamically
        /// adopts the player-aligned route to pass/overtake the player through the station without forcing switches back.
        /// </summary>
        private bool TryAdoptPlayerAlignedPassingRoute(RailTrack curTrack)
        {
            if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
                return false;

            if (_trainCar == null) return false;

            // Destination track
            RailTrack destTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
            if (destTrack == null) return false;

            // Look up upcoming diverging junction within lookahead horizon (up to 1200m / 5 tracks ahead)
            Junction divergeJunction = null;
            RailTrack divergeInTrack = null;
            int divergePathIdx = -1;

            for (int i = CurrentPathTrackIndex; i < CurrentPath.Tracks.Count - 1 && i < CurrentPathTrackIndex + 5; i++)
            {
                var tA = CurrentPath.Tracks[i];
                var tB = CurrentPath.Tracks[i + 1];
                if (tA == null || tB == null) continue;

                Junction junc;
                byte reqBranch;
                if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(tA, tB, out junc, out reqBranch))
                {
                    if (junc != null && junc.inBranch != null && junc.inBranch.track == tA && junc.outBranches != null && junc.outBranches.Count > 1)
                    {
                        divergeJunction = junc;
                        divergeInTrack = tA;
                        divergePathIdx = i;
                        break;
                    }
                }
            }

            if (divergeJunction == null || divergeInTrack == null || divergeJunction.outBranches == null)
                return false;

            byte currentSelectedBranch = divergeJunction.selectedBranch;
            if (currentSelectedBranch >= divergeJunction.outBranches.Count)
                return false;

            var alignedBranch = divergeJunction.outBranches[currentSelectedBranch];
            if (alignedBranch == null || alignedBranch.track == null)
                return false;

            RailTrack alignedFirstTrack = alignedBranch.track;

            // Check if the switch is set to an alternative branch, or if our planned route ahead is blocked by player
            RailTrack plannedNextTrack = (divergePathIdx + 1 < CurrentPath.Tracks.Count) ? CurrentPath.Tracks[divergePathIdx + 1] : null;
            bool isSwitchSetToAlternative = (plannedNextTrack != null && alignedFirstTrack != plannedNextTrack);

            if (!isSwitchSetToAlternative)
            {
                bool isPlannedBlocked = false;
                for (int p = divergePathIdx + 1; p < CurrentPath.Tracks.Count && p < divergePathIdx + 6; p++)
                {
                    if (AITraffic.Navigation.SignalRegistry.IsTrackOccupiedByPlayer(CurrentPath.Tracks[p], _trainCar != null ? _trainCar.trainset : null))
                    {
                        isPlannedBlocked = true;
                        break;
                    }
                }
                if (!isPlannedBlocked) return false;
            }

            // 1. Trace the physically aligned route through the station
            var alignedRouteTracks = new List<RailTrack>();
            alignedRouteTracks.Add(alignedFirstTrack);

            RailTrack traceTrack = alignedFirstTrack;
            for (int step = 0; step < 15; step++)
            {
                if (traceTrack == null || traceTrack == destTrack) break;

                Junction nextJunc = (traceTrack.outJunction != null) ? traceTrack.outJunction : traceTrack.inJunction;
                if (nextJunc == null) break;

                if (nextJunc.inBranch != null && nextJunc.inBranch.track == traceTrack && nextJunc.outBranches != null)
                {
                    byte sb = nextJunc.selectedBranch;
                    if (sb < nextJunc.outBranches.Count && nextJunc.outBranches[sb] != null && nextJunc.outBranches[sb].track != null)
                    {
                        var nxt = nextJunc.outBranches[sb].track;
                        if (nxt != traceTrack && !alignedRouteTracks.Contains(nxt))
                        {
                            alignedRouteTracks.Add(nxt);
                            traceTrack = nxt;
                            continue;
                        }
                    }
                }
                else if (nextJunc.inBranch != null && nextJunc.inBranch.track != null && nextJunc.outBranches != null)
                {
                    bool isTrailing = false;
                    for (int b = 0; b < nextJunc.outBranches.Count; b++)
                    {
                        if (nextJunc.outBranches[b] != null && nextJunc.outBranches[b].track == traceTrack)
                        {
                            isTrailing = true;
                            break;
                        }
                    }
                    if (isTrailing && nextJunc.inBranch.track != traceTrack && !alignedRouteTracks.Contains(nextJunc.inBranch.track))
                    {
                        alignedRouteTracks.Add(nextJunc.inBranch.track);
                        traceTrack = nextJunc.inBranch.track;
                        continue;
                    }
                }

                break;
            }

            // 2. Validate Aligned Route:
            // 2a. No track in the aligned route may be occupied by the player
            for (int i = 0; i < alignedRouteTracks.Count; i++)
            {
                if (AITraffic.Navigation.SignalRegistry.IsTrackOccupiedByPlayer(alignedRouteTracks[i], _trainCar != null ? _trainCar.trainset : null))
                    return false;
            }

            // 2b. Check entry / governing signal on this aligned route (must NOT be Hp0 / Red)
            DVSignal routeSig;
            float sigDist;
            double curSpan = (_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0;
            if (AITraffic.Navigation.SignalRegistry.TryFindUpcomingSignal(curTrack, curSpan, TargetDirection, alignedRouteTracks, out routeSig, out sigDist))
            {
                if (routeSig != null && routeSig.CurrentAspect != null)
                {
                    bool isRed = routeSig.CurrentAspect.DisallowPassing ||
                                 (routeSig.CurrentAspect.Id != null && (routeSig.CurrentAspect.Id.IndexOf("HP0", StringComparison.OrdinalIgnoreCase) >= 0 || routeSig.CurrentAspect.Id.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0));
                    if (isRed)
                        return false; // Entry signal is still at Hp 0, cannot enter
                }
            }

            // 2c. Check Destination Reachability from the end of the aligned through-track
            RailTrack lastAlignedTrack = alignedRouteTracks[alignedRouteTracks.Count - 1];
            AITraffic.Navigation.RailPath continuation = null;

            if (lastAlignedTrack != destTrack)
            {
                var options = new AITraffic.Navigation.PathfinderOptions
                {
                    Requester = this,
                    PreventPlayerOvertake = false,
                    AvoidOccupiedTracks = true,
                    StrictlyAvoidOccupied = false
                };

                var pathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
                continuation = pathfinder.FindPath(lastAlignedTrack, destTrack, options);

                if (continuation == null || !continuation.IsValid)
                    return false;
            }

            // 2d. Check Station / Passenger Pickup Reachability
            if (IsStationDestination && destTrack != null)
            {
                bool destReachable = (lastAlignedTrack == destTrack) || (continuation != null && continuation.Tracks != null && continuation.Tracks.Contains(destTrack));
                if (!destReachable)
                    return false;
            }

            // 3. Construct and splice the new adopted route
            var combinedTracks = new List<RailTrack>();
            for (int i = 0; i <= divergePathIdx; i++)
            {
                combinedTracks.Add(CurrentPath.Tracks[i]);
            }
            for (int i = 0; i < alignedRouteTracks.Count; i++)
            {
                if (!combinedTracks.Contains(alignedRouteTracks[i]))
                    combinedTracks.Add(alignedRouteTracks[i]);
            }
            if (continuation != null && continuation.Tracks != null)
            {
                for (int i = 0; i < continuation.Tracks.Count; i++)
                {
                    var cTrk = continuation.Tracks[i];
                    if (!combinedTracks.Contains(cTrk))
                        combinedTracks.Add(cTrk);
                }
            }

            if (combinedTracks.Count < 2) return false;

            var fullPathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
            var newFullPath = fullPathfinder.BuildPathFromTracks(combinedTracks);

            if (newFullPath != null && newFullPath.IsValid)
            {
                CurrentPath = newFullPath;
                _upcomingTracks.Clear();
                int newIdx = CurrentPath.Tracks.IndexOf(curTrack);
                if (newIdx >= 0) CurrentPathTrackIndex = newIdx;
                for (int i = CurrentPathTrackIndex; i < CurrentPath.Tracks.Count; i++)
                {
                    _upcomingTracks.Add(CurrentPath.Tracks[i]);
                }

                Debug.Log(string.Format("[AITraffic] [AIEngineer] Dynamic Dispatch: Adopted player-aligned through route ({0} tracks) to destination '{1}'. Signal is clear. Overtaking player on clear through-line.",
                    CurrentPath.Tracks.Count, DestinationStationName ?? (destTrack != null ? destTrack.name : "End")));
                return true;
            }

            return false;
        }

        private float _speedProfileUpdateCooldown = 0.0f;

        private void UpdateSpeedProfile(float dt)
        {
            if (_isEmergencyStop)
            {
                TargetSpeedKmh = 0.0f;
                TargetSpeedMs = 0.0f;
                return;
            }

            _speedProfileUpdateCooldown -= dt;
            if (_speedProfileUpdateCooldown > 0.0f) return;
            
            // Stagger updates slightly across multiple trains
            _speedProfileUpdateCooldown = 0.25f + UnityEngine.Random.Range(0f, 0.05f);

            RailTrack currentTrack = null;
            if (_trainCar != null)
            {
                if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
                    currentTrack = _trainCar.FrontBogie.track;
                else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
                    currentTrack = _trainCar.RearBogie.track;
            }

            double currentSpan = 0.0;
            if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
            {
                currentSpan = _trainCar.FrontBogie.traveller.Span;
            }
            else if (_trainCar != null && _trainCar.RearBogie != null && _trainCar.RearBogie.traveller != null)
            {
                currentSpan = _trainCar.RearBogie.traveller.Span;
            }

            SpeedProfileResult profile = SpeedProfiler.CalculateTargetSpeed(
                currentTrack: currentTrack,
                currentSpan: currentSpan,
                direction: TargetDirection,
                upcomingTracks: UpcomingTracks,
                upcomingSignals: _upcomingSignals,
                distanceToObstacle: DistanceToObstacle,
                distanceToDestination: DistanceToDestination,
                isStationStop: IsStationDestination,
                isTerminusStop: IsTerminusDestination
            );

            CurrentSpeedProfile = profile;
            TargetSpeedKmh = profile.TargetSpeedKmh;
            TargetSpeedMs = profile.TargetSpeedMs;
        }

        #endregion

        #region Wheel Slip & Traction Control

        private void UpdateWheelSlipProtection(float dt)
        {
            _isWheelSlipping = false;

            // 1. Check WheelslipController on locomotive
            if (_trainCar.SimController != null && _trainCar.SimController.wheelslipController != null)
            {
                if (_trainCar.SimController.wheelslipController.IsWheelslipping ||
                    _trainCar.SimController.wheelslipController.wheelslip > 0.005f)
                {
                    _isWheelSlipping = true;
                }
            }

            // 2. Check AdhesionController on locomotive
            if (!_isWheelSlipping && _trainCar.adhesionController != null)
            {
                if (_trainCar.adhesionController.IsWheelSliding ||
                    _trainCar.adhesionController.wheelSlide > 0.005f)
                {
                    _isWheelSlipping = true;
                }
            }

            // 3. Request sander and modulate throttle
            if (_isWheelSlipping)
            {
                _sanderRequested = true;

                // Reduce throttle to regain traction
                _slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, 0.45f, 2.0f * dt);
            }
            else
            {
                // Smoothly restore throttle authority
                _slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, 1.0f, 0.5f * dt);
            }

            UpdateSanderActuation(dt);
        }

        private void UpdateSanderActuation(float dt)
        {
            if (_controlsOverrider == null || _controlsOverrider.Sander == null) return;

            // Immediate hard shutoff when stopped, station holding, or idle
            if (CurrentSpeedKmh < 0.2f || State == EngineState.Idle || State == EngineState.StationHold || State == EngineState.TerminusStop)
            {
                _sanderRequested = false;
                _sanderActiveTimer = 0.0f;
                _sanderRestTimer = 0.0f;
                _controlsOverrider.Sander.Set(0.0f);
                return;
            }

            if (_sanderRequested)
            {
                if (_sanderRestTimer <= 0.0f)
                {
                    // Active sander interval (pulse up to 2.5s)
                    _controlsOverrider.Sander.Set(1.0f);
                    _sanderActiveTimer += dt;

                    if (_sanderActiveTimer >= 2.5f)
                    {
                        // Pulse duration reached: cut sander off and start rest interval (1.5s)
                        _controlsOverrider.Sander.Set(0.0f);
                        _sanderActiveTimer = 0.0f;
                        _sanderRestTimer = 1.5f;
                    }
                }
                else
                {
                    // In rest cooldown interval: ensure sander remains OFF
                    _controlsOverrider.Sander.Set(0.0f);
                    _sanderRestTimer -= dt;
                }
            }
            else
            {
                // No sander requested: turn OFF and decrement rest timer
                _controlsOverrider.Sander.Set(0.0f);
                _sanderActiveTimer = 0.0f;
                if (_sanderRestTimer > 0.0f)
                {
                    _sanderRestTimer -= dt;
                }
            }

            // Clear request flag for next frame
            _sanderRequested = false;
        }

        #endregion

        #region Level Crossing Horn Automation

        private void UpdateLevelCrossingHorn(float dt)
        {
            if (_controlsOverrider == null || _controlsOverrider.Horn == null) return;

            // Find closest upcoming level crossing along route
            float closestDistance = float.PositiveInfinity;
            Vector3 locoPos = _trainCar.transform.position;

            for (int i = 0; i < LevelCrossings.Count; i++)
            {
                float dist = Vector3.Distance(locoPos, LevelCrossings[i]);
                if (dist < closestDistance)
                {
                    closestDistance = dist;
                }
            }

            // Horn triggers 200m before crossing until loco passes 5m beyond
            bool shouldHorn = closestDistance <= 200.0f && closestDistance >= 5.0f && CurrentSpeedKmh > 2.0f;

            if (shouldHorn)
            {
                if (!_isHornPatternActive)
                {
                    _isHornPatternActive = true;
                    _hornStepTimer = 0.0f;
                    _hornStepIndex = 0;
                }

                ExecuteHornPattern(dt);
            }
            else
            {
                if (_isHornPatternActive)
                {
                    _isHornPatternActive = false;
                    _controlsOverrider.Horn.Set(0.0f);
                }
            }
        }

        /// <summary>
        /// Executes standard railway crossing whistle cadence: Long (2.0s) - Long (2.0s) - Short (0.8s) - Long (2.0s).
        /// </summary>
        private void ExecuteHornPattern(float dt)
        {
            _hornStepTimer += dt;

            // Array of durations: [Blast, Silence, Blast, Silence, ShortBlast, Silence, LongBlast, RepeatDelay]
            float[] patternDurations = new float[] { 2.0f, 0.4f, 2.0f, 0.4f, 0.8f, 0.4f, 2.5f, 2.0f };
            bool isSounding = (_hornStepIndex % 2 == 0) && (_hornStepIndex < 7);

            if (_controlsOverrider != null && _controlsOverrider.Horn != null)
            {
                _controlsOverrider.Horn.Set(isSounding ? 1.0f : 0.0f);
            }

            if (_hornStepTimer >= patternDurations[_hornStepIndex])
            {
                _hornStepTimer = 0.0f;
                _hornStepIndex = (_hornStepIndex + 1) % patternDurations.Length;
            }
        }

        /// <summary>
        /// Computes progressive, surge-free throttle using measured acceleration feedback
        /// and speed-dependent current ceilings to prevent blown fuses on diesel-electrics (DE2/DE6).
        /// </summary>
        private float ComputeAcceleratingThrottle(float dt)
        {
            float speedDeltaKmh = TargetSpeedKmh - CurrentSpeedKmh;
            if (speedDeltaKmh <= 0.0f)
            {
                _rampThrottle = Mathf.MoveTowards(_rampThrottle, 0.0f, 0.2f * dt);
                _hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0.0f, 0.2f * dt);
                return 0.0f;
            }

            // 1. Base speed-based current/overload ceiling
            float baseCeiling = 0.28f;
            if (CurrentSpeedKmh < 6.0f)
            {
                baseCeiling = 0.28f; // Soft launch notch 1-2 on level track
            }
            else if (CurrentSpeedKmh < 15.0f)
            {
                baseCeiling = Mathf.Lerp(0.28f, 0.60f, (CurrentSpeedKmh - 6.0f) / 9.0f);
            }
            else if (CurrentSpeedKmh < 25.0f)
            {
                baseCeiling = Mathf.Lerp(0.60f, 1.00f, (CurrentSpeedKmh - 15.0f) / 10.0f);
            }
            else
            {
                baseCeiling = 1.00f;
            }

            // 2. Dynamic Hill-Start & Heavy-Load Acceleration Assist:
            // Detect if the locomotive is climbing an uphill incline or acceleration is critically sluggish (< 0.08 m/s^2)
            float travelPitch = 0.0f;
            if (_trainCar != null)
            {
                // Positive pitch along travel direction = uphill climb
                travelPitch = _trainCar.transform.forward.y * _desiredReverser;
            }

            bool isUphill = travelPitch > 0.008f; // > 0.8% grade
            bool isSluggish = CurrentAccelerationMs2 < 0.08f && speedDeltaKmh > 3.0f;

            if (CurrentSpeedKmh < 20.0f && (isUphill || isSluggish))
            {
                if (CurrentAccelerationMs2 < 0.10f)
                {
                    // Acceleration is super slow on hill/heavy load -> progressively ramp up hill boost ceiling
                    float boostRate = (isUphill && travelPitch > 0.015f) ? 0.09f : 0.05f;
                    _hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0.60f, boostRate * dt);
                }
                else if (CurrentAccelerationMs2 > 0.20f)
                {
                    // Acceleration is now healthy -> gently ease off excess hill boost
                    _hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0.0f, 0.06f * dt);
                }
            }
            else
            {
                // Level/downhill track or reached running speed -> decay hill boost back to zero
                _hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0.0f, 0.10f * dt);
            }

            float maxThrottleCeiling = Mathf.Clamp01(baseCeiling + _hillAssistBoost);

            // 3. Acceleration Feedback: only ramp up if there isn't enough acceleration felt
            const float minDesiredAcc = 0.12f; // m/s^2 (~0.43 km/h/s)
            const float maxAllowedAcc = 0.30f; // m/s^2 (~1.08 km/h/s)

            if (_rampThrottle < 0.12f && CurrentSpeedKmh < 1.0f)
            {
                _rampThrottle = 0.12f; // Initial soft launch notch
            }

            if (CurrentAccelerationMs2 < minDesiredAcc)
            {
                // Not accelerating enough -> notch up throttle
                float notchRate = (CurrentSpeedKmh < 6.0f) ? 0.040f : 0.070f;
                if ((isUphill || isSluggish) && CurrentAccelerationMs2 < 0.05f)
                {
                    notchRate = 0.085f; // Notch up faster if stalled or barely moving on steep gradient
                }
                _rampThrottle += notchRate * dt;
            }
            else if (CurrentAccelerationMs2 > maxAllowedAcc)
            {
                // Accelerating too fast -> ease off power to prevent surge & slip
                _rampThrottle -= 0.07f * dt;
            }
            // else acceleration is within comfortable range -> maintain steady throttle!

            // 4. Proportional ramp-down approaching target speed to prevent overshoot
            if (speedDeltaKmh < 4.0f)
            {
                float approachCap = Mathf.Clamp01(speedDeltaKmh / 4.0f);
                maxThrottleCeiling = Mathf.Min(maxThrottleCeiling, approachCap);
            }

            _rampThrottle = Mathf.Clamp(_rampThrottle, 0.0f, maxThrottleCeiling);
            return _rampThrottle * _slipThrottleReduction;
        }

        private float ComputeCruisingThrottle(float dt)
        {
            float speedDeltaKmh = TargetSpeedKmh - CurrentSpeedKmh;

            float pidCruise = ThrottlePID.Update(TargetSpeedKmh, CurrentSpeedKmh, dt);
            _rampThrottle = Mathf.MoveTowards(_rampThrottle, pidCruise, 0.06f * dt);
            return _rampThrottle * _slipThrottleReduction;
        }

        #endregion

        #region State Machine Logic

        private void UpdateStateMachine(float dt)
        {
            float speedDeltaKmh = TargetSpeedKmh - CurrentSpeedKmh;

            // Auto-recover stalled engine or blown breaker
            if (_controlsOverrider != null && _controlsOverrider.EngineOnReader != null && !_controlsOverrider.EngineOnReader.IsOn)
            {
                if (State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting)
                {
                    _stallRestartCooldown -= dt;
                    if (_stallRestartCooldown <= 0.0f)
                    {
                        _stallRestartCooldown = 2.0f;
                        try
                        {
                            DV.Simulation.Controllers.StartupHelper.Startup(_trainCar);
                        }
                        catch { }
                    }
                }
            }

            if (_brakeHoldTimer > 0.0f)
            {
                _brakeHoldTimer -= dt;
            }

            // Universal Terminus / Station Arrival Check:
            bool isAtFinalDestination = (IsStationDestination || IsTerminusDestination) && 
                ((DistanceToDestination <= 8.0f) || 
                 (CurrentPath != null && CurrentPath.Tracks != null && CurrentPathTrackIndex >= CurrentPath.Tracks.Count - 1 && DistanceToDestination <= 25.0f));

            if (isAtFinalDestination && CurrentSpeedKmh < 1.0f && State != EngineState.TerminusStop && State != EngineState.StationHold)
            {
                if (IsTerminusDestination)
                {
                    EnterTerminusStop();
                    return;
                }
                else if (IsStationDestination)
                {
                    EnterStationHold();
                    return;
                }
            }

            switch (State)
            {
                case EngineState.Idle:
                    _commandedThrottle = 0.0f;
                    _commandedDynamicBrake = 0.0f;
                    _commandedTrainBrake = 0.4f;
                    _commandedIndependentBrake = 1.0f;
                    _commandedReverser = _desiredReverser;
                    _rampThrottle = 0.0f;

                    if (TargetSpeedKmh > 1.0f && !_isEmergencyStop)
                    {
                        _commandedTrainBrake = 0.0f;
                        _commandedIndependentBrake = 0.0f;
                        State = EngineState.Starting;
                    }
                    break;

                case EngineState.Starting:
                    _commandedReverser = _desiredReverser;
                    _commandedIndependentBrake = 0.0f;
                    _commandedTrainBrake = 0.0f;
                    _commandedDynamicBrake = 0.0f;
                    _rampThrottle = 0.0f;

                    if (Mathf.Abs(_currentReverser - _desiredReverser) < 0.1f)
                    {
                        State = EngineState.Accelerating;
                    }
                    break;

                case EngineState.Accelerating:
                    _commandedReverser = _desiredReverser;
                    _commandedIndependentBrake = 0.0f;
                    _commandedTrainBrake = 0.0f;
                    _commandedDynamicBrake = 0.0f;

                    _commandedThrottle = ComputeAcceleratingThrottle(dt);
                    BrakePID.Reset();

                    if (TargetSpeedKmh <= 0.01f || speedDeltaKmh < -3.5f)
                    {
                        _rampThrottle = 0.0f;
                        _brakeHoldTimer = 3.0f;
                        State = EngineState.Braking;
                    }
                    else if (Mathf.Abs(speedDeltaKmh) <= SpeedToleranceKmh)
                    {
                        State = EngineState.Cruising;
                    }
                    break;

                case EngineState.Cruising:
                    _commandedReverser = _desiredReverser;
                    _commandedIndependentBrake = 0.0f;
                    _commandedTrainBrake = 0.0f;

                    _commandedThrottle = ComputeCruisingThrottle(dt);
                    BrakePID.Reset();

                    if (TargetSpeedKmh <= 0.01f || speedDeltaKmh < -4.0f)
                    {
                        _rampThrottle = 0.0f;
                        _brakeHoldTimer = 3.0f;
                        State = EngineState.Braking;
                    }
                    else if (speedDeltaKmh < -1.0f)
                    {
                        // Minor downhill/coasting speed control: cut throttle and apply dynamic brake first without depleting train air
                        State = EngineState.Coasting;
                    }
                    else if (speedDeltaKmh > SpeedToleranceKmh * 1.5f)
                    {
                        State = EngineState.Accelerating;
                    }
                    else if (TargetSpeedKmh > 0.0f && Mathf.Abs(_commandedThrottle) < 0.02f)
                    {
                        State = EngineState.Coasting;
                    }
                    break;

                case EngineState.Coasting:
                    _commandedReverser = _desiredReverser;
                    _commandedThrottle = 0.0f;
                    _rampThrottle = 0.0f;

                    // Modulate dynamic brake for gentle descent speed trimming
                    if (_hasDynamicBrake && speedDeltaKmh < 0.0f)
                    {
                        _commandedDynamicBrake = Mathf.Clamp01(-speedDeltaKmh / 5.0f);
                        _commandedTrainBrake = 0.0f;
                    }
                    else if (!_hasDynamicBrake && speedDeltaKmh < -0.8f)
                    {
                        // For non-dynamic-brake locomotives (DE2/DM3), apply just a couple percent (3% to 18%) of air brake for gentle speed trimming
                        _commandedDynamicBrake = 0.0f;
                        _commandedTrainBrake = Mathf.Clamp(-speedDeltaKmh * 0.04f, 0.0f, 0.20f);
                    }
                    else
                    {
                        _commandedDynamicBrake = 0.0f;
                        _commandedTrainBrake = 0.0f;
                    }

                    if (TargetSpeedKmh <= 0.01f || speedDeltaKmh < -4.0f)
                    {
                        _brakeHoldTimer = 3.0f;
                        State = EngineState.Braking;
                    }
                    else if (speedDeltaKmh > SpeedToleranceKmh)
                    {
                        _commandedDynamicBrake = 0.0f;
                        _commandedTrainBrake = 0.0f;
                        State = EngineState.Accelerating;
                    }
                    break;

                case EngineState.Braking:
                    _commandedThrottle = 0.0f;
                    _rampThrottle = 0.0f;
                    ThrottlePID.Reset();

                    float brakeOutput = BrakePID.Update(CurrentSpeedKmh, TargetSpeedKmh, dt);

                    // Dynamic stopping urgency calculation for Red Signals (Hp 0), Obstacles, and Buffer Stops
                    float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(DistanceToObstacle, DistanceToDestination));
                    if (distToStop < 500.0f)
                    {
                        if (TargetSpeedKmh <= 0.5f)
                        {
                            // In final deceleration & stop zone (< 17m to signal mast / buffer stop)
                            float effectiveDist = Mathf.Max(0.5f, distToStop - 9.0f);
                            float reqDecel = (CurrentSpeedMs * CurrentSpeedMs) / (2.0f * effectiveDist);

                            // Scale braking demand up progressively to halt precisely at the 9m stop mark
                            if (reqDecel > 0.15f)
                            {
                                float stopRatio = Mathf.Clamp01((reqDecel - 0.15f) / 0.30f);
                                brakeOutput = Mathf.Max(brakeOutput, 0.40f + stopRatio * 0.60f);
                            }

                            // Emergency clamps if over-speeding close to the stop line:
                            if (distToStop < 17.0f && CurrentSpeedKmh > 10.0f)
                            {
                                brakeOutput = 1.0f; // Full emergency stopping brake
                            }
                            else if (distToStop < 11.0f && CurrentSpeedKmh > 3.5f)
                            {
                                brakeOutput = 1.0f;
                                _sanderRequested = true; // Request sand rails for maximum adhesion
                            }
                            else if (distToStop <= 9.0f)
                            {
                                brakeOutput = 1.0f;
                            }

                            // Maintain solid holding brake while stopped at the signal
                            if (CurrentSpeedKmh < 0.4f)
                            {
                                brakeOutput = Mathf.Max(0.50f, brakeOutput);
                            }
                        }
                        else
                        {
                            // In service deceleration or crawl approach phase (75m -> 12m)
                            // Emergency clamp only if approaching far too fast
                            if (distToStop < 45.0f && CurrentSpeedKmh > 20.0f)
                            {
                                brakeOutput = 1.0f;
                            }
                        }
                    }

                    ApplyBrakeBlending(brakeOutput);

                    if (TargetSpeedKmh <= 0.01f && CurrentSpeedKmh < 0.3f)
                    {
                        if (IsStationDestination && isAtFinalDestination)
                        {
                            EnterStationHold();
                        }
                        else if (IsTerminusDestination && isAtFinalDestination)
                        {
                            EnterTerminusStop();
                        }
                        else
                        {
                            State = EngineState.Idle; // Waiting for red signal or obstacle to clear
                        }
                    }
                    else if (TargetSpeedKmh > 1.0f && speedDeltaKmh >= -0.5f && _brakeHoldTimer <= 0.0f)
                    {
                        // Signal or path ahead cleared -> resume acceleration!
                        _commandedTrainBrake = 0.0f;
                        _commandedIndependentBrake = 0.0f;
                        State = EngineState.Accelerating;
                    }
                    break;

                case EngineState.StationHold:
                    _commandedThrottle = 0.0f;
                    _commandedDynamicBrake = 0.0f;
                    _commandedTrainBrake = 0.5f;
                    _commandedIndependentBrake = 1.0f;
                    _commandedReverser = _desiredReverser;

                    DwellTimeRemaining -= dt;
                    if (DwellTimeRemaining <= 0.0f)
                    {
                        IsStationDestination = false;
                        State = EngineState.Starting;
                    }
                    break;

                case EngineState.TerminusStop:
                    _commandedThrottle = 0.0f;
                    _commandedDynamicBrake = 0.0f;
                    _commandedTrainBrake = 1.0f;
                    _commandedIndependentBrake = 1.0f;
                    _commandedReverser = 0.0f;

                    if (_controlsOverrider != null)
                    {
                        if (_controlsOverrider.Throttle != null && _controlsOverrider.Throttle.Value > 0.0f)
                            _controlsOverrider.Throttle.Set(0.0f);
                        if (_controlsOverrider.Reverser != null && _controlsOverrider.Reverser.Value != 0.0f)
                            _controlsOverrider.Reverser.Set(0.0f);
                        if (_controlsOverrider.HeadlightsFront != null && _controlsOverrider.HeadlightsFront.Value > 0.0f)
                            _controlsOverrider.HeadlightsFront.Set(0.0f);
                        if (_controlsOverrider.HeadlightsRear != null && _controlsOverrider.HeadlightsRear.Value > 0.0f)
                            _controlsOverrider.HeadlightsRear.Set(0.0f);
                        if (_controlsOverrider.EngineOnReader != null && _controlsOverrider.EngineOnReader.IsOn)
                        {
                            if (_controlsOverrider.PowerOff != null)
                                _controlsOverrider.PowerOff.Set(1.0f);
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Applies pneumatic train air brakes across all cars and blends dynamic braking on the locomotive.
        /// Ensures train air brakes are always engaged during service/stop deceleration to prevent train overruns.
        /// </summary>
        private void ApplyBrakeBlending(float brakeDemand)
        {
            // Train air brake is the primary retarding force across all rolling stock
            _commandedTrainBrake = brakeDemand;

            // Dynamic brake supplements locomotive retarding force above 5 km/h
            if (_hasDynamicBrake && CurrentSpeedKmh > 5.0f)
            {
                _commandedDynamicBrake = Mathf.Clamp01(brakeDemand * 1.25f);
            }
            else
            {
                _commandedDynamicBrake = 0.0f;
            }

            // Independent locomotive direct brake assists at low speeds / final stop
            float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(DistanceToObstacle, DistanceToDestination));
            if (TargetSpeedKmh <= 0.5f || distToStop < 100.0f)
            {
                if (CurrentSpeedKmh < 18.0f)
                {
                    _commandedIndependentBrake = Mathf.Clamp01(brakeDemand * 1.5f);
                }
                else
                {
                    _commandedIndependentBrake = 0.0f;
                }
            }
            else
            {
                _commandedIndependentBrake = 0.0f;
            }

            // Imminent physical obstacle emergency clamping
            if (DistanceToObstacle < 150.0f)
            {
                _commandedTrainBrake = 1.0f;
                _commandedIndependentBrake = 1.0f;
                if (_hasDynamicBrake) _commandedDynamicBrake = 1.0f;
            }

            // Absolute holding brake when stopped
            if (CurrentSpeedKmh < 0.5f && (TargetSpeedKmh <= 0.1f || DistanceToObstacle < 150.0f))
            {
                _commandedTrainBrake = Mathf.Max(0.6f, brakeDemand);
                _commandedIndependentBrake = 1.0f;
            }
        }

        private void EnterStationHold()
        {
            State = EngineState.StationHold;
            DwellTimeRemaining = StationDwellDuration > 0.0f ? StationDwellDuration : UnityEngine.Random.Range(30.0f, 60.0f);
        }

        private void EnterTerminusStop()
        {
            State = EngineState.TerminusStop;
            ShutdownEngine();

            // Release track reservations and junction locks when safely stopped at terminus
            if (_reservedTracks.Count > 0)
            {
                foreach (var resTrack in _reservedTracks)
                {
                    if (resTrack != null && AITraffic.Navigation.RailGraph.Instance != null)
                    {
                        AITraffic.Navigation.RailGraph.Instance.ReleaseTrackReservation(resTrack, this);
                    }
                }
                _reservedTracks.Clear();
            }
            if (AITraffic.Navigation.JunctionController.Instance != null)
            {
                AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(this);
            }
            ReleaseAllSignalReservations();

            if (IsWorkerDriven && _controlsOverrider != null && _controlsOverrider.Handbrake != null)
            {
                _controlsOverrider.Handbrake.Set(1.0f);
            }

            try
            {
                if (OnTerminusArrival != null)
                {
                    OnTerminusArrival(this);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError(string.Format("[AITraffic] Error in OnTerminusArrival callback: {0}", ex));
            }
        }

        #endregion

        #region Control Output Execution

        /// <summary>
        /// Applies smoothly rate-limited commands to the locomotive controlsOverrider.
        /// </summary>
        private void ExecuteControlOutputs(float dt)
        {
            if (_controlsOverrider == null) return;

            // 1. Throttle Rate-Limiting (slew rate)
            float targetThrottle = _commandedThrottle;
            if (_dm3Controller != null && _dm3Controller.IsDM3 && _dm3Controller.IsShifting)
            {
                targetThrottle = 0.0f; // Cut fuel/throttle to disengage load during mechanical gear shift
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _currentThrottle = 0.0f; // Instantly cut current throttle to unload gearbox
            }

            _currentThrottle = Mathf.MoveTowards(_currentThrottle, targetThrottle, ThrottleSlewRate * dt);
            if (_controlsOverrider.Throttle != null)
            {
                _controlsOverrider.Throttle.Set(_currentThrottle);
            }

            // 2. Train Air Brake Rate-Limiting
            // Fast application (3.5/s) when increasing brake, gentle smooth release (0.45/s) to conserve reservoir air
            float brakeSlew = (_commandedTrainBrake > _currentTrainBrake) ? 3.5f : 0.45f;
            float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(DistanceToObstacle, DistanceToDestination));

            // Instant full application for emergency / signal stop danger zone:
            if (_commandedTrainBrake >= 0.90f && (distToStop < 160.0f || DistanceToObstacle < 160.0f))
            {
                _currentTrainBrake = _commandedTrainBrake; // Instant full application
            }
            else
            {
                _currentTrainBrake = Mathf.MoveTowards(_currentTrainBrake, _commandedTrainBrake, brakeSlew * dt);
            }

            if (_controlsOverrider.Brake != null)
            {
                _controlsOverrider.Brake.Set(_currentTrainBrake);
            }

            // 3. Dynamic Brake Rate-Limiting
            if (_hasDynamicBrake && _controlsOverrider.DynamicBrake != null)
            {
                _currentDynamicBrake = Mathf.MoveTowards(_currentDynamicBrake, _commandedDynamicBrake, DynamicBrakeSlewRate * dt);
                _controlsOverrider.DynamicBrake.Set(_currentDynamicBrake);
            }

            // 4. Independent Brake
            _currentIndependentBrake = Mathf.MoveTowards(_currentIndependentBrake, _commandedIndependentBrake, 0.8f * dt);
            if (_controlsOverrider.IndependentBrake != null)
            {
                _controlsOverrider.IndependentBrake.Set(_currentIndependentBrake);
            }

            // 5. Reverser (Safety interlock: only switch reverser when nearly stationary)
            if (CurrentSpeedMs < 0.15f)
            {
                _currentReverser = _commandedReverser;
                if (_controlsOverrider.Reverser != null)
                {
                    _controlsOverrider.Reverser.Set(_currentReverser);
                }
            }
        }

        #endregion

        #region Engine Lifecycle & Startup Helpers

        /// <summary>
        /// Verifies that the locomotive engine is started and running. If off, starts it automatically.
        /// </summary>
        public void EnsureEngineRunning()
        {
            if (_trainCar == null || State == EngineState.TerminusStop) return;

            if (_controlsOverrider == null && _trainCar.SimController != null)
            {
                _controlsOverrider = _trainCar.SimController.controlsOverrider;
            }

            if (_controlsOverrider != null && _controlsOverrider.EngineOnReader != null && !_controlsOverrider.EngineOnReader.IsOn)
            {
                if (!_isStartingEngine && gameObject.activeInHierarchy)
                {
                    StartCoroutine(StartEngineSequence());
                }
            }
        }

        private IEnumerator StartEngineSequence()
        {
            _isStartingEngine = true;

            // For DM3 mechanical diesel, disconnect transmission to neutral first
            if (_dm3Controller != null && _dm3Controller.IsDM3)
            {
                _dm3Controller.ApplyGearsInstant(0, 0);
            }

            // Trigger official DV startup helper to close breakers & prime electrics
            try
            {
                DV.Simulation.Controllers.StartupHelper.Startup(_trainCar);
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("StartupHelper warning in AIEngineer: {0}", ex.Message));
            }

            // Release handbrake and secure reverser
            if (_controlsOverrider != null)
            {
                if (_controlsOverrider.Handbrake != null) _controlsOverrider.Handbrake.Set(0.0f);
                if (_controlsOverrider.IndependentBrake != null) _controlsOverrider.IndependentBrake.Set(0.0f);
                if (_controlsOverrider.Brake != null) _controlsOverrider.Brake.Set(0.0f);
                if (_controlsOverrider.Reverser != null) _controlsOverrider.Reverser.Set(TargetDirection);
                if (_controlsOverrider.BrakeCutout != null) _controlsOverrider.BrakeCutout.Set(1.0f);
                if (_controlsOverrider.HeadlightsFront != null) _controlsOverrider.HeadlightsFront.Set(2.0f);
            }

            float timeout = 6.0f;
            while (timeout > 0.0f)
            {
                if (_controlsOverrider != null && _controlsOverrider.EngineOnReader != null && _controlsOverrider.EngineOnReader.IsOn)
                {
                    break;
                }
                timeout -= 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            _isStartingEngine = false;
        }

        /// <summary>
        /// Gracefully shuts down the locomotive engine, cab auxiliaries (headlights, cab lights, wipers, horn), and secures brakes.
        /// </summary>
        public void ShutdownEngine()
        {
            if (_trainCar == null) return;

            if (_controlsOverrider == null && _trainCar.SimController != null)
            {
                _controlsOverrider = _trainCar.SimController.controlsOverrider;
            }

            // Zero out all driving commands
            _commandedThrottle = 0.0f;
            _rampThrottle = 0.0f;
            _currentThrottle = 0.0f;
            _commandedDynamicBrake = 0.0f;
            _commandedTrainBrake = 1.0f;
            _commandedIndependentBrake = 1.0f;
            _commandedReverser = 0.0f;
            _desiredReverser = 0.0f;

            if (_dm3Controller != null && _dm3Controller.IsDM3)
            {
                _dm3Controller.ApplyGearsInstant(0, 0);
            }

            if (_controlsOverrider != null)
            {
                if (_controlsOverrider.Throttle != null) _controlsOverrider.Throttle.Set(0.0f);
                if (_controlsOverrider.Brake != null) _controlsOverrider.Brake.Set(1.0f);
                if (_controlsOverrider.IndependentBrake != null) _controlsOverrider.IndependentBrake.Set(1.0f);
                if (_controlsOverrider.Handbrake != null) _controlsOverrider.Handbrake.Set(1.0f);
                if (_controlsOverrider.Reverser != null) _controlsOverrider.Reverser.Set(0.0f);

                // Turn off lights, wipers, aux
                if (_controlsOverrider.HeadlightsFront != null) _controlsOverrider.HeadlightsFront.Set(0.0f);
                if (_controlsOverrider.HeadlightsRear != null) _controlsOverrider.HeadlightsRear.Set(0.0f);
                if (_controlsOverrider.CabLight != null) _controlsOverrider.CabLight.Set(0.0f);
                if (_controlsOverrider.IndCabLight != null) _controlsOverrider.IndCabLight.Set(0.0f);
                if (_controlsOverrider.Wipers != null) _controlsOverrider.Wipers.Set(0.0f);
                if (_controlsOverrider.Starter != null) _controlsOverrider.Starter.Set(0.0f);

                // Shut down engine
                if (_controlsOverrider.PowerOff != null)
                {
                    _controlsOverrider.PowerOff.Set(1.0f);
                }
            }

            // Also shut down any helper / multi-unit locomotives in this trainset
            if (_trainCar.trainset != null && _trainCar.trainset.cars != null)
            {
                foreach (var car in _trainCar.trainset.cars)
                {
                    if (car != null && car != _trainCar && car.IsLoco && car.SimController != null && car.SimController.controlsOverrider != null)
                    {
                        var overrider = car.SimController.controlsOverrider;
                        if (overrider.Throttle != null) overrider.Throttle.Set(0.0f);
                        if (overrider.Brake != null) overrider.Brake.Set(1.0f);
                        if (overrider.IndependentBrake != null) overrider.IndependentBrake.Set(1.0f);
                        if (overrider.Handbrake != null) overrider.Handbrake.Set(1.0f);
                        if (overrider.Reverser != null) overrider.Reverser.Set(0.0f);
                        if (overrider.HeadlightsFront != null) overrider.HeadlightsFront.Set(0.0f);
                        if (overrider.HeadlightsRear != null) overrider.HeadlightsRear.Set(0.0f);
                        if (overrider.CabLight != null) overrider.CabLight.Set(0.0f);
                        if (overrider.Starter != null) overrider.Starter.Set(0.0f);
                        if (overrider.PowerOff != null) overrider.PowerOff.Set(1.0f);
                    }
                }
            }
        }

        #endregion

        #region Public Control API

        /// <summary>
        /// Sets a destination stop target with distance and stop type.
        /// </summary>
        /// <param name="distance">Distance in meters to the destination stop.</param>
        /// <param name="isStation">True if stopping at a passenger/goods station platform.</param>
        /// <param name="isTerminus">True if stopping at a final buffer stop.</param>
        /// <param name="dwellTime">Station dwell time in seconds (30-60s).</param>
        public void SetDestination(float distance, bool isStation, bool isTerminus, float dwellTime)
        {
            DistanceToDestination = distance;
            IsStationDestination = isStation;
            IsTerminusDestination = isTerminus;
            StationDwellDuration = Mathf.Clamp(dwellTime, 10.0f, 300.0f);
        }

        /// <summary>
        /// Clears the destination stop target, allowing the locomotive to resume line speed.
        /// </summary>
        public void ClearDestination()
        {
            DistanceToDestination = float.PositiveInfinity;
            IsStationDestination = false;
            IsTerminusDestination = false;
        }

        /// <summary>
        /// Triggers an immediate emergency stop, cutting throttle and applying all emergency brakes.
        /// </summary>
        public void EmergencyStop()
        {
            _isEmergencyStop = true;
            TargetSpeedKmh = 0.0f;
            TargetSpeedMs = 0.0f;
            State = EngineState.Braking;
            _commandedThrottle = 0.0f;
            _commandedTrainBrake = 1.0f;
            _commandedIndependentBrake = 1.0f;
            if (_controlsOverrider != null)
            {
                if (_controlsOverrider.Throttle != null) _controlsOverrider.Throttle.Set(0.0f);
                if (_controlsOverrider.Brake != null) _controlsOverrider.Brake.Set(1.0f);
                if (_controlsOverrider.IndependentBrake != null) _controlsOverrider.IndependentBrake.Set(1.0f);
            }
        }

        /// <summary>
        /// Alias for EmergencyStop.
        /// </summary>
        public void EmergencyBrake()
        {
            EmergencyStop();
        }

        /// <summary>
        /// Clears emergency stop state and allows normal AI regulation to resume.
        /// </summary>
        public void Resume()
        {
            _isEmergencyStop = false;
            if (State == EngineState.TerminusStop || State == EngineState.Idle)
            {
                State = EngineState.Starting;
            }
        }

        /// <summary>
        /// Finds the connecting junction between two consecutive route tracks and determines the required branch index (0, 1, 2).
        /// </summary>
        public static bool TryGetJunctionBetweenTracks(RailTrack trackA, RailTrack trackB, out Junction junction, out byte requiredBranch)
        {
            junction = null;
            requiredBranch = 0;

            if (trackA == null || trackB == null) return false;

            // Direct check for connected junctions on trackA or trackB
            junction = trackA.outJunction ?? trackA.inJunction ?? trackB.inJunction ?? trackB.outJunction;
            if (junction == null) return false;

            // 1. Facing move: trackA is the inBranch (single trunk), diverging to trackB (one of outBranches)
            if (junction.inBranch != null && junction.inBranch.track == trackA)
            {
                if (junction.outBranches != null)
                {
                    for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch != null && branch.track == trackB)
                        {
                            requiredBranch = i;
                            return true;
                        }
                    }
                }
            }

            // 2. Trailing move: trackA is one of outBranches, converging onto trackB (the inBranch)
            if (junction.inBranch != null && junction.inBranch.track == trackB)
            {
                if (junction.outBranches != null)
                {
                    for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch != null && branch.track == trackA)
                        {
                            requiredBranch = i;
                            return true;
                        }
                    }
                }
            }

            // 3. Out-to-out or multi-branch connectivity
            if (junction.outBranches != null)
            {
                for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && (branch.track == trackB || branch.track == trackA))
                    {
                        requiredBranch = i;
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion
    }
}
