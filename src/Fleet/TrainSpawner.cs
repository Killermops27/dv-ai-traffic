using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DV.Simulation.Cars;
using DV.ThingTypes;
using AITraffic.Compat;
using AITraffic.Driver;

namespace AITraffic.Fleet
{
    /// <summary>
    /// Handles procedural instantiation, coupling, mechanical configuration,
    /// engine startup, and AI engineer attachment for AI train consists.
    /// </summary>
    public static class TrainSpawner
    {
        /// <summary>
        /// True while an ambient AI consist is being instantiated by CarSpawner.
        /// Checked by debt and penalty patches to suppress registration during spawning.
        /// </summary>
        public static bool IsSpawningAmbientConsist { get; private set; }

        /// <summary>
        /// Spawns an AI train consist of the specified type on the given track, matching origin and destination industrial chains.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="consistType">The type of consist to spawn.</param>
        /// <param name="originYard">Optional origin station yard ID.</param>
        /// <param name="destYard">Optional destination station yard ID.</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <param name="rng">Optional random number generator.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(
            RailTrack track,
            ConsistType consistType,
            string originYard = null,
            string destYard = null,
            double startSpan = 15.0,
            bool flipTrainConsist = false,
            System.Random rng = null)
        {
            if (track == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Target track is null.");
                return null;
            }

            List<ConsistCarSpec> specs = ConsistDefinitions.GetConsistSpecs(consistType, originYard, destYard, rng);
            if (specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("TrainSpawner.SpawnAITrain failed: No specs generated for consist type {0}.", consistType));
                return null;
            }

