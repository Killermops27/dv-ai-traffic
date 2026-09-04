# Derail Valley AI Traffic Mod (`AITraffic`)

[![Game: Derail Valley](https://img.shields.io/badge/Game-Derail%20Valley-blue.svg)](http://www.derailvalley.com/)
[![Mod Loader: UMM](https://img.shields.io/badge/ModLoader-Unity%20Mod%20Manager-orange.svg)](https://www.nexusmods.com/site/mods/21)
[![Requires: DVSignals](https://img.shields.io/badge/Requires-DVSignals-green.svg)](https://github.com/WhistleWiz/dv-signals)
[![Requires: CommsRadioAPI](https://img.shields.io/badge/Requires-CommsRadioAPI-purple.svg)](https://github.com/Killermops27/dv-ai-traffic)
[![Latest Release](https://img.shields.io/github/v/release/Killermops27/dv-ai-traffic?include_prereleases&color=brightgreen)](https://github.com/Killermops27/dv-ai-traffic/releases)
[![Status: Early Alpha](https://img.shields.io/badge/Status-Early%20Alpha%20(Bugs%20Expected)-red.svg)](#disclaimer)

An autonomous AI train traffic, timetable dispatching, and player-employed AI worker system for **Derail Valley**, bringing the railway network to life with schedule-driven freight, passenger, shunting, and haulage movements.

---

> [!CAUTION]
> ### ⚠️ EXPERIMENTAL EARLY ALPHA — LOTS OF BUGS STILL PRESENT!
> This mod is in **very early active development**. 
> **THERE ARE CURRENTLY MANY BUGS, POTENTIAL DEADLOCKS, ROUTING EDGE CASES, AND UNEXPECTED BEHAVIORS.**
> Trains may occasionally stop unexpectedly, get stuck on complex junction ladders, misjudge braking distances with extreme consist weights, or conflict with track reservations.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE TESTING.**
> We are actively patching and improving AI behaviors daily. Please report all bugs and quirks on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

## 🚂 Key Features & Architecture

### 1. 👷 Player-Employed AI Worker System (Phase 2)
- **Point-to-Point AI Hauls:** Hire AI engineers to haul your assembled consists from station to station across the valley map.
- **Diegetic Comms Radio Dispatch:** Equipped with a dedicated **AI Worker** Comms Radio mode (cyan laser beam). Aim at any locomotive to inspect train car count, total length (meters), and mass (metric tons), cycle destination stations and arrival tracks via the scroll wheel, and pull the trigger to dispatch!
- **Dynamic Siding Auto-Selection:** Evaluates receiving and yard storage tracks at the destination station, verifying length ($\ge \text{consist} + 25\text{m}$), clearance (`!IsTrackOccupied`), and path navigability.
- **Dynamic Economy & Driver Wages:** Computes realistic driver compensation based on distance and ton-mileage ($\text{Base } \$300 + \$0.06/\text{meter} + \$0.35/\text{ton}$), deducting fees directly from the player's wallet with an audio register chime.
- **Consist Automation & Despawn Immunity:** Automatically couples air hoses, sets cock levers, releases handbrakes across the consist, and starts prime movers. Flagged with despawn immunity (`IsWorkerDriven = true`).
- **Arrival Handover:** Halts on the target destination track, applies the parking handbrake, strips the AI driver, and displays an on-screen notification returning 100% control to the player.

### 2. Autonomous Virtual Engineer (`AIEngineer`)
- **Closed-Loop Speed Regulation:** Custom PID controllers modulate throttle, dynamic braking, and independent/train air brakes to track dynamic target velocities smoothly across varying terrain and gradients.
- **Speed Profile Calculation:** Generates realistic deceleration and braking curves based on upcoming track speed limits, curves, red/yellow signal aspects, and station stopping points.
- **Locomotive Intelligence:** Includes specialized powertrain controllers such as automatic mechanical gear-shifting for the **DM3** (`DM3TransmissionController`), reverser management, and cab safety interlocks.

### 3. Network Pathfinding & Dispatching
- **Topological Graph Representation (`RailGraph`):** Parses the game's track nodes, branch lines, and yards into an optimized navigable graph.
- **Intelligent Pathfinder (`Pathfinder`):** A* path calculation with route reservation weights, dynamic track occupancy detection, and reverse maneuver penalties.
- **Junction Locking (`JunctionController`):** Automatically aligns and locks switches ahead of active AI train paths while respecting manual user overrides and safety clearances.

### 4. Signaling & Block Occupancy Integration
- **DVSignals Integration:** Interacts directly with the [DVSignals](https://github.com/WhistleWiz/dv-signals) framework for physical signal aspect resolution, block reservation, and headway control to ensure safe train separation.

### 5. Ambient Fleet Spawning & Yard Persistence
- **Dynamic Spawning & Despawning:** Manages ambient traffic density around the player with yard-based and off-screen spawner/despawner lifecycles.
- **Station Wake-Up & Persistence:** Dynamically activates destination yards ahead of approaching AI workers without despawning jobs or existing rolling stock.
- **Diverse Consists:** Supports predefined and randomized consists spanning DE2, DE6, DH4, DM3, steam engines, passenger rakes, and mixed freight wagons.

### 6. Mod Compatibility
Built with cross-mod interoperability in mind:
- **DVSignals** *(Required)*
- **CommsRadioAPI** *(Required)*
- **DoubleTrack** *(Strongly Recommended)*
- **PassengerJobs**
- **PersistentJobsMod**
- **SelfShunt**

---

## 📦 Requirements & Recommendations

### Required:
1. **[Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/)** (PC / Steam release)
2. **[Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21)** (v0.27.0 or newer, configured for Doorstop / Assembly Injection)
3. **[DVSignals](https://github.com/WhistleWiz/dv-signals)** installed in your `Derail Valley/Mods/` directory
4. **[CommsRadioAPI](https://github.com/Killermops27/dv-ai-traffic)** installed in your `Derail Valley/Mods/` directory

### Strongly Recommended:
* **Double Track (`DoubleTrack`)**: Highly recommended for smooth traffic flow. Double track sections provide bi-directional passing capacity, significantly mitigating single-track traffic bottlenecks and deadlocks between ambient AI trains and player operations.

---

## 🛠️ Installation

1. Download the latest **`AITraffic-v<version>.zip`** from the **[Releases](https://github.com/Killermops27/dv-ai-traffic/releases)** section.
2. Install via **Unity Mod Manager (UMM)**:
   - Drag and drop the downloaded `.zip` file directly into the UMM **Mods** tab, **OR**
   - Extract the `.zip` archive into your `Derail Valley/Mods/` folder so that `Info.json` is located at:
     ```text
     Derail Valley/Mods/AITraffic/Info.json
     ```
3. Start the game. Open the Unity Mod Manager interface (<kbd>Ctrl</kbd> + <kbd>F10</kbd>) to verify that `AI Traffic` is loaded with a green status indicator and configure mod settings.

---

## ⚙️ Building from Source

### Prerequisites
- **Visual Studio 2022** or **JetBrains Rider** / **MSBuild** with .NET Framework 4.8 targeting pack.
- A valid installation of **Derail Valley**.

### Build Steps
1. Clone the repository including submodules:
   ```bash
   git clone --recurse-submodules git@github.com:Killermops27/dv-ai-traffic.git
   ```
2. Open `AITraffic.csproj` or the solution in Visual Studio.
3. Configure the `DVInstallPath` property in `AITraffic.csproj` if your Steam library is located outside the default path:
   ```xml
   <DVInstallPath>C:\Path\To\SteamLibrary\steamapps\common\Derail Valley</DVInstallPath>
   ```
4. Build in **Debug** or **Release** configuration. The build output will package `AITraffic.dll` and `Info.json` into `bin/`.

---

## 🗺️ Roadmap & Goals

- [ ] Interactive dispatcher map / tablet overview for live train monitoring.
- [ ] Timetable schedule manager with customizable station dwell times.
- [ ] Expanded AI communication (horns at grade crossings, cab lighting, radio alerts).
- [ ] Full shunting yard automation and car classification movements.
- [ ] Performance profiling and chunk-based train LOD physics suspension.

---

## 📄 License & Acknowledgements

- Mod authored by **Killermops27**.
- Special thanks to **WhistleWiz** and the Derail Valley modding community for the `dv-signals` framework and invaluable reverse-engineering insights.
