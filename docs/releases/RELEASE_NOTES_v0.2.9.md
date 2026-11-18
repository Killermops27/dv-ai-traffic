# AI Traffic v0.2.9 – Urgent Signaling, Right-Hand Running, DM3/Thermal Fixes & Phase 2 Worker Expansion (Part 1)

> ⚠️ **RELEASE NOTE: PULLED FORWARD FOR URGENT FIXES**
> This **v0.2.9** update was **pulled forward ahead of schedule** to deliver urgent fixes for signaling, right-hand double-track running, DM3 transmission & thermal handling, MU headlight cross-talk, and AI worker stability.
> Because of this expedited release, **not all new features originally planned for v0.2.9 made it in**—remaining Phase 2 features (such as in-game selection for Multi-Stop Itineraries, Intermediate Tail-Cut Wagon Drop-Offs, Diegetic Station Paperwork / Itinerary Sheets, and full Autonomous Run-Around / Reverse Shoving) are deferred to a follow-up update.

> ⚠️ **EXPERIMENTAL ALPHA:** Always back up your save files before using or updating this mod. Report reproducible bugs on [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) or the [Nexus Mods Bug Tracker](https://www.nexusmods.com/derailvalley/mods/1685?tab=bugs).

---

### 🌟 What's New & Fixed in v0.2.9

#### 👷 Player-Employed AI Workers (Phase 2 — Included Features)
* **Worker Cancellation Refunds & Multi-Stop Architecture (`[WRK-07]`, `[WRK-11]`, `[WRK-12]`) *(Multi-Stop: Backend Architecture Only — Not Selectable In-Game Yet)*:**
  * **Active In-Game:** Pro-rated cancellation refunds with a **10s / 25m 100% refund grace period** calculated before driver teardown.
  * **Backend Architecture Only (Not Yet Selectable In-Game):** The underlying multi-leg `ItineraryLeg` engine, intermediate station dwells, non-blocking async waypoint departures (`Pathfinder.FindPathAsync`) with dead-end spur switchback support (`[WRK-02]`), and non-destructive 15s waypoint exit retries are implemented at the architecture level, **but in-game Comms Radio dispatch currently remains single A-to-B leg only** until the multi-stop UI/paperwork is completed in a future update.
* **Valley-Wide Comms Radio Active Worker Channel (`[WRK-08]`):**
  * Monitor active worker trains remotely from anywhere on the map via the **AI Worker** Comms Radio channel without line-of-sight targeting.
  * Issue remote in-flight orders: **Resume Run**, **Hold at Next Loop**, **Emergency Stop** (locks Train, Independent, and Handbrake once stopped), or inspect **30s Blocked-Track Escalation** options (*Wait 1/3/5 Min*, *Reroute Bypass*, *Backup to Siding*, *Hand Over Control*) backed by async option pre-evaluation.
* **In-Cab Co-Pilot Touch-to-Yield & Consumable Stall Capping (`[WRK-09]`, `[AIE-23]`):**
  * Stepping into an AI worker cab and moving any primary control (`Throttle`, `Brake`, `IndependentBrake`, `DynamicBrake`, or `Reverser`) cleanly yields control to the player (`PlayerCoPilotStandby`) after a 3.0s attach/cab-entry grace window and 0.15s per-lever settle guard, preventing false parked takeovers.
  * Re-engage AI driving smoothly (2.0s Hermite blend) via the configurable **`CoPilotResumeHotkey`** (default **`Ctrl+A`** in `settings.ini`) or Comms Radio **RESUME AI DRIVING**.
  * Freezes fuel, oil, and sand drain (`0%`) during stationary holds and caps consumption spikes during wheel slip or hill-start recovery.
* **Destination Approach Congestion Staging (`[WRK-10]`):**
  * Worker trains audit their destination arrival track within 2,000m; if occupied, they brake smoothly to a stop line outside the yard throat (outer loop, entry signal, or throat switch), release turnout locks, play a Comms Radio warning ping (`WorkerAudio`), and re-audit every 15s (auto-selected legs asynchronously adopt free alternate tracks; explicitly chosen tracks are never altered).

*(Deferred to a future update due to pulled-forward release: In-game UI/paperwork selection for Multi-Stop Itineraries, Intermediate Tail-Cut Wagon Drop-Offs, Diegetic Station Paperwork / Itinerary Sheets, and full mid-route Autonomous Run-Around / Reverse Shoving.)*

---

#### 🚦 Signaling, Interlocking & Double-Track Right-Hand Running
* **Type-Driven Signal Classification & World-Load Index Invalidation (`[SIG-CLS-1/2/3]`, `[SIG-IDX-1/2]`, `[SIG-DSP-1]`, `[SIG-RSV-1]`):**
  * Fixed classification of combined Main/Entry signal masts (such as Oil Well Central entry signal `A`) so they are never misidentified as passive distant signals.
  * Cleans and rebuilds the DVSignals index on scene unload, world load, and mod toggle-on with bounded post-load rescans (~2s / ~15s).
  * Fixed signal aspect labels and added a 5-second per-signal failure retry cooldown to eliminate per-tick DVSignals reservation contention storms.
* **Right-Hand Running (*Rechtsfahrgebot*) & CW–OWC Routing Fixes (`[RGR-07]`, `[RGR-12]`, `[JNC-10]`, `[QUIRK-17]`):**
  * Reduced non-turntable endpoint merge tolerance to `0.35m` so diamond crossings (e.g. `CWSXCD` / `DT-CWSB`) no longer weld into phantom degree-4 nodes.
  * Added safe same-direction following on paired right-hand double-track corridors instead of unnecessarily crossing onto wrong-way left tracks, plus proactive background async right-hand corridor realignment crossovers.
  * Prevented passing encounters and ambient trains from spawning wrong-way on center/left parallel tracks, fixed corridor token leaks, and exempted station through-tracks (e.g. Farm) and freight spur filtering edge cases.
* **Destination Exit Signal & Two-Open-Sided Track Stopping (`[AIE-17]`, `[AIE-19]`, `[AIE-20]`):**
  * Trains arriving on two-open-sided destination tracks now detect and stop cleanly ahead of the destination exit signal and forward switch instead of rolling into the throat.
  * Split `SpeedProfileGenerator` hold braking into distinct `ObstacleHold`, `CorridorHold`, and `SwitchHold` limiting reasons.

---

#### 🚂 DM3 Transmission, Thermal Protection, Hill-Start & Lighting Fixes
* **DM3 Gearbox & Speed Fixes (`[AIE-21]`, `[AIE-24]`, `[REQ-06]`):**
  * Fixed the DM3 `A2–B2` gear snap-back bug (DV's DM3 transmission rests in `A1–B1` with no neutral notch) and prevented gear shifts below `0.5 km/h`.
  * Froze hill-stagnation timers while the DM3 gearbox is shifting or unloading, preventing false `PermanentlyStalled` escalations during shifts.
  * Enforced a **`65 km/h` mainline speed cap** for DM3 locomotives and tightened `0.5%` hill threshold / flat rollback clamping.
* **Universal Thermal Overheat Cut & Dynamic Brake Cutoff (`[AIE-24]`):**
  * Enforced progressive thermal derating from `85°C` to `95°C` and immediate throttle cut at `>= 95°C` across all consist locomotives (including MU helpers).
  * Added a dynamic brake thermal cutoff above `90°C` with automatic fallback to light train air braking.
* **Bounded Hill-Stall & Terminal Parked Latch (`[AIE-22]`):**
  * `MomentumRunUp` now retries up to 3 times before declaring `PermanentlyStalled`, and a stall no longer falsely fires `OnTerminusArrival` / job completion.
  * Introduced a one-shot `SecureTrainForTerminalStop()` parked latch, eliminating per-frame reverser/headlight servo ping-pong on parked trains.
* **MU Cable Headlight Cross-Talk Fix (`[FLT-14]`):**
  * Trailing MU-connected helper locomotives are initialized prior to the lead unit and skipped for direct headlight/brake/reverser overrides, stopping rear helpers from extinguishing the lead locomotive's forward headlights at night.

---

### 📦 Requirements & Installation
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DVSignals** (Required)
4. **CommsRadioAPI** (Required)
5. **Double Track** (*Strongly Recommended*)
6. **Persistent Jobs** (*Fully Compatible & Officially Supported*)

**Install:** Download **`AITraffic-v0.2.9.zip`** and drag-and-drop into UMM, or extract into `Derail Valley/Mods/`.
