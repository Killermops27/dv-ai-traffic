# AI Traffic v0.2.5 – Hillstart Decoupled Brakes, Anti-Slip Protection, Deadlock-Breaking Yield Inversion & Encounter Trains

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update brings massive improvements to steep grade hillstarts, traction wheel slip sensing, turnout deadlock resolution, switch self-occupancy interlocks, interactive encounter train dispatching, and consist fitting pre-checks.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### ⛰️ 1. Steep Grade Hillstarts & Decoupled 2-Stage Brake Release
* **Physical Lever Precharge Sensing:** Prime mover precharge is now evaluated strictly against physical cab throttle lever position (`_currentThrottle`) rather than the internal ramp variable, dynamically scaling with track gradient (0.40–0.65).
* **Decoupled 2-Stage Brake Release:** Completely decouples train and independent brake pipes on steep inclines. The train brake pipe vents and charges first while the locomotive independent brake remains 100% clamped; once train brakes are fully released and prime mover delivers forward tractive effort, the independent brake smoothly graduates off.
* **Rollback & Coupler Slack Tuning:** Raised forward motion detection to 0.15 m/s (0.8 km/h) to prevent coupler slack bounce from prematurely dumping holding brakes, and tuned rollback trip limit to -0.08 m/s, eliminating backward runaways on steep mountain grades (e.g. Iron Ore Mine, Steel Mill, Goods Factory).

---

### 🛞 2. True Traction Wheel-Slip Sensing & Sander Regulation
* **Decoupled Skidding vs. Wheel-Slip:** Separated brake sliding (`AdhesionController.IsWheelSliding`) from genuine tractive power slip (`wheelslipController.IsWheelslipping`). Clamped holding brakes no longer trigger phantom sanding or choke engine throttle down to 0.20.
* **Protected Hillstart Authority:** Enforced a guaranteed 0.50 throttle floor during hill starts so locomotives can pull away cleanly under heavy tonnage.
* **Dynamic Sander Application:** Sanders activate exclusively upon genuine traction wheel slip or steep positive gradients (> 1.5%).

---

### 🔄 3. Dynamic Deadlock-Breaking Yield Inversion
* **Physical Feasibility Overrides Priority:** Resolved head-on bottlenecks and turnout throat deadlocks (e.g. L-002 vs L-050). When a train is stopped (< 1.5 km/h) and blocked by an obstacle ahead (< 150m), it yields priority, suppresses switch alignment requests and locking, and immediately releases all junction locks (`ReleaseJunction`) and DVSignals reservations (`ClearDVSignalReservation`).
* **Unblocked Clearing Traversal:** The unblocked clearing train (taking a siding or detour branch) retains priority, aligns switches to its path, and vacates the bottleneck.
* **Defense-in-Depth Lock Ignoral:** Junction locks held by stationary, blocked trains are disregarded by passing trains in `JunctionController.IsJunctionLockedByOther`.

---

### 🔀 4. Junction Self-Occupancy Differentiation & Switch Hold Uncoupling
* **Requester Trainset Distinction:** `JunctionController.IsJunctionPhysicallyOccupied` now differentiates the requesting train from external trains. Approaching trains no longer deny switch alignment against themselves within the clearance envelope (eliminating thousands of false `strictly DENIED for requester` logs). Switch blades remain protected if wheels are directly over points (<= 6.5m) or straddling across branches.
* **Deceleration Buffer Unstacking:** Removed duplicate 25m deduction from `_switchHoldDistance` that was stacking with `SpeedProfileGenerator`'s deceleration buffer and bringing trains to a halt 50m before switches.
* **Obstacle Decoupling:** Uncoupled switch hold distance from obstacle blockage (`DistanceToObstacle < 150f`), preventing circular waiting locks.

---

### 🤝 5. Interactive Encounter Trains & Passing Dispatch
* **Passing Meets & Encounters:** Added `DispatchPassingTrainCoroutine` to schedule trains that dynamically pass or meet the player along active valley routes (toggleable via `MoreTrainEncounters` setting).
* **Predictive Deadlock Conflict Detection:** Added `PredictDeadlockConflict` to verify passing loop availability along single-track corridors before dispatching opposing traffic.
* **Passing Loop Yard Exclusion:** Strictly excludes industrial yard storage tracks (`[Y]`) from candidate passing loops via `IsDedicatedPassingLoopTrack` and `IsPassingOrLoopTrack`.
* **Station Route Caching:** Static departure and destination tracks are cached (`s_staticDepartureTracksCache`, `s_staticPaxDestTracksCache`, `s_staticFreightDestTracksCache`) to eliminate repetitive graph queries.
* **Expanded Mining Corridors:** Added scheduled timetables connecting Iron Mine East (`IME`), Coal Mine East (`CME`), and Coal Mine (`CM`) with Harbor (`HB`) and Steel Mill (`SM`).

---

### 📏 6. Instant Track Length Pre-Checking & Smooth Spawning
* **Mathematical Spline Length Pre-Check:** Added `TrainSpawner.CanConsistFitOnTrack` and `CalculateConsistLength` to evaluate consist length against departure track splines in sub-0.05ms before attempting physical instantiation.
* **Paced Pathfinding:** Capped encounter path searches at 8 max with 1 search per frame pacing to eliminate the 70-path-search freeze.
* **Brake Pipe Pre-Charging:** Pre-charges brake pipe before car coupling, eliminating base-game `Attempt to SetBrakePipePressure on Brakeset with multiple cars` console warnings.
* **Car Orientation Correction:** Fixed car orientation array generation to prevent locomotives from spawning reversed.

---

### ⚡ 7. Performance Profiler & Interactive Topology Vector Map (Debug)
* **In-Game Performance Profiler (`PerformanceProfiler.cs`):** Live rolling FPS counter, pathfinding search times, explored node counts, slow search detector, and car spawn duration accessible via the top-right HUD panel toggle `[ ⚡ Perf ]`.
* **Interactive Topology Map Exporter (`TopologyMapExporter.cs`):** Press <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>M</kbd> in Debug builds to export a standalone interactive HTML5/SVG vector map of the entire Derail Valley railway network directly to your Desktop.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.5.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that DVSignals and CommsRadioAPI are active.
