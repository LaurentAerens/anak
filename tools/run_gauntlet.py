#!/usr/bin/env python3
"""
Anak Chess Engine — Automated Reference Ladder Gauntlet Runner
Deeply integrates:
  1. Elo Benchmarking (Normalized Elo, Logistic Elo, & Bradley-Terry Anchor MLE via elo_kit.py)
  2. Weakness Finder (Vectorized DuckDB ingestion, blunder detection, & ECO breakdown)
  3. Opening Book Tuning (Reinforcement Learning credit updates & refutation pruning on book.bin)
"""

import argparse
from datetime import datetime
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time
from typing import Dict, List, Optional, Tuple

# Paths
REPO_ROOT = Path(__file__).resolve().parent.parent
TOOLS_DIR = REPO_ROOT / "tools"
FASTCHESS_DIR = TOOLS_DIR / "fastchess"
BIN_DIR = FASTCHESS_DIR / "bin"
RESULTS_DIR = FASTCHESS_DIR / "results"
DEFAULT_OPENINGS = FASTCHESS_DIR / "openings.epd"
FASTCHESS_EXE = BIN_DIR / ("fastchess.exe" if os.name == "nt" else "fastchess")
STOCKFISH_EXE = BIN_DIR / ("stockfish.exe" if os.name == "nt" else "stockfish")
ANAK_EXE = BIN_DIR / "anak" / ("anak.exe" if os.name == "nt" else "anak")
DEFAULT_BOOK = REPO_ROOT / "book.bin"
DUCKDB_PATH = TOOLS_DIR / "anak_weakness_finder" / "weakness_finder.duckdb"

# Path setup for modular tool imports
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
if str(FASTCHESS_DIR) not in sys.path:
    sys.path.insert(0, str(FASTCHESS_DIR))

# Import tools
from manage_engines import (
    load_registry,
    get_all_engines,
    get_engine_by_name,
    get_installed_path,
    get_engines_for_tier,
)
from fastchess.elo_kit import (
    calibrate_anchor_elo,
    calculate_trinomial_nelo,
    calculate_pentanomial_nelo,
    calculate_logistic_elo,
    parse_fastchess_pgn,
)
from anak_weakness_finder.ingest_pgn import ingest_pgn_file
from anak_weakness_finder.db_schema import get_connection, initialize_schema
from tuning.tune_book import PolyGlotBookTable, ReinforcementBookTuner


def ensure_binaries(skip_build: bool = False):
    """Validates that anak and fastchess exist."""
    if not FASTCHESS_EXE.exists():
        raise FileNotFoundError(
            f"fastchess executable not found at {FASTCHESS_EXE}. Run tools/fastchess/run_elo_bench.py to download."
        )

    if not skip_build or not ANAK_EXE.exists():
        print("[+] Building and publishing current Anak Dev build (Release) ...")
        cmd = [
            "dotnet",
            "publish",
            str(REPO_ROOT / "src" / "Anak.Protocol" / "Anak.Protocol.csproj"),
            "-c",
            "Release",
            "-o",
            str(ANAK_EXE.parent),
        ]
        res = subprocess.run(cmd, cwd=str(REPO_ROOT))
        if res.returncode != 0:
            raise RuntimeError("Failed to build Anak. Check dotnet SDK installation.")

    if not ANAK_EXE.exists():
        raise FileNotFoundError(f"Anak executable not found at {ANAK_EXE}.")


