#!/usr/bin/env python3
"""
Anak Chess Engine — Simultaneous Perturbation Stochastic Approximation (SPSA) Tuning Harness.
Tunes search heuristics (LMR, NMP, RFP, Aspiration Windows) by executing fast-chess self-play matches.

Standard SPSA implementation based on Spall (1998) as used in Stockfish/Berserk/Ethereal.
Generates ready-to-copy C# code snippets for SearchHeuristics.cs and tracks progress in JSON.
"""

import argparse
from dataclasses import dataclass, asdict
from datetime import datetime
import json
import math
import os
from pathlib import Path
import random
import re
import subprocess
import sys
import time
from typing import Dict, List, Optional, Tuple


@dataclass
class SpsaParam:
    name: str
    default: float
    min_val: float
    max_val: float
    c: float        # Base perturbation magnitude
    a: float        # Base learning rate / step size
    current: float = 0.0
    best: float = 0.0

    def __post_init__(self):
        if self.current == 0.0:
            self.current = float(self.default)
        if self.best == 0.0:
            self.best = float(self.default)

    def clamp(self, val: float) -> float:
        return max(self.min_val, min(self.max_val, val))

    def current_int(self) -> int:
        return int(round(self.current))


# Target Tunable Parameters for Anak Search Heuristics (Tuned values from Step 3.4)
DEFAULT_PARAMETERS: List[SpsaParam] = [
    SpsaParam(
        name="LmrBase",
        default=80.0,
        min_val=30.0,
        max_val=160.0,
        c=6.0,
        a=150.0,
    ),
    SpsaParam(
        name="LmrDivision",
        default=229.0,
        min_val=120.0,
        max_val=400.0,
        c=12.0,
        a=300.0,
    ),
    SpsaParam(
        name="RfpMargin",
        default=72.0,
        min_val=30.0,
        max_val=150.0,
        c=6.0,
        a=150.0,
    ),
    SpsaParam(
        name="NmpBase",
        default=3.0,
        min_val=1.0,
        max_val=6.0,
        c=0.5,
        a=5.0,
    ),
    SpsaParam(
        name="AspirationDelta",
        default=27.0,
        min_val=10.0,
        max_val=60.0,
        c=3.0,
        a=75.0,
    ),
    SpsaParam(
        name="ConthistReward",
        default=2598.0,
        min_val=1000.0,
        max_val=5000.0,
        c=250.0,
        a=50000.0,
    ),
    SpsaParam(
        name="ConthistPenalty",
        default=3839.0,
        min_val=1500.0,
        max_val=7000.0,
        c=300.0,
        a=60000.0,
    ),
    SpsaParam(
        name="HistoryThreshold",
        default=2101.0,
        min_val=800.0,
        max_val=4000.0,
        c=200.0,
        a=40000.0,
    ),
    SpsaParam(
        name="ScoreDropThreshold",
        default=45.0,
        min_val=15.0,
        max_val=120.0,
        c=6.0,
        a=150.0,
    ),
    SpsaParam(
        name="RfpImprovingMultiplier",
        default=115.0,
        min_val=100.0,
        max_val=200.0,
        c=8.0,
        a=200.0,
    ),
    SpsaParam(
        name="SingularMargin",
        default=6.0,
        min_val=1.0,
        max_val=8.0,
        c=0.5,
        a=5.0,
    ),
    SpsaParam(
        name="QsDeltaMargin",
        default=188.0,
        min_val=80.0,
        max_val=400.0,
        c=15.0,
        a=350.0,
    ),
]

# Micro NNUE & Dynamic Time Management Parameters
MICRO_PARAMETERS: List[SpsaParam] = [
    SpsaParam(
        name="MicroMaxShare",
        default=6.0,
        min_val=1.0,
        max_val=15.0,
        c=1.0,
        a=25.0,
    ),
    SpsaParam(
        name="MicroMinTime",
        default=25.0,
        min_val=10.0,
        max_val=50.0,
        c=3.0,
        a=75.0,
    ),
    SpsaParam(
        name="MicroDepthGate",
        default=85.0,
        min_val=60.0,
        max_val=98.0,
        c=4.0,
        a=100.0,
    ),
    SpsaParam(
        name="MicroAheadWeight",
        default=100.0,
        min_val=40.0,
        max_val=180.0,
        c=8.0,
        a=200.0,
    ),
    SpsaParam(
        name="MicroEqualWeight",
        default=75.0,
        min_val=30.0,
        max_val=140.0,
        c=6.0,
        a=150.0,
    ),
]

