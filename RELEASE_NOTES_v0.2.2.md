# AI Traffic v0.2.2 – Safety, Powertrain Handling, Performance & Station Routing

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update introduces major thru-station routing fixes, frame-cached snapshot occupancy optimizations, single-track corridor holding, engine thermal derating, and mod compatibility fixes. 
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### 🚦 1. Thru-Station Pathfinding & Occupied Track Elimination
* **Strict Avoidance of Occupied Intermediate Tracks:** Pathfinder now strictly rejects intermediate tracks occupied by any railcars or rolling stock (`StrictlyAvoidOccupied = true`), preventing trains from being routed through occupied sidings or blocked mainlines.
* **Separation of Passing Loops vs. Industrial Storage Tracks:**
  * Passing loops and station passing sidings (`[S]`, `Siding`, `Loop`, `Pass`) are now differentiated from industrial storage tracks (`[Y]`, `[L]`, `[I]`, `[O]`, etc.).
  * Passing loops have a minimal preference cost penalty (+350m) so clear mainlines are chosen by default, but an empty passing loop is immediately chosen over an occupied mainline.
  * Industrial storage tracks retain heavy through-transit penalties (+500,000m) to keep ambient freight from intruding into yards.
* **Guaranteed Obstacle Detour Exclusion:** `TryDynamicObstacleReroute` now explicitly adds the blocked track to `ExcludedTracks` and enforces `StrictlyAvoidOccupied = true`, guaranteeing that dynamic detours find an alternate clear route around stopped traffic.
* **Dynamic Worker Siding Selection:** AI worker dispatching via the Comms Radio and WorkerManager now enforces strict occupancy avoidance, ensuring worker-driven trains never navigate through occupied station tracks.

---

### ⚡ 2. Frame-Cached Snapshot Occupancy & Lag Spike Elimination
* **O(1) Snapshot Occupancy Checks:** Replaced repeated $O(N)$ linear scans through `CarSpawner.Instance.AllCars` with a single-pass `HashSet<RailTrack>` snapshot (`BuildOccupiedTracksSnapshot()`), reducing track occupancy evaluations from hundreds of milliseconds to under 3ms.
* **Spatial Pre-Filtering:** In `TrafficScheduler`, RailGraph edge evaluations now check Euclidean distance and yard IDs *before* performing dead-end or occupancy checks, skipping ~98% of tracks immediately.
* **Bounded Route Search:** Capped candidate departure and destination track evaluations to the top 2 candidates per station during dispatch, slashing A* pathfinding search time from ~250ms down to <8ms per dispatch attempt.
* **True Consist Spawner Time-Slicing:** Ambient train cars are instantiated strictly one car per frame across 15–25 frames (0.25–0.4s total), eliminating the 400ms–1,200ms main-thread freeze.

---

### 🛡️ 3. Single-Track Corridor Holding & Deadlock Prevention (GitHub Issue #5)
* **Dynamic Lookahead Corridor Reservation:** AI trains evaluate single-track corridors ahead of passing loops. If an opposing train or conflict is detected downstream, the train holds safely inside the passing siding until the corridor clears.
* **Player Train Conflict Detection:** AI trains respect player-operated trains on single-track lines (while preserving Ride-Along mode when the player is a passenger on the AI train).
* **Deepened Rear Obstacle Buffers:** Rear-end collision buffers clamped to prevent trailing trains from running into stopped consists.

---

### 🔀 4. Switch Interlocking & Anti-Split Switch Protection (GitHub Issue #4)
* **Physical Bogie & Car Occupancy Checking:** Switches are strictly locked against throwing or alignment while any car body or bogie is physically straddling the switch points or clearance envelope.
* **Traversing Lock Extension:** Active trains automatically renew switch locks frame-by-frame while traversing junctions until the last car has cleared the points.
* **Approach Stopping Buffer:** Trains approaching misaligned or occupied switches decelerate and halt 25m prior to the switch.

---

### 🌡️ 5. Engine Thermal Derating & Stall Anti-Rollback (GitHub Issue #6)
* **Engine Temperature Monitoring:** Tracks engine coolant and oil temperatures across all locomotive types.
* **Proactive Throttle Derating:** Automatically rolls off throttle linearly between 95°C and 105°C to prevent blown prime movers.
* **Pneumatic Grade Anti-Rollback Clamp:** If an engine stalls on an upgrade, the pneumatic independent brake is clamped immediately to prevent runaway backward rollbacks down mountain grades.
* **DM3 Neutral Shift Brake Hold:** Automatically applies holding brakes during mechanical gear changes on grades.

---

