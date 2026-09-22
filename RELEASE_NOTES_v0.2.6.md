# AI Traffic v0.2.6 – Signal Interlocking, Switch Protection, Anti-Derailment & Deadlock Prevention

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update delivers major stability fixes targeting switch interlocking, corridor deadlocks, derailment prevention, and signal reservation clearance.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### 🛠️ What's New in v0.2.6

* **Player Corridor Right-of-Way & Signal Clearance:** AI now checks player DVSignals block reservations. If a single-track corridor is reserved by a player signal route, AI trains immediately release all junction locks and switch alignment requests to give the player full right-of-way.
* **Head-to-Head Encounter & Siding Deadlock Prevention:** Ambient trains approaching single-track corridors evaluate opposing traffic in advance and hold safely inside passing loops with deterministic tie-breaking.
* **Station Entry Signal Lockout Fix:** Corrected signal hierarchy so main signals equipped with distant repeater heads are properly recognized. AI now reserves strictly one governing signal at a time, preventing downstream station exit signals from locking entry signals to red.
* **Dynamic Chain-of-Switches Alignment:** Facing a red signal expands switch alignment lookahead up to 64 tracks and 3500m, proactively setting throat switches and waking signal controllers until aspects clear to green.
* **Refined Destination Despawning & Final Parking:** Trains no longer shut down prematurely at track entry switches; engines only idle down once fully inside the siding (within 35m of buffer/obstacle). All despawn checks now strictly respect the user-configured `DespawnDistance` setting.
* **Crash Recovery & World AI Car Purge:** Consist-wide derailment detection triggers emergency shutdowns and releases locks. Added automatic cleanup of crashed consists outside despawn range, plus a manual and automatic `Purge All AI Cars` tool that wipes orphaned ambient cars while safeguarding player trains.
* **In-Block Switch Protection & Player Interlock:** Switches inside an active signal block or within 250m of an approaching train (≥ 1.5 km/h) remain locked against manual or Comms Radio player interference, playing native rejection audio if thrown.
* **Physics-Based Derailment Prevention & Tail Speed Clamping:** Lowered curve minimum speed clamp from 30 km/h to 15 km/h, added 1800m centrifugal curve lookahead, and hold locomotive speed until the entire consist clears tight curves. AI Damage Immunity now fully suppresses derailment stress.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.6.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that DVSignals and CommsRadioAPI are active.
