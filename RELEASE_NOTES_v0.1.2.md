# AI Traffic v0.1.2 – Architecture Refactor & Ambient Traffic Stabilization

This maintenance release cleans up internal architecture and stabilizes the background traffic scheduling engine in preparation for the upcoming **AI Worker System (v0.2.0)**.

### Highlights & Changes

* **Scheduler Decoupling:** Purged legacy experimental `JobOperator` routines from the codebase. Background traffic scheduling now focuses exclusively on the Tier 1 Ambient Traffic engine, eliminating dead code paths and potential dispatch race conditions.
* **Streamlined In-Game Settings:** Removed obsolete dispatch modes and buttons from the Unity Mod Manager settings and debug overlay, presenting a clean interface focused on ambient density and behavior toggles.
* **Foundation for v0.2.0:** Sets the stage for the new player-employed **AI Worker System** (commissioning AI drivers for mainline station-to-station hauls, yard shunting, and consist loading).

### Requirements

* **Derail Valley** (PC / Steam)
* **Unity Mod Manager (UMM)** v0.27.0+
* **DVSignals** installed in `Derail Valley/Mods/`

### Installation

1. Download **`AITraffic-v0.1.2.zip`** from the release assets below.
2. Drag and drop the `.zip` archive into Unity Mod Manager's **Mods** tab, or extract directly into your `Derail Valley/Mods/` directory.
3. Launch Derail Valley and verify `AI Traffic` loads with a green status indicator in the UMM menu (<kbd>Ctrl</kbd> + <kbd>F10</kbd>).
