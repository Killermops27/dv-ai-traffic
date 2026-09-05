using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using DV.Damage;
using DV.ServicePenalty;
using AITraffic.Core;
using AITraffic.Driver;
using AITraffic.Fleet;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches and maintenance utilities completely exempting ambient AI locomotives
    /// and rolling stock from generating career debts, fuel/oil/sand consumption fees, wear-and-tear fees,
    /// or staged 'destroyed locomotive' penalties.
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
            public static bool Prefix(TrainCar car, LocoDebtTrackerBase locoDebtTracker)
            {
                try
                {
                    if (car != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(car)))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[AIDebt] Suppressed debt registration for ambient AI loco '{0}'.", car.ID));

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
                        if (existing.car != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(existing.car) || ModCompatManager.IsAmbientAITrainId(existing.car.ID)))
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
                        // Check if debtTrackerToStage belongs to an ambient AI loco ID
                        var debtData = locoDebtTrackerToStage.GetDebtData();
                        if (debtData != null && ModCompatManager.IsAmbientAITrainId(debtData.id))
                        {
                            return false;
                        }

                        // Untracked loco despawned: suppress vanilla InvalidOperationException
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
            public static bool Prefix(CarDebtController __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        var trainCar = __instance.GetComponent<TrainCar>();
                        if (trainCar != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(trainCar)))
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

        /// <summary>
        /// Prevents SimulatedCarDebtTracker from updating consumption values (fuel, oil, sand) or wear debt
        /// for ambient AI locomotives driving around the map.
        /// </summary>
        [HarmonyPatch(typeof(SimulatedCarDebtTracker), "UpdateDebtValues")]
        public static class SimulatedCarDebtTracker_UpdateDebtValues_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(SimulatedCarDebtTracker __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        var data = __instance.GetDebtData();
                        if (data != null && !string.IsNullOrEmpty(data.id))
                        {
                            if (ModCompatManager.IsAmbientAITrainId(data.id))
                            {
                                return false; // Suppress fuel/oil/sand/wear accumulation for ambient AI
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in SimulatedCarDebtTracker.UpdateDebtValues patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents ambient procedural AI cars from registering as jobless cars in JobDebtController.
        /// </summary>
        [HarmonyPatch(typeof(JobDebtController), "AddJoblessCarDebtTracker")]
        public static class JobDebtController_AddJoblessCarDebtTracker_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(DV.Logic.Job.Car car)
            {
                try
                {
                    if (car != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrainId(car.ID)))
                    {
                        return false; // Suppress tracking ambient AI rolling stock as jobless cars
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in AddJoblessCarDebtTracker patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents despawned or destroyed ambient AI cars from staging jobless car debt penalties.
        /// </summary>
        [HarmonyPatch(typeof(JobDebtController), "StageJoblessCarDebtOnCarDestroy")]
        public static class JobDebtController_StageJoblessCarDebtOnCarDestroy_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(DebtTrackerCar debtTrackerCar)
            {
                try
                {
                    if (debtTrackerCar != null)
                    {
                        var data = debtTrackerCar.GetDebtData();
                        if (data != null && ModCompatManager.IsAmbientAITrainId(data.id))
                        {
                            return false; // Suppress creating staged debt for ambient AI cars
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in StageJoblessCarDebtOnCarDestroy patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Prevents CareerManagerDebtController from ever accepting or registering any debt
        /// belonging to ambient procedural AI trains.
        /// </summary>
        [HarmonyPatch(typeof(CareerManagerDebtController), "RegisterDebt")]
        public static class CareerManagerDebtController_RegisterDebt_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(DisplayableDebt debt)
            {
                try
                {
                    if (debt != null && IsAmbientAIDebt(debt))
                    {
                        if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                            Main.ModEntry.Logger.Log(string.Format("[AIDebt] Blocked ambient AI debt registration in CareerManager: ID='{0}', Type={1}", debt.ID, debt.GetType().Name));

                        return false; // Strictly refuse to register ambient AI debt in player career ledger
                    }
                }
                catch (Exception ex)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in CareerManagerDebtController.RegisterDebt patch: {0}", ex));
                }

                return true;
            }
        }

        /// <summary>
        /// Runs before CareerManager recalculates fees and verifies job eligibility.
        /// Scrubs any ambient AI debts so they never count towards TotalFees or block taking jobs.
        /// </summary>
        [HarmonyPatch(typeof(CareerManagerDebtController), "RefreshExistingDebtsState")]
        public static class CareerManagerDebtController_RefreshExistingDebtsState_Patch
        {
            [HarmonyPrefix]
            public static void Prefix()
            {
                ScrubActiveAIDebts();
            }
        }

        #endregion

        #region Debt Identification & Scrubber

        /// <summary>
        /// Identifies whether any DisplayableDebt instance belongs to an ambient procedural AI locomotive or car.
        /// </summary>
        public static bool IsAmbientAIDebt(DisplayableDebt debt)
        {
            if (debt == null) return false;

            try
            {
                var locoDebt = debt as ExistingLocoDebt;
                if (locoDebt != null)
                {
                    if (locoDebt.car != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(locoDebt.car)))
                        return true;
                    if (!string.IsNullOrEmpty(locoDebt.ID) && ModCompatManager.IsAmbientAITrainId(locoDebt.ID))
                        return true;
                }

                var ownedDebt = debt as ExistingOwnedCarDebt;
                if (ownedDebt != null)
                {
                    if (ownedDebt.car != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(ownedDebt.car)))
                        return true;
                    if (!string.IsNullOrEmpty(ownedDebt.ID) && ModCompatManager.IsAmbientAITrainId(ownedDebt.ID))
                        return true;
                }

                var stagedLoco = debt as StagedLocoDebt;
                if (stagedLoco != null)
                {
                    if (stagedLoco.locoDebtData != null && ModCompatManager.IsAmbientAITrainId(stagedLoco.locoDebtData.id))
                        return true;
                }

                var stagedOwned = debt as StagedOwnedCarDebt;
                if (stagedOwned != null)
                {
                    if (stagedOwned.carDebtData != null && ModCompatManager.IsAmbientAITrainId(stagedOwned.carDebtData.id))
                        return true;
                }

                var otherDebt = debt as ExistingOtherDebt;
                if (otherDebt != null)
                {
                    if (otherDebt.joblessCarsTrackers != null)
                    {
                        for (int i = otherDebt.joblessCarsTrackers.Count - 1; i >= 0; i--)
                        {
                            var tracker = otherDebt.joblessCarsTrackers[i];
                            var data = tracker != null ? tracker.GetDebtData() : null;
                            if (data != null && ModCompatManager.IsAmbientAITrainId(data.id))
                            {
                                otherDebt.joblessCarsTrackers.RemoveAt(i);
                            }
                        }
                        return otherDebt.joblessCarsTrackers.Count == 0;
                    }
                }

                var stagedOther = debt as StagedOtherDebt;
                if (stagedOther != null)
                {
                    if (stagedOther.joblessCarsDebtData != null)
                    {
                        for (int i = stagedOther.joblessCarsDebtData.Count - 1; i >= 0; i--)
                        {
                            var data = stagedOther.joblessCarsDebtData[i];
                            if (data != null && ModCompatManager.IsAmbientAITrainId(data.id))
                            {
                                stagedOther.joblessCarsDebtData.RemoveAt(i);
                            }
                        }
                        return stagedOther.joblessCarsDebtData.Count == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error(string.Format("[AIDebt] Error in IsAmbientAIDebt: {0}", ex));
            }

            return false;
        }

        /// <summary>
        /// Inspects the live game's debt controllers and purges any ambient AI debts.
        /// Guaranteed safe: player locomotives, leased locos, and player-employed worker trains are NEVER touched.
        /// </summary>
        public static int ScrubActiveAIDebts()
        {
            int scrubbedCount = 0;
            try
            {
                // 1. Scrub LocoDebtController tracked locos
                if (LocoDebtController.Instance != null && LocoDebtController.Instance.trackedLocosDebts != null)
                {
                    var debts = LocoDebtController.Instance.trackedLocosDebts;
                    for (int i = debts.Count - 1; i >= 0; i--)
                    {
                        var debt = debts[i];
                        if (debt == null) continue;

                        TrainCar loco = debt.car;
                        if ((loco != null && (TrainSpawner.IsSpawningAmbientConsist || ModCompatManager.IsAmbientAITrain(loco))) ||
                            (!string.IsNullOrEmpty(debt.ID) && ModCompatManager.IsAmbientAITrainId(debt.ID)))
                        {
                            if (CareerManagerDebtController.Instance != null)
                            {
                                CareerManagerDebtController.Instance.UnregisterDebt(debt);
                            }
                            debts.RemoveAt(i);
                            scrubbedCount++;

                            if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                                Main.ModEntry.Logger.Log(string.Format("[AIDebt] Scrubbed active ambient AI debt for loco '{0}'.", loco != null ? loco.ID : debt.ID));
                        }
                    }
                }

                // 2. Scrub CareerManager current non-zero and zero priced debts
                if (CareerManagerDebtController.Instance != null)
                {
                    var traverse = Traverse.Create(CareerManagerDebtController.Instance);
                    var nonZero = traverse.Field<List<DisplayableDebt>>("currentNonZeroPricedDebts").Value;
                    if (nonZero != null)
                    {
                        for (int i = nonZero.Count - 1; i >= 0; i--)
                        {
                            var debt = nonZero[i];
                            if (debt != null && IsAmbientAIDebt(debt))
                            {
                                CareerManagerDebtController.Instance.UnregisterDebt(debt);
                                scrubbedCount++;
                            }
                        }
                    }

                    var zero = traverse.Field<List<DisplayableDebt>>("currentZeroPricedDebts").Value;
                    if (zero != null)
                    {
                        for (int i = zero.Count - 1; i >= 0; i--)
                        {
                            var debt = zero[i];
                            if (debt != null && IsAmbientAIDebt(debt))
                            {
                                CareerManagerDebtController.Instance.UnregisterDebt(debt);
                                scrubbedCount++;
                            }
                        }
                    }
                }

                // 3. Scrub JobDebtController existing jobless car debts
                if (JobDebtController.Instance != null && JobDebtController.Instance.existingJoblessCarDebts != null)
                {
                    var other = JobDebtController.Instance.existingJoblessCarDebts;
                    if (other.joblessCarsTrackers != null)
                    {
                        for (int i = other.joblessCarsTrackers.Count - 1; i >= 0; i--)
                        {
                            var tracker = other.joblessCarsTrackers[i];
                            var data = tracker != null ? tracker.GetDebtData() : null;
                            if (data != null && ModCompatManager.IsAmbientAITrainId(data.id))
                            {
                                other.joblessCarsTrackers.RemoveAt(i);
                                scrubbedCount++;
                            }
                        }
                    }
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
