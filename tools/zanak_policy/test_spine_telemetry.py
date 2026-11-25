"""Unit tests for offline Stockfish spine telemetry parsing and aggregation."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import spine_telemetry as st  # noqa: E402


class TestSpineTelemetryParsing(unittest.TestCase):
    def test_normalize_uci_score_monotonic_mate_and_cp(self) -> None:
        m1 = st.normalize_uci_score("mate", 1)
        m3 = st.normalize_uci_score("mate", 3)
        cp_pos = st.normalize_uci_score("cp", 520)
        cp_neg = st.normalize_uci_score("cp", -400)
        mn3 = st.normalize_uci_score("mate", -3)
        mn1 = st.normalize_uci_score("mate", -1)
        m0 = st.normalize_uci_score("mate", 0)

        self.assertGreater(m1, m3)
        self.assertGreater(m3, cp_pos)
        self.assertGreater(cp_pos, cp_neg)
        self.assertGreater(cp_neg, mn3)
        self.assertGreater(mn3, mn1)
        self.assertGreater(mn1, m0)
        self.assertEqual(m1, 29990)
        self.assertEqual(mn1, -29990)

    def test_extract_multipv_table_prefers_exact_at_target_depth(self) -> None:
        lines = [
            "info depth 9 seldepth 12 multipv 1 score cp 45 nodes 1000 pv e2e4 e7e5",
            "info depth 10 seldepth 14 multipv 1 score cp 99 lowerbound nodes 2000 pv d2d4",
            "info depth 10 seldepth 14 multipv 1 score cp 62 nodes 2400 pv e2e4 e7e5",
            "info depth 10 seldepth 14 multipv 2 score cp -15 nodes 2500 pv a7a8q",
            "info depth 10 seldepth 14 multipv 3 score mate -2 upperbound nodes 2600 pv g2g4",
        ]
        table = st.extract_multipv_table(lines, target_depth=10)
        self.assertEqual([e["multipv"] for e in table], [1, 2, 3])
        self.assertEqual(table[0]["score_cp"], 62)
        self.assertEqual(table[0]["pv"], ["e2e4", "e7e5"])
        self.assertEqual(table[1]["pv"], ["a7a8q"])
        self.assertEqual(table[2]["score_cp"], -29980)

    def test_extract_multipv_table_handles_forced_mate_below_target_depth(self) -> None:
        lines = [
            "info depth 1 multipv 1 score mate 1 nodes 20 pv h5f7",
        ]
        table = st.extract_multipv_table(lines, target_depth=10)
        self.assertEqual(len(table), 1)
        self.assertEqual(table[0]["score_cp"], 29990)

    def test_classifications_and_summary(self) -> None:
        base_spine = {
            "fen_idx": 0,
            "ply": 2,
            "fen": "8/8/8/8/8/8/8/K6k w - - 0 1",
            "zanak_move": "a1a2",
            "zanak_eval_before": 50,
            "zanak_eval_after": -100,
            "static_eval_delta": -150,
            "is_capture": False,
            "is_check": False,
            "is_promotion": False,
            "prev_was_capture": True,
            "dest_attacked_after": True,
            "see_cp": -100,
            "max_threatened_before": 300,
            "legal_count": 30,
            "in_check_before": False,
            "is_recapture": False,
            "moved_piece_type": "queen",
            "from_attacked_before": False,
            "from_defended_before": True,
            "dest_defended_after": False,
        }
        sf_info = {
            "sf_depth": 10,
            "sf_multipv_count": 20,
            "sf_best_move": "h1h2",
            "best_sf_cp": 200,
            "sf_best_score_type": "cp",
            "sf_best_score_value": 200,
            "sf_rank": 21,
            "sf_rank_label": "21+",
            "outside_top_20": True,
            "zanak_sf_cp": -250,
            "zanak_score_type": "cp",
            "zanak_score_value": -250,
            "cp_loss_raw": 450,
            "cp_loss": 450,
        }
        row = st.enrich_row(base_spine, sf_info)
        self.assertEqual(row["ply_bucket"], "ply 1-2")
        self.assertEqual(row["transition"], "capture -> quiet")
        self.assertEqual(row["policy_category"], "fallback_likely")
        self.assertEqual(row["see_bucket"], "see_cp < 0")
        self.assertTrue(row["shock_abs"])
        self.assertEqual(row["turn_role"], "player (ply%2==0)")
        self.assertEqual(row["rank_bucket"], "21+")

        md = st.build_markdown_summary([row])
        self.assertIn("1. Overall Stockfish Rank Distribution", md)
        self.assertIn("2a. Breakdown by Spine Ply Depth Bucket", md)
        self.assertIn("3a. Breakdown by Move Transition", md)
        self.assertIn("4a. Breakdown by Destination Attack Status", md)
        self.assertIn("5a. Breakdown by Shock Magnitude", md)
        self.assertIn("6a. Breakdown by Spine Turn", md)


if __name__ == "__main__":
    unittest.main()
