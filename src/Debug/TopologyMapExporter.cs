#if DEBUG
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using DV.Logic.Job;
using AITraffic.Core;
using AITraffic.Navigation;
using AITraffic.Fleet;
using AITraffic.Compat;
using Newtonsoft.Json;

namespace AITraffic.Diagnostics
{
    /// <summary>
    /// Debug-only exporter that inspects the rail network, classifies candidate spawns & sidings,
    /// performs batch corridor navigability verification, and exports a standalone interactive
    /// HTML5/SVG vector map directly to the user's Desktop.
    /// Excluded entirely from Release builds via #if DEBUG.
    /// </summary>
    public static class TopologyMapExporter
    {
        [Serializable]
        public class ValleyBoundsDto
        {
            public float minX;
            public float maxX;
            public float minZ;
            public float maxZ;
            public float width;
            public float height;
        }

        [Serializable]
        public class TrackDto
        {
            public string id;
            public string name;
            public string logicId;
            public List<float[]> pts;
            public float len;
            public float grade;
            public bool isDeadEnd;
            public bool isSpawn;
            public bool isFreightIn;
            public bool isStorage;
            public bool isPax;
            public bool isLoop;
            public bool isSteep;
            public bool isOccupied;
            public string station;
        }

        [Serializable]
        public class StationDto
        {
            public string id;
            public string name;
            public float x;
            public float y;
        }

        [Serializable]
        public class CorridorDto
        {
            public string orig;
            public string dest;
            public string consist;
            public bool passed;
            public float dist;
            public float depGrade;
            public string depTrack;
            public string destTrack;
            public List<string> trackIds;
        }

        [Serializable]
        public class TrainDto
        {
            public string carId;
            public string consist;
            public float speed;
            public float heading;
            public string orig;
            public string dest;
            public float x;
            public float y;
            public List<string> path;
        }

        [Serializable]
        public class MapPayloadDto
        {
            public ValleyBoundsDto valleyBounds;
            public List<TrackDto> tracks;
            public List<StationDto> stations;
            public List<CorridorDto> corridors;
            public List<TrainDto> activeTrains;
        }

        /// <summary>
        /// Gathers the rail network state, generates the vector map HTML document,
        /// saves it to the Desktop, and opens it in the default browser.
        /// </summary>
        public static void ExportToDesktop()
        {
            try
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log("[TopologyMapExporter] Starting vector map snapshot extraction...");
                }

                // 1. Gather all rail tracks
                List<RailTrack> allTracks = null;
                try
                {
                    if (RailTrackRegistryBase.Instance != null && RailTrackRegistryBase.Instance.AllTracks != null && RailTrackRegistryBase.Instance.AllTracks.Length > 0)
                    {
                        allTracks = RailTrackRegistryBase.Instance.AllTracks.Where(t => t != null && t.curve != null).ToList();
                    }
                }
                catch { }

                if (allTracks == null || allTracks.Count == 0)
                {
                    allTracks = UnityEngine.Object.FindObjectsOfType<RailTrack>()
                        .Where(t => t != null && t.curve != null)
                        .ToList();
                }

                if (allTracks == null || allTracks.Count == 0)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning("[TopologyMapExporter] No rail tracks found in world. Is a save loaded?");
                    return;
                }

                var occupiedSnapshot = RailGraph.BuildOccupiedTracksSnapshot();

                // 2. Sample curves and compute global bounding box
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                var rawSampledTracks = new Dictionary<RailTrack, List<Vector3>>();

                for (int i = 0; i < allTracks.Count; i++)
                {
                    var track = allTracks[i];
                    if (track == null || track.curve == null || track.curve.length < 0.1f)
                        continue;

                    float len = track.curve.length;
                    // Full Bézier curve resolution: sample every ~4 meters (min 4, max 64 samples)
                    int samples = Mathf.Clamp(Mathf.RoundToInt(len / 4f), 4, 64);
                    var pts = new List<Vector3>(samples + 1);

                    for (int s = 0; s <= samples; s++)
                    {
                        float frac = (float)s / samples;
                        Vector3 pt = track.curve.GetPointAt(frac);
                        pts.Add(pt);

                        if (pt.x < minX) minX = pt.x;
                        if (pt.x > maxX) maxX = pt.x;
                        if (pt.z < minZ) minZ = pt.z;
                        if (pt.z > maxZ) maxZ = pt.z;
                    }

                    rawSampledTracks[track] = pts;
                }

