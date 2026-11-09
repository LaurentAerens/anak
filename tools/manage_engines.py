#!/usr/bin/env python3
"""
Anak Chess Engine — Reference Engine Manager
Manages real, non-throttled reference UCI engines across calibrated CCRL rating tiers (1600 to 3350 Elo).
Provides multiple diverse engine options per tier (tactical, positional, classical, NNUE)
to prevent style over-fitting in tuning and weakness finding.
"""

import argparse
import json
import os
from pathlib import Path
import sys
from typing import Dict, List, Optional, Tuple

REPO_ROOT = Path(__file__).resolve().parent.parent
REGISTRY_FILE = REPO_ROOT / "tools" / "engines_registry.json"
ENGINES_DIR = REPO_ROOT / "tools" / "reference_engines"


def load_registry() -> List[dict]:
    """Loads all tiers from registry."""
    if not REGISTRY_FILE.exists():
        raise FileNotFoundError(f"Registry file not found at {REGISTRY_FILE}")
    with open(REGISTRY_FILE, "r", encoding="utf-8") as f:
        data = json.load(f)
    return data.get("tiers", [])


def get_all_engines() -> List[dict]:
    """Flattens tiers into a single list of all registered engines."""
    tiers = load_registry()
    flat = []
    for t in tiers:
        tier_val = t.get("tier", 0)
        for e in t.get("engines", []):
            entry = dict(e)
            entry["tier"] = tier_val
            flat.append(entry)
    return flat


def get_engines_for_tier(target_tier: int) -> List[dict]:
    """Returns all engine options registered for a specific tier (or closest tier)."""
    tiers = load_registry()
    if not tiers:
        return []

    # Find tier with smallest distance
    best_tier = min(tiers, key=lambda t: abs(t.get("tier", 0) - target_tier))
    tier_val = best_tier.get("tier", 0)
    result = []
    for e in best_tier.get("engines", []):
        entry = dict(e)
        entry["tier"] = tier_val
        result.append(entry)
    return result


def get_engine_by_tier(target_tier: int) -> Optional[dict]:
    """Returns the primary engine registered for a specific tier."""
    engines = get_engines_for_tier(target_tier)
    return engines[0] if engines else None


def get_engine_by_name(name: str) -> Optional[dict]:
    for e in get_all_engines():
        if e["name"].lower() == name.lower():
            return e
    return None


def get_installed_path(engine_entry: dict) -> Optional[Path]:
    ENGINES_DIR.mkdir(parents=True, exist_ok=True)
    binary_name = engine_entry.get("binary_name", "")
    target = ENGINES_DIR / binary_name
    if target.exists():
        return target
    candidates = list(ENGINES_DIR.rglob(binary_name))
    if candidates:
        return candidates[0]
    # Check fastchess/bin fallback (e.g. for stockfish.exe)
    fc_bin = REPO_ROOT / "tools" / "fastchess" / "bin" / binary_name
    if fc_bin.exists():
        return fc_bin
    return None


def list_engines(installed_only: bool = False):
    tiers = load_registry()
    print("\n" + "=" * 90)
    print("      ANAK MULTI-ENGINE REFERENCE LADDER (UNHAMSTRUNG REAL ENGINES)")
    print("=" * 90)

    for t in tiers:
        tier_num = t.get("tier", 0)
        desc = t.get("description", "")
        print(f"\n[Tier {tier_num}] {desc}")
        print(f"  {'Engine Name':<18} {'CCRL Elo':<10} {'Style / Philosophy':<30} {'Status':<12}")
        print("  " + "-" * 75)
        for e in t.get("engines", []):
            path = get_installed_path(e)
            status = "[Installed]" if path else "[Missing]"
            if installed_only and not path:
                continue
            print(f"  {e['name']:<18} {e['known_elo']:<10} {e.get('style', '-'):<30} {status:<12}")

    print("\n" + "=" * 90)
    print("Engines Directory: " + str(ENGINES_DIR))
    print("Drop engine executables into 'tools/reference_engines/' to activate them.")
    print("=" * 90 + "\n")


def main():
    parser = argparse.ArgumentParser(description="Anak Multi-Engine Reference Ladder Manager")
    parser.add_argument("action", choices=["list", "check", "installed"], default="list", nargs="?",
                        help="Action to perform (default: list)")
    args = parser.parse_args()

    if args.action in ("list", "installed"):
        list_engines(installed_only=(args.action == "installed"))
    elif args.action == "check":
        all_engines = get_all_engines()
        installed = [e for e in all_engines if get_installed_path(e)]
        missing = [e for e in all_engines if not get_installed_path(e)]
        print(f"[+] Total registered: {len(all_engines)} across {len(load_registry())} tiers.")
        print(f"[+] Installed: {len(installed)} | Missing: {len(missing)}")
        if missing:
            print("\nMissing binaries in tools/reference_engines/:")
            for m in missing:
                print(f"  * Tier {m['tier']} (Elo {m['known_elo']}): {m['name']} ({m['binary_name']}) - {m.get('style')}")


if __name__ == "__main__":
    main()
