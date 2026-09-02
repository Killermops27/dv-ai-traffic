using System;
using System.Collections.Generic;
using Signals.Common;
using Signals.Common.Aspects;
using Signals.Game;
using Signals.Game.Aspects;
using UnityEngine;
using DVSignal = Signals.Game.Signal;

namespace AITraffic.Driver
{
    /// <summary>
    /// Identifies the primary factor restricting the locomotive's target speed.
    /// </summary>
    public enum SpeedLimitReason
    {
        DefaultLineSpeed,
        TrackSignLimit,
        CurvatureRadius,
        YardRestriction,
        SignalAspect,
        StationStop,
        BufferStop,
        EmergencyStop
    }

    /// <summary>
    /// Result structure containing speed targets and diagnostic breakdown of restrictions.
    /// </summary>
    public struct SpeedProfileResult
    {
        /// <summary>
        /// Final calculated target speed in meters per second (m/s).
        /// </summary>
        public float TargetSpeedMs;

        /// <summary>
        /// Final calculated target speed in kilometers per hour (km/h).
        /// </summary>
        public float TargetSpeedKmh;

        /// <summary>
        /// Maximum line / track limit (km/h).
        /// </summary>
        public float TrackLimitKmh;

        /// <summary>
        /// Physical centrifugal curvature limit (km/h).
        /// </summary>
        public float CurvatureLimitKmh;

        /// <summary>
        /// Dynamic speed limit imposed by approaching signal (km/h).
        /// </summary>
        public float SignalLimitKmh;

        /// <summary>
        /// Dynamic braking speed target to destination stop (km/h).
        /// </summary>
        public float StopLimitKmh;

        /// <summary>
        /// The governing restriction factor that determined the final target speed.
        /// </summary>
        public SpeedLimitReason LimitingReason;

        /// <summary>
        /// Distance remaining to the primary stop target (or infinity if none).
        /// </summary>
        public float DistanceToStop;

        public override string ToString()
        {
            return string.Format("Target: {0:F1} km/h ({1:F1} m/s) [Reason: {2}, Track: {3:F0}, Curv: {4:F0}, Sig: {5:F0}, Stop: {6:F0}]",
                TargetSpeedKmh, TargetSpeedMs, LimitingReason, TrackLimitKmh, CurvatureLimitKmh, SignalLimitKmh, StopLimitKmh);
        }
    }

    /// <summary>
    /// Computes realistic, smooth speed targets for AI-driven locomotives based on track geometry,
    /// curvature centrifugal safety limits (&lt; 0.5G), DVSignals signal aspects, and destination braking curves.
    /// </summary>
    public class SpeedProfileGenerator
    {
        #region Constants

        /// <summary>
        /// Standard gravitational acceleration (m/s^2).
        /// </summary>
        public const float Gravity = 9.80665f;

        /// <summary>
        /// Maximum safe lateral centrifugal acceleration in Gs before derailment risk (0.5G = ~4.9 m/s^2).
        /// </summary>
        public const float MaxCentrifugalG = 0.5f;

        /// <summary>
        /// Default service deceleration rate (m/s^2) for comfortable, early braking initiation.
        /// </summary>
        public const float DefaultDeceleration = 0.35f;

        /// <summary>
        /// Maximum line speed allowed anywhere on the network (km/h).
        /// </summary>
        public const float MaxNetworkSpeedKmh = 120.0f;

        /// <summary>
        /// Default speed in station/yard tracks (km/h).
        /// </summary>
        public const float DefaultYardSpeedKmh = 30.0f;

        /// <summary>
        /// Default speed on main yard tracks (km/h).
        /// </summary>
        public const float DefaultYardMainSpeedKmh = 50.0f;

        /// <summary>
        /// Default speed for yellow / caution signal aspects (km/h).
        /// </summary>
        public const float DefaultYellowSignalSpeedKmh = 40.0f;

        /// <summary>
        /// Safety distance buffer before stop line / signal mast (meters).
        /// Halts locomotive front 9.0 meters in front of the signal mast / buffer stop.
        /// </summary>
        public const float StopBufferDistance = 9.0f;

        /// <summary>
        /// Distance at which the train transitions into the low-speed crawl approach phase (meters).
        /// </summary>
        public const float CrawlStartDistance = 75.0f;

        /// <summary>
        /// Steady low crawl speed when approaching a red signal / stop target (km/h).
        /// </summary>
        public const float CrawlSpeedKmh = 10.0f;

