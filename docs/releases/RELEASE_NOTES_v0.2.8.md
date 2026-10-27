# AI Traffic v0.2.8 – Stability, Physics Overhaul, Single-Track Corridor Tokens & Advanced INI Configuration

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This major update addresses the top community-reported issues from v0.2.7, delivering crucial crash-to-desktop (CTD) fixes, a comprehensive wet-weather locomotive physics overhaul, atomic single-track corridor tokens with diegetic rollback maneuvers, yard through-transit protection, complete Military Base license gating, and an advanced INI configuration system.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page or the [Nexus Mods Bug Tracker](https://www.nexusmods.com/derailvalley/mods/1685?tab=bugs)!

---

### 🧩 Compatibility Spotlight: Persistent Jobs is FULLY Supported!
> **Persistent Jobs is now 100% officially supported and fully integrated into AI Traffic as of v0.2.8.**  
> Even though the *Persistent Jobs* mod page or description may currently state that AI Traffic is unsupported or incompatible, **this information is outdated**. AI Traffic v0.2.8 includes dedicated integration patches that reflect lightweight suspended cars (`FarCarOpt`) in occupancy checks and pathfinding, enforce track-specific yard protection, and segregate debt ledgers so both mods run together seamlessly!

---

### 🌟 Release Highlights & What's New in v0.2.8

#### 🛡️ Stability, Memory & Crash to Desktop (CTD) Fixes
* **30-Minute Vanilla Job Desynchronization CTD Fix:**
  * Resolved the recurring ~30-minute crash-to-desktop caused by vanilla Derail Valley's procedural job generation cycle (`jobGenerationTimeout = 1800f`).
  * Replaced broad station-wide destruction zone locks with **track-specific protection** (`Yard.IsTrackOccupied`). Vanilla DV is now free to refresh unoccupied yard tracks on its normal 30-minute schedule, preventing overlapping PhysX collision explosions, NaN coordinates, and native crashes.
* **Nested Recursive Harmony Patch Elimination:**
  * Replaced recursive `StationController.IsPlayerInJobGenerationZone` calls in `StationWakeUpPatches.cs` with direct distance checks against `JobGenerationZoneRadiusSqr`, eliminating stack overflow crashes.
* **Debounced Event-Driven Signal Wakeups:**
  * Eliminated per-frame synchronous DVSignals graph traversal storms during switch lookahead. Signal controllers now evaluate wakeups strictly upon turnout state changes or fresh block reservations, protected by a 0.5s per-signal cooldown debounce.
* **Static Cache Leak & Spawner Coroutine Cleanup:**
  * Fixed static station cache leaks by hooking into `TrainCar.OnDestroy` and `DespawnAITrain`.
  * Added `TrafficScheduler.ResetSpawningState()` on scene unload to cleanly reset dispatch flags (`_isDispatching = false`), clearing corridor states and desynchronized engineer references to resolve the mid-game spawner lockup and save reload "Max Reached" bugs.

#### 🌧️ Wet Rail Adhesion, Steep Grades & Locomotive Physics
* **DE2 Slip-Grip Oscillation Elimination & 5-Stage Hill-Start Architecture:**
  * Fixed an issue where diesel-electric locomotives on steep wet grades entered an endless 1–2 second slip-grip oscillation cycle due to premature hill stagnation timer decay.
  * Introduced cumulative 3D ground progress tracking over a 6-second window that prevents timer decay during transient grip cycles.
  * Implemented an automated **5-Stage Hill-Start State Machine**:
    1. `Stage1_Clean`: Initial clean start without sand.
    2. `Stage2_Sanded`: Continuous sanding (triggered immediately in rain or upon stagnation $\ge 3.0\text{s}$).
    3. `RollbackDescent`: Controlled descent to a flatter grade (< 0.5% or max 200m) after continuous sanding timeout.
    4. `MomentumRunUp`: Aggressive re-launch with continuous sanding and accelerated air bleed ($0.60/\text{s}$) to tackle the grade with momentum.
    5. `PermanentlyStalled`: Safe train containment and notification if momentum run-up fails.
* **Active Wheel Slide Protection (WSP):**
  * Decoupled power wheel slip from brake slide detection. AI now continuously scans `TrainCar.adhesionController.IsWheelSliding` across the locomotive and all consist cars.
  * In rain or wet conditions, service braking deceleration is dynamically scaled ($0.45 \to 0.27\text{ m/s}^2$) and approach pre-sanding initiates within 250m of stop targets.
  * Upon detecting a kinetic wheel slide, the AI cuts dynamic braking, continuously applies sand (`_sandHoldTimer = 3.0s`), and modulates train brake authority (`_wspBrakeAuthority = 0.55`) to break wheel lockup before smoothly restoring service pressure.
* **Multiple Unit (MU) Helper Automation:**
  * Lead locomotives now spawn with `BrakeCutout = 1.0f` and `IndependentBrake = 0.0f`.
  * Trailing helper locomotives are automatically configured with double-heading cut-outs closed (`BrakeCutout = 0.0f`), independent brakes released (`0.0f`), and train brakes released (`0.0f`), resolving immobilized multi-locomotive consists.
  * AI Worker hiring automatically couples MU air cables and starts trailing engines.
* **Demonstrator Locomotive Preservation & Sea-Level Spawn Guard:**
  * Restrictive AI tagging (`AITrafficCarMarker` and `ModCompatManager.IsAmbientAITrain`) prevents derailed museum demonstrator locomotives at City South from being falsely purged as ghost cars.
  * Enforced spawn position validation against `Vector3.zero` and rail center distance $< 0.1\text{m}$, preventing locomotives from spawning at sea level offshore.

#### 🚦 Signaling, Interlocking & Corridor Routing
* **Atomic Single-Track Corridor Tokens & Standoff Prevention:**
  * AI trains must atomically acquire a canonical, direction-independent **Single-Track Corridor Token** covering the entire continuous single-track segment between passing loops before departing.
  * Single-track corridor reservations are strictly exempt from the periodic 0.4s signal block release sweep until the train physically clears the exit turnout.
* **Diegetic Single-Track Standoff Yield & Rollback Maneuver:**
  * If two trains meet face-to-face on single track (e.g. against an unannounced player movement), the AI executes an automated yield maneuver:
    1. Brings the consist to a complete halt and yields right-of-way.
    2. Sounds **three (3) short 2-second horn bursts** (standard railway reverse warning).
    3. Swaps front headlights to **illuminated red marker/tail lights** to visually signal reverse movement.
    4. Backs up at $\le 15\text{ km/h}$ to the nearest trailing siding.
    5. Throws the turnout, enters the clear siding, sets the parking handbrake, and leaves the mainline clear.
* **Turnout Lock Interlocking & AI Preemption ([JNC-09]):**
  * Protects crossover switches in reserved signal blocks while holding an active DVSignals reservation so trailing crossovers are not dropped before signals clear.
  * Enforces switch alignment retry cooldowns (3s moving, 5s stationary) to prevent 0.4s crossover thrashing.
  * Establishes a strict partial order for AI-vs-AI turnout lock preemption based on signal reservations and train priority.
* **Dynamic Alternate Siding Search & Station Throat Hold:**
  * Ambient trains approaching an occupied destination track automatically evaluate alternate receiving/yard tracks ($\ge \text{consist} + 25\text{m}$) and repath seamlessly.
  * If the entire destination yard is congested, the train safely holds outside the station throat and releases all turnout locks so player shunting is not obstructed, displaying an on-screen toast notification and polling for openings every 30 seconds.
* **Siding Shunting Mode Handover & Multi-Head Signal Clearance:**
  * Resolved the bug where trains leaving sidings (such as L-059 and L-088) remained trapped at 15 km/h on mainlines.
  * Implemented a 4-layer physical mast crossing architecture and aspect latching via `SignalRegistry.IsSignalDisplayingClear`, transitioning smoothly to full track line speed upon entering interlocked mainline blocks.
* **Station Yard Through-Transit Elimination & Central Running Line Protection:**
  * Yard classification, storage, and transfer tracks (`-O]`, `-I]`, `-S]`, `-C]`, `-P]`) are assigned a +500,000m routing penalty to strictly prevent through trains from cutting through yards.
  * Explicitly protects central running lines through yard throats ending in `[--]` (e.g. `[Y]_[OWC]_[--]`), exempting them from yard penalties and speed restrictions.
* **Persistent Jobs Suspended Car Visibility:**
  * Fully integrated with `PersistentJobsMod.Optimization.FarCarOpt.TracksToSpaceOccupiedBySuspendedCars`, making suspended cars 100% visible to AI occupancy checks and A* pathfinding.
* **Complete Military Base License Gating:**
  * AI traffic is completely excluded from routing through or spawning at all three military installations (**Military Base `MB`**, **Harbor Town `HB` military spur**, and **Machine Factory `MF` military section**) unless the player has unlocked the `Military 1` career license.

#### ⚙️ Configuration & Immersion
* **Advanced INI Configuration (`Mods/AITraffic/settings.ini`):**
  * Real-time tunable parameters across `[Physics]`, `[Dispatcher]`, `[Economy]`, `[Consists]`, and `[Lighting]`.
  * Auto-generates a fully documented, heavily commented default configuration file on first launch.
  * Added an **Apply & Reload INI** button in the Unity Mod Manager settings menu for runtime reloading without restarting the game.
* **AI Locomotive Headlight Modes:**
  * 4 selectable modes: `Automatic (Day/Night/Tunnel)` [Default], `Always High Beam`, `Always Low Beam`, and `Always Off`.
  * Automatic activation of cab gauge backlights and dome lights at night.
* **Consist End-of-Train (EOT) Markers:**
  * Passenger coaches and rear light engines illuminate native electric red tail lights.
  * Freight consists and cabooses spawn and mount a diegetic red End-of-Train (EOT) marker lantern onto the rear coupler/bracket.
* **Player Worker System Refinements:**
  * Fully verified terminus stops (`StationaryTimer >= 2.0s` AND rear bogie on destination track), setting handbrakes, releasing all turnout locks, removing damage immunity, and returning 100% manual control to the player upon arrival or task cancellation.

---

### 📦 Requirements & Compatibility
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)
6. **Persistent Jobs** (**Fully Compatible & Officially Supported!**)

---

### 🛠️ Installation
1. Download **`AITraffic-v0.2.8.zip`**.
2. Drag and drop the ZIP archive into Unity Mod Manager, or extract it directly into `Derail Valley/Mods/`.
3. Verify that AI Traffic, DVSignals, and CommsRadioAPI show green status indicators in UMM (`Ctrl + F10`).