                // Add 150m boundary margin
                minX -= 150f;
                maxX += 150f;
                minZ -= 150f;
                maxZ += 150f;

                float viewBoxWidth = maxX - minX;
                float viewBoxHeight = maxZ - minZ;

                // 3. Pre-index station tracks and candidate departure tracks
                var stationTrackMap = new Dictionary<RailTrack, string>();
                var candidateSpawnTracks = new HashSet<RailTrack>();

                if (StationController.allStations != null)
                {
                    for (int s = 0; s < StationController.allStations.Count; s++)
                    {
                        var sc = StationController.allStations[s];
                        if (sc == null || sc.stationInfo == null) continue;
                        string yardId = sc.stationInfo.YardID;

                        if (sc.AllStationTracks != null)
                        {
                            for (int t = 0; t < sc.AllStationTracks.Count; t++)
                            {
                                var trk = sc.AllStationTracks[t];
                                if (trk != null && !stationTrackMap.ContainsKey(trk))
                                {
                                    stationTrackMap[trk] = yardId;
                                }
                            }
                        }

                        // Collect candidate departure tracks for this station
                        var depTracks = TrafficScheduler.GetCandidateDepartureTracks(sc, 100f, null);
                        if (depTracks != null)
                        {
                            for (int d = 0; d < depTracks.Count; d++)
                            {
                                candidateSpawnTracks.Add(depTracks[d]);
                            }
                        }
                    }
                }

                // 4. Transform tracks into DTOs
                var trackToIdMap = new Dictionary<RailTrack, string>();
                int trkCounter = 0;
                foreach (var trk in rawSampledTracks.Keys)
                {
                    trackToIdMap[trk] = "trk_" + trkCounter++;
                }

                var trackDtos = new List<TrackDto>(rawSampledTracks.Count);