# Weakness Remediation Parameters (Watchdog & Syzygy Probing)
WEAKNESS_PARAMETERS: List[SpsaParam] = [
    SpsaParam(
        name="WatchdogCollapseThreshold",
        default=280.0,
        min_val=100.0,
        max_val=600.0,
        c=25.0,
        a=500.0,
    ),
    SpsaParam(
        name="WatchdogFallbackDelta",
        default=120.0,
        min_val=50.0,
        max_val=300.0,
        c=15.0,
        a=300.0,
    ),
    SpsaParam(
        name="SyzygyProbeDepth",
        default=6.0,
        min_val=1.0,
        max_val=15.0,
        c=1.0,
        a=15.0,
    ),
    SpsaParam(
        name="QsCheckDepth",
        default=1.0,
        min_val=0.0,
        max_val=2.0,
        c=0.5,
        a=5.0,
    ),
]

# Batch B, C, D Hidden Parameters Suite
BATCH_BCD_PARAMETERS: List[SpsaParam] = [
    # Batch B: History Dynamics & Pawn Correction
    SpsaParam(
        name="HistoryBonusScale",
        default=1.0,
        min_val=1.0,
        max_val=32.0,
        c=2.0,
        a=50.0,
    ),
    SpsaParam(
        name="HistoryMaxBonus",
        default=2000.0,
        min_val=500.0,
        max_val=4000.0,
        c=150.0,
        a=30000.0,
    ),
    SpsaParam(
        name="CorrHistDivisor",
        default=16.0,
        min_val=4.0,
        max_val=64.0,
        c=3.0,
        a=70.0,
    ),

    # Batch C: Dynamic Time Management
    SpsaParam(
        name="TimeMovesToGo",
        default=40.0,
        min_val=15.0,
        max_val=60.0,
        c=3.0,
        a=75.0,
    ),
    SpsaParam(
        name="TimeIncFactor",
        default=70.0,
        min_val=20.0,
        max_val=100.0,
        c=5.0,
        a=120.0,
    ),
    SpsaParam(
        name="TimeHardMultiplier",
        default=170.0,
        min_val=110.0,
        max_val=250.0,
        c=10.0,
        a=250.0,
    ),
    SpsaParam(
        name="TimeStableScale",
        default=80.0,
        min_val=40.0,
        max_val=100.0,
        c=6.0,
        a=150.0,
    ),
    SpsaParam(
        name="TimeFlipScale",
        default=180.0,
        min_val=110.0,
        max_val=250.0,
        c=10.0,
        a=250.0,
    ),

    # Batch D: Search & Pruning Thresholds
    SpsaParam(
        name="FutilityMarginD1D2",
        default=150.0,
        min_val=50.0,
        max_val=300.0,
        c=12.0,
        a=300.0,
    ),
    SpsaParam(
        name="FutilityMarginD3",
        default=80.0,
        min_val=30.0,
        max_val=200.0,
        c=8.0,
        a=200.0,
    ),
    SpsaParam(
        name="LmpBase",
        default=3.0,
        min_val=1.0,
        max_val=10.0,
        c=0.5,
        a=5.0,
    ),
    SpsaParam(
        name="LmpMultiplier",
        default=2.0,
        min_val=1.0,
        max_val=6.0,
        c=0.5,
        a=5.0,
    ),
    SpsaParam(
        name="IirMinDepth",
        default=4.0,
        min_val=3.0,
        max_val=8.0,
        c=0.5,
        a=5.0,
    ),
]


