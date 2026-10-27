"""
Runs a 10,000-game validation set of Anak against Stockfish-2200,
ingests results into DuckDB, and calculates the before/after delta.
"""

import os
import subprocess
import sys
import time
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
sys.path.insert(0, str(REPO_ROOT))

from tools.anak_weakness_finder.run_weakness_ladder import ensure_binaries, RESULTS_DIR, FASTCHESS_EXE, STOCKFISH_EXE, ANAK_EXE, DEFAULT_OPENINGS
from tools.anak_weakness_finder.ingest_pgn import ingest_pgn_file
from tools.anak_weakness_finder.db_schema import get_connection, initialize_schema
from tools.anak_weakness_finder.enrich_features import enrich_database_features
from tools.anak_weakness_finder.report_generator import generate_reports


def run_sf2200_benchmark(total_games: int = 10000, concurrency: int = 14, tc: str = "1+0.01"):
    ensure_binaries()
    conn = get_connection()
    initialize_schema(conn)

    rounds = max(1, total_games // 2)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    pgn_output = RESULTS_DIR / f"validation_sf2200_{timestamp}.pgn"
    RESULTS_DIR.mkdir(parents=True, exist_ok=True)

    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = ANAK_EXE.name
    anak_dir = str(os.path.relpath(ANAK_EXE.parent, REPO_ROOT))
    opp_cmd = STOCKFISH_EXE.name
    opp_dir = str(os.path.relpath(STOCKFISH_EXE.parent, REPO_ROOT))
    openings_rel = str(os.path.relpath(DEFAULT_OPENINGS, REPO_ROOT))
    pgn_rel = str(os.path.relpath(pgn_output, REPO_ROOT))

    opp_options = [
        "option.Threads=1",
        "option.Hash=32",
        "option.UCI_LimitStrength=true",
        "option.UCI_Elo=2200",
    ]

    anak_options = [
        "option.Threads=1",
        "option.Hash=32",
        "option.SyzygyPath=syzygy",
    ]

    cmd = [
        fastchess_cmd,
        "-engine",
        f"cmd={anak_cmd}",
        f"dir={anak_dir}",
        "name=Anak-Dev",
    ] + anak_options + [
        "-engine",
        f"cmd={opp_cmd}",
        f"dir={opp_dir}",
        "name=Stockfish-2200",
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

    print("=" * 75)
    print(f"[*] LAUNCHING VALIDATION MATCH: Anak-Dev vs Stockfish-2200 ({rounds * 2} games)")
    print(f"    Concurrency: {concurrency} workers | TC: {tc} | Output: {pgn_rel}")
    print("=" * 75)
    sys.stdout.flush()

    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        print(f"[!] Warning: fastchess exited with code {res.returncode}")

    # Ingest into DuckDB
    match_id = f"validation_sf2200_{timestamp}"
    print(f"[*] Ingesting {pgn_output.name} into DuckDB (Match ID: {match_id})...")
    ingest_pgn_file(
        pgn_path=pgn_output,
        match_id=match_id,
        opponent_name="Stockfish-2200",
        stockfish_elo=2200,
        time_control=tc
    )

    # Re-enrich database and generate updated diagnostic reports
    print("[*] Re-enriching database features and updating weakness reports...")
    enrich_database_features(run_oracle=False)
    generate_reports()

    # Print summary
    row = conn.execute("""
        SELECT score, anak_wins, anak_losses, draws 
        FROM matches 
        WHERE match_id = ?
    """, (match_id,)).fetchone()

    if row:
        score, wins, losses, draws = row
        print("\n" + "=" * 75)
        print("          VALIDATION RESULT: ANAK-DEV VS STOCKFISH-2200")
        print("=" * 75)
        print(f"  Total Games:    {wins + losses + draws:,}")
        print(f"  Score:          {score * 100:.2f}% (Baseline was 52.70%)")
        print(f"  Wins / Losses / Draws: {wins:,} / {losses:,} / {draws:,}")
        delta = (score * 100.0) - 52.70
        print(f"  Delta:          {delta:+.2f}%")
        print("=" * 75)


if __name__ == "__main__":
    concurrency = int(sys.argv[1]) if len(sys.argv) > 1 else 14
    games = int(sys.argv[2]) if len(sys.argv) > 2 else 10000
    run_sf2200_benchmark(total_games=games, concurrency=concurrency)
