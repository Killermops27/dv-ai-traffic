using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using DV.Simulation.Cars;
using DV.ThingTypes;
using DV.WeatherSystem;
using AITraffic.Config;
using AITraffic.Fleet;

namespace AITraffic.Driver
{
    /// <summary>
    /// Manages intelligent, environment-aware locomotive and consist lighting for AI trains.
    /// Supports:
    /// - 4 Operating Modes: Automatic (Day/Night/Tunnel), Always High Beam, Always Low Beam, Always Off.
    /// - Direction-aware forward headlights.
    /// - Consist rear lighting:
    ///     * Passenger Wagons (Pax): illuminates native electric red marker/tail lights.
    ///     * Locomotives: illuminates native rear red marker lights.
    ///     * Freight Cars & Caboose: attaches the standalone ConsistEOTMarker to the rear coupler/bumper.
    /// - Interior cab gauge backlights and dome light during night or tunnels.
    /// </summary>
    public class AILocomotiveLighting : MonoBehaviour
    {
        public TrainCar Locomotive { get; private set; }
        public AIEngineer Engineer { get; set; }
        public AILightingMode Mode { get; set; }

        private void Awake()
        {
            Mode = AILightingMode.Automatic;
        }

        private BaseControlsOverrider _controlsOverrider;
        private TrainCar _lastRearCar;
        private float _updateTimer = 0f;
        private const float UpdateInterval = 0.5f; // Stagger checks twice a second for high performance

        private bool _isNightOrTunnel = false;
        private bool _isInitialized = false;

        public bool IsLightEngine
        {
            get
            {
                if (Locomotive == null) return true;
                bool rearCoupled = Locomotive.rearCoupler != null && Locomotive.rearCoupler.IsCoupled();
                bool frontCoupled = Locomotive.frontCoupler != null && Locomotive.frontCoupler.IsCoupled();
                if (!rearCoupled && !frontCoupled) return true;

                var cars = GetConsistCars();
                return cars == null || cars.Count <= 1;
            }
        }

        /// <summary>
        /// Retrieves or creates an AILocomotiveLighting manager component on the specified locomotive.
        /// </summary>
        public static AILocomotiveLighting GetOrCreate(TrainCar loco, AIEngineer engineer = null)
        {
            if (loco == null || loco.gameObject == null) return null;

            var lighting = loco.GetComponent<AILocomotiveLighting>();
            if (lighting == null)
            {
                lighting = loco.gameObject.AddComponent<AILocomotiveLighting>();
            }

            lighting.Initialize(loco, engineer);
            return lighting;
        }

        /// <summary>
        /// Direct static utility to configure consist lighting on demand.
        /// </summary>
        public static void ApplyConsistLighting(TrainCar leadLoco, AILightingMode mode = AILightingMode.Automatic)
        {
            var lighting = GetOrCreate(leadLoco);
            if (lighting != null)
            {
                lighting.Mode = mode;
                lighting.UpdateLightingInstant();
            }
        }

        public void Initialize(TrainCar loco, AIEngineer engineer = null)
        {
            Locomotive = loco;
            Engineer = engineer;

            if (IniConfigManager.Instance != null && IniConfigManager.Instance.Lighting != null)
            {
                Mode = IniConfigManager.Instance.Lighting.Mode;
            }

            if (loco != null && loco.SimController != null)
            {
                _controlsOverrider = loco.SimController.controlsOverrider;
            }

            // Suppress native AutomaticHeadlightsController so base game does not conflict with AI lighting
            if (loco != null)
            {
                var auto = loco.GetComponent<AutomaticHeadlightsController>();
                if (auto != null)
                {
                    UnityEngine.Object.Destroy(auto);
                }
            }

            _isInitialized = true;
            UpdateLightingInstant();
        }

        private void Update()
        {
            if (!_isInitialized || Locomotive == null || Locomotive.gameObject == null) return;

            _updateTimer += Time.deltaTime;
            if (_updateTimer >= UpdateInterval)
            {
                _updateTimer = 0f;
#if DEBUG
                long __wdStart = AITraffic.Diagnostics.HitchWatchdog.GetTimestamp();
                long __wdMem = AITraffic.Diagnostics.HitchWatchdog.GetCurrentMemory();
                try {
                    UpdateLighting(UpdateInterval);
                } finally {
                    AITraffic.Diagnostics.HitchWatchdog.RecordLighting(__wdStart, __wdMem);
                }
#else
                UpdateLighting(UpdateInterval);
#endif
            }
        }

