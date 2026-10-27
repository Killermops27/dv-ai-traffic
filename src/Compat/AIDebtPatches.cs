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
                    // CRITICAL INVARIANT [FLT-06]: Check IsSpawningAmbientConsist in addition to IsCarSpawning(car) because
                    // SimController.OnLogicCarInitialized fires synchronously inside CarSpawner.SpawnCar before RegisterSpawningCar.
                    if (car != null && (TrainSpawner.IsSpawningAmbientConsist || TrainSpawner.IsCarSpawning(car) || ModCompatManager.IsAmbientAITrain(car)))
                    {
                        if (Main.IsDebugLoggingEnabled)
                            Main.LogDebug(string.Format("[AIDebt] Suppressed debt registration for ambient AI loco '{0}'.", ModCompatManager.GetCarId(car)));

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
                        // CRITICAL INVARIANT [FLT-06]: TrainCar.PrepareForDestroy sets existing.car.logicCar = null before firing
                        // OnDestroyCar -> StageLocoDebtOnLocoDestroy. Always fall back to existing.ID and debtData.id when GetCarId is empty.
                        string existingCarId = existing.car != null ? ModCompatManager.GetCarId(existing.car) : string.Empty;
                        if (string.IsNullOrEmpty(existingCarId) && !string.IsNullOrEmpty(existing.ID))
                        {
                            existingCarId = existing.ID;
                        }
                        if (string.IsNullOrEmpty(existingCarId))
                        {
                            var trackerData = locoDebtTrackerToStage.GetDebtData();
                            if (trackerData != null && !string.IsNullOrEmpty(trackerData.id))
                            {
                                existingCarId = trackerData.id;
                            }
                        }

                        if (TrainSpawner.IsSpawningAmbientConsist ||
                            (existing.car != null && (TrainSpawner.IsCarSpawning(existing.car) || ModCompatManager.IsAmbientAITrain(existing.car))) ||
                            (!string.IsNullOrEmpty(existingCarId) && (TrainSpawner.IsCarSpawning(existingCarId) || ModCompatManager.IsAmbientAITrainId(existingCarId))))
                        {
                            LocoDebtController.Instance.trackedLocosDebts.Remove(existing);
                            SafeUnregisterDebt(existing);

                            if (Main.IsDebugLoggingEnabled)
                                Main.LogDebug(string.Format("[AIDebt] Suppressed staged destroyed debt on despawn of AI loco '{0}'.", existingCarId));

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
                        // CRITICAL INVARIANT [FLT-06]: Check IsSpawningAmbientConsist in addition to IsCarSpawning(trainCar) because
                        // TrainCar.InitializeLogicCarRelatedScript fires synchronously inside CarSpawner.SpawnCar before RegisterSpawningCar.
                        var trainCar = __instance.GetComponent<TrainCar>();
                        if (TrainSpawner.IsSpawningAmbientConsist || (trainCar != null && (TrainSpawner.IsCarSpawning(trainCar) || ModCompatManager.IsAmbientAITrain(trainCar))))
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
                        if (TrainSpawner.IsSpawningAmbientConsist)
                        {
                            return false;
                        }

                        var data = __instance.GetDebtData();
                        if (data != null && !string.IsNullOrEmpty(data.id))
                        {
                            if (TrainSpawner.IsCarSpawning(data.id) || ModCompatManager.IsAmbientAITrainId(data.id))
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
                    if (car != null && (TrainSpawner.IsSpawningAmbientConsist || TrainSpawner.IsCarSpawning(car.ID) || ModCompatManager.IsAmbientAITrainId(car.ID)))
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
                        if (data != null && (TrainSpawner.IsSpawningAmbientConsist || TrainSpawner.IsCarSpawning(data.id) || ModCompatManager.IsAmbientAITrainId(data.id)))
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
                        if (Main.IsDebugLoggingEnabled)
                            Main.LogDebug(string.Format("[AIDebt] Blocked ambient AI debt registration in CareerManager: ID='{0}', Type={1}", debt.ID, debt.GetType().Name));

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
                    // CRITICAL INVARIANT [FLT-12]: Live car => car-scoped checks only; ID fallback only when the car no longer exists.
                    if (locoDebt.car != null)
                    {
                        if (TrainSpawner.IsCarSpawning(locoDebt.car) || ModCompatManager.IsAmbientAITrain(locoDebt.car))
                            return true;
                    }
                    else if (!string.IsNullOrEmpty(locoDebt.ID) && (TrainSpawner.IsCarSpawning(locoDebt.ID) || ModCompatManager.IsAmbientAITrainId(locoDebt.ID)))
                        return true;
                }

                var ownedDebt = debt as ExistingOwnedCarDebt;
                if (ownedDebt != null)
                {
                    // CRITICAL INVARIANT [FLT-12]: Live car => car-scoped checks only; ID fallback only when the car no longer exists.
                    if (ownedDebt.car != null)
                    {
                        if (TrainSpawner.IsCarSpawning(ownedDebt.car) || ModCompatManager.IsAmbientAITrain(ownedDebt.car))
                            return true;
                    }
                    else if (!string.IsNullOrEmpty(ownedDebt.ID) && (TrainSpawner.IsCarSpawning(ownedDebt.ID) || ModCompatManager.IsAmbientAITrainId(ownedDebt.ID)))
                        return true;
                }

                // CRITICAL INVARIANT [FLT-12]: Staged debts (StagedLocoDebt / StagedOwnedCarDebt / StagedOtherDebt) are NEVER auto-hidden,
                // cleared, or purged. Ambient debts are prevented at the source ([FLT-06] registration/staging prefixes).
                // Supersedes the v0.2.8 [FLT-06] blanket staged purge (user decision 2026-10-24).

                var otherDebt = debt as ExistingOtherDebt;
                if (otherDebt != null)
                {
                    if (otherDebt.joblessCarsTrackers != null)
                    {
                        for (int i = otherDebt.joblessCarsTrackers.Count - 1; i >= 0; i--)
                        {
                            var tracker = otherDebt.joblessCarsTrackers[i];
                            var data = tracker != null ? tracker.GetDebtData() : null;
                            if (data != null && (TrainSpawner.IsCarSpawning(data.id) || ModCompatManager.IsAmbientAITrainId(data.id)))
                            {
                                otherDebt.joblessCarsTrackers.RemoveAt(i);
                            }
                        }
                        return otherDebt.joblessCarsTrackers.Count == 0;
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

        // CRITICAL INVARIANT [FLT-06]: Never call CareerManagerDebtController.Instance.UnregisterDebt(debt) unconditionally.
        // Vanilla UnregisterDebt logs Debug.LogError("Couldn't unregister debt, not found!") whenever the debt was blocked from
        // entering currentZeroPricedDebts / currentNonZeroPricedDebts. Always verify list membership via SafeUnregisterDebt first.
        public static void SafeUnregisterDebt(DisplayableDebt debt)
        {
            if (debt == null || CareerManagerDebtController.Instance == null) return;

            var cmdc = CareerManagerDebtController.Instance;
            var traverse = Traverse.Create(cmdc);
            var zero = traverse.Field<List<DisplayableDebt>>("currentZeroPricedDebts").Value;
            var nonZero = traverse.Field<List<DisplayableDebt>>("currentNonZeroPricedDebts").Value;
            bool inZero = zero != null && zero.Contains(debt);
            bool inNonZero = nonZero != null && nonZero.Contains(debt);
            if (inZero || inNonZero)
            {
                cmdc.UnregisterDebt(debt);
            }
        }

        /// <summary>
        /// Inspects the live game's debt controllers and purges any ambient AI debts and staged despawn debts.
        /// Guaranteed safe: active player locomotives, leased locos, active jobs, and player-employed worker trains are NEVER touched.
        /// </summary>
        public static int ScrubActiveAIDebts()
        {
            int scrubbedCount = 0;
            try
            {
                // 1. Scrub LocoDebtController tracked locos and staged destroyedLocosDebts
                if (LocoDebtController.Instance != null)
                {
                    if (LocoDebtController.Instance.trackedLocosDebts != null)
                    {
                        var debts = LocoDebtController.Instance.trackedLocosDebts;
                        for (int i = debts.Count - 1; i >= 0; i--)
                        {
                            var debt = debts[i];
                            if (debt == null) continue;

                            TrainCar loco = debt.car;
                            // CRITICAL INVARIANT [FLT-12]: ID-based classification only when the tracked car no longer exists.
                            if ((loco != null && (TrainSpawner.IsCarSpawning(loco) || ModCompatManager.IsAmbientAITrain(loco))) ||
                                (loco == null && !string.IsNullOrEmpty(debt.ID) && (TrainSpawner.IsCarSpawning(debt.ID) || ModCompatManager.IsAmbientAITrainId(debt.ID))))
                            {
                                SafeUnregisterDebt(debt);
                                debts.RemoveAt(i);
                                scrubbedCount++;

                                if (Main.IsDebugLoggingEnabled)
                                    Main.LogDebug(string.Format("[AIDebt] Scrubbed active ambient AI debt for loco '{0}'.", loco != null ? ModCompatManager.GetCarId(loco) : debt.ID));
                            }
                        }
                    }

                    // [FLT-12]: destroyedLocosDebts (StagedLocoDebt) are never auto-purged; see IsAmbientAIDebt.
                }

                // 2. Scrub JobDebtController existing jobless car debts and staged deletedJoblessCarDebts
                if (JobDebtController.Instance != null)
                {
                    if (JobDebtController.Instance.existingJoblessCarDebts != null)
                    {
                        var other = JobDebtController.Instance.existingJoblessCarDebts;
                        if (other.joblessCarsTrackers != null)
                        {
                            for (int i = other.joblessCarsTrackers.Count - 1; i >= 0; i--)
                            {
                                var tracker = other.joblessCarsTrackers[i];
                                var data = tracker != null ? tracker.GetDebtData() : null;
                                if (data != null && (TrainSpawner.IsCarSpawning(data.id) || ModCompatManager.IsAmbientAITrainId(data.id)))
                                {
                                    other.joblessCarsTrackers.RemoveAt(i);
                                    scrubbedCount++;
                                }
                            }
                            if (other.joblessCarsTrackers.Count == 0)
                            {
                                SafeUnregisterDebt(other);
                            }
                        }
                    }

                    // [FLT-12]: deletedJoblessCarDebts (StagedOtherDebt) are never auto-purged; see IsAmbientAIDebt.
                }

                // 3. Scrub CareerManager current non-zero and zero priced debts
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
                                SafeUnregisterDebt(debt);
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
                                SafeUnregisterDebt(debt);
                                scrubbedCount++;
                            }
                        }
                    }

                    if (scrubbedCount > 0)
                    {
                        CareerManagerDebtController.Instance.RefreshExistingDebtsState();
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

                    SafeUnregisterDebt(debt);
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
