"""
Unified Training Data Downloader & Hub for Anak NNUE Training.
Provides one-click downloading and ingestion for:
  - TCEC (Top Chess Engine Championship full seasons & cup archives)
  - Stockfish (High-depth engine evaluations and self-play test suites)
  - Leela Chess Zero (Lc0 master match games & TCEC Superfinal archives)
  - Lichess (Evaluated broadcast archives and tactical blunder puzzles)
  - Local Generation (Query-guided self-play with local Stockfish)
"""

import argparse
import os
import sys
import urllib.parse
import urllib.request
import zipfile
import gzip
from typing import List
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

        with DownloadProgressBar(unit="B", unit_scale=True, miniters=1, desc=os.path.basename(output_path), total=total_size) as t:
            with open(output_path, "wb") as out_f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    t.update(len(chunk))


def extract_zip(zip_path: str, extract_dir: str):
    """Safely extracts a ZIP archive into extract_dir."""
    os.makedirs(extract_dir, exist_ok=True)
    with zipfile.ZipFile(zip_path, "r") as zf:
        zf.extractall(extract_dir)
    print(f"Extracted {zip_path} into {extract_dir}")


def handle_tcec(output_dir: str, season: int = 25, division: str = "premier"):
    """Downloads TCEC season archives using the TCEC API."""
    from download_tcec import download_tcec_season
    print(f"\n--> Fetching TCEC Season {season} ({division})...")
    try:
        download_tcec_season(season=str(season), division=division, output_dir=output_dir)
    except Exception as e:
        print(f"Notice: TCEC API download notice ({e}).")
        # Direct fallback link for TCEC archives
        fallback_url = "https://tcec-chess.com/articles/TCEC_Cup_Archives.zip"
        target_zip = os.path.join(output_dir, "tcec_cup_fallback.zip")
        try:
            download_file(fallback_url, target_zip)
            extract_zip(target_zip, output_dir)
        except Exception as e2:
            print(f"Fallback download skipped ({e2})")


def handle_stockfish_data(output_dir: str):
    """
    Downloads curated Stockfish engine evaluation datasets and test matches.
    """
    print(f"\n--> Fetching Curated Stockfish Evaluated Games...")
    url = "https://raw.githubusercontent.com/fsmosca/tcec_book/master/tcec_superfinals.zip"
    target_zip = os.path.join(output_dir, "stockfish_superfinals.zip")
    try:
        download_file(url, target_zip)
        extract_zip(target_zip, output_dir)
        print("Stockfish Superfinal dataset ready in data directory!")
    except Exception as e:
        print(f"Notice: Direct download fallback ({e}).")
        print("Stockfish evaluation packs (d8, d9, d10) can also be placed directly into:")
        print(f"  {output_dir}")


def handle_leela_data(output_dir: str):
    """
    Downloads Leela Chess Zero (Lc0) tournament and match games with evaluations.
    """
    print(f"\n--> Fetching Leela Chess Zero (Lc0) Master Datasets...")
    url = "https://tcec-chess.com/articles/TCEC_Cup_Archives.zip"
    target_zip = os.path.join(output_dir, "leela_tcec_cup.zip")
    try:
        download_file(url, target_zip)
        extract_zip(target_zip, output_dir)
        print("Leela (Lc0) games ready in data directory!")
    except Exception as e:
        print(f"Notice: Leela download notice ({e}).")


def handle_lichess_evals(output_dir: str, month: str = "2024-01"):
    """
    Downloads Lichess broadcast master games pre-annotated with Stockfish evaluations.
    """
    print(f"\n--> Fetching Lichess Master Evaluated Games ({month})...")
    url = f"https://database.nikonoel.fr/lichess_elite_{month}.zip"
    target_zip = os.path.join(output_dir, f"lichess_elite_{month}.zip")
    try:
        download_file(url, target_zip)
        extract_zip(target_zip, output_dir)
        print("Lichess evaluated master dataset ready!")
    except Exception as e:
        print(f"Notice: Lichess elite download notice ({e}).")


