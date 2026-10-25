#!/usr/bin/env python3
"""
Anak Chess Engine — NNUE expert-routing tuner.

Run AFTER the NNUE experts are trained. Finds the rule order and thresholds used by
PawnTopology.ClassifyExpert that give the strongest engine, by playing fastchess matches.

Phase 1 (order):      every candidate ExpertRouteOrder is played against the default order
                      (EQIA) with all numeric thresholds at their defaults. Best order wins
                      only if it beats the baseline by --min-elo.
Phase 2 (thresholds): SPSA over the numeric routing options with the winning order pinned:
                      ExpertEndgameMaxPieces, ExpertImbalanceMargin, ExpertOpenThreshold,
                      ExpertAttackMinOpenness (the gate that sends closed, pawn-chain-locked
                      opposite-castling positions to Closed instead of Attack).

Order letters: E=Endgame, Q=Queenless, I=Imbalance, A=Attack. First matching rule wins;
omitted letters disable that rule; Open/Closed is always the final fallback.

Usage:
    python tools/tuning/tune_routing.py --dry-run                     # validate pipeline, no games
    python tools/tuning/tune_routing.py --games-per-order 200 --spsa-steps 60
    python tools/tuning/tune_routing.py --phase order --include-disabled
"""

import argparse
from dataclasses import dataclass
from datetime import datetime
import itertools
import json
import math
import os
from pathlib import Path
import sys
from typing import Dict, List, Optional, Tuple

sys.path.insert(0, str(Path(__file__).resolve().parent))
from spsa import SpsaParam, SpsaTuner, find_default_paths  # noqa: E402

DEFAULT_ORDER = "EQIA"

# Numeric routing options (UCI spin values; thresholds are in hundredths of openness).
ROUTING_PARAMETERS: List[SpsaParam] = [
    SpsaParam(name="ExpertEndgameMaxPieces", default=14.0, min_val=8.0, max_val=22.0, c=1.5, a=30.0),
    SpsaParam(name="ExpertImbalanceMargin", default=2.0, min_val=1.0, max_val=5.0, c=0.5, a=5.0),
    SpsaParam(name="ExpertOpenThreshold", default=50.0, min_val=30.0, max_val=70.0, c=4.0, a=100.0),
    SpsaParam(name="ExpertAttackMinOpenness", default=0.0, min_val=0.0, max_val=70.0, c=5.0, a=100.0),
]


@dataclass
class OrderResult:
    order: str
    wins: int
    losses: int
    draws: int
    elo: float
    elo_err: float

    @property
    def games(self) -> int:
        return self.wins + self.losses + self.draws


def elo_with_error(wins: int, losses: int, draws: int) -> Tuple[float, float]:
    """Elo difference and ~95% error margin from a W/L/D record."""
    n = wins + losses + draws
    if n == 0:
        return 0.0, float("inf")
    score = (wins + 0.5 * draws) / n
    score = min(max(score, 1e-3), 1 - 1e-3)
    elo = -400.0 * math.log10(1.0 / score - 1.0)
    var = (wins * (1 - score) ** 2 + losses * score ** 2 + draws * (0.5 - score) ** 2) / n
    stderr = math.sqrt(var / n)
    lo = max(score - 1.96 * stderr, 1e-3)
    hi = min(score + 1.96 * stderr, 1 - 1e-3)
    err = (-400.0 * math.log10(1.0 / hi - 1.0) - (-400.0 * math.log10(1.0 / lo - 1.0))) / 2.0
    return elo, err


def candidate_orders(include_disabled: bool, only: Optional[List[str]]) -> List[str]:
    if only:
        return [o.upper() for o in only]
    orders = ["".join(p) for p in itertools.permutations("EQIA")]
    if include_disabled:
        # Disable each single rule while keeping the default relative order
        for rule in DEFAULT_ORDER:
            orders.append(DEFAULT_ORDER.replace(rule, ""))
    return orders


def make_tuner(args, paths, params: List[SpsaParam], games: int, progress: Path,
               extra: Optional[Dict[str, object]] = None) -> SpsaTuner:
    _, fastchess, anak, openings = paths
    return SpsaTuner(
        params=params,
        max_steps=args.spsa_steps,
        games_per_step=games,
        tc=args.tc,
        concurrency=args.concurrency,
        hash_mb=args.hash,
        threads=args.threads,
        fastchess_path=Path(args.fastchess) if args.fastchess else fastchess,
        anak_path=Path(args.anak) if args.anak else anak,
        openings_path=Path(args.openings) if args.openings else openings,
        progress_file=progress,
        dry_run=args.dry_run,
        extra_options=extra,
    )


def run_order_phase(args, paths, out_dir: Path) -> Tuple[str, List[OrderResult]]:
    orders = [o for o in candidate_orders(args.include_disabled, args.orders) if o != DEFAULT_ORDER]
    print(f"\n=== Phase 1: order search ({len(orders)} candidates vs baseline {DEFAULT_ORDER}) ===")
    runner = make_tuner(args, paths, [], args.games_per_order, out_dir / "order_tmp.json")

    results: List[OrderResult] = []
    for i, order in enumerate(orders, 1):
        # Baseline runs the default order; candidate runs the tested one. Both use default thresholds.
        w, l, d = runner.play_match({"ExpertRouteOrder": order}, {"ExpertRouteOrder": DEFAULT_ORDER})
        elo, err = elo_with_error(w, l, d)
        res = OrderResult(order, w, l, d, elo, err)
        results.append(res)
        print(f"[{i:2d}/{len(orders)}] {order:5s} vs {DEFAULT_ORDER}: {w}W/{l}L/{d}D  {elo:+6.1f} +/- {err:4.1f} Elo")

    results.sort(key=lambda r: r.elo, reverse=True)
    best = results[0] if results else None
    if best and best.elo >= args.min_elo:
        chosen = best.order
        print(f"\n[+] Best order: {chosen} ({best.elo:+.1f} +/- {best.elo_err:.1f} Elo vs {DEFAULT_ORDER})")
    else:
        chosen = DEFAULT_ORDER
        print(f"\n[=] No order beat {DEFAULT_ORDER} by >= {args.min_elo} Elo; keeping {DEFAULT_ORDER}")
    return chosen, results