def run_single_gauntlet_match(
    opp_entry: dict,
    opp_bin: Path,
    games: int,
    tc: str,
    concurrency: int,
    hash_mb: int,
    threads: int,
    openings_path: Path,
    out_dir: Path,
    is_sf_calibrated: bool = False,
    calibrated_elo: int = 2400,
) -> Tuple[Path, int, int, int]:
    """
    Executes a fastchess paired match between Anak-Dev and an opponent engine.
    Returns (pgn_path, wins, losses, draws).
    """
    rounds = max(1, games // 2)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    clean_name = re.sub(r"[^a-zA-Z0-9_]", "_", opp_entry["name"].lower())
    pgn_path = out_dir / f"gauntlet_anak_vs_{clean_name}_{timestamp}.pgn"

    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = ANAK_EXE.name
    anak_dir = str(os.path.relpath(ANAK_EXE.parent, REPO_ROOT))
    opp_cmd = opp_bin.name
    opp_dir = str(os.path.relpath(opp_bin.parent, REPO_ROOT))
    openings_rel = str(os.path.relpath(openings_path, REPO_ROOT))
    pgn_rel = str(os.path.relpath(pgn_path, REPO_ROOT))

    opp_options = [f"option.Hash={hash_mb}"]
    if is_sf_calibrated:
        opp_options.extend([
            f"option.Threads={threads}",
            "option.UCI_LimitStrength=true",
            f"option.UCI_Elo={calibrated_elo}",
        ])
    else:
        for k, v in opp_entry.get("options", {}).items():
            opp_options.append(f"option.{k}={v}")

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
        f"name={opp_entry['name']}",
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

    proc = subprocess.Popen(
        cmd,
        cwd=str(REPO_ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
    )

    wins, losses, draws = 0, 0, 0
    results_pattern = re.compile(r"Games:\s*(\d+),\s*Wins:\s*(\d+),\s*Losses:\s*(\d+),\s*Draws:\s*(\d+)")

    for line in proc.stdout:
        print("  " + line, end="")
        m = results_pattern.search(line)
        if m:
            wins = int(m.group(2))
            losses = int(m.group(3))
            draws = int(m.group(4))

    proc.wait()
    return pgn_path, wins, losses, draws


class GauntletRunner:
    """Orchestrates multi-opponent gauntlets across the reference engine ladder."""

    def __init__(
        self,
        games_per_opponent: int = 10,
        tc: str = "1+0.01",
        concurrency: int = 4,
        hash_mb: int = 64,
        threads: int = 1,
        openings_path: Path = DEFAULT_OPENINGS,
        min_tier: int = 1600,
        max_tier: int = 3700,
        specific_engines: Optional[List[str]] = None,
        fallback_sf: bool = False,
        tune_book: bool = True,
        ingest_db: bool = True,
        book_path: Path = DEFAULT_BOOK,
    ):
        self.games_per_opp = games_per_opponent
        self.tc = tc
        self.concurrency = concurrency
        self.hash_mb = hash_mb
        self.threads = threads
        self.openings_path = openings_path
        self.min_tier = min_tier
        self.max_tier = max_tier
        self.specific_engines = specific_engines
        self.fallback_sf = fallback_sf
        self.tune_book_enabled = tune_book
        self.ingest_db_enabled = ingest_db
        self.book_path = book_path

        self.match_results: List[dict] = []
        self.total_wins = 0
        self.total_losses = 0
        self.total_draws = 0
        self.total_games = 0

    def resolve_gauntlet_pool(self) -> List[Tuple[dict, Path, bool, int]]:
        """
        Discovers all valid opponent engines matching tier filters and installed binaries.
        Returns list of (engine_dict, binary_path, is_sf_calibrated, effective_elo).
        """
        all_engines = get_all_engines()
        pool: List[Tuple[dict, Path, bool, int]] = []

        if self.specific_engines:
            targets = [s.strip().lower() for s in self.specific_engines]
            for eng in all_engines:
                if any(t in eng["name"].lower() for t in targets):
                    bin_path = get_installed_path(eng)
                    if bin_path:
                        pool.append((eng, bin_path, False, eng["known_elo"]))
                    else:
                        print(f"[!] Warning: Requested engine '{eng['name']}' has no installed binary. Skipping.")
            return pool

        # Collect installed engines in range
        tiers = load_registry()
        for t in tiers:
            tier_val = t.get("tier", 0)
            if tier_val < self.min_tier or tier_val > self.max_tier:
                continue

            tier_engines = t.get("engines", [])
            installed_tier = []
            for e in tier_engines:
                bin_path = get_installed_path(e)
                if bin_path:
                    installed_tier.append((e, bin_path, False, e["known_elo"]))

            if installed_tier:
                pool.extend(installed_tier)
            elif self.fallback_sf and STOCKFISH_EXE.exists():
                sf_entry = {
                    "name": f"Stockfish-{tier_val}",
                    "known_elo": tier_val,
                    "author": "Stockfish Developers",
                    "style": "Calibrated UCI_Elo",
                    "binary_name": STOCKFISH_EXE.name,
                    "options": {},
                    "tier": tier_val,
                }
                pool.append((sf_entry, STOCKFISH_EXE, True, tier_val))

        return pool

    def run(self):
        """Executes the full gauntlet tournament."""
        RESULTS_DIR.mkdir(parents=True, exist_ok=True)
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        run_out_dir = RESULTS_DIR / f"gauntlet_{timestamp}"
        run_out_dir.mkdir(parents=True, exist_ok=True)

        pool = self.resolve_gauntlet_pool()
        if not pool:
            print("[-] No opponent engines found for gauntlet run.")
            print("    Check tools/manage_engines.py to inspect available and installed reference engines.")
            return

        total_scheduled_games = len(pool) * self.games_per_opp

        print("\n" + "=" * 80)
        print("          ANAK AUTOMATED REFERENCE LADDER GAUNTLET")
        print("=" * 80)
        print(f"  Candidate:             Anak (Current Dev Build)")
        print(f"  Opponent Pool:         {len(pool)} engine(s) across Tiers {self.min_tier}–{self.max_tier}")
        print(f"  Games Per Opponent:    {self.games_per_opp} ({total_scheduled_games} total games)")
        print(f"  Time Control:          {self.tc}")
        print(f"  Concurrency:           {self.concurrency} threads")
        print(f"  Opening Suite:         {self.openings_path.name}")
        print(f"  Weakness DB Ingest:    {'Enabled' if self.ingest_db_enabled else 'Disabled'}")
        print(f"  RL Opening Book Tune:  {'Enabled' if self.tune_book_enabled else 'Disabled'}")
        print("=" * 80 + "\n")

        # Database connection if enabled
        db_conn = None
        if self.ingest_db_enabled:
            db_conn = get_connection(DUCKDB_PATH)
            initialize_schema(db_conn)

        # Opening book table if enabled
        book_table = None
        book_tuner = None
        if self.tune_book_enabled:
            book_table = PolyGlotBookTable()
            if self.book_path.is_file():
                book_table.load(self.book_path)
                book_tuner = ReinforcementBookTuner(
                    book_table=book_table,
                    learning_rate=10.0,
                    discount_factor=0.85,
                    elo_scaling=True,
                    max_ply=16,
                )

        gauntlet_pgns: List[Path] = []

        # Execute matches
        for idx, (opp_entry, opp_bin, is_sf_calibrated, effective_elo) in enumerate(pool, start=1):
            opp_name = opp_entry["name"]
            style = opp_entry.get("style", "Standard")
            print(f"\n[{idx}/{len(pool)}] MATCH: Anak vs {opp_name} (CCRL ~{effective_elo} | Style: {style})")
            print("-" * 75)

            pgn_path, w, l, d = run_single_gauntlet_match(
                opp_entry=opp_entry,
                opp_bin=opp_bin,
                games=self.games_per_opp,
                tc=self.tc,
                concurrency=self.concurrency,
                hash_mb=self.hash_mb,
                threads=self.threads,
                openings_path=self.openings_path,
                out_dir=run_out_dir,
                is_sf_calibrated=is_sf_calibrated,
                calibrated_elo=effective_elo,
            )

            gauntlet_pgns.append(pgn_path)
            total = w + l + d
            score = (w + 0.5 * d) / max(1, total)

            # Calculate individual pairwise metrics
            nelo, nelo_err = calculate_trinomial_nelo(w, l, d)
            log_elo, log_err = calculate_logistic_elo(w, l, d)

            match_info = {
                "name": opp_name,
                "known_elo": effective_elo,
                "style": style,
                "tier": opp_entry.get("tier", effective_elo),
                "games": total,
                "wins": w,
                "losses": l,
                "draws": d,
                "score": round(score, 4),
                "nelo": round(nelo, 1),
                "nelo_err": round(nelo_err, 1),
                "logistic_elo": round(log_elo, 1),
                "logistic_err": round(log_err, 1),
                "pgn_file": str(pgn_path),
            }
            self.match_results.append(match_info)

            self.total_wins += w
            self.total_losses += l
            self.total_draws += d
            self.total_games += total

            print(f"\n  -> Result vs {opp_name}: {w}W - {l}L - {d}D ({score*100.0:.1f}%) | nElo: {nelo:+.1f} +/- {nelo_err:.1f}")

            # Pillar 2: Stream match to Weakness Finder DuckDB
            if self.ingest_db_enabled and pgn_path.is_file():
                match_id = f"gauntlet_{re.sub(r'[^a-zA-Z0-9_]', '_', opp_name.lower())}_{int(time.time())}"
                ingest_pgn_file(
                    pgn_path=pgn_path,
                    match_id=match_id,
                    opponent_name=opp_name,
                    stockfish_elo=effective_elo,
                    time_control=self.tc,
                )

            # Pillar 3: Incremental Reinforcement Learning on book.bin
            if self.tune_book_enabled and book_tuner and pgn_path.is_file():
                res = book_tuner.train_pgn_file(pgn_path, target_player="Anak")
                print(f"  [RL Book] Updated {res['move_updates']} opening move weights in memory.")

        # Save tuned book to disk once gauntlet completes
        if self.tune_book_enabled and book_table and self.book_path.exists():
            print(f"\n[*] Saving reinforced opening book to {self.book_path} ...")
            book_table.save(self.book_path)
            print("[+] Opening book updated with gauntlet experience!")

        # Pillar 1: Bradley-Terry Anchor MLE Calibration
        anchor_stats = [
            {
                "name": r["name"],
                "known_elo": r["known_elo"],
                "wins": r["wins"],
                "losses": r["losses"],
                "draws": r["draws"],
            }
            for r in self.match_results
        ]
        anchor_calib = calibrate_anchor_elo(engine_name="Anak", opponents=anchor_stats)

        # Generate and print reports
        self.print_gauntlet_report(anchor_calib)
        self.save_reports(run_out_dir, anchor_calib, timestamp)

    def print_gauntlet_report(self, calib: dict):
        """Displays cross-table and final Bradley-Terry rating."""
        print("\n" + "=" * 90)
        print("                  ANAK REFERENCE LADDER GAUNTLET REPORT")
        print("=" * 90)
        print(f"  {'Opponent Engine':<22} {'CCRL':<7} {'Style':<24} {'Games':<7} {'Score %':<9} {'W-L-D':<10} {'nElo':<10}")
        print("  " + "-" * 85)

        for r in self.match_results:
            wld = f"{r['wins']}-{r['losses']}-{r['draws']}"
            nelo_str = f"{r['nelo']:+.1f}" if not math.isinf(r['nelo']) else "inf"
            print(f"  {r['name']:<22} {r['known_elo']:<7} {r['style']:<24} {r['games']:<7} {r['score']*100.0:>6.1f}%   {wld:<10} {nelo_str:<10}")

        print("  " + "-" * 85)
        tot_pts = self.total_wins + 0.5 * self.total_draws
        tot_pct = (tot_pts / max(1, self.total_games)) * 100.0
        tot_wld = f"{self.total_wins}-{self.total_losses}-{self.total_draws}"
        print(f"  {'TOTAL GAUNTLET':<22} {'-':<7} {'All Styles':<24} {self.total_games:<7} {tot_pct:>6.1f}%   {tot_wld:<10}")
        print("=" * 90)
        print(f"  >>> ANCHORED CCRL ELO: {calib['calibrated_elo']} +/- {calib['ci95']} (95% CI Bradley-Terry MLE) <<<")
        print("=" * 90 + "\n")

    def save_reports(self, out_dir: Path, calib: dict, timestamp: str):
        """Saves Markdown and JSON reports."""
        json_file = out_dir / f"gauntlet_report_{timestamp}.json"
        md_file = out_dir / f"gauntlet_report_{timestamp}.md"

        data = {
            "timestamp": datetime.now().isoformat(),
            "time_control": self.tc,
            "total_games": self.total_games,
            "wins": self.total_wins,
            "losses": self.total_losses,
            "draws": self.total_draws,
            "score": round((self.total_wins + 0.5 * self.total_draws) / max(1, self.total_games), 4),
            "calibrated_ccrl_elo": calib["calibrated_elo"],
            "ci95": calib["ci95"],
            "matches": self.match_results,
        }

        with open(json_file, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

        md_lines = [
            f"# Anak Reference Ladder Gauntlet Report — {datetime.now().strftime('%Y-%m-%d %H:%M')}",
            "",
            f"**Anchored CCRL Elo:** `{calib['calibrated_elo']} +/- {calib['ci95']}` (95% CI)",  
            f"**Total Score:** `{data['score']*100.0:.1f}%` ({self.total_wins}W - {self.total_losses}L - {self.total_draws}D across {self.total_games} games)",
            f"**Time Control:** `{self.tc}`",
            "",
            "## Tournament Cross-Table",
            "",
            "| Opponent Engine | Known CCRL | Style | Games | Score % | W-L-D | nElo | Logistic Elo |",
            "| :--- | :--- | :--- | :--- | :--- | :--- | :--- | :--- |",
        ]
        for r in self.match_results:
            wld = f"{r['wins']}-{r['losses']}-{r['draws']}"
            nelo_s = f"{r['nelo']:+.1f} +/- {r['nelo_err']:.1f}"
            log_s = f"{r['logistic_elo']:+.1f} +/- {r['logistic_err']:.1f}"
            md_lines.append(f"| **{r['name']}** | {r['known_elo']} | {r['style']} | {r['games']} | {r['score']*100.0:.1f}% | {wld} | {nelo_s} | {log_s} |")

        md_lines.extend([
            "",
            "## Unified Engine Telemetry",
            "- **Weakness Database:** Ingested into `tools/anak_weakness_finder/weakness_finder.duckdb` for tactical/opening blunder query.",
            "- **Opening Book Tuning:** Reinforcement updates applied to `book.bin` based on win/loss attribution.",
            f"- **Match PGN Directory:** `{out_dir}`",
        ])

        with open(md_file, "w", encoding="utf-8") as f:
            f.write("\n".join(md_lines) + "\n")

        print(f"[+] Gauntlet reports saved to:\n    - {md_file}\n    - {json_file}")


def main():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — Unified Reference Ladder Gauntlet Runner")
    parser.add_argument("--games", type=int, default=10, help="Games per opponent engine (default: 10)")
    parser.add_argument("--tc", type=str, default="1+0.01", help="Time control in seconds base+inc (default: 1+0.01)")
    total_cpus = os.cpu_count() or 2
    default_concurrency = max(1, total_cpus // 2)
    parser.add_argument("--concurrency", type=int, default=default_concurrency, help=f"Concurrency (default: {default_concurrency})")
    parser.add_argument("--hash", type=int, default=64, help="Transposition table Hash in MB per engine (default: 64)")
    parser.add_argument("--threads", type=int, default=1, help="Search threads per engine instance (default: 1)")
    parser.add_argument("--min-tier", type=int, default=1600, help="Minimum tier Elo to include (default: 1600)")
    parser.add_argument("--max-tier", type=int, default=3700, help="Maximum tier Elo to include (default: 3700)")
    parser.add_argument("--engines", nargs="*", default=None, help="Specific engine names to match against (e.g. FoxSEE-8.2 Stockfish-19)")
    parser.add_argument("--fallback-sf", action="store_true", help="Fill missing reference engine tiers with calibrated Stockfish")
    parser.add_argument("--no-tune-book", dest="tune_book", action="store_false", help="Disable automatic RL opening book tuning from gauntlet results")
    parser.add_argument("--no-db", dest="ingest_db", action="store_false", help="Disable automatic match ingestion into DuckDB")
    parser.add_argument("--no-build", action="store_true", help="Skip rebuilding Anak if binary already exists")
    parser.add_argument("--openings", type=Path, default=DEFAULT_OPENINGS, help="Opening suite file (default: tools/fastchess/openings.epd)")
    parser.add_argument("--book", type=Path, default=DEFAULT_BOOK, help="Path to book.bin (default: book.bin)")

    args = parser.parse_args()

    ensure_binaries(skip_build=args.no_build)

    runner = GauntletRunner(
        games_per_opponent=args.games,
        tc=args.tc,
        concurrency=args.concurrency,
        hash_mb=args.hash,
        threads=args.threads,
        openings_path=args.openings,
        min_tier=args.min_tier,
        max_tier=args.max_tier,
        specific_engines=args.engines,
        fallback_sf=args.fallback_sf,
        tune_book=args.tune_book,
        ingest_db=args.ingest_db,
        book_path=args.book,
    )
    runner.run()


if __name__ == "__main__":
    main()