        /// <summary>
        /// Distance at which crawl speed begins final tapering to 0 stop (meters).
        /// </summary>
        public const float FinalDecelDistance = 17.0f;

        #endregion

        #region Configuration & Fields

        /// <summary>
        /// Configured comfortable deceleration rate in m/s^2.
        /// </summary>
        public float ServiceDeceleration { get; set; }

        /// <summary>
        /// Emergency / maximum deceleration rate in m/s^2.
        /// </summary>
        public float EmergencyDeceleration { get; set; }

        /// <summary>
        /// Lateral acceleration limit multiplier (in Gs).
        /// </summary>
        public float LateralAccG { get; set; }

        private readonly Dictionary<RailTrack, float> _trackRadiusCache = new Dictionary<RailTrack, float>();
        private readonly Dictionary<RailTrack, float> _trackSpeedCache = new Dictionary<RailTrack, float>();

        #endregion

        #region Constructor

        public SpeedProfileGenerator()
        {
            ServiceDeceleration = DefaultDeceleration;
            EmergencyDeceleration = 0.9f;
            LateralAccG = MaxCentrifugalG;
        }

        #endregion

        #region Unit Conversion Helpers

        /// <summary>
        /// Converts kilometers per hour to meters per second.
        /// </summary>
        public static float KmHToMs(float kmh)
        {
            return kmh / 3.6f;
        }

        /// <summary>
        /// Converts meters per second to kilometers per hour.
        /// </summary>
        public static float MsToKmH(float ms)
        {
            return ms * 3.6f;
        }

        #endregion

        #region Curvature & Track Speed Limit Calculation

        /// <summary>
        /// Clears internal track geometry and speed caches.
        /// </summary>
        public void ClearCache()
        {
            _trackRadiusCache.Clear();
            _trackSpeedCache.Clear();
        }

        /// <summary>
        /// <summary>
        /// Returns Derail Valley standard track speed limit table value for a given curve radius,
        /// matching the game's official DV.Signs.SignPlacer thresholds.
        /// </summary>
        /// <param name="radius">Curve radius in meters.</param>
        /// <returns>Speed limit in km/h.</returns>
        public static float GetSignSpeedLimitForRadius(float radius)
        {
            if (float.IsInfinity(radius) || radius >= 1200.0f) return 120.0f;
            if (radius >= 900.0f) return 100.0f;
            if (radius >= 700.0f) return 90.0f;
            if (radius >= 360.0f) return 80.0f;
            if (radius >= 230.0f) return 70.0f;
            if (radius >= 170.0f) return 60.0f;
            if (radius >= 130.0f) return 50.0f;
            if (radius >= 95.0f) return 40.0f;
            return 30.0f;
        }

        /// <summary>
        /// Calculates the maximum theoretical speed to prevent derailment based on centrifugal acceleration:
        /// a_c = v^2 / R &lt;= a_max =&gt; v_max = sqrt(a_max * R).
        /// </summary>
        /// <param name="radius">Curve radius in meters.</param>
        /// <param name="maxG">Max lateral G-force allowed (default 0.5G).</param>
        /// <returns>Maximum speed in km/h.</returns>
        public static float GetCentrifugalSpeedLimit(float radius, float maxG)
        {
            if (float.IsInfinity(radius) || radius <= 0.0f)
            {
                return MaxNetworkSpeedKmh;
            }

            float maxLateralAcc = maxG * Gravity; // e.g. 0.5 * 9.80665 = 4.9033 m/s^2
            float vMaxMs = Mathf.Sqrt(maxLateralAcc * radius);
            return Mathf.Max(30.0f, vMaxMs * 3.6f);
        }

