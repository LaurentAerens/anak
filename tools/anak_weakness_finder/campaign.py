"""
Single-Command Full-Chain Campaign Orchestrator for Anak Weakness Finder.
Runs ladder matches -> DuckDB streaming ingestion -> Full-Power Stockfish Oracle ->
analytics & visual reporting -> training dataset export -> search bug repro generation.
Includes persistent state management with automatic crash recovery and resume support.
"""

import argparse
import json
import os
import sys
import time
from pathlib import Path
from typing import Dict, Any, Optional

from .db_schema import DEFAULT_DB_PATH, get_connection, initialize_schema
from .run_weakness_ladder import run_tier_match, ensure_binaries, DEFAULT_OPENINGS, RESULTS_DIR
from .ingest_pgn import ingest_pgn_file
from .enrich_features import enrich_database_features
from .report_generator import generate_reports
from .export_training_fens import export_weakness_training_data
from .test_case_generator import generate_python_repro_script

CAMPAIGN_STATE_FILE = Path(__file__).resolve().parent / "campaign_state.json"


class CampaignManager:
    def __init__(self, state_file: Optional[Path] = None):
        self.state_file = state_file or CAMPAIGN_STATE_FILE
        self.state: Dict[str, Any] = self._load_state()

    def _load_state(self) -> Dict[str, Any]:
        if self.state_file.exists():
            try:
                with open(self.state_file, "r", encoding="utf-8") as f:
                    return json.load(f)
            except Exception as e:
                print(f"[!] Warning: Could not read {self.state_file.name} ({e}), starting fresh.")
        return {
            "campaign_id": f"campaign_{time.strftime('%Y%m%d_%H%M%S')}",
            "status": "idle",
            "current_elo": 1600,
            "completed_tiers": [],
            "created_at": time.strftime("%Y-%m-%d %H:%M:%S"),
            "last_updated": time.strftime("%Y-%m-%d %H:%M:%S")
        }

    def save_state(self):
        self.state["last_updated"] = time.strftime("%Y-%m-%d %H:%M:%S")
        self.state_file.parent.mkdir(parents=True, exist_ok=True)
        with open(self.state_file, "w", encoding="utf-8") as f:
            json.dump(self.state, f, indent=2)

    def reset_state(self):
        if self.state_file.exists():
            self.state_file.unlink()
        self.state = self._load_state()
        print("[+] Campaign state reset to initial baseline.")


