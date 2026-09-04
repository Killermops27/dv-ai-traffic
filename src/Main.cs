using System;
using System.Reflection;
using HarmonyLib;
using UnityModManagerNet;
using AITraffic.Config;
using AITraffic.Compat;
using AITraffic.Core;
using AITraffic.Navigation;

namespace AITraffic
{
    public static class Main
    {
        public static UnityModManager.ModEntry ModEntry { get; private set; }
        public static AITrafficSettings Settings { get; private set; }
        public static bool Enabled { get; private set; }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                ModEntry = modEntry;
                modEntry.OnToggle = OnToggle;

                // Load Settings
                Settings = UnityModManager.ModSettings.Load<AITrafficSettings>(modEntry);
                modEntry.OnGUI = entry => Settings.Draw(entry);
                modEntry.OnSaveGUI = entry => Settings.Save(entry);

                // Initialize Compatibility Layer
                ModCompatManager.Initialize();

                // Apply Harmony Patches
                var harmony = new Harmony(modEntry.Info.Id);
                harmony.PatchAll(Assembly.GetExecutingAssembly());

                // Subscribe to World Loading event
                WorldStreamingInit.LoadingFinished += OnWorldLoaded;

                // Register Comms Radio mode via CommsRadioAPI
                try
                {
                    CommsRadioAPI.ControllerAPI.Ready += AITraffic.Workers.Radio.WorkerRadioMode.Register;
                }
                catch (Exception ex)
                {
                    modEntry.Logger.Warning(string.Format("Failed to hook CommsRadioAPI.Ready: {0}", ex));
                }

                Enabled = true;

                // If world is already loaded, start manager immediately
                if (WorldStreamingInit.IsLoaded)
                {
                    OnWorldLoaded();
                }

                ModEntry.Logger.Log("AI Traffic Mod initialized successfully.");
                return true;
            }
            catch (Exception ex)
            {
                if (modEntry != null && modEntry.Logger != null)
                    modEntry.Logger.Error(string.Format("Failed to initialize AI Traffic Mod: {0}", ex));
                return false;
            }
        }

        private static void OnWorldLoaded()
        {
            try
            {
                if (!Enabled) return;

                if (ModEntry != null && ModEntry.Logger != null)
                    ModEntry.Logger.Log("World streaming finished. Starting AI Traffic Manager and Rail Graph...");

                // Initialize Rail Graph navigation network
                RailGraph.Instance.Initialize();

                // Start Traffic Manager
                TrafficManager.Instance.Settings = Settings;
                TrafficManager.Instance.enabled = true;

                // Ensure Worker Radio mode is registered with Comms Radio
                try
                {
                    AITraffic.Workers.Radio.WorkerRadioMode.Register();
                }
                catch { }

                if (ModEntry != null && ModEntry.Logger != null)
                    ModEntry.Logger.Log("AI Traffic Manager started.");
            }
            catch (Exception ex)
            {
                if (ModEntry != null && ModEntry.Logger != null)
                    ModEntry.Logger.Error(string.Format("Error in AI Traffic OnWorldLoaded: {0}", ex));
            }
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            Enabled = value;

            try
            {
                if (value)
                {
                    if (WorldStreamingInit.IsLoaded)
                    {
                        RailGraph.Instance.Initialize();
                        TrafficManager.Instance.Settings = Settings;
                        TrafficManager.Instance.enabled = true;
                    }
                }
                else
                {
                    if (TrafficManager.Instance != null)
                    {
                        TrafficManager.Instance.DespawnAllAITrains();
                        TrafficManager.Instance.enabled = false;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ModEntry != null && ModEntry.Logger != null)
                    ModEntry.Logger.Error(string.Format("Error toggling AI Traffic mod: {0}", ex));
            }

            return true;
        }
    }
}
