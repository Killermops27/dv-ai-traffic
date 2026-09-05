using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using DV.Damage;
using DV.ServicePenalty;
using AITraffic.Core;
using AITraffic.Driver;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches and maintenance utilities preventing AI-controlled locomotives and rolling stock
    /// from generating career debts, fuel/oil/sand consumption fees, or staged 'destroyed locomotive' penalties.
    /// Protects the career manager fee office kiosk from lockups while keeping player rolling stock 100% realistic.
    /// </summary>
    public static class AIDebtPatches
    {
        #region Harmony Patches

        /// <summary>
        /// Prevents ambient procedural AI locomotives from registering into the player's career debt tracker.
        /// </summary>
        [HarmonyPatch(typeof(LocoDebtController), "RegisterLocoDebtTracker")]
        public static class LocoDebtController_RegisterLocoDebtTracker_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(TrainCar loco, LocoDebtTrackerBase locoDebtTracker)
            {
                try
                {
                    if (loco != null && ModCompatManager.IsAmbientAITrain(loco))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[AIDebt] Suppressed debt registration for ambient AI loco '{0}'.", loco.ID));

                        return false; // Do not track debt for ambient AI locomotives
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in RegisterLocoDebtTracker patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents despawning or deleted ambient AI locomotives from staging 'Destroyed Locomotive' penalties
        /// in the player's career ledger ($25,000 - $100,000+). Also prevents InvalidOperationExceptions
        /// if an untracked AI locomotive despawns.
        /// </summary>
        [HarmonyPatch(typeof(LocoDebtController), "StageLocoDebtOnLocoDestroy")]
        public static class LocoDebtController_StageLocoDebtOnLocoDestroy_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(LocoDebtTrackerBase locoDebtTrackerToStage)
            {
                try
                {
                    if (locoDebtTrackerToStage == null) return false;

                    ExistingLocoDebt existing = null;
                    if (LocoDebtController.Instance != null && LocoDebtController.Instance.trackedLocosDebts != null)
                    {
                        existing = LocoDebtController.Instance.trackedLocosDebts.Find(d => d != null && d.locoDebtTracker == locoDebtTrackerToStage);
                    }

                    if (existing != null)
                    {
                        if (existing.car != null && ModCompatManager.IsAmbientAITrain(existing.car))
                        {
                            LocoDebtController.Instance.trackedLocosDebts.Remove(existing);
                            if (CareerManagerDebtController.Instance != null)
                            {
                                CareerManagerDebtController.Instance.UnregisterDebt(existing);
                            }

                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                Main.ModEntry.Logger.Log(string.Format("[AIDebt] Suppressed staged destroyed debt on despawn of AI loco '{0}'.", existing.car.ID));

                            return false; // Suppress creating StagedLocoDebt
                        }

                        // Legitimate player locomotive destroyed
                        return true;
                    }
                    else
                    {
                        // Not found in trackedLocosDebts: this locomotive was untracked (e.g. ambient AI loco).
                        // Return false to prevent vanilla InvalidOperationException.
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in StageLocoDebtOnLocoDestroy patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Ensures ambient AI rolling stock attaches dummy debt trackers instead of charging the player for wear and tear.
        /// </summary>
        [HarmonyPatch(typeof(CarDebtController), "SetDebtTracker")]
        public static class CarDebtController_SetDebtTracker_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(CarDebtController __instance, CarDamageModel carDamage, CargoDamageModel cargoDamage)
            {
                try
                {
                    if (__instance != null)
                    {
                        var trainCar = __instance.GetComponent<TrainCar>();
                        if (trainCar != null && ModCompatManager.IsAmbientAITrain(trainCar))
                        {
                            __instance.SetDummyDebtTracker();
                            return false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in CarDebtController.SetDebtTracker patch: {0}", ex));
                }

                return true;
            }
        }

        #endregion

        #region Debt Scrubber & Recovery

        /// <summary>
        /// Inspects the live game's tracked locomotive debts and removes any active ambient AI locomotives.
        /// Guaranteed safe: only trains with living TrainCar instances tagged as ambient AI are scrubbed.
        /// Player locomotives, leased locos, and player-employed worker trains are NEVER touched.
        /// </summary>
        public static int ScrubActiveAIDebts()
        {
            int scrubbedCount = 0;
            try
            {
                if (LocoDebtController.Instance == null || LocoDebtController.Instance.trackedLocosDebts == null)
                    return 0;

                var debts = LocoDebtController.Instance.trackedLocosDebts;
                for (int i = debts.Count - 1; i >= 0; i--)
                {
                    var debt = debts[i];
                    if (debt == null) continue;

                    // Inspect living locomotive instance
                    TrainCar loco = debt.car;
                    if (loco != null && ModCompatManager.IsAmbientAITrain(loco))
                    {
                        if (CareerManagerDebtController.Instance != null)
                        {
                            CareerManagerDebtController.Instance.UnregisterDebt(debt);
                        }
                        debts.RemoveAt(i);
                        scrubbedCount++;

                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[AIDebt] Scrubbed active ambient AI debt for loco '{0}'.", loco.ID));
                    }
                }

                if (scrubbedCount > 0 && CareerManagerDebtController.Instance != null)
                {
                    CareerManagerDebtController.Instance.RefreshExistingDebtsState();
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error scrubbing active AI debts: {0}", ex));
            }

            return scrubbedCount;
        }

        /// <summary>
        /// Optional manual recovery tool available in UMM settings for players whose saves were affected
        /// by older mod versions before v0.2.1, clearing staged destroyed loco penalties.
        /// </summary>
        public static int PurgeHistoricalStagedDebts()
        {
            int purgedCount = 0;
            try
            {
                if (LocoDebtController.Instance == null || LocoDebtController.Instance.destroyedLocosDebts == null)
                    return 0;

                var staged = LocoDebtController.Instance.destroyedLocosDebts;
                for (int i = staged.Count - 1; i >= 0; i--)
                {
                    var debt = staged[i];
                    if (debt == null) continue;

                    if (CareerManagerDebtController.Instance != null)
                    {
                        CareerManagerDebtController.Instance.UnregisterDebt(debt);
                    }
                    staged.RemoveAt(i);
                    purgedCount++;
                }

                if (purgedCount > 0 && CareerManagerDebtController.Instance != null)
                {
                    CareerManagerDebtController.Instance.RefreshExistingDebtsState();
                }

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Log(string.Format("[AIDebt] Purged {0} historical staged destroyed loco debts.", purgedCount));
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error purging historical staged debts: {0}", ex));
            }

            return purgedCount;
        }

        #endregion
    }
}
