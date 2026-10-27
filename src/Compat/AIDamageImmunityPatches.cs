using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using DV.Damage;
using LocoSim.Implementations;
using LocoSim.Implementations.Wheels;
using AITraffic.Core;
using AITraffic.Driver;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches providing damage and explosion immunity for AI-controlled trains
    /// while leaving player locomotives and trains 100% realistic.
    /// </summary>
    public static class AIDamageImmunityPatches
    {
        private static readonly HashSet<SimComponent> _aiProtectedSimComponents = new HashSet<SimComponent>();
        private static readonly object _simLock = new object();

        /// <summary>
        /// Registers all LocoSim components belonging to an AI-operated locomotive so drivetrain
        /// failure simulation (breaker trips, traction motor burnouts) is suppressed while under AI control.
        /// </summary>
        public static void RegisterAISimComponents(TrainCar car)
        {
            if (car == null || car.SimController == null || car.SimController.SimulationFlow == null) return;
            var comps = car.SimController.SimulationFlow.OrderedSimComps;
            if (comps == null) return;

            lock (_simLock)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c != null)
                    {
                        _aiProtectedSimComponents.Add(c);
                    }
                }
            }
        }

        /// <summary>
        /// Unregisters all LocoSim components belonging to a locomotive when AI control ends
        /// (e.g. when a hired worker finishes a haul and hands the locomotive back to the player).
        /// </summary>
        public static void UnregisterAISimComponents(TrainCar car)
        {
            if (car == null || car.SimController == null || car.SimController.SimulationFlow == null) return;
            var comps = car.SimController.SimulationFlow.OrderedSimComps;
            if (comps == null) return;

            lock (_simLock)
            {
                for (int i = 0; i < comps.Count; i++)
                {
                    var c = comps[i];
                    if (c != null)
                    {
                        _aiProtectedSimComponents.Remove(c);
                    }
                }
            }
        }

        public static bool IsProtectedSimComponent(SimComponent comp)
        {
            if (comp == null) return false;
            lock (_simLock)
            {
                return _aiProtectedSimComponents.Contains(comp);
            }
        }

        /// <summary>
        /// Prevents TractionGenerator overcurrent from tripping the main/TM power breaker on AI-operated locomotives.
        /// </summary>
        [HarmonyPatch(typeof(TractionGenerator), "SimulateDamage", new Type[] { typeof(float) })]
        public static class TractionGenerator_SimulateDamage_Patch
        {
            public static bool Prefix(TractionGenerator __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings == null || Main.Settings.AIDamageImmunity)
                {
                    if (IsProtectedSimComponent(__instance))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents TractionMotorSet overheating/overspeed from tripping the TM power breaker or burning out traction motors on AI-operated locomotives.
        /// </summary>
        [HarmonyPatch(typeof(TractionMotorSet), "SimulateDamage", new Type[] { typeof(float) })]
        public static class TractionMotorSet_SimulateDamage_Patch
        {
            public static bool Prefix(TractionMotorSet __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings == null || Main.Settings.AIDamageImmunity)
                {
                    if (IsProtectedSimComponent(__instance))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents traction motor wheel blowouts on AI-operated locomotives.
        /// </summary>
        [HarmonyPatch(typeof(PoweredWheelsManager), "KillOnePoweredWheel")]
        public static class PoweredWheelsManager_KillOnePoweredWheel_Patch
        {
            public static bool Prefix(PoweredWheelsManager __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings == null || Main.Settings.AIDamageImmunity)
                {
                    var car = __instance.GetComponentInParent<TrainCar>();
                    if (car != null && TrafficManager.IsAITrain(car))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents full traction motor wheelset blowouts on AI-operated locomotives.
        /// </summary>
        [HarmonyPatch(typeof(PoweredWheelsManager), "KillAllPoweredWheels")]
        public static class PoweredWheelsManager_KillAllPoweredWheels_Patch
        {
            public static bool Prefix(PoweredWheelsManager __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings == null || Main.Settings.AIDamageImmunity)
                {
                    var car = __instance.GetComponentInParent<TrainCar>();
                    if (car != null && TrafficManager.IsAITrain(car))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents structural and collision damage to AI-controlled train cars.
        /// </summary>
        [HarmonyPatch(typeof(CarDamageModel), "DamageCar", new Type[] { typeof(float), typeof(bool) })]
        public static class CarDamageModel_DamageCar_Patch
        {
            public static bool Prefix(CarDamageModel __instance)
            {
                if (__instance == null || __instance.trainCar == null) return true;

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    if (TrafficManager.IsAITrain(__instance.trainCar))
                    {
                        return false; // Suppress damage for AI train
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents explosion destruction on AI-controlled rolling stock.
        /// </summary>
        [HarmonyPatch(typeof(TrainCarExplosion), "CreateExplosion")]
        public static class TrainCarExplosion_CreateExplosion_Patch
        {
            public static bool Prefix(TrainCarExplosion __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    var car = __instance.GetComponent<TrainCar>();
                    if (car != null && TrafficManager.IsAITrain(car))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[DamageImmunity] Suppressed explosion on AI train '{0}'.", car.ID));
                        return false; // Suppress explosion
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents stress buildup derailments on AI-controlled rolling stock.
        /// </summary>
        [HarmonyPatch(typeof(TrainStress), "Derail")]
        public static class TrainStress_Derail_Patch
        {
            public static bool Prefix(TrainStress __instance, string msg)
            {
                if (__instance == null) return true;

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    TrainCar car = __instance.GetComponent<TrainCar>();
                    if (car == null)
                    {
                        var bogie = __instance.GetComponent<Bogie>();
                        if (bogie != null) car = bogie.Car;
                    }
                    if (car == null)
                    {
                        car = __instance.GetComponentInParent<TrainCar>();
                    }
                    if (car == null)
                    {
                        car = Traverse.Create(__instance).Field("car").GetValue<TrainCar>();
                    }

                    if (car != null && TrafficManager.IsAITrain(car))
                    {
                        __instance.ResetTrainStress();
                        return false; // Suppress stress derailment
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents individual bogie derailment on AI-controlled rolling stock.
        /// </summary>
        [HarmonyPatch(typeof(Bogie), "Derail")]
        public static class Bogie_Derail_Patch
        {
            public static bool Prefix(Bogie __instance)
            {
                if (__instance == null || __instance.Car == null) return true;

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    if (TrafficManager.IsAITrain(__instance.Car))
                    {
                        return false; // Suppress bogie derailment
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents full car derailment on AI-controlled rolling stock.
        /// </summary>
        [HarmonyPatch(typeof(TrainCar), "DerailAllBogies")]
        public static class TrainCar_DerailAllBogies_Patch
        {
            public static bool Prefix(TrainCar __instance)
            {
                if (__instance == null) return true;

                if (Main.Settings != null && Main.Settings.AIDamageImmunity)
                {
                    if (TrafficManager.IsAITrain(__instance))
                    {
                        return false; // Suppress car derailment
                    }
                }

                return true;
            }
        }
    }
}

