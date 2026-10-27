#!/usr/bin/env python3
"""
Derail Valley AI Traffic - Pre-Build Static Invariant Gatekeeper
===============================================================
Acts as a mandatory pre-build static gatekeeper to enforce architectural
invariants and detect forbidden anti-patterns defined in GRAVEYARD.md and
VISION_AND_ARCHITECTURE.md.

Exit code 0: All critical invariants verified, zero anti-patterns detected.
Exit code 1: One or more critical guards missing or anti-patterns detected.
"""

import os
import sys
import re
import argparse
from pathlib import Path
from typing import List, Dict, Tuple, Optional

# Reconfigure stdout/stderr for clean UTF-8 output on Windows consoles
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
if hasattr(sys.stderr, "reconfigure"):
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")

# Color formatting helpers for Windows/POSIX terminals
USE_COLOR = sys.stdout.isatty() or os.environ.get("TERM") is not None
def _color(text: str, code: str) -> str:
    return f"\033[{code}m{text}\033[0m" if USE_COLOR else text

def red(text: str) -> str: return _color(text, "91;1")
def green(text: str) -> str: return _color(text, "92;1")
def yellow(text: str) -> str: return _color(text, "93;1")
def cyan(text: str) -> str: return _color(text, "96")
def bold(text: str) -> str: return _color(text, "1")

