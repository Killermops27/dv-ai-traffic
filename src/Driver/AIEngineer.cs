using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using AITraffic.Compat;
using AITraffic.Config;
using AITraffic.Core;
using AITraffic.Fleet;
using AITraffic.Navigation;
using AITraffic.Workers;
using DV.CabControls;
using DV.Simulation.Cars;
using DV.Simulation.Controllers;
using DV.Simulation.Fuses;
using DV.ThingTypes;
using DV.Utils;
using DV.WeatherSystem;
using LocoSim.Definitions;
using LocoSim.Implementations;
using LocoSim.Implementations.Wheels;
using Signals.Common;
using Signals.Common.Aspects;
using Signals.Game;
using Signals.Game.Railway;
using UnityEngine;

namespace AITraffic.Driver
{
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

	// CRITICAL INVARIANT [RGR-06]: Absolute Prohibition of traversing occupied tracks in train routing
	public class AIEngineer : MonoBehaviour
	{
		public enum HillLaunchStage
		{
			None,
			Stage1_Clean,
			Stage2_Sanded,
			RollbackDescent,
			MomentumRunUp,
			PermanentlyStalled
		}

		public enum SingleTrackReverseStage
		{
			None,
			HaltAndYield,
			SoundingHorn,
			SettingLights,
			ReversingToSiding,
			DuckingIntoSiding,
			SecuredInSiding
		}

		public enum OffRouteReverseStage
		{
			None,
			Halting,
			ReversingBehindSwitch,
			RealigningAndResuming
		}

		private const float RerouteConsumedTtlSeconds = 10f;

		[SerializeField]
		private TrainCar _trainCar;

		private string _cachedLocoId;

		[SerializeField]
		private EngineState _state;

		private readonly List<RailTrack> _upcomingTracks = new List<RailTrack>();

		private readonly List<Vector3> _levelCrossings = new List<Vector3>();

		private readonly List<SignalBlockInfo> _upcomingSignalBlocks = new List<SignalBlockInfo>();

		private readonly List<TrainCar> _registeredConsistCars = new List<TrainCar>();

		private DM3TransmissionController _dm3Controller;

		private BaseControlsOverrider _controlsOverrider;

		private bool _hasDynamicBrake;

		private float _commandedThrottle;

		private float _commandedTrainBrake;

		private float _commandedDynamicBrake;

		private float _commandedIndependentBrake;

		private float _commandedReverser;

		private float _desiredReverser = 1f;

		private float _ambientLockedReverser = 0f;

		private float _currentThrottle;

		private float _currentTrainBrake;

		private float _currentDynamicBrake;

		private float _currentIndependentBrake;

		private float _currentReverser;

		private float _brakeHoldTimer;

		private bool _isWheelSlipping;

		private bool _isWheelSliding;

		private bool _isLocoWheelSliding;

		private float _wspBrakeAuthority = 1f;

		private float _sanderActiveTimer;

		private float _sanderRestTimer;

		private bool _sanderRequested;

		private float _sandHoldTimer;

		private float _slipThrottleReduction;

		private ConsistType _consistType = ConsistType.RegionalFreight;

		private float _thermalThrottleLimit;

		private float _overcurrentThrottleLimit;

		private bool _isOverheated;

		private bool _isRollbackDetected;

		private bool _isHoldingForCorridor;

		private bool _isHoldingInMultiTrack;

		private string _corridorHoldReason;

		private float _corridorCheckCooldown;

		private float _corridorHoldDistance = float.PositiveInfinity;

		private float _switchHoldDistance = float.PositiveInfinity;

		private float _stoppedSwitchAlignCooldown = 0f;

		// CRITICAL INVARIANT [AIE-18]: Main Signal Yield Protocol State for Player Switch Contention
		private float _playerYieldWaitTimer = 0f;

		private Junction _yieldContendedSwitch = null;

		private Signals.Game.Signal _yieldGoverningSignal = null;

		private bool _portsInitialized;

		private readonly List<Port> _temperaturePorts = new List<Port>();

		private readonly List<Port> _amperagePorts = new List<Port>();

		private Port _portAmpsPerTM;

		private Port _portTotalAmps;

		private Port _portEngineOn;

		private Port _portEngineRpm;

		private float _rollbackClearTimer;

		private float _lastSpeedMs;

		private float _smoothAccMs2;

		private float _rampThrottle;

		private float _hillAssistBoost;

		private float _stallRestartCooldown;

		private float _hillStagnationTimer;

		private Vector3 _rollbackStartPosition;

		private bool _hasAttemptedMomentumRunUp;

		private float _stalledNotificationCooldown;

		private float _hillGroundProgressWindowTimer;

		private Vector3 _hillGroundWindowStartPos;

		private float _stage2ContinuousSandingTimer;

		private float _tmZeroCurrentTimer;

		private readonly HashSet<RailTrack> _activeCorridorTracks = new HashSet<RailTrack>();

		private SingleTrackReverseStage _reverseProtocolStage = SingleTrackReverseStage.None;

		private float _reverseProtocolTimer;

		private Junction _trailingSidingSwitch;

		private byte _trailingSidingBranch;

		private RailTrack _trailingSidingTrack;

		private OffRouteReverseStage _offRouteReverseStage = OffRouteReverseStage.None;

		private float _offRouteReverseTimer = 0f;

		private RailTrack _offRouteRecoveryTargetTrack = null;

		private Junction _offRouteDivergingSwitch = null;

		private byte _offRouteMainlineBranch = 0;

		private byte _trailingSidingMainlineBranch = 0;

		private float _corridorClearConfirmTimer = 0f;

		private bool _isWaitingOutsideStationThroat;

		private float _stationThroatPollTimer;

		private float _stationThroatToastCooldown;

		private static readonly Dictionary<Signals.Game.Signal, float> s_signalWakeupCooldowns = new Dictionary<Signals.Game.Signal, float>();

		private bool _isHornPatternActive;

		private float _hornStepTimer;

		private int _hornStepIndex;

		private bool _isStartingEngine;

		private bool _isEmergencyStop;

		private readonly List<SignalRegistry.UpcomingSignal> _upcomingSignals = new List<SignalRegistry.UpcomingSignal>();

		private readonly List<SignalRegistry.UpcomingSignal> _filteredSignalsCache = new List<SignalRegistry.UpcomingSignal>();

		private readonly List<RailTrack> _corridorTracksCache = new List<RailTrack>();

		private readonly HashSet<RailTrack> _reservedTracks = new HashSet<RailTrack>();

		private readonly HashSet<Signals.Game.Signal> _reservedDVSignals = new HashSet<Signals.Game.Signal>();

		private RailTrack _obstacleTrack;

		private float _obstacleRerouteCooldown;

		private float _signalRerouteCooldown;

		private float _signalWaitRetryTimer = 5f;

		private float _stoppedAtRedTimer;

		private float _stuckRecoveryCooldown = 10f;

		private bool _isShuntingMode;

		private bool _isSelfBlockedShunting;

		private Signals.Game.Signal _shuntingBypassedSignal;

		private Signals.Game.Signal _shuntingTargetSignal;

		private RailTrack _shuntingTargetTrack;

		private double _shuntingTargetSpan;

		private int _shuntingTargetTrackPathIndex = -1;

		private float _lastShuntingTargetDistance = float.PositiveInfinity;

		private bool _shuntingTargetWasClear;

		private readonly HashSet<Junction> _seenJunctionsThisPass = new HashSet<Junction>();

		private float _heavySensorUpdateCooldown = 0f;

		private float _pathUpdateCooldown = 0f;

		private readonly HashSet<RailTrack> _desiredReservationsScratch = new HashSet<RailTrack>();

		private readonly HashSet<Signals.Game.Signal> _desiredSignalReservationsScratch = new HashSet<Signals.Game.Signal>();

		private readonly List<Signals.Game.Signal> _sigsToReleaseScratch = new List<Signals.Game.Signal>();

		private readonly List<RailTrack> _tracksToReleaseScratch = new List<RailTrack>();

		private readonly List<Junction> _lockedJunctionsScratch = new List<Junction>();

		private readonly List<RailTrack> _candidateTracksScratch = new List<RailTrack>();

		private bool _isObstacleComingHeadOn;

		private bool _isObstacleSameDirection;

		private bool _hasPendingDivergingSwitchHold;

		private float _pendingDivergingSwitchDistance = float.PositiveInfinity;

		private float _obstacleWaitTimer;

		private Task<RailPath> _rerouteTask;

		private string _rerouteTaskKey;

		private bool _reroutePending;

		private readonly Dictionary<string, float> _consumedRerouteKeys = new Dictionary<string, float>();

		private float _speedProfileUpdateCooldown = 0f;

		private readonly List<bool> _amperagePortIsTotal = new List<bool>();

		private bool _cachedLocoIdentityInitialized;

		private bool _cachedIsDE2;

		private bool _cachedIsDE6;

		private bool _cachedIsBE2;

		private int _cachedNumMotors = 1;

		private float _breakerCheckCooldown = 0f;

		public TrainCar TrainCar
		{
			get
			{
				return _trainCar;
			}
		}

		public string LocoId
		{
			get
			{
				if (!string.IsNullOrEmpty(_cachedLocoId))
				{
					return _cachedLocoId;
				}
				if (_trainCar != null && _trainCar.logicCar != null)
				{
					string carId = ModCompatManager.GetCarId(_trainCar);
					if (!string.IsNullOrEmpty(carId))
					{
						_cachedLocoId = carId;
						return _cachedLocoId;
					}
				}
				return "Loco";
			}
		}

		public EngineState State
		{
			get
			{
				return _state;
			}
			private set
			{
				if (_state != value)
				{
					EngineState state = _state;
					_state = value;
					if (this.OnStateChanged != null)
					{
						this.OnStateChanged(state, _state);
					}
				}
			}
		}

		public float CurrentSpeedKmh { get; private set; }

		public float CurrentSpeedMs { get; private set; }

		public float TargetSpeedKmh { get; private set; }

		public float TargetSpeedMs { get; private set; }

		public float TargetDirection { get; set; }

		public PIDController ThrottlePID { get; private set; }

		public PIDController BrakePID { get; private set; }

		public SpeedProfileGenerator SpeedProfiler { get; private set; }

		public List<RailTrack> UpcomingTracks
		{
			get
			{
				return _upcomingTracks;
			}
		}

		public List<Vector3> LevelCrossings
		{
			get
			{
				return _levelCrossings;
			}
		}

		public Signals.Game.Signal ApproachingSignal { get; set; }

		public float DistanceToSignal { get; set; }

		public Signals.Game.Signal GoverningSignal { get; set; }

		public float DistanceToGoverningSignal { get; set; }

		public float DistanceToRedSignal
		{
			get
			{
				if (ApproachingSignal != null && DistanceToSignal < 2000f)
				{
					Signals.Game.Signal signal = ((ApproachingSignal.Parent != null) ? ApproachingSignal.Parent : ApproachingSignal);
					Signals.Game.Signal signal2 = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
					if ((!_isShuntingMode || (signal != signal2 && ApproachingSignal != signal2)) && signal != null && signal.IsOn && signal.CurrentAspect != null)
					{
						AspectBaseDefinition definition = signal.CurrentAspect.GetDefinition();
						if (signal.CurrentAspect.DisallowPassing || (definition != null && definition.DisallowPassing))
						{
							return DistanceToSignal;
						}
					}
				}
				if (_upcomingSignals != null && _upcomingSignals.Count > 0)
				{
					for (int i = 0; i < _upcomingSignals.Count; i++)
					{
						SignalRegistry.UpcomingSignal upcomingSignal = _upcomingSignals[i];
						if (upcomingSignal.Signal == null)
						{
							continue;
						}
						Signals.Game.Signal signal3 = ((upcomingSignal.Signal.Parent != null) ? upcomingSignal.Signal.Parent : upcomingSignal.Signal);
						Signals.Game.Signal signal2 = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
						if ((!_isShuntingMode || (signal3 != signal2 && upcomingSignal.Signal != signal2)) && signal3 != null && signal3.IsOn && signal3.CurrentAspect != null)
						{
							AspectBaseDefinition definition = signal3.CurrentAspect.GetDefinition();
							if (signal3.CurrentAspect.DisallowPassing || (definition != null && definition.DisallowPassing))
							{
								return upcomingSignal.Distance;
							}
						}
					}
				}
				return float.PositiveInfinity;
			}
		}

		public float DistanceToObstacle { get; set; }

		public float EffectiveObstacleDistance
		{
			get
			{
				return Mathf.Min(DistanceToObstacle, Mathf.Min(_corridorHoldDistance, _switchHoldDistance));
			}
		}

		public float DistanceToDestination { get; set; }

		public bool IsStationDestination { get; set; }

		public bool IsTerminusDestination { get; set; }

		public float TerminusArrivalTime { get; private set; }

		public bool HasArrivedAtDestination { get; private set; }

		public bool IsShuntingMode
		{
			get
			{
				return _isShuntingMode;
			}
		}

		public List<SignalBlockInfo> UpcomingSignalBlocks
		{
			get
			{
				return _upcomingSignalBlocks;
			}
		}

		public List<TrainCar> RegisteredConsistCars
		{
			get
			{
				return _registeredConsistCars;
			}
		}

		public float ConsistLengthMeters
		{
			get
			{
				List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
				if (list == null || list.Count == 0)
				{
					return 20f;
				}
				float num = 0f;
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar = list[i];
					if (!(trainCar == null))
					{
						num += ((trainCar.InterCouplerDistance > 0f) ? trainCar.InterCouplerDistance : 15f);
					}
				}
				return num;
			}
		}

		public int ConsistCarCount
		{
			get
			{
				List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
				return (list == null || list.Count <= 0) ? 1 : list.Count;
			}
		}

