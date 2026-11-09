#!/usr/bin/env python3
"""
Anak Chess Engine — Elo Determination & Statistical Calibration Kit
Provides rigorous Elo calculation models:
  - Normalized Elo (nElo) with pentanomial & trinomial error modeling (fastchess / Fishtest standard)
  - Traditional Logistic Elo with 95% Confidence Intervals
  - Multi-Engine Gauntlet Anchor Calibration (Bradley-Terry Maximum Likelihood)
  - Fastchess / Cutechess PGN Tournament Parsing & Analysis
"""

import argparse
from dataclasses import dataclass
import math
import os
from pathlib import Path
import re
import sys
from typing import Dict, List, Optional, Tuple

# Fishtest / fastchess constant: 200 * sqrt(2) / ln(10)
NELO_SCALE = 200.0 * math.sqrt(2.0) / math.log(10.0)  # ~122.871565


@dataclass
class EloResult:
    score: float
    games: int
    wins: int
    losses: int
    draws: int
    draw_rate: float
    nelo: float
    nelo_err: float
    logistic_elo: float
    logistic_err: float
    pairs_count: int = 0
    pentanomial: Optional[Tuple[int, int, int, int, int]] = None  # (LL, LD, DD/WL, WD, WW)

    def summary(self) -> str:
        lines = [
            f"Games: {self.games} | W: {self.wins} | L: {self.losses} | D: {self.draws} ({self.draw_rate*100:.1f}% draws)",
            f"Score: {self.score*100:.2f}% ({self.wins + 0.5*self.draws:g} / {self.games})",
            f"Normalized Elo (nElo): {self.nelo:+.2f} +/- {self.nelo_err:.2f} (95% CI)",
            f"Logistic Elo:          {self.logistic_elo:+.2f} +/- {self.logistic_err:.2f} (95% CI)",
        ]
        if self.pentanomial and self.pairs_count > 0:
            ll, ld, ddwl, wd, ww = self.pentanomial
            lines.append(f"Pentanomial Pairs ({self.pairs_count}): [LL: {ll}, LD: {ld}, DD/WL: {ddwl}, WD: {wd}, WW: {ww}]")
        return "\n".join(lines)


def calculate_trinomial_nelo(wins: int, losses: int, draws: int) -> Tuple[float, float]:
    """
    Computes Normalized Elo and 95% confidence margin from trinomial outcomes (Wins, Losses, Draws).
    Follows Fishtest / fastchess normalized Elo definition.
    """
    n = wins + losses + draws
    if n == 0:
        return 0.0, float("inf")

    p_w = wins / n
    p_l = losses / n
    p_d = draws / n

    # Score on [-1, +1] scale
    mu = p_w - p_l

    # Variance on [-1, +1] scale: Var(X) = E[X^2] - (E[X])^2 = (p_w + p_l) - mu^2
    var = (p_w + p_l) - (mu * mu)
    if var <= 1e-9:
        sigma = 1e-4
    else:
        sigma = math.sqrt(var)

    nelo = NELO_SCALE * (mu / sigma)

    # Standard error of nElo: SE = (NELO_SCALE / sqrt(N)) * sqrt(1 + mu^2 / (2 * var))
    var_rel = 1.0 + (mu * mu) / (2.0 * max(1e-9, var))
    se_nelo = (NELO_SCALE / math.sqrt(n)) * math.sqrt(var_rel)
    ci95 = 1.95996 * se_nelo

    return nelo, ci95


def calculate_pentanomial_nelo(pairs: Tuple[int, int, int, int, int]) -> Tuple[float, float, int]:
    """
    Computes Normalized Elo from pentanomial game-pair counts:
    pairs = (LL, LD, DD/WL, WD, WW)
    """
    ll, ld, ddwl, wd, ww = pairs
    total_pairs = ll + ld + ddwl + wd + ww
    if total_pairs == 0:
        return 0.0, float("inf"), 0

    # Values for pair scores centered around 0:
    # LL: -1.0, LD: -0.5, DD/WL: 0.0, WD: +0.5, WW: +1.0
    p = [c / total_pairs for c in (ll, ld, ddwl, wd, ww)]
    vals = [-1.0, -0.5, 0.0, 0.5, 1.0]

    mu = sum(p[i] * vals[i] for i in range(5))
    var = sum(p[i] * (vals[i] - mu) ** 2 for i in range(5))

    if var <= 1e-9:
        sigma = 1e-4
    else:
        sigma = math.sqrt(var)

    nelo = NELO_SCALE * (mu / sigma)
    var_rel = 1.0 + (mu * mu) / (2.0 * max(1e-9, var))
    se_nelo = (NELO_SCALE / math.sqrt(total_pairs)) * math.sqrt(var_rel)
    ci95 = 1.95996 * se_nelo

    return nelo, ci95, total_pairs