# Mandatory architectural invariants that MUST exist in specific files
MANDATORY_INVARIANTS = [
    {
        "tag": "AIE-03",
        "description": "Decoupled Hill-Start Balanced Bleed & Independent Brake Motion Invariant",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-03]"
    },
    {
        "tag": "FLT-03",
        "description": "Sea-Level Ocean Spawning at Vector3.Zero Validation Guard",
        "expected_files": ["Fleet/TrainSpawner.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-03]"
    },
    {
        "tag": "FLT-04",
        "description": "Corrupted logicCar NRE Safe Consist Deletion via SafeDeleteCars",
        "expected_files": ["Fleet/TrainDespawner.cs", "Fleet/TrainSpawner.cs", "Core/TrafficManager.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-04]"
    },
    {
        "tag": "FLT-05",
        "description": "Scene Unload Dispatching Coroutine Leak & Spawner State Reset",
        "expected_files": ["Core/TrafficManager.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-05]"
    },
    {
        "tag": "FLT-06",
        "description": "Career Debt Segregation & Despawn Post-Deletion Untagging",
        "expected_files": ["Compat/AIDebtPatches.cs", "Fleet/TrainDespawner.cs", "Compat/ModCompatManager.cs"],
        "min_count": 3,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-06]"
    },
    {
        "tag": "JNC-01",
        "description": "Active Rear Bogie Clearance Monitoring Before Switch Release",
        "expected_files": ["Navigation/JunctionController.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 2 [JNC-01]"
    },
    {
        "tag": "JNC-04",
        "description": "Preserving Player Shunting Agency & Re-Entrancy Switch Lock Bypass",
        "expected_files": ["Navigation/JunctionController.cs", "Compat/PlayerSwitchLockPatches.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 2 [JNC-04]"
    },
    {
        "tag": "JNC-06",
        "description": "Station Throat Dwell Timeout Lock Release",
        "expected_files": ["Navigation/JunctionController.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 2 [JNC-06]"
    },
    {
        "tag": "RGR-03",
        "description": "Unity Main-Thread State Snapshotting for Background Thread Pathfinding",
        "expected_files": ["Navigation/Pathfinder.cs", "Navigation/RailGraph.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-03]"
    },
    {
        "tag": "RGR-04",
        "description": "Visibility of FarCarOpt Suspended Cars in Occupancy Snapshot",
        "expected_files": ["Navigation/RailGraph.cs", "Compat/PersistentJobsPatches.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-04]"
    },
    {
        "tag": "RGR-06",
        "description": "Prohibition of StrictlyAvoidOccupied = false in Train Routing",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-06]"
    },
    {
        "tag": "RGR-07",
        "description": "Universal Right-Hand Running (Rechtsfahrgebot) & Zero-Stagger Corridor Pairing",
        "expected_files": ["Navigation/RailGraph.cs", "Navigation/Pathfinder.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-07]"
    },
    {
        "tag": "QUIRK-04",
        "description": "Event-Driven Signal Wake-Up Debouncing (0.5s Cooldown)",
        "expected_files": ["Driver/AIEngineer.cs", "Core/TrafficManager.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-05] / QUIRK-04"
    },
    {
        "tag": "AIE-08",
        "description": "Entry Signal Interlocking Lookahead & Multi-Track Yellow-Box Decoupling",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-08]"
    },
    {
        "tag": "FLT-10",
        "description": "1:1 Alternation Gate Between Omnipresent Passing Encounters and Corridor Traffic",
        "expected_files": ["Core/TrafficScheduler.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-10]"
    },
    {
        "tag": "AIE-09",
        "description": "Off-Route Spur Reverse Interlocking, TargetDirection Preservation & Consist Clearance",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 3,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-09]"
    },
    {
        "tag": "AIE-10",
        "description": "Unified 35m Unaligned Switch Stop Boundary for Facing & Trailing Approaches",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-10]"
    },
    {
        "tag": "AIE-11",
        "description": "Full-Route Corridor Conflict Lookahead Immunity to _upcomingTracks Truncation",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-11]"
    },
    {
        "tag": "AIE-12",
        "description": "Dynamic Detour Direct Path Adoption, Siding Reverse Clearance & Recalculation Exit Gate",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-12]"
    },
    {
        "tag": "AIE-13",
        "description": "Single-Track Multi-Track Unlatching, Immediate Conflict Braking & Full-Corridor Reverse",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 3,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-13]"
    },
    {
        "tag": "RGR-08",
        "description": "Single-Track Corridor Token Player-Signal Preemption & Re-Validation",
        "expected_files": ["Navigation/RailGraph.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-08]"
    },
    {
        "tag": "AIE-14",
        "description": "Amperage Port Filtering, Float Sanitization & Dynamic Closed-Loop Current Governor",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-14]"
    },
    {
        "tag": "FLT-11",
        "description": "Complete Military Base Career License Gating & Road MA/MB Classification",
        "expected_files": ["Fleet/TrainSpawner.cs", "Navigation/Pathfinder.cs", "Core/TrafficScheduler.cs", "Navigation/RailGraph.cs"],
        "min_count": 4,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-11]"
    },
    {
        "tag": "AIE-15",
        "description": "Head-On vs Same-Direction Obstacle Discrimination & Worker Reroute Prohibition",
        "expected_files": ["Driver/AIEngineer.cs", "Navigation/SignalRegistry.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-15]"
    },
    {
        "tag": "JNC-07",
        "description": "Clearance Monitor Entry Latch (HasEnteredSwitch) & Safe Discard",
        "expected_files": ["Navigation/JunctionController.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 2 [JNC-07]"
    },
    {
        "tag": "WRK-04",
        "description": "Completed Worker Consists Player Agency & Ghost Lock Teardown",
        "expected_files": ["Navigation/JunctionController.cs", "Driver/AIEngineer.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 6 [WRK-04]"
    },
    {
        "tag": "WRK-05",
        "description": "Designation-Tiered, Proximity-Sorted Arrival Track Auto-Selection",
        "expected_files": ["Workers/WorkerManager.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 6 [WRK-05]"
    },
    {
        "tag": "FLT-12",
        "description": "Live-Car Identity, Recycled Car IDs & No Automatic Staged-Debt Purge",
        "expected_files": ["Compat/ModCompatManager.cs", "Compat/AIDebtPatches.cs", "Core/TrafficManager.cs"],
        "min_count": 3,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-12]"
    },
    {
        "tag": "FLT-13",
        "description": "Normalized Reverser Domain Mapping in Lighting",
        "expected_files": ["Driver/AILocomotiveLighting.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-13]"
    },
    {
        "tag": "RGR-09",
        "description": "Scheduler Negative-Result Cache & Traversable-Edge Cache Locking",
        "expected_files": ["Navigation/Pathfinder.cs", "Navigation/RailGraph.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-09]"
    },
    {
        "tag": "AIE-16",
        "description": "Background Reroute Requests for Stationary Triggers",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-16]"
    },
    {
        "tag": "RGR-10",
        "description": "Live Spawn-Track Re-Check After Async Pathfinding",
        "expected_files": ["Core/TrafficScheduler.cs"],
        "min_count": 3,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-10]"
    },
    {
        "tag": "WRK-06",
        "description": "Bounded Worker Arrival Route Checks & O(1) Spur Switch Lookup",
        "expected_files": ["Workers/WorkerManager.cs"],
        "min_count": 2,
        "graveyard_ref": "GRAVEYARD.md Section 6 [WRK-06]"
    },
    {
        "tag": "AIE-18",
        "description": "Main Signal Yield Protocol & 120s Hold on Player Switch Contention",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 4,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-18]"
    },
    {
        "tag": "JNC-08",
        "description": "Non-Mutating Hover Preview vs. Actuation Yield in Player Switch Patches",
        "expected_files": ["Compat/PlayerSwitchLockPatches.cs", "Navigation/JunctionController.cs"],
        "min_count": 4,
        "graveyard_ref": "GRAVEYARD.md Section 2 [JNC-08]"
    },
    {
        "tag": "AIE-19",
        "description": "Pre-Signal Block-0 Switch Protection at Hp 0, Same-Tick Reserve-Purge Prevention & Red Timer Flicker Decay",
        "expected_files": ["Driver/AIEngineer.cs"],
        "min_count": 4,
        "graveyard_ref": "GRAVEYARD.md Section 1 [AIE-19]"
    },
    {
        "tag": "RGR-11",
        "description": "Per-Train SignalBlockInfo Instance Isolation in CalculateUpcomingSignalBlocks",
        "expected_files": ["Navigation/SignalRegistry.cs"],
        "min_count": 1,
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-11]"
    }
]

# Regex patterns for anti-pattern detection
ANTI_PATTERNS = [
    {
        "name": "StrictlyAvoidOccupied = false in Train Routing",
        "regex": re.compile(r'\bStrictlyAvoidOccupied\s*=\s*false\b'),
        "allowed_files": ["Debug/TopologyMapExporter.cs"],
        "message": (
            "Setting 'StrictlyAvoidOccupied = false' in train routing paths is strictly prohibited (RGR-06).\n"
            "   Under NO circumstances may an AI train traverse an occupied track.\n"
            "   Only TopologyMapExporter is permitted to bypass occupancy checks for offline topology export."
        ),
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-06]"
    },
    {
        "name": "Forbidden curve-staggered midpoint lateralOffset calculation in RailGraph.cs",
        "regex": re.compile(r'Vector3\.Dot\s*\(\s*mid2\s*-\s*mid1\s*,\s*rightNormal1\s*\)'),
        "allowed_files": [],
        "message": (
            "Computing lateralOffset using raw midpoint differences (mid2 - mid1) is strictly forbidden (RGR-07).\n"
            "   On curved tracks, longitudinal stagger causes radial deflection that flips the sign of lateralOffset.\n"
            "   Use zero-stagger alternating spline cross-section projection instead."
        ),
        "graveyard_ref": "GRAVEYARD.md Section 3 [RGR-07]"
    },
    {
        "name": "Raw CarSpawner.Instance.DeleteTrainCars or DeleteCar Call",
        "regex": re.compile(r'\bCarSpawner\.Instance\.(DeleteTrainCars|DeleteCar)\b'),
        "allowed_files": ["Fleet/TrainDespawner.cs"],
        "message": (
            "Direct invocation of CarSpawner.Instance.DeleteTrainCars or DeleteCar is strictly forbidden (FLT-04).\n"
            "   Consist deletion must route through TrainDespawner.SafeDeleteCars to prevent orphan zombie cars\n"
            "   and unhandled NullReferenceExceptions caused by corrupted logicCar references."
        ),
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-04]"
    },
    {
        "name": "Overbroad AI Ghost Car Deletion Heuristic",
        "regex": re.compile(r'playerSpawnedCar\s*&&\s*(?:[a-zA-Z0-9_.]+\.)?preventDebtDisplay|preventDebtDisplay\s*&&\s*(?:[a-zA-Z0-9_.]+\.)?playerSpawnedCar'),
        "allowed_files": [],
        "message": (
            "Suspect ghost car deletion heuristic 'playerSpawnedCar && preventDebtDisplay' detected (FLT-02).\n"
            "   This heuristic erroneously classifies museum demonstrator locomotives at City South as AI ghosts\n"
            "   and deletes them. Use explicit AITrafficCarMarker or ModCompatManager.IsAmbientAITrain instead."
        ),
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-02]"
    },
    {
        "name": "Ambient Corridor Starvation via Short-Circuiting MoreTrainEncounters || _nextDispatchIsEncounter",
        "regex": re.compile(r'MoreTrainEncounters\s*\|\|\s*_nextDispatchIsEncounter'),
        "allowed_files": [],
        "message": (
            "Combining MoreTrainEncounters and _nextDispatchIsEncounter with '||' is strictly forbidden (FLT-10).\n"
            "   This short-circuits passing encounter checks on every tick and starves corridor dispatches.\n"
            "   Gate passing encounters with '&& _nextDispatchIsEncounter' instead."
        ),
        "graveyard_ref": "GRAVEYARD.md Section 5 [FLT-10]"
    }
]


def normalize_rel_path(path: Path, root: Path) -> str:
    """Normalizes a path relative to the root directory using forward slashes."""
    return str(path.relative_to(root)).replace("\\", "/")


def scan_source_files(src_dir: Path) -> List[Path]:
    """Recursively finds all .cs files in src directory, ignoring build artifacts."""
    files = []
    for p in sorted(src_dir.rglob("*.cs")):
        rel = normalize_rel_path(p, src_dir)
        if any(ignored in rel for ignored in ["bin/", "obj/", "dist/"]):
            continue
        files.append(p)
    return files


