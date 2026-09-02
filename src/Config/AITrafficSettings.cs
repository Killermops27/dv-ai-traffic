using System;
using UnityEngine;
using UnityModManagerNet;
using AITraffic.Compat;

namespace AITraffic.Config
{
    public enum TrafficMode
    {
        AmbientOnly,
        RealJobsOnly,
        Hybrid
    }

    public enum TrafficDensity
    {
        Off,
        Light,
        Medium,
        Dense
    }

    public class AITrafficSettings : UnityModManager.ModSettings
    {
        public TrafficMode Mode = TrafficMode.Hybrid;
        public TrafficDensity Density = TrafficDensity.Medium;
        public bool PlayerPriority = true;
        public float MaxActiveTrains = 4f;
        public float SpawnDistanceMin = 800f;
        public float SpawnDistanceMax = 2500f;
        public float DespawnDistance = 3000f;
        public bool DebugVisuals = false;
        public bool ShowRouteVisualizer = true;
        public bool ShowSignalTags = true;
        public bool HornAtCrossings = true;
        public bool AIDamageImmunity = true;
        public bool RideAlongMode = false;

        // Custom styling cache
        [NonSerialized]
        private GUIStyle headerStyle;
        [NonSerialized]
        private GUIStyle subHeaderStyle;
        [NonSerialized]
        private GUIStyle boxStyle;
        [NonSerialized]
        private GUIStyle descStyle;
        [NonSerialized]
        private GUIStyle statusGoodStyle;
        [NonSerialized]
        private GUIStyle statusNeutralStyle;
        [NonSerialized]
        private bool stylesInitialized = false;

