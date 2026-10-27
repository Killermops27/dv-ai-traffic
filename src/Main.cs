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
        public static int MainThreadId { get; private set; }

        private static readonly object s_logLock = new object();

        // CRITICAL INVARIANT [LOG-01]: Build-aware diagnostic logging gate & background-thread-safe logger ([RGR-03]).
        public static bool IsDebugLoggingEnabled
        {
            get
            {
                if (Settings != null) return Settings.VerboseLogging;
#if DEBUG
                return true;
#else
                return false;
#endif
            }
        }

        public static void LogDebug(string msg)
        {
            if (!IsDebugLoggingEnabled || string.IsNullOrEmpty(msg)) return;
            WriteLogInternal(msg, 0);
        }

        public static void Log(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            WriteLogInternal(msg, 0);
        }

        public static void LogWarning(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            WriteLogInternal(msg, 1);
        }

        public static void LogError(string msg)
        {
            if (string.IsNullOrEmpty(msg)) return;
            WriteLogInternal(msg, 2);
        }

        private static void WriteLogInternal(string msg, int level)
        {
            lock (s_logLock)
            {
                try
                {
                    // CRITICAL INVARIANT [RGR-03]: Background worker threads must fall back to UnityEngine.Debug.Log*.
                    bool isBackgroundThread = MainThreadId != 0 && System.Threading.Thread.CurrentThread.ManagedThreadId != MainThreadId;
                    if (!isBackgroundThread && ModEntry != null && ModEntry.Logger != null)
                    {
                        if (level == 2) ModEntry.Logger.Error(msg);
                        else if (level == 1) ModEntry.Logger.Warning(msg);
                        else ModEntry.Logger.Log(msg);
                        return;
                    }
                }
                catch { }

                try
                {
                    if (level == 2) UnityEngine.Debug.LogError(msg);
                    else if (level == 1) UnityEngine.Debug.LogWarning(msg);
                    else UnityEngine.Debug.Log(msg);
                }
                catch { }
            }
        }

        /// <summary>
        /// In Derail Valley, WorldStreamingInit.IsLoaded returns true when SingletonBehaviour<WorldStreamingInit>.Instance
        /// is null (which occurs at the Main Menu before the world has ever loaded!).
        /// IsWorldFullyLoaded guarantees that WorldStreamingInit is active AND finished loading.
        /// </summary>
        public static bool IsWorldFullyLoaded
        {
            get
            {
                return WorldStreamingInit.Instance != null && WorldStreamingInit.IsLoaded;
            }
        }

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            try
            {
                MainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
                ModEntry = modEntry;
                modEntry.OnToggle = OnToggle;

                // Load Settings
                Settings = UnityModManager.ModSettings.Load<AITrafficSettings>(modEntry);
                modEntry.OnGUI = entry => Settings.Draw(entry);
                modEntry.OnSaveGUI = entry => Settings.Save(entry);

                // Initialize Advanced INI Configuration (auto-generates Mods/AITraffic/settings.ini if missing)
                try
                {
                    IniConfigManager.Instance.Load();
                }
                catch (Exception ex)
                {
                    modEntry.Logger.Warning(string.Format("Failed to initialize IniConfigManager: {0}", ex));
                }

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
                    // CRITICAL INVARIANT [WRK-01]: Hook OnControllerReady so newly spawned CommsRadioController instances always receive the AI WORKER mode.
                    CommsRadioAPI.ControllerAPI.Ready += AITraffic.Workers.Radio.WorkerRadioMode.OnControllerReady;
                }
                catch (Exception ex)
                {
                    modEntry.Logger.Warning(string.Format("Failed to hook CommsRadioAPI.Ready: {0}", ex));
                }

                Enabled = true;

                // If world is already loaded (e.g. mod reload in-game), start manager immediately
                if (IsWorldFullyLoaded)
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
                if (!Enabled || !IsWorldFullyLoaded) return;

                if (ModEntry != null && ModEntry.Logger != null)
                    ModEntry.Logger.Log("World streaming finished. Starting AI Traffic Manager and Rail Graph...");

                // Initialize Rail Graph navigation network
                RailGraph.Instance.Initialize();
                TrafficScheduler.ClearCaches();

                // Start Traffic Manager
                TrafficManager.Instance.Settings = Settings;
                TrafficManager.Instance.enabled = true;

                // Purge any stale ambient AI cars lingering from previous sessions or legacy save files
                try
                {
                    TrafficManager.Instance.PurgeAllWorldAICars();
                }
                catch { }

                // Ensure Worker Radio mode is registered with Comms Radio
                try
                {
                    AITraffic.Workers.Radio.WorkerRadioMode.Register();
                }
                catch { }

                // Scrub any active ambient AI debts from career debt tracker
                try
                {
                    AIDebtPatches.ScrubActiveAIDebts();
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
                    if (IsWorldFullyLoaded)
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
