using System;
using HarmonyLib;
using AITraffic.Navigation;

namespace AITraffic.Compat
{
    /// <summary>
    /// Harmony patches preventing player manual switching (lever or Comms Radio) on junctions
    /// currently locked and approached/traversed by active AI trains moving at speed.
    /// </summary>
    public static class PlayerSwitchLockPatches
    {
        // CRITICAL INVARIANT [JNC-04] [JNC-08]: Preserving Player Shunting Agency & Non-Mutating Main Signal Yield Preview (See GRAVEYARD.md)
        [HarmonyPatch(typeof(JunctionSwitcherManager), "IsSwitchingAllowed")]
        public static class JunctionSwitcherManager_IsSwitchingAllowed_Patch
        {
            public static void Postfix(Junction junction, ref bool __result)
            {
                if (!__result || junction == null) return;
                if (JunctionController.IsInternalAISwitchCall) return;

                if (JunctionController.Instance != null)
                {
                    // Strict protection against player switching under AI trains or non-yieldable AI route reservations:
                    if (JunctionController.IsJunctionPhysicallyOccupiedByAI(junction))
                    {
                        __result = false;
                    }
                    else if (JunctionController.Instance.IsJunctionLockedByAI(junction) &&
                             !JunctionController.Instance.CanAIYieldJunctionToPlayer(junction))
                    {
                        __result = false;
                    }
                }
            }
        }

        // CRITICAL INVARIANT [JNC-08]: Main Signal Yield Execution on Player Switch Actuation (See GRAVEYARD.md)
        [HarmonyPatch(typeof(Junction), "Switch", new Type[] { typeof(Junction.SwitchMode) })]
        public static class Junction_Switch_Mode_Patch
        {
            public static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            {
                if (__instance == null) return true;
                if (mode == Junction.SwitchMode.FORCED || JunctionController.IsInternalAISwitchCall) return true; // AI alignment and trailing spring-switch moves always allowed

                if (JunctionController.Instance != null)
                {
                    // Strict protection against player switching under AI trains:
                    if (JunctionController.IsJunctionPhysicallyOccupiedByAI(__instance))
                    {
                        return false;
                    }
                    if (JunctionController.Instance.IsJunctionLockedByAI(__instance) &&
                        !JunctionController.Instance.TryYieldJunctionToPlayer(__instance))
                    {
                        return false; // Deny regular player switch attempt when AI cannot safely yield at a main signal
                    }
                }

                return true;
            }
        }

        // CRITICAL INVARIANT [JNC-08]: Main Signal Yield Execution on Player Switch Branch Actuation (See GRAVEYARD.md)
        [HarmonyPatch(typeof(Junction), "Switch", new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
        public static class Junction_Switch_ModeBranch_Patch
        {
            public static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            {
                if (__instance == null) return true;
                if (mode == Junction.SwitchMode.FORCED || JunctionController.IsInternalAISwitchCall) return true; // AI alignment and trailing spring-switch moves always allowed

                if (JunctionController.Instance != null)
                {
                    // Strict protection against player switching under AI trains:
                    if (JunctionController.IsJunctionPhysicallyOccupiedByAI(__instance))
                    {
                        return false;
                    }
                    if (JunctionController.Instance.IsJunctionLockedByAI(__instance) &&
                        !JunctionController.Instance.TryYieldJunctionToPlayer(__instance))
                    {
                        return false; // Deny regular player switch attempt when AI cannot safely yield at a main signal
                    }
                }

                return true;
            }
        }
    }
}
