using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using LocoSim.Implementations;

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

        private float _shiftCooldown = 0.0f;
        private float _shiftPhaseTimer = 0.0f;
        private int _pendingTargetGear = 0;
        private int _shiftStep = 0;
        private float _restartCooldown = 0.0f;

        public DM3TransmissionController(AIEngineer engineer, TrainCar trainCar)
        {
            _engineer = engineer;
            _trainCar = trainCar;

            CurrentGearIndex = 0;
            CurrentGearA = 0;
            CurrentGearB = 0;
            IsInNeutral = true;

            Initialize();
        }

        public void Initialize()
        {
            if (_trainCar == null) return;

            string liveryId = _trainCar.carLivery != null ? _trainCar.carLivery.id : "";
            bool hasGearController = _trainCar.GetComponent<DV.Simulation.Controllers.ManualGearShiftingController>() != null;

            IsDM3 = liveryId.IndexOf("DM3", StringComparison.OrdinalIgnoreCase) >= 0 || hasGearController;
            if (!IsDM3) return;

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

                // Start in Neutral to prevent stalling at rest
                ApplyGearsInstant(0, 0);
                IsInNeutral = true;

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[DM3Transmission] Initialized DM3 loco '{0}' (PortA: {1}, PortB: {2}) in Neutral.", 
                        _trainCar.ID, _portGearboxA != null ? _portGearboxA.id : "null", _portGearboxB != null ? _portGearboxB.id : "null"));
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[DM3Transmission] Error initializing DM3: {0}", ex.Message));
            }
        }

        public void Update(float dt)
        {
            if (!IsDM3 || _trainCar == null) return;

            if (_portGearboxA == null || _portGearboxB == null)
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
                // Disengage transmission to Neutral so starter has zero load
                if (!IsInNeutral)
                {
                    ApplyGearsInstant(0, 0);
                    IsInNeutral = true;
                    CurrentGearIndex = 0;
                }

                // Restart engine in Neutral
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

            // 3. Speed & Target Analysis
            float speedKmh = _engineer != null ? _engineer.CurrentSpeedKmh : 0.0f;
            float targetKmh = _engineer != null ? _engineer.TargetSpeedKmh : 0.0f;
            float brakeAmt = _engineer != null ? _engineer.CurrentTrainBrake : 0.0f;

            // 4. Neutral Management at Standstill / Braking (only when stopping)
            if (targetKmh <= 0.5f || (targetKmh <= 1.0f && speedKmh < 1.5f && brakeAmt > 0.15f))
            {
                if (!IsInNeutral && _shiftCooldown <= 0.0f)
                {
                    ApplyGearsInstant(0, 0);
                    IsInNeutral = true;
                    CurrentGearIndex = 0;
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

        public void ApplyGearsInstant(int gearA, int gearB)
        {
            // Derail Valley ManualTransmissionInput uses a normalized 0.0 to 1.0 range:
            // 0 = 0.0f (Neutral)
            // 1 = 0.3333333f (Gear 1)
            // 2 = 0.6666667f (Gear 2)
            // 3 = 1.0f (Gear 3)
            float valA = gearA <= 0 ? 0.0f : (gearA / 3.0f);
            float valB = gearB <= 0 ? 0.0f : (gearB / 3.0f);

            if (_portGearboxA != null)
            {
                _portGearboxA.Value = valA;
            }

            if (_portGearboxB != null)
            {
                _portGearboxB.Value = valB;
            }
        }
    }
}
