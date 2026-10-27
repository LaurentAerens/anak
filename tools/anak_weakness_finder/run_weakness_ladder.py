"""
Automated Elo Ladder Runner for Anak Weakness Finder.
Executes stepped matches against calibrated Stockfish (SF 1600 -> +200 Elo per step)
with 10,000 games per tier until Anak's score drops below 50%.
Automatically streams results into DuckDB.
"""

import argparse
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

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
    openings_path: Path
) -> Path:
    """Runs a single fastchess match tier against calibrated Stockfish."""
    rounds = max(1, total_games // 2)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    pgn_output = RESULTS_DIR / f"weakness_ladder_sf{target_elo}_{timestamp}.pgn"
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)

    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = ANAK_EXE.name
    anak_dir = str(os.path.relpath(ANAK_EXE.parent, REPO_ROOT))
    opp_cmd = STOCKFISH_EXE.name
    opp_dir = str(os.path.relpath(STOCKFISH_EXE.parent, REPO_ROOT))
    openings_rel = str(os.path.relpath(openings_path, REPO_ROOT))
    pgn_rel = str(os.path.relpath(pgn_output, REPO_ROOT))

    opp_options = [
        f"option.Threads={threads}",
        f"option.Hash={hash_mb}",
        "option.UCI_LimitStrength=true",
        f"option.UCI_Elo={target_elo}",
    ]

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
        f"name=Stockfish-{target_elo}",
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
    print(f"[*] LAUNCHING TIER: Anak vs Stockfish {target_elo} ({rounds * 2} games, TC: {tc})")
    print("=" * 70)

    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        print(f"[!] Warning: fastchess exited with code {res.returncode}")

    return pgn_output


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
    max_elo: int = 3400
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
    print(f"  Start Elo:            {start_elo}")
    print(f"  Step Increment:       +{step_elo} Elo per tier")
    print(f"  Games Per Tier:       {games_per_tier} (paired color swaps)")
    print(f"  Stop Condition:       Score < {stop_threshold * 100:.0f}%")
    print(f"  Time Control:         {tc}")
    print(f"  Concurrency:          {concurrency} threads")
    print(f"  Opening Suite:        {openings_path.name}")
    print("=" * 75)

    while current_elo <= max_elo:
        pgn_file = run_tier_match(
            target_elo=current_elo,
            total_games=games_per_tier,
            tc=tc,
            concurrency=concurrency,
            hash_mb=hash_mb,
            threads=threads,
            openings_path=openings_path
        )

        # Ingest into DuckDB
        match_id = f"tier_sf{current_elo}_{int(time.time())}"
        games_ingested, _ = ingest_pgn_file(
            pgn_path=pgn_file,
            match_id=match_id,
            opponent_name=f"Stockfish-{current_elo}",
            stockfish_elo=current_elo,
            time_control=tc
        )

        # Query result from DuckDB
        row = conn.execute("""
            SELECT score, anak_wins, anak_losses, draws 
            FROM matches 
            WHERE match_id = ?
        """, (match_id,)).fetchone()

        if row:
            score, wins, losses, draws = row
            perf_pct = score * 100.0
            print(f"[+] Tier SF {current_elo} Finished: {wins}W - {losses}L - {draws}D ({perf_pct:.1f}%)")

            if score < stop_threshold:
                print("=" * 75)
                print(f"[!] LADDER CEILING REACHED AT SF {current_elo} (Score {perf_pct:.1f}% < {stop_threshold * 100:.0f}%)")
                print(f"    Halting ladder. Total peak established. Running deep analysis...")
                print("=" * 75)
                break
        else:
            print(f"[!] Warning: Could not retrieve score for {match_id}")

        current_elo += step_elo


def main():
    parser = argparse.ArgumentParser(description="Anak Weakness Finder — Stepped Elo Ladder Runner")
    parser.add_argument("--start-elo", type=int, default=1600, help="Initial Stockfish Elo (default: 1600)")
    parser.add_argument("--step-elo", type=int, default=200, help="Elo increment per tier (default: 200)")
    parser.add_argument("--games", type=int, default=10000, help="Total games per tier (default: 10000)")
    parser.add_argument("--stop-threshold", type=float, default=0.50, help="Stop ladder if score falls below this (default: 0.50)")
    parser.add_argument("--tc", type=str, default="1+0.01", help="Time control in seconds+inc (default: 1+0.01)")
    parser.add_argument("--concurrency", type=int, default=12, help="Number of concurrent games (default: 12)")
    parser.add_argument("--hash", type=int, default=32, help="Hash table MB per engine (default: 32)")
    parser.add_argument("--threads", type=int, default=1, help="Threads per engine (default: 1)")
    parser.add_argument("--openings", type=str, default=str(DEFAULT_OPENINGS), help="Opening book file path (default: openings.epd)")
    parser.add_argument("--max-elo", type=int, default=3400, help="Maximum Elo to test (default: 3400)")

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
        max_elo=args.max_elo
    )


if __name__ == "__main__":
    main()