def run_full_campaign(
    start_elo: int = 1600,
    step_elo: int = 200,
    games_per_tier: int = 10000,
    stop_threshold: float = 0.50,
    tc: str = "1+0.01",
    concurrency: int = 12,
    hash_mb: int = 32,
    threads: int = 1,
    openings_path: Path = DEFAULT_OPENINGS,
    max_elo: int = 3400,
    oracle_limit: Optional[int] = None,
    oracle_delta_threshold: float = 2.0,
    oracle_only_throws: bool = False,
    oracle_training_depth: int = 9,
    oracle_analysis_depth: int = 20,
    oracle_concurrency: int = 16,
    resume: bool = True,
    reset: bool = False,
    reprocess_only: bool = False,
    db_path: Optional[Path] = None
):
    """
    Executes the entire end-to-end testing, analysis, and datagen pipeline.
    Resilient to interruption: resumes from where it left off on crash/reboot.
    """
    manager = CampaignManager()
    if reset:
        manager.reset_state()
        resume = False
        target_db = db_path or DEFAULT_DB_PATH
        if target_db.exists():
            try:
                target_db.unlink()
                wal = target_db.with_name(target_db.name + ".wal")
                if wal.exists():
                    wal.unlink()
                print(f"[+] Deleted previous database: {target_db.name}")
            except Exception as e:
                print(f"[!] Warning: Could not delete database file ({e})")

    state = manager.state
    ensure_binaries()
    conn = get_connection(db_path)
    initialize_schema(conn)

    # Determine starting point for ladder
    completed_elos = {t["elo"] for t in state.get("completed_tiers", [])}
    current_elo = state.get("current_elo", start_elo)
    if resume and completed_elos:
        # Move to the highest completed + step_elo
        highest_completed = max(completed_elos)
        current_elo = max(current_elo, highest_completed + step_elo)
        print(f"[+] Resuming campaign from state file: Next target is Stockfish {current_elo}")
    else:
        current_elo = start_elo

    print("=" * 78)
    print("           ANAK WEAKNESS FINDER — FULL END-TO-END AUTOMATED CAMPAIGN")
    print("=" * 78)
    print(f"  Start Elo:            {current_elo} (Configured start: {start_elo})")
    print(f"  Step Increment:       +{step_elo} Elo per tier")
    print(f"  Games Per Tier:       {games_per_tier} (paired color swaps)")
    print(f"  Halt Condition:       Score < {stop_threshold * 100:.0f}%")
    print(f"  Time Control:         {tc}")
    print(f"  Concurrency:          {concurrency} threads")
    print(f"  State Persistence:    {manager.state_file}")
    print("=" * 78)

    state["status"] = "running_ladder"
    state["config"] = {
        "start_elo": start_elo,
        "step_elo": step_elo,
        "games_per_tier": games_per_tier,
        "stop_threshold": stop_threshold,
        "tc": tc
    }
    manager.save_state()

    ladder_halted = False
    ceiling_elo = None

    if resume and state.get("completed_tiers"):
        for t in state["completed_tiers"]:
            if t.get("score", 1.0) < stop_threshold:
                print(f"[*] Previous campaign already hit ceiling at Stockfish {t['elo']} ({t.get('perf_percent', 0):.1f}% < {stop_threshold*100:.0f}%).")
                ladder_halted = True
                ceiling_elo = t["elo"]
                break

    # PHASE 1: Elo Ladder Loop
    if reprocess_only or ladder_halted:
        print("\n[*] Ladder already completed or reprocess flag set: Skipping match ladder games.")
    else:
        while current_elo <= max_elo:
            if current_elo in completed_elos and resume:
                print(f"[*] Tier SF {current_elo} already completed in previous run. Skipping.")
                current_elo += step_elo
                continue

            print(f"\n>>> [CAMPAIGN STEP 1/5] Checking Ladder Match for Stockfish {current_elo}...")
            state["current_elo"] = current_elo
            manager.save_state()

            # Check if an existing PGN archive already exists for this tier (e.g. from interrupted run)
            existing_pgns = sorted(
                RESULTS_DIR.glob(f"weakness_ladder_sf{current_elo}_*.pgn"),
                key=lambda p: p.stat().st_mtime,
                reverse=True
            )

            pgn_output = None
            if existing_pgns and resume:
                candidate = existing_pgns[0]
                # Verify file has substantial size (> 500 KB)
                if candidate.stat().st_size > 500 * 1024:
                    print(f"[+] Found completed match archive: {candidate.name} ({candidate.stat().st_size / (1024*1024):.2f} MB)")
                    print(f"    Skipping match replay and proceeding directly to ingestion!")
                    pgn_output = candidate

            if pgn_output is None:
                pgn_output = run_tier_match(
                    target_elo=current_elo,
                    total_games=games_per_tier,
                    tc=tc,
                    concurrency=concurrency,
                    hash_mb=hash_mb,
                    threads=threads,
                    openings_path=openings_path
                )

            # Ingestion into DuckDB
            print(f"\n>>> [CAMPAIGN STEP 2/5] Ingesting Tier SF {current_elo} PGN into DuckDB...")
            match_id = f"tier_sf{current_elo}_{int(time.time())}"
            games_ingested, plies_ingested = ingest_pgn_file(
                pgn_path=pgn_output,
                match_id=match_id,
                opponent_name=f"Stockfish-{current_elo}",
                stockfish_elo=current_elo,
                time_control=tc,
                db_path=db_path
            )

            # Check performance
            row = conn.execute("""
                SELECT score, anak_wins, anak_losses, draws 
                FROM matches 
                WHERE match_id = ?
            """, (match_id,)).fetchone()

            score, wins, losses, draws = row if row else (0.0, 0, 0, 0)
            perf_pct = score * 100.0

            state["completed_tiers"].append({
                "elo": current_elo,
                "match_id": match_id,
                "pgn_file": str(pgn_output),
                "games": games_ingested,
                "score": score,
                "wins": wins,
                "losses": losses,
                "draws": draws,
                "perf_percent": perf_pct
            })
            completed_elos.add(current_elo)
            manager.save_state()

            print(f"[+] Tier SF {current_elo} Result: {wins}W - {losses}L - {draws}D ({perf_pct:.1f}%)")

            if score < stop_threshold:
                print("=" * 78)
                print(f"[!] CEILING REACHED: Anak scored {perf_pct:.1f}% vs Stockfish {current_elo} (< {stop_threshold*100:.0f}%)")
                print("    Halting ladder matches. Transitioning to Deep Diagnostics & Datagen...")
                print("=" * 78)
                ladder_halted = True
                ceiling_elo = current_elo
                break

            current_elo += step_elo

    # PHASE 2: Data Enrichment & Stockfish Oracle Audit (Stage 1)
    print(f"\n>>> [CAMPAIGN STEP 3/5] Running Feature Generation & Oracle Diagnostics...")
    state["status"] = "enriching_features"
    manager.save_state()

    enrich_database_features(
        db_path=db_path,
        run_oracle=(oracle_limit is None or oracle_limit > 0),
        oracle_limit=oracle_limit,
        oracle_delta_threshold=oracle_delta_threshold,
        oracle_only_throws=oracle_only_throws,
        oracle_training_depth=oracle_training_depth,
        oracle_analysis_depth=oracle_analysis_depth,
        oracle_concurrency=oracle_concurrency
    )

    # PHASE 3: Pure Declarative Reporting (Stage 2)
    print(f"\n>>> [CAMPAIGN STEP 4/5] Generating Diagnostic Markdown & HTML Dashboards...")
    state["status"] = "generating_reports"
    manager.save_state()

    out_md = Path("weakness_report.md")
    out_html = Path("weakness_report.html")
    generate_reports(db_path=db_path, out_md=out_md, out_html=out_html)

    # PHASE 4: Actionable Datagen & Search Bug Repro Export
    print(f"\n>>> [CAMPAIGN STEP 5/5] Exporting Targeted Training Datasets & Search Bug Repros...")
    
    # 1. Export ST9 weakness training data (standard EPD format with centipawns ce)
    out_epd = Path("training/data/weakness_training_st9.epd")
    export_weakness_training_data(
        output_path=out_epd,
        export_format="epd",
        db_path=db_path
    )

    # 2. Export search bug regression repro script
    out_repro = Path("tools/anak_weakness_finder/repro_search_throws.py")
    generate_python_repro_script(
        output_path=out_repro,
        limit=25,
        db_path=db_path
    )

    state["status"] = "completed"
    state["ceiling_elo"] = ceiling_elo
    manager.save_state()

    print("\n" + "=" * 78)
    print("                     CAMPAIGN COMPLETED SUCCESSFULLY!")
    print("=" * 78)
    print(f"  [+] Match History:        Stored in DuckDB ({DEFAULT_DB_PATH.name})")
    print(f"  [+] Markdown Report:      file:///{out_md.resolve()}")
    print(f"  [+] Visual Dashboard:     file:///{out_html.resolve()}")
    print(f"  [+] NNUE Training Data:   {out_epd} (ST9 depth labels)")
    print(f"  [+] Search Bug Repro:     {out_repro}")
    print("=" * 78)