        private void InitStyles()
        {
            if (stylesInitialized && headerStyle != null) return;

            headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.9f, 0.75f, 0.2f) },
                margin = new RectOffset(0, 0, 8, 4)
            };

            subHeaderStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f) },
                margin = new RectOffset(0, 0, 4, 2)
            };

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(10, 10, 8, 8),
                margin = new RectOffset(0, 0, 4, 8)
            };

            descStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f) },
                wordWrap = true
            };

            statusGoodStyle = new GUIStyle(GUI.skin.label)
            {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.3f, 0.9f, 0.3f) }
            };

            statusNeutralStyle = new GUIStyle(GUI.skin.label)
            {
                normal = { textColor = new Color(0.6f, 0.6f, 0.6f) }
            };

            stylesInitialized = true;
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        public void Draw(UnityModManager.ModEntry modEntry)
        {
            InitStyles();

            try
            {
                GUILayout.BeginVertical(boxStyle);
                GUILayout.Label("AI Traffic Settings", headerStyle);
                GUILayout.Space(4);

                // --- TRAFFIC MODE ---
                GUILayout.Label("Traffic Operating Mode", subHeaderStyle);
                var prevMode = Mode;
                string[] modeNames = { "Ambient Only", "Real Jobs Only", "Hybrid" };
                Mode = (TrafficMode)GUILayout.Toolbar((int)Mode, modeNames);
                if (Mode != prevMode)
                {
                    // Clamp or adjust settings if mode changed
                }

                string modeDescription = string.Empty;
                switch (Mode)
                {
                    case TrafficMode.AmbientOnly:
                        modeDescription = "Ambient Only: AI trains run on schedules purely for world immersion (spawned & despawned dynamically).";
                        break;
                    case TrafficMode.RealJobsOnly:
                        modeDescription = "Real Jobs Only: AI trains execute actual station haul and freight jobs across the valley.";
                        break;
                    case TrafficMode.Hybrid:
                        modeDescription = "Hybrid: Dynamic mix of background ambient trains and freight job runners.";
                        break;
                }
                GUILayout.Label(modeDescription, descStyle);
                GUILayout.Space(8);

                // --- TRAFFIC DENSITY ---
                GUILayout.Label("Traffic Density", subHeaderStyle);
                var prevDensity = Density;
                string[] densityNames = { "Off", "Light", "Medium", "Dense" };
                Density = (TrafficDensity)GUILayout.Toolbar((int)Density, densityNames);
                if (Density != prevDensity)
                {
                    switch (Density)
                    {
                        case TrafficDensity.Off:
                            MaxActiveTrains = 0f;
                            break;
                        case TrafficDensity.Light:
                            MaxActiveTrains = 2f;
                            break;
                        case TrafficDensity.Medium:
                            MaxActiveTrains = 4f;
                            break;
                        case TrafficDensity.Dense:
                            MaxActiveTrains = 7f;
                            break;
                    }
                }

                GUILayout.Space(8);

                // --- ACTIVE TRAINS SLIDER ---
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format("Max Active Trains: <b>{0}</b>", (int)MaxActiveTrains), GUILayout.Width(200));
                MaxActiveTrains = Mathf.Round(GUILayout.HorizontalSlider(MaxActiveTrains, 0f, 10f));
                GUILayout.EndHorizontal();
                GUILayout.Space(8);

                // --- SPAWN & DESPAWN DISTANCES ---
                GUILayout.Label("Spawning & Despawning Distances", subHeaderStyle);

                // Min Spawn Distance
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format("Min Spawn Distance: <b>{0} m</b>", (int)SpawnDistanceMin), GUILayout.Width(220));
                SpawnDistanceMin = Mathf.Round(GUILayout.HorizontalSlider(SpawnDistanceMin, 400f, 2000f) / 50f) * 50f;
                GUILayout.EndHorizontal();

                // Max Spawn Distance
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format("Max Spawn Distance: <b>{0} m</b>", (int)SpawnDistanceMax), GUILayout.Width(220));
                SpawnDistanceMax = Mathf.Round(GUILayout.HorizontalSlider(SpawnDistanceMax, 1500f, 5000f) / 50f) * 50f;
                if (SpawnDistanceMax < SpawnDistanceMin + 200f)
                {
                    SpawnDistanceMax = SpawnDistanceMin + 200f;
                }
                GUILayout.EndHorizontal();

                // Despawn Distance
                GUILayout.BeginHorizontal();
                GUILayout.Label(string.Format("Despawn Distance: <b>{0} m</b>", (int)DespawnDistance), GUILayout.Width(220));
                DespawnDistance = Mathf.Round(GUILayout.HorizontalSlider(DespawnDistance, 2000f, 6000f) / 50f) * 50f;
                if (DespawnDistance < SpawnDistanceMax + 300f)
                {
                    DespawnDistance = SpawnDistanceMax + 300f;
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(8);

                // --- BEHAVIOR TOGGLES ---
                GUILayout.Label("AI Train Behaviors & Signaling", subHeaderStyle);

                PlayerPriority = GUILayout.Toggle(PlayerPriority, " Player Priority (Signal dispatching prioritizes player train over AI)");
                HornAtCrossings = GUILayout.Toggle(HornAtCrossings, " Horn at Level Crossings (AI locomotives sound horn approaching crossings)");
                AIDamageImmunity = GUILayout.Toggle(AIDamageImmunity, " AI Damage Immunity (Disables engine, body, powertrain & wheel damage for AI locos only)");
                RideAlongMode = GUILayout.Toggle(RideAlongMode, " Ride Along Mode (AI driver ignores player presence so you can ride cab/cars freely)");
                ShowRouteVisualizer = GUILayout.Toggle(ShowRouteVisualizer, " Show 3D Route Visualization (Draws luminous 3D path line along tracks in world)");
                ShowSignalTags = GUILayout.Toggle(ShowSignalTags, " Show 3D Signal Tags (Renders in-world floating status tags over upcoming signals)");
                DebugVisuals = GUILayout.Toggle(DebugVisuals, " Debug Visuals (Render AI monitor, sensors and route gizmos)");

                GUILayout.Space(12);

                // --- MOD COMPATIBILITY STATUS ---
                GUILayout.Label("Mod Compatibility Status", subHeaderStyle);
                DrawCompatItem("DVSignals (Signaling & Interlocking)", ModCompatManager.IsDVSignalsLoaded, true);
                DrawCompatItem("DoubleTrack (Multi-track mainline routing)", ModCompatManager.IsDoubleTrackLoaded, false);
                DrawCompatItem("PersistentJobs (Job-car isolation)", ModCompatManager.IsPersistentJobsLoaded, false);
                DrawCompatItem("SelfShunt / YardMaster (Yard shunting exclusion)", ModCompatManager.IsYardMasterLoaded, false);
                DrawCompatItem("PassengerJobs (Station platform routing)", ModCompatManager.IsPassengerJobsLoaded, false);

                GUILayout.EndVertical();
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error(string.Format("Error rendering AITrafficSettings GUI: {0}", ex));
            }
        }

        private void DrawCompatItem(string name, bool isLoaded, bool isRequired)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(string.Format("• {0}:", name), GUILayout.Width(340));
            if (isLoaded)
            {
                GUILayout.Label("Active", statusGoodStyle);
            }
            else
            {
                GUILayout.Label(isRequired ? "Missing (Required)" : "Not installed", statusNeutralStyle);
            }
            GUILayout.EndHorizontal();
        }
    }
}