def calculate_logistic_elo(wins: int, losses: int, draws: int) -> Tuple[float, float]:
    """
    Computes traditional Logistic Elo and 95% confidence interval.
    """
    n = wins + losses + draws
    if n == 0:
        return 0.0, float("inf")

    score = (wins + 0.5 * draws) / n
    clamped_score = min(max(score, 1e-4), 1.0 - 1e-4)

    elo = -400.0 * math.log10((1.0 / clamped_score) - 1.0)

    # Variance of mean score: Var(S) = (w*(1-s)^2 + l*s^2 + d*(0.5-s)^2) / n
    var_s = (wins * (1.0 - clamped_score)**2 + losses * clamped_score**2 + draws * (0.5 - clamped_score)**2) / n
    se_s = math.sqrt(max(1e-9, var_s) / n)

    s_lo = max(1e-4, clamped_score - 1.95996 * se_s)
    s_hi = min(1.0 - 1e-4, clamped_score + 1.95996 * se_s)

    elo_lo = -400.0 * math.log10((1.0 / s_lo) - 1.0)
    elo_hi = -400.0 * math.log10((1.0 / s_hi) - 1.0)
    ci95 = (elo_hi - elo_lo) / 2.0

    return elo, ci95


def analyze_wdl(wins: int, losses: int, draws: int,
                pentanomial: Optional[Tuple[int, int, int, int, int]] = None) -> EloResult:
    """Combines trinomial and pentanomial analysis into a unified result."""
    n = wins + losses + draws
    score = (wins + 0.5 * draws) / max(1, n)
    draw_rate = draws / max(1, n)

    if pentanomial:
        nelo, nelo_err, pairs_count = calculate_pentanomial_nelo(pentanomial)
    else:
        nelo, nelo_err = calculate_trinomial_nelo(wins, losses, draws)
        pairs_count = n // 2

    log_elo, log_err = calculate_logistic_elo(wins, losses, draws)

    return EloResult(
        score=score,
        games=n,
        wins=wins,
        losses=losses,
        draws=draws,
        draw_rate=draw_rate,
        nelo=nelo,
        nelo_err=nelo_err,
        logistic_elo=log_elo,
        logistic_err=log_err,
        pairs_count=pairs_count,
        pentanomial=pentanomial,
    )


def calibrate_anchor_elo(
    engine_name: str,
    opponents: List[Dict[str, object]],
) -> Dict[str, object]:
    """
    Computes Maximum Likelihood anchored Elo for a target engine against a gauntlet
    of opponents with known ratings using the Bradley-Terry logistic model.

    opponents is a list of dicts:
      {
         "name": "EngineName",
         "known_elo": 3100.0,
         "wins": int,
         "losses": int,
         "draws": int
      }
    """
    if not opponents:
        raise ValueError("At least one opponent required for anchor calibration.")

    # Objective: find R_target that solves sum_j [ (W_j + 0.5 * D_j) - N_j * E(S_j) ] = 0
    # where E(S_j) = 1 / (1 + 10^((R_j - R_target) / 400))
    def expected_score(r_target: float, r_opp: float) -> float:
        diff = (r_opp - r_target) / 400.0
        if diff > 15.0:
            return 0.0
        elif diff < -15.0:
            return 1.0
        return 1.0 / (1.0 + 10.0 ** diff)

    def f_score(r: float) -> float:
        total = 0.0
        for opp in opponents:
            w = opp["wins"]
            l = opp["losses"]
            d = opp["draws"]
            n = w + l + d
            pts = w + 0.5 * d
            r_opp = float(opp["known_elo"])
            e = expected_score(r, r_opp)
            total += pts - (n * e)
        return total

    def f_prime(r: float) -> float:
        total = 0.0
        scale = math.log(10.0) / 400.0
        for opp in opponents:
            w = opp["wins"]
            l = opp["losses"]
            d = opp["draws"]
            n = w + l + d
            r_opp = float(opp["known_elo"])
            e = expected_score(r, r_opp)
            total += -n * scale * e * (1.0 - e)
        return total

    # Initial guess: weighted average of known Elo + simple logistic deltas
    total_pts = sum(opp["wins"] + 0.5 * opp["draws"] for opp in opponents)
    total_games = sum(opp["wins"] + opp["losses"] + opp["draws"] for opp in opponents)
    if total_games == 0:
        return {"calibrated_elo": 0.0, "ci95": float("inf"), "opponents": opponents}

    avg_opp_elo = sum(float(opp["known_elo"]) * (opp["wins"] + opp["losses"] + opp["draws"]) for opp in opponents) / total_games
    overall_score = total_pts / total_games
    clamped_score = min(max(overall_score, 0.01), 0.99)
    current_r = avg_opp_elo - 400.0 * math.log10((1.0 / clamped_score) - 1.0)

    # Newton-Raphson solver with step damping
    for _ in range(50):
        val = f_score(current_r)
        slope = f_prime(current_r)
        if abs(slope) < 1e-12:
            break
        step = val / slope
        step = max(-200.0, min(200.0, step))
        current_r -= step
        if abs(step) < 1e-4:
            break

    # Fisher information & standard error
    fisher_info = -f_prime(current_r)
    if fisher_info > 1e-9:
        se = 1.0 / math.sqrt(fisher_info)
        ci95 = 1.95996 * se
    else:
        ci95 = float("inf")

    return {
        "engine": engine_name,
        "calibrated_elo": round(current_r, 1),
        "ci95": round(ci95, 1),
        "total_games": total_games,
        "total_points": total_pts,
        "opponents": opponents,
    }