def verify_critical_invariants(cs_files: List[Path], src_dir: Path, verbose: bool = False) -> Tuple[bool, List[str]]:
    """
    Scans files for `// CRITICAL INVARIANT [...]` tags and verifies that all
    mandatory invariants are present in their required locations.
    """
    errors: List[str] = []
    # Map tag -> list of (rel_path, line_no, full_line)
    found_tags: Dict[str, List[Tuple[str, int, str]]] = {}
    tag_pattern = re.compile(r'//\s*CRITICAL\s+INVARIANT\s+\[([^\]]+)\](?::\s*(.*))?', re.IGNORECASE)

    total_tag_instances = 0

    for file_path in cs_files:
        rel_path = normalize_rel_path(file_path, src_dir)
        try:
            with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                lines = f.readlines()
        except Exception as e:
            errors.append(f"Could not read file {rel_path}: {e}")
            continue

        for idx, line in enumerate(lines, 1):
            match = tag_pattern.search(line)
            if match:
                total_tag_instances += 1
                tag_id = match.group(1).strip()
                # Some tags have secondary tags like [JNC-01] [QUIRK-02]
                sub_tags = re.findall(r'([A-Z0-9_\-\.]+)', tag_id)
                for t in sub_tags:
                    found_tags.setdefault(t, []).append((rel_path, idx, line.strip()))

    print(cyan(f"[*] Found {total_tag_instances} 'CRITICAL INVARIANT' comments across {len(found_tags)} distinct tags."))

    if verbose:
        for t, occurrences in sorted(found_tags.items()):
            print(f"    - [{t}]: {len(occurrences)} occurrence(s)")

    # Validate mandatory invariants
    print(bold("\n--- 1. Mandatory Invariant Checks ---"))
    for req in MANDATORY_INVARIANTS:
        tag = req["tag"]
        desc = req["description"]
        expected_files = req["expected_files"]
        min_count = req["min_count"]
        ref = req["graveyard_ref"]

        occurrences = found_tags.get(tag, [])
        total_found = len(occurrences)

        # Check total count
        if total_found < min_count:
            err = (
                f"{red('[FAIL]')} Missing mandatory invariant tag [{bold(tag)}] ({desc})\n"
                f"       Required minimum occurrences: {min_count}, found: {total_found}\n"
                f"       Reference: {ref}"
            )
            errors.append(err)
            print(f"  {err}")
            continue

        # Check required files
        files_with_tag = {rel for rel, _, _ in occurrences}
        missing_files = [ef for ef in expected_files if ef not in files_with_tag]

        if missing_files:
            err = (
                f"{red('[FAIL]')} Invariant tag [{bold(tag)}] missing from mandatory file(s): {', '.join(missing_files)}\n"
                f"       Present in: {', '.join(sorted(files_with_tag)) or 'None'}\n"
                f"       Reference: {ref}"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} [{tag}] {desc} ({total_found} occurrences in {len(files_with_tag)} file(s))")

    return len(errors) == 0, errors


def verify_anti_patterns(cs_files: List[Path], src_dir: Path) -> Tuple[bool, List[str]]:
    """
    Scans files for forbidden coding anti-patterns that bypass architectural invariants.
    """
    print(bold("\n--- 2. Forbidden Anti-Pattern Checks ---"))
    errors: List[str] = []

    for rule in ANTI_PATTERNS:
        rule_name = rule["name"]
        pattern = rule["regex"]
        allowed_files = set(rule["allowed_files"])
        message = rule["message"]
        ref = rule["graveyard_ref"]

        rule_violations = 0

        for file_path in cs_files:
            rel_path = normalize_rel_path(file_path, src_dir)
            if rel_path in allowed_files:
                continue

            try:
                with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                    lines = f.readlines()
            except Exception as e:
                errors.append(f"Could not read file {rel_path}: {e}")
                continue

            for idx, line in enumerate(lines, 1):
                # Ignore comment lines
                stripped = line.strip()
                if stripped.startswith("//") or stripped.startswith("/*") or stripped.startswith("*"):
                    continue

                if pattern.search(line):
                    rule_violations += 1
                    err = (
                        f"{red('[VIOLATION]')} {bold(rule_name)} in {rel_path}:{idx}\n"
                        f"            Code: {stripped}\n"
                        f"            {message}\n"
                        f"            Reference: {ref}"
                    )
                    errors.append(err)
                    print(f"  {err}\n")

        if rule_violations == 0:
            print(f"  {green('[PASS]')} {rule_name}: Zero violations detected.")

    return len(errors) == 0, errors


