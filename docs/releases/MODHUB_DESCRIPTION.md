# Derail Valley AI Traffic (`AITraffic`)

[![Game: Derail Valley](https://img.shields.io/badge/Game-Derail%20Valley-blue.svg)](http://www.derailvalley.com/)
[![Mod Loader: UMM](https://img.shields.io/badge/ModLoader-Unity%20Mod%20Manager-orange.svg)](https://www.nexusmods.com/site/mods/21)
[![Requires: DVSignals](https://img.shields.io/badge/Requires-DVSignals-green.svg)](https://github.com/WhistleWiz/dv-signals)
[![Requires: CommsRadioAPI](https://img.shields.io/badge/Requires-CommsRadioAPI-purple.svg)](https://github.com/fauxnik/dv-comms-radio-api)
[![Compatible: ZCouplers](https://img.shields.io/badge/Compatible-ZCouplers-blueviolet.svg)](https://github.com/mspielberg/dv-zcouplers)
[![Latest Release: v0.2.8](https://img.shields.io/badge/Release-v0.2.8-brightgreen.svg)](https://github.com/Killermops27/dv-ai-traffic/releases/tag/v0.2.8)
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

## 🌟 What's New in v0.2.8

- 🛡️ **30-Minute Vanilla Job Desynchronization CTD Fix:** Replaced broad station-wide destruction zone locks with track-specific protection (`Yard.IsTrackOccupied`). Vanilla DV cleanly refreshes unoccupied yard tracks every 30 minutes without PhysX overlapping explosions, NaN coordinates, or crashes.
- 🔁 **Stack Overflow & Patch Optimization:** Replaced nested recursive `StationController.IsPlayerInJobGenerationZone` calls with direct distance comparisons, eliminating recursive stack overflow crashes.
- 🚦 **Debounced Signal Wakeup Calls:** Eliminated per-frame synchronous DVSignals graph traversal storms with a 0.5s per-signal cooldown debounce, triggered strictly on turnout realignment or fresh block reservations.
- 🌧️ **Wet-Weather Physics & 5-Stage Hill-Start Architecture:** Introduced 6-second cumulative ground progress tracking to eliminate the DE2 slip-grip oscillation trap, immediate Stage 2 sanding in rain, and an automated controlled rollback descent mechanism.
- 🛑 **Active Wheel Slide Protection (WSP):** Consist-wide brake slide detection modulates brake authority (`0.55`), cuts dynamic brakes on locomotive slide, scales wet service deceleration ($0.45 \to 0.27\text{ m/s}^2$), and triggers continuous sanding within 250m of stop targets.
- 🚂 **Multiple Unit (MU) Helper Automation:** Trailing helper locomotives automatically configure double-heading cut-outs closed (`BrakeCutout = 0.0f`), release independent brakes (`0.0f`), and couple MU air lines.
- 🏛️ **Demonstrator Loco Preservation & Spawn Validation:** Protects museum demonstrator locomotives at City South from false AI ghost purges, and validates spawn coordinates against `Vector3.zero` to prevent ocean spawns.
- 🔄 **Atomic Single-Track Corridor Tokens & Diegetic Reverse Protocol:** Atomic tokens prevent opposing corridor entries; if an unavoidable standoff occurs, the AI sounds 3 horn blasts, illuminates front red marker lights, and backs up at $\le 15\text{ km/h}$ into the nearest trailing siding.
- 🔀 **Destination Siding Search & Station Throat Hold:** Diverts to clear alternate sidings when the scheduled destination track is occupied, or safely holds outside the station throat and releases turnout locks to preserve player shunting freedom.
- 🏎️ **Siding Shunting Handover & Mainline Speed Restoration:** Implemented a 4-layer mast crossing check and multi-head clearance latching, ending the bug where trains leaving sidings crawled at 15 km/h across the mainline.
- 🚫 **Yard Through-Transit Elimination & Running Line Protection:** Applies a +500,000m routing penalty to station yard tracks (`-O]`, `-I]`, `-S]`, etc.) while protecting central running lines (`[--]`) at full line speed.
- 👻 **Persistent Jobs Suspended Car Visibility:** Integrates with `FarCarOpt.TracksToSpaceOccupiedBySuspendedCars` so lightweight suspended cars are fully recognized by AI dispatching and pathfinding.
- 🎖️ **Military Base License Gating:** Gated AI access across Military Base (`MB`), Harbor Town (`HB` military spur), and Machine Factory (`MF` military yard) behind the `Military 1` license.
- ⚙️ **Advanced INI Configuration (`settings.ini`):** Full runtime control over physics PID gains, dispatcher safety buffers, economics, and lighting modes, with an in-game UMM "Apply & Reload INI" button.
- 💡 **AI Lighting Modes & EOT Markers:** 4-mode locomotive headlight selector (Automatic, Always High, Always Low, Off) and diegetic rear End-of-Train (EOT) marker lanterns on freight consists and cabooses.

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
- **DVSignals Integration & Remote Keep-Alive:** Interacts directly with DVSignals for physical signal aspect resolution, block reservation, and headway control down to the 0m stop line, keeping distant signal controllers active within 1500m of AI trains.
- **Periodic Signal Wait Retry:** Automatically re-requests switch alignment and wakes DVSignals controllers every 20s when halted at red signals.
- **Player Corridor Right-of-Way:** AI respects player DVSignals reservations, immediately yielding switch alignment requests to give player consists right-of-way.
- **In-Block Switch Protection:** Turnouts inside an active signal block or within 250m of a moving AI train remain locked against unauthorized throwing.
- **Tail Speed Protection:** Evaluates consist-wide bogie positions, ensuring entire trainsets clear restrictive curves and turnout ladders before accelerating.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **`AITraffic-v0.2.8.zip`**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that AI Traffic, DVSignals, and CommsRadioAPI show green status indicators in UMM (`Ctrl + F10`).
