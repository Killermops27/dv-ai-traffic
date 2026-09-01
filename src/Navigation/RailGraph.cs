using System;
using System.Collections.Generic;
using System.Linq;
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
        public Junction Junction { get; private set; }
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
        public DV.Logic.Job.Track LogicTrack { get; internal set; }

        public RailEdge(int id, RailTrack track, RailNode fromNode, RailNode toNode)
        {
            if (track == null) throw new ArgumentNullException("track");

            Id = id;
            Track = track;
            FromNode = fromNode;
            ToNode = toNode;

            if (track.curve != null)
            {
                Length = track.curve.length;
            }
            else
            {
                Vector3 p1 = fromNode != null ? fromNode.Position : Vector3.zero;
                Vector3 p2 = toNode != null ? toNode.Position : Vector3.zero;
                Length = Vector3.Distance(p1, p2);
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

        public bool IsForward(RailNode fromNode, RailNode toNode)
        {
            return fromNode == FromNode && toNode == ToNode;
        }

        public Vector3 GetDirection(RailNode fromNode)
        {
            if (fromNode == FromNode)
            {
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
                if (Track != null && Track.curve != null && Track.curve.pointCount > 0)
                {
                    return -Track.curve.GetTangentAt(1f).normalized;
                }
                if (FromNode != null && ToNode != null)
                {
                    return (FromNode.Position - ToNode.Position).normalized;
                }
            }

            return Vector3.forward;
        }

        public Vector3 GetMidPoint()
        {
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

        public RailGraph()
        {
            s_instance = this;
            Nodes = new List<RailNode>();
            Edges = new List<RailEdge>();
        }

        private static void Log(string msg)
        {
            Debug.Log("[AITraffic] " + msg);
        }

        private static void LogWarning(string msg)
        {
            Debug.LogWarning("[AITraffic] " + msg);
        }

        private static void LogError(string msg)
        {
            Debug.LogError("[AITraffic] " + msg);
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

                    IsInitialized = true;
                    Log(string.Format("[RailGraph] Initialization complete: {0} nodes, {1} edges.", Nodes.Count, Edges.Count));

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
            const float positionMergeThreshold = 0.35f;

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
                    if (junction != null && item.Node.Junction == null)
                    {
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

                int sampleCount = Mathf.Max(6, Mathf.CeilToInt(length / 10f));
                float maxCurvature = 0f;
                float minRadius = float.PositiveInfinity;

                Vector3 prevPoint = curve.GetPointAt(0f);
                Vector3 prevTangent = curve.GetTangentAt(0f).normalized;

                for (int s = 1; s <= sampleCount; s++)
                {
                    float t = (float)s / sampleCount;
                    Vector3 currentPoint = curve.GetPointAt(t);
                    Vector3 currentTangent = curve.GetTangentAt(t).normalized;

                    float segmentLen = Vector3.Distance(prevPoint, currentPoint);
                    if (segmentLen > 0.01f)
                    {
                        float angleRad = Vector3.Angle(prevTangent, currentTangent) * Mathf.Deg2Rad;
                        float curvature = angleRad / segmentLen;

                        if (curvature > maxCurvature)
                        {
                            maxCurvature = curvature;
                        }

                        if (curvature > 0.0001f)
                        {
                            float radius = 1f / curvature;
                            if (radius < minRadius)
                            {
                                minRadius = radius;
                            }
                        }
                    }

                    prevPoint = currentPoint;
                    prevTangent = currentTangent;
                }

                edge.Curvature = maxCurvature;
                edge.MinRadius = minRadius;
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
                                bool isDeadEnd = (edge.FromNode != null && edge.FromNode.IsDeadEnd) || 
                                                 (edge.ToNode != null && edge.ToNode.IsDeadEnd) ||
                                                 (edge.Track.inJunction == null && edge.Track.outJunction == null);

                                bool isStorageOrLoading = tName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                                                          tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                                          tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                                                          trackPart.StartsWith("Y", StringComparison.OrdinalIgnoreCase) ||
                                                          trackPart.StartsWith("L", StringComparison.OrdinalIgnoreCase) ||
                                                          trackPart.StartsWith("C", StringComparison.OrdinalIgnoreCase);

                                // Passing sidings [S] and platform loops [P] connected at both ends are through-running lines, not yard tracks
                                if (isDeadEnd || isStorageOrLoading)
                                {
                                    edge.IsYardTrack = true;
                                }
                            }
                        }
                        else
                        {
                            string tName = edge.Track.name ?? string.Empty;
                            bool isDeadEnd = (edge.FromNode != null && edge.FromNode.IsDeadEnd) || 
                                             (edge.ToNode != null && edge.ToNode.IsDeadEnd);

                            if (isDeadEnd ||
                                tName.StartsWith("[yard", StringComparison.OrdinalIgnoreCase) ||
                                tName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                                tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                                tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase))
                            {
                                edge.IsYardTrack = true;
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
            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                float radius = edge.MinRadius;
                float geoLimit;

                if (float.IsInfinity(radius) || radius >= 1100f) geoLimit = 120f;
                else if (radius >= 850f) geoLimit = 100f;
                else if (radius >= 650f) geoLimit = 90f;
                else if (radius >= 480f) geoLimit = 80f;
                else if (radius >= 340f) geoLimit = 70f;
                else if (radius >= 240f) geoLimit = 60f;
                else if (radius >= 160f) geoLimit = 50f;
                else if (radius >= 100f) geoLimit = 40f;
                else geoLimit = 30f;

                string tName = edge.Track != null ? (edge.Track.name ?? string.Empty) : string.Empty;
                bool isPlatform = tName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase) ||
                                  tName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  tName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0;

                if (edge.IsYardTrack)
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
            const float maxParallelDistance = 12.0f;
            const float minTangentAlignment = 0.85f;

            var mainlineEdges = Edges.Where(e => !e.IsYardTrack && e.Length >= 20f).ToList();

            for (int i = 0; i < mainlineEdges.Count; i++)
            {
                var edge1 = mainlineEdges[i];
                Vector3 mid1 = edge1.GetMidPoint();
                Vector3 tan1 = edge1.GetTangentAtSpan(edge1.Length * 0.5f);

                for (int j = i + 1; j < mainlineEdges.Count; j++)
                {
                    var edge2 = mainlineEdges[j];
                    Vector3 mid2 = edge2.GetMidPoint();

                    float dist = Vector3.Distance(mid1, mid2);
                    if (dist < minParallelDistance || dist > maxParallelDistance)
                        continue;

                    Vector3 tan2 = edge2.GetTangentAtSpan(edge2.Length * 0.5f);
                    float dot = Vector3.Dot(tan1, tan2);

                    if (Mathf.Abs(dot) >= minTangentAlignment)
                    {
                        edge1.IsDoubleTrackMainline = true;
                        edge2.IsDoubleTrackMainline = true;
                        edge1.ParallelEdge = edge2;
                        edge2.ParallelEdge = edge1;

                        Vector3 forwardDir1 = tan1;
                        Vector3 rightNormal1 = Vector3.Cross(Vector3.up, forwardDir1).normalized;
                        Vector3 offset12 = mid2 - mid1;

                        float lateralOffset = Vector3.Dot(offset12, rightNormal1);

                        // If lateralOffset > 0, edge2 is to the RIGHT of edge1 when traveling forward (+tan1).
                        // In right-hand running, forward (+tan1) traffic should use edge2, reverse (-tan1) should use edge1.
                        if (lateralOffset > 0f)
                        {
                            edge2.PreferredForward = (dot > 0f);
                            edge1.PreferredForward = false;
                        }
                        else
                        {
                            edge1.PreferredForward = true;
                            edge2.PreferredForward = (dot <= 0f);
                        }
                    }
                }
            }
        }

        public RailEdge GetEdge(RailTrack track)
        {
            if (track == null) return null;
            RailEdge edge;
            _trackToEdge.TryGetValue(track, out edge);
            return edge;
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
            minDistance = float.MaxValue;
            RailEdge bestEdge = null;

            for (int i = 0; i < Edges.Count; i++)
            {
                var edge = Edges[i];
                Vector3 mid = edge.GetMidPoint();
                float d = Vector3.Distance(position, mid);
                if (d < minDistance)
                {
                    minDistance = d;
                    bestEdge = edge;
                }
            }

            return bestEdge;
        }

        public RailNode GetClosestNode(Vector3 position, out float minDistance)
        {
            minDistance = float.MaxValue;
            RailNode bestNode = null;

            for (int i = 0; i < Nodes.Count; i++)
            {
                var node = Nodes[i];
                float d = Vector3.Distance(position, node.Position);
                if (d < minDistance)
                {
                    minDistance = d;
                    bestNode = node;
                }
            }

            return bestNode;
        }

        /// <summary>
        /// Returns all traversable outgoing edges from currentNode when arriving via incomingEdge,
        /// respecting junction geometry and switch constraints.
        /// </summary>
        public List<RailEdge> GetTraversableEdges(RailNode currentNode, RailEdge incomingEdge)
        {
            var result = new List<RailEdge>();
            if (currentNode == null) return result;

            if (incomingEdge == null)
            {
                result.AddRange(currentNode.IncidentEdges);
                return result;
            }

            var junction = currentNode.Junction;
            if (junction == null)
            {
                for (int i = 0; i < currentNode.IncidentEdges.Count; i++)
                {
                    var edge = currentNode.IncidentEdges[i];
                    if (edge != incomingEdge)
                    {
                        result.Add(edge);
                    }
                }
                return result;
            }

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
                            if (edge != null && !result.Contains(edge))
                            {
                                result.Add(edge);
                            }
                        }
                    }
                }
                return result;
            }

            // Trailing move: entering from an outBranch -> can only exit onto inBranch
            if (junction.outBranches != null)
            {
                bool isTrailing = false;
                for (int i = 0; i < junction.outBranches.Count; i++)
                {
                    var branch = junction.outBranches[i];
                    if (branch != null && branch.track == incomingEdge.Track)
                    {
                        isTrailing = true;
                        break;
                    }
                }

                if (isTrailing && junction.inBranch != null && junction.inBranch.track != null)
                {
                    var inEdge = GetEdge(junction.inBranch.track);
                    if (inEdge != null)
                    {
                        result.Add(inEdge);
                        return result;
                    }
                }
            }

            // Fallback for irregular junctions
            for (int i = 0; i < currentNode.IncidentEdges.Count; i++)
            {
                var edge = currentNode.IncidentEdges[i];
                if (edge != incomingEdge)
                {
                    result.Add(edge);
                }
            }

            return result;
        }

        public byte GetRequiredBranch(RailNode junctionNode, RailEdge incomingEdge, RailEdge outgoingEdge)
        {
            if (junctionNode == null || junctionNode.Junction == null || outgoingEdge == null)
                return 0;

            var junction = junctionNode.Junction;

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

        public bool IsTrackOccupied(RailTrack track)
        {
            if (track == null) return false;

            try
            {
                if (RailTrackRegistry.RailTrackToLogicTrack != null)
                {
                    DV.Logic.Job.Track logicTrack;
                    if (RailTrackRegistry.RailTrackToLogicTrack.TryGetValue(track, out logicTrack))
                    {
                        if (logicTrack != null && !logicTrack.IsFree())
                            return true;
                    }
                }
            }
            catch
            {
                // Fallback to internal checks
            }

            return false;
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
    }
}