def verify_specific_motion_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    Verifies that specific critical functional guards are intact in code
    (e.g., independent brake motion invariant in AIEngineer.cs).
    """
    print(bold("\n--- 3. Functional Safety Guard Checks ---"))
    errors: List[str] = []

    # Check AIEngineer.cs for the independent brake motion invariant
    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if not ai_eng_path.exists():
        err = f"{red('[FAIL]')} Expected file not found: Driver/AIEngineer.cs"
        errors.append(err)
        print(f"  {err}")
        return False, errors

    with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
        ai_eng_content = f.read()

    # Invariant: CurrentSpeedKmh >= 0.2f clamps independent brake to 0.0f
    speed_clamp_pattern = re.compile(
        r'if\s*\(\s*CurrentSpeedKmh\s*>=\s*0\.2f\s*\)\s*\{[^}]*_commandedIndependentBrake\s*=\s*0\.0f',
        re.DOTALL
    )
    if not speed_clamp_pattern.search(ai_eng_content):
        err = (
            f"{red('[FAIL]')} Absolute Motion Invariant missing in Driver/AIEngineer.cs:\n"
            f"       'if (CurrentSpeedKmh >= 0.2f) {{ _commandedIndependentBrake = 0.0f; ... }}' was altered or removed!\n"
            f"       Reference: GRAVEYARD.md §1 [AIE-03]"
        )
        errors.append(err)
        print(f"  {err}")
    else:
        print(f"  {green('[PASS]')} Independent Brake Motion Invariant verified in Driver/AIEngineer.cs")

    return len(errors) == 0, errors


def verify_rgr07_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [RGR-07] Verifies Universal Right-Hand Running Rule, zero-stagger cross-section pairing,
    precomputed lookahead tangents, and right-hand corridor cost dominance.
    """
    print(bold("\n--- 4. Right-Hand Running Invariant Checks [RGR-07] ---"))
    errors: List[str] = []

    def check_file_contains(rel_file: str, required_strings: List[str], desc: str):
        target = src_dir / rel_file.replace("src/", "").replace("src\\", "")
        if not target.exists():
            err = f"{red('[FAIL]')} Expected file not found: {target}"
            errors.append(err)
            print(f"  {err}")
            return
        with open(target, "r", encoding="utf-8", errors="replace") as f:
            content = f.read()
        missing = [s for s in required_strings if s not in content]
        if missing:
            err = (
                f"{red('[FAIL]')} {desc}\n"
                f"       Missing required substring(s) in {rel_file}: {missing}\n"
                f"       Reference: GRAVEYARD.md Section 3 [RGR-07]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} {desc}")

    def check_file_avoid(rel_file: str, forbidden_strings: List[str], desc: str):
        target = src_dir / rel_file.replace("src/", "").replace("src\\", "")
        if not target.exists():
            return
        with open(target, "r", encoding="utf-8", errors="replace") as f:
            content = f.read()
        found = [s for s in forbidden_strings if s in content]
        if found:
            err = (
                f"{red('[FAIL]')} {desc}\n"
                f"       Found forbidden substring(s) in {rel_file}: {found}\n"
                f"       Reference: GRAVEYARD.md Section 3 [RGR-07]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} {desc}")

    # [RGR-07] Universal Right-Hand Running (Rechtsfahrgebot), Zero-Stagger Cross-Section Pairing & Lookahead Tangents
    check_file_contains(
        "src/Navigation/RailGraph.cs",
        [
            "CRITICAL INVARIANT [RGR-07]",
            "FromLookaheadTangent",
            "ToLookaheadTangent",
            "GetLookaheadDirection",
            "maxLongitudinalStagger",
            "rightFlowsIntoShared",
            "bool edge1IsShorter = (edge1.Length <= edge2.Length);",
            "RailEdge edgeShort",
            "bestColinearDot",
            "PropagateBoundedSplitToMergeCorridors",
            "maxLengthDelta = 500.0f",
            "Mathf.Abs(lenRight - lenLeft) > maxLengthDelta",
            "IsCrossoverTrackName",
            '"SX"',
            '"DTC-"',
        ],
        "[RGR-07] RailGraph zero-stagger cross-section pairing, shorter-edge projection seeding, colinear lead propagation & bounded split-to-merge continuation",
    )
    check_file_avoid(
        "src/Navigation/RailGraph.cs",
        [
            "Vector3.Dot(mid2 - mid1, rightNormal1)",
            'StartsWith("[Y]"',
            'IndexOf("_[Y]"',
            'IndexOf("_Y_"',
        ],
        "[RGR-07] Forbidden curve-staggered midpoint calculation & [Y] namespace prefix checks in RailGraph.cs",
    )
    check_file_contains(
        "src/Navigation/Pathfinder.cs",
        [
            "CRITICAL INVARIANT [RGR-07]",
            "GetLookaheadDirection(fromNode)",
            "isRightHandCorridorTrack",
            "isLeftHandCorridorTrack",
            "effectiveCrossoverPenalty",
            "effectiveWrongDirFlatPenalty",
            "wasOnRightHandCorridor",
            "5000f",
            "1500f",
            '"SX"',
            '"DTC-"',
            "if (isRightHandCorridorTrack && !isLeftHandDivergingBranch && !isLeftHandCorridorTrack && !edge.IsIndustrialYardOrStorageTrack)",
        ],
        "[RGR-07] Pathfinder lookahead branch divergence, right-hand corridor cost dominance & mainline discount parity",
    )
    check_file_avoid(
        "src/Navigation/Pathfinder.cs",
        [
            'StartsWith("[Y]"',
            'IndexOf("_[Y]"',
            'IndexOf("_Y_"',
            "isMainline || isRightHandCorridorTrack",
        ],
        "[RGR-07] Forbidden [Y] namespace prefix checks & unfair isMainline corridor discount in Pathfinder.cs",
    )

    return len(errors) == 0, errors


def verify_aie08_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [AIE-08] Verifies Entry Signal interlocking lookahead preservation,
    station Entry Signal boundary check in multi-track yellow-box protection,
    and absence of raw sig.Block.FlagAsDirty() in UpdateSingleTrackCorridorProtection.
    """
    print(bold("\n--- 5. Entry Signal Interlocking & Multi-Track Hold Checks [AIE-08] ---"))
    errors: List[str] = []

    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if not ai_eng_path.exists():
        err = f"{red('[FAIL]')} Expected file not found: Driver/AIEngineer.cs"
        errors.append(err)
        print(f"  {err}")
        return False, errors

    with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
        content = f.read()

    # a) Driver/AIEngineer.cs contains CRITICAL INVARIANT [AIE-08]
    if "CRITICAL INVARIANT [AIE-08]" not in content:
        err = (
            f"{red('[FAIL]')} Missing comment anchor 'CRITICAL INVARIANT [AIE-08]' in Driver/AIEngineer.cs\n"
            f"       Reference: GRAVEYARD.md Section 1 [AIE-08]"
        )
        errors.append(err)
        print(f"  {err}")
    else:
        print(f"  {green('[PASS]')} Invariant anchor [AIE-08] found in Driver/AIEngineer.cs")

    # b) AITraffic.Navigation.SignalRegistry.IsEntrySignal(upcomingSig.Signal) is present in Driver/AIEngineer.cs
    if "AITraffic.Navigation.SignalRegistry.IsEntrySignal(upcomingSig.Signal)" not in content:
        err = (
            f"{red('[FAIL]')} Missing Entry Signal yellow-box boundary guard in Driver/AIEngineer.cs:\n"
            f"       Expected: 'AITraffic.Navigation.SignalRegistry.IsEntrySignal(upcomingSig.Signal)'\n"
            f"       Reference: GRAVEYARD.md Section 1 [AIE-08]"
        )
        errors.append(err)
        print(f"  {err}")
    else:
        print(f"  {green('[PASS]')} Entry Signal yellow-box boundary guard verified in Driver/AIEngineer.cs")

    # c) !AITraffic.Navigation.SignalRegistry.IsEntrySignal(governingSig) is present in Driver/AIEngineer.cs
    if "!AITraffic.Navigation.SignalRegistry.IsEntrySignal(governingSig)" not in content:
        err = (
            f"{red('[FAIL]')} Missing Entry Signal lookahead exemption in Driver/AIEngineer.cs:\n"
            f"       Expected: '!AITraffic.Navigation.SignalRegistry.IsEntrySignal(governingSig)'\n"
            f"       Reference: GRAVEYARD.md Section 1 [AIE-08]"
        )
        errors.append(err)
        print(f"  {err}")
    else:
        print(f"  {green('[PASS]')} Entry Signal lookahead exemption verified in Driver/AIEngineer.cs")

    # d) Ensure UpdateSingleTrackCorridorProtection does not call sig.Block.FlagAsDirty()
    corridor_method_match = re.search(
        r'void\s+UpdateSingleTrackCorridorProtection\s*\([^\)]*\)\s*\{',
        content
    )
    if not corridor_method_match:
        err = (
            f"{red('[FAIL]')} Could not locate method 'UpdateSingleTrackCorridorProtection' in Driver/AIEngineer.cs"
        )
        errors.append(err)
        print(f"  {err}")
    else:
        start_idx = corridor_method_match.end()
        end_match = re.search(r'void\s+ReleaseActiveCorridorToken\s*\(', content[start_idx:])
        if end_match:
            method_body = content[start_idx : start_idx + end_match.start()]
        else:
            method_body = content[start_idx : start_idx + 25000]

        if "sig.Block.FlagAsDirty()" in method_body:
            err = (
                f"{red('[FAIL]')} Raw 'sig.Block.FlagAsDirty()' call detected inside UpdateSingleTrackCorridorProtection!\n"
                f"       Use DebouncedWakeAndForceUpdateSignal(sig) instead.\n"
                f"       Reference: GRAVEYARD.md Section 1 [AIE-08]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Zero raw sig.Block.FlagAsDirty() calls in UpdateSingleTrackCorridorProtection")

    return len(errors) == 0, errors


def verify_v028_hotfix_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [FLT-10], [AIE-09], [AIE-10], [AIE-11]
    Verifies 1:1 passing encounter gating, off-route reverse interlocking & consist clearance,
    35m unaligned switch stop boundary, and full-route corridor conflict scanning.
    """
    print(bold("\n--- 6. Switch, Corridor & Reversal Invariant Checks [FLT-10, AIE-09..11] ---"))
    errors: List[str] = []

    # FLT-10 in TrafficScheduler.cs
    sched_path = src_dir / "Core" / "TrafficScheduler.cs"
    if sched_path.exists():
        with open(sched_path, "r", encoding="utf-8", errors="replace") as f:
            sched_content = f.read()
        if "&& _nextDispatchIsEncounter" not in sched_content:
            err = (
                f"{red('[FAIL]')} Missing '&& _nextDispatchIsEncounter' passing gate in Core/TrafficScheduler.cs\n"
                f"       Reference: GRAVEYARD.md Section 5 [FLT-10]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Passing encounter 1:1 alternation gate verified in Core/TrafficScheduler.cs")

    # AIE-09, AIE-10, AIE-11 in AIEngineer.cs
    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if ai_eng_path.exists():
        with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
            ai_eng_content = f.read()

        # AIE-09: UpdatePathAndJunctions guarded during off-route reverse
        if "if (_offRouteReverseStage == OffRouteReverseStage.None)" not in ai_eng_content:
            err = (
                f"{red('[FAIL]')} Missing '_offRouteReverseStage == OffRouteReverseStage.None' guard around UpdatePathAndJunctions in Driver/AIEngineer.cs\n"
                f"       Reference: GRAVEYARD.md Section 1 [AIE-09]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Off-route reverse path lookahead suppression verified in Driver/AIEngineer.cs")

        # AIE-10: 35m unaligned switch stop boundary
        if "Mathf.Max(0.0f, routeDist - 35.0f)" not in ai_eng_content:
            err = (
                f"{red('[FAIL]')} Missing 35m unaligned switch stop boundary calculation in Driver/AIEngineer.cs\n"
                f"       Reference: GRAVEYARD.md Section 1 [AIE-10]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} 35m unaligned switch protective stop boundary verified in Driver/AIEngineer.cs")

        # AIE-11: Full CurrentPath.Tracks scan and 4.0s timer
        if "for (int i = Math.Max(0, CurrentPathTrackIndex + 1); i < CurrentPath.Tracks.Count; i++)" not in ai_eng_content:
            err = (
                f"{red('[FAIL]')} Missing full CurrentPath.Tracks scan in UpdateSingleTrackCorridorProtection in Driver/AIEngineer.cs\n"
                f"       Reference: GRAVEYARD.md Section 1 [AIE-11]"
            )
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Full CurrentPath.Tracks corridor conflict scan verified in Driver/AIEngineer.cs")

    return len(errors) == 0, errors