### ⛰️ 6. Hill-Start Pre-Charge Sequence & DM3 High-Power Launch
* **DM3 Fluid-Coupling High Power Setting:** Mechanical DM3 locomotives require high engine RPM to build hydraulic torque across the fluid coupling on an incline. Launch throttle ceiling is boosted to 0.90–1.00 (initial notch: 0.70–0.80), with precharge thresholds raised to 0.60–0.85 and rapid notch rate (0.22/s). This guarantees full tractive effort before independent brakes graduate off, eliminating stalls and backward rollbacks.
* **Sensitive Grade Detection:** Uphill detection threshold lowered to > 0.15% grade, preventing false "level track" power clamping on moderate slopes.
* **Throttle Spool-Up Before Brake Release:** On upgrades (>= 0.15%), independent and train brakes remain firmly clamped while the prime mover ramps throttle up to launching power (0.40–0.85).
* **Staged Brake Handoff:** Air brakes exhaust first while independent locomotive brakes hold; independent brakes only graduate off once tractive torque overcomes train gravity.
* **Zero Rollback Guarantee:** Eliminates backward roll on starts, preventing blown traction motor fuses on diesel-electrics and drivetrain stalls on mechanical shunters.

---

### ⚡ 7. Traction Motor Fuse & Over-Current Protection
* **Fast Amperage Derating:** Monitored DE traction motor currents automatically derate throttle above 380A and cut power at 420A–480A to prevent fuse blowouts.
* **Stationary Blown Fuse Auto-Reset:** If a fuse trips during operation, the engineer automatically resets the tripped fuse once the consist is brought to a complete standstill.

---

### 🔗 8. Multiple Unit (MU) Automatic Cable Coupling
* **Automatic Consist MU Cables:** Adjacent locomotives and slug units (e.g. DE6 + DE6, DE2 + Slug) automatically couple both physical 3D Multiple Unit cables and logical control block propagators upon spawn and worker preparation.
* **Career License Bypass:** AI consist cable coupling executes directly without requiring player career license unlock.

---

### 🧲 9. Zeibach's Couplers Compatibility (GitHub Issue #2)
* **Stress Breakage Exemption on AI Consists:** When Zeibach's ZCouplers mod is active, AI trains are exempted from stress-induced coupler snapping via dynamic Harmony patching on `CouplerBreaker.FixedUpdate`.
* **Full Visual & Joint Fidelity:** Custom knuckle coupler visuals, 3D models, buffer visuals, and spring joints remain 100% active. Player-operated trains retain full breakable coupler mechanics.
* **Spawn Joint Impulse Suppression:** Newly coupled AI cars immediately clear settling joint stress to eliminate uncoupling during train instantiation.

---

### 🎥 10. Freecam 3D Nameplate Projection (GitHub Issue #1)
* **Dynamic Active Camera Tracking:** Updated world-to-screen projection to query `PlayerManager.ActiveCamera`, resolving `PlayerCameraOverride` when Freecam, Photo Mode, or Orbit Camera is active.
* **Eliminated Misalignment:** 3D locomotive tags and upcoming signal overlays now render precisely above trains and track masts in both first-person and freecam views.

---

### ⚙️ 11. Settings, Visuals & HUD Polish
* **Diegetic Worker Dispatch Description:** Updated UMM settings descriptions to clarify that AI workers are employed in-game via the Comms Radio tool in AI WORKER mode.
* **Mod Compatibility Checklist:** Added ZCouplers and updated CommsRadioAPI status indicators in the Mod Compatibility panel.
* **Synchronized HUD & 3D Nametag Colors:** In-world 3D nametags and HUD train entries are now color-coded to match their active route path lines on the tracks.
* **Cached Route Visualizer:** Track path line rendering is cached by track span, elevating line geometry 0.65m above rails to eliminate z-fighting with ballast while avoiding per-frame vertex recomputations.

---

### 🚀 12. Engine Stability, Memory & Clearance Polish
* **Corridor Fouling Buffer Expansion:** Increased single-track corridor hold distance from 25m to 45m (`Mathf.Max(5.0f, distToCorridorStart - 45.0f)`). This ensures holding trains park safely outside the switch's 35m physical clearance envelope, preventing false switch alignment locks against the oncoming train.
* **Reservation Yield on Hold:** AI trains holding for single-track corridors or stopped at signals temporarily yield downstream reservations, allowing passing trains to clear junctions without conflict.
* **A* Memory Pooling & Zero-GC Pathfinding:** Reusable collections for the Pathfinder's open set, best-cost table, and target nodes eliminate repeated heap allocations and GC spikes during route queries.
* **O(1) Station Indexing:** Fast dictionary lookups for station controllers and yard aliases (`CW`/`CSW`, `FM`/`FR`) replace linear searches across the station registry.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.2.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that DVSignals and CommsRadioAPI are active.