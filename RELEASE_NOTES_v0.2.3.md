# AI Traffic v0.2.3 – Double Track Routing, Signal Interlocking & Traffic Flow

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update introduces major signal interlocking fixes, Double Track crossover routing, anti-split switch protection, and encounter-driven traffic scheduling. 
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### 🚦 1. Remote DVSignals Controller Keep-Alive
* **Background Signal Evaluation:** Implemented Harmony postfix on `BasicSignalController.ShouldUpdate` to keep signal controllers awake when AI trains approach within 1500m, even when the player is far across the map.
* **Eliminated False Clear Signals:** Eliminates dormant signals freezing in clear aspects when far from the player camera, ensuring signals accurately reflect downstream block occupancy.

---

### 🔀 2. Double Track Crossovers & Mirrored Mast Compatibility
* **Mirrored Mast Aspect Resolution:** Added detection for inverted mast localScale (`transform.localScale.z < 0.0f`) applied by `RealisticSignalPlacer` in the Double Track mod. Correctly negates the signal facing vector so AI trains read governing signals accurately on mirrored crossover masts.
* **Staggered Parallel Track Pairing:** Enhanced `RailGraph` with perpendicular lateral offset math, properly detecting and pairing staggered parallel double-track segments across curves and elevation shifts.
* **Crossover Track Penalties:** Pathfinder now penalizes unnecessary zigzagging across mainline crossovers (`CX`, `Cross`, `Xing`), maintaining smooth, consistent right-hand running on double-track corridors.

---

### 🛡️ 3. Signal Route Verification & Switch Alignment Interlocking
* **False Green Rejection:** If an upcoming signal shows green but governing switches within its immediate block are not yet aligned (e.g., waiting on switch lock or player clearance), the AI engineer flags the signal as untrustworthy and enforces a 0 km/h stop line 15m before the signal until switches are physically thrown and locked.
* **Whole-Trainset Switch Straddle Detection:** `JunctionController` now evaluates entire trainsets across `inBranch` and all `outBranches`. If any car or bogie in a consist is spanning switch points, the switch is unconditionally locked against throwing, eliminating switch splits and derailments caused by collider trigger lag.
* **Forward Angular Continuity:** Pathfinder enforces vector continuity (`alignment <= 0.0f`), strictly forbidding acute-angle hairpin U-turns (> 90 degrees) across junction frogs.

---

### 🧠 4. Ambient Reverser Locking & Dynamic Off-Route Recovery
* **Reverser Oscillation Lock:** Ambient trains now lock their reverser at spawn (`_ambientLockedReverser`), completely eliminating reverser flapping or false backward travel.
* **Dynamic Off-Route Rerouting:** If an ambient train is diverted off its planned route (e.g. by manual player switch throws), the engineer waits for the rear car to clear and dynamically calculates an A* recovery path forward to its destination or a safe siding rather than stalling or emergency-stopping.
* **Corridor Right-of-Way Optimization:** If an opposing train is already stopped in its passing loop, oncoming trains recognize their clear right-of-way and traverse single-track corridors without mutual waiting.

---

### 💨 5. Brake Pipe Integrity & Spawner Time-Slicing
* **Outer Angle Cock Securing:** Train spawner automatically closes the outer uncoupled angle cocks at the train's front and rear ends (`IsCockOpen = false`), guaranteeing the pneumatic brake pipe holds full air pressure from spawn.
* **Staggered Coupler & Locomotive Setup:** Coupler connections and locomotive initializations are time-sliced across frames (1 locomotive per frame), eliminating spawn lag spikes while properly precharging brake reservoirs.

---

### 🚉 6. Encounter-Driven Traffic Scheduling & Siding Variety
* **Realistic Player Encounter Bias:** `TrafficScheduler` prevents trains from spawning within 1000m of the player, while prioritizing stations between 1000m and 3000m. Spurred trains travel *toward* the player for frequent, organic mainline meets.
* **Origin & Destination Anti-Repetition:** Tracks both recent origin and destination yards (`_recentOrigins`, `_recentDestinations`), preventing repetitive spawn loops from the same stations.
* **Track Shuffling & Siding Selection:** Candidate flat departure tracks and destination receiving sidings are randomized across spawns, distributing traffic realistically across station yards.
* **Station Alias Additions:** Added Coal Power Plant (`CP` / `CPP`) station aliases for robust corridor generation.

---

### 🧹 7. Tightened Encounter Despawn Lifecycle
* **Prompt Encounter Pruning:** Ambient trains that have traversed past the player and moved beyond 1000m (outside camera frustum and moving away) despawn promptly, freeing mainline slots and system memory for fresh spawns ahead of the player.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.3.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that DVSignals and CommsRadioAPI are active.