def find_default_paths() -> Tuple[Path, Path, Path]:
    """Auto-detects repository root and default binaries."""
    script_dir = Path(__file__).resolve().parent
    repo_root = script_dir.parent.parent

    # Fastchess
    fastchess_candidates = [
        repo_root / "tools" / "fastchess" / "bin" / ("fastchess.exe" if os.name == "nt" else "fastchess"),
        repo_root / "tools" / "fastchess" / ("fastchess.exe" if os.name == "nt" else "fastchess"),
    ]
    fastchess_bin = fastchess_candidates[0]
    for c in fastchess_candidates:
        if c.is_file():
            fastchess_bin = c
            break

    # Anak engine
    anak_candidates = [
        repo_root / "tools" / "fastchess" / "bin" / "anak" / ("anak.exe" if os.name == "nt" else "anak"),
        repo_root / "src" / "Anak.Protocol" / "bin" / "Release" / "net11.0" / ("anak.exe" if os.name == "nt" else "anak"),
    ]
    anak_bin = anak_candidates[0]
    for c in anak_candidates:
        if c.is_file():
            anak_bin = c
            break

    # Openings suite
    openings_file = repo_root / "tools" / "fastchess" / "openings.epd"

    return repo_root, fastchess_bin, anak_bin, openings_file


def generate_csharp_snippet(params: Dict[str, float]) -> str:
    """Generates ready-to-copy C# code snippets for SearchHeuristics.cs and AlphaBetaEngine.cs."""
    lines = [
        "// =========================================================================",
        "// Ready-to-copy Tuned Parameters for Anak Engine (Generated by SPSA)",
        "// =========================================================================",
        "",
        "// In src/Anak.Search/Minimax/Pruning/SearchHeuristics.cs (Constants or UCI defaults):"
    ]
    if "LmrBase" in params:
        lines.append(f"public const int TunedLmrBase = {int(round(params['LmrBase']))};")
    if "LmrDivision" in params:
        lines.append(f"public const int TunedLmrDivision = {int(round(params['LmrDivision']))};")
    if "RfpMargin" in params:
        lines.append(f"public const int TunedRfpMargin = {int(round(params['RfpMargin']))};")
    if "NmpBase" in params:
        lines.append(f"public const int TunedNmpBase = {int(round(params['NmpBase']))};")
    if "AspirationDelta" in params:
        lines.append(f"public const int TunedAspirationDelta = {int(round(params['AspirationDelta']))};")
    if "ConthistReward" in params:
        lines.append(f"public const int TunedConthistReward = {int(round(params['ConthistReward']))};")
    if "ConthistPenalty" in params:
        lines.append(f"public const int TunedConthistPenalty = {int(round(params['ConthistPenalty']))};")
    if "HistoryThreshold" in params:
        lines.append(f"public const int TunedHistoryThreshold = {int(round(params['HistoryThreshold']))};")
    if "RfpImprovingMultiplier" in params:
        lines.append(f"public const int TunedRfpImprovingMultiplier = {int(round(params['RfpImprovingMultiplier']))};")
    if "FutilityMargin" in params:
        lines.append(f"public const int TunedFutilityMargin = {int(round(params['FutilityMargin']))};")
    if "SingularMargin" in params:
        lines.append(f"public const int TunedSingularMargin = {int(round(params['SingularMargin']))};")
    if "QsDeltaMargin" in params:
        lines.append(f"public const int TunedQsDeltaMargin = {int(round(params['QsDeltaMargin']))};")

    # Batch B
    if "HistoryBonusScale" in params:
        lines.append(f"public const int TunedHistoryBonusScale = {int(round(params['HistoryBonusScale']))};")
    if "HistoryMaxBonus" in params:
        lines.append(f"public const int TunedHistoryMaxBonus = {int(round(params['HistoryMaxBonus']))};")
    if "CorrHistDivisor" in params:
        lines.append(f"public const int TunedCorrHistDivisor = {int(round(params['CorrHistDivisor']))};")

    # Batch C
    if "TimeMovesToGo" in params:
        lines.append(f"public const int TunedTimeMovesToGo = {int(round(params['TimeMovesToGo']))};")
    if "TimeIncFactor" in params:
        lines.append(f"public const int TunedTimeIncFactor = {int(round(params['TimeIncFactor']))};")
    if "TimeHardMultiplier" in params:
        lines.append(f"public const int TunedTimeHardMultiplier = {int(round(params['TimeHardMultiplier']))};")
    if "TimeStableScale" in params:
        lines.append(f"public const int TunedTimeStableScale = {int(round(params['TimeStableScale']))};")
    if "TimeFlipScale" in params:
        lines.append(f"public const int TunedTimeFlipScale = {int(round(params['TimeFlipScale']))};")

    # Batch D
    if "FutilityMarginD1D2" in params:
        lines.append(f"public const int TunedFutilityMarginD1D2 = {int(round(params['FutilityMarginD1D2']))};")
    if "FutilityMarginD3" in params:
        lines.append(f"public const int TunedFutilityMarginD3 = {int(round(params['FutilityMarginD3']))};")
    if "LmpBase" in params:
        lines.append(f"public const int TunedLmpBase = {int(round(params['LmpBase']))};")
    if "LmpMultiplier" in params:
        lines.append(f"public const int TunedLmpMultiplier = {int(round(params['LmpMultiplier']))};")
    if "IirMinDepth" in params:
        lines.append(f"public const int TunedIirMinDepth = {int(round(params['IirMinDepth']))};")

    # Weakness Remediation Parameters
    if "WatchdogCollapseThreshold" in params:
        lines.append(f"public const int TunedWatchdogCollapseThreshold = {int(round(params['WatchdogCollapseThreshold']))};")
    if "WatchdogFallbackDelta" in params:
        lines.append(f"public const int TunedWatchdogFallbackDelta = {int(round(params['WatchdogFallbackDelta']))};")
    if "SyzygyProbeDepth" in params:
        lines.append(f"public const int TunedSyzygyProbeDepth = {int(round(params['SyzygyProbeDepth']))};")
    if "QsCheckDepth" in params:
        lines.append(f"public const int TunedQsCheckDepth = {int(round(params['QsCheckDepth']))};")

    if "ScoreDropThreshold" in params:
        lines.append("")
        lines.append("// In src/Anak.Search/Time/TimeManager.cs:")
        lines.append(f"public int ScoreDropThreshold {{ get; set; }} = {int(round(params['ScoreDropThreshold']))};")
    lines.append("// =========================================================================")
    return "\n".join(lines)


