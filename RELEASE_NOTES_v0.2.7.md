# AI Traffic v0.2.7 – Interlocking Stabilization, Async Pathfinding & Performance Overhaul

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update delivers major systemic stability and performance improvements across signaling interlocking, switch protection, dynamic station job generation, physical hill starts, and background pathfinding.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### 🛠️ What's New in v0.2.7

#### ⚡ Performance, Spawning & Physics
* **Locomotive Career License Blocker Elimination & Lag Spike Fix:**
  * Fixed severe micro-stutters and 60 Hz log spam when operating locomotives for which the player lacks career licenses (DE6, DH4, S282).
  * Automatically removes `LocoZoneBlocker` components on AI locomotives upon spawn/dispatch, restoring normal interior loading and cab teleportation for Ride-Along mode without disturbing player yard locomotives.
* **Station Job Generation Infinite Loop & FPS Spam Elimination:**
  * Fixed mutual exclusion bug between station job generation and destruction zones. Prevents 30–60 Hz flip-flop loops that previously generated continuous `[Station] job generation started/stopped` console spam and yard car flickering.
* **Asynchronous Background Route Pathfinding & Zero-Allocation Caching:**
  * Heavy full-valley A* path calculations now execute asynchronously on background threads (`FindPathAsync`), completely freeing the Unity main thread and eliminating 30ms–150ms frame hitches during ambient spawn cycles.
  * Precomputed immutable topology fields (`MidPoint`, `Grade`, `FromTangent`, `ToTangent`) and cached traversable edge lookups reduce pathfinding GC allocations to zero.
* **Scene Reload & Zombie Consist Recovery:**
  * Cancels active spawner coroutines immediately on scene unload (`sceneUnloaded`), applies immediate per-car AI tagging on spawn, enforces atomic spawn rollbacks on errors, and introduces an in-game `[ 💥 Purge Derailed ]` tool to cleanly clear orphaned ghost cars.
* **Dynamic Physics-Adaptive Hill Starts & Balanced Bleed:**
  * Dynamic equilibrium throttle model computes holding power against grade and consist mass. Progressive simultaneous train brake bleed eliminates stalls, micro-drift backward runaway, and traction wheel slip.

#### 🚦 Signaling, Switches & Corridor Routing
* **Switch-Blade 7m Zone & Turnout Lock De-Thrashing:**
  * Reduced switch fouling margin from 60m to a realistic 7.0m movable blade envelope (`SwitchBladeZoneMeters = 7.0f`). Parked cars on sidings no longer foul main turnouts, preventing switch alignment lockouts.
  * Eliminated high-frequency lock release and re-lock thrashing on stopped trains.
* **Station Entry Signal Reservation Purge & Interlocking Fallback:**
  * Scans station tracks and safely purges orphaned downstream route reservations in DVSignals `TrackReserver`, resolving indefinite `Hp 0` red signal lockouts.
  * Differentiates reservation ownership to protect other active trains and eliminate reservation ping-pong thrashing.
* **Distant Signal Advance Warning Braking Fix:**
  * Corrected distant advance warning evaluation (`NEXT_STOP` / `VR0`) to calculate braking distance to downstream governing masts instead of halting at green proceed masts.
* **Stopped Switch Manual Throwing:**
  * Confined approach switch locking strictly to moving trains (≥ 1.5 km/h). Switches remain 100% unlocked while stopped so players can align turnouts manually without AI contention.
* **Strict Yard Track Avoidance & Emergency Slow Shunting Detours:**
  * Mainline routing strictly avoids intermediate loading (`-L]`), transfer (`-I]`, `-O]`), shunting (`-S]`), and turntable (`[T]`) tracks.
  * Trains halted at red signals for ≥ 30s can take emergency slow shunting detours (≤ 15 km/h) through verified clear yard bypasses.
* **Balloon Loop Route Self-Block & Turnout Priority:**
  * Consist-length-aware cycle detection avoids turning loops shorter than train length, enforces first-instance switch setting priority, and crawls on sight through self-occupied signal blocks.
* **Emergency Deadlock Despawn for Stalled Ambient Trains:**
  * Procedural ambient trains deadlocked or stalled for ≥ 5 minutes are cleanly despawned, releasing all locks and reservations without touching player consists or worker trains.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.7.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that AI Traffic, DVSignals, and CommsRadioAPI show green status indicators in UMM (`Ctrl + F10`).
