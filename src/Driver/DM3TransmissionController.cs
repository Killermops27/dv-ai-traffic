using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using LocoSim.Implementations;
using AITraffic.Compat;

namespace AITraffic.Driver
{
    /// <summary>
    /// Automatic transmission and gear shifting controller for the DM3 mechanical diesel shunter.
    /// Manages compound 3-speed Gearbox A and 3-speed Gearbox B levers (9 forward / 9 reverse gears),
    /// throttle disengagement during shifts, speed-based upshifts/downshifts.
    /// </summary>
    public class DM3TransmissionController
    {
        public struct DM3Gear
        {
            public int GearNumber;    // 1 to 9
            public int GearA;         // 1, 2, or 3
            public int GearB;         // 1, 2, or 3
            public float DownshiftKmh; // Speed below which to downshift
            public float UpshiftKmh;   // Speed at or above which to upshift

            public DM3Gear(int num, int a, int b, float downshift, float upshift)
            {
                GearNumber = num;
                GearA = a;
                GearB = b;
                DownshiftKmh = downshift;
                UpshiftKmh = upshift;
            }
        }

        private static readonly DM3Gear[] s_gearTable = new DM3Gear[]
        {
            new DM3Gear(1, 1, 1,  0.0f,  8.0f),
            new DM3Gear(2, 1, 2,  4.5f, 13.0f),
            new DM3Gear(3, 2, 1,  7.0f, 16.5f),
            new DM3Gear(4, 2, 2, 11.0f, 22.0f),
            new DM3Gear(5, 3, 1, 15.0f, 26.5f),
            new DM3Gear(6, 3, 2, 19.5f, 32.5f),
            new DM3Gear(7, 2, 3, 24.5f, 43.0f),
            new DM3Gear(8, 3, 3, 35.0f, 75.0f)
        };

        private readonly AIEngineer _engineer;
        private readonly TrainCar _trainCar;

        public bool IsDM3 { get; private set; }
        public bool IsShifting { get; private set; }
        public bool IsInNeutral { get; private set; }
        public int CurrentGearIndex { get; private set; }
        public int CurrentGearA { get; private set; }
        public int CurrentGearB { get; private set; }

        private Port _portGearboxA;
        private Port _portGearboxB;
        private bool _initialized = false;

        private float _shiftCooldown = 0.0f;
        private float _shiftPhaseTimer = 0.0f;
        private int _pendingTargetGear = 0;
        private int _shiftStep = 0;
        private float _restartCooldown = 0.0f;

        public DM3TransmissionController(AIEngineer engineer, TrainCar trainCar)
        {
            _engineer = engineer;
            _trainCar = trainCar;

            // DV's DM3 gearbox has no Neutral notch (valid positions are 1..3 = 0.0 / 0.5 / 1.0);
            // physical idle/neutral uses Reverser = 0 & Throttle = 0 with levers resting in A1-B1.
            CurrentGearIndex = 1;
            CurrentGearA = 1;
            CurrentGearB = 1;
            IsInNeutral = false;

            Initialize();
        }

        public void Initialize()
        {
            if (_trainCar == null) return;

            string liveryId = _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
            bool hasGearController = _trainCar.GetComponent<DV.Simulation.Controllers.ManualGearShiftingController>() != null;

            IsDM3 = liveryId.IndexOf("DM3", StringComparison.OrdinalIgnoreCase) >= 0 || hasGearController;
            if (!IsDM3) return;

            // CRITICAL INVARIANT [LOG-01]: Single-attempt initialization latch prevents 60 Hz retry & log spam in Update().
            _initialized = true;

            try
            {
                // Find ports directly from SimulationFlow
                if (_trainCar.SimController != null && _trainCar.SimController.SimulationFlow != null)
                {
                    var simFlow = _trainCar.SimController.SimulationFlow;

                    // Standard DM3 port names in Derail Valley
                    simFlow.TryGetPort("gearInputA.CONTROL_EXT_IN", out _portGearboxA);
                    simFlow.TryGetPort("gearInputB.CONTROL_EXT_IN", out _portGearboxB);

                    if (_portGearboxA == null || _portGearboxB == null)
                    {
                        var allPorts = simFlow.AllPorts;
                        if (allPorts != null)
                        {
                            for (int i = 0; i < allPorts.Count; i++)
                            {
                                var port = allPorts[i];
                                if (port == null || string.IsNullOrEmpty(port.id)) continue;

                                string lower = port.id.ToLowerInvariant();
                                if (_portGearboxA == null && (lower.Contains("gearinputa") || lower.Contains("gearboxa")) && lower.Contains("ext_in"))
                                {
                                    _portGearboxA = port;
                                }
                                else if (_portGearboxB == null && (lower.Contains("gearinputb") || lower.Contains("gearboxb")) && lower.Contains("ext_in"))
                                {
                                    _portGearboxB = port;
                                }
                            }
                        }
                    }
                }

                // DV DM3 rests in lowest reduction A1-B1 (0.0f, 0.0f); engine/drivetrain neutral is via Reverser=0 & Throttle=0
                ApplyGearsInstant(1, 1);
                CurrentGearIndex = 1;
                CurrentGearA = 1;
                CurrentGearB = 1;
                IsInNeutral = false;

                if (Main.IsDebugLoggingEnabled)
                    Main.LogDebug(string.Format("[DM3Transmission] Initialized DM3 loco '{0}' (PortA: {1}, PortB: {2}) in A1-B1.",
                        ModCompatManager.GetCarId(_trainCar), _portGearboxA != null ? _portGearboxA.id : "null", _portGearboxB != null ? _portGearboxB.id : "null"));
            }
            catch (Exception ex)
            {
                Main.LogWarning(string.Format("[DM3Transmission] Error initializing DM3: {0}", ex.Message));
            }
        }

        public void Update(float dt)
        {
            if (!IsDM3 || _trainCar == null) return;

            // CRITICAL INVARIANT [AIE-22]: halted forever once the train is parked. This is what stops the
            // "shifts → throttle cut → brake → shift" cycle that was audible after the permanent-stall clamp, and it
            // is the only thing that keeps a 0 km/h gearbox from upshifting out of gear 1 (UpshiftKmh == 0.0f).
            if (_parked) return;

            // Lazy initialization retry (single-attempt latch)
            if (!_initialized && (_portGearboxA == null || _portGearboxB == null))
            {
                Initialize();
            }

            if (_restartCooldown > 0.0f) _restartCooldown -= dt;
            if (_shiftCooldown > 0.0f) _shiftCooldown -= dt;

            // 1. Anti-Stall & Engine Running Verification
            bool isEngineOn = true;
            if (_trainCar.SimController != null && _trainCar.SimController.controlsOverrider != null)
            {
                var controls = _trainCar.SimController.controlsOverrider;
                if (controls.EngineOnReader != null)
                {
                    isEngineOn = controls.EngineOnReader.IsOn;
                }
            }

            if (!isEngineOn)
            {
                // Seat gearbox in A1-B1 while restarting engine (Reverser=0 / Throttle=0 unloads DieselEngineDirectDrive)
                if (IsShifting || IsInNeutral || CurrentGearIndex != 1 || CurrentGearA != 1 || CurrentGearB != 1)
                {
                    ForceNeutral();
                }
                else
                {
                    EnsurePortsMatchCurrentGears();
                }

                // Restart engine in A1-B1 rest state
                if (_restartCooldown <= 0.0f)
                {
                    _restartCooldown = 2.0f;
                    if (_engineer != null) _engineer.EnsureEngineRunning();
                }
                return;
            }

            // 2. Handle Active Shift Sequence (throttle is held at absolute 0)
            if (IsShifting)
            {
                ProcessShiftSequence(dt);
                return;
            }

            // Self-heal if prefab initialization order or SteppedJoint reset drifted ports away from current gear
            EnsurePortsMatchCurrentGears();

            // 3. Speed & Target Analysis
            float speedKmh = _engineer != null ? _engineer.CurrentSpeedKmh : 0.0f;
            float targetKmh = _engineer != null ? _engineer.TargetSpeedKmh : 0.0f;
            float brakeAmt = _engineer != null ? _engineer.CurrentTrainBrake : 0.0f;

            // 4. Rest Gear (A1-B1) Management at Standstill / Braking (no dummy 0->1 shift needed on launch)
            if (targetKmh <= 0.5f || (targetKmh <= 1.0f && speedKmh < 1.5f && brakeAmt > 0.15f))
            {
                if ((IsInNeutral || CurrentGearIndex != 1 || CurrentGearA != 1 || CurrentGearB != 1) && _shiftCooldown <= 0.0f)
                {
                    ApplyGearsInstant(1, 1);
                    CurrentGearIndex = 1;
                    CurrentGearA = 1;
                    CurrentGearB = 1;
                    IsInNeutral = false;
                    _shiftCooldown = 0.5f;
                }
                return;
            }

            // 5. Determine Ideal Gear for moving
            int idealGear = DetermineIdealGear(speedKmh, targetKmh);
            if ((idealGear != CurrentGearIndex || IsInNeutral) && _shiftCooldown <= 0.0f)
            {
                BeginShift(idealGear);
            }
        }

        private int DetermineIdealGear(float speedKmh, float targetKmh)
        {
            if (targetKmh <= 0.1f && speedKmh < 3.0f)
            {
                return 1;
            }

            int current = CurrentGearIndex;
            if (current <= 0) current = 1;

            // CRITICAL INVARIANT [AIE-21]: no gear change at a standstill. Gear 1 has UpshiftKmh == 0.0f, so
            // `speedKmh >= curDef.UpshiftKmh` is trivially true at 0 km/h and the controller upshifts the moment the
            // train stops — the mechanical half of the Issue 3 loop. Downshifting is likewise pointless while the
            // wheels are not turning; the neutral-management branch above already covers a genuine stop.
            if (speedKmh < 0.5f)
            {
                return current;
            }

            // Cap maximum gear so the DM3 does not upshift beyond target line speed
            int maxAllowedGear = s_gearTable.Length;
            for (int i = 0; i < s_gearTable.Length; i++)
            {
                if (targetKmh <= s_gearTable[i].UpshiftKmh + 2.0f)
                {
                    maxAllowedGear = s_gearTable[i].GearNumber;
                    break;
                }
            }

            // Check for upshift
            if (current < maxAllowedGear)
            {
                var curDef = s_gearTable[current - 1];
                if (speedKmh >= curDef.UpshiftKmh)
                {
                    return current + 1;
                }
            }

            // Check for downshift (wide hysteresis prevents hunting when speed drops during shifts)
            if (current > 1)
            {
                var curDef = s_gearTable[current - 1];
                if (speedKmh < curDef.DownshiftKmh)
                {
                    return current - 1;
                }
            }

            return current;
        }

        public void BeginShift(int targetGear)
        {
            if (targetGear < 1 || targetGear > s_gearTable.Length)
                return;

            _pendingTargetGear = targetGear;
            IsShifting = true;
            _shiftStep = 1;
            _shiftPhaseTimer = 0.25f; // Step 1: Throttle immediately cut to 0 to unload gearbox

            if (_engineer != null && _engineer.ThrottlePID != null)
            {
                _engineer.ThrottlePID.Reset();
            }
        }

        private void ProcessShiftSequence(float dt)
        {
            _shiftPhaseTimer -= dt;
            if (_shiftPhaseTimer > 0.0f) return;

            switch (_shiftStep)
            {
                case 1: // Step 2: Switch gears while completely unloaded at 0 throttle
                    var gearDef = s_gearTable[_pendingTargetGear - 1];
                    ApplyGearsInstant(gearDef.GearA, gearDef.GearB);
                    CurrentGearIndex = _pendingTargetGear;
                    CurrentGearA = gearDef.GearA;
                    CurrentGearB = gearDef.GearB;
                    IsInNeutral = false;

                    _shiftStep = 2;
                    _shiftPhaseTimer = 0.45f; // Hold 0 throttle for gear synchronizers and meshing
                    break;

                case 2: // Step 3: Brief settling delay before restoring throttle
                    _shiftStep = 3;
                    _shiftPhaseTimer = 0.25f; // Settling delay at 0 throttle
                    break;

                case 3: // Shift complete, re-enable throttle with 3.5s cooldown lock
                    IsShifting = false;
                    _shiftStep = 0;
                    _shiftCooldown = 3.5f; // Prevents shift oscillation / hunting
                    if (_engineer != null && _engineer.ThrottlePID != null)
                    {
                        _engineer.ThrottlePID.Reset();
                    }
                    break;
            }
        }

        // CRITICAL INVARIANT [AIE-22]: a parked controller must never shift again. Set once by
        // AIEngineer.SecureTrainForTerminalStop() and cleared only by AIEngineer.Resume().
        private bool _parked = false;

        public bool IsParked
        {
            get { return _parked; }
        }

        public void SetParked(bool parked)
        {
            _parked = parked;
        }

        private static float GearToNormalizedPortValue(int gear)
        {
            // DV DM3 ManualTransmissionInput has numOfGears = 3 with gear0IsNeutral = false and 3-notch SteppedJoint:
            // Notch 0 (-35 deg) = Gear 1 -> 0.0f
            // Notch 1 (  0 deg) = Gear 2 -> 0.5f (A2 / B2)
            // Notch 2 (+35 deg) = Gear 3 -> 1.0f
            int clamped = Mathf.Clamp(gear, 1, 3);
            return (clamped - 1) * 0.5f;
        }

        private void EnsurePortsMatchCurrentGears()
        {
            int targetA = Mathf.Clamp(CurrentGearA, 1, 3);
            int targetB = Mathf.Clamp(CurrentGearB, 1, 3);
            float expectedA = GearToNormalizedPortValue(targetA);
            float expectedB = GearToNormalizedPortValue(targetB);

            bool desyncA = _portGearboxA != null && Mathf.Abs(_portGearboxA.Value - expectedA) > 0.08f;
            bool desyncB = _portGearboxB != null && Mathf.Abs(_portGearboxB.Value - expectedB) > 0.08f;
            if (desyncA || desyncB)
            {
                ApplyGearsInstant(targetA, targetB);
            }
        }

        public void ApplyGearsInstant(int gearA, int gearB)
        {
            float valA = GearToNormalizedPortValue(gearA);
            float valB = GearToNormalizedPortValue(gearB);

            if (_portGearboxA != null)
            {
                _portGearboxA.Value = valA;
            }

            if (_portGearboxB != null)
            {
                _portGearboxB.Value = valB;
            }
        }

        // CRITICAL INVARIANT [AIE-22]: ApplyGearsInstant() writes the ports but never updates CurrentGearIndex /
        // CurrentGearA / CurrentGearB / IsInNeutral, so every external caller leaves the controller lying about its
        // physical state and the next Update re-shifts (the port-flap described in the scout report). This helper is
        // the single, index-consistent rest/park state setter (seating DV's 3-notch gearbox into A1-B1 = 0.0f, 0.0f).
        public void ForceNeutral()
        {
            ApplyGearsInstant(1, 1);
            CurrentGearIndex = 1;
            CurrentGearA = 1;
            CurrentGearB = 1;
            IsInNeutral = false;
            IsShifting = false;
            _shiftStep = 0;
            _shiftCooldown = 0.0f;
        }
    }
}