        /// <summary>
        /// Robust horizontal curvature radius calculator for a track curve.
        /// Uses Derail Valley's native BezierArcApproximation (matching SignPlacer)
        /// with a 2D horizontal circumcircle fallback, ignoring elevation/grade changes.
        /// </summary>
        public static float CalculateTrackHorizontalRadius(BezierCurve curve)
        {
            if (curve == null || curve.pointCount < 2 || curve.length < 0.5f)
            {
                return float.PositiveInfinity;
            }

            try
            {
                // 1. Native Derail Valley 2D arc approximation (same method used by game's SignPlacer)
                var arcs = new List<BezierArcApproximation.Arc>();
                BezierArcApproximation.CalculateArcs(curve, 1.0f, arcs);
                if (arcs != null && arcs.Count > 0)
                {
                    float minArcRadius = float.PositiveInfinity;
                    for (int i = 0; i < arcs.Count; i++)
                    {
                        var arc = arcs[i];
                        // Disregard straight lines (r >= 2500m) and tiny micro-transitions (< 8m)
                        if (arc.r > 0.0f && arc.r < 2500.0f && arc.Length >= 8.0f)
                        {
                            if (arc.r < minArcRadius)
                            {
                                minArcRadius = arc.r;
                            }
                        }
                    }

                    if (!float.IsInfinity(minArcRadius) && minArcRadius > 0.0f)
                    {
                        return minArcRadius;
                    }
                    if (arcs.Count > 0)
                    {
                        return float.PositiveInfinity; // Straight track
                    }
                }
            }
            catch
            {
                // Fallback below
            }

            // 2. 2D Horizontal Circumcircle across 3 points in XZ plane (immune to elevation/pitch)
            try
            {
                Vector3 p1 = curve.GetPointAt(0.0f);
                Vector3 p2 = curve.GetPointAt(0.5f);
                Vector3 p3 = curve.GetPointAt(1.0f);

                double a = Math.Sqrt((p2.x - p1.x) * (p2.x - p1.x) + (p2.z - p1.z) * (p2.z - p1.z));
                double b = Math.Sqrt((p3.x - p2.x) * (p3.x - p2.x) + (p3.z - p2.z) * (p3.z - p2.z));
                double c = Math.Sqrt((p1.x - p3.x) * (p1.x - p3.x) + (p1.z - p3.z) * (p1.z - p3.z));

                double area2 = Math.Abs((p2.x - p1.x) * (p3.z - p1.z) - (p2.z - p1.z) * (p3.x - p1.x));
                if (area2 < 0.01)
                {
                    return float.PositiveInfinity;
                }

                double r = (a * b * c) / (2.0 * area2);
                return (float)r;
            }
            catch
            {
                return float.PositiveInfinity;
            }
        }

        /// <summary>
        /// Computes or retrieves the minimum curve radius of a RailTrack spline.
        /// Prefers the smoothed, arc-sampled radius from RailGraph.
        /// </summary>
        public float GetTrackMinimumRadius(RailTrack track)
        {
            if (track == null) return float.PositiveInfinity;

            float cachedRadius;
            if (_trackRadiusCache.TryGetValue(track, out cachedRadius))
            {
                return cachedRadius;
            }

            // 1. Prefer RailGraph precomputed curvature radius
            if (AITraffic.Navigation.RailGraph.Instance != null)
            {
                var edge = AITraffic.Navigation.RailGraph.Instance.GetEdge(track);
                if (edge != null && !float.IsInfinity(edge.MinRadius) && edge.MinRadius > 0.0f)
                {
                    _trackRadiusCache[track] = edge.MinRadius;
                    return edge.MinRadius;
                }
            }

            // 2. Fallback: calculate horizontal radius directly
            float minRadius = CalculateTrackHorizontalRadius(track.curve);
            _trackRadiusCache[track] = minRadius;
            return minRadius;
        }

        /// <summary>
        /// Evaluates the intrinsic static speed limit of a track based on curve sign limits and yard rules.
        /// </summary>
        public float GetTrackSpeedLimit(RailTrack track)
        {
            if (track == null) return MaxNetworkSpeedKmh;

            float cachedSpeed;
            if (_trackSpeedCache.TryGetValue(track, out cachedSpeed))
            {
                return cachedSpeed;
            }

            // 1. Physical Curvature & Centrifugal limit (natural curve physics)
            float radius = GetTrackMinimumRadius(track);
            float signLimit = GetSignSpeedLimitForRadius(radius);
            float centrifugalLimit = GetCentrifugalSpeedLimit(radius, LateralAccG);

            float trackLimit = Mathf.Max(30.0f, Mathf.Min(signLimit, centrifugalLimit));

            // 2. Mainline / Station platform tracks: capped at 80 km/h max (or curve limit if lower)
            string trackName = track.name ?? string.Empty;
            bool isPlatformTrack = trackName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase) ||
                                   trackName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   trackName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isPlatformTrack)
            {
                trackLimit = Mathf.Min(trackLimit, 80.0f);
            }

