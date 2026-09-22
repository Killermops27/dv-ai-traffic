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
        [HarmonyPatch(typeof(JunctionSwitcherManager), "IsSwitchingAllowed")]
        public static class JunctionSwitcherManager_IsSwitchingAllowed_Patch
        {
            public static void Postfix(Junction junction, ref bool __result)
            {
                if (!__result || junction == null) return;

                if (JunctionController.Instance != null && JunctionController.Instance.IsJunctionLockedByAI(junction))
                {
                    __result = false;
                }
            }
        }

        [HarmonyPatch(typeof(Junction), "Switch", new Type[] { typeof(Junction.SwitchMode) })]
        public static class Junction_Switch_Mode_Patch
        {
            public static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            {
                if (__instance == null) return true;
                if (mode == Junction.SwitchMode.FORCED) return true; // AI alignment and trailing spring-switch moves always allowed

                if (JunctionController.Instance != null && JunctionController.Instance.IsJunctionLockedByAI(__instance))
                {
                    return false; // Deny regular player switch attempt
                }

                return true;
            }
        }

        [HarmonyPatch(typeof(Junction), "Switch", new Type[] { typeof(Junction.SwitchMode), typeof(byte) })]
        public static class Junction_Switch_ModeBranch_Patch
        {
            public static bool Prefix(Junction __instance, Junction.SwitchMode mode)
            {
                if (__instance == null) return true;
                if (mode == Junction.SwitchMode.FORCED) return true; // AI alignment and trailing spring-switch moves always allowed

                if (JunctionController.Instance != null && JunctionController.Instance.IsJunctionLockedByAI(__instance))
                {
                    return false; // Deny regular player switch attempt
                }

                return true;
            }
        }
    }
}
