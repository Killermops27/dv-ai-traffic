# Derail Valley AI Traffic (`AITraffic`)

[![Game: Derail Valley](https://img.shields.io/badge/Game-Derail%20Valley-blue.svg)](http://www.derailvalley.com/)
[![Mod Loader: UMM](https://img.shields.io/badge/ModLoader-Unity%20Mod%20Manager-orange.svg)](https://www.nexusmods.com/site/mods/21)
[![Requires: DVSignals](https://img.shields.io/badge/Requires-DVSignals-green.svg)](https://github.com/WhistleWiz/dv-signals)
[![Requires: CommsRadioAPI](https://img.shields.io/badge/Requires-CommsRadioAPI-purple.svg)](https://github.com/Killermops27/dv-ai-traffic)
[![Compatible: ZCouplers](https://img.shields.io/badge/Compatible-ZCouplers-blueviolet.svg)](https://www.nexusmods.com/derailvalley/mods/813)
[![Latest Release: v0.2.2](https://img.shields.io/badge/Release-v0.2.2-brightgreen.svg)](https://github.com/Killermops27/dv-ai-traffic/releases/tag/v0.2.2)
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

## 🌟 What's New in v0.2.2

- 🚦 **Thru-Station Pathfinding & Occupancy Elimination:** Intermediate tracks occupied by parked cars or rolling stock are strictly rejected (`StrictlyAvoidOccupied = true`). AI trains will no longer route into occupied sidings!
- 🔄 **Passing Loops vs. Industrial Storage Yards:** AI traffic differentiates passing loops (`[S]`, `Siding`, `Loop`) from industrial storage yards (`[Y]`, `[L]`). Passing loops have minimal preference cost (+350m) so clear lines are preferred, but empty loops are taken over blocked lines. Industrial yards carry heavy transit penalties (+500,000m) to keep ambient freight from cutting through shunting tracks.
- 🛡️ **Single-Track Corridor Lookahead & Deadlock Prevention (GitHub Issue #5):** AI trains evaluate single-track corridors ahead of passing loops. If an opposing train is detected, the train holds safely inside the passing siding outside the switch clearance foul envelope (45m buffer) until the single track clears. Direction-aware tracking allows same-direction trains to follow freely!
- 🔀 **Switch Interlocking & Anti-Split Switch Safety (GitHub Issue #4):** Switches are strictly locked against throwing while any railcar body or bogie is physically straddling the switch points or clearance envelope. Continuous lock renewal protects switches until the entire consist has cleared.
- 🌡️ **Powertrain Thermal Protection & Overheating Roll-Off (GitHub Issue #6):** Actively monitors engine coolant and oil temperatures, linearly derating throttle between 95°C and 105°C to eliminate blown prime movers on mountain grades.
- ⛰️ **Hill-Start Pre-Charge Sequence & Anti-Rollback:** On upgrades ($\ge 0.15\%$), independent and train brakes stay firmly clamped while the engine spools up to build launching torque before brakes release. Guarantees 0 rollback on steep mountain starts.
- ⚙️ **DM3 High-Power Fluid Launch & Gear Shift Brake Hold:** Mechanical DM3 locomotives spool throttle up to 0.90–1.00 before releasing independent brakes on slopes, and apply holding brakes during neutral gear shifts.
- ⚡ **Traction Motor Over-Current Protection:** Monitored traction motor amperage rolls throttle back above 380A and cuts power at 420–480A to prevent blown fuses. Tripped fuses automatically reset once stopped.
- 🔗 **Automatic Multiple Unit (MU) Cabling:** Consists with adjacent locomotives and slugs (e.g. DE6+DE6, DE2+Slug) automatically couple physical 3D MU jumper cables and propagate controls without requiring career license unlocks.
- 🧲 **Zeibach's Couplers (`ZCouplers`) Compatibility (GitHub Issue #2):** AI trains are exempted from stress-induced coupler snapping while retaining full 3D knuckle visuals, animations, and buffer spring physics.
- 🎥 **Freecam / Photo Mode 3D Nametag Projection (GitHub Issue #1):** Overhead nameplates and route visualizer lines properly track active camera in Freecam, Orbit, and Photo Mode.
- ⚡ **O(1) Snapshot Occupancy & Frame-Staggered Spawning:** Evaluates track occupancy in <3ms using frame-cached snapshots, and instantiates ambient trains 1 car per frame to completely eliminate spawn stutter and frame drops.
- 🎨 **Visual Route Line & HUD Sync:** In-world route lines are elevated 0.65m above rails to eliminate ballast z-fighting, and HUD train entries match their route visualizer colors.

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
- **DVSignals Integration:** Interacts directly with the [DVSignals](https://github.com/WhistleWiz/dv-signals) framework for physical signal aspect resolution, block reservation, and headway control down to the 0m stop line.
- **Single-Track Corridor Protection:** Evaluates single-track corridors ahead of passing loops. If an opposing train is detected downstream, the train holds safely inside the passing siding outside the switch fouling envelope (with a 45m buffer) until the corridor clears.
- **Direction-Aware Corridor Arbitration:** Distinguishes opposing trains from same-direction followers, allowing consecutive trains to proceed smoothly without deadlock.
- **Anti-Split Switch & Bogie Interlocking:** Switches are strictly locked against throwing while any railcar body or bogie is physically straddling the switch points or clearance envelope.
- **Ride-Along Mode:** Ride along as a passenger in an AI train cab or nearby track without triggering occupancy stop sensors.

### 4. 🗺️ Network Pathfinding & Yard Protection
- **Strict Intermediate Occupancy Avoidance:** Intermediate tracks occupied by rolling stock or parked cars are strictly rejected (`StrictlyAvoidOccupied = true`), preventing through-trains from crashing into occupied sidings.
- **Passing Loops vs. Industrial Storage Yards:** Passing loops carry a minimal preference penalty (+350m) so clear mainlines are favored but empty loops are selected over blocked lines. Industrial storage tracks retain heavy transit penalties (+500,000m) to keep ambient freight from cutting through shunting yards.
- **Guaranteed Obstacle Detour Routing:** Dynamic obstacle recalculation excludes blocked tracks and routes trains around stopped obstacles.
- **Zero-GC Pooled Pathfinding:** Reusable collections eliminate heap allocations and GC stutter during A* path generation.

### 5. 🔗 Automatic Multiple Unit (MU) Cable Coupling
- **Automatic MU Cables:** Adjacent locomotives and slug units (e.g. DE6 + DE6, DE2 + Slug) automatically couple both physical 3D Multiple Unit cables and logical control block propagators upon spawn and worker preparation, without requiring career license unlocks.

### 6. ⚡ Ambient Fleet Simulation & Yard Persistence
- **Frame-Cached Snapshot Occupancy:** Evaluates track occupancy in under 3ms via O(1) frame snapshots (`BuildOccupiedTracksSnapshot()`), eliminating lag spikes.
- **True Time-Sliced Ambient Spawning:** Ambient train cars instantiate one car per frame across 15–25 frames, completely eliminating the 400ms–1,200ms main-thread freeze.
- **Station Wake-Up & Yard Persistence:** Approaching AI trains ($\approx 1200\text{m}$) dynamically activate destination yards without despawning jobs or existing rolling stock.
- **Dynamic Cargo Consists:** Ambient freight rakes spawn loaded with industry-appropriate cargo types rather than empty wagons.

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

1. Download the latest **`AITraffic-v0.2.2.zip`** from [GitHub Releases](https://github.com/Killermops27/dv-ai-traffic/releases) or [Nexus Mods](https://www.nexusmods.com/derailvalley/mods/1685).
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