            return SpawnAITrain(track, specs, startSpan, flipTrainConsist);
        }

        /// <summary>
        /// Spawns an AI train consist of the specified type on the given track (overload for default origin/destination).
        /// </summary>
        public static AIEngineer SpawnAITrain(RailTrack track, ConsistType consistType, double startSpan = 15.0, bool flipTrainConsist = false, System.Random rng = null)
        {
            return SpawnAITrain(track, consistType, null, null, startSpan, flipTrainConsist, rng);
        }

        /// <summary>
        /// Spawns an AI train consist with the specified car specifications and loads matching cargo on rolling stock.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="specs">The ordered list of car specifications (starting with lead locomotive).</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(RailTrack track, List<ConsistCarSpec> specs, double startSpan = 15.0, bool flipTrainConsist = false)
        {
            if (track == null || specs == null || specs.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Invalid arguments.");
                return null;
            }

            List<TrainCarLivery> liveries = new List<TrainCarLivery>(specs.Count);
            for (int i = 0; i < specs.Count; i++)
            {
                if (specs[i].Livery != null)
                {
                    liveries.Add(specs[i].Livery);
                }
            }

            return SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
        }

        /// <summary>
        /// Spawns an AI train consist with the specified liveries on the given track.
        /// </summary>
        /// <param name="track">The rail track to spawn on.</param>
        /// <param name="liveries">The ordered list of car liveries (starting with lead locomotive).</param>
        /// <param name="startSpan">Starting distance offset along the track (meters).</param>
        /// <param name="flipTrainConsist">Whether to reverse consist orientation.</param>
        /// <returns>The attached AIEngineer controller on the lead locomotive, or null if spawn failed.</returns>
        public static AIEngineer SpawnAITrain(RailTrack track, List<TrainCarLivery> liveries, double startSpan = 15.0, bool flipTrainConsist = false)
        {
            if (track == null || liveries == null || liveries.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Invalid arguments.");
                return null;
            }

            List<ConsistCarSpec> specs = new List<ConsistCarSpec>(liveries.Count);
            for (int i = 0; i < liveries.Count; i++)
            {
                specs.Add(new ConsistCarSpec(liveries[i], CargoType.None));
            }

            return SpawnAITrainInternal(track, liveries, specs, startSpan, flipTrainConsist);
        }

        private static AIEngineer SpawnAITrainInternal(
            RailTrack track,
            List<TrainCarLivery> liveries,
            List<ConsistCarSpec> specs,
            double startSpan,
            bool flipTrainConsist)
        {
            if (track == null || liveries == null || liveries.Count == 0)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: Invalid arguments.");
                return null;
            }

            if (CarSpawner.Instance == null)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("TrainSpawner.SpawnAITrain failed: CarSpawner instance not available.");
                return null;
            }

            try
            {
                // Validate consist and track lengths for forward vs reverse span
                float totalConsistLength = CarSpawner.Instance.GetTotalCarLiveriesLength(liveries);
                float trackLength = track.curve != null ? track.curve.length : 0f;

                if (trackLength > 0f)
                {
                    if (!flipTrainConsist)
                    {
                        // Forward travel (0 -> L): start near 0, extend towards L
                        if (startSpan + totalConsistLength > trackLength)
                        {
                            if (totalConsistLength + 10f <= trackLength)
                            {
                                startSpan = Math.Max(5.0, (trackLength - totalConsistLength) * 0.5);
                            }
                        }
                    }
                    else
                    {
                        // Reverse travel (L -> 0): start near L, extend towards 0
                        if (startSpan < totalConsistLength + 5.0 || startSpan > trackLength)
                        {
                            startSpan = Math.Max(totalConsistLength + 5.0, trackLength - 15.0);
                        }
                    }
                }

                // Build orientation list matching flipTrainConsist so locomotives and cars physically face the travel direction
                List<bool> orientationList = new List<bool>(liveries.Count);
                for (int i = 0; i < liveries.Count; i++)
                {
                    orientationList.Add(flipTrainConsist);
                }

                // 1. Spawn cars on track with playerSpawnedCars = true so SimController natively skips debt tracking
                List<TrainCar> spawnedCars;
                try
                {
                    IsSpawningAmbientConsist = true;
                    spawnedCars = CarSpawner.Instance.SpawnCarTypesOnTrack(
                        trainCarTypes: liveries,
                        carsOrientationReversed: orientationList,
                        railTrack: track,
                        preventAutoCoupleOnLastCars: false,
                        applyHandbrakeOnLastCars: false,
                        startSpan: startSpan,
                        flipTrainConsist: flipTrainConsist,
                        playerSpawnedCars: true
                    );
                }
                finally
                {
                    IsSpawningAmbientConsist = false;
                }

                if (spawnedCars == null || spawnedCars.Count == 0)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("TrainSpawner: CarSpawner returned null or empty car list on track '{0}'.", track.name));
                    return null;
                }

                // 2. Locate lead locomotive (first supported diesel/mechanical/electric locomotive in consist)
                TrainCar leadLoco = null;
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car != null && IsSupportedAILocomotive(car))
                    {
                        leadLoco = car;
                        break;
                    }
                }

                if (leadLoco == null)
                {
                    leadLoco = spawnedCars[0];
                }

                // 3. Tag trainset immediately for AI save segregation, mod compatibility, and debt suppression
                if (leadLoco.trainset != null)
                {
                    ModCompatManager.TagTrainAsAITraffic(leadLoco.trainset);
                }

                // 4. Connect couplers, air hoses, open cocks, tighten chains
                ConfigureConsistCouplers(spawnedCars);

                // 5. Release handbrakes, apply damage immunity, and guarantee debt immunity across all cars
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car == null) continue;

                    car.playerSpawnedCar = true;
                    car.preventDebtDisplay = true;

                    // Attach dummy debt tracker to eliminate any car/cargo damage debt
                    var cdc = car.GetComponent<DV.ServicePenalty.CarDebtController>();
                    if (cdc != null)
                    {
                        cdc.SetDummyDebtTracker();
                    }

                    // Purge any accidental registration in LocoDebtController
                    if (car.IsLoco && DV.ServicePenalty.LocoDebtController.Instance != null && DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts != null)
                    {
                        var existingDebt = DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Find(d => d != null && d.car == car);
                        if (existingDebt != null)
                        {
                            DV.ServicePenalty.LocoDebtController.Instance.trackedLocosDebts.Remove(existingDebt);
                            if (DV.ServicePenalty.CareerManagerDebtController.Instance != null)
                            {
                                DV.ServicePenalty.CareerManagerDebtController.Instance.UnregisterDebt(existingDebt);
                            }
                        }
                    }

                    if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                    {
                        ApplyAIDamageImmunity(car, true);
                    }

                    // Release handbrake
                    var controls = car.GetComponent<BaseControlsOverrider>();
                    if (controls != null && controls.Handbrake != null)
                    {
                        controls.Handbrake.Set(0f);
                    }
                }

                // 6. Initialize locomotives (start engine, headlights, reverser)
                for (int i = 0; i < spawnedCars.Count; i++)
                {
                    var car = spawnedCars[i];
                    if (car != null && car.IsLoco)
                    {
                        InitializeLocomotive(car);
                    }
                }

                // 7. Attach AIEngineer component to lead locomotive
                AIEngineer engineer = leadLoco.gameObject.GetComponent<AIEngineer>();
                if (engineer == null)
                {
                    engineer = leadLoco.gameObject.AddComponent<AIEngineer>();
                }

                // 8. Time-slice procedural cargo loading across frames to eliminate spawn stutter while maintaining 100% visual/weight fidelity
                if (engineer != null && specs != null && specs.Count > 0)
                {
                    engineer.StartCoroutine(PopulateConsistCargoAsync(spawnedCars, specs));
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[TrainSpawner] Successfully spawned AI train ({0} cars, Lead Loco: {1}) on track '{2}'.",
                        spawnedCars.Count, leadLoco.ID, track.name));
                }

                return engineer;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("Error in TrainSpawner.SpawnAITrain on track '{0}': {1}", track.name, ex));
                return null;
            }
        }

        private static System.Collections.IEnumerator PopulateConsistCargoAsync(List<TrainCar> spawnedCars, List<ConsistCarSpec> specs)
        {
            if (spawnedCars == null || specs == null) yield break;

            // Yield initial frame so car rigidbodies and bogies settle cleanly
            yield return null;

            int loadedCarCount = 0;
            string sampleCargoName = "None";

            for (int i = 0; i < spawnedCars.Count && i < specs.Count; i++)
            {
                var car = spawnedCars[i];
                if (car == null || car.IsLoco) continue;

                CargoType cargoToLoad = specs[i].Cargo;
                if (cargoToLoad != CargoType.None && car.logicCar != null)
                {
                    try
                    {
                        float amount = car.logicCar.capacity > 0f ? car.logicCar.capacity : 1f;
                        car.logicCar.LoadCargo(amount, cargoToLoad, null);
                        loadedCarCount++;
                        sampleCargoName = cargoToLoad.ToString();
                    }
                    catch (Exception ex)
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Warning(string.Format("Failed loading cargo '{0}' onto car '{1}': {2}", cargoToLoad, car.ID, ex.Message));
                    }

                    // Stagger: yield every 2 cars to distribute 3D mesh instantiation and texture uploads across frames seamlessly
                    if (loadedCarCount % 2 == 0)
                    {
                        yield return null;
                    }
                }
            }

            if (Main.ModEntry != null && Main.ModEntry.Logger != null && loadedCarCount > 0)
            {
                Main.ModEntry.Logger.Log(string.Format("[TrainSpawner] Completed cargo loading for {0} cars ({1}).", loadedCarCount, sampleCargoName));
            }
        }

        #region Mechanical & Coupler Setup

        /// <summary>
        /// Automatically connects couplers, attaches air hoses, opens angle cocks,
        /// tightens screw chains between adjacent cars, and closes outer end cocks.
        /// </summary>
        public static void ConfigureConsistCouplers(List<TrainCar> cars)
        {
            if (cars == null || cars.Count == 0) return;

            // Connect adjacent cars in sequence
            for (int i = 0; i < cars.Count - 1; i++)
            {
                CoupleAdjacentCars(cars[i], cars[i + 1]);
            }

            // Ensure outer end-most couplers have closed angle cocks so brake pipe holds air
            for (int i = 0; i < cars.Count; i++)
            {
                var car = cars[i];
                if (car == null) continue;

                if (car.frontCoupler != null && !car.frontCoupler.IsCoupled())
                {
                    car.frontCoupler.IsCockOpen = false;
                }

                if (car.rearCoupler != null && !car.rearCoupler.IsCoupled())
                {
                    car.rearCoupler.IsCockOpen = false;
                }
            }
        }

        private static void CoupleAdjacentCars(TrainCar carA, TrainCar carB)
        {
            if (carA == null || carB == null) return;

            Coupler[] couplersA = new Coupler[] { carA.frontCoupler, carA.rearCoupler };
            Coupler[] couplersB = new Coupler[] { carB.frontCoupler, carB.rearCoupler };

            Coupler bestA = null;
            Coupler bestB = null;
            float bestDistSq = float.MaxValue;

            for (int i = 0; i < couplersA.Length; i++)
            {
                var cA = couplersA[i];
                if (cA == null) continue;

                for (int j = 0; j < couplersB.Length; j++)
                {
                    var cB = couplersB[j];
                    if (cB == null) continue;

                    float distSq = (cA.transform.position - cB.transform.position).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestA = cA;
                        bestB = cB;
                    }
                }
            }

            if (bestA != null && bestB != null && bestDistSq < 25.0f) // 5m squared
            {
                try
                {
                    if (!bestA.IsCoupled() || bestA.GetCoupled() != bestB)
                    {
                        bestA.CoupleTo(bestB, playAudio: false, viaChainInteraction: false);
                    }

                    if (bestA.GetAirHoseConnectedTo() == null)
                    {
                        bestA.ConnectAirHose(bestB, playAudio: false);
                    }

                    bestA.IsCockOpen = true;
                    bestB.IsCockOpen = true;
                    bestA.SetChainTight(true);
                    bestB.SetChainTight(true);
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("Warning coupling cars '{0}' and '{1}': {2}", carA.ID, carB.ID, ex.Message));
                }
            }
        }

        public static void InitializeLocomotive(TrainCar loco)
        {
            if (loco == null) return;

            try
            {
                // Official Derail Valley locomotive startup (closes fuses, primes fuel, cranks engine)
                try
                {
                    DV.Simulation.Controllers.StartupHelper.Startup(loco);
                }
                catch (Exception stEx)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("StartupHelper for loco '{0}' warning: {1}", loco.ID, stEx.Message));
                }

                var controls = loco.GetComponent<BaseControlsOverrider>();
                if (controls == null && loco.SimController != null)
                {
                    controls = loco.SimController.controlsOverrider;
                }

                if (controls != null)
                {
                    if (controls.Handbrake != null) controls.Handbrake.Set(0f);
                    if (controls.Brake != null) controls.Brake.Set(0f);
                    if (controls.IndependentBrake != null) controls.IndependentBrake.Set(0f);
                    if (controls.DynamicBrake != null) controls.DynamicBrake.Set(0f);
                    if (controls.Reverser != null) controls.Reverser.Set(1f);
                    if (controls.HeadlightsFront != null) controls.HeadlightsFront.Set(2f);
                    if (controls.BrakeCutout != null) controls.BrakeCutout.Set(1f);

                    // Ensure engine starter was primed if not already on
                    if (controls.EngineOnReader != null && !controls.EngineOnReader.IsOn && controls.Starter != null)
                    {
                        controls.Starter.Set(1f);
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Warning initializing locomotive '{0}': {1}", loco.ID, ex.Message));
            }
        }

        /// <summary>
        /// Checks if a locomotive is fully supported by the AI driver (excludes steam locos: S060, S282).
        /// </summary>
        public static bool IsSupportedAILocomotive(TrainCar car)
        {
            if (car == null || !car.IsLoco) return false;
            string id = car.carLivery != null ? car.carLivery.id : "";
            if (id.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("S060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("S282", StringComparison.OrdinalIgnoreCase) >= 0 ||
                id.IndexOf("Tender", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Applies or clears damage immunity across an AI train car.
        /// </summary>
        public static void ApplyAIDamageImmunity(TrainCar car, bool immune = true)
        {
            if (car == null) return;

            try
            {
                // 1. Car body & collision damage
                if (car.CarDamage != null)
                {
                    car.CarDamage.IgnoreDamage(immune);
                }

                // 2. Powertrain, wheel, mechanical and electrical damage
                var dc = car.GetComponent<DV.Damage.DamageController>();
                if (dc != null)
                {
                    dc.IgnoreDamage(immune);
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Error setting AI damage immunity on '{0}': {1}", car.ID, ex.Message));
            }
        }

        #endregion
    }
}
