using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace AITraffic.Config
{
    /// <summary>
    /// Operating modes for AI locomotive headlights.
    /// </summary>
    public enum AILightingMode
    {
        Automatic,
        AlwaysHigh,
        AlwaysLow,
        Off
    }

    /// <summary>
    /// Configuration settings for locomotive physics and control loops.
    /// </summary>
    public class PhysicsConfig
    {
        public float ThrottleKp = 0.08f;
        public float ThrottleKi = 0.02f;
        public float ThrottleKd = 0.04f;

        public float BrakeKp = 0.14f;
        public float BrakeKi = 0.02f;
        public float BrakeKd = 0.05f;

        public float DecelerationMultiplier = 1.0f;
        public float WetDecelerationMultiplier = 0.60f;
        public bool EnableWheelSlideProtection = true;
        public float MaxWetServiceBrakeDemand = 0.55f;
        public float SlipCutFactor = 0.6f;
        public float MaxGradeClimbSpeedKmh = 40.0f;
        public float MaxGradeDescendSpeedKmh = 30.0f;
        public float HillStartBrakeHoldSeconds = 3.0f;
    }

    /// <summary>
    /// Configuration settings for track dispatching, safety zones, and station activation.
    /// </summary>
    public class DispatcherConfig
    {
        public float DeadlockTimeoutSeconds = 300.0f;
        public float SwitchBladeZoneMeters = 7.0f;
        public float SidingClearanceBufferMeters = 25.0f;
        public float StationWakeUpRadius = 1200.0f;
        public float StationOuterRadius = 1600.0f;
    }

    /// <summary>
    /// Configuration settings for AI worker hiring fees and billing rates.
    /// </summary>
    public class EconomyConfig
    {
        public double DriverBaseFee = 300.0;
        public double PerMeterRate = 0.06;
        public double PerTonRate = 0.35;
    }

    /// <summary>
    /// Configuration settings for train consist generation and locomotive selection weights.
    /// </summary>
    public class ConsistsConfig
    {
        public int MinCars = 3;
        public int MaxCars = 20;

        public float WeightDE2 = 1.0f;
        public float WeightDH4 = 1.0f;
        public float WeightDE6 = 0.8f;
        public float WeightDM3 = 0.6f;
        public float WeightS060 = 0.4f;
        public float WeightS282 = 0.3f;
    }

    /// <summary>
    /// Configuration settings for consist lighting and End-of-Train (EOT) markers.
    /// </summary>
    public class LightingConfig
    {
        public AILightingMode Mode = AILightingMode.Automatic;
        public bool EOTMarker = true;
        public bool EOTMarkerFlashing = true;
        public float EOTFlashRateHz = 1.0f;
        public bool CabLightingAtNight = true;
    }

    /// <summary>
    /// INI configuration manager for Mods/AITraffic/settings.ini.
    /// Provides load, save, reload, and default auto-generation with extensive comments.
    /// </summary>
    public class IniConfigManager
    {
        private static IniConfigManager s_instance;
        public static IniConfigManager Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new IniConfigManager();
                    s_instance.Load();
                }
                return s_instance;
            }
        }

        public PhysicsConfig Physics { get; private set; }
        public DispatcherConfig Dispatcher { get; private set; }
        public EconomyConfig Economy { get; private set; }
        public ConsistsConfig Consists { get; private set; }
        public LightingConfig Lighting { get; private set; }

        private readonly Dictionary<string, Dictionary<string, string>> _rawSections =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();

        public IniConfigManager()
        {
            Physics = new PhysicsConfig();
            Dispatcher = new DispatcherConfig();
            Economy = new EconomyConfig();
            Consists = new ConsistsConfig();
            Lighting = new LightingConfig();
        }

        /// <summary>
        /// Resolves the absolute path to Mods/AITraffic/settings.ini.
        /// </summary>
        public static string GetIniFilePath()
        {
            try
            {
                if (Main.ModEntry != null && !string.IsNullOrEmpty(Main.ModEntry.Path))
                {
                    return Path.Combine(Main.ModEntry.Path, "settings.ini");
                }
            }
            catch { }

            try
            {
                string asmLocation = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(asmLocation))
                {
                    string dir = Path.GetDirectoryName(asmLocation);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        return Path.Combine(dir, "settings.ini");
                    }
                }
            }
            catch { }

            return Path.Combine(Application.dataPath, "../Mods/AITraffic/settings.ini");
        }

        /// <summary>
        /// Loads the settings from settings.ini. If missing, auto-generates the file with full comments.
        /// </summary>
        public void Load()
        {
            lock (_lock)
            {
                string filePath = GetIniFilePath();

                try
                {
                    if (!File.Exists(filePath))
                    {
                        LogInfo(string.Format("Configuration file not found at '{0}'. Generating default settings.ini...", filePath));
                        SaveDefaultsWithComments(filePath);
                        return;
                    }

                    _rawSections.Clear();
                    ParseIniFile(filePath);

                    // Map parsed values to strongly typed properties
                    MapPhysicsSection();
                    MapDispatcherSection();
                    MapEconomySection();
                    MapConsistsSection();
                    MapLightingSection();

                    LogInfo(string.Format("Successfully loaded configuration from '{0}'.", filePath));
                }
                catch (Exception ex)
                {
                    LogError(string.Format("Error loading INI config from '{0}': {1}", filePath, ex));
                }
            }
        }

        /// <summary>
        /// Saves the current configuration to settings.ini, preserving well-commented section structure.
        /// </summary>
        public void Save()
        {
            lock (_lock)
            {
                string filePath = GetIniFilePath();
                try
                {
                    SaveDefaultsWithComments(filePath);
                    LogInfo(string.Format("Configuration saved successfully to '{0}'.", filePath));
                }
                catch (Exception ex)
                {
                    LogError(string.Format("Error saving INI config to '{0}': {1}", filePath, ex));
                }
            }
        }

        /// <summary>
        /// Reloads settings from disk, applying overrides immediately.
        /// </summary>
        public void Reload()
        {
            lock (_lock)
            {
                LogInfo("Reloading settings.ini configuration...");
                Load();
            }
        }

        #region INI Parsing & Mapping

        private void ParseIniFile(string filePath)
        {
            string currentSection = string.Empty;
            string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);

            for (int i = 0; i < lines.Length; i++)
            {
                string rawLine = lines[i];
                if (string.IsNullOrEmpty(rawLine)) continue;

                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#"))
                {
                    continue; // Skip comments and empty lines
                }

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    if (!_rawSections.ContainsKey(currentSection))
                    {
                        _rawSections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    }
                    continue;
                }

                int eqIdx = line.IndexOf('=');
                if (eqIdx > 0)
                {
                    string key = line.Substring(0, eqIdx).Trim();
                    string val = line.Substring(eqIdx + 1).Trim();

                    // Strip inline comments if present
                    int commentIdx = val.IndexOfAny(new char[] { ';', '#' });
                    if (commentIdx >= 0)
                    {
                        val = val.Substring(0, commentIdx).Trim();
                    }

                    if (!string.IsNullOrEmpty(currentSection))
                    {
                        if (!_rawSections.ContainsKey(currentSection))
                        {
                            _rawSections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        }
                        _rawSections[currentSection][key] = val;
                    }
                }
            }
        }

        private void MapPhysicsSection()
        {
            const string sec = "Physics";
            Physics.ThrottleKp = GetFloat(sec, "ThrottleKp", Physics.ThrottleKp);
            Physics.ThrottleKi = GetFloat(sec, "ThrottleKi", Physics.ThrottleKi);
            Physics.ThrottleKd = GetFloat(sec, "ThrottleKd", Physics.ThrottleKd);

            Physics.BrakeKp = GetFloat(sec, "BrakeKp", Physics.BrakeKp);
            Physics.BrakeKi = GetFloat(sec, "BrakeKi", Physics.BrakeKi);
            Physics.BrakeKd = GetFloat(sec, "BrakeKd", Physics.BrakeKd);

            Physics.DecelerationMultiplier = GetFloat(sec, "DecelerationMultiplier", Physics.DecelerationMultiplier);
            Physics.WetDecelerationMultiplier = GetFloat(sec, "WetDecelerationMultiplier", Physics.WetDecelerationMultiplier);
            Physics.EnableWheelSlideProtection = GetBool(sec, "EnableWheelSlideProtection", Physics.EnableWheelSlideProtection);
            Physics.MaxWetServiceBrakeDemand = GetFloat(sec, "MaxWetServiceBrakeDemand", Physics.MaxWetServiceBrakeDemand);
            Physics.SlipCutFactor = GetFloat(sec, "SlipCutFactor", Physics.SlipCutFactor);
            Physics.MaxGradeClimbSpeedKmh = GetFloat(sec, "MaxGradeClimbSpeedKmh", Physics.MaxGradeClimbSpeedKmh);
            Physics.MaxGradeDescendSpeedKmh = GetFloat(sec, "MaxGradeDescendSpeedKmh", Physics.MaxGradeDescendSpeedKmh);
            Physics.HillStartBrakeHoldSeconds = GetFloat(sec, "HillStartBrakeHoldSeconds", Physics.HillStartBrakeHoldSeconds);
        }

        private void MapDispatcherSection()
        {
            const string sec = "Dispatcher";
            Dispatcher.DeadlockTimeoutSeconds = GetFloat(sec, "DeadlockTimeoutSeconds", Dispatcher.DeadlockTimeoutSeconds);
            Dispatcher.SwitchBladeZoneMeters = GetFloat(sec, "SwitchBladeZoneMeters", Dispatcher.SwitchBladeZoneMeters);
            Dispatcher.SidingClearanceBufferMeters = GetFloat(sec, "SidingClearanceBufferMeters", Dispatcher.SidingClearanceBufferMeters);
            Dispatcher.StationWakeUpRadius = GetFloat(sec, "StationWakeUpRadius", Dispatcher.StationWakeUpRadius);
            Dispatcher.StationOuterRadius = GetFloat(sec, "StationOuterRadius", Dispatcher.StationOuterRadius);
        }

        private void MapEconomySection()
        {
            const string sec = "Economy";
            Economy.DriverBaseFee = GetDouble(sec, "DriverBaseFee", Economy.DriverBaseFee);
            Economy.PerMeterRate = GetDouble(sec, "PerMeterRate", Economy.PerMeterRate);
            Economy.PerTonRate = GetDouble(sec, "PerTonRate", Economy.PerTonRate);
        }

        private void MapConsistsSection()
        {
            const string sec = "Consists";
            Consists.MinCars = GetInt(sec, "MinCars", Consists.MinCars);
            Consists.MaxCars = GetInt(sec, "MaxCars", Consists.MaxCars);

            Consists.WeightDE2 = GetFloat(sec, "WeightDE2", Consists.WeightDE2);
            Consists.WeightDH4 = GetFloat(sec, "WeightDH4", Consists.WeightDH4);
            Consists.WeightDE6 = GetFloat(sec, "WeightDE6", Consists.WeightDE6);
            Consists.WeightDM3 = GetFloat(sec, "WeightDM3", Consists.WeightDM3);
            Consists.WeightS060 = GetFloat(sec, "WeightS060", Consists.WeightS060);
            Consists.WeightS282 = GetFloat(sec, "WeightS282", Consists.WeightS282);
        }

        private void MapLightingSection()
        {
            const string sec = "Lighting";
            Lighting.Mode = GetEnum(sec, "Mode", Lighting.Mode);
            Lighting.EOTMarker = GetBool(sec, "EOTMarker", Lighting.EOTMarker);
            Lighting.EOTMarkerFlashing = GetBool(sec, "EOTMarkerFlashing", Lighting.EOTMarkerFlashing);
            Lighting.EOTFlashRateHz = GetFloat(sec, "EOTFlashRateHz", Lighting.EOTFlashRateHz);
            Lighting.CabLightingAtNight = GetBool(sec, "CabLightingAtNight", Lighting.CabLightingAtNight);
        }

        #endregion

        #region Type-Safe Value Getters

        public string GetString(string section, string key, string defaultValue = "")
        {
            Dictionary<string, string> keys;
            if (_rawSections.TryGetValue(section, out keys))
            {
                string val;
                if (keys.TryGetValue(key, out val))
                {
                    return val;
                }
            }
            return defaultValue;
        }

        public float GetFloat(string section, string key, float defaultValue)
        {
            string raw = GetString(section, key);
            float result;
            if (!string.IsNullOrEmpty(raw) && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return defaultValue;
        }

        public double GetDouble(string section, string key, double defaultValue)
        {
            string raw = GetString(section, key);
            double result;
            if (!string.IsNullOrEmpty(raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return defaultValue;
        }

        public int GetInt(string section, string key, int defaultValue)
        {
            string raw = GetString(section, key);
            int result;
            if (!string.IsNullOrEmpty(raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            {
                return result;
            }
            return defaultValue;
        }

        public bool GetBool(string section, string key, bool defaultValue)
        {
            string raw = GetString(section, key);
            if (!string.IsNullOrEmpty(raw))
            {
                string lower = raw.Trim().ToLowerInvariant();
                if (lower == "true" || lower == "1" || lower == "yes" || lower == "on") return true;
                if (lower == "false" || lower == "0" || lower == "no" || lower == "off") return false;
            }
            return defaultValue;
        }

        public T GetEnum<T>(string section, string key, T defaultValue) where T : struct
        {
            string raw = GetString(section, key);
            if (!string.IsNullOrEmpty(raw))
            {
                try
                {
                    if (Enum.IsDefined(typeof(T), raw))
                    {
                        return (T)Enum.Parse(typeof(T), raw, true);
                    }
                }
                catch { }
            }
            return defaultValue;
        }

        #endregion

        #region Default INI Generation with Comments

        private void SaveDefaultsWithComments(string filePath)
        {
            string dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var sb = new StringBuilder();
            sb.AppendLine("; =============================================================================");
            sb.AppendLine(";  Derail Valley AI Traffic - Advanced Configuration File");
            sb.AppendLine(";  Location: Mods/AITraffic/settings.ini");
            sb.AppendLine(";  Settings in this file override defaults and can be reloaded in real time.");
            sb.AppendLine("; =============================================================================");
            sb.AppendLine();

            // [Physics]
            sb.AppendLine("[Physics]");
            sb.AppendLine("; Proportional gain for throttle PID controller (default: 0.08)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "ThrottleKp = {0:F4}", Physics.ThrottleKp));
            sb.AppendLine("; Integral gain for throttle PID controller (default: 0.02)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "ThrottleKi = {0:F4}", Physics.ThrottleKi));
            sb.AppendLine("; Derivative gain for throttle PID controller (default: 0.04)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "ThrottleKd = {0:F4}", Physics.ThrottleKd));
            sb.AppendLine("; Proportional gain for train brake PID controller (default: 0.14)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "BrakeKp = {0:F4}", Physics.BrakeKp));
            sb.AppendLine("; Integral gain for train brake PID controller (default: 0.02)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "BrakeKi = {0:F4}", Physics.BrakeKi));
            sb.AppendLine("; Derivative gain for train brake PID controller (default: 0.05)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "BrakeKd = {0:F4}", Physics.BrakeKd));
            sb.AppendLine("; Service braking curve deceleration multiplier (default: 1.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "DecelerationMultiplier = {0:F2}", Physics.DecelerationMultiplier));
            sb.AppendLine("; Service deceleration multiplier applied during rain/wet track conditions (default: 0.60)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WetDecelerationMultiplier = {0:F2}", Physics.WetDecelerationMultiplier));
            sb.AppendLine("; Active Wheel Slide Protection (WSP / anti-skid brake venting and auto-sanding) (default: true)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "EnableWheelSlideProtection = {0}", Physics.EnableWheelSlideProtection ? "true" : "false"));
            sb.AppendLine("; Maximum service train brake pipe reduction in wet weather without sand (default: 0.55)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "MaxWetServiceBrakeDemand = {0:F2}", Physics.MaxWetServiceBrakeDemand));
            sb.AppendLine("; Minimum traction authority floor during active wheel slip (0.20 to 0.80, default: 0.60)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "SlipCutFactor = {0:F2}", Physics.SlipCutFactor));
            sb.AppendLine("; Maximum allowed speed when climbing steep uphill grades in km/h (default: 40.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "MaxGradeClimbSpeedKmh = {0:F1}", Physics.MaxGradeClimbSpeedKmh));
            sb.AppendLine("; Maximum allowed speed when descending steep downhill grades in km/h (default: 30.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "MaxGradeDescendSpeedKmh = {0:F1}", Physics.MaxGradeDescendSpeedKmh));
            sb.AppendLine("; Independent/train brake holding duration in seconds during hill starts (default: 3.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "HillStartBrakeHoldSeconds = {0:F1}", Physics.HillStartBrakeHoldSeconds));
            sb.AppendLine();

            // [Dispatcher]
            sb.AppendLine("[Dispatcher]");
            sb.AppendLine("; Inactivity timeout before stuck/deadlocked AI train emergency despawn in seconds (default: 300.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "DeadlockTimeoutSeconds = {0:F1}", Dispatcher.DeadlockTimeoutSeconds));
            sb.AppendLine("; Switch blade safety margin in meters around movable switch points (default: 7.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "SwitchBladeZoneMeters = {0:F1}", Dispatcher.SwitchBladeZoneMeters));
            sb.AppendLine("; Clearance buffer in meters required beyond consist length for siding reservation (default: 25.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "SidingClearanceBufferMeters = {0:F1}", Dispatcher.SidingClearanceBufferMeters));
            sb.AppendLine("; Station procedural job generation wake-up inner radius in meters (default: 1200.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "StationWakeUpRadius = {0:F1}", Dispatcher.StationWakeUpRadius));
            sb.AppendLine("; Station outer hysteresis radius for job protection and clean reset in meters (default: 1600.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "StationOuterRadius = {0:F1}", Dispatcher.StationOuterRadius));
            sb.AppendLine();

            // [Economy]
            sb.AppendLine("[Economy]");
            sb.AppendLine("; Flat base fee for hiring an AI driver in dollars (default: 300.00)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "DriverBaseFee = {0:F2}", Economy.DriverBaseFee));
            sb.AppendLine("; Distance rate per route meter in dollars ($0.06/m = $60/km, default: 0.06)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "PerMeterRate = {0:F4}", Economy.PerMeterRate));
            sb.AppendLine("; Mass surcharge per ton of consist weight in dollars ($0.35/ton = $35/100t, default: 0.35)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "PerTonRate = {0:F4}", Economy.PerTonRate));
            sb.AppendLine();

            // [Consists]
            sb.AppendLine("[Consists]");
            sb.AppendLine("; Minimum number of cars for freight consists (default: 3)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "MinCars = {0}", Consists.MinCars));
            sb.AppendLine("; Maximum number of cars for freight consists (default: 20)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "MaxCars = {0}", Consists.MaxCars));
            sb.AppendLine("; Relative engine spawning selection weights (0.0 to 1.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightDE2 = {0:F2}", Consists.WeightDE2));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightDH4 = {0:F2}", Consists.WeightDH4));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightDE6 = {0:F2}", Consists.WeightDE6));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightDM3 = {0:F2}", Consists.WeightDM3));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightS060 = {0:F2}", Consists.WeightS060));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "WeightS282 = {0:F2}", Consists.WeightS282));
            sb.AppendLine();

            // [Lighting]
            sb.AppendLine("[Lighting]");
            sb.AppendLine("; Headlight operating mode: Automatic, AlwaysHigh, AlwaysLow, Off (default: Automatic)");
            sb.AppendLine(string.Format("Mode = {0}", Lighting.Mode));
            sb.AppendLine("; Attach End-of-Train (EOT) marker lantern to rear freight cars and caboose (default: true)");
            sb.AppendLine(string.Format("EOTMarker = {0}", Lighting.EOTMarker ? "true" : "false"));
            sb.AppendLine("; EOT marker flash mode: true for ~1 Hz flashing, false for steady red light (default: true)");
            sb.AppendLine(string.Format("EOTMarkerFlashing = {0}", Lighting.EOTMarkerFlashing ? "true" : "false"));
            sb.AppendLine("; EOT flash frequency in Hertz (default: 1.0)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "EOTFlashRateHz = {0:F1}", Lighting.EOTFlashRateHz));
            sb.AppendLine("; Enable locomotive cab interior backlights and dome light at night (default: true)");
            sb.AppendLine(string.Format("CabLightingAtNight = {0}", Lighting.CabLightingAtNight ? "true" : "false"));

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        #endregion

        #region Logging Helpers

        private static void LogInfo(string message)
        {
            if (Main.IsDebugLoggingEnabled)
            {
                Main.LogDebug("[IniConfig] " + message);
            }
        }

        private static void LogError(string message)
        {
            Main.LogError("[IniConfig] " + message);
        }

        #endregion
    }
}
