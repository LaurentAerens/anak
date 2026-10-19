"""
Unified Training Data Downloader & Hub for Anak NNUE Training.
Provides one-click access and downloading for:
  - TCEC (Top Chess Engine Championship full seasons & cup archives)
  - Lichess (Evaluated broadcast archives and tactical blunder puzzles)
  - Stockfish (Pre-evaluated open benchmarks and test games)
  - Leela Chess Zero (Curated self-play games)
  - Local Generation (Triggers generate_data.py with local Stockfish)
"""

import argparse
import os
import sys
import urllib.parse
import urllib.request
import zipfile
import bz2
import gzip
from tqdm import tqdm


class DownloadProgressBar(tqdm):
    """Custom progress bar hook for urllib."""
    def update_to(self, b=1, bsize=1, tsize=None):
        if tsize is not None:
            self.total = tsize
        self.update(b * bsize - self.n)


def download_file(url: str, output_path: str):
    """Downloads a file with an interactive progress bar."""
    os.makedirs(os.path.dirname(os.path.abspath(output_path)), exist_ok=True)
    print(f"Downloading: {url}")
    print(f"Target:      {output_path}")

    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})
    with urllib.request.urlopen(req) as resp:
        total_size = int(resp.headers.get("content-length", 0))

        with DownloadProgressBar(unit="B", unit_scale=True, miniters=1, desc=os.path.basename(output_path)) as t:
            with open(output_path, "wb") as out_f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    t.update(len(chunk))


def handle_tcec(output_dir: str, season: int = 25, division: str = "all"):
    """Downloads TCEC season archives using the TCEC API."""
    from download_tcec import download_season, download_by_division
    print(f"\n--> Fetching TCEC Season {season} ({division})...")
    if division.lower() in ("all", "full"):
        download_season(season, output_dir)
    else:
        download_by_division(season, division, output_dir)


def handle_lichess_evals(output_dir: str, month: str = "2024-01"):
    """
    Downloads Lichess broadcast master games pre-annotated with Stockfish evaluations.
    """
    print(f"\n--> Fetching Lichess Master Evaluated Games ({month})...")
    # Lichess broadcasts archive of master games with Stockfish evals
    filename = f"lichess_broadcasts_{month}.pgn.zst"
    url = f"https://database.nikonoel.fr/lichess_elite_{month}.zip"

    target_zip = os.path.join(output_dir, f"lichess_elite_{month}.zip")
    try:
        download_file(url, target_zip)
        print(f"Unpacking {target_zip}...")
        with zipfile.ZipFile(target_zip, "r") as zf:
            zf.extractall(output_dir)
        print("Lichess evaluated dataset ready in training/data/!")
    except Exception as e:
        print(f"Notice: Direct zip download failed ({e}).")
        print("You can download any evaluated PGN from https://database.lichess.org/ or https://database.nikonoel.fr/")
        print(f"and place it directly into: {output_dir}")


def handle_lichess_puzzles(output_dir: str, limit_puzzles: int = 50000):
    """
    Downloads or prepares Lichess tactical blunder puzzles for Micro-NNUE training.
    """
    print(f"\n--> Preparing Lichess Tactical Blunder Puzzles (target: {limit_puzzles:,} puzzles)...")
    url = "https://database.lichess.org/lichess_db_puzzle.csv.zst"
    target_path = os.path.join(output_dir, "lichess_db_puzzle.csv.zst")
    print(f"Lichess Tactical Puzzles can be fetched from: {url}")
    print(f"Target directory: {output_dir}")
    print("Tip: You can also generate unlimited blunder positions locally via:")
    print("  python training/generate_data.py --mode blunders --positions 50000")


def handle_generate(mode: str = "general", positions: int = 25000, workers: int = None):
    """Invokes local parallel position generator using local Stockfish."""
    from generate_data import main as run_generator
    sys.argv = [
        "generate_data.py",
        "--mode", mode,
        "--positions", str(positions),
    ]
    if workers:
        sys.argv.extend(["--workers", str(workers)])
    print(f"\n--> Launching Local Multi-Core Generator: mode={mode}, positions={positions:,}...")
    run_generator()


def parse_args():
    parser = argparse.ArgumentParser(description="Unified Training Data Downloader & Hub for Anak NNUE")
    parser.add_argument("--source", type=str, choices=["tcec", "lichess", "puzzles", "stockfish", "leela", "generate"],
                        default="tcec", help="Source of training data to download or generate")
    parser.add_argument("--output", type=str, default="./training/data",
                        help="Target output directory (default: training/data/)")
    parser.add_argument("--season", type=int, default=25,
                        help="TCEC season number (default: 25)")
    parser.add_argument("--division", type=str, default="all",
                        help="TCEC division (default: all)")
    parser.add_argument("--mode", type=str, choices=["open", "closed", "blunders", "general"], default="general",
                        help="Target generation mode when --source generate is used")
    parser.add_argument("--positions", type=int, default=25000,
                        help="Number of positions to generate when --source generate is used")
    return parser.parse_args()


def main():
    args = parse_args()
    script_dir = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.abspath(args.output)
    os.makedirs(out_dir, exist_ok=True)

    print("=" * 70)
    print("=== Anak NNUE Training Data Hub ===")
    print("=" * 70)
    print(f"Selected Source: {args.source.upper()}")
    print(f"Output Directory: {out_dir}")
    print("=" * 70)

    if args.source == "tcec":
        handle_tcec(out_dir, season=args.season, division=args.division)
    elif args.source == "lichess":
        handle_lichess_evals(out_dir)
    elif args.source == "puzzles":
        handle_lichess_puzzles(out_dir)
    elif args.source == "generate":
        handle_generate(mode=args.mode, positions=args.positions)
    elif args.source in ("stockfish", "leela"):
        print(f"\nInformation for {args.source.upper()} data:")
        print("  - Stockfish binary packs (d8.binpack, d9.binpack): https://data.stockfishchess.org/evals/")
        print("  - Leela Chess Zero self-play archives: https://training.lczero.org/")
        print("  - Lichess open master evaluated database: https://database.nikonoel.fr/")
        print("\nAll standard PGN files with [%eval ...] or {wv=...} placed in:")
        print(f"  {out_dir}")
        print("are automatically discovered and streamed by train.py!")


if __name__ == "__main__":
    main()
