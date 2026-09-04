using System;
using UnityEngine;
using CommsRadioAPI;

namespace AITraffic.Workers.Radio
{
    /// <summary>
    /// Registers and manages the in-game 'AI WORKER' Comms Radio mode.
    /// </summary>
    public static class WorkerRadioMode
    {
        private static bool s_registered = false;

        /// <summary>
        /// Registers the AI Worker mode with CommsRadioAPI when the radio controller is ready.
        /// </summary>
        public static void Register()
        {
            if (s_registered) return;

            try
            {
                CommsRadioMode.Create(
                    startingState: new WorkerRadioScanState(),
                    laserColor: Color.cyan,
                    insertBefore: null
                );
                s_registered = true;

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log("[WorkerRadio] 'AI WORKER' mode registered successfully with CommsRadioAPI.");
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Error(string.Format("[WorkerRadio] Failed to register CommsRadio mode: {0}", ex));
                }
            }
        }
    }
}