                foreach (var kvp in rawSampledTracks)
                {
                    var trk = kvp.Key;
                    var pts = kvp.Value;
                    string tName = trk.name ?? "";
                    string trackId = trackToIdMap[trk];

                    // Find associated station
                    string stationYardId = "";
                    if (!stationTrackMap.TryGetValue(trk, out stationYardId))
                    {
                        // Fallback name heuristic (e.g. "[Y]_[HB]_[C-04-I]" -> "HB")
                        if (StationController.allStations != null)
                        {
                            for (int s = 0; s < StationController.allStations.Count; s++)
                            {
                                var sc = StationController.allStations[s];
                                if (sc != null && sc.stationInfo != null && !string.IsNullOrEmpty(sc.stationInfo.YardID))
                                {
                                    if (tName.IndexOf(sc.stationInfo.YardID, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        stationYardId = sc.stationInfo.YardID;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    StationController assignedStation = !string.IsNullOrEmpty(stationYardId) ? TrafficScheduler.FindStation(stationYardId) : null;

                    Vector3 p0 = pts[0];
                    Vector3 pEnd = pts[pts.Count - 1];
                    float trackLen = trk.curve.length;
                    float grade = (trackLen > 0.5f) ? (Mathf.Abs(pEnd.y - p0.y) / trackLen) * 100f : 0f;

                    var svgPts = new List<float[]>(pts.Count);
                    for (int p = 0; p < pts.Count; p++)
                    {
                        // Normalize (X, Z) to SVG viewBox, with North (+Z) pointing up (-Y_svg)
                        float svgX = (float)Math.Round(pts[p].x - minX, 1);
                        float svgY = (float)Math.Round(maxZ - pts[p].z, 1);
                        svgPts.Add(new float[] { svgX, svgY });
                    }

                    var lt = ModCompatManager.GetLogicTrack(trk);
                    string logicId = (lt != null && lt.ID != null) ? lt.ID.FullDisplayID : "";

                    bool isFreight = assignedStation != null && TrafficScheduler.IsInboundTrack(trk, assignedStation);
                    bool isStorage = assignedStation != null && TrafficScheduler.IsYardStorageTrack(trk, assignedStation);
                    bool isPax = assignedStation != null && TrafficScheduler.IsPlatformTrack(trk, assignedStation);
                    bool isLoop = TrafficScheduler.IsPassingOrLoopTrack(trk);
                    bool isSpawn = candidateSpawnTracks.Contains(trk);
                    bool isDeadEnd = TrafficScheduler.IsDeadEndTrack(trk);
                    bool isOccupied = TrafficScheduler.IsTrackOccupied(trk, occupiedSnapshot);
                    bool isSteep = grade > TrafficScheduler.MaxSpawnInclineGrade;

                    trackDtos.Add(new TrackDto
                    {
                        id = trackId,
                        name = tName,
                        logicId = logicId,
                        pts = svgPts,
                        len = (float)Math.Round(trackLen, 1),
                        grade = (float)Math.Round(grade, 2),
                        isDeadEnd = isDeadEnd,
                        isSpawn = isSpawn,
                        isFreightIn = isFreight,
                        isStorage = isStorage,
                        isPax = isPax,
                        isLoop = isLoop,
                        isSteep = isSteep,
                        isOccupied = isOccupied,
                        station = stationYardId
                    });
                }

                // 5. Gather Stations
                var stationDtos = new List<StationDto>();
                if (StationController.allStations != null)
                {
                    for (int s = 0; s < StationController.allStations.Count; s++)
                    {
                        var sc = StationController.allStations[s];
                        if (sc == null || sc.stationInfo == null) continue;
                        Vector3 pos = sc.transform.position;

                        stationDtos.Add(new StationDto
                        {
                            id = sc.stationInfo.YardID,
                            name = sc.stationInfo.Name,
                            x = (float)Math.Round(pos.x - minX, 1),
                            y = (float)Math.Round(maxZ - pos.z, 1)
                        });
                    }
                }

                // 6. Batch Corridor Route Verification
                var corridorDtos = new List<CorridorDto>();
                var pathfinder = new Pathfinder();
                var pathOptions = new PathfinderOptions
                {
                    StrictlyAvoidOccupied = false, // Validate topological connectivity
                    AvoidOccupiedTracks = false
                };

                if (TrafficScheduler.s_corridors != null)
                {
                    for (int c = 0; c < TrafficScheduler.s_corridors.Length; c++)
                    {
                        var corridor = TrafficScheduler.s_corridors[c];
                        var origStation = TrafficScheduler.FindStation(corridor.OriginYardId);
                        var destStation = TrafficScheduler.FindStation(corridor.DestinationYardId);

                        if (origStation == null || destStation == null) continue;

                        var depTracks = TrafficScheduler.GetCandidateDepartureTracks(origStation, 100f, null);
                        var destTracks = TrafficScheduler.GetCandidateDestinationTracks(destStation, corridor.PreferredConsist, null);

                        if (depTracks != null && depTracks.Count > 1)
                        {
                            Vector3 origPos = origStation.transform.position;
                            depTracks = depTracks
                                .OrderBy(t => Vector3.Distance(t.curve != null ? t.curve.GetPointAt(0.5f) : t.transform.position, origPos))
                                .ToList();
                        }

                        if (destTracks != null && destTracks.Count > 1)
                        {
                            Vector3 destPos = destStation.transform.position;
                            destTracks = destTracks
                                .OrderBy(t => Vector3.Distance(t.curve != null ? t.curve.GetPointAt(0.5f) : t.transform.position, destPos))
                                .ToList();
                        }

                        RailPath bestPath = null;
                        RailTrack bestDep = null;
                        RailTrack bestDest = null;

                        int maxDep = Math.Min(8, depTracks != null ? depTracks.Count : 0);
                        int maxDest = Math.Min(5, destTracks != null ? destTracks.Count : 0);

                        for (int i = 0; i < maxDep; i++)
                        {
                            for (int j = 0; j < maxDest; j++)
                            {
                                if (depTracks[i] == destTracks[j]) continue;
                                var p = pathfinder.FindPath(depTracks[i], destTracks[j], pathOptions);
                                if (p != null && p.IsValid && p.Tracks.Count > 0)
                                {
                                    if (bestPath == null || p.TotalDistance < bestPath.TotalDistance)
                                    {
                                        bestPath = p;
                                        bestDep = depTracks[i];
                                        bestDest = destTracks[j];
                                    }
                                }
                            }
                        }

                        bool passed = (bestPath != null && bestPath.IsValid);
                        float depGrade = (bestPath != null) ? TrafficScheduler.CalculateDepartureGrade(bestPath) : 0f;
                        var routeTrackIds = new List<string>();

                        if (bestPath != null && bestPath.Tracks != null)
                        {
                            for (int t = 0; t < bestPath.Tracks.Count; t++)
                            {
                                var rTrk = bestPath.Tracks[t];
                                if (rTrk != null)
                                {
                                    string rId;
                                    if (trackToIdMap.TryGetValue(rTrk, out rId))
                                        routeTrackIds.Add(rId);
                                }
                            }
                        }

                        corridorDtos.Add(new CorridorDto
                        {
                            orig = corridor.OriginYardId,
                            dest = corridor.DestinationYardId,
                            consist = corridor.PreferredConsist.ToString(),
                            passed = passed,
                            dist = bestPath != null ? (float)Math.Round(bestPath.TotalDistance, 1) : 0f,
                            depGrade = (float)Math.Round(depGrade, 2),
                            depTrack = bestDep != null ? bestDep.name : "None",
                            destTrack = bestDest != null ? bestDest.name : "None",
                            trackIds = routeTrackIds
                        });
                    }
                }

                // 7. Gather Active AI Trains
                var trainDtos = new List<TrainDto>();
                if (TrafficManager.Instance != null && TrafficManager.Instance.ActiveEngineers != null)
                {
                    var engs = TrafficManager.Instance.ActiveEngineers;
                    for (int i = 0; i < engs.Count; i++)
                    {
                        var eng = engs[i];
                        if (eng == null || eng.TrainCar == null) continue;

                        Vector3 pos = eng.TrainCar.transform.position;
                        Vector3 fwd = eng.TrainCar.transform.forward;
                        float headingDeg = Mathf.Atan2(fwd.x, fwd.z) * Mathf.Rad2Deg;

                        var pathTracks = new List<string>();
                        if (eng.CurrentPath != null && eng.CurrentPath.Tracks != null)
                        {
                            for (int t = 0; t < eng.CurrentPath.Tracks.Count; t++)
                            {
                                var rTrk = eng.CurrentPath.Tracks[t];
                                if (rTrk != null)
                                {
                                    string rId;
                                    if (trackToIdMap.TryGetValue(rTrk, out rId))
                                        pathTracks.Add(rId);
                                }
                            }
                        }

                        trainDtos.Add(new TrainDto
                        {
                            carId = eng.TrainCar.ID ?? "Loco",
                            consist = eng.IsWorkerDriven ? "Player Worker" : "Ambient AI",
                            speed = (float)Math.Round(Mathf.Abs(eng.CurrentSpeedKmh), 1),
                            heading = (float)Math.Round(headingDeg, 1),
                            orig = eng.OriginStationName ?? "",
                            dest = eng.DestinationStationName ?? "",
                            x = (float)Math.Round(pos.x - minX, 1),
                            y = (float)Math.Round(maxZ - pos.z, 1),
                            path = pathTracks
                        });
                    }
                }

                // 8. Package JSON payload
                var payload = new MapPayloadDto
                {
                    valleyBounds = new ValleyBoundsDto
                    {
                        minX = minX,
                        maxX = maxX,
                        minZ = minZ,
                        maxZ = maxZ,
                        width = viewBoxWidth,
                        height = viewBoxHeight
                    },
                    tracks = trackDtos,
                    stations = stationDtos,
                    corridors = corridorDtos,
                    activeTrains = trainDtos
                };

                string jsonPayload = JsonConvert.SerializeObject(payload, Formatting.None);
                string htmlContent = TopologyMapTemplate.GetHtml(jsonPayload);

                // 9. Save to user's Desktop
                string desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string outputPath = Path.Combine(desktopFolder, "dv_ai_traffic_map.html");

                File.WriteAllText(outputPath, htmlContent, Encoding.UTF8);

                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                {
                    Main.ModEntry.Logger.Log(string.Format("[TopologyMapExporter] Vector map successfully exported to: {0}", outputPath));
                }

                // 10. Automatically launch default web browser
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = outputPath,
                        UseShellExecute = true
                    });
                }
                catch (Exception pEx)
                {
                    if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                        Main.ModEntry.Logger.Warning(string.Format("[TopologyMapExporter] Could not auto-launch browser: {0}. File is saved at {1}", pEx.Message, outputPath));
                }
            }
            catch (Exception ex)
            {
                if (Main.ModEntry != null && Main.ModEntry.Logger != null)
                    Main.ModEntry.Logger.Error("[TopologyMapExporter] Error during vector map export: " + ex);
            }
        }
    }
}
#endif
