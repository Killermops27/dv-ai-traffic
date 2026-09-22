using System;
using HarmonyLib;
using UnityEngine;
using AITraffic.Core;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches and utilities to eliminate the vanilla LocoZoneBlocker collider lag spikes
    /// and transform snapping warnings on AI-controlled locomotives moving at speed.
    /// </summary>
    public static class LocoZoneBlockerPatches
    {
        private static readonly Action<LocoZoneBlocker> s_unblockLocoDelegate;

        static LocoZoneBlockerPatches()
        {
            try
            {
                var method = AccessTools.Method(typeof(LocoZoneBlocker), "UnblockLoco");
                if (method != null)
                {
                    s_unblockLocoDelegate = (Action<LocoZoneBlocker>)Delegate.CreateDelegate(typeof(Action<LocoZoneBlocker>), method);
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("Failed to bind LocoZoneBlocker.UnblockLoco delegate: {0}", ex.Message));
            }
        }

        /// <summary>
        /// Safely invokes UnblockLoco on a LocoZoneBlocker instance, destroying blocker colliders
        /// and restoring cab accessibility.
        /// </summary>
        public static void Unblock(LocoZoneBlocker blocker)
        {
            if (blocker == null) return;

            try
            {
                if (s_unblockLocoDelegate != null)
                {
                    s_unblockLocoDelegate(blocker);
                }
                else
                {
                    var m = AccessTools.Method(typeof(LocoZoneBlocker), "UnblockLoco");
                    if (m != null)
                    {
                        m.Invoke(blocker, null);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(blocker.gameObject);
                    }
                }
            }
            catch
            {
                try
                {
                    if (blocker.gameObject != null)
                    {
                        UnityEngine.Object.Destroy(blocker.gameObject);
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// Intercepts LocoZoneBlocker.Update before it checks (diff.sqrMagnitude >= 0.04f).
        /// If the locomotive belongs to an AI train, unblocks and destroys the blocker immediately,
        /// completely preventing log spam, stack trace generation, and PhysX collider thrashing.
        /// For vanilla / player locomotives in yards, leaves behavior 100% normal.
        /// </summary>
        [HarmonyPatch(typeof(LocoZoneBlocker), "Update")]
        public static class LocoZoneBlocker_Update_Patch
        {
            public static bool Prefix(LocoZoneBlocker __instance, TrainCar ___train)
            {
                if (__instance == null) return true;

                TrainCar car = ___train ?? TrainCar.Resolve(__instance.gameObject);
                if (car != null && TrafficManager.IsAITrain(car))
                {
                    Unblock(__instance);
                    return false; // Skip original Update body
                }

                return true;
            }
        }
    }
}