def run_threshold_phase(args, paths, order: str, out_dir: Path) -> Dict[str, int]:
    print(f"\n=== Phase 2: SPSA over routing thresholds (order pinned to {order}) ===")
    params = [SpsaParam(p.name, p.default, p.min_val, p.max_val, p.c, p.a) for p in ROUTING_PARAMETERS]
    tuner = make_tuner(args, paths, params, args.games_per_step, out_dir / "routing_spsa_progress.json",
                       extra={"ExpertRouteOrder": order})
    if args.resume:
        tuner.load_progress()
    tuner.run()
    return {name: p.current_int() for name, p in tuner.params.items()}


def write_report(out_dir: Path, order: str, order_results: List[OrderResult], thresholds: Dict[str, int], args):
    report = {
        "generated": datetime.now().isoformat(),
        "dry_run": args.dry_run,
        "baseline_order": DEFAULT_ORDER,
        "best_order": order,
        "order_results": [
            {"order": r.order, "wins": r.wins, "losses": r.losses, "draws": r.draws,
             "games": r.games, "elo": round(r.elo, 1), "elo_err": round(r.elo_err, 1)}
            for r in order_results
        ],
        "thresholds": thresholds,
        "uci_options": {"ExpertRouteOrder": order, **thresholds},
        "csharp": (
            f'new ExpertRouting(order: "{order}"'
            + (f", endgameMaxPieces: {thresholds['ExpertEndgameMaxPieces']}" if thresholds else "")
            + (f", imbalanceMargin: {thresholds['ExpertImbalanceMargin']}" if thresholds else "")
            + (f", openThreshold: {thresholds['ExpertOpenThreshold'] / 100.0:.2f}f" if thresholds else "")
            + (f", attackMinOpenness: {thresholds['ExpertAttackMinOpenness'] / 100.0:.2f}f" if thresholds else "")
            + ")"
        ),
    }
    path = out_dir / "routing_report.json"
    path.write_text(json.dumps(report, indent=2), encoding="utf-8")

    print("\n" + "=" * 75)
    print("   TUNED EXPERT ROUTING")
    print("=" * 75)
    print(f"setoption name ExpertRouteOrder value {order}")
    for name, val in thresholds.items():
        print(f"setoption name {name} value {val}")
    print(f"\nC# default: {report['csharp']}")
    print(f"Report:     {path}")
    print("=" * 75)


def parse_args():
    ap = argparse.ArgumentParser(description="Anak — NNUE expert routing tuner (order + thresholds)")
    ap.add_argument("--phase", choices=["order", "thresholds", "both"], default="both")
    ap.add_argument("--orders", nargs="*", default=None,
                    help="Explicit candidate orders (e.g. AEQI EAQI). Default: all 24 permutations")
    ap.add_argument("--include-disabled", action="store_true",
                    help="Also test orders with each single rule disabled (e.g. EQI = no Attack expert)")
    ap.add_argument("--order", default=None, help="Pin this order for the thresholds phase (skips order search)")
    ap.add_argument("--games-per-order", type=int, default=200, help="Games per candidate order vs baseline")
    ap.add_argument("--min-elo", type=float, default=3.0, help="Minimum Elo gain over EQIA to adopt a new order")
    ap.add_argument("--spsa-steps", type=int, default=60)
    ap.add_argument("--games-per-step", type=int, default=60)
    ap.add_argument("--tc", default="2+0.02")
    ap.add_argument("--concurrency", type=int, default=min(16, max(1, os.cpu_count() or 4)))
    ap.add_argument("--hash", type=int, default=16)
    ap.add_argument("--threads", type=int, default=1)
    ap.add_argument("--dry-run", "--mock", dest="dry_run", action="store_true",
                    help="Simulated matches, no fastchess required")
    ap.add_argument("--resume", action="store_true", help="Resume the threshold SPSA from its progress file")
    ap.add_argument("--fastchess", default=None)
    ap.add_argument("--anak", default=None)
    ap.add_argument("--openings", default=None)
    ap.add_argument("--output-dir", default=None, help="Directory for reports (default: tools/tuning)")
    return ap.parse_args()


def main():
    args = parse_args()
    paths = find_default_paths()
    out_dir = Path(args.output_dir) if args.output_dir else paths[0] / "tools" / "tuning"
    out_dir.mkdir(parents=True, exist_ok=True)

    order = (args.order or DEFAULT_ORDER).upper()
    order_results: List[OrderResult] = []
    thresholds: Dict[str, int] = {}

    if args.phase in ("order", "both") and not args.order:
        order, order_results = run_order_phase(args, paths, out_dir)
    if args.phase in ("thresholds", "both"):
        thresholds = run_threshold_phase(args, paths, order, out_dir)

    write_report(out_dir, order, order_results, thresholds, args)


if __name__ == "__main__":
    main()
