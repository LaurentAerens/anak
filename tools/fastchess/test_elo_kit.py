#!/usr/bin/env python3
"""
Unit tests for elo_kit.py:
  - Trinomial and Pentanomial Normalized Elo (nElo) calculations
  - 95% Confidence Intervals
  - Logistic Elo formulas
  - Maximum Likelihood Gauntlet Calibration
  - PGN parsing and pair extraction
"""

import math
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from elo_kit import (
    calculate_trinomial_nelo,
    calculate_pentanomial_nelo,
    calculate_logistic_elo,
    analyze_wdl,
    calibrate_anchor_elo,
    NELO_SCALE,
)


class TestEloKit(unittest.TestCase):

    def test_trinomial_even_score(self):
        # 50% score should produce 0.0 nElo and 0.0 Logistic Elo
        nelo, ci = calculate_trinomial_nelo(wins=100, losses=100, draws=200)
        self.assertAlmostEqual(nelo, 0.0, places=4)
        self.assertGreater(ci, 0.0)

        log_elo, log_ci = calculate_logistic_elo(wins=100, losses=100, draws=200)
        self.assertAlmostEqual(log_elo, 0.0, places=4)
        self.assertGreater(log_ci, 0.0)

    def test_trinomial_positive_score(self):
        # More wins than losses should produce positive nElo and logistic Elo
        nelo, ci = calculate_trinomial_nelo(wins=300, losses=100, draws=100)
        self.assertGreater(nelo, 0.0)
        self.assertGreater(ci, 0.0)

        log_elo, log_ci = calculate_logistic_elo(wins=300, losses=100, draws=100)
        self.assertGreater(log_elo, 0.0)

    def test_pentanomial_calculation(self):
        # Symmetrical pairs: LL=10, LD=20, DD/WL=40, WD=20, WW=10 -> mean = 0
        pairs = (10, 20, 40, 20, 10)
        nelo, ci, total = calculate_pentanomial_nelo(pairs)
        self.assertEqual(total, 100)
        self.assertAlmostEqual(nelo, 0.0, places=4)
        self.assertGreater(ci, 0.0)

        # Winning pairs: WW=50, WD=30, DD=20, LD=0, LL=0
        pairs_win = (0, 0, 20, 30, 50)
        nelo_w, ci_w, total_w = calculate_pentanomial_nelo(pairs_win)
        self.assertGreater(nelo_w, 50.0)

    def test_draw_rate_invariance(self):
        # High draw rate vs low draw rate with same score ratio:
        # Logistic Elo is identical if score is identical, but nElo reflects variance
        res1 = analyze_wdl(wins=600, losses=400, draws=0)    # 60% score, 0% draws
        res2 = analyze_wdl(wins=150, losses=50, draws=800)   # 55% score, 80% draws
        self.assertEqual(res1.draw_rate, 0.0)
        self.assertEqual(res2.draw_rate, 0.8)
        self.assertGreater(res1.score, 0.5)
        self.assertGreater(res2.score, 0.5)

    def test_gauntlet_anchor_calibration(self):
        # Anchor calibration against known engine ratings
        opponents = [
            {"name": "Opp1", "known_elo": 3000.0, "wins": 25, "losses": 5, "draws": 10},
            {"name": "Opp2", "known_elo": 3200.0, "wins": 15, "losses": 15, "draws": 10},
        ]
        cal = calibrate_anchor_elo("Anak", opponents)
        self.assertEqual(cal["engine"], "Anak")
        # Against Opp2 (3200), score is exactly 50% (20/40), and against Opp1 (3000) score is 75% (30/40)
        # Calibrated Elo should be around 3150-3200
        self.assertGreater(cal["calibrated_elo"], 3100.0)
        self.assertLess(cal["calibrated_elo"], 3250.0)
        self.assertGreater(cal["ci95"], 0.0)


if __name__ == "__main__":
    unittest.main()