def parse_fastchess_pgn(pgn_path: Path, engine_name: str = "Anak") -> EloResult:
    """
    Parses a PGN file produced by fastchess/cutechess, matches opening rounds into pairs,
    and extracts full trinomial and pentanomial game statistics.
    """
    if not pgn_path.exists():
        raise FileNotFoundError(f"PGN file not found: {pgn_path}")

    content = pgn_path.read_text(encoding="utf-8", errors="replace")
    games_raw = re.split(r"\n(?=\[Event )", content)

    # Structured round tracker: round_id -> {1: outcome, 2: outcome}
    rounds: Dict[str, Dict[str, float]] = {}
    wins, losses, draws = 0, 0, 0

    for raw in games_raw:
        if not raw.strip():
            continue

        white_match = re.search(r'\[White "([^"]+)"\]', raw)
        black_match = re.search(r'\[Black "([^"]+)"\]', raw)
        result_match = re.search(r'\[Result "([^"]+)"\]', raw)
        round_match = re.search(r'\[Round "([^"]+)"\]', raw)

        if not (white_match and black_match and result_match):
            continue

        white = white_match.group(1)
        black = black_match.group(1)
        res_str = result_match.group(1)
        round_str = round_match.group(1) if round_match else ""

        is_target_white = engine_name.lower() in white.lower()
        is_target_black = engine_name.lower() in black.lower()

        if not (is_target_white or is_target_black):
            continue

        # Score from target perspective
        if res_str == "1-0":
            pts = 1.0 if is_target_white else 0.0
        elif res_str == "0-1":
            pts = 0.0 if is_target_white else 1.0
        elif res_str in ("1/2-1/2", "0.5-0.5"):
            pts = 0.5
        else:
            continue

        if pts == 1.0:
            wins += 1
        elif pts == 0.0:
            losses += 1
        else:
            draws += 1

        # Track opening pairs:
        # Either round is 'X.Y' (e.g. '12.1' and '12.2') or integer rounds '1', '2' paired by (round-1)//2
        # or pair by start FEN
        fen_match = re.search(r'\[FEN "([^"]+)"\]', raw)
        fen_str = fen_match.group(1).strip() if fen_match else ""

        pair_key = None
        if round_str and "." in round_str:
            pair_key = round_str.split(".", 1)[0]
        elif round_str and round_str.isdigit():
            pair_key = f"r_{(int(round_str) - 1) // 2}"
        elif fen_str:
            pair_key = f"fen_{fen_str}"

        if pair_key:
            side_key = "w" if is_target_white else "b"
            rounds.setdefault(pair_key, {})[side_key] = pts

    # Compute pentanomial if paired rounds detected
    pentanomial = None
    if rounds:
        ll, ld, ddwl, wd, ww = 0, 0, 0, 0, 0
        valid_pairs = 0
        for pair_idx, sides in rounds.items():
            if len(sides) == 2 and "w" in sides and "b" in sides:
                s_w = sides["w"]
                s_b = sides["b"]
                total_pts = s_w + s_b
                valid_pairs += 1
                if total_pts == 2.0:
                    ww += 1
                elif total_pts == 1.5:
                    wd += 1
                elif total_pts == 1.0:
                    ddwl += 1
                elif total_pts == 0.5:
                    ld += 1
                elif total_pts == 0.0:
                    ll += 1

        if valid_pairs > 0:
            pentanomial = (ll, ld, ddwl, wd, ww)

    return analyze_wdl(wins, losses, draws, pentanomial)


