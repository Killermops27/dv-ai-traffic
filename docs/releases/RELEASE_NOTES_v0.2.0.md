# AI Traffic v0.2.0 – The AI Worker System & Comms Radio Dispatch

> ⚠️ **CRITICAL DISCLAIMER: EXPERIMENTAL EARLY ALPHA — BUGS ARE EXPECTED!**
> This build introduces massive new gameplay systems and routing mechanics. 
> **THERE ARE STILL NUMEROUS BUGS, EDGE CASES, ROUTING DEADLOCKS, AND UNEXPECTED BEHAVIORS.**
> Trains may occasionally get stuck at complex junctions, miscalculate braking distances with extreme train weights, or encounter signal aspect conflicts.
> **ALWAYS BACK UP YOUR SAVE FILES BEFORE USING THIS MOD.**
> Please report any reproducible bugs or edge-case behavior on our GitHub Issues page!

---

### 👷 Phase 2: Player-Employed AI Worker System
You can now hire AI engineers to drive your assembled trains across the valley from point A to point B:
* **Train & Consist Inspection:** Point and inspect any locomotive or consist. The worker system automatically measures total cars, physical train length (meters), and mass (metric tons).
* **Dynamic Siding Auto-Selection:** Evaluates candidate receiving (`transferIn`) and yard storage tracks at the destination station, verifying length ($\ge \text{consist} + 25\text{m}$), clearance (`!IsTrackOccupied`), and path navigability before selecting.
* **Dynamic Economy & Wages:** Computes realistic driver wages based on route distance and consist weight ($\text{Base } \$300 + \$0.06/\text{meter} + \$0.35/\text{ton}$), checks player wallet (`Inventory.Instance.PlayerMoney`), and deducts payment upon dispatch.
* **Consist Automation & Despawn Immunity:** Automatically locks couplers, connects air hoses, releases handbrakes across the consist, and starts up locomotive prime movers. Flagged with `engineer.IsWorkerDriven = true` to strictly prevent distance-based despawning.
* **Arrival Handover & Handbrake Securing:** Upon arrival on the destination track, the AI halts, sets the parking handbrake on the locomotive, destroys the `AIEngineer` component to return 100% manual control to the player, and broadcasts an on-screen toast banner.
* **UI Integration:** Accessible via both Unity Mod Manager options tab (`Ctrl+F10`) and in-game Debug HUD overlay (`[ 👷 Workers ]` toggle in `TrafficManager.OnGUI`).

---

### 📻 Comms Radio Integration (`CommsRadioAPI`)
* **Diegetic Dispatch Mode:** Cycle your Comms Radio to the **AI Worker** mode (cyan laser beam).
* **Point & Target:** Aim at any locomotive to view its consist summary (ID, cars, length, mass) or dismiss an active driver.
* **Select Destination & Track:** Scroll wheel to cycle through all valley stations and candidate tracks (including `[Auto Clear Siding]`).
* **Trigger to Dispatch:** Squeeze the trigger to confirm feasibility, deduct the driver fee from your wallet with an audio cash register chime, and dispatch the train onto the main line!

---

### 🚉 Station Dynamic Wake-Up & Persistence
* **Approaching AI Activation:** Approaching worker trains ($\approx 1200\text{m}$) dynamically trigger station yard population via Harmony postfixes on `StationJobGenerationRange.IsPlayerInJobGenerationZone` and `IsPlayerOutOfJobDestroyZone`.
* **Retains Yards Exactly As Left:** Retains yards without despawning cars/jobs; fully compatible with `PersistentJobsMod` and `SelfShunt`.

---

### 📦 Requirements
1. **Derail Valley** (Latest PC / Steam release)
2. **Unity Mod Manager (UMM)** v0.27.0+
3. **DV Signals** (Required for block occupancy and physical signal logic)
4. **CommsRadioAPI** (Required for Comms Radio dispatch mode)
5. **Double Track** (*STRONGLY RECOMMENDED* to prevent single-line gridlock)

---

### 🛠️ Installation
1. Download **`AITraffic-v0.2.0.zip`**.
2. Drag and drop into Unity Mod Manager, or extract into `Derail Valley/Mods/`.
3. Ensure both `DVSignals` and `CommsRadioAPI` are installed and green in UMM.
