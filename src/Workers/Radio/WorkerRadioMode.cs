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

        // CRITICAL INVARIANT [WRK-01]: Re-register CommsRadioMode whenever a new CommsRadioController awakens or scene resets (See GRAVEYARD.md)
        public static void OnControllerReady()
        {
            s_registered = false;
            Register();
        }

        public static void Reset()
        {
            s_registered = false;
        }

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
            catch (InvalidOperationException)
            {
                // CommsRadioController not ready yet; Ready event will trigger registration
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Warning(string.Format("[WorkerRadio] Failed to register CommsRadio mode: {0}", ex.Message));
                }
            }
        }
    }
}