def main():
    parser = argparse.ArgumentParser(
        description="Anak Weakness Finder — Single-Command Full-Chain Campaign Runner"
    )
    parser.add_argument("--start-elo", type=int, default=1600, help="Initial Stockfish Elo (default: 1600)")
    parser.add_argument("--step-elo", type=int, default=200, help="Elo increment per tier (default: 200)")
    parser.add_argument("--games", type=int, default=10000, help="Games per tier (default: 10000)")
    parser.add_argument("--stop-threshold", type=float, default=0.50, help="Stop score threshold (default: 0.50)")
    parser.add_argument("--tc", type=str, default="1+0.01", help="Time control (default: 1+0.01)")
    parser.add_argument("--concurrency", type=int, default=12, help="Concurrency (default: 12)")
    parser.add_argument("--hash", type=int, default=32, help="Hash table MB per engine (default: 32)")
    parser.add_argument("--threads", type=int, default=1, help="Threads per engine (default: 1)")
    parser.add_argument("--openings", type=str, default=str(DEFAULT_OPENINGS), help="Openings file")
    parser.add_argument("--max-elo", type=int, default=3400, help="Maximum Elo to test")
    parser.add_argument("--oracle-limit", type=lambda x: None if x.lower() in ("none", "0", "unlimited") else int(x), default=None, help="Max unique positions to audit with Oracle (default: None = unlimited, every qualifying threshold position)")
    parser.add_argument("--oracle-delta-threshold", type=float, default=2.0, help="Eval drop threshold in pawns where engine went very wrong (default: 2.0)")
    parser.add_argument("--oracle-only-throws", action="store_true", default=False, help="Only evaluate blown leads (eval >= +1.0 to loss)")
    parser.add_argument("--oracle-training-depth", type=int, default=9, help="Oracle depth for training data (default: 9)")
    parser.add_argument("--oracle-analysis-depth", type=int, default=20, help="Oracle depth for diagnostics (default: 20)")
    parser.add_argument("--oracle-concurrency", type=int, default=16, help="Concurrency for Oracle analysis (default: 16)")
    parser.add_argument("--resume", action="store_true", default=True, help="Auto-resume from previous state (default: True)")
    parser.add_argument("--no-resume", action="store_false", dest="resume", help="Do not resume, start new campaign")
    parser.add_argument("--reset", action="store_true", default=False, help="Wipe campaign state and start clean")
    parser.add_argument("--reprocess-only", "--analytics-only", action="store_true", default=False, dest="reprocess_only",
                        help="Skip playing match games and only reprocess existing data & regenerate reports")

    args = parser.parse_args()
    run_full_campaign(
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
        oracle_limit=args.oracle_limit,
        oracle_delta_threshold=args.oracle_delta_threshold,
        oracle_only_throws=args.oracle_only_throws,
        oracle_training_depth=args.oracle_training_depth,
        oracle_analysis_depth=args.oracle_analysis_depth,
        oracle_concurrency=args.oracle_concurrency,
        resume=args.resume,
        reset=args.reset,
        reprocess_only=args.reprocess_only
    )


if __name__ == "__main__":
    main()