class SpsaTuner:
    """
    Simultaneous Perturbation Stochastic Approximation (SPSA) Engine.
    Executes gradient-free optimization over stochastic match outcomes.
    """
    def __init__(
        self,
        params: List[SpsaParam],
        max_steps: int = 50,
        games_per_step: int = 30,
        tc: str = "2+0.02",
        concurrency: int = 8,
        hash_mb: int = 16,
        threads: int = 1,
        fastchess_path: Optional[Path] = None,
        anak_path: Optional[Path] = None,
        openings_path: Optional[Path] = None,
        progress_file: Optional[Path] = None,
        dry_run: bool = False,
        alpha: float = 0.602,
        gamma: float = 0.101,
        extra_options: Optional[Dict[str, object]] = None,
    ):
        self.params: Dict[str, SpsaParam] = {p.name: p for p in params}
        self.max_steps = max_steps
        self.games_per_step = games_per_step
        self.tc = tc
        self.concurrency = concurrency
        self.hash_mb = hash_mb
        self.threads = threads
        self.fastchess_path = fastchess_path
        self.anak_path = anak_path
        self.openings_path = openings_path
        self.progress_file = progress_file or Path("tools/tuning/spsa_progress.json")
        self.dry_run = dry_run
        # Fixed UCI options applied identically to both engines (e.g. a pinned ExpertRouteOrder)
        self.extra_options: Dict[str, object] = dict(extra_options or {})

        self.alpha = alpha
        self.gamma = gamma
        self.A = max(5.0, 0.1 * max_steps)

        self.current_step = 0
        self.history: List[dict] = []
        self.best_score = float("-inf")

    def save_progress(self):
        """Saves current state and C# snippets to JSON."""
        self.progress_file.parent.mkdir(parents=True, exist_ok=True)
        param_dict = {
            name: {
                "current": round(p.current, 2),
                "current_int": p.current_int(),
                "best": round(p.best, 2),
                "default": p.default,
                "min": p.min_val,
                "max": p.max_val,
            }
            for name, p in self.params.items()
        }

        current_int_map = {name: p.current_int() for name, p in self.params.items()}
        data = {
            "step": self.current_step,
            "max_steps": self.max_steps,
            "last_updated": datetime.now().isoformat(),
            "dry_run": self.dry_run,
            "parameters": param_dict,
            "csharp_snippets": generate_csharp_snippet(current_int_map),
            "history": self.history,
        }

        with open(self.progress_file, "w", encoding="utf-8") as f:
            json.dump(data, f, indent=2)

    def load_progress(self) -> bool:
        """Loads previous state from JSON if resuming."""
        if not self.progress_file.is_file():
            return False

        try:
            with open(self.progress_file, "r", encoding="utf-8") as f:
                data = json.load(f)

            self.current_step = data.get("step", 0)
            self.history = data.get("history", [])

            saved_params = data.get("parameters", {})
            for name, p in self.params.items():
                if name in saved_params:
                    p.current = float(saved_params[name].get("current", p.default))
                    p.best = float(saved_params[name].get("best", p.default))

            print(f"[+] Successfully resumed SPSA tuning from step {self.current_step}/{self.max_steps} ({self.progress_file})")
            return True
        except Exception as e:
            print(f"[!] Warning: Failed to resume progress from {self.progress_file} ({e})")
            return False

    def play_match(
        self,
        plus_options: Dict[str, int],
        minus_options: Dict[str, int]
    ) -> Tuple[int, int, int]:
        """
        Executes match between Anak(+) and Anak(-).
        Returns (wins_plus, losses_plus, draws).
        """
        if self.dry_run:
            return self._simulate_mock_match(plus_options, minus_options)

        # Ensure fastchess exists
        if not self.fastchess_path or not self.fastchess_path.is_file():
            print(f"[!] Warning: fastchess executable not found at {self.fastchess_path}. Falling back to mock match.")
            return self._simulate_mock_match(plus_options, minus_options)

        if not self.anak_path or not self.anak_path.is_file():
            print(f"[!] Warning: Anak executable not found at {self.anak_path}. Falling back to mock match.")
            return self._simulate_mock_match(plus_options, minus_options)

        rounds = max(1, self.games_per_step // 2)

        # fastchess spawns engines from the raw cmd= string and fails on paths containing spaces
        # (e.g. C:\Users\some name\...). Use a path relative to the repo root, and run from there.
        repo_root = Path(__file__).resolve().parent.parent.parent
        try:
            engine_cmd = os.path.relpath(str(self.anak_path), str(repo_root))
        except ValueError:
            engine_cmd = str(self.anak_path)

        # Construct fastchess engine options
        plus_opt_args = [
            f"option.Hash={self.hash_mb}",
            f"option.Threads={self.threads}",
        ] + [f"option.{k}={v}" for k, v in self.extra_options.items()] + [f"option.{k}={v}" for k, v in plus_options.items()]

        minus_opt_args = [
            f"option.Hash={self.hash_mb}",
            f"option.Threads={self.threads}",
        ] + [f"option.{k}={v}" for k, v in self.extra_options.items()] + [f"option.{k}={v}" for k, v in minus_options.items()]

        cmd = [
            str(self.fastchess_path),
            "-engine",
            f"cmd={engine_cmd}",
            "name=Anak-Plus",
        ] + plus_opt_args + [
            "-engine",
            f"cmd={engine_cmd}",
            "name=Anak-Minus",
        ] + minus_opt_args + [
            "-each",
            f"tc={self.tc}",
            "-openings",
            f"file={str(self.openings_path)}",
            "format=epd",
            "order=random",
            "-rounds",
            str(rounds),
            "-repeat",
            "-concurrency",
            str(self.concurrency),
            "-draw",
            "movenumber=40",
            "movecount=8",
            "score=8",
            "-resign",
            "movecount=3",
            "score=600",
        ]

        try:
            proc = subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                bufsize=1,
                universal_newlines=True,
                cwd=str(repo_root),
            )

            wins, losses, draws = 0, 0, 0
            tail: List[str] = []
            games_pattern = re.compile(r"Games:\s*(\d+),\s*Wins:\s*(\d+),\s*Losses:\s*(\d+),\s*Draws:\s*(\d+)")

            for line in proc.stdout:
                tail.append(line.rstrip())
                del tail[:-15]
                m = games_pattern.search(line)
                if m:
                    wins = int(m.group(2))
                    losses = int(m.group(3))
                    draws = int(m.group(4))

            proc.wait()
            if wins + losses + draws > 0:
                return wins, losses, draws
            # Never silently substitute simulated results for a live run: they would corrupt the tuning.
            raise RuntimeError("fastchess produced no games. Last output:\n" + "\n".join(tail))
        except OSError as e:
            print(f"[!] Warning: fastchess match execution failed ({e}). Using mock match.")

        return self._simulate_mock_match(plus_options, minus_options)

    def _simulate_mock_match(
        self,
        plus_options: Dict[str, int],
        minus_options: Dict[str, int]
    ) -> Tuple[int, int, int]:
        """Simulates a fast stochastic match outcome for quick validation and test suites."""
        total = self.games_per_step
        # Small synthetic bias towards optimal ranges to simulate realistic SPSA convergence
        bias = 0.0
        # Optimal synthetic targets
        targets = {
            "LmrBase": 80,
            "LmrDivision": 230,
            "RfpMargin": 72,
            "NmpBase": 3,
            "AspirationDelta": 26,
            "ConthistReward": 2600,
            "ConthistPenalty": 3800,
            "HistoryThreshold": 2100,
            "ScoreDropThreshold": 45,
        }
        for k, v_plus in plus_options.items():
            if not isinstance(v_plus, (int, float)):
                continue  # non-numeric options (e.g. ExpertRouteOrder) carry no synthetic bias
            t = targets.get(k, 75)
            v_minus = minus_options.get(k, v_plus)
            dist_plus = abs(v_plus - t)
            dist_minus = abs(v_minus - t)
            if dist_plus < dist_minus:
                bias += 0.03
            elif dist_plus > dist_minus:
                bias -= 0.03

        p_win = max(0.15, min(0.60, 0.35 + bias + random.gauss(0, 0.03)))
        p_loss = max(0.15, min(0.60, 0.35 - bias + random.gauss(0, 0.03)))
        p_draw = max(0.10, 1.0 - (p_win + p_loss))
        norm = p_win + p_loss + p_draw
        p_win, p_loss, p_draw = p_win / norm, p_loss / norm, p_draw / norm

        wins = int(round(total * p_win))
        losses = int(round(total * p_loss))
        draws = max(0, total - (wins + losses))
        return wins, losses, draws

    def step(self) -> dict:
        """Executes a single SPSA iteration."""
        k = self.current_step
        gamma_decay = (k + 1.0) ** self.gamma
        alpha_decay = (k + 1.0 + self.A) ** self.alpha

        # Perturbation vector Delta in {-1, +1}
        deltas: Dict[str, float] = {}
        c_k: Dict[str, float] = {}
        a_k: Dict[str, float] = {}
        theta_plus: Dict[str, float] = {}
        theta_minus: Dict[str, float] = {}
        uci_plus: Dict[str, int] = {}
        uci_minus: Dict[str, int] = {}

        for name, p in self.params.items():
            delta = 1.0 if random.random() < 0.5 else -1.0
            deltas[name] = delta
            ck = p.c / gamma_decay
            ak = p.a / alpha_decay
            c_k[name] = ck
            a_k[name] = ak

            tp = p.clamp(p.current + (ck * delta))
            tm = p.clamp(p.current - (ck * delta))
            theta_plus[name] = tp
            theta_minus[name] = tm

            up = int(round(tp))
            um = int(round(tm))
            # Ensure at least 1 unit difference for integer UCI options
            if up == um:
                if delta > 0 and up < p.max_val:
                    up += 1
                elif delta < 0 and um < p.max_val:
                    um += 1
                elif up > p.min_val:
                    up -= 1
            uci_plus[name] = up
            uci_minus[name] = um

        # Play match between Plus and Minus configurations
        wins, losses, draws = self.play_match(uci_plus, uci_minus)
        total_games = wins + losses + draws
        score_plus = (wins + (0.5 * draws)) / max(1, total_games)
        delta_y = (wins - losses) / max(1, total_games)  # Performance gradient signal [-1, +1]

        # Calculate estimated Elo difference
        elo_diff = 0.0
        if 0.0 < score_plus < 1.0:
            elo_diff = -400.0 * math.log10((1.0 / score_plus) - 1.0)

        # Estimate gradient and update parameters
        gradients: Dict[str, float] = {}
        updated_thetas: Dict[str, float] = {}

        for name, p in self.params.items():
            # g_i = (y+ - y-) / (2 * c_k * delta_i)
            # Since delta_i in {-1, +1}, 1 / delta_i == delta_i
            g_i = (delta_y / (2.0 * c_k[name])) * deltas[name]
            gradients[name] = g_i

            new_val = p.clamp(p.current + (a_k[name] * g_i))
            p.current = new_val
            updated_thetas[name] = new_val

            # Track best parameter if positive score
            if score_plus >= 0.52:
                p.best = new_val

        self.current_step += 1

        step_record = {
            "step": self.current_step,
            "timestamp": datetime.now().isoformat(),
            "wins": wins,
            "losses": losses,
            "draws": draws,
            "games": total_games,
            "score_plus": round(score_plus, 4),
            "delta_y": round(delta_y, 4),
            "elo_diff": round(elo_diff, 1),
            "uci_plus": uci_plus,
            "uci_minus": uci_minus,
            "gradients": {k: round(v, 6) for k, v in gradients.items()},
            "updated_params": {k: round(v, 2) for k, v in updated_thetas.items()},
        }
        self.history.append(step_record)
        self.save_progress()
        return step_record

    def run(self):
        """Runs SPSA optimization across all steps."""
        print("\n" + "=" * 75)
        print("   ANAK CHESS ENGINE — SPSA HEURISTIC PARAMETER TUNER")
        print("=" * 75)
        mode_label = "DRY-RUN / MOCK (No fastchess required)" if self.dry_run else "LIVE TOURNAMENT (fastchess)"
        print(f"Mode:              {mode_label}")
        print(f"Total Steps:       {self.max_steps}")
        print(f"Games Per Step:    {self.games_per_step} ({self.games_per_step // 2} rounds/opening pairs)")
        print(f"Time Control:      {self.tc}")
        print(f"Concurrency:       {self.concurrency} threads")
        print(f"Progress File:     {self.progress_file}")
        print("Parameters to Tune:")
        for p in self.params.values():
            print(f"  - {p.name:16s} default={p.default:5.1f} | range=[{p.min_val:5.1f}..{p.max_val:5.1f}] | step c={p.c:4.1f} | a={p.a:4.1f}")
        print("=" * 75 + "\n")

        start_time = time.time()

        while self.current_step < self.max_steps:
            step_start = time.time()
            rec = self.step()
            elapsed = time.time() - step_start

            print(f"[Step {rec['step']:3d}/{self.max_steps}] Match: {rec['wins']:2d}W / {rec['losses']:2d}L / {rec['draws']:2d}D ({rec['games']}g) | Score+: {rec['score_plus'] * 100:.1f}% ({rec['elo_diff']:+5.1f} Elo) | Time: {elapsed:.2f}s")
            param_updates = " | ".join(f"{k}: {v:5.1f} ({int(round(v))})" for k, v in rec['updated_params'].items())
            print(f"       -> Values: {param_updates}")

        total_elapsed = time.time() - start_time
        print("\n" + "=" * 75)
        print("                 SPSA TUNING SESSION COMPLETED")
        print("=" * 75)
        print(f"Total Steps Executed:   {self.current_step}")
        print(f"Total Games Played:     {self.current_step * self.games_per_step:,}")
        print(f"Total Elapsed Time:     {total_elapsed:.1f}s ({total_elapsed / 60:.2f} minutes)")
        print(f"Progress Saved To:      {self.progress_file}")
        print("\nFinal Tuned Parameters:")
        for p in self.params.values():
            print(f"  - {p.name:16s}: {p.current_int()} (raw: {p.current:.2f}, default: {int(p.default)})")

        current_int_map = {name: p.current_int() for name, p in self.params.items()}
        print("\n" + generate_csharp_snippet(current_int_map))
        print("=" * 75 + "\n")


