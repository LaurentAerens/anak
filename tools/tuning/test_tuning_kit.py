#!/usr/bin/env python3
"""
Unit tests for tuning tools:
  - SPSA parameter clamping, decay schedule, and gradient computation
  - Offline Texel sigmoid and MSE calculations
  - Progress persistence and C# snippet generation
"""

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from spsa import SpsaParam, SpsaTuner, generate_csharp_snippet
from offline_tuner import sigmoid, compute_texel_mse, parse_epd_line


class TestTuningKit(unittest.TestCase):

    def test_spsa_param_clamping(self):
        p = SpsaParam(name="TestParam", default=50.0, min_val=20.0, max_val=100.0, c=2.0, a=10.0)
        self.assertEqual(p.clamp(120.0), 100.0)
        self.assertEqual(p.clamp(10.0), 20.0)
        self.assertEqual(p.clamp(60.0), 60.0)
        self.assertEqual(p.current_int(), 50)

    def test_spsa_dry_run_step(self):
        params = [
            SpsaParam(name="LmrBase", default=80.0, min_val=40.0, max_val=150.0, c=3.0, a=50.0),
            SpsaParam(name="RfpMargin", default=75.0, min_val=30.0, max_val=150.0, c=3.0, a=50.0),
        ]
        tuner = SpsaTuner(
            params=params,
            max_steps=2,
            games_per_step=10,
            dry_run=True,
            progress_file=Path("tools/tuning/test_progress.json"),
        )
        rec = tuner.step()
        self.assertEqual(rec["step"], 1)
        self.assertEqual(rec["games"], 10)
        self.assertIn("nelo", rec)
        self.assertIn("uci_plus", rec)
        self.assertIn("uci_minus", rec)

        # Cleanup test progress
        if tuner.progress_file.exists():
            tuner.progress_file.unlink()

    def test_csharp_snippet_generator(self):
        params = {"LmrBase": 82, "RfpMargin": 74}
        snippet = generate_csharp_snippet(params)
        self.assertIn("public const int TunedLmrBase = 82;", snippet)
        self.assertIn("public const int TunedRfpMargin = 74;", snippet)

    def test_texel_sigmoid_and_mse(self):
        # 0 cp eval -> 50% expected score
        self.assertAlmostEqual(sigmoid(0.0), 0.5, places=4)
        # +400 cp eval -> ~90.9% expected score
        self.assertAlmostEqual(sigmoid(400.0), 1.0 / (1.0 + 0.1), places=3)

        # Perfect predictions have 0 MSE
        preds = [4000.0, -4000.0, 0.0]
        targets = [1.0, 0.0, 0.5]
        mse = compute_texel_mse(preds, targets)
        self.assertLess(mse, 0.001)

    def test_epd_line_parsing(self):
        line = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1 c9 \"1/2-1/2\";"
        rec = parse_epd_line(line)
        self.assertIsNotNone(rec)
        self.assertEqual(rec.result, 0.5)


if __name__ == "__main__":
    unittest.main()
