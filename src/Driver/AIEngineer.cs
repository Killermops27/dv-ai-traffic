using System;
using System.Collections;
using System.Collections.Generic;
using DV.Simulation.Cars;
using DV.ThingTypes;
using LocoSim.Definitions;
using LocoSim.Implementations;
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
        public float EffectiveObstacleDistance
        {
            get { return Mathf.Min(DistanceToObstacle, Mathf.Min(_corridorHoldDistance, _switchHoldDistance)); }
        }
        public float DistanceToDestination { get; set; }
        public bool IsStationDestination { get; set; }
        public bool IsTerminusDestination { get; set; }
        public float TerminusArrivalTime { get; private set; }
        public List<AITraffic.Navigation.SignalBlockInfo> UpcomingSignalBlocks
        {
            get { return _upcomingSignalBlocks; }
        }
        private readonly List<AITraffic.Navigation.SignalBlockInfo> _upcomingSignalBlocks = new List<AITraffic.Navigation.SignalBlockInfo>();

        private readonly List<TrainCar> _registeredConsistCars = new List<TrainCar>();
        /// <summary>
        /// Gets all TrainCar instances originally spawned with or assigned to this AI consist.
        /// Retained even if couplers break, cars derail, or the trainset splits into multiple fragments.
        /// </summary>
        public List<TrainCar> RegisteredConsistCars
        {
            get { return _registeredConsistCars; }
        }

        /// <summary>
        /// Registers all cars in the consist so they are tracked together for safety, derailment, and despawning.
        /// </summary>
        public void RegisterConsistCars(List<TrainCar> cars)
        {
            _registeredConsistCars.Clear();
            if (cars != null)
            {
                for (int i = 0; i < cars.Count; i++)
                {
                    var c = cars[i];
                    if (c != null && !_registeredConsistCars.Contains(c))
                    {
                        _registeredConsistCars.Add(c);
                    }
                }
            }
        }

        /// <summary>
        /// Indicates whether this locomotive or any car in its registered consist has suffered a derailment or crash.
        /// </summary>
        public bool HasConsistDerailed { get; private set; }

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

        public RailTrack CurrentTrack
        {
            get
            {
                if (_trainCar != null)
                {
                    if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null) return _trainCar.FrontBogie.track;
                    if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null) return _trainCar.RearBogie.track;
                }
                return null;
            }
        }

        public DM3TransmissionController DM3Controller { get { return _dm3Controller; } }
        public SpeedProfileResult CurrentSpeedProfile { get; private set; }
        public float SpawnTime { get; private set; }
        public float StationaryTimer { get; private set; }

        public bool IsWorkerDriven { get; set; }
        public bool IsEncounterTrain { get; set; }
        public event Action<AIEngineer> OnTerminusArrival;

        public bool HoldsSignalReservation(DVSignal signal)
        {
            if (signal == null) return false;
            return _reservedDVSignals.Contains(signal) || (signal.Parent != null && _reservedDVSignals.Contains(signal.Parent));
        }

        /// <summary>
        /// Determines whether a junction should remain locked against the player because this AI train
        /// has entered the governing signal block or is approaching the switch at speed.
        /// Yields only when the train has stopped or is crawling (< 1.5 km/h).
        /// </summary>
        public bool ShouldProtectJunctionLock(Junction junction)
        {
            if (junction == null) return false;
            if (CurrentSpeedKmh < 1.5f) return false;

            if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
            {
                var curBlock = _upcomingSignalBlocks[0];
                if (curBlock != null && curBlock.Switches != null)
                {
                    for (int s = 0; s < curBlock.Switches.Count; s++)
                    {
                        if (curBlock.Switches[s].Junction == junction)
                        {
                            return true;
                        }
                    }
                }
            }

            if (_trainCar != null)
            {
                float dist = Vector3.Distance(_trainCar.transform.position, junction.position);
                if (dist < 250f)
                {
                    return true;
                }
            }

            return false;
        }

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
        private float _ambientLockedReverser = 0.0f;

        private float _currentThrottle;
        private float _currentTrainBrake;
        private float _currentDynamicBrake;
        private float _currentIndependentBrake;
        private float _currentReverser;
        private float _brakeHoldTimer;

        // Wheel slip recovery & sander interval control
        public bool IsWheelSlipping { get { return _isWheelSlipping; } }
        public float SlipThrottleReduction { get { return _slipThrottleReduction; } }
        private bool _isWheelSlipping;
        private float _sanderActiveTimer;
        private float _sanderRestTimer;
        private bool _sanderRequested;
        private float _sandHoldTimer;
        private float _slipThrottleReduction;

        // Powertrain Protection: Thermal, Over-Current & Anti-Rollback
        public float CurrentMaxTemperature { get; private set; }
        public float CurrentAmpsPerTM { get; private set; }
        public float ThermalThrottleLimit { get; private set; }
        public float OvercurrentThrottleLimit { get; private set; }
        public bool IsOverheated { get; private set; }
        public bool IsRollbackDetected { get; private set; }
        public bool IsHillStarting { get; private set; }

        public bool IsLocoDM3
        {
            get
            {
                if (_dm3Controller != null && _dm3Controller.IsDM3) return true;
                if (_trainCar != null)
                {
                    if (_trainCar.carType == TrainCarType.LocoDM3) return true;
                    string lId = _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
                    if (lId.IndexOf("DM3", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }
                return false;
            }
        }

        // Single-Track Corridor & Deadlock Prevention
        public bool IsHoldingForCorridor { get { return _isHoldingForCorridor; } }
        public string CorridorHoldReason { get { return _corridorHoldReason; } }
        private AITraffic.Fleet.ConsistType _consistType = AITraffic.Fleet.ConsistType.RegionalFreight;
        public AITraffic.Fleet.ConsistType ConsistType { get { return _consistType; } set { _consistType = value; } }

        private float _thermalThrottleLimit;
        private float _overcurrentThrottleLimit;
        private bool _isOverheated;
        private bool _isRollbackDetected;
        private bool _isHoldingForCorridor;
        private string _corridorHoldReason;
        private float _corridorCheckCooldown;
        private float _corridorHoldDistance = float.PositiveInfinity; // Separate from DistanceToObstacle; not cleared by obstacle sensor
        private float _switchHoldDistance = float.PositiveInfinity; // Facing switch stop buffer; not cleared by obstacle sensor
        private bool _portsInitialized;

        private readonly List<Port> _temperaturePorts = new List<Port>();
        private readonly List<Port> _amperagePorts = new List<Port>();
        private Port _portAmpsPerTM;
        private Port _portTotalAmps;
        private Port _portEngineOn;
        private Port _portEngineRpm;
        private float _rollbackClearTimer;

        // Acceleration feedback & soft-launch throttle
        public float CurrentAccelerationMs2 { get; private set; }
        private float _lastSpeedMs;
        private float _smoothAccMs2;
        private float _rampThrottle;
        private float _hillAssistBoost;
        private float _hillRollbackHoldTimer;
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
        private readonly List<AITraffic.Navigation.SignalRegistry.UpcomingSignal> _filteredSignalsCache = new List<AITraffic.Navigation.SignalRegistry.UpcomingSignal>();
        private readonly List<RailTrack> _corridorTracksCache = new List<RailTrack>();
        private readonly HashSet<RailTrack> _reservedTracks = new HashSet<RailTrack>();
        private readonly HashSet<DVSignal> _reservedDVSignals = new HashSet<DVSignal>();
        private RailTrack _obstacleTrack;
        private float _obstacleRerouteCooldown;
        private float _signalRerouteCooldown;
        private float _signalWaitRetryTimer = 5.0f;
        private float _stoppedAtRedTimer;
        private float _stuckRecoveryCooldown = 10.0f;

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
            _thermalThrottleLimit = 1.0f;
            _overcurrentThrottleLimit = 1.0f;
            _rampThrottle = 0.0f;
            SpawnTime = Time.time;
            StationaryTimer = 0.0f;
            _heavySensorUpdateCooldown = UnityEngine.Random.Range(0.25f, 0.50f);

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
                if (_registeredConsistCars.Count == 0 && _trainCar.trainset != null && _trainCar.trainset.cars != null)
                {
                    RegisterConsistCars(_trainCar.trainset.cars);
                }
            }
        }

        private void Update()
        {
            if (_trainCar == null) return;

            // Safety killswitch: Immediate shutdown on derailment or collision across ANY car in the consist
            bool isLeadDerailed = _trainCar.derailed ||
                (_trainCar.FrontBogie != null && _trainCar.FrontBogie.HasDerailed) ||
                (_trainCar.RearBogie != null && _trainCar.RearBogie.HasDerailed);

            bool isAnyCarDerailed = isLeadDerailed;
            if (!isAnyCarDerailed && _registeredConsistCars.Count > 0)
            {
                for (int i = 0; i < _registeredConsistCars.Count; i++)
                {
                    var c = _registeredConsistCars[i];
                    if (c != null && (c.derailed || (c.FrontBogie != null && c.FrontBogie.HasDerailed) || (c.RearBogie != null && c.RearBogie.HasDerailed)))
                    {
                        isAnyCarDerailed = true;
                        break;
                    }
                }
            }

            if (isAnyCarDerailed)
            {
                HasConsistDerailed = true;
                if (State != EngineState.TerminusStop)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("[AIEngineer] Consist derailment detected on train '{0}'! Triggering emergency crash shutdown and releasing reservations.", _trainCar.ID));
                    EmergencyCrashShutdown();
                }
                return;
            }

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

            UpdatePathAndJunctions(dt);
            if (_trainCar == null) return;
            UpdateSensors(dt);
            UpdateSingleTrackCorridorProtection(dt);
            UpdateWheelSlipProtection(dt);
            UpdatePowertrainProtection(dt);
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
                InitializeSimulationPorts();
            }

            if (_controlsOverrider != null)
            {
                _hasDynamicBrake = _controlsOverrider.DynamicBrake != null;
                EnsureEngineRunning();
            }

            ReleaseAllConsistHandbrakes();
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
            if (!float.IsInfinity(_corridorHoldDistance)) _corridorHoldDistance = Mathf.Max(0f, _corridorHoldDistance - distTraveled);
            if (!float.IsInfinity(_switchHoldDistance)) _switchHoldDistance = Mathf.Max(0f, _switchHoldDistance - distTraveled);
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
                        Vector3 nextStart = nextTrack.curve.GetPointAt(0.0f);
                        Vector3 nextEnd = nextTrack.curve.GetPointAt(1.0f);

                        float distEndToNext = Mathf.Min(Vector3.Distance(curEnd, nextStart), Vector3.Distance(curEnd, nextEnd));
                        float distStartToNext = Mathf.Min(Vector3.Distance(curStart, nextStart), Vector3.Distance(curStart, nextEnd));

                        TargetDirection = (distEndToNext <= distStartToNext) ? 1.0f : -1.0f;
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
                    TargetDirection = (Vector3.Dot(locoHeading, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }

                // AI line service trains must always run in gear matching route movement
                Vector3 desiredMoveVector = tangent * TargetDirection;
                _desiredReverser = (Vector3.Dot(_trainCar.transform.forward, desiredMoveVector) >= 0.0f) ? 1.0f : -1.0f;

                if (!IsWorkerDriven)
                {
                    if (_ambientLockedReverser == 0.0f)
                    {
                        _ambientLockedReverser = _desiredReverser;
                    }
                    else
                    {
                        _desiredReverser = _ambientLockedReverser;
                    }

                    // Enforce that TargetDirection matches forward travel along the track tangent
                    Vector3 forwardMove = _trainCar.transform.forward * _desiredReverser;
                    TargetDirection = (Vector3.Dot(forwardMove, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }
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
            RailTrack obstTrack;
            if (AITraffic.Navigation.SignalRegistry.TryFindUpcomingObstacle(currentTrack, currentSpan, TargetDirection, UpcomingTracks, _trainCar != null ? _trainCar.trainset : null, out distObstacle, _trainCar, out obstTrack))
            {
                DistanceToObstacle = distObstacle;
                _obstacleTrack = obstTrack;
            }
            else
            {
                DistanceToObstacle = float.PositiveInfinity;
                _obstacleTrack = null;
            }

            // Calculate upcoming Signal Blocks along active route (Block 1: Train -> S1, Block 2: S1 -> S2)
            AITraffic.Navigation.SignalRegistry.CalculateUpcomingSignalBlocks(
                currentTrack, currentSpan, TargetDirection, UpcomingTracks, _trainCar != null ? _trainCar.trainset : null, _upcomingSignalBlocks, 3000f);
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
                else if (!IsWorkerDriven)
                {
                    // Ambient train is off its planned route!
                    // Check if rear bogie is still on path before declaring complete diversion
                    bool rearOnPath = false;
                    if (_trainCar != null && _trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
                    {
                        int rearIdx = CurrentPath.Tracks.IndexOf(_trainCar.RearBogie.track);
                        if (rearIdx >= 0)
                        {
                            rearOnPath = true;
                        }
                    }

                    if (!rearOnPath)
                    {
                        // Locomotive has completely departed from planned route!
                        // Attempt to find a forward path from curTrack to destination
                        RailTrack destTrack = (CurrentPath.Tracks.Count > 0) ? CurrentPath.Tracks[CurrentPath.Tracks.Count - 1] : null;
                        bool rerouted = false;
                        if (destTrack != null && destTrack != curTrack && AITraffic.Navigation.RailGraph.Instance != null && AITraffic.Navigation.RailGraph.Instance.IsInitialized)
                        {
                            var pathOptions = new AITraffic.Navigation.PathfinderOptions
                            {
                                Requester = this,
                                RequesterTrainset = _trainCar != null ? _trainCar.trainset : null,
                                PreventPlayerOvertake = false,
                                AvoidOccupiedTracks = true,
                                StrictlyAvoidOccupied = false
                            };
                            var pathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
                            bool forwardOnTrack = (TargetDirection >= 0f);
                            if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null &&
                                _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null &&
                                _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
                            {
                                forwardOnTrack = (_trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span);
                            }
                            var newPath = pathfinder.FindPath(curTrack, destTrack, forwardOnTrack, pathOptions);
                            if (newPath != null && newPath.IsValid && newPath.Tracks != null && newPath.Tracks.Count > 1)
                            {
                                CurrentPath = newPath;
                                CurrentPathTrackIndex = 0;
                                _upcomingTracks.Clear();
                                for (int t = 0; t < CurrentPath.Tracks.Count; t++)
                                {
                                    _upcomingTracks.Add(CurrentPath.Tracks[t]);
                                }
                                rerouted = true;
                                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                {
                                    Main.ModEntry.Logger.Log(string.Format("[AITraffic] Ambient train '{0}' diverted off route onto '{1}'. Successfully rerouted forward to '{2}' ({3} tracks).",
                                        _trainCar != null ? _trainCar.ID : "unknown", curTrack.name, DestinationStationName ?? destTrack.name, newPath.Tracks.Count));
                                }
                            }
                        }

                        if (!rerouted)
                        {
                            // No valid forward path to destination: despawn ambient train!
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            {
                                Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Ambient train '{0}' diverted off route onto '{1}' with no forward path to destination. Despawning.",
                                    _trainCar != null ? _trainCar.ID : "unknown", curTrack.name));
                            }
                            AITraffic.Fleet.TrainDespawner.DespawnTrain(this, true);
                            return;
                        }
                    }
                }
            }

            // 1b. Legacy passing route adoption disabled: AI must maintain planned mainline corridor
            // and throw switches to its own path rather than adopting diverging yard sidings.
            // If the route ahead is blocked, dynamic obstacle detour handles rerouting below.

            // 1c. Dynamic Obstacle Detour: if route ahead is blocked by cars on an intermediate track
            if (curTrack != null && _obstacleTrack != null && _obstacleTrack != curTrack && DistanceToObstacle < 2500f)
            {
                _obstacleRerouteCooldown -= dt;
                if (_obstacleRerouteCooldown <= 0.0f)
                {
                    bool rerouted = TryDynamicObstacleReroute(curTrack, _obstacleTrack);
                    _obstacleRerouteCooldown = rerouted ? 4.0f : 8.0f;
                }
            }

            // 1d. Dynamic Red Signal Detour: if stopped at an Hp 0 (Red) signal for an extended period (>= 30s)
            if (curTrack != null && ApproachingSignal != null && DistanceToSignal < 250f)
            {
                bool isSignalRed = (ApproachingSignal.CurrentAspect != null && ApproachingSignal.CurrentAspect.DisallowPassing);

                if (isSignalRed && CurrentSpeedKmh < 1.0f)
                {
                    _stoppedAtRedTimer += dt;
                    if (_stoppedAtRedTimer >= 30.0f)
                    {
                        _signalRerouteCooldown -= dt;
                        if (_signalRerouteCooldown <= 0.0f)
                        {
                            bool rerouted = TryDynamicRedSignalReroute(curTrack);
                            _signalRerouteCooldown = rerouted ? 8.0f : 15.0f;
                        }
                    }
                }
                else
                {
                    _stoppedAtRedTimer = 0.0f;
                }

                if (isSignalRed)
                {
                    // 1e. Periodic Signal Wait Retry & DVSignals Wakeup:
                    // When stopped at an Hp 0 (Red) signal, periodically re-request switch alignment
                    // for upcoming clear tracks and wake DVSignals controllers to re-evaluate block state.
                    if (CurrentSpeedKmh < 1.0f)
                    {
                        _signalWaitRetryTimer -= dt;
                        if (_signalWaitRetryTimer <= 0.0f)
                        {
                            _signalWaitRetryTimer = 5.0f;

                            bool isBlockedByObstacle = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f);

                            if (!isBlockedByObstacle)
                            {
                                // Re-request alignment for clear upcoming switches along planned route
                                if (_upcomingTracks != null && _upcomingTracks.Count > 1 && AITraffic.Navigation.JunctionController.Instance != null)
                                {
                                    int scanLimit = Math.Min(_upcomingTracks.Count, 64);
                                    for (int sIdx = 1; sIdx < scanLimit; sIdx++)
                                    {
                                        var tA = _upcomingTracks[sIdx - 1];
                                        var tB = _upcomingTracks[sIdx];
                                        if (tA != null && tB != null && tA != tB)
                                        {
                                            Junction junc;
                                            byte reqBr;
                                            if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(tA, tB, out junc, out reqBr))
                                            {
                                                if (junc.selectedBranch != reqBr)
                                                {
                                                    if (!AITraffic.Navigation.SignalRegistry.IsJunctionReservedByPlayerSignal(junc))
                                                    {
                                                        AITraffic.Navigation.JunctionController.Instance.RequestJunctionAlignment(junc, reqBr, this);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }

                                // Awaken DVSignals to re-evaluate block clearance and clear signal aspect
                                AITraffic.Navigation.SignalRegistry.WakeAndForceUpdateSignal(ApproachingSignal);
                                if (AITraffic.Navigation.SignalRegistry.TryReserveDVSignal(ApproachingSignal))
                                {
                                    var govSig = ApproachingSignal != null && ApproachingSignal.Parent != null ? ApproachingSignal.Parent : ApproachingSignal;
                                    if (govSig != null && !_reservedDVSignals.Contains(govSig))
                                    {
                                        _reservedDVSignals.Add(govSig);
                                    }
                                }
                            }
                            else
                            {
                                // If blocked by obstacle, proactively release signal reservation to break potential deadlocks
                                if (ApproachingSignal != null)
                                {
                                    var govSig = ApproachingSignal.Parent != null ? ApproachingSignal.Parent : ApproachingSignal;
                                    AITraffic.Navigation.SignalRegistry.ClearDVSignalReservation(ApproachingSignal);
                                    _reservedDVSignals.Remove(ApproachingSignal);
                                    if (govSig != null) _reservedDVSignals.Remove(govSig);
                                }
                            }
                        }
                    }
                    else
                    {
                        _signalWaitRetryTimer = 5.0f;
                    }
                }
            }

            // 1f. Stuck Train & Route Sanity Recovery:
            // If the train is stationary (< 1.0 km/h) and upcoming route contains a sharp hairpin turn,
            // invalidate and recalculate a clean forward route.
            if (curTrack != null && CurrentSpeedKmh < 1.0f && CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 1)
            {
                bool upcomingHairpin = false;
                int startIdx = CurrentPathTrackIndex;
                for (int k = startIdx; k < CurrentPath.Tracks.Count - 1 && k < startIdx + 4; k++)
                {
                    var tA = CurrentPath.Tracks[k];
                    var tB = CurrentPath.Tracks[k + 1];
                    if (tA != null && tA.curve != null && tB != null && tB.curve != null)
                    {
                        var edgeA = AITraffic.Navigation.RailGraph.Instance.GetEdge(tA);
                        var edgeB = AITraffic.Navigation.RailGraph.Instance.GetEdge(tB);
                        if (edgeA != null && edgeB != null)
                        {
                            var sharedNode = AITraffic.Navigation.RailGraph.Instance.GetConnectingNode(edgeA, edgeB);
                            if (sharedNode != null)
                            {
                                Vector3 vIn = AITraffic.Navigation.RailGraph.GetIncomingVector(sharedNode, edgeA);
                                Vector3 vOut = AITraffic.Navigation.RailGraph.GetDepartingVector(sharedNode, edgeB);
                                if (Vector3.Dot(vIn, vOut) < 0.20f)
                                {
                                    upcomingHairpin = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                if (upcomingHairpin)
                {
                    _stuckRecoveryCooldown -= dt;
                    if (_stuckRecoveryCooldown <= 0.0f)
                    {
                        _stuckRecoveryCooldown = 10.0f;
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        {
                            Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Train '{0}' detected upcoming hairpin loop in planned route on track '{1}'. Recalculating route.",
                                _trainCar != null ? _trainCar.ID : "unknown", curTrack.name));
                        }

                        RailTrack destTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
                        if (destTrack != null && destTrack != curTrack)
                        {
                            bool forwardOnTrack = (TargetDirection >= 0f);
                            if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null &&
                                _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null &&
                                _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
                            {
                                forwardOnTrack = (_trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span);
                            }

                            var pathOptions = new AITraffic.Navigation.PathfinderOptions
                            {
                                Requester = this,
                                RequesterTrainset = _trainCar != null ? _trainCar.trainset : null,
                                PreferSpeedOverDistance = true,
                                AvoidOccupiedTracks = true,
                                StrictlyAvoidOccupied = true,
                                MaxSearchDistance = 5000000f
                            };

                            var pathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
                            var recoveredPath = pathfinder.FindPath(curTrack, destTrack, forwardOnTrack, pathOptions);
                            if (recoveredPath != null && recoveredPath.IsValid && recoveredPath.Tracks != null && recoveredPath.Tracks.Count > 1)
                            {
                                CurrentPath = recoveredPath;
                                CurrentPathTrackIndex = 0;
                                _upcomingTracks.Clear();
                                for (int t = 0; t < CurrentPath.Tracks.Count; t++)
                                {
                                    _upcomingTracks.Add(CurrentPath.Tracks[t]);
                                }
                            }
                        }
                    }
                }
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
                double curSpanForDir = (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0;
                float fracDir = (float)Mathf.Clamp01((float)(curSpanForDir / curTrack.curve.length));
                Vector3 tangent = curTrack.curve.GetTangentAt(fracDir);

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
                    Vector3 locoHeading = (_trainCar != null) ? _trainCar.transform.forward : Vector3.forward;
                    TargetDirection = (Vector3.Dot(locoHeading, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }

                // 2c. AI line service trains must always run in gear matching route direction
                Vector3 routeMoveVector = tangent * TargetDirection;
                Vector3 trainHeading = (_trainCar != null) ? _trainCar.transform.forward : Vector3.forward;
                _desiredReverser = (Vector3.Dot(trainHeading, routeMoveVector) >= 0.0f) ? 1.0f : -1.0f;

                if (!IsWorkerDriven)
                {
                    if (_ambientLockedReverser == 0.0f)
                    {
                        _ambientLockedReverser = _desiredReverser;
                    }
                    else
                    {
                        _desiredReverser = _ambientLockedReverser;
                    }

                    // Enforce that TargetDirection matches forward travel along the track tangent
                    Vector3 forwardMove = trainHeading * _desiredReverser;
                    TargetDirection = (Vector3.Dot(forwardMove, tangent) >= 0.0f) ? 1.0f : -1.0f;
                }
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
                    var cars = _trainCar.trainset.cars;
                    TrainCar trailingCar = null;
                    if (cars.Count > 1)
                    {
                        int myIdx = cars.IndexOf(_trainCar);
                        if (myIdx == 0)
                        {
                            trailingCar = cars[cars.Count - 1];
                        }
                        else if (myIdx == cars.Count - 1)
                        {
                            trailingCar = cars[0];
                        }
                        else
                        {
                            TrainCar end0 = cars[0];
                            TrainCar endN = cars[cars.Count - 1];
                            float d0 = (end0 != null) ? Vector3.Distance(trainPos, end0.transform.position) : 0f;
                            float dN = (endN != null) ? Vector3.Distance(trainPos, endN.transform.position) : 0f;
                            trailingCar = (d0 > dN) ? end0 : endN;
                        }
                    }
                    else
                    {
                        trailingCar = cars[0];
                    }

                    if (trailingCar != null)
                    {
                        if (trailingCar.FrontBogie != null && trailingCar.RearBogie != null)
                        {
                            float dF = Vector3.Distance(trainPos, trailingCar.FrontBogie.transform.position);
                            float dR = Vector3.Distance(trainPos, trailingCar.RearBogie.transform.position);
                            rearBogie = (dF > dR) ? trailingCar.FrontBogie : trailingCar.RearBogie;
                        }
                        else
                        {
                            rearBogie = trailingCar.RearBogie ?? trailingCar.FrontBogie;
                        }
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

                // 3a. Proactively align and lock switches along upcoming planned route
                // Dynamic Chain-of-Switches Alignment:
                // If a governing signal facing the train is restricted (Hp 0 / Red), we must work down
                // the chain of switches along the interlocking route (up to 64 tracks or 3500m, matching
                // DVSignals TrackWalker.MaxDepth) until the governing signal clears to Green.
                // A fixed distance/track cutoff (e.g. 1200m / 20 tracks) leaves switches at the far end of
                // station ladders or crossover throats unaligned, permanently pinning exit signals at Hp 0!
                float curTrackLen = (curTrack != null && curTrack.curve != null) ? curTrack.curve.length : 0f;
                float distToEndOfCurTrack = (TargetDirection >= 0.0f) ? Mathf.Max(0f, curTrackLen - (float)curSpan) : Mathf.Max(0f, (float)curSpan);
                float accumulatedSwitchDist = distToEndOfCurTrack;
                _switchHoldDistance = float.PositiveInfinity;

                bool isParked = (State == EngineState.StationHold || State == EngineState.TerminusStop);
                bool isStoppedWaiting = (CurrentSpeedKmh < 1.0f && (isParked || State == EngineState.Idle || (State == EngineState.Braking && TargetSpeedKmh <= 1.0f)));
                if ((State == EngineState.TerminusStop || (State == EngineState.StationHold && DwellTimeRemaining > 10.0f)) && AITraffic.Navigation.JunctionController.Instance != null)
                {
                    AITraffic.Navigation.JunctionController.Instance.ReleaseAllLocksFor(this);
                }

                bool isRideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);

                // Determine if a governing signal directly facing the train is currently restricted (Hp 0 / Red)
                DVSignal governingSig = null;
                bool isGoverningSignalRed = false;
                if (ApproachingSignal != null && DistanceToSignal < 2000f)
                {
                    governingSig = ApproachingSignal.Parent != null ? ApproachingSignal.Parent : ApproachingSignal;
                    if (governingSig != null && governingSig.IsOn && governingSig.CurrentAspect != null)
                    {
                        isGoverningSignalRed = governingSig.CurrentAspect.DisallowPassing;
                    }
                }

                // Dynamic lookahead horizon:
                // If governing signal is restricted (Hp 0), expand horizon up to 64 tracks or 3500m until it clears.
                // Once signal is green or if line is open, maintain standard advance alignment (~1500m / 30 tracks).
                int maxTracksToScan = isGoverningSignalRed ? Math.Min(_upcomingTracks.Count, 64) : Math.Min(_upcomingTracks.Count, 30);
                float maxDistToScan = isGoverningSignalRed ? 3500f : 1500f;

                for (int i = 1; i < _upcomingTracks.Count; i++)
                {
                    if (i >= maxTracksToScan || accumulatedSwitchDist > maxDistToScan)
                    {
                        break;
                    }

                    var trackA = _upcomingTracks[i - 1];
                    var trackB = _upcomingTracks[i];

                    if (trackA != null && trackB != null && trackA != trackB)
                    {
                        Junction junction;
                        byte requiredBranch;
                        if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out requiredBranch))
                        {
                            float dist = Vector3.Distance(trainPos, junction.position);
                            float routeDist = accumulatedSwitchDist;

                            // Check if junction belongs to a downstream player-occupied signal block
                            bool isJunctionInDownstreamPlayerBlock = false;
                            bool isJunctionInCurrentBlock = false;
                            if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
                            {
                                var curBlock = _upcomingSignalBlocks[0];
                                if (curBlock != null && curBlock.Switches != null)
                                {
                                    for (int s = 0; s < curBlock.Switches.Count; s++)
                                    {
                                        if (curBlock.Switches[s].Junction == junction)
                                        {
                                            isJunctionInCurrentBlock = true;
                                            break;
                                        }
                                    }
                                }

                                if (!isRideAlong)
                                {
                                    for (int b = 1; b < _upcomingSignalBlocks.Count; b++)
                                    {
                                        var block = _upcomingSignalBlocks[b];
                                        if (block != null && block.IsPlayerOccupied && block.Switches != null)
                                        {
                                            for (int s = 0; s < block.Switches.Count; s++)
                                            {
                                                if (block.Switches[s].Junction == junction)
                                                {
                                                    isJunctionInDownstreamPlayerBlock = true;
                                                    break;
                                                }
                                            }
                                        }
                                        if (isJunctionInDownstreamPlayerBlock) break;
                                    }
                                }
                            }

                            bool isPlayerNearJunction = !isRideAlong && AITraffic.Navigation.SignalRegistry.IsJunctionOccupiedByPlayer(junction, _trainCar != null ? _trainCar.trainset : null);
                            bool isJunctionReservedByPlayer = !isRideAlong && AITraffic.Navigation.SignalRegistry.IsJunctionReservedByPlayerSignal(junction);

                            // Bug 14: Once a train has entered a signal block (or is approaching switch in close proximity),
                            // junctions in that block MUST be locked against the player unless the AI has stopped or is crawling (< 1.5 km/h).
                            bool protectInBlockSwitch = ShouldProtectJunctionLock(junction);

                            if (!isRideAlong && !protectInBlockSwitch && (isJunctionInDownstreamPlayerBlock || isJunctionReservedByPlayer || isPlayerNearJunction))
                            {
                                // Do not throw or lock switches for downstream blocks occupied by player or when stopped/crawling near player
                                AITraffic.Navigation.JunctionController.Instance.ReleaseJunction(junction, this);
                                if (trackB != null && trackB.curve != null)
                                {
                                    accumulatedSwitchDist += trackB.curve.length;
                                }
                                if (accumulatedSwitchDist > maxDistToScan || i >= maxTracksToScan) break;
                                continue;
                            }

                            bool isBlockedByObstacle = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f);

                            // Advance Alignment: set switch immediately so station exit/entry route is aligned
                            // Proactively sets switches even while waiting for corridor/departure so exit signals clear to Green
                            bool switchAligned = (junction.selectedBranch == requiredBranch);
                            bool canAlignSwitches = !isBlockedByObstacle && (State != EngineState.TerminusStop) && (State != EngineState.StationHold || DwellTimeRemaining <= 10.0f);
                            if (!switchAligned && canAlignSwitches)
                            {
                                switchAligned = AITraffic.Navigation.JunctionController.Instance.RequestJunctionAlignment(junction, requiredBranch, this);
                                if (switchAligned && isGoverningSignalRed && governingSig != null)
                                {
                                    // As we align switches down the chain, synchronously re-evaluate the governing signal aspect
                                    AITraffic.Navigation.SignalRegistry.WakeAndForceUpdateSignal(governingSig);
                                    if (governingSig.IsOn && governingSig.CurrentAspect != null)
                                    {
                                        isGoverningSignalRed = governingSig.CurrentAspect.DisallowPassing;
                                        if (!isGoverningSignalRed)
                                        {
                                            // The governing signal has turned GREEN! Interlocking path is complete.
                                            // Tighten remaining lookahead horizon to avoid needlessly throwing distant switches
                                            maxTracksToScan = Math.Min(maxTracksToScan, Math.Max(i + 3, 20));
                                            maxDistToScan = Mathf.Min(maxDistToScan, Mathf.Max(accumulatedSwitchDist + 300f, 1200f));
                                        }
                                    }
                                }
                            }

                            // If an upcoming junction within 600m along route is NOT aligned to our route (e.g. occupied or locked by another train):
                            // Treat the switch fouling point as a protective stop boundary!
                            // Only apply when outside the switch entry zone (routeDist >= 30m) and facing (diverging move).
                            bool isFacingMove = (junction.inBranch != null && junction.inBranch.track == trackA);
                            if (!switchAligned && isFacingMove && routeDist >= 30.0f && routeDist < 600f)
                            {
                                _switchHoldDistance = Mathf.Min(_switchHoldDistance, routeDist);
                            }

                            // Critical Approach Lock: within 250m ahead of train along route (or immediate proximity or in entered block)
                            if (routeDist < 250f || dist < 200f || isJunctionInCurrentBlock)
                            {
                                if (!isBlockedByObstacle)
                                {
                                    AITraffic.Navigation.JunctionController.Instance.TryLockJunction(junction, this, 60f);

                                    // Register passing clearance for rear bogie when entering switch zone
                                    if ((routeDist < 40f || dist < 40f) && rearBogie != null)
                                    {
                                        AITraffic.Navigation.JunctionController.Instance.RegisterTrainPassing(this, junction, rearBogie, trackB, 90f);
                                    }
                                }
                                else
                                {
                                    // If stopped and blocked by obstacle, proactively release junction lock so clearing train can throw it
                                    AITraffic.Navigation.JunctionController.Instance.ReleaseJunction(junction, this);
                                }
                            }
                        }
                    }

                    if (trackB != null && trackB.curve != null)
                    {
                        accumulatedSwitchDist += trackB.curve.length;
                    }
                    if (accumulatedSwitchDist > maxDistToScan || i >= maxTracksToScan)
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
                // Strictly reserve ONLY the immediate governing signal directly in front of the train!
                // Reserving downstream exit/departure signals prematurely overlaps station track reservations,
                // causing DVSignals TrackReserver.IsSignalReservedByAnother to drop the station entry signal to Hp 0.
                var desiredSignalReservations = new HashSet<DVSignal>();

                DVSignal targetSignalToReserve = null;
                bool isBlockedByObstacleForSignals = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f);

                if (!isBlockedByObstacleForSignals)
                {
                    // If an immediate signal stands directly in front of the train (within approach distance < 2000m),
                    // that signal is the SOLE authorized reservation target!
                    // Under NO circumstances should downstream signals (e.g. station departure signal S104) be reserved
                    // while an unpassed entry/governing signal stands facing the train.
                    DVSignal immediateSig = null;
                    if (ApproachingSignal != null && DistanceToSignal < 2000f)
                    {
                        immediateSig = ApproachingSignal.Parent != null ? ApproachingSignal.Parent : ApproachingSignal;
                    }

                    if (immediateSig != null)
                    {
                        if (AITraffic.Navigation.SignalRegistry.IsGoverningSignal(immediateSig))
                        {
                            bool routeToSigClear = true;
                            bool downstreamReady = true;

                            if (_upcomingSignalBlocks.Count > 0)
                            {
                                var block1 = _upcomingSignalBlocks[0];
                                routeToSigClear = block1.AreSwitchesAligned && block1.IsClear;

                                // If block1 ends at immediateSig, verify that block2 (the block entered past immediateSig) is ready
                                if (block1.ExitSignal == immediateSig && _upcomingSignalBlocks.Count > 1)
                                {
                                    var block2 = _upcomingSignalBlocks[1];
                                    downstreamReady = block2.AreSwitchesAligned && block2.IsClear;
                                }
                            }

                            if (routeToSigClear && downstreamReady)
                            {
                                targetSignalToReserve = immediateSig;
                            }
                        }
                        // CRITICAL: When an unpassed governing signal stands directly in front of the train,
                        // DO NOT fall through to reserve downstream signals beyond it under ANY circumstance!
                    }
                    // Otherwise, reserve the immediate next Main Signal ahead of the train within lookahead horizon
                    // ONLY when there is NO immediate governing signal facing the train!
                    else if (_upcomingSignalBlocks.Count > 0 && _upcomingSignalBlocks[0].ExitSignal != null)
                    {
                        var block1 = _upcomingSignalBlocks[0];
                        float distToMainSignal = block1.DistanceToExit;

                        // Reserve when within lookahead horizon (2000m) and switches up to the signal are aligned and clear
                        if (distToMainSignal < 2000f && block1.AreSwitchesAligned && block1.IsClear)
                        {
                            var candidateSig = block1.ExitSignal;

                            // Check downstream block governed by candidateSig (e.g. station track)
                            bool downstreamReady = true;
                            if (_upcomingSignalBlocks.Count > 1 && _upcomingSignalBlocks[1].EntrySignal == candidateSig)
                            {
                                var block2 = _upcomingSignalBlocks[1];
                                downstreamReady = block2.AreSwitchesAligned && block2.IsClear;
                            }

                            if (downstreamReady)
                            {
                                targetSignalToReserve = candidateSig;
                            }
                        }
                    }

                    if (targetSignalToReserve != null)
                    {
                        desiredSignalReservations.Add(targetSignalToReserve);
                    }
                }

                // Progressive release of passed or unneeded signal reservations FIRST
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

                // If stopped and blocked by obstacle, also ensure ApproachingSignal is cleared
                if (isBlockedByObstacleForSignals && ApproachingSignal != null)
                {
                    AITraffic.Navigation.SignalRegistry.ClearDVSignalReservation(ApproachingSignal);
                    _reservedDVSignals.Remove(ApproachingSignal);
                }

                // Now reserve the governing signal if not already held
                if (targetSignalToReserve != null && !_reservedDVSignals.Contains(targetSignalToReserve))
                {
                    if (AITraffic.Navigation.SignalRegistry.TryReserveDVSignal(targetSignalToReserve))
                    {
                        _reservedDVSignals.Add(targetSignalToReserve);
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

            // Enforce that our planned route ahead MUST be physically blocked by player or obstacles before even considering an alternative
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
            // 2a. NEVER adopt yard, siding, or shunting tracks; no track may be occupied by player
            for (int i = 0; i < alignedRouteTracks.Count; i++)
            {
                if (AITraffic.Navigation.Pathfinder.IsYardOrShuntingTrack(alignedRouteTracks[i]))
                    return false;
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
                    RequesterTrainset = _trainCar != null ? _trainCar.trainset : null,
                    PreventPlayerOvertake = false,
                    AvoidOccupiedTracks = true,
                    StrictlyAvoidOccupied = true
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

        /// <summary>
        /// Dynamic Obstacle Detour:
        /// If an upcoming intermediate track on our planned route is physically blocked by stationary cars
        /// (e.g. from freshly populated station tracks or unexpected stopped stock), dynamically calculates
        /// an alternative route avoiding the occupied track and splices it into CurrentPath.
        /// </summary>
        private bool TryDynamicObstacleReroute(RailTrack curTrack, RailTrack blockedTrack)
        {
            if (curTrack == null || blockedTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
                return false;

            if (_trainCar == null) return false;

            // Destination track
            RailTrack destTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
            if (destTrack == null || blockedTrack == destTrack)
                return false; // Cannot detour around final destination track

            // Ensure blocked track is actually in our upcoming route
            int blockedIdx = CurrentPath.Tracks.IndexOf(blockedTrack);
            if (blockedIdx <= CurrentPathTrackIndex)
                return false;

            var pathOptions = new AITraffic.Navigation.PathfinderOptions
            {
                Requester = this,
                RequesterTrainset = _trainCar.trainset,
                PreferSpeedOverDistance = true,
                AvoidOccupiedTracks = true,
                StrictlyAvoidOccupied = true,
                AvoidReservedTracks = true,
                StrictlyAvoidReserved = false,
                PreventPlayerOvertake = false,
                MaxSearchDistance = 5000000f,
                ExcludedTracks = new HashSet<RailTrack> { blockedTrack }
            };

            bool forwardOnTrack = (TargetDirection >= 0f);
            if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null &&
                _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null &&
                _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
            {
                forwardOnTrack = (_trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span);
            }
            var pathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
            var alternativePath = pathfinder.FindPath(curTrack, destTrack, forwardOnTrack, pathOptions);

            if (alternativePath == null || !alternativePath.IsValid || alternativePath.Tracks == null || alternativePath.Tracks.Count < 2)
                return false;

            // Verify that the alternative route does NOT contain the blocked track
            if (alternativePath.Tracks.Contains(blockedTrack))
                return false;

            // Adopt the alternative detour route!
            var combinedTracks = new List<RailTrack>();
            for (int i = 0; i <= CurrentPathTrackIndex; i++)
            {
                combinedTracks.Add(CurrentPath.Tracks[i]);
            }
            for (int i = 1; i < alternativePath.Tracks.Count; i++)
            {
                var trk = alternativePath.Tracks[i];
                if (!combinedTracks.Contains(trk))
                {
                    combinedTracks.Add(trk);
                }
            }

            var fullPathfinder = new AITraffic.Navigation.Pathfinder(AITraffic.Navigation.RailGraph.Instance);
            var newFullPath = fullPathfinder.BuildPathFromTracks(combinedTracks);

            if (newFullPath != null && newFullPath.IsValid)
            {
                // Verify no sharp hairpin kinks anywhere in the spliced detour
                bool hasHairpin = false;
                for (int k = 0; k < newFullPath.Tracks.Count - 1; k++)
                {
                    var tA = newFullPath.Tracks[k];
                    var tB = newFullPath.Tracks[k + 1];
                    if (tA != null && tA.curve != null && tB != null && tB.curve != null)
                    {
                        var edgeA = AITraffic.Navigation.RailGraph.Instance.GetEdge(tA);
                        var edgeB = AITraffic.Navigation.RailGraph.Instance.GetEdge(tB);
                        if (edgeA != null && edgeB != null)
                        {
                            var sharedNode = AITraffic.Navigation.RailGraph.Instance.GetConnectingNode(edgeA, edgeB);
                            if (sharedNode != null)
                            {
                                Vector3 vIn = AITraffic.Navigation.RailGraph.GetIncomingVector(sharedNode, edgeA);
                                Vector3 vOut = AITraffic.Navigation.RailGraph.GetDepartingVector(sharedNode, edgeB);
                                if (Vector3.Dot(vIn, vOut) < -0.30f)
                                {
                                    hasHairpin = true;
                                    break;
                                }
                            }
                        }
                    }
                }
                if (hasHairpin)
                    return false;

                CurrentPath = newFullPath;
                _upcomingTracks.Clear();
                int newIdx = CurrentPath.Tracks.IndexOf(curTrack);
                if (newIdx >= 0) CurrentPathTrackIndex = newIdx;
                for (int i = CurrentPathTrackIndex; i < CurrentPath.Tracks.Count; i++)
                {
                    _upcomingTracks.Add(CurrentPath.Tracks[i]);
                }

                _obstacleTrack = null;
                DistanceToObstacle = float.PositiveInfinity;

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Detour: Route ahead was blocked by cars on '{0}'. Successfully re-routed via alternative clear path ({1} tracks) to destination '{2}'.",
                        blockedTrack.name, CurrentPath.Tracks.Count, DestinationStationName ?? destTrack.name));
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// Scans upcoming junctions within approach range of an Hp 0 (Red) signal.
        /// If a diverging junction has an alternative clear branch (such as a parallel siding, passing loop,
        /// or double-track) that reaches our destination without using the blocked branch, dynamically adopts it.
        /// </summary>
        private bool TryDynamicRedSignalReroute(RailTrack curTrack)
        {
            if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
                return false;

            int curIdx = CurrentPathTrackIndex;
            for (int i = curIdx; i < CurrentPath.Tracks.Count - 1 && i < curIdx + 6; i++)
            {
                var trackA = CurrentPath.Tracks[i];
                var trackB = CurrentPath.Tracks[i + 1];
                if (trackA == null || trackB == null) continue;

                Junction junction;
                byte reqBranch;
                if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out reqBranch))
                {
                    if (junction != null && junction.outBranches != null && junction.outBranches.Count > 1)
                    {
                        // Check if any other branch at this junction is clear and leads to destination
                        for (int b = 0; b < junction.outBranches.Count; b++)
                        {
                            var branch = junction.outBranches[b];
                            if (branch == null || branch.track == null || branch.track == trackB) continue;

                            // NEVER detour into yard storage, loading, or industrial tracks from a mainline/through route
                            var branchEdge = (AITraffic.Navigation.RailGraph.Instance != null)
                                ? AITraffic.Navigation.RailGraph.Instance.GetEdge(branch.track)
                                : null;

                            string bName = branch.track.name ?? "";
                            bool isYardBranch = (branchEdge != null && branchEdge.IsYardTrack) ||
                                                bName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                                                bName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                                bName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                                                bName.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                bName.IndexOf("-L]", StringComparison.OrdinalIgnoreCase) >= 0;

                            bool isDoubleTrackOrMain = (branchEdge != null && branchEdge.IsDoubleTrackMainline) ||
                                                       bName.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       bName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                       bName.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0;

                            if (isYardBranch && !isDoubleTrackOrMain)
                                continue;

                            // If this alternate branch is not physically blocked, try rerouting excluding trackB
                            bool isCarsOnBranch = (AITraffic.Navigation.RailGraph.Instance != null) &&
                                                  AITraffic.Navigation.RailGraph.Instance.IsTrackOccupied(branch.track, null, _trainCar != null ? _trainCar.trainset : null);
                            if (!isCarsOnBranch)
                            {
                                if (TryDynamicObstacleReroute(curTrack, trackB))
                                {
                                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                    {
                                        Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Red Signal Detour: Train '{0}' stopped at Hp 0 signal before switch '{1}'. Successfully rerouted via clear alternative branch to destination.",
                                            _trainCar != null ? _trainCar.ID : "unknown", junction.name));
                                    }
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Relative priority of train consist for single-track passing loop negotiation.
        /// Passenger and heavy freight have mainline priority over regional and shunter freight.
        /// Player train has absolute top priority.
        /// </summary>
        private static int GetTrainPriority(AIEngineer eng)
        {
            if (eng == null) return 999; // Non-AIEngineer or Player train has top priority
            if (eng.IsWorkerDriven) return 500; // AI Worker trains driven on behalf of player
            switch (eng.ConsistType)
            {
                case AITraffic.Fleet.ConsistType.PassengerCommuter:
                    return 400; // Fixed passenger timetable
                case AITraffic.Fleet.ConsistType.MainlineHeavy:
                    return 300; // Heavy bulk freight (>1000t)
                case AITraffic.Fleet.ConsistType.RegionalFreight:
                    return 200;
                case AITraffic.Fleet.ConsistType.ShunterFreight:
                    return 100;
                default:
                    return 200;
            }
        }

        /// <summary>
        /// Dynamic Passing Loop Siding Adoption:
        /// Scans upcoming junctions within approach range. If a junction branches into a clear passing siding
        /// or station passing loop [S], diverts CurrentPath into the siding so our train can yield to an oncoming
        /// player train or higher-priority through-train on the mainline.
        /// </summary>
        private bool TryDynamicPassingReroute(RailTrack curTrack)
        {
            if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
                return false;

            int curIdx = CurrentPathTrackIndex;
            for (int i = curIdx; i < CurrentPath.Tracks.Count - 1 && i < curIdx + 8; i++)
            {
                var trackA = CurrentPath.Tracks[i];
                var trackB = CurrentPath.Tracks[i + 1];
                if (trackA == null || trackB == null) continue;

                Junction junction;
                byte reqBranch;
                if (AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out reqBranch))
                {
                    if (junction != null && junction.outBranches != null && junction.outBranches.Count > 1)
                    {
                        for (int b = 0; b < junction.outBranches.Count; b++)
                        {
                            var branch = junction.outBranches[b];
                            if (branch == null || branch.track == null || branch.track == trackB) continue;

                            var altTrack = branch.track;
                            bool isSiding = AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(altTrack) ||
                                            (altTrack.name != null && (
                                                altTrack.name.IndexOf("[S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                altTrack.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                altTrack.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                altTrack.name.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0));

                            bool isInvalid = AITraffic.Navigation.Pathfinder.IsIndustrialYardOrStorageTrack(altTrack) ||
                                             (altTrack.inJunction == null && altTrack.outJunction == null);

                            if (isSiding && !isInvalid)
                            {
                                // Verify siding is clear (no cars, no player)
                                bool isPlayerOnSiding = AITraffic.Navigation.SignalRegistry.IsTrackOccupiedByPlayer(altTrack, null);
                                bool isCarsOnSiding = (AITraffic.Navigation.RailGraph.Instance != null) &&
                                                      AITraffic.Navigation.RailGraph.Instance.IsTrackOccupied(altTrack, null, _trainCar != null ? _trainCar.trainset : null);

                                if (!isPlayerOnSiding && !isCarsOnSiding)
                                {
                                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                    {
                                        Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Passing: Yielding train '{0}' adopting passing loop '{1}' to allow through traffic on '{2}'.",
                                            _trainCar != null ? _trainCar.ID : "unknown", altTrack.name, trackB.name));
                                    }
                                    return TryDynamicObstacleReroute(curTrack, trackB);
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        public float DistToCorridorStart { get; private set; }

        /// <summary>
        /// Single-Track Corridor & Anti-Deadlock Interlocking:
        /// Scans the upcoming single-track corridor bounded by passing loops, sidings, or stations.
        /// If an opposing AI train or oncoming/occupying player train is detected in the corridor:
        /// - If an intermediate passing siding is available, the yielding train diverts into the loop and holds.
        /// - The higher-priority train (or player train) maintains the through mainline route.
        /// - If holding before entering the single track, waits safely outside the switch clearance margin.
        /// - Player train encounters always grant the player full through-mainline priority.
        /// Respects Ride-Along mode exemption when player is riding the AI train.
        /// </summary>
        private void UpdateSingleTrackCorridorProtection(float dt)
        {
            if (CurrentPath == null || CurrentPath.Tracks == null || _trainCar == null) return;
            if (AITraffic.Navigation.RailGraph.Instance == null || !AITraffic.Navigation.RailGraph.Instance.IsInitialized) return;

            _corridorCheckCooldown -= dt;
            if (_corridorCheckCooldown > 0.0f) return;
            _corridorCheckCooldown = 0.30f; // Evaluate ~3 times per second

            RailTrack curTrack = null;
            if (_trainCar.FrontBogie != null) curTrack = _trainCar.FrontBogie.track;
            if (curTrack == null && _trainCar.RearBogie != null) curTrack = _trainCar.RearBogie.track;
            if (curTrack == null) return;

            // 1. Identify contiguous upcoming single-track corridor segments ahead
            _corridorTracksCache.Clear();
            var corridorTracks = _corridorTracksCache;
            float distToCorridorStart = 0.0f;
            float accumulatedDist = 0.0f;
            bool enteredCorridor = false;

            double curSpan = (_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0;
            float curTrackLen = (curTrack.curve != null) ? curTrack.curve.length : 50.0f;
            float distRemainingOnCur = (TargetDirection >= 0.0f) ? Mathf.Max(0.0f, curTrackLen - (float)curSpan) : Mathf.Max(0.0f, (float)curSpan);
            accumulatedDist = distRemainingOnCur;

            var curEdge = AITraffic.Navigation.RailGraph.Instance.GetEdge(curTrack);
            bool isCurDouble = (curEdge != null && curEdge.IsDoubleTrackMainline) ||
                               (curTrack != null && curTrack.name != null && (curTrack.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || curTrack.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0));
            bool isCurPassingSiding = AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(curTrack);
            bool isCurrentTrackSingle = (curEdge != null && !isCurDouble && !curEdge.IsYardTrack && !isCurPassingSiding);

            if (isCurrentTrackSingle)
            {
                enteredCorridor = true;
                distToCorridorStart = 0.0f;
                corridorTracks.Add(curTrack);
            }

            for (int i = 0; i < _upcomingTracks.Count; i++)
            {
                var trk = _upcomingTracks[i];
                if (trk == null || trk == curTrack) continue;

                var edge = AITraffic.Navigation.RailGraph.Instance.GetEdge(trk);
                bool isDouble = (edge != null && edge.IsDoubleTrackMainline) ||
                                (trk != null && trk.name != null && (trk.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || trk.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0));
                bool isPassingSiding = AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(trk);
                bool isSingle = (edge != null && !isDouble && !edge.IsYardTrack && !isPassingSiding);

                if (isSingle)
                {
                    if (!enteredCorridor)
                    {
                        enteredCorridor = true;
                        distToCorridorStart = accumulatedDist;
                    }
                    corridorTracks.Add(trk);
                }
                else
                {
                    if (enteredCorridor)
                    {
                        // Reached a passing siding, station loop, or double-track line at the end of the corridor!
                        break;
                    }
                }

                accumulatedDist += (trk.curve != null ? trk.curve.length : 50.0f);
                if (accumulatedDist > 15000.0f) break; // Uncapped to full corridor length (up to 15km)
            }

            DistToCorridorStart = distToCorridorStart;

            if (corridorTracks.Count == 0)
            {
                _isHoldingForCorridor = false;
                _corridorHoldReason = null;
                _corridorHoldDistance = float.PositiveInfinity;
                return;
            }

            // 2. Check for Conflicts in the upcoming single-track corridor
            bool isPlayerInCorridor = false;
            bool isReservedByOther = false;
            AIEngineer opposingAIEngineer = null;
            string conflictDetails = null;

            bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);

            for (int c = 0; c < corridorTracks.Count; c++)
            {
                var cTrk = corridorTracks[c];

                // 2a. Check Player Train Conflict (respects Ride-Along mode exemption)
                if (!rideAlong && (AITraffic.Navigation.SignalRegistry.IsTrackOccupiedByPlayer(cTrk, _trainCar.trainset) ||
                                   AITraffic.Navigation.SignalRegistry.IsTrackReservedByPlayerSignal(cTrk)))
                {
                    isPlayerInCorridor = true;
                    conflictDetails = "Player train or signal route in corridor ahead";
                    break;
                }

                // 2b. Check AI Train Conflict — direction-aware to avoid false locks on same-direction followers
                object holderObj;
                if (AITraffic.Navigation.RailGraph.Instance.TryGetTrackReservationHolder(cTrk, this, out holderObj))
                {
                    var holderEng = holderObj as AIEngineer;
                    if (holderEng == null)
                    {
                        // Non-AIEngineer holder — treat as blocking to be safe
                        isReservedByOther = true;
                        conflictDetails = "Corridor reserved by unknown agent";
                        break;
                    }

                    Vector3 thisMoveDir = this._trainCar != null ? this._trainCar.transform.forward * this._desiredReverser : Vector3.zero;
                    Vector3 holderMoveDir = holderEng._trainCar != null ? holderEng._trainCar.transform.forward * holderEng._desiredReverser : Vector3.zero;
                    
                    bool isOpposing = Vector3.Dot(thisMoveDir, holderMoveDir) < 0.0f;
                    bool isStopped  = (holderEng.CurrentSpeedKmh < 1.5f);

                    if (isOpposing || isStopped)
                    {
                        isReservedByOther = true;
                        opposingAIEngineer = holderEng;
                        conflictDetails = isOpposing ? "Opposing AI train in corridor" : "Stopped AI train blocking corridor";
                        break;
                    }
                }
            }

            // 3. Resolve Conflict: Passing Loop Coordination or Hold
            if (isPlayerInCorridor || isReservedByOther)
            {
                // Determine if WE should yield:
                // Rule 1: Always yield to Player train
                // Rule 2: Dynamic Deadlock-Breaking Yield Inversion:
                //         If a train is stopped (< 1.5 km/h) and blocked by an obstacle (< 150m),
                //         it CANNOT physically proceed. The opposing train (which has rerouted/clearing path)
                //         MUST move first to break the lock!
                // Rule 3: In-Block Invariant:
                //         A train already inside the single-track corridor NEVER yields to a train outside!
                // Rule 4: If AI vs AI under normal running outside the block, lower priority train yields
                // Rule 5: If equal priority outside the block, train farther from corridor start yields (closer train enters)
                // Rule 6: Deterministic consist ID comparison breaks exact distance ties
                bool weShouldYield = isPlayerInCorridor;
                if (!weShouldYield && opposingAIEngineer != null)
                {
                    bool thisBlocked = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f);
                    bool otherBlocked = (opposingAIEngineer.CurrentSpeedKmh < 1.5f && opposingAIEngineer.DistanceToObstacle < 150f);

                    if (thisBlocked && !otherBlocked)
                    {
                        weShouldYield = true; // We are blocked, but other train has clear path to vacate! We must yield!
                    }
                    else if (!thisBlocked && otherBlocked)
                    {
                        weShouldYield = false; // We are the clearing train with an open detour! Do not yield!
                    }
                    else
                    {
                        // In-Block Invariant: A train already inside the single-track corridor NEVER yields to a train outside!
                        bool thisInCorridor = isCurrentTrackSingle;
                        bool otherInCorridor = (opposingAIEngineer.DistToCorridorStart <= 5.0f || (opposingAIEngineer.CurrentTrack != null && corridorTracks.Contains(opposingAIEngineer.CurrentTrack)));

                        if (thisInCorridor && !otherInCorridor)
                        {
                            weShouldYield = false; // We are already inside the single track; we must exit!
                        }
                        else if (!thisInCorridor && otherInCorridor)
                        {
                            weShouldYield = true; // Opposing train is already inside the single track; we cannot enter!
                        }
                        else
                        {
                            int myPriority = GetTrainPriority(this);
                            int otherPriority = GetTrainPriority(opposingAIEngineer);

                            if (myPriority < otherPriority)
                            {
                                weShouldYield = true;
                            }
                            else if (myPriority == otherPriority)
                            {
                                float otherDist = opposingAIEngineer.DistToCorridorStart;
                                if (Mathf.Abs(distToCorridorStart - otherDist) > 5.0f)
                                {
                                    weShouldYield = (distToCorridorStart > otherDist); // Farther train yields; closer train enters!
                                }
                                else
                                {
                                    // Deterministic tie-breaker: compare unique TrainCar IDs
                                    string myId = (_trainCar != null) ? _trainCar.ID : string.Empty;
                                    string otherId = (opposingAIEngineer._trainCar != null) ? opposingAIEngineer._trainCar.ID : string.Empty;
                                    weShouldYield = string.CompareOrdinal(myId, otherId) < 0;
                                }
                            }
                            else
                            {
                                weShouldYield = false; // We have higher priority! Mainline through-passage.
                            }
                        }
                    }
                }

                // If WE should yield, try to divert into an upcoming passing loop siding!
                if (weShouldYield)
                {
                    bool reroutedIntoSiding = TryDynamicPassingReroute(curTrack);

                    // If we rerouted into a passing siding ahead:
                    // We must advance through the switch INTO the siding track, and hold 45m before the EXIT of the siding!
                    if (reroutedIntoSiding)
                    {
                        float distToSidingExit = 0f;
                        bool foundSidingExit = false;
                        for (int u = 0; u < _upcomingTracks.Count; u++)
                        {
                            var uTrk = _upcomingTracks[u];
                            if (uTrk == null) continue;
                            float uLen = (uTrk.curve != null ? uTrk.curve.length : 50f);
                            distToSidingExit += uLen;
                            if (AITraffic.Navigation.Pathfinder.IsPassingOrSidingTrack(uTrk))
                            {
                                foundSidingExit = true;
                                break;
                            }
                        }

                        _isHoldingForCorridor = true;
                        _corridorHoldReason = conflictDetails;

                        if (foundSidingExit)
                        {
                            float sidingHoldDist = Mathf.Max(15.0f, distToSidingExit - 45.0f);
                            _corridorHoldDistance = Mathf.Min(_corridorHoldDistance, sidingHoldDist);
                        }
                        else
                        {
                            float holdStopDist = Mathf.Max(5.0f, distToCorridorStart - 45.0f);
                            _corridorHoldDistance = Mathf.Min(_corridorHoldDistance, holdStopDist);
                        }
                        return;
                    }

                    // If we have NOT yet entered the single-track corridor (holding in passing loop / siding / station):
                    // We MUST HOLD at the passing point before entering the single track!
                    if (!isCurrentTrackSingle || distToCorridorStart > 10.0f)
                    {
                        _isHoldingForCorridor = true;
                        _corridorHoldReason = conflictDetails;

                        // Stop safely before the start of the single-track section outside the 45m switch clearance margin
                        float holdStopDist = Mathf.Max(5.0f, distToCorridorStart - 45.0f);
                        _corridorHoldDistance = Mathf.Min(_corridorHoldDistance, holdStopDist);
                        return;
                    }
                    else
                    {
                        // We are already inside the single track:
                        // Hold safely before collision with a safe buffer
                        _isHoldingForCorridor = true;
                        _corridorHoldReason = conflictDetails;
                        float safeStopDist = Mathf.Max(15.0f, DistanceToObstacle - 50.0f);
                        _corridorHoldDistance = Mathf.Min(_corridorHoldDistance, safeStopDist);
                        return;
                    }
                }
                else
                {
                    // WE HAVE PRIORITY! (Mainline through-passage)
                    // Only hold if the opposing train (or player) is ALREADY physically inside the single-track corridor!
                    bool opposingAlreadyInCorridor = false;
                    if (opposingAIEngineer != null)
                    {
                        RailTrack oppTrack = opposingAIEngineer.CurrentTrack;
                        // Opposing train is inside the single track if its DistToCorridorStart <= 5m,
                        // OR if its current track is in our upcoming corridorTracks!
                        opposingAlreadyInCorridor = (opposingAIEngineer.DistToCorridorStart <= 5.0f || (oppTrack != null && corridorTracks.Contains(oppTrack)));
                    }
                    else if (isPlayerInCorridor)
                    {
                        opposingAlreadyInCorridor = true;
                    }

                    if (opposingAlreadyInCorridor)
                    {
                        _isHoldingForCorridor = true;
                        _corridorHoldReason = conflictDetails;
                        float holdDist = (!isCurrentTrackSingle || distToCorridorStart > 10.0f)
                            ? Mathf.Max(5.0f, distToCorridorStart - 45.0f)
                            : Mathf.Max(15.0f, DistanceToObstacle - 50.0f);
                        _corridorHoldDistance = Mathf.Min(_corridorHoldDistance, holdDist);
                        return;
                    }
                    else
                    {
                        // Opposing train is waiting/holding outside or in its passing siding.
                        // We have the clear right-of-way! DO NOT HOLD!
                        bool wasHolding = _isHoldingForCorridor;
                        _isHoldingForCorridor = false;
                        _corridorHoldReason = null;
                        _corridorHoldDistance = float.PositiveInfinity;
                        if (wasHolding)
                        {
                            _pathUpdateCooldown = 0.0f;
                            _speedProfileUpdateCooldown = 0.0f;
                            for (int b = 0; b < _upcomingSignalBlocks.Count; b++)
                            {
                                var sig = _upcomingSignalBlocks[b].EntrySignal ?? _upcomingSignalBlocks[b].ExitSignal;
                                if (sig != null && sig.Block != null)
                                {
                                    sig.Block.FlagAsDirty();
                                }
                            }
                        }
                    }
                }
            }
            else
            {
                // Corridor is completely clear!
                bool wasHolding = _isHoldingForCorridor;
                _isHoldingForCorridor = false;
                _corridorHoldReason = null;
                _corridorHoldDistance = float.PositiveInfinity;
                if (wasHolding)
                {
                    _pathUpdateCooldown = 0.0f;
                    _speedProfileUpdateCooldown = 0.0f;
                    for (int b = 0; b < _upcomingSignalBlocks.Count; b++)
                    {
                        var sig = _upcomingSignalBlocks[b].EntrySignal ?? _upcomingSignalBlocks[b].ExitSignal;
                        if (sig != null && sig.Block != null)
                        {
                            sig.Block.FlagAsDirty();
                        }
                    }
                }
            }

            // 4. Update Reservations
            if (State == EngineState.TerminusStop)
            {
                // Release all track reservations upon terminus shutdown
                foreach (var rTrk in _reservedTracks)
                {
                    AITraffic.Navigation.RailGraph.Instance.ReleaseTrackReservation(rTrk, this);
                }
                _reservedTracks.Clear();
            }
            else if (!_isHoldingForCorridor)
            {
                // Acquire track reservations for the corridor to protect it from opposing traffic
                for (int c = 0; c < corridorTracks.Count; c++)
                {
                    var cTrk = corridorTracks[c];
                    if (!_reservedTracks.Contains(cTrk))
                    {
                        if (AITraffic.Navigation.RailGraph.Instance.TryReserveTrack(cTrk, this))
                        {
                            _reservedTracks.Add(cTrk);
                        }
                    }
                }
            }
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

            // Prevent trusting false green signals:
            // If a signal governs a block where our intended switches are not yet aligned (e.g. occupied by player),
            // the signal is reading the WRONG route and may show a false Green.
            // We must temporarily treat such signals as Red (Hp 0) to hold the train until the switch is aligned.
            _filteredSignalsCache.Clear();
            var filteredSignals = _filteredSignalsCache;
            float effObstacleDistance = EffectiveObstacleDistance;

            if (_upcomingSignals != null)
            {
                for (int i = 0; i < _upcomingSignals.Count; i++)
                {
                    var sigEntry = _upcomingSignals[i];
                    bool isUntrustworthy = false;

                    // Only check switch alignment for the immediate governing signal block facing the train
                    // (within approach distance, e.g. < 400m), NOT distant downstream signals hundreds of meters away
                    if (sigEntry.Distance < 400f && _upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
                    {
                        var block = _upcomingSignalBlocks[0];
                        if (block != null && (block.EntrySignal == sigEntry.Signal || block.ExitSignal == sigEntry.Signal) && AITraffic.Navigation.SignalRegistry.IsGoverningSignal(sigEntry.Signal))
                        {
                            if (block.Switches != null && block.Switches.Count > 0 && !block.AreSwitchesAligned)
                            {
                                isUntrustworthy = true;
                            }
                        }
                    }

                    if (isUntrustworthy)
                    {
                        // Signal is reading the wrong physical path! Create a dummy Red aspect signal entry.
                        // Since we cannot easily mock DVSignal, we instead inject a 0km/h speed limit via distanceToObstacle.
                        effObstacleDistance = Mathf.Min(effObstacleDistance, Mathf.Max(0.0f, sigEntry.Distance - 15.0f));
                    }
                    
                    filteredSignals.Add(sigEntry);
                }
            }

            SpeedProfileResult profile = SpeedProfiler.CalculateTargetSpeed(
                currentTrack: currentTrack,
                currentSpan: currentSpan,
                direction: TargetDirection,
                upcomingTracks: UpcomingTracks,
                upcomingSignals: filteredSignals,
                distanceToObstacle: effObstacleDistance,
                distanceToDestination: DistanceToDestination,
                isStationStop: IsStationDestination,
                isTerminusStop: IsTerminusDestination
            );

            // Consist Tail Speed Protection:
            // Evaluate governing speed limit across all cars in the consist.
            // If any car/bogie is still traversing a restricted curve or turnout, clamp the train's target speed
            // so the locomotive cannot pull trailing cars through the curve above its safe limit!
            float consistTailSpeedLimit = SpeedProfileGenerator.MaxNetworkSpeedKmh;
            var carsList = _registeredConsistCars.Count > 0 ? _registeredConsistCars : (_trainCar != null && _trainCar.trainset != null ? _trainCar.trainset.cars : null);
            if (carsList != null && carsList.Count > 0 && SpeedProfiler != null)
            {
                for (int c = 0; c < carsList.Count; c++)
                {
                    var car = carsList[c];
                    if (car == null) continue;
                    if (car.FrontBogie != null && car.FrontBogie.track != null)
                    {
                        float fLimit = SpeedProfiler.GetTrackSpeedLimit(car.FrontBogie.track);
                        if (fLimit < consistTailSpeedLimit) consistTailSpeedLimit = fLimit;
                    }
                    if (car.RearBogie != null && car.RearBogie.track != null)
                    {
                        float rLimit = SpeedProfiler.GetTrackSpeedLimit(car.RearBogie.track);
                        if (rLimit < consistTailSpeedLimit) consistTailSpeedLimit = rLimit;
                    }
                }
            }

            if (consistTailSpeedLimit < profile.TargetSpeedKmh)
            {
                profile.TargetSpeedKmh = consistTailSpeedLimit;
                profile.TargetSpeedMs = SpeedProfileGenerator.KmHToMs(consistTailSpeedLimit);
                profile.LimitingReason = SpeedLimitReason.CurvatureRadius;
            }

            CurrentSpeedProfile = profile;
            TargetSpeedKmh = profile.TargetSpeedKmh;
            TargetSpeedMs = profile.TargetSpeedMs;
        }

        #endregion

        #region Wheel Slip, Traction & Powertrain Protection

        private void InitializeSimulationPorts()
        {
            _temperaturePorts.Clear();
            _amperagePorts.Clear();
            _portAmpsPerTM = null;
            _portTotalAmps = null;
            _portEngineOn = null;
            _portsInitialized = false;

            if (_trainCar == null || _trainCar.SimController == null || _trainCar.SimController.SimulationFlow == null)
                return;

            var allPorts = _trainCar.SimController.SimulationFlow.AllPorts;
            if (allPorts == null) return;

            for (int i = 0; i < allPorts.Count; i++)
            {
                var port = allPorts[i];
                if (port == null || string.IsNullOrEmpty(port.id)) continue;

                // 1. Temperature Ports (Engine, Oil, Water, Transmission, TM)
                if (port.valueType == PortValueType.TEMPERATURE ||
                    port.id.IndexOf("TEMPERATURE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    port.id.IndexOf("engineTemp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    port.id.IndexOf("oilTemp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    port.id.IndexOf("waterTemp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _temperaturePorts.Add(port);
                }

                // 2. Amperage Ports (Traction motor load on Diesel-Electric / Electric locomotives)
                if ((port.valueType == PortValueType.AMPS ||
                     port.id.IndexOf("AMPS", StringComparison.OrdinalIgnoreCase) >= 0) &&
                    port.id.IndexOf("MAX", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    _amperagePorts.Add(port);
                    if (port.id.IndexOf("AMPS_PER_TM", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _portAmpsPerTM = port;
                    }
                    else if (port.id.IndexOf("TOTAL_AMPS", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _portTotalAmps = port;
                    }
                }

                // 3. Engine Running & RPM Ports
                if (_portEngineOn == null && (port.id.IndexOf("ENGINE_ON", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("engineOn", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    _portEngineOn = port;
                }
                else if (_portEngineRpm == null && (port.id.IndexOf("ENGINE_RPM", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("engineRpm", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.EndsWith(".RPM", StringComparison.OrdinalIgnoreCase)))
                {
                    _portEngineRpm = port;
                }
            }

            _portsInitialized = true;
        }

        public bool IsEngineRunning()
        {
            if (_controlsOverrider != null && _controlsOverrider.EngineOnReader != null)
            {
                return _controlsOverrider.EngineOnReader.IsOn;
            }
            if (_portEngineOn != null)
            {
                return _portEngineOn.Value > 0.5f;
            }
            if (_portEngineRpm != null)
            {
                return _portEngineRpm.Value > 100.0f;
            }
            return true;
        }

        private float GetTractionAuthorityMultiplier()
        {
            return Mathf.Min(_slipThrottleReduction, Mathf.Min(_thermalThrottleLimit, _overcurrentThrottleLimit));
        }

        private void UpdateWheelSlipProtection(float dt)
        {
            _isWheelSlipping = false;

            // 1. Check WheelslipController on locomotive (traction power slip)
            if (_trainCar.SimController != null && _trainCar.SimController.wheelslipController != null)
            {
                if (_trainCar.SimController.wheelslipController.IsWheelslipping)
                {
                    _isWheelSlipping = true;
                }
            }

            // 2. Request sander and modulate throttle
            if (_isWheelSlipping)
            {
                _sanderRequested = true;
                _sandHoldTimer = 2.5f; // Maintain sand application to restore firm track grip

                // Rapidly cut back throttle authority to break wheel slip cycle
                // On hill starts, enforce a higher authority floor (>= 0.50f) so momentary slip cannot choke engine below hill holding power
                float minSlipAuth = (IsLocoDM3 && CurrentSpeedKmh < 8.0f) ? 0.60f : (IsHillStarting ? 0.50f : 0.20f);
                _slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, minSlipAuth, 4.0f * dt);
            }
            else
            {
                if (_sandHoldTimer > 0.0f)
                {
                    _sandHoldTimer -= dt;
                    _sanderRequested = true; // Continue applying sand while regaining full grip
                }

                // Smoothly restore traction authority
                _slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, 1.0f, 0.40f * dt);
            }

            UpdateSanderActuation(dt);
        }

        private void UpdateSanderActuation(float dt)
        {
            if (_controlsOverrider == null || _controlsOverrider.Sander == null) return;

            // Hard shutoff only when train is parked, station holding, or idle with no throttle demanded
            if ((CurrentSpeedKmh < 0.2f && _commandedThrottle < 0.01f && !_isWheelSlipping) ||
                State == EngineState.Idle || State == EngineState.StationHold || State == EngineState.TerminusStop)
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
                    // Active sander interval (pulse up to 3.0s)
                    _controlsOverrider.Sander.Set(1.0f);
                    _sanderActiveTimer += dt;

                    if (_sanderActiveTimer >= 3.0f)
                    {
                        // Pulse duration reached: cut sander off and start short rest interval (1.0s)
                        _controlsOverrider.Sander.Set(0.0f);
                        _sanderActiveTimer = 0.0f;
                        _sanderRestTimer = 1.0f;
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

        private void UpdatePowertrainProtection(float dt)
        {
            if (!_portsInitialized && _trainCar != null && _trainCar.SimController != null && _trainCar.SimController.SimulationFlow != null)
            {
                InitializeSimulationPorts();
            }

            // 1. Thermal Management (Engine, Oil, Water & Transmission Temperatures)
            float maxTemp = 0.0f;
            for (int i = 0; i < _temperaturePorts.Count; i++)
            {
                float t = _temperaturePorts[i].Value;
                if (t > maxTemp) maxTemp = t;
            }
            CurrentMaxTemperature = maxTemp;

            // Thermal derating curve:
            // Under 95°C: 100% authority
            // 95°C - 105°C: proportional linear throttle reduction (from 1.0 down to 0.25)
            // >= 105°C: Hard cutoff to idle (0.0) until cooled down below 95°C to prevent engine explosion
            if (maxTemp >= 105.0f)
            {
                _isOverheated = true;
            }
            else if (maxTemp < 95.0f)
            {
                _isOverheated = false;
            }

            IsOverheated = _isOverheated;

            if (_isOverheated)
            {
                _thermalThrottleLimit = 0.0f; // Cut to idle for cooling
            }
            else if (maxTemp > 95.0f)
            {
                float factor = (maxTemp - 95.0f) / 10.0f;
                _thermalThrottleLimit = Mathf.Lerp(1.0f, 0.25f, factor);
            }
            else
            {
                _thermalThrottleLimit = 1.0f;
            }
            ThermalThrottleLimit = _thermalThrottleLimit;

            // 2. Over-Current Protection (Traction Motor Amperage on Diesel-Electric Locomotives)
            string liveryId = _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
            bool isDE2 = _trainCar.carType == TrainCarType.LocoShunter ||
                         liveryId.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         liveryId.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isDE6 = _trainCar.carType == TrainCarType.LocoDiesel ||
                         liveryId.IndexOf("DE6", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         liveryId.IndexOf("Diesel", StringComparison.OrdinalIgnoreCase) >= 0;
            bool isBE2 = _trainCar.carType == TrainCarType.LocoMicroshunter ||
                         liveryId.IndexOf("BE2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         liveryId.IndexOf("Microshunter", StringComparison.OrdinalIgnoreCase) >= 0;

            int numMotors = 1;
            if (isDE2) numMotors = 2;
            else if (isDE6) numMotors = 6;
            else if (isBE2) numMotors = 1;

            float maxAmpsPerTm = 0.0f;
            if (_portAmpsPerTM != null)
            {
                maxAmpsPerTm = _portAmpsPerTM.Value;
            }
            else if (_portTotalAmps != null)
            {
                maxAmpsPerTm = _portTotalAmps.Value / Mathf.Max(1, numMotors);
            }
            else if (_amperagePorts.Count > 0)
            {
                for (int i = 0; i < _amperagePorts.Count; i++)
                {
                    var p = _amperagePorts[i];
                    if (p.id.IndexOf("MAX", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    float a = p.Value;
                    if (p.id.IndexOf("TOTAL", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        a /= Mathf.Max(1, numMotors);
                    }
                    if (a > maxAmpsPerTm) maxAmpsPerTm = a;
                }
            }
            CurrentAmpsPerTM = maxAmpsPerTm;

            // Over-current derating:
            // 1) Non-electric locos (DH4, DM3, Steam): no traction motor fuse -> full authority (1.0f).
            // 2) DE6: Heavy mainline freight hauler.
            //    - Rolling (>= 3.0 km/h): 100% full throttle authority! In Derail Valley, DE6 traction motors
            //      safely haul at 100% throttle under counter-EMF. There is no fragile TM fuse to blow while rolling.
            //    - Stationary / hill-start (< 3.0 km/h): only if current is dangerously extreme (> 550A per motor)
            //      softly derate to 0.50f, never choking down to 5%.
            // 3) DE2: Shunter with fragile traction motor fuse.
            //    - Rolling (>= 5.0 km/h): 100% throttle authority.
            //    - Stationary / hill-start (< 5.0 km/h): If maxAmpsPerTm >= 420A, clamp throttle limit to 0.30f
            //      to protect the fuse.
            if (!isDE2 && !isDE6 && !isBE2)
            {
                _overcurrentThrottleLimit = 1.0f;
            }
            else if (isDE6)
            {
                if (CurrentSpeedKmh >= 3.0f)
                {
                    _overcurrentThrottleLimit = 1.0f;
                }
                else
                {
                    if (maxAmpsPerTm >= 550.0f)
                    {
                        _overcurrentThrottleLimit = 0.50f;
                    }
                    else if (maxAmpsPerTm > 450.0f)
                    {
                        float factor = (maxAmpsPerTm - 450.0f) / 100.0f;
                        _overcurrentThrottleLimit = Mathf.Lerp(1.0f, 0.50f, factor);
                    }
                    else
                    {
                        _overcurrentThrottleLimit = 1.0f;
                    }
                }
            }
            else if (isDE2)
            {
                if (CurrentSpeedKmh >= 5.0f)
                {
                    _overcurrentThrottleLimit = 1.0f;
                }
                else
                {
                    if (maxAmpsPerTm >= 500.0f) // was 420.0f
                    {
                        _overcurrentThrottleLimit = 0.50f; // was 0.30f
                        _rampThrottle = Mathf.Min(_rampThrottle, 0.50f); // was 0.30f
                    }
                    else if (maxAmpsPerTm > 450.0f) // was 380.0f
                    {
                        float overcurrentFactor = (maxAmpsPerTm - 450.0f) / (500.0f - 450.0f); // was (380.0f) / (420.0f - 380.0f)
                        _overcurrentThrottleLimit = Mathf.Lerp(1.0f, 0.50f, overcurrentFactor); // was 0.40f
                    }
                    else
                    {
                        _overcurrentThrottleLimit = 1.0f;
                    }
                }
            }
            else // BE2
            {
                _overcurrentThrottleLimit = (CurrentSpeedKmh >= 4.0f || maxAmpsPerTm < 400.0f) ? 1.0f : 0.50f;
            }
            OvercurrentThrottleLimit = _overcurrentThrottleLimit;

            // 3. Anti-Rollback & Runaway Protection on Grades
            float forwardSpeedMs = _trainCar.GetForwardSpeed();
            // Compare against actual commanded reverser lever orientation to prevent tangent flutter inversions
            float activeReverser = (_commandedReverser != 0.0f) ? _commandedReverser : _desiredReverser;
            float directedSpeedMs = forwardSpeedMs * activeReverser; // positive = moving in commanded gear direction, negative = rolling backwards

            bool engineRunning = IsEngineRunning();

            // Settle timer: if train has been essentially stopped for > 0.4s, clear rollback clamp
            if (Mathf.Abs(forwardSpeedMs) < 0.15f)
            {
                _rollbackClearTimer += dt;
            }
            else
            {
                _rollbackClearTimer = 0.0f;
            }

            // Forward Driving Confirmation:
            // If train is moving forward in commanded gear (> 0.15 m/s or speed > 3 km/h while cruising/accelerating),
            // rollback is impossible! Clear clamp immediately!
            bool isDrivingForwardInGear = (directedSpeedMs > 0.15f) || (CurrentSpeedKmh > 3.0f && (State == EngineState.Cruising || State == EngineState.Accelerating));
            if (isDrivingForwardInGear)
            {
                _isRollbackDetected = false;
            }
            else
            {
                // Detect unintended reverse rollback:
                if (!engineRunning)
                {
                    if (directedSpeedMs < -0.30f)
                    {
                        _isRollbackDetected = true;
                    }
                    else if (directedSpeedMs > -0.10f || _rollbackClearTimer > 0.4f)
                    {
                        _isRollbackDetected = false;
                    }
                }
                else
                {
                    // For running engines: only genuine reverse roll (> 0.60 m/s backwards) trips runaway clamp
                    if (directedSpeedMs < -0.60f && (State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting))
                    {
                        _isRollbackDetected = true;
                    }
                    else if (directedSpeedMs > -0.10f || _rollbackClearTimer > 0.4f)
                    {
                        _isRollbackDetected = false;
                    }
                }
            }
            IsRollbackDetected = _isRollbackDetected;

            // DE2 & General Anti-Rollback Zero-Throttle Interlock:
            // If the train has sustained backward drift relative to reverser, NEVER apply throttle!
            // Applying forward power to reverse-spinning traction motors causes instant current surge and blows TM fuse.
            // On hill starts, provide slight headroom (-0.08 m/s vs -0.04 m/s) to tolerate coupler buffer spring compression.
            float rollbackZeroThrottleThreshold = IsHillStarting ? -0.08f : -0.04f;
            if (directedSpeedMs < rollbackZeroThrottleThreshold && (State == EngineState.Accelerating || State == EngineState.Starting))
            {
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _commandedIndependentBrake = 1.0f;
                _commandedTrainBrake = 1.0f; // Firmly clamp train air brake across all cars!
                _currentIndependentBrake = 1.0f;
                _currentTrainBrake = 1.0f;
            }

            if (_isRollbackDetected)
            {
                // Immediately clamp the train with service + independent brakes to arrest runaway descent
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _commandedTrainBrake = 1.0f;
                _commandedIndependentBrake = 1.0f;
                _currentIndependentBrake = 1.0f;
                _currentTrainBrake = 1.0f;
            }

            // 4. Stalled Engine Grade-Hold Protection
            // When engine stalls on a hill, do NOT release brakes while attempting restart!
            if (!engineRunning && (State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting))
            {
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _commandedIndependentBrake = Mathf.Max(_commandedIndependentBrake, 0.8f);
                _commandedTrainBrake = Mathf.Max(_commandedTrainBrake, 0.5f);
            }

            // 5. Automatic TM Fuse Recovery:
            // If stationary and throttle is at idle, automatically reset any tripped fuses
            if (Mathf.Abs(forwardSpeedMs) < 0.05f && _commandedThrottle <= 0.01f &&
                _trainCar != null && _trainCar.SimController != null && _trainCar.SimController.SimulationFlow != null)
            {
                var fuses = _trainCar.SimController.SimulationFlow.AllFuses;
                if (fuses != null)
                {
                    for (int f = 0; f < fuses.Count; f++)
                    {
                        var fuse = fuses[f];
                        if (fuse != null && !fuse.State)
                        {
                            fuse.ChangeState(true);
                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            {
                                Main.ModEntry.Logger.Log(string.Format("[AITraffic] Reset tripped fuse '{0}' on '{1}'.",
                                    fuse.id ?? "TM Fuse", _trainCar.ID));
                            }
                        }
                    }
                }
            }
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

            // 1. Incline and Locomotive Dynamics:
            float activeRev = (_commandedReverser != 0.0f) ? _commandedReverser : ((_desiredReverser != 0.0f) ? _desiredReverser : TargetDirection);
            if (activeRev == 0.0f) activeRev = 1.0f;

            float travelPitch = 0.0f;
            if (_trainCar != null)
            {
                // Positive pitch along travel direction = uphill climb
                travelPitch = _trainCar.transform.forward.y * activeRev;
            }

            bool isLocoDM3 = IsLocoDM3;
            bool isUphill = travelPitch > 0.0015f; // > 0.15% grade (sensitive uphill detection)
            bool isSluggish = CurrentAccelerationMs2 < 0.08f && speedDeltaKmh > 3.0f;

            string liveryId = _trainCar != null && _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
            bool isLocoDE2 = _trainCar != null && (_trainCar.carType == TrainCarType.LocoShunter ||
                             liveryId.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             liveryId.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0);

            // 2. Base speed-based current/overload ceiling
            float baseCeiling = 0.28f;
            if (CurrentSpeedKmh < 6.0f)
            {
                if (isUphill)
                {
                    // Uphill launch needs substantial tractive effort to overcome gravity
                    if (isLocoDM3)
                    {
                        // DM3 mechanical diesel requires high engine RPM (0.90 - 1.00 throttle) to build
                        // sufficient hydraulic torque across the fluid coupling on an uphill gradient
                        baseCeiling = (travelPitch > 0.010f) ? 1.00f : 0.90f;
                    }
                    else if (isLocoDE2)
                    {
                        baseCeiling = 0.40f;
                    }
                    else
                    {
                        baseCeiling = (travelPitch > 0.015f) ? 0.85f : 0.70f;
                    }
                }
                else
                {
                    // Level / gentle track: DM3 still needs enough power to overcome transmission inertia
                    baseCeiling = isLocoDM3 ? 0.60f : 0.28f;
                }
            }
            else if (CurrentSpeedKmh < 15.0f)
            {
                float lowSpeedFloor = isUphill ? (isLocoDM3 ? 0.90f : 0.65f) : (isLocoDM3 ? 0.65f : 0.28f);
                baseCeiling = Mathf.Lerp(lowSpeedFloor, isLocoDM3 ? 1.00f : 0.75f, (CurrentSpeedKmh - 6.0f) / 9.0f);
            }
            else if (CurrentSpeedKmh < 25.0f)
            {
                baseCeiling = Mathf.Lerp(isLocoDM3 ? 0.90f : 0.75f, 1.00f, (CurrentSpeedKmh - 15.0f) / 10.0f);
            }
            else
            {
                baseCeiling = 1.00f;
            }

            // 3. Dynamic Hill-Start & Heavy-Load Acceleration Assist:
            if (CurrentSpeedKmh < 20.0f && (isUphill || isSluggish))
            {
                if (CurrentAccelerationMs2 < 0.10f)
                {
                    // Acceleration is super slow on hill/heavy load -> progressively ramp up hill boost ceiling
                    float boostRate = (isUphill && travelPitch > 0.015f) ? 0.16f : 0.10f;
                    float maxBoost = isLocoDM3 ? 0.85f : 0.60f;
                    _hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, maxBoost, boostRate * dt);
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

            // 4. Acceleration Feedback: only ramp up if there isn't enough acceleration felt
            const float minDesiredAcc = 0.12f; // m/s^2 (~0.43 km/h/s)
            const float maxAllowedAcc = 0.30f; // m/s^2 (~1.08 km/h/s)

            if (_rampThrottle < 0.15f && CurrentSpeedKmh < 1.0f)
            {
                if (isLocoDM3)
                {
                    // DM3: instantly spool up to 0.70f (or 0.80f on steep grade) to pre-charge fluid coupling
                    _rampThrottle = isUphill ? (travelPitch > 0.010f ? 0.80f : 0.70f) : 0.35f;
                }
                else
                {
                    _rampThrottle = isUphill ? (isLocoDE2 ? 0.22f : 0.35f) : 0.12f; // Initial launch notch
                }
            }

            if (CurrentAccelerationMs2 < minDesiredAcc)
            {
                // Not accelerating enough -> notch up throttle
                float notchRate = (CurrentSpeedKmh < 6.0f) ? 0.050f : 0.070f;
                if ((isUphill || isSluggish) && CurrentAccelerationMs2 < 0.05f)
                {
                    notchRate = isLocoDM3 ? 0.22f : 0.12f; // DM3 notches up rapidly to build pre-charge holding torque
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

            float tractionAuthority = GetTractionAuthorityMultiplier();
            if (_rampThrottle > maxThrottleCeiling * tractionAuthority)
            {
                _rampThrottle = Mathf.MoveTowards(_rampThrottle, maxThrottleCeiling * tractionAuthority, 2.0f * dt);
            }
            return _rampThrottle;
        }

        private float ComputeCruisingThrottle(float dt)
        {
            float speedDeltaKmh = TargetSpeedKmh - CurrentSpeedKmh;

            float pidCruise = ThrottlePID.Update(TargetSpeedKmh, CurrentSpeedKmh, dt);
            _rampThrottle = Mathf.MoveTowards(_rampThrottle, pidCruise, 0.06f * dt);
            return _rampThrottle * GetTractionAuthorityMultiplier();
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
            bool isOnFinalTrack = (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0 && CurrentPathTrackIndex >= CurrentPath.Tracks.Count - 1);
            
            // Also check if current physical bogie track matches destination track
            RailTrack curPhysTrack = null;
            if (_trainCar != null)
            {
                if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null) curPhysTrack = _trainCar.FrontBogie.track;
                else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null) curPhysTrack = _trainCar.RearBogie.track;
            }
            if (!isOnFinalTrack && curPhysTrack != null && CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0)
            {
                var destPathTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
                if (curPhysTrack == destPathTrack || (!string.IsNullOrEmpty(DestinationTrackName) && curPhysTrack.name == DestinationTrackName))
                {
                    isOnFinalTrack = true;
                }
            }

            // Strict Final Parking & Destination Arrival Validation:
            // A train must genuinely enter final parking before transitioning to TerminusStop / StationHold.
            // It must not enter terminus stop prematurely while still moving, braking, or stopped at an entrance signal/throat switch!
            bool isAtFinalDestination = false;
            if ((IsStationDestination || IsTerminusDestination) && CurrentSpeedKmh < 0.5f && StationaryTimer >= 2.0f)
            {
                if (DistanceToDestination <= 12.0f)
                {
                    isAtFinalDestination = true;
                }
                else if (isOnFinalTrack)
                {
                    bool isBlockedByCarsAhead = (DistanceToObstacle < 35.0f);
                    bool isNearBufferStop = (DistanceToDestination <= 35.0f);

                    // Check if rear car has cleared into destination track
                    bool isConsistInsideTrack = false;
                    TrainCar rearCar = (_trainCar != null && _trainCar.trainset != null && _trainCar.trainset.cars != null && _trainCar.trainset.cars.Count > 0)
                        ? _trainCar.trainset.cars[_trainCar.trainset.cars.Count - 1]
                        : _trainCar;
                    if (rearCar != null)
                    {
                        RailTrack rearTrack = (rearCar.RearBogie != null) ? rearCar.RearBogie.track : ((rearCar.FrontBogie != null) ? rearCar.FrontBogie.track : null);
                        var destTrack = (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0) ? CurrentPath.Tracks[CurrentPath.Tracks.Count - 1] : null;
                        if (rearTrack == destTrack)
                        {
                            isConsistInsideTrack = true;
                        }
                    }

                    if (isBlockedByCarsAhead || isNearBufferStop || (isConsistInsideTrack && DistanceToDestination <= 60.0f))
                    {
                        isAtFinalDestination = true;
                    }
                }
            }

            if (isAtFinalDestination && State != EngineState.TerminusStop && State != EngineState.StationHold)
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
                    _rampThrottle = 0.0f;
                    _commandedReverser = _desiredReverser;

                    // 100% Rock-Solid Holding Brake on Slopes:
                    // Any track incline requires full train air brake across all cars to prevent consist creep.
                    float idlePitch = (_trainCar != null) ? _trainCar.transform.forward.y : 0.0f;
                    bool isOnSlope = Mathf.Abs(idlePitch) > 0.002f;
                    _commandedTrainBrake = isOnSlope ? 1.0f : 0.85f;
                    _commandedIndependentBrake = 1.0f;

                    if (TargetSpeedKmh > 1.0f && !_isEmergencyStop)
                    {
                        // Transitioning to Starting: DO NOT release train brake yet!
                        // Maintain full clamp until reverser is aligned and tractive effort builds in Accelerating.
                        State = EngineState.Starting;
                    }
                    break;

                case EngineState.Starting:
                    _commandedReverser = _desiredReverser;
                    _commandedIndependentBrake = 1.0f; // Hill-hold: keep locomotive clamped while reverser aligns
                    float startPitch = (_trainCar != null) ? _trainCar.transform.forward.y * _desiredReverser : 0.0f;
                    bool startOnSlope = Mathf.Abs(startPitch) > 0.002f || (_trainCar != null && Mathf.Abs(_trainCar.transform.forward.y) > 0.002f);
                    _commandedTrainBrake = startOnSlope ? 1.0f : 0.85f; // Hold full train brake to prevent any consist drift
                    _commandedDynamicBrake = 0.0f;
                    _rampThrottle = 0.0f;
                    ReleaseAllConsistHandbrakes();

                    if (Mathf.Abs(_currentReverser - _desiredReverser) < 0.1f)
                    {
                        State = EngineState.Accelerating;
                    }
                    break;

                case EngineState.Accelerating:
                    _commandedReverser = _desiredReverser;
                    _commandedDynamicBrake = 0.0f;

                    // Hill-hold assist & DM3 launch hold: hold brakes until tractive effort is established
                    float activeRev = (_commandedReverser != 0.0f) ? _commandedReverser : ((_desiredReverser != 0.0f) ? _desiredReverser : TargetDirection);
                    if (activeRev == 0.0f) activeRev = 1.0f;
                    float launchPitch = (_trainCar != null) ? _trainCar.transform.forward.y * activeRev : 0.0f;
                    float forwardSpd = (_trainCar != null) ? _trainCar.GetForwardSpeed() : 0.0f;
                    float dirSpeed = forwardSpd * activeRev;
                    bool isLocoDM3 = IsLocoDM3;
                    bool isDM3Launching = isLocoDM3 && (_dm3Controller == null || _dm3Controller.IsInNeutral || _dm3Controller.IsShifting || _dm3Controller.CurrentGearIndex <= 1);
                    bool isUphillLaunch = (launchPitch > 0.0015f);
                    bool isDownhillLaunch = (launchPitch < -0.003f);
                    bool needsHillHold = (isUphillLaunch || isDM3Launching) && CurrentSpeedKmh < 5.0f;
                    IsHillStarting = needsHillHold && (dirSpeed < 0.20f);

                    if (dirSpeed < -0.08f)
                    {
                        // Train is actively drifting backward down the gradient:
                        // 1. Cut throttle to 0 to prevent blowing TM fuse or shocking drivetrain
                        _commandedThrottle = 0.0f;
                        _rampThrottle = 0.0f;
                        // 2. Firmly clamp BOTH independent and train air brakes to 100% across all cars!
                        _commandedIndependentBrake = 1.0f;
                        _commandedTrainBrake = 1.0f;
                        _currentIndependentBrake = 1.0f; // Instant snap
                        _currentTrainBrake = 1.0f;
                        // 3. Set rollback arrest hold timer: must remain stationary for 1.5s to settle slack before relaunch
                        _hillRollbackHoldTimer = 1.5f;
                        // 4. Boost starting power ceiling for the next attempt
                        _hillAssistBoost = Mathf.Min(isLocoDM3 ? 0.85f : 0.65f, _hillAssistBoost + 0.20f);
                    }
                    else if (_hillRollbackHoldTimer > 0.0f)
                    {
                        // Waiting in stationary arrest hold for slack and brake pipe to stabilize
                        _hillRollbackHoldTimer -= dt;
                        _commandedThrottle = 0.0f;
                        _rampThrottle = 0.0f;
                        _commandedIndependentBrake = 1.0f;
                        _commandedTrainBrake = 1.0f;
                    }
                    else if (needsHillHold)
                    {
                        // Active Hill-Start Sequence:
                        // Apply sander on steep slopes, DM3 launches, or whenever genuine traction slip is present
                        if (_isWheelSlipping || launchPitch > 0.015f || isLocoDM3)
                        {
                            _sanderRequested = true;
                        }

                        if (_dm3Controller != null && _dm3Controller.IsDM3 && (_dm3Controller.IsInNeutral || _dm3Controller.IsShifting))
                        {
                            _commandedIndependentBrake = 1.0f;
                            _commandedTrainBrake = 0.85f;
                            _commandedThrottle = 0.0f;
                            _rampThrottle = 0.0f;
                        }
                        else
                        {
                            // Compute accelerating throttle with hill assist
                            _commandedThrottle = ComputeAcceleratingThrottle(dt);

                            string lId = _trainCar != null && _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
                            bool isLocoDE2 = _trainCar != null && (_trainCar.carType == TrainCarType.LocoShunter ||
                                             lId.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                             lId.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0);

                            float throttlePrechargeThreshold;
                            float throttleFullReleaseThreshold;

                            if (isLocoDM3)
                            {
                                // DM3 mechanical shunter: must spool up to high RPM (>= 0.60) before train air releases,
                                // and hold independent brake until full tractive torque (>= 0.85) is established
                                throttlePrechargeThreshold = Mathf.Clamp(0.60f + launchPitch * 10.0f, 0.60f, 0.85f);
                                throttleFullReleaseThreshold = 0.88f;
                            }
                            else if (isLocoDE2)
                            {
                                throttlePrechargeThreshold = Mathf.Clamp(0.30f + launchPitch * 10.0f, 0.30f, 0.50f);
                                throttleFullReleaseThreshold = Mathf.Min(0.65f, throttlePrechargeThreshold + 0.20f);
                            }
                            else
                            {
                                // DH4, DE6, Steam: scale precharge threshold dynamically with grade
                                throttlePrechargeThreshold = Mathf.Clamp(0.40f + launchPitch * 12.0f, 0.40f, 0.65f);
                                throttleFullReleaseThreshold = Mathf.Min(0.90f, throttlePrechargeThreshold + 0.22f);
                            }

                            // Evaluate against ACTUAL physical locomotive throttle lever (_currentThrottle),
                            // ensuring brakes never begin releasing while the throttle is still ramping up through idle!
                            float actualThrottle = _currentThrottle;

                            // Positive forward motion established: only conclude hill-hold once genuine momentum exists
                            if ((dirSpeed >= 0.15f || CurrentSpeedKmh >= 0.8f) && actualThrottle >= (throttlePrechargeThreshold * 0.75f))
                            {
                                _commandedIndependentBrake = 0.0f;
                                _commandedTrainBrake = 0.0f;
                            }
                            else if (actualThrottle < throttlePrechargeThreshold)
                            {
                                // Stage 1: Precharge clamp. Throttle spools up against 100% locked brakes!
                                _commandedIndependentBrake = 1.0f;
                                _commandedTrainBrake = 1.0f;
                            }
                            else
                            {
                                // Stage 2: Throttle has reached precharge threshold!
                                // Release train air brake across all cars, while locomotive independent brake remains FIRMLY CLAMPED at 1.0f!
                                float progress = Mathf.Clamp01((actualThrottle - throttlePrechargeThreshold) / Mathf.Max(0.05f, throttleFullReleaseThreshold - throttlePrechargeThreshold));
                                _commandedTrainBrake = Mathf.Clamp01(1.0f - progress * 2.0f);

                                // Stage 3: Independent Brake Handover.
                                // Locomotive independent brake ONLY begins releasing after train brake has mostly vented (<= 0.25f)
                                // AND throttle has advanced past the handover point.
                                if (_currentTrainBrake <= 0.25f && progress > 0.30f)
                                {
                                    float indReleaseProgress = Mathf.Clamp01((progress - 0.30f) / 0.70f);
                                    _commandedIndependentBrake = Mathf.Clamp01(1.0f - indReleaseProgress);
                                }
                                else
                                {
                                    _commandedIndependentBrake = 1.0f; // Lock locomotive in place while consist air empties
                                }
                            }
                        }
                    }
                    else if (isDownhillLaunch && CurrentSpeedKmh < 3.0f)
                    {
                        // Controlled downhill release: release brakes progressively to prevent consist run-in shock
                        _commandedThrottle = ComputeAcceleratingThrottle(dt);
                        _commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0.0f, 0.35f * dt);
                        _commandedIndependentBrake = Mathf.MoveTowards(_commandedIndependentBrake, 0.0f, 0.50f * dt);
                    }
                    else
                    {
                        // Level normal launch
                        _commandedIndependentBrake = 0.0f;
                        _commandedTrainBrake = 0.0f;
                        _commandedThrottle = ComputeAcceleratingThrottle(dt);
                    }
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

                    // Dynamic stopping urgency calculation for Red Signals (Hp 0), Obstacles, Corridor Holds, and Buffer Stops
                    float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(EffectiveObstacleDistance, DistanceToDestination));
                    if (distToStop < 600.0f && TargetSpeedKmh <= 15.0f)
                    {
                        // Stop target buffer: 20m before signal mast / buffer stop
                        float effectiveDist = Mathf.Max(0.5f, distToStop - 20.0f);
                        float reqDecel = (CurrentSpeedMs * CurrentSpeedMs) / (2.0f * effectiveDist);

                        // Anticipatory pneumatic braking: prime train air line early so cylinders have pressure before reaching stop zone
                        if (reqDecel > 0.15f)
                        {
                            float stopRatio = Mathf.Clamp01((reqDecel - 0.15f) / 0.30f);
                            brakeOutput = Mathf.Max(brakeOutput, 0.45f + stopRatio * 0.55f);
                        }

                        // Safety stop boundaries:
                        if (distToStop <= 20.0f)
                        {
                            brakeOutput = 1.0f; // Absolute stop at 20m safety line
                        }
                        else if (distToStop < 35.0f && CurrentSpeedKmh > 6.0f)
                        {
                            brakeOutput = 1.0f;
                            _sanderRequested = true; // Deploy sand to prevent wheel slide on final stop
                        }
                        else if (distToStop < 60.0f && CurrentSpeedKmh > 15.0f)
                        {
                            brakeOutput = 1.0f;
                            _sanderRequested = true;
                        }
                        else if (distToStop < 120.0f && CurrentSpeedKmh > 30.0f)
                        {
                            brakeOutput = 0.85f;
                        }

                        // Maintain solid holding brake while stopped at signal/target
                        if (CurrentSpeedKmh < 0.4f)
                        {
                            brakeOutput = Mathf.Max(0.60f, brakeOutput);
                        }
                    }

                    if (CurrentSpeedProfile.LimitingReason == SpeedLimitReason.CurvatureRadius && speedDeltaKmh < -2.0f)
                    {
                        float curveUrgency = Mathf.Clamp01((-speedDeltaKmh - 2.0f) / 10.0f);
                        brakeOutput = Mathf.Max(brakeOutput, 0.40f + curveUrgency * 0.60f);
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
            float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(EffectiveObstacleDistance, DistanceToDestination));
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

            // Imminent physical obstacle / corridor hold emergency clamping
            // Only force emergency application if critically close or carrying dangerous excess speed
            float effObstDist = EffectiveObstacleDistance;
            if (effObstDist < 30.0f || (effObstDist < 80.0f && CurrentSpeedKmh > 25.0f))
            {
                _commandedTrainBrake = 1.0f;
                _commandedIndependentBrake = 1.0f;
                if (_hasDynamicBrake) _commandedDynamicBrake = 1.0f;
            }

            // Absolute holding brake when stopped at destination or behind obstacle / corridor hold
            if (CurrentSpeedKmh < 0.5f && (TargetSpeedKmh <= 0.1f || effObstDist <= 45.0f))
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

        /// <summary>
        /// Emergency killswitch invoked upon derailment or collision.
        /// Immediately halts train, applies all brakes, shuts down engine, and releases all reservations.
        /// </summary>
        private void EmergencyCrashShutdown()
        {
            _commandedThrottle = 0.0f;
            _rampThrottle = 0.0f;
            _currentThrottle = 0.0f;
            _commandedDynamicBrake = 0.0f;
            _currentDynamicBrake = 0.0f;
            _commandedTrainBrake = 1.0f;
            _currentTrainBrake = 1.0f;
            _commandedIndependentBrake = 1.0f;
            _currentIndependentBrake = 1.0f;
            _desiredReverser = 0.0f;
            _commandedReverser = 0.0f;
            _currentReverser = 0.0f;

            if (_controlsOverrider != null)
            {
                if (_controlsOverrider.Throttle != null) _controlsOverrider.Throttle.Set(0.0f);
                if (_controlsOverrider.Brake != null) _controlsOverrider.Brake.Set(1.0f);
                if (_controlsOverrider.IndependentBrake != null) _controlsOverrider.IndependentBrake.Set(1.0f);
                if (_controlsOverrider.Handbrake != null) _controlsOverrider.Handbrake.Set(1.0f);
                if (_controlsOverrider.Reverser != null) _controlsOverrider.Reverser.Set(0.0f);
            }

            EnterTerminusStop();
        }

        private void EnterTerminusStop()
        {
            State = EngineState.TerminusStop;
            TerminusArrivalTime = Time.time;
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

            // 0. Safety Interlocks: Rollback clamp & Overheat throttle cut
            if (_isRollbackDetected)
            {
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _currentThrottle = 0.0f;
                _commandedIndependentBrake = 1.0f;
                _commandedTrainBrake = 1.0f;
                _currentIndependentBrake = 1.0f;
                _currentTrainBrake = 1.0f;
            }
            else if (_isOverheated)
            {
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
            }

            // 1. Throttle Rate-Limiting (slew rate)
            float targetThrottle = _commandedThrottle;
            if (_dm3Controller != null && _dm3Controller.IsDM3 && _dm3Controller.IsShifting)
            {
                targetThrottle = 0.0f; // Cut fuel/throttle to disengage load during mechanical gear shift
                _commandedThrottle = 0.0f;
                _rampThrottle = 0.0f;
                _currentThrottle = 0.0f; // Instantly cut current throttle to unload gearbox
            }

            float effectiveThrottleSlew = (IsHillStarting && targetThrottle > _currentThrottle) ? Mathf.Max(ThrottleSlewRate, 0.16f) : ThrottleSlewRate;
            _currentThrottle = Mathf.MoveTowards(_currentThrottle, targetThrottle, effectiveThrottleSlew * dt);
            if (_controlsOverrider.Throttle != null)
            {
                _controlsOverrider.Throttle.Set(_currentThrottle);
            }

            // 2. Train Air Brake Rate-Limiting
            // Fast application (3.5/s) when increasing brake, gentle smooth release (0.45/s) to conserve reservoir air
            float brakeSlew = (_commandedTrainBrake > _currentTrainBrake) ? 3.5f : 0.45f;
            float distToStop = Mathf.Min(DistanceToSignal, Mathf.Min(EffectiveObstacleDistance, DistanceToDestination));

            // Instant full application for emergency, stationary holding, or rollback arrest:
            if (_commandedTrainBrake >= 0.85f &&
                (distToStop < 160.0f || EffectiveObstacleDistance < 160.0f ||
                 _isRollbackDetected || _hillRollbackHoldTimer > 0.0f ||
                 State == EngineState.Idle || State == EngineState.Starting || State == EngineState.StationHold || State == EngineState.TerminusStop))
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
            // Fast application (4.0/s) to clamp loco quickly; smooth release (1.0/s)
            float indBrakeSlew = (_commandedIndependentBrake > _currentIndependentBrake) ? 4.0f : 1.0f;
            if (_commandedIndependentBrake >= 0.90f && 
                (CurrentSpeedMs < 0.15f || _isRollbackDetected || _hillRollbackHoldTimer > 0.0f || 
                 State == EngineState.Idle || State == EngineState.Starting || State == EngineState.StationHold || State == EngineState.TerminusStop))
            {
                _currentIndependentBrake = _commandedIndependentBrake; // Instant snap clamp
            }
            else
            {
                _currentIndependentBrake = Mathf.MoveTowards(_currentIndependentBrake, _commandedIndependentBrake, indBrakeSlew * dt);
            }
            if (_controlsOverrider.IndependentBrake != null)
            {
                _controlsOverrider.IndependentBrake.Set(_currentIndependentBrake);
            }

            // 5. Reverser (Safety interlock: only switch reverser when nearly stationary)
            if (!IsWorkerDriven && _ambientLockedReverser != 0.0f)
            {
                _commandedReverser = _ambientLockedReverser;
            }

            if (CurrentSpeedMs < 0.15f)
            {
                _currentReverser = _commandedReverser;
                if (_controlsOverrider.Reverser != null)
                {
                    _controlsOverrider.Reverser.Set(_currentReverser);
                }
            }

            // 6. Continuous Handbrake Release while in active driving service
            if (_controlsOverrider.Handbrake != null && State != EngineState.TerminusStop)
            {
                _controlsOverrider.Handbrake.Set(0.0f);
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

            if (_trainCar.brakeSystem != null)
            {
                _trainCar.brakeSystem.SetHandbrakePosition(1.0f, true);
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
                    if (car != null && car != _trainCar && car.brakeSystem != null)
                    {
                        car.brakeSystem.SetHandbrakePosition(1.0f, true);
                    }
                }
            }
        }

        /// <summary>
        /// Ensures all handbrakes across the locomotive, helper locomotives, and all cars in the consist are released.
        /// </summary>
        public void ReleaseAllConsistHandbrakes()
        {
            if (_controlsOverrider != null && _controlsOverrider.Handbrake != null)
            {
                _controlsOverrider.Handbrake.Set(0.0f);
            }
            if (_trainCar != null && _trainCar.brakeSystem != null)
            {
                _trainCar.brakeSystem.SetHandbrakePosition(0.0f, true);
            }
            if (_trainCar != null && _trainCar.trainset != null && _trainCar.trainset.cars != null)
            {
                for (int i = 0; i < _trainCar.trainset.cars.Count; i++)
                {
                    var car = _trainCar.trainset.cars[i];
                    if (car == null) continue;
                    if (car.brakeSystem != null)
                    {
                        car.brakeSystem.SetHandbrakePosition(0.0f, true);
                    }
                    if (car.SimController != null && car.SimController.controlsOverrider != null)
                    {
                        if (car.SimController.controlsOverrider.Handbrake != null)
                        {
                            car.SimController.controlsOverrider.Handbrake.Set(0.0f);
                        }
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
            return AITraffic.Navigation.SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out requiredBranch);
        }

        #endregion
    }
}