def main():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — Elo Determination & Calibration Kit")
    subparsers = parser.add_subparsers(dest="command", help="Sub-commands")

    # Command: stats
    p_stats = subparsers.add_parser("stats", help="Compute Elo metrics from W/L/D numbers or PGN file")
    p_stats.add_argument("--pgn", type=Path, help="Path to PGN file to parse")
    p_stats.add_argument("--engine", type=str, default="Anak", help="Target engine name inside PGN")
    p_stats.add_argument("-w", "--wins", type=int, default=0, help="Wins")
    p_stats.add_argument("-l", "--losses", type=int, default=0, help="Losses")
    p_stats.add_argument("-d", "--draws", type=int, default=0, help="Draws")

    # Command: calibrate
    p_cal = subparsers.add_parser("calibrate", help="Calibrate anchor rating against gauntlet opponents")
    p_cal.add_argument("--engine", type=str, default="Anak", help="Target engine name")
    p_cal.add_argument("--opponents", nargs="+", required=True,
                       metavar="NAME:KNOWN_ELO:W:L:D",
                       help="Opponent records format: 'Name:Elo:Wins:Losses:Draws'")

    # Command: nelo
    p_nelo = subparsers.add_parser("nelo", help="Calculate Normalized Elo directly from pentanomial or trinomial stats")
    p_nelo.add_argument("--pentanomial", type=str, help="Pair counts: 'LL,LD,DD/WL,WD,WW' e.g. '10,25,80,30,15'")
    p_nelo.add_argument("-w", "--wins", type=int, default=0)
    p_nelo.add_argument("-l", "--losses", type=int, default=0)
    p_nelo.add_argument("-d", "--draws", type=int, default=0)

    args = parser.parse_args()

    if args.command == "stats":
        if args.pgn:
            res = parse_fastchess_pgn(args.pgn, args.engine)
            print(f"\n[+] Analyzed PGN: {args.pgn} (Engine: {args.engine})")
        else:
            res = analyze_wdl(args.wins, args.losses, args.draws)
            print("\n[+] Analyzed W/L/D:")
        print("=" * 65)
        print(res.summary())
        print("=" * 65 + "\n")

    elif args.command == "calibrate":
        records = []
        for opp_str in args.opponents:
            parts = opp_str.split(":")
            if len(parts) != 5:
                print(f"[!] Error: Invalid format '{opp_str}'. Expected 'Name:Elo:Wins:Losses:Draws'")
                sys.exit(1)
            records.append({
                "name": parts[0],
                "known_elo": float(parts[1]),
                "wins": int(parts[2]),
                "losses": int(parts[3]),
                "draws": int(parts[4]),
            })
        cal = calibrate_anchor_elo(args.engine, records)
        print("\n" + "=" * 65)
        print("         ANAK GAUNTLET MAXIMUM LIKELIHOOD CALIBRATION")
        print("=" * 65)
        print(f"Target Engine:         {cal['engine']}")
        print(f"Calibrated Rating:     {cal['calibrated_elo']} +/- {cal['ci95']} (95% CI)")
        print(f"Total Games Analyzed:  {cal['total_games']} ({cal['total_points']:g} points)")
        print("-" * 65)
        print("Opponent Breakdown:")
        for opp in cal["opponents"]:
            n = opp["wins"] + opp["losses"] + opp["draws"]
            pts = opp["wins"] + 0.5 * opp["draws"]
            pct = (pts / n) * 100.0 if n > 0 else 0.0
            print(f"  * {opp['name']:18s} (Known Elo: {opp['known_elo']:4.0f}) | Score: {pts:g}/{n} ({pct:5.1f}%) | W:{opp['wins']} L:{opp['losses']} D:{opp['draws']}")
        print("=" * 65 + "\n")

    elif args.command == "nelo":
        if args.pentanomial:
            parts = [int(x.strip()) for x in args.pentanomial.split(",")]
            if len(parts) != 5:
                print("[!] Error: --pentanomial requires 5 comma-separated integers: LL,LD,DD/WL,WD,WW")
                sys.exit(1)
            nelo, ci, total = calculate_pentanomial_nelo(tuple(parts))
            print(f"Pentanomial nElo ({total} pairs): {nelo:+.2f} +/- {ci:.2f} (95% CI)")
        else:
            nelo, ci = calculate_trinomial_nelo(args.wins, args.losses, args.draws)
            print(f"Trinomial nElo ({args.wins+args.losses+args.draws} games): {nelo:+.2f} +/- {ci:.2f} (95% CI)")
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