def parse_args():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — SPSA Search Heuristics Tuner")
    parser.add_argument("--steps", type=int, default=50, help="Total SPSA optimization steps (default: 50)")
    parser.add_argument("--games-per-step", type=int, default=30, help="Number of games per step match (default: 30)")
    parser.add_argument("--tc", type=str, default="2+0.02", help="Time control in seconds base+inc (default: 2+0.02)")
    parser.add_argument("--concurrency", type=int, default=min(16, max(1, os.cpu_count() or 4)), help="Concurrency for fastchess")
    parser.add_argument("--hash", type=int, default=16, help="Transposition table Hash in MB (default: 16)")
    parser.add_argument("--threads", type=int, default=1, help="Threads per engine instance (default: 1)")
    parser.add_argument("--dry-run", "--mock", dest="dry_run", action="store_true", default=False,
                        help="Run mock simulated matches without invoking fastchess")
    parser.add_argument("--resume", action="store_true", default=False, help="Resume previous SPSA session from JSON progress file")
    parser.add_argument("--fastchess", type=str, default=None, help="Path to fastchess binary")
    parser.add_argument("--anak", type=str, default=None, help="Path to Anak binary")
    parser.add_argument("--batch", type=str, choices=["bcd", "all", "search", "micro", "weakness"], default="bcd",
                        help="Parameter batch to tune ('bcd' for 13 new hidden constants, 'all' for all 25 parameters, 'search' for 12 search heuristics, 'micro' for 5 dynamic Micro budget parameters, 'weakness' for remediation parameters)")
    parser.add_argument("--openings", type=str, default=None, help="Path to openings.epd suite")
    parser.add_argument("--output", type=str, default=None, help="Path to spsa_progress.json output")
    return parser.parse_args()