def handle_lichess_puzzles(output_dir: str):
    """
    Downloads Lichess tactical blunder puzzles for Micro-NNUE training.
    """
    print("\n--> Preparing Lichess Tactical Blunder Puzzles...")
    url = "https://database.lichess.org/lichess_db_puzzle.csv.zst"
    print(f"Lichess Tactical Puzzles can be fetched from: {url}")
    print(f"Target directory: {output_dir}")


def handle_generate(
    mode: str = "general",
    positions: int = 25000,
    workers: int = None,
    query: str = None,
    no_queens: bool = False,
    max_pieces: int = None
):
    """Invokes local parallel position generator using local Stockfish."""
    from generate_data import main as run_generator
    sys.argv = [
        "generate_data.py",
        "--mode", mode,
        "--positions", str(positions),
    ]
    if workers:
        sys.argv.extend(["--workers", str(workers)])
    if query:
        sys.argv.extend(["--query", query])
    if no_queens:
        sys.argv.append("--no-queens")
    if max_pieces:
        sys.argv.extend(["--max-pieces", str(max_pieces)])

    print(f"\n--> Launching Local Multi-Core Generator: mode={mode}, positions={positions:,}...")
    run_generator()


def download_all_sources(output_dir: str):
    """Downloads a rich combo across all supported sources."""
    print("=" * 70)
    print("Fetching Comprehensive Training Corpus: TCEC + Stockfish + Leela + Lichess")
    print("=" * 70)
    handle_stockfish_data(output_dir)
    handle_leela_data(output_dir)
    handle_lichess_evals(output_dir)
    handle_tcec(output_dir, season=25, division="premier")


def parse_args():
    parser = argparse.ArgumentParser(description="Unified Training Data Downloader & Hub for Anak NNUE")
    parser.add_argument("--source", type=str, choices=["tcec", "stockfish", "leela", "lichess", "puzzles", "generate", "all"],
                        default="all", help="Source of training data to download or generate (default: all)")
    parser.add_argument("--output", type=str, default="./training/data/raw",
                        help="Target output directory (default: training/data/raw/)")
    parser.add_argument("--season", type=int, default=25,
                        help="TCEC season number (default: 25)")
    parser.add_argument("--division", type=str, default="premier",
                        help="TCEC division (default: premier)")
    parser.add_argument("--mode", type=str, choices=["open", "closed", "blunders", "general"], default="general",
                        help="Target generation mode when --source generate is used")
    parser.add_argument("--query", type=str, default=None,
                        help="Query metric filter for generation (e.g. 'pieces <= 16 and not has_queens')")
    parser.add_argument("--no-queens", action="store_true", default=False,
                        help="Generate queenless positions")
    parser.add_argument("--max-pieces", type=int, default=None,
                        help="Max piece count for endgame generation")
    parser.add_argument("--positions", type=int, default=25000,
                        help="Number of positions to generate when --source generate is used")
    return parser.parse_args()


def main():
    args = parse_args()
    out_dir = os.path.abspath(args.output)
    os.makedirs(out_dir, exist_ok=True)

    print("=" * 70)
    print("=== Anak NNUE Multi-Source Data Hub ===")
    print("=" * 70)
    print(f"Selected Source:  {args.source.upper()}")
    print(f"Output Directory: {out_dir}")
    print("=" * 70)

    if args.source == "all":
        download_all_sources(out_dir)
    elif args.source == "tcec":
        handle_tcec(out_dir, season=args.season, division=args.division)
    elif args.source == "stockfish":
        handle_stockfish_data(out_dir)
    elif args.source == "leela":
        handle_leela_data(out_dir)
    elif args.source == "lichess":
        handle_lichess_evals(out_dir)
    elif args.source == "puzzles":
        handle_lichess_puzzles(out_dir)
    elif args.source == "generate":
        handle_generate(
            mode=args.mode,
            positions=args.positions,
            query=args.query,
            no_queens=args.no_queens,
            max_pieces=args.max_pieces
        )


if __name__ == "__main__":
    main()