            // 2b. Turnout and diverging junction switches: hard 40 km/h safety ceiling
            if (trackName.IndexOf("diverging", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trackName.IndexOf("turnout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trackName.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                trackLimit = Mathf.Min(trackLimit, 40.0f);
            }

            // 3. True storage/industrial loading tracks (only slow shunting spurs)
            bool isYardOrLoading = trackName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                                   trackName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                   trackName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase);

            if (isYardOrLoading)
            {
                trackLimit = Mathf.Min(trackLimit, DefaultYardSpeedKmh); // 30 km/h
            }

            // 4. Overwrite/respect precalculated RailGraph edge speed limit if edge is NOT a yard track
            if (AITraffic.Navigation.RailGraph.Instance != null)
            {
                var edge = AITraffic.Navigation.RailGraph.Instance.GetEdge(track);
                if (edge != null && !edge.IsYardTrack && edge.SpeedLimit > 0.0f)
                {
                    if (isPlatformTrack)
                    {
                        trackLimit = Mathf.Min(80.0f, edge.SpeedLimit);
                    }
                    else
                    {
                        trackLimit = Mathf.Min(trackLimit, edge.SpeedLimit);
                    }
                }
            }

            // Enforce hard 40 km/h cap on any diverging switch track
            if (trackName.IndexOf("diverging", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trackName.IndexOf("turnout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                trackName.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                trackLimit = Mathf.Min(trackLimit, 40.0f);
            }

            _trackSpeedCache[track] = trackLimit;
            return trackLimit;
        }

        #endregion

        #region Dynamic Braking Curves

        /// <summary>
        /// Calculates dynamic braking curve target speed:
        /// v_target = sqrt(v_end^2 + 2 * a_decel * distance_remaining).
        /// </summary>
        /// <param name="targetSpeedAtEndMs">Speed setpoint at destination (m/s).</param>
        /// <param name="distanceRemaining">Distance to the destination or constraint (meters).</param>
        /// <param name="deceleration">Service deceleration rate (m/s^2).</param>
        /// <returns>Allowable approach speed in m/s.</returns>
        public static float CalculateBrakingSpeed(float targetSpeedAtEndMs, float distanceRemaining, float deceleration)
        {
            if (distanceRemaining <= 0.0f)
            {
                return targetSpeedAtEndMs;
            }

            float safeDecel = Mathf.Max(0.01f, deceleration);
            float vEndSq = targetSpeedAtEndMs * targetSpeedAtEndMs;
            float maxSpeedSq = vEndSq + (2.0f * safeDecel * distanceRemaining);

            return Mathf.Sqrt(maxSpeedSq);
        }

        /// <summary>
        /// Calculates stopping curve towards a fixed stop point (v_end = 0):
        /// v_target = sqrt(2 * a_decel * distance_remaining).
        /// </summary>
        /// <param name="distanceToStop">Distance to the stop line in meters.</param>
        /// <param name="deceleration">Service deceleration in m/s^2.</param>
        /// <returns>Allowable speed in m/s.</returns>
        public static float CalculateStopBrakingSpeed(float distanceToStop, float deceleration)
        {
            // Within 4 meters of the mast / buffer stop: absolute halt
            if (distanceToStop <= StopBufferDistance)
            {
                return 0.0f;
            }

            // Final deceleration zone (4m to 12m): smoothly taper from 10 km/h down to 0 km/h
            if (distanceToStop <= FinalDecelDistance)
            {
                float t = Mathf.Clamp01((distanceToStop - StopBufferDistance) / (FinalDecelDistance - StopBufferDistance));
                return KmHToMs(CrawlSpeedKmh * t);
            }

            // Crawl zone (12m to 75m): maintain steady 10 km/h crawl to bring the train right to the signal
            if (distanceToStop <= CrawlStartDistance)
            {
                return KmHToMs(CrawlSpeedKmh);
            }

            // Service braking curve from line speed down to 10 km/h at 75m
            float effectiveDistance = distanceToStop - CrawlStartDistance;
            float safeDecel = Mathf.Max(0.01f, deceleration);
            float vCrawlMs = KmHToMs(CrawlSpeedKmh);
            float maxSpeedSq = (vCrawlMs * vCrawlMs) + (2.0f * safeDecel * effectiveDistance);

            return Mathf.Sqrt(maxSpeedSq);
        }

        #endregion

        #region Signal Speed Evaluation

        /// <summary>
        /// Evaluates the target speed for an approaching DVSignals signal.
        /// </summary>
        /// <param name="signal">Approaching signal instance.</param>
        /// <param name="distanceToSignal">Distance along track to the signal (meters).</param>
        /// <param name="lineSpeedKmh">Current line speed in km/h.</param>
        /// <returns>Speed constraint in km/h.</returns>
        /// <summary>
        /// Evaluates the allowed passing speed (in km/h) when traversing this signal mast.
        /// Handles m/s to km/h conversion from DVSignals AspectBaseDefinition and differentiates
        /// between immediate signal restrictions and advance distant warnings (NEXT_).
        /// </summary>
        public static float ResolveAspectPassingSpeedKmh(IAspect aspect, AspectBaseDefinition def, string aspectId, float lineSpeedKmh)
        {
            if (aspect == null) return lineSpeedKmh;

            bool disallowPassing = aspect.DisallowPassing || (def != null && def.DisallowPassing);
            if (disallowPassing) return 0.0f;

            string id = (aspectId ?? string.Empty).ToUpperInvariant();

            // 1. Pure Distant Indications (NEXT_RESTRICTED, NEXT_STOP, NEXT_CAUTION, NEXT_CLEAR)
            // When the aspect is "NEXT_..." (without a leading RESTRICTED_), the main signal head is GREEN/CLEAR.
            // The passing speed at THIS mast is full line speed; the caution applies to the subsequent block.
            if (id.StartsWith("NEXT_") || id.Contains("DISTANT") || id.Contains("REPEATER") || id.Contains("VR"))
            {
                return lineSpeedKmh;
            }

            // 2. Explicit DVSignals PassingSpeed definition
            if (def != null && def.UsePassingSpeed && def.PassingSpeed > 0f)
            {
                // DVSignals stores physical PassingSpeed in m/s (e.g. 2.11 m/s = 7.6 km/h, 11.11 m/s = 40 km/h)
                float speedKmh = (def.PassingSpeed <= 35.0f) ? (def.PassingSpeed * 3.6f) : def.PassingSpeed;
                return Mathf.Max(25.0f, speedKmh);
            }

            // 3. Main Signal Aspect Classification
            if (id.Contains("RESTRICT") || id.Contains("HP2") || id.Contains("YELLOW") ||
                id.Contains("SLOW") || id.Contains("CAUTION") || id.Contains("DIVERGING"))
            {
                return DefaultYellowSignalSpeedKmh; // 40 km/h
            }

            // 4. Clear / Green / Shunting Permitted
            return lineSpeedKmh;
        }

        /// <summary>
        /// Checks if the aspect gives an advance warning for a restriction or stop at the NEXT downstream signal.
        /// </summary>
        public static bool TryGetDistantWarningTarget(IAspect aspect, AspectBaseDefinition def, string aspectId, out float targetSpeedKmh)
        {
            targetSpeedKmh = MaxNetworkSpeedKmh;
            if (aspect == null) return false;

            string id = (aspectId ?? string.Empty).ToUpperInvariant();

            if (id.Contains("NEXT_STOP") || id.Contains("VR0") || id.Contains("DISTANT_STOP"))
            {
                targetSpeedKmh = 0.0f;
                return true;
            }

            if (id.Contains("NEXT_RESTRICTED") || id.Contains("VR2") || id.Contains("NEXT_CAUTION") ||
                id.Contains("NEXT_SLOW") || id.Contains("NEXT_DIVERGING") || id.Contains("DISTANT_CAUTION"))
            {
                targetSpeedKmh = DefaultYellowSignalSpeedKmh; // 40 km/h
                return true;
            }

            return false;
        }

        /// <summary>
        /// Evaluates the target speed for an approaching DVSignals signal.
        /// </summary>
        /// <param name="signal">Approaching signal instance.</param>
        /// <param name="distanceToSignal">Distance along track to the signal (meters).</param>
        /// <param name="lineSpeedKmh">Current line speed in km/h.</param>
        /// <returns>Speed constraint in km/h.</returns>
        public float EvaluateSignalTargetSpeed(DVSignal signal, float distanceToSignal, float lineSpeedKmh)
        {
            if (signal == null || !signal.IsOn)
            {
                return lineSpeedKmh;
            }

            IAspect currentAspect = signal.CurrentAspect;
            if (currentAspect == null)
            {
                return lineSpeedKmh;
            }

            AspectBaseDefinition def = currentAspect.GetDefinition();
            bool disallowPassing = currentAspect.DisallowPassing || (def != null && def.DisallowPassing);
            string aspectId = currentAspect.Id ?? string.Empty;

            // RED Aspect (Stop signal)
            if (disallowPassing)
            {
                float stopSpeedMs = CalculateStopBrakingSpeed(distanceToSignal, ServiceDeceleration);
                return MsToKmH(stopSpeedMs);
            }

            // Distant advance warning
            float distantTargetKmh;
            if (TryGetDistantWarningTarget(currentAspect, def, aspectId, out distantTargetKmh))
            {
                if (distantTargetKmh < lineSpeedKmh)
                {
                    float approachSpeedMs = CalculateBrakingSpeed(KmHToMs(distantTargetKmh), distanceToSignal, ServiceDeceleration);
                    return Mathf.Min(lineSpeedKmh, MsToKmH(approachSpeedMs));
                }
            }

            // Main mast passing speed
            float passingSpeedKmh = ResolveAspectPassingSpeedKmh(currentAspect, def, aspectId, lineSpeedKmh);
            if (passingSpeedKmh < lineSpeedKmh)
            {
                float approachSpeedMs = CalculateBrakingSpeed(KmHToMs(passingSpeedKmh), distanceToSignal, ServiceDeceleration);
                return Mathf.Min(lineSpeedKmh, MsToKmH(approachSpeedMs));
            }

            return lineSpeedKmh;
        }

        #endregion

        #region Comprehensive Speed Profile Computation

        /// <summary>
        /// Computes the comprehensive target speed profile considering track limits, curvature limits,
        /// approaching signals, and destination stop distance.
        /// </summary>
        /// <param name="currentTrack">The track segment the locomotive is currently on.</param>
        /// <param name="upcomingTracks">List of upcoming track segments in route.</param>
        /// <param name="approachingSignal">Next signal on route (or null if none).</param>
        /// <param name="distanceToSignal">Distance in meters to the next signal (or infinity).</param>
        /// <param name="distanceToDestination">Distance in meters to final destination / buffer stop / platform (or infinity).</param>
        /// <param name="isStationStop">True if the destination is a station platform dwell stop.</param>
        /// <param name="isTerminusStop">True if the destination is a final terminus / buffer stop.</param>
        /// <returns>SpeedProfileResult with target speeds and limiting reason.</returns>
        public SpeedProfileResult CalculateTargetSpeed(
            RailTrack currentTrack,
            IList<RailTrack> upcomingTracks,
            DVSignal approachingSignal,
            float distanceToSignal,
            float distanceToDestination,
            bool isStationStop,
            bool isTerminusStop)
        {
            var signalsList = new List<AITraffic.Navigation.SignalRegistry.UpcomingSignal>();
            if (approachingSignal != null && distanceToSignal > 0.0f)
            {
                signalsList.Add(new AITraffic.Navigation.SignalRegistry.UpcomingSignal { Signal = approachingSignal, Distance = distanceToSignal });
            }

            return CalculateTargetSpeed(
                currentTrack: currentTrack,
                currentSpan: 0.0,
                direction: 1.0f,
                upcomingTracks: upcomingTracks,
                upcomingSignals: signalsList,
                distanceToObstacle: float.PositiveInfinity,
                distanceToDestination: distanceToDestination,
                isStationStop: isStationStop,
                isTerminusStop: isTerminusStop
            );
        }

        /// <summary>
        /// Backwards-compatible overload for 7-argument calls.
        /// </summary>
        public SpeedProfileResult CalculateTargetSpeed(
            RailTrack currentTrack,
            IList<RailTrack> upcomingTracks,
            IList<AITraffic.Navigation.SignalRegistry.UpcomingSignal> upcomingSignals,
            float distanceToObstacle,
            float distanceToDestination,
            bool isStationStop,
            bool isTerminusStop)
        {
            return CalculateTargetSpeed(
                currentTrack: currentTrack,
                currentSpan: 0.0,
                direction: 1.0f,
                upcomingTracks: upcomingTracks,
                upcomingSignals: upcomingSignals,
                distanceToObstacle: distanceToObstacle,
                distanceToDestination: distanceToDestination,
                isStationStop: isStationStop,
                isTerminusStop: isTerminusStop
            );
        }

        /// <summary>
        /// Computes the comprehensive target speed profile evaluating multi-signal lookahead,
        /// red signal stop lines, distant warning signal deceleration, and obstacle avoidance.
        /// </summary>
        public SpeedProfileResult CalculateTargetSpeed(
            RailTrack currentTrack,
            double currentSpan,
            float direction,
            IList<RailTrack> upcomingTracks,
            IList<AITraffic.Navigation.SignalRegistry.UpcomingSignal> upcomingSignals,
            float distanceToObstacle,
            float distanceToDestination,
            bool isStationStop,
            bool isTerminusStop)
        {
            SpeedProfileResult result = new SpeedProfileResult();
            result.TrackLimitKmh = MaxNetworkSpeedKmh;
            result.CurvatureLimitKmh = MaxNetworkSpeedKmh;
            result.SignalLimitKmh = MaxNetworkSpeedKmh;
            result.StopLimitKmh = MaxNetworkSpeedKmh;
            result.DistanceToStop = distanceToDestination;
            result.LimitingReason = SpeedLimitReason.DefaultLineSpeed;

            // 1. Current Track Speed Limit
            if (currentTrack != null)
            {
                result.TrackLimitKmh = GetTrackSpeedLimit(currentTrack);
                float radius = GetTrackMinimumRadius(currentTrack);
                result.CurvatureLimitKmh = GetCentrifugalSpeedLimit(radius, LateralAccG);
            }

            float finalTargetKmh = Mathf.Max(30.0f, Mathf.Min(result.TrackLimitKmh, result.CurvatureLimitKmh));
            result.LimitingReason = (result.CurvatureLimitKmh < result.TrackLimitKmh) 
                ? SpeedLimitReason.CurvatureRadius 
                : SpeedLimitReason.TrackSignLimit;

            // 2. Lookahead for Upcoming Track Speed Drops (Curves / Diverging Turnouts / Yard speed drops up to 1800m ahead)
            if (upcomingTracks != null && upcomingTracks.Count > 0)
            {
                // Calculate accurate remaining distance along the current track to the start of the next track
                float curTrackLen = (currentTrack != null && currentTrack.curve != null) ? currentTrack.curve.length : 100.0f;
                float remainingOnCurrentTrack = (direction >= 0.0f) ? Mathf.Max(0.0f, curTrackLen - (float)currentSpan) : Mathf.Max(0.0f, (float)currentSpan);

                float accumulatedDistance = remainingOnCurrentTrack;

                // Start from index 1 (the next track ahead) if upcomingTracks[0] is the current track
                int startIdx = (upcomingTracks.Count > 0 && upcomingTracks[0] == currentTrack) ? 1 : 0;
                if (startIdx == 0) accumulatedDistance = 0.0f;

                for (int i = startIdx; i < upcomingTracks.Count; i++)
                {
                    RailTrack uTrack = upcomingTracks[i];
                    if (uTrack == null) continue;

                    float uLimit = GetTrackSpeedLimit(uTrack);
                    if (uLimit < finalTargetKmh)
                    {
                        // Calculate maximum allowable speed at current train position so it decelerates smoothly to reach uLimit right at uTrack entry
                        float approachSpeedMs = CalculateBrakingSpeed(KmHToMs(uLimit), accumulatedDistance, ServiceDeceleration);
                        float approachSpeedKmh = MsToKmH(approachSpeedMs);

                        if (approachSpeedKmh < finalTargetKmh)
                        {
                            finalTargetKmh = approachSpeedKmh;
                            result.LimitingReason = SpeedLimitReason.CurvatureRadius;
                        }
                    }

                    accumulatedDistance += (uTrack.curve != null ? uTrack.curve.length : 50.0f);
                    if (accumulatedDistance > 1800.0f) break;
                }
            }

            // 3. Multi-Signal Constraint Evaluation
            // - RED main signals enforce full stop curves down to 0 km/h
            // - Warning / Distant signals (Vr 0 / Expect Stop) slow trains down to 40 km/h approach speed
            // - Permissive shunting signals (White / Shunting Allowed) allow passage
            if (upcomingSignals != null && upcomingSignals.Count > 0)
            {
                for (int i = 0; i < upcomingSignals.Count; i++)
                {
                    var sigEntry = upcomingSignals[i];
                    var sig = sigEntry.Signal;
                    float distSig = sigEntry.Distance;

                    if (sig == null || !sig.IsOn || distSig <= 0.0f || distSig > 2000.0f) continue;

                    IAspect aspect = sig.CurrentAspect;
                    if (aspect == null) continue;

                    AspectBaseDefinition def = aspect.GetDefinition();
                    bool disallowPassing = aspect.DisallowPassing || (def != null && def.DisallowPassing);
                    string aspectId = aspect.Id ?? string.Empty;

                    // 1. Red / Stop Aspect (DisallowPassing)
                    if (disallowPassing)
                    {
                        float redStopSpeedMs = CalculateStopBrakingSpeed(distSig, ServiceDeceleration);
                        float redStopSpeedKmh = MsToKmH(redStopSpeedMs);
                        if (redStopSpeedKmh < finalTargetKmh)
                        {
                            finalTargetKmh = redStopSpeedKmh;
                            result.SignalLimitKmh = redStopSpeedKmh;
                            result.LimitingReason = SpeedLimitReason.SignalAspect;
                        }
                    }
                    else
                    {
                        // 2. Distant Advance Warning (NEXT_RESTRICTED, NEXT_STOP, VR2, etc.)
                        float distantTargetKmh;
                        if (TryGetDistantWarningTarget(aspect, def, aspectId, out distantTargetKmh))
                        {
                            if (distantTargetKmh < finalTargetKmh)
                            {
                                float approachMs = CalculateBrakingSpeed(KmHToMs(distantTargetKmh), distSig, ServiceDeceleration);
                                float approachKmh = MsToKmH(approachMs);
                                if (approachKmh < finalTargetKmh)
                                {
                                    finalTargetKmh = approachKmh;
                                    result.SignalLimitKmh = approachKmh;
                                    result.LimitingReason = SpeedLimitReason.SignalAspect;
                                }
                            }
                        }

                        // 3. Main Mast Passing Speed Restriction (RESTRICTED, DIVERGING, HP2, etc.)
                        float passSpeedKmh = ResolveAspectPassingSpeedKmh(aspect, def, aspectId, MaxNetworkSpeedKmh);
                        if (passSpeedKmh < finalTargetKmh)
                        {
                            float approachMs = CalculateBrakingSpeed(KmHToMs(passSpeedKmh), distSig, ServiceDeceleration);
                            float approachKmh = MsToKmH(approachMs);
                            if (approachKmh < finalTargetKmh)
                            {
                                finalTargetKmh = approachKmh;
                                result.SignalLimitKmh = approachKmh;
                                result.LimitingReason = SpeedLimitReason.SignalAspect;
                            }
                        }
                    }
                }
            }

            // 4. Physical Track Occupancy Obstacle (Another train or player on the route ahead)
            // Enforces strict absolute block separation: decelerates down to 0 km/h stop line before the obstacle/block boundary
            if (distanceToObstacle > 0.0f && distanceToObstacle < 2000.0f)
            {
                float obstacleDecel = Mathf.Max(ServiceDeceleration, 0.50f);
                float obstacleStopSpeedMs = CalculateStopBrakingSpeed(distanceToObstacle, obstacleDecel);
                float obstacleStopSpeedKmh = MsToKmH(obstacleStopSpeedMs);

                if (obstacleStopSpeedKmh < finalTargetKmh)
                {
                    finalTargetKmh = obstacleStopSpeedKmh;
                    result.StopLimitKmh = obstacleStopSpeedKmh;
                    result.DistanceToStop = distanceToObstacle;
                    result.LimitingReason = SpeedLimitReason.BufferStop;
                }
            }

            // 5. Destination / Buffer Stop / Station Platform Braking Curve
            if (distanceToDestination < 3000.0f)
            {
                float stopSpeedMs = CalculateStopBrakingSpeed(distanceToDestination, ServiceDeceleration);
                result.StopLimitKmh = MsToKmH(stopSpeedMs);

                if (result.StopLimitKmh < finalTargetKmh)
                {
                    finalTargetKmh = result.StopLimitKmh;
                    if (isStationStop)
                    {
                        result.LimitingReason = SpeedLimitReason.StationStop;
                    }
                    else if (isTerminusStop)
                    {
                        result.LimitingReason = SpeedLimitReason.BufferStop;
                    }
                    else
                    {
                        result.LimitingReason = SpeedLimitReason.StationStop;
                    }
                }
            }

            // Ensure non-negative target speed
            finalTargetKmh = Mathf.Max(0.0f, finalTargetKmh);

            result.TargetSpeedKmh = finalTargetKmh;
            result.TargetSpeedMs = KmHToMs(finalTargetKmh);

            return result;
        }

        #endregion
    }
}
