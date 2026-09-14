# AI Traffic v0.2.4 – Signal Wait Retry, Prototype Consists, Spawner Safety & City South Corridors

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL ALPHA**
> This update introduces periodic red signal retry logic, prototype unit trains and block consist grouping, spawner head-on safety interlocks, and expanded valley traffic corridors. 
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our [GitHub Issues](https://github.com/Killermops27/dv-ai-traffic/issues) page!

---

### 🚦 1. Periodic Signal Wait Retry & DVSignals Wakeup
* **Automatic Red Signal Recovery:** When stopped at an `Hp 0` (Red) signal, AI trains now run an automatic 20-second retry timer (`_signalWaitRetryTimer`).
* **Route Re-Alignment:** Periodically re-requests switch alignment along the upcoming route (scanning up to 12 tracks ahead) for any junctions that were previously denied or held.
* **DVSignals Forced Evaluation:** Directly awakens and refreshes the approaching DVSignals controller (`WakeAndForceUpdateSignal`) and re-attempts signal reservation (`TryReserveDVSignal`), preventing trains from being stranded indefinitely when downstream blocks clear.

---

### 🚂 2. Prototype Consist Grouping & Unit Trains
* **Homogeneous Unit Trains:** Implemented a 60% probability for heavy bulk commodities (Coal, Iron Ore, Crude Oil) and a 35% probability for general freight to spawn as dedicated single-commodity unit trains (e.g. all hoppers, all tankers, or all container flats).
* **Block Wagon Cuts:** Mixed freight trains now group rolling stock into realistic cuts/blocks of 2–4 matching cars for shunters and 3–6 cars for regional/mainline freights, eliminating random single-car "clown train" rakes.

---

### 🛡️ 3. Spawner Head-On Safety Interlock
* **Route Departure Distance Check:** Added `IsDepartureHeadingTowardsPlayer` in `TrafficScheduler`. Candidate spawn tracks and their initial 6 departure waypoints are checked against player position.
* **Yard Ladder Conflict Prevention:** Rejects spawns if the candidate track is within 800m of the player or if the train would depart straight toward a player who is within 1400m (passing within 350m of player position). Eliminates head-on collisions on long station ladders (such as Harbor D/G tracks).

---

### 🚉 4. City South & Forest South Valley Corridors
* **New Scheduled Corridors:** Added scheduled timetables connecting **City South (`CS`)** and **Forest South (`FS`/`FRS`)** with Harbor (`HB`), Steel Mill (`SM`), Sawmill (`SW`), and Machine Factory (`MF`).
* **Station Alias Disambiguation:** Precision station parsing separates City South (`CS`) from City South West / City West (`CW`/`CSW`), and Forest South (`FS`) from Forest Meadow (`FM`/`FR`).

---

### ⏱️ 5. Terminus Arrival Tracking & Despawn Safety
* **Arrival Timestamp Tracking:** AI Engineer records the exact arrival timestamp (`TerminusArrivalTime`) upon halting at its final terminus destination.
* **Safe Clearance Despawning:** Ensures completed ambient trains remain parked safely in yards until the player has physically moved beyond clearance distance before cleaning up rolling stock.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio AI Worker dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **AITraffic-v0.2.4.zip**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Verify that DVSignals and CommsRadioAPI are active.