def verify_aie13_rgr08_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [AIE-13], [RGR-08]
    Verifies single-track corridor token player-signal preemption, multi-track unlatching,
    immediate conflict braking, 10,000m trailing siding scan, stem-to-outBranch reverse support,
    and stall-only reverse timeout.
    """
    print(bold("\n--- 7. Single-Track Reverse & Token Preemption Checks [AIE-13, RGR-08] ---"))
    errors: List[str] = []

    # RGR-08 in RailGraph.cs
    rg_path = src_dir / "Navigation" / "RailGraph.cs"
    if rg_path.exists():
        with open(rg_path, "r", encoding="utf-8", errors="replace") as f:
            rg_content = f.read()
        if "CRITICAL INVARIANT [RGR-08]" not in rg_content:
            err = f"{red('[FAIL]')} Missing 'CRITICAL INVARIANT [RGR-08]' in Navigation/RailGraph.cs"
            errors.append(err)
            print(f"  {err}")
        elif "SignalRegistry.IsTrackReservedByPlayerSignal(track)" not in rg_content:
            err = f"{red('[FAIL]')} Missing 'SignalRegistry.IsTrackReservedByPlayerSignal(track)' in Navigation/RailGraph.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Single-track corridor token player signal preemption [RGR-08] verified in Navigation/RailGraph.cs")

    # AIE-13 in AIEngineer.cs
    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if ai_eng_path.exists():
        with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
            ai_eng_content = f.read()

        if "CRITICAL INVARIANT [AIE-13]" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing 'CRITICAL INVARIANT [AIE-13]' in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Critical invariant [AIE-13] anchors verified in Driver/AIEngineer.cs")

        if "_reverseProtocolStage != SingleTrackReverseStage.None" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing '_reverseProtocolStage != SingleTrackReverseStage.None' guard in UpdateSingleTrackCorridorProtection in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Active reverse protocol corridor hold guard verified in Driver/AIEngineer.cs")

        if "accumulatedDist > 10000.0f" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing 10,000m scan limit in FindNearestTrailingSiding in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} 10,000m trailing siding traversal horizon verified in Driver/AIEngineer.cs")

        if "DoesJunctionConnectTrack" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing DoesJunctionConnectTrack method in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} DoesJunctionConnectTrack forward-junction exclusion verified in Driver/AIEngineer.cs")

        if "CurrentSpeedKmh < 1.0f" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing stall-only reverse timer in ReversingToSiding in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Stall-only reverse timeout condition verified in Driver/AIEngineer.cs")

        if "IsJunctionPhysicallyOccupied(_trailingSidingSwitch, out occCar, reqTrainset)" not in ai_eng_content or "signalClearanceMet" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing requesterTrainset differentiation or signalClearanceMet in DuckingIntoSiding [AIE-12/JNC-03] in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Siding reverse requesterTrainset & governing signal clearance [AIE-12/JNC-03] verified in Driver/AIEngineer.cs")

    return len(errors) == 0, errors


def verify_aie14_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [AIE-14] Verifies Amperage Port Filtering, Float Sanitization, and Closed-Loop Governor Ceilings.
    """
    print(bold("\n--- 8. Amperage Port Filtering & Current Governor Checks [AIE-14] ---"))
    errors: List[str] = []

    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if ai_eng_path.exists():
        with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
            ai_eng_content = f.read()

        if "CRITICAL INVARIANT [AIE-14]" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing 'CRITICAL INVARIANT [AIE-14]' in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Critical invariant [AIE-14] anchors verified in Driver/AIEngineer.cs")

        if 'port.id.IndexOf("LIMIT", StringComparison.OrdinalIgnoreCase)' not in ai_eng_content or \
           'port.id.IndexOf("EXT_IN", StringComparison.OrdinalIgnoreCase)' not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing 'LIMIT' or 'EXT_IN' port exclusion in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Amperage port 'LIMIT' / 'EXT_IN' exclusion verified in Driver/AIEngineer.cs")

        if "float.IsInfinity" not in ai_eng_content or "!float.IsInfinity" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing float.IsInfinity sanitization in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Non-finite float.IsInfinity sanitization verified in Driver/AIEngineer.cs")

        if "650.0f" not in ai_eng_content or "1000.0f" not in ai_eng_content:
            err = f"{red('[FAIL]')} Missing 650.0f (DE2) or 1000.0f (DE6) ceiling limits in Driver/AIEngineer.cs"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Closed-loop current governor ceilings (650A DE2 / 1000A DE6) verified in Driver/AIEngineer.cs")

    return len(errors) == 0, errors


