"""
Automated Elo Ladder Runner for Anak Weakness Finder.
Executes stepped matches against calibrated Stockfish (SF 1600 -> +200 Elo per step)
with 10,000 games per tier until Anak's score drops below 50%.
Automatically streams results into DuckDB.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
from pathlib import Path
from typing import Dict, List, Optional, Tuple

# Paths
MODULE_DIR = Path(__file__).resolve().parent
REPO_ROOT = MODULE_DIR.parent.parent
FASTCHESS_DIR = REPO_ROOT / "tools" / "fastchess"
BIN_DIR = FASTCHESS_DIR / "bin"
RESULTS_DIR = FASTCHESS_DIR / "results"
DEFAULT_OPENINGS = FASTCHESS_DIR / "openings.epd"
FASTCHESS_EXE = BIN_DIR / ("fastchess.exe" if os.name == "nt" else "fastchess")
STOCKFISH_EXE = BIN_DIR / ("stockfish.exe" if os.name == "nt" else "stockfish")
ANAK_EXE = BIN_DIR / "anak" / ("anak.exe" if os.name == "nt" else "anak")
ENGINES_DIR = REPO_ROOT / "tools" / "reference_engines"

# Import reference engine registry
if str(REPO_ROOT / "tools") not in sys.path:
    sys.path.insert(0, str(REPO_ROOT / "tools"))
try:
    from manage_engines import get_engines_for_tier, get_engine_by_tier, get_installed_path, load_registry
except ImportError:
    get_engines_for_tier = None
    get_engine_by_tier = None
    get_installed_path = None
    load_registry = None

from .ingest_pgn import ingest_pgn_file
from .db_schema import get_connection, initialize_schema


def ensure_binaries():
    """Validates that anak, fastchess, and stockfish binaries exist."""
    if not FASTCHESS_EXE.exists():
        raise FileNotFoundError(f"fastchess executable not found at {FASTCHESS_EXE}. Run tools/fastchess/run_elo_bench.py to auto-download.")
    if not STOCKFISH_EXE.exists():
        raise FileNotFoundError(f"stockfish executable not found at {STOCKFISH_EXE}. Run tools/fastchess/run_elo_bench.py to auto-download.")
    if not ANAK_EXE.exists():
        raise FileNotFoundError(f"Anak executable not found at {ANAK_EXE}. Please run 'dotnet publish' on Anak.Protocol.")


def run_tier_match(
    target_elo: int,
    total_games: int,
    tc: str,
    concurrency: int,
    hash_mb: int,
    threads: int,
    openings_path: Path,
    opponent_type: str = "reference",
    specific_engine: Optional[dict] = None,
) -> Tuple[Path, str, int]:
    """Runs a single fastchess match tier against a real reference engine or calibrated Stockfish."""
    rounds = max(1, total_games // 2)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)

    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = ANAK_EXE.name
    anak_dir = str(os.path.relpath(ANAK_EXE.parent, REPO_ROOT))
    openings_rel = str(os.path.relpath(openings_path, REPO_ROOT))

    opp_name = None
    actual_elo = target_elo
    opp_cmd = None
    opp_dir = None
    opp_options = []

    # Check for real reference engine in registry
    ref_engine = specific_engine
    if ref_engine is None and opponent_type == "reference" and get_engine_by_tier:
        ref_engine = get_engine_by_tier(target_elo)

    if ref_engine and get_installed_path:
        installed_bin = get_installed_path(ref_engine)
        if installed_bin:
            opp_name = ref_engine["name"]
            actual_elo = ref_engine["known_elo"]
            opp_cmd = installed_bin.name
            opp_dir = str(os.path.relpath(installed_bin.parent, REPO_ROOT))
            opp_options.append(f"option.Hash={hash_mb}")
            for k, v in ref_engine.get("options", {}).items():
                opp_options.append(f"option.{k}={v}")
            style = ref_engine.get("style", "Standard")
            print(f"[+] Opponent: {opp_name} (CCRL Elo ~{actual_elo} | Style: {style})")

    # Fallback to calibrated Stockfish if reference engine not installed or stockfish explicitly requested
    if not opp_cmd:
        opp_name = f"Stockfish-{target_elo}"
        opp_cmd = STOCKFISH_EXE.name
        opp_dir = str(os.path.relpath(STOCKFISH_EXE.parent, REPO_ROOT))
        opp_options = [
            f"option.Threads={threads}",
            f"option.Hash={hash_mb}",
            "option.UCI_LimitStrength=true",
            f"option.UCI_Elo={target_elo}",
        ]
        if opponent_type == "reference":
            print(f"[!] Note: No reference engine binary found for ~{target_elo} Elo in tools/reference_engines/.")
            print(f"    Falling back to Stockfish-{target_elo} (Run 'python tools/manage_engines.py' for engine list).")

    clean_slug = re.sub(r"[^a-zA-Z0-9_]", "_", opp_name.lower())
    pgn_output = RESULTS_DIR / f"weakness_ladder_{clean_slug}_{timestamp}.pgn"
    pgn_rel = str(os.path.relpath(pgn_output, REPO_ROOT))

    cmd = [
        fastchess_cmd,
        "-engine",
        f"cmd={anak_cmd}",
        f"dir={anak_dir}",
        "name=Anak-Dev",
        f"option.Threads={threads}",
        f"option.Hash={hash_mb}",
        "-engine",
        f"cmd={opp_cmd}",
        f"dir={opp_dir}",
        f"name={opp_name}",
    ] + opp_options + [
        "-each",
        f"tc={tc}",
        "-openings",
        f"file={openings_rel}",
        "format=epd" if openings_rel.endswith(".epd") else "format=pgn",
        "order=random",
        "-rounds",
        str(rounds),
        "-repeat",
        "-concurrency",
        str(concurrency),
        "-draw",
        "movenumber=40",
        "movecount=8",
        "score=8",
        "-resign",
        "movecount=3",
        "score=600",
        "-pgnout",
        f"file={pgn_rel}",
    ]

    print("=" * 70)
    print(f"[*] LAUNCHING TIER MATCH: Anak vs {opp_name} (Elo ~{actual_elo}) ({rounds * 2} games, TC: {tc})")
    print("=" * 70)

    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        print(f"[!] Warning: fastchess exited with code {res.returncode}")

    return pgn_output, opp_name, actual_elo


def run_ladder(
    start_elo: int = 1600,
    step_elo: int = 200,
    games_per_tier: int = 10000,
    stop_threshold: float = 0.50,
    tc: str = "1+0.01",
    concurrency: int = 12,
    hash_mb: int = 32,
    threads: int = 1,
    openings_path: Path = DEFAULT_OPENINGS,
    max_elo: int = 3700,
    opponent_type: str = "reference",
):
    """
    Orchestrates the entire Elo ladder:
    Advances by step_elo until score drops below stop_threshold.
    """
    ensure_binaries()
    conn = get_connection()
    initialize_schema(conn)

    current_elo = start_elo

    print("=" * 75)
    print("              ANAK AUTOMATED ELO WEAKNESS LADDER")
    print("=" * 75)
    print(f"  Opponent Pool:        {'Real Reference Engines' if opponent_type == 'reference' else 'Stockfish (UCI_Elo)'}")
    print(f"  Start Elo:            {start_elo}")
    print(f"  Step Increment:       +{step_elo} Elo per tier")
    print(f"  Games Per Tier:       {games_per_tier} (paired color swaps)")
    print(f"  Stop Condition:       Score < {stop_threshold * 100:.0f}%")
    print(f"  Time Control:         {tc}")
    print(f"  Concurrency:          {concurrency} threads")
    print(f"  Opening Suite:        {openings_path.name}")
    print("=" * 75)

    while current_elo <= max_elo:
        # Check installed engines for this tier to run a mini-gauntlet
        installed_in_tier = []
        if opponent_type == "reference" and get_engines_for_tier and get_installed_path:
            for eng in get_engines_for_tier(current_elo):
                bin_path = get_installed_path(eng)
                if bin_path:
                    installed_in_tier.append(eng)

        target_engines = installed_in_tier if installed_in_tier else [None]
        games_per_target = max(2, games_per_tier // len(target_engines))
        print(f"\n[>>>] BEGINNING TIER {current_elo} ELO ({len(target_engines)} engine{'s' if len(target_engines) > 1 else ''} in pool, {games_per_target} games each)")

        tier_wins, tier_losses, tier_draws, tier_games = 0, 0, 0, 0

        for target_engine in target_engines:
            pgn_file, opp_name, actual_elo = run_tier_match(
                target_elo=current_elo,
                total_games=games_per_target,
                tc=tc,
                concurrency=concurrency,
                hash_mb=hash_mb,
                threads=threads,
                openings_path=openings_path,
                opponent_type=opponent_type,
                specific_engine=target_engine,
            )

            # Ingest into DuckDB
            clean_opp_slug = re.sub(r"[^a-zA-Z0-9_]", "_", opp_name.lower())
            match_id = f"tier_{clean_opp_slug}_{int(time.time())}"
            games_ingested, _ = ingest_pgn_file(
                pgn_path=pgn_file,
                match_id=match_id,
                opponent_name=opp_name,
                stockfish_elo=actual_elo,
                time_control=tc
            )

            # Query result from DuckDB
            row = conn.execute("""
                SELECT score, anak_wins, anak_losses, draws 
                FROM matches 
                WHERE match_id = ?
            """, (match_id,)).fetchone()

            if row:
                sub_score, w, l, d = row
                tier_wins += w
                tier_losses += l
                tier_draws += d
                tier_games += (w + l + d)
                print(f"  -> Match vs {opp_name}: {w}W - {l}L - {d}D ({sub_score*100.0:.1f}%)")

        tier_pts = tier_wins + 0.5 * tier_draws
        tier_score = (tier_pts / tier_games) if tier_games > 0 else 0.0
        perf_pct = tier_score * 100.0

        print("-" * 75)
        print(f"[+] TIER {current_elo} AGGREGATE: {tier_wins}W - {tier_losses}L - {tier_draws}D ({perf_pct:.1f}% across {tier_games} games)")
        print("-" * 75)

        if tier_score < stop_threshold:
            print("=" * 75)
            print(f"[!] LADDER CEILING REACHED AT TIER {current_elo} (Score {perf_pct:.1f}% < {stop_threshold * 100:.0f}%)")
            print(f"    Halting ladder. Total peak established. Running deep analysis...")
            print("=" * 75)
            break

        current_elo += step_elo


def main():
    parser = argparse.ArgumentParser(description="Anak Weakness Finder — Stepped Elo Ladder Runner")
    parser.add_argument("--start-elo", type=int, default=1600, help="Initial Elo tier (default: 1600)")
    parser.add_argument("--step-elo", type=int, default=200, help="Elo increment per tier (default: 200)")
    parser.add_argument("--games", type=int, default=10000, help="Total games per tier (default: 10000)")
    parser.add_argument("--stop-threshold", type=float, default=0.50, help="Stop ladder if score falls below this (default: 0.50)")
    parser.add_argument("--tc", type=str, default="1+0.01", help="Time control in seconds+inc (default: 1+0.01)")
    parser.add_argument("--concurrency", type=int, default=12, help="Number of concurrent games (default: 12)")
    parser.add_argument("--hash", type=int, default=32, help="Hash table MB per engine (default: 32)")
    parser.add_argument("--threads", type=int, default=1, help="Threads per engine (default: 1)")
    parser.add_argument("--openings", type=str, default=str(DEFAULT_OPENINGS), help="Opening book file path (default: openings.epd)")
    parser.add_argument("--max-elo", type=int, default=3700, help="Maximum Elo to test (default: 3700)")
    parser.add_argument("--opponent-pool", choices=["reference", "stockfish"], default="reference",
                        help="Opponent engine pool: 'reference' for real unhamstrung engines, 'stockfish' for throttled SF (default: reference)")

    args = parser.parse_args()
    run_ladder(
        start_elo=args.start_elo,
        step_elo=args.step_elo,
        games_per_tier=args.games,
        stop_threshold=args.stop_threshold,
        tc=args.tc,
        concurrency=args.concurrency,
        hash_mb=args.hash,
        threads=args.threads,
        openings_path=Path(args.openings),
        max_elo=args.max_elo,
        opponent_type=args.opponent_pool,
    )


if __name__ == "__main__":
    main()
