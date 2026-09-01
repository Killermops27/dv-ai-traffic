# Derail Valley AI Traffic Mod (`AITraffic`)

[![Game: Derail Valley](https://img.shields.io/badge/Game-Derail%20Valley-blue.svg)](http://www.derailvalley.com/)
[![Mod Loader: UMM](https://img.shields.io/badge/ModLoader-Unity%20Mod%20Manager-orange.svg)](https://www.nexusmods.com/site/mods/21)
[![Requires: DVSignals](https://img.shields.io/badge/Requires-DVSignals-green.svg)](https://github.com/WhistleWiz/dv-signals)
[![Status: Early Alpha](https://img.shields.io/badge/Status-Early%20Development-yellow.svg)](#disclaimer)

An autonomous AI train traffic and dispatching system for **Derail Valley**, bringing the railway network to life with schedule-driven and dynamic freight, passenger, and shunting movements.

---

> [!WARNING]
> ### ⚠️ Early Development Disclaimer
> This mod is currently in **early active development (Alpha / Work in Progress)**. 
> Features, architecture, pathfinding heuristics, and save-game compatibility are subject to rapid change. You may encounter bugs, unexpected AI behaviors, or edge-case routing deadlocks. 
> **Back up your save files before testing.** Feedback, issue reports, and contributions are warmly welcome!

---

## 🚂 Key Features & Architecture

### 1. Autonomous Virtual Engineer (`AIEngineer`)
- **Closed-Loop Speed Regulation:** Custom PID controllers modulate throttle, dynamic braking, and independent/train air brakes to track dynamic target velocities smoothly across varying terrain and gradients.
- **Speed Profile Calculation:** Generates realistic deceleration and braking curves based on upcoming track speed limits, curves, red/yellow signal aspects, and station stopping points.
- **Locomotive Intelligence:** Includes specialized powertrain controllers such as automatic mechanical gear-shifting for the **DM3** (`DM3TransmissionController`), reverser management, and cab safety interlocks.

### 2. Network Pathfinding & Dispatching
- **Topological Graph Representation (`RailGraph`):** Parses the game's track nodes, branch lines, and yards into an optimized navigable graph.
- **Intelligent Pathfinder (`Pathfinder`):** A* path calculation with route reservation weights, dynamic track occupancy detection, and reverse maneuver penalties.
- **Junction Locking (`JunctionController`):** Automatically aligns and locks switches ahead of active AI train paths while respecting manual user overrides and safety clearances.

### 3. Signaling & Block Occupancy Integration
- **DVSignals Integration:** Interacts directly with the [DVSignals](https://github.com/WhistleWiz/dv-signals) framework for physical signal aspect resolution, block reservation, and headway control to ensure safe train separation.

### 4. Fleet Management & Spawning
- **Dynamic Spawning & Despawning:** Manages ambient traffic density around the player with yard-based and off-screen spawner/despawner lifecycles.
- **Diverse Consists:** Supports predefined and randomized consists spanning DE2, DE6, DH4, DM3, steam engines, passenger rakes, and mixed freight wagons.

### 5. Mod Compatibility
Built with cross-mod interoperability in mind:
- **DVSignals** (Hard requirement)
- **PassengerJobs**
- **PersistentJobsMod**
- **DoubleTrack**
- **SelfShunt**

---

## 📦 Requirements & Prerequisites

1. **[Derail Valley](https://store.steampowered.com/app/588030/Derail_Valley/)** (PC / Steam release)
2. **[Unity Mod Manager (UMM)](https://www.nexusmods.com/site/mods/21)** (v0.27.0 or newer, configured for Doorstop / Assembly Injection)
3. **[DVSignals](https://github.com/WhistleWiz/dv-signals)** installed in your `Derail Valley/Mods/` directory

---

## 🛠️ Installation

1. Download or compile the latest release of `AITraffic`.
2. Extract the `AITraffic` directory into your `Derail Valley/Mods/` folder so that `Info.json` is located at:
   ```text
   Derail Valley/Mods/AITraffic/Info.json
   ```
3. Start the game. Open the Unity Mod Manager interface (<kbd>Ctrl</kbd> + <kbd>F10</kbd>) to configure mod settings.

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

- Mod authored by **Domann** (`Killermops27`).
- Special thanks to **WhistleWiz** and the Derail Valley modding community for the `dv-signals` framework and invaluable reverse-engineering insights.