def verify_flt06_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [FLT-06] Verifies:
    1. CarDebtController_SetDebtTracker_Patch in AIDebtPatches.cs checks TrainSpawner.IsSpawningAmbientConsist.
    2. LocoDebtController_StageLocoDebtOnLocoDestroy_Patch in AIDebtPatches.cs falls back to existing.ID and GetDebtData().
    3. ModCompatManager.IsAmbientAITrainId does not call s_knownNonAiCarIds.Add.
    4. TrainDespawner.SafeDeleteCars calls ModCompatManager.UntagCars after DeleteTrainCars.
    """
    print(bold("\n--- 9. Career Debt & Despawn Untagging Checks [FLT-06] ---"))
    errors: List[str] = []

    ai_debt_path = src_dir / "Compat" / "AIDebtPatches.cs"
    if ai_debt_path.exists():
        with open(ai_debt_path, "r", encoding="utf-8", errors="replace") as f:
            debt_content = f.read()

        # 1. CarDebtController_SetDebtTracker_Patch checks TrainSpawner.IsSpawningAmbientConsist
        if "TrainSpawner.IsSpawningAmbientConsist" not in debt_content or \
           "CarDebtController_SetDebtTracker_Patch" not in debt_content:
            err = f"{red('[FAIL]')} Missing TrainSpawner.IsSpawningAmbientConsist check in Compat/AIDebtPatches.cs [FLT-06]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} CarDebtController_SetDebtTracker_Patch checks IsSpawningAmbientConsist [FLT-06]")

        # 2. LocoDebtController_StageLocoDebtOnLocoDestroy_Patch falls back to existing.ID and GetDebtData()
        if "existingCarId = existing.ID;" not in debt_content or \
           "trackerData = locoDebtTrackerToStage.GetDebtData()" not in debt_content:
            err = f"{red('[FAIL]')} Missing fallback to existing.ID or GetDebtData() in StageLocoDebtOnLocoDestroy [FLT-06]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} StageLocoDebtOnLocoDestroy logicCar==null fallback to existing.ID / GetDebtData() [FLT-06]")

    mod_compat_path = src_dir / "Compat" / "ModCompatManager.cs"
    if mod_compat_path.exists():
        with open(mod_compat_path, "r", encoding="utf-8", errors="replace") as f:
            compat_content = f.read()

        # 3. ModCompatManager.IsAmbientAITrainId does not call s_knownNonAiCarIds.Add
        method_match = re.search(r'public\s+static\s+bool\s+IsAmbientAITrainId\s*\([^)]*\)\s*\{', compat_content)
        if method_match:
            start_idx = method_match.end()
            end_match = re.search(r'\n\s*public\s+', compat_content[start_idx:])
            method_body = compat_content[start_idx : start_idx + end_match.start()] if end_match else compat_content[start_idx:start_idx+2000]
            if "s_knownNonAiCarIds.Add" in method_body:
                err = f"{red('[FAIL]')} s_knownNonAiCarIds.Add call detected inside ModCompatManager.IsAmbientAITrainId! [FLT-06]"
                errors.append(err)
                print(f"  {err}")
            else:
                print(f"  {green('[PASS]')} Zero s_knownNonAiCarIds negative-cache poisoning in IsAmbientAITrainId [FLT-06]")
        else:
            err = f"{red('[FAIL]')} Could not locate IsAmbientAITrainId in Compat/ModCompatManager.cs [FLT-06]"
            errors.append(err)
            print(f"  {err}")

    despawner_path = src_dir / "Fleet" / "TrainDespawner.cs"
    if despawner_path.exists():
        with open(despawner_path, "r", encoding="utf-8", errors="replace") as f:
            desp_content = f.read()

        # 4. TrainDespawner.SafeDeleteCars calls ModCompatManager.UntagCars after DeleteTrainCars
        safe_del_match = re.search(r'public\s+static\s+void\s+SafeDeleteCars\s*\([^)]*\)\s*\{', desp_content)
        if safe_del_match:
            start_idx = safe_del_match.end()
            end_match = re.search(r'\n\s*public\s+', desp_content[start_idx:])
            method_body = desp_content[start_idx : start_idx + end_match.start()] if end_match else desp_content[start_idx:start_idx+4000]
            if "ModCompatManager.UntagCars" not in method_body:
                err = f"{red('[FAIL]')} Missing ModCompatManager.UntagCars call inside SafeDeleteCars in Fleet/TrainDespawner.cs [FLT-06]"
                errors.append(err)
                print(f"  {err}")
            else:
                print(f"  {green('[PASS]')} SafeDeleteCars calls ModCompatManager.UntagCars after deletion [FLT-06]")
        else:
            err = f"{red('[FAIL]')} Could not locate SafeDeleteCars in Fleet/TrainDespawner.cs [FLT-06]"
            errors.append(err)
            print(f"  {err}")

    return len(errors) == 0, errors


def verify_flt11_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [FLT-11] Verifies:
    1. TrainSpawner.cs contains EndsWith(" MA" and EndsWith(" MB"
    2. Pathfinder.cs contains EndsWith(" MA" and EndsWith(" MB"
    3. RailGraph.cs contains !edge.IsGatedMilitaryTrack
    4. TrafficScheduler.cs contains edge.IsGatedMilitaryTrack || TrainSpawner.IsMilitaryTrack
    """
    print(bold("\n--- 10. Complete Military Base Career License Gating [FLT-11] ---"))
    errors: List[str] = []

    # 1. TrainSpawner.cs
    spawner_path = src_dir / "Fleet" / "TrainSpawner.cs"
    if spawner_path.exists():
        with open(spawner_path, "r", encoding="utf-8", errors="replace") as f:
            spawner_content = f.read()
        if 'name.EndsWith(" MA"' not in spawner_content or 'name.EndsWith(" MB"' not in spawner_content:
            err = f"{red('[FAIL]')} Missing 'EndsWith(\" MA\"' or 'EndsWith(\" MB\"' in Fleet/TrainSpawner.cs [FLT-11]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Standalone MA/MB token detection verified in Fleet/TrainSpawner.cs [FLT-11]")

    # 2. Pathfinder.cs
    pathfinder_path = src_dir / "Navigation" / "Pathfinder.cs"
    if pathfinder_path.exists():
        with open(pathfinder_path, "r", encoding="utf-8", errors="replace") as f:
            pf_content = f.read()
        if 'tName.EndsWith(" MA"' not in pf_content or 'tName.EndsWith(" MB"' not in pf_content:
            err = f"{red('[FAIL]')} Missing 'EndsWith(\" MA\"' or 'EndsWith(\" MB\"' in Navigation/Pathfinder.cs [FLT-11]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Standalone MA/MB token detection verified in Navigation/Pathfinder.cs [FLT-11]")

    # 3. RailGraph.cs
    rg_path = src_dir / "Navigation" / "RailGraph.cs"
    if rg_path.exists():
        with open(rg_path, "r", encoding="utf-8", errors="replace") as f:
            rg_content = f.read()
        if "!edge.IsGatedMilitaryTrack" not in rg_content:
            err = f"{red('[FAIL]')} Missing '!edge.IsGatedMilitaryTrack' guard on isOpenCountryRoad in Navigation/RailGraph.cs [FLT-11]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Military track exclusion from isOpenCountryRoad verified in Navigation/RailGraph.cs [FLT-11]")

    # 4. TrafficScheduler.cs
    sched_path = src_dir / "Core" / "TrafficScheduler.cs"
    if sched_path.exists():
        with open(sched_path, "r", encoding="utf-8", errors="replace") as f:
            sched_content = f.read()
        if "edge.IsGatedMilitaryTrack || TrainSpawner.IsMilitaryTrack" not in sched_content:
            err = f"{red('[FAIL]')} Missing 'edge.IsGatedMilitaryTrack || TrainSpawner.IsMilitaryTrack' in Core/TrafficScheduler.cs [FLT-11]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Passing & static departure military track gating verified in Core/TrafficScheduler.cs [FLT-11]")

    return len(errors) == 0, errors