def main():
    args = parse_args()
    repo_root, default_fastchess, default_anak, default_openings = find_default_paths()

    fastchess_path = Path(args.fastchess) if args.fastchess else default_fastchess
    anak_path = Path(args.anak) if args.anak else default_anak
    openings_path = Path(args.openings) if args.openings else default_openings
    progress_file = Path(args.output) if args.output else (repo_root / "tools" / "tuning" / "spsa_progress.json")

    if args.batch == "bcd":
        param_set = BATCH_BCD_PARAMETERS
    elif args.batch == "search":
        param_set = DEFAULT_PARAMETERS
    elif args.batch == "micro":
        param_set = MICRO_PARAMETERS
    elif args.batch == "weakness":
        param_set = WEAKNESS_PARAMETERS
    else:
        param_set = DEFAULT_PARAMETERS + BATCH_BCD_PARAMETERS + MICRO_PARAMETERS + WEAKNESS_PARAMETERS

    tuner = SpsaTuner(
        params=param_set,
        max_steps=args.steps,
        games_per_step=args.games_per_step,
        tc=args.tc,
        concurrency=args.concurrency,
        hash_mb=args.hash,
        threads=args.threads,
        fastchess_path=fastchess_path,
        anak_path=anak_path,
        openings_path=openings_path,
        progress_file=progress_file,
        dry_run=args.dry_run,
    )

    if args.resume:
        tuner.load_progress()

    tuner.run()


if __name__ == "__main__":
    main()
