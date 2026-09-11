using System;
using HarmonyLib;
using AITraffic.Workers;
using AITraffic.Navigation;
using Signals.Game.Controllers;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches that dynamically wake up station yard generation when player-employed AI workers
    /// or AI trains approach a destination station, and inhibit premature job/car deletion.
    /// Fully compatible with vanilla Derail Valley, PersistentJobsMod, and SelfShunt.
    /// </summary>
    public static class StationWakeUpPatches
    {
        [HarmonyPatch(typeof(StationJobGenerationRange), "IsPlayerInJobGenerationZone")]
        public static class IsPlayerInJobGenerationZone_Patch
        {
            public static void Postfix(StationJobGenerationRange __instance, ref bool __result)
            {
                if (__result) return; // Player is already in range, nothing to override

                try
                {
                    if (StationWakeUpManager.Instance.ShouldWakeStation(__instance))
                    {
                        __result = true;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("Error in IsPlayerInJobGenerationZone_Patch: {0}", ex));
                }
            }
        }

        [HarmonyPatch(typeof(StationJobGenerationRange), "IsPlayerOutOfJobDestroyZone")]
        public static class IsPlayerOutOfJobDestroyZone_Patch
        {
            public static void Postfix(StationJobGenerationRange __instance, ref bool __result)
            {
                if (!__result) return; // Not out of zone, nothing to override

                try
                {
                    if (StationWakeUpManager.Instance.ShouldInhibitJobDestroy(__instance))
                    {
                        __result = false;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("Error in IsPlayerOutOfJobDestroyZone_Patch: {0}", ex));
                }
            }
        }

        /// <summary>
        /// Harmony patch ensuring DVSignals controllers stay awake and actively evaluate
        /// block occupancy and aspects when AI trains are within approach distance (1500m),
        /// even when the player is far away across the map.
        /// Eliminates dormant signals displaying false green aspects to opposing traffic.
        /// </summary>
        [HarmonyPatch(typeof(BasicSignalController), "ShouldUpdate")]
        public static class BasicSignalController_ShouldUpdate_Patch
        {
            [HarmonyPostfix]
            public static void Postfix(BasicSignalController __instance, ref bool __result)
            {
                if (__result) return; // Player camera is already within 1500m

                try
                {
                    if (SignalRegistry.IsSignalNearAnyAITrain(__instance, 1500f))
                    {
                        __result = true;
                    }
                }
                catch (Exception)
                {
                    // Guard against potential null refs during scene transitions
                }
            }
        }
    }
}