        /// <summary>
        /// Periodic lighting evaluation.
        /// </summary>
        public void UpdateLighting(float dt)
        {
            if (Locomotive == null) return;

            // Ensure controls overrider reference is valid
            if (_controlsOverrider == null && Locomotive.SimController != null)
            {
                _controlsOverrider = Locomotive.SimController.controlsOverrider;
            }

            // Sync mode with IniConfigManager if updated
            if (IniConfigManager.Instance != null && IniConfigManager.Instance.Lighting != null)
            {
                Mode = IniConfigManager.Instance.Lighting.Mode;
            }

            // 1. Evaluate environmental illumination conditions
            float desiredIntensity = CalculateDesiredHeadlightIntensity();

            // 2. Update direction-aware forward headlights
            UpdateForwardHeadlights(desiredIntensity);

            // 3. Update cab backlights and dome illumination
            UpdateCabLighting();

            // 4. Update consist rear marker lighting
            UpdateConsistRearLighting();
        }

        /// <summary>
        /// Forces an immediate synchronous lighting refresh.
        /// </summary>
        public void UpdateLightingInstant()
        {
            UpdateLighting(0f);
        }

        #region Environmental Evaluation

        private float CalculateDesiredHeadlightIntensity()
        {
            switch (Mode)
            {
                case AILightingMode.AlwaysHigh:
                    _isNightOrTunnel = CheckNightOrTunnel();
                    return 1.0f; // High beam

                case AILightingMode.AlwaysLow:
                    _isNightOrTunnel = CheckNightOrTunnel();
                    return 0.8f; // Low / Mid beam

                case AILightingMode.Off:
                    _isNightOrTunnel = false;
                    return 0.4f; // Off (neutral)

                case AILightingMode.Automatic:
                default:
                    _isNightOrTunnel = CheckNightOrTunnel();
                    // High beam (1.0f) in dark/inclement conditions, low/normal beam (0.8f) during daytime for crossing warning
                    return _isNightOrTunnel ? 1.0f : 0.8f;
            }
        }

