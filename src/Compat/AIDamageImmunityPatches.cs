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
    }
}

