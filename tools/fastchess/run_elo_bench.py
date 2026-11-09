#!/usr/bin/env python3
"""
Anak Chess Engine — Automated Fast-Chess Elo Benchmark Runner (Windows / Cross-Platform)
Measures Anak's Elo rating compared to stored baselines (self-play regression/progression)
or calibrated Stockfish levels using fast-chess.
"""

import argparse
import io
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from pathlib import Path

# Add fastchess script dir to sys.path for elo_kit
SCRIPT_DIR = Path(__file__).resolve().parent
if str(SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(SCRIPT_DIR))
import elo_kit

# Add tools dir for manage_engines
TOOLS_DIR = SCRIPT_DIR.parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))
try:
    from manage_engines import get_engine_by_name, get_installed_path, load_registry
except ImportError:
    get_engine_by_name = None
    get_installed_path = None
    load_registry = None

REPO_ROOT = SCRIPT_DIR.parent.parent
BIN_DIR = SCRIPT_DIR / "bin"
RESULTS_DIR = SCRIPT_DIR / "results"
BASELINES_DIR = SCRIPT_DIR / "baselines"
RATINGS_FILE = SCRIPT_DIR / "ratings.json"
BENCHMARKS_MD = REPO_ROOT / "BENCHMARKS.md"
OPENINGS_FILE = SCRIPT_DIR / "openings.epd"
ANAK_PUBLISH_DIR = BIN_DIR / "anak"
ANAK_EXE = ANAK_PUBLISH_DIR / ("anak.exe" if os.name == "nt" else "anak")
FASTCHESS_EXE = BIN_DIR / ("fastchess.exe" if os.name == "nt" else "fastchess")
STOCKFISH_EXE = BIN_DIR / ("stockfish.exe" if os.name == "nt" else "stockfish")

FASTCHESS_WINDOWS_URL = "https://github.com/Disservin/fastchess/releases/download/v1.8.2-alpha/fastchess-windows-x86-64.zip"
STOCKFISH_WINDOWS_URL = "https://github.com/official-stockfish/Stockfish/releases/download/sf_19/stockfish-windows-x86-64-universal.zip"