		public float ConsistTotalMassTons
		{
			get
			{
				List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
				if (list == null || list.Count == 0)
				{
					if (_trainCar != null && _trainCar.massController != null && _trainCar.massController.TotalMass > 0f)
					{
						return _trainCar.massController.TotalMass / 1000f;
					}
					return 80f;
				}
				float num = 0f;
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar = list[i];
					if (!(trainCar == null))
					{
						float num2 = ((trainCar.massController != null && trainCar.massController.TotalMass > 0f) ? trainCar.massController.TotalMass : 45000f);
						num += num2;
					}
				}
				return num / 1000f;
			}
		}

		public bool HasConsistDerailed { get; private set; }

		public string OriginStationName { get; set; }

		public string DestinationStationName { get; set; }

		public string DestinationTrackName { get; set; }

		public RailPath CurrentPath { get; set; }

		public int CurrentPathTrackIndex { get; private set; }

		public float StationDwellDuration { get; set; }

		public float DwellTimeRemaining { get; private set; }

		public float SpeedToleranceKmh { get; set; }

		public float ThrottleSlewRate { get; set; }

		public float BrakeSlewRate { get; set; }

		public float DynamicBrakeSlewRate { get; set; }

		public float CommandedThrottle
		{
			get
			{
				return _commandedThrottle;
			}
		}

		public float CommandedTrainBrake
		{
			get
			{
				return _commandedTrainBrake;
			}
		}

		public float CommandedDynamicBrake
		{
			get
			{
				return _commandedDynamicBrake;
			}
		}

		public float CommandedIndependentBrake
		{
			get
			{
				return _commandedIndependentBrake;
			}
		}

		public float CommandedReverser
		{
			get
			{
				return _commandedReverser;
			}
		}

		public float DesiredReverser
		{
			get
			{
				return _desiredReverser;
			}
		}

		public float CurrentThrottle
		{
			get
			{
				return _currentThrottle;
			}
		}

		public float CurrentTrainBrake
		{
			get
			{
				return _currentTrainBrake;
			}
		}

		public float CurrentDynamicBrake
		{
			get
			{
				return _currentDynamicBrake;
			}
		}

		public float CurrentIndependentBrake
		{
			get
			{
				return _currentIndependentBrake;
			}
		}

		public float CurrentReverser
		{
			get
			{
				return _currentReverser;
			}
		}

		public RailTrack CurrentTrack
		{
			get
			{
				if (_trainCar != null)
				{
					if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
					{
						return _trainCar.FrontBogie.track;
					}
					if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
					{
						return _trainCar.RearBogie.track;
					}
				}
				return null;
			}
		}

		public DM3TransmissionController DM3Controller
		{
			get
			{
				return _dm3Controller;
			}
		}

		public SpeedProfileResult CurrentSpeedProfile { get; private set; }

		public float SpawnTime { get; private set; }

		public float StationaryTimer { get; private set; }

		public bool IsWorkerDriven { get; set; }

		public bool IsEncounterTrain { get; set; }

		public bool IsWheelSlipping
		{
			get
			{
				return _isWheelSlipping;
			}
		}

		public bool IsWheelSliding
		{
			get
			{
				return _isWheelSliding;
			}
		}

		public bool IsLocoWheelSliding
		{
			get
			{
				return _isLocoWheelSliding;
			}
		}

		public float SlipThrottleReduction
		{
			get
			{
				return _slipThrottleReduction;
			}
		}

		public float WspBrakeAuthority
		{
			get
			{
				return _wspBrakeAuthority;
			}
		}

		public HillLaunchStage HillStage { get; private set; }

		public bool IsPermanentlyStalled { get; private set; }

		public float HillEquilibriumThrottle { get; private set; }

		public float StalledTime { get; private set; }

		public bool IsLocoDE2
		{
			get
			{
				if (_trainCar == null)
				{
					return false;
				}
				if (_trainCar.carType == TrainCarType.LocoShunter)
				{
					return true;
				}
				string text = ((_trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
				return text.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}

		public bool IsLocoDE6
		{
			get
			{
				if (_trainCar == null)
				{
					return false;
				}
				if (_trainCar.carType == TrainCarType.LocoDiesel)
				{
					return true;
				}
				string text = ((_trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
				return text.IndexOf("DE6", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Diesel", StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}

		public bool IsLocoDH4
		{
			get
			{
				if (_trainCar == null)
				{
					return false;
				}
				if (_trainCar.carType == TrainCarType.LocoDH4)
				{
					return true;
				}
				string text = ((_trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
				return text.IndexOf("DH4", StringComparison.OrdinalIgnoreCase) >= 0;
			}
		}

		public float MaxRatedTractiveEffortNewtons
		{
			get
			{
				if (IsLocoDE6)
				{
					return 420000f;
				}
				if (IsLocoDH4)
				{
					return 220000f;
				}
				if (IsLocoDM3)
				{
					return 160000f;
				}
				if (IsLocoDE2)
				{
					return 120000f;
				}
				return 100000f;
			}
		}

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
				if (_dm3Controller != null && _dm3Controller.IsDM3)
				{
					return true;
				}
				if (_trainCar != null)
				{
					if (_trainCar.carType == TrainCarType.LocoDM3)
					{
						return true;
					}
					string text = ((_trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
					if (text.IndexOf("DM3", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return true;
					}
				}
				return false;
			}
		}

		public bool IsHoldingForCorridor
		{
			get
			{
				return _isHoldingForCorridor;
			}
		}

		public string CorridorHoldReason
		{
			get
			{
				return _corridorHoldReason;
			}
		}

		public ConsistType ConsistType
		{
			get
			{
				return _consistType;
			}
			set
			{
				_consistType = value;
			}
		}

		public float CurrentAccelerationMs2 { get; private set; }

		public bool HasAttemptedMomentumRunUp
		{
			get
			{
				return _hasAttemptedMomentumRunUp;
			}
		}

		public bool IsIntentionalReverseActive
		{
			get
			{
				return _reverseProtocolStage == SingleTrackReverseStage.ReversingToSiding || _reverseProtocolStage == SingleTrackReverseStage.DuckingIntoSiding || _offRouteReverseStage == OffRouteReverseStage.ReversingBehindSwitch;
			}
		}

		public float DistToCorridorStart { get; private set; }

		public event Action<EngineState, EngineState> OnStateChanged;

		public event Action<AIEngineer> OnTerminusArrival;

		public void RegisterConsistCars(List<TrainCar> cars)
		{
			_registeredConsistCars.Clear();
			if (cars == null)
			{
				return;
			}
			for (int i = 0; i < cars.Count; i++)
			{
				TrainCar trainCar = cars[i];
				if (trainCar != null && !_registeredConsistCars.Contains(trainCar))
				{
					_registeredConsistCars.Add(trainCar);
				}
			}
		}

		public bool HoldsSignalReservation(Signals.Game.Signal signal)
		{
			if (signal == null)
			{
				return false;
			}
			return _reservedDVSignals.Contains(signal) || (signal.Parent != null && _reservedDVSignals.Contains(signal.Parent));
		}

		public bool HasActiveSignalReservation()
		{
			return _reservedDVSignals != null && _reservedDVSignals.Count > 0;
		}

		public float CalculateStoppingDistance()
		{
			float currentSpeedMs = CurrentSpeedMs;
			if (currentSpeedMs <= 0.5f)
			{
				return 20f;
			}
			float num = 0.5f;
			float trackWetness = GetTrackWetness();
			float b = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics.WetDecelerationMultiplier : 0.6f);
			float num2 = Mathf.Lerp(1f, b, trackWetness);
			float num3 = Mathf.Max(0.2f, num * num2);
			float num4 = 2.5f;
			return currentSpeedMs * currentSpeedMs / (2f * num3) + currentSpeedMs * num4 + 20f;
		}

		public bool ShouldProtectJunctionLock(Junction junction)
		{
			if (junction == null)
			{
				return false;
			}
			if (_trainCar != null)
			{
				TrainCar occupyingCar;
				if (JunctionController.IsJunctionPhysicallyOccupied(junction, out occupyingCar, _trainCar.trainset))
				{
					return true;
				}
				if (JunctionController.Instance != null && JunctionController.Instance.IsJunctionMonitoredForClearance(junction, this))
				{
					return true;
				}
				Vector3 trailingCarPosition = GetTrailingCarPosition();
				Vector3 rhs = junction.position - trailingCarPosition;
				Vector3 lhs = _trainCar.transform.forward * ((TargetDirection >= 0f) ? 1f : (-1f));
				if (Vector3.Dot(lhs, rhs) < -20f)
				{
					return false;
				}
			}
			bool flag = false;
			Signals.Game.Signal signal = ((ApproachingSignal != null) ? ApproachingSignal : GoverningSignal);
			if (signal == null && _upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
			{
				signal = _upcomingSignalBlocks[0].ExitSignal;
			}
			if (signal != null && (DistanceToSignal < 2000f || DistanceToGoverningSignal < 2000f))
			{
				Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
				if (signal2 != null && signal2.IsOn && signal2.CurrentAspect != null)
				{
					flag = signal2.CurrentAspect.DisallowPassing;
				}
			}
			if ((CurrentSpeedKmh < 1.5f && (State == EngineState.StationHold || State == EngineState.TerminusStop || DistanceToObstacle < 150f)) || _isWaitingOutsideStationThroat)
			{
				return false;
			}
			// CRITICAL INVARIANT [AIE-19]: Protect switches in the immediate pre-signal block (Block 0) even when halted at Hp 0
			bool flag2 = false;
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
			{
				SignalBlockInfo signalBlockInfo = _upcomingSignalBlocks[0];
				if (signalBlockInfo != null && signalBlockInfo.Switches != null)
				{
					for (int i = 0; i < signalBlockInfo.Switches.Count; i++)
					{
						if (signalBlockInfo.Switches[i].Junction == junction)
						{
							flag2 = true;
							break;
						}
					}
				}
			}
			// CRITICAL INVARIANT [JNC-09]: Protect switches in the reserved signal block (Block 1 / direct signal reservation) when holding an active DVSignals reservation so ReleasePassedAndConflictingJunctions does not drop crossover locks before Hp 1/Hp 2 clears
			if (!flag2 && _playerYieldWaitTimer <= 0f && HasActiveSignalReservation() && !SignalRegistry.IsJunctionReservedByPlayerSignal(junction))
			{
				if (SignalRegistry.GetEngineerReservingJunctionSignal(junction) == this)
				{
					flag2 = true;
				}
				else if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 1)
				{
					SignalBlockInfo reservedBlockInfo = _upcomingSignalBlocks[1];
					if (reservedBlockInfo != null && reservedBlockInfo.Switches != null)
					{
						for (int i = 0; i < reservedBlockInfo.Switches.Count; i++)
						{
							if (reservedBlockInfo.Switches[i].Junction == junction)
							{
								flag2 = true;
								break;
							}
						}
					}
				}
			}
			if (flag2)
			{
				return true;
			}
			if (CurrentSpeedKmh < 1.5f && flag)
			{
				return false;
			}
			// CRITICAL INVARIANT [AIE-18]: Never protect switches beyond the immediate entered block while yielding at a main signal
			if (_playerYieldWaitTimer > 0f)
			{
				return false;
			}
			if (_upcomingTracks != null && _upcomingTracks.Count > 1)
			{
				int num = Math.Min(_upcomingTracks.Count, 6);
				for (int j = 1; j < num; j++)
				{
					Junction junction2;
					byte requiredBranch;
					if (SignalRegistry.TryGetJunctionBetweenTracks(_upcomingTracks[j - 1], _upcomingTracks[j], out junction2, out requiredBranch) && junction2 == junction)
					{
						if (SignalRegistry.IsJunctionReservedByPlayerSignal(junction))
						{
							return false;
						}
						return true;
					}
				}
			}
			if (CurrentSpeedKmh < 1.5f)
			{
				return false;
			}
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 1)
			{
				SignalBlockInfo signalBlockInfo = _upcomingSignalBlocks[0];
				SignalBlockInfo signalBlockInfo2 = _upcomingSignalBlocks[1];
				bool flag3 = false;
				if (signalBlockInfo2 != null && signalBlockInfo2.Switches != null)
				{
					for (int i = 0; i < signalBlockInfo2.Switches.Count; i++)
					{
						if (signalBlockInfo2.Switches[i].Junction == junction)
						{
							flag3 = true;
							break;
						}
					}
				}
				if (flag3)
				{
					Signals.Game.Signal signal3 = ((signalBlockInfo != null) ? signalBlockInfo.ExitSignal : ApproachingSignal);
					if (signal3 != null && signal3.Parent != null)
					{
						signal3 = signal3.Parent;
					}
					if (signal3 != null && signal3.IsOn && signal3.CurrentAspect != null && !signal3.CurrentAspect.DisallowPassing)
					{
						float num2 = ((signalBlockInfo != null && signalBlockInfo.DistanceToExit > 0f) ? signalBlockInfo.DistanceToExit : DistanceToSignal);
						float num3 = CalculateStoppingDistance();
						if (num2 <= num3)
						{
							return true;
						}
					}
					return false;
				}
			}
			return false;
		}

		// CRITICAL INVARIANT [AIE-18]: Main Signal Yield Feasibility & 120s Hold Execution for Player Switch Contention
		public bool CanYieldToPlayerAtSwitch(Junction targetJunction)
		{
			if (targetJunction == null || _trainCar == null)
			{
				return false;
			}
			TrainCar occupyingCar;
			if (JunctionController.IsJunctionPhysicallyOccupied(targetJunction, out occupyingCar, _trainCar.trainset))
			{
				return false;
			}
			if (JunctionController.Instance != null && JunctionController.Instance.IsJunctionMonitoredForClearance(targetJunction, this))
			{
				return false;
			}
			Vector3 trailingCarPosition = GetTrailingCarPosition();
			Vector3 rhs = targetJunction.position - trailingCarPosition;
			Vector3 lhs = _trainCar.transform.forward * ((TargetDirection >= 0f) ? 1f : (-1f));
			if (Vector3.Dot(lhs, rhs) < -20f)
			{
				return true;
			}
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
			{
				SignalBlockInfo block0 = _upcomingSignalBlocks[0];
				if (block0 != null && block0.EntrySignal == null && block0.Switches != null)
				{
					for (int i = 0; i < block0.Switches.Count; i++)
					{
						if (block0.Switches[i].Junction == targetJunction)
						{
							return false;
						}
					}
				}
			}
			if (_playerYieldWaitTimer > 0f && _yieldGoverningSignal != null)
			{
				return true;
			}
			Signals.Game.Signal govSig = GoverningSignal;
			float distToGovSig = DistanceToGoverningSignal;
			if (govSig == null || !SignalRegistry.IsMainSignal(govSig) || distToGovSig <= 0f || float.IsInfinity(distToGovSig))
			{
				return false;
			}
			float distanceToSwitch = Vector3.Distance(_trainCar.transform.position, targetJunction.position);
			if (_upcomingTracks != null && _upcomingTracks.Count > 1)
			{
				float accumDist = 0f;
				bool foundFirstJunc = false;
				for (int j = 1; j < _upcomingTracks.Count; j++)
				{
					Junction junc;
					byte reqBranch;
					if (SignalRegistry.TryGetJunctionBetweenTracks(_upcomingTracks[j - 1], _upcomingTracks[j], out junc, out reqBranch) && junc != null)
					{
						if (!foundFirstJunc)
						{
							accumDist = Vector3.Distance(_trainCar.transform.position, junc.position);
							foundFirstJunc = true;
						}
						if (junc == targetJunction)
						{
							distanceToSwitch = Mathf.Max(distanceToSwitch, accumDist);
							break;
						}
					}
					if (foundFirstJunc && _upcomingTracks[j] != null && _upcomingTracks[j].curve != null)
					{
						accumDist += _upcomingTracks[j].curve.length;
					}
				}
			}
			if (distToGovSig >= distanceToSwitch)
			{
				return false;
			}
			if (distToGovSig < CalculateStoppingDistance())
			{
				return false;
			}
			return true;
		}

		public bool TryRequestPlayerSwitchYield(Junction targetJunction)
		{
			if (!CanYieldToPlayerAtSwitch(targetJunction))
			{
				return false;
			}
			Signals.Game.Signal govSig = ((GoverningSignal != null) ? GoverningSignal : _yieldGoverningSignal);
			_playerYieldWaitTimer = 120f;
			_yieldContendedSwitch = targetJunction;
			_yieldGoverningSignal = govSig;
			_stoppedSwitchAlignCooldown = Mathf.Max(_stoppedSwitchAlignCooldown, 5f);
			_stoppedAtRedTimer = 0f;
			_obstacleWaitTimer = 0f;
			if (!float.IsInfinity(DistanceToGoverningSignal))
			{
				_switchHoldDistance = Mathf.Min(_switchHoldDistance, Mathf.Max(0f, DistanceToGoverningSignal - 15f));
			}
			IList<JunctionSwitchInfo> immediateSwitches = null;
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0 && _upcomingSignalBlocks[0] != null && _upcomingSignalBlocks[0].EntrySignal == null)
			{
				immediateSwitches = _upcomingSignalBlocks[0].Switches;
			}
			if (JunctionController.Instance != null)
			{
				JunctionController.Instance.ReleaseDownstreamLocksForYield(this, targetJunction, immediateSwitches);
			}
			if (_reservedDVSignals.Count > 0)
			{
				_sigsToReleaseScratch.Clear();
				foreach (Signals.Game.Signal sig in _reservedDVSignals)
				{
					_sigsToReleaseScratch.Add(sig);
				}
				for (int i = 0; i < _sigsToReleaseScratch.Count; i++)
				{
					SignalRegistry.ClearDVSignalReservation(_sigsToReleaseScratch[i]);
				}
				_reservedDVSignals.Clear();
			}
			if (ApproachingSignal != null)
			{
				SignalRegistry.ClearDVSignalReservation(ApproachingSignal);
			}
			if (govSig != null && govSig != ApproachingSignal)
			{
				SignalRegistry.ClearDVSignalReservation(govSig);
			}
			Main.LogDebug(string.Format("[AITraffic] Train '{0}' yielding switch '{1}' to player before main signal '{2}' (dist={3:F1}m, stopDist={4:F1}m). Holding for 120s.", LocoId, targetJunction.name, SignalRegistry.GetSignalName(govSig), DistanceToGoverningSignal, CalculateStoppingDistance()));
			return true;
		}

		// CRITICAL INVARIANT [QUIRK-04]: Event-Driven Signal Wake-Up Debouncing
		public static void ClearSignalWakeupCooldowns()
		{
			s_signalWakeupCooldowns.Clear();
		}

		private static void DebouncedWakeAndForceUpdateSignal(Signals.Game.Signal signal)
		{
			if (signal != null)
			{
				float time = Time.time;
				float value;
				if (!s_signalWakeupCooldowns.TryGetValue(signal, out value) || !(time - value < 0.5f))
				{
					s_signalWakeupCooldowns[signal] = time;
					SignalRegistry.WakeAndForceUpdateSignal(signal);
				}
			}
		}

		private void ClearShuntingState()
		{
			_isShuntingMode = false;
			_isSelfBlockedShunting = false;
			_shuntingBypassedSignal = null;
			_shuntingTargetSignal = null;
			_shuntingTargetTrack = null;
			_shuntingTargetSpan = 0.0;
			_shuntingTargetTrackPathIndex = -1;
			_shuntingTargetWasClear = false;
			_lastShuntingTargetDistance = float.PositiveInfinity;
		}

		private void SetShuntingTarget(Signals.Game.Signal signal, float distance = float.PositiveInfinity, bool isClear = false)
		{
			_shuntingTargetSignal = signal;
			_lastShuntingTargetDistance = distance;
			_shuntingTargetWasClear = isClear;
			_shuntingTargetTrack = null;
			_shuntingTargetSpan = 0.0;
			_shuntingTargetTrackPathIndex = -1;
			if (signal == null)
			{
				return;
			}
			Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
			RailTrack track;
			double span;
			if (SignalRegistry.TryGetSignalPlacement(signal2, out track, out span))
			{
				_shuntingTargetTrack = track;
				_shuntingTargetSpan = span;
				if (CurrentPath != null && CurrentPath.Tracks != null && track != null)
				{
					_shuntingTargetTrackPathIndex = CurrentPath.Tracks.IndexOf(track);
				}
			}
		}

		private void Awake()
		{
			_state = EngineState.Idle;
			TargetDirection = 1f;
			DistanceToSignal = float.PositiveInfinity;
			DistanceToObstacle = float.PositiveInfinity;
			DistanceToDestination = float.PositiveInfinity;
			StationDwellDuration = 45f;
			SpeedToleranceKmh = 1f;
			ThrottleSlewRate = 0.08f;
			BrakeSlewRate = 0.85f;
			DynamicBrakeSlewRate = 0.85f;
			_slipThrottleReduction = 1f;
			_thermalThrottleLimit = 1f;
			_overcurrentThrottleLimit = 1f;
			_rampThrottle = 0f;
			ClearShuntingState();
			SpawnTime = Time.time;
			StationaryTimer = 0f;
			_heavySensorUpdateCooldown = UnityEngine.Random.Range(0.25f, 0.5f);
			if (_trainCar == null)
			{
				_trainCar = GetComponent<TrainCar>();
			}
			InitializeControllers();
			if (TrafficManager.IsRunning && TrafficManager.Instance != null)
			{
				TrafficManager.Instance.RegisterEngineer(this);
			}
		}

		public void SetLockedPullingReverser(float pullingReverser)
		{
			_ambientLockedReverser = ((pullingReverser >= 0f) ? 1f : (-1f));
			_desiredReverser = _ambientLockedReverser;
			_commandedReverser = _ambientLockedReverser;
		}

		public void InitiateSpurReverseEscape(Junction divergingSwitch, RailTrack recoveryTargetTrack, byte mainlineBranch, float pullingReverser)
		{
			SetLockedPullingReverser(pullingReverser);
			_offRouteDivergingSwitch = divergingSwitch;
			_offRouteRecoveryTargetTrack = recoveryTargetTrack;
			_offRouteMainlineBranch = mainlineBranch;
			_offRouteReverseStage = OffRouteReverseStage.Halting;
			_offRouteReverseTimer = 0f;
		}

		private void OnDestroy()
		{
			try
			{
				if (_trainCar != null)
				{
					AILocomotiveLighting component = _trainCar.GetComponent<AILocomotiveLighting>();
					if (component != null)
					{
						component.Shutdown();
						UnityEngine.Object.Destroy(component);
					}
					ConsistEOTMarker.RemoveFromCar(_trainCar);
				}
				for (int i = 0; i < _registeredConsistCars.Count; i++)
				{
					TrainCar trainCar = _registeredConsistCars[i];
					if (trainCar != null)
					{
						AILocomotiveLighting component2 = trainCar.GetComponent<AILocomotiveLighting>();
						if (component2 != null)
						{
							component2.Shutdown();
							UnityEngine.Object.Destroy(component2);
						}
						ConsistEOTMarker.RemoveFromCar(trainCar);
					}
				}
				if (IsWorkerDriven)
				{
					if (_trainCar != null)
					{
						AIDamageImmunityPatches.UnregisterAISimComponents(_trainCar);
					}
					for (int i = 0; i < _registeredConsistCars.Count; i++)
					{
						TrainCar trainCar = _registeredConsistCars[i];
						if (trainCar != null && trainCar.IsLoco)
						{
							AIDamageImmunityPatches.UnregisterAISimComponents(trainCar);
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				if (RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
				{
					RailGraph.Instance.ReleaseAllReservationsFor(this);
					RailGraph.Instance.ReleaseAllCorridorTokensFor(this);
				}
			}
			catch
			{
			}
			_reservedTracks.Clear();
			_activeCorridorTracks.Clear();
			ReleaseAllSignalReservations();
			ClearShuntingState();
			if (TrafficManager.IsRunning && TrafficManager.Instance != null)
			{
				TrafficManager.Instance.UnregisterEngineer(this);
			}
			try
			{
				if (JunctionController.Instance != null)
				{
					// CRITICAL INVARIANT [WRK-04]: ReleaseAllLocksFor
				JunctionController.Instance.ReleaseAllLocksFor(this);
				}
			}
			catch
			{
			}
		}

		public void ReleaseAllSignalReservations()
		{
			if (_reservedDVSignals.Count <= 0)
			{
				return;
			}
			foreach (Signals.Game.Signal reservedDVSignal in _reservedDVSignals)
			{
				if (reservedDVSignal != null)
				{
					SignalRegistry.ClearDVSignalReservation(reservedDVSignal);
				}
			}
			_reservedDVSignals.Clear();
		}

		private void Start()
		{
			if (!(_trainCar != null))
			{
				return;
			}
			InitializeControls();
			if (_registeredConsistCars.Count == 0 && _trainCar.trainset != null && _trainCar.trainset.cars != null)
			{
				RegisterConsistCars(_trainCar.trainset.cars);
			}
			TrainSpawner.RemoveLocoZoneBlockers(_trainCar);
			TrainSpawner.ApplyAIDamageImmunity(_trainCar);
			for (int i = 0; i < _registeredConsistCars.Count; i++)
			{
				TrainCar trainCar = _registeredConsistCars[i];
				if (trainCar != null && trainCar.IsLoco)
				{
					TrainSpawner.RemoveLocoZoneBlockers(trainCar);
					TrainSpawner.ApplyAIDamageImmunity(trainCar);
				}
			}
			try
			{
				AILocomotiveLighting.GetOrCreate(_trainCar, this);
			}
			catch
			{
			}
		}

		private void Update()
		{
			if (_trainCar == null)
			{
				return;
			}
#if DEBUG
			long __wdStart = AITraffic.Diagnostics.HitchWatchdog.GetTimestamp();
			long __wdMem = AITraffic.Diagnostics.HitchWatchdog.GetCurrentMemory();
			try {
#endif
			bool flag = _trainCar.derailed || (_trainCar.FrontBogie != null && _trainCar.FrontBogie.HasDerailed) || (_trainCar.RearBogie != null && _trainCar.RearBogie.HasDerailed);
			bool flag2 = flag;
			if (!flag2 && _registeredConsistCars.Count > 0)
			{
				for (int i = 0; i < _registeredConsistCars.Count; i++)
				{
					TrainCar trainCar = _registeredConsistCars[i];
					if (trainCar != null && (trainCar.derailed || (trainCar.FrontBogie != null && trainCar.FrontBogie.HasDerailed) || (trainCar.RearBogie != null && trainCar.RearBogie.HasDerailed)))
					{
						flag2 = true;
						break;
					}
				}
			}
			if (flag2)
			{
				HasConsistDerailed = true;
				if (State != EngineState.TerminusStop)
				{
					if (Main.ModEntry != null && Main.ModEntry.Logger != null)
					{
						Main.ModEntry.Logger.Warning(string.Format("[AIEngineer] Consist derailment detected on train '{0}'! Triggering emergency crash shutdown and releasing reservations.", ModCompatManager.GetCarId(_trainCar)));
					}
					EmergencyCrashShutdown();
				}
				return;
			}
			float deltaTime = Time.deltaTime;
			if (deltaTime <= 0f)
			{
				return;
			}
			if (CurrentSpeedKmh < 0.2f && State != EngineState.TerminusStop && State != EngineState.StationHold)
			{
				StationaryTimer += deltaTime;
			}
			else
			{
				StationaryTimer = 0f;
			}
			if (_reverseProtocolStage != 0 || _offRouteReverseStage != 0)
			{
				if (_offRouteReverseStage == OffRouteReverseStage.None)
				{
					UpdatePathAndJunctions(deltaTime);
				}
				if (!(_trainCar == null))
				{
					UpdateSensors(deltaTime);
					UpdateWheelTractionAndSlideProtection(deltaTime);
					UpdatePowertrainProtection(deltaTime);
					if (_reverseProtocolStage != 0)
					{
						UpdateSingleTrackReverseProtocol(deltaTime);
					}
					else if (_offRouteReverseStage != 0)
					{
						UpdateOffRouteReverseProtocol(deltaTime);
					}
					ExecuteControlOutputs(deltaTime);
				}
				return;
			}
			UpdatePathAndJunctions(deltaTime);
			if (!(_trainCar == null))
			{
				UpdateSensors(deltaTime);
				UpdateSingleTrackCorridorProtection(deltaTime);
				UpdateWheelTractionAndSlideProtection(deltaTime);
				UpdatePowertrainProtection(deltaTime);
				UpdateLevelCrossingHorn(deltaTime);
				UpdateSpeedProfile(deltaTime);
				UpdateStateMachine(deltaTime);
				if (_dm3Controller != null && _dm3Controller.IsDM3)
				{
					_dm3Controller.Update(deltaTime);
				}
				ExecuteControlOutputs(deltaTime);
			}
#if DEBUG
			}
			finally
			{
				AITraffic.Diagnostics.HitchWatchdog.RecordEngineer(this, __wdStart, __wdMem);
			}
#endif
		}

		public void Initialize(TrainCar locomotive)
		{
			_trainCar = locomotive;
			InitializeControllers();
			InitializeControls();
		}

		private void InitializeControllers()
		{
			PhysicsConfig physicsConfig = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics : new PhysicsConfig());
			ThrottlePID = new PIDController(physicsConfig.ThrottleKp, physicsConfig.ThrottleKi, physicsConfig.ThrottleKd, 0f, 1f, 0.15f);
			BrakePID = new PIDController(physicsConfig.BrakeKp, physicsConfig.BrakeKi, physicsConfig.BrakeKd);
			SpeedProfiler = new SpeedProfileGenerator();
			SpeedProfiler.ServiceDeceleration = 0.45f * physicsConfig.DecelerationMultiplier;
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

		private void UpdateSensors(float dt)
		{
			float forwardSpeed = _trainCar.GetForwardSpeed();
			CurrentSpeedMs = Mathf.Abs(forwardSpeed);
			CurrentSpeedKmh = CurrentSpeedMs * 3.6f;
			float b = ((dt > 0.001f) ? ((CurrentSpeedMs - _lastSpeedMs) / dt) : 0f);
			_lastSpeedMs = CurrentSpeedMs;
			_smoothAccMs2 = Mathf.Lerp(_smoothAccMs2, b, Mathf.Clamp01(dt * 3f));
			CurrentAccelerationMs2 = _smoothAccMs2;
			if (_controlsOverrider == null && _trainCar.SimController != null)
			{
				_controlsOverrider = _trainCar.SimController.controlsOverrider;
				if (_controlsOverrider != null)
				{
					_hasDynamicBrake = _controlsOverrider.DynamicBrake != null;
				}
			}
			float num = CurrentSpeedMs * dt;
			if (!float.IsInfinity(DistanceToSignal))
			{
				DistanceToSignal = Mathf.Max(0f, DistanceToSignal - num);
			}
			if (!float.IsInfinity(DistanceToObstacle))
			{
				DistanceToObstacle = Mathf.Max(0f, DistanceToObstacle - num);
			}
			if (!float.IsInfinity(_corridorHoldDistance))
			{
				_corridorHoldDistance = Mathf.Max(0f, _corridorHoldDistance - num);
			}
			if (!float.IsInfinity(_switchHoldDistance))
			{
				_switchHoldDistance = Mathf.Max(0f, _switchHoldDistance - num);
			}
			if (!float.IsInfinity(DistanceToDestination))
			{
				DistanceToDestination = Mathf.Max(0f, DistanceToDestination - num);
			}
			if (!float.IsInfinity(_lastShuntingTargetDistance))
			{
				_lastShuntingTargetDistance = Mathf.Max(0f, _lastShuntingTargetDistance - num);
			}
			_heavySensorUpdateCooldown -= dt;
			if (_heavySensorUpdateCooldown > 0f)
			{
				return;
			}
			_heavySensorUpdateCooldown = 0.25f + UnityEngine.Random.Range(0f, 0.05f);
			RailTrack railTrack = null;
			double num2 = 0.0;
			if (_trainCar != null)
			{
				if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
				{
					railTrack = _trainCar.FrontBogie.track;
					num2 = ((_trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0);
				}
				else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
				{
					railTrack = _trainCar.RearBogie.track;
					num2 = ((_trainCar.RearBogie.traveller != null) ? _trainCar.RearBogie.traveller.Span : 0.0);
				}
			}
			if (railTrack != null && railTrack.curve != null && _trainCar != null)
			{
				float length = railTrack.curve.length;
				float t = ((length > 0.1f) ? Mathf.Clamp01((float)(num2 / (double)length)) : 0.5f);
				Vector3 tangentAt = railTrack.curve.GetTangentAt(t);
				if (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex + 1)
				{
					RailTrack railTrack2 = CurrentPath.Tracks[CurrentPathTrackIndex + 1];
					if (railTrack2 != null && railTrack2.curve != null)
					{
						Vector3 pointAt = railTrack.curve.GetPointAt(0f);
						Vector3 pointAt2 = railTrack.curve.GetPointAt(1f);
						Vector3 pointAt3 = railTrack2.curve.GetPointAt(0f);
						Vector3 pointAt4 = railTrack2.curve.GetPointAt(1f);
						float num3 = Mathf.Min(Vector3.Distance(pointAt2, pointAt3), Vector3.Distance(pointAt2, pointAt4));
						float num4 = Mathf.Min(Vector3.Distance(pointAt, pointAt3), Vector3.Distance(pointAt, pointAt4));
						TargetDirection = ((num3 <= num4) ? 1f : (-1f));
					}
				}
				else if (CurrentPath != null && CurrentPath.Tracks != null && CurrentPathTrackIndex > 0 && CurrentPathTrackIndex == CurrentPath.Tracks.Count - 1)
				{
					RailTrack railTrack3 = CurrentPath.Tracks[CurrentPathTrackIndex - 1];
					if (railTrack3 != null && railTrack3.curve != null)
					{
						Vector3 pointAt = railTrack.curve.GetPointAt(0f);
						Vector3 pointAt2 = railTrack.curve.GetPointAt(1f);
						Vector3 pointAt5 = railTrack3.curve.GetPointAt(0f);
						Vector3 pointAt6 = railTrack3.curve.GetPointAt(1f);
						float num5 = Mathf.Min(Vector3.Distance(pointAt, pointAt5), Vector3.Distance(pointAt, pointAt6));
						float num6 = Mathf.Min(Vector3.Distance(pointAt2, pointAt5), Vector3.Distance(pointAt2, pointAt6));
						TargetDirection = ((num5 <= num6) ? 1f : (-1f));
					}
				}
				else
				{
					Vector3 forward = _trainCar.transform.forward;
					TargetDirection = ((Vector3.Dot(forward, tangentAt) >= 0f) ? 1f : (-1f));
				}
				Vector3 rhs = tangentAt * TargetDirection;
				_desiredReverser = ((Vector3.Dot(_trainCar.transform.forward, rhs) >= 0f) ? 1f : (-1f));
				if (_ambientLockedReverser == 0f)
				{
					_ambientLockedReverser = _desiredReverser;
				}
				if (IsIntentionalReverseActive)
				{
					_commandedReverser = ((_ambientLockedReverser != 0f) ? (0f - _ambientLockedReverser) : (0f - TargetDirection));
					_desiredReverser = _commandedReverser;
				}
				else
				{
					_desiredReverser = _ambientLockedReverser;
				}
				float num7 = ((_ambientLockedReverser != 0f) ? _ambientLockedReverser : _desiredReverser);
				Vector3 lhs = _trainCar.transform.forward * num7;
				TargetDirection = ((Vector3.Dot(lhs, tangentAt) >= 0f) ? 1f : (-1f));
			}
			else if (TargetDirection == 0f)
			{
				TargetDirection = 1f;
			}
			SignalRegistry.TryFindUpcomingSignals(railTrack, num2, TargetDirection, UpcomingTracks, _upcomingSignals);
			if (_upcomingSignals.Count > 0)
			{
				ApproachingSignal = _upcomingSignals[0].Signal;
				DistanceToSignal = _upcomingSignals[0].Distance;
				float distance;
				GoverningSignal = SignalRegistry.FindFirstGoverningSignal(_upcomingSignals, out distance);
				DistanceToGoverningSignal = distance;
			}
			else
			{
				ApproachingSignal = null;
				DistanceToSignal = float.PositiveInfinity;
				GoverningSignal = null;
				DistanceToGoverningSignal = float.PositiveInfinity;
			}
			float distanceToObstacle;
			RailTrack obstacleTrack;
			TrainCar obstacleCar;
			Vector3 ourDirectionAtObstacle;
			if (SignalRegistry.TryFindUpcomingObstacle(railTrack, num2, TargetDirection, UpcomingTracks, (_trainCar != null) ? _trainCar.trainset : null, out distanceToObstacle, _trainCar, out obstacleTrack, out obstacleCar, out ourDirectionAtObstacle))
			{
				_obstacleTrack = obstacleTrack;
				ClassifyUpcomingObstacle(obstacleCar, ourDirectionAtObstacle, out _isObstacleComingHeadOn, out _isObstacleSameDirection);
				float switchDistance;
				if (!IsWorkerDriven && obstacleTrack != null && obstacleTrack != railTrack && !_isObstacleSameDirection && TryGetUpcomingDivergingSwitchDistance(railTrack, num2, obstacleTrack, out switchDistance))
				{
					float b2 = Mathf.Max(7.5f, switchDistance - 15f);
					DistanceToObstacle = Mathf.Min(distanceToObstacle, b2);
					_hasPendingDivergingSwitchHold = true;
					_pendingDivergingSwitchDistance = switchDistance;
				}
				else
				{
					DistanceToObstacle = distanceToObstacle;
					_hasPendingDivergingSwitchHold = false;
					_pendingDivergingSwitchDistance = float.PositiveInfinity;
				}
			}
			else
			{
				DistanceToObstacle = float.PositiveInfinity;
				_obstacleTrack = null;
				_isObstacleComingHeadOn = false;
				_isObstacleSameDirection = false;
				_hasPendingDivergingSwitchHold = false;
				_pendingDivergingSwitchDistance = float.PositiveInfinity;
			}
			SignalRegistry.CalculateUpcomingSignalBlocks(railTrack, num2, TargetDirection, UpcomingTracks, (_trainCar != null) ? _trainCar.trainset : null, _upcomingSignalBlocks, 3000f);
			UpdateShuntingHandover(railTrack, num2);
		}

		private void UpdateShuntingHandover(RailTrack currentTrack, double currentSpan)
		{
			if (!_isShuntingMode)
			{
				return;
			}
			if (!_isSelfBlockedShunting && currentTrack != null && CurrentPathTrackIndex >= 2)
			{
				RailEdge edge = RailGraph.Instance.GetEdge(currentTrack);
				bool flag = (edge != null && edge.IsYardTrack) || Pathfinder.IsIndustrialYardOrStorageTrack(currentTrack);
				if ((edge != null && edge.IsDoubleTrackMainline) || (currentTrack.name != null && (currentTrack.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || currentTrack.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0)) || (!flag && edge != null && edge.SpeedLimit >= 30f))
				{
					ClearShuntingState();
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' reached mainline track '{1}' (index {2}). Cleared Slow Shunting Mode via mainline safety net.", LocoId, currentTrack.name, CurrentPathTrackIndex));
					}
					return;
				}
			}
			Signals.Game.Signal signal = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
			Signals.Game.Signal signal2 = null;
			float distance = float.PositiveInfinity;
			bool isClear = false;
			if (_upcomingSignals != null)
			{
				for (int i = 0; i < _upcomingSignals.Count; i++)
				{
					SignalRegistry.UpcomingSignal upcomingSignal = _upcomingSignals[i];
					Signals.Game.Signal signal3 = upcomingSignal.Signal;
					if (signal3 != null)
					{
						Signals.Game.Signal signal4 = ((signal3.Parent != null) ? signal3.Parent : signal3);
						if ((signal4 == null || signal4 != signal) && SignalRegistry.IsLineSpeedAuthorizingSignal(signal4))
						{
							signal2 = signal4;
							distance = upcomingSignal.Distance;
							isClear = SignalRegistry.IsSignalDisplayingClear(signal4);
							break;
						}
					}
				}
			}
			if (_shuntingTargetSignal != null)
			{
				Signals.Game.Signal signal5 = ((_shuntingTargetSignal.Parent != null) ? _shuntingTargetSignal.Parent : _shuntingTargetSignal);
				bool flag2 = false;
				float num = float.PositiveInfinity;
				if (_upcomingSignals != null)
				{
					for (int i = 0; i < _upcomingSignals.Count; i++)
					{
						SignalRegistry.UpcomingSignal upcomingSignal = _upcomingSignals[i];
						Signals.Game.Signal signal3 = upcomingSignal.Signal;
						if (signal3 != null)
						{
							Signals.Game.Signal signal4 = ((signal3.Parent != null) ? signal3.Parent : signal3);
							if (signal4 == signal5 || (signal4.Controller != null && signal5.Controller != null && signal4.Controller == signal5.Controller))
							{
								flag2 = true;
								num = upcomingSignal.Distance;
								break;
							}
						}
					}
				}
				bool flag3 = SignalRegistry.IsSignalDisplayingClear(signal5);
				if (flag3)
				{
					_shuntingTargetWasClear = true;
				}
				RailTrack track;
				double span;
				if (_shuntingTargetTrack == null && SignalRegistry.TryGetSignalPlacement(signal5, out track, out span))
				{
					_shuntingTargetTrack = track;
					_shuntingTargetSpan = span;
				}
				if (_shuntingTargetTrack != null && CurrentPath != null && CurrentPath.Tracks != null && _shuntingTargetTrackPathIndex < 0)
				{
					_shuntingTargetTrackPathIndex = CurrentPath.Tracks.IndexOf(_shuntingTargetTrack);
				}
				bool flag4 = false;
				if ((CurrentSpeedKmh >= 0.5f || _commandedThrottle > 0.05f) && (_shuntingTargetWasClear || flag3))
				{
					if (_lastShuntingTargetDistance <= 120f && (!flag2 || num <= 1.5f))
					{
						flag4 = true;
					}
					else if (_shuntingTargetTrackPathIndex >= 0 && CurrentPathTrackIndex > _shuntingTargetTrackPathIndex)
					{
						flag4 = true;
					}
					else if (_shuntingTargetTrack != null && currentTrack == _shuntingTargetTrack && ((TargetDirection >= 0f) ? (currentSpan >= _shuntingTargetSpan - 1.0) : (currentSpan <= _shuntingTargetSpan + 1.0)))
					{
						flag4 = true;
					}
				}
				if (flag4)
				{
					Signals.Game.Signal shuntingTargetSignal = _shuntingTargetSignal;
					ClearShuntingState();
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' passed governing Hauptsignal '{1}' ({2}) displaying GO aspect. Cleared Slow Shunting Mode, transitioning to line speed.", LocoId, SignalRegistry.GetSignalName(shuntingTargetSignal), SignalRegistry.GetAspectDisplayName(shuntingTargetSignal)));
					}
					return;
				}
				if (flag2)
				{
					_lastShuntingTargetDistance = num;
				}
				else
				{
					bool flag5 = _shuntingTargetTrack != null && CurrentPath != null && CurrentPath.Tracks != null && !CurrentPath.Tracks.Contains(_shuntingTargetTrack);
					bool flag6 = _shuntingTargetTrackPathIndex >= 0 && CurrentPathTrackIndex > _shuntingTargetTrackPathIndex;
					bool flag7 = _lastShuntingTargetDistance <= 30f && CurrentSpeedKmh >= 1f;
					if (flag5 || flag6 || flag7)
					{
						_shuntingTargetSignal = null;
						_shuntingTargetTrack = null;
						_shuntingTargetSpan = 0.0;
						_shuntingTargetTrackPathIndex = -1;
						_shuntingTargetWasClear = false;
						_lastShuntingTargetDistance = float.PositiveInfinity;
					}
				}
			}
			if (_shuntingTargetSignal == null && signal2 != null)
			{
				SetShuntingTarget(signal2, distance, isClear);
			}
		}

		private void UpdatePathAndJunctions(float dt)
		{
			if (CurrentPath == null || !CurrentPath.IsValid)
			{
				return;
			}
			if (_stoppedSwitchAlignCooldown > 0f)
			{
				_stoppedSwitchAlignCooldown -= dt;
			}
			// CRITICAL INVARIANT [AIE-18]: Maintain 120s player switch yield hold & suppress realignment/detour timers
			if (_playerYieldWaitTimer > 0f)
			{
				_playerYieldWaitTimer -= dt;
				_stoppedSwitchAlignCooldown = Mathf.Max(_stoppedSwitchAlignCooldown, 5f);
				_stoppedAtRedTimer = 0f;
				_obstacleWaitTimer = 0f;
				if (_playerYieldWaitTimer <= 0f)
				{
					_playerYieldWaitTimer = 0f;
					_yieldContendedSwitch = null;
					_yieldGoverningSignal = null;
				}
			}
			_pathUpdateCooldown -= dt;
			if (_pathUpdateCooldown > 0f)
			{
				return;
			}
			_pathUpdateCooldown = 0.4f;
			RailTrack railTrack = null;
			if (_trainCar != null && _trainCar.FrontBogie != null)
			{
				railTrack = _trainCar.FrontBogie.track;
			}
			if (railTrack == null && _trainCar != null && _trainCar.RearBogie != null)
			{
				railTrack = _trainCar.RearBogie.track;
			}
			if (railTrack != null && CurrentPath.Tracks != null)
			{
				int num = Mathf.Max(0, CurrentPathTrackIndex - 1);
				int num2 = ((num < CurrentPath.Tracks.Count) ? CurrentPath.Tracks.IndexOf(railTrack, num) : (-1));
				if (num2 < 0)
				{
					num2 = CurrentPath.Tracks.IndexOf(railTrack);
				}
				if (num2 >= 0)
				{
					CurrentPathTrackIndex = num2;
				}
				else if (!IsIntentionalReverseActive && _reverseProtocolStage == SingleTrackReverseStage.None && _offRouteReverseStage == OffRouteReverseStage.None)
				{
					bool flag = false;
					if (_trainCar != null && _trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
					{
						int num3 = CurrentPath.Tracks.IndexOf(_trainCar.RearBogie.track);
						if (num3 >= 0)
						{
							flag = true;
						}
					}
					if (!flag)
					{
						RailTrack railTrack2 = ((CurrentPath.Tracks.Count > 0) ? CurrentPath.Tracks[CurrentPath.Tracks.Count - 1] : null);
						bool flag2 = false;
						if (railTrack2 != null && railTrack2 != railTrack && RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
						{
							PathfinderOptions pathfinderOptions = new PathfinderOptions();
							pathfinderOptions.Requester = this;
							pathfinderOptions.RequesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
							pathfinderOptions.PreventPlayerOvertake = false;
							pathfinderOptions.AvoidOccupiedTracks = true;
							pathfinderOptions.StrictlyAvoidOccupied = true;
							PathfinderOptions pathfinderOptions2 = pathfinderOptions;
							Pathfinder pathfinder = new Pathfinder(RailGraph.Instance);
							bool flag3 = TargetDirection >= 0f;
							if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
							{
								flag3 = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
							}
							string text = BuildRerouteKey("OFF1", railTrack, null, railTrack2, flag3);
							RailPath path;
							if (!TryGetAsyncReroute(text, railTrack, railTrack2, flag3, pathfinderOptions2, out path))
							{
								_reroutePending = true;
								_switchHoldDistance = 0f;
								_pathUpdateCooldown = 0.05f;
								return;
							}
							if (path != null && path.IsValid && path.Tracks != null && path.Tracks.Count > 1)
							{
								CurrentPath = path;
								CurrentPathTrackIndex = 0;
								_upcomingTracks.Clear();
								for (int i = 0; i < CurrentPath.Tracks.Count; i++)
								{
									_upcomingTracks.Add(CurrentPath.Tracks[i]);
								}
								flag2 = true;
								if (Main.IsDebugLoggingEnabled)
								{
									Main.LogDebug(string.Format("[AITraffic] Ambient train '{0}' diverted off route onto '{1}'. Successfully rerouted forward to '{2}' ({3} tracks).", LocoId, railTrack.name, DestinationStationName ?? railTrack2.name, path.Tracks.Count));
								}
							}
							else
							{
								pathfinderOptions2.AllowShuntingDetour = true;
								pathfinderOptions2.MaxSearchDistance = 2.5E+07f;
								string text2 = BuildRerouteKey("OFF2", railTrack, null, railTrack2, flag3);
								if (!TryGetAsyncReroute(text2, railTrack, railTrack2, flag3, pathfinderOptions2, out path))
								{
									_reroutePending = true;
									_switchHoldDistance = 0f;
									_pathUpdateCooldown = 0.05f;
									return;
								}
								if (path != null && path.IsValid && path.Tracks != null && path.Tracks.Count > 1)
								{
									CurrentPath = path;
									CurrentPathTrackIndex = 0;
									_upcomingTracks.Clear();
									for (int i = 0; i < CurrentPath.Tracks.Count; i++)
									{
										_upcomingTracks.Add(CurrentPath.Tracks[i]);
									}
									flag2 = true;
									StartInShuntingMode(FindNextGoverningMainSignal(null));
									if (Main.IsDebugLoggingEnabled)
									{
										Main.LogDebug(string.Format("[AITraffic] Ambient train '{0}' diverted off route onto '{1}'. Found detour in shunting mode to '{2}' ({3} tracks).", LocoId, railTrack.name, DestinationStationName ?? railTrack2.name, path.Tracks.Count));
									}
								}
								else
								{
									_consumedRerouteKeys.Remove(text);
									_consumedRerouteKeys.Remove(text2);
								}
							}
						}
						if (!flag2)
						{
							bool flag4 = false;
							int num4 = Mathf.Clamp(CurrentPathTrackIndex, 0, CurrentPath.Tracks.Count - 1);
							RailTrack railTrack3 = ((CurrentPath.Tracks != null && CurrentPath.Tracks.Count > num4) ? CurrentPath.Tracks[num4] : null);
							Junction junction;
							byte requiredBranch;
							if (railTrack3 != null && railTrack3 != railTrack && (SignalRegistry.TryGetJunctionBetweenTracks(railTrack3, railTrack, out junction, out requiredBranch) || SignalRegistry.TryGetJunctionBetweenTracks(railTrack, railTrack3, out junction, out requiredBranch)) && junction != null)
							{
								_offRouteRecoveryTargetTrack = railTrack3;
								_offRouteDivergingSwitch = junction;
								byte offRouteMainlineBranch = 0;
								if (num4 + 1 < CurrentPath.Tracks.Count)
								{
									RailTrack trackB = CurrentPath.Tracks[num4 + 1];
									Junction junction2;
									byte requiredBranch2;
									if (SignalRegistry.TryGetJunctionBetweenTracks(railTrack3, trackB, out junction2, out requiredBranch2) && junction2 == junction)
									{
										offRouteMainlineBranch = requiredBranch2;
									}
								}
								_offRouteMainlineBranch = offRouteMainlineBranch;
								_offRouteReverseStage = OffRouteReverseStage.Halting;
								_offRouteReverseTimer = 0f;
								flag4 = true;
								if (Main.IsDebugLoggingEnabled)
								{
									Main.LogDebug(string.Format("[AITraffic] Train '{0}' off route on dead-end spur '{1}'. Initiating Pass 3 reverse recovery back to '{2}'.", LocoId, railTrack.name, railTrack3.name));
								}
							}
							if (!flag4)
							{
								if (Main.ModEntry != null && Main.ModEntry.Logger != null)
								{
									Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Ambient train '{0}' diverted off route onto '{1}' with no recovery possible. Securing at terminus stop.", LocoId, railTrack.name));
								}
								EnterTerminusStop();
								return;
							}
						}
					}
				}
			}
			if (!IsWorkerDriven && railTrack != null && _obstacleTrack != null && _obstacleTrack != railTrack && !_isObstacleSameDirection && DistanceToObstacle < 2500f)
			{
				_obstacleRerouteCooldown = Mathf.Max(0f, _obstacleRerouteCooldown - dt);
				if (CurrentSpeedKmh < 2f || (_hasPendingDivergingSwitchHold && _pendingDivergingSwitchDistance <= 75f && CurrentSpeedKmh < 5f))
				{
					_obstacleWaitTimer += dt;
				}
				float num5 = (_isObstacleComingHeadOn ? 6f : 12f);
				float requiredWait = num5;
			if (_obstacleWaitTimer >= requiredWait && _obstacleRerouteCooldown <= 0f && _hasPendingDivergingSwitchHold)
				{
					_reroutePending = false;
					bool flag2 = TryDynamicObstacleReroute(railTrack, _obstacleTrack);
					if (!_reroutePending)
					{
						_obstacleRerouteCooldown = (flag2 ? 8f : 15f);
					}
					if (flag2)
					{
						_obstacleWaitTimer = 0f;
					}
				}
			}
			else
			{
				_obstacleWaitTimer = 0f;
			}
			Signals.Game.Signal signal = ((GoverningSignal != null && DistanceToGoverningSignal < 350f) ? GoverningSignal : ApproachingSignal);
			float num6 = ((signal == GoverningSignal) ? DistanceToGoverningSignal : DistanceToSignal);
			Signals.Game.Signal signal3;
			if (railTrack != null && signal != null && num6 < 350f)
			{
				Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
				signal3 = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
				AspectBaseDefinition aspectBaseDefinition = ((signal.CurrentAspect != null) ? signal.CurrentAspect.GetDefinition() : null);
				bool flag5 = signal.CurrentAspect != null && (signal.CurrentAspect.DisallowPassing || (aspectBaseDefinition != null && aspectBaseDefinition.DisallowPassing));
				if (!flag5 && ApproachingSignal != null && SignalRegistry.IsDistantSignal(ApproachingSignal) && ApproachingSignal.CurrentAspect != null)
				{
					string text3 = ApproachingSignal.CurrentAspect.Id ?? "";
					if (text3.IndexOf("VR0", StringComparison.OrdinalIgnoreCase) >= 0 || text3.IndexOf("DISTANT_STOP", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						flag5 = true;
					}
				}
				if (_isShuntingMode && signal2 != null && signal2 == signal3)
				{
					flag5 = false;
				}
				if ((_isSelfBlockedShunting || _shuntingBypassedSignal != null) && (_isHoldingForCorridor || _isHoldingInMultiTrack || !float.IsInfinity(DistanceToObstacle)))
				{
					_shuntingBypassedSignal = null;
					_isSelfBlockedShunting = false;
				}
				// CRITICAL INVARIANT [AIE-19]: Decay _stoppedAtRedTimer on transient aspect flicker while stationary; hard-zero on movement or player yield
				if (_playerYieldWaitTimer > 0f || CurrentSpeedKmh >= 1.5f)
				{
					_stoppedAtRedTimer = 0f;
				}
				else if (!flag5)
				{
					_stoppedAtRedTimer = Mathf.Max(0f, _stoppedAtRedTimer - 2f * dt);
				}
				if (flag5 && CurrentSpeedKmh < 1f && _playerYieldWaitTimer <= 0f)
				{
					_stoppedAtRedTimer += dt;
					if (_stoppedAtRedTimer >= 12f && !_isSelfBlockedShunting && !_isHoldingForCorridor && !_isHoldingInMultiTrack && float.IsInfinity(DistanceToObstacle) && (IsSignalBlockedOnlyBySelf(signal) || CanEnterLoopOnSight(signal)))
					{
						_isShuntingMode = true;
						_isSelfBlockedShunting = true;
						_shuntingBypassedSignal = signal;
						SetShuntingTarget(FindNextGoverningMainSignal(signal));
						_stoppedAtRedTimer = 0f;
						if (Main.ModEntry != null && Main.ModEntry.Logger != null)
						{
							Main.ModEntry.Logger.Log(string.Format("[AITraffic] Train '{0}' stopped at signal '{1}' for 12s. Entering Slow Shunting Mode (<= 15 km/h) on sight to enter loop/clear interlocking. Next governing signal: '{2}'.", LocoId, SignalRegistry.GetSignalName(signal), (_shuntingTargetSignal != null) ? SignalRegistry.GetSignalName(_shuntingTargetSignal) : "None (Line Clear)"));
						}
					}
					if (_stoppedAtRedTimer >= 30f && !IsWorkerDriven)
					{
						_signalRerouteCooldown -= dt;
						if (_signalRerouteCooldown <= 0f)
						{
							bool flag6 = SignalRegistry.IsEntrySignal(signal);
							bool flag2 = false;
							_reroutePending = false;
							if (!flag6 || !IsStationDestination)
							{
								flag2 = TryDynamicRedSignalReroute(railTrack);
							}
							if (!flag2 && !_reroutePending)
							{
								flag2 = TryDynamicShuntingDetour(railTrack);
							}
							if (!_reroutePending)
							{
								_signalRerouteCooldown = (flag2 ? 8f : 15f);
							}
						}
					}
				}
				else
				{
					_stoppedAtRedTimer = 0f;
				}
				if (flag5 && _playerYieldWaitTimer <= 0f)
				{
					if (CurrentSpeedKmh < 1f)
					{
						_signalWaitRetryTimer -= dt;
						if (_signalWaitRetryTimer <= 0f)
						{
							_signalWaitRetryTimer = 5f;
							if (!(CurrentSpeedKmh < 1.5f) || !(DistanceToObstacle < 150f))
							{
								if (SignalRegistry.TryReserveDVSignal(signal))
								{
									DebouncedWakeAndForceUpdateSignal(signal);
									Signals.Game.Signal signal4 = ((signal != null && signal.Parent != null) ? signal.Parent : signal);
									if (signal4 != null && !_reservedDVSignals.Contains(signal4))
									{
										_reservedDVSignals.Add(signal4);
									}
								}
							}
							else if (signal != null)
							{
								Signals.Game.Signal signal4 = ((signal.Parent != null) ? signal.Parent : signal);
								SignalRegistry.ClearDVSignalReservation(signal);
								_reservedDVSignals.Remove(signal);
								if (signal4 != null)
								{
									_reservedDVSignals.Remove(signal4);
								}
							}
						}
					}
					else
					{
						_signalWaitRetryTimer = 5f;
					}
				}
			}
			if (railTrack != null && CurrentSpeedKmh < 1f && CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 1)
			{
				bool flag7 = false;
				int currentPathTrackIndex = CurrentPathTrackIndex;
				for (int j = currentPathTrackIndex; j < CurrentPath.Tracks.Count - 1 && j < currentPathTrackIndex + 4; j++)
				{
					RailTrack railTrack4 = CurrentPath.Tracks[j];
					RailTrack railTrack5 = CurrentPath.Tracks[j + 1];
					if (!(railTrack4 != null) || !(railTrack4.curve != null) || !(railTrack5 != null) || !(railTrack5.curve != null))
					{
						continue;
					}
					RailEdge edge = RailGraph.Instance.GetEdge(railTrack4);
					RailEdge edge2 = RailGraph.Instance.GetEdge(railTrack5);
					if (edge == null || edge2 == null)
					{
						continue;
					}
					RailNode connectingNode = RailGraph.Instance.GetConnectingNode(edge, edge2);
					if (connectingNode != null)
					{
						Vector3 incomingVector = RailGraph.GetIncomingVector(connectingNode, edge);
						Vector3 departingVector = RailGraph.GetDepartingVector(connectingNode, edge2);
						if (Vector3.Dot(incomingVector, departingVector) < 0.2f)
						{
							flag7 = true;
							break;
						}
					}
				}
				if (flag7)
				{
					_stuckRecoveryCooldown -= dt;
					if (_stuckRecoveryCooldown <= 0f)
					{
						RailTrack railTrack2 = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
						if (railTrack2 != null && railTrack2 != railTrack)
						{
							bool flag3 = TargetDirection >= 0f;
							if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
							{
								flag3 = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
							}
							PathfinderOptions pathfinderOptions3 = new PathfinderOptions();
							pathfinderOptions3.Requester = this;
							pathfinderOptions3.RequesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
							pathfinderOptions3.PreferSpeedOverDistance = true;
							pathfinderOptions3.AvoidOccupiedTracks = true;
							pathfinderOptions3.StrictlyAvoidOccupied = true;
							pathfinderOptions3.MaxSearchDistance = 5000000f;
							PathfinderOptions pathfinderOptions2 = pathfinderOptions3;
							string text4 = BuildRerouteKey("HAIRPIN", railTrack, null, railTrack2, flag3);
							RailPath path2;
							if (!TryGetAsyncReroute(text4, railTrack, railTrack2, flag3, pathfinderOptions2, out path2))
							{
								_reroutePending = true;
								_switchHoldDistance = 0f;
								_pathUpdateCooldown = 0.05f;
								return;
							}
							_stuckRecoveryCooldown = 10f;
							_consumedRerouteKeys.Remove(text4);
							if (Main.ModEntry != null && Main.ModEntry.Logger != null)
							{
								Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Train '{0}' detected upcoming hairpin loop in planned route on track '{1}'. Recalculating route.", LocoId, railTrack.name));
							}
							if (path2 != null && path2.IsValid && path2.Tracks != null && path2.Tracks.Count > 1)
							{
								CurrentPath = path2;
								CurrentPathTrackIndex = 0;
								_upcomingTracks.Clear();
								for (int i = 0; i < CurrentPath.Tracks.Count; i++)
								{
									_upcomingTracks.Add(CurrentPath.Tracks[i]);
								}
							}
						}
						else
						{
							_stuckRecoveryCooldown = 10f;
						}
					}
				}
			}
			if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex)
			{
				_upcomingTracks.Clear();
				for (int k = CurrentPathTrackIndex; k < CurrentPath.Tracks.Count; k++)
				{
					_upcomingTracks.Add(CurrentPath.Tracks[k]);
				}
			}
			if (railTrack != null && railTrack.curve != null)
			{
				double num7 = ((_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0);
				float t = Mathf.Clamp01((float)(num7 / (double)railTrack.curve.length));
				Vector3 tangentAt = railTrack.curve.GetTangentAt(t);
				if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex + 1)
				{
					RailTrack railTrack6 = CurrentPath.Tracks[CurrentPathTrackIndex + 1];
					if (railTrack6 != null && railTrack6.curve != null)
					{
						Vector3 pointAt = railTrack.curve.GetPointAt(0f);
						Vector3 pointAt2 = railTrack.curve.GetPointAt(1f);
						Vector3 pointAt3 = railTrack6.curve.GetPointAt(0f);
						Vector3 pointAt4 = railTrack6.curve.GetPointAt(1f);
						float num8 = Mathf.Min(Vector3.Distance(pointAt2, pointAt3), Vector3.Distance(pointAt2, pointAt4));
						float num9 = Mathf.Min(Vector3.Distance(pointAt, pointAt3), Vector3.Distance(pointAt, pointAt4));
						TargetDirection = ((num8 <= num9) ? 1f : (-1f));
					}
				}
				else if (CurrentPath.Tracks != null && CurrentPathTrackIndex > 0 && CurrentPathTrackIndex == CurrentPath.Tracks.Count - 1)
				{
					RailTrack railTrack3 = CurrentPath.Tracks[CurrentPathTrackIndex - 1];
					if (railTrack3 != null && railTrack3.curve != null)
					{
						Vector3 pointAt = railTrack.curve.GetPointAt(0f);
						Vector3 pointAt2 = railTrack.curve.GetPointAt(1f);
						Vector3 pointAt5 = railTrack3.curve.GetPointAt(0f);
						Vector3 pointAt6 = railTrack3.curve.GetPointAt(1f);
						float num10 = Mathf.Min(Vector3.Distance(pointAt, pointAt5), Vector3.Distance(pointAt, pointAt6));
						float num11 = Mathf.Min(Vector3.Distance(pointAt2, pointAt5), Vector3.Distance(pointAt2, pointAt6));
						TargetDirection = ((num10 <= num11) ? 1f : (-1f));
					}
				}
				else
				{
					Vector3 lhs = ((_trainCar != null) ? _trainCar.transform.forward : Vector3.forward);
					TargetDirection = ((Vector3.Dot(lhs, tangentAt) >= 0f) ? 1f : (-1f));
				}
				Vector3 rhs = tangentAt * TargetDirection;
				Vector3 vector = ((_trainCar != null) ? _trainCar.transform.forward : Vector3.forward);
				_desiredReverser = ((Vector3.Dot(vector, rhs) >= 0f) ? 1f : (-1f));
				if (_ambientLockedReverser == 0f)
				{
					_ambientLockedReverser = _desiredReverser;
				}
				if (IsIntentionalReverseActive)
				{
					_commandedReverser = ((_ambientLockedReverser != 0f) ? (0f - _ambientLockedReverser) : (0f - TargetDirection));
					_desiredReverser = _commandedReverser;
				}
				else
				{
					_desiredReverser = _ambientLockedReverser;
				}
				Vector3 lhs2 = vector * _ambientLockedReverser;
				TargetDirection = ((Vector3.Dot(lhs2, tangentAt) >= 0f) ? 1f : (-1f));
			}
			double num12;
			if (CurrentPath.Tracks != null && CurrentPath.Tracks.Count > CurrentPathTrackIndex)
			{
				num12 = 0.0;
				if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
				{
					num12 = _trainCar.FrontBogie.traveller.Span;
				}
				else if (_trainCar != null && _trainCar.RearBogie != null && _trainCar.RearBogie.traveller != null)
				{
					num12 = _trainCar.RearBogie.traveller.Span;
				}
				float num13 = 0f;
				if (railTrack != null && railTrack.curve != null)
				{
					float length = railTrack.curve.length;
					num13 = ((TargetDirection >= 0f) ? Mathf.Max(0f, length - (float)num12) : Mathf.Max(0f, (float)num12));
				}
				for (int k = CurrentPathTrackIndex + 1; k < CurrentPath.Tracks.Count; k++)
				{
					RailTrack railTrack7 = CurrentPath.Tracks[k];
					if (railTrack7 != null && railTrack7.curve != null)
					{
						num13 += railTrack7.curve.length;
					}
				}
				if (IsStationDestination || IsTerminusDestination)
				{
					DistanceToDestination = Mathf.Max(0f, num13);
				}
				else
				{
					DistanceToDestination = float.PositiveInfinity;
				}
			}
			if (IsStationDestination && (DistanceToDestination < 2000f || _isWaitingOutsideStationThroat))
			{
				ApproachDestination(dt);
			}
			if (CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1 || !(_trainCar != null) || _reverseProtocolStage != 0 || _offRouteReverseStage != 0)
			{
				return;
			}
			Vector3 position = _trainCar.transform.position;
			Bogie bogie = null;
			if (_trainCar.trainset != null && _trainCar.trainset.cars != null && _trainCar.trainset.cars.Count > 0)
			{
				List<TrainCar> cars = _trainCar.trainset.cars;
				TrainCar trainCar = null;
				if (cars.Count > 1)
				{
					int num14 = cars.IndexOf(_trainCar);
					if (num14 == 0)
					{
						trainCar = cars[cars.Count - 1];
					}
					else if (num14 == cars.Count - 1)
					{
						trainCar = cars[0];
					}
					else
					{
						TrainCar trainCar2 = cars[0];
						TrainCar trainCar3 = cars[cars.Count - 1];
						float num15 = ((trainCar2 != null) ? Vector3.Distance(position, trainCar2.transform.position) : 0f);
						float num16 = ((trainCar3 != null) ? Vector3.Distance(position, trainCar3.transform.position) : 0f);
						trainCar = ((num15 > num16) ? trainCar2 : trainCar3);
					}
				}
				else
				{
					trainCar = cars[0];
				}
				if (trainCar != null)
				{
					if (trainCar.FrontBogie != null && trainCar.RearBogie != null)
					{
						float num17 = Vector3.Distance(position, trainCar.FrontBogie.transform.position);
						float num18 = Vector3.Distance(position, trainCar.RearBogie.transform.position);
						bogie = ((num17 > num18) ? trainCar.FrontBogie : trainCar.RearBogie);
					}
					else
					{
						bogie = trainCar.RearBogie ?? trainCar.FrontBogie;
					}
				}
			}
			if (bogie == null)
			{
				bogie = _trainCar.RearBogie ?? _trainCar.FrontBogie;
			}
			num12 = 0.0;
			if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
			{
				num12 = _trainCar.FrontBogie.traveller.Span;
			}
			float num19 = ((railTrack != null && railTrack.curve != null) ? railTrack.curve.length : 0f);
			float num20 = ((TargetDirection >= 0f) ? Mathf.Max(0f, num19 - (float)num12) : Mathf.Max(0f, (float)num12));
			float num21 = num20;
			_switchHoldDistance = float.PositiveInfinity;
			bool flag8 = State == EngineState.StationHold || State == EngineState.TerminusStop;
			bool flag9 = CurrentSpeedKmh < 1f && (flag8 || State == EngineState.Idle || (State == EngineState.Braking && TargetSpeedKmh <= 1f));
			if ((State == EngineState.TerminusStop || (State == EngineState.StationHold && DwellTimeRemaining > 10f)) && JunctionController.Instance != null)
			{
				// CRITICAL INVARIANT [WRK-04]: ReleaseAllLocksFor
				JunctionController.Instance.ReleaseAllLocksFor(this);
			}
			ReleasePassedAndConflictingJunctions();
			bool flag10 = Main.Settings != null && Main.Settings.RideAlongMode;
			// CRITICAL INVARIANT [AIE-17]: Resolve governing signal before distant ApproachingSignal so Vr 0 never masks Hp 0
			Signals.Game.Signal signal5 = null;
			bool flag11 = false;
			Signals.Game.Signal candidateSig = (GoverningSignal != null && DistanceToGoverningSignal < 2000f)
				? GoverningSignal
				: ((ApproachingSignal != null && DistanceToSignal < 2000f) ? ApproachingSignal : null);
			if (candidateSig != null)
			{
				signal5 = ((candidateSig.Parent != null) ? candidateSig.Parent : candidateSig);
				if (signal5 != null && signal5.IsOn && signal5.CurrentAspect != null)
				{
					flag11 = signal5.CurrentAspect.DisallowPassing;
					if (!flag11 && CurrentSpeedKmh >= 1.5f)
					{
						_stoppedSwitchAlignCooldown = 0f;
					}
				}
			}
			if ((signal5 == null || !SignalRegistry.IsGoverningSignal(signal5)) && _upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0 && _upcomingSignalBlocks[0].ExitSignal != null && _upcomingSignalBlocks[0].DistanceToExit < 2000f)
			{
				signal5 = _upcomingSignalBlocks[0].ExitSignal;
				if (signal5 != null && signal5.IsOn && signal5.CurrentAspect != null)
				{
					flag11 = signal5.CurrentAspect.DisallowPassing;
					if (!flag11 && CurrentSpeedKmh >= 1.5f)
					{
						_stoppedSwitchAlignCooldown = 0f;
					}
				}
			}
			signal3 = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
			if (_isShuntingMode && signal5 != null && signal5 == signal3)
			{
				flag11 = false;
			}
			int num22 = (flag11 ? Math.Min(_upcomingTracks.Count, 40) : Math.Min(_upcomingTracks.Count, 30));
			float num23 = (flag11 ? 2500f : 1500f);
			var governingSig = signal5;
			// CRITICAL INVARIANT [AIE-08]: Entry Signal lookahead exemption
			if ((_isHoldingForCorridor || _isHoldingInMultiTrack) && (governingSig == null || !AITraffic.Navigation.SignalRegistry.IsEntrySignal(governingSig)))
			{
				float num24 = (float.IsInfinity(_corridorHoldDistance) ? 0f : _corridorHoldDistance);
				if (num24 > 15f)
				{
					num23 = Mathf.Min(num23, num24 - 15f);
				}
			}
			if (!float.IsInfinity(DistanceToObstacle))
			{
				num23 = Mathf.Min(num23, Mathf.Max(0f, DistanceToObstacle - 25f));
			}
			bool flag12 = CurrentSpeedKmh < 1.5f && (flag11 || flag8);
			_seenJunctionsThisPass.Clear();
			int num25 = 0;
			for (int k = 1; k < _upcomingTracks.Count && k < num22 && !(num21 > num23) && !(num23 <= 0f); k++)
			{
				RailTrack railTrack8 = _upcomingTracks[k - 1];
				RailTrack railTrack9 = _upcomingTracks[k];
				Junction junction3;
				byte requiredBranch3;
				if (railTrack8 != null && railTrack9 != null && railTrack8 != railTrack9 && SignalRegistry.TryGetJunctionBetweenTracks(railTrack8, railTrack9, out junction3, out requiredBranch3))
				{
					if (_isShuntingMode && _isSelfBlockedShunting && num25 >= 1)
					{
						if (railTrack9 != null && railTrack9.curve != null)
						{
							num21 += railTrack9.curve.length;
						}
						continue;
					}
					if (!_seenJunctionsThisPass.Add(junction3))
					{
						if (railTrack9 != null && railTrack9.curve != null)
						{
							num21 += railTrack9.curve.length;
						}
						continue;
					}
					float num26 = Vector3.Distance(position, junction3.position);
					float num27 = num21;
					bool flag13 = false;
					bool flag14 = false;
					if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
					{
						SignalBlockInfo signalBlockInfo = _upcomingSignalBlocks[0];
						if (signalBlockInfo != null && signalBlockInfo.Switches != null)
						{
							for (int l = 0; l < signalBlockInfo.Switches.Count; l++)
							{
								if (signalBlockInfo.Switches[l].Junction == junction3)
								{
									flag14 = true;
									break;
								}
							}
						}
						if (!flag10)
						{
							for (int m = 1; m < _upcomingSignalBlocks.Count; m++)
							{
								SignalBlockInfo signalBlockInfo2 = _upcomingSignalBlocks[m];
								if (signalBlockInfo2 != null && signalBlockInfo2.EntrySignal != null && SignalRegistry.IsEntrySignal(signalBlockInfo2.EntrySignal))
								{
									continue;
								}
								if (signalBlockInfo2 != null && signalBlockInfo2.IsPlayerOccupied && signalBlockInfo2.Switches != null)
								{
									for (int l = 0; l < signalBlockInfo2.Switches.Count; l++)
									{
										if (signalBlockInfo2.Switches[l].Junction == junction3)
										{
											flag13 = true;
											break;
										}
									}
								}
								if (flag13)
								{
									break;
								}
							}
						}
					}
					bool flag15 = !flag10 && SignalRegistry.IsJunctionOccupiedByPlayer(junction3, (_trainCar != null) ? _trainCar.trainset : null);
					bool flag16 = !flag10 && SignalRegistry.IsJunctionReservedByPlayerSignal(junction3);
					bool flag17 = ShouldProtectJunctionLock(junction3);
					Trainset playerTrainset;
					Vector3 playerPosition;
					float playerSpeedKmh;
					bool flag18 = SignalRegistry.TryGetPlayerTrainInfo(out playerTrainset, out playerPosition, out playerSpeedKmh) && playerSpeedKmh >= 1f;
					if (!flag10 && !flag17 && (flag13 || flag16 || (flag15 && flag18)))
					{
						JunctionController.Instance.ReleaseJunction(junction3, this);
						if (railTrack9 != null && railTrack9.curve != null)
						{
							num21 += railTrack9.curve.length;
						}
						if (num21 > num23 || k >= num22)
						{
							break;
						}
						continue;
					}
					bool flag19 = CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f;
					flag12 = CurrentSpeedKmh < 1.5f && (flag11 || flag8);
					bool flag20 = junction3.selectedBranch == requiredBranch3;
					bool flag21 = !flag19 && State != EngineState.TerminusStop && (State != EngineState.StationHold || DwellTimeRemaining <= 10f);
					bool flag22 = CurrentSpeedKmh < 1.5f;
					if (_stoppedSwitchAlignCooldown > 0f)
					{
						flag21 = false;
					}
					if ((flag12 || flag22) && !flag20 && flag21)
					{
						TrainCar occupyingCar;
						bool flag23 = JunctionController.IsJunctionPhysicallyOccupied(junction3, out occupyingCar, (_trainCar != null) ? _trainCar.trainset : null);
						bool flag24 = flag15 && flag18;
						bool flag25 = flag16;
						if (flag23 || flag24 || flag25)
						{
							_stoppedSwitchAlignCooldown = 30f;
							flag21 = false;
						}
					}
					bool flag26 = false;
					if (!flag20 && flag21)
					{
						int selectedBranch = junction3.selectedBranch;
						flag20 = JunctionController.Instance.RequestJunctionAlignment(junction3, requiredBranch3, this, false);
						if (flag20 && junction3.selectedBranch != selectedBranch)
						{
							flag26 = true;
						}
						else if (!flag20 && _stoppedSwitchAlignCooldown <= 0f)
						{
							// CRITICAL INVARIANT [JNC-09]: Enforce switch alignment retry cooldown when denied (3s moving, 5s stationary) to prevent 0.4s crossover lock thrashing
							_stoppedSwitchAlignCooldown = ((CurrentSpeedKmh >= 1.5f) ? 3f : 5f);
						}
					}
					if (_isShuntingMode && _isSelfBlockedShunting)
					{
						num25++;
					}
					if (flag26 && flag11 && signal5 != null)
					{
						DebouncedWakeAndForceUpdateSignal(signal5);
						if (signal5.IsOn && signal5.CurrentAspect != null)
						{
							flag11 = signal5.CurrentAspect.DisallowPassing;
							if (!flag11)
							{
								_stoppedSwitchAlignCooldown = ((CurrentSpeedKmh >= 1.5f) ? 0f : 5f);
								_switchHoldDistance = float.PositiveInfinity;
								num22 = Math.Min(num22, Math.Max(k + 3, 20));
								num23 = Mathf.Min(num23, Mathf.Max(num21 + 300f, 1200f));
							}
						}
					}
					if (!flag20 && num27 < 600f)
					{
						float routeDist = num27;
			// CRITICAL INVARIANT [AIE-10]: Unified 35m Unaligned Switch Stop Boundary
			float b = Mathf.Max(0.0f, routeDist - 35.0f);
						if (CurrentSpeedKmh < 1.5f && num27 <= 45f)
						{
							b = 0f;
						}
						_switchHoldDistance = Mathf.Min(_switchHoldDistance, b);
					}
					if (_isShuntingMode && num27 < 300f)
					{
						bool flag27 = !flag20;
						if (!flag27 && railTrack9 != null)
						{
							object holder;
							if (RailGraph.Instance.IsTrackOccupied(railTrack9, null, _trainCar.trainset) || SignalRegistry.IsTrackOccupiedByPlayer(railTrack9, _trainCar.trainset) || SignalRegistry.IsTrackReservedByPlayerSignal(railTrack9))
							{
								flag27 = true;
							}
							else if (RailGraph.Instance.TryGetTrackReservationHolder(railTrack9, this, out holder) && !object.ReferenceEquals(holder, this))
							{
								flag27 = true;
							}
						}
						if (flag27)
						{
							float b2 = Mathf.Max(0f, num27 - 50f);
							if (CurrentSpeedKmh < 1.5f && num27 <= 60f)
							{
								b2 = 0f;
							}
							_switchHoldDistance = Mathf.Min(_switchHoldDistance, b2);
						}
					}
					bool flag28 = !flag11 && !flag8 && TargetSpeedKmh > 0f && (_commandedThrottle > 0.05f || State == EngineState.Accelerating || CurrentSpeedKmh >= 0.5f);
					bool flag29 = CurrentSpeedKmh >= 0.5f && !flag11 && !flag8;
					bool flag30 = _isShuntingMode && _isSelfBlockedShunting && (CurrentSpeedKmh >= 0.5f || _commandedThrottle > 0.05f) && !flag8;
					// CRITICAL INVARIANT [AIE-19]: Lock aligned Block-0 / protected throat switches while waiting at Hp 0 when not holding/yielding
					bool flagRedBlock0Lock = flag11 && !flag8 && !flag19 && !_isHoldingForCorridor && !_isHoldingInMultiTrack && _playerYieldWaitTimer <= 0f && flag20 && (flag14 || flag17);
					bool flag31 = flag29 || flag28 || flag30 || flagRedBlock0Lock;
					bool flag32 = !flag11 && !flag8 && TargetSpeedKmh > 0f && (num27 < 50f || num26 < 50f);
					if ((flag31 || flag32) && (flag14 || flag17 || flag30 || num27 < 150f || num26 < 150f) && (num27 < 150f || num26 < 150f || flag14))
					{
						if (!flag19)
						{
							JunctionController.Instance.TryLockJunction(junction3, this, 60f);
							if (!flag11 && (num27 < 40f || num26 < 40f) && bogie != null)
							{
								JunctionController.Instance.RegisterTrainPassing(this, junction3, bogie, railTrack9, 90f);
							}
						}
						else
						{
							JunctionController.Instance.ReleaseJunction(junction3, this);
						}
					}
				}
				if (railTrack9 != null && railTrack9.curve != null)
				{
					num21 += railTrack9.curve.length;
				}
				if (num21 > num23 || k >= num22)
				{
					break;
				}
			}
			// CRITICAL INVARIANT [AIE-19]: Pre-gate signal5 reservation with !flag33 && _playerYieldWaitTimer <= 0f to prevent same-tick reserve-then-purge storms, and enforce 5s retry cooldown per [AIE-08]
			bool flag33PreGate = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f) || _isHoldingForCorridor || _isHoldingInMultiTrack;
			if (flag12 && flag11 && _stoppedSwitchAlignCooldown <= 0f && !flag33PreGate && _playerYieldWaitTimer <= 0f)
			{
				if (signal5 != null && SignalRegistry.IsEntrySignal(signal5))
				{
					HashSet<Signals.Game.Signal> hashSet = new HashSet<Signals.Game.Signal>();
					List<Signals.Game.Signal> list = null;
					foreach (Signals.Game.Signal reservedDVSignal in _reservedDVSignals)
					{
						Signals.Game.Signal signal6 = ((reservedDVSignal != null && reservedDVSignal.Parent != null) ? reservedDVSignal.Parent : reservedDVSignal);
						Signals.Game.Signal signal7 = ((signal5 != null && signal5.Parent != null) ? signal5.Parent : signal5);
						if (signal6 != null && signal6 != signal7)
						{
							if (list == null)
							{
								list = new List<Signals.Game.Signal>();
							}
							list.Add(signal6);
						}
					}
					if (list != null)
					{
						for (int l = 0; l < list.Count; l++)
						{
							Signals.Game.Signal signal8 = list[l];
							SignalRegistry.ClearDVSignalReservation(signal8);
							_reservedDVSignals.Remove(signal8);
							hashSet.Add(signal8);
						}
					}
					List<RailTrack> list2 = new List<RailTrack>();
					if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 1)
					{
						for (int m = 1; m < _upcomingSignalBlocks.Count; m++)
						{
							SignalBlockInfo signalBlockInfo3 = _upcomingSignalBlocks[m];
							if (signalBlockInfo3 == null || signalBlockInfo3.Tracks == null)
							{
								continue;
							}
							for (int i = 0; i < signalBlockInfo3.Tracks.Count; i++)
							{
								RailTrack railTrack10 = signalBlockInfo3.Tracks[i];
								if (railTrack10 != null && !list2.Contains(railTrack10))
								{
									list2.Add(railTrack10);
								}
							}
						}
					}
					if (_upcomingTracks != null)
					{
						int currentPathTrackIndex = Math.Min(1, _upcomingTracks.Count);
						int num28 = Math.Min(_upcomingTracks.Count, 40);
						for (int i = currentPathTrackIndex; i < num28; i++)
						{
							RailTrack railTrack10 = _upcomingTracks[i];
							if (railTrack10 != null && !list2.Contains(railTrack10))
							{
								list2.Add(railTrack10);
							}
						}
					}
					int num29 = SignalRegistry.PurgeDownstreamRouteReservations(list2, signal5, this, hashSet);
					if ((hashSet.Count > 0 || num29 > 0) && Main.ModEntry != null && Main.ModEntry.Logger != null)
					{
						Main.ModEntry.Logger.Log(string.Format("[AITraffic] Train '{0}' held at Entry Signal '{1}' with Hp 0. Purged downstream reservations ({2} signals). Retrying Entry Signal...", LocoId, SignalRegistry.GetSignalName(signal5), hashSet.Count + num29));
					}
					if (SignalRegistry.TryReserveDVSignal(signal5))
					{
						DebouncedWakeAndForceUpdateSignal(signal5);
						Signals.Game.Signal signal7 = ((signal5 != null && signal5.Parent != null) ? signal5.Parent : signal5);
						if (signal7 != null && !_reservedDVSignals.Contains(signal7))
						{
							_reservedDVSignals.Add(signal7);
						}
					}
					if (signal5.IsOn && signal5.CurrentAspect != null)
					{
						flag11 = signal5.CurrentAspect.DisallowPassing;
						if (!flag11)
						{
							_stoppedSwitchAlignCooldown = ((CurrentSpeedKmh >= 1.5f) ? 0f : 5f);
							_switchHoldDistance = float.PositiveInfinity;
						}
						else
						{
							_stoppedSwitchAlignCooldown = 5f;
						}
					}
					else
					{
						_stoppedSwitchAlignCooldown = 5f;
					}
				}
				else if (signal5 != null)
				{
					if (SignalRegistry.TryReserveDVSignal(signal5))
					{
						DebouncedWakeAndForceUpdateSignal(signal5);
						Signals.Game.Signal signal7 = ((signal5 != null && signal5.Parent != null) ? signal5.Parent : signal5);
						if (signal7 != null && !_reservedDVSignals.Contains(signal7))
						{
							_reservedDVSignals.Add(signal7);
						}
					}
					if (signal5.IsOn && signal5.CurrentAspect != null)
					{
						flag11 = signal5.CurrentAspect.DisallowPassing;
						if (!flag11)
						{
							_stoppedSwitchAlignCooldown = ((CurrentSpeedKmh >= 1.5f) ? 0f : 5f);
							_switchHoldDistance = float.PositiveInfinity;
						}
						else
						{
							_stoppedSwitchAlignCooldown = 5f;
						}
					}
					else
					{
						_stoppedSwitchAlignCooldown = 5f;
					}
				}
			}
			HashSet<RailTrack> desiredReservationsScratch = _desiredReservationsScratch;
			desiredReservationsScratch.Clear();
			for (int m = 0; m < _upcomingSignalBlocks.Count; m++)
			{
				SignalBlockInfo signalBlockInfo2 = _upcomingSignalBlocks[m];
				if (signalBlockInfo2 == null || signalBlockInfo2.Tracks == null)
				{
					continue;
				}
				for (int i = 0; i < signalBlockInfo2.Tracks.Count; i++)
				{
					RailTrack railTrack10 = signalBlockInfo2.Tracks[i];
					if (railTrack10 == null)
					{
						continue;
					}
					desiredReservationsScratch.Add(railTrack10);
					if (!_reservedTracks.Contains(railTrack10))
					{
						if (RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
						{
							RailGraph.Instance.TryReserveTrack(railTrack10, this);
						}
						_reservedTracks.Add(railTrack10);
					}
				}
			}
			HashSet<Signals.Game.Signal> desiredSignalReservationsScratch = _desiredSignalReservationsScratch;
			desiredSignalReservationsScratch.Clear();
			Signals.Game.Signal signal9 = null;
			bool flag33 = (CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f) || _isHoldingForCorridor || _isHoldingInMultiTrack;
			if (!flag33)
			{
				Signals.Game.Signal signal10 = null;
				if (signal5 != null)
				{
					signal10 = ((signal5.Parent != null) ? signal5.Parent : signal5);
				}
				else if (ApproachingSignal != null && DistanceToSignal < 2000f)
				{
					Signals.Game.Signal signal11 = ((ApproachingSignal.Parent != null) ? ApproachingSignal.Parent : ApproachingSignal);
					if (SignalRegistry.IsGoverningSignal(signal11))
					{
						signal10 = signal11;
					}
				}
				if (signal10 == null && GoverningSignal != null && DistanceToGoverningSignal < 2000f)
				{
					Signals.Game.Signal signal12 = ((GoverningSignal.Parent != null) ? GoverningSignal.Parent : GoverningSignal);
					if (SignalRegistry.IsGoverningSignal(signal12))
					{
						signal10 = signal12;
					}
				}
				if (signal10 == null && _upcomingSignalBlocks.Count > 0 && _upcomingSignalBlocks[0].ExitSignal != null && _upcomingSignalBlocks[0].DistanceToExit < 2000f)
				{
					signal10 = _upcomingSignalBlocks[0].ExitSignal;
				}
				if (_isShuntingMode && signal10 != null && signal10 == signal3)
				{
					signal10 = null;
				}
				if (signal10 != null && SignalRegistry.IsGoverningSignal(signal10))
				{
					Signals.Game.Signal signal13 = ((signal10.Parent != null) ? signal10.Parent : signal10);
					SignalBlockInfo signalBlockInfo4 = ((_upcomingSignalBlocks.Count > 0) ? _upcomingSignalBlocks[0] : null);
					SignalBlockInfo signalBlockInfo5 = ((signalBlockInfo4 != null && signalBlockInfo4.ExitSignal == signal13 && _upcomingSignalBlocks.Count > 1) ? _upcomingSignalBlocks[1] : null);
					bool flag34 = (signalBlockInfo4 == null || !signalBlockInfo4.IsPlayerOccupied) && (signalBlockInfo5 == null || !signalBlockInfo5.IsPlayerOccupied);
					if (HoldsSignalReservation(signal13) && flag34)
					{
						signal9 = signal13;
						desiredSignalReservationsScratch.Add(signal13);
					}
					else
					{
						bool flag35 = SignalRegistry.IsEntrySignal(signal13);
						bool flag36 = true;
						bool flag37 = true;
						if (signalBlockInfo4 != null)
						{
							flag36 = signalBlockInfo4.AreSwitchesAligned && signalBlockInfo4.IsClear;
							if (signalBlockInfo5 != null)
							{
								flag37 = ((!flag35) ? (signalBlockInfo5.AreSwitchesAligned && signalBlockInfo5.IsClear) : (signalBlockInfo5.AreSwitchesAligned && !signalBlockInfo5.IsPlayerOccupied));
							}
						}
						if (flag36 && flag37 && flag34)
						{
							signal9 = signal13;
						}
					}
				}
				else if (_upcomingSignalBlocks.Count > 0 && _upcomingSignalBlocks[0].ExitSignal != null)
				{
					SignalBlockInfo signalBlockInfo4 = _upcomingSignalBlocks[0];
					float distanceToExit = signalBlockInfo4.DistanceToExit;
					if (distanceToExit < 2000f && signalBlockInfo4.AreSwitchesAligned && signalBlockInfo4.IsClear)
					{
						Signals.Game.Signal exitSignal = signalBlockInfo4.ExitSignal;
						Signals.Game.Signal signal14 = ((exitSignal.Parent != null) ? exitSignal.Parent : exitSignal);
						SignalBlockInfo signalBlockInfo5 = ((_upcomingSignalBlocks.Count > 1 && _upcomingSignalBlocks[1].EntrySignal == signal14) ? _upcomingSignalBlocks[1] : null);
						bool flag38 = !signalBlockInfo4.IsPlayerOccupied && (signalBlockInfo5 == null || !signalBlockInfo5.IsPlayerOccupied);
						if (HoldsSignalReservation(signal14) && flag38)
						{
							signal9 = signal14;
							desiredSignalReservationsScratch.Add(signal14);
						}
						else
						{
							bool flag35 = SignalRegistry.IsEntrySignal(signal14);
							bool flag37 = true;
							if (signalBlockInfo5 != null)
							{
								flag37 = ((!flag35) ? (signalBlockInfo5.AreSwitchesAligned && signalBlockInfo5.IsClear) : (signalBlockInfo5.AreSwitchesAligned && !signalBlockInfo5.IsPlayerOccupied));
							}
							if (flag37 && flag38)
							{
								signal9 = signal14;
							}
						}
					}
				}
				if (signal5 != null && HoldsSignalReservation(signal5))
				{
					Signals.Game.Signal signal7 = ((signal5.Parent != null) ? signal5.Parent : signal5);
					SignalBlockInfo signalBlockInfo4 = ((_upcomingSignalBlocks.Count > 0) ? _upcomingSignalBlocks[0] : null);
					SignalBlockInfo signalBlockInfo5 = ((_upcomingSignalBlocks.Count > 1) ? _upcomingSignalBlocks[1] : null);
					if ((signalBlockInfo4 == null || !signalBlockInfo4.IsPlayerOccupied) && (signalBlockInfo5 == null || !signalBlockInfo5.IsPlayerOccupied))
					{
						desiredSignalReservationsScratch.Add(signal7);
					}
				}
				if (signal9 != null)
				{
					desiredSignalReservationsScratch.Add(signal9);
				}
			}
			List<Signals.Game.Signal> sigsToReleaseScratch = _sigsToReleaseScratch;
			sigsToReleaseScratch.Clear();
			foreach (Signals.Game.Signal reservedDVSignal2 in _reservedDVSignals)
			{
				if (!desiredSignalReservationsScratch.Contains(reservedDVSignal2))
				{
					sigsToReleaseScratch.Add(reservedDVSignal2);
				}
			}
			if (sigsToReleaseScratch.Count > 0)
			{
				for (int l = 0; l < sigsToReleaseScratch.Count; l++)
				{
					SignalRegistry.ClearDVSignalReservation(sigsToReleaseScratch[l]);
					_reservedDVSignals.Remove(sigsToReleaseScratch[l]);
				}
			}
			// CRITICAL INVARIANT [AIE-17] [AIE-18]: Release ApproachingSignal, governing signal5, and yielded main signal reservations when holding
			if (flag33 || _playerYieldWaitTimer > 0f)
			{
				if (ApproachingSignal != null)
				{
					SignalRegistry.ClearDVSignalReservation(ApproachingSignal);
					_reservedDVSignals.Remove(ApproachingSignal);
				}
				if (signal5 != null && signal5 != ApproachingSignal)
				{
					SignalRegistry.ClearDVSignalReservation(signal5);
					_reservedDVSignals.Remove(signal5);
				}
				if (_yieldGoverningSignal != null && _yieldGoverningSignal != ApproachingSignal && _yieldGoverningSignal != signal5)
				{
					SignalRegistry.ClearDVSignalReservation(_yieldGoverningSignal);
					_reservedDVSignals.Remove(_yieldGoverningSignal);
				}
			}
			if (_playerYieldWaitTimer <= 0f && signal9 != null && !_reservedDVSignals.Contains(signal9) && SignalRegistry.TryReserveDVSignal(signal9))
			{
				DebouncedWakeAndForceUpdateSignal(signal9);
				_reservedDVSignals.Add(signal9);
			}
			List<RailTrack> tracksToReleaseScratch = _tracksToReleaseScratch;
			tracksToReleaseScratch.Clear();
			foreach (RailTrack reservedTrack in _reservedTracks)
			{
				if (!desiredReservationsScratch.Contains(reservedTrack) && !_activeCorridorTracks.Contains(reservedTrack))
				{
					tracksToReleaseScratch.Add(reservedTrack);
				}
			}
			if (tracksToReleaseScratch.Count <= 0)
			{
				return;
			}
			for (int n = 0; n < tracksToReleaseScratch.Count; n++)
			{
				if (RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
				{
					RailGraph.Instance.ReleaseTrackReservation(tracksToReleaseScratch[n], this);
				}
				_reservedTracks.Remove(tracksToReleaseScratch[n]);
			}
		}

		private void ReleasePassedAndConflictingJunctions()
		{
			if (JunctionController.Instance == null)
			{
				return;
			}
			List<Junction> lockedJunctionsScratch = _lockedJunctionsScratch;
			JunctionController.Instance.GetLockedJunctionsFor(this, lockedJunctionsScratch);
			if (lockedJunctionsScratch.Count == 0)
			{
				return;
			}
			for (int i = 0; i < lockedJunctionsScratch.Count; i++)
			{
				Junction junction = lockedJunctionsScratch[i];
				TrainCar occupyingCar;
				if (junction == null || JunctionController.IsJunctionPhysicallyOccupied(junction, out occupyingCar, (_trainCar != null) ? _trainCar.trainset : null) || JunctionController.Instance.IsJunctionMonitoredForClearance(junction, this))
				{
					continue;
				}
				bool flag = false;
				if (_upcomingTracks != null && _upcomingTracks.Count > 1)
				{
					int num = Math.Min(_upcomingTracks.Count, 64);
					for (int j = 1; j < num; j++)
					{
						Junction junction2;
						byte requiredBranch;
						if (SignalRegistry.TryGetJunctionBetweenTracks(_upcomingTracks[j - 1], _upcomingTracks[j], out junction2, out requiredBranch) && junction2 == junction)
						{
							flag = true;
							break;
						}
					}
				}
				if (!flag)
				{
					Vector3 trailingCarPosition = GetTrailingCarPosition();
					Vector3 rhs = junction.position - trailingCarPosition;
					Vector3 lhs = ((_trainCar != null) ? (_trainCar.transform.forward * ((TargetDirection >= 0f) ? 1f : (-1f))) : Vector3.zero);
					if (Vector3.Dot(lhs, rhs) < -10f)
					{
						JunctionController.Instance.ReleaseJunction(junction, this);
					}
				}
				else
				{
					if (ShouldProtectJunctionLock(junction))
					{
						continue;
					}
					bool flag2 = SignalRegistry.IsJunctionReservedByPlayerSignal(junction);
					bool flag3 = SignalRegistry.IsJunctionOccupiedByPlayer(junction, (_trainCar != null) ? _trainCar.trainset : null);
					Trainset playerTrainset;
					Vector3 playerPosition;
					float playerSpeedKmh;
					bool flag4 = SignalRegistry.TryGetPlayerTrainInfo(out playerTrainset, out playerPosition, out playerSpeedKmh) && playerSpeedKmh >= 1f;
					bool flag5 = false;
					Signals.Game.Signal signal = ((ApproachingSignal != null) ? ApproachingSignal : GoverningSignal);
					if (signal == null && _upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
					{
						signal = _upcomingSignalBlocks[0].ExitSignal;
					}
					if (signal != null && (DistanceToSignal < 2000f || DistanceToGoverningSignal < 2000f))
					{
						Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
						if (signal2 != null && signal2.IsOn && signal2.CurrentAspect != null)
						{
							flag5 = signal2.CurrentAspect.DisallowPassing;
						}
					}
					if ((CurrentSpeedKmh < 1.5f && (flag5 || State == EngineState.StationHold || State == EngineState.TerminusStop)) || flag2 || (flag3 && flag4))
					{
						JunctionController.Instance.ReleaseJunction(junction, this);
					}
				}
			}
		}

		private void ApproachDestination(float dt)
		{
			if (!IsStationDestination || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count == 0)
			{
				return;
			}
			RailTrack railTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
			if (railTrack == null || (!TrafficScheduler.IsTrackOccupied(railTrack) && !RailGraph.Instance.IsTrackOccupied(railTrack, (_trainCar != null) ? _trainCar.trainset : null) && !SignalRegistry.IsTrackOccupiedByPlayer(railTrack, (_trainCar != null) ? _trainCar.trainset : null) && !_isWaitingOutsideStationThroat))
			{
				return;
			}
			if (!_isWaitingOutsideStationThroat && TargetSpeedKmh > 30f)
			{
				TargetSpeedKmh = 30f;
			}
			if (_isWaitingOutsideStationThroat)
			{
				_stationThroatPollTimer -= dt;
				if (_stationThroatPollTimer > 0f)
				{
					_stationThroatToastCooldown -= dt;
					if (_stationThroatToastCooldown <= 0f)
					{
						_stationThroatToastCooldown = 30f;
						string locoId = LocoId;
						string arg = DestinationStationName ?? "Destination";
						if (WorkerManager.Instance != null)
						{
							WorkerManager.Instance.ShowToast(string.Format("[AI Traffic] Train {0} waiting outside {1}: all arrival tracks occupied.", locoId, arg), "[AI Traffic]", 8f);
						}
					}
					return;
				}
			}
			StationController stationController = null;
			if (!string.IsNullOrEmpty(DestinationStationName))
			{
				stationController = TrafficScheduler.FindStation(DestinationStationName);
			}
			if (stationController == null && StationController.allStations != null)
			{
				for (int i = 0; i < StationController.allStations.Count; i++)
				{
					StationController stationController2 = StationController.allStations[i];
					if (stationController2 != null && stationController2.AllStationTracks != null && stationController2.AllStationTracks.Contains(railTrack))
					{
						stationController = stationController2;
						break;
					}
				}
			}
			if (stationController == null || stationController.AllStationTracks == null || stationController.AllStationTracks.Count == 0)
			{
				return;
			}
			float num = ConsistLengthMeters + 25f;
			List<RailTrack> candidateTracksScratch = _candidateTracksScratch;
			candidateTracksScratch.Clear();
			if (stationController.transferInRailtracksGONames != null && stationController.transferInRailtracksGONames.Count > 0)
			{
				for (int j = 0; j < stationController.AllStationTracks.Count; j++)
				{
					RailTrack railTrack2 = stationController.AllStationTracks[j];
					if (railTrack2 != null && railTrack2.curve != null && stationController.transferInRailtracksGONames.Contains(railTrack2.name))
					{
						candidateTracksScratch.Add(railTrack2);
					}
				}
			}
			if (candidateTracksScratch.Count == 0 && stationController.storageRailtracksGONames != null)
			{
				for (int j = 0; j < stationController.AllStationTracks.Count; j++)
				{
					RailTrack railTrack2 = stationController.AllStationTracks[j];
					if (railTrack2 != null && railTrack2.curve != null && stationController.storageRailtracksGONames.Contains(railTrack2.name))
					{
						candidateTracksScratch.Add(railTrack2);
					}
				}
			}
			if (candidateTracksScratch.Count == 0)
			{
				candidateTracksScratch.AddRange(stationController.AllStationTracks);
			}
			RailTrack railTrack3 = CurrentTrack;
			if (railTrack3 == null && _trainCar != null)
			{
				if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
				{
					railTrack3 = _trainCar.FrontBogie.track;
				}
				else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
				{
					railTrack3 = _trainCar.RearBogie.track;
				}
			}
			RailTrack railTrack4 = null;
			RailPath railPath = null;
			PathfinderOptions pathfinderOptions = new PathfinderOptions();
			pathfinderOptions.Requester = this;
			pathfinderOptions.RequesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
			pathfinderOptions.PreferSpeedOverDistance = true;
			pathfinderOptions.AvoidOccupiedTracks = true;
			pathfinderOptions.StrictlyAvoidOccupied = true;
			pathfinderOptions.AvoidReservedTracks = true;
			pathfinderOptions.StrictlyAvoidReserved = false;
			pathfinderOptions.PreventPlayerOvertake = false;
			pathfinderOptions.MaxSearchDistance = 5000000f;
			PathfinderOptions options = pathfinderOptions;
			bool flag = TargetDirection >= 0f;
			if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
			{
				flag = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
			}
			for (int j = 0; j < candidateTracksScratch.Count; j++)
			{
				RailTrack railTrack5 = candidateTracksScratch[j];
				if (!(railTrack5 == null) && !(railTrack5.curve == null) && !(railTrack5.curve.length < num) && !TrafficScheduler.IsTrackOccupied(railTrack5) && !RailGraph.Instance.IsTrackOccupied(railTrack5, (_trainCar != null) ? _trainCar.trainset : null) && !SignalRegistry.IsTrackOccupiedByPlayer(railTrack5, (_trainCar != null) ? _trainCar.trainset : null) && railTrack3 != null)
				{
					string requestKey = BuildRerouteKey("DestSiding", railTrack3, railTrack, railTrack5, flag);
					RailPath path;
					if (!TryGetAsyncReroute(requestKey, railTrack3, railTrack5, flag, options, out path))
					{
						_reroutePending = true;
						return;
					}
					if (path != null && path.Tracks != null && path.Tracks.Count > 0)
					{
						railTrack4 = railTrack5;
						railPath = path;
						break;
					}
				}
			}
			_stationThroatPollTimer = 30f;
			if (railTrack4 != null && railPath != null)
			{
				CurrentPath = railPath;
				_upcomingTracks.Clear();
				int num2 = CurrentPath.Tracks.IndexOf(railTrack3);
				if (num2 >= 0)
				{
					CurrentPathTrackIndex = num2;
				}
				for (int j = CurrentPathTrackIndex; j < CurrentPath.Tracks.Count; j++)
				{
					_upcomingTracks.Add(CurrentPath.Tracks[j]);
				}
				DestinationTrackName = railTrack4.name;
				_isWaitingOutsideStationThroat = false;
				_isHoldingForCorridor = false;
				TargetSpeedKmh = 15f;
				if (Main.IsDebugLoggingEnabled)
				{
					Main.LogDebug(string.Format("[AITraffic] Train '{0}' repathed into unoccupied destination siding '{1}' at '{2}'.", LocoId, railTrack4.name, (stationController.stationInfo != null) ? stationController.stationInfo.YardID : "Station"));
				}
			}
			else
			{
				_isWaitingOutsideStationThroat = true;
				TargetSpeedKmh = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				// CRITICAL INVARIANT [WRK-04]: ReleaseAllLocksFor
				JunctionController.Instance.ReleaseAllLocksFor(this);
				_stationThroatToastCooldown = 30f;
				string locoId2 = LocoId;
				string arg2 = DestinationStationName ?? ((stationController.stationInfo != null) ? stationController.stationInfo.YardID : "Station");
				string message = string.Format("[AI Traffic] Train {0} waiting outside {1}: all arrival tracks occupied.", locoId2, arg2);
				if (WorkerManager.Instance != null)
				{
					WorkerManager.Instance.ShowToast(message, "[AI Traffic]", 8f);
				}
			}
		}

		// CRITICAL INVARIANT [AIE-15]: Head-On vs Same-Direction Obstacle Discrimination & Worker Reroute Prohibition
		private void ClassifyUpcomingObstacle(TrainCar obstCar, Vector3 ourDirAtObst, out bool isComingHeadOn, out bool isSameDirectionTraffic)
		{
			isComingHeadOn = false;
			isSameDirectionTraffic = false;
			if (obstCar == null)
			{
				return;
			}
			if (ourDirAtObst.sqrMagnitude < 0.001f && _trainCar != null)
			{
				float num = ((Mathf.Abs(_desiredReverser) > 0.01f) ? Mathf.Sign(_desiredReverser) : ((_ambientLockedReverser < 0f) ? (-1f) : 1f));
				ourDirAtObst = _trainCar.transform.forward * num;
			}
			if (ourDirAtObst.sqrMagnitude < 0.001f)
			{
				return;
			}
			Vector3 normalized = ourDirAtObst.normalized;
			Vector3 vector = ((obstCar.rb != null) ? obstCar.rb.velocity : (obstCar.transform.forward * obstCar.GetForwardSpeed()));
			float magnitude = vector.magnitude;
			if (magnitude >= 0.4f)
			{
				float num2 = Vector3.Dot(normalized, vector.normalized);
				if (num2 < -0.2f)
				{
					isComingHeadOn = true;
					return;
				}
				if (num2 > 0.2f)
				{
					isSameDirectionTraffic = true;
					return;
				}
			}
			if (!(TrafficManager.Instance != null) || TrafficManager.Instance.ActiveEngineers == null)
			{
				return;
			}
			List<AIEngineer> activeEngineers = TrafficManager.Instance.ActiveEngineers;
			for (int i = 0; i < activeEngineers.Count; i++)
			{
				AIEngineer aIEngineer = activeEngineers[i];
				if (aIEngineer == null || aIEngineer == this || aIEngineer._trainCar == null || (!(aIEngineer._trainCar == obstCar) && (aIEngineer._trainCar.trainset == null || obstCar.trainset != aIEngineer._trainCar.trainset) && (aIEngineer._registeredConsistCars == null || !aIEngineer._registeredConsistCars.Contains(obstCar))))
				{
					continue;
				}
				if (aIEngineer._trainCar != obstCar)
				{
					Vector3 vector2 = aIEngineer._trainCar.transform.position - obstCar.transform.position;
					if (vector2.sqrMagnitude > 1f)
					{
						float num3 = Vector3.Dot(normalized, vector2.normalized);
						if (num3 > 0.15f)
						{
							isSameDirectionTraffic = true;
							break;
						}
						if (num3 < -0.15f)
						{
							isComingHeadOn = true;
							break;
						}
					}
				}
				float num4 = ((Mathf.Abs(aIEngineer._desiredReverser) > 0.01f) ? Mathf.Sign(aIEngineer._desiredReverser) : ((aIEngineer._ambientLockedReverser < 0f) ? (-1f) : 1f));
				float num5 = Vector3.Dot(normalized, (aIEngineer._trainCar.transform.forward * num4).normalized);
				if (num5 > 0.2f)
				{
					isSameDirectionTraffic = true;
					break;
				}
				if (num5 < -0.2f)
				{
					isComingHeadOn = true;
					break;
				}
			}
		}

		// CRITICAL INVARIANT [AIE-15]: TryGetUpcomingDivergingSwitchDistance
		private bool TryGetUpcomingDivergingSwitchDistance(RailTrack curTrack, double curSpan, RailTrack blockedTrack, out float switchDistance)
		{
			switchDistance = float.PositiveInfinity;
			if (curTrack == null || blockedTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
			{
				return false;
			}
			int num = Mathf.Max(0, CurrentPathTrackIndex - 1);
			int num2 = ((num < CurrentPath.Tracks.Count) ? CurrentPath.Tracks.IndexOf(curTrack, num) : (-1));
			if (num2 < 0)
			{
				num2 = CurrentPath.Tracks.IndexOf(curTrack);
			}
			if (num2 < 0)
			{
				return false;
			}
			int num3 = CurrentPath.Tracks.IndexOf(blockedTrack, num2);
			if (num3 <= num2)
			{
				return false;
			}
			double num4 = ((curTrack.curve != null) ? ((double)curTrack.curve.length) : 100.0);
			float num5 = ((TargetDirection >= 0f) ? Mathf.Max(0f, (float)(num4 - curSpan)) : Mathf.Max(0f, (float)curSpan));
			for (int i = num2; i < num3; i++)
			{
				RailTrack railTrack = CurrentPath.Tracks[i];
				RailTrack railTrack2 = CurrentPath.Tracks[i + 1];
				if (railTrack == null || railTrack2 == null)
				{
					break;
				}
				if (i > num2)
				{
					num5 += ((railTrack.curve != null) ? railTrack.curve.length : 100f);
				}
				if ((num5 < 15f && CurrentSpeedKmh >= 1.5f) || num5 < 7.5f)
				{
					continue;
				}
				Junction junction;
				byte requiredBranch;
				if (SignalRegistry.TryGetJunctionBetweenTracks(railTrack, railTrack2, out junction, out requiredBranch) && junction != null && junction.inBranch != null && junction.inBranch.track == railTrack && junction.outBranches != null && junction.outBranches.Count > 1)
				{
					bool flag = false;
					for (int j = 0; j < junction.outBranches.Count; j++)
					{
						Junction.Branch branch = junction.outBranches[j];
						if (branch != null && !(branch.track == null) && !(branch.track == railTrack2) && (RailGraph.Instance == null || !RailGraph.Instance.IsTrackOccupied(branch.track, null, (_trainCar != null) ? _trainCar.trainset : null)))
						{
							flag = true;
							break;
						}
					}
					if (flag)
					{
						switchDistance = num5;
						return true;
					}
				}
				if (num5 > 1500f)
				{
					break;
				}
			}
			return false;
		}

		private static string BuildRerouteKey(string kind, RailTrack startTrack, RailTrack excludedTrack, RailTrack destTrack, bool forward)
		{
			return kind + "|" + ((startTrack != null) ? startTrack.GetInstanceID().ToString() : "0") + "|" + ((excludedTrack != null) ? excludedTrack.GetInstanceID().ToString() : "0") + "|" + ((destTrack != null) ? destTrack.GetInstanceID().ToString() : "0") + "|" + (forward ? "F" : "R");
		}

		// CRITICAL INVARIANT [AIE-16]: Background Reroute Requests for Stationary Triggers
		private bool TryGetAsyncReroute(string requestKey, RailTrack startTrack, RailTrack destTrack, bool startForward, PathfinderOptions options, out RailPath path)
		{
			path = null;
			float value;
			if (_consumedRerouteKeys.TryGetValue(requestKey, out value))
			{
				if (Time.time - value < 10f)
				{
					return true;
				}
				_consumedRerouteKeys.Remove(requestKey);
			}
			if (_rerouteTask != null)
			{
				if (!_rerouteTask.IsCompleted)
				{
					return false;
				}
				if (_rerouteTaskKey == requestKey)
				{
					if (_rerouteTask.Status == TaskStatus.RanToCompletion)
					{
						path = _rerouteTask.Result;
					}
					else if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AIEngineer] Async reroute '{0}' ended with status {1}.", requestKey, _rerouteTask.Status));
					}
					_rerouteTask = null;
					_rerouteTaskKey = null;
					MarkRerouteKeyConsumed(requestKey);
					return true;
				}
				_rerouteTask = null;
				_rerouteTaskKey = null;
			}
			_rerouteTask = Pathfinder.FindPathAsync(startTrack, destTrack, startForward, options);
			_rerouteTaskKey = requestKey;
			return false;
		}

		private void MarkRerouteKeyConsumed(string requestKey)
		{
			if (_consumedRerouteKeys.Count >= 64)
			{
				List<string> list = new List<string>();
				foreach (KeyValuePair<string, float> consumedRerouteKey in _consumedRerouteKeys)
				{
					if (Time.time - consumedRerouteKey.Value >= 10f)
					{
						list.Add(consumedRerouteKey.Key);
					}
				}
				for (int i = 0; i < list.Count; i++)
				{
					_consumedRerouteKeys.Remove(list[i]);
				}
			}
			_consumedRerouteKeys[requestKey] = Time.time;
		}

		private bool TryDynamicObstacleReroute(RailTrack curTrack, RailTrack blockedTrack)
		{
			if (IsWorkerDriven) return false;
			if (curTrack == null || blockedTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
			{
				return false;
			}
			if (_trainCar == null)
			{
				return false;
			}
			RailTrack railTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
			if (railTrack == null)
			{
				return false;
			}
			if (blockedTrack == railTrack)
			{
				ApproachDestination(0f);
				return !_isWaitingOutsideStationThroat;
			}
			int num = Mathf.Max(0, CurrentPathTrackIndex);
			int num2 = ((num < CurrentPath.Tracks.Count) ? CurrentPath.Tracks.IndexOf(blockedTrack, num) : (-1));
			if (num2 <= CurrentPathTrackIndex)
			{
				return false;
			}
			PathfinderOptions pathfinderOptions = new PathfinderOptions();
			pathfinderOptions.Requester = this;
			pathfinderOptions.RequesterTrainset = _trainCar.trainset;
			pathfinderOptions.PreferSpeedOverDistance = true;
			pathfinderOptions.AvoidOccupiedTracks = true;
			pathfinderOptions.StrictlyAvoidOccupied = true;
			pathfinderOptions.AvoidReservedTracks = true;
			pathfinderOptions.StrictlyAvoidReserved = false;
			pathfinderOptions.PreventPlayerOvertake = false;
			pathfinderOptions.MaxSearchDistance = 5000000f;
			pathfinderOptions.ExcludedTracks = new HashSet<RailTrack> { blockedTrack };
			PathfinderOptions options = pathfinderOptions;
			bool flag = TargetDirection >= 0f;
			if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
			{
				flag = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
				if (_ambientLockedReverser < 0f)
				{
					flag = !flag;
				}
			}
			RailPath path;
			if (!TryGetAsyncReroute(BuildRerouteKey("OBS", curTrack, blockedTrack, railTrack, flag), curTrack, railTrack, flag, options, out path))
			{
				_reroutePending = true;
				return false;
			}
			if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count < 2)
			{
				return false;
			}
			if (path.Tracks.Contains(blockedTrack))
			{
				return false;
			}
			bool flag2 = false;
			int num3 = ((CurrentPathTrackIndex >= 0 && CurrentPathTrackIndex < CurrentPath.Tracks.Count && CurrentPath.Tracks[CurrentPathTrackIndex] == curTrack) ? CurrentPathTrackIndex : CurrentPath.Tracks.IndexOf(curTrack));
			if (num3 > 0)
			{
				RailTrack railTrack2 = CurrentPath.Tracks[num3 - 1];
				RailTrack railTrack3 = path.Tracks[1];
				if (railTrack2 != null && curTrack.curve != null && railTrack3 != null)
				{
					RailEdge edge = RailGraph.Instance.GetEdge(railTrack2);
					RailEdge edge2 = RailGraph.Instance.GetEdge(curTrack);
					RailEdge edge3 = RailGraph.Instance.GetEdge(railTrack3);
					if (edge != null && edge2 != null && edge3 != null)
					{
						RailNode connectingNode = RailGraph.Instance.GetConnectingNode(edge, edge2);
						RailNode connectingNode2 = RailGraph.Instance.GetConnectingNode(edge2, edge3);
						if (connectingNode != null && connectingNode2 != null)
						{
							if (connectingNode == connectingNode2)
							{
								flag2 = true;
							}
							else
							{
								Vector3 incomingVector = RailGraph.GetIncomingVector(connectingNode, edge);
								Vector3 departingVector = RailGraph.GetDepartingVector(connectingNode, edge2);
								if (Vector3.Dot(incomingVector, departingVector) < -0.3f)
								{
									flag2 = true;
								}
							}
						}
					}
				}
			}
			if (!flag2)
			{
				for (int i = 0; i < path.Tracks.Count - 1; i++)
				{
					RailTrack railTrack4 = path.Tracks[i];
					RailTrack railTrack5 = path.Tracks[i + 1];
					if (!(railTrack4 != null) || !(railTrack4.curve != null) || !(railTrack5 != null) || !(railTrack5.curve != null))
					{
						continue;
					}
					RailEdge edge4 = RailGraph.Instance.GetEdge(railTrack4);
					RailEdge edge5 = RailGraph.Instance.GetEdge(railTrack5);
					if (edge4 == null || edge5 == null)
					{
						continue;
					}
					RailNode connectingNode3 = RailGraph.Instance.GetConnectingNode(edge4, edge5);
					if (connectingNode3 != null)
					{
						Vector3 incomingVector = RailGraph.GetIncomingVector(connectingNode3, edge4);
						Vector3 departingVector = RailGraph.GetDepartingVector(connectingNode3, edge5);
						if (Vector3.Dot(incomingVector, departingVector) < -0.3f)
						{
							flag2 = true;
							break;
						}
					}
				}
			}
			if (flag2)
			{
				return false;
			}
			CurrentPath = path;
			CurrentPathTrackIndex = 0;
			_upcomingTracks.Clear();
			for (int j = 0; j < CurrentPath.Tracks.Count; j++)
			{
				_upcomingTracks.Add(CurrentPath.Tracks[j]);
			}
			_obstacleTrack = null;
			DistanceToObstacle = float.PositiveInfinity;
			_obstacleWaitTimer = 0f;
			_hasPendingDivergingSwitchHold = false;
			_pendingDivergingSwitchDistance = float.PositiveInfinity;
			if (Main.ModEntry != null && Main.ModEntry.Logger != null)
			{
				Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Detour: Route ahead was blocked by cars on '{0}'. Successfully re-routed via alternative clear path ({1} tracks) to destination '{2}'.", blockedTrack.name, CurrentPath.Tracks.Count, DestinationStationName ?? railTrack.name));
			}
			return true;
		}

		private bool TryDynamicRedSignalReroute(RailTrack curTrack)
		{
			if (IsWorkerDriven) return false;
			if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
			{
				return false;
			}
			int currentPathTrackIndex = CurrentPathTrackIndex;
			for (int i = currentPathTrackIndex; i < CurrentPath.Tracks.Count - 1 && i < currentPathTrackIndex + 6; i++)
			{
				RailTrack railTrack = CurrentPath.Tracks[i];
				RailTrack railTrack2 = CurrentPath.Tracks[i + 1];
				Junction junction;
				byte requiredBranch;
				if (railTrack == null || railTrack2 == null || !SignalRegistry.TryGetJunctionBetweenTracks(railTrack, railTrack2, out junction, out requiredBranch) || !(junction != null) || junction.outBranches == null || junction.outBranches.Count <= 1)
				{
					continue;
				}
				for (int j = 0; j < junction.outBranches.Count; j++)
				{
					Junction.Branch branch = junction.outBranches[j];
					if (branch == null || branch.track == null || branch.track == railTrack2)
					{
						continue;
					}
					RailEdge railEdge = ((RailGraph.Instance != null) ? RailGraph.Instance.GetEdge(branch.track) : null);
					string text = branch.track.name ?? "";
					bool flag = (railEdge != null && railEdge.IsYardTrack) || text.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) || text.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) || text.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("-L]", StringComparison.OrdinalIgnoreCase) >= 0;
					bool flag2 = (railEdge != null && railEdge.IsDoubleTrackMainline) || text.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0;
					if ((flag && !flag2) || (RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(branch.track, null, (_trainCar != null) ? _trainCar.trainset : null)))
					{
						continue;
					}
					bool flag3 = TryDynamicObstacleReroute(curTrack, railTrack2);
					if (!flag3 && _reroutePending)
					{
						return false;
					}
					if (flag3)
					{
						if (Main.ModEntry != null && Main.ModEntry.Logger != null)
						{
							Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Red Signal Detour: Train '{0}' stopped at Hp 0 signal before switch '{1}'. Successfully rerouted via clear alternative branch to destination.", LocoId, junction.name));
						}
						return true;
					}
				}
			}
			return false;
		}

		private bool TryDynamicShuntingDetour(RailTrack curTrack)
		{
			if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
			{
				return false;
			}
			if (_trainCar == null)
			{
				return false;
			}
			RailTrack railTrack = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
			if (railTrack == null)
			{
				return false;
			}
			int currentPathTrackIndex = CurrentPathTrackIndex;
			int num = Mathf.Min(CurrentPath.Tracks.Count - 1, currentPathTrackIndex + 6);
			for (int i = currentPathTrackIndex; i < num; i++)
			{
				RailTrack railTrack2 = CurrentPath.Tracks[i];
				RailTrack railTrack3 = CurrentPath.Tracks[i + 1];
				Junction junction;
				byte requiredBranch;
				if (railTrack2 == null || railTrack3 == null || !SignalRegistry.TryGetJunctionBetweenTracks(railTrack2, railTrack3, out junction, out requiredBranch) || junction == null || junction.outBranches == null || junction.outBranches.Count <= 1)
				{
					continue;
				}
				PathfinderOptions pathfinderOptions = new PathfinderOptions();
				pathfinderOptions.Requester = this;
				pathfinderOptions.RequesterTrainset = _trainCar.trainset;
				pathfinderOptions.PreferSpeedOverDistance = true;
				pathfinderOptions.AvoidOccupiedTracks = true;
				pathfinderOptions.StrictlyAvoidOccupied = true;
				pathfinderOptions.AvoidReservedTracks = true;
				pathfinderOptions.StrictlyAvoidReserved = false;
				pathfinderOptions.PreventPlayerOvertake = false;
				pathfinderOptions.AllowShuntingDetour = true;
				pathfinderOptions.MaxSearchDistance = 5000000f;
				pathfinderOptions.ExcludedTracks = new HashSet<RailTrack> { railTrack3 };
				PathfinderOptions options = pathfinderOptions;
				bool flag = TargetDirection >= 0f;
				if (_trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
				{
					flag = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
				}
				RailPath path;
				if (!TryGetAsyncReroute(BuildRerouteKey("SHU", curTrack, railTrack3, railTrack, flag), curTrack, railTrack, flag, options, out path))
				{
					_reroutePending = true;
					return false;
				}
				if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count < 2 || path.Tracks.Contains(railTrack3))
				{
					continue;
				}
				bool flag2 = false;
				for (int j = 0; j < path.Tracks.Count; j++)
				{
					if (Pathfinder.IsShuntingOrStorageTrack(path.Tracks[j]))
					{
						flag2 = true;
						break;
					}
				}
				if (!flag2)
				{
					continue;
				}
				List<RailTrack> list = new List<RailTrack>();
				for (int k = 0; k <= currentPathTrackIndex; k++)
				{
					list.Add(CurrentPath.Tracks[k]);
				}
				for (int k = 1; k < path.Tracks.Count; k++)
				{
					RailTrack item = path.Tracks[k];
					if (!list.Contains(item))
					{
						list.Add(item);
					}
				}
				Pathfinder pathfinder = new Pathfinder(RailGraph.Instance);
				RailPath railPath = pathfinder.BuildPathFromTracks(list);
				if (railPath == null || !railPath.IsValid)
				{
					continue;
				}
				bool flag3 = false;
				for (int l = 0; l < railPath.Tracks.Count - 1; l++)
				{
					RailTrack railTrack4 = railPath.Tracks[l];
					RailTrack railTrack5 = railPath.Tracks[l + 1];
					if (!(railTrack4 != null) || !(railTrack4.curve != null) || !(railTrack5 != null) || !(railTrack5.curve != null))
					{
						continue;
					}
					RailEdge edge = RailGraph.Instance.GetEdge(railTrack4);
					RailEdge edge2 = RailGraph.Instance.GetEdge(railTrack5);
					if (edge == null || edge2 == null)
					{
						continue;
					}
					RailNode connectingNode = RailGraph.Instance.GetConnectingNode(edge, edge2);
					if (connectingNode != null)
					{
						Vector3 incomingVector = RailGraph.GetIncomingVector(connectingNode, edge);
						Vector3 departingVector = RailGraph.GetDepartingVector(connectingNode, edge2);
						if (Vector3.Dot(incomingVector, departingVector) < -0.3f)
						{
							flag3 = true;
							break;
						}
					}
				}
				if (flag3)
				{
					continue;
				}
				double currentSpan = 0.0;
				if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null)
				{
					currentSpan = _trainCar.FrontBogie.traveller.Span;
				}
				else if (_trainCar.RearBogie != null && _trainCar.RearBogie.traveller != null)
				{
					currentSpan = _trainCar.RearBogie.traveller.Span;
				}
				List<RailTrack> list2 = new List<RailTrack>();
				int num2 = railPath.Tracks.IndexOf(curTrack);
				if (num2 < 0)
				{
					num2 = 0;
				}
				for (int j = num2; j < railPath.Tracks.Count; j++)
				{
					list2.Add(railPath.Tracks[j]);
				}
				List<SignalRegistry.UpcomingSignal> list3 = new List<SignalRegistry.UpcomingSignal>();
				SignalRegistry.TryFindUpcomingSignals(curTrack, currentSpan, TargetDirection, list2, list3);
				Signals.Game.Signal approachingSignal = ApproachingSignal;
				Signals.Game.Signal signal = null;
				float num3 = float.PositiveInfinity;
				for (int m = 0; m < list3.Count; m++)
				{
					Signals.Game.Signal signal2 = list3[m].Signal;
					if (signal2 != null)
					{
						Signals.Game.Signal signal3 = ((signal2.Parent != null) ? signal2.Parent : signal2);
						Signals.Game.Signal signal4 = ((approachingSignal != null && approachingSignal.Parent != null) ? approachingSignal.Parent : approachingSignal);
						if (signal3 != signal4 && list3[m].Distance > 10f && SignalRegistry.IsLineSpeedAuthorizingSignal(signal2))
						{
							signal = signal2;
							num3 = list3[m].Distance;
							break;
						}
					}
				}
				bool flag4 = true;
				float num4 = 0f;
				for (int j = 0; j < list2.Count; j++)
				{
					RailTrack item = list2[j];
					if (!(item == null) && !(item == curTrack))
					{
						if (RailGraph.Instance.IsTrackOccupied(item, null, _trainCar.trainset) || SignalRegistry.IsTrackOccupiedByPlayer(item, _trainCar.trainset))
						{
							flag4 = false;
							break;
						}
						if (item.curve != null)
						{
							num4 += item.curve.length;
						}
						if (signal != null && num4 >= num3 + 10f)
						{
							break;
						}
					}
				}
				if (flag4)
				{
					CurrentPath = railPath;
					CurrentPathTrackIndex = num2;
					_upcomingTracks.Clear();
					for (int j = CurrentPathTrackIndex; j < CurrentPath.Tracks.Count; j++)
					{
						_upcomingTracks.Add(CurrentPath.Tracks[j]);
					}
					_obstacleTrack = null;
					DistanceToObstacle = float.PositiveInfinity;
					_stoppedAtRedTimer = 0f;
					_isShuntingMode = true;
					_isSelfBlockedShunting = false;
					_shuntingBypassedSignal = approachingSignal;
					SetShuntingTarget(signal);
					if (Main.ModEntry != null && Main.ModEntry.Logger != null)
					{
						Main.ModEntry.Logger.Log(string.Format("[AITraffic] Shunting Detour Adopted: Train '{0}' bypassing red signal '{1}' via yard shunting detour ({2} tracks). Next governing signal: '{3}'. Speed capped to 15 km/h.", LocoId, SignalRegistry.GetSignalName(approachingSignal), railPath.Tracks.Count, (signal != null) ? SignalRegistry.GetSignalName(signal) : "None (Destination Siding)"));
					}
					return true;
				}
			}
			return false;
		}

		public void StartInShuntingMode(Signals.Game.Signal nextGoverningSignal = null)
		{
			_isShuntingMode = true;
			_isSelfBlockedShunting = false;
			_shuntingBypassedSignal = null;
			SetShuntingTarget(nextGoverningSignal);
			if (Main.ModEntry != null && Main.ModEntry.Logger != null)
			{
				Main.ModEntry.Logger.Log(string.Format("[AITraffic] Train '{0}' starting in Slow Shunting Mode on siding. Speed capped to 15 km/h until governing main signal clear.", LocoId));
			}
		}

		public bool IsSignalBlockedOnlyBySelf(Signals.Game.Signal signal)
		{
			if (signal == null)
			{
				return false;
			}
			Trainset playerTrainset;
			Vector3 playerPosition;
			float playerSpeedKmh;
			if (SignalRegistry.TryGetPlayerTrainInfo(out playerTrainset, out playerPosition, out playerSpeedKmh))
			{
				bool flag = _trainCar != null && (PlayerManager.Car == _trainCar || (playerTrainset != null && playerTrainset == _trainCar.trainset));
				bool flag2 = Main.Settings != null && Main.Settings.RideAlongMode;
				if (!flag && !flag2 && Vector3.Distance(SignalRegistry.GetSignalPosition(signal), playerPosition) < 150f)
				{
					return false;
				}
			}
			SignalBlockInfo signalBlockInfo = null;
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
			{
				for (int i = 0; i < _upcomingSignalBlocks.Count; i++)
				{
					SignalBlockInfo signalBlockInfo2 = _upcomingSignalBlocks[i];
					if (signalBlockInfo2 != null && signalBlockInfo2.EntrySignal == signal)
					{
						signalBlockInfo = signalBlockInfo2;
						break;
					}
				}
				if (signalBlockInfo == null && _upcomingSignalBlocks.Count > 1 && _upcomingSignalBlocks[0].ExitSignal == signal)
				{
					signalBlockInfo = _upcomingSignalBlocks[1];
				}
			}
			HashSet<RailTrack> hashSet = new HashSet<RailTrack>();
			bool flag3 = false;
			Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
			RailTrack track;
			double span;
			SignalRegistry.TryGetSignalPlacement(signal2, out track, out span);
			TrackBlock trackBlock = signal2.Block ?? signal.Block;
			if (trackBlock != null)
			{
				if (trackBlock.IsSelfLoop)
				{
					flag3 = true;
				}
				if (trackBlock.Tracks != null)
				{
					for (int j = 0; j < trackBlock.Tracks.Length; j++)
					{
						RailTrack track2 = trackBlock.Tracks[j].Track;
						if (track2 != null)
						{
							hashSet.Add(track2);
						}
					}
				}
				if (trackBlock.ExtraTracks != null)
				{
					for (int j = 0; j < trackBlock.ExtraTracks.Length; j++)
					{
						RailTrack track2 = trackBlock.ExtraTracks[j];
						if (track2 != null)
						{
							hashSet.Add(track2);
						}
					}
				}
			}
			bool flag4 = flag3;
			if (!flag4 && _upcomingTracks != null && _upcomingTracks.Count > 1)
			{
				HashSet<RailTrack> hashSet2 = new HashSet<RailTrack>();
				for (int j = 0; j < _upcomingTracks.Count; j++)
				{
					RailTrack track2 = _upcomingTracks[j];
					if (!(track2 == null) && !hashSet2.Add(track2))
					{
						flag4 = true;
						break;
					}
				}
			}
			if (!flag4)
			{
				return false;
			}
			if (signalBlockInfo != null && signalBlockInfo.Tracks != null && signalBlockInfo.Tracks.Count > 0)
			{
				if (!signalBlockInfo.AreSwitchesAligned)
				{
					return false;
				}
				if (signalBlockInfo.IsPlayerOccupied)
				{
					return false;
				}
				for (int k = 0; k < signalBlockInfo.Tracks.Count; k++)
				{
					RailTrack railTrack = signalBlockInfo.Tracks[k];
					if (railTrack != null)
					{
						hashSet.Add(railTrack);
					}
				}
			}
			else if (hashSet.Count == 0)
			{
				float num = 0f;
				for (int k = 0; k < _upcomingTracks.Count; k++)
				{
					RailTrack railTrack = _upcomingTracks[k];
					if (!(railTrack == null))
					{
						hashSet.Add(railTrack);
						if (railTrack.curve != null)
						{
							num += railTrack.curve.length;
						}
						if (num > 500f)
						{
							break;
						}
					}
				}
			}
			if (hashSet.Count == 0)
			{
				return false;
			}
			Trainset trainset = ((_trainCar != null) ? _trainCar.trainset : null);
			bool flag5 = false;
			foreach (RailTrack item in hashSet)
			{
				if (item == null)
				{
					continue;
				}
				if (SignalRegistry.IsTrackOccupiedByPlayer(item, trainset))
				{
					return false;
				}
				HashSet<Bogie> hashSet3 = item.BogiesOnTrack();
				if (hashSet3 != null && hashSet3.Count > 0)
				{
					foreach (Bogie item2 in hashSet3)
					{
						if (!(item2 == null) && !(item2.Car == null))
						{
							if ((trainset == null || (item2.Car.trainset != trainset && (trainset.cars == null || !trainset.cars.Contains(item2.Car)))) && (_registeredConsistCars == null || !_registeredConsistCars.Contains(item2.Car)))
							{
								return false;
							}
							if (item != CurrentTrack && item != track)
							{
								flag5 = true;
							}
						}
					}
				}
				Signals.Game.Signal by;
				if (TrackReserver.IsTrackReserved(item, out by) && by != null)
				{
					Signals.Game.Signal signal3 = ((by.Parent != null) ? by.Parent : by);
					Signals.Game.Signal signal4 = ((signal.Parent != null) ? signal.Parent : signal);
					if (signal3 != signal4)
					{
						return false;
					}
				}
			}
			return (flag5 || flag3) && flag4;
		}

		public bool CanEnterLoopOnSight(Signals.Game.Signal signal)
		{
			if (signal == null)
			{
				return false;
			}
			Trainset playerTrainset;
			Vector3 playerPosition;
			float playerSpeedKmh;
			if (SignalRegistry.TryGetPlayerTrainInfo(out playerTrainset, out playerPosition, out playerSpeedKmh))
			{
				bool flag = _trainCar != null && (PlayerManager.Car == _trainCar || (playerTrainset != null && playerTrainset == _trainCar.trainset));
				bool flag2 = Main.Settings != null && Main.Settings.RideAlongMode;
				if (!flag && !flag2 && Vector3.Distance(SignalRegistry.GetSignalPosition(signal), playerPosition) < 150f)
				{
					return false;
				}
			}
			bool flag3 = false;
			if (_upcomingTracks != null && _upcomingTracks.Count > 1)
			{
				HashSet<RailTrack> hashSet = new HashSet<RailTrack>();
				for (int i = 0; i < _upcomingTracks.Count; i++)
				{
					RailTrack railTrack = _upcomingTracks[i];
					if (!(railTrack == null) && !hashSet.Add(railTrack))
					{
						flag3 = true;
						break;
					}
				}
			}
			Signals.Game.Signal signal2 = ((signal.Parent != null) ? signal.Parent : signal);
			TrackBlock trackBlock = signal2.Block ?? signal.Block;
			if (trackBlock != null && trackBlock.IsSelfLoop)
			{
				flag3 = true;
			}
			if (!flag3)
			{
				return false;
			}
			Trainset trainset = ((_trainCar != null) ? _trainCar.trainset : null);
			float num = 0f;
			if (_upcomingTracks != null)
			{
				for (int i = 0; i < _upcomingTracks.Count; i++)
				{
					RailTrack railTrack2 = _upcomingTracks[i];
					if (railTrack2 == null)
					{
						continue;
					}
					if (SignalRegistry.IsTrackOccupiedByPlayer(railTrack2, trainset))
					{
						return false;
					}
					HashSet<Bogie> hashSet2 = railTrack2.BogiesOnTrack();
					if (hashSet2 != null && hashSet2.Count > 0)
					{
						foreach (Bogie item in hashSet2)
						{
							if (item == null || item.Car == null || (trainset != null && (item.Car.trainset == trainset || (trainset.cars != null && trainset.cars.Contains(item.Car)))) || (_registeredConsistCars != null && _registeredConsistCars.Contains(item.Car)))
							{
								continue;
							}
							return false;
						}
					}
					if (railTrack2.curve != null)
					{
						num += railTrack2.curve.length;
					}
					if (num > 500f)
					{
						break;
					}
				}
			}
			if (DistanceToObstacle < 100f)
			{
				return false;
			}
			return true;
		}

		private Signals.Game.Signal FindNextGoverningMainSignal(Signals.Game.Signal currentSignal)
		{
			Signals.Game.Signal signal = ((currentSignal != null && currentSignal.Parent != null) ? currentSignal.Parent : currentSignal);
			if (_upcomingSignals != null)
			{
				for (int i = 0; i < _upcomingSignals.Count; i++)
				{
					Signals.Game.Signal signal2 = _upcomingSignals[i].Signal;
					if (signal2 != null)
					{
						Signals.Game.Signal signal3 = ((signal2.Parent != null) ? signal2.Parent : signal2);
						if (signal3 != signal && _upcomingSignals[i].Distance > 10f && SignalRegistry.IsLineSpeedAuthorizingSignal(signal2))
						{
							return signal2;
						}
					}
				}
			}
			if (_upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 1)
			{
				for (int j = 0; j < _upcomingSignalBlocks.Count; j++)
				{
					SignalBlockInfo signalBlockInfo = _upcomingSignalBlocks[j];
					if (signalBlockInfo == null)
					{
						continue;
					}
					Signals.Game.Signal signal2 = signalBlockInfo.ExitSignal ?? signalBlockInfo.EntrySignal;
					if (signal2 != null)
					{
						Signals.Game.Signal signal3 = ((signal2.Parent != null) ? signal2.Parent : signal2);
						if (signal3 != signal && signalBlockInfo.DistanceToExit > 10f && SignalRegistry.IsLineSpeedAuthorizingSignal(signal2))
						{
							return signal2;
						}
					}
				}
			}
			return null;
		}

		public static int GetTrainPriority(AIEngineer eng)
		{
			if (eng == null)
			{
				return 999;
			}
			if (eng.IsWorkerDriven)
			{
				return 500;
			}
			switch (eng.ConsistType)
			{
			case ConsistType.PassengerCommuter:
				return 400;
			case ConsistType.MainlineHeavy:
				return 300;
			case ConsistType.RegionalFreight:
				return 200;
			case ConsistType.ShunterFreight:
				return 100;
			default:
				return 200;
			}
		}

		private bool TryDynamicPassingReroute(RailTrack curTrack)
		{
			if (IsWorkerDriven) return false;
			if (curTrack == null || CurrentPath == null || CurrentPath.Tracks == null || CurrentPath.Tracks.Count <= 1)
			{
				return false;
			}
			int currentPathTrackIndex = CurrentPathTrackIndex;
			for (int i = currentPathTrackIndex; i < CurrentPath.Tracks.Count - 1 && i < currentPathTrackIndex + 8; i++)
			{
				RailTrack railTrack = CurrentPath.Tracks[i];
				RailTrack railTrack2 = CurrentPath.Tracks[i + 1];
				Junction junction;
				byte requiredBranch;
				if (railTrack == null || railTrack2 == null || !SignalRegistry.TryGetJunctionBetweenTracks(railTrack, railTrack2, out junction, out requiredBranch) || !(junction != null) || junction.outBranches == null || junction.outBranches.Count <= 1)
				{
					continue;
				}
				for (int j = 0; j < junction.outBranches.Count; j++)
				{
					Junction.Branch branch = junction.outBranches[j];
					if (branch == null || branch.track == null || branch.track == railTrack2)
					{
						continue;
					}
					RailTrack track = branch.track;
					bool flag = Pathfinder.IsPassingOrSidingTrack(track) || (track.name != null && (track.name.IndexOf("[S]", StringComparison.OrdinalIgnoreCase) >= 0 || track.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || track.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 || track.name.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0));
					bool flag2 = Pathfinder.IsIndustrialYardOrStorageTrack(track) || (track.inJunction == null && track.outJunction == null);
					if (!flag || flag2)
					{
						continue;
					}
					bool flag3 = SignalRegistry.IsTrackOccupiedByPlayer(track);
					bool flag4 = RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(track, null, (_trainCar != null) ? _trainCar.trainset : null);
					if (!flag3 && !flag4)
					{
						if (Main.ModEntry != null && Main.ModEntry.Logger != null)
						{
							Main.ModEntry.Logger.Log(string.Format("[AITraffic] Dynamic Passing: Yielding train '{0}' adopting passing loop '{1}' to allow through traffic on '{2}'.", LocoId, track.name, railTrack2.name));
						}
						return TryDynamicObstacleReroute(curTrack, railTrack2);
					}
				}
			}
			return false;
		}

		// CRITICAL INVARIANT [AIE-11]: Full-Route Corridor Conflict Lookahead Immunity
		private void UpdateSingleTrackCorridorProtection(float dt)
		{
			// CRITICAL INVARIANT [AIE-13]: Multi-track hold on reverse protocol
			if (_reverseProtocolStage != SingleTrackReverseStage.None) { _isHoldingForCorridor = true; return; }
			if (CurrentPath == null || CurrentPath.Tracks == null || _trainCar == null || RailGraph.Instance == null || !RailGraph.Instance.IsInitialized)
			{
				return;
			}
			if (_reverseProtocolStage != 0)
			{
				_isHoldingForCorridor = true;
				return;
			}
			_corridorCheckCooldown -= dt;
			if (_corridorCheckCooldown > 0f)
			{
				return;
			}
			_corridorCheckCooldown = 0.3f;
			RailTrack railTrack = null;
			if (_trainCar.FrontBogie != null)
			{
				railTrack = _trainCar.FrontBogie.track;
			}
			if (railTrack == null && _trainCar.RearBogie != null)
			{
				railTrack = _trainCar.RearBogie.track;
			}
			if (railTrack == null)
			{
				return;
			}
			_corridorTracksCache.Clear();
			List<RailTrack> corridorTracksCache = _corridorTracksCache;
			float num = 0f;
			float num2 = 0f;
			bool flag = false;
			double num3 = ((_trainCar.FrontBogie != null && _trainCar.FrontBogie.traveller != null) ? _trainCar.FrontBogie.traveller.Span : 0.0);
			float num4 = ((railTrack.curve != null) ? railTrack.curve.length : 50f);
			float num5 = ((TargetDirection >= 0f) ? Mathf.Max(0f, num4 - (float)num3) : Mathf.Max(0f, (float)num3));
			num2 = num5;
			RailEdge edge = RailGraph.Instance.GetEdge(railTrack);
			bool flag2 = (edge != null && edge.IsDoubleTrackMainline) || (railTrack != null && railTrack.name != null && (railTrack.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || railTrack.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0));
			bool flag3 = Pathfinder.IsPassingOrSidingTrack(railTrack);
			bool flag4 = edge != null && !flag2 && !edge.IsYardTrack && !flag3;
			if (flag4)
			{
				flag = true;
				num = 0f;
				corridorTracksCache.Add(railTrack);
			}
			for (int i = Math.Max(0, CurrentPathTrackIndex + 1); i < CurrentPath.Tracks.Count; i++)
			{
				RailTrack railTrack2 = CurrentPath.Tracks[i];
				if (railTrack2 == null || railTrack2 == railTrack)
				{
					continue;
				}
				RailEdge edge2 = RailGraph.Instance.GetEdge(railTrack2);
				bool flag5 = (edge2 != null && edge2.IsDoubleTrackMainline) || (railTrack2 != null && railTrack2.name != null && (railTrack2.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 || railTrack2.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0));
				bool flag6 = Pathfinder.IsPassingOrSidingTrack(railTrack2);
				if (edge2 != null && !flag5 && !edge2.IsYardTrack && !flag6)
				{
					if (!flag)
					{
						flag = true;
						num = num2;
					}
					corridorTracksCache.Add(railTrack2);
				}
				else if (flag)
				{
					break;
				}
				num2 += ((railTrack2.curve != null) ? railTrack2.curve.length : 50f);
				if (num2 > 15000f)
				{
					break;
				}
			}
			DistToCorridorStart = num;
			if (corridorTracksCache.Count == 0)
			{
				_isHoldingInMultiTrack = false;
				_isHoldingForCorridor = false;
				_corridorHoldReason = null;
				_corridorHoldDistance = float.PositiveInfinity;
				return;
			}
			bool flag7 = false;
			bool flag8 = false;
			bool flag9 = false;
			AIEngineer aIEngineer = null;
			string text = null;
			bool flag10 = Main.Settings != null && Main.Settings.RideAlongMode;
			for (int j = 0; j < corridorTracksCache.Count; j++)
			{
				RailTrack track = corridorTracksCache[j];
				if (!flag10 && (SignalRegistry.IsTrackOccupiedByPlayer(track, _trainCar.trainset) || SignalRegistry.IsTrackReservedByPlayerSignal(track)))
				{
					flag7 = true;
					text = "Player train on single track corridor";
					break;
				}
				object holder;
				if (RailGraph.Instance.TryGetTrackReservationHolder(track, this, out holder))
				{
					AIEngineer aIEngineer2 = holder as AIEngineer;
					if (aIEngineer2 != null && aIEngineer2.IsWorkerDriven)
					{
						flag8 = true;
						aIEngineer = aIEngineer2;
						text = "Player worker train in corridor ahead";
						break;
					}
					if (aIEngineer2 == null)
					{
						flag9 = true;
						text = "Corridor reserved by unknown agent";
						break;
					}
					Vector3 lhs = ((_trainCar != null) ? (_trainCar.transform.forward * _desiredReverser) : Vector3.zero);
					Vector3 rhs = ((aIEngineer2._trainCar != null) ? (aIEngineer2._trainCar.transform.forward * aIEngineer2._desiredReverser) : Vector3.zero);
					bool flag11 = Vector3.Dot(lhs, rhs) < 0f;
					bool flag12 = aIEngineer2.CurrentSpeedKmh < 1.5f;
					if (flag11 || flag12)
					{
						flag9 = true;
						aIEngineer = aIEngineer2;
						text = (flag11 ? "Opposing AI train in corridor" : "Stopped AI train blocking corridor");
						break;
					}
				}
			}
			if (!flag7 && !flag8 && TrafficManager.Instance != null && TrafficManager.Instance.ActiveEngineers != null)
			{
				List<AIEngineer> activeEngineers = TrafficManager.Instance.ActiveEngineers;
				for (int k = 0; k < activeEngineers.Count; k++)
				{
					AIEngineer aIEngineer3 = activeEngineers[k];
					if (aIEngineer3 != null && aIEngineer3 != this && aIEngineer3.IsWorkerDriven && aIEngineer3.CurrentTrack != null && corridorTracksCache.Contains(aIEngineer3.CurrentTrack))
					{
						flag8 = true;
						aIEngineer = aIEngineer3;
						text = "Player worker train on corridor track";
						break;
					}
				}
			}
			bool flag13 = false;
			if ((flag2 || flag3) && _upcomingSignals != null && _upcomingSignals.Count > 0)
			{
				for (int l = 0; l < _upcomingSignals.Count; l++)
				{
					SignalRegistry.UpcomingSignal upcomingSig = _upcomingSignals[l];
					SignalRegistry.UpcomingSignal upcomingSignal = upcomingSig;
					if (upcomingSig.Signal == null)
					{
						continue;
					}
					// CRITICAL INVARIANT [AIE-08]: Entry Signal yellow-box boundary guard
					if (upcomingSig.Distance > 1200f || AITraffic.Navigation.SignalRegistry.IsEntrySignal(upcomingSig.Signal))
					{
						break;
					}
					if (upcomingSignal.Signal.CurrentAspect == null || !upcomingSignal.Signal.CurrentAspect.DisallowPassing || !(upcomingSignal.Distance >= Mathf.Max(0f, num - 10f)))
					{
						continue;
					}
					bool flag14 = upcomingSignal.Signal.Block != null && upcomingSignal.Signal.Block.IsOccupied(CrossingCheckMode.WholeTrack);
					bool flag15 = TrackReserver.IsSignalReservedByAnother(upcomingSignal.Signal);
					if (flag14 || flag15 || flag7 || flag8 || flag9 || aIEngineer != null)
					{
						flag13 = true;
						if (string.IsNullOrEmpty(text))
						{
							text = "Junction exit signal is RED (Hp 0) and bottleneck occupied";
						}
						break;
					}
				}
			}
			if (flag7 || flag8 || flag9 || flag13)
			{
				_corridorClearConfirmTimer = 4f;
				bool flag16 = flag7 || flag8 || flag13;
				if (!flag16 && aIEngineer != null)
				{
					bool flag17 = CurrentSpeedKmh < 1.5f && DistanceToObstacle < 150f;
					bool flag18 = aIEngineer.CurrentSpeedKmh < 1.5f && aIEngineer.DistanceToObstacle < 150f;
					if (flag17 && !flag18)
					{
						flag16 = true;
					}
					else if (!flag17 && flag18)
					{
						flag16 = false;
					}
					else
					{
						bool flag19 = flag4;
						bool flag20 = aIEngineer.DistToCorridorStart <= 5f || (aIEngineer.CurrentTrack != null && corridorTracksCache.Contains(aIEngineer.CurrentTrack));
						if (flag19 && !flag20)
						{
							flag16 = false;
						}
						else if (!flag19 && flag20)
						{
							flag16 = true;
						}
						else
						{
							int trainPriority = GetTrainPriority(this);
							int trainPriority2 = GetTrainPriority(aIEngineer);
							if (trainPriority < trainPriority2)
							{
								flag16 = true;
							}
							else if (trainPriority == trainPriority2)
							{
								float distToCorridorStart = aIEngineer.DistToCorridorStart;
								if (Mathf.Abs(num - distToCorridorStart) > 5f)
								{
									flag16 = num > distToCorridorStart;
								}
								else
								{
									string locoId = LocoId;
									string locoId2 = aIEngineer.LocoId;
									flag16 = string.CompareOrdinal(locoId, locoId2) < 0;
								}
							}
							else
							{
								flag16 = false;
							}
						}
					}
				}
				if (flag16 && !IsWorkerDriven)
				{
					if (TryDynamicPassingReroute(railTrack))
					{
						float num6 = 0f;
						bool flag21 = false;
						for (int m = 0; m < _upcomingTracks.Count; m++)
						{
							RailTrack railTrack3 = _upcomingTracks[m];
							if (!(railTrack3 == null))
							{
								float num7 = ((railTrack3.curve != null) ? railTrack3.curve.length : 50f);
								num6 += num7;
								if (Pathfinder.IsPassingOrSidingTrack(railTrack3))
								{
									flag21 = true;
									break;
								}
							}
						}
						_isHoldingForCorridor = true;
						_corridorHoldReason = text;
						if (flag21)
						{
							float b = Mathf.Max(15f, num6 - 45f);
							_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b);
						}
						else
						{
							float b2 = Mathf.Max(5f, num - 45f);
							_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b2);
						}
						return;
					}
					bool flag22 = flag2 || flag3 || num > 10f;
					if (!flag22)
					{
						_isHoldingInMultiTrack = false;
					}
					if (flag22)
					{
						if (!_isHoldingInMultiTrack && Main.IsDebugLoggingEnabled)
						{
							Main.LogDebug(string.Format("[AITraffic] Train '{0}' holding in multi-track section before junction bottleneck. Reason: {1}", LocoId, text));
						}
						_isHoldingInMultiTrack = true;
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Holding in multi-track section";
						_corridorClearConfirmTimer = 4f;
						float b2 = Mathf.Max(0f, num - 40f);
						if (CurrentSpeedKmh < 1.5f && (num <= 50f || _isHoldingInMultiTrack))
						{
							b2 = 0f;
						}
						_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b2);
					}
					else if ((DistanceToObstacle < 500f || CurrentSpeedKmh < 1.5f) && FindNearestTrailingSiding(out _trailingSidingSwitch, out _trailingSidingBranch, out _trailingSidingTrack))
					{
						if (_reverseProtocolStage == SingleTrackReverseStage.None)
						{
							_reverseProtocolStage = SingleTrackReverseStage.HaltAndYield;
							_reverseProtocolTimer = 0f;
							if (Main.IsDebugLoggingEnabled)
							{
								Main.LogDebug(string.Format("[AITraffic] Face-to-face confrontation on single track! Train '{0}' initiating yield & reverse protocol.", LocoId));
							}
						}
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Yielding and reversing on single track";
					}
					else
					{
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Holding on single track";
						float b3 = ((CurrentSpeedKmh < 1.5f) ? 0f : Mathf.Min(50f, Mathf.Max(0f, DistanceToObstacle - 45f)));
						_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b3);
					}
					return;
				}
				bool flag23 = false;
				if (aIEngineer != null)
				{
					RailTrack currentTrack = aIEngineer.CurrentTrack;
					flag23 = aIEngineer.DistToCorridorStart <= 5f || (currentTrack != null && corridorTracksCache.Contains(currentTrack));
				}
				else if (flag7 || flag8)
				{
					flag23 = true;
				}
				if (flag23)
				{
					bool flag22 = flag2 || flag3 || num > 10f;
					if (!flag22)
					{
						_isHoldingInMultiTrack = false;
					}
					if (flag22)
					{
						_isHoldingInMultiTrack = true;
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Holding in multi-track section";
						float b2 = Mathf.Max(0f, num - 40f);
						if (CurrentSpeedKmh < 1.5f && (num <= 50f || _isHoldingInMultiTrack))
						{
							b2 = 0f;
						}
						_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b2);
					}
					else if ((((flag7 || flag8) && (DistanceToObstacle < 500f || CurrentSpeedKmh < 1.5f)) || (aIEngineer != null && DistanceToObstacle < 120f)) && FindNearestTrailingSiding(out _trailingSidingSwitch, out _trailingSidingBranch, out _trailingSidingTrack))
					{
						if (_reverseProtocolStage == SingleTrackReverseStage.None)
						{
							_reverseProtocolStage = SingleTrackReverseStage.HaltAndYield;
							_reverseProtocolTimer = 0f;
							if (Main.IsDebugLoggingEnabled)
							{
								Main.LogDebug(string.Format("[AITraffic] Standoff on single track! Train '{0}' yielding and reversing for opposing train.", LocoId));
							}
						}
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Yielding and reversing on single track";
					}
					else
					{
						_isHoldingForCorridor = true;
						_corridorHoldReason = text ?? "Holding in single track";
						float b3 = ((CurrentSpeedKmh < 1.5f) ? 0f : Mathf.Min(50f, Mathf.Max(0f, DistanceToObstacle - 45f)));
						_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b3);
					}
					return;
				}
				if (_isHoldingForCorridor || _isHoldingInMultiTrack)
				{
					_corridorClearConfirmTimer -= 0.3f;
					if (_corridorClearConfirmTimer > 0f)
					{
						return;
					}
				}
				bool flag24 = _isHoldingForCorridor || _isHoldingInMultiTrack;
				_isHoldingInMultiTrack = false;
				_isHoldingForCorridor = false;
				_corridorHoldReason = null;
				_corridorHoldDistance = float.PositiveInfinity;
				if (flag24)
				{
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' cleared corridor hold. Resuming mainline transit.", LocoId));
					}
					_pathUpdateCooldown = 0f;
					_speedProfileUpdateCooldown = 0f;
					_stoppedSwitchAlignCooldown = 0f;
					for (int n = 0; n < _upcomingSignalBlocks.Count; n++)
					{
						Signals.Game.Signal signal = _upcomingSignalBlocks[n].EntrySignal ?? _upcomingSignalBlocks[n].ExitSignal;
						if (signal != null)
						{
							DebouncedWakeAndForceUpdateSignal(signal);
						}
					}
				}
			}
			else
			{
				if ((flag2 || flag3 || num > 10f) && num <= 150f && corridorTracksCache.Count > 0)
				{
					if (!RailGraph.Instance.TryAcquireCorridorToken(corridorTracksCache, this))
					{
						ReleaseActiveCorridorToken();
						_isHoldingInMultiTrack = true;
						_isHoldingForCorridor = true;
						_corridorHoldReason = "Waiting for single-track corridor token";
						_corridorClearConfirmTimer = 4f;
						float b2 = Mathf.Max(0f, num - 40f);
						if (CurrentSpeedKmh < 1.5f && (num <= 50f || _isHoldingInMultiTrack))
						{
							b2 = 0f;
						}
						_corridorHoldDistance = Mathf.Min(_corridorHoldDistance, b2);
						return;
					}
					for (int j = 0; j < corridorTracksCache.Count; j++)
					{
						RailTrack track = corridorTracksCache[j];
						_activeCorridorTracks.Add(track);
						_reservedTracks.Add(track);
					}
				}
				if (_isHoldingForCorridor || _isHoldingInMultiTrack)
				{
					_corridorClearConfirmTimer -= 0.3f;
					if (_corridorClearConfirmTimer > 0f)
					{
						return;
					}
				}
				bool flag24 = _isHoldingForCorridor || _isHoldingInMultiTrack;
				_isHoldingInMultiTrack = false;
				_isHoldingForCorridor = false;
				_corridorHoldReason = null;
				_corridorHoldDistance = float.PositiveInfinity;
				if (flag24)
				{
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' cleared corridor hold. Resuming mainline transit.", LocoId));
					}
					_pathUpdateCooldown = 0f;
					_speedProfileUpdateCooldown = 0f;
					_stoppedSwitchAlignCooldown = 0f;
					for (int n = 0; n < _upcomingSignalBlocks.Count; n++)
					{
						Signals.Game.Signal signal = _upcomingSignalBlocks[n].EntrySignal ?? _upcomingSignalBlocks[n].ExitSignal;
						if (signal != null)
						{
							DebouncedWakeAndForceUpdateSignal(signal);
						}
					}
				}
			}
			if (State == EngineState.TerminusStop)
			{
				foreach (RailTrack reservedTrack in _reservedTracks)
				{
					RailGraph.Instance.ReleaseTrackReservation(reservedTrack, this);
				}
				_reservedTracks.Clear();
				ReleaseActiveCorridorToken();
			}
			else if (!_isHoldingForCorridor)
			{
				for (int j = 0; j < corridorTracksCache.Count; j++)
				{
					RailTrack track = corridorTracksCache[j];
					if (!_reservedTracks.Contains(track) && RailGraph.Instance.TryReserveTrack(track, this))
					{
						_reservedTracks.Add(track);
						_activeCorridorTracks.Add(track);
					}
				}
			}
			if (_activeCorridorTracks.Count <= 0)
			{
				return;
			}
			bool flag25 = false;
			if (_trainCar != null && _trainCar.trainset != null && _trainCar.trainset.cars != null)
			{
				List<TrainCar> cars = _trainCar.trainset.cars;
				for (int j = 0; j < cars.Count; j++)
				{
					TrainCar trainCar = cars[j];
					if (!(trainCar == null))
					{
						if (trainCar.FrontBogie != null && _activeCorridorTracks.Contains(trainCar.FrontBogie.track))
						{
							flag25 = true;
							break;
						}
						if (trainCar.RearBogie != null && _activeCorridorTracks.Contains(trainCar.RearBogie.track))
						{
							flag25 = true;
							break;
						}
					}
				}
			}
			if (!flag25 && !flag)
			{
				ReleaseActiveCorridorToken();
			}
		}

		private void ReleaseActiveCorridorToken()
		{
			if (_activeCorridorTracks.Count == 0)
			{
				return;
			}
			if (RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
			{
				RailGraph.Instance.ReleaseAllCorridorTokensFor(this);
			}
			foreach (RailTrack activeCorridorTrack in _activeCorridorTracks)
			{
				if (RailGraph.Instance != null && RailGraph.Instance.IsInitialized)
				{
					RailGraph.Instance.ReleaseTrackReservation(activeCorridorTrack, this);
				}
				_reservedTracks.Remove(activeCorridorTrack);
			}
			_activeCorridorTracks.Clear();
		}

		private bool FindNearestTrailingSiding(out Junction trailingSwitch, out byte sidingBranch, out RailTrack sidingTrack, out byte mainlineBranch)
		{
			trailingSwitch = null;
			sidingBranch = 0;
			sidingTrack = null;
			mainlineBranch = 0;
			if (CurrentPath == null || CurrentPath.Tracks == null || CurrentPathTrackIndex < 0)
			{
				return false;
			}
			float num = 0f;
			RailTrack railTrack = ((CurrentPathTrackIndex + 1 < CurrentPath.Tracks.Count) ? CurrentPath.Tracks[CurrentPathTrackIndex + 1] : null);
			for (int num2 = CurrentPathTrackIndex; num2 >= 0; num2--)
			{
				RailTrack railTrack2 = CurrentPath.Tracks[num2];
				if (!(railTrack2 == null))
				{
					if (railTrack2.curve != null)
					{
						num += railTrack2.curve.length;
						float accumulatedDist = num;
				if (accumulatedDist > 10000.0f)
						{
							break;
						}
					}
					if ((num2 != CurrentPathTrackIndex || railTrack == null || !DoesJunctionConnectTrack(railTrack2.inJunction, railTrack)) && CheckJunctionForSidingBranch(railTrack2.inJunction, railTrack2, out sidingBranch, out sidingTrack, out mainlineBranch) && IsSidingClear(sidingTrack))
					{
						trailingSwitch = railTrack2.inJunction;
						_trailingSidingMainlineBranch = mainlineBranch;
						return true;
					}
					if ((num2 != CurrentPathTrackIndex || railTrack == null || !DoesJunctionConnectTrack(railTrack2.outJunction, railTrack)) && CheckJunctionForSidingBranch(railTrack2.outJunction, railTrack2, out sidingBranch, out sidingTrack, out mainlineBranch) && IsSidingClear(sidingTrack))
					{
						trailingSwitch = railTrack2.outJunction;
						_trailingSidingMainlineBranch = mainlineBranch;
						return true;
					}
				}
			}
			return false;
		}

		private static bool DoesJunctionConnectTrack(Junction j, RailTrack targetTrack)
		{
			if (j == null || targetTrack == null)
			{
				return false;
			}
			if (j.inBranch != null && j.inBranch.track == targetTrack)
			{
				return true;
			}
			if (j.outBranches != null)
			{
				for (int i = 0; i < j.outBranches.Count; i++)
				{
					if (j.outBranches[i] != null && j.outBranches[i].track == targetTrack)
					{
						return true;
					}
				}
			}
			return false;
		}

		private bool FindNearestTrailingSiding(out Junction trailingSwitch, out byte sidingBranch, out RailTrack sidingTrack)
		{
			byte mainlineBranch;
			return FindNearestTrailingSiding(out trailingSwitch, out sidingBranch, out sidingTrack, out mainlineBranch);
		}

		private bool IsSidingClear(RailTrack sidingTrack)
		{
			if (sidingTrack == null)
			{
				return false;
			}
			Trainset trainset = ((_trainCar != null) ? _trainCar.trainset : null);
			if (SignalRegistry.IsTrackOccupiedByPlayer(sidingTrack, trainset))
			{
				return false;
			}
			if (RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(sidingTrack, null, trainset))
			{
				return false;
			}
			return true;
		}

		private bool CheckJunctionForSidingBranch(Junction j, RailTrack approachingTrack, out byte sidingBranch, out RailTrack sidingTrack, out byte mainlineBranch)
		{
			sidingBranch = 0;
			sidingTrack = null;
			mainlineBranch = 0;
			if (j == null || j.outBranches == null || j.outBranches.Count < 2)
			{
				return false;
			}
			bool flag = j.inBranch != null && j.inBranch.track == approachingTrack;
			byte b = 0;
			RailTrack railTrack = null;
			bool flag2 = false;
			byte b2 = 0;
			RailTrack railTrack2 = null;
			bool flag3 = false;
			for (byte b3 = 0; b3 < j.outBranches.Count; b3 = (byte)(b3 + 1))
			{
				Junction.Branch branch = j.outBranches[b3];
				if (branch != null && !(branch.track == null))
				{
					if (!flag && branch.track == approachingTrack)
					{
						mainlineBranch = b3;
					}
					else if (IsSidingClear(branch.track))
					{
						bool flag4 = SignalRegistry.IsTrackReservedByPlayerSignal(branch.track);
						if ((Pathfinder.IsPassingOrSidingTrack(branch.track) || (branch.track.name != null && (branch.track.name.IndexOf("siding", StringComparison.OrdinalIgnoreCase) >= 0 || branch.track.name.IndexOf("loop", StringComparison.OrdinalIgnoreCase) >= 0 || branch.track.name.IndexOf("storage", StringComparison.OrdinalIgnoreCase) >= 0 || branch.track.name.IndexOf("-S]", StringComparison.OrdinalIgnoreCase) >= 0 || branch.track.name.IndexOf("-L]", StringComparison.OrdinalIgnoreCase) >= 0))) && !flag4 && !flag2)
						{
							b = b3;
							railTrack = branch.track;
							flag2 = true;
						}
						else if (flag && !flag3 && (!flag4 || railTrack2 == null))
						{
							b2 = b3;
							railTrack2 = branch.track;
							if (!flag4)
							{
								flag3 = true;
							}
						}
					}
				}
			}
			if (flag2)
			{
				sidingBranch = b;
				sidingTrack = railTrack;
				if (flag)
				{
					mainlineBranch = (byte)((sidingBranch == 0) ? 1u : 0u);
				}
				return true;
			}
			if (flag && railTrack2 != null)
			{
				sidingBranch = b2;
				sidingTrack = railTrack2;
				mainlineBranch = (byte)((sidingBranch == 0) ? 1u : 0u);
				return true;
			}
			return false;
		}

		private Vector3 GetTrailingCarPosition()
		{
			if (_trainCar == null)
			{
				return Vector3.zero;
			}
			if (_trainCar.trainset != null && _trainCar.trainset.cars != null && _trainCar.trainset.cars.Count > 0)
			{
				List<TrainCar> cars = _trainCar.trainset.cars;
				TrainCar trainCar = cars[cars.Count - 1];
				if (trainCar != null)
				{
					return trainCar.transform.position;
				}
			}
			return _trainCar.transform.position;
		}

		private bool IsAnyCarOnTrack(RailTrack trk)
		{
			if (trk == null || _trainCar == null || _trainCar.trainset == null || _trainCar.trainset.cars == null)
			{
				return false;
			}
			List<TrainCar> cars = _trainCar.trainset.cars;
			for (int i = 0; i < cars.Count; i++)
			{
				TrainCar trainCar = cars[i];
				if (!(trainCar == null))
				{
					if (trainCar.FrontBogie != null && trainCar.FrontBogie.track == trk)
					{
						return true;
					}
					if (trainCar.RearBogie != null && trainCar.RearBogie.track == trk)
					{
						return true;
					}
				}
			}
			return false;
		}

		// CRITICAL INVARIANT [AIE-12]: Dynamic Detour Direct Path Adoption & Recalculation Exit Gate
		private bool RecalculateRouteToDestination(bool allowAsync = false)
		{
			if (CurrentTrack == null || RailGraph.Instance == null || !RailGraph.Instance.IsInitialized)
			{
				return false;
			}
			RailTrack railTrack = ((CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0) ? CurrentPath.Tracks[CurrentPath.Tracks.Count - 1] : null);
			if (railTrack == null && !string.IsNullOrEmpty(DestinationTrackName))
			{
				railTrack = RailGraph.Instance.GetTrackByName(DestinationTrackName);
			}
			if (railTrack == null || railTrack == CurrentTrack)
			{
				return false;
			}
			PathfinderOptions pathfinderOptions = new PathfinderOptions();
			pathfinderOptions.Requester = this;
			pathfinderOptions.RequesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
			pathfinderOptions.PreventPlayerOvertake = false;
			pathfinderOptions.AvoidOccupiedTracks = true;
			pathfinderOptions.StrictlyAvoidOccupied = true;
			PathfinderOptions options = pathfinderOptions;
			Pathfinder pathfinder = new Pathfinder(RailGraph.Instance);
			bool flag = TargetDirection >= 0f;
			if (_trainCar != null && _trainCar.FrontBogie != null && _trainCar.RearBogie != null && _trainCar.FrontBogie.traveller != null && _trainCar.RearBogie.traveller != null && _trainCar.FrontBogie.track == _trainCar.RearBogie.track)
			{
				flag = _trainCar.FrontBogie.traveller.Span >= _trainCar.RearBogie.traveller.Span;
				if (_ambientLockedReverser < 0f)
				{
					flag = !flag;
				}
			}
			string text = null;
			string text2 = null;
			RailPath path;
			if (allowAsync)
			{
				text = BuildRerouteKey("RCF", CurrentTrack, null, railTrack, flag);
				if (!TryGetAsyncReroute(text, CurrentTrack, railTrack, flag, options, out path))
				{
					_reroutePending = true;
					return false;
				}
				if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count <= 1)
				{
					text2 = BuildRerouteKey("RCB", CurrentTrack, null, railTrack, !flag);
					if (!TryGetAsyncReroute(text2, CurrentTrack, railTrack, !flag, options, out path))
					{
						_reroutePending = true;
						return false;
					}
				}
				if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count <= 1)
				{
					if (text != null)
					{
						_consumedRerouteKeys.Remove(text);
					}
					if (text2 != null)
					{
						_consumedRerouteKeys.Remove(text2);
					}
				}
			}
			else
			{
				path = pathfinder.FindPath(CurrentTrack, railTrack, flag, options);
				if (path == null || !path.IsValid || path.Tracks == null || path.Tracks.Count <= 1)
				{
					path = pathfinder.FindPath(CurrentTrack, railTrack, !flag, options);
				}
			}
			if (path != null && path.IsValid && path.Tracks != null && path.Tracks.Count > 1)
			{
				CurrentPath = path;
				CurrentPathTrackIndex = 0;
				_ambientLockedReverser = 0f;
				_upcomingTracks.Clear();
				for (int i = 0; i < CurrentPath.Tracks.Count; i++)
				{
					_upcomingTracks.Add(CurrentPath.Tracks[i]);
				}
				return true;
			}
			return false;
		}

		private void ApplyClosedLoopReverseControl(float targetReverseSpeedKmh, float dt)
		{
			TargetSpeedKmh = targetReverseSpeedKmh;
			float num = ((_ambientLockedReverser != 0f) ? (0f - _ambientLockedReverser) : (0f - TargetDirection));
			if (num == 0f)
			{
				num = -1f;
			}
			_desiredReverser = num;
			_commandedReverser = num;
			float obstDist;
			if (IsRearRollbackPathObstructed(out obstDist) && obstDist < 30f)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = 0f;
				return;
			}
			if (Mathf.Abs(_currentReverser - _commandedReverser) > 0.1f)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = Mathf.Max(_commandedTrainBrake, 0.6f);
				_commandedIndependentBrake = 0f;
				return;
			}
			float currentSpeedKmh = CurrentSpeedKmh;
			if (currentSpeedKmh > targetReverseSpeedKmh + 1.5f)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				float num2 = currentSpeedKmh - targetReverseSpeedKmh;
				float target = Mathf.Clamp(num2 * 0.1f, 0.25f, 0.75f);
				_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, target, 0.6f * dt);
				_commandedIndependentBrake = 0f;
			}
			else if (currentSpeedKmh > targetReverseSpeedKmh - 1f)
			{
				_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0f, 0.5f * dt);
				_commandedIndependentBrake = 0f;
				_commandedThrottle = Mathf.MoveTowards(_commandedThrottle, 0.1f, 0.15f * dt);
				_rampThrottle = _commandedThrottle;
			}
			else
			{
				_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0f, 0.8f * dt);
				_commandedIndependentBrake = 0f;
				float target2 = ((currentSpeedKmh < 3f) ? 0.35f : 0.25f);
				_commandedThrottle = Mathf.MoveTowards(_commandedThrottle, target2, 0.2f * dt);
				_rampThrottle = _commandedThrottle;
			}
		}

		private void UpdateSingleTrackReverseProtocol(float dt)
		{
			if (_reverseProtocolStage == SingleTrackReverseStage.None)
			{
				return;
			}
			TrainCar occupyingCar;
			switch (_reverseProtocolStage)
			{
			// CRITICAL INVARIANT [AIE-13]: SingleTrackReverse HaltAndYield
			case SingleTrackReverseStage.HaltAndYield:
				TargetSpeedKmh = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = 0f;
				if (CurrentSpeedKmh < 0.2f)
				{
					_commandedIndependentBrake = 1f;
					_reverseProtocolTimer = 0f;
					_reverseProtocolStage = SingleTrackReverseStage.SoundingHorn;
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' stopped and yielded on single track. Sounding reverse horn bursts.", LocoId));
					}
				}
				break;
			// CRITICAL INVARIANT [AIE-13]: SingleTrackReverse SoundingHorn
			case SingleTrackReverseStage.SoundingHorn:
			{
				_reverseProtocolTimer += dt;
				float reverseProtocolTimer = _reverseProtocolTimer;
				if (reverseProtocolTimer < 2f)
				{
					if (_controlsOverrider != null && _controlsOverrider.Horn != null)
					{
						_controlsOverrider.Horn.Set(1f);
					}
					break;
				}
				if (reverseProtocolTimer < 3f)
				{
					if (_controlsOverrider != null && _controlsOverrider.Horn != null)
					{
						_controlsOverrider.Horn.Set(0f);
					}
					break;
				}
				if (reverseProtocolTimer < 5f)
				{
					if (_controlsOverrider != null && _controlsOverrider.Horn != null)
					{
						_controlsOverrider.Horn.Set(1f);
					}
					break;
				}
				if (reverseProtocolTimer < 6f)
				{
					if (_controlsOverrider != null && _controlsOverrider.Horn != null)
					{
						_controlsOverrider.Horn.Set(0f);
					}
					break;
				}
				if (reverseProtocolTimer < 8f)
				{
					if (_controlsOverrider != null && _controlsOverrider.Horn != null)
					{
						_controlsOverrider.Horn.Set(1f);
					}
					break;
				}
				if (_controlsOverrider != null && _controlsOverrider.Horn != null)
				{
					_controlsOverrider.Horn.Set(0f);
				}
				_reverseProtocolStage = SingleTrackReverseStage.SettingLights;
				break;
			}
			case SingleTrackReverseStage.SettingLights:
				if (_controlsOverrider != null)
				{
					if (_controlsOverrider.HeadlightsFront != null)
					{
						_controlsOverrider.HeadlightsFront.Set(0f);
					}
					if (_controlsOverrider.HeadlightsRear != null)
					{
						_controlsOverrider.HeadlightsRear.Set(1f);
					}
				}
				FindNearestTrailingSiding(out _trailingSidingSwitch, out _trailingSidingBranch, out _trailingSidingTrack, out _trailingSidingMainlineBranch);
				_reverseProtocolStage = SingleTrackReverseStage.ReversingToSiding;
				_reverseProtocolTimer = 0f;
				break;
			// CRITICAL INVARIANT [AIE-13]: ReversingToSiding stall-only reverse timer
			case SingleTrackReverseStage.ReversingToSiding:
				if (CurrentSpeedKmh < 1.0f)
				{
					_reverseProtocolTimer += dt;
				}
				else
				{
					_reverseProtocolTimer = 0f;
				}
				if (_reverseProtocolTimer > 120f)
				{
					if (Main.ModEntry != null && Main.ModEntry.Logger != null)
					{
						Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Train '{0}' reverse to siding stalled for {1:F0}s. Securing train at terminus stop.", LocoId, _reverseProtocolTimer));
					}
					_reverseProtocolStage = SingleTrackReverseStage.None;
					_reverseProtocolTimer = 0f;
					EnterTerminusStop();
					break;
				}
				ApplyClosedLoopReverseControl(15f, dt);
				if (_trailingSidingSwitch != null)
				{
					Vector3 position = _trailingSidingSwitch.transform.position;
					Vector3 trailingCarPosition = GetTrailingCarPosition();
					float num = Vector3.Distance(trailingCarPosition, position);
					if (num < 120f)
					{
						JunctionController.Instance.RequestJunctionAlignment(_trailingSidingSwitch, _trailingSidingBranch, this);
					}
					if (_trailingSidingTrack != null && IsAnyCarOnTrack(_trailingSidingTrack))
					{
						_reverseProtocolStage = SingleTrackReverseStage.DuckingIntoSiding;
						_reverseProtocolTimer = 0f;
					}
				}
				else if (CurrentTrack != null && (Pathfinder.IsPassingOrSidingTrack(CurrentTrack) || CurrentTrack.name.IndexOf("siding", StringComparison.OrdinalIgnoreCase) >= 0))
				{
					_reverseProtocolStage = SingleTrackReverseStage.DuckingIntoSiding;
					_reverseProtocolTimer = 0f;
				}
				break;
			case SingleTrackReverseStage.DuckingIntoSiding:
			{
				if (CurrentSpeedKmh < 1f)
				{
					_reverseProtocolTimer += dt;
				}
				else
				{
					_reverseProtocolTimer = 0f;
				}
				if (_reverseProtocolTimer > 120f)
				{
					if (Main.ModEntry != null && Main.ModEntry.Logger != null)
					{
						Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Train '{0}' ducking into siding timed out after {1:F0}s. Securing train in siding.", LocoId, _reverseProtocolTimer));
					}
					_reverseProtocolStage = SingleTrackReverseStage.SecuredInSiding;
					_reverseProtocolTimer = 0f;
					break;
				}
				ApplyClosedLoopReverseControl(10f, dt);
				bool flag5 = false;
				if (_trailingSidingSwitch != null)
				{
					RailTrack railTrack3 = null;
					if (_trailingSidingSwitch.outBranches != null && _trailingSidingMainlineBranch < _trailingSidingSwitch.outBranches.Count)
					{
						Junction.Branch branch = _trailingSidingSwitch.outBranches[_trailingSidingMainlineBranch];
						if (branch != null)
						{
							railTrack3 = branch.track;
						}
					}
					RailTrack railTrack4 = ((_trailingSidingSwitch.inBranch != null && _trailingSidingSwitch.inBranch.track != _trailingSidingTrack) ? _trailingSidingSwitch.inBranch.track : null);
					RailTrack railTrack5 = ((_trainCar != null && _trainCar.FrontBogie != null) ? _trainCar.FrontBogie.track : null);
					RailTrack railTrack6 = ((_trainCar != null && _trainCar.RearBogie != null) ? _trainCar.RearBogie.track : null);
					bool flag6 = railTrack5 != null && railTrack6 != null && railTrack5 != railTrack3 && railTrack6 != railTrack3 && railTrack5 != railTrack4 && railTrack6 != railTrack4;
					if (flag6 && CurrentPath != null && CurrentPath.Tracks != null)
					{
						for (int j = 0; j < CurrentPath.Tracks.Count; j++)
						{
							RailTrack railTrack7 = CurrentPath.Tracks[j];
							if (!(railTrack7 == null) && !(railTrack7 == _trailingSidingTrack) && (railTrack5 == railTrack7 || railTrack6 == railTrack7 || IsAnyCarOnTrack(railTrack7)))
							{
								flag6 = false;
								break;
							}
						}
					}
					Vector3 a2 = ((_trainCar != null) ? _trainCar.transform.position : Vector3.zero);
					Vector3 position = _trailingSidingSwitch.transform.position;
					float num2 = Vector3.Distance(a2, position);
					bool flag7 = (railTrack3 != null && IsAnyCarOnTrack(railTrack3)) || (railTrack4 != null && IsAnyCarOnTrack(railTrack4));
					Trainset requesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
					TrainCar occCar;
					Trainset reqTrainset = requesterTrainset;
					// CRITICAL INVARIANT [AIE-12]: Siding reverse clearance
					bool flag = JunctionController.IsJunctionPhysicallyOccupied(_trailingSidingSwitch, out occCar, reqTrainset);
					bool flag8 = GoverningSignal == null || DistanceToGoverningSignal > 65f || DistanceToGoverningSignal >= 5f;
					bool signalClearanceMet = flag8;
					if (GoverningSignal != null && DistanceToGoverningSignal < 5f && num2 < 60f)
					{
						flag8 = false;
					}
					if (flag6 && num2 >= 22f && !flag7 && !flag && (flag8 || num2 >= 60f))
					{
						flag5 = true;
					}
				}
				else
				{
					flag5 = CurrentSpeedKmh < 1f;
				}
				if (flag5)
				{
					_reverseProtocolStage = SingleTrackReverseStage.SecuredInSiding;
					_reverseProtocolTimer = 0f;
				}
				break;
			}
			case SingleTrackReverseStage.SecuredInSiding:
			{
				TargetSpeedKmh = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = ((CurrentSpeedKmh < 0.2f) ? 1f : 0f);
				if (CurrentSpeedKmh < 0.2f && _controlsOverrider != null && _controlsOverrider.Handbrake != null)
				{
					_controlsOverrider.Handbrake.Set(1f);
				}
				ReleaseActiveCorridorToken();
				State = EngineState.Idle;
				RailTrack railTrack = null;
				if (_trailingSidingSwitch != null && _trailingSidingSwitch.outBranches != null && _trailingSidingMainlineBranch < _trailingSidingSwitch.outBranches.Count)
				{
					Junction.Branch branch = _trailingSidingSwitch.outBranches[_trailingSidingMainlineBranch];
					if (branch != null)
					{
						railTrack = branch.track;
					}
				}
				RailTrack railTrack2 = ((_trailingSidingSwitch != null && _trailingSidingSwitch.inBranch != null && _trailingSidingSwitch.inBranch.track != _trailingSidingTrack) ? _trailingSidingSwitch.inBranch.track : null);
				if (_trailingSidingSwitch != null && JunctionController.Instance != null)
				{
					Trainset requesterTrainset = ((_trainCar != null) ? _trainCar.trainset : null);
					TrainCar occupyingCar2;
					bool flag = JunctionController.IsJunctionPhysicallyOccupied(_trailingSidingSwitch, out occupyingCar2, requesterTrainset);
					bool flag2 = (railTrack != null && IsAnyCarOnTrack(railTrack)) || (railTrack2 != null && IsAnyCarOnTrack(railTrack2));
					if (!flag && !flag2)
					{
						if (_trailingSidingSwitch.selectedBranch != _trailingSidingMainlineBranch)
						{
							JunctionController.Instance.RequestJunctionAlignment(_trailingSidingSwitch, _trailingSidingMainlineBranch, this, false);
						}
						JunctionController.Instance.ReleaseJunction(_trailingSidingSwitch, this);
					}
				}
				bool flag3 = false;
				Trainset trainset = ((_trainCar != null) ? _trainCar.trainset : null);
				Vector3 a = ((_trainCar != null) ? _trainCar.transform.position : Vector3.zero);
				if (railTrack != null && (SignalRegistry.IsTrackOccupiedByPlayer(railTrack, trainset) || SignalRegistry.IsTrackReservedByPlayerSignal(railTrack) || (RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(railTrack, null, trainset))))
				{
					flag3 = true;
				}
				if (!flag3 && railTrack2 != null && (SignalRegistry.IsTrackOccupiedByPlayer(railTrack2, trainset) || SignalRegistry.IsTrackReservedByPlayerSignal(railTrack2) || (RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(railTrack2, null, trainset))))
				{
					flag3 = true;
				}
				if (!flag3 && PlayerManager.PlayerTransform != null && Vector3.Distance(a, PlayerManager.PlayerTransform.position) < 400f)
				{
					flag3 = true;
				}
				if (!flag3 && TrafficManager.Instance != null && TrafficManager.Instance.ActiveEngineers != null)
				{
					List<AIEngineer> activeEngineers = TrafficManager.Instance.ActiveEngineers;
					for (int i = 0; i < activeEngineers.Count; i++)
					{
						AIEngineer aIEngineer = activeEngineers[i];
						if (aIEngineer != null && aIEngineer != this && aIEngineer.TrainCar != null && Vector3.Distance(a, aIEngineer.TrainCar.transform.position) < 400f)
						{
							flag3 = true;
							break;
						}
					}
				}
				if (flag3)
				{
					_reverseProtocolTimer = 0f;
					break;
				}
				_reverseProtocolTimer += dt;
				if (!(_reverseProtocolTimer >= 10f))
				{
					break;
				}
				_reroutePending = false;
				bool flag4 = RecalculateRouteToDestination(true);
				if (!flag4 && _reroutePending)
				{
					break;
				}
				if (flag4)
				{
					if (_controlsOverrider != null)
					{
						if (_controlsOverrider.Handbrake != null)
						{
							_controlsOverrider.Handbrake.Set(0f);
						}
						if (_controlsOverrider.HeadlightsFront != null)
						{
							_controlsOverrider.HeadlightsFront.Set(1f);
						}
						if (_controlsOverrider.HeadlightsRear != null)
						{
							_controlsOverrider.HeadlightsRear.Set(0f);
						}
					}
					_commandedIndependentBrake = 0f;
					_commandedTrainBrake = 0f;
					_reverseProtocolStage = SingleTrackReverseStage.None;
					_reverseProtocolTimer = 0f;
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' siding wait complete (corridor clear for 10s). Resuming forward route to destination.", LocoId));
					}
					State = EngineState.Accelerating;
				}
				else
				{
					_reverseProtocolTimer = 5f;
				}
				break;
			}
			}
		}

		// CRITICAL INVARIANT [AIE-09]: Off-Route Spur Reverse Interlocking
		private void UpdateOffRouteReverseProtocol(float dt)
		{
			if (_offRouteReverseStage == OffRouteReverseStage.None)
			{
				return;
			}
			_offRouteReverseTimer += dt;
			if (_offRouteReverseTimer > 90f)
			{
				if (Main.ModEntry != null && Main.ModEntry.Logger != null)
				{
					Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Train '{0}' off-route reverse protocol timed out after {1:F0}s. Parking train safely.", LocoId, _offRouteReverseTimer));
				}
				_offRouteReverseStage = OffRouteReverseStage.None;
				_offRouteReverseTimer = 0f;
				EnterTerminusStop();
				return;
			}
			switch (_offRouteReverseStage)
			{
			case OffRouteReverseStage.Halting:
				TargetSpeedKmh = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = 0f;
				if (CurrentSpeedKmh < 0.2f)
				{
					_commandedIndependentBrake = 1f;
					_offRouteReverseStage = OffRouteReverseStage.ReversingBehindSwitch;
					_offRouteReverseTimer = 0f;
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' halted on spur. Commencing reverse behind switch '{1}'.", LocoId, (_offRouteDivergingSwitch != null) ? _offRouteDivergingSwitch.name : "unknown"));
					}
				}
				break;
			// CRITICAL INVARIANT [AIE-09]: OffRouteReverse ReversingBehindSwitch
			case OffRouteReverseStage.ReversingBehindSwitch:
			{
				Trainset trainset = ((_trainCar != null) ? _trainCar.trainset : null);
				if (_offRouteRecoveryTargetTrack != null && ((RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(_offRouteRecoveryTargetTrack, null, trainset)) || SignalRegistry.IsTrackOccupiedByPlayer(_offRouteRecoveryTargetTrack, trainset)))
				{
					TargetSpeedKmh = 0f;
					_commandedThrottle = 0f;
					_rampThrottle = 0f;
					_commandedTrainBrake = 1f;
					_commandedIndependentBrake = ((CurrentSpeedKmh < 0.2f) ? 1f : 0f);
					_offRouteReverseTimer = Mathf.Max(0f, _offRouteReverseTimer - dt);
					break;
				}
				Junction junction;
				byte requiredBranch;
				if (_offRouteDivergingSwitch != null && _offRouteRecoveryTargetTrack != null && CurrentTrack != null && CurrentTrack != _offRouteRecoveryTargetTrack && (SignalRegistry.TryGetJunctionBetweenTracks(_offRouteRecoveryTargetTrack, CurrentTrack, out junction, out requiredBranch) || SignalRegistry.TryGetJunctionBetweenTracks(CurrentTrack, _offRouteRecoveryTargetTrack, out junction, out requiredBranch)) && junction == _offRouteDivergingSwitch && _offRouteDivergingSwitch.selectedBranch != requiredBranch)
				{
					JunctionController.Instance.RequestJunctionAlignment(_offRouteDivergingSwitch, requiredBranch, this, false);
				}
				ApplyClosedLoopReverseControl(15f, dt);
				bool flag2 = false;
				if (_offRouteDivergingSwitch != null)
				{
					Vector3 position = _offRouteDivergingSwitch.transform.position;
					TrainCar occupyingCar;
					bool flag3 = JunctionController.IsJunctionPhysicallyOccupied(_offRouteDivergingSwitch, out occupyingCar);
					bool flag4 = false;
					List<TrainCar> list = ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null);
					if (list != null && list.Count > 0)
					{
						for (int i = 0; i < list.Count; i++)
						{
							TrainCar trainCar = list[i];
							if (trainCar == null)
							{
								continue;
							}
							if (Vector3.Distance(trainCar.transform.position, position) < 22f)
							{
								flag3 = true;
							}
							if (_offRouteRecoveryTargetTrack != null)
							{
								bool flag5 = trainCar.FrontBogie == null || trainCar.FrontBogie.track == null || trainCar.FrontBogie.track == _offRouteRecoveryTargetTrack || (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Contains(trainCar.FrontBogie.track));
								bool flag6 = trainCar.RearBogie == null || trainCar.RearBogie.track == null || trainCar.RearBogie.track == _offRouteRecoveryTargetTrack || (CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Contains(trainCar.RearBogie.track));
								if (!flag5 || !flag6)
								{
									flag4 = true;
								}
							}
						}
					}
					else if (_trainCar != null && Vector3.Distance(_trainCar.transform.position, position) < 22f)
					{
						flag3 = true;
					}
					if (!flag3 && !flag4 && CurrentTrack == _offRouteRecoveryTargetTrack)
					{
						flag2 = true;
					}
				}
				else if (CurrentTrack == _offRouteRecoveryTargetTrack)
				{
					flag2 = true;
				}
				if (flag2)
				{
					_offRouteReverseStage = OffRouteReverseStage.RealigningAndResuming;
					_offRouteReverseTimer = 0f;
					if (Main.IsDebugLoggingEnabled)
					{
						Main.LogDebug(string.Format("[AITraffic] Train '{0}' cleared switch into target track '{1}'. Realigning and resuming.", LocoId, (_offRouteRecoveryTargetTrack != null) ? _offRouteRecoveryTargetTrack.name : "unknown"));
					}
				}
				break;
			}
			// CRITICAL INVARIANT [AIE-09]: OffRouteReverse RealigningAndResuming
			case OffRouteReverseStage.RealigningAndResuming:
			{
				TargetSpeedKmh = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = ((CurrentSpeedKmh < 0.2f) ? 1f : 0f);
				if (!(CurrentSpeedKmh < 0.2f))
				{
					break;
				}
				bool flag = true;
				if (_offRouteDivergingSwitch != null)
				{
					flag = JunctionController.Instance.RequestJunctionAlignment(_offRouteDivergingSwitch, _offRouteMainlineBranch, this, false);
					JunctionController.Instance.ReleaseJunction(_offRouteDivergingSwitch, this);
				}
				if (!flag)
				{
					_offRouteReverseTimer = Mathf.Max(0f, _offRouteReverseTimer - dt);
					break;
				}
				_reroutePending = false;
				if (!RecalculateRouteToDestination(true) && _reroutePending)
				{
					_offRouteReverseTimer = Mathf.Max(0f, _offRouteReverseTimer - dt);
					break;
				}
				float ambientLockedReverser = (_commandedReverser = (_desiredReverser = ((_ambientLockedReverser != 0f) ? _ambientLockedReverser : ((TargetDirection != 0f) ? TargetDirection : 1f))));
				_commandedIndependentBrake = 0f;
				_commandedTrainBrake = 0f;
				_offRouteReverseStage = OffRouteReverseStage.None;
				_offRouteReverseTimer = 0f;
				_ambientLockedReverser = ambientLockedReverser;
				State = EngineState.Accelerating;
				if (Main.IsDebugLoggingEnabled)
				{
					Main.LogDebug(string.Format("[AITraffic] Train '{0}' off-route recovery complete. Resuming forward journey.", LocoId));
				}
				break;
			}
			}
		}

		private void UpdateSpeedProfile(float dt)
		{
			if (_isEmergencyStop || _isWaitingOutsideStationThroat)
			{
				TargetSpeedKmh = 0f;
				TargetSpeedMs = 0f;
				return;
			}
			_speedProfileUpdateCooldown -= dt;
			if (_speedProfileUpdateCooldown > 0f)
			{
				return;
			}
			_speedProfileUpdateCooldown = 0.25f + UnityEngine.Random.Range(0f, 0.05f);
			RailTrack currentTrack = null;
			if (_trainCar != null)
			{
				if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
				{
					currentTrack = _trainCar.FrontBogie.track;
				}
				else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
				{
					currentTrack = _trainCar.RearBogie.track;
				}
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
			_filteredSignalsCache.Clear();
			List<SignalRegistry.UpcomingSignal> filteredSignalsCache = _filteredSignalsCache;
			float num = EffectiveObstacleDistance;
			// CRITICAL INVARIANT [AIE-18]: Enforce service stop 15m before governing main signal during 120s player switch yield
			if (_playerYieldWaitTimer > 0f && !float.IsInfinity(DistanceToGoverningSignal))
			{
				float yieldStopDist = ((CurrentSpeedKmh < 1.5f && DistanceToGoverningSignal <= 25f) ? 0f : Mathf.Max(0f, DistanceToGoverningSignal - 15f));
				num = Mathf.Min(num, yieldStopDist);
			}
			if (_upcomingSignals != null)
			{
				for (int i = 0; i < _upcomingSignals.Count; i++)
				{
					SignalRegistry.UpcomingSignal item = _upcomingSignals[i];
					if (_isShuntingMode)
					{
						Signals.Game.Signal signal = ((item.Signal != null && item.Signal.Parent != null) ? item.Signal.Parent : item.Signal);
						Signals.Game.Signal signal2 = ((_shuntingBypassedSignal != null && _shuntingBypassedSignal.Parent != null) ? _shuntingBypassedSignal.Parent : _shuntingBypassedSignal);
						if (signal != null && signal == signal2)
						{
							continue;
						}
					}
					// CRITICAL INVARIANT [AIE-17]: Block occupancy & unaligned switch stop enforcement at governing signal boundary
					bool flag = false;
					if (item.Distance < 400f && _upcomingSignalBlocks != null && _upcomingSignalBlocks.Count > 0)
					{
						SignalBlockInfo signalBlockInfo = _upcomingSignalBlocks[0];
						if (signalBlockInfo != null && (signalBlockInfo.EntrySignal == item.Signal || signalBlockInfo.ExitSignal == item.Signal) && SignalRegistry.IsGoverningSignal(item.Signal))
						{
							bool switchesUnaligned = signalBlockInfo.Switches != null && signalBlockInfo.Switches.Count > 0 && !signalBlockInfo.AreSwitchesAligned;
							bool blockOccupied = !signalBlockInfo.IsClear || signalBlockInfo.IsPlayerOccupied;
							if (switchesUnaligned || blockOccupied)
							{
								flag = true;
							}
						}
					}
					if (flag)
					{
						num = Mathf.Min(num, Mathf.Max(0f, item.Distance - 15f));
					}
					filteredSignalsCache.Add(item);
				}
			}
			float trackWetness = GetTrackWetness();
			float num2 = 0.45f;
			if (IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null)
			{
				num2 *= IniConfigManager.Instance.Physics.DecelerationMultiplier;
			}
			float b = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics.WetDecelerationMultiplier : 0.6f);
			float num3 = Mathf.Lerp(1f, b, trackWetness);
			SpeedProfiler.ServiceDeceleration = Mathf.Max(0.2f, num2 * num3);
			SpeedProfileResult currentSpeedProfile = SpeedProfiler.CalculateTargetSpeed(currentTrack, currentSpan, TargetDirection, UpcomingTracks, filteredSignalsCache, num, DistanceToDestination, IsStationDestination, IsTerminusDestination);
			float num4 = 120f;
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			if (list != null && list.Count > 0 && SpeedProfiler != null)
			{
				for (int j = 0; j < list.Count; j++)
				{
					TrainCar trainCar = list[j];
					if (trainCar == null)
					{
						continue;
					}
					if (trainCar.FrontBogie != null && trainCar.FrontBogie.track != null)
					{
						float trackSpeedLimit = SpeedProfiler.GetTrackSpeedLimit(trainCar.FrontBogie.track);
						if (trackSpeedLimit < num4)
						{
							num4 = trackSpeedLimit;
						}
					}
					if (trainCar.RearBogie != null && trainCar.RearBogie.track != null)
					{
						float trackSpeedLimit2 = SpeedProfiler.GetTrackSpeedLimit(trainCar.RearBogie.track);
						if (trackSpeedLimit2 < num4)
						{
							num4 = trackSpeedLimit2;
						}
					}
				}
			}
			if (num4 < currentSpeedProfile.TargetSpeedKmh)
			{
				currentSpeedProfile.TargetSpeedKmh = num4;
				currentSpeedProfile.TargetSpeedMs = SpeedProfileGenerator.KmHToMs(num4);
				currentSpeedProfile.LimitingReason = SpeedLimitReason.CurvatureRadius;
			}
			if (_isShuntingMode && currentSpeedProfile.TargetSpeedKmh > 15f)
			{
				currentSpeedProfile.TargetSpeedKmh = 15f;
				currentSpeedProfile.TargetSpeedMs = SpeedProfileGenerator.KmHToMs(15f);
				currentSpeedProfile.LimitingReason = SpeedLimitReason.YardRestriction;
			}
			CurrentSpeedProfile = currentSpeedProfile;
			TargetSpeedKmh = currentSpeedProfile.TargetSpeedKmh;
			TargetSpeedMs = currentSpeedProfile.TargetSpeedMs;
		}

		private void EnsureLocoIdentityCached()
		{
			if (!_cachedLocoIdentityInitialized && !(_trainCar == null))
			{
				string text = ((_trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
				_cachedIsDE2 = _trainCar.carType == TrainCarType.LocoShunter || text.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0;
				_cachedIsDE6 = _trainCar.carType == TrainCarType.LocoDiesel || text.IndexOf("DE6", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Diesel", StringComparison.OrdinalIgnoreCase) >= 0;
				_cachedIsBE2 = _trainCar.carType == TrainCarType.LocoMicroshunter || text.IndexOf("BE2", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Microshunter", StringComparison.OrdinalIgnoreCase) >= 0;
				if (_cachedIsDE2)
				{
					_cachedNumMotors = 2;
				}
				else if (_cachedIsDE6)
				{
					_cachedNumMotors = 6;
				}
				else
				{
					_cachedNumMotors = 1;
				}
				_cachedLocoIdentityInitialized = true;
			}
		}

		private void InitializeSimulationPorts()
		{
			_temperaturePorts.Clear();
			_amperagePorts.Clear();
			_amperagePortIsTotal.Clear();
			_portAmpsPerTM = null;
			_portTotalAmps = null;
			_portEngineOn = null;
			_portsInitialized = false;
			_cachedLocoIdentityInitialized = false;
			if (_trainCar == null || _trainCar.SimController == null || _trainCar.SimController.SimulationFlow == null)
			{
				return;
			}
			EnsureLocoIdentityCached();
			AIDamageImmunityPatches.RegisterAISimComponents(_trainCar);
			CollectSimulationPortsFromLoco(_trainCar, true);
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar = list[i];
					if (trainCar != null && trainCar != _trainCar && trainCar.IsLoco && TrainSpawner.IsSupportedAILocomotive(trainCar))
					{
						AIDamageImmunityPatches.RegisterAISimComponents(trainCar);
						CollectSimulationPortsFromLoco(trainCar, false);
					}
				}
			}
			_portsInitialized = true;
		}

		// CRITICAL INVARIANT [AIE-14]: CollectSimulationPortsFromLoco
		private void CollectSimulationPortsFromLoco(TrainCar loco, bool isLead)
		{
			if (loco == null || loco.SimController == null || loco.SimController.SimulationFlow == null)
			{
				return;
			}
			IReadOnlyList<Port> allPorts = loco.SimController.SimulationFlow.AllPorts;
			if (allPorts == null)
			{
				return;
			}
			for (int i = 0; i < allPorts.Count; i++)
			{
				Port port = allPorts[i];
				if (port == null || string.IsNullOrEmpty(port.id))
				{
					continue;
				}
				if (port.valueType == PortValueType.TEMPERATURE || port.id.IndexOf("TEMPERATURE", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("engineTemp", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("oilTemp", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("waterTemp", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("tmTemp", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					_temperaturePorts.Add(port);
				}
				if ((port.valueType == PortValueType.AMPS || port.id.IndexOf("AMPS", StringComparison.OrdinalIgnoreCase) >= 0) && port.id.IndexOf("MAX", StringComparison.OrdinalIgnoreCase) < 0 && port.id.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase) < 0 && port.id.IndexOf("EXT_IN", StringComparison.OrdinalIgnoreCase) < 0)
				{
					_amperagePorts.Add(port);
					_amperagePortIsTotal.Add(port.id.IndexOf("TOTAL", StringComparison.OrdinalIgnoreCase) >= 0);
					if (isLead)
					{
						if (port.id.IndexOf("AMPS_PER_TM", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							_portAmpsPerTM = port;
						}
						else if (port.id.IndexOf("TOTAL_AMPS", StringComparison.OrdinalIgnoreCase) >= 0)
						{
							_portTotalAmps = port;
						}
					}
				}
				if (isLead)
				{
					if (_portEngineOn == null && (port.id.IndexOf("ENGINE_ON", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("engineOn", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						_portEngineOn = port;
					}
					else if (_portEngineRpm == null && (port.id.IndexOf("ENGINE_RPM", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.IndexOf("engineRpm", StringComparison.OrdinalIgnoreCase) >= 0 || port.id.EndsWith(".RPM", StringComparison.OrdinalIgnoreCase)))
					{
						_portEngineRpm = port;
					}
				}
			}
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
				return _portEngineRpm.Value > 100f;
			}
			return true;
		}

		private float GetTractionAuthorityMultiplier()
		{
			return Mathf.Min(_slipThrottleReduction, Mathf.Min(_thermalThrottleLimit, _overcurrentThrottleLimit));
		}

		private static float MapDirectionToReverserControl(float dir)
		{
			if (dir > 0.1f)
			{
				return 1f;
			}
			if (dir < -0.1f)
			{
				return 0f;
			}
			return 0.5f;
		}

		public float GetTrackWetness()
		{
			try
			{
				WeatherDriver instance = SingletonBehaviour<WeatherDriver>.Instance;
				if (instance != null)
				{
					if (instance.WetnessValue != null)
					{
						return Mathf.Clamp01(instance.WetnessValue.CurrentValue);
					}
					if (instance.IsRaining)
					{
						return 1f;
					}
				}
			}
			catch
			{
			}
			return 0f;
		}

		public bool IsPrecipitationActive()
		{
			try
			{
				WeatherDriver instance = SingletonBehaviour<WeatherDriver>.Instance;
				if (instance != null)
				{
					if (instance.IsRaining)
					{
						return true;
					}
					if (instance.RainValue != null && instance.RainValue.CurrentValue > 0.05f)
					{
						return true;
					}
					if (instance.WetnessValue != null && instance.WetnessValue.CurrentValue > 0.1f)
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private void UpdateWheelTractionAndSlideProtection(float dt)
		{
			_isWheelSlipping = false;
			_isWheelSliding = false;
			_isLocoWheelSliding = false;
			if (_trainCar != null && _trainCar.SimController != null && _trainCar.SimController.wheelslipController != null && _trainCar.SimController.wheelslipController.IsWheelslipping)
			{
				_isWheelSlipping = true;
			}
			bool flag = IniConfigManager.Instance == null || IniConfigManager.Instance.Physics == null || IniConfigManager.Instance.Physics.EnableWheelSlideProtection;
			if (flag && (_currentTrainBrake > 0.05f || _currentDynamicBrake > 0.05f || _commandedTrainBrake > 0.05f || _commandedDynamicBrake > 0.05f))
			{
				if (_trainCar != null && _trainCar.adhesionController != null && _trainCar.adhesionController.IsWheelSliding)
				{
					_isLocoWheelSliding = true;
					_isWheelSliding = true;
				}
				if (!_isWheelSliding)
				{
					List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
					if (list != null)
					{
						for (int i = 0; i < list.Count; i++)
						{
							TrainCar trainCar = list[i];
							if (!(trainCar == null) && trainCar.adhesionController != null && trainCar.adhesionController.IsWheelSliding)
							{
								_isWheelSliding = true;
								break;
							}
						}
					}
				}
			}
			if (_isWheelSlipping)
			{
				_sanderRequested = true;
				_sandHoldTimer = 2.5f;
				float num = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics.SlipCutFactor : 0.6f);
				float target = ((IsLocoDM3 && CurrentSpeedKmh < 8f) ? 0.65f : (IsHillStarting ? Mathf.Max(0.5f, HillEquilibriumThrottle) : num));
				_slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, target, 4f * dt);
			}
			else
			{
				_slipThrottleReduction = Mathf.MoveTowards(_slipThrottleReduction, 1f, 0.4f * dt);
			}
			if (_isWheelSliding && CurrentSpeedKmh > 1f && flag)
			{
				_sanderRequested = true;
				_sandHoldTimer = 3f;
				if (_isLocoWheelSliding && _hasDynamicBrake)
				{
					_commandedDynamicBrake = 0f;
				}
				_wspBrakeAuthority = Mathf.MoveTowards(_wspBrakeAuthority, 0.55f, 3f * dt);
			}
			else
			{
				_wspBrakeAuthority = Mathf.MoveTowards(_wspBrakeAuthority, 1f, 0.6f * dt);
				if (_sandHoldTimer > 0f)
				{
					_sandHoldTimer -= dt;
					_sanderRequested = true;
				}
			}
			UpdateSanderActuation(dt);
		}

		private void UpdateSanderActuation(float dt)
		{
			if (_controlsOverrider == null || _controlsOverrider.Sander == null)
			{
				return;
			}
			if (HillStage == HillLaunchStage.Stage1_Clean && IsPrecipitationActive())
			{
				HillStage = HillLaunchStage.Stage2_Sanded;
				_sanderRequested = true;
				_stage2ContinuousSandingTimer = 0f;
			}
			if (HillStage == HillLaunchStage.Stage1_Clean)
			{
				_sanderRequested = false;
				_sanderActiveTimer = 0f;
				_sanderRestTimer = 0f;
				_controlsOverrider.Sander.Set(0f);
				return;
			}
			if ((CurrentSpeedKmh < 0.2f && _commandedThrottle < 0.01f && !_isWheelSlipping && !_isWheelSliding) || State == EngineState.Idle || State == EngineState.StationHold || State == EngineState.TerminusStop)
			{
				_sanderRequested = false;
				_sanderActiveTimer = 0f;
				_sanderRestTimer = 0f;
				_controlsOverrider.Sander.Set(0f);
				return;
			}
			if (_sanderRequested)
			{
				if (HillStage == HillLaunchStage.Stage2_Sanded || HillStage == HillLaunchStage.MomentumRunUp || _isWheelSliding || _sandHoldTimer > 0f)
				{
					_controlsOverrider.Sander.Set(1f);
					_sanderActiveTimer += dt;
					_sanderRestTimer = 0f;
				}
				else if (_sanderRestTimer <= 0f)
				{
					_controlsOverrider.Sander.Set(1f);
					_sanderActiveTimer += dt;
					if (_sanderActiveTimer >= 3f)
					{
						_controlsOverrider.Sander.Set(0f);
						_sanderActiveTimer = 0f;
						_sanderRestTimer = 1f;
					}
				}
				else
				{
					_controlsOverrider.Sander.Set(0f);
					_sanderRestTimer -= dt;
				}
			}
			else
			{
				_controlsOverrider.Sander.Set(0f);
				_sanderActiveTimer = 0f;
				if (_sanderRestTimer > 0f)
				{
					_sanderRestTimer -= dt;
				}
			}
			_sanderRequested = false;
		}

		// CRITICAL INVARIANT [AIE-14]: Dynamic Closed-Loop Current Governor
		private void UpdatePowertrainProtection(float dt)
		{
			if (!_portsInitialized && _trainCar != null && _trainCar.SimController != null && _trainCar.SimController.SimulationFlow != null)
			{
				InitializeSimulationPorts();
			}
			float num = 0f;
			for (int i = 0; i < _temperaturePorts.Count; i++)
			{
				float value = _temperaturePorts[i].Value;
				if (value > num)
				{
					num = value;
				}
			}
			CurrentMaxTemperature = num;
			if (num >= 105f)
			{
				_isOverheated = true;
			}
			else if (num < 95f)
			{
				_isOverheated = false;
			}
			IsOverheated = _isOverheated;
			if (_isOverheated)
			{
				_thermalThrottleLimit = 0f;
			}
			else if (num > 95f)
			{
				float t = (num - 95f) / 10f;
				_thermalThrottleLimit = Mathf.Lerp(1f, 0.25f, t);
			}
			else
			{
				_thermalThrottleLimit = 1f;
			}
			ThermalThrottleLimit = _thermalThrottleLimit;
			EnsureLocoIdentityCached();
			bool cachedIsDE = _cachedIsDE2;
			bool cachedIsDE2 = _cachedIsDE6;
			bool cachedIsBE = _cachedIsBE2;
			int cachedNumMotors = _cachedNumMotors;
			float num2 = 0f;
			if (_portAmpsPerTM != null)
			{
				float value2 = _portAmpsPerTM.Value;
				if (!float.IsNaN(value2) && !float.IsInfinity(value2))
				{
					num2 = value2;
				}
			}
			else if (_portTotalAmps != null)
			{
				float value2 = _portTotalAmps.Value;
				if (!float.IsNaN(value2) && !float.IsInfinity(value2))
				{
					num2 = value2 / (float)Mathf.Max(1, cachedNumMotors);
				}
			}
			if (_amperagePorts.Count > 0)
			{
				for (int i = 0; i < _amperagePorts.Count; i++)
				{
					Port port = _amperagePorts[i];
					if (port == null)
					{
						continue;
					}
					float num3 = port.Value;
					if (!float.IsNaN(num3) && !float.IsInfinity(num3) && !float.IsInfinity(num2))
					{
						if (i < _amperagePortIsTotal.Count && _amperagePortIsTotal[i])
						{
							num3 /= (float)Mathf.Max(1, cachedNumMotors);
						}
						if (num3 > num2)
						{
							num2 = num3;
						}
					}
				}
			}
			CurrentAmpsPerTM = num2;
			if (!cachedIsDE && !cachedIsDE2 && !cachedIsBE)
			{
				_overcurrentThrottleLimit = 1f;
			}
			else
			{
				float num4 = (cachedIsDE2 ? 800f : (cachedIsDE ? 520f : 400f));
				float num5 = (cachedIsDE2 ? 1000.0f : (cachedIsDE ? 650.0f : 500.0f));
				if (num2 >= num5)
				{
					float t2 = Mathf.Clamp01((num2 - num5) / 150f);
					float target = Mathf.Lerp(0.25f, 0.15f, t2);
					_overcurrentThrottleLimit = Mathf.MoveTowards(_overcurrentThrottleLimit, target, 0.6f * dt);
				}
				else if (num2 > num4)
				{
					float t = (num2 - num4) / Mathf.Max(1f, num5 - num4);
					float target = Mathf.Lerp(1f, 0.25f, t);
					_overcurrentThrottleLimit = Mathf.MoveTowards(_overcurrentThrottleLimit, target, 0.4f * dt);
				}
				else
				{
					_overcurrentThrottleLimit = Mathf.MoveTowards(_overcurrentThrottleLimit, 1f, 0.35f * dt);
				}
				_overcurrentThrottleLimit = Mathf.Clamp(_overcurrentThrottleLimit, 0.15f, 1f);
			}
			OvercurrentThrottleLimit = _overcurrentThrottleLimit;
			bool flag = IsEngineRunning();
			if ((cachedIsDE || cachedIsDE2 || cachedIsBE || _portAmpsPerTM != null || _portTotalAmps != null || _amperagePorts.Count > 0) && flag && (_commandedThrottle >= 0.1f || _rampThrottle >= 0.1f || _currentThrottle >= 0.1f) && num2 <= 0.02f)
			{
				_tmZeroCurrentTimer += dt;
				if (_tmZeroCurrentTimer > 3.5f)
				{
					_commandedThrottle = 0f;
					_rampThrottle = 0f;
					_currentThrottle = 0f;
					if (_controlsOverrider != null && _controlsOverrider.Throttle != null)
					{
						_controlsOverrider.Throttle.Set(0f);
					}
					_tmZeroCurrentTimer = 0f;
					if (_breakerCheckCooldown <= 0f)
					{
						_breakerCheckCooldown = 4f;
						CycleBreakerLever();
					}
				}
			}
			else
			{
				_tmZeroCurrentTimer = 0f;
			}
			float forwardSpeed = _trainCar.GetForwardSpeed();
			float num6 = ((_commandedReverser != 0f) ? _commandedReverser : _desiredReverser);
			float num7 = forwardSpeed * num6;
			if (Mathf.Abs(forwardSpeed) < 0.15f)
			{
				_rollbackClearTimer += dt;
			}
			else
			{
				_rollbackClearTimer = 0f;
			}
			if (num7 > 0.15f || (CurrentSpeedKmh > 3f && (State == EngineState.Cruising || State == EngineState.Accelerating)) || IsIntentionalReverseActive)
			{
				_isRollbackDetected = false;
			}
			else if (!flag)
			{
				if (num7 < -0.3f)
				{
					_isRollbackDetected = true;
				}
				else if (num7 > -0.1f || _rollbackClearTimer > 0.4f)
				{
					_isRollbackDetected = false;
				}
			}
			else if (num7 < -0.6f && (State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting))
			{
				_isRollbackDetected = true;
			}
			else if (num7 > -0.1f || _rollbackClearTimer > 0.4f)
			{
				_isRollbackDetected = false;
			}
			if (IsIntentionalReverseActive)
			{
				_isRollbackDetected = false;
			}
			IsRollbackDetected = _isRollbackDetected;
			if (HillStage != HillLaunchStage.RollbackDescent && !IsIntentionalReverseActive)
			{
				float num8 = (IsHillStarting ? (-0.4f) : (-0.08f));
				if (num7 < num8 && (State == EngineState.Accelerating || State == EngineState.Starting))
				{
					_commandedThrottle = 0f;
					_rampThrottle = 0f;
					_commandedIndependentBrake = 0f;
					_commandedTrainBrake = 1f;
					_currentTrainBrake = 1f;
				}
				if (_isRollbackDetected)
				{
					_commandedThrottle = 0f;
					_rampThrottle = 0f;
					_commandedTrainBrake = 1f;
					_commandedIndependentBrake = 0f;
					_currentTrainBrake = 1f;
				}
			}
			if (!flag && (State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting))
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedIndependentBrake = 0f;
				_commandedTrainBrake = Mathf.Max(_commandedTrainBrake, 0.85f);
			}
			_breakerCheckCooldown -= dt;
			if (!(_breakerCheckCooldown <= 0f) || State == EngineState.TerminusStop)
			{
				return;
			}
			if (HasAnyBlownFuse() || HasAnyStoppedConsistLoco())
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_currentThrottle = 0f;
				if (_controlsOverrider != null && _controlsOverrider.Throttle != null)
				{
					_controlsOverrider.Throttle.Set(0f);
				}
				_breakerCheckCooldown = 3f;
				CycleBreakerLever();
			}
			else
			{
				_breakerCheckCooldown = 2f;
			}
		}

		private static bool HasBlownFuseOnLoco(TrainCar loco)
		{
			if (loco == null || loco.SimController == null || loco.SimController.SimulationFlow == null)
			{
				return false;
			}
			IReadOnlyList<Fuse> allFuses = loco.SimController.SimulationFlow.AllFuses;
			if (allFuses != null)
			{
				for (int i = 0; i < allFuses.Count; i++)
				{
					Fuse fuse = allFuses[i];
					if (fuse != null && !fuse.State)
					{
						return true;
					}
				}
			}
			return false;
		}

		private bool HasAnyBlownFuse()
		{
			if (HasBlownFuseOnLoco(_trainCar))
			{
				return true;
			}
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar = list[i];
					if (trainCar != null && trainCar != _trainCar && trainCar.IsLoco && TrainSpawner.IsSupportedAILocomotive(trainCar) && HasBlownFuseOnLoco(trainCar))
					{
						return true;
					}
				}
			}
			return false;
		}

		private bool HasAnyStoppedConsistLoco()
		{
			if (State == EngineState.TerminusStop)
			{
				return false;
			}
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar != null && _trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar = list[i];
					if (trainCar != null && trainCar.IsLoco && TrainSpawner.IsSupportedAILocomotive(trainCar) && trainCar.SimController != null && trainCar.SimController.controlsOverrider != null && trainCar.SimController.controlsOverrider.EngineOnReader != null && !trainCar.SimController.controlsOverrider.EngineOnReader.IsOn)
					{
						return true;
					}
				}
			}
			return false;
		}

		private void CycleBreakerLever()
		{
			if (_trainCar == null)
			{
				return;
			}
			CycleBreakerLeverForLoco(_trainCar, true);
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			if (list == null)
			{
				return;
			}
			for (int i = 0; i < list.Count; i++)
			{
				TrainCar trainCar = list[i];
				if (trainCar != null && trainCar != _trainCar && trainCar.IsLoco && TrainSpawner.IsSupportedAILocomotive(trainCar))
				{
					CycleBreakerLeverForLoco(trainCar, false);
				}
			}
		}

		private void CycleBreakerLeverForLoco(TrainCar loco, bool isLead)
		{
			if (loco == null)
			{
				return;
			}
			try
			{
				bool flag = false;
				AIDamageImmunityPatches.RegisterAISimComponents(loco);
				BaseControlsOverrider baseControlsOverrider = ((loco.SimController != null) ? loco.SimController.controlsOverrider : loco.GetComponent<BaseControlsOverrider>());
				if (baseControlsOverrider != null)
				{
					if (baseControlsOverrider.Throttle != null)
					{
						baseControlsOverrider.Throttle.Set(0f);
					}
					if (baseControlsOverrider.DynamicBrake != null)
					{
						baseControlsOverrider.DynamicBrake.Set(0f);
					}
				}
				PoweredWheelsManager componentInChildren = loco.GetComponentInChildren<PoweredWheelsManager>();
				if (componentInChildren != null)
				{
					componentInChildren.RepairAllPoweredWheels();
				}
				if (loco.SimController != null && loco.SimController.SimulationFlow != null)
				{
					IReadOnlyList<Fuse> allFuses = loco.SimController.SimulationFlow.AllFuses;
					if (allFuses != null)
					{
						for (int i = 0; i < allFuses.Count; i++)
						{
							Fuse fuse = allFuses[i];
							if (fuse != null && !fuse.State)
							{
								fuse.ChangeState(true);
								flag = true;
							}
						}
					}
				}
				InteractableFuseFeedersController componentInChildren2 = loco.GetComponentInChildren<InteractableFuseFeedersController>();
				if (componentInChildren2 != null && componentInChildren2.entries != null)
				{
					FieldInfo field = typeof(InteractableFuseFeeder).GetField("ctrl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					FieldInfo field2 = typeof(InteractableFuseFeeder).GetField("fuse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					for (int j = 0; j < componentInChildren2.entries.Length; j++)
					{
						InteractableFuseFeeder interactableFuseFeeder = componentInChildren2.entries[j];
						if (interactableFuseFeeder == null)
						{
							continue;
						}
						if (field != null)
						{
							ControlImplBase controlImplBase = field.GetValue(interactableFuseFeeder) as ControlImplBase;
							if (controlImplBase != null)
							{
								controlImplBase.SetValue(0f);
								controlImplBase.SetValue(1f);
							}
						}
						if (field2 != null)
						{
							Fuse fuse = field2.GetValue(interactableFuseFeeder) as Fuse;
							if (fuse != null && !fuse.State)
							{
								fuse.ChangeState(true);
								flag = true;
							}
						}
					}
				}
				StartupHelper.Startup(loco);
				if (isLead && baseControlsOverrider != null && baseControlsOverrider.Reverser != null)
				{
					float dir = ((_ambientLockedReverser != 0f) ? _ambientLockedReverser : ((_desiredReverser != 0f) ? _desiredReverser : 1f));
					baseControlsOverrider.Reverser.Set(MapDirectionToReverserControl(dir));
				}
				if (flag && Main.ModEntry != null && Main.ModEntry.Logger != null)
				{
					Main.ModEntry.Logger.Log(string.Format("[AITraffic] Cycled TM breaker lever and restored fuses on '{0}'.", ModCompatManager.GetCarId(loco)));
				}
			}
			catch (Exception ex)
			{
				if (Main.ModEntry != null && Main.ModEntry.Logger != null)
				{
					Main.ModEntry.Logger.Warning(string.Format("[AITraffic] Exception cycling breaker lever on '{0}': {1}", (loco != null) ? ModCompatManager.GetCarId(loco) : "Loco", ex.Message));
				}
			}
		}

		private void UpdateLevelCrossingHorn(float dt)
		{
			if (_controlsOverrider == null || _controlsOverrider.Horn == null)
			{
				return;
			}
			float num = float.PositiveInfinity;
			Vector3 position = _trainCar.transform.position;
			for (int i = 0; i < LevelCrossings.Count; i++)
			{
				float num2 = Vector3.Distance(position, LevelCrossings[i]);
				if (num2 < num)
				{
					num = num2;
				}
			}
			if (num <= 200f && num >= 5f && CurrentSpeedKmh > 2f)
			{
				if (!_isHornPatternActive)
				{
					_isHornPatternActive = true;
					_hornStepTimer = 0f;
					_hornStepIndex = 0;
				}
				ExecuteHornPattern(dt);
			}
			else if (_isHornPatternActive)
			{
				_isHornPatternActive = false;
				_controlsOverrider.Horn.Set(0f);
			}
		}

		private void ExecuteHornPattern(float dt)
		{
			_hornStepTimer += dt;
			float[] array = new float[8] { 2f, 0.4f, 2f, 0.4f, 0.8f, 0.4f, 2.5f, 2f };
			bool flag = _hornStepIndex % 2 == 0 && _hornStepIndex < 7;
			if (_controlsOverrider != null && _controlsOverrider.Horn != null)
			{
				_controlsOverrider.Horn.Set(flag ? 1f : 0f);
			}
			if (_hornStepTimer >= array[_hornStepIndex])
			{
				_hornStepTimer = 0f;
				_hornStepIndex = (_hornStepIndex + 1) % array.Length;
			}
		}

		// CRITICAL INVARIANT [AIE-03]: Grade Equilibrium Model
		public void ComputeHillStartEquilibrium(out float reqForceN, out float eqThrottle, out float targetLaunchThrottle)
		{
			float num = ((_commandedReverser != 0f) ? _commandedReverser : ((_desiredReverser != 0f) ? _desiredReverser : TargetDirection));
			if (num == 0f)
			{
				num = 1f;
			}
			float b = ((_trainCar != null) ? (_trainCar.transform.forward.y * num) : 0f);
			float num2 = ConsistTotalMassTons * 1000f;
			float num3 = num2 * 9.81f * Mathf.Max(0f, b);
			float num4 = num2 * 9.81f * 0.002f;
			float num5 = num2 * 0.1f;
			float num6 = num3 + num4;
			reqForceN = num6 + num5;
			float maxRatedTractiveEffortNewtons = MaxRatedTractiveEffortNewtons;
			eqThrottle = Mathf.Clamp01(num6 / maxRatedTractiveEffortNewtons);
			targetLaunchThrottle = Mathf.Clamp01(reqForceN / maxRatedTractiveEffortNewtons);
			if (IsLocoDM3)
			{
				eqThrottle = Mathf.Max(eqThrottle, 0.7f);
				targetLaunchThrottle = Mathf.Max(targetLaunchThrottle, 0.85f);
			}
			else if (IsLocoDH4)
			{
				eqThrottle = Mathf.Max(eqThrottle, 0.45f);
				targetLaunchThrottle = Mathf.Max(targetLaunchThrottle, 0.6f);
			}
			else if (IsLocoDE2)
			{
				eqThrottle = Mathf.Max(eqThrottle, 0.25f);
				targetLaunchThrottle = Mathf.Max(targetLaunchThrottle, 0.4f);
			}
			else if (IsLocoDE6)
			{
				eqThrottle = Mathf.Max(eqThrottle, 0.2f);
				targetLaunchThrottle = Mathf.Max(targetLaunchThrottle, 0.35f);
			}
		}

		private bool IsRearRollbackPathObstructed(out float obstDist)
		{
			obstDist = float.PositiveInfinity;
			if (_trainCar == null)
			{
				return false;
			}
			List<TrainCar> list = ((_registeredConsistCars.Count > 0) ? _registeredConsistCars : ((_trainCar.trainset != null) ? _trainCar.trainset.cars : null));
			TrainCar trainCar = _trainCar;
			if (list != null && list.Count > 1)
			{
				float num = 0f;
				Vector3 position = _trainCar.transform.position;
				for (int i = 0; i < list.Count; i++)
				{
					TrainCar trainCar2 = list[i];
					if (!(trainCar2 == null))
					{
						float num2 = Vector3.Distance(position, trainCar2.transform.position);
						if (num2 > num)
						{
							num = num2;
							trainCar = trainCar2;
						}
					}
				}
			}
			if (trainCar == null)
			{
				return false;
			}
			RailTrack railTrack = null;
			if (trainCar.RearBogie != null && trainCar.RearBogie.track != null)
			{
				railTrack = trainCar.RearBogie.track;
			}
			else if (trainCar.FrontBogie != null && trainCar.FrontBogie.track != null)
			{
				railTrack = trainCar.FrontBogie.track;
			}
			if (railTrack == null)
			{
				return false;
			}
			if (RailGraph.Instance != null && RailGraph.Instance.IsTrackOccupied(railTrack, null, _trainCar.trainset))
			{
				obstDist = 15f;
				return true;
			}
			return false;
		}

		public static string GetCardinalBearing(Vector3 fromPos, Vector3 toPos)
		{
			Vector3 vector = toPos - fromPos;
			vector.y = 0f;
			if (vector.sqrMagnitude < 100f)
			{
				return "near";
			}
			float num = Mathf.Atan2(vector.x, vector.z) * 57.29578f;
			if (num < 0f)
			{
				num += 360f;
			}
			if (num >= 337.5f || num < 22.5f)
			{
				return "North";
			}
			if (num >= 22.5f && num < 67.5f)
			{
				return "Northeast";
			}
			if (num >= 67.5f && num < 112.5f)
			{
				return "East";
			}
			if (num >= 112.5f && num < 157.5f)
			{
				return "Southeast";
			}
			if (num >= 157.5f && num < 202.5f)
			{
				return "South";
			}
			if (num >= 202.5f && num < 247.5f)
			{
				return "Southwest";
			}
			if (num >= 247.5f && num < 292.5f)
			{
				return "West";
			}
			return "Northwest";
		}

		private void NotifyPermanentStall()
		{
			if (_stalledNotificationCooldown > 0f)
			{
				return;
			}
			_stalledNotificationCooldown = 30f;
			string locoId = LocoId;
			if (IsWorkerDriven)
			{
				StationController stationController = null;
				float num = float.MaxValue;
				Vector3 vector = ((_trainCar != null) ? _trainCar.transform.position : Vector3.zero);
				if (StationController.allStations != null)
				{
					foreach (StationController allStation in StationController.allStations)
					{
						if (!(allStation == null))
						{
							float num2 = Vector3.Distance(vector, allStation.transform.position);
							if (num2 < num)
							{
								num = num2;
								stationController = allStation;
							}
						}
					}
				}
				string text = "Yard";
				string text2 = "near";
				if (stationController != null)
				{
					text = ((stationController.stationInfo != null && !string.IsNullOrEmpty(stationController.stationInfo.Name)) ? stationController.stationInfo.Name : ((stationController.stationInfo != null) ? stationController.stationInfo.YardID : "Yard"));
					text2 = GetCardinalBearing(stationController.transform.position, vector);
				}
				float num3 = num / 1000f;
				string message = string.Format("Worker {0} failed to deliver: Consist stalled on steep grade. Location: {1:F1} km {2} of {3}. Train secured with brakes.", locoId, num3, text2, text);
				if (WorkerManager.Instance != null)
				{
					WorkerManager.Instance.ShowToast(message, "[AI Worker Dispatcher]", 10f);
				}
				return;
			}
			if (TrafficManager.Instance != null)
			{
				TrafficManager.Instance.RegisterStalledAmbientEngineer(this);
			}
			string message2 = string.Format("AI Train {0} stalled on grade. Consist exceeds hauling capacity. [Press K to Despawn]", locoId);
			if (WorkerManager.Instance == null)
			{
				return;
			}
			WorkerManager.Instance.ShowToast(message2, "[AI Traffic]", 10f, "Despawn Stalled Train (K)", delegate
			{
				if (TrafficManager.Instance != null)
				{
					TrafficManager.Instance.DespawnFirstStalledAmbientTrain();
				}
			});
		}

		private float ComputeAcceleratingThrottle(float dt)
		{
			float num = TargetSpeedKmh - CurrentSpeedKmh;
			if (num <= 0f)
			{
				_rampThrottle = Mathf.MoveTowards(_rampThrottle, 0f, 0.2f * dt);
				_hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0f, 0.2f * dt);
				return 0f;
			}
			float num2 = ((_commandedReverser != 0f) ? _commandedReverser : ((_desiredReverser != 0f) ? _desiredReverser : TargetDirection));
			if (num2 == 0f)
			{
				num2 = 1f;
			}
			float num3 = 0f;
			if (_trainCar != null)
			{
				num3 = _trainCar.transform.forward.y * num2;
			}
			bool isLocoDM = IsLocoDM3;
			bool flag = num3 > 0.0015f;
			bool flag2 = CurrentAccelerationMs2 < 0.08f && num > 3f;
			string text = ((_trainCar != null && _trainCar.carLivery != null) ? _trainCar.carLivery.id : "");
			bool flag3 = _trainCar != null && (_trainCar.carType == TrainCarType.LocoShunter || text.IndexOf("DE2", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Shunter", StringComparison.OrdinalIgnoreCase) >= 0);
			float num4 = 0.28f;
			if (HillStage == HillLaunchStage.MomentumRunUp)
			{
				num4 = 1f;
			}
			else if (IsHillStarting || HillStage != 0)
			{
				num4 = Mathf.Max(0.4f, Mathf.Min(1f, HillEquilibriumThrottle + 0.2f));
			}
			else if (CurrentSpeedKmh < 6f)
			{
				num4 = ((!flag) ? (isLocoDM ? 0.6f : 0.28f) : (isLocoDM ? ((num3 > 0.01f) ? 1f : 0.9f) : ((!flag3) ? ((num3 > 0.015f) ? 0.85f : 0.7f) : 0.4f)));
			}
			else if (!(CurrentSpeedKmh < 15f))
			{
				num4 = ((!(CurrentSpeedKmh < 25f)) ? 1f : Mathf.Lerp(isLocoDM ? 0.9f : 0.75f, 1f, (CurrentSpeedKmh - 15f) / 10f));
			}
			else
			{
				float a = ((!flag) ? (isLocoDM ? 0.65f : 0.28f) : (isLocoDM ? 0.9f : 0.65f));
				num4 = Mathf.Lerp(a, isLocoDM ? 1f : 0.75f, (CurrentSpeedKmh - 6f) / 9f);
			}
			if (CurrentSpeedKmh < 20f && (flag || flag2))
			{
				if (CurrentAccelerationMs2 < 0.1f)
				{
					float num5 = ((flag && num3 > 0.015f) ? 0.16f : 0.1f);
					float target = (isLocoDM ? 0.85f : 0.6f);
					_hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, target, num5 * dt);
				}
				else if (CurrentAccelerationMs2 > 0.2f)
				{
					_hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0f, 0.06f * dt);
				}
			}
			else
			{
				_hillAssistBoost = Mathf.MoveTowards(_hillAssistBoost, 0f, 0.1f * dt);
			}
			float num6 = Mathf.Clamp01(num4 + _hillAssistBoost);
			if (_rampThrottle < 0.15f && CurrentSpeedKmh < 1f)
			{
				if (HillStage == HillLaunchStage.MomentumRunUp)
				{
					_rampThrottle = 0.85f;
				}
				else if (IsHillStarting || HillStage != 0)
				{
					_rampThrottle = Mathf.Max(0.2f, HillEquilibriumThrottle);
				}
				else if (isLocoDM)
				{
					_rampThrottle = ((!flag) ? 0.35f : ((num3 > 0.01f) ? 0.8f : 0.7f));
				}
				else
				{
					_rampThrottle = ((!flag) ? 0.12f : (flag3 ? 0.22f : 0.35f));
				}
			}
			if (CurrentAccelerationMs2 < 0.12f)
			{
				float num7 = ((CurrentSpeedKmh < 6f) ? 0.05f : 0.07f);
				if ((flag || flag2) && CurrentAccelerationMs2 < 0.05f)
				{
					num7 = (isLocoDM ? 0.22f : 0.12f);
				}
				if (HillStage != HillLaunchStage.Stage2_Sanded && HillStage != HillLaunchStage.MomentumRunUp)
				{
					float trackWetness = GetTrackWetness();
					if (trackWetness > 0.01f)
					{
						num7 *= 1f - 0.35f * trackWetness;
					}
				}
				_rampThrottle += num7 * dt;
			}
			else if (CurrentAccelerationMs2 > 0.3f)
			{
				_rampThrottle -= 0.07f * dt;
			}
			if (num < 4f)
			{
				float b = Mathf.Clamp01(num / 4f);
				num6 = Mathf.Min(num6, b);
			}
			_rampThrottle = Mathf.Clamp(_rampThrottle, 0f, num6);
			float tractionAuthorityMultiplier = GetTractionAuthorityMultiplier();
			if (_rampThrottle > num6 * tractionAuthorityMultiplier)
			{
				_rampThrottle = Mathf.MoveTowards(_rampThrottle, num6 * tractionAuthorityMultiplier, 2f * dt);
			}
			return _rampThrottle;
		}

		private float ComputeCruisingThrottle(float dt)
		{
			float num = TargetSpeedKmh - CurrentSpeedKmh;
			float target = ThrottlePID.Update(TargetSpeedKmh, CurrentSpeedKmh, dt);
			_rampThrottle = Mathf.MoveTowards(_rampThrottle, target, 0.06f * dt);
			return _rampThrottle * GetTractionAuthorityMultiplier();
		}

		private void UpdateStateMachine(float dt)
		{
			float num = TargetSpeedKmh - CurrentSpeedKmh;
			if ((State == EngineState.Accelerating || State == EngineState.Cruising || State == EngineState.Starting) && ((_controlsOverrider != null && _controlsOverrider.EngineOnReader != null && !_controlsOverrider.EngineOnReader.IsOn) || HasAnyStoppedConsistLoco()))
			{
				_stallRestartCooldown -= dt;
				if (_stallRestartCooldown <= 0f)
				{
					_stallRestartCooldown = 2f;
					CycleBreakerLever();
				}
			}
			if (_brakeHoldTimer > 0f)
			{
				_brakeHoldTimer -= dt;
			}
			bool flag = CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0 && CurrentPathTrackIndex >= CurrentPath.Tracks.Count - 1;
			RailTrack railTrack = null;
			if (_trainCar != null)
			{
				if (_trainCar.FrontBogie != null && _trainCar.FrontBogie.track != null)
				{
					railTrack = _trainCar.FrontBogie.track;
				}
				else if (_trainCar.RearBogie != null && _trainCar.RearBogie.track != null)
				{
					railTrack = _trainCar.RearBogie.track;
				}
			}
			if (!flag && railTrack != null && CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0)
			{
				RailTrack railTrack2 = CurrentPath.Tracks[CurrentPath.Tracks.Count - 1];
				if (railTrack == railTrack2 || (!string.IsNullOrEmpty(DestinationTrackName) && railTrack.name == DestinationTrackName))
				{
					flag = true;
				}
			}
			bool flag2 = false;
			if ((IsStationDestination || IsTerminusDestination) && CurrentSpeedKmh < 0.5f && StationaryTimer >= 2f)
			{
				if (DistanceToDestination <= 12f)
				{
					flag2 = true;
				}
				else if (flag)
				{
					bool flag3 = DistanceToObstacle < 35f;
					bool flag4 = DistanceToDestination <= 35f;
					bool flag5 = false;
					TrainCar trainCar = ((_trainCar != null && _trainCar.trainset != null && _trainCar.trainset.cars != null && _trainCar.trainset.cars.Count > 0) ? _trainCar.trainset.cars[_trainCar.trainset.cars.Count - 1] : _trainCar);
					if (trainCar != null)
					{
						RailTrack railTrack3 = ((trainCar.RearBogie != null) ? trainCar.RearBogie.track : ((trainCar.FrontBogie != null) ? trainCar.FrontBogie.track : null));
						RailTrack railTrack4 = ((CurrentPath != null && CurrentPath.Tracks != null && CurrentPath.Tracks.Count > 0) ? CurrentPath.Tracks[CurrentPath.Tracks.Count - 1] : null);
						if (railTrack3 == railTrack4)
						{
							flag5 = true;
						}
					}
					if (flag3 || flag4 || (flag5 && DistanceToDestination <= 60f))
					{
						flag2 = true;
					}
				}
			}
			if (flag2)
			{
				HasArrivedAtDestination = true;
			}
			if (flag2 && State != EngineState.TerminusStop && State != EngineState.StationHold)
			{
				if (IsTerminusDestination)
				{
					EnterTerminusStop();
					return;
				}
				if (IsStationDestination)
				{
					EnterStationHold();
					return;
				}
			}
			switch (State)
			{
			case EngineState.Idle:
			{
				_commandedThrottle = 0f;
				_commandedDynamicBrake = 0f;
				_rampThrottle = 0f;
				_commandedReverser = _desiredReverser;
				float f = ((_trainCar != null) ? _trainCar.transform.forward.y : 0f);
				bool flag6 = Mathf.Abs(f) > 0.002f;
				_commandedTrainBrake = (flag6 ? 1f : 0.85f);
				_commandedIndependentBrake = 1f;
				if (TargetSpeedKmh > 1f && !_isEmergencyStop)
				{
					State = EngineState.Starting;
				}
				break;
			}
			case EngineState.Starting:
			{
				_commandedReverser = _desiredReverser;
				float num9 = ((_trainCar != null) ? (_trainCar.transform.forward.y * _desiredReverser) : 0f);
				bool flag7 = Mathf.Abs(num9) > 0.002f || (_trainCar != null && Mathf.Abs(_trainCar.transform.forward.y) > 0.002f);
				_commandedTrainBrake = (flag7 ? 1f : 0.85f);
				_commandedDynamicBrake = 0f;
				_rampThrottle = 0f;
				bool flag8 = ConsistCarCount <= 1;
				_commandedIndependentBrake = (flag8 ? 1f : 0f);
				ReleaseAllConsistHandbrakes();
				if (!(Mathf.Abs(_currentReverser - _desiredReverser) < 0.1f))
				{
					break;
				}
				if (num9 > 0.0015f)
				{
					if (HillStage == HillLaunchStage.None)
					{
						HillStage = ((!IsPrecipitationActive()) ? HillLaunchStage.Stage1_Clean : HillLaunchStage.Stage2_Sanded);
						_hillStagnationTimer = 0f;
						_stage2ContinuousSandingTimer = 0f;
						_hillGroundProgressWindowTimer = 0f;
						if (_trainCar != null)
						{
							_hillGroundWindowStartPos = _trainCar.transform.position;
						}
					}
				}
				else
				{
					HillStage = HillLaunchStage.None;
					_hillStagnationTimer = 0f;
					_stage2ContinuousSandingTimer = 0f;
					_hillGroundProgressWindowTimer = 0f;
				}
				State = EngineState.Accelerating;
				break;
			}
			case EngineState.Accelerating:
			{
				_commandedReverser = _desiredReverser;
				_commandedDynamicBrake = 0f;
				float num10 = ((_commandedReverser != 0f) ? _commandedReverser : ((_desiredReverser != 0f) ? _desiredReverser : TargetDirection));
				if (num10 == 0f)
				{
					num10 = 1f;
				}
				float num11 = ((_trainCar != null) ? (_trainCar.transform.forward.y * num10) : 0f);
				float num12 = ((_trainCar != null) ? _trainCar.GetForwardSpeed() : 0f);
				float num13 = num12 * num10;
				bool isLocoDM = IsLocoDM3;
				bool flag9 = isLocoDM && (_dm3Controller == null || _dm3Controller.IsInNeutral || _dm3Controller.IsShifting || _dm3Controller.CurrentGearIndex <= 1);
				bool flag10 = num11 > 0.0015f;
				bool flag11 = num11 < -0.003f;
				bool flag12 = (flag10 || flag9) && CurrentSpeedKmh < 5f;
				bool flag13 = ConsistCarCount <= 1;
				IsHillStarting = flag12 && num13 < 0.2f;
				if (flag12)
				{
					if (HillStage == HillLaunchStage.None)
					{
						HillStage = ((!IsPrecipitationActive()) ? HillLaunchStage.Stage1_Clean : HillLaunchStage.Stage2_Sanded);
						_hillStagnationTimer = 0f;
						_stage2ContinuousSandingTimer = 0f;
						_hillGroundProgressWindowTimer = 0f;
						if (_trainCar != null)
						{
							_hillGroundWindowStartPos = _trainCar.transform.position;
						}
					}
					if (HillStage == HillLaunchStage.RollbackDescent)
					{
						_commandedReverser = (_desiredReverser = 0f - num10);
						_commandedThrottle = 0f;
						_rampThrottle = 0f;
						_commandedIndependentBrake = 0f;
						if (_controlsOverrider != null && _controlsOverrider.Sander != null)
						{
							_controlsOverrider.Sander.Set(0f);
						}
						float obstDist;
						bool flag14 = IsRearRollbackPathObstructed(out obstDist);
						if (flag14 && obstDist < 30f)
						{
							_commandedTrainBrake = 1f;
							_currentTrainBrake = 1f;
						}
						else
						{
							float num14 = Mathf.Abs(num12) * 3.6f;
							if (num14 > 9f)
							{
								_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0.75f, 0.8f * dt);
							}
							else if (num14 < 6f)
							{
								_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0.2f, 0.5f * dt);
							}
							else
							{
								_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0.45f, 0.3f * dt);
							}
						}
						float num15 = Vector3.Distance(_trainCar.transform.position, _rollbackStartPosition);
						float num16 = Mathf.Abs(_trainCar.transform.forward.y);
						bool flag15 = num16 < 0.005f;
						bool flag16 = num15 >= 200f;
						if (flag15 || flag16 || (flag14 && obstDist < 30f))
						{
							_commandedTrainBrake = 1f;
							_currentTrainBrake = 1f;
							if (Mathf.Abs(num12) < 0.08f)
							{
								_desiredReverser = num10;
								_commandedReverser = num10;
								_hasAttemptedMomentumRunUp = true;
								_hillStagnationTimer = 0f;
								HillStage = HillLaunchStage.MomentumRunUp;
							}
						}
					}
					else
					{
						if (HillStage == HillLaunchStage.PermanentlyStalled)
						{
							_commandedThrottle = 0f;
							_rampThrottle = 0f;
							_currentThrottle = 0f;
							_commandedTrainBrake = 1f;
							_currentTrainBrake = 1f;
							_commandedIndependentBrake = 1f;
							_currentIndependentBrake = 1f;
							if (_controlsOverrider != null && _controlsOverrider.Handbrake != null)
							{
								_controlsOverrider.Handbrake.Set(1f);
							}
							IsPermanentlyStalled = true;
							State = EngineState.TerminusStop;
							NotifyPermanentStall();
							break;
						}
						if (HillStage == HillLaunchStage.Stage1_Clean && IsPrecipitationActive())
						{
							HillStage = HillLaunchStage.Stage2_Sanded;
							_hillStagnationTimer = 0f;
							_stage2ContinuousSandingTimer = 0f;
						}
						if (HillStage == HillLaunchStage.Stage2_Sanded)
						{
							_stage2ContinuousSandingTimer += dt;
							if (_stage2ContinuousSandingTimer >= 11f && CurrentSpeedKmh < 1f)
							{
								_stage2ContinuousSandingTimer = 0f;
								_hillStagnationTimer = 0f;
								_hillGroundProgressWindowTimer = 0f;
								_commandedTrainBrake = 1f;
								_currentTrainBrake = 1f;
								_commandedThrottle = 0f;
								_rampThrottle = 0f;
								HillStage = HillLaunchStage.RollbackDescent;
								_rollbackStartPosition = ((_trainCar != null) ? _trainCar.transform.position : Vector3.zero);
								if (Main.IsDebugLoggingEnabled)
								{
									Main.LogDebug(string.Format("[AITraffic] Train '{0}' exceeded continuous sanding timeout (11s) on hill. Initiating controlled rollback descent.", LocoId));
								}
								break;
							}
						}
						else
						{
							_stage2ContinuousSandingTimer = 0f;
						}
						float reqForceN;
						float eqThrottle;
						float targetLaunchThrottle;
						ComputeHillStartEquilibrium(out reqForceN, out eqThrottle, out targetLaunchThrottle);
						HillEquilibriumThrottle = eqThrottle;
						if (HillStage == HillLaunchStage.Stage1_Clean)
						{
							_sanderRequested = false;
							if (_controlsOverrider != null && _controlsOverrider.Sander != null)
							{
								_controlsOverrider.Sander.Set(0f);
							}
						}
						else
						{
							_sanderRequested = true;
							if (_controlsOverrider != null && _controlsOverrider.Sander != null)
							{
								_controlsOverrider.Sander.Set(1f);
							}
						}
						if (isLocoDM && _dm3Controller != null)
						{
							if (_dm3Controller.IsInNeutral || _dm3Controller.IsShifting)
							{
								_commandedTrainBrake = 1f;
								_commandedThrottle = 0f;
								_rampThrottle = 0f;
								break;
							}
							if (_dm3Controller.CurrentGearIndex > 1)
							{
								_dm3Controller.ApplyGearsInstant(1, 1);
							}
						}
						_commandedThrottle = ComputeAcceleratingThrottle(dt);
						float currentThrottle = _currentThrottle;
						if (HillStage == HillLaunchStage.MomentumRunUp)
						{
							_commandedThrottle = Mathf.Max(_commandedThrottle, 0.95f);
						}
						if (currentThrottle >= eqThrottle * 0.85f)
						{
							float num17 = ((HillStage == HillLaunchStage.MomentumRunUp) ? 0.6f : 0.35f);
							_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0f, num17 * dt);
						}
						else
						{
							_commandedTrainBrake = 1f;
						}
						if (num13 < -0.04f)
						{
							float b = Mathf.Clamp01((0f - num13 - 0.04f) * 4f);
							_commandedTrainBrake = Mathf.Max(_commandedTrainBrake, b);
						}
						_commandedIndependentBrake = ((!flag13) ? 0f : ((currentThrottle < eqThrottle) ? 1f : Mathf.MoveTowards(_commandedIndependentBrake, 0f, 0.5f * dt)));
						if (num13 >= 0.15f || CurrentSpeedKmh >= 1f)
						{
							_commandedTrainBrake = 0f;
							if (CurrentSpeedKmh >= 5f && CurrentAccelerationMs2 >= 0.08f)
							{
								HillStage = HillLaunchStage.None;
								_sanderRequested = false;
								_hillStagnationTimer = 0f;
								_stage2ContinuousSandingTimer = 0f;
								_hillGroundProgressWindowTimer = 0f;
								if (_controlsOverrider != null && _controlsOverrider.Sander != null)
								{
									_controlsOverrider.Sander.Set(0f);
								}
							}
						}
						_hillGroundProgressWindowTimer += dt;
						if (_hillGroundProgressWindowTimer >= 6f)
						{
							float num18 = ((_trainCar != null) ? Vector3.Distance(_trainCar.transform.position, _hillGroundWindowStartPos) : 0f);
							if (num18 < 1f && _commandedThrottle > 0.2f && CurrentSpeedKmh < 0.5f)
							{
								_hillStagnationTimer = Mathf.Max(_hillStagnationTimer, 3f);
							}
							_hillGroundProgressWindowTimer = 0f;
							if (_trainCar != null)
							{
								_hillGroundWindowStartPos = _trainCar.transform.position;
							}
						}
						bool flag17 = _commandedTrainBrake <= 0.15f;
						bool flag18 = currentThrottle >= Mathf.Min(0.9f, targetLaunchThrottle * 0.9f);
						bool flag19 = CurrentAccelerationMs2 <= 0.02f && num13 < 0.2f;
						bool flag20 = _isWheelSlipping && num13 < 0.2f;
						bool flag21 = _commandedThrottle > 0.1f;
						bool flag22 = flag10 && CurrentSpeedKmh < 0.5f && flag21;
						if (flag17 && flag18 && flag19)
						{
							_hillStagnationTimer += dt;
						}
						else if (flag17 && flag20)
						{
							_hillStagnationTimer += dt * 0.8f;
						}
						else if (flag22)
						{
							_hillStagnationTimer += dt * 0.6f;
						}
						else
						{
							_hillStagnationTimer = Mathf.Max(0f, _hillStagnationTimer - 0.5f * dt);
						}
						if (_hillStagnationTimer >= 3f)
						{
							_hillStagnationTimer = 0f;
							_stage2ContinuousSandingTimer = 0f;
							_hillGroundProgressWindowTimer = 0f;
							if (_trainCar != null)
							{
								_hillGroundWindowStartPos = _trainCar.transform.position;
							}
							_commandedTrainBrake = 1f;
							_currentTrainBrake = 1f;
							_commandedThrottle = 0f;
							_rampThrottle = 0f;
							if (HillStage == HillLaunchStage.Stage1_Clean)
							{
								HillStage = HillLaunchStage.Stage2_Sanded;
							}
							else if (HillStage == HillLaunchStage.Stage2_Sanded)
							{
								HillStage = HillLaunchStage.RollbackDescent;
								_rollbackStartPosition = ((_trainCar != null) ? _trainCar.transform.position : Vector3.zero);
							}
							else if (HillStage == HillLaunchStage.MomentumRunUp)
							{
								HillStage = HillLaunchStage.PermanentlyStalled;
							}
						}
					}
				}
				else if (flag11 && CurrentSpeedKmh < 3f)
				{
					_commandedThrottle = ComputeAcceleratingThrottle(dt);
					_commandedTrainBrake = Mathf.MoveTowards(_commandedTrainBrake, 0f, 0.35f * dt);
					_commandedIndependentBrake = (flag13 ? Mathf.MoveTowards(_commandedIndependentBrake, 0f, 0.5f * dt) : 0f);
				}
				else
				{
					_commandedIndependentBrake = 0f;
					_commandedTrainBrake = 0f;
					_commandedThrottle = ComputeAcceleratingThrottle(dt);
					HillStage = HillLaunchStage.None;
				}
				BrakePID.Reset();
				if (TargetSpeedKmh <= 0.01f || num < -3.5f)
				{
					_rampThrottle = 0f;
					_brakeHoldTimer = 3f;
					State = EngineState.Braking;
				}
				else if (Mathf.Abs(num) <= SpeedToleranceKmh)
				{
					State = EngineState.Cruising;
				}
				break;
			}
			case EngineState.Cruising:
				_commandedReverser = _desiredReverser;
				_commandedIndependentBrake = 0f;
				_commandedTrainBrake = 0f;
				_commandedThrottle = ComputeCruisingThrottle(dt);
				BrakePID.Reset();
				if (TargetSpeedKmh <= 0.01f || num < -4f)
				{
					_rampThrottle = 0f;
					_brakeHoldTimer = 3f;
					State = EngineState.Braking;
				}
				else if (num < -1f)
				{
					State = EngineState.Coasting;
				}
				else if (num > SpeedToleranceKmh * 1.5f)
				{
					State = EngineState.Accelerating;
				}
				else if (TargetSpeedKmh > 0f && Mathf.Abs(_commandedThrottle) < 0.02f)
				{
					State = EngineState.Coasting;
				}
				break;
			case EngineState.Coasting:
				_commandedReverser = _desiredReverser;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				if (_hasDynamicBrake && num < 0f)
				{
					_commandedDynamicBrake = Mathf.Clamp01((0f - num) / 5f);
					_commandedTrainBrake = 0f;
				}
				else if (!_hasDynamicBrake && num < -0.8f)
				{
					_commandedDynamicBrake = 0f;
					_commandedTrainBrake = Mathf.Clamp((0f - num) * 0.04f, 0f, 0.2f);
				}
				else
				{
					_commandedDynamicBrake = 0f;
					_commandedTrainBrake = 0f;
				}
				if (TargetSpeedKmh <= 0.01f || num < -4f)
				{
					_brakeHoldTimer = 3f;
					State = EngineState.Braking;
				}
				else if (num > SpeedToleranceKmh)
				{
					_commandedDynamicBrake = 0f;
					_commandedTrainBrake = 0f;
					State = EngineState.Accelerating;
				}
				break;
			case EngineState.Braking:
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				ThrottlePID.Reset();
				float num2 = BrakePID.Update(CurrentSpeedKmh, TargetSpeedKmh, dt);
				float trackWetness = GetTrackWetness();
				float num3 = Mathf.Min(DistanceToRedSignal, Mathf.Min(EffectiveObstacleDistance, DistanceToDestination));
				if (trackWetness > 0.1f && num3 < 250f && CurrentSpeedKmh > 5f && num2 > 0.2f)
				{
					_sanderRequested = true;
				}
				if (num3 < 600f && TargetSpeedKmh <= 15f)
				{
					float num4 = Mathf.Max(0.5f, num3 - 20f);
					float num5 = CurrentSpeedMs * CurrentSpeedMs / (2f * num4);
					if (num5 > 0.15f)
					{
						float num6 = Mathf.Clamp01((num5 - 0.15f) / 0.3f);
						float num7 = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics.MaxWetServiceBrakeDemand : 0.55f);
						float a = ((trackWetness > 0.1f && !_sanderRequested && _sandHoldTimer <= 0f) ? num7 : 1f);
						num2 = Mathf.Min(a, Mathf.Max(num2, 0.45f + num6 * 0.55f));
					}
					if (num3 <= 20f)
					{
						num2 = 1f;
					}
					else if (num3 < 35f && CurrentSpeedKmh > 6f)
					{
						num2 = 1f;
						_sanderRequested = true;
					}
					else if (num3 < 60f && CurrentSpeedKmh > 15f)
					{
						num2 = 1f;
						_sanderRequested = true;
					}
					else if (num3 < 120f && CurrentSpeedKmh > 30f)
					{
						num2 = ((trackWetness > 0.1f) ? 0.7f : 0.85f);
						if (trackWetness > 0.1f)
						{
							_sanderRequested = true;
						}
					}
					if (CurrentSpeedKmh < 0.4f)
					{
						num2 = Mathf.Max(0.6f, num2);
					}
				}
				if (CurrentSpeedProfile.LimitingReason == SpeedLimitReason.CurvatureRadius && num < -2f)
				{
					float num8 = Mathf.Clamp01((0f - num - 2f) / 10f);
					num2 = Mathf.Max(num2, 0.4f + num8 * 0.6f);
				}
				ApplyBrakeBlending(num2);
				if (TargetSpeedKmh <= 0.01f && CurrentSpeedKmh < 0.3f)
				{
					if (IsStationDestination && flag2)
					{
						EnterStationHold();
					}
					else if (IsTerminusDestination && flag2)
					{
						EnterTerminusStop();
					}
					else
					{
						State = EngineState.Idle;
					}
				}
				else if (TargetSpeedKmh > 1f && num >= -0.5f && _brakeHoldTimer <= 0f)
				{
					_commandedTrainBrake = 0f;
					_commandedIndependentBrake = 0f;
					State = EngineState.Accelerating;
				}
				break;
			}
			case EngineState.StationHold:
				_commandedThrottle = 0f;
				_commandedDynamicBrake = 0f;
				_commandedTrainBrake = 0.5f;
				_commandedIndependentBrake = 1f;
				_commandedReverser = _desiredReverser;
				DwellTimeRemaining -= dt;
				if (DwellTimeRemaining <= 0f)
				{
					IsStationDestination = false;
					State = EngineState.Starting;
				}
				break;
			case EngineState.TerminusStop:
				_commandedThrottle = 0f;
				_commandedDynamicBrake = 0f;
				_commandedTrainBrake = 1f;
				_commandedIndependentBrake = 1f;
				_commandedReverser = 0f;
				if (_controlsOverrider != null)
				{
					if (_controlsOverrider.Throttle != null && _controlsOverrider.Throttle.Value > 0f)
					{
						_controlsOverrider.Throttle.Set(0f);
					}
					if (_controlsOverrider.Reverser != null && Mathf.Abs(_controlsOverrider.Reverser.Value - 0.5f) > 0.05f)
					{
						_controlsOverrider.Reverser.Set(0.5f);
					}
					if (_controlsOverrider.HeadlightsFront != null)
					{
						_controlsOverrider.HeadlightsFront.Set(0.4f);
					}
					if (_controlsOverrider.HeadlightsRear != null)
					{
						_controlsOverrider.HeadlightsRear.Set(0.4f);
					}
					if (_controlsOverrider.EngineOnReader != null && _controlsOverrider.EngineOnReader.IsOn && _controlsOverrider.PowerOff != null)
					{
						_controlsOverrider.PowerOff.Set(1f);
					}
				}
				break;
			}
		}

		private void ApplyBrakeBlending(float brakeDemand)
		{
			float trackWetness = GetTrackWetness();
			_commandedTrainBrake = brakeDemand * _wspBrakeAuthority;
			if (trackWetness > 0.1f && !_sanderRequested && _sandHoldTimer <= 0f)
			{
				float a = ((IniConfigManager.Instance != null && IniConfigManager.Instance.Physics != null) ? IniConfigManager.Instance.Physics.MaxWetServiceBrakeDemand : 0.55f);
				_commandedTrainBrake = Mathf.Min(a, _commandedTrainBrake);
			}
			if (_hasDynamicBrake && CurrentSpeedKmh > 5f && !_isLocoWheelSliding)
			{
				float num = Mathf.Lerp(1.25f, 0.7f, trackWetness);
				if (trackWetness > 0.1f && _commandedTrainBrake > 0.35f)
				{
					num *= 0.5f;
				}
				_commandedDynamicBrake = Mathf.Clamp01(brakeDemand * num);
			}
			else
			{
				_commandedDynamicBrake = 0f;
			}
			_commandedIndependentBrake = 0f;
			float effectiveObstacleDistance = EffectiveObstacleDistance;
			if (effectiveObstacleDistance < 30f || (effectiveObstacleDistance < 80f && CurrentSpeedKmh > 25f))
			{
				_commandedTrainBrake = 1f;
				if (_hasDynamicBrake)
				{
					_commandedDynamicBrake = 1f;
				}
			}
			if (CurrentSpeedKmh < 0.2f && (TargetSpeedKmh <= 0.1f || effectiveObstacleDistance <= 45f))
			{
				_commandedTrainBrake = Mathf.Max(0.6f, brakeDemand);
				_commandedIndependentBrake = 1f;
			}
		}

		private void EnterStationHold()
		{
			State = EngineState.StationHold;
			DwellTimeRemaining = ((StationDwellDuration > 0f) ? StationDwellDuration : UnityEngine.Random.Range(30f, 60f));
		}

		private void EmergencyCrashShutdown()
		{
			_commandedThrottle = 0f;
			_rampThrottle = 0f;
			_currentThrottle = 0f;
			_commandedDynamicBrake = 0f;
			_currentDynamicBrake = 0f;
			_commandedTrainBrake = 1f;
			_currentTrainBrake = 1f;
			_commandedIndependentBrake = 1f;
			_currentIndependentBrake = 1f;
			_desiredReverser = 0f;
			_commandedReverser = 0f;
			_currentReverser = 0f;
			if (_controlsOverrider != null)
			{
				if (_controlsOverrider.Throttle != null)
				{
					_controlsOverrider.Throttle.Set(0f);
				}
				if (_controlsOverrider.Brake != null)
				{
					_controlsOverrider.Brake.Set(1f);
				}
				if (_controlsOverrider.IndependentBrake != null)
				{
					_controlsOverrider.IndependentBrake.Set(1f);
				}
				if (_controlsOverrider.Handbrake != null)
				{
					_controlsOverrider.Handbrake.Set(1f);
				}
				if (_controlsOverrider.Reverser != null)
				{
					_controlsOverrider.Reverser.Set(0.5f);
				}
			}
			EnterTerminusStop();
		}

		private void EnterTerminusStop()
		{
			State = EngineState.TerminusStop;
			TerminusArrivalTime = Time.time;
			ShutdownEngine();
			if (_reservedTracks.Count > 0)
			{
				foreach (RailTrack reservedTrack in _reservedTracks)
				{
					if (reservedTrack != null && RailGraph.Instance != null)
					{
						RailGraph.Instance.ReleaseTrackReservation(reservedTrack, this);
					}
				}
				_reservedTracks.Clear();
			}
			if (JunctionController.Instance != null)
			{
				// CRITICAL INVARIANT [WRK-04]: ReleaseAllLocksFor
				JunctionController.Instance.ReleaseAllLocksFor(this);
			}
			ReleaseAllSignalReservations();
			_isHoldingForCorridor = false;
			_isHoldingInMultiTrack = false;
			_corridorHoldReason = null;
			_corridorHoldDistance = float.PositiveInfinity;
			if (IsWorkerDriven && _controlsOverrider != null && _controlsOverrider.Handbrake != null)
			{
				_controlsOverrider.Handbrake.Set(1f);
			}
			try
			{
				if (this.OnTerminusArrival != null)
				{
					this.OnTerminusArrival(this);
				}
			}
			catch (Exception arg)
			{
				Debug.LogError(string.Format("[AITraffic] Error in OnTerminusArrival callback: {0}", arg));
			}
		}

		private void ExecuteControlOutputs(float dt)
		{
			if (_controlsOverrider == null)
			{
				return;
			}
			if (!IsIntentionalReverseActive && (DistanceToObstacle < 45f || (DistanceToRedSignal < 12f && !_isSelfBlockedShunting)))
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_currentThrottle = 0f;
				_commandedTrainBrake = 1f;
				_currentTrainBrake = 1f;
			}
			if (_isRollbackDetected && HillStage != HillLaunchStage.RollbackDescent && !IsIntentionalReverseActive)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_currentThrottle = 0f;
				_commandedIndependentBrake = 0f;
				_commandedTrainBrake = 1f;
				_currentIndependentBrake = 0f;
				_currentTrainBrake = 1f;
			}
			else if (_isOverheated)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
			}
			if (IsIntentionalReverseActive && Mathf.Abs(_currentReverser - _commandedReverser) > 0.1f)
			{
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_commandedTrainBrake = Mathf.Max(_commandedTrainBrake, 0.6f);
			}
			float num = _commandedThrottle;
			if (_dm3Controller != null && _dm3Controller.IsDM3 && _dm3Controller.IsShifting)
			{
				num = 0f;
				_commandedThrottle = 0f;
				_rampThrottle = 0f;
				_currentThrottle = 0f;
			}
			float num2 = ((IsHillStarting && num > _currentThrottle) ? Mathf.Max(ThrottleSlewRate, 0.16f) : ThrottleSlewRate);
			_currentThrottle = Mathf.MoveTowards(_currentThrottle, num, num2 * dt);
			if (_controlsOverrider.Throttle != null)
			{
				_controlsOverrider.Throttle.Set(_currentThrottle);
			}
			float trackWetness = GetTrackWetness();
			float num3 = ((trackWetness > 0.1f) ? 1.2f : 3.5f);
			float num4 = ((_commandedTrainBrake > _currentTrainBrake) ? num3 : 0.45f);
			float num5 = Mathf.Min(DistanceToRedSignal, Mathf.Min(EffectiveObstacleDistance, DistanceToDestination));
			if (_commandedTrainBrake >= 0.85f && (num5 < 40f || EffectiveObstacleDistance < 40f || (!IsIntentionalReverseActive && (DistanceToObstacle < 45f || (DistanceToRedSignal < 12f && !_isSelfBlockedShunting))) || (_isRollbackDetected && HillStage != HillLaunchStage.RollbackDescent && !IsIntentionalReverseActive) || State == EngineState.Idle || State == EngineState.Starting || State == EngineState.StationHold || State == EngineState.TerminusStop))
			{
				_currentTrainBrake = _commandedTrainBrake;
			}
			else
			{
				_currentTrainBrake = Mathf.MoveTowards(_currentTrainBrake, _commandedTrainBrake, num4 * dt);
			}
			if (_controlsOverrider.Brake != null)
			{
				_controlsOverrider.Brake.Set(_currentTrainBrake);
			}
			if (_hasDynamicBrake && _controlsOverrider.DynamicBrake != null)
			{
				_currentDynamicBrake = Mathf.MoveTowards(_currentDynamicBrake, _commandedDynamicBrake, DynamicBrakeSlewRate * dt);
				_controlsOverrider.DynamicBrake.Set(_currentDynamicBrake);
			}
			// CRITICAL INVARIANT [AIE-03]: Decoupled Hill-Start Balanced Bleed & Independent Brake Motion Invariant
			if (CurrentSpeedKmh >= 0.2f)
			{
				_commandedIndependentBrake = 0.0f;
				_currentIndependentBrake = 0f;
			}
			float num6 = ((_commandedIndependentBrake > _currentIndependentBrake) ? 4f : 1f);
			if (_commandedIndependentBrake >= 0.9f && (CurrentSpeedMs < 0.15f || State == EngineState.Idle || State == EngineState.StationHold || State == EngineState.TerminusStop))
			{
				_currentIndependentBrake = _commandedIndependentBrake;
			}
			else
			{
				_currentIndependentBrake = Mathf.MoveTowards(_currentIndependentBrake, _commandedIndependentBrake, num6 * dt);
			}
			if (_controlsOverrider.IndependentBrake != null)
			{
				_controlsOverrider.IndependentBrake.Set(_currentIndependentBrake);
			}
			if (IsIntentionalReverseActive)
			{
				_commandedReverser = ((_ambientLockedReverser != 0f) ? (0f - _ambientLockedReverser) : (0f - TargetDirection));
				if (_commandedReverser == 0f)
				{
					_commandedReverser = -1f;
				}
			}
			else if (_ambientLockedReverser != 0f)
			{
				_commandedReverser = _ambientLockedReverser;
			}
			if (CurrentSpeedMs < 0.15f)
			{
				_currentReverser = _commandedReverser;
				if (_controlsOverrider.Reverser != null)
				{
					_controlsOverrider.Reverser.Set(MapDirectionToReverserControl(_currentReverser));
				}
			}
			if (_controlsOverrider.Handbrake != null && State != EngineState.TerminusStop && _reverseProtocolStage != SingleTrackReverseStage.SecuredInSiding)
			{
				_controlsOverrider.Handbrake.Set(0f);
			}
		}

		public void EnsureEngineRunning()
		{
			if (!(_trainCar == null) && State != EngineState.TerminusStop)
			{
				if (_controlsOverrider == null && _trainCar.SimController != null)
				{
					_controlsOverrider = _trainCar.SimController.controlsOverrider;
				}
				if (_controlsOverrider != null && _controlsOverrider.EngineOnReader != null && !_controlsOverrider.EngineOnReader.IsOn && !_isStartingEngine && base.gameObject.activeInHierarchy)
				{
					StartCoroutine(StartEngineSequence());
				}
			}
		}

		private IEnumerator StartEngineSequence()
		{
			_isStartingEngine = true;
			if (_dm3Controller != null && _dm3Controller.IsDM3)
			{
				_dm3Controller.ApplyGearsInstant(0, 0);
			}
			try
			{
				StartupHelper.Startup(_trainCar);
			}
			catch (Exception ex)
			{
				if (Main.ModEntry != null && Main.ModEntry.Logger != null)
				{
					Main.ModEntry.Logger.Warning(string.Format("StartupHelper warning in AIEngineer: {0}", ex.Message));
				}
			}
			if (_controlsOverrider != null)
			{
				if (_controlsOverrider.Handbrake != null)
				{
					_controlsOverrider.Handbrake.Set(0f);
				}
				if (_controlsOverrider.IndependentBrake != null)
				{
					_controlsOverrider.IndependentBrake.Set(0f);
				}
				if (_controlsOverrider.Brake != null)
				{
					_controlsOverrider.Brake.Set(0f);
				}
				float dir = ((_ambientLockedReverser != 0f) ? _ambientLockedReverser : ((_desiredReverser != 0f) ? _desiredReverser : 1f));
				if (_controlsOverrider.Reverser != null)
				{
					_controlsOverrider.Reverser.Set(MapDirectionToReverserControl(dir));
				}
				if (_controlsOverrider.BrakeCutout != null)
				{
					_controlsOverrider.BrakeCutout.Set(1f);
				}
				if (_controlsOverrider.HeadlightsFront != null)
				{
					_controlsOverrider.HeadlightsFront.Set(1f);
				}
			}
			float timeout = 6f;
			while (timeout > 0f && (!(_controlsOverrider != null) || !(_controlsOverrider.EngineOnReader != null) || !_controlsOverrider.EngineOnReader.IsOn))
			{
				timeout -= 0.5f;
				yield return new WaitForSeconds(0.5f);
			}
			_isStartingEngine = false;
		}

		public void ShutdownEngine()
		{
			if (_trainCar == null)
			{
				return;
			}
			if (_controlsOverrider == null && _trainCar.SimController != null)
			{
				_controlsOverrider = _trainCar.SimController.controlsOverrider;
			}
			_commandedThrottle = 0f;
			_rampThrottle = 0f;
			_currentThrottle = 0f;
			_commandedDynamicBrake = 0f;
			_commandedTrainBrake = 1f;
			_commandedIndependentBrake = 1f;
			_commandedReverser = 0f;
			_desiredReverser = 0f;
			if (_dm3Controller != null && _dm3Controller.IsDM3)
			{
				_dm3Controller.ApplyGearsInstant(0, 0);
			}
			if (_controlsOverrider != null)
			{
				if (_controlsOverrider.Throttle != null)
				{
					_controlsOverrider.Throttle.Set(0f);
				}
				if (_controlsOverrider.Brake != null)
				{
					_controlsOverrider.Brake.Set(1f);
				}
				if (_controlsOverrider.IndependentBrake != null)
				{
					_controlsOverrider.IndependentBrake.Set(1f);
				}
				if (_controlsOverrider.Handbrake != null)
				{
					_controlsOverrider.Handbrake.Set(1f);
				}
				if (_controlsOverrider.Reverser != null)
				{
					_controlsOverrider.Reverser.Set(0.5f);
				}
				if (_controlsOverrider.HeadlightsFront != null)
				{
					_controlsOverrider.HeadlightsFront.Set(0.4f);
				}
				if (_controlsOverrider.HeadlightsRear != null)
				{
					_controlsOverrider.HeadlightsRear.Set(0.4f);
				}
				if (_controlsOverrider.CabLight != null)
				{
					_controlsOverrider.CabLight.Set(0f);
				}
				if (_controlsOverrider.IndCabLight != null)
				{
					_controlsOverrider.IndCabLight.Set(0f);
				}
				if (_controlsOverrider.Wipers != null)
				{
					_controlsOverrider.Wipers.Set(0f);
				}
				if (_controlsOverrider.Starter != null)
				{
					_controlsOverrider.Starter.Set(0f);
				}
				if (_controlsOverrider.PowerOff != null)
				{
					_controlsOverrider.PowerOff.Set(1f);
				}
			}
			if (_trainCar.brakeSystem != null)
			{
				_trainCar.brakeSystem.SetHandbrakePosition(1f);
			}
			if (_trainCar.trainset == null || _trainCar.trainset.cars == null)
			{
				return;
			}
			foreach (TrainCar car in _trainCar.trainset.cars)
			{
				if (car != null && car != _trainCar && car.IsLoco && car.SimController != null && car.SimController.controlsOverrider != null)
				{
					BaseControlsOverrider controlsOverrider = car.SimController.controlsOverrider;
					if (controlsOverrider.Throttle != null)
					{
						controlsOverrider.Throttle.Set(0f);
					}
					if (controlsOverrider.Brake != null)
					{
						controlsOverrider.Brake.Set(1f);
					}
					if (controlsOverrider.IndependentBrake != null)
					{
						controlsOverrider.IndependentBrake.Set(1f);
					}
					if (controlsOverrider.Handbrake != null)
					{
						controlsOverrider.Handbrake.Set(1f);
					}
					if (controlsOverrider.Reverser != null)
					{
						controlsOverrider.Reverser.Set(0.5f);
					}
					if (controlsOverrider.HeadlightsFront != null)
					{
						controlsOverrider.HeadlightsFront.Set(0.4f);
					}
					if (controlsOverrider.HeadlightsRear != null)
					{
						controlsOverrider.HeadlightsRear.Set(0.4f);
					}
					if (controlsOverrider.CabLight != null)
					{
						controlsOverrider.CabLight.Set(0f);
					}
					if (controlsOverrider.Starter != null)
					{
						controlsOverrider.Starter.Set(0f);
					}
					if (controlsOverrider.PowerOff != null)
					{
						controlsOverrider.PowerOff.Set(1f);
					}
				}
				if (car != null && car != _trainCar && car.brakeSystem != null)
				{
					car.brakeSystem.SetHandbrakePosition(1f);
				}
			}
		}

		public void ReleaseAllConsistHandbrakes()
		{
			if (_controlsOverrider != null && _controlsOverrider.Handbrake != null)
			{
				_controlsOverrider.Handbrake.Set(0f);
			}
			if (_trainCar != null && _trainCar.brakeSystem != null)
			{
				_trainCar.brakeSystem.SetHandbrakePosition(0f);
			}
			if (!(_trainCar != null) || _trainCar.trainset == null || _trainCar.trainset.cars == null)
			{
				return;
			}
			for (int i = 0; i < _trainCar.trainset.cars.Count; i++)
			{
				TrainCar trainCar = _trainCar.trainset.cars[i];
				if (!(trainCar == null))
				{
					if (trainCar.brakeSystem != null)
					{
						trainCar.brakeSystem.SetHandbrakePosition(0f);
					}
					if (trainCar.SimController != null && trainCar.SimController.controlsOverrider != null && trainCar.SimController.controlsOverrider.Handbrake != null)
					{
						trainCar.SimController.controlsOverrider.Handbrake.Set(0f);
					}
				}
			}
		}

		public void SetDestination(float distance, bool isStation, bool isTerminus, float dwellTime)
		{
			DistanceToDestination = distance;
			IsStationDestination = isStation;
			IsTerminusDestination = isTerminus;
			StationDwellDuration = Mathf.Clamp(dwellTime, 10f, 300f);
		}

		public void ClearDestination()
		{
			DistanceToDestination = float.PositiveInfinity;
			IsStationDestination = false;
			IsTerminusDestination = false;
			HasArrivedAtDestination = false;
		}

		public void EmergencyStop()
		{
			_isEmergencyStop = true;
			TargetSpeedKmh = 0f;
			TargetSpeedMs = 0f;
			State = EngineState.Braking;
			_commandedThrottle = 0f;
			_commandedTrainBrake = 1f;
			_commandedIndependentBrake = 1f;
			if (_controlsOverrider != null)
			{
				if (_controlsOverrider.Throttle != null)
				{
					_controlsOverrider.Throttle.Set(0f);
				}
				if (_controlsOverrider.Brake != null)
				{
					_controlsOverrider.Brake.Set(1f);
				}
				if (_controlsOverrider.IndependentBrake != null)
				{
					_controlsOverrider.IndependentBrake.Set(1f);
				}
			}
		}

		public void EmergencyBrake()
		{
			EmergencyStop();
		}

		public void Resume()
		{
			_isEmergencyStop = false;
			HasArrivedAtDestination = false;
			if (State == EngineState.TerminusStop || State == EngineState.Idle)
			{
				State = EngineState.Starting;
			}
		}

		public static bool TryGetJunctionBetweenTracks(RailTrack trackA, RailTrack trackB, out Junction junction, out byte requiredBranch)
		{
			return SignalRegistry.TryGetJunctionBetweenTracks(trackA, trackB, out junction, out requiredBranch);
		}
	}
}
