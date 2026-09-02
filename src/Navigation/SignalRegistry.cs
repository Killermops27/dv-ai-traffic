using System;
using System.Collections.Generic;
using AITraffic.Compat;
using UnityEngine;
using DVSignal = Signals.Game.Signal;

namespace AITraffic.Navigation
{
    /// <summary>
    /// Information about a single turnable or junction turnout switch inside a signal block.
    /// </summary>
    public struct JunctionSwitchInfo
    {
        public Junction Junction;
        public byte RequiredBranch;
        public byte CurrentBranch
        {
            get { return Junction != null ? Junction.selectedBranch : (byte)0; }
        }
        public bool IsAligned
        {
            get { return Junction != null && Junction.selectedBranch == RequiredBranch; }
        }
    }

    /// <summary>
    /// Represents a dynamic signal block along the active train route (e.g. Block 1: Train to S1, Block 2: S1 to S2).
    /// </summary>
    public class SignalBlockInfo
    {
        public int BlockIndex;
        public DVSignal EntrySignal;
        public DVSignal ExitSignal;
        public float DistanceToEntry;
        public float DistanceToExit;
        public float BlockLength;
        public List<RailTrack> Tracks = new List<RailTrack>();
        public List<JunctionSwitchInfo> Switches = new List<JunctionSwitchInfo>();
        public bool IsClear = true;
        public bool IsPlayerOccupied = false;
        public bool AreSwitchesAligned = false;
        public string AspectName = "Hp 1 (Green / Clear)";
        public Color AspectColor = new Color(0.0f, 1.0f, 0.55f);
    }

    /// <summary>
    /// Registry for DVSignals indexing, lookahead scanning, and dynamic block calculation.
    /// </summary>
    public static class SignalRegistry
    {
        private static readonly Dictionary<RailTrack, List<DVSignal>> s_trackSignals = new Dictionary<RailTrack, List<DVSignal>>();
        private static readonly Dictionary<RailTrack, RailTrackBogiesOnTrack> s_bogiesCache = new Dictionary<RailTrack, RailTrackBogiesOnTrack>();
        private static bool s_isInitialized = false;
        private static float s_lastScanTime = 0f;

        private static RailTrackBogiesOnTrack GetBogiesOnTrack(RailTrack track)
        {
            if (track == null) return null;
            RailTrackBogiesOnTrack comp;
            if (s_bogiesCache.TryGetValue(track, out comp))
            {
                if (comp != null) return comp;
                s_bogiesCache.Remove(track);
            }
            var fetched = track.GetComponent<RailTrackBogiesOnTrack>();
            if (fetched != null) s_bogiesCache[track] = fetched;
            return fetched;
        }

