"""
Unified Training Data Downloader & Hub for Anak NNUE Training.
Provides one-click downloading and high-speed native conversion for:
  - Stockfish Depth-9 Massive Training Database (500M_d9_p1.binpack & 500M_d9_mix_p1.binpack)
  - TCEC (Top Chess Engine Championship full seasons & cup archives)
  - Leela Chess Zero / Stockfish relabeled binpacks
  - Local Generation (Query-guided self-play with local Stockfish)
"""

import argparse
import os
import shutil
import struct
import subprocess
import sys
import urllib.parse
import urllib.request
import zipfile
from typing import List
from tqdm import tqdm

STOCKFISH_D9_URLS = [
    ("500M_d9_p1.binpack", "https://huggingface.co/datasets/jshriver/historic-binpacks/resolve/main/500M_d9_p1.binpack"),
    ("500M_d9_mix_p1.binpack", "https://huggingface.co/datasets/jshriver/historic-binpacks/resolve/main/500M_d9_mix_p1.binpack"),
]


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
    with urllib.request.urlopen(req, timeout=30) as resp:
        total_size = int(resp.headers.get("content-length", 0))

        with DownloadProgressBar(unit="B", unit_scale=True, miniters=1, desc=os.path.basename(output_path), total=total_size) as t:
            with open(output_path, "wb") as out_f:
                while True:
                    chunk = resp.read(65536)
                    if not chunk:
                        break
                    out_f.write(chunk)
                    t.update(len(chunk))


def download_stockfish_d9_binpacks(output_dir: str, chunks_per_file: int = 18) -> List[str]:
    """
    Streams complete, chunk-aligned Stockfish Depth-9 BINP archives (500M_d9_p1.binpack
    and 500M_d9_mix_p1.binpack) from HuggingFace.
    Each BINP chunk (~674 KB) contains 200,000 Stockfish Depth-9 evaluated positions.
    Default chunks_per_file=18 (36 chunks total, ~24.3 MB) yields 7,200,000 positions!
    Pass chunks_per_file=0 to download the full multi-gigabyte 500M files.
    """
    os.makedirs(output_dir, exist_ok=True)
    downloaded_paths = []
    min_expected_bytes = max(4 * 1024 * 1024, chunks_per_file * 600_000)

    for filename, url in STOCKFISH_D9_URLS:
        target_path = os.path.join(output_dir, filename)
        if os.path.isfile(target_path) and os.path.getsize(target_path) >= min_expected_bytes:
            with open(target_path, "rb") as f:
                if f.read(4) == b"BINP":
                    print(f"Found existing Stockfish d9 archive: {target_path} ({os.path.getsize(target_path):,} bytes)")
                    downloaded_paths.append(target_path)
                    continue

        if chunks_per_file <= 0:
            download_file(url, target_path)
            downloaded_paths.append(target_path)
            continue

        est_positions = chunks_per_file * 200_000
        print(f"\n--> Streaming {chunks_per_file} BINP chunks (~{est_positions:,} Depth-9 positions) from {filename}...")
        req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})
        with urllib.request.urlopen(req, timeout=30) as resp:
            with open(target_path, "wb") as out_f:
                pbar = tqdm(total=chunks_per_file, desc=f"Streaming {filename}", unit="chunk")
                for _ in range(chunks_per_file):
                    hdr = resp.read(8)
                    if len(hdr) < 8:
                        break
                    magic, chunk_size = struct.unpack("<4sI", hdr)
                    if magic != b"BINP" or chunk_size == 0 or chunk_size > 100 * 1024 * 1024:
                        break
                    out_f.write(hdr)
                    remaining = chunk_size
                    while remaining > 0:
                        block = resp.read(min(65536, remaining))
                        if not block:
                            break
                        out_f.write(block)
                        remaining -= len(block)
                    pbar.update(1)
                pbar.close()

        print(f"Saved {target_path} ({os.path.getsize(target_path):,} bytes, ~{est_positions:,} Stockfish d9 positions)")
        downloaded_paths.append(target_path)

    return downloaded_paths


def ensure_sf_binpack_converter() -> str:
    """Ensures the native C++20 Stockfish BINP -> Anak binpack converter binary is compiled."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    cpp_dir = os.path.join(script_dir, "cpp_binpack")
    exe_name = "sf_binpack_to_anak.exe" if os.name == "nt" else "sf_binpack_to_anak"
    exe_path = os.path.join(cpp_dir, exe_name)
    cpp_path = os.path.join(cpp_dir, "sf_binpack_to_anak.cpp")

    if os.path.isfile(exe_path) and os.path.isfile(cpp_path):
        if os.path.getmtime(exe_path) >= os.path.getmtime(cpp_path):
            return exe_path

    gpp = shutil.which("g++") or shutil.which("clang++")
    if not gpp:
        raise RuntimeError("C++20 compiler (g++ or clang++) not found for compiling sf_binpack_to_anak.cpp")

    print("Compiling native Stockfish Depth-9 BINP converter (sf_binpack_to_anak)...")
    subprocess.run([gpp, "-O3", "-std=c++20", cpp_path, "-o", exe_path], check=True)
    return exe_path


def convert_stockfish_d9_to_anak(
    sf_binpack_files: List[str],
    output_dir: str,
    max_per_pack: int = 1_000_000
):
    """
    Converts raw Stockfish Depth-9 BINP files into globally shuffled Anak memory-mapped
    binpacks (open.binpack, closed.binpack, micro.binpack).
    """
    os.makedirs(output_dir, exist_ok=True)
    exe_path = ensure_sf_binpack_converter()
    cmd = [exe_path, output_dir, str(max_per_pack)] + sf_binpack_files
    print(f"\n--> Converting & shuffling Stockfish Depth-9 dataset (target: {max_per_pack:,} positions per model)...")
    subprocess.run(cmd, check=True)


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


def handle_stockfish_data(output_dir: str, chunks_per_file: int = 12):
    """Downloads Stockfish 500M Depth-9 evaluation datasets."""
    print("\n--> Fetching Official Stockfish 500M Depth-9 (d9) Training Database...")
    return download_stockfish_d9_binpacks(output_dir, chunks_per_file=chunks_per_file)


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


def download_all_sources(output_dir: str, chunks_per_file: int = 12):
    """Downloads Stockfish 500M Depth-9 dataset plus TCEC archives."""
    print("=" * 70)
    print("Fetching Stockfish 500M Depth-9 (d9) Training Corpus")
    print("=" * 70)
    return handle_stockfish_data(output_dir, chunks_per_file=chunks_per_file)


def parse_args():
    parser = argparse.ArgumentParser(description="Unified Training Data Downloader & Hub for Anak NNUE")
    parser.add_argument("--source", type=str, choices=["stockfish", "tcec", "generate", "all"],
                        default="all", help="Source of training data to download or generate (default: all)")
    parser.add_argument("--output", type=str, default="./training/data/raw",
                        help="Target output directory (default: training/data/raw/)")
    parser.add_argument("--chunks", type=int, default=12,
                        help="Number of 200k-position BINP chunks per d9 file (default: 12 = 4.8M positions; 0 = full 500M)")
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

    if args.source in ("all", "stockfish"):
        sf_files = handle_stockfish_data(out_dir, chunks_per_file=args.chunks)
        preprocessed_dir = os.path.join(os.path.dirname(out_dir), "preprocessed")
        convert_stockfish_d9_to_anak(sf_files, preprocessed_dir, max_per_pack=300_000)
    elif args.source == "tcec":
        handle_tcec(out_dir, season=args.season, division=args.division)
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
