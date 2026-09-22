using System;
using HarmonyLib;
using UnityEngine;
using DV.Damage;
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

