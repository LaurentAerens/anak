"""
Unit and Integration Tests for Anak NNUE Training Pipeline Improvements:
- Query DSL & Metric Extraction (no queens, endgames, openness, blunders)
- Dataset Recipes & Quota Routing ('how much of what')
- Fast Memory-Mapped Binary Pack Serialization & Reading (.binpack)
- GPU-Accelerated Sparse Feature Scatter & DataLoader
"""

import os
import shutil
import tempfile
import unittest
import chess
import numpy as np
import torch

from query import (
    calculate_pawn_openness,
    extract_metrics_from_board,
    PositionQuery,
    DatasetRecipe,
)
from binpack import (
    BinpackWriter,
    BinpackReader,
    encode_features_to_indices,
    RECORD_DTYPE,
    SENTINEL_INDEX,
)
from fast_loader import (
    fast_scatter_features,
    FastMemoryMappedBatchLoader,
)
from model import StandardNNUE, MicroNNUE


class TestQueryAndMetrics(unittest.TestCase):
    def test_starting_position_metrics(self):
        board = chess.Board()
        metrics = extract_metrics_from_board(board, eval_val=0.25, source="st")

        self.assertEqual(metrics["pieces"], 32)
        self.assertTrue(metrics["has_queens"])
        self.assertFalse(metrics["no_queens"])
        self.assertEqual(metrics["queens"], 2)
        self.assertEqual(metrics["white_queens"], 1)
        self.assertEqual(metrics["black_queens"], 1)
        self.assertEqual(metrics["pawns"], 16)
        self.assertEqual(metrics["source"], "st")

    def test_queenless_endgame_query(self):
        # King and Rook vs King (no queens, 3 pieces)
        board = chess.Board("8/8/8/4k3/8/8/4K3/R7 w - - 0 1")
        metrics = extract_metrics_from_board(board, eval_val=5.0)

        self.assertEqual(metrics["pieces"], 3)
        self.assertTrue(metrics["no_queens"])
        self.assertFalse(metrics["has_queens"])
        self.assertTrue(metrics["is_endgame"])

        q1 = PositionQuery("pieces <= 16 and not has_queens")
        self.assertTrue(q1.matches(metrics))

        q2 = PositionQuery("no_queens and pieces <= 10")
        self.assertTrue(q2.matches(metrics))

        q3 = PositionQuery("has_queens or pieces > 16")
        self.assertFalse(q3.matches(metrics))

    def test_openness_metric_and_query(self):
        # Completely open board without pawns
        board_open = chess.Board("8/8/4k3/8/8/4K3/8/8 w - - 0 1")
        metrics_open = extract_metrics_from_board(board_open)
        self.assertGreaterEqual(metrics_open["openness"], 0.50)
        self.assertTrue(metrics_open["is_open"])

        q_open = PositionQuery("openness >= 0.50")
        self.assertTrue(q_open.matches(metrics_open))

    def test_dataset_recipe_quota_routing(self):
        recipe = DatasetRecipe({
            "endgame": {"query": "pieces <= 10", "ratio": 0.50},
            "open_middlegame": {"query": "pieces > 10 and has_queens", "ratio": 0.50},
        })

        b_endgame = chess.Board("8/8/8/4k3/8/8/4K3/R7 w - - 0 1")
        m_endgame = extract_metrics_from_board(b_endgame)

        b_middle = chess.Board()
        m_middle = extract_metrics_from_board(b_middle)

        self.assertEqual(recipe.route_sample(m_endgame), "endgame")
        self.assertEqual(recipe.route_sample(m_middle), "open_middlegame")


class TestBinpackAndFastLoader(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()
        self.binpack_path = os.path.join(self.temp_dir, "test.binpack")

    def tearDown(self):
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_binpack_roundtrip(self):
        board = chess.Board()

        # Write 100 sample records
        with BinpackWriter(self.binpack_path, model_tag="test", buffer_size=32) as writer:
            for i in range(100):
                writer.add_board(board, target_eval=0.15 * i, target_wdl=0.5, openness=0.60)

        self.assertTrue(os.path.isfile(self.binpack_path))
        # Header is 64 bytes, each record is 144 bytes
        expected_size = 64 + (100 * 144)
        self.assertEqual(os.path.getsize(self.binpack_path), expected_size)

        # Read back with memory-mapped reader
        reader = BinpackReader(self.binpack_path)
        self.assertEqual(len(reader), 100)
        self.assertEqual(reader.model_tag, "test")

        first_rec = reader[0]
        self.assertEqual(first_rec["piece_count"], 32)
        self.assertAlmostEqual(first_rec["openness"], 0.60, places=3)
        self.assertAlmostEqual(first_rec["target_eval"], 0.0, places=3)

        last_rec = reader[99]
        self.assertAlmostEqual(last_rec["target_eval"], 0.15 * 99, places=2)
        reader.close()

    def test_fast_scatter_features(self):
        board = chess.Board()
        our_idx, their_idx, count = encode_features_to_indices(board)

        indices_t = torch.from_numpy(our_idx.astype(np.int32)).unsqueeze(0).long()
        dense = fast_scatter_features(indices_t)

        self.assertEqual(dense.shape, (1, 768))
        self.assertEqual(int(dense.sum().item()), count)  # exactly 32 pieces active

    def test_fast_batch_loader_and_model_forward(self):
        # Write 250 positions
        with BinpackWriter(self.binpack_path, model_tag="open") as writer:
            b = chess.Board()
            for _ in range(250):
                writer.add_board(b, target_eval=0.25, target_wdl=0.5, openness=0.55)

        device = torch.device("cpu")
        loader = FastMemoryMappedBatchLoader(
            self.binpack_path,
            batch_size=64,
            shuffle=True,
            device=device
        )

        model = StandardNNUE().to(device)
        model.eval()

        batches_seen = 0
        total_samples = 0
        with torch.no_grad():
            for batch in loader:
                batches_seen += 1
                out = model(batch["our_features"], batch["their_features"])
                self.assertEqual(out.shape[0], batch["our_features"].shape[0])
                total_samples += out.shape[0]

        self.assertEqual(total_samples, 250)
        self.assertEqual(batches_seen, 4)  # 64 + 64 + 64 + 58 = 250
        loader.close()


if __name__ == "__main__":
    unittest.main()
