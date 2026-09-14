# Derail Valley AI Traffic (`AITraffic`)

[![Game: Derail Valley](https://img.shields.io/badge/Game-Derail%20Valley-blue.svg)](http://www.derailvalley.com/)
[![Mod Loader: UMM](https://img.shields.io/badge/ModLoader-Unity%20Mod%20Manager-orange.svg)](https://www.nexusmods.com/site/mods/21)
[![Requires: DVSignals](https://img.shields.io/badge/Requires-DVSignals-green.svg)](https://github.com/WhistleWiz/dv-signals)
[![Requires: CommsRadioAPI](https://img.shields.io/badge/Requires-CommsRadioAPI-purple.svg)](https://github.com/Killermops27/dv-ai-traffic)
[![Compatible: ZCouplers](https://img.shields.io/badge/Compatible-ZCouplers-blueviolet.svg)](https://www.nexusmods.com/derailvalley/mods/813)
[![Latest Release: v0.2.4](https://img.shields.io/badge/Release-v0.2.4-brightgreen.svg)](https://github.com/Killermops27/dv-ai-traffic/releases/tag/v0.2.4)
[![Nexus Mods](https://img.shields.io/badge/Nexus%20Mods-1685-orange.svg)](https://www.nexusmods.com/derailvalley/mods/1685)

An autonomous AI train traffic, timetable dispatching, and player-employed AI worker system for **Derail Valley**, bringing the railway network to life with schedule-driven freight, passenger, shunting, and haulage movements.

---

> [!CAUTION]
> ### ⚠️ EXPERIMENTAL EARLY ALPHA — BUGS ARE EXPECTED!
> This mod is in **active early alpha development**. 
> **THERE ARE CURRENTLY INEVITABLE BUGS, ROUTING DEADLOCKS, AND UNEXPECTED BEHAVIORS.**
> Trains may occasionally stop unexpectedly, encounter tricky switch ladders, or miscalculate braking under extreme consist tonnages.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE TESTING.**
> We are actively patching and improving AI behaviors. Please report all reproducible issues on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

## 🌟 What's New in v0.2.4

- 🚦 **Periodic Signal Wait Retry & Controller Wakeup:** When stopped at an `Hp 0` (Red) signal, AI trains run an automatic 20s retry timer to re-request switch alignment and force DVSignals controllers to re-evaluate downstream blocks, preventing trains from being stranded indefinitely when blocks clear.
- 🚂 **Prototype Consist Cuts & Unit Trains:** Bulk freight (Coal, Ore, Crude Oil) has a 60% probability (35% for general freight) of spawning as a dedicated homogeneous unit train. Mixed freight rakes generate cars in realistic matching cuts/blocks of 2–4 wagons (shunter) or 3–6 wagons (regional/mainline) rather than random single-car clown trains.
- 🛡️ **Spawner Head-On Safety Interlock:** Analyzes candidate spawn tracks and initial 6 departure waypoints against player location, rejecting spawns within 800m of the player or heading directly toward an oncoming player within 1400m to eliminate head-on conflicts on long yard ladders (e.g. Harbor D/G).
- 🚉 **City South & Forest South Scheduled Corridors:** Timetabled corridors expanded to include City South (`CS`) and Forest South (`FS`/`FRS`) connections to Harbor, Steel Mill, Sawmill, and Machine Factory, with precision station alias disambiguation.
- ⏱️ **Terminus Arrival Tracking & Safe Clearance Despawning:** AI Engineer records the exact arrival timestamp (`TerminusArrivalTime`) upon reaching terminus tracks, ensuring completed ambient trains remain parked safely in yards until the player has physically moved beyond clearance distance.

---

## 🚂 Key Systems & Features

### 1. 👷 Player-Employed AI Worker System (Phase 2)
- **Point-to-Point AI Hauls:** Hire AI engineers to haul your assembled consists from station to station across the valley map.
- **Diegetic Comms Radio Dispatch:** Equipped with a dedicated **AI Worker** Comms Radio mode (cyan laser beam). Aim at any locomotive to inspect train car count, total length (meters), and mass (metric tons), cycle destination stations and arrival tracks via the scroll wheel, and pull the trigger to dispatch!
- **Dynamic Clear Siding Auto-Selection:** Evaluates candidate receiving (`transferIn`) and yard storage tracks at the destination station, verifying length ($\ge \text{consist} + 25\text{m}$), clearance (`!IsTrackOccupied`), and path navigability before selecting.
- **Dynamic Economy & Driver Wages:** Computes realistic driver compensation based on route distance and consist weight ($\text{Base } \$300 + \$0.06/\text{meter} + \$0.35/\text{ton}$), deducting fees directly from the player's wallet with an audio cash register chime.
- **Consist Automation & Despawn Immunity:** Automatically locks couplers, connects air hoses, sets cock levers, releases handbrakes across the consist, and starts prime movers. Flagged with despawn immunity (`IsWorkerDriven = true`).
- **Arrival Handover:** Halts on the target destination track, sets the parking handbrake, removes the AI driver component, and displays an on-screen toast banner returning 100% manual control to the player.

### 2. 🧠 Autonomous Virtual Engineer (`AIEngineer`)
- **Closed-Loop Speed Regulation:** Custom PID controllers modulate throttle, dynamic braking, and independent/train air brakes to track dynamic target velocities smoothly across varying terrain and gradients.
- **Speed Profile Calculation:** Generates realistic deceleration and braking curves based on upcoming track speed limits, curves (evaluated in the 2D horizontal plane using native Bezier arc approximations), red/yellow signal aspects, and station stopping points.
- **Powertrain Thermal Protection:** Proactive throttle derating between 95°C and 105°C prevents engine explosions on steep grades; pneumatic grade anti-rollback clamp holds trains if power is lost.
- **Hill-Start Pre-Charge Sequence:** Prime mover throttles up and builds tractive effort before brakes release on grades ($\ge 0.15\%$), eliminating rollback stalls.
- **DM3 Mechanical Shunter Automation:** Specialized automatic gear-shifting controller (`DM3TransmissionController`), neutral gear-shift brake hold, and high-power fluid coupling launches.
- **Traction Motor Over-Current Protection:** Fast amperage derating (>380A) and automatic stationary fuse reset for diesel-electrics.

### 3. 🚦 Signaling, Corridor Holding & Safety Interlocking
- **DVSignals Integration & Remote Keep-Alive:** Interacts directly with DVSignals for physical signal aspect resolution, block reservation, and headway control down to the 0m stop line, keeping distant signal controllers active within 1500m of AI trains.
- **Periodic Signal Wait Retry:** Automatically re-requests switch alignment and wakes DVSignals controllers every 20s when halted at red signals.
- **Double Track Crossover & Mirrored Mast Support:** Automatically accounts for inverted mast scales on Double Track crossovers, correctly resolving governing signal aspects across mirrored masts.
- **False Green Rejection:** AI engineers verify that upcoming switches within a signal's governing block are physically aligned before trusting a green aspect. Misaligned routes enforce an immediate 0 km/h stop line 15m before the signal.
- **Whole-Trainset Switch Straddle Interlocking:** Switches are strictly locked against throwing while any car body or bogie in a consist is physically straddling switch points.
- **Single-Track Corridor Protection:** Evaluates single-track corridors ahead of passing loops. Holds safely inside passing sidings outside the switch fouling envelope (45m buffer) until the corridor clears.
- **Direction-Aware Corridor Arbitration:** Distinguishes opposing trains from same-direction followers, allowing consecutive trains to proceed smoothly without deadlock.
- **Ride-Along Mode:** Ride along as a passenger in an AI train cab or nearby track without triggering occupancy stop sensors.

### 4. 🗺️ Network Pathfinding & Yard Protection
- **Forward Angular Continuity:** Enforces vector travel continuity (`alignment <= 0.0f`), strictly eliminating acute-angle hairpin U-turns (> 90 degrees) across junction frogs.
- **Double-Track Crossover Penalties:** Penalizes unnecessary zigzagging across parallel mainline crossovers (`CX`, `Cross`, `Xing`), maintaining smooth right-hand running.
- **Strict Intermediate Occupancy Avoidance:** Intermediate tracks occupied by rolling stock or parked cars are strictly rejected (`StrictlyAvoidOccupied = true`), preventing through-trains from crashing into occupied sidings.
- **Passing Loops vs. Industrial Storage Yards:** Passing loops carry a minimal preference penalty (+350m) so clear mainlines are favored but empty loops are selected over blocked lines. Industrial storage tracks retain heavy transit penalties (+500,000m) to keep ambient freight from cutting through shunting yards.
- **Dynamic Off-Route Recovery:** If an ambient train is diverted off-path, the engineer dynamically recalculates an A* recovery route forward once the rear bogie clears the switch.
- **Guaranteed Obstacle Detour Routing:** Dynamic obstacle recalculation excludes blocked tracks and routes trains around stopped obstacles.
- **Zero-GC Pooled Pathfinding:** Reusable collections eliminate heap allocations and GC stutter during A* path generation.

### 5. 🔗 Automatic Multiple Unit (MU) Cable Coupling
- **Automatic MU Cables:** Adjacent locomotives and slug units (e.g. DE6 + DE6, DE2 + Slug) automatically couple both physical 3D Multiple Unit cables and logical control block propagators upon spawn and worker preparation, without requiring career license unlocks.

### 6. ⚡ Ambient Fleet Simulation & Yard Persistence
- **Encounter-Driven Traffic Scheduling:** Prevents ambient trains from spawning closer than 1000m to the player while biasing origin stations between 1000m and 3000m to generate frequent, natural mainline meets.
- **Origin & Destination Anti-Repetition:** Dynamically records recent origins and destinations to prevent repetitive spawn loops from identical stations.
- **Brake Pipe Integrity & Staggered Spawning:** Closes outer uncoupled angle cocks at spawn and time-slices locomotive initialization (1 loco per frame) and coupler settling across frames, ensuring full air pressure with zero frame drops.
- **Frame-Cached Snapshot Occupancy:** Evaluates track occupancy in under 3ms via O(1) frame snapshots (`BuildOccupiedTracksSnapshot()`), eliminating lag spikes.
- **Station Wake-Up & Yard Persistence:** Approaching AI trains ($\approx 1200\text{m}$) dynamically activate destination yards without despawning jobs or existing rolling stock.

### 7. 🧩 Mod Compatibility
Built with cross-mod interoperability in mind:
- **DVSignals** *(Required)* – Signaling and block protection.
- **CommsRadioAPI** *(Required)* – Diegetic Comms Radio dispatch mode.
- **DoubleTrack** *(Strongly Recommended)* – Provides bi-directional passing capacity to minimize single-track bottlenecks.
- **Zeibach's Couplers (`ZCouplers`)** *(Supported)* – AI consists are exempted from stress-induced coupler snapping while retaining full 3D knuckle visuals, animations, and buffer spring physics.
- **Freecam / Photo Mode** *(Supported)* – 3D overhead tags and route overlays track `PlayerManager.ActiveCamera`.
- **PassengerJobs**, **PersistentJobsMod**, **SelfShunt** *(Supported)*.

---

## 🎮 Controls & How to Use

### Diegetic Comms Radio (AI Worker Mode)
1. Equip the **Comms Radio** from your inventory.
2. Cycle modes until you reach **AI Worker** (indicated by a cyan targeting laser beam).
3. **Target Locomotive:** Aim at the locomotive you wish to dispatch. The LCD screen displays the loco ID, car count, train length (m), and total mass (t).
4. **Select Destination:** Rotate the **Scroll Wheel** to cycle through valley stations and arrival tracks (or select `[Auto Clear Siding]` to let the AI automatically pick an empty track).
5. **Dispatch:** Pull the **Trigger**. The system verifies route navigability, deducts the driver fee with a cash register chime, and dispatches the train!
6. **Dismiss Driver:** To dismiss an active AI worker and regain immediate manual control, target the locomotive again and pull the trigger.

### Unity Mod Manager & HUD Overlays
- Press <kbd>Ctrl</kbd> + <kbd>F10</kbd> to open Unity Mod Manager and configure mod settings.
- Toggle **Debug HUD** to view real-time train status, current speed, PID output, and limiting speed factors.
- Use **[ 3D Locos ]** to toggle overhead floating nameplates.
- Use **[ Ride-Along ]** to enter passenger mode when boarding AI locomotives.

---

## 📦 Requirements & Recommendations

### Required:
1. **[Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/)** (PC / Steam release)
2. **[Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21)** (v0.27.0 or newer)
3. **[DVSignals](https://github.com/WhistleWiz/dv-signals)** installed in `Derail Valley/Mods/`
4. **[CommsRadioAPI](https://github.com/Killermops27/dv-ai-traffic)** installed in `Derail Valley/Mods/`

### Strongly Recommended:
* **[Double Track (`DoubleTrack`)](https://www.nexusmods.com/derailvalley/mods/808)**: Strongly recommended for smooth traffic flow and bi-directional mainline capacity.
* **[Zeibach's Couplers (`ZCouplers`)](https://www.nexusmods.com/derailvalley/mods/813)**: Supported with AI stress protection.

---

## 🛠️ Installation

1. Download the latest **`AITraffic-v0.2.4.zip`** from [GitHub Releases](https://github.com/Killermops27/dv-ai-traffic/releases) or [Nexus Mods](https://www.nexusmods.com/derailvalley/mods/1685).
2. Install via **Unity Mod Manager (UMM)**:
   - Drag and drop the downloaded `.zip` file directly into the UMM **Mods** tab, **OR**
   - Extract the `.zip` archive into your `Derail Valley/Mods/` folder so that `Info.json` is located at:
     ```text
     Derail Valley/Mods/AITraffic/Info.json
     ```
3. Start the game. Open UMM (<kbd>Ctrl</kbd> + <kbd>F10</kbd>) to verify that `AI Traffic` is loaded with a green status indicator.

---

## 🗺️ Roadmap & Goals

- [ ] Interactive dispatcher map / tablet overview for live train monitoring.
- [ ] Timetable schedule manager with customizable station dwell times.
- [ ] Expanded AI audio/visual communication (horns at grade crossings, cab lighting, radio callouts).
- [ ] Full shunting yard automation and car classification movements.
- [ ] Chunk-based train LOD physics suspension for distant consists.

---

## 📄 License & Acknowledgements

- Mod authored by **Killermops27**.
- Special thanks to **WhistleWiz** and the Derail Valley modding community for the `dv-signals` framework and invaluable reverse-engineering insights.
