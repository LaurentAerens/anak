#!/usr/bin/env python3
"""
Anak Chess Engine — Automated Fast-Chess Elo Benchmark Runner (Windows / Cross-Platform)
Measures Anak's Elo rating compared to calibrated Stockfish levels using fast-chess.
"""

import argparse
import io
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

# Paths relative to repo root
SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
BIN_DIR = SCRIPT_DIR / "bin"
RESULTS_DIR = SCRIPT_DIR / "results"
OPENINGS_FILE = SCRIPT_DIR / "openings.epd"
ANAK_PUBLISH_DIR = BIN_DIR / "anak"
ANAK_EXE = ANAK_PUBLISH_DIR / ("anak.exe" if os.name == "nt" else "anak")
FASTCHESS_EXE = BIN_DIR / ("fastchess.exe" if os.name == "nt" else "fastchess")
STOCKFISH_EXE = BIN_DIR / ("stockfish.exe" if os.name == "nt" else "stockfish")

FASTCHESS_WINDOWS_URL = "https://github.com/Disservin/fastchess/releases/download/v1.8.2-alpha/fastchess-windows-x86-64.zip"
STOCKFISH_WINDOWS_URL = "https://github.com/official-stockfish/Stockfish/releases/download/sf_19/stockfish-windows-x86-64-universal.zip"


def download_and_extract_zip(url: str, dest_dir: Path, description: str):
    print(f"[+] Downloading {description} from {url} ...")
    req = urllib.request.Request(url, headers={"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"})
    with urllib.request.urlopen(req) as response:
        total_size = int(response.headers.get("content-length", 0))
        data = io.BytesIO()
        downloaded = 0
        chunk_size = 64 * 1024

        while True:
            chunk = response.read(chunk_size)
            if not chunk:
                break
            data.write(chunk)
            downloaded += len(chunk)
            if total_size > 0:
                percent = downloaded * 100 // total_size
                mb = downloaded / (1024 * 1024)
                print(f"\r    Progress: {percent}% ({mb:.1f} MB)", end="", flush=True)

        print("\n[+] Extracting files ...")
        with zipfile.ZipFile(data) as z:
            z.extractall(dest_dir)
        print(f"[+] {description} ready in {dest_dir}")


def ensure_fastchess():
    if FASTCHESS_EXE.exists():
        return

    # Check if unpacked in subdirectory
    subdirs = [p for p in BIN_DIR.glob("fastchess*") if p.is_dir()]
    for s in subdirs:
        candidate = s / ("fastchess.exe" if os.name == "nt" else "fastchess")
        if candidate.exists():
            shutil.copy2(candidate, FASTCHESS_EXE)
            print(f"[+] Found fast-chess executable: {FASTCHESS_EXE}")
            return

    BIN_DIR.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        download_and_extract_zip(FASTCHESS_WINDOWS_URL, BIN_DIR, "fast-chess for Windows")
        # Find extracted fastchess.exe
        for candidate in BIN_DIR.rglob("fastchess.exe"):
            if candidate != FASTCHESS_EXE:
                shutil.copy2(candidate, FASTCHESS_EXE)
                break
    else:
        raise RuntimeError("Please install fast-chess on non-Windows platforms or place fastchess binary in tools/fastchess/bin/")


def ensure_stockfish():
    if STOCKFISH_EXE.exists():
        return

    # Check if unpacked in subdirectory
    subdirs = [p for p in BIN_DIR.glob("stockfish*") if p.is_dir()]
    for s in subdirs:
        for candidate in s.rglob("*.exe" if os.name == "nt" else "*"):
            if "stockfish" in candidate.name.lower() and candidate.is_file():
                shutil.copy2(candidate, STOCKFISH_EXE)
                print(f"[+] Found Stockfish executable: {STOCKFISH_EXE}")
                return

    BIN_DIR.mkdir(parents=True, exist_ok=True)
    if os.name == "nt":
        download_and_extract_zip(STOCKFISH_WINDOWS_URL, BIN_DIR, "Stockfish 19 for Windows")
        # Find extracted stockfish executable
        for candidate in BIN_DIR.rglob("*.exe"):
            if "stockfish" in candidate.name.lower() and candidate != STOCKFISH_EXE:
                shutil.copy2(candidate, STOCKFISH_EXE)
                print(f"[+] Configured Stockfish at {STOCKFISH_EXE}")
                break
    else:
        raise RuntimeError("Please install Stockfish on non-Windows platforms or place stockfish binary in tools/fastchess/bin/")


def ensure_anak(force_rebuild: bool = False):
    if not force_rebuild and ANAK_EXE.exists():
        return

    print("[+] Building and publishing Anak in Release configuration ...")
    cmd = [
        "dotnet",
        "publish",
        str(REPO_ROOT / "src" / "Anak.Protocol" / "Anak.Protocol.csproj"),
        "-c",
        "Release",
        "-o",
        str(ANAK_PUBLISH_DIR),
    ]
    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        raise RuntimeError("Failed to build Anak. Please ensure .NET SDK is installed.")
    print(f"[+] Anak executable ready: {ANAK_EXE}")


def parse_args():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — Fast-Chess Elo Benchmark Runner")
    parser.add_argument("--elo", type=int, default=1800, help="Target Stockfish Elo rating (default: 1800, range: 1320-3190)")
    parser.add_argument("--rounds", type=int, default=10, help="Number of opening rounds (each played twice with swapped colors, default: 10 = 20 games)")
    parser.add_argument("--tc", type=str, default="10+0.1", help="Time control base+inc in seconds (default: 10+0.1)")
    parser.add_argument("--concurrency", type=int, default=min(8, max(1, os.cpu_count() or 2)), help="Number of concurrent games (default: CPU cores)")
    parser.add_argument("--hash", type=int, default=64, help="Transposition Table Hash in MB per engine (default: 64)")
    parser.add_argument("--threads", type=int, default=1, help="Search threads per engine (default: 1)")
    parser.add_argument("--quick", action="store_true", help="Quick smoke test: 5 rounds (10 games) at 3+0.05 time control")
    parser.add_argument("--rebuild", action="store_true", help="Force rebuild of Anak before benchmark")
    return parser.parse_args()


