# DV AI Traffic — Phase 2 Technical Implementation Plan: Player-Employed AI Worker System

> **DOCUMENT TYPE:** Technical Architecture & Phased Implementation Plan  
> **TARGET SYSTEM:** `dv_ai_traffic`  
> **SCOPE:** Player-Employed AI Worker System (Phase 2)  
> **PRIMARY SPECIFICATION:** [`dv_ai_traffic/phase2_spec.md`](file:///z:/Gemini/DerailValley/dv_ai_traffic/phase2_spec.md)  
> **ARCHITECTURAL BASELINE:** [`dv_ai_traffic/VISION_AND_ARCHITECTURE.md`](file:///z:/Gemini/DerailValley/dv_ai_traffic/VISION_AND_ARCHITECTURE.md)  
> **INVARIANT REGISTRY:** [`dv_ai_traffic/GRAVEYARD.md`](file:///z:/Gemini/DerailValley/dv_ai_traffic/GRAVEYARD.md)  
> **STATUS:** In Progress — Packages 1, 2 & 3 Complete (`[WRK-07]`, `[WRK-08]`, `[WRK-09]`)  

---

## 1. Executive Summary & Architectural Strategy

The Phase 2 specification elevates the AI worker subsystem in `dv_ai_traffic` from a single-leg point-to-point delivery utility (`AtoBHaulTask`) into a **diegetic, multi-stage rail operations engine**. The system must orchestrate multi-stop itineraries, physical car drop-offs (set-outs), locomotive run-arounds, outer yard staging, valley-wide radio communications, and smooth in-cab copilot transitions—all while respecting strict physical grounding, career ledger safety, and zero-tolerance track occupancy invariants.

### The Complexity Challenge & Staged Decomposition
Attempting to implement all 10 goals in a single monolithic pass carries an unacceptable risk of regressions in train physics, switch interlocking, and save game persistence. To guarantee stability and facilitate iterative testing, the 10 goals are decomposed into **7 logically paired, modular packages** arranged in a strict dependency sequence:

```
               [Package 1: Core Itinerary & Ledger Foundation — COMPLETED]
                                     |
               [Package 2: Valley-Wide Radio Spine & Dispatch — COMPLETED]
                                     |
               +---------------------+---------------------+
               |                                           |
               v                                           v
[Package 3: In-Cab Copilot & Fuel/Wear — COMPLETED]  [Package 4: Mainline Flow & Congestion Staging]
               |                                           |
               +---------------------+---------------------+
                                     |
                                     v
               [Package 5: Turnaround & Shoving Switchbacks]
                                     |
                                     v
               [Package 6: Intermediate Consist Set-Outs]
                                     |
                                     v
               [Package 7: Diegetic Paperwork & Desk Spawning]
```

Each package is self-contained, introduces zero regressions to the existing ambient traffic or single-leg worker systems, and can be completely developed, validated, and merged before progressing to the next.

---

## 2. Logical Pairings & Dependency Matrix

The table below outlines how the 10 specification goals are paired into discrete implementation packages, detailing the rationale, structural coupling, and primary architectural boundaries.

| Package | Goal Pairing | Architectural Domain | Coupling Rationale |
| :--- | :--- | :--- | :--- |
| **Package 1** ✅ | **Goal 1 (Model) + Goal 10** | Core Domain & Ledger | **[COMPLETED — `[WRK-07]`]** *Itinerary Models & Financial Accounting.* Unified `AtoBHaulTask` (`ItineraryLeg` list, `CurrentLegIndex`), upfront multi-leg billing (`HireDriverForItinerary`, `CalculateLegVariableFee`), intermediate leg dwell & engine preservation (`WaitingAtIntermediate`, `AssignNextItineraryLeg`), and pre-teardown cancellation refunds (`CalculateCancellationRefund` with 10s / 25m 100% grace window and pro-rated variable fee refund). |
| **Package 2** ✅ | **Goal 4 + Goal 9** | Remote Communications | **[COMPLETED — `[WRK-08]`]** *Valley-Wide Radio & Escalation Dispatch.* Remote inspection, live telemetry, and active fleet carousel (`WorkerRadioActiveListState`, `WorkerRadioTrainDetailState`), in-flight directives (`WorkerRadioInFlightCommandsState`: *Resume Run*, *Hold at Next Loop*, *Emergency Stop*, *Abort Haul*), and blocked-train escalation menu (`WorkerRadioBlockedMenuState`: *Wait 1/3/5 Min*, *Reroute Bypass*, *Backup to Siding*, *Hand Over*) backed by background async pre-evaluation and stationary max-brake locking (`[WRK-08]`). Integrates infinite intermediate dwell (`DwellSeconds < 0f`) with engine shutdown awaiting Comms Radio `Resume Run`. |
| **Package 3** ✅ | **Goal 7 + Goal 8** | Cab Controls & Powertrain | **[COMPLETED — `[WRK-09]`]** *In-Cab Copilot & Physical Consumables.* In-cab "Touch-to-Yield" (`PlayerCoPilotStandby`), full secondary servo write suppression (`UpdateSanderActuation`, `DM3TransmissionController`, `UpdatePowertrainProtection`, horn, 30s blocked alert), configurable hotkey (`Ctrl+A`), Comms Radio `RESUME AI DRIVING`, 2.0s Hermite blend, destination arrival preservation, and physical `FUEL`/`SAND`/`OIL` stall capping (0% drain during stationary holds, 20%/25% ceiling during slip/hill recovery). |
| **Package 4** | **Goal 1 (Exec) + Goal 6** | Navigation & Flow Control | *Sequential Legs & Congestion Staging.* Station arrival flow control connects waypoint dwell timers (Goal 1 execution) with approach congestion auditing and outer passing loop holding (Goal 6). |
| **Package 5** | **Goal 5** | Physical Switching & Pathing | *Run-Arounds & Buffer Switchbacks.* Reversing light engines around consists and slow reverse shoving ($\le 15\text{ km/h}$) require dedicated state machines for uncoupling, light-engine pathfinding, trailing switch handling, and recoupling. |
| **Package 6** | **Goal 2** | Trainset Reconfiguration | *Intermediate Consist Set-Outs.* Dropping trailing cuts of cars at intermediate sidings builds directly on the waypoint dwells of Package 4 and the pneumatic/uncoupling primitives established in Package 5. |
| **Package 7** | **Goal 3** | World Immersion & Spawning | *Diegetic Station Paperwork.* In-game physical itinerary sheets and cab clipboards represent the physical manifestation of itineraries, requiring all underlying itinerary models, wage engines, and radio hooks to be stabilized first. |

---

## 3. Detailed Technical Packages & Implementation Steps

---

### Package 1: Multi-Leg Itinerary Foundation & Financial Ledger ✅ `[COMPLETED — WRK-07]`

#### 1.1 Objective & Scope
- Formalize the multi-stop journey architecture by upgrading `AtoBHaulTask` into a unified multi-leg itinerary model (`List<ItineraryLeg> Legs`, `CurrentLegIndex`).
- Implement full itinerary fee estimation (`CalculateHiringFee` + `CalculateLegVariableFee`), upfront wallet deduction (`HireDriverForItinerary`), and pre-teardown pro-rated refund calculations (`CalculateCancellationRefund`) for cancelled or failed hauls.
- Ensure 100% backward compatibility with single-leg hauls (a single-leg haul is an itinerary with $N=1$).

#### 1.2 Implemented Architecture & Data Flow (`[WRK-07]`)
- **Data Model (`AtoBHaulTask` & `ItineraryLeg`):**
  - `ItineraryLeg` defines `LegIndex`, `OriginStation`, `OriginTrack`, `DestinationStation`, `DestinationTrack`, `IsAutoSelectedTrack`, `RouteDistance`, `LegFee`, `DwellSeconds`, and `ItineraryLegStatus` (`Pending`, `EnRoute`, `Completed`, `Cancelled`, `Failed`).
  - `AtoBHaulTask` encapsulates `List<ItineraryLeg> Legs` and `int CurrentLegIndex`, dynamically resolving scalar properties (`OriginStation`, `OriginTrack`, `DestinationStation`, `DestinationTrack`, `IsAutoSelectedTrack`, `RouteDistance`) from `CurrentLeg` while tracking `HiringFee` / `TotalHiringFee`, `BaseFeeCharged`, `RefundedAmount`, `TotalRouteDistance`, and `InitialWorldPosition`.
  - Intermediate arrival transitions to `HaulTaskStatus.WaitingAtIntermediate`, preserving the running prime mover (`!WorkerManager.ShouldKeepEngineRunningOnArrival(this)`) and advancing to the next leg via `WorkerManager.TryAdvanceToNextItineraryLeg` and `AIEngineer.AssignNextItineraryLeg`. *(Optional infinite dwell with engine shutdown awaiting Comms Radio Resume is deferred to Package 2).*
- **Financial Accounting Model:**
  - **Upfront Multi-Leg Billing:** `DriverBaseFee` (`$300`) is charged once on Leg 0 (`CalculateHiringFee`), while subsequent legs (`Leg 1..N-1`) charge `CalculateLegVariableFee` (distance + tonnage rounded to `$10`). The total itinerary fee is deducted upfront via `Inventory.Instance.RemoveMoney(totalFee)`.
  - **Pre-Teardown Cancellation Refund (`CalculateCancellationRefund`):** Evaluated in `WorkerManager.CancelTask` **before** `task.Engineer` is destroyed (preventing `RemainingDistance` from zeroing).
    - **Grace Period:** If cancelled on Leg 0 within $\le 10.0\text{s}$ AND $< 25.0\text{m}$ moved from `InitialWorldPosition`, **100% of `HiringFee`** (including `DriverBaseFee`) is refunded.
    - **Post-Grace Pro-Rata:** After the grace window, `DriverBaseFee` and `Completed` legs are non-refundable, `Pending` future legs refund **100%**, and the active `EnRoute` leg refunds its variable portion (`LegFee - BaseFeeCharged` on Leg 0, `LegFee` on Leg $k > 0$) pro-rated by `Mathf.Clamp01(remDist / legDist)`, rounded to the nearest `$10`, and credited via `Inventory.Instance.AddMoney(refund)`.
  - **Operational Failure Refund:** If `TryAdvanceToNextItineraryLeg` cannot find a valid route for an upcoming leg, `CancelTask(task)` triggers automatically, refunding all remaining unperformed legs and returning control to the player.

#### 1.3 Target Subsystems & File Locks (Completed)
- `src/Workers/AtoBHaulTask.cs` (`ItineraryLeg`, `ItineraryLegStatus`, `HaulTaskStatus.WaitingAtIntermediate`, multi-leg `AtoBHaulTask`)
- `src/Workers/WorkerManager.cs` (`CalculateLegVariableFee`, `CalculateCancellationRefund`, `ShouldKeepEngineRunningOnArrival`, `HireDriverForItinerary`, `TryAdvanceToNextItineraryLeg`, refund-aware `CancelTask`)
- `src/Workers/StationWakeUpManager.cs` (Multi-leg station wake-up and arrival track reservation across `task.Legs`)
- `src/Driver/AIEngineer.cs` (`AssignNextItineraryLeg`, engine shutdown suppression during intermediate leg stops)

#### 1.4 Step-by-Step Implementation Sequence
1. **Step 1.1 [DONE]:** Defined `ItineraryLeg` data structures, `ItineraryLegStatus`, and `HaulTaskStatus.WaitingAtIntermediate`.
2. **Step 1.2 [DONE]:** Upgraded `AtoBHaulTask` to manage `List<ItineraryLeg> Legs`, `CurrentLegIndex`, and leg-specific distance/fee tracking.
3. **Step 1.3 [DONE]:** Extended `WorkerManager` with `CalculateLegVariableFee` and `HireDriverForItinerary`.
4. **Step 1.4 [DONE]:** Implemented `CalculateCancellationRefund` and `Inventory.Instance.AddMoney(refund)` in `CancelTask` prior to engineer teardown.
5. **Step 1.5 [DONE]:** Updated `HandleTaskTerminusArrival`, `WorkerManager.Update`, `AIEngineer.EnterTerminusStop`, and `StationWakeUpManager` for seamless multi-leg execution.

#### 1.5 Critical Invariants & Guardrails
- **`[WRK-04]`**: Completed intermediate legs keep the task in `ActiveTasks` until the final terminus is reached, preventing premature ghost lock purges or loss of despawn immunity.
- **`[WRK-07]`**: Cancellation refunds must always be calculated before destroying `task.Engineer`, and intermediate stops must preserve the prime mover while `task.HasRemainingLegs` is true.

---

### Package 2: Valley-Wide Comms Radio Backbone & Alert Dispatch ✅ `[COMPLETED — WRK-08]`

#### 2.1 Objective & Scope
- Transform the `AI WORKER` Comms Radio mode from a short-range point-and-click scanner into a valley-wide monitoring and dispatch interface.
- Implement remote channel navigation to cycle through all active worker trains across the island.
- Build the **Blocked-Track Escalation & Resolution Menu** within the radio interface to handle mid-route disruptions diegetically, backed by background async option pre-evaluation and stationary max-brake locking.
- Support infinite intermediate waypoint dwell (`DwellSeconds < 0f`) with engine shutdown awaiting Comms Radio `Resume Run`.

#### 2.2 Implemented Architecture & Data Flow (`[WRK-08]`)
- **Two-Tier Targeting & Remote Access:**
  - *Local Mode:* Cyan laser raycast selects locomotives or consist cars within 100m. Aiming at an active worker train opens its `WorkerRadioTrainDetailState` (or `WorkerRadioBlockedMenuState` if currently blocked).
  - *Remote Channel Mode:* Aiming at empty space (`_hoveredLoco == null`) with trigger or scrolling Up/Down switches to `WorkerRadioActiveListState` (or jumps straight to `WorkerRadioBlockedMenuState` if any active worker train has an unresolved blocked alert).
- **Radio State Hierarchy:**
  - `WorkerRadioActiveListState`: Carousel of all active trains in `WorkerManager.ActiveTasks` displaying ID, origin/destination yard, current speed, remaining distance, and operational state tag (`RUN`, `ALERT!`, `E-STOP`, `HOLD-LOOP`, `HOLD-WPT`, `DWELL`, `WAIT`), plus a `[+ Scan / New Haul]` exit entry.
  - `WorkerRadioTrainDetailState`: Real-time telemetry HUD displaying lead loco ID, car count, destination yard, current track, current speed, remaining distance, governing signal aspect label, and driver state.
  - `WorkerRadioInFlightCommandsState`: Remote directives menu (*Resume Run*, *Hold at Next Loop*, *Emergency Stop*, *Blocked Menu* [if blocked], *Abort & Hand Over*, *Back*).
  - `WorkerRadioBlockedMenuState`: Triggered on $\ge 30\text{s}$ obstruction holds. Offers dynamic wait timers (*Wait 1 Min (60s)*, *Wait 3 Min (180s)*, *Wait 5 Min (300s)*), verified bypass (*Reroute via Clear Bypass*), trailing siding backup (*Backup into Trailing Siding*), and manual takeover (*Hand Over Control*).
- **Driver & Dispatch Subsystems (`AIEngineer` & `WorkerManager`):**
  - **Pre-Switch Standoff Clamping (`AIEngineer.cs:1847`):** Clamps `DistanceToObstacle` 15m prior to facing diverging switches for worker trains as well, ensuring bypass detours remain topologically valid.
  - **Single-Track Auto-Reverse Suppression (`AIEngineer.cs:4796`):** Prevents worker trains from autonomously reversing without player radio authorization.
  - **Pulling Orientation Lock (`[WRK-02]`, `AIEngineer.cs:5206-5266`):** Preserves `_ambientLockedReverser` pulling orientation during reroute recalculations.
  - **Async Pre-Evaluation Before Toast (`[WRK-08]`):** Once halted $\ge 30\text{s}$ at an obstacle, red signal, corridor hold, switch hold, or throat hold, `AIEngineer` pre-evaluates mainline bypass, shunting detour, and trailing siding options asynchronously in the background (`TryGetAsyncReroute`). The toast/alert is broadcast only after pre-evaluation completes.
  - **E-Stop Max-Brake Lock (`[WRK-08]`):** `EmergencyStop()` enforces `TrainBrake = 1.0f` and `IndependentBrake = 0.0f` while $\ge 0.2\text{ km/h}$ (`[AIE-03]`), and once stationary continuously locks all three brake systems (`TrainBrake = 1.0f`, `IndependentBrake = 1.0f`, `Handbrake = 1.0f`) until `Resume()` or `CancelTask`.
  - **Infinite Intermediate Dwell (`WorkerManager.cs:628-640, 1451-1471`):** `DwellSeconds < 0f` shuts down the engine and sets `IntermediateDwellExpireTime = float.PositiveInfinity`. Radio `Resume Run` restarts the prime mover (`EnsureEngineRunning()`), releases handbrakes, and resumes transit.

#### 2.3 Target Subsystems & File Locks (Completed)
- `src/Workers/AtoBHaulTask.cs` (Blocked alert tracking and live telemetry helper properties)
- `src/Driver/AIEngineer.cs` (Pre-switch standoff, 30s async pre-evaluation, E-stop stationary max-brake lock, loop hold, trailing siding backup)
- `src/Workers/WorkerManager.cs` (Remote command methods, 30s blocked toast dispatch, infinite intermediate dwell engine shutdown)
- `src/Workers/Radio/WorkerRadioStates.cs` (`WorkerRadioScanState`, `WorkerRadioActiveListState`, `WorkerRadioTrainDetailState`, `WorkerRadioInFlightCommandsState`, `WorkerRadioBlockedMenuState`)

#### 2.4 Step-by-Step Implementation Sequence
1. **Step 2.1 [DONE]:** Implemented `WorkerRadioActiveListState` to query `WorkerManager.ActiveTasks` and render a navigable carousel of active trains on the radio LCD.
2. **Step 2.2 [DONE]:** Built `WorkerRadioTrainDetailState` displaying real-time telemetry extracted from `AIEngineer` and `RailGraph`.
3. **Step 2.3 [DONE]:** Implemented remote command dispatch methods in `WorkerManager` (*Hold at Next Loop*, *Resume*, *E-Stop*, *Wait*, *Reroute*, *Backup*) triggered directly from radio button actions.
4. **Step 2.4 [DONE]:** Built 30-second obstruction hold logic and background async option pre-evaluation in `AIEngineer`, firing alerts only once pre-calculation completes.
5. **Step 2.5 [DONE]:** Implemented `WorkerRadioBlockedMenuState` offering dynamic wait timers (*Wait 1/3/5 Min*), bypass reroute, siding backup, and handover.

#### 2.5 Critical Invariants & Guardrails
- **`[WRK-01]`**: Comms Radio mode registration must properly hook `ControllerAPI.Ready` and reset static tracking flags upon scene unloads and radio re-instantiations.
- **`[WRK-08]`**: Pre-switch stop clamping enabled for worker trains; 30s blocked hold pre-evaluates bypass and siding options asynchronously before raising alert toast; E-stop locks all 3 brake systems at 100% once stopped until resumed.
- **`[AIE-03]`**: Zero independent brake while moving ($\ge 0.2\text{ km/h}$); stationary max-brake clamp only engages once completely stopped.
- **`[AIE-15]`**: Dynamic obstacle reroutes triggered via the radio menu must strictly respect the pre-switch stopping distance and must never permit wild, unvalidated path recalculations across occupied tracks.

---

### Package 3: In-Cab Assistant & Drivetrain Simulation Guardrails ✅ `[COMPLETED — WRK-09]`

#### 3.1 Objective & Scope
- Provide a responsive co-pilot experience where the player can ride in the cab and take over physical controls at any moment without causing state desynchronization.
- Implement realistic physical consumption of fuel, water, coal, lubricating oil, and sand at 1.0x vanilla cruising rates.
- Implement artificial anti-hunting/stall capping during wheel-slip or stagnation recovery, while retaining full mechanical damage immunity under `AIDamageImmunityPatches`.

#### 3.2 Implemented Architecture & Data Flow (`[WRK-09]`)
- **In-Cab Touch-to-Yield & Complete Servo Write Suppression (`EngineState.PlayerCoPilotStandby`):**
  - Monitors player physical manipulation of cab controls (`Throttle`, `TrainBrake`, `IndependentBrake`, `DynamicBrake`, `Reverser`) on the lead locomotive (`PlayerManager.Car == _trainCar`).
  - Upon detecting input deviation exceeding deadzone threshold ($\Delta > 0.08$), `AIEngineer` immediately transitions its driving state to `PlayerCoPilotStandby`.
  - In `PlayerCoPilotStandby`, all primary and secondary servo loops are suppressed: `ExecuteControlOutputs` yields control to player lever positions; `UpdateSanderActuation` does not reset manual sander toggles; `DM3TransmissionController.Update` is bypassed to avoid gear resets; `UpdatePowertrainProtection` breaker resets and throttle cutouts are skipped; `UpdateLevelCrossingHorn` horn patterns are silenced; and `UpdateWorkerLoopHoldAndBlockedEscalation` 30s obstruction alerts are suppressed.
  - Route navigation, switch alignment reservations, signal tracking, and **manual destination arrival detection** (`HasArrivedAtDestination`) remain active, allowing the player to manually spot the train on the destination siding to complete the haul leg/task.
  - Player is notified via top-center toast banner: *"You have taken over manual control of [LocoID]. Press [Ctrl+A] or use Comms Radio [AI WORKER] to re-engage AI driver."*
  - Re-engagement via configurable hotkey (`[Economy] CoPilotResumeHotkey = Ctrl+A` in `settings.ini`) or Comms Radio `RESUME AI DRIVING` invokes `ResumeAIDriving()`, resetting PIDs and smoothly ramping physical controls back to AI PID targets over a 2.0-second Hermite blend (`Mathf.SmoothStep`) while strictly enforcing `[AIE-03]` (`IndependentBrake = 0.0f` at $\ge 0.2\text{ km/h}$).
- **Physical Consumables Simulation & Stall Capping (`UpdateResourceConsumptionStallCapping`):**
  - Ambient trains remain 100% debt-exempt via `AIDebtPatches`.
  - For player-employed worker trains, `AIDebtPatches` retains native `LocoDebtController` and `SimulatedCarDebtTracker` registration (`ModCompatManager.IsAmbientAITrain` is `false`), draining physical `FUEL`, `SAND`, and `OIL` reservoir ports during steady-state cruising.
  - `UpdateResourceConsumptionStallCapping` audits per-frame delta depletion across consist locomotives:
    - **Stationary AI Holds (0% Net Drain):** Port drain is frozen during stationary holds (`Idle`, `StationHold`, `TerminusStop`, signal/corridor/throat/loop holds, E-stop, or `_hillStagnationTimer > 5.0f`).
    - **Slip & Hill-Start Recovery (Baseline Cruise Ceiling):** Port drain is capped to a nominal ceiling (`20%` raw drain for `FUEL`/`OIL`, `25%` raw drain for `SAND`) during active wheel slip (`_isWheelSlipping`), WSP slide (`_isWheelSliding`), or hill-launch recovery (`IsHillStarting || HillStage != HillLaunchStage.None || _hillStagnationTimer > 0f`).
  - `AIDamageImmunityPatches` remains strictly engaged throughout `PlayerCoPilotStandby` and only unregisters upon final arrival handover or task cancellation.

#### 3.3 Target Subsystems & File Locks (Completed)
- `src/Config/IniConfigManager.cs` (`CoPilotResumeHotkey`, `IsCoPilotResumeHotkeyDown`)
- `src/Driver/AIEngineer.cs` (`PlayerCoPilotStandby`, `UpdatePlayerCoPilotHandover`, `ResumeAIDriving`, `UpdateResourceConsumptionStallCapping`, servo suppression gates)
- `src/Workers/WorkerManager.cs` (`ResumeWorkerTask` with `ResumeAIDriving` routing)
- `src/Workers/Radio/WorkerRadioStates.cs` (`CO-PILOT` state tags and `RESUME AI DRIVING` command action)

#### 3.4 Step-by-Step Implementation Sequence
1. **Step 3.1 [DONE]:** Implemented `IsCoPilotResumeHotkeyDown()` and `CoPilotResumeHotkey` in `IniConfigManager` (default `Ctrl+A`).
2. **Step 3.2 [DONE]:** Implemented Touch-to-Yield input deviation listeners on `_controlsOverrider` in `AIEngineer`, transitioning to `PlayerCoPilotStandby`.
3. **Step 3.3 [DONE]:** Gated secondary servo loops (`UpdateSanderActuation`, `DM3TransmissionController.Update`, `UpdatePowertrainProtection`, `UpdateLevelCrossingHorn`, 30s blocked-track timers) to prevent fighting player inputs.
4. **Step 3.4 [DONE]:** Implemented 2.0-second Hermite smooth control resumption (`ResumeAIDriving`) triggered via hotkey or Comms Radio.
5. **Step 3.5 [DONE]:** Preserved manual destination arrival detection in `UpdateStateMachine` so manual spotting completes the task.
6. **Step 3.6 [DONE]:** Implemented `UpdateResourceConsumptionStallCapping` on `FUEL`, `SAND`, and `OIL` reservoir ports, capping drain during slip and freezing during holds.

#### 3.5 Critical Invariants & Guardrails
- **`[AIE-03]` (Absolute Motion Invariant):** The independent brake must NEVER be applied while moving ($\ge 0.2\text{ km/h}$) or during copilot handover transitions.
- **`[WRK-09]`**: Touch-to-yield strictly suppresses all secondary servo writes in `PlayerCoPilotStandby`; destination arrival remains active; resumption smoothly blends over 2.0s; `UpdateResourceConsumptionStallCapping` freezes drain in stationary holds and caps to 20%/25% during slip/hill recovery.
- **`[COMPAT]`**: `AIDamageImmunityPatches` must remain 100% engaged while in AI driving and co-pilot modes, and only cleanly unregister upon final arrival or complete task cancellation.

---

### Package 4: Mainline Flow Control, Station Staging & Sequential Dwells

#### 4.1 Objective & Scope
- Implement sequential leg progression, allowing a train to stop at scheduled intermediate stations, dwell for a prescribed duration, and automatically depart on the next leg.
- Build the **Destination & Approach Congestion Staging** subsystem, preventing terminal yard gridlocks by holding outside full stations.

#### 4.2 Architectural Design & Lookahead Strategy
- **Approach Congestion Lookahead:**
  - When an AI worker is within $1500\text{m} - 2000\text{m}$ of the final destination station, `AIEngineer` triggers an arrival feasibility audit.
  - Queries `WorkerManager.SelectBestArrivalTrack` to identify candidate receiving sidings.
  - If all candidate tracks are occupied or reserved, the train computes a holding stop before the station entry signal or at the outermost passing loop.
  - The train secures its position, sets holding lights, and issues an audible Comms Radio hail to the player: *"[Train ID] Holding at [Station] Approach — Receiving tracks occupied."*
  - The train periodically polls yard track availability (every 15 seconds) or waits for a player radio directive.
- **Sequential Leg Progression & Dwells:**
  - Upon reaching an intermediate waypoint siding, the train brings itself to a full stop.
  - Once verified stopped (`CurrentSpeedKmh < 0.2f`), the train enters `WaypointDwell`.
  - A dwell timer runs down while holding train brakes.
  - When the timer expires, the worker triggers `FindWorkerRouteWithSpurFallback` for the next itinerary leg, verifies line clearance ahead, clears the dwell state, and accelerates toward the next waypoint.

#### 4.3 Target Subsystems & File Locks
- `src/Driver/AIEngineer.cs`
- `src/Workers/WorkerManager.cs`
- `src/Workers/MultiStopHaulTask.cs` (or equivalent)

#### 4.4 Step-by-Step Implementation Sequence
1. **Step 4.1:** Implement destination approach scanning in `AIEngineer` triggered at 2000m from destination, querying arrival track viability.
2. **Step 4.2:** Build the `ApproachCongestionHolding` state in `AIEngineer`, commanding a service stop before entry signals or at passing loops when no siding is clear.
3. **Step 4.3:** Wire congestion alert notifications into `WorkerManager` to broadcast HUD toasts and trigger Comms Radio audible alerts.
4. **Step 4.4:** Implement intermediate waypoint arrival verification in `AIEngineer`, transitioning the active task into `WaypointDwell`.
5. **Step 4.5:** Implement dwell countdown timers and automatic leg advance logic, initiating pathfinding and departure for the next leg upon expiration.

#### 4.5 Critical Invariants & Guardrails
- **`[WRK-05]`**: Arrival track evaluation must strictly follow designation-tiered, proximity-sorted order (`transferIn` first, then `storage`), respecting consist length $+ 25\text{m}$ clearance and zero occupancy.
- **`[AIE-08]` / `[AIE-11]`**: Approach holding logic must never truncate corridor lookaheads or spam switch alignment requests across station entry signals.

---

### Package 5: Turnaround Maneuvers & Shoving Switchbacks

#### 5.1 Objective & Scope
- Provide complete autonomous directional turnaround capabilities so worker trains never become stranded at dead-end spurs, buffer stops, or terminal stations.
- Implement the primary choice: **Locomotive Run-Around via Passing Loop**.
- Implement the fallback choice: **Slow Shunting Reverse Shove ($\le 15\text{ km/h}$)**.

#### 5.2 Architectural Design & State Machines
- **Turnaround Decision Hierarchy:**
  - *Condition 1:* Locomotive is facing a buffer stop or the exit path is in the opposite direction of current consist orientation.
  - *Evaluation:* Audit adjacent tracks for a clear parallel loop.
  - *Decision A (Loop Available):* Initiate **Run-Around Protocol**.
  - *Decision B (No Loop / Trapped at Buffer):* Initiate **Slow Shunting Reverse Shove**.
- **Run-Around Protocol State Machine:**
  1. `SecureConsist`: Apply mechanical handbrakes on all trailing cars; verify train is completely stationary.
  2. `UncoupleLocomotive`: Close lead angle cock, uncouple rear of locomotive (or locomotive set).
  3. `AdvancePastThroat`: Pull light engine forward past the throat turnout until rear bogie clears the switch by $\ge 20\text{m}$.
  4. `TraverseLoop`: Throw switch to adjacent clear passing loop; run light engine through the loop past the rear of the consist.
  5. `SwitchBack`: Throw trailing switch behind consist; move light engine toward the rear coupler.
  6. `CouplingApproach`: Approach at $\le 5\text{ km/h}$; engage knuckle coupler.
  7. `ConsistRecommission`: Connect air hoses, open angle cocks, recharge brake pipe to 5.0 bar, release consist handbrakes.
  8. `ReadyForDeparture`: Update train orientation and resume forward route execution.
- **Slow Shunting Reverse Shove State Machine:**
  - Keep consist coupled.
  - Set reverser to reverse (`-1.0f`).
  - Cap maximum speed strictly to $15\text{ km/h}$.
  - Shove consist in reverse until reaching a trailing turnout or siding where a forward pulling route opens up or a run-around loop becomes accessible.
  - Once pulling forward again across a clear governing main signal, restore standard line speed.

#### 5.3 Target Subsystems & File Locks
- `src/Driver/AIEngineer.cs`
- `src/Navigation/JunctionController.cs`
- `src/Navigation/Pathfinder.cs`
- `src/Workers/WorkerManager.cs`

#### 5.4 Step-by-Step Implementation Sequence
1. **Step 5.1:** Implement loop topology detection in `RailGraph`/`Pathfinder` to identify accessible parallel loops from a given siding.
2. **Step 5.2:** Build the physical uncoupling and air cock isolation sequences in `AIEngineer`, safely detaching the locomotive while securing consist handbrakes.
3. **Step 5.3:** Implement light-engine route planning and throat switch negotiation to navigate around the trainset.
4. **Step 5.4:** Implement precision reverse coupling logic with speed clamping ($\le 5\text{ km/h}$) and knuckle engagement detection.
5. **Step 5.5:** Build the air brake pipe recharge and handbrake release verification routine before departure.
6. **Step 5.6:** Implement the fallback `SlowShuntingReverseShove` state machine, enforcing $\le 15\text{ km/h}$ and switch alignment safety until forward movement is achieved.

#### 5.5 Critical Invariants & Guardrails
- **`[AIE-04]`**: When uncoupling and pulling past throat turnouts, switch clearance must be verified against the physical rear of the moving unit before throwing switches.
- **`[JNC-02]`**: The 7.0m movable switch blade safety envelope must be strictly enforced during all run-around switch operations.
- **Zero-Tolerance Track Occupancy:** The parallel loop traversed by the light engine must be 100% physically and virtually unoccupied.

---

### Package 6: Intermediate Consist Set-Outs

#### 6.1 Objective & Scope
- Allow worker trains to drop off designated trailing cuts of cars at intermediate station sidings before proceeding with the remainder of the train.

#### 6.2 Operational Flow & Pneumatic Sequence
- **Step-by-Step Set-Out Execution:**
  1. *Arrival on Siding:* Pull consist completely onto designated drop track; bring train to a complete stop (`StationaryTimer >= 2.0s`).
  2. *Secure Dropped Cut:* Iterate through the trailing cars designated for set-out and apply mechanical handbrakes (`SetHandbrakePosition(1.0f)`).
  3. *Pneumatic Isolation:* Close the pneumatic angle cocks between the last car of the retained forward consist and the first car of the set-out cut.
  4. *Knuckle Decoupling:* Actuate the coupler release lever between the two cuts.
  5. *Forward Consist Verification:* Verify brake pipe continuity and pressure on the retained forward consist; release train air brakes.
  6. *Component Teardown on Dropped Cut:* Invoke `ConsistEOTMarker.RemoveFromCar` and ensure no orphaned AI components or ghost locks remain on the set-out cars.
  7. *Consist Metric Recalculation:* Update `MultiStopHaulTask` consist lists, car count, total length, and tonnage.
  8. *Departure:* Clear the intermediate siding and resume transit on the next itinerary leg.

#### 6.3 Target Subsystems & File Locks
- `src/Driver/AIEngineer.cs`
- `src/Workers/WorkerManager.cs`
- `src/Workers/MultiStopHaulTask.cs`
- `src/Fleet/ConsistEOTMarker.cs`

#### 6.4 Step-by-Step Implementation Sequence
1. **Step 6.1:** Extend `HaulLeg` definitions to include `SetOutCutCount` specifying how many trailing cars are to be dropped at the leg's destination track.
2. **Step 6.2:** Implement the precision siding stopping check, ensuring the set-out cut will fully clear the siding clearance point once detached.
3. **Step 6.3:** Build the pneumatic angle cock closure and coupler release sequence in `AIEngineer`.
4. **Step 6.4:** Implement handbrake application across all cars in the set-out cut to prevent runaways.
5. **Step 6.5:** Implement component teardown on the dropped cut, removing EOT markers, updating trainset structures, and purging ghost locks.
6. **Step 6.6:** Update retained consist metrics and transition the train into departure for the subsequent leg.

#### 6.5 Critical Invariants & Guardrails
- **`[WRK-03]` & `[WRK-04]`**: Dropped cars must be completely freed from AI tracking so the player can immediately shunt or inspect them without encountering ghost switch locks.
- **Runaway Prevention**: Handbrakes on set-out cars must be 100% applied BEFORE the coupler is released.

---

### Package 7: Diegetic Station Paperwork & Desk Spawning

#### 7.1 Objective & Scope
- Provide a physical, immersive workflow for configuring and authorizing multi-stop itineraries using station dispatch desks.
- Allow players to spawn physical itinerary folders, carry them to the cab, or validate them via Comms Radio.

#### 7.2 Architectural Design & Interaction Flow
- **Desk Interaction:**
  - Utilize existing station office desks (adjacent to job validation machines).
  - Interact with desk to open an in-game GUI or physical sheet configuration interface.
  - Player selects assigned locomotive, destination waypoints, set-out cuts, and reviews total hiring fees.
- **Physical Booklet Spawning:**
  - Upon clicking "Print Itinerary Sheet", instantiate a physical booklet item in the station tray.
  - The booklet displays the route summary, train manifest, and total price.
- **Authorization Methods:**
  - *Tactile Placement:* Carrying the booklet into the locomotive cab and placing it onto the clipboard or dash triggers itinerary validation and employee assignment.
  - *Comms Radio Trigger:* Pointing the Comms Radio at the locomotive while holding or looking at the validated paperwork activates the itinerary directly.

#### 7.3 Target Subsystems & File Locks
- `src/Workers/WorkerManager.cs`
- `src/Workers/StationWakeUpManager.cs`
- New: `src/Workers/Paperwork/WorkerItinerarySheet.cs`

#### 7.4 Step-by-Step Implementation Sequence
1. **Step 7.1:** Create `WorkerItinerarySheet` component handling item instantiation, booklet mesh/texture binding, and metadata storage.
2. **Step 7.2:** Implement station desk interaction hooks to open the multi-stop itinerary configuration screen.
3. **Step 7.3:** Implement booklet item spawning in the station office output tray upon fee approval.
4. **Step 7.4:** Implement cab placement detection (proximity to cab clipboard collider) to trigger task dispatch.
5. **Step 7.5:** Add Comms Radio activation alternative, allowing players to point-and-click dispatch once paperwork is printed.

#### 7.5 Critical Invariants & Guardrails
- **Item Stability**: Booklet items must utilize standard Derail Valley inventory/pickup physics without falling through floors or causing physics glitches.
- **Non-Interference**: Spawning paperwork must never block or corrupt vanilla job booklet racks or validation kiosks.

---

## 4. Phase-by-Phase Rollout Roadmap & Work Breakdown Structure (WBS)

To ensure manageable, verifiable progression, the implementation is scheduled across four development phases:

```
+-----------------------------------------------------------------------------+
| Phase 2A: Foundational Architecture (COMPLETED)                             |
| - [DONE] Package 1: Itinerary Model, Multi-Leg Wages & Refunds ([WRK-07])   |
| - [DONE] Package 2: Valley-Wide Comms Radio Backbone & Dispatch ([WRK-08])  |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
| Phase 2B: Cab Dynamics & Mainline Operations                                |
| - [DONE] Package 3: In-Cab Assistant (Touch-to-Yield) & Stall-Capped Consumables ([WRK-09])
| - [NEXT] Package 4: Mainline Flow Control, Waypoint Dwells & Yard Congestion Holds |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
| Phase 2C: Advanced Maneuvering & Consist Reconfiguration                    |
| - Package 5: Autonomous Run-Around Protocol & Slow Shunting Reverse Shove   |
| - Package 6: Intermediate Tail-Cut Car Drop-Offs (Set-Outs)                 |
+-----------------------------------------------------------------------------+
                                       |
                                       v
+-----------------------------------------------------------------------------+
| Phase 2D: World Immersion & Diegetic Polish                                 |
| - Package 7: Station Dispatch Desk Paperwork & Cab Clipboard Interaction    |
| - System-wide end-to-end integration testing and invariant audit            |
+-----------------------------------------------------------------------------+
```

### Detailed Milestone Breakdown

| Milestone | Package Scope | Key Deliverables | Verification Exit Criteria |
| :--- | :--- | :--- | :--- |
| **M2.1** ✅ | Package 1 (Complete) | Unified multi-leg `AtoBHaulTask`, `ItineraryLeg`, `CalculateLegVariableFee`, `CalculateCancellationRefund` (`[WRK-07]`). | Completed & verified via `verify_invariants.py`; single-leg & multi-leg hauls and pro-rated refunds active. |
| **M2.2** ✅ | Package 2 (Complete) | `WorkerRadioActiveListState`, `WorkerRadioTrainDetailState`, `WorkerRadioInFlightCommandsState`, `WorkerRadioBlockedMenuState`, async pre-evaluation, max-brake E-stop (`[WRK-08]`). | Completed & verified via `verify_invariants.py`; player monitors & commands active trains valley-wide; blocked holds pre-evaluate options before alerting. |
| **M2.3** ✅ | Package 3 (Complete) | Touch-to-yield cab overrides (`PlayerCoPilotStandby`), full secondary servo suppression, configurable hotkey (`Ctrl+A`), Comms Radio `RESUME AI DRIVING`, 2.0s Hermite blend, destination arrival preservation, and stall-capped fuel/sand/oil drain (`[WRK-09]`). | Completed & verified via `verify_invariants.py`; player grabs cab levers, AI yields instantly; hotkey or radio resumes AI smoothly; fuel/sand drains at cruise with stall capping and 100% damage immunity. |
| **M2.4** | Package 4 | Sequential waypoint dwells, automatic leg departure, destination approach congestion staging. | Train executes 3-station run with intermediate dwell; holds at approach loop when yard is full. |
| **M2.5** | Package 5 | Run-around state machine (uncouple, loop, couple, recharge) & reverse shove fallback. | Locomotive performs complete loop run-around at terminal; reverse shoves safely from dead-end spurs. |
| **M2.6** | Package 6 | Pneumatic isolation, handbrake securing, coupler uncoupling, set-out consist teardown. | Consist drops rear 4 cars on intermediate siding; dropped cars secured; forward train departs. |
| **M2.7** | Package 7 | Physical booklet item, station desk generator, cab clipboard trigger. | Player prints sheet at desk, places in cab clipboard, worker begins multi-stop run. |

---

## 5. Risk Management & Invariant Compliance Matrix

Every proposed change in this plan has been audited against [`dv_ai_traffic/GRAVEYARD.md`](file:///z:/Gemini/DerailValley/dv_ai_traffic/GRAVEYARD.md) to prevent known failure modes:

| Invariant Code | Graveyard Hazard Description | Plan Safeguard & Mitigation |
| :--- | :--- | :--- |
| **`[AIE-03]`** | Clamping independent brake while moving ($\ge 0.2\text{ km/h}$) causes wheel lockup and severe oscillations. | Handover, copilot transitions, and run-around uncoupling strictly enforce `CurrentSpeedKmh < 0.2f` before touching independent brakes. |
| **`[AIE-04]`** | Releasing turnout locks before consist tail clears switch points causes trailing car split-switch derailments. | Package 5 and Package 6 verify clearance using `GetTrailingCarPosition()` with $\ge 20\text{m}$ safety margins before throwing switches. |
| **`[AIE-08]`** | Evaluating station entry signals or truncating lookahead during corridor holds causes reservation-purge storms. | Approach congestion staging (Package 4) holds before entry signals without zeroing lookahead horizons. |
| **`[AIE-15]`** | Preceding same-direction trains triggering immediate detours or worker trains rerouting off course. | In-flight rerouting remains strictly disabled for worker trains unless explicitly ordered by player via Blocked Menu. |
| **`[WRK-01]`** | Comms Radio mode vanishing upon scene reload or radio controller re-instantiation. | Radio state extensions in Package 2 strictly preserve `ControllerAPI.Ready` hooks and reset static flags on unloads. |
| **`[WRK-02]`** | Calling direction-agnostic pathfinding causes worker trains to push consists backwards across the valley. | All leg transitions in Package 1, 4, 5, and 6 strictly use pulling-forward orientation and spur fallback routines. |
| **`[WRK-03]`** | Orphaned lighting or EOT markers continuing to control car lights after worker departure. | Car drop-offs (Package 6) and task teardowns strictly execute full component cleanup across all detached rolling stock. |
| **`[WRK-04]`** | Completed worker hauls holding ghost locks and blocking player switch-throwing actions. | Completed legs and set-out cars immediately purge switch locks and unregister from active worker tracking. |
| **`[WRK-05]`** | Arrival tracks selected in arbitrary scene order skipping valid storage tracks. | Congestion staging (Package 4) audits candidate tracks in strict tiered, proximity-sorted order (`transferIn` then `storage`). |
| **`[WRK-08]`** | Rolling past diverging switches prevents bypass routing; firing blocked toasts before async evaluation leaves player without options; E-stop releasing handbrakes allows rollaways. | Pre-switch stop clamping enabled for worker trains; 30s blocked hold pre-evaluates bypass and siding options before raising alert toast; E-stop locks all 3 brake systems at 100% once stopped until resumed. |
| **`[WRK-09]`** | Bypassing only state machine in copilot mode leaves secondary servos active (sander snap-back, DM3 shifting to neutral, breaker resets, horn sounding, false 30s blocked alerts); zeroing drain on stall without clamping leads to rapid fuel burn or unrealistic free fuel. | Touch-to-yield strictly suppresses all secondary servo writes in `PlayerCoPilotStandby`; destination arrival remains active; resumption smoothly blends over 2.0s; `UpdateResourceConsumptionStallCapping` freezes drain in stationary holds and caps to 20%/25% during slip/hill recovery. |

---

## 6. Verification Protocol & Acceptance Test Scenarios

The technical plan includes dedicated verification scenarios to be executed in-game upon completion of the respective milestones:

### Test Scenario A: Multi-Stop Itinerary with Intermediate Dwell (Packages 1, 2, 4)
- **Setup:** DE6 locomotive with 10 loaded hopper cars at Harbor (`HB`).
- **Itinerary:** Leg 1: Harbor to Steel Mill (`SM`) with 60s dwell $\to$ Leg 2: Steel Mill to Machine Town (`MF`).
- **Execution:**
  1. Dispatch train; verify upfront wallet deduction for both legs.
  2. Travel to Steel Mill; verify train halts on designated track and dwells for 60 seconds.
  3. Verify automatic departure on Leg 2 toward Machine Town.
  4. Cancel task mid-transit between SM and MF via Comms Radio; verify Leg 2 is refunded pro-rata while Leg 1 is retained.

### Test Scenario B: In-Cab Copilot Override & Resumption (Package 3)
- **Setup:** Active worker train cruising along mainline at 60 km/h.
- **Execution:**
  1. Player enters cab, adjusts throttle lever; verify AI immediately releases controls and status displays *"Player in Control — AI Copilot Standing By"*.
  2. Player accelerates to 70 km/h, negotiates curves and signals manually.
  3. Player selects *"Resume AI Driving"* on Comms Radio; verify AI smoothly resumes throttle/brake modulation without abrupt jerking or brake dumps.
  4. Inspect locomotive fuel/oil/sand levels; verify realistic consumption without spike depletion during starts.

### Test Scenario C: Destination Congestion Staging (Package 4)
- **Setup:** Completely block all receiving tracks at Farm (`FM`) with parked rolling stock.
- **Itinerary:** Worker dispatched from Food Factory (`FF`) to Farm (`FM`).
- **Execution:**
  1. As train approaches within 1800m of Farm, verify it detects no clear receiving tracks.
  2. Verify train brings itself to a smooth halt before the station entry signal or at outer passing loop.
  3. Verify audible radio alert sounds and radio displays congestion hold status.
  4. Player clears a siding at Farm; verify train detects clear track and automatically proceeds into the yard.

### Test Scenario D: Autonomous Run-Around Maneuver (Package 5)
- **Setup:** Locomotive facing a dead-end bumper track at Harbor with wagons coupled behind it, adjacent to a clear passing loop.
- **Execution:**
  1. Trigger turnaround protocol.
  2. Verify consist handbrakes are secured before uncoupling.
  3. Verify light engine moves past switch, traverses loop, switches back, and couples to rear at $\le 5\text{ km/h}$.
  4. Verify air hoses are coupled, train brake pipe charges to 5.0 bar, handbrakes release, and train departs forward.

### Test Scenario E: Intermediate Siding Set-Out (Package 6)
- **Setup:** Locomotive with 8 cars at Station A, tasked to drop rear 4 cars at Station B siding and continue to Station C.
- **Execution:**
  1. Train pulls onto Station B siding, stops completely.
  2. Handbrakes apply to rear 4 cars; angle cocks close; coupler uncouples.
  3. Lead locomotive and forward 4 cars recharge brake pipe, pull away, and resume route to Station C.
  4. Player inspects dropped 4 cars; verify handbrakes held, EOT markers updated, and zero ghost locks on switches.

---
*End of Technical Implementation Plan — Phase 2.*
