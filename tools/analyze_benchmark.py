#!/usr/bin/env python3
"""
Comprehensive PGN Benchmark Analyzer for Anak Chess Engine.
Analyzes 10,000-game match archives to identify:
- White vs Black win/loss disparity
- Move count at loss (early tactics vs late endgame)
- Termination reasons (mate, resignation, timeout, draw rules)
- Average depth & time spent per move (Anak vs Opponent)
- Eval divergence & "Throws" (winning -> losing)
- Single-move blunders (sharp eval drops)
- Opening pair performance (2-0, 1-1, 0-2)
"""

import sys
import os
import re
from pathlib import Path
from collections import defaultdict, Counter

def analyze_pgn(pgn_path: Path):
    print("=" * 75)
    print(f"Analyzing: {pgn_path.name}")
    print("=" * 75)

    if not pgn_path.exists():
        print(f"File not found: {pgn_path}")
        return

    # Game headers regex
    re_white = re.compile(r'\[White "(.*?)"\]')
    re_black = re.compile(r'\[Black "(.*?)"\]')
    re_result = re.compile(r'\[Result "(.*?)"\]')
    re_fen = re.compile(r'\[FEN "(.*?)"\]')
    re_ply = re.compile(r'\[PlyCount "(\d+)"\]')
    re_term = re.compile(r'\[Termination "(.*?)"\]')
    re_round = re.compile(r'\[Round "(\d+)"\]')

    # Move annotation regex: e.g. 9. Ba3 {+0.55/1 0.215s} or {-0.29/11 0.062s}
    re_eval_depth_time = re.compile(r'\{([+-]?\d+(?:\.\d+)?|M[+-]?\d+)/(\d+)\s+([\d\.]+)s(?:,\s*([^\}]+))?\}')

    total_games = 0
    anak_wins = 0
    anak_losses = 0
    draws = 0

    anak_white_wins = 0
    anak_white_losses = 0
    anak_white_draws = 0

    anak_black_wins = 0
    anak_black_losses = 0
    anak_black_draws = 0

    loss_plies = []
    win_plies = []
    draw_plies = []

    loss_terminations = Counter()
    win_terminations = Counter()
    draw_terminations = Counter()

    anak_depths = []
    opp_depths = []
    anak_times = []
    opp_times = []

    # Blunder and throw metrics
    throws = 0  # Anak reached >= +2.0 at any point, but eventually lost
    comebacks = 0 # Anak was <= -2.0 at any point, but won
    losses_with_early_blunder = 0 # Anak blundered before ply 30
    
    # Opening pair tracking: round_id -> { 'white': result, 'black': result }
    pair_results = defaultdict(dict)

    with open(pgn_path, 'r', encoding='utf-8', errors='ignore') as f:
        content = f.read()

    # Split games by [Event
    raw_games = content.split('[Event "')
    
    for g_text in raw_games:
        if not g_text.strip():
            continue
        g_text = '[Event "' + g_text

        # Extract headers
        m_w = re_white.search(g_text)
        m_b = re_black.search(g_text)
        m_res = re_result.search(g_text)
        m_ply = re_ply.search(g_text)
        m_term = re_term.search(g_text)
        m_rnd = re_round.search(g_text)
        m_fen = re_fen.search(g_text)

        if not (m_w and m_b and m_res):
            continue

        white = m_w.group(1)
        black = m_b.group(1)
        result = m_res.group(1)
        plies = int(m_ply.group(1)) if m_ply else 0
        term = m_term.group(1) if m_term else "unknown"
        rnd = int(m_rnd.group(1)) if m_rnd else 0
        fen = m_fen.group(1) if m_fen else ""

        is_anak_white = "anak" in white.lower()
        is_anak_black = "anak" in black.lower()

        if not (is_anak_white or is_anak_black):
            continue

        total_games += 1

        # Determine outcome for Anak
        if result == "1-0":
            anak_won = is_anak_white
            anak_lost = is_anak_black
            is_draw = False
        elif result == "0-1":
            anak_won = is_anak_black
            anak_lost = is_anak_white
            is_draw = False
        else:
            anak_won = False
            anak_lost = False
            is_draw = True

        if is_anak_white:
            pair_results[rnd]['white'] = 1.0 if anak_won else (0.5 if is_draw else 0.0)
            if anak_won:
                anak_white_wins += 1
            elif anak_lost:
                anak_white_losses += 1
            else:
                anak_white_draws += 1
        else:
            pair_results[rnd]['black'] = 1.0 if anak_won else (0.5 if is_draw else 0.0)
            if anak_won:
                anak_black_wins += 1
            elif anak_lost:
                anak_black_losses += 1
            else:
                anak_black_draws += 1

        if anak_won:
            anak_wins += 1
            win_plies.append(plies)
            win_terminations[term] += 1
        elif anak_lost:
            anak_losses += 1
            loss_plies.append(plies)
            loss_terminations[term] += 1
        else:
            draws += 1
            draw_plies.append(plies)
            draw_terminations[term] += 1

        # Parse moves body for evals, depths, and throws
        # Extract everything after the last header tag
        body_start = g_text.rfind(']\n\n')
        if body_start != -1:
            body = g_text[body_start + 3:]
        else:
            body = g_text

        # Find all move annotations {eval/depth times}
        annotations = re_eval_depth_time.findall(body)
        
        anak_peak_eval = -999.0
        anak_min_eval = 999.0
        
        for idx, (eval_str, depth_str, time_str, extra) in enumerate(annotations):
            is_anak_turn = (idx % 2 == 0) if is_anak_white else (idx % 2 == 1)
            
            # Parse eval to centipawns/pawns
            try:
                if eval_str.startswith('M'):
                    ev = 50.0 if not eval_str.startswith('M-') else -50.0
                else:
                    ev = float(eval_str)
            except ValueError:
                ev = 0.0

            d = int(depth_str)
            t = float(time_str)

            if is_anak_turn:
                anak_depths.append(d)
                anak_times.append(t)
                if ev > anak_peak_eval:
                    anak_peak_eval = ev
                if ev < anak_min_eval:
                    anak_min_eval = ev
            else:
                opp_depths.append(d)
                opp_times.append(t)

        if anak_lost and anak_peak_eval >= 2.0:
            throws += 1
        if anak_won and anak_min_eval <= -2.0:
            comebacks += 1

    # Statistical summaries
    avg_anak_depth = sum(anak_depths) / len(anak_depths) if anak_depths else 0
    avg_opp_depth = sum(opp_depths) / len(opp_depths) if opp_depths else 0
    avg_anak_time = (sum(anak_times) / len(anak_times)) * 1000 if anak_times else 0
    avg_opp_time = (sum(opp_times) / len(opp_times)) * 1000 if opp_times else 0

    avg_win_len = (sum(win_plies) / len(win_plies)) / 2 if win_plies else 0
    avg_loss_len = (sum(loss_plies) / len(loss_plies)) / 2 if loss_plies else 0
    avg_draw_len = (sum(draw_plies) / len(draw_plies)) / 2 if draw_plies else 0

    # Depth distribution for Anak
    depth_buckets = Counter()
    for d in anak_depths:
        if d <= 3:
            depth_buckets['1-3 (Critically Low)'] += 1
        elif d <= 6:
            depth_buckets['4-6 (Average Fast)'] += 1
        elif d <= 9:
            depth_buckets['7-9 (Good Depth)'] += 1
        else:
            depth_buckets['10+ (Deep)'] += 1

    total_moves = len(anak_depths) or 1

    # Loss move distribution (early vs late)
    loss_buckets = Counter()
    for p in loss_plies:
        m = p // 2
        if m < 25:
            loss_buckets['< 25 moves (Opening/Early Tactic)'] += 1
        elif m < 45:
            loss_buckets['25 - 45 moves (Middlegame)'] += 1
        elif m < 70:
            loss_buckets['45 - 70 moves (Late Middlegame/Endgame)'] += 1
        else:
            loss_buckets['70+ moves (Deep Endgame / Grind)'] += 1

    # Opening pairs breakdown:
    # 2.0 = both won (2-0), 1.5 = won white + draw black or vice versa
    pair_counts = Counter()
    for rnd, res in pair_results.items():
        if 'white' in res and 'black' in res:
            tot = res['white'] + res['black']
            if tot == 2.0:
                pair_counts['2-0 (Anak swept pair)'] += 1
            elif tot == 1.5:
                pair_counts['1.5 - 0.5 (Anak won pair)'] += 1
            elif tot == 1.0:
                pair_counts['1 - 1 (Split pair / Equal)'] += 1
            elif tot == 0.5:
                pair_counts['0.5 - 1.5 (Opponent won pair)'] += 1
            elif tot == 0.0:
                pair_counts['0 - 2 (Opponent swept pair)'] += 1

    white_score = (anak_white_wins + 0.5 * anak_white_draws)
    white_total = (anak_white_wins + anak_white_losses + anak_white_draws) or 1
    white_pct = (white_score / white_total) * 100.0

    black_score = (anak_black_wins + 0.5 * anak_black_draws)
    black_total = (anak_black_wins + anak_black_losses + anak_black_draws) or 1
    black_pct = (black_score / black_total) * 100.0

    print(f"\n1. COLOR BIAS (WHITE vs BLACK)")
    print(f"  Anak as WHITE: {white_score:g} / {white_total} ({white_pct:.2f}%)  [+{anak_white_wins} -{anak_white_losses} ={anak_white_draws}]")
    print(f"  Anak as BLACK: {black_score:g} / {black_total} ({black_pct:.2f}%)  [+{anak_black_wins} -{anak_black_losses} ={anak_black_draws}]")
    print(f"  White-to-Black Score Difference: {white_pct - black_pct:+.2f}%")

    print(f"\n2. SEARCH DEPTH & SPEED PROFILE")
    print(f"  Anak Average Depth:       {avg_anak_depth:.2f} ply  |  Opponent: {avg_opp_depth:.2f} ply  (Difference: {avg_anak_depth - avg_opp_depth:+.2f} ply)")
    print(f"  Anak Average Move Time:   {avg_anak_time:.1f} ms    |  Opponent: {avg_opp_time:.1f} ms")
    print(f"  Anak Depth Breakdown:")
    for k, v in sorted(depth_buckets.items()):
        print(f"    - {k:25}: {v:6d} ({v*100.0/total_moves:5.1f}%)")

    print(f"\n3. WHERE DOES ANAK LOSE? (MOVE DURATION & TERMINATION)")
    print(f"  Average Game Length:  Wins: {avg_win_len:.1f} moves  |  Losses: {avg_loss_len:.1f} moves  |  Draws: {avg_draw_len:.1f} moves")
    print(f"  Loss Timing Distribution:")
    for k, v in sorted(loss_buckets.items()):
        print(f"    - {k:40}: {v:5d} ({v*100.0/anak_losses if anak_losses else 0:5.1f}%)")
    print(f"  Loss Termination Reasons:")
    for k, v in loss_terminations.most_common():
        print(f"    - {k:20}: {v:5d} ({v*100.0/anak_losses if anak_losses else 0:5.1f}%)")

    print(f"\n4. THROWS & COMEBACKS (BLUNDERS)")
    print(f"  'Thrown' Games (Anak was winning >= +2.00, but LOST): {throws:5d} ({throws*100.0/anak_losses if anak_losses else 0:5.1f}% of losses!)")
    print(f"  Comeback Games (Anak was losing <= -2.00, but WON):   {comebacks:5d} ({comebacks*100.0/anak_wins if anak_wins else 0:5.1f}% of wins)")

    print(f"\n5. OPENING PAIR BREAKDOWN (Color-swapped sets)")
    completed_pairs = sum(pair_counts.values()) or 1
    for k, v in sorted(pair_counts.items()):
        print(f"    - {k:30}: {v:5d} ({v*100.0/completed_pairs:5.1f}%)")
    print("=" * 75 + "\n")


if __name__ == "__main__":
    if len(sys.argv) > 1:
        for p in sys.argv[1:]:
            analyze_pgn(Path(p))
    else:
        results_dir = Path("tools/fastchess/results")
        p1800 = results_dir / "anak_vs_sf1800_20261025_123838.pgn"
        p2000 = results_dir / "anak_vs_sf2000_20261025_125347.pgn"
        if p1800.exists():
            analyze_pgn(p1800)
        if p2000.exists():
            analyze_pgn(p2000)