def load_ratings() -> dict:
    if RATINGS_FILE.exists():
        try:
            with open(RATINGS_FILE, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception as e:
            print(f"[!] Warning: Failed to load ratings.json ({e})")
    return {"versions": {}}


def save_ratings(data: dict):
    RATINGS_FILE.parent.mkdir(parents=True, exist_ok=True)
    with open(RATINGS_FILE, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=2)
    print(f"[+] Updated ratings registry: {RATINGS_FILE}")


def update_benchmarks_md(ratings: dict):
    if not BENCHMARKS_MD.exists():
        return

    try:
        content = BENCHMARKS_MD.read_text(encoding="utf-8")
        table_header = "| Version | Release Date | Estimated Elo | 95% CI | Reference Opponent | Score | W / L / D | TC | PGN Archive |\n| :--- | :---: | :---: | :---: | :--- | :---: | :---: | :---: | :--- |"
        
        rows = []
        for ver, data in ratings.get("versions", {}).items():
            tag = data.get("version", ver)
            date = data.get("date", "-")
            elo = data.get("estimated_elo", "-")
            margin = data.get("elo_margin")
            ci = f"&plusmn;{margin}" if margin is not None else "-"
            opp = data.get("baseline_opponent", "-")
            score = data.get("score", "-")
            perf = f" ({data['perf_percent']}%)" if "perf_percent" in data else ""
            score_str = f"{score}{perf}"
            w = data.get("wins", 0)
            l = data.get("losses", 0)
            d = data.get("draws", 0)
            wld = f"{w} / {l} / {d}"
            tc = data.get("tc", "-")
            pgn = data.get("pgn", "-")
            pgn_str = f"[`{Path(pgn).name}`]({pgn})" if pgn != "-" else "-"
            rows.append(f"| [**{tag}**](https://github.com/laerens/anak/releases/tag/{tag}) | {date} | **{elo}** | {ci} | {opp} | {score_str} | {wld} | {tc} | {pgn_str} |")

        new_table = table_header + "\n" + "\n".join(rows) + "\n"
        
        # Replace existing table in BENCHMARKS.md between table header and next blank line / section
        pattern = r"\| Version \| Release Date \| Estimated Elo \|.*?\n(?=\n|\Z|##)"
        if re.search(pattern, content, re.DOTALL):
            content = re.sub(pattern, new_table.strip(), content, flags=re.DOTALL)
            BENCHMARKS_MD.write_text(content, encoding="utf-8")
            print(f"[+] Updated benchmark markdown: {BENCHMARKS_MD}")
    except Exception as e:
        print(f"[!] Warning: Failed to update BENCHMARKS.md ({e})")


def get_available_baselines() -> list[str]:
    if not BASELINES_DIR.exists():
        return []
    baselines = []
    for d in BASELINES_DIR.iterdir():
        if d.is_dir():
            exe = d / ("anak.exe" if os.name == "nt" else "anak")
            if exe.exists():
                baselines.append(d.name)

    def version_key(name: str):
        nums = [int(n) for n in re.findall(r"\d+", name)]
        return (1 if name.startswith("v") else 0, nums, name)

    return sorted(baselines, key=version_key)


def archive_baseline(version_tag: str, elo: int = None, margin: int = None, opponent: str = None, notes: str = "", profile: str = None):
    target_dir = BASELINES_DIR / version_tag
    target_dir.mkdir(parents=True, exist_ok=True)

    print(f"[+] Archiving current Anak build as baseline '{version_tag}' in {target_dir} ...")
    cmd = [
        "dotnet",
        "publish",
        str(REPO_ROOT / "src" / "Anak.Protocol" / "Anak.Protocol.csproj"),
        "-c",
        "Release",
    ]
    if profile:
        cmd.append(f"-p:PublishProfile={profile}")
    cmd.extend([
        "-o",
        str(target_dir),
    ])
    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        raise RuntimeError("Failed to build and archive baseline Anak binary.")

    exe_path = target_dir / ("anak.exe" if os.name == "nt" else "anak")
    if not exe_path.exists():
        raise RuntimeError(f"Expected binary {exe_path} was not created.")

    print(f"[+] Baseline '{version_tag}' archived successfully at: {exe_path}")

    if elo is not None:
        ratings = load_ratings()
        ratings.setdefault("versions", {})[version_tag] = {
            "version": version_tag,
            "date": time.strftime("%Y-%m-%d"),
            "estimated_elo": elo,
            "elo_margin": margin,
            "baseline_opponent": opponent or "Baseline",
            "notes": notes,
        }
        save_ratings(ratings)
        update_benchmarks_md(ratings)


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
        for candidate in BIN_DIR.rglob("fastchess.exe"):
            if candidate != FASTCHESS_EXE:
                shutil.copy2(candidate, FASTCHESS_EXE)
                break
    else:
        raise RuntimeError("Please install fast-chess on non-Windows platforms or place fastchess binary in tools/fastchess/bin/")


def ensure_stockfish():
    if STOCKFISH_EXE.exists():
        return

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
        for candidate in BIN_DIR.rglob("*.exe"):
            if "stockfish" in candidate.name.lower() and candidate != STOCKFISH_EXE:
                shutil.copy2(candidate, STOCKFISH_EXE)
                print(f"[+] Configured Stockfish at {STOCKFISH_EXE}")
                break
    else:
        raise RuntimeError("Please install Stockfish on non-Windows platforms or place stockfish binary in tools/fastchess/bin/")


def ensure_anak(skip_build: bool = False, profile: str = None):
    if skip_build and ANAK_EXE.exists():
        return

    print(f"[+] Building and publishing Anak in Release configuration{(f' (profile: {profile})') if profile else ''} ...")
    cmd = [
        "dotnet",
        "publish",
        str(REPO_ROOT / "src" / "Anak.Protocol" / "Anak.Protocol.csproj"),
        "-c",
        "Release",
    ]
    if profile:
        cmd.append(f"-p:PublishProfile={profile}")
    cmd.extend([
        "-o",
        str(ANAK_PUBLISH_DIR),
    ])
    res = subprocess.run(cmd, cwd=str(REPO_ROOT))
    if res.returncode != 0:
        raise RuntimeError("Failed to build Anak. Please ensure .NET SDK is installed.")
    print(f"[+] Anak executable ready: {ANAK_EXE}")


def parse_args():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — Fast-Chess Elo Benchmark Runner")
    parser.add_argument("--opponent", type=str, choices=["baseline", "stockfish", "reference"], default=None,
                        help="Benchmark opponent type ('baseline', 'stockfish', or 'reference'). Defaults to 'baseline' if a baseline is archived, else 'stockfish'.")
    parser.add_argument("--engine", "--reference-engine", dest="reference_engine", type=str, default=None,
                        help="Specific reference engine name or fuzzy match from registry when --opponent reference is selected (e.g. 'foxsee', 'FoxSEE-8.2').")
    parser.add_argument("--baseline", type=str, default="latest",
                        help="Specific baseline version to benchmark against (default: 'latest')")
    parser.add_argument("--elo", type=int, default=1800,
                        help="Target Stockfish Elo rating when benchmarking against Stockfish (default: 1800, range: 1320-3190)")
    parser.add_argument("--games", "--max-games", dest="games", type=int, default=None,
                        help="Total number of games to play (default: 1000). Sets rounds = games // 2 with color reversal.")
    parser.add_argument("--rounds", type=int, default=None,
                        help="Number of opening pairs (each played twice with swapped colors, default: 500 = 1000 games)")
    parser.add_argument("--tc", type=str, default="3+0.05",
                        help="Time control base+inc in seconds (default: 3+0.05 for high-volume 1000-game matches)")
    parser.add_argument("--nodes", type=int, default=None,
                        help="Fixed node limit per move (e.g. 50000) for deterministic hardware-independent benchmarking")
    parser.add_argument("--candidate-nnue-dir", type=str, default=None,
                        help="Path to candidate .nnue networks directory (passed as option.NnuePath to candidate Anak)")
    total_cpus = os.cpu_count() or 2
    default_concurrency = max(1, (total_cpus // 2) - 2) if total_cpus > 8 else max(1, total_cpus // 2)
    parser.add_argument("--concurrency", type=int, default=default_concurrency,
                        help=f"Number of concurrent games (default: {default_concurrency} leaving headroom for OS and 2 engines/game)")
    parser.add_argument("--hash", type=int, default=64,
                        help="Transposition Table Hash in MB per engine (default: 64)")
    parser.add_argument("--threads", type=int, default=1,
                        help="Search threads per engine (default: 1)")
    parser.add_argument("--quick", action="store_true",
                        help="Quick smoke test: 25 rounds (50 games) at 3+0.05 time control")
    parser.add_argument("--sprt", nargs="*", default=None, metavar=("ELO0", "ELO1"),
                        help="Enable SPRT (Sequential Probability Ratio Test) with bounds [elo0, elo1] (default: 0.0 3.0)")
    parser.add_argument("--model", type=str, choices=["normalized", "logistic", "bayesian"], default="normalized",
                        help="Elo model for SPRT and evaluation ('normalized' for Fishtest nElo, 'logistic' for FIDE Elo). Default: 'normalized'.")
    parser.add_argument("--early-stop", action="store_true",
                        help="Abort match early if statistically negligible chance of positive Elo (e.g. upper 95%% CI < 0)")
    parser.add_argument("--rebuild", action="store_true",
                        help="Force rebuild of Anak (candidate build) before benchmark (enabled by default)")
    parser.add_argument("--no-build", action="store_true",
                        help="Skip rebuilding Anak if binary already exists")
    parser.add_argument("--archive-baseline", type=str, default=None, metavar="TAG",
                        help="Archive the current engine build as a named baseline (e.g. 'v0.0.1') and exit")
    parser.add_argument("--record", type=str, default=None, metavar="VERSION_TAG",
                        help="Record benchmark results into ratings.json & BENCHMARKS.md under the given version tag")
    parser.add_argument("--notes", type=str, default="",
                        help="Optional notes when archiving or recording a version")
    parser.add_argument("--profile", type=str, default=None,
                        help="Publish profile to use when building Anak (e.g. win-x64-avx512, win-x64-avx2, win-x64-generic)")
    return parser.parse_args()


def main():
    args = parse_args()

    # Handle --archive-baseline mode
    if args.archive_baseline:
        archive_baseline(args.archive_baseline, elo=args.elo if "--elo" in sys.argv else None, notes=args.notes, profile=args.profile)
        return

    available_baselines = get_available_baselines()

    # Determine opponent
    if args.opponent is None:
        args.opponent = "baseline" if available_baselines else "stockfish"

    if args.opponent == "baseline" and not available_baselines:
        print("[!] No baseline versions found in tools/fastchess/baselines/.")
        print("    Falling back to Stockfish-1800. (Archive a baseline with --archive-baseline <version>)")
        args.opponent = "stockfish"

    # Determine number of rounds (1 round = 2 games with color reversal)
    if args.quick:
        args.rounds = 25
        print("[!] Quick smoke mode enabled: 25 rounds (50 games) at 3+0.05")
    elif args.games is not None:
        args.rounds = max(1, args.games // 2)
    elif args.rounds is None:
        args.rounds = 500  # Default: 500 rounds = 1000 games!

    total_games = args.rounds * 2
    ratings = load_ratings()

    if args.reference_engine and args.opponent is None:
        args.opponent = "reference"
    elif args.opponent is None:
        args.opponent = "baseline" if len(available_baselines) > 0 else "stockfish"

    # Resolve baseline details if applicable
    baseline_tag = None
    baseline_exe = None
    baseline_elo = 1600
    ref_engine = None
    ref_installed = None

    if args.opponent == "baseline":
        if args.baseline == "latest":
            baseline_tag = available_baselines[-1]
        else:
            baseline_tag = args.baseline
            if baseline_tag not in available_baselines:
                print(f"[!] Error: Baseline '{baseline_tag}' not found in tools/fastchess/baselines/.")
                print(f"    Available baselines: {', '.join(available_baselines)}")
                sys.exit(1)

        baseline_exe = BASELINES_DIR / baseline_tag / ("anak.exe" if os.name == "nt" else "anak")
        if not baseline_exe.exists():
            print(f"[!] Error: Baseline executable {baseline_exe} does not exist.")
            sys.exit(1)

        # Lookup baseline's known Elo from ratings.json
        version_meta = ratings.get("versions", {}).get(baseline_tag, {})
        baseline_elo = version_meta.get("estimated_elo", 1616)
        opponent_label = f"Anak {baseline_tag} (Known Elo: {baseline_elo})"
    elif args.opponent == "reference":
        target_name = args.reference_engine or "FoxSEE-8.2"
        if get_engine_by_name:
            ref_engine = get_engine_by_name(target_name)
        if not ref_engine and load_registry:
            for t in load_registry():
                for e in t.get("engines", []):
                    if target_name.lower() in e["name"].lower():
                        ref_engine = e
                        break
                if ref_engine:
                    break
        if not ref_engine:
            print(f"[!] Error: Reference engine '{target_name}' not found in tools/engines_registry.json.")
            print(f"    Run 'python tools/manage_engines.py list' to view available reference engines.")
            sys.exit(1)
        ref_installed = get_installed_path(ref_engine) if get_installed_path else None
        if not ref_installed:
            print(f"[!] Error: Reference engine '{ref_engine['name']}' binary '{ref_engine.get('binary_name')}' is not installed.")
            print(f"    Place the binary in tools/reference_engines/ (Run 'python tools/manage_engines.py' for details).")
            sys.exit(1)
        baseline_elo = ref_engine["known_elo"]
        opponent_label = f"{ref_engine['name']} (CCRL Elo: ~{baseline_elo} | Style: {ref_engine.get('style', 'Standard')})"
    else:
        opponent_label = f"Stockfish 19 (Calibrated Elo: {args.elo})"

    print("=" * 70)
    print("                    ANAK ELO BENCHMARK RUNNER")
    print("=" * 70)
    print(f"  Candidate:            Anak (Current Dev Build)")
    print(f"  Opponent:             {opponent_label}")
    print(f"  Rounds:               {args.rounds} opening pairs ({total_games} total games)")
    print(f"  Time Control:         {args.tc} (seconds + increment)")
    print(f"  Concurrency:          {args.concurrency} parallel games")
    print(f"  Hash Table Size:      {args.hash} MB per engine")
    print(f"  Opening Suite:        {OPENINGS_FILE.name}")
    print("=" * 70)

    # Ensure required binaries
    ensure_anak(skip_build=args.no_build, profile=args.profile)
    ensure_fastchess()
    if args.opponent == "stockfish":
        ensure_stockfish()

    RESULTS_DIR.mkdir(parents=True, exist_ok=True)
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    
    if args.opponent == "baseline":
        pgn_output = RESULTS_DIR / f"anak_dev_vs_{baseline_tag}_{timestamp}.pgn"
        opp_name = f"Anak-{baseline_tag}"
        opp_cmd = baseline_exe.name
        opp_dir = str(os.path.relpath(baseline_exe.parent, REPO_ROOT))
        syzygy_abs = str((REPO_ROOT / "syzygy").resolve())
        opp_options = [
            f"option.Threads={args.threads}",
            f"option.Hash={args.hash}",
            f"option.SyzygyPath={syzygy_abs}",
        ]
    elif args.opponent == "reference":
        clean_slug = re.sub(r"[^a-zA-Z0-9_]", "_", ref_engine["name"].lower())
        pgn_output = RESULTS_DIR / f"anak_vs_{clean_slug}_{timestamp}.pgn"
        opp_name = ref_engine["name"]
        opp_cmd = ref_installed.name
        opp_dir = str(os.path.relpath(ref_installed.parent, REPO_ROOT))
        opp_options = [
            f"option.Hash={args.hash}",
        ]
        for k, v in ref_engine.get("options", {}).items():
            opp_options.append(f"option.{k}={v}")
    else:
        pgn_output = RESULTS_DIR / f"anak_vs_sf{args.elo}_{timestamp}.pgn"
        opp_name = f"Stockfish-{args.elo}"
        opp_cmd = STOCKFISH_EXE.name
        opp_dir = str(os.path.relpath(STOCKFISH_EXE.parent, REPO_ROOT))
        opp_options = [
            f"option.Threads={args.threads}",
            f"option.Hash={args.hash}",
            "option.UCI_LimitStrength=true",
            f"option.UCI_Elo={args.elo}",
        ]

    fastchess_cmd = str(os.path.relpath(FASTCHESS_EXE, REPO_ROOT))
    anak_cmd = ANAK_EXE.name
    anak_dir = str(os.path.relpath(ANAK_EXE.parent, REPO_ROOT))
    openings_path = str(os.path.relpath(OPENINGS_FILE, REPO_ROOT))
    pgn_path = str(os.path.relpath(pgn_output, REPO_ROOT))

    # Build fast-chess command
    syzygy_abs = str((REPO_ROOT / "syzygy").resolve())
    anak_dev_options = [
        "-engine",
        f"cmd={anak_cmd}",
        f"dir={anak_dir}",
        "name=Anak-Dev",
        f"option.Threads={args.threads}",
        f"option.Hash={args.hash}",
        f"option.SyzygyPath={syzygy_abs}",
    ]
    if args.candidate_nnue_dir:
        cand_dir_abs = os.path.abspath(args.candidate_nnue_dir)
        anak_dev_options.append(f"option.NnuePath={cand_dir_abs}")

    each_limit = f"nodes={args.nodes}" if args.nodes else f"tc={args.tc}"

    cmd = [
        fastchess_cmd,
    ] + anak_dev_options + [
        "-engine",
        f"cmd={opp_cmd}",
        f"dir={opp_dir}",
        f"name={opp_name}",
    ] + opp_options + [
        "-each",
        each_limit,
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
        "10" if args.rounds >= 50 else "1",
        "-pgnout",
        f"file={pgn_path}",
    ]

    if args.sprt is not None:
        elo0 = args.sprt[0] if len(args.sprt) > 0 else "0.0"
        elo1 = args.sprt[1] if len(args.sprt) > 1 else "3.0"
        cmd.extend(["-sprt", f"elo0={elo0}", f"elo1={elo1}", f"model={args.model}", "alpha=0.05", "beta=0.05"])

    print("\n[+] Launching fast-chess match ...")
    print(f"[+] PGN output will be saved to: {pgn_output}\n")

    # Execute fast-chess and stream output
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

        m_elo = re.search(r"Elo:\s*([+-]?\d+(?:\.\d+)?)\s*\+/-\s*(\d+(?:\.\d+)?)", line)
        if m_elo:
            elo_match = (float(m_elo.group(1)), float(m_elo.group(2)))

        m_games = re.search(r"Games:\s*(\d+),\s*Wins:\s*(\d+),\s*Losses:\s*(\d+),\s*Draws:\s*(\d+)", line)
        if m_games:
            games_match = (
                int(m_games.group(1)),
                int(m_games.group(2)),
                int(m_games.group(3)),
                int(m_games.group(4)),
            )

            if args.early_stop and elo_match:
                g = games_match[0]
                delta_elo, err = elo_match
                # Stop early if regression is statistically clear
                # e.g. after >= 500 games, if upper 95% CI is negative (delta_elo + err < 0.0)
                # or after >= 1000 games, if upper 95% CI < 1.0 Elo
                if (g >= 500 and (delta_elo + err) < 0.0) or (g >= 1000 and (delta_elo + err) < 1.0):
                    print(f"\n[!] EARLY STOP: After {g} games, Elo is {delta_elo:+.1f} +/- {err:.1f} (Upper 95% CI: {delta_elo+err:+.1f}).")
                    print("[!] Negligible chance of positive Elo; terminating match early.\n")
                    if os.name == "nt":
                        subprocess.run(["taskkill", "/F", "/T", "/PID", str(proc.pid)], capture_output=True)
                    else:
                        proc.kill()
                    break

    proc.wait()

    # Display Final Summary Report
    reference_elo = baseline_elo if args.opponent == "baseline" else args.elo
    estimated_elo = None
    err_margin = None

    print("\n" + "=" * 70)
    print("                    ANAK ELO EVALUATION REPORT")
    print("=" * 70)
    print(f"  Reference Opponent:       {opponent_label}")

    w, l, d, pts, pct = 0, 0, 0, 0.0, 0.0
    elo_res = None
    if pgn_output.exists():
        try:
            elo_res = elo_kit.parse_fastchess_pgn(pgn_output, "Anak-Dev")
            w, l, d = elo_res.wins, elo_res.losses, elo_res.draws
            pts = w + (0.5 * d)
            g = elo_res.games
            pct = elo_res.score * 100.0
            print(f"  Total Games Played:       {g}")
            print(f"  Score:                    {pts:g} / {g}  (Wins: {w}, Losses: {l}, Draws: {d} | Draw Rate: {elo_res.draw_rate*100:.1f}%)")
            print(f"  Performance Percentage:   {pct:.2f}%")
            if elo_res.pentanomial and elo_res.pairs_count > 0:
                ll, ld, ddwl, wd, ww = elo_res.pentanomial
                print(f"  Pentanomial Pairs ({elo_res.pairs_count}): [LL: {ll}, LD: {ld}, DD/WL: {ddwl}, WD: {wd}, WW: {ww}]")
        except Exception as e:
            print(f"[!] Warning: elo_kit PGN parsing failed ({e})")

    if elo_res is None and games_match:
        g, w, l, d = games_match
        pts = w + (0.5 * d)
        pct = (pts / g) * 100.0 if g > 0 else 0.0
        print(f"  Total Games Played:       {g}")
        print(f"  Score:                    {pts:g} / {g}  (Wins: {w}, Losses: {l}, Draws: {d})")
        print(f"  Performance Percentage:   {pct:.2f}%")

    if elo_res:
        delta_nelo = elo_res.nelo
        err_nelo = elo_res.nelo_err
        delta_log = elo_res.logistic_elo
        err_log = elo_res.logistic_err

        err_margin = round(err_nelo if args.model == "normalized" else err_log)
        delta_used = delta_nelo if args.model == "normalized" else delta_log
        estimated_elo = round(reference_elo + delta_used)

        print(f"  Normalized Elo (nElo):    {delta_nelo:+0.2f} +/- {err_nelo:.2f} (95% CI)")
        print(f"  Logistic Elo:             {delta_log:+0.2f} +/- {err_log:.2f} (95% CI)")
        print("  " + "-" * 66)
        print(f"  >>> ESTIMATED ANAK ELO:   {estimated_elo} +/- {err_margin} ({args.model}) <<<")
        print("  " + "-" * 66)
    elif elo_match:
        delta_elo, err = elo_match
        err_margin = round(err)
        estimated_elo = round(reference_elo + delta_elo)
        print(f"  Elo Difference:           {delta_elo:+0.1f} +/- {err:.1f} (95% CI)")
        print("  " + "-" * 66)
        print(f"  >>> ESTIMATED ANAK ELO:   {estimated_elo} +/- {err_margin} <<<")
        print("  " + "-" * 66)
    else:
        print("  Elo Rating:               Calculated in tournament log above")

    print(f"  PGN Game Archive:         {pgn_output}")
    print("=" * 70 + "\n")

    # Record result if requested
    if args.record and estimated_elo is not None:
        tag = args.record
        record_entry = {
            "version": tag,
            "date": time.strftime("%Y-%m-%d"),
            "estimated_elo": estimated_elo,
            "elo_margin": err_margin,
            "baseline_opponent": opponent_label,
            "score": f"{pts:g} / {g}",
            "wins": w,
            "losses": l,
            "draws": d,
            "perf_percent": round(pct, 2),
            "tc": args.tc,
            "pgn": str(pgn_output.relative_to(REPO_ROOT)).replace("\\", "/"),
            "notes": args.notes or f"Benchmarked against {opponent_label}",
        }
        if elo_res:
            record_entry["nelo"] = round(elo_res.nelo, 2)
            record_entry["nelo_margin"] = round(elo_res.nelo_err, 2)
            record_entry["draw_rate"] = round(elo_res.draw_rate, 4)
            if elo_res.pentanomial:
                record_entry["pentanomial"] = list(elo_res.pentanomial)
        ratings.setdefault("versions", {})[tag] = record_entry
        save_ratings(ratings)
        update_benchmarks_md(ratings)


if __name__ == "__main__":
    main()