def verify_aie15_guards(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [AIE-15] Verifies:
    1. SignalRegistry.cs outputs obstacleCar and ourDirectionAtObstacle in TryFindUpcomingObstacle.
    2. AIEngineer.cs defines ClassifyUpcomingObstacle and TryGetUpcomingDivergingSwitchDistance.
    3. AIEngineer.cs enforces pre-switch wait timer and Diverging Switch stop target clamp.
    4. AIEngineer.cs strictly guards TryDynamicObstacleReroute, TryDynamicRedSignalReroute, and TryDynamicPassingReroute against IsWorkerDriven.
    """
    print(bold("\n--- 11. Obstacle Discrimination, Pre-Switch Hold & Worker Route Immutability [AIE-15] ---"))
    errors: List[str] = []

    # 1. SignalRegistry.cs
    sig_path = src_dir / "Navigation" / "SignalRegistry.cs"
    if sig_path.exists():
        with open(sig_path, "r", encoding="utf-8", errors="replace") as f:
            sig_content = f.read()
        if "out Vector3 ourDirectionAtObstacle" not in sig_content or "out TrainCar obstacleCar" not in sig_content:
            err = f"{red('[FAIL]')} Missing obstacleCar or ourDirectionAtObstacle output in Navigation/SignalRegistry.cs [AIE-15]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Obstacle TrainCar and route tangent vector output verified in Navigation/SignalRegistry.cs [AIE-15]")

    # 2. AIEngineer.cs
    ai_eng_path = src_dir / "Driver" / "AIEngineer.cs"
    if ai_eng_path.exists():
        with open(ai_eng_path, "r", encoding="utf-8", errors="replace") as f:
            ai_content = f.read()

        if "ClassifyUpcomingObstacle" not in ai_content:
            err = f"{red('[FAIL]')} Missing ClassifyUpcomingObstacle method in Driver/AIEngineer.cs [AIE-15]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} ClassifyUpcomingObstacle method verified in Driver/AIEngineer.cs [AIE-15]")

        if "TryGetUpcomingDivergingSwitchDistance" not in ai_content:
            err = f"{red('[FAIL]')} Missing TryGetUpcomingDivergingSwitchDistance method in Driver/AIEngineer.cs [AIE-15]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} TryGetUpcomingDivergingSwitchDistance method verified in Driver/AIEngineer.cs [AIE-15]")

        if "_obstacleWaitTimer >= requiredWait" not in ai_content or "_hasPendingDivergingSwitchHold" not in ai_content:
            err = f"{red('[FAIL]')} Missing _obstacleWaitTimer or _hasPendingDivergingSwitchHold pre-switch hold logic in Driver/AIEngineer.cs [AIE-15]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Pre-switch wait timer and diverging switch stop clamp verified in Driver/AIEngineer.cs [AIE-15]")

        # Worker guards in reroute methods
        if "if (IsWorkerDriven) return false;" not in ai_content:
            err = f"{red('[FAIL]')} Missing 'if (IsWorkerDriven) return false;' guard in Driver/AIEngineer.cs [AIE-15]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} Worker AI reroute prohibition (IsWorkerDriven) verified in Driver/AIEngineer.cs [AIE-15]")

    return len(errors) == 0, errors


def verify_v029_invariants(src_dir: Path) -> Tuple[bool, List[str]]:
    """
    [v0.2.9 Invariants Verification]
    Enforces all explicit assertions for JNC-07, WRK-04, WRK-05, FLT-12, FLT-13, RGR-09, AIE-16, WRK-06, RGR-10.
    """
    print(bold("\n--- 12. v0.2.9 SPEC Invariants Verification [JNC-07, WRK-04..06, FLT-12..13, RGR-09..10, AIE-16] ---"))
    errors: List[str] = []

    def check_file(rel_path: str, must_contain: List[str], must_not_contain: List[str], tag: str):
        target = src_dir / rel_path
        if not target.exists():
            err = f"{red('[FAIL]')} File {rel_path} does not exist [{tag}]"
            errors.append(err)
            print(f"  {err}")
            return
        with open(target, "r", encoding="utf-8", errors="replace") as f:
            content = f.read()
        for token in must_contain:
            if token not in content:
                err = f"{red('[FAIL]')} {rel_path} missing required token '{token}' [{tag}]"
                errors.append(err)
                print(f"  {err}")
            else:
                print(f"  {green('[PASS]')} {rel_path} contains '{token}' [{tag}]")
        for token in must_not_contain:
            if token in content:
                err = f"{red('[FAIL]')} {rel_path} contains FORBIDDEN token '{token}' [{tag}]"
                errors.append(err)
                print(f"  {err}")
            else:
                print(f"  {green('[PASS]')} {rel_path} does not contain forbidden '{token}' [{tag}]")

    # JNC-07: JunctionController.cs
    check_file("Navigation/JunctionController.cs",
               ["HasEnteredSwitch", "[JNC-07]", "pastClearanceMargin && info.HasEnteredSwitch"],
               [], "JNC-07")

    # WRK-04: JunctionController.cs & AIEngineer.cs
    check_file("Navigation/JunctionController.cs",
               ["[WRK-04]", "IsTrainCarInActiveWorkerTask(occupyingCar)"],
               ["IsTrainCarInAnyWorkerTask(occupyingCar)"], "WRK-04")
    check_file("Driver/AIEngineer.cs",
               ["ReleaseAllLocksFor(this)"],
               [], "WRK-04")

    # WRK-05: WorkerManager.cs
    check_file("Workers/WorkerManager.cs",
               ["[WRK-05]", "IsTrainCarInActiveWorkerTask"],
               [], "WRK-05")

    # FLT-12: ModCompatManager.cs, AIDebtPatches.cs, TrafficManager.cs
    check_file("Compat/ModCompatManager.cs",
               ["[FLT-12]"],
               ["TrainSpawner.IsSpawningAmbientConsist || AITraffic.Fleet.TrainSpawner.IsCarSpawning"], "FLT-12")
    check_file("Compat/AIDebtPatches.cs",
               ["[FLT-12]"],
               ["stagedLocos.RemoveAt(i)", "deletedOther.joblessCarsDebtData.Clear()", "stagedOther.joblessCarsDebtData.Clear()"], "FLT-12")
    check_file("Core/TrafficManager.cs",
               ["[FLT-12]"],
               ["ModCompatManager.IsAmbientAITrainId(carId)) ||"], "FLT-12")

    # FLT-13: AILocomotiveLighting.cs
    check_file("Driver/AILocomotiveLighting.cs",
               ["(_controlsOverrider.Reverser.Value - 0.5f) * 2.0f"],
               [], "FLT-13")

    # RGR-09: Pathfinder.cs, RailGraph.cs, TrafficManager.cs
    check_file("Navigation/Pathfinder.cs",
               ["[RGR-09]", "UnreachablePairCacheTtlMs = 30000", "CacheUnreachablePair(pairKey)"],
               [], "RGR-09")
    check_file("Navigation/RailGraph.cs",
               ["[RGR-09]"],
               [], "RGR-09")
    check_file("Core/TrafficManager.cs",
               ["ClearUnreachablePairCache()"],
               [], "RGR-09")

    # AIE-16: AIEngineer.cs
    check_file("Driver/AIEngineer.cs",
               ["[AIE-16]", "TryGetAsyncReroute", "FindPathAsync", 'BuildRerouteKey("OBS"', 'BuildRerouteKey("SHU"', 'BuildRerouteKey("OFF1"', 'BuildRerouteKey("OFF2"', 'BuildRerouteKey("HAIRPIN"', "RecalculateRouteToDestination(true)"],
               ["TryAdoptPlayerAlignedPassingRoute"], "AIE-16")

    # WRK-06: WorkerManager.cs
    check_file("Workers/WorkerManager.cs",
               ["[WRK-06]", "MaxArrivalRouteChecks = 6", "originTrack.inJunction, originTrack.outJunction"],
               [], "WRK-06")

    # RGR-10: TrafficScheduler.cs
    target_sch = src_dir / "Core/TrafficScheduler.cs"
    if target_sch.exists():
        with open(target_sch, "r", encoding="utf-8", errors="replace") as f:
            sch_text = f.read()
        cnt_rgr10 = sch_text.count("[RGR-10]")
        if cnt_rgr10 < 3:
            err = f"{red('[FAIL]')} TrafficScheduler.cs has only {cnt_rgr10} occurrences of [RGR-10] (expected >= 3) [RGR-10]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} TrafficScheduler.cs has {cnt_rgr10} occurrences of '[RGR-10]' [RGR-10]")
        if "private static RailTrack FindClearDepartureTrack(" in sch_text:
            err = f"{red('[FAIL]')} TrafficScheduler.cs contains deleted method FindClearDepartureTrack [RGR-10]"
            errors.append(err)
            print(f"  {err}")
        else:
            print(f"  {green('[PASS]')} TrafficScheduler.cs does not contain dead method FindClearDepartureTrack [RGR-10]")

    return len(errors) == 0, errors


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Derail Valley AI Traffic - Pre-Build Static Invariant Gatekeeper"
    )
    parser.add_argument(
        "--src-dir",
        type=Path,
        default=None,
        help="Path to dv_ai_traffic/src directory (defaults to auto-detection)"
    )
    parser.add_argument(
        "-v", "--verbose",
        action="store_true",
        help="Enable verbose output"
    )
    args = parser.parse_args()

    # Determine src directory
    script_dir = Path(__file__).resolve().parent
    if args.src_dir:
        src_dir = args.src_dir.resolve()
    else:
        candidate = script_dir / "src"
        if candidate.exists() and candidate.is_dir():
            src_dir = candidate
        else:
            candidate = script_dir / "dv_ai_traffic" / "src"
            if candidate.exists() and candidate.is_dir():
                src_dir = candidate
            else:
                src_dir = Path.cwd() / "src"

    banner = "=" * 76
    print(cyan(banner))
    print(cyan(f"   Derail Valley AI Traffic - Pre-Build Static Invariant Gatekeeper"))
    print(cyan(banner))
    print(f"Scanning target: {bold(str(src_dir))}")

    if not src_dir.exists() or not src_dir.is_dir():
        print(red(f"[FATAL] Source directory does not exist: {src_dir}"))
        return 1

    cs_files = scan_source_files(src_dir)
    print(f"Found {bold(str(len(cs_files)))} C# source (.cs) files.")

    if not cs_files:
        print(red("[FATAL] No .cs files found to scan!"))
        return 1

    invariants_ok, invariant_errors = verify_critical_invariants(cs_files, src_dir, verbose=args.verbose)
    anti_patterns_ok, anti_pattern_errors = verify_anti_patterns(cs_files, src_dir)
    motion_guards_ok, motion_errors = verify_specific_motion_guards(src_dir)
    rgr07_ok, rgr07_errors = verify_rgr07_guards(src_dir)
    aie08_ok, aie08_errors = verify_aie08_guards(src_dir)
    hotfix_guards_ok, hotfix_errors = verify_v028_hotfix_guards(src_dir)
    aie13_rgr08_ok, aie13_rgr08_errors = verify_aie13_rgr08_guards(src_dir)
    aie14_ok, aie14_errors = verify_aie14_guards(src_dir)
    flt06_ok, flt06_errors = verify_flt06_guards(src_dir)
    flt11_ok, flt11_errors = verify_flt11_guards(src_dir)
    aie15_ok, aie15_errors = verify_aie15_guards(src_dir)
    v029_ok, v029_errors = verify_v029_invariants(src_dir)

    all_passed = invariants_ok and anti_patterns_ok and motion_guards_ok and rgr07_ok and aie08_ok and hotfix_guards_ok and aie13_rgr08_ok and aie14_ok and flt06_ok and flt11_ok and aie15_ok and v029_ok
    total_errors = len(invariant_errors) + len(anti_pattern_errors) + len(motion_errors) + len(rgr07_errors) + len(aie08_errors) + len(hotfix_errors) + len(aie13_rgr08_errors) + len(aie14_errors) + len(flt06_errors) + len(flt11_errors) + len(aie15_errors) + len(v029_errors)

    print(bold("\n" + "=" * 76))
    if all_passed:
        print(green(f" [PASS] All critical invariants verified. Zero forbidden anti-patterns detected."))
        print(green(f"        Codebase complies with GRAVEYARD.md and VISION_AND_ARCHITECTURE.md."))
        print(bold("=" * 76 + "\n"))
        return 0
    else:
        print(red(f" [FAIL] Static gatekeeper failed with {total_errors} violation(s). Build aborted!"))
        print(red(f"        Please consult GRAVEYARD.md to fix regression(s) before deploying."))
        print(bold("=" * 76 + "\n"))
        return 1


if __name__ == "__main__":
    sys.exit(main())
