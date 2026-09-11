using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace AITraffic.Navigation
{
    /// <summary>
    /// Configuration options for A* rail pathfinding.
    /// </summary>
    public class PathfinderOptions
    {
        public static PathfinderOptions Default
        {
            get { return new PathfinderOptions(); }
        }

        /// <summary>
        /// Allow routing on double-track mainline against the preferred right-hand running direction.
        /// </summary>
        public bool AllowWrongDirection { get; set; }

        /// <summary>
        /// Flat penalty in meters added for each wrong-direction track segment on double-track corridors.
        /// </summary>
        public float WrongDirectionFlatPenalty { get; set; }

        /// <summary>
        /// Cost multiplier on segment length for wrong-direction running.
        /// </summary>
        public float WrongDirectionMultiplier { get; set; }

        /// <summary>
        /// Avoid tracks currently occupied by rolling stock or trains.
        /// </summary>
        public bool AvoidOccupiedTracks { get; set; }

        /// <summary>
        /// Cost penalty added when traversing an occupied track (if StrictlyAvoidOccupied is false).
        /// </summary>
        public float OccupiedTrackPenalty { get; set; }

        /// <summary>
        /// If true, occupied tracks cannot be traversed at all (except start and destination tracks).
        /// </summary>
        public bool StrictlyAvoidOccupied { get; set; }

        /// <summary>
        /// Avoid tracks reserved by other trains or dispatchers.
        /// </summary>
        public bool AvoidReservedTracks { get; set; }

        /// <summary>
        /// Cost penalty added when traversing a track reserved by another requester.
        /// </summary>
        public float ReservedTrackPenalty { get; set; }

        /// <summary>
        /// If true, reserved tracks cannot be traversed at all (except start and destination tracks).
        /// </summary>
        public bool StrictlyAvoidReserved { get; set; }

        /// <summary>
        /// The requester object (e.g. TrainCar or AI train controller) to distinguish own reservations from others.
        /// </summary>
        public object Requester { get; set; }

        /// <summary>
        /// Cost penalty in meters for taking a diverging branch through a switch vs through/straight route.
        /// </summary>
        public float TurnoutDivergingPenalty { get; set; }

        /// <summary>
        /// Cost penalty added for traversing crossover tracks connecting parallel double-track mainlines.
        /// </summary>
        public float CrossoverPenalty { get; set; }

        /// <summary>
        /// Penalty added per meter on yard tracks to prefer mainline routing when travelling between stations.
        /// </summary>
        public float YardTrackPenaltyPerMeter { get; set; }

        /// <summary>
        /// When true, factors estimated travel time (length / speedLimit) into edge traversal costs.
        /// </summary>
        public bool PreferSpeedOverDistance { get; set; }

        /// <summary>
        /// When true, AI trains will never route through passing loops/sidings to overtake the player in the same corridor.
        /// </summary>
        public bool PreventPlayerOvertake { get; set; }

        /// <summary>
        /// Maximum allowable search distance (in meters) before terminating search.
        /// </summary>
        public float MaxSearchDistance { get; set; }

        /// <summary>
        /// Optional Trainset belonging to the requester. Cars belonging to this trainset are ignored during occupancy checks.
        /// </summary>
        public Trainset RequesterTrainset { get; set; }

        /// <summary>
        /// Specific set of tracks completely excluded from pathfinding (hard forbidden).
        /// </summary>
        public HashSet<RailTrack> ExcludedTracks { get; set; }

        /// <summary>
        /// Optional precomputed snapshot of occupied tracks for O(1) checks across multiple searches.
        /// </summary>
        public HashSet<RailTrack> OccupiedTracksSnapshot { get; set; }

        public PathfinderOptions()
        {
            AllowWrongDirection = true;
            WrongDirectionFlatPenalty = 1500f;
            WrongDirectionMultiplier = 3.0f;
            AvoidOccupiedTracks = true;
            OccupiedTrackPenalty = 10000000f;
            StrictlyAvoidOccupied = true;
            AvoidReservedTracks = true;
            ReservedTrackPenalty = 25000f;
            StrictlyAvoidReserved = false;
            PreventPlayerOvertake = true;
            Requester = null;
            RequesterTrainset = null;
            ExcludedTracks = null;
            OccupiedTracksSnapshot = null;
            TurnoutDivergingPenalty = 40f;
            CrossoverPenalty = 0f;
            YardTrackPenaltyPerMeter = 1.5f;
            PreferSpeedOverDistance = true;
            MaxSearchDistance = 5000000f;
        }
    }

    /// <summary>
    /// Represents a junction switch setting along a computed route.
    /// </summary>
    public struct JunctionSwitchAction
    {
        public Junction Junction;
        public byte Branch;

        public JunctionSwitchAction(Junction junction, byte branch)
        {
            Junction = junction;
            Branch = branch;
        }

        public override string ToString()
        {
            return string.Format("{0} -> Branch {1}", Junction != null ? Junction.name : "null", Branch);
        }
    }

    /// <summary>
    /// Represents a computed route through the Derail Valley rail network.
    /// </summary>
    public class RailPath
    {
        public List<RailTrack> Tracks { get; private set; }
        public List<RailEdge> Edges { get; private set; }
        public List<RailNode> Nodes { get; private set; }
        public Dictionary<Junction, byte> JunctionSwitches { get; private set; }
        public List<JunctionSwitchAction> OrderedJunctionSwitches { get; private set; }
        public List<float> SpeedLimits { get; private set; }

        public float TotalDistance { get; private set; }
        public float MinSpeedLimit { get; private set; }
        public float MaxSpeedLimit { get; private set; }
        public float AverageSpeedLimit { get; private set; }
        public float EstimatedTravelTime { get; private set; } // in seconds

        public bool IsValid
        {
            get { return Tracks != null && Tracks.Count > 0; }
        }

        public RailPath(
            List<RailTrack> tracks,
            List<RailEdge> edges,
            List<RailNode> nodes,
            Dictionary<Junction, byte> junctionSwitches,
            List<JunctionSwitchAction> orderedJunctionSwitches,
            List<float> speedLimits,
            float totalDistance)
        {
            Tracks = tracks ?? new List<RailTrack>();
            Edges = edges ?? new List<RailEdge>();
            Nodes = nodes ?? new List<RailNode>();
            JunctionSwitches = junctionSwitches ?? new Dictionary<Junction, byte>();
            OrderedJunctionSwitches = orderedJunctionSwitches ?? new List<JunctionSwitchAction>();
            SpeedLimits = speedLimits ?? new List<float>();
            TotalDistance = totalDistance;

            if (SpeedLimits.Count > 0)
            {
                MinSpeedLimit = SpeedLimits.Min();
                MaxSpeedLimit = SpeedLimits.Max();

                float totalTime = 0f;
                float weightedSpeedSum = 0f;

                for (int i = 0; i < Edges.Count && i < SpeedLimits.Count; i++)
                {
                    float len = Edges[i].Length;
                    float speedKmh = Mathf.Max(10f, SpeedLimits[i]);
                    float speedMs = speedKmh / 3.6f;

                    totalTime += len / speedMs;
                    weightedSpeedSum += speedKmh * len;
                }

                EstimatedTravelTime = totalTime;
                AverageSpeedLimit = TotalDistance > 0.01f ? weightedSpeedSum / TotalDistance : SpeedLimits[0];
            }
            else
            {
                MinSpeedLimit = 120f;
                MaxSpeedLimit = 120f;
                AverageSpeedLimit = 120f;
                EstimatedTravelTime = TotalDistance / (120f / 3.6f);
            }
        }

        public bool ContainsTrack(RailTrack track)
        {
            if (track == null || Tracks == null) return false;
            return Tracks.Contains(track);
        }

        public bool TryGetBranch(Junction junction, out byte branch)
        {
            branch = 0;
            if (junction == null || JunctionSwitches == null) return false;
            return JunctionSwitches.TryGetValue(junction, out branch);
        }

        public float GetDistanceToTrack(RailTrack track)
        {
            if (track == null || Tracks == null) return -1f;

            float dist = 0f;
            for (int i = 0; i < Tracks.Count; i++)
            {
                if (Tracks[i] == track) return dist;
                if (i < Edges.Count) dist += Edges[i].Length;
            }

            return -1f;
        }

        public float GetDistanceToJunction(Junction junction)
        {
            if (junction == null || Nodes == null) return -1f;

            float dist = 0f;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i] != null && Nodes[i].Junction == junction) return dist;
                if (i < Edges.Count) dist += Edges[i].Length;
            }

            return -1f;
        }

        public RailTrack GetNextTrack(RailTrack currentTrack)
        {
            if (currentTrack == null || Tracks == null) return null;
            int idx = Tracks.IndexOf(currentTrack);
            if (idx >= 0 && idx + 1 < Tracks.Count)
            {
                return Tracks[idx + 1];
            }
            return null;
        }

        public Junction GetNextJunction(RailTrack currentTrack)
        {
            if (currentTrack == null || Tracks == null) return null;
            int idx = Tracks.IndexOf(currentTrack);
            if (idx >= 0 && idx + 1 < Nodes.Count)
            {
                for (int i = idx + 1; i < Nodes.Count; i++)
                {
                    if (Nodes[i] != null && Nodes[i].Junction != null)
                        return Nodes[i].Junction;
                }
            }
            return null;
        }

        public override string ToString()
        {
            return string.Format("RailPath[Tracks={0}, Switches={1}, Dist={2:F1}m, Time={3:F1}s, AvgSpeed={4:F1}km/h]",
                Tracks.Count, OrderedJunctionSwitches.Count, TotalDistance, EstimatedTravelTime, AverageSpeedLimit);
        }
    }

    /// <summary>
    /// A* shortest-path algorithm for rail network pathfinding.
    /// Supports turnouts, double-track mainline corridor preferences, occupancy/reservation avoidance, and speed limits.
    /// </summary>
    public class Pathfinder
    {
        private readonly RailGraph _graph;
        private readonly MinHeap<SearchNode> _openSet = new MinHeap<SearchNode>();
        private readonly Dictionary<long, float> _nodeBestCost = new Dictionary<long, float>();
        private readonly HashSet<RailTrack> _requesterTracks = new HashSet<RailTrack>();
        private readonly HashSet<RailNode> _targetNodes = new HashSet<RailNode>();

        public Pathfinder(RailGraph graph = null)
        {
            _graph = graph ?? RailGraph.Instance ?? new RailGraph();
        }

        private static void LogWarning(string msg)
        {
            Debug.LogWarning("[AITraffic] " + msg);
        }

        /// <summary>
        /// Finds the shortest route from start track to destination track.
        /// </summary>
        public RailPath FindPath(RailTrack startTrack, RailTrack destinationTrack, bool startForward = true, PathfinderOptions options = null)
        {
            if (startTrack == null || destinationTrack == null)
            {
                LogWarning("[Pathfinder] FindPath called with null startTrack or destinationTrack.");
                return null;
            }

            if (!_graph.IsInitialized)
            {
                _graph.Initialize();
            }

            var startEdge = _graph.GetEdge(startTrack);
            var destEdge = _graph.GetEdge(destinationTrack);

            if (startEdge == null || destEdge == null)
            {
                LogWarning(string.Format("[Pathfinder] Could not resolve edges for tracks '{0}' -> '{1}'.", startTrack.name, destinationTrack.name));
                return null;
            }

            if (startTrack == destinationTrack)
            {
                return CreateSingleTrackPath(startEdge);
            }

            options = options ?? PathfinderOptions.Default;

            RailNode startNode = startForward ? startEdge.ToNode : startEdge.FromNode;
            return RunAStar(startEdge, startNode, destEdge, options);
        }

        /// <summary>
        /// Finds the shortest route from start track to destination track searching both forward and reverse headings.
        /// </summary>
        public RailPath FindPath(RailTrack startTrack, RailTrack destinationTrack, PathfinderOptions options)
        {
            if (startTrack == null || destinationTrack == null) return null;

            var pathForward = FindPath(startTrack, destinationTrack, true, options);
            var pathReverse = FindPath(startTrack, destinationTrack, false, options);

            if (pathForward == null) return pathReverse;
            if (pathReverse == null) return pathForward;

            return pathForward.TotalDistance <= pathReverse.TotalDistance ? pathForward : pathReverse;
        }

        /// <summary>
        /// Finds the shortest route between two rail nodes.
        /// </summary>
        public RailPath FindPath(RailNode startNode, RailNode destinationNode, PathfinderOptions options = null)
        {
            if (startNode == null || destinationNode == null) return null;

            if (!_graph.IsInitialized)
            {
                _graph.Initialize();
            }

            if (startNode == destinationNode)
            {
                return new RailPath(new List<RailTrack>(), new List<RailEdge>(), new List<RailNode> { startNode }, new Dictionary<Junction, byte>(), new List<JunctionSwitchAction>(), new List<float>(), 0f);
            }

            options = options ?? PathfinderOptions.Default;
            return RunAStar(null, startNode, destinationNode, options);
        }

        /// <summary>
        /// Builds a RailPath directly from an ordered contiguous list of RailTracks.
        /// </summary>
        public RailPath BuildPathFromTracks(List<RailTrack> tracks)
        {
            if (tracks == null || tracks.Count == 0) return null;

            if (!_graph.IsInitialized)
            {
                _graph.Initialize();
            }

            var pathTracks = new List<RailTrack>();
            var pathEdges = new List<RailEdge>();
            var pathNodes = new List<RailNode>();
            var junctionSwitches = new Dictionary<Junction, byte>();
            var orderedSwitches = new List<JunctionSwitchAction>();
            var speedLimits = new List<float>();
            float totalDist = 0f;

            for (int i = 0; i < tracks.Count; i++)
            {
                var trk = tracks[i];
                if (trk == null) continue;

                var edge = _graph.GetEdge(trk);
                if (edge == null) continue;

                pathTracks.Add(trk);
                pathEdges.Add(edge);
                speedLimits.Add(edge.SpeedLimit);
                totalDist += edge.Length;

                if (i == 0)
                {
                    if (tracks.Count > 1)
                    {
                        var nextEdge = _graph.GetEdge(tracks[1]);
                        var sharedNode = _graph.GetConnectingNode(edge, nextEdge);
                        var startNode = (sharedNode == edge.ToNode) ? edge.FromNode : edge.ToNode;
                        if (startNode != null) pathNodes.Add(startNode);
                        if (sharedNode != null) pathNodes.Add(sharedNode);
                    }
                    else
                    {
                        if (edge.FromNode != null) pathNodes.Add(edge.FromNode);
                        if (edge.ToNode != null) pathNodes.Add(edge.ToNode);
                    }
                }
                else
                {
                    var prevEdge = pathEdges[pathEdges.Count - 2];
                    var sharedNode = _graph.GetConnectingNode(prevEdge, edge);
                    var otherNode = (sharedNode == edge.FromNode) ? edge.ToNode : edge.FromNode;
                    if (otherNode != null) pathNodes.Add(otherNode);

                    if (sharedNode != null && sharedNode.Junction != null)
                    {
                        byte requiredBranch = _graph.GetRequiredBranch(sharedNode, prevEdge, edge);
                        junctionSwitches[sharedNode.Junction] = requiredBranch;
                        orderedSwitches.Add(new JunctionSwitchAction(sharedNode.Junction, requiredBranch));
                    }
                }
            }

            if (pathTracks.Count == 0) return null;
            return new RailPath(pathTracks, pathEdges, pathNodes, junctionSwitches, orderedSwitches, speedLimits, totalDist);
        }

        /// <summary>
        /// Finds the shortest route between two world positions by snapping to the closest tracks.
        /// </summary>
        public RailPath FindPath(Vector3 startPosition, Vector3 destinationPosition, PathfinderOptions options = null)
        {
            if (!_graph.IsInitialized)
            {
                _graph.Initialize();
            }

            float d1, d2;
            var startEdge = _graph.GetClosestEdge(startPosition, out d1);
            var destEdge = _graph.GetClosestEdge(destinationPosition, out d2);

            if (startEdge == null || destEdge == null)
            {
                LogWarning("[Pathfinder] Could not snap start/destination position to rail graph.");
                return null;
            }

            return FindPath(startEdge.Track, destEdge.Track, options ?? PathfinderOptions.Default);
        }

        /// <summary>
        /// Finds the shortest route for a train car taking its current orientation into account.
        /// </summary>
        public RailPath FindPath(TrainCar trainCar, RailTrack destinationTrack, PathfinderOptions options = null)
        {
            if (trainCar == null || destinationTrack == null) return null;

            RailTrack currentTrack = null;
            if (trainCar.RearBogie != null) currentTrack = trainCar.RearBogie.track;
            if (currentTrack == null && trainCar.FrontBogie != null) currentTrack = trainCar.FrontBogie.track;

            if (currentTrack == null) return null;

            options = options ?? new PathfinderOptions { Requester = trainCar };

            bool forward = true;
            if (trainCar.FrontBogie != null && trainCar.RearBogie != null && currentTrack.curve != null)
            {
                Vector3 carForward = trainCar.transform.forward;
                Vector3 trackTangent = currentTrack.curve.GetTangentAt(0.5f).normalized;
                forward = Vector3.Dot(carForward, trackTangent) >= 0f;
            }

            return FindPath(currentTrack, destinationTrack, forward, options);
        }

        private RailPath CreateSingleTrackPath(RailEdge edge)
        {
            var tracks = new List<RailTrack> { edge.Track };
            var edges = new List<RailEdge> { edge };
            var nodes = new List<RailNode> { edge.FromNode, edge.ToNode };
            var speedLimits = new List<float> { edge.SpeedLimit };

            return new RailPath(
                tracks,
                edges,
                nodes,
                new Dictionary<Junction, byte>(),
                new List<JunctionSwitchAction>(),
                speedLimits,
                edge.Length
            );
        }

        private RailPath RunAStar(RailEdge initialEdge, RailNode startNode, RailEdge destEdge, PathfinderOptions options)
        {
            _targetNodes.Clear();
            if (destEdge.FromNode != null) _targetNodes.Add(destEdge.FromNode);
            if (destEdge.ToNode != null) _targetNodes.Add(destEdge.ToNode);

            Vector3 targetPosition = destEdge.GetMidPoint();
            return ExecuteAStarCore(initialEdge, startNode, targetPosition, destEdge, _targetNodes, options);
        }

        private RailPath RunAStar(RailEdge initialEdge, RailNode startNode, RailNode destNode, PathfinderOptions options)
        {
            _targetNodes.Clear();
            _targetNodes.Add(destNode);
            Vector3 targetPosition = destNode.Position;
            return ExecuteAStarCore(initialEdge, startNode, targetPosition, null, _targetNodes, options);
        }

        private static long GetStateKey(int nodeId, int incomingEdgeId)
        {
            return ((long)nodeId << 32) | (uint)incomingEdgeId;
        }

        private RailPath ExecuteAStarCore(
            RailEdge initialEdge,
            RailNode startNode,
            Vector3 targetPosition,
            RailEdge destEdge,
            HashSet<RailNode> targetNodes,
            PathfinderOptions options)
        {
            _openSet.Clear();
            var openSet = _openSet;
            _nodeBestCost.Clear();
            var nodeBestCost = _nodeBestCost;

            // Pre-index requester's own cars into a HashSet<RailTrack> for O(1) checks during A*
            HashSet<RailTrack> requesterTracks = null;
            if (options.RequesterTrainset != null && options.RequesterTrainset.cars != null)
            {
                _requesterTracks.Clear();
                requesterTracks = _requesterTracks;
                for (int c = 0; c < options.RequesterTrainset.cars.Count; c++)
                {
                    var car = options.RequesterTrainset.cars[c];
                    if (car == null) continue;
                    if (car.FrontBogie != null && car.FrontBogie.track != null) requesterTracks.Add(car.FrontBogie.track);
                    if (car.RearBogie != null && car.RearBogie.track != null) requesterTracks.Add(car.RearBogie.track);
                }
            }

            // Ensure occupied tracks snapshot is available for O(1) checks during A*
            HashSet<RailTrack> occupiedSnapshot = options.OccupiedTracksSnapshot ?? RailGraph.BuildOccupiedTracksSnapshot(options.RequesterTrainset);

            float startG = initialEdge != null ? initialEdge.Length : 0f;
            float startH = Vector3.Distance(startNode.Position, targetPosition);
            var startSearchNode = new SearchNode
            {
                Node = startNode,
                IncomingEdge = initialEdge,
                GScore = startG,
                HScore = startH,
                FScore = startG + startH,
                Parent = null,
                EdgeFromParent = initialEdge,
                SwitchBranch = 0
            };

            openSet.Push(startSearchNode);
            nodeBestCost[GetStateKey(startNode.Id, initialEdge != null ? initialEdge.Id : 0)] = startSearchNode.GScore;

            SearchNode goalNode = null;

            int exploredNodes = 0;
            int prunedByDist = 0;
            int prunedByBestG = 0;
            float maxGScoreReached = 0f;

            while (openSet.Count > 0)
            {
                var current = openSet.Pop();
                exploredNodes++;

                if (current.GScore > maxGScoreReached)
                    maxGScoreReached = current.GScore;

                // Check destination reached
                if (destEdge != null && current.IncomingEdge == destEdge)
                {
                    goalNode = current;
                    break;
                }

                if (destEdge == null && targetNodes.Contains(current.Node))
                {
                    goalNode = current;
                    break;
                }

                if (current.GScore > options.MaxSearchDistance)
                {
                    prunedByDist++;
                    continue;
                }

                long currentKey = GetStateKey(current.Node.Id, current.IncomingEdge != null ? current.IncomingEdge.Id : 0);
                float bestG;
                if (nodeBestCost.TryGetValue(currentKey, out bestG) && current.GScore > bestG + 0.01f)
                {
                    prunedByBestG++;
                    continue;
                }

                var traversableEdges = _graph.GetTraversableEdges(current.Node, current.IncomingEdge);

                for (int i = 0; i < traversableEdges.Count; i++)
                {
                    var edge = traversableEdges[i];
                    if (edge == null || edge == current.IncomingEdge) continue;

                    var nextNode = edge.GetOtherNode(current.Node);
                    if (nextNode == null) continue;

                    float traversalCost;
                    byte requiredBranch;
                    if (!EvaluateEdgeCost(edge, current.Node, nextNode, current.IncomingEdge, initialEdge, destEdge, options, occupiedSnapshot, requesterTracks, out traversalCost, out requiredBranch))
                    {
                        continue;
                    }

                    float tentativeGScore = current.GScore + traversalCost;
                    long nextKey = GetStateKey(nextNode.Id, edge.Id);

                    float existingG;
                    if (nodeBestCost.TryGetValue(nextKey, out existingG) && tentativeGScore >= existingG)
                    {
                        continue;
                    }

                    nodeBestCost[nextKey] = tentativeGScore;

                    float hScore = Vector3.Distance(nextNode.Position, targetPosition);
                    var nextSearchNode = new SearchNode
                    {
                        Node = nextNode,
                        IncomingEdge = edge,
                        GScore = tentativeGScore,
                        HScore = hScore,
                        FScore = tentativeGScore + hScore,
                        Parent = current,
                        EdgeFromParent = edge,
                        SwitchBranch = requiredBranch
                    };

                    openSet.Push(nextSearchNode);
                }
            }

            if (goalNode == null)
            {
                if (AITraffic.Main.ModEntry != null && AITraffic.Main.ModEntry.Logger != null)
                {
                    string startTrk = initialEdge != null && initialEdge.Track != null ? initialEdge.Track.name : "null";
                    string destTrk = destEdge != null && destEdge.Track != null ? destEdge.Track.name : "null";
                    AITraffic.Main.ModEntry.Logger.Log(string.Format("[Pathfinder] Route search failed: '{0}' -> '{1}'. Explored: {2}, OpenRemaining: {3}, MaxG: {4:F0}, PrunedDist: {5}, PrunedBestG: {6}.",
                        startTrk, destTrk, exploredNodes, openSet.Count, maxGScoreReached, prunedByDist, prunedByBestG));
                }
                return null;
            }

            var resultPath = ReconstructPath(goalNode, initialEdge);
            if (resultPath != null && AITraffic.Main.ModEntry != null && AITraffic.Main.ModEntry.Logger != null)
            {
                string startTrk = initialEdge != null && initialEdge.Track != null ? initialEdge.Track.name : "null";
                string destTrk = destEdge != null && destEdge.Track != null ? destEdge.Track.name : "null";
                AITraffic.Main.ModEntry.Logger.Log(string.Format("[Pathfinder] Route search succeeded: '{0}' -> '{1}'. Explored: {2}, Dist: {3:F0}m, Tracks: {4}.",
                    startTrk, destTrk, exploredNodes, resultPath.TotalDistance, resultPath.Tracks != null ? resultPath.Tracks.Count : 0));
            }
            return resultPath;
        }

        private bool EvaluateEdgeCost(
            RailEdge edge,
            RailNode fromNode,
            RailNode toNode,
            RailEdge incomingEdge,
            RailEdge initialEdge,
            RailEdge destEdge,
            PathfinderOptions options,
            HashSet<RailTrack> occupiedSnapshot,
            HashSet<RailTrack> requesterTracks,
            out float cost,
            out byte requiredBranch)
        {
            cost = 0f;
            requiredBranch = 0;

            if (edge.Track == null) return false;

            // 0. Hard-excluded tracks
            if (options.ExcludedTracks != null && options.ExcludedTracks.Contains(edge.Track))
            {
                return false;
            }

            // Junction branch alignment
            if (fromNode.Junction != null)
            {
                requiredBranch = _graph.GetRequiredBranch(fromNode, incomingEdge, edge);
            }

            bool isInitial = (initialEdge != null && edge == initialEdge);
            bool isStartOrDest = (destEdge != null && edge == destEdge) || isInitial;

            // Check if track is occupied by requester's own train cars (O(1))
            bool isOccupiedByRequester = (requesterTracks != null && requesterTracks.Contains(edge.Track));

            // Occupancy checks:
            // Intermediate tracks that are physically occupied MUST NEVER be chosen.
            if (options.AvoidOccupiedTracks && !isStartOrDest && !isOccupiedByRequester)
            {
                bool isOccupied = _graph.IsTrackOccupied(edge.Track, occupiedSnapshot, options.RequesterTrainset);
                if (isOccupied)
                {
                    if (options.StrictlyAvoidOccupied)
                        return false;

                    // Insurmountable penalty (+10,000,000m) to guarantee an occupied track is NEVER chosen
                    // over an empty siding or bypass track
                    cost += options.OccupiedTrackPenalty;
                }
            }

            // Reservation checks
            if (options.AvoidReservedTracks && !isInitial)
            {
                bool isReservedByOther = _graph.IsTrackReservedByOther(edge.Track, options.Requester);
                if (isReservedByOther)
                {
                    if (options.StrictlyAvoidReserved)
                        return false;

                    cost += options.ReservedTrackPenalty;
                }
            }

            // Direct Player Track Occupancy Avoidance (never applies to start/dest, requester's own train, or AI workers)
            var eng = options.Requester as AITraffic.Driver.AIEngineer;
            bool isWorkerOrPlayerLoco = options.RequesterTrainset != null ||
                                        options.Requester is TrainCar ||
                                        (eng != null && eng.IsWorkerDriven);

            if (options.PreventPlayerOvertake && !isWorkerOrPlayerLoco && !isStartOrDest && !isOccupiedByRequester)
            {
                if (SignalRegistry.IsTrackOccupiedByPlayer(edge.Track, options.RequesterTrainset))
                {
                    cost += 2000000f;
                }
            }

            // Base length
            float baseDistance = edge.Length;
            cost += baseDistance;

            // Speed preference
            if (options.PreferSpeedOverDistance)
            {
                float speedKmh = Mathf.Max(15f, edge.SpeedLimit);
                float speedFactor = 120f / speedKmh; // 1.0 at 120km/h, 4.0 at 30km/h
                cost += baseDistance * (speedFactor - 1f) * 0.5f;
            }

            string trackName = edge.Track.name ?? string.Empty;

            // City West (CW / CSW) Station Avoidance:
            // City West contains dead-end passenger terminals and tight yard ladders.
            // Trains traveling across the valley MUST use the mainline bypass (DT-CWNAA, DT-CWSAA, or mainline bypass)
            // and must NEVER cut into the City West terminal station tracks unless City West is the start or final destination!
            bool isCWTrack = trackName.StartsWith("[CW", StringComparison.OrdinalIgnoreCase) ||
                             trackName.StartsWith("[CSW", StringComparison.OrdinalIgnoreCase) ||
                             trackName.IndexOf("CityWest", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             trackName.IndexOf("CitySouthWest", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isCWTrack && !isStartOrDest)
            {
                bool isBypass = trackName.IndexOf("DT-CWNAA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                trackName.IndexOf("DT-CWSAA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                trackName.IndexOf("[#]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                trackName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!isBypass)
                {
                    // Severely penalize entering City West terminal station tracks for through-trains
                    cost += 500000f;
                }
            }

            // Station Ladders, Passing Sidings & Industrial Yard Handling:
            Vector3 edgeMid = edge.GetMidPoint();
            bool isApproachingDest = (destEdge != null && Vector3.Distance(edgeMid, destEdge.GetMidPoint()) < 350f);
            bool isLeavingOrigin = (initialEdge != null && Vector3.Distance(edgeMid, initialEdge.GetMidPoint()) < 350f);
            bool isImmediateLadder = isApproachingDest || isLeavingOrigin;

            // Passing loops and sidings [S]: valid secondary through-tracks.
            // Apply a modest preference penalty (+350m) when not approaching origin/destination,
            // so through-trains prefer the mainline [#] when clear, but will seamlessly take
            // the passing siding if the mainline is occupied, reserved, or blocked.
            bool isPassingLoop = IsPassingOrSidingTrack(edge.Track);
            if (isPassingLoop && !isStartOrDest && !isImmediateLadder)
            {
                cost += 350f;
            }

            // Industrial Yard & Storage Track Avoidance for Through-Trains:
            // Through-trains must NEVER cut through intermediate storage [Y], loading [L], caboose [C],
            // engine [E], turntable [T], or transfer [I]/[O] tracks.
            bool isIndustrialYard = IsIndustrialYardOrStorageTrack(edge.Track);
            if (isIndustrialYard && !isStartOrDest)
            {
                if (!isImmediateLadder)
                {
                    // Massive through-transit penalty to strictly force through-trains onto mainline bypasses
                    cost += 500000f + (baseDistance * 20f);
                }
                else
                {
                    cost += baseDistance * options.YardTrackPenaltyPerMeter;
                }
            }

            // Mainline Through-Track Preference Discount (favors [#], DT-, Platform [P], and mainlines)
            bool isMainline = trackName.Contains("[#]") ||
                              trackName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              trackName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              trackName.IndexOf("ML", StringComparison.OrdinalIgnoreCase) >= 0 ||
                              trackName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase);

            if (isMainline)
            {
                cost *= 0.85f; // 15% preference discount for mainline corridors
            }

            // Prevent AI trains from taking passing sidings/loops to overtake the player in the same corridor
            if (options.PreventPlayerOvertake && options.Requester is AITraffic.Driver.AIEngineer && !isStartOrDest && !isApproachingDest)
            {
                if (isPassingLoop)
                {
                    Vector3 pPos;
                    Trainset pSet;
                    float pSpeed;
                    if (SignalRegistry.TryGetPlayerTrainInfo(out pSet, out pPos, out pSpeed))
                    {
                        if (Vector3.Distance(edge.GetMidPoint(), pPos) < 1500f)
                        {
                            cost += 500000f;
                        }
                    }
                }
            }

            // Forward Angular Continuity Check
            // A train arriving at fromNode cannot make an acute-angle hairpin U-turn (> 90 degrees) onto outgoing edge
            if (incomingEdge != null)
            {
                Vector3 inDir = -incomingEdge.GetDirection(fromNode); // Arriving direction of travel
                Vector3 candidateDir = edge.GetDirection(fromNode);   // Departing direction of travel
                inDir.y = 0f;
                inDir.Normalize();
                candidateDir.y = 0f;
                candidateDir.Normalize();
                float alignment = Vector3.Dot(inDir, candidateDir);

                if (alignment <= 0.0f)
                {
                    // Acute angle turnback / hairpin U-turn (> 90 degrees): physically impossible for railway equipment!
                    return false;
                }
            }

            // Crossover Track Penalty:
            // Severely disfavor unnecessary jumping across crossovers between parallel double-track mainlines
            bool isCrossover = (trackName.IndexOf("CX", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                trackName.IndexOf("Cross", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                trackName.IndexOf("Xing", StringComparison.OrdinalIgnoreCase) >= 0);

            if (!isCrossover && incomingEdge != null && incomingEdge.IsDoubleTrackMainline && edge.IsDoubleTrackMainline)
            {
                if (edge != incomingEdge && (edge.ParallelEdge == incomingEdge || incomingEdge.ParallelEdge == edge))
                {
                    isCrossover = true;
                }
            }

            if (isCrossover && !isStartOrDest && options.CrossoverPenalty > 0f)
            {
                cost += options.CrossoverPenalty;
            }

            // Diverging turnout penalty
            if (fromNode.Junction != null && requiredBranch > 0)
            {
                cost += options.TurnoutDivergingPenalty;
            }

            // Right-Hand Running Preference at Diverging Junctions
            if (incomingEdge != null && fromNode.IncidentEdges != null && fromNode.IncidentEdges.Count > 2)
            {
                Vector3 inDir = -incomingEdge.GetDirection(fromNode);
                Vector3 rightNormal = Vector3.Cross(Vector3.up, inDir).normalized;

                Vector3 candidateDir = edge.GetDirection(fromNode);
                float candidateLateral = Vector3.Dot(candidateDir, rightNormal);

                // Find lateral spread of alternative outgoing edges from this node
                float maxLateral = float.MinValue;
                float minLateral = float.MaxValue;
                for (int b = 0; b < fromNode.IncidentEdges.Count; b++)
                {
                    var altEdge = fromNode.IncidentEdges[b];
                    if (altEdge == null || altEdge == incomingEdge) continue;

                    Vector3 altDir = altEdge.GetDirection(fromNode);
                    float altLat = Vector3.Dot(altDir, rightNormal);
                    if (altLat > maxLateral) maxLateral = altLat;
                    if (altLat < minLateral) minLateral = altLat;
                }

                if (maxLateral - minLateral > 0.05f)
                {
                    // If this candidate is NOT the rightmost track option, apply a non-negative penalty to left tracks
                    if (Mathf.Abs(candidateLateral - maxLateral) >= 0.02f)
                    {
                        cost += 300f; // Disfavor left-hand track when a right-hand option is available
                    }
                }
            }

            // Double track wrong-way running penalty (non-negative)
            if (edge.IsDoubleTrackMainline && edge.ParallelEdge != null)
            {
                bool traversingForward = edge.IsForward(fromNode, toNode);
                bool isCorrectDirection = (traversingForward == edge.PreferredForward);

                if (!isCorrectDirection)
                {
                    if (!options.AllowWrongDirection)
                        return false;

                    cost += options.WrongDirectionFlatPenalty + (baseDistance * options.WrongDirectionMultiplier);
                }
            }

            return true;
        }

        private RailPath ReconstructPath(SearchNode goalNode, RailEdge initialEdge)
        {
            var tracks = new List<RailTrack>();
            var edges = new List<RailEdge>();
            var nodes = new List<RailNode>();
            var speedLimits = new List<float>();
            var junctionSwitches = new Dictionary<Junction, byte>();
            var orderedSwitches = new List<JunctionSwitchAction>();

            var current = goalNode;
            float totalDist = 0f;

            var pathNodes = new List<SearchNode>();
            while (current != null)
            {
                pathNodes.Add(current);
                current = current.Parent;
            }
            pathNodes.Reverse();

            for (int i = 0; i < pathNodes.Count; i++)
            {
                var step = pathNodes[i];
                if (i == 0 && step.EdgeFromParent == null)
                {
                    nodes.Add(step.Node);
                    continue;
                }

                var edge = step.EdgeFromParent;
                if (edge != null)
                {
                    tracks.Add(edge.Track);
                    edges.Add(edge);
                    speedLimits.Add(edge.SpeedLimit);
                    totalDist += edge.Length;

                    if (nodes.Count == 0)
                    {
                        var originNode = edge.GetOtherNode(step.Node);
                        nodes.Add(originNode ?? edge.FromNode);
                    }
                    nodes.Add(step.Node);

                    var prevNode = (i > 0) ? pathNodes[i - 1].Node : null;
                    if (prevNode != null && prevNode.Junction != null)
                    {
                        var junction = prevNode.Junction;
                        byte branch = step.SwitchBranch;
                        junctionSwitches[junction] = branch;
                        orderedSwitches.Add(new JunctionSwitchAction(junction, branch));
                    }
                }
            }

            return new RailPath(tracks, edges, nodes, junctionSwitches, orderedSwitches, speedLimits, totalDist);
        }

        private static HashSet<string> s_cachedStationYardGONames = null;
        private static readonly object s_yardGONamesLock = new object();

        private static HashSet<string> GetStationYardGONames()
        {
            if (s_cachedStationYardGONames != null) return s_cachedStationYardGONames;
            lock (s_yardGONamesLock)
            {
                if (s_cachedStationYardGONames != null) return s_cachedStationYardGONames;
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (StationController.allStations != null)
                {
                    for (int s = 0; s < StationController.allStations.Count; s++)
                    {
                        var station = StationController.allStations[s];
                        if (station == null) continue;
                        if (station.storageRailtracksGONames != null)
                        {
                            for (int i = 0; i < station.storageRailtracksGONames.Count; i++)
                                set.Add(station.storageRailtracksGONames[i]);
                        }
                        if (station.transferInRailtracksGONames != null)
                        {
                            for (int i = 0; i < station.transferInRailtracksGONames.Count; i++)
                                set.Add(station.transferInRailtracksGONames[i]);
                        }
                        if (station.transferOutRailtracksGONames != null)
                        {
                            for (int i = 0; i < station.transferOutRailtracksGONames.Count; i++)
                                set.Add(station.transferOutRailtracksGONames[i]);
                        }
                    }
                }
                s_cachedStationYardGONames = set;
                return s_cachedStationYardGONames;
            }
        }

        /// <summary>
        /// Determines whether a given track is a passing loop, station siding, or bypass track
        /// designed for through-trains to meet or pass around traffic.
        /// </summary>
        public static bool IsPassingOrSidingTrack(RailTrack track)
        {
            if (track == null) return false;
            string tName = track.name ?? string.Empty;

            // Mainline [#], doubletrack, and platform tracks are not sidings
            if (tName.Contains("[#]") ||
                tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("ML", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Siding / passing loop keywords
            if (tName.StartsWith("[S]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("_[S]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.EndsWith("-S]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("_S_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Siding", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Loop", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Pass", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Ensure it's not actually an industrial storage [Y], loading [L], or transfer track
                if (!tName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) &&
                    !tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) &&
                    !tName.StartsWith("[I]", StringComparison.OrdinalIgnoreCase) &&
                    !tName.StartsWith("[O]", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Determines whether a track is an industrial storage, loading/unloading, caboose,
        /// engine servicing, or transfer track that through-trains must strictly avoid.
        /// </summary>
        public static bool IsIndustrialYardOrStorageTrack(RailTrack track)
        {
            if (track == null) return false;
            string tName = track.name ?? string.Empty;

            // Mainline through tracks and passenger platforms are exempt
            if (tName.Contains("[#]") ||
                tName.IndexOf("DT-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("ML", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.StartsWith("[P]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("Platform", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Pax", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            // Check game LogicTrack ID classification
            var logicTrack = AITraffic.Compat.ModCompatManager.GetLogicTrack(track);
            if (logicTrack != null && logicTrack.ID != null)
            {
                string part = logicTrack.ID.TrackPartOnly ?? string.Empty;
                string signPart = logicTrack.ID.SignIDTrackPart ?? string.Empty;
                string display = logicTrack.ID.FullDisplayID ?? string.Empty;

                if (part == DV.Logic.Job.TrackID.MAIN_LINE_TYPE || display.Contains("[#]"))
                    return false;

                if (signPart.EndsWith(DV.Logic.Job.TrackID.STORAGE_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    signPart.EndsWith(DV.Logic.Job.TrackID.LOADING_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    signPart.EndsWith(DV.Logic.Job.TrackID.REGULAR_IN_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    signPart.EndsWith(DV.Logic.Job.TrackID.REGULAR_OUT_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    signPart.EndsWith(DV.Logic.Job.TrackID.PARKING_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    part.EndsWith(DV.Logic.Job.TrackID.STORAGE_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    part.EndsWith(DV.Logic.Job.TrackID.LOADING_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    part.EndsWith(DV.Logic.Job.TrackID.REGULAR_IN_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    part.EndsWith(DV.Logic.Job.TrackID.REGULAR_OUT_TYPE, StringComparison.OrdinalIgnoreCase) ||
                    part.EndsWith(DV.Logic.Job.TrackID.PARKING_TYPE, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // Check cached station yard GONames (O(1))
            if (GetStationYardGONames().Contains(tName))
                return true;

            // Yard & siding prefix and keyword heuristics:
            // [Y] (Yard Storage), [L] (Loading), [C] (Caboose/Storage),
            // [I] (Inbound Transfer), [O] (Outbound Transfer), [E] (Engine), [B] (Service), [T] (Turntable)
            if (tName.StartsWith("[Y]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[L]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[C]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[I]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[O]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[E]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[B]", StringComparison.OrdinalIgnoreCase) ||
                tName.StartsWith("[T]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("_[Y]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("_[L]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("_[I]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("_[O]", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.EndsWith("-L]", StringComparison.OrdinalIgnoreCase) ||
                tName.EndsWith("-I]", StringComparison.OrdinalIgnoreCase) ||
                tName.EndsWith("-O]", StringComparison.OrdinalIgnoreCase) ||
                tName.IndexOf("_Y_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Spur", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Storage", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Stub", StringComparison.OrdinalIgnoreCase) >= 0 ||
                tName.IndexOf("Shunt", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Determines whether a given <see cref="RailTrack"/> is a yard, siding, shunting, loading,
        /// storage, or transfer track.
        /// </summary>
        public static bool IsYardOrShuntingTrack(RailTrack track)
        {
            return IsPassingOrSidingTrack(track) || IsIndustrialYardOrStorageTrack(track);
        }

        /// <summary>
        /// Determines whether an edge is a yard, siding, shunting, loading, or transfer track.
        /// </summary>
        public static bool IsYardOrShuntingTrack(RailEdge edge)
        {
            if (edge == null || edge.Track == null) return false;
            if (edge.IsYardTrack) return true;
            return IsYardOrShuntingTrack(edge.Track);
        }

        private class SearchNode : IComparable<SearchNode>
        {
            public RailNode Node;
            public RailEdge IncomingEdge;
            public float GScore;
            public float HScore;
            public float FScore;
            public SearchNode Parent;
            public RailEdge EdgeFromParent;
            public byte SwitchBranch;

            public int CompareTo(SearchNode other)
            {
                if (other == null) return 1;
                return FScore.CompareTo(other.FScore);
            }
        }

        /// <summary>
        /// Lightweight binary min-heap priority queue for optimal A* search performance.
        /// </summary>
        private class MinHeap<T> where T : IComparable<T>
        {
            private readonly List<T> _elements = new List<T>();

            public int Count
            {
                get { return _elements.Count; }
            }

            public void Clear()
            {
                _elements.Clear();
            }

            public void Push(T item)
            {
                _elements.Add(item);
                int i = _elements.Count - 1;
                while (i > 0)
                {
                    int parent = (i - 1) / 2;
                    if (_elements[i].CompareTo(_elements[parent]) >= 0) break;
                    Swap(i, parent);
                    i = parent;
                }
            }

            public T Pop()
            {
                if (_elements.Count == 0) throw new InvalidOperationException("Heap is empty");
                T root = _elements[0];
                int last = _elements.Count - 1;
                _elements[0] = _elements[last];
                _elements.RemoveAt(last);

                int i = 0;
                while (true)
                {
                    int left = 2 * i + 1;
                    int right = 2 * i + 2;
                    int smallest = i;

                    if (left < _elements.Count && _elements[left].CompareTo(_elements[smallest]) < 0)
                        smallest = left;

                    if (right < _elements.Count && _elements[right].CompareTo(_elements[smallest]) < 0)
                        smallest = right;

                    if (smallest == i) break;

                    Swap(i, smallest);
                    i = smallest;
                }

                return root;
            }

            private void Swap(int a, int b)
            {
                T temp = _elements[a];
                _elements[a] = _elements[b];
                _elements[b] = temp;
            }
        }
    }
}