        /// <summary>
        /// Retrieves player trainset, locomotive position, and current speed if available.
        /// </summary>
        public static bool TryGetPlayerTrainInfo(out Trainset playerTrainset, out Vector3 playerPosition, out float playerSpeedKmh)
        {
            playerTrainset = null;
            playerPosition = Vector3.zero;
            playerSpeedKmh = 0f;

            try
            {
                if (PlayerManager.Car != null)
                {
                    playerTrainset = PlayerManager.Car.trainset;
                    playerPosition = PlayerManager.Car.transform.position;
                    playerSpeedKmh = Mathf.Abs(PlayerManager.Car.GetForwardSpeed()) * 3.6f;
                    return true;
                }
                else if (PlayerManager.LastLoco != null)
                {
                    playerTrainset = PlayerManager.LastLoco.trainset;
                    playerPosition = PlayerManager.LastLoco.transform.position;
                    playerSpeedKmh = Mathf.Abs(PlayerManager.LastLoco.GetForwardSpeed()) * 3.6f;
                    return true;
                }
                else if (PlayerManager.PlayerTransform != null)
                {
                    playerPosition = PlayerManager.PlayerTransform.position;
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// Checks whether a track segment is currently occupied by the player's train, rolling stock, or player avatar.
        /// </summary>
        public static bool IsTrackOccupiedByPlayer(RailTrack track, Trainset ignoringTrainset = null)
        {
            if (track == null) return false;

            bool rideAlong = (Main.Settings != null && Main.Settings.RideAlongMode);

            // 1. Check player's active trainset
            try
            {
                Trainset pTrainset;
                Vector3 pPos;
                float pSpeed;
                if (TryGetPlayerTrainInfo(out pTrainset, out pPos, out pSpeed) && pTrainset != null && pTrainset.cars != null)
                {
                    // If player is on or riding the AI train, do not flag it as an opposing player obstacle
                    bool isIgnoringTrain = (ignoringTrainset != null && pTrainset == ignoringTrainset);
                    bool isAITrain = false;
                    for (int i = 0; i < pTrainset.cars.Count; i++)
                    {
                        var c = pTrainset.cars[i];
                        if (c != null && (c.GetComponent<AITraffic.Driver.AIEngineer>() != null || (ignoringTrainset != null && ignoringTrainset.cars != null && ignoringTrainset.cars.Contains(c))))
                        {
                            isAITrain = true;
                            break;
                        }
                    }

                    if (!isIgnoringTrain && !isAITrain)
                    {
                        for (int i = 0; i < pTrainset.cars.Count; i++)
                        {
                            var car = pTrainset.cars[i];
                            if (car == null) continue;
                            if (car.FrontBogie != null && car.FrontBogie.track == track) return true;
                            if (car.RearBogie != null && car.RearBogie.track == track) return true;
                        }
                    }
                }
            }
            catch { }

            // 2. Check rolling stock on track that is player-spawned or non-AI locomotive
            var bogiesComp = GetBogiesOnTrack(track);
            if (bogiesComp != null && bogiesComp.bogiesOnTrack != null)
            {
                foreach (var bogie in bogiesComp.bogiesOnTrack)
                {
                    if (bogie == null || bogie.Car == null) continue;
                    if (ignoringTrainset != null)
                    {
                        if (bogie.Car.trainset == ignoringTrainset) continue;
                        if (ignoringTrainset.cars != null && ignoringTrainset.cars.Contains(bogie.Car)) continue;
                    }

                    var ai = bogie.Car.GetComponent<AITraffic.Driver.AIEngineer>();
                    if (ai == null && (bogie.Car.playerSpawnedCar || (bogie.Car.IsLoco && !bogie.Car.preventDebtDisplay)))
                    {
                        return true;
                    }
                }
            }

            // 3. Check player avatar proximity to track curve (< 20m) - suppressed in Ride Along mode or when riding the train
            if (!rideAlong)
            {
                try
                {
                    if (PlayerManager.PlayerTransform != null && track.curve != null)
                    {
                        // If player is standing on/in the AI train, do not treat as an external obstacle
                        bool playerOnIgnoringTrain = false;
                        if (ignoringTrainset != null && ignoringTrainset.cars != null)
                        {
                            Vector3 playerPos = PlayerManager.PlayerTransform.position;
                            for (int i = 0; i < ignoringTrainset.cars.Count; i++)
                            {
                                var c = ignoringTrainset.cars[i];
                                if (c != null && Vector3.Distance(playerPos, c.transform.position) < 8.0f)
                                {
                                    playerOnIgnoringTrain = true;
                                    break;
                                }
                            }
                        }

                        if (!playerOnIgnoringTrain)
                        {
                            Vector3 playerPos = PlayerManager.PlayerTransform.position;
                            Vector3 midPoint = track.curve.GetPointAt(0.5f);
                            float trackLen = track.curve.length;
                            if (Vector3.Distance(playerPos, midPoint) <= (trackLen * 0.5f + 25f))
                            {
                                float dStart = Vector3.Distance(playerPos, track.curve.GetPointAt(0.0f));
                                float dEnd = Vector3.Distance(playerPos, track.curve.GetPointAt(1.0f));
                                float dMid = Vector3.Distance(playerPos, midPoint);
                                if (Mathf.Min(dStart, Mathf.Min(dEnd, dMid)) < 20f)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a junction switch or any of its connecting branches is currently occupied by the player.
        /// </summary>
        public static bool IsJunctionOccupiedByPlayer(Junction junction, Trainset ignoringTrainset = null)
        {
            if (junction == null) return false;

            if (Main.Settings != null && Main.Settings.RideAlongMode)
            {
                return false;
            }

            // 1. Check inBranch
            if (junction.inBranch != null && junction.inBranch.track != null)
            {
                if (IsTrackOccupiedByPlayer(junction.inBranch.track, ignoringTrainset)) return true;
            }

            // 2. Check outBranches
            if (junction.outBranches != null)
            {
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track != null)
                    {
                        if (IsTrackOccupiedByPlayer(branch.track, ignoringTrainset)) return true;
                    }
                }
            }

            // 3. Proximity to junction stand mechanism (30m)
            try
            {
                if (PlayerManager.PlayerTransform != null)
                {
                    bool playerOnIgnoringTrain = false;
                    if (ignoringTrainset != null && ignoringTrainset.cars != null)
                    {
                        Vector3 playerPos = PlayerManager.PlayerTransform.position;
                        for (int i = 0; i < ignoringTrainset.cars.Count; i++)
                        {
                            var c = ignoringTrainset.cars[i];
                            if (c != null && Vector3.Distance(playerPos, c.transform.position) < 8.0f)
                            {
                                playerOnIgnoringTrain = true;
                                break;
                            }
                        }
                    }

                    if (!playerOnIgnoringTrain)
                    {
                        if (Vector3.Distance(PlayerManager.PlayerTransform.position, junction.position) < 30f)
                        {
                            return true;
                        }
                    }
                }
            }
            catch { }

            return false;
        }

        public static void Initialize(bool force = false)
        {
            if (!ModCompatManager.IsDVSignalsLoaded)
            {
                s_trackSignals.Clear();
                s_isInitialized = true;
                return;
            }

            if (!force && s_isInitialized && (Time.time - s_lastScanTime < 30f))
            {
                return;
            }

            s_trackSignals.Clear();
            s_lastScanTime = Time.time;

            try
            {
                var lights = UnityEngine.Object.FindObjectsOfType<Signals.Game.Lights.SignalLight>();
                if (lights != null)
                {
                    for (int i = 0; i < lights.Length; i++)
                    {
                        if (lights[i] == null) continue;
                        var sig = lights[i].Signal;
                        if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue)
                            continue;

                        var info = sig.Controller.PlacementInfo.Value;
                        if (info.Track == null) continue;

                        List<DVSignal> list;
                        if (!s_trackSignals.TryGetValue(info.Track, out list))
                        {
                            list = new List<DVSignal>();
                            s_trackSignals[info.Track] = list;
                        }

                        if (!list.Contains(sig))
                        {
                            list.Add(sig);
                        }
                    }
                }

                s_isInitialized = true;

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    int sigCount = lights != null ? lights.Length : 0;
                    Main.ModEntry.Logger.Log(string.Format("[SignalRegistry] Indexed {0} signal lights across {1} tracks.", sigCount, s_trackSignals.Count));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SignalRegistry] Error indexing signals: {0}", ex.Message));
            }
        }

        public struct UpcomingSignal
        {
            public DVSignal Signal;
            public float Distance;
        }

        /// <summary>
        /// Checks if a signal is facing towards oncoming traffic for the given track traversal direction.
        /// In DVSignals:
        /// - A signal facing traffic moving forward along track curve (0 -> Length) points its mast/lamps towards 0 (TrackDirection.In = 1).
        /// - A signal facing traffic moving reverse along track curve (Length -> 0) points its mast/lamps towards Length (TrackDirection.Out = 0).
        /// When facing an approaching train, the signal forward vector and train movement vector point towards each other (Dot < 0).
        /// </summary>
        public static bool IsSignalFacingTrain(DVSignal sig, float traversalDirection)
        {
            if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue) return false;

            var pInfo = sig.Controller.PlacementInfo.Value;
            int sigDir = (int)pInfo.Direction; // 0 = Out (faces Length -> 0 traffic), 1 = In (faces 0 -> Length traffic)

            // 1. Physical 3D Mast Orientation Validation (Most accurate & foolproof):
            // In DVSignals, Definition.transform.forward points in the direction the signal faces (where lamps shine).
            // A signal governing oncoming traffic points its lamps TOWARDS the approaching train.
            // When a train is moving in traversalDirection, its movement vector is tangent * traversalDirection.
            // Therefore, signalForward and trainMoveVector point in OPPOSITE directions: Dot(signalForward, trainMoveVector) < 0.
            // (If Dot > 0, the train is looking at the back of the signal mast, i.e. it is an opposing signal for traffic in the other direction).
            if (sig.Controller.Definition != null && pInfo.Track != null && pInfo.Track.curve != null)
            {
                double span = pInfo.Span;
                double trackLen = pInfo.Track.curve.length;
                float frac = (trackLen > 0.1) ? Mathf.Clamp01((float)(span / trackLen)) : 0.5f;
                Vector3 tangent = pInfo.Track.curve.GetTangentAt(frac);
                Vector3 trainMoveVector = tangent * traversalDirection;
                Vector3 signalForward = sig.Controller.Definition.transform.forward;

                float dot = Vector3.Dot(signalForward, trainMoveVector);
                if (Mathf.Abs(dot) > 0.2f)
                {
                    return dot < 0.0f;
                }
            }

            // 2. Logical DVSignals PlacementInfo Direction Mapping Fallback:
            // Forward travel (moving 0 -> Length): governed by TrackDirection.In (1) (signal faces towards span 0)
            // Reverse travel (moving Length -> 0): governed by TrackDirection.Out (0) (signal faces towards span Length)
            return (traversalDirection >= 0.0f) ? (sigDir == 1) : (sigDir == 0);
        }

        /// <summary>
        /// Checks whether a DVSignals signal acts as a Main Signal (governs block entry/exit).
        /// Filters out shunting signals and distant repeaters.
        /// </summary>
        public static bool IsMainSignal(DVSignal signal)
        {
            if (signal == null) return false;
            if (signal.IsShunting) return false;
            if (signal.CurrentAspect != null)
            {
                string aId = signal.CurrentAspect.Id ?? string.Empty;
                if (aId.IndexOf("DISTANT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    aId.IndexOf("REPEATER", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    aId.IndexOf("VR", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Gets a clean display string for a signal's current aspect (e.g. "Hp 1 (Green)", "Hp 0 (Red)", "Hp 2 (Yellow)").
        /// </summary>
        public static string GetAspectDisplayName(DVSignal signal)
        {
            if (signal == null) return "Clear / Open Route";
            if (signal.IsOff) return "Off";
            if (signal.CurrentAspect == null) return "Off / Clear";

            string id = signal.CurrentAspect.Id ?? string.Empty;
            bool disallow = signal.CurrentAspect.DisallowPassing;

            if (disallow || id.IndexOf("HP0", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hp 0 (Red / Stop)";
            if (id.IndexOf("HP2", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("YELLOW", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CAUTION", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hp 2 (Yellow / Slow)";
            if (id.IndexOf("HP1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("GREEN", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CLEAR", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Hp 1 (Green / Clear)";
            if (id.IndexOf("SH0", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Sh 0 (Red / Stop)";
            if (id.IndexOf("SH1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("WHITE", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Sh 1 (White / Shunt)";
            if (id.IndexOf("VR0", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Vr 0 (Expect Stop)";
            if (id.IndexOf("VR1", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Vr 1 (Expect Clear)";
            if (id.IndexOf("VR2", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Vr 2 (Expect Slow)";

            return id;
        }

        /// <summary>
        /// Gets a color hex string for GUI rendering corresponding to the signal aspect.
        /// </summary>
        public static string GetAspectColorHex(DVSignal signal)
        {
            if (signal == null || signal.IsOff || signal.CurrentAspect == null) return "#AAAAAA";

            string id = signal.CurrentAspect.Id ?? string.Empty;
            bool disallow = signal.CurrentAspect.DisallowPassing;

            if (disallow || id.IndexOf("HP0", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("SH0", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR0", StringComparison.OrdinalIgnoreCase) >= 0)
                return "#FF4444";
            if (id.IndexOf("HP2", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("YELLOW", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CAUTION", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR2", StringComparison.OrdinalIgnoreCase) >= 0)
                return "#FFD700";
            if (id.IndexOf("HP1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("GREEN", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CLEAR", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR1", StringComparison.OrdinalIgnoreCase) >= 0)
                return "#00FF88";
            if (id.IndexOf("SH1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("WHITE", StringComparison.OrdinalIgnoreCase) >= 0)
                return "#E0E0FF";

            return "#FFFFFF";
        }

        /// <summary>
        /// Gets a Color value corresponding to the signal aspect.
        /// </summary>
        public static Color GetAspectColor(DVSignal signal)
        {
            if (signal == null || signal.IsOff || signal.CurrentAspect == null) return new Color(0.7f, 0.7f, 0.7f);

            string id = signal.CurrentAspect.Id ?? string.Empty;
            bool disallow = signal.CurrentAspect.DisallowPassing;

            if (disallow || id.IndexOf("HP0", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("STOP", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("SH0", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR0", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Color(1.0f, 0.25f, 0.25f);
            if (id.IndexOf("HP2", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("YELLOW", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CAUTION", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR2", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Color(1.0f, 0.85f, 0.1f);
            if (id.IndexOf("HP1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("GREEN", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("CLEAR", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("VR1", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Color(0.0f, 1.0f, 0.55f);
            if (id.IndexOf("SH1", StringComparison.OrdinalIgnoreCase) >= 0 || id.IndexOf("WHITE", StringComparison.OrdinalIgnoreCase) >= 0)
                return new Color(0.9f, 0.9f, 1.0f);

            return Color.white;
        }

        /// <summary>
        /// Gets the human-readable display name of a signal.
        /// </summary>
        public static string GetSignalName(DVSignal signal)
        {
            if (signal == null) return "Signal";
            if (!string.IsNullOrEmpty(signal.Name)) return signal.Name;
            if (!string.IsNullOrEmpty(signal.InternalName)) return signal.InternalName;
            if (signal.Controller != null && !string.IsNullOrEmpty(signal.Controller.Name)) return signal.Controller.Name;
            return "Signal";
        }

        /// <summary>
        /// Gets the world-space position of a signal mast/light.
        /// </summary>
        public static Vector3 GetSignalPosition(DVSignal signal)
        {
            if (signal == null) return Vector3.zero;
            if (signal.Controller != null) return signal.Controller.Position;
            return Vector3.zero;
        }

        /// <summary>
        /// Attempts to reserve a DVSignal route via DVSignals TrackReserver.
        /// This satisfies SpecialRequireReservationAspect and turns station entry/exit signals from Hp 0 to Hp 1/Hp 2.
        /// </summary>
        public static bool TryReserveDVSignal(DVSignal signal)
        {
            if (signal == null || !ModCompatManager.IsDVSignalsLoaded) return false;
            try
            {
                if (!Signals.Game.Railway.TrackReserver.HasReservation(signal))
                {
                    Signals.Game.Railway.TrackReserver.ReserveForSignal(signal);
                }
                if (signal.Controller != null)
                {
                    signal.Controller.RequestUpdate(1);
                }
                return true;
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SignalRegistry] Error reserving DVSignal '{0}': {1}", GetSignalName(signal), ex.Message));
                return false;
            }
        }

        /// <summary>
        /// Clears an active route reservation on a DVSignal via DVSignals TrackReserver.
        /// </summary>
        public static void ClearDVSignalReservation(DVSignal signal)
        {
            if (signal == null || !ModCompatManager.IsDVSignalsLoaded) return;
            try
            {
                Signals.Game.Railway.TrackReserver.ClearFromSignal(signal);
                if (signal.Controller != null)
                {
                    signal.Controller.RequestUpdate(1);
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Warning(string.Format("[SignalRegistry] Error clearing DVSignal reservation '{0}': {1}", GetSignalName(signal), ex.Message));
            }
        }

        /// <summary>
        /// Finds the connecting junction between two consecutive route tracks and determines the required branch index.
        /// </summary>
        public static bool TryGetJunctionBetweenTracks(RailTrack trackA, RailTrack trackB, out Junction junction, out byte requiredBranch)
        {
            junction = null;
            requiredBranch = 0;

            if (trackA == null || trackB == null) return false;

            junction = trackA.outJunction ?? trackA.inJunction ?? trackB.inJunction ?? trackB.outJunction;
            if (junction == null) return false;

            // 1. Facing move: trackA is inBranch, diverging to trackB (one of outBranches)
            if (junction.inBranch != null && junction.inBranch.track == trackA)
            {
                if (junction.outBranches != null)
                {
                    for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch != null && branch.track == trackB)
                        {
                            requiredBranch = i;
                            return true;
                        }
                    }
                }
            }

            // 2. Trailing move: trackA is one of outBranches, converging to trackB (the inBranch)
            if (junction.inBranch != null && junction.inBranch.track == trackB)
            {
                if (junction.outBranches != null)
                {
                    for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch != null && branch.track == trackA)
                        {
                            requiredBranch = i;
                            return true;
                        }
                    }
                }
            }

            // 3. Out-to-out or multi-branch connectivity
            if (junction.outBranches != null)
            {
                for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && (branch.track == trackB || branch.track == trackA))
                    {
                        requiredBranch = i;
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Calculates upcoming Signal Blocks along the active route tracks:
        /// - Block 1: From train's current track to next upcoming Main Signal (S1).
        /// - Block 2: From S1 to subsequent downstream Main Signal (S2), up to 1500m ahead.
        /// Extracts all switches inside each block and computes clearance state.
        /// </summary>
        public static bool CalculateUpcomingSignalBlocks(
            RailTrack currentTrack,
            double currentSpan,
            float direction,
            IList<RailTrack> upcomingRoute,
            Trainset myTrainset,
            List<SignalBlockInfo> results,
            float maxHorizon = 1500f)
        {
            if (results == null) return false;
            results.Clear();

            if (currentTrack == null) return false;

            if (!s_isInitialized || s_trackSignals.Count == 0 || (Time.time - s_lastScanTime > 60f))
            {
                Initialize();
            }

            var currentBlock = new SignalBlockInfo
            {
                BlockIndex = 1,
                EntrySignal = null,
                DistanceToEntry = 0f,
                IsClear = true
            };
            results.Add(currentBlock);

            float accumulatedDist = 0.0f;
            RailTrack prevTrack = currentTrack;
            currentBlock.Tracks.Add(currentTrack);

            // 1. Check current track for physical train obstacles
            var curBogies = GetBogiesOnTrack(currentTrack);
            if (curBogies != null && curBogies.bogiesOnTrack != null)
            {
                foreach (var bogie in curBogies.bogiesOnTrack)
                {
                    if (bogie == null || bogie.Car == null) continue;
                    if (myTrainset != null && (bogie.Car.trainset == myTrainset || (myTrainset.cars != null && myTrainset.cars.Contains(bogie.Car)))) continue;

                    double bSpan = bogie.traveller != null ? bogie.traveller.Span : 0.0;
                    if ((direction >= 0.0f && bSpan > currentSpan) || (direction < 0.0f && bSpan < currentSpan))
                    {
                        currentBlock.IsClear = false;
                    }
                }
            }

            if (IsTrackOccupiedByPlayer(currentTrack, myTrainset))
            {
                currentBlock.IsPlayerOccupied = true;
                currentBlock.IsClear = false;
            }

            // Check current track for signals ahead of train (sorted by distance)
            List<DVSignal> curSignals;
            if (s_trackSignals.TryGetValue(currentTrack, out curSignals) && curSignals != null)
            {
                var aheadSignals = new List<UpcomingSignal>();
                for (int i = 0; i < curSignals.Count; i++)
                {
                    var sig = curSignals[i];
                    if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue) continue;
                    if (!IsSignalFacingTrain(sig, direction)) continue;

                    var pInfo = sig.Controller.PlacementInfo.Value;
                    double sigSpan = pInfo.Span;
                    float dist = (direction >= 0.0f) ? (float)(sigSpan - currentSpan) : (float)(currentSpan - sigSpan);

                    if (dist > 0f)
                    {
                        aheadSignals.Add(new UpcomingSignal { Signal = sig, Distance = dist });
                    }
                }

                if (aheadSignals.Count > 1)
                {
                    aheadSignals.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                }

                for (int i = 0; i < aheadSignals.Count; i++)
                {
                    var sig = aheadSignals[i].Signal;
                    float dist = aheadSignals[i].Distance;

                    if (IsMainSignal(sig))
                    {
                        if (currentBlock.BlockIndex == 1)
                        {
                            currentBlock.ExitSignal = sig;
                            currentBlock.DistanceToExit = dist;
                            currentBlock.BlockLength = dist - currentBlock.DistanceToEntry;
                            currentBlock.AspectName = GetAspectDisplayName(sig);
                            currentBlock.AspectColor = GetAspectColor(sig);

                            currentBlock = new SignalBlockInfo
                            {
                                BlockIndex = 2,
                                EntrySignal = sig,
                                DistanceToEntry = dist,
                                IsClear = true
                            };
                            results.Add(currentBlock);
                        }
                        else if (currentBlock.BlockIndex == 2)
                        {
                            currentBlock.ExitSignal = sig;
                            currentBlock.DistanceToExit = dist;
                            currentBlock.BlockLength = dist - currentBlock.DistanceToEntry;
                            currentBlock.AspectName = GetAspectDisplayName(sig);
                            currentBlock.AspectColor = GetAspectColor(sig);
                            break;
                        }
                    }
                }
            }

            double trackLen = currentTrack.curve != null ? currentTrack.curve.length : 100.0;
            accumulatedDist += (direction >= 0.0f) ? Mathf.Max(0.0f, (float)(trackLen - currentSpan)) : Mathf.Max(0.0f, (float)currentSpan);

            // 2. Traverse upcoming route tracks
            if (upcomingRoute != null && upcomingRoute.Count > 0)
            {
                Vector3 lastTrackExitPos = (currentTrack.curve != null)
                    ? ((direction >= 0.0f) ? currentTrack.curve.GetPointAt(1.0f) : currentTrack.curve.GetPointAt(0.0f))
                    : Vector3.zero;

                for (int r = 0; r < upcomingRoute.Count; r++)
                {
                    var rTrack = upcomingRoute[r];
                    if (rTrack == null || rTrack.curve == null) continue;
                    if (rTrack == currentTrack) continue;

                    Vector3 startPos = rTrack.curve.GetPointAt(0.0f);
                    Vector3 endPos = rTrack.curve.GetPointAt(1.0f);

                    float distToStart = Vector3.Distance(lastTrackExitPos, startPos);
                    float distToEnd = Vector3.Distance(lastTrackExitPos, endPos);
                    float routeDir = (distToStart <= distToEnd) ? 1.0f : -1.0f;
                    lastTrackExitPos = (routeDir >= 0.0f) ? endPos : startPos;

                    double rTrackLen = rTrack.curve.length;

                    // Check junction switch between prevTrack and rTrack
                    Junction junction;
                    byte requiredBranch;
                    if (TryGetJunctionBetweenTracks(prevTrack, rTrack, out junction, out requiredBranch))
                    {
                        currentBlock.Switches.Add(new JunctionSwitchInfo
                        {
                            Junction = junction,
                            RequiredBranch = requiredBranch
                        });
                    }

                    currentBlock.Tracks.Add(rTrack);

                    // Check physical obstacles on rTrack
                    var rBogies = GetBogiesOnTrack(rTrack);
                    if (rBogies != null && rBogies.bogiesOnTrack != null && rBogies.bogiesOnTrack.Count > 0)
                    {
                        foreach (var bogie in rBogies.bogiesOnTrack)
                        {
                            if (bogie == null || bogie.Car == null) continue;
                            if (myTrainset != null && (bogie.Car.trainset == myTrainset || (myTrainset.cars != null && myTrainset.cars.Contains(bogie.Car)))) continue;
                            currentBlock.IsClear = false;
                        }
                    }

                    if (IsTrackOccupiedByPlayer(rTrack, myTrainset))
                    {
                        currentBlock.IsPlayerOccupied = true;
                        currentBlock.IsClear = false;
                    }

                    // Check signals on rTrack (sorted by distance)
                    bool reachedS2 = false;
                    List<DVSignal> rSignals;
                    if (s_trackSignals.TryGetValue(rTrack, out rSignals) && rSignals != null)
                    {
                        var trackAheadSignals = new List<UpcomingSignal>();
                        for (int i = 0; i < rSignals.Count; i++)
                        {
                            var sig = rSignals[i];
                            if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue) continue;
                            if (!IsSignalFacingTrain(sig, routeDir)) continue;

                            var pInfo = sig.Controller.PlacementInfo.Value;
                            double sSpan = pInfo.Span;
                            float distFromEntry = (routeDir >= 0.0f) ? (float)sSpan : (float)(rTrackLen - sSpan);
                            float sigDist = accumulatedDist + distFromEntry;

                            trackAheadSignals.Add(new UpcomingSignal { Signal = sig, Distance = sigDist });
                        }

                        if (trackAheadSignals.Count > 1)
                        {
                            trackAheadSignals.Sort((a, b) => a.Distance.CompareTo(b.Distance));
                        }

                        for (int i = 0; i < trackAheadSignals.Count; i++)
                        {
                            var sig = trackAheadSignals[i].Signal;
                            float sigDist = trackAheadSignals[i].Distance;

                            if (IsMainSignal(sig))
                            {
                                currentBlock.ExitSignal = sig;
                                currentBlock.DistanceToExit = sigDist;
                                currentBlock.BlockLength = sigDist - currentBlock.DistanceToEntry;
                                currentBlock.AspectName = GetAspectDisplayName(sig);
                                currentBlock.AspectColor = GetAspectColor(sig);

                                int nextIdx = currentBlock.BlockIndex + 1;
                                if (nextIdx <= 4 && accumulatedDist < maxHorizon)
                                {
                                    currentBlock = new SignalBlockInfo
                                    {
                                        BlockIndex = nextIdx,
                                        EntrySignal = sig,
                                        DistanceToEntry = sigDist,
                                        IsClear = true
                                    };
                                    results.Add(currentBlock);
                                }
                                else
                                {
                                    reachedS2 = true;
                                    break;
                                }
                            }
                        }
                    }

                    accumulatedDist += (float)rTrackLen;
                    prevTrack = rTrack;

                    if (reachedS2 || accumulatedDist >= maxHorizon)
                    {
                        break;
                    }
                }
            }

            // Finalize open blocks
            for (int b = 0; b < results.Count; b++)
            {
                var blk = results[b];
                if (blk.DistanceToExit <= 0f || float.IsInfinity(blk.DistanceToExit))
                {
                    blk.DistanceToExit = accumulatedDist;
                    blk.BlockLength = Mathf.Max(0f, accumulatedDist - blk.DistanceToEntry);
                    if (string.IsNullOrEmpty(blk.AspectName) || blk.AspectName == "Clear / Open Route")
                    {
                        blk.AspectName = (blk.ExitSignal != null) ? GetAspectDisplayName(blk.ExitSignal) : ((blk.EntrySignal != null) ? GetAspectDisplayName(blk.EntrySignal) : "Hp 1 (Clear / Open Route)");
                        blk.AspectColor = GetAspectColor(blk.ExitSignal ?? blk.EntrySignal);
                    }
                }

                bool allSwitchesAligned = true;
                for (int s = 0; s < blk.Switches.Count; s++)
                {
                    if (!blk.Switches[s].IsAligned)
                    {
                        allSwitchesAligned = false;
                        break;
                    }
                }
                blk.AreSwitchesAligned = allSwitchesAligned;
            }

            return results.Count > 0;
        }

        /// <summary>
        /// Scans and returns all upcoming signals along the active route within lookahead horizon,
        /// sorted in ascending order of distance, strictly filtering for signals facing oncoming traffic.
        /// </summary>
        public static bool TryFindUpcomingSignals(
            RailTrack currentTrack,
            double currentSpan,
            float direction,
            IList<RailTrack> upcomingRoute,
            List<UpcomingSignal> results)
        {
            if (results == null) return false;
            results.Clear();

            if (!ModCompatManager.IsDVSignalsLoaded || currentTrack == null)
            {
                return false;
            }

            if (!s_isInitialized || s_trackSignals.Count == 0)
            {
                Initialize();
            }

            float accumulatedDist = 0.0f;

            // 1. Check current track ahead of locomotive span
            List<DVSignal> curSignals;
            if (s_trackSignals.TryGetValue(currentTrack, out curSignals) && curSignals != null)
            {
                for (int i = 0; i < curSignals.Count; i++)
                {
                    var sig = curSignals[i];
                    if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue) continue;

                    if (!IsSignalFacingTrain(sig, direction)) continue;

                    var pInfo = sig.Controller.PlacementInfo.Value;
                    double sigSpan = pInfo.Span;

                    if (direction >= 0.0f && sigSpan > currentSpan)
                    {
                        float dist = (float)(sigSpan - currentSpan);
                        results.Add(new UpcomingSignal { Signal = sig, Distance = dist });
                    }
                    else if (direction < 0.0f && sigSpan < currentSpan)
                    {
                        float dist = (float)(currentSpan - sigSpan);
                        results.Add(new UpcomingSignal { Signal = sig, Distance = dist });
                    }
                }
            }

            double trackLen = currentTrack.curve != null ? currentTrack.curve.length : 100.0;
            accumulatedDist += (direction >= 0.0f) ? Mathf.Max(0.0f, (float)(trackLen - currentSpan)) : Mathf.Max(0.0f, (float)currentSpan);

            // 2. Lookahead along upcomingRoute (up to 2500m horizon)
            if (upcomingRoute != null && upcomingRoute.Count > 0)
            {
                Vector3 lastTrackExitPos = (currentTrack.curve != null)
                    ? ((direction >= 0.0f) ? currentTrack.curve.GetPointAt(1.0f) : currentTrack.curve.GetPointAt(0.0f))
                    : Vector3.zero;

                for (int r = 0; r < upcomingRoute.Count; r++)
                {
                    var routeTrack = upcomingRoute[r];
                    if (routeTrack == null || routeTrack.curve == null)
                        continue;

                    Vector3 startPos = routeTrack.curve.GetPointAt(0.0f);
                    Vector3 endPos = routeTrack.curve.GetPointAt(1.0f);

                    float distToStart = Vector3.Distance(lastTrackExitPos, startPos);
                    float distToEnd = Vector3.Distance(lastTrackExitPos, endPos);
                    float routeDir = (distToStart <= distToEnd) ? 1.0f : -1.0f;

                    lastTrackExitPos = (routeDir >= 0.0f) ? endPos : startPos;

                    double rTrackLen = routeTrack.curve.length;

                    List<DVSignal> rSignals;
                    if (s_trackSignals.TryGetValue(routeTrack, out rSignals) && rSignals != null)
                    {
                        for (int i = 0; i < rSignals.Count; i++)
                        {
                            var sig = rSignals[i];
                            if (sig == null || sig.Controller == null || !sig.Controller.PlacementInfo.HasValue) continue;

                            if (!IsSignalFacingTrain(sig, routeDir)) continue;

                            var pInfo = sig.Controller.PlacementInfo.Value;
                            double sSpan = pInfo.Span;
                            float distFromEntry = (routeDir >= 0.0f) ? (float)sSpan : (float)(rTrackLen - sSpan);

                            float dist = accumulatedDist + distFromEntry;
                            results.Add(new UpcomingSignal { Signal = sig, Distance = dist });
                        }
                    }

                    accumulatedDist += (float)rTrackLen;
                    if (accumulatedDist > 2500f) break;
                }
            }

            if (results.Count > 1)
            {
                results.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            }

            return results.Count > 0;
        }

        public static bool TryFindUpcomingSignal(
            RailTrack currentTrack,
            double currentSpan,
            float direction,
            IList<RailTrack> upcomingRoute,
            out DVSignal upcomingSignal,
            out float distanceToSignal)
        {
            upcomingSignal = null;
            distanceToSignal = float.PositiveInfinity;

            var list = new List<UpcomingSignal>();
            if (TryFindUpcomingSignals(currentTrack, currentSpan, direction, upcomingRoute, list) && list.Count > 0)
            {
                upcomingSignal = list[0].Signal;
                distanceToSignal = list[0].Distance;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Scans upcoming tracks along the active route for any other train cars (AI, player, or rolling stock)
        /// to guarantee double-layer collision prevention.
        /// </summary>
        public static bool TryFindUpcomingObstacle(
            RailTrack currentTrack,
            double currentSpan,
            float direction,
            IList<RailTrack> upcomingRoute,
            Trainset myTrainset,
            out float distanceToObstacle)
        {
            distanceToObstacle = float.PositiveInfinity;
            if (currentTrack == null) return false;

            float accumulatedDist = 0.0f;

            // 1. Check current track for other cars ahead
            var curBogiesComp = GetBogiesOnTrack(currentTrack);
            if (curBogiesComp != null && curBogiesComp.bogiesOnTrack != null)
            {
                foreach (var bogie in curBogiesComp.bogiesOnTrack)
                {
                    if (bogie == null || bogie.Car == null) continue;
                    if (myTrainset != null && (bogie.Car.trainset == myTrainset || (myTrainset.cars != null && myTrainset.cars.Contains(bogie.Car)))) continue;

                    double bSpan = bogie.traveller != null ? bogie.traveller.Span : 0.0;
                    if (direction >= 0.0f && bSpan > currentSpan)
                    {
                        float dist = (float)(bSpan - currentSpan);
                        if (dist < distanceToObstacle) distanceToObstacle = dist;
                    }
                    else if (direction < 0.0f && bSpan < currentSpan)
                    {
                        float dist = (float)(currentSpan - bSpan);
                        if (dist < distanceToObstacle) distanceToObstacle = dist;
                    }
                }
            }

            if (!float.IsInfinity(distanceToObstacle))
            {
                return true;
            }

            double trackLen = currentTrack.curve != null ? currentTrack.curve.length : 100.0;
            accumulatedDist += (direction >= 0.0f) ? Mathf.Max(0.0f, (float)(trackLen - currentSpan)) : Mathf.Max(0.0f, (float)currentSpan);

            // 2. Scan upcoming route tracks (up to 1500m lookahead) with geometric direction tracking
            if (upcomingRoute != null && upcomingRoute.Count > 0)
            {
                Vector3 lastTrackExitPos = (currentTrack.curve != null)
                    ? ((direction >= 0.0f) ? currentTrack.curve.GetPointAt(1.0f) : currentTrack.curve.GetPointAt(0.0f))
                    : Vector3.zero;

                for (int r = 0; r < upcomingRoute.Count; r++)
                {
                    var rTrack = upcomingRoute[r];
                    if (rTrack == null || rTrack.curve == null)
                        continue;

                    Vector3 startPos = rTrack.curve.GetPointAt(0.0f);
                    Vector3 endPos = rTrack.curve.GetPointAt(1.0f);

                    float distToStart = Vector3.Distance(lastTrackExitPos, startPos);
                    float distToEnd = Vector3.Distance(lastTrackExitPos, endPos);
                    float routeDir = (distToStart <= distToEnd) ? 1.0f : -1.0f;

                    lastTrackExitPos = (routeDir >= 0.0f) ? endPos : startPos;
                    double rTrackLen = rTrack.curve.length;

                    var rBogiesComp = GetBogiesOnTrack(rTrack);
                    if (rBogiesComp != null && rBogiesComp.bogiesOnTrack != null && rBogiesComp.bogiesOnTrack.Count > 0)
                    {
                        foreach (var bogie in rBogiesComp.bogiesOnTrack)
                        {
                            if (bogie == null || bogie.Car == null) continue;
                            if (myTrainset != null && (bogie.Car.trainset == myTrainset || (myTrainset.cars != null && myTrainset.cars.Contains(bogie.Car)))) continue;

                            double bSpan = bogie.traveller != null ? bogie.traveller.Span : 0.0;
                            float distFromEntry = (routeDir >= 0.0f) ? (float)bSpan : (float)(rTrackLen - bSpan);
                            float dist = accumulatedDist + distFromEntry;
                            if (dist < distanceToObstacle)
                            {
                                distanceToObstacle = dist;
                            }
                        }

                        if (!float.IsInfinity(distanceToObstacle))
                        {
                            return true;
                        }
                    }

                    accumulatedDist += (float)rTrackLen;
                    if (accumulatedDist > 1500f) break;
                }
            }

            return !float.IsInfinity(distanceToObstacle);
        }
    }
}