def main():
    args = parse_args()

    if args.quick:
        args.rounds = 5
        args.tc = "3+0.05"
        print("[!] Quick mode enabled: 5 rounds (10 games) at 3+0.05")

    total_games = args.rounds * 2

    print("=" * 70)
    print("  ANAK CHESS ENGINE — ELO RATING EVALUATION (FAST-CHESS)")
    print("=" * 70)
    print(f"  Reference Opponent:   Stockfish 19 (Calibrated Elo: {args.elo})")
    print(f"  Rounds:               {args.rounds} opening pairs ({total_games} total games)")
    print(f"  Time Control:         {args.tc} (seconds + increment)")
    print(f"  Concurrency:          {args.concurrency} parallel games")
    print(f"  Hash Table Size:      {args.hash} MB per engine")
    print(f"  Opening Suite:        {OPENINGS_FILE.name}")
    print("=" * 70)

    # 1. Ensure dependencies
    ensure_anak(force_rebuild=args.rebuild)
    ensure_fastchess()
    ensure_stockfish()

    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    pgn_output = RESULTS_DIR / f"anak_vs_sf{args.elo}_{timestamp}.pgn"

    # Use paths relative to REPO_ROOT to avoid spaces in paths breaking fastchess CLI parsing
    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = str(os.path.relpath(ANAK_EXE, REPO_ROOT))
    stockfish_cmd = str(os.path.relpath(STOCKFISH_EXE, REPO_ROOT))
    openings_path = str(os.path.relpath(OPENINGS_FILE, REPO_ROOT))
    pgn_path = str(os.path.relpath(pgn_output, REPO_ROOT))

    # 2. Build fast-chess command
    cmd = [
        fastchess_cmd,
        "-engine",
        f"cmd={anak_cmd}",
        "name=Anak",
        f"option.Threads={args.threads}",
        f"option.Hash={args.hash}",
        "-engine",
        f"cmd={stockfish_cmd}",
        f"name=Stockfish-{args.elo}",
        f"option.Threads={args.threads}",
        f"option.Hash={args.hash}",
        "option.UCI_LimitStrength=true",
        f"option.UCI_Elo={args.elo}",
        "-each",
        f"tc={args.tc}",
        "-openings",
        f"file={openings_path}",
        "format=epd",
        "order=random",
        "-rounds",
        str(args.rounds),
        "-repeat",
        "-concurrency",
        str(args.concurrency),
        "-draw",
        "movenumber=40",
        "movecount=8",
        "score=8",
        "-resign",
        "movecount=3",
        "score=600",
        "-ratinginterval",
        "1",
        "-pgnout",
        f"file={pgn_path}",
    ]

    print("\n[+] Launching fast-chess match ...")
    print(f"[+] PGN output will be saved to: {pgn_output}\n")

    # 3. Execute fast-chess and stream output
    proc = subprocess.Popen(
        cmd,
        cwd=str(REPO_ROOT),
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        bufsize=1,
        universal_newlines=True,
    )

    full_output = []
    elo_match = None
    games_match = None

    for line in proc.stdout:
        print(line, end="")
        full_output.append(line)

        # Regex match: Elo: +42.3 +/- 55.1 or Elo: -12.0 +/- 48.0
        m_elo = re.search(r"Elo:\s*([+-]?\d+(?:\.\d+)?)\s*\+/-\s*(\d+(?:\.\d+)?)", line)
        if m_elo:
            elo_match = (float(m_elo.group(1)), float(m_elo.group(2)))

        # Regex match: Games: 20, Wins: 11, Losses: 5, Draws: 4
        m_games = re.search(r"Games:\s*(\d+),\s*Wins:\s*(\d+),\s*Losses:\s*(\d+),\s*Draws:\s*(\d+)", line)
        if m_games:
            games_match = (
                int(m_games.group(1)),
                int(m_games.group(2)),
                int(m_games.group(3)),
                int(m_games.group(4)),
            )

    proc.wait()

    # 4. Display Final Summary Report
    print("\n" + "=" * 70)
    print("                    ANAK ELO EVALUATION REPORT")
    print("=" * 70)
    print(f"  Reference Opponent:       Stockfish 19 (Calibrated Elo: {args.elo})")

    if games_match:
        g, w, l, d = games_match
        pts = w + (0.5 * d)
        pct = (pts / g) * 100.0 if g > 0 else 0.0
        print(f"  Total Games Played:       {g}")
        print(f"  Score:                    {pts:g} / {g}  (Wins: {w}, Losses: {l}, Draws: {d})")
        print(f"  Performance Percentage:   {pct:.2f}%")

    if elo_match:
        delta_elo, err = elo_match
        estimated_elo = round(args.elo + delta_elo)
        print(f"  Elo Difference:           {delta_elo:+0.1f} +/- {err:.1f} (95% CI)")
        print("  " + "-" * 66)
        print(f"  >>> ESTIMATED ANAK ELO:   {estimated_elo} +/- {round(err)} <<<")
        print("  " + "-" * 66)
    else:
        print("  Elo Rating:               Calculated in tournament log above")

    print(f"  PGN Game Archive:         {pgn_output}")
    print("=" * 70 + "\n")


if __name__ == "__main__":
    main()