        private bool CheckNightOrTunnel()
        {
            // 1. Check time of day (Night / Dawn / Dusk)
            // Derail Valley timeOfDay is 0.0 to 1.0 (0.2917 ~ 7:00 AM, 0.8333 ~ 8:00 PM)
            float timeOfDay = 0.5f;
            try
            {
                if (WeatherDriver.Instance != null && WeatherDriver.Instance.manager != null)
                {
                    timeOfDay = WeatherDriver.Instance.manager.timeOfDay;
                }
            }
            catch { }

            bool isNight = (timeOfDay < 0.2916667f || timeOfDay > 0.8333333f);
            if (isNight) return true;

            // 2. Check inclement weather / fog / rain
            try
            {
                if (WeatherDriver.Instance != null && WeatherDriver.Instance.IsRaining)
                {
                    return true;
                }

                if (WeatherDriver.Instance != null && Locomotive != null)
                {
                    float fog = WeatherDriver.Instance.GetFogDensity(Locomotive.transform.position);
                    if (fog > 0.45f) return true;
                }
            }
            catch { }

            // 3. Check if inside a tunnel or covered cavern
            if (Locomotive != null)
            {
                Vector3 origin = Locomotive.transform.position + Vector3.up * 1.5f;
                int mask = LayerMask.GetMask("Terrain", "World", "Default");
                if (mask == 0) mask = ~0;

                RaycastHit hit;
                if (Physics.Raycast(origin, Vector3.up, out hit, 35.0f, mask))
                {
                    if (hit.collider != null && !hit.collider.transform.IsChildOf(Locomotive.transform))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region Headlight & Cab Control

        /// <summary>
        /// Derail Valley locomotives use 0.4f (HeadlightsControlBase.NEUTRAL_VALUE) as the neutral OFF notch.
        /// Setting 0.0f selects index 0 which corresponds to RED marker / taillights.
        /// This helper dynamically returns the exact control value corresponding to mainOffSetup on any locomotive.
        /// </summary>
        public static float GetOffControlValue(TrainCar loco, bool front)
        {
            if (loco != null)
            {
                var hmc = loco.GetComponent<HeadlightsMainController>();
                if (hmc != null)
                {
                    int count = hmc.GetSetupCount(front);
                    if (count > 1)
                    {
                        int offIndex = hmc.GetOffIndex(front);
                        return Mathf.Clamp01((float)offIndex / (float)(count - 1));
                    }
                }
            }
            return 0.4f; // Fallback to HeadlightsControlBase.NEUTRAL_VALUE
        }

        private void UpdateForwardHeadlights(float desiredIntensity)
        {
            if (_controlsOverrider == null) return;

            float offValFront = GetOffControlValue(Locomotive, true);
            float offValRear = GetOffControlValue(Locomotive, false);

            if (Mode == AILightingMode.Off)
            {
                if (_controlsOverrider.HeadlightsFront != null) _controlsOverrider.HeadlightsFront.Set(offValFront);
                if (_controlsOverrider.HeadlightsRear != null) _controlsOverrider.HeadlightsRear.Set(offValRear);
                return;
            }

            // Determine active locomotive movement direction based on physical reverser gear
            float direction = 1.0f;
            // CRITICAL INVARIANT [FLT-13]: DV Reverser.Value is normalized [0,1] (0 = reverse, 0.5 = neutral, 1 = forward).
            // Always map it to signed [-1,1] before comparing against the signed AIEngineer reverser domain.
            float physicalReverserDir = (_controlsOverrider != null && _controlsOverrider.Reverser != null)
                ? (_controlsOverrider.Reverser.Value - 0.5f) * 2.0f
                : 0.0f;
            if (Mathf.Abs(physicalReverserDir) > 0.1f)
            {
                direction = physicalReverserDir;
            }
            else if (Engineer != null && Mathf.Abs(Engineer.CommandedReverser) > 0.1f)
            {
                direction = Engineer.CommandedReverser;
            }
            else if (Engineer != null && Mathf.Abs(Engineer.DesiredReverser) > 0.1f)
            {
                direction = Engineer.DesiredReverser;
            }
            else if (Engineer != null && Mathf.Abs(Engineer.CurrentReverser) > 0.1f)
            {
                direction = Engineer.CurrentReverser;
            }

            bool isForward = direction >= 0f;

            // Physical coupler checks: determine if cars are physically coupled to the front or rear of this locomotive
            bool frontHasCars = (Locomotive.frontCoupler != null &&
                (Locomotive.frontCoupler.IsCoupled() || (Locomotive.frontCoupler.hoseAndCock != null && Locomotive.frontCoupler.hoseAndCock.IsHoseConnected)));

            bool rearHasCars = (Locomotive.rearCoupler != null &&
                (Locomotive.rearCoupler.IsCoupled() || (Locomotive.rearCoupler.hoseAndCock != null && Locomotive.rearCoupler.hoseAndCock.IsHoseConnected)));

            // Additional trainset consistency check: if the trainset contains other cars, verify connection
            if (Locomotive.trainset != null && Locomotive.trainset.cars != null && Locomotive.trainset.cars.Count > 1)
            {
                var cars = Locomotive.trainset.cars;
                for (int i = 0; i < cars.Count; i++)
                {
                    var c = cars[i];
                    if (c == null || c == Locomotive) continue;
                    if (Locomotive.frontCoupler != null && Locomotive.frontCoupler.GetCoupled() != null && Locomotive.frontCoupler.GetCoupled().train == c)
                    {
                        frontHasCars = true;
                    }
                    if (Locomotive.rearCoupler != null && Locomotive.rearCoupler.GetCoupled() != null && Locomotive.rearCoupler.GetCoupled().train == c)
                    {
                        rearHasCars = true;
                    }
                }
            }

            float frontControlVal;
            float rearControlVal;

            if (isForward)
            {
                // Driving forward:
                // Front end is leading: illuminate forward headlights if no cars attached in front, otherwise off
                frontControlVal = frontHasCars ? offValFront : desiredIntensity;

                // Rear end is trailing:
                // Taillight is ONLY on/red (0.0f) when there aren't any cars attached behind this locomotive
                // (e.g. solo light engine or locomotive at the rear of the train).
                // If consist has cars attached behind, rear headlights are strictly turned OFF (offValRear = 0.4f).
                rearControlVal = rearHasCars ? offValRear : 0.0f;
            }
            else
            {
                // Driving in reverse:
                // Rear end is leading: illuminate rear headlights if no cars attached behind, otherwise off
                rearControlVal = rearHasCars ? offValRear : desiredIntensity;

                // Front end is trailing:
                // Taillight is ONLY on/red (0.0f) when there aren't any cars attached in front of this locomotive.
                // If consist has cars attached in front, front headlights are strictly turned OFF (offValFront = 0.4f).
                frontControlVal = frontHasCars ? offValFront : 0.0f;
            }

            if (_controlsOverrider.HeadlightsFront != null)
            {
                _controlsOverrider.HeadlightsFront.Set(frontControlVal);
            }
            if (_controlsOverrider.HeadlightsRear != null)
            {
                _controlsOverrider.HeadlightsRear.Set(rearControlVal);
            }
        }

        private void UpdateCabLighting()
        {
            if (_controlsOverrider == null) return;

            if (Mode == AILightingMode.Off)
            {
                if (_controlsOverrider.CabLight != null) _controlsOverrider.CabLight.Set(0.0f);
                if (_controlsOverrider.IndCabLight != null) _controlsOverrider.IndCabLight.Set(0.0f);
                return;
            }

            bool cabConfig = true;
            if (IniConfigManager.Instance != null && IniConfigManager.Instance.Lighting != null)
            {
                cabConfig = IniConfigManager.Instance.Lighting.CabLightingAtNight;
            }
            bool enableCab = _isNightOrTunnel && cabConfig;
            float cabVal = enableCab ? 1.0f : 0.0f;

            if (_controlsOverrider.CabLight != null)
            {
                _controlsOverrider.CabLight.Set(cabVal);
            }

            if (_controlsOverrider.IndCabLight != null)
            {
                _controlsOverrider.IndCabLight.Set(cabVal);
            }
        }

        #endregion

        #region Consist Rear Lighting & EOT Marker

        /// <summary>
        /// Evaluates consist composition and updates the rear-most vehicle's marker lights.
        /// </summary>
        public void UpdateConsistRearLighting()
        {
            TrainCar rearCar = GetConsistRearCar();
            if (rearCar == null) return;

            // If the rear car changed, clean up previous car marker
            if (_lastRearCar != null && _lastRearCar != rearCar)
            {
                ConsistEOTMarker.RemoveFromCar(_lastRearCar);
                ExtinguishRearCarNativeLights(_lastRearCar);
            }
            _lastRearCar = rearCar;

            // Ensure intermediate consist cars do not retain lingering tail markers or EOT lanterns
            var consistCars = GetConsistCars();
            if (consistCars != null)
            {
                for (int i = 0; i < consistCars.Count; i++)
                {
                    var c = consistCars[i];
                    if (c != null && c != rearCar)
                    {
                        ConsistEOTMarker.RemoveFromCar(c);
                        if (IsPassengerCar(c) || c.IsLoco)
                        {
                            ExtinguishRearCarNativeLights(c);
                        }
                    }
                }
            }

            // If lighting mode is Off, extinguish markers
            if (Mode == AILightingMode.Off)
            {
                ConsistEOTMarker.RemoveFromCar(rearCar);
                ExtinguishRearCarNativeLights(rearCar);
                return;
            }

            // Case A: Passenger Wagon (Pax)
            if (IsPassengerCar(rearCar))
            {
                // Ensure standalone EOT marker is removed
                ConsistEOTMarker.RemoveFromCar(rearCar);
                // Illuminate native electric red marker/tail lights
                IlluminatePassengerTailLights(rearCar);
            }
            // Case B: Trailing Locomotive (Helper / DPU / Light engine)
            else if (rearCar.IsLoco)
            {
                ConsistEOTMarker.RemoveFromCar(rearCar);
                IlluminateLocomotiveRearMarkers(rearCar);
            }
            // Case C: Freight Car or Caboose
            else
            {
                ExtinguishRearCarNativeLights(rearCar);

                bool eotEnabled = true;
                bool flashing = true;
                float flashRate = 1.0f;
                if (IniConfigManager.Instance != null && IniConfigManager.Instance.Lighting != null)
                {
                    eotEnabled = IniConfigManager.Instance.Lighting.EOTMarker;
                    flashing = IniConfigManager.Instance.Lighting.EOTMarkerFlashing;
                    flashRate = IniConfigManager.Instance.Lighting.EOTFlashRateHz;
                }

                if (eotEnabled)
                {
                    ConsistEOTMarker.AttachToRearCar(rearCar, flashing, flashRate);
                }
                else
                {
                    ConsistEOTMarker.RemoveFromCar(rearCar);
                }
            }
        }

        private static Type s_cachedCoachLightType;
        private static MethodInfo s_cachedChangeRearLights;
        private static MethodInfo s_cachedChangeFrontLights;
        private static readonly object[] s_boxedTrue = new object[] { true };
        private static readonly object[] s_boxedFalse = new object[] { false };
        private static MaterialPropertyBlock s_tailLightPropBlock;

        private void IlluminatePassengerTailLights(TrainCar car)
        {
            if (car == null) return;

            bool uncoupledIsRear = (car.rearCoupler != null && !car.rearCoupler.IsCoupled());

            // 1. Check for PassengerJobs mod CoachLightController via reflection
            try
            {
                var coachLight = car.GetComponent("CoachLightController");
                if (coachLight != null)
                {
                    Type t = coachLight.GetType();
                    if (t != s_cachedCoachLightType)
                    {
                        s_cachedCoachLightType = t;
                        s_cachedChangeRearLights = t.GetMethod("ChangeRearLights", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        s_cachedChangeFrontLights = t.GetMethod("ChangeFrontLights", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    }

                    // NOTE: Do NOT invoke SetLightsOn here. PassengerJobs manages interior coach lighting
                    // based on its own day/night/tunnel cycle via NightLightController. Forcing SetLightsOn(true)
                    // creates a race condition that causes interior lights to flash/strobe.

                    if (uncoupledIsRear)
                    {
                        if (s_cachedChangeRearLights != null) s_cachedChangeRearLights.Invoke(coachLight, s_boxedTrue);
                        if (s_cachedChangeFrontLights != null) s_cachedChangeFrontLights.Invoke(coachLight, s_boxedFalse);
                    }
                    else
                    {
                        if (s_cachedChangeFrontLights != null) s_cachedChangeFrontLights.Invoke(coachLight, s_boxedTrue);
                        if (s_cachedChangeRearLights != null) s_cachedChangeRearLights.Invoke(coachLight, s_boxedFalse);
                    }
                    return;
                }
            }
            catch { }

            // 2. Direct hierarchy search for red tail lamp GameObjects / Renderers on passenger wagon
            try
            {
                var renderers = car.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    var rend = renderers[i];
                    if (rend == null) continue;
                    string name = rend.name;

                    bool isRearEndObject = uncoupledIsRear
                        ? (name.IndexOf("rear", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("r_", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0)
                        : (name.IndexOf("front", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("f_", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("_f", StringComparison.OrdinalIgnoreCase) >= 0);

                    if ((name.IndexOf("red", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("tail", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         name.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0) &&
                        (isRearEndObject || name.IndexOf("lamp", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        if (!rend.gameObject.activeSelf) rend.gameObject.SetActive(true);
                        if (rend.sharedMaterial != null)
                        {
                            rend.sharedMaterial.EnableKeyword("_EMISSION");
                            if (s_tailLightPropBlock == null) s_tailLightPropBlock = new MaterialPropertyBlock();
                            rend.GetPropertyBlock(s_tailLightPropBlock);
                            s_tailLightPropBlock.SetColor("_EmissionColor", new Color(1.0f, 0.05f, 0.05f));
                            rend.SetPropertyBlock(s_tailLightPropBlock);
                        }
                    }
                }
            }
            catch { }
        }

        private void IlluminateLocomotiveRearMarkers(TrainCar car)
        {
            if (car == null) return;

            // If the rear car is the lead locomotive itself, UpdateForwardHeadlights already
            // handles its direction-aware headlights and uncoupled taillights.
            if (car == Locomotive) return;

            if (car.SimController != null && car.SimController.controlsOverrider != null)
            {
                var overrider = car.SimController.controlsOverrider;
                bool uncoupledIsRear = (car.rearCoupler != null && !car.rearCoupler.IsCoupled());
                bool uncoupledIsFront = (car.frontCoupler != null && !car.frontCoupler.IsCoupled());

                float offFront = GetOffControlValue(car, true);
                float offRear = GetOffControlValue(car, false);

                if (uncoupledIsRear)
                {
                    if (overrider.HeadlightsRear != null) overrider.HeadlightsRear.Set(0.0f); // 0.0f = Red marker lights
                    if (overrider.HeadlightsFront != null) overrider.HeadlightsFront.Set(offFront);
                }
                else if (uncoupledIsFront)
                {
                    if (overrider.HeadlightsFront != null) overrider.HeadlightsFront.Set(0.0f); // 0.0f = Red marker lights
                    if (overrider.HeadlightsRear != null) overrider.HeadlightsRear.Set(offRear);
                }
                else
                {
                    // Neither end is uncoupled (surrounded by cars) -> strictly extinguish both ends!
                    if (overrider.HeadlightsFront != null) overrider.HeadlightsFront.Set(offFront);
                    if (overrider.HeadlightsRear != null) overrider.HeadlightsRear.Set(offRear);
                }
            }
        }

        private void ExtinguishRearCarNativeLights(TrainCar car)
        {
            if (car == null) return;

            // Turn off locomotive lights if trailing
            if (car.IsLoco && car != Locomotive && car.SimController != null && car.SimController.controlsOverrider != null)
            {
                var overrider = car.SimController.controlsOverrider;
                if (overrider.HeadlightsFront != null) overrider.HeadlightsFront.Set(GetOffControlValue(car, true));
                if (overrider.HeadlightsRear != null) overrider.HeadlightsRear.Set(GetOffControlValue(car, false));
            }

            // Turn off passenger coach tail lights
            try
            {
                var coachLight = car.GetComponent("CoachLightController");
                if (coachLight != null)
                {
                    Type t = coachLight.GetType();
                    var rearMethod = t.GetMethod("ChangeRearLights", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    var frontMethod = t.GetMethod("ChangeFrontLights", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (rearMethod != null) rearMethod.Invoke(coachLight, new object[] { false });
                    if (frontMethod != null) frontMethod.Invoke(coachLight, new object[] { false });
                }
            }
            catch { }
        }

        private List<TrainCar> GetConsistCars()
        {
            if (Engineer != null && Engineer.RegisteredConsistCars != null && Engineer.RegisteredConsistCars.Count > 0)
            {
                return Engineer.RegisteredConsistCars;
            }

            if (Locomotive != null && Locomotive.trainset != null && Locomotive.trainset.cars != null && Locomotive.trainset.cars.Count > 0)
            {
                return Locomotive.trainset.cars;
            }

            return null;
        }

        private TrainCar GetConsistRearCar()
        {
            var cars = GetConsistCars();
            if (cars == null || cars.Count == 0) return Locomotive;
            if (cars.Count == 1) return cars[0];

            // In trainset.cars, the locomotive is typically at one end
            if (cars[0] == Locomotive)
            {
                return cars[cars.Count - 1];
            }
            if (cars[cars.Count - 1] == Locomotive)
            {
                return cars[0];
            }

            // Fallback: pick the car furthest from the lead locomotive
            TrainCar furthest = null;
            float maxDistSq = -1f;
            Vector3 locoPos = Locomotive.transform.position;

            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                if (c == null) continue;
                float d = (c.transform.position - locoPos).sqrMagnitude;
                if (d > maxDistSq)
                {
                    maxDistSq = d;
                    furthest = c;
                }
            }

            return furthest ?? Locomotive;
        }

        private static bool IsPassengerCar(TrainCar car)
        {
            if (car == null) return false;

            if (car.carType == TrainCarType.PassengerRed ||
                car.carType == TrainCarType.PassengerGreen ||
                car.carType == TrainCarType.PassengerBlue)
            {
                return true;
            }

            if (car.carLivery != null && !string.IsNullOrEmpty(car.carLivery.id))
            {
                string id = car.carLivery.id;
                if (id.IndexOf("Passenger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Coach", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    id.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            if (car.GetComponent("CoachLightController") != null)
            {
                return true;
            }

            return false;
        }

        #endregion

        /// <summary>
        /// Gracefully shuts down all headlights, cab lighting, and removes consist EOT markers.
        /// </summary>
        public void Shutdown()
        {
            _isInitialized = false;

            if (_controlsOverrider != null)
            {
                if (_controlsOverrider.HeadlightsFront != null) _controlsOverrider.HeadlightsFront.Set(GetOffControlValue(Locomotive, true));
                if (_controlsOverrider.HeadlightsRear != null) _controlsOverrider.HeadlightsRear.Set(GetOffControlValue(Locomotive, false));
                if (_controlsOverrider.CabLight != null) _controlsOverrider.CabLight.Set(0.0f);
                if (_controlsOverrider.IndCabLight != null) _controlsOverrider.IndCabLight.Set(0.0f);
            }

            if (_lastRearCar != null)
            {
                ConsistEOTMarker.RemoveFromCar(_lastRearCar);
                ExtinguishRearCarNativeLights(_lastRearCar);
            }

            var cars = GetConsistCars();
            if (cars != null)
            {
                for (int i = 0; i < cars.Count; i++)
                {
                    var c = cars[i];
                    if (c != null)
                    {
                        ConsistEOTMarker.RemoveFromCar(c);
                        ExtinguishRearCarNativeLights(c);
                    }
                }
            }
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
