using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;
using AITraffic;

namespace AITraffic.Navigation
{
    /// <summary>
    /// Represents a vertex in the Derail Valley rail network graph (junction, track end, or dead end).
    /// </summary>
    public class RailNode
    {
        public int Id { get; private set; }
        public string Name { get; private set; }
        public Vector3 Position { get; private set; }
        public Junction Junction { get; internal set; }
        public float Grade { get; internal set; }
        public float SpeedLimit { get; internal set; }

        public List<RailEdge> IncidentEdges { get; private set; }
        public List<RailEdge> InEdges { get; private set; }
        public List<RailEdge> OutEdges { get; private set; }

        public bool IsJunction
        {
            get { return Junction != null; }
        }

        public bool IsDeadEnd
        {
            get
            {
                return IncidentEdges.Count <= 1 && (Junction == null || Junction.outBranches == null || Junction.outBranches.Count == 0);
            }
        }

        public RailNode(int id, string name, Vector3 position, Junction junction = null)
        {
            Id = id;
            Name = !string.IsNullOrEmpty(name) ? name : (junction != null ? junction.name : "Node_" + id);
            Position = position;
            Junction = junction;
            Grade = 0f;
            SpeedLimit = 120f;

            IncidentEdges = new List<RailEdge>();
            InEdges = new List<RailEdge>();
            OutEdges = new List<RailEdge>();
        }

        public bool IsFacing(RailEdge incomingEdge)
        {
            if (Junction == null || incomingEdge == null || Junction.inBranch == null)
                return false;

            return incomingEdge.Track == Junction.inBranch.track;
        }

        public bool IsTrailing(RailEdge incomingEdge)
        {
            if (Junction == null || incomingEdge == null || Junction.outBranches == null)
                return false;

            for (int i = 0; i < Junction.outBranches.Count; i++)
            {
                var branch = Junction.outBranches[i];
                if (branch != null && branch.track == incomingEdge.Track)
                    return true;
            }
            return false;
        }

        public byte GetBranchIndexForEdge(RailEdge targetEdge)
        {
            if (Junction == null || targetEdge == null)
                return 0;

            if (Junction.inBranch != null && Junction.inBranch.track == targetEdge.Track)
                return 0;

            if (Junction.outBranches != null)
            {
                for (byte i = 0; i < (byte)Junction.outBranches.Count; i++)
                {
                    var branch = Junction.outBranches[i];
                    if (branch != null && branch.track == targetEdge.Track)
                        return i;
                }
            }

            return 0;
        }

        public override string ToString()
        {
            return string.Format("RailNode[Id={0}, Name='{1}', Junction={2}, Pos={3}]",
                Id, Name, Junction != null ? Junction.name : "null", Position);
        }
    }

    /// <summary>
    /// Represents an edge in the rail network graph, corresponding to a RailTrack segment.
    /// </summary>
    public class RailEdge
    {
        public int Id { get; private set; }
        public RailTrack Track { get; private set; }
        public string TrackName { get; private set; }
        public RailNode FromNode { get; internal set; } // Track in-node (curve[0])
        public RailNode ToNode { get; internal set; }   // Track out-node (curve.Last())
        public float Length { get; private set; }
        public float Curvature { get; internal set; }   // Max curvature (1/R) in 1/m
        public float MinRadius { get; internal set; }   // Minimum curve radius in meters
        public float Grade { get; internal set; }       // Average grade in %
        public float SpeedLimit { get; internal set; }  // Maximum allowed speed in km/h

        public bool IsDoubleTrackMainline { get; internal set; }
        public bool PreferredForward { get; internal set; } // True if FromNode -> ToNode is right-hand running
        public RailEdge ParallelEdge { get; internal set; }

        public byte InBranchIndex { get; internal set; }
        public byte OutBranchIndex { get; internal set; }
        public bool IsYardTrack { get; internal set; }
        public bool IsJunctionTrack { get; internal set; }
        public bool IsStrictlyAvoidedThroughTrack { get; internal set; }
        public bool IsPassingLoopOrSiding { get; internal set; }
        public bool IsShuntingOrStorageTrack { get; internal set; }
        public bool IsGatedMilitaryTrack { get; internal set; }
        public bool IsIndustrialYardOrStorageTrack { get; internal set; }
        public DV.Logic.Job.Track LogicTrack { get; internal set; }

        public Vector3 MidPoint { get; internal set; }
        public Vector3 FromTangent { get; internal set; }
        public Vector3 ToTangent { get; internal set; }

        public RailEdge(int id, RailTrack track, RailNode fromNode, RailNode toNode)
        {
            if (track == null) throw new ArgumentNullException("track");

            Id = id;
            Track = track;
            // CRITICAL INVARIANT [RGR-03]: Pre-cache TrackName on the Main Thread for background thread pathfinding safety (See GRAVEYARD.md)
            TrackName = track.name ?? string.Empty;
            FromNode = fromNode;
            ToNode = toNode;

            if (track.curve != null && track.curve.pointCount > 0)
            {
                Length = track.curve.length;
                MidPoint = track.curve.GetPointAt(0.5f);
                FromTangent = track.curve.GetTangentAt(0f).normalized;
                ToTangent = track.curve.GetTangentAt(1f).normalized;
            }
            else
            {
                Vector3 p1 = fromNode != null ? fromNode.Position : Vector3.zero;
                Vector3 p2 = toNode != null ? toNode.Position : Vector3.zero;
                Length = Vector3.Distance(p1, p2);
                MidPoint = (p1 + p2) * 0.5f;
                Vector3 dir = (Length > 0.001f) ? (p2 - p1).normalized : Vector3.forward;
                FromTangent = dir;
                ToTangent = dir;
            }

            IsJunctionTrack = track.isJunctionTrack;
            Curvature = 0f;
            MinRadius = float.PositiveInfinity;
            Grade = 0f;
            SpeedLimit = 120f;
            IsDoubleTrackMainline = false;
            PreferredForward = true;
        }

        public RailNode GetOtherNode(RailNode node)
        {
            if (node == FromNode) return ToNode;
            if (node == ToNode) return FromNode;
            return null;
        }

        public Vector3 FromLookaheadTangent { get; internal set; }
        public Vector3 ToLookaheadTangent { get; internal set; }

        public bool IsForward(RailNode fromNode, RailNode toNode)
        {
            return fromNode == FromNode && toNode == ToNode;
        }

        public bool SharesNodeWith(RailEdge other)
        {
            if (other == null) return false;
            return FromNode == other.FromNode || FromNode == other.ToNode ||
                   ToNode == other.FromNode || ToNode == other.ToNode;
        }

        public bool IsRightHandInDirection(bool forward)
        {
            return forward == PreferredForward;
        }

        public string TrackDisplayName
        {
            get
            {
                if (LogicTrack != null && LogicTrack.ID != null && !string.IsNullOrEmpty(LogicTrack.ID.FullDisplayID))
                    return LogicTrack.ID.FullDisplayID;
                if (!string.IsNullOrEmpty(TrackName))
                    return TrackName;
                if (Track != null && !string.IsNullOrEmpty(Track.name))
                    return Track.name;
                return string.Empty;
            }
        }

        // CRITICAL INVARIANT [RGR-07]: Thread-safe precomputed lookahead departure direction (18m past switch blades) for right-hand branch resolution [RGR-03].
        public Vector3 GetLookaheadDirection(RailNode fromNode)
        {
            if (fromNode == FromNode && FromLookaheadTangent != Vector3.zero)
                return FromLookaheadTangent;
            if (fromNode == ToNode && ToLookaheadTangent != Vector3.zero)
                return ToLookaheadTangent;
            return GetDirection(fromNode);
        }

        public Vector3 GetDirection(RailNode fromNode)
        {
            if (fromNode == FromNode)
            {
                if (FromTangent != Vector3.zero) return FromTangent;
                if (Track != null && Track.curve != null && Track.curve.pointCount > 0)
                {
                    return Track.curve.GetTangentAt(0f).normalized;
                }
                if (ToNode != null && FromNode != null)
                {
                    return (ToNode.Position - FromNode.Position).normalized;
                }
            }
            else if (fromNode == ToNode)
            {
                if (ToTangent != Vector3.zero) return -ToTangent;
                if (Track != null && Track.curve != null && Track.curve.pointCount > 0)
                {
                    return -Track.curve.GetTangentAt(1f).normalized;
                }
                if (FromNode != null && ToNode != null)
                {
                    return (FromNode.Position - ToNode.Position).normalized;
                }
            }
            else if (fromNode != null)
            {
                // Position-based fallback if reference doesn't match
                float dFrom = FromNode != null ? Vector3.SqrMagnitude(fromNode.Position - FromNode.Position) : float.MaxValue;
                float dTo = ToNode != null ? Vector3.SqrMagnitude(fromNode.Position - ToNode.Position) : float.MaxValue;
                if (dFrom <= dTo)
                {
                    if (FromTangent != Vector3.zero) return FromTangent;
                    if (Track != null && Track.curve != null && Track.curve.pointCount > 0)
                        return Track.curve.GetTangentAt(0f).normalized;
                    if (ToNode != null && FromNode != null)
                        return (ToNode.Position - FromNode.Position).normalized;
                }
                else
                {
                    if (ToTangent != Vector3.zero) return -ToTangent;
                    if (Track != null && Track.curve != null && Track.curve.pointCount > 0)
                        return -Track.curve.GetTangentAt(1f).normalized;
                    if (FromNode != null && ToNode != null)
                        return (FromNode.Position - ToNode.Position).normalized;
                }
            }

            return (FromTangent != Vector3.zero)
                ? FromTangent
                : ((Track != null && Track.curve != null && Track.curve.pointCount > 0) ? Track.curve.GetTangentAt(0f).normalized : Vector3.forward);
        }

        public Vector3 GetMidPoint()
        {
            if (MidPoint != Vector3.zero) return MidPoint;
            if (Track != null && Track.curve != null)
            {
                return Track.curve.GetPointAt(0.5f);
            }
            if (FromNode != null && ToNode != null)
            {
                return (FromNode.Position + ToNode.Position) * 0.5f;
            }
            return Vector3.zero;
        }

        public Vector3 GetPointAtSpan(float span)
        {
            if (Track == null || Track.curve == null || Length <= 0f)
                return FromNode != null ? FromNode.Position : Vector3.zero;

            float t = Mathf.Clamp01(span / Length);
            return Track.curve.GetPointAt(t);
        }

        public Vector3 GetTangentAtSpan(float span)
        {
            if (Track == null || Track.curve == null || Length <= 0f)
                return Vector3.forward;

            float t = Mathf.Clamp01(span / Length);
            return Track.curve.GetTangentAt(t).normalized;
        }

        public override string ToString()
        {
            return string.Format("RailEdge[Id={0}, Track='{1}', FromNode={2}, ToNode={3}, Len={4:F1}m, Speed={5}km/h, DoubleTrack={6}]",
                Id, Track.name, FromNode != null ? FromNode.Id : 0, ToNode != null ? ToNode.Id : 0, Length, SpeedLimit, IsDoubleTrackMainline);
        }
    }

    /// <summary>
    public class RailGraph
    {
        private static RailGraph s_instance;
        public static RailGraph Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = new RailGraph();
                }
                return s_instance;
            }
        }

        public List<RailNode> Nodes { get; private set; }
        public List<RailEdge> Edges { get; private set; }
        public bool IsInitialized { get; private set; }

        public event Action OnGraphInitialized;

        private readonly Dictionary<RailTrack, RailEdge> _trackToEdge = new Dictionary<RailTrack, RailEdge>();
        private readonly Dictionary<Junction, RailNode> _junctionToNode = new Dictionary<Junction, RailNode>();
        private readonly Dictionary<int, RailNode> _nodesById = new Dictionary<int, RailNode>();
        private readonly Dictionary<int, RailEdge> _edgesById = new Dictionary<int, RailEdge>();

        private readonly Dictionary<RailTrack, object> _trackReservations = new Dictionary<RailTrack, object>();
        private readonly object _lock = new object();

        // CRITICAL INVARIANT [RGR-04]: Per-frame cache of tracks occupied by suspended/optimized cars (where bogie.enabled == false)
        // to prevent O(AllCars) rescans on every IsTrackOccupied(track, ignoreTrainset) query.
        private static int s_cachedSuspendedTracksFrame = -1;
        private static readonly Dictionary<RailTrack, Trainset> s_cachedSuspendedTrackOwner = new Dictionary<RailTrack, Trainset>();
        private static readonly HashSet<RailTrack> s_cachedMultiOwnerSuspendedTracks = new HashSet<RailTrack>();

        public RailGraph()
        {
            s_instance = this;
            Nodes = new List<RailNode>();
            Edges = new List<RailEdge>();
        }

        private static void Log(string msg)
        {
            if (Main.IsDebugLoggingEnabled)
                Main.LogDebug("[AITraffic] " + msg);
        }

        private static void LogWarning(string msg)
        {
            Main.LogWarning("[AITraffic] " + msg);
        }

        private static void LogError(string msg)
        {
            Main.LogError("[AITraffic] " + msg);
        }

        /// <summary>
        /// Scans WorldData or RailTrackRegistry to initialize the graph automatically.
        /// </summary>
        public void Initialize()
        {
            try
            {
                var tracks = GetWorldTracks();
                if (tracks == null || tracks.Count == 0)
                {
                    LogWarning("[RailGraph] No tracks found to initialize graph.");
                    return;
                }

                Initialize(tracks);
            }
            catch (Exception ex)
            {
                LogError("[RailGraph] Error during parameterless Initialize: " + ex);
            }
        }

        /// <summary>
        /// Scans the provided list of RailTrack segments to build the graph.
        /// </summary>
        public void Initialize(IEnumerable<RailTrack> allTracks)
        {
            if (allTracks == null)
                throw new ArgumentNullException("allTracks");

            Initialize(allTracks.ToList());
        }

        /// <summary>
        /// Builds the rail network graph from the given RailTrack list.
        /// </summary>
        public void Initialize(List<RailTrack> allTracks)
        {
            if (allTracks == null || allTracks.Count == 0)
            {
                LogWarning("[RailGraph] Initialize called with empty tracks list.");
                return;
            }

            lock (_lock)
            {
                try
                {
                    Log(string.Format("[RailGraph] Initializing rail graph with {0} tracks...", allTracks.Count));

                    Clear();

                    BuildNodesAndEdges(allTracks);
                    LinkJunctionsAndIncidentEdges();
                    CalculateCurvatureAndGrade();
                    MapLogicTracks();
                    CalculateSpeedLimits();
                    DetectDoubleTrackCorridors();
                    DiscoverSingleTrackCorridors();
                    BuildSpatialTrackGrid();
                    PrecomputeTraversableEdges();

                    IsInitialized = true;
                    Main.Log(string.Format("[RailGraph] Initialization complete: {0} nodes, {1} edges.", Nodes.Count, Edges.Count));

                    if (OnGraphInitialized != null)
                    {
                        OnGraphInitialized.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    LogError("[RailGraph] Failed to initialize RailGraph: " + ex);
                    throw;
                }
            }
        }

                private void Clear()
        {
            Nodes.Clear();
            Edges.Clear();
            _trackToEdge.Clear();
            _junctionToNode.Clear();
            _nodesById.Clear();
            _edgesById.Clear();
            _trackReservations.Clear();
            _spatialGrid.Clear();
            _traversableEdgesCache.Clear();
            s_cachedSuspendedTracksFrame = -1;
            s_cachedSuspendedTrackOwner.Clear();
            s_cachedMultiOwnerSuspendedTracks.Clear();
            IsInitialized = false;
        }

        private List<RailTrack> GetWorldTracks()
        {
            try
            {
                if (RailTrackRegistryBase.Instance != null && RailTrackRegistryBase.Instance.AllTracks != null && RailTrackRegistryBase.Instance.AllTracks.Length > 0)
                {
                    return RailTrackRegistryBase.Instance.AllTracks.Where(t => t != null).ToList();
                }
            }
            catch
            {
            }

            var foundTracks = UnityEngine.Object.FindObjectsOfType<RailTrack>();
            if (foundTracks != null && foundTracks.Length > 0)
            {
                return foundTracks.Where(t => t != null).ToList();
            }

            return new List<RailTrack>();
        }

        private struct PositionNodeEntry
        {
            public Vector3 Position;
            public RailNode Node;
            public PositionNodeEntry(Vector3 pos, RailNode node)
            {
                Position = pos;
                Node = node;
            }
        }

        private void BuildNodesAndEdges(List<RailTrack> allTracks)
        {
            int nodeIdCounter = 1;
            int edgeIdCounter = 1;

            var positionToNode = new List<PositionNodeEntry>();
            const float positionMergeThreshold = 1.2f;

            for (int tIdx = 0; tIdx < allTracks.Count; tIdx++)
            {
                var track = allTracks[tIdx];
                if (track == null || track.curve == null || track.curve.pointCount < 2)
                    continue;

                Vector3 inPos = track.curve[0].position;
                Vector3 outPos = track.curve.Last().position;

                RailNode fromNode = GetOrCreateNode(inPos, track.inJunction, track.inJunction != null ? track.inJunction.name : track.name + "_In", ref nodeIdCounter, positionToNode, positionMergeThreshold);
                RailNode toNode = GetOrCreateNode(outPos, track.outJunction, track.outJunction != null ? track.outJunction.name : track.name + "_Out", ref nodeIdCounter, positionToNode, positionMergeThreshold);

                var edge = new RailEdge(edgeIdCounter++, track, fromNode, toNode);
                Edges.Add(edge);
                _edgesById[edge.Id] = edge;
                _trackToEdge[track] = edge;

                fromNode.IncidentEdges.Add(edge);
                fromNode.OutEdges.Add(edge);

                toNode.IncidentEdges.Add(edge);
                toNode.InEdges.Add(edge);
            }
        }

        private RailNode GetOrCreateNode(
            Vector3 pos,
            Junction junction,
            string namePrefix,
            ref int nodeIdCounter,
            List<PositionNodeEntry> positionToNode,
            float positionMergeThreshold)
        {
            RailNode existingJunctionNode;
            if (junction != null && _junctionToNode.TryGetValue(junction, out existingJunctionNode))
            {
                return existingJunctionNode;
            }

            for (int i = 0; i < positionToNode.Count; i++)
            {
                var item = positionToNode[i];
                if (Vector3.Distance(item.Position, pos) <= positionMergeThreshold)
                {
                    if (junction != null)
                    {
                        if (item.Node.Junction == null)
                        {
                            item.Node.Junction = junction;
                        }
                        _junctionToNode[junction] = item.Node;
                    }
                    return item.Node;
                }
            }

            var newNode = new RailNode(nodeIdCounter++, namePrefix, pos, junction);
            Nodes.Add(newNode);
            _nodesById[newNode.Id] = newNode;
            positionToNode.Add(new PositionNodeEntry(pos, newNode));

            if (junction != null)
            {
                _junctionToNode[junction] = newNode;
            }

            return newNode;
        }

        private void LinkJunctionsAndIncidentEdges()
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge.Track == null) continue;

                if (edge.FromNode != null && edge.FromNode.Junction != null)
                {
                    edge.InBranchIndex = edge.FromNode.GetBranchIndexForEdge(edge);
                }

                if (edge.ToNode != null && edge.ToNode.Junction != null)
                {
                    edge.OutBranchIndex = edge.ToNode.GetBranchIndexForEdge(edge);
                }
            }
        }

        private void CalculateCurvatureAndGrade()
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                var track = edge.Track;
                if (track == null || track.curve == null) continue;

                var curve = track.curve;
                float length = curve.length;
                if (length <= 0.1f)
                {
                    edge.Curvature = 0f;
                    edge.MinRadius = float.PositiveInfinity;
                    edge.Grade = 0f;
                    continue;
                }

                Vector3 pStart = curve[0].position;
                Vector3 pEnd = curve.Last().position;
                float deltaY = pEnd.y - pStart.y;
                float horizontalDist = Mathf.Sqrt(Mathf.Pow(pEnd.x - pStart.x, 2) + Mathf.Pow(pEnd.z - pStart.z, 2));

                edge.Grade = horizontalDist > 0.1f ? (deltaY / horizontalDist) * 100f : 0f;

                float minRadius = AITraffic.Driver.SpeedProfileGenerator.CalculateTrackHorizontalRadius(curve);
                edge.MinRadius = minRadius;
                edge.Curvature = float.IsInfinity(minRadius) || minRadius <= 0f ? 0f : (1f / minRadius);
            }

            for (int n = 0; n < Nodes.Count; n++)
            {
                var node = Nodes[n];
                if (node.IncidentEdges.Count > 0)
                {
                    float gradeSum = 0f;
                    for (int e = 0; e < node.IncidentEdges.Count; e++)
                    {
                        gradeSum += node.IncidentEdges[e].Grade;
                    }
                    node.Grade = gradeSum / node.IncidentEdges.Count;
                }
            }
        }

        private void MapLogicTracks()
        {
            try
            {
                // CRITICAL INVARIANT [RGR-03]: Ensure station yard GONames cache is initialized on the Unity Main Thread before classifying edges.
                Pathfinder.InitializeStationYardCache();

                if (RailTrackRegistry.RailTrackToLogicTrack != null)
                {
                    for (int i = 0; i < Edges.Count; i++)
                    {
                        var edge = Edges[i];
                        if (edge == null || edge.Track == null) continue;

                        DV.Logic.Job.Track logicTrack;
                        if (RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(edge.Track, out logicTrack))
                        {
                            edge.LogicTrack = logicTrack;
                            if (logicTrack != null && logicTrack.ID != null)
                            {
                                string yardId = logicTrack.ID.yardId ?? string.Empty;
                                string fullId = logicTrack.ID.FullDisplayID ?? string.Empty;
                                string trackPart = logicTrack.ID.TrackPartOnly ?? string.Empty;

                                string tName = edge.Track.name ?? string.Empty;
                                bool isDoubleTrackMod = tName.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                        tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool isMainlineMod = tName.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool isCrossover = IsCrossoverTrackName(tName);

                                bool isDeadEnd = (edge.FromNode != null && edge.FromNode.IsDeadEnd) || 
                                                 (edge.ToNode != null && edge.ToNode.IsDeadEnd);
                                bool isOpenCountryRoad = (tName.StartsWith("Road ", StringComparison.OrdinalIgnoreCase) || isMainlineMod) && !isDeadEnd;

                                if (isDoubleTrackMod || isOpenCountryRoad || isCrossover)
                                {
                                    if (isDoubleTrackMod) edge.IsDoubleTrackMainline = true;
                                    edge.IsYardTrack = false;
                                    edge.IsShuntingOrStorageTrack = false;
                                    edge.IsIndustrialYardOrStorageTrack = false;
                                    edge.IsStrictlyAvoidedThroughTrack = false;
                                }
                                else if (tName.Contains("[--]") || tName.EndsWith("--", StringComparison.OrdinalIgnoreCase) || tName.EndsWith("--]", StringComparison.OrdinalIgnoreCase) || Pathfinder.IsPassingOrSidingTrack(edge.Track))
                                {
                                    edge.IsYardTrack = false;
                                }
                                else
                                {
                                    // Classify yard tracks strictly by Track Type Suffix (-I], -O], -L], -S], -C], -T], -P]), LogicTrack type, or StationController registry (never by leading [Y] namespace prefix)
                                    bool isStorageOrLoading = tName.StartsWith("[yard", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.StartsWith("[I]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.StartsWith("[O]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.StartsWith("[T]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-I]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-O]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-L]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-S]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-C]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-T]", StringComparison.OrdinalIgnoreCase) ||
                                                              tName.EndsWith("-P]", StringComparison.OrdinalIgnoreCase) ||
                                                              trackPart.StartsWith("L", StringComparison.OrdinalIgnoreCase) ||
                                                              trackPart.StartsWith("C", StringComparison.OrdinalIgnoreCase) ||
                                                              Pathfinder.IsStrictlyAvoidedThroughTrack(edge.Track) ||
                                                              Pathfinder.IsIndustrialYardOrStorageTrack(edge.Track);

                                    if (isDeadEnd || isStorageOrLoading)
                                    {
                                        edge.IsYardTrack = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            string tName = edge.Track.name ?? string.Empty;
                            bool isDoubleTrackMod = tName.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                    tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isMainlineMod = tName.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isCrossover = IsCrossoverTrackName(tName);

                            bool isDeadEnd = (edge.FromNode != null && edge.FromNode.IsDeadEnd) || 
                                             (edge.ToNode != null && edge.ToNode.IsDeadEnd);
                            bool isOpenCountryRoad = (tName.StartsWith("Road ", StringComparison.OrdinalIgnoreCase) || isMainlineMod) && !isDeadEnd;

                            if (isDoubleTrackMod || isOpenCountryRoad || isCrossover)
                            {
                                if (isDoubleTrackMod) edge.IsDoubleTrackMainline = true;
                                edge.IsYardTrack = false;
                                edge.IsShuntingOrStorageTrack = false;
                                edge.IsIndustrialYardOrStorageTrack = false;
                                edge.IsStrictlyAvoidedThroughTrack = false;
                            }
                            else if (tName.Contains("[--]") || tName.EndsWith("--", StringComparison.OrdinalIgnoreCase) || tName.EndsWith("--]", StringComparison.OrdinalIgnoreCase) || Pathfinder.IsPassingOrSidingTrack(edge.Track))
                            {
                                edge.IsYardTrack = false;
                            }
                            else
                            {
                                if (isDeadEnd ||
                                    tName.StartsWith("[yard", StringComparison.OrdinalIgnoreCase) ||
                                    tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.StartsWith("[I]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.StartsWith("[O]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.StartsWith("[T]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-I]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-O]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-L]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-S]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-C]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-T]", StringComparison.OrdinalIgnoreCase) ||
                                    tName.EndsWith("-P]", StringComparison.OrdinalIgnoreCase) ||
                                    Pathfinder.IsStrictlyAvoidedThroughTrack(edge.Track) ||
                                    Pathfinder.IsIndustrialYardOrStorageTrack(edge.Track))
                                {
                                    edge.IsYardTrack = true;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogWarning(string.Format("Failed to map logic tracks from RailTrackRegistry: {0}", ex.Message));
            }
        }

        private void CalculateSpeedLimits()
        {
            // CRITICAL INVARIANT [RGR-03]: Pre-populate station yard GameObject names and edge classifications on the Unity Main Thread (See GRAVEYARD.md)
            Pathfinder.InitializeStationYardCache();

            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                float radius = edge.MinRadius;
                float geoLimit;

                if (float.IsInfinity(radius) || radius >= 1200f) geoLimit = 120f;
                else if (radius >= 900f) geoLimit = 100f;
                else if (radius >= 700f) geoLimit = 90f;
                else if (radius >= 360f) geoLimit = 80f;
                else if (radius >= 230f) geoLimit = 70f;
                else if (radius >= 170f) geoLimit = 60f;
                else if (radius >= 130f) geoLimit = 50f;
                else if (radius >= 95f) geoLimit = 40f;
                else geoLimit = 30f;

                string tName = edge.TrackName ?? string.Empty;
                bool isPlatform = tName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase) ||
                                  tName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0;

                edge.IsStrictlyAvoidedThroughTrack = Pathfinder.IsStrictlyAvoidedThroughTrack(edge.Track);
                edge.IsPassingLoopOrSiding = Pathfinder.IsPassingOrSidingTrack(edge.Track);
                edge.IsShuntingOrStorageTrack = Pathfinder.IsShuntingOrStorageTrack(edge.Track);
                edge.IsGatedMilitaryTrack = Pathfinder.IsGatedMilitaryTrack(edge.Track);
                edge.IsIndustrialYardOrStorageTrack = Pathfinder.IsIndustrialYardOrStorageTrack(edge.Track);

                bool isDeadEnd = (edge.FromNode != null && edge.FromNode.IsDeadEnd) || 
                                 (edge.ToNode != null && edge.ToNode.IsDeadEnd);
                // CRITICAL INVARIANT [FLT-11]: Never classify gated military "Road ... MA/MB" tracks as open-country mainline roads (See GRAVEYARD.md)
                bool isOpenCountryRoad = !edge.IsGatedMilitaryTrack &&
                                         (tName.StartsWith("Road ", StringComparison.OrdinalIgnoreCase) ||
                                          tName.StartsWith("[#]", StringComparison.OrdinalIgnoreCase) ||
                                          tName.Contains("[#]") ||
                                          IsCrossoverTrackName(tName)) && !isDeadEnd;
                if (isOpenCountryRoad)
                {
                    edge.IsYardTrack = false;
                    edge.IsShuntingOrStorageTrack = false;
                    edge.IsIndustrialYardOrStorageTrack = false;
                    edge.IsStrictlyAvoidedThroughTrack = false;
                }

                bool isPassingLoopOutsideStation = (edge.IsPassingLoopOrSiding || tName.IndexOf("Siding", StringComparison.OrdinalIgnoreCase) >= 0) &&
                                                   !Pathfinder.IsIndustrialYardOrStorageTrack(edge.Track);

                if (isPassingLoopOutsideStation)
                {
                    // User directive: on sidings other than in station allow the speed like on mainline depending on curve of the track
                    edge.SpeedLimit = geoLimit;
                }
                else if (edge.IsYardTrack)
                {
                    edge.SpeedLimit = Mathf.Min(30f, geoLimit);
                }
                else if (isPlatform)
                {
                    edge.SpeedLimit = Mathf.Min(80f, geoLimit);
                }
                else
                {
                    edge.SpeedLimit = geoLimit;
                }
            }

            for (int n = 0; n < Nodes.Count; n++)
            {
                var node = Nodes[n];
                if (node.IncidentEdges.Count > 0)
                {
                    float minSpeed = float.MaxValue;
                    for (int e = 0; e < node.IncidentEdges.Count; e++)
                    {
                        float sp = node.IncidentEdges[e].SpeedLimit;
                        if (sp < minSpeed) minSpeed = sp;
                    }
                    node.SpeedLimit = minSpeed;
                }
                else
                {
                    node.SpeedLimit = 30f;
                }
            }
        }

        /// <summary>
        /// Seamlessly detects parallel double-track mainline corridors and designates right-hand running direction.
        /// </summary>
        public void DetectDoubleTrackCorridors()
        {
            const float minParallelDistance = 2.5f;
            const float maxParallelDistance = 18.0f;
            const float minTangentAlignment = 0.85f;
            const float maxLongitudinalStagger = 6.0f;
            const float maxVerticalSeparation = 4.0f;
            const int longEdgeSampleCount = 16;
            float[] shortSeedFractions = new float[] { 0.25f, 0.50f, 0.75f };

            // CRITICAL INVARIANT [RGR-07]: Precompute lookahead departure tangents on Main Thread [RGR-03] and pair all parallel tracks via zero-stagger cross-section projection.
            for (int e = 0; e < Edges.Count; e++)
            {
                var edge = Edges[e];
                if (edge == null) continue;

                edge.FromLookaheadTangent = edge.FromTangent;
                edge.ToLookaheadTangent = -edge.ToTangent;

                if (edge.Track != null && edge.Track.curve != null && edge.Length > 0.5f)
                {
                    float lookahead = Mathf.Min(18.0f, edge.Length * 0.5f);
                    Vector3 p0 = edge.GetPointAtSpan(0f);
                    Vector3 pFromAhead = edge.GetPointAtSpan(lookahead);
                    Vector3 tFromAhead = edge.GetTangentAtSpan(lookahead);
                    Vector3 chordFrom = pFromAhead - p0;
                    chordFrom.y = 0f;
                    if (chordFrom.sqrMagnitude > 0.0001f) chordFrom.Normalize();
                    Vector3 flatTanFrom = new Vector3(tFromAhead.x, 0f, tFromAhead.z);
                    if (flatTanFrom.sqrMagnitude > 0.0001f) flatTanFrom.Normalize();
                    Vector3 blendFrom = chordFrom + flatTanFrom;
                    if (blendFrom.sqrMagnitude > 0.0001f)
                        edge.FromLookaheadTangent = blendFrom.normalized;

                    Vector3 p1 = edge.GetPointAtSpan(edge.Length);
                    float revSpan = Mathf.Max(0f, edge.Length - lookahead);
                    Vector3 pToAhead = edge.GetPointAtSpan(revSpan);
                    Vector3 tToAhead = -edge.GetTangentAtSpan(revSpan);
                    Vector3 chordTo = pToAhead - p1;
                    chordTo.y = 0f;
                    if (chordTo.sqrMagnitude > 0.0001f) chordTo.Normalize();
                    Vector3 flatTanTo = new Vector3(tToAhead.x, 0f, tToAhead.z);
                    if (flatTanTo.sqrMagnitude > 0.0001f) flatTanTo.Normalize();
                    Vector3 blendTo = chordTo + flatTanTo;
                    if (blendTo.sqrMagnitude > 0.0001f)
                        edge.ToLookaheadTangent = blendTo.normalized;
                }

                if (edge.Track != null && edge.Track.name != null)
                {
                    if (edge.Track.name.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        edge.Track.name.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        edge.IsDoubleTrackMainline = true;
                        edge.IsYardTrack = false;
                    }
                    else if (edge.Track.name.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             edge.Track.name.StartsWith("Road ", StringComparison.OrdinalIgnoreCase) ||
                             IsCrossoverTrackName(edge.Track.name) ||
                             edge.Track.name.Contains("[--]") ||
                             edge.Track.name.EndsWith("--", StringComparison.OrdinalIgnoreCase) ||
                             edge.Track.name.EndsWith("--]", StringComparison.OrdinalIgnoreCase))
                    {
                        edge.IsYardTrack = false;
                    }
                }
            }

            var candidateEdges = new List<RailEdge>();
            for (int e = 0; e < Edges.Count; e++)
            {
                var edge = Edges[e];
                if (edge == null || edge.Track == null || edge.Track.curve == null || edge.IsStrictlyAvoidedThroughTrack)
                    continue;
                if (edge.Length < 10f)
                    continue;

                // Never allow 24m junction switch tracks ([track through] / [track diverging]) into Pass 1 geometric pairing.
                // They inherit corridor state strictly via Pass 2 colinear propagation or verified Pass 3 split-to-merge chains.
                if (IsShortSwitchTrackEdge(edge))
                    continue;

                string tName = edge.TrackDisplayName;
                bool isCrossover = IsCrossoverTrackName(tName);
                if (isCrossover) continue;

                if (edge.IsYardTrack && !edge.IsPassingLoopOrSiding &&
                    (tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) < 0 && tName.IndexOf("doubletrack", StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                if (Pathfinder.IsIndustrialYardOrStorageTrack(edge.Track))
                    continue;

                candidateEdges.Add(edge);
            }

            float[] bestParallelDist = new float[Edges.Count + 1];
            for (int i = 0; i < bestParallelDist.Length; i++) bestParallelDist[i] = float.MaxValue;

            int geomPairedCount = 0;

            // Pass 1: Multi-sample local cross-section geometric pairing (3 seeds on edgeShort x 16 samples on edgeLong)
            for (int i = 0; i < candidateEdges.Count; i++)
            {
                RailEdge edge1 = candidateEdges[i];
                for (int j = i + 1; j < candidateEdges.Count; j++)
                {
                    RailEdge edge2 = candidateEdges[j];
                    if (edge1.SharesNodeWith(edge2)) continue;

                    // CRITICAL INVARIANT [RGR-07]: Seed alternating spline cross-section projection from the SHORTER edge
                    bool edge1IsShorter = (edge1.Length <= edge2.Length);
                    RailEdge edgeShort = edge1IsShorter ? edge1 : edge2;
                    RailEdge edgeLong = edge1IsShorter ? edge2 : edge1;

                    // CRITICAL INVARIANT: A short junction switch track or throat neck (< 35m) must NEVER pair directly in Pass 1
                    // with a long non-junction track if their length ratio exceeds 3.0x.
                    // Short switch tracks ([track through], [track diverging]) get their pairing cleanly in Pass 2 / Pass 3.
                    bool isShortSwitchTrack = IsShortSwitchTrackEdge(edgeShort) || edgeShort.Length < 35.0f;
                    if (isShortSwitchTrack && (edgeLong.Length > 60.0f && edgeLong.Length > edgeShort.Length * 3.0f))
                        continue;

                    // Quick bounding sphere pre-filter before multi-sample curve evaluation
                    float maxSep = (edgeShort.Length * 0.5f) + (edgeLong.Length * 0.5f) + maxParallelDistance + maxLongitudinalStagger;
                    if ((edgeShort.MidPoint - edgeLong.MidPoint).sqrMagnitude > maxSep * maxSep)
                        continue;

                    float bestPairDist = float.MaxValue;
                    Vector3 bestPtShort = Vector3.zero;
                    Vector3 bestTanShort = Vector3.zero;
                    Vector3 bestPtLong = Vector3.zero;
                    Vector3 bestTanLong = Vector3.zero;
                    bool foundValidSection = false;

                    for (int s = 0; s < shortSeedFractions.Length; s++)
                    {
                        float spanShort = edgeShort.Length * shortSeedFractions[s];
                        Vector3 ptShort = edgeShort.GetPointAtSpan(spanShort);
                        Vector3 tanShort = edgeShort.GetTangentAtSpan(spanShort);
                        Vector3 flatTanShort = new Vector3(tanShort.x, 0f, tanShort.z);
                        if (flatTanShort.sqrMagnitude < 0.0001f) continue;
                        flatTanShort.Normalize();

                        float closestLongSpan = 0f;
                        float closestSqDist = float.MaxValue;
                        for (int k = 0; k < longEdgeSampleCount; k++)
                        {
                            float sampleFrac = (longEdgeSampleCount > 1) ? ((float)k / (longEdgeSampleCount - 1)) : 0.5f;
                            float sampleSpan = edgeLong.Length * sampleFrac;
                            Vector3 samplePt = edgeLong.GetPointAtSpan(sampleSpan);
                            float sqDist = (samplePt - ptShort).sqrMagnitude;
                            if (sqDist < closestSqDist)
                            {
                                closestSqDist = sqDist;
                                closestLongSpan = sampleSpan;
                            }
                        }

                        // Project once along local tangent to refine closestLongSpan
                        Vector3 initPtLong = edgeLong.GetPointAtSpan(closestLongSpan);
                        Vector3 initTanLong = edgeLong.GetTangentAtSpan(closestLongSpan);
                        float projDelta = Vector3.Dot(ptShort - initPtLong, initTanLong);
                        float refinedSpanLong = Mathf.Clamp(closestLongSpan + projDelta, 0f, edgeLong.Length);

                        Vector3 ptLong = edgeLong.GetPointAtSpan(refinedSpanLong);
                        Vector3 tanLong = edgeLong.GetTangentAtSpan(refinedSpanLong);

                        if (Mathf.Abs(ptLong.y - ptShort.y) > maxVerticalSeparation) continue;

                        Vector3 diff = ptLong - ptShort;
                        float longStagger = Mathf.Abs(Vector3.Dot(diff, flatTanShort));
                        if (longStagger > maxLongitudinalStagger) continue;

                        float horizDist = new Vector2(diff.x, diff.z).magnitude;
                        if (horizDist < minParallelDistance || horizDist > maxParallelDistance) continue;

                        Vector3 flatTanLong = new Vector3(tanLong.x, 0f, tanLong.z);
                        if (flatTanLong.sqrMagnitude < 0.0001f) continue;
                        flatTanLong.Normalize();

                        float dot = Vector3.Dot(flatTanShort, flatTanLong);
                        if (Mathf.Abs(dot) < minTangentAlignment) continue;

                        if (horizDist < bestPairDist)
                        {
                            bestPairDist = horizDist;
                            bestPtShort = ptShort;
                            bestTanShort = flatTanShort;
                            bestPtLong = ptLong;
                            bestTanLong = flatTanLong;
                            foundValidSection = true;
                        }
                    }

                    if (!foundValidSection) continue;

                    Vector3 rightNormalShort = Vector3.Cross(Vector3.up, bestTanShort).normalized;
                    Vector3 crossVec = bestPtLong - bestPtShort;
                    float lateralOffset = Vector3.Dot(crossVec, rightNormalShort);
                    if (Mathf.Abs(lateralOffset) < 1.5f) continue;

                    float alignDot = Vector3.Dot(bestTanShort, bestTanLong);
                    // lateralOffset > 0 means edgeLong is to the RIGHT of edgeShort when facing +bestTanShort:
                    // -> edgeShort is the LEFT track (PreferredForward = false), edgeLong is the RIGHT track.
                    // lateralOffset < 0 means edgeLong is to the LEFT of edgeShort when facing +bestTanShort:
                    // -> edgeShort is the RIGHT track (PreferredForward = true), edgeLong is the LEFT track.
                    bool prefShort = (lateralOffset <= 0f);
                    bool prefLong = (lateralOffset > 0f) ? (alignDot > 0f) : (alignDot <= 0f);

                    if (bestPairDist < bestParallelDist[edgeShort.Id])
                    {
                        if (!edgeShort.IsDoubleTrackMainline) geomPairedCount++;
                        bestParallelDist[edgeShort.Id] = bestPairDist;
                        edgeShort.IsDoubleTrackMainline = true;
                        edgeShort.ParallelEdge = edgeLong;
                        edgeShort.PreferredForward = prefShort;
                    }

                    if (bestPairDist < bestParallelDist[edgeLong.Id])
                    {
                        if (!edgeLong.IsDoubleTrackMainline) geomPairedCount++;
                        bestParallelDist[edgeLong.Id] = bestPairDist;
                        edgeLong.IsDoubleTrackMainline = true;
                        edgeLong.ParallelEdge = edgeShort;
                        edgeLong.PreferredForward = prefLong;
                    }
                    else if (!edgeLong.IsDoubleTrackMainline)
                    {
                        // Symmetric 1-to-N handshake guarantee
                        geomPairedCount++;
                        edgeLong.IsDoubleTrackMainline = true;
                        edgeLong.ParallelEdge = edgeShort;
                        edgeLong.PreferredForward = prefLong;
                    }
                }
            }

            // Pass 2A: Propagate corridor pairing and directional handedness across degree-2 nodes and conflict-free colinear switch leads
            PropagateColinearCorridorLeads(maxLongitudinalStagger, maxParallelDistance);

            // Pass 3: Bounded Split-to-Merge Twin Corridor Continuation (CRITICAL INVARIANT [RGR-07]: <= 500.0f Length Delta Cap)
            int splitMergePairedCount = PropagateBoundedSplitToMergeCorridors();

            // Pass 2B: Propagate corridor pairing to colinear switch leads adjacent to newly paired Pass 3 split-to-merge corridors
            PropagateColinearCorridorLeads(maxLongitudinalStagger, maxParallelDistance);

            int totalPairedCorridorEdges = 0;
            for (int i = 0; i < Edges.Count; i++)
            {
                if (Edges[i] != null && Edges[i].IsDoubleTrackMainline && Edges[i].ParallelEdge != null)
                    totalPairedCorridorEdges++;
            }

            Log(string.Format("[RailGraph] Double-track corridor detection complete: {0} geometric edges, {1} split-to-merge continuation edges ({2} total active corridor edges).",
                geomPairedCount, splitMergePairedCount, totalPairedCorridorEdges));
        }

        private static bool IsShortSwitchTrackEdge(RailEdge edge)
        {
            if (edge == null) return false;
            if (edge.IsJunctionTrack) return true;
            string tName = edge.TrackName;
            return !string.IsNullOrEmpty(tName) && tName.StartsWith("[track ", StringComparison.OrdinalIgnoreCase);
        }

        private void PropagateColinearCorridorLeads(float maxLongitudinalStagger, float maxParallelDistance)
        {
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < Edges.Count; i++)
                {
                    var pairedEdge = Edges[i];
                    if (pairedEdge == null || !pairedEdge.IsDoubleTrackMainline || pairedEdge.ParallelEdge == null)
                        continue;
                    if (IsShortSwitchTrackEdge(pairedEdge) || IsShortSwitchTrackEdge(pairedEdge.ParallelEdge))
                        continue;

                    RailNode[] endpoints = new RailNode[] { pairedEdge.FromNode, pairedEdge.ToNode };
                    for (int n = 0; n < 2; n++)
                    {
                        RailNode sharedNode = endpoints[n];
                        if (sharedNode == null || sharedNode.IncidentEdges == null || sharedNode.IncidentEdges.Count < 2)
                            continue;

                        RailEdge leadEdge = null;
                        bool isDegree2 = (!sharedNode.IsJunction && sharedNode.IncidentEdges.Count == 2);

                        if (isDegree2)
                        {
                            leadEdge = (sharedNode.IncidentEdges[0] == pairedEdge) ? sharedNode.IncidentEdges[1] : sharedNode.IncidentEdges[0];
                        }
                        else
                        {
                            // Junction-bounded crossover/throat lead: find the unique colinear continuation edge across sharedNode
                            Vector3 arriveDir = -pairedEdge.GetDirection(sharedNode);
                            arriveDir.y = 0f;
                            if (arriveDir.sqrMagnitude > 0.0001f) arriveDir.Normalize();

                            Vector3 arriveLookahead = -pairedEdge.GetLookaheadDirection(sharedNode);
                            arriveLookahead.y = 0f;
                            if (arriveLookahead.sqrMagnitude > 0.0001f) arriveLookahead.Normalize();

                            float bestColinearDot = 0.95f;
                            for (int b = 0; b < sharedNode.IncidentEdges.Count; b++)
                            {
                                var candEdge = sharedNode.IncidentEdges[b];
                                if (candEdge == null || candEdge == pairedEdge || candEdge.ParallelEdge != null)
                                    continue;

                                string cName = candEdge.TrackDisplayName;
                                if (IsCrossoverTrackName(cName))
                                    continue;

                                Vector3 departDir = candEdge.GetDirection(sharedNode);
                                departDir.y = 0f;
                                if (departDir.sqrMagnitude > 0.0001f) departDir.Normalize();

                                Vector3 departLookahead = candEdge.GetLookaheadDirection(sharedNode);
                                departLookahead.y = 0f;
                                if (departLookahead.sqrMagnitude > 0.0001f) departLookahead.Normalize();

                                float originDot = Vector3.Dot(arriveDir, departDir);
                                float lookaheadDot = Vector3.Dot(arriveLookahead, departLookahead);
                                if (originDot >= bestColinearDot && lookaheadDot >= 0.90f)
                                {
                                    bestColinearDot = originDot;
                                    leadEdge = candEdge;
                                }
                            }
                        }

                        if (leadEdge == null || leadEdge.ParallelEdge != null || leadEdge.IsStrictlyAvoidedThroughTrack || leadEdge.Length > 150f)
                            continue;
                        if (Pathfinder.IsIndustrialYardOrStorageTrack(leadEdge.Track))
                            continue;

                        bool rightFlowsIntoShared = (pairedEdge.ToNode == sharedNode) ? pairedEdge.PreferredForward : !pairedEdge.PreferredForward;
                        RailNode farNode = leadEdge.GetOtherNode(sharedNode);

                        // If leadEdge is a junction switch track ([track through] / [track diverging]), ensure its far end
                        // does not connect directly into the opposing parallel track (crossover) or conflict with another corridor's handedness.
                        if (IsShortSwitchTrackEdge(leadEdge) && farNode != null && farNode.IncidentEdges != null)
                        {
                            bool isCrossoverOrConflicting = false;
                            Vector3 leadArriveFar = -leadEdge.GetDirection(farNode);
                            leadArriveFar.y = 0f;
                            if (leadArriveFar.sqrMagnitude > 0.0001f) leadArriveFar.Normalize();

                            for (int f = 0; f < farNode.IncidentEdges.Count; f++)
                            {
                                RailEdge farEdge = farNode.IncidentEdges[f];
                                if (farEdge == null || farEdge == leadEdge) continue;

                                if (farEdge == pairedEdge.ParallelEdge || farEdge.ParallelEdge == pairedEdge)
                                {
                                    isCrossoverOrConflicting = true;
                                    break;
                                }

                                if (farEdge.IsDoubleTrackMainline && farEdge.ParallelEdge != null)
                                {
                                    Vector3 farDepart = farEdge.GetDirection(farNode);
                                    farDepart.y = 0f;
                                    if (farDepart.sqrMagnitude > 0.0001f) farDepart.Normalize();
                                    if (Vector3.Dot(leadArriveFar, farDepart) > 0.5f)
                                    {
                                        bool farRightFlowsOut = (farEdge.FromNode == farNode) ? farEdge.PreferredForward : !farEdge.PreferredForward;
                                        if (farRightFlowsOut != rightFlowsIntoShared)
                                        {
                                            isCrossoverOrConflicting = true;
                                            break;
                                        }
                                    }
                                }
                            }

                            if (isCrossoverOrConflicting)
                                continue;
                        }

                        bool connectsToTurnout = (farNode != null && (farNode.IsJunction || (farNode.IncidentEdges != null && farNode.IncidentEdges.Count > 2)));
                        RailEdge oppEdge = pairedEdge.ParallelEdge;
                        bool nearParallelCorridor = false;

                        if (oppEdge != null && oppEdge.Track != null && oppEdge.Track.curve != null)
                        {
                            Vector3 leadMid = leadEdge.GetMidPoint();
                            float spanOpp = oppEdge.Length * 0.5f;
                            float bestDistSq = float.MaxValue;
                            for (int s = 1; s <= 7; s++)
                            {
                                float testSpan = oppEdge.Length * (s * 0.125f);
                                float dSq = (oppEdge.GetPointAtSpan(testSpan) - leadMid).sqrMagnitude;
                                if (dSq < bestDistSq)
                                {
                                    bestDistSq = dSq;
                                    spanOpp = testSpan;
                                }
                            }
                            for (int iter = 0; iter < 2; iter++)
                            {
                                Vector3 pOpp = oppEdge.GetPointAtSpan(spanOpp);
                                Vector3 tOpp = oppEdge.GetTangentAtSpan(spanOpp);
                                spanOpp = Mathf.Clamp(spanOpp + Vector3.Dot(leadMid - pOpp, tOpp), 0f, oppEdge.Length);
                            }
                            Vector3 refOpp = oppEdge.GetPointAtSpan(spanOpp);
                            Vector3 tanOpp = oppEdge.GetTangentAtSpan(spanOpp);
                            Vector3 delta = refOpp - leadMid;
                            float stagger = Mathf.Abs(Vector3.Dot(delta, tanOpp));
                            float hDist = new Vector2(delta.x, delta.z).magnitude;
                            if (stagger <= maxLongitudinalStagger && hDist >= 1.0f && hDist <= maxParallelDistance)
                            {
                                nearParallelCorridor = true;
                            }
                            else if (isDegree2 && Vector3.Distance(leadMid, oppEdge.GetMidPoint()) <= (oppEdge.Length * 0.5f + 25f))
                            {
                                nearParallelCorridor = true;
                            }
                        }

                        if ((isDegree2 && (connectsToTurnout || nearParallelCorridor)) || (!isDegree2 && nearParallelCorridor))
                        {
                            leadEdge.IsDoubleTrackMainline = true;
                            leadEdge.ParallelEdge = pairedEdge.ParallelEdge;
                            leadEdge.PreferredForward = (leadEdge.FromNode == sharedNode) ? rightFlowsIntoShared : !rightFlowsIntoShared;
                        }
                    }
                }
            }
        }

        private static bool IsCrossoverTrackName(string tName)
        {
            if (string.IsNullOrEmpty(tName)) return false;
            return tName.IndexOf("CX", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tName.IndexOf("SX", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (tName.IndexOf("DTC-", StringComparison.OrdinalIgnoreCase) >= 0 && tName.IndexOf("[track through]", StringComparison.OrdinalIgnoreCase) < 0) ||
                   tName.IndexOf("Cross", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tName.IndexOf("Xing", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // CRITICAL INVARIANT [RGR-07]: Bounded Split-to-Merge Twin Corridor Continuation.
        // Pairs diverging/reconverging through-track chains ONLY when |lenRight - lenLeft| <= 500.0f
        // (maxHops <= 8, maxBranchLength <= 3000.0f). Never pairs long detours (> 500.0f delta).
        private int PropagateBoundedSplitToMergeCorridors()
        {
            const int maxChainHops = 8;
            const float maxChainLength = 3000.0f;
            const float maxLengthDelta = 500.0f;

            int newlyPairedCount = 0;

            for (int i = 0; i < Edges.Count; i++)
            {
                RailEdge rightSeed = Edges[i];
                if (rightSeed == null || !rightSeed.IsDoubleTrackMainline || rightSeed.ParallelEdge == null)
                    continue;
                if (rightSeed.IsIndustrialYardOrStorageTrack || IsShortSwitchTrackEdge(rightSeed))
                    continue;

                RailEdge leftSeed = rightSeed.ParallelEdge;
                if (leftSeed == null || leftSeed.IsIndustrialYardOrStorageTrack || IsShortSwitchTrackEdge(leftSeed))
                    continue;

                // Evaluate in the direction where rightSeed is the RIGHT-HAND track
                RailNode rightExitNode = rightSeed.PreferredForward ? rightSeed.ToNode : rightSeed.FromNode;
                Vector3 rightTravelDir = -rightSeed.GetDirection(rightExitNode);

                // Determine the corresponding downstream exit node on leftSeed in the same world travel direction
                Vector3 leftFromToDir = -leftSeed.GetDirection(leftSeed.ToNode);
                bool leftTravelsForward = Vector3.Dot(rightTravelDir, leftFromToDir) >= 0f;
                RailNode leftExitNode = leftTravelsForward ? leftSeed.ToNode : leftSeed.FromNode;

                if (rightExitNode == null || leftExitNode == null || rightExitNode == leftExitNode)
                    continue;

                List<RailEdge> rightChain = new List<RailEdge>();
                List<bool> rightChainForward = new List<bool>();
                List<RailNode> rightNodes = new List<RailNode>();
                TraceContinuationChain(rightSeed, rightExitNode, maxChainHops, maxChainLength, rightChain, rightChainForward, rightNodes);

                List<RailEdge> leftChain = new List<RailEdge>();
                List<bool> leftChainForward = new List<bool>();
                List<RailNode> leftNodes = new List<RailNode>();
                TraceContinuationChain(leftSeed, leftExitNode, maxChainHops, maxChainLength, leftChain, leftChainForward, leftNodes);

                if (rightChain.Count == 0 && leftChain.Count == 0)
                    continue;

                // Find earliest reconvergence (either shared downstream node OR hitting an already-paired corridor pair)
                int matchR = -1;
                int matchL = -1;
                for (int r = 0; r < rightNodes.Count && matchR < 0; r++)
                {
                    RailNode rNode = rightNodes[r];
                    for (int l = 0; l < leftNodes.Count; l++)
                    {
                        RailNode lNode = leftNodes[l];
                        if (rNode == lNode)
                        {
                            matchR = r;
                            matchL = l;
                            break;
                        }
                        if (rightChain[r].IsDoubleTrackMainline && rightChain[r].ParallelEdge == leftChain[l])
                        {
                            matchR = r;
                            matchL = l;
                            break;
                        }
                    }
                }

                if (matchR < 0 || matchL < 0)
                    continue;

                // Calculate total chain lengths from seed exit to reconvergence
                // Include seed portion if one seed already spanned past the other's exit node
                float lenRight = 0f;
                for (int r = 0; r <= matchR; r++) lenRight += rightChain[r].Length;

                float lenLeft = 0f;
                for (int l = 0; l <= matchL; l++) lenLeft += leftChain[l].Length;

                // Account for longitudinal offset between rightExitNode and leftExitNode along rightTravelDir
                float exitOffset = Vector3.Dot(leftExitNode.Position - rightExitNode.Position, rightTravelDir);
                if (exitOffset > 0f)
                    lenLeft += exitOffset;
                else
                    lenRight += (-exitOffset);

                if (lenRight > maxChainLength || lenLeft > maxChainLength)
                    continue;

                // STRICT 500m GUARDRAIL: Never pair chains whose total length difference exceeds 500.0f meters!
                if (Mathf.Abs(lenRight - lenLeft) > maxLengthDelta)
                    continue;

                RailEdge repRight = rightSeed;
                for (int r = 0; r <= matchR; r++)
                {
                    if (!IsShortSwitchTrackEdge(rightChain[r]))
                    {
                        repRight = rightChain[r];
                        break;
                    }
                }

                RailEdge repLeft = leftSeed;
                for (int l = 0; l <= matchL; l++)
                {
                    if (!IsShortSwitchTrackEdge(leftChain[l]))
                    {
                        repLeft = leftChain[l];
                        break;
                    }
                }

                for (int r = 0; r <= matchR; r++)
                {
                    RailEdge eR = rightChain[r];
                    bool fwdR = rightChainForward[r];
                    if (!eR.IsDoubleTrackMainline || eR.ParallelEdge == null)
                    {
                        eR.IsDoubleTrackMainline = true;
                        eR.ParallelEdge = repLeft;
                        eR.PreferredForward = fwdR; // Traveling along chain (split -> merge) is RIGHT-HAND preferred
                        newlyPairedCount++;
                    }
                }

                for (int l = 0; l <= matchL; l++)
                {
                    RailEdge eL = leftChain[l];
                    bool fwdL = leftChainForward[l];
                    if (!eL.IsDoubleTrackMainline || eL.ParallelEdge == null)
                    {
                        eL.IsDoubleTrackMainline = true;
                        eL.ParallelEdge = repRight;
                        eL.PreferredForward = !fwdL; // Traveling along chain (split -> merge) is LEFT-HAND penalized
                        newlyPairedCount++;
                    }
                }
            }

            // CRITICAL INVARIANT [RGR-07]: Single-Node Turnout Split-to-Merge Twin Corridor Continuation (<= 500.0f Length Delta Cap).
            // Pairs diverging turnout branches (e.g. W-0486 Branch 0 vs Branch 1 at CSW) that split from a single-track turnout node
            // and reconverge at a shared downstream node within maxChainHops (8), maxChainLength (3000m), and maxLengthDelta (500m).
            for (int n = 0; n < Nodes.Count; n++)
            {
                RailNode splitNode = Nodes[n];
                if (splitNode == null || splitNode.IncidentEdges == null || splitNode.IncidentEdges.Count <= 2)
                    continue;

                for (int inIdx = 0; inIdx < splitNode.IncidentEdges.Count; inIdx++)
                {
                    RailEdge inEdge = splitNode.IncidentEdges[inIdx];
                    if (inEdge == null || inEdge.IsStrictlyAvoidedThroughTrack || inEdge.IsIndustrialYardOrStorageTrack)
                        continue;

                    // Do not treat a turnout on an already-paired double-track mainline as a single-to-double split!
                    if (inEdge.IsDoubleTrackMainline)
                        continue;

                    Vector3 inDir = -inEdge.GetDirection(splitNode);
                    inDir.y = 0f;
                    if (inDir.sqrMagnitude < 0.0001f)
                        continue;
                    inDir.Normalize();
                    Vector3 rightNormal = Vector3.Cross(Vector3.up, inDir).normalized;

                    RailEdge rightStart = null;
                    RailEdge leftStart = null;
                    float maxLat = float.MinValue;
                    float minLat = float.MaxValue;
                    int validBranches = 0;

                    for (int b = 0; b < splitNode.IncidentEdges.Count; b++)
                    {
                        RailEdge cand = splitNode.IncidentEdges[b];
                        if (cand == null || cand == inEdge || cand.IsStrictlyAvoidedThroughTrack || cand.IsIndustrialYardOrStorageTrack)
                            continue;

                        Vector3 origDir = cand.GetDirection(splitNode);
                        origDir.y = 0f;
                        if (origDir.sqrMagnitude < 0.0001f)
                            continue;
                        origDir.Normalize();
                        if (Vector3.Dot(inDir, origDir) <= 0.0f)
                            continue;

                        Vector3 lookDir = cand.GetLookaheadDirection(splitNode);
                        lookDir.y = 0f;
                        if (lookDir.sqrMagnitude < 0.0001f)
                            continue;
                        lookDir.Normalize();

                        float lat = Vector3.Dot(lookDir, rightNormal);
                        validBranches++;
                        if (lat > maxLat)
                        {
                            maxLat = lat;
                            rightStart = cand;
                        }
                        if (lat < minLat)
                        {
                            minLat = lat;
                            leftStart = cand;
                        }
                    }

                    if (validBranches < 2 || rightStart == null || leftStart == null || rightStart == leftStart || (maxLat - minLat <= 0.01f))
                        continue;

                    RailNode rightFirstNode = rightStart.GetOtherNode(splitNode);
                    RailNode leftFirstNode = leftStart.GetOtherNode(splitNode);
                    if (rightFirstNode == null || leftFirstNode == null || rightFirstNode == leftFirstNode)
                        continue;

                    List<RailEdge> rightChain = new List<RailEdge> { rightStart };
                    List<bool> rightChainForward = new List<bool> { rightStart.FromNode == splitNode };
                    List<RailNode> rightNodes = new List<RailNode> { rightFirstNode };
                    TraceContinuationChain(rightStart, rightFirstNode, maxChainHops - 1, maxChainLength - rightStart.Length, rightChain, rightChainForward, rightNodes);

                    List<RailEdge> leftChain = new List<RailEdge> { leftStart };
                    List<bool> leftChainForward = new List<bool> { leftStart.FromNode == splitNode };
                    List<RailNode> leftNodes = new List<RailNode> { leftFirstNode };
                    TraceContinuationChain(leftStart, leftFirstNode, maxChainHops - 1, maxChainLength - leftStart.Length, leftChain, leftChainForward, leftNodes);

                    int matchR = -1;
                    int matchL = -1;
                    for (int r = 0; r < rightNodes.Count && matchR < 0; r++)
                    {
                        RailNode rNode = rightNodes[r];
                        for (int l = 0; l < leftNodes.Count; l++)
                        {
                            if (rNode == leftNodes[l])
                            {
                                matchR = r;
                                matchL = l;
                                break;
                            }
                        }
                    }

                    if (matchR < 0 || matchL < 0)
                        continue;

                    // Verify neither chain is merely merging into an already-paired double-track mainline,
                    // and select non-switch representative edges for both chains.
                    bool touchesExistingCorridor = false;
                    RailEdge repRight = null;
                    RailEdge repLeft = null;

                    float lenRight = 0f;
                    for (int r = 0; r <= matchR; r++)
                    {
                        lenRight += rightChain[r].Length;
                        if (rightChain[r].IsDoubleTrackMainline) touchesExistingCorridor = true;
                        if (repRight == null && !IsShortSwitchTrackEdge(rightChain[r])) repRight = rightChain[r];
                    }

                    float lenLeft = 0f;
                    for (int l = 0; l <= matchL; l++)
                    {
                        lenLeft += leftChain[l].Length;
                        if (leftChain[l].IsDoubleTrackMainline) touchesExistingCorridor = true;
                        if (repLeft == null && !IsShortSwitchTrackEdge(leftChain[l])) repLeft = leftChain[l];
                    }

                    if (touchesExistingCorridor || repRight == null || repLeft == null)
                        continue;

                    if (lenRight > maxChainLength || lenLeft > maxChainLength)
                        continue;

                    // STRICT 500m GUARDRAIL: Never pair chains whose total length difference exceeds 500.0f meters!
                    if (Mathf.Abs(lenRight - lenLeft) > maxLengthDelta)
                        continue;

                    for (int r = 0; r <= matchR; r++)
                    {
                        RailEdge eR = rightChain[r];
                        bool fwdR = rightChainForward[r];
                        if (!eR.IsDoubleTrackMainline || eR.ParallelEdge == null)
                        {
                            eR.IsDoubleTrackMainline = true;
                            eR.ParallelEdge = repLeft;
                            eR.PreferredForward = fwdR;
                            newlyPairedCount++;
                        }
                    }

                    for (int l = 0; l <= matchL; l++)
                    {
                        RailEdge eL = leftChain[l];
                        bool fwdL = leftChainForward[l];
                        if (!eL.IsDoubleTrackMainline || eL.ParallelEdge == null)
                        {
                            eL.IsDoubleTrackMainline = true;
                            eL.ParallelEdge = repRight;
                            eL.PreferredForward = !fwdL;
                            newlyPairedCount++;
                        }
                    }
                }
            }

            return newlyPairedCount;
        }

        private void TraceContinuationChain(
            RailEdge startEdge,
            RailNode startExitNode,
            int maxHops,
            float maxLength,
            List<RailEdge> outEdges,
            List<bool> outForwardFlags,
            List<RailNode> outExitNodes)
        {
            RailEdge currentEdge = startEdge;
            RailNode currentNode = startExitNode;
            float accumulatedLength = 0f;

            for (int hop = 0; hop < maxHops; hop++)
            {
                if (currentNode == null || currentNode.IncidentEdges == null) break;

                Vector3 inDir = -currentEdge.GetDirection(currentNode);
                RailEdge bestNext = null;
                float bestAlign = 0.80f; // Require smooth forward continuation

                for (int idx = 0; idx < currentNode.IncidentEdges.Count; idx++)
                {
                    RailEdge cand = currentNode.IncidentEdges[idx];
                    if (cand == null || cand == currentEdge) continue;
                    if (cand.IsIndustrialYardOrStorageTrack) continue;

                    string cName = cand.TrackDisplayName;
                    if (IsCrossoverTrackName(cName)) continue;

                    Vector3 outDir = cand.GetDirection(currentNode);
                    float align = Vector3.Dot(inDir, outDir);
                    if (align > bestAlign)
                    {
                        // Prefer already-paired corridor edges or non-diverging through continuations
                        bestAlign = align;
                        bestNext = cand;
                    }
                }

                if (bestNext == null) break;
                if (accumulatedLength + bestNext.Length > maxLength) break;

                bool traversedForward = (bestNext.FromNode == currentNode);
                RailNode nextNode = bestNext.GetOtherNode(currentNode);
                if (nextNode == null || outEdges.Contains(bestNext)) break;

                accumulatedLength += bestNext.Length;
                outEdges.Add(bestNext);
                outForwardFlags.Add(traversedForward);
                outExitNodes.Add(nextNode);

                currentEdge = bestNext;
                currentNode = nextNode;
            }
        }

        public RailEdge GetEdge(RailTrack track)
        {
            if (track == null) return null;
            RailEdge edge;
            _trackToEdge.TryGetValue(track, out edge);
            return edge;
        }

        public RailTrack GetTrackByName(string trackName)
        {
            if (string.IsNullOrEmpty(trackName)) return null;
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge != null && edge.Track != null && string.Equals(edge.Track.name, trackName, StringComparison.OrdinalIgnoreCase))
                {
                    return edge.Track;
                }
            }
            return null;
        }

        public RailNode GetNode(Junction junction)
        {
            if (junction == null) return null;
            RailNode node;
            _junctionToNode.TryGetValue(junction, out node);
            return node;
        }

        public RailNode GetInNode(RailTrack track)
        {
            var edge = GetEdge(track);
            return edge != null ? edge.FromNode : null;
        }

        public RailNode GetOutNode(RailTrack track)
        {
            var edge = GetEdge(track);
            return edge != null ? edge.ToNode : null;
        }

        public RailEdge GetClosestEdge(Vector3 position, out float minDistance)
        {
            float minSqrDistance = float.MaxValue;
            RailEdge bestEdge = null;

            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                Vector3 mid = edge.GetMidPoint();
                float sqrD = (position - mid).sqrMagnitude;
                if (sqrD < minSqrDistance)
                {
                    minSqrDistance = sqrD;
                    bestEdge = edge;
                }
            }

            minDistance = minSqrDistance < float.MaxValue ? Mathf.Sqrt(minSqrDistance) : float.MaxValue;
            return bestEdge;
        }

        public RailNode GetClosestNode(Vector3 position, out float minDistance)
        {
            float minSqrDist = float.MaxValue;
            RailNode bestNode = null;

            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                float dSqr = (position - node.Position).sqrMagnitude;
                if (dSqr < minSqrDist)
                {
                    minSqrDist = dSqr;
                    bestNode = node;
                }
            }

            minDistance = minSqrDist < float.MaxValue ? Mathf.Sqrt(minSqrDist) : float.MaxValue;
            return bestNode;
        }

        /// <summary>
        /// Calculates the forward direction vector of a train arriving at currentNode via incomingEdge.
        /// </summary>
        public static Vector3 GetIncomingVector(RailNode currentNode, RailEdge incomingEdge)
        {
            if (incomingEdge == null) return Vector3.forward;

            if (currentNode == incomingEdge.ToNode)
            {
                if (incomingEdge.ToTangent != Vector3.zero) return incomingEdge.ToTangent;
                if (incomingEdge.Track != null && incomingEdge.Track.curve != null && incomingEdge.Track.curve.pointCount >= 2)
                    return incomingEdge.Track.curve.GetTangentAt(1.0f).normalized;
            }
            if (currentNode == incomingEdge.FromNode)
            {
                if (incomingEdge.FromTangent != Vector3.zero) return -incomingEdge.FromTangent;
                if (incomingEdge.Track != null && incomingEdge.Track.curve != null && incomingEdge.Track.curve.pointCount >= 2)
                    return -incomingEdge.Track.curve.GetTangentAt(0.0f).normalized;
            }

            if (incomingEdge.Track != null && incomingEdge.Track.curve != null && incomingEdge.Track.curve.pointCount >= 2)
            {
                Vector3 p0 = incomingEdge.Track.curve[0].position;
                Vector3 p1 = incomingEdge.Track.curve.Last().position;
                if (Vector3.Distance(p1, currentNode.Position) <= Vector3.Distance(p0, currentNode.Position))
                {
                    return incomingEdge.Track.curve.GetTangentAt(1.0f).normalized;
                }
                return -incomingEdge.Track.curve.GetTangentAt(0.0f).normalized;
            }

            return Vector3.forward;
        }

        /// <summary>
        /// Calculates the departing direction vector of a train leaving currentNode along candidateEdge.
        /// </summary>
        public static Vector3 GetDepartingVector(RailNode currentNode, RailEdge candidateEdge)
        {
            if (candidateEdge == null) return Vector3.forward;

            if (currentNode == candidateEdge.FromNode)
            {
                if (candidateEdge.FromTangent != Vector3.zero) return candidateEdge.FromTangent;
                if (candidateEdge.Track != null && candidateEdge.Track.curve != null && candidateEdge.Track.curve.pointCount >= 2)
                    return candidateEdge.Track.curve.GetTangentAt(0.0f).normalized;
            }
            if (currentNode == candidateEdge.ToNode)
            {
                if (candidateEdge.ToTangent != Vector3.zero) return -candidateEdge.ToTangent;
                if (candidateEdge.Track != null && candidateEdge.Track.curve != null && candidateEdge.Track.curve.pointCount >= 2)
                    return -candidateEdge.Track.curve.GetTangentAt(1.0f).normalized;
            }

            if (candidateEdge.Track != null && candidateEdge.Track.curve != null && candidateEdge.Track.curve.pointCount >= 2)
            {
                Vector3 p0 = candidateEdge.Track.curve[0].position;
                Vector3 p1 = candidateEdge.Track.curve.Last().position;
                if (Vector3.Distance(p0, currentNode.Position) <= Vector3.Distance(p1, currentNode.Position))
                {
                    return candidateEdge.Track.curve.GetTangentAt(0.0f).normalized;
                }
                return -candidateEdge.Track.curve.GetTangentAt(1.0f).normalized;
            }

            return Vector3.forward;
        }

        private static readonly RailEdge[] s_emptyEdges = new RailEdge[0];
        private readonly Dictionary<long, RailEdge[]> _traversableEdgesCache = new Dictionary<long, RailEdge[]>();

        private static long GetTraversableKey(int nodeId, int incomingEdgeId)
        {
            return ((long)nodeId << 32) | (uint)incomingEdgeId;
        }

        public void PrecomputeTraversableEdges()
        {
            _traversableEdgesCache.Clear();
            if (Nodes == null) return;
            for (int n = 0; n < Nodes.Count; n++)
            {
                var node = Nodes[n];
                if (node == null) continue;

                // 1. Unconstrained
                var unconstrained = ComputeTraversableEdgesInternal(node, null);
                _traversableEdgesCache[GetTraversableKey(node.Id, 0)] = unconstrained.ToArray();

                // 2. Constrained by each incident edge
                if (node.IncidentEdges != null)
                {
                    for (int e = 0; e < node.IncidentEdges.Count; e++)
                    {
                        var inEdge = node.IncidentEdges[e];
                        if (inEdge == null) continue;
                        var traversable = ComputeTraversableEdgesInternal(node, inEdge);
                        _traversableEdgesCache[GetTraversableKey(node.Id, inEdge.Id)] = traversable.ToArray();
                    }
                }
            }
        }

        /// <summary>
        /// Returns all traversable outgoing edges from currentNode when arriving via incomingEdge,
        /// respecting junction geometry, switch constraints, and vector angular continuity.
        /// Fully thread-safe using a precomputed array lookup cache.
        /// </summary>
        public RailEdge[] GetTraversableEdges(RailNode currentNode, RailEdge incomingEdge)
        {
            if (currentNode == null) return s_emptyEdges;
            long key = GetTraversableKey(currentNode.Id, incomingEdge != null ? incomingEdge.Id : 0);
            RailEdge[] cached;
            // CRITICAL INVARIANT [RGR-09]: _traversableEdgesCache is read concurrently by background A* threads and the Main Thread;
            // reads MUST take the same lock as writes (Dictionary is not safe for concurrent read + write).
            lock (_traversableEdgesCache)
            {
                if (_traversableEdgesCache.TryGetValue(key, out cached) && cached != null)
                {
                    return cached;
                }
            }

            var list = ComputeTraversableEdgesInternal(currentNode, incomingEdge);
            cached = list.ToArray();
            lock (_traversableEdgesCache)
            {
                _traversableEdgesCache[key] = cached;
            }
            return cached;
        }

        private List<RailEdge> ComputeTraversableEdgesInternal(RailNode currentNode, RailEdge incomingEdge)
        {
            var result = new List<RailEdge>();
            if (currentNode == null) return result;

            if (incomingEdge == null)
            {
                result.AddRange(currentNode.IncidentEdges);
                return result;
            }

            var junction = currentNode.Junction;
            bool isTrailingFromOutBranch = false;
            if (junction != null && junction.outBranches != null)
            {
                for (int b = 0; b < junction.outBranches.Count; b++)
                {
                    if (junction.outBranches[b] != null && junction.outBranches[b].track == incomingEdge.Track)
                    {
                        isTrailingFromOutBranch = true;
                        break;
                    }
                }
            }

            var track = incomingEdge.Track;
            if (track != null)
            {
                try
                {
                    List<Junction.Branch> branches = null;
                    if (currentNode == incomingEdge.FromNode)
                    {
                        branches = track.GetAllInBranches();
                    }
                    else if (currentNode == incomingEdge.ToNode)
                    {
                        branches = track.GetAllOutBranches();
                    }

                    if (branches != null && branches.Count > 0)
                    {
                        for (int i = 0; i < branches.Count; i++)
                        {
                            var branch = branches[i];
                            if (branch != null && branch.track != null)
                            {
                                var edge = GetEdge(branch.track);
                                if (edge != null && edge != incomingEdge && !result.Contains(edge))
                                {
                                    result.Add(edge);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Fall back to graph heuristics if DV track query fails
                }
            }

            // Fallback 1: Use Junction heuristics if result is still empty
            if (result.Count == 0 && junction != null)
            {
                // Facing move: entering from inBranch -> can exit onto any outBranch
                if (junction.inBranch != null && junction.inBranch.track == incomingEdge.Track)
                {
                    if (junction.outBranches != null)
                    {
                        for (int i = 0; i < junction.outBranches.Count; i++)
                        {
                            var branch = junction.outBranches[i];
                            if (branch != null && branch.track != null)
                            {
                                var edge = GetEdge(branch.track);
                                if (edge != null && edge != incomingEdge && !result.Contains(edge))
                                {
                                    result.Add(edge);
                                }
                            }
                        }
                    }
                }
                // Trailing move: entering from an outBranch -> can only exit onto inBranch
                else if (isTrailingFromOutBranch && junction.inBranch != null && junction.inBranch.track != null)
                {
                    var inEdge = GetEdge(junction.inBranch.track);
                    if (inEdge != null && inEdge != incomingEdge && !result.Contains(inEdge))
                    {
                        result.Add(inEdge);
                    }
                }
            }

            // IncidentEdges merge: Ensure merged/modded nodes (e.g. DoubleTrack crossover joints at Node 109 and Node 110)
            // never sever continuation edges when GetAllInBranches/GetAllOutBranches returns incomplete branches.
            if (currentNode.IncidentEdges != null)
            {
                bool incomingInJunction = false;
                if (junction != null)
                {
                    if (junction.inBranch != null && junction.inBranch.track == incomingEdge.Track)
                    {
                        incomingInJunction = true;
                    }
                    else if (junction.outBranches != null)
                    {
                        for (int b = 0; b < junction.outBranches.Count; b++)
                        {
                            if (junction.outBranches[b] != null && junction.outBranches[b].track == incomingEdge.Track)
                            {
                                incomingInJunction = true;
                                break;
                            }
                        }
                    }
                }

                for (int i = 0; i < currentNode.IncidentEdges.Count; i++)
                {
                    var edge = currentNode.IncidentEdges[i];
                    if (edge == null || edge == incomingEdge || result.Contains(edge))
                        continue;

                    bool edgeInJunction = false;
                    if (junction != null)
                    {
                        if (junction.inBranch != null && junction.inBranch.track == edge.Track)
                        {
                            edgeInJunction = true;
                        }
                        else if (junction.outBranches != null)
                        {
                            for (int b = 0; b < junction.outBranches.Count; b++)
                            {
                                if (junction.outBranches[b] != null && junction.outBranches[b].track == edge.Track)
                                {
                                    edgeInJunction = true;
                                    break;
                                }
                            }
                        }
                    }

                    // Merge if:
                    // 1. Node has no junction (e.g. Node 109)
                    // 2. edge.Track is not part of junction inBranch/outBranches (modded/merged track)
                    // 3. incomingEdge.Track is not part of junction inBranch/outBranches (incoming from modded/merged track, e.g. Node 110)
                    // 4. result is still empty (fallback safety net)
                    if (junction == null || !edgeInJunction || !incomingInJunction || result.Count == 0)
                    {
                        result.Add(edge);
                    }
                }
            }

            // Angular Vector Continuity & Trailing Switch Filter:
            // Eliminate impossible 180-degree hairpin U-turns, reverse forks, and trailing outBranch crossovers.
            if (result.Count > 0)
            {
                Vector3 inDir = GetIncomingVector(currentNode, incomingEdge);
                for (int i = result.Count - 1; i >= 0; i--)
                {
                    var candidate = result[i];
                    if (candidate == null || candidate == incomingEdge)
                    {
                        result.RemoveAt(i);
                        continue;
                    }

                    // Trailing move rule: cannot exit onto another outBranch of the same junction
                    if (isTrailingFromOutBranch && junction != null && junction.outBranches != null)
                    {
                        bool isAnotherOutBranch = false;
                        for (int b = 0; b < junction.outBranches.Count; b++)
                        {
                            if (junction.outBranches[b] != null && junction.outBranches[b].track == candidate.Track)
                            {
                                isAnotherOutBranch = true;
                                break;
                            }
                        }
                        if (isAnotherOutBranch)
                        {
                            result.RemoveAt(i);
                            continue;
                        }
                    }

                    // Vector angular continuity: reject any turn sharper than ~78 degrees (Dot < 0.20f)
                    Vector3 outDir = GetDepartingVector(currentNode, candidate);
                    float dot = Vector3.Dot(inDir, outDir);
                    if (dot < 0.20f)
                    {
                        result.RemoveAt(i);
                    }
                }
            }

            return result;
        }

        public byte GetRequiredBranch(RailNode junctionNode, RailEdge incomingEdge, RailEdge outgoingEdge)
        {
            if (outgoingEdge == null)
                return 0;

            var junction = junctionNode != null ? junctionNode.Junction : null;
            if (junction == null && incomingEdge != null && incomingEdge.Track != null)
            {
                junction = incomingEdge.Track.inJunction ?? incomingEdge.Track.outJunction;
            }
            if (junction == null && outgoingEdge.Track != null)
            {
                junction = outgoingEdge.Track.inJunction ?? outgoingEdge.Track.outJunction;
            }
            if (junction == null)
                return 0;

            // Facing move: selecting which outBranch to diverge to
            if (junction.inBranch != null && incomingEdge != null && junction.inBranch.track == incomingEdge.Track)
            {
                if (junction.outBranches != null)
                {
                    for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                    {
                        var branch = junction.outBranches[i];
                        if (branch != null && branch.track == outgoingEdge.Track)
                        {
                            return i;
                        }
                    }
                }
            }

            // Trailing move: selecting incoming branch so points align properly
            if (junction.outBranches != null && incomingEdge != null)
            {
                for (byte i = 0; i < (byte)junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track == incomingEdge.Track)
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        public RailNode GetConnectingNode(RailEdge edgeA, RailEdge edgeB)
        {
            if (edgeA == null || edgeB == null) return null;
            if (edgeA.FromNode != null && (edgeA.FromNode == edgeB.FromNode || edgeA.FromNode == edgeB.ToNode)) return edgeA.FromNode;
            if (edgeA.ToNode != null && (edgeA.ToNode == edgeB.FromNode || edgeA.ToNode == edgeB.ToNode)) return edgeA.ToNode;
            return null;
        }

        #region Reservations & Occupancy

        /// <summary>
        /// Builds a snapshot set of all tracks physically occupied by cars in the world,
        /// optionally ignoring cars belonging to ignoreTrainset.
        /// Call once before a batch of pathfinding/occupancy queries to achieve O(1) checks.
        /// </summary>
        public static HashSet<RailTrack> BuildOccupiedTracksSnapshot(Trainset ignoreTrainset = null)
        {
            var occupied = new HashSet<RailTrack>();
            try
            {
                if (CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
                {
                    var allCars = CarSpawner.Instance.AllCars;
                    int count = allCars.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var car = allCars[i];
                        if (car == null) continue;
                        if (ignoreTrainset != null && (car.trainset == ignoreTrainset || (ignoreTrainset.cars != null && ignoreTrainset.cars.Contains(car))))
                            continue;

                        if (car.FrontBogie != null && car.FrontBogie.track != null)
                            occupied.Add(car.FrontBogie.track);
                        if (car.RearBogie != null && car.RearBogie.track != null)
                            occupied.Add(car.RearBogie.track);
                    }
                }

                // CRITICAL INVARIANT [RGR-04]: Visibility of FarCarOpt Suspended Cars in Occupancy Snapshot (See GRAVEYARD.md)
                AITraffic.Compat.PersistentJobsPatches.AddSuspendedOccupiedTracks(occupied);
            }
            catch { }
            return occupied;
        }

        private static void EnsureSuspendedTracksFrameCache()
        {
            int frame = Time.frameCount;
            if (s_cachedSuspendedTracksFrame == frame)
                return;

            s_cachedSuspendedTracksFrame = frame;
            s_cachedSuspendedTrackOwner.Clear();
            s_cachedMultiOwnerSuspendedTracks.Clear();

            try
            {
                if (CarSpawner.Instance != null && CarSpawner.Instance.AllCars != null)
                {
                    var allCars = CarSpawner.Instance.AllCars;
                    for (int i = 0; i < allCars.Count; i++)
                    {
                        var car = allCars[i];
                        if (car == null) continue;

                        Trainset ts = car.trainset;
                        if (car.FrontBogie != null && car.FrontBogie.track != null)
                        {
                            RecordSuspendedTrack(car.FrontBogie.track, ts);
                        }
                        if (car.RearBogie != null && car.RearBogie.track != null && car.RearBogie.track != (car.FrontBogie != null ? car.FrontBogie.track : null))
                        {
                            RecordSuspendedTrack(car.RearBogie.track, ts);
                        }
                    }
                }
            }
            catch
            {
                // Safe fallback if AllCars is modified mid-iteration
            }
        }

        private static void RecordSuspendedTrack(RailTrack track, Trainset owner)
        {
            if (track == null || s_cachedMultiOwnerSuspendedTracks.Contains(track))
                return;

            Trainset existing;
            if (!s_cachedSuspendedTrackOwner.TryGetValue(track, out existing))
            {
                s_cachedSuspendedTrackOwner[track] = owner;
            }
            else if (existing != owner || existing == null)
            {
                s_cachedMultiOwnerSuspendedTracks.Add(track);
            }
        }

        private static bool IsTrackOccupiedInSuspendedFrameCache(RailTrack track, Trainset ignoreTrainset)
        {
            EnsureSuspendedTracksFrameCache();
            if (s_cachedMultiOwnerSuspendedTracks.Contains(track))
                return true;

            Trainset owner;
            if (s_cachedSuspendedTrackOwner.TryGetValue(track, out owner))
            {
                if (ignoreTrainset != null && owner == ignoreTrainset)
                    return false;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if a track is occupied using a pre-built snapshot (or live bogies + per-frame suspended car cache if no snapshot provided).
        /// </summary>
        public bool IsTrackOccupied(RailTrack track, HashSet<RailTrack> occupiedSnapshot, Trainset ignoreTrainset = null)
        {
            if (track == null)
                return false;

            if (occupiedSnapshot != null)
            {
                return occupiedSnapshot.Contains(track);
            }

            try
            {
                var bogies = track.BogiesOnTrack();
                if (bogies != null && bogies.Count > 0)
                {
                    foreach (var b in bogies)
                    {
                        if (b != null && b.Car != null)
                        {
                            if (ignoreTrainset != null && b.Car.trainset == ignoreTrainset)
                                continue;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                // Ignore if DV internal bogie list is mid-update
            }

            // CRITICAL INVARIANT [RGR-04]: Hybrid Track Occupancy Detection (See GRAVEYARD.md)
            // Catch stationary/distant cars whose Bogie components are disabled by vanilla physics optimization using O(1) per-frame cache.
            return IsTrackOccupiedInSuspendedFrameCache(track, ignoreTrainset);
        }

        /// <summary>
        /// Checks if a specific track currently has any TrainCar bogies on it, optionally ignoring a specific Trainset.
        /// </summary>
        // CRITICAL INVARIANT [RGR-04]: Hybrid Track Occupancy Detection (See GRAVEYARD.md)
        public bool IsTrackOccupied(RailTrack track, Trainset ignoreTrainset = null)
        {
            return IsTrackOccupied(track, null, ignoreTrainset);
        }

        public bool IsTrackReserved(RailTrack track)
        {
            if (track == null) return false;
            lock (_lock)
            {
                return _trackReservations.ContainsKey(track);
            }
        }

        public bool IsTrackReservedByOther(RailTrack track, object requester)
        {
            if (track == null) return false;
            lock (_lock)
            {
                object holder;
                if (_trackReservations.TryGetValue(track, out holder))
                {
                    return holder != requester;
                }
                return false;
            }
        }

        /// <summary>
        /// Returns the AIEngineer (or other object) that holds the reservation on this track,
        /// as long as it is not the requester itself. Returns null if unreserved or self-reserved.
        /// Used for direction-aware conflict detection in corridor protection.
        /// </summary>
        public bool TryGetTrackReservationHolder(RailTrack track, object requester, out object holder)
        {
            holder = null;
            if (track == null) return false;
            lock (_lock)
            {
                object h;
                if (_trackReservations.TryGetValue(track, out h) && h != null && h != requester)
                {
                    holder = h;
                    return true;
                }
                return false;
            }
        }

        public bool TryReserveTrack(RailTrack track, object requester)
        {
            if (track == null || requester == null) return false;
            lock (_lock)
            {
                object holder;
                if (_trackReservations.TryGetValue(track, out holder))
                {
                    return holder == requester;
                }
                _trackReservations[track] = requester;
                return true;
            }
        }

        public void ReleaseTrackReservation(RailTrack track, object requester)
        {
            if (track == null) return;
            lock (_lock)
            {
                object holder;
                if (_trackReservations.TryGetValue(track, out holder) && holder == requester)
                {
                    _trackReservations.Remove(track);
                }
            }
        }

        public void ReleaseAllReservationsFor(object requester)
        {
            if (requester == null) return;
            lock (_lock)
            {
                var keysToRemove = new List<RailTrack>();
                foreach (var kvp in _trackReservations)
                {
                    if (kvp.Value == requester)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    _trackReservations.Remove(keysToRemove[i]);
                }
            }
        }

        #endregion

        #region Proactive Centralized Single-Track Corridor Token System

        private readonly object _corridorLock = new object();
        private readonly Dictionary<string, object> _corridorTokenHolders = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, float> _corridorTokenAcquiredTime = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<RailTrack>> _corridorTracksMap = new Dictionary<string, List<RailTrack>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<RailTrack, string> _trackToCorridorIdMap = new Dictionary<RailTrack, string>();

        /// <summary>
        /// Attempts to acquire an exclusive corridor token for a single-track section between passing loops.
        /// An AI train cannot enter a single-track section between passing loops unless it obtains the corridor token.
        /// Performs bidirectional physical rolling stock occupancy checks across all corridor tracks (IsTrackOccupied)
        /// for all trains before granting a token.
        /// </summary>
        /// <summary>
        /// Attempts to acquire an exclusive corridor token for a single-track section identified by its tracks.
        /// </summary>
        public bool TryAcquireCorridorToken(List<RailTrack> tracks, object train)
        {
            if (tracks == null || tracks.Count == 0 || train == null) return false;
            string corridorId = GetCanonicalCorridorId(tracks);
            if (string.IsNullOrEmpty(corridorId)) return false;
            return TryAcquireCorridorToken(corridorId, train);
        }

        public bool TryAcquireCorridorToken(string corridorId, object train)
        {
            if (string.IsNullOrEmpty(corridorId) || train == null) return false;

            lock (_corridorLock)
            {
                // CRITICAL INVARIANT [RGR-08]: Single-Track Corridor Token Player-Signal Preemption & Re-Validation.
                // Never grant or retain a corridor token while any track in the corridor is reserved by a player signal
                // (SignalRegistry.IsTrackReservedByPlayerSignal) or occupied by player/external rolling stock.
                // Even if currentHolder == train, re-validate corridor tracks and revoke the token if preempted by player signal/occupancy.
                object currentHolder;
                bool alreadyHeldByRequester = false;
                if (_corridorTokenHolders.TryGetValue(corridorId, out currentHolder) && currentHolder != null)
                {
                    if (currentHolder == train)
                    {
                        alreadyHeldByRequester = true;
                    }
                    else
                    {
                        return false; // Token currently held by another train
                    }
                }

                // Retrieve all tracks belonging to this corridor
                var tracks = GetTracksForCorridor(corridorId);
                Trainset requesterTrainset = GetTrainsetFromTrain(train);

                // Bidirectional physical rolling stock occupancy and player signal reservation checks across all corridor tracks
                if (tracks != null && tracks.Count > 0)
                {
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        var track = tracks[i];
                        if (track == null) continue;

                        // 1. Direct physical bogie occupancy check (bidirectional across all rolling stock; includes live BogiesOnTrack + [RGR-04] suspended car cache)
                        if (IsTrackOccupied(track, requesterTrainset))
                        {
                            if (alreadyHeldByRequester) ReleaseCorridorToken(corridorId, train);
                            return false;
                        }

                        // 3. Player train occupancy and DVSignals player signal reservation check ([RGR-08])
                        if (SignalRegistry.IsTrackOccupiedByPlayer(track, requesterTrainset) ||
                            SignalRegistry.IsTrackReservedByPlayerSignal(track))
                        {
                            if (alreadyHeldByRequester) ReleaseCorridorToken(corridorId, train);
                            return false;
                        }

                        // 4. Reservation check (cannot grant token if reserved by another train)
                        if (IsTrackReservedByOther(track, train))
                        {
                            if (alreadyHeldByRequester) ReleaseCorridorToken(corridorId, train);
                            return false;
                        }
                    }

                    // Protect and reserve all corridor tracks under requester
                    for (int i = 0; i < tracks.Count; i++)
                    {
                        if (tracks[i] != null)
                        {
                            TryReserveTrack(tracks[i], train);
                        }
                    }
                }

                // Grant corridor token
                _corridorTokenHolders[corridorId] = train;
                if (!alreadyHeldByRequester)
                {
                    _corridorTokenAcquiredTime[corridorId] = Time.time;
                }
                return true;
            }
        }

        /// <summary>
        /// Releases the corridor token held by the train for the specified single-track corridor.
        /// </summary>
        public void ReleaseCorridorToken(string corridorId, object train)
        {
            if (string.IsNullOrEmpty(corridorId) || train == null) return;

            lock (_corridorLock)
            {
                object currentHolder;
                if (_corridorTokenHolders.TryGetValue(corridorId, out currentHolder) && currentHolder == train)
                {
                    _corridorTokenHolders.Remove(corridorId);
                    _corridorTokenAcquiredTime.Remove(corridorId);

                    // Release reservations on the corridor tracks held by this train
                    var tracks = GetTracksForCorridor(corridorId);
                    if (tracks != null)
                    {
                        for (int i = 0; i < tracks.Count; i++)
                        {
                            if (tracks[i] != null)
                            {
                                ReleaseTrackReservation(tracks[i], train);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Checks whether the specified train currently holds the corridor token for this single-track corridor.
        /// </summary>
        public bool IsCorridorTokenHeld(string corridorId, object train)
        {
            if (string.IsNullOrEmpty(corridorId) || train == null) return false;

            lock (_corridorLock)
            {
                object currentHolder;
                if (_corridorTokenHolders.TryGetValue(corridorId, out currentHolder))
                {
                    return currentHolder == train;
                }
                return false;
            }
        }

        /// <summary>
        /// Checks whether a corridor token is currently held by any train.
        /// </summary>
        public bool IsCorridorTokenActive(string corridorId)
        {
            if (string.IsNullOrEmpty(corridorId)) return false;
            lock (_corridorLock)
            {
                return _corridorTokenHolders.ContainsKey(corridorId);
            }
        }

        /// <summary>
        /// Attempts to get the current holder of a corridor token.
        /// </summary>
        public bool TryGetCorridorTokenHolder(string corridorId, out object holder)
        {
            holder = null;
            if (string.IsNullOrEmpty(corridorId)) return false;
            lock (_corridorLock)
            {
                return _corridorTokenHolders.TryGetValue(corridorId, out holder);
            }
        }

        /// <summary>
        /// Releases all corridor tokens held by the specified train.
        /// </summary>
        public void ReleaseAllCorridorTokensFor(object train)
        {
            if (train == null) return;
            lock (_corridorLock)
            {
                var keysToRemove = new List<string>();
                foreach (var kvp in _corridorTokenHolders)
                {
                    if (kvp.Value == train)
                    {
                        keysToRemove.Add(kvp.Key);
                    }
                }

                for (int i = 0; i < keysToRemove.Count; i++)
                {
                    string id = keysToRemove[i];
                    _corridorTokenHolders.Remove(id);
                    _corridorTokenAcquiredTime.Remove(id);

                    var tracks = GetTracksForCorridor(id);
                    if (tracks != null)
                    {
                        for (int t = 0; t < tracks.Count; t++)
                        {
                            if (tracks[t] != null)
                            {
                                ReleaseTrackReservation(tracks[t], train);
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Explicitly registers or overrides the set of tracks belonging to a named single-track corridor.
        /// </summary>
        public void RegisterCorridorTracks(string corridorId, IEnumerable<RailTrack> tracks)
        {
            if (string.IsNullOrEmpty(corridorId) || tracks == null) return;
            lock (_corridorLock)
            {
                var list = new List<RailTrack>();
                foreach (var trk in tracks)
                {
                    if (trk != null && !list.Contains(trk))
                    {
                        list.Add(trk);
                        _trackToCorridorIdMap[trk] = corridorId;
                    }
                }
                _corridorTracksMap[corridorId] = list;
            }
        }

        /// <summary>
        /// Gets all tracks belonging to a corridor. If not explicitly registered, attempts dynamic resolution.
        /// </summary>
        public List<RailTrack> GetTracksForCorridor(string corridorId)
        {
            if (string.IsNullOrEmpty(corridorId)) return new List<RailTrack>();

            lock (_corridorLock)
            {
                List<RailTrack> list;
                if (_corridorTracksMap.TryGetValue(corridorId, out list) && list != null && list.Count > 0)
                {
                    return list;
                }

                // If not in pre-registered map, check if corridorId is a track name
                var track = GetTrackByName(corridorId);
                if (track != null)
                {
                    string mappedId;
                    if (_trackToCorridorIdMap.TryGetValue(track, out mappedId) && _corridorTracksMap.TryGetValue(mappedId, out list))
                    {
                        return list;
                    }

                    list = new List<RailTrack> { track };
                    return list;
                }

                return new List<RailTrack>();
            }
        }

        /// <summary>
        /// Gets or derives a canonical, direction-independent identifier for a sequence of corridor tracks.
        /// First checks if any of the tracks already belong to a pre-discovered corridor in _trackToCorridorIdMap.
        /// If not, deterministically builds a canonical ID from the sorted track identifiers.
        /// </summary>
        public string GetCanonicalCorridorId(List<RailTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0) return null;

            lock (_corridorLock)
            {
                // 1. Check if any track is already mapped to an existing corridor
                for (int i = 0; i < tracks.Count; i++)
                {
                    var trk = tracks[i];
                    if (trk != null)
                    {
                        string existingId;
                        if (_trackToCorridorIdMap.TryGetValue(trk, out existingId) && !string.IsNullOrEmpty(existingId))
                        {
                            return existingId;
                        }
                    }
                }

                // 2. If not pre-mapped, build a deterministic bidirectional key from min/max track IDs or sorted names
                int minId = int.MaxValue;
                int maxId = int.MinValue;
                string firstName = null;
                string lastName = null;

                for (int i = 0; i < tracks.Count; i++)
                {
                    var trk = tracks[i];
                    if (trk != null)
                    {
                        int id = trk.GetInstanceID();
                        if (id < minId) minId = id;
                        if (id > maxId) maxId = id;

                        string tName = !string.IsNullOrEmpty(trk.name) ? trk.name : ("track_" + id);
                        if (firstName == null || string.Compare(tName, firstName, StringComparison.Ordinal) < 0)
                            firstName = tName;
                        if (lastName == null || string.Compare(tName, lastName, StringComparison.Ordinal) > 0)
                            lastName = tName;
                    }
                }

                if (minId != int.MaxValue && maxId != int.MinValue)
                {
                    return string.Format("Corridor_{0}_{1}", firstName != null ? firstName : minId.ToString(), lastName != null ? lastName : maxId.ToString());
                }

                return null;
            }
        }

        /// <summary>
        /// Returns the single-track corridor identifier that this track belongs to, if any.
        /// </summary>
        public string GetCorridorIdForTrack(RailTrack track)
        {
            if (track == null) return null;
            lock (_corridorLock)
            {
                string id;
                if (_trackToCorridorIdMap.TryGetValue(track, out id))
                {
                    return id;
                }
                return null;
            }
        }

        /// <summary>
        /// Evaluates whether an AI train is permitted to enter a single-track section.
        /// An AI train cannot enter a single-track section between passing loops unless it obtains the corridor token.
        /// </summary>
        public bool CanEnterSingleTrackSection(RailTrack track, object train)
        {
            if (track == null || train == null) return true;
            string corridorId = GetCorridorIdForTrack(track);
            if (string.IsNullOrEmpty(corridorId))
            {
                return true; // Not a single-track bottleneck requiring token
            }
            return IsCorridorTokenHeld(corridorId, train);
        }

        /// <summary>
        /// Evaluates whether an AI train is permitted to enter a single-track corridor by ID.
        /// </summary>
        public bool CanTrainEnterCorridor(string corridorId, object train)
        {
            if (string.IsNullOrEmpty(corridorId)) return true;
            return IsCorridorTokenHeld(corridorId, train);
        }

        /// <summary>
        /// Discovers and catalogs all single-track sections between passing loops, sidings, and double-track mainlines.
        /// Precomputes corridor track collections for O(1) corridor token management.
        /// </summary>
        public void DiscoverSingleTrackCorridors()
        {
            lock (_corridorLock)
            {
                _corridorTracksMap.Clear();
                _trackToCorridorIdMap.Clear();

                var visitedEdges = new HashSet<RailEdge>();

                for (int i = 0; i < Edges.Count; i++)
                {
                    var startEdge = Edges[i];
                    if (startEdge == null || startEdge.Track == null) continue;
                    if (visitedEdges.Contains(startEdge)) continue;

                    // Only consider through mainline single tracks
                    if (startEdge.IsDoubleTrackMainline || startEdge.IsPassingLoopOrSiding || startEdge.IsYardTrack || startEdge.IsShuntingOrStorageTrack)
                    {
                        continue;
                    }

                    // Discover contiguous single-track chain
                    var chainEdges = new List<RailEdge>();
                    var queue = new Queue<RailEdge>();
                    queue.Enqueue(startEdge);
                    visitedEdges.Add(startEdge);

                    while (queue.Count > 0)
                    {
                        var curr = queue.Dequeue();
                        chainEdges.Add(curr);

                        // Check FromNode
                        if (curr.FromNode != null && !IsCorridorBoundaryNode(curr.FromNode))
                        {
                            for (int e = 0; e < curr.FromNode.IncidentEdges.Count; e++)
                            {
                                var neighbor = curr.FromNode.IncidentEdges[e];
                                if (neighbor != null && neighbor != curr && !visitedEdges.Contains(neighbor))
                                {
                                    if (!neighbor.IsDoubleTrackMainline && !neighbor.IsPassingLoopOrSiding && !neighbor.IsYardTrack && !neighbor.IsShuntingOrStorageTrack)
                                    {
                                        visitedEdges.Add(neighbor);
                                        queue.Enqueue(neighbor);
                                    }
                                }
                            }
                        }

                        // Check ToNode
                        if (curr.ToNode != null && !IsCorridorBoundaryNode(curr.ToNode))
                        {
                            for (int e = 0; e < curr.ToNode.IncidentEdges.Count; e++)
                            {
                                var neighbor = curr.ToNode.IncidentEdges[e];
                                if (neighbor != null && neighbor != curr && !visitedEdges.Contains(neighbor))
                                {
                                    if (!neighbor.IsDoubleTrackMainline && !neighbor.IsPassingLoopOrSiding && !neighbor.IsYardTrack && !neighbor.IsShuntingOrStorageTrack)
                                    {
                                        visitedEdges.Add(neighbor);
                                        queue.Enqueue(neighbor);
                                    }
                                }
                            }
                        }
                    }

                    if (chainEdges.Count > 0)
                    {
                        int minId = int.MaxValue;
                        int maxId = int.MinValue;
                        var tracks = new List<RailTrack>();

                        for (int c = 0; c < chainEdges.Count; c++)
                        {
                            var edge = chainEdges[c];
                            if (edge.Id < minId) minId = edge.Id;
                            if (edge.Id > maxId) maxId = edge.Id;
                            if (edge.Track != null && !tracks.Contains(edge.Track))
                            {
                                tracks.Add(edge.Track);
                            }
                        }

                        string corridorId = string.Format("Corridor_ST_{0}_{1}", minId, maxId);
                        _corridorTracksMap[corridorId] = tracks;
                        for (int t = 0; t < tracks.Count; t++)
                        {
                            _trackToCorridorIdMap[tracks[t]] = corridorId;
                        }
                    }
                }

                Log(string.Format("[RailGraph] Discovered {0} single-track corridor sections between passing loops.", _corridorTracksMap.Count));
            }
        }

        private static bool IsCorridorBoundaryNode(RailNode node)
        {
            if (node == null) return true;
            if (node.IsJunction && node.IncidentEdges != null && node.IncidentEdges.Count > 2)
            {
                return true;
            }

            if (node.IncidentEdges != null)
            {
                for (int i = 0; i < node.IncidentEdges.Count; i++)
                {
                    var edge = node.IncidentEdges[i];
                    if (edge != null && (edge.IsDoubleTrackMainline || edge.IsPassingLoopOrSiding || edge.IsYardTrack || edge.IsShuntingOrStorageTrack))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static Trainset GetTrainsetFromTrain(object train)
        {
            if (train == null) return null;
            var ts = train as Trainset;
            if (ts != null) return ts;
            var tc = train as TrainCar;
            if (tc != null) return tc.trainset;
            var eng = train as AITraffic.Driver.AIEngineer;
            if (eng != null && eng.TrainCar != null) return eng.TrainCar.trainset;
            return null;
        }

        #endregion

        #region Spatial Track Grid

        private const float SpatialGridCellSize = 500f;

        public struct SpatialCellKey : IEquatable<SpatialCellKey>
        {
            public readonly int X;
            public readonly int Z;

            public SpatialCellKey(int x, int z)
            {
                X = x;
                Z = z;
            }

            public bool Equals(SpatialCellKey other)
            {
                return X == other.X && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                if (obj is SpatialCellKey)
                {
                    return Equals((SpatialCellKey)obj);
                }
                return false;
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (X * 397) ^ Z;
                }
            }
        }

        private readonly Dictionary<SpatialCellKey, List<RailTrack>> _spatialGrid = new Dictionary<SpatialCellKey, List<RailTrack>>();

        private void BuildSpatialTrackGrid()
        {
            _spatialGrid.Clear();
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge == null || edge.Track == null || edge.Track.curve == null) continue;

                var trk = edge.Track;
                Vector3 mid = edge.GetMidPoint();
                int midX = Mathf.FloorToInt(mid.x / SpatialGridCellSize);
                int midZ = Mathf.FloorToInt(mid.z / SpatialGridCellSize);
                AddTrackToSpatialCell(midX, midZ, trk);

                // For tracks longer than 250m, also register endpoints so long tracks aren't missed
                if (edge.Length > 250f)
                {
                    Vector3 start = edge.Track.curve.GetPointAt(0f);
                    int sX = Mathf.FloorToInt(start.x / SpatialGridCellSize);
                    int sZ = Mathf.FloorToInt(start.z / SpatialGridCellSize);
                    if (sX != midX || sZ != midZ)
                    {
                        AddTrackToSpatialCell(sX, sZ, trk);
                    }

                    Vector3 end = edge.Track.curve.GetPointAt(1f);
                    int eX = Mathf.FloorToInt(end.x / SpatialGridCellSize);
                    int eZ = Mathf.FloorToInt(end.z / SpatialGridCellSize);
                    if ((eX != midX || eZ != midZ) && (eX != sX || eZ != sZ))
                    {
                        AddTrackToSpatialCell(eX, eZ, trk);
                    }
                }
            }
        }

        private void AddTrackToSpatialCell(int x, int z, RailTrack trk)
        {
            var key = new SpatialCellKey(x, z);
            List<RailTrack> list;
            if (!_spatialGrid.TryGetValue(key, out list))
            {
                list = new List<RailTrack>(8);
                _spatialGrid[key] = list;
            }
            if (!list.Contains(trk))
            {
                list.Add(trk);
            }
        }

        /// <summary>
        /// Retrieves candidate tracks within an annular spatial ring [minRadius, maxRadius] around center.
        /// Uses the pre-indexed 2D spatial grid for instantaneous O(1) cell lookup.
        /// </summary>
        public void GetTracksInRadius(Vector3 center, float minRadius, float maxRadius, List<RailTrack> results)
        {
            if (results == null) return;
            results.Clear();

            int minX = Mathf.FloorToInt((center.x - maxRadius) / SpatialGridCellSize);
            int maxX = Mathf.FloorToInt((center.x + maxRadius) / SpatialGridCellSize);
            int minZ = Mathf.FloorToInt((center.z - maxRadius) / SpatialGridCellSize);
            int maxZ = Mathf.FloorToInt((center.z + maxRadius) / SpatialGridCellSize);

            float minRadSq = minRadius * minRadius;
            float maxRadSq = maxRadius * maxRadius;

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    List<RailTrack> cellTracks;
                    if (_spatialGrid.TryGetValue(new SpatialCellKey(x, z), out cellTracks) && cellTracks != null)
                    {
                        for (int i = 0; i < cellTracks.Count; i++)
                        {
                            var trk = cellTracks[i];
                            if (trk == null || trk.curve == null) continue;

                            Vector3 mid = trk.curve.GetPointAt(0.5f);
                            float dSq = (mid.x - center.x) * (mid.x - center.x) + (mid.z - center.z) * (mid.z - center.z);
                            if (dSq >= minRadSq && dSq <= maxRadSq)
                            {
                                if (!results.Contains(trk))
                                {
                                    results.Add(trk);
                                }
                            }
                        }
                    }
                }
            }
        }

        #endregion

        #region OWC Diagnostic Dump

        /// <summary>
        /// Temporary startup diagnostic to inspect the graph topology, traversability, and Pathfinder costs
        /// along the right-hand mainline track from 'Road 8BA' (Node 115) to 'Road 82AA' (Node 79).
        /// </summary>
        public void DumpOwcRightHandChain()
        {
            try
            {
                Log("================================================================================");
                Log("[OWC-DIAG] START: DumpOwcRightHandChain() - Diagnostic Analysis of Right-Hand Track");
                Log("================================================================================");

                string[] targetNames = new string[]
                {
                    "Road 8BA",
                    "Road 8A",
                    "Road 7",
                    "Road 82BB",
                    "Road 82BA",
                    "Road 82AA"
                };

                var targetEdges = new Dictionary<string, RailEdge>(StringComparer.OrdinalIgnoreCase);
                for (int t = 0; t < targetNames.Length; t++)
                {
                    string tName = targetNames[t];
                    var edge = FindEdgeByMatchingName(tName);
                    targetEdges[tName] = edge;

                    Log(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "[OWC-DIAG-TARGET] Query '{0}' => Edge '{1}' (Id={2}, FromNode={3}, ToNode={4}, Len={5:F1}m, DT={6}, PrefFwd={7}, Parallel='{8}', Yard={9}, IndYard={10}, StrictlyAvoid={11}, PassingLoop={12})",
                        tName,
                        edge != null ? edge.TrackName : "NOT_FOUND",
                        edge != null ? edge.Id : -1,
                        (edge != null && edge.FromNode != null) ? edge.FromNode.Id : -1,
                        (edge != null && edge.ToNode != null) ? edge.ToNode.Id : -1,
                        edge != null ? edge.Length : 0f,
                        edge != null ? edge.IsDoubleTrackMainline : false,
                        edge != null ? edge.PreferredForward : false,
                        (edge != null && edge.ParallelEdge != null) ? edge.ParallelEdge.TrackName : "null",
                        edge != null ? edge.IsYardTrack : false,
                        edge != null ? edge.IsIndustrialYardOrStorageTrack : false,
                        edge != null ? edge.IsStrictlyAvoidedThroughTrack : false,
                        edge != null ? edge.IsPassingLoopOrSiding : false
                    ));
                }

                RailEdge eRoad8BA = targetEdges["Road 8BA"];
                RailEdge eRoad8A = targetEdges["Road 8A"];
                RailEdge eRoad7 = targetEdges["Road 7"];
                RailEdge eRoad82BB = targetEdges["Road 82BB"];
                RailEdge eRoad82BA = targetEdges["Road 82BA"];
                RailEdge eRoad82AA = targetEdges["Road 82AA"];

                if (eRoad8BA == null)
                {
                    LogWarning("[OWC-DIAG] 'Road 8BA' edge not found! Aborting right-hand chain walk.");
                    return;
                }

                // Determine start node on Road 8BA (specifically Node 115 or the node leading towards Road 8A)
                RailNode startNode = null;
                if (eRoad8BA.ToNode != null && eRoad8BA.ToNode.Id == 115) startNode = eRoad8BA.ToNode;
                else if (eRoad8BA.FromNode != null && eRoad8BA.FromNode.Id == 115) startNode = eRoad8BA.FromNode;
                else
                {
                    if (eRoad8BA.ToNode != null && eRoad8BA.ToNode.IncidentEdges != null && eRoad8BA.ToNode.IncidentEdges.Count > 1)
                        startNode = eRoad8BA.ToNode;
                    else
                        startNode = eRoad8BA.FromNode;
                }

                Log(string.Format("[OWC-DIAG] Starting walk from Edge '{0}' (Id={1}) at Node {2} (Name='{3}')...",
                    eRoad8BA.TrackName, eRoad8BA.Id, startNode != null ? startNode.Id : -1, startNode != null ? startNode.Name : "null"));

                // Build topological sequence of edges connecting waypoints
                var fullChain = new List<RailEdge>();
                fullChain.Add(eRoad8BA);

                RailNode currentWalkNode = startNode;
                RailEdge currentIncomingEdge = eRoad8BA;
                RailEdge[] waypointTargets = new RailEdge[] { eRoad8A, eRoad82BA, eRoad82BB, eRoad82AA };

                for (int w = 0; w < waypointTargets.Length; w++)
                {
                    var targetWp = waypointTargets[w];
                    if (targetWp == null || currentWalkNode == null) continue;

                    var subChain = FindEdgeChainBetween(currentWalkNode, currentIncomingEdge, targetWp, 12);
                    if (subChain != null && subChain.Count > 0)
                    {
                        RailNode lastExitNode = currentWalkNode;
                        for (int s = 0; s < subChain.Count; s++)
                        {
                            var e = subChain[s];
                            if (!fullChain.Contains(e))
                            {
                                fullChain.Add(e);
                            }
                            lastExitNode = e.GetOtherNode(lastExitNode);
                            currentIncomingEdge = e;
                        }
                        currentWalkNode = lastExitNode;
                    }
                    else
                    {
                        Log(string.Format("[OWC-DIAG] WARNING: No topological graph chain found from Node {0} to '{1}'!",
                            currentWalkNode != null ? currentWalkNode.Id.ToString() : "null",
                            targetWp.TrackName));
                    }
                }

                Log(string.Format("[OWC-DIAG] Assembled right-hand topological sequence ({0} edges): [{1}]",
                    fullChain.Count,
                    string.Join(" -> ", fullChain.Select(e => "'" + e.TrackName + "' (Id=" + e.Id + ")").ToArray())));

                var pathfinder = new Pathfinder(this);
                var pathOptions = new PathfinderOptions
                {
                    AllowWrongDirection = true,
                    AvoidOccupiedTracks = false,
                    PreferSpeedOverDistance = true
                };
                var emptyOccupied = new HashSet<RailTrack>();

                RailNode currStepNode = startNode;

                for (int step = 0; step < fullChain.Count; step++)
                {
                    var incomingEdge = fullChain[step];
                    var nextChainEdge = (step + 1 < fullChain.Count) ? fullChain[step + 1] : null;

                    RailNode stepNode = currStepNode;

                    Log(string.Format(
                        System.Globalization.CultureInfo.InvariantCulture,
                        "--- [OWC-DIAG STEP {0}] At Node {1} (Name='{2}', Junction='{3}') via Incoming Edge '{4}' (Id={5}, Len={6:F1}m, DT={7}, PrefFwd={8}, Parallel='{9}') ---",
                        step,
                        stepNode != null ? stepNode.Id : -1,
                        stepNode != null ? stepNode.Name : "null",
                        (stepNode != null && stepNode.Junction != null) ? stepNode.Junction.name : "null",
                        incomingEdge.TrackName,
                        incomingEdge.Id,
                        incomingEdge.Length,
                        incomingEdge.IsDoubleTrackMainline,
                        incomingEdge.PreferredForward,
                        incomingEdge.ParallelEdge != null ? incomingEdge.ParallelEdge.TrackName : "null"
                    ));

                    if (stepNode == null)
                    {
                        LogWarning(string.Format("[OWC-DIAG] Step {0}: stepNode is null! Stopping walk.", step));
                        break;
                    }

                    var traversable = GetTraversableEdges(stepNode, incomingEdge);
                    Log(string.Format("    GetTraversableEdges count = {0}: [{1}]",
                        traversable.Length,
                        string.Join(", ", traversable.Select(e => "'" + e.TrackName + "' (Id=" + e.Id + ")").ToArray())));

                    if (stepNode.IncidentEdges != null)
                    {
                        for (int i = 0; i < stepNode.IncidentEdges.Count; i++)
                        {
                            var cand = stepNode.IncidentEdges[i];
                            if (cand == null || cand == incomingEdge) continue;

                            bool isChosenChain = (cand == nextChainEdge);
                            string tag = isChosenChain ? "[CHOSEN RIGHT-CHAIN]" : "[ALT BRANCH]";

                            bool inTraversable = traversable.Contains(cand);
                            string traversableStatus = inTraversable ? "YES" : "NO";
                            string missingReason = "";

                            if (!inTraversable)
                            {
                                Vector3 inDir = GetIncomingVector(stepNode, incomingEdge);
                                Vector3 outDir = GetDepartingVector(stepNode, cand);
                                float dot = Vector3.Dot(inDir, outDir);

                                var junction = stepNode.Junction;
                                bool isTrailingFromOutBranch = false;
                                if (junction != null && junction.outBranches != null)
                                {
                                    for (int b = 0; b < junction.outBranches.Count; b++)
                                    {
                                        if (junction.outBranches[b] != null && junction.outBranches[b].track == incomingEdge.Track)
                                        {
                                            isTrailingFromOutBranch = true;
                                            break;
                                        }
                                    }
                                }

                                bool isAnotherOutBranch = false;
                                if (isTrailingFromOutBranch && junction != null && junction.outBranches != null)
                                {
                                    for (int b = 0; b < junction.outBranches.Count; b++)
                                    {
                                        if (junction.outBranches[b] != null && junction.outBranches[b].track == cand.Track)
                                        {
                                            isAnotherOutBranch = true;
                                            break;
                                        }
                                    }
                                }

                                if (dot < 0.20f)
                                {
                                    float angleDeg = Mathf.Acos(Mathf.Clamp(dot, -1f, 1f)) * Mathf.Rad2Deg;
                                    missingReason = string.Format("Angular reject (Dot={0:F3} < 0.20, Angle={1:F1} deg)", dot, angleDeg);
                                }
                                else if (isAnotherOutBranch)
                                {
                                    missingReason = "Trailing switch filter (entering from outBranch, candidate is another outBranch)";
                                }
                                else
                                {
                                    missingReason = "Not present in junction/track connected branches";
                                }
                            }

                            float cost;
                            byte branch;
                            var nextNodeOfCand = cand.GetOtherNode(stepNode);
                            bool isValid = pathfinder.EvaluateEdgeCost(
                                cand, stepNode, nextNodeOfCand, incomingEdge, eRoad8BA, eRoad82AA,
                                pathOptions, emptyOccupied, null, out cost, out branch);

                            if (!isValid)
                            {
                                cost = float.PositiveInfinity;
                            }

                            var penalties = new List<string>();
                            if (cand.IsStrictlyAvoidedThroughTrack) penalties.Add("StrictlyAvoidedThroughTrack (+10M)");
                            if (cand.IsIndustrialYardOrStorageTrack) penalties.Add("IndustrialYardOrStorageTrack (+500k)");
                            if (cand.IsYardTrack && !cand.IsIndustrialYardOrStorageTrack) penalties.Add("IsYardTrack (+500k)");
                            if (cand.IsPassingLoopOrSiding) penalties.Add("PassingLoopOrSiding");
                            if (IsCrossoverTrackName(cand.TrackName)) penalties.Add("CrossoverTrack (+1500)");

                            if (cand.IsDoubleTrackMainline && cand.ParallelEdge != null)
                            {
                                bool fwd = cand.IsForward(stepNode, nextNodeOfCand);
                                if (fwd != cand.PreferredForward)
                                {
                                    penalties.Add("WrongDirectionOnDoubleTrack (+5000 + 2*L)");
                                }
                                else
                                {
                                    penalties.Add("CorrectRightHandDirection (0.85x discount)");
                                }
                            }

                            string penaltySummary = penalties.Count > 0 ? string.Join(", ", penalties.ToArray()) : "None (Clear mainline)";

                            Log(string.Format(
                                System.Globalization.CultureInfo.InvariantCulture,
                                "    -> {0} Edge '{1}' (Id={2}, Len={3:F1}m, DT={4}, PrefFwd={5}, Parallel='{6}', NextNode={7}): Traversable={8}{9} | Valid={10}, Branch={11}, Cost={12:F1} | Penalties: {13}",
                                tag,
                                cand.TrackName,
                                cand.Id,
                                cand.Length,
                                cand.IsDoubleTrackMainline,
                                cand.PreferredForward,
                                cand.ParallelEdge != null ? cand.ParallelEdge.TrackName : "null",
                                nextNodeOfCand != null ? nextNodeOfCand.Id : -1,
                                traversableStatus,
                                !inTraversable ? (" [REASON: " + missingReason + "]") : "",
                                isValid,
                                branch,
                                cost,
                                penaltySummary
                            ));

                            if (step == 0 && (cand.Id == 113 || (cand.TrackName != null && cand.TrackName.IndexOf("[track through]", StringComparison.OrdinalIgnoreCase) >= 0)))
                            {
                                Log(string.Format("[OWC-DIAG-STEP0] Left Mainline Continuation Edge '{0}' (Id={1} -> Node {2}): Traversable={3}, Valid={4}, Cost={5:F1}",
                                    cand.TrackName, cand.Id, nextNodeOfCand != null ? nextNodeOfCand.Id : -1, traversableStatus, isValid, cost));
                            }
                            if (step == 0 && (cand.Id == 137 || (cand.TrackName != null && cand.TrackName.IndexOf("DT-CWSB 24", StringComparison.OrdinalIgnoreCase) >= 0)))
                            {
                                Log(string.Format("[OWC-DIAG-STEP0] Right Mainline Continuation Edge '{0}' (Id={1} -> Node {2}): Traversable={3}, Valid={4}, Cost={5:F1}",
                                    cand.TrackName, cand.Id, nextNodeOfCand != null ? nextNodeOfCand.Id : -1, traversableStatus, isValid, cost));
                            }

                            if (isChosenChain && !inTraversable)
                            {
                                LogError(string.Format("[OWC-DIAG] CRITICAL TOPOLOGY DEFECT: Right-hand continuation edge '{0}' (Id={1}) is MISSING from GetTraversableEdges at Node {2}! ({3})",
                                    cand.TrackName, cand.Id, stepNode.Id, missingReason));
                            }
                            if (isChosenChain && cost > 1000f)
                            {
                                LogWarning(string.Format("[OWC-DIAG] HIGH PENALTY DETECTED: Right-hand continuation edge '{0}' (Id={1}) has abnormal cost {2:F1}!",
                                    cand.TrackName, cand.Id, cost));
                            }
                        }
                    }

                    if (nextChainEdge != null)
                    {
                        currStepNode = nextChainEdge.GetOtherNode(stepNode);
                    }
                }

                Log(string.Format("[OWC-DIAG] Terminus reached at Node {0} (Name='{1}').",
                    currStepNode != null ? currStepNode.Id : -1,
                    currStepNode != null ? currStepNode.Name : "null"));
                Log("================================================================================");
                Log("[OWC-DIAG] END: DumpOwcRightHandChain() complete.");
                Log("================================================================================");
            }
            catch (Exception ex)
            {
                LogError(string.Format("[OWC-DIAG] Exception during DumpOwcRightHandChain: {0}", ex));
            }
        }

        private RailEdge FindEdgeByMatchingName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge == null) continue;
                if (string.Equals(edge.TrackName, name, StringComparison.OrdinalIgnoreCase) ||
                    (edge.Track != null && string.Equals(edge.Track.name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    return edge;
                }
            }
            string pattern = @"\b" + Regex.Escape(name) + @"\b";
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge == null) continue;
                if ((edge.TrackName != null && Regex.IsMatch(edge.TrackName, pattern, RegexOptions.IgnoreCase)) ||
                    (edge.Track != null && edge.Track.name != null && Regex.IsMatch(edge.Track.name, pattern, RegexOptions.IgnoreCase)))
                {
                    return edge;
                }
            }
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                if (edge == null) continue;
                if ((edge.TrackName != null && edge.TrackName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (edge.Track != null && edge.Track.name != null && edge.Track.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return edge;
                }
            }
            return null;
        }

        private List<RailEdge> FindEdgeChainBetween(RailNode startNode, RailEdge initialIncomingEdge, RailEdge targetEdge, int maxHops = 10)
        {
            if (startNode == null || targetEdge == null) return null;
            if (startNode == targetEdge.FromNode || startNode == targetEdge.ToNode)
            {
                return new List<RailEdge> { targetEdge };
            }

            var queue = new Queue<Tuple<RailNode, RailEdge, List<RailEdge>>>();
            var visitedNodes = new HashSet<RailNode> { startNode };
            queue.Enqueue(new Tuple<RailNode, RailEdge, List<RailEdge>>(startNode, initialIncomingEdge, new List<RailEdge>()));

            while (queue.Count > 0)
            {
                var entry = queue.Dequeue();
                var node = entry.Item1;
                var incoming = entry.Item2;
                var chain = entry.Item3;

                if (chain.Count >= maxHops) continue;

                RailEdge[] candidates = (incoming != null)
                    ? GetTraversableEdges(node, incoming)
                    : (node.IncidentEdges != null ? node.IncidentEdges.ToArray() : new RailEdge[0]);

                // Prioritize non-crossover, right-hand corridor, and [track through] continuations over diverging crossover branches
                Func<RailEdge, int> score = (e) =>
                {
                    if (e == null) return 999;
                    int s = 0;
                    string n = e.TrackName ?? string.Empty;
                    if (IsCrossoverTrackName(n)) s += 100;
                    if (n.IndexOf("[track diverging]", StringComparison.OrdinalIgnoreCase) >= 0) s += 20;
                    if (n.IndexOf("[track through]", StringComparison.OrdinalIgnoreCase) >= 0) s -= 10;
                    var next = e.GetOtherNode(node);
                    if (e.IsDoubleTrackMainline && e.ParallelEdge != null && next != null)
                    {
                        if (e.IsForward(node, next) == e.PreferredForward) s -= 30;
                        else s += 50;
                    }
                    return s;
                };

                Array.Sort(candidates, (a, b) => score(a).CompareTo(score(b)));

                for (int i = 0; i < candidates.Length; i++)
                {
                    var edge = candidates[i];
                    if (edge == null || edge == incoming || chain.Contains(edge)) continue;

                    if (edge == targetEdge)
                    {
                        var res = new List<RailEdge>(chain);
                        res.Add(edge);
                        return res;
                    }

                    var nextNode = edge.GetOtherNode(node);
                    if (nextNode != null && !visitedNodes.Contains(nextNode))
                    {
                        visitedNodes.Add(nextNode);
                        var nextChain = new List<RailEdge>(chain);
                        nextChain.Add(edge);
                        queue.Enqueue(new Tuple<RailNode, RailEdge, List<RailEdge>>(nextNode, edge, nextChain));
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
