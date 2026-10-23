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
    classify_expert,
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
    resolve_binpack_files,
)
from model import StandardNNUE, MicroNNUE


class TestQueryAndMetrics(unittest.TestCase):
    def test_starting_position_metrics(self):
        board = chess.Board()
        metrics = extract_metrics_from_board(board, eval_val=0.25, source="st")

        self.assertEqual(metrics["pieces"], 32)
        self.assertTrue(metrics["has_queens"])
        self.assertFalse(metrics["no_queens"])
        self.assertFalse(metrics["is_queenless"])
        self.assertFalse(metrics["is_imbalance"])
        self.assertFalse(metrics["opposite_castling"])
        self.assertEqual(metrics["queens"], 2)
        self.assertEqual(metrics["white_queens"], 1)
        self.assertEqual(metrics["black_queens"], 1)
        self.assertEqual(metrics["pawns"], 16)
        self.assertEqual(metrics["source"], "st")
        self.assertIn(metrics["expert"], ("open", "closed"))

    def test_expert_classification_priority(self):
        # 1. Endgame (<= 14 pieces)
        b_endgame = chess.Board("8/8/8/4k3/8/8/4K3/R7 w - - 0 1")
        self.assertEqual(classify_expert(b_endgame), "endgame")
        self.assertEqual(extract_metrics_from_board(b_endgame)["expert"], "endgame")

        # 2. Queenless (> 14 pieces, queens == 0)
        b_queenless = chess.Board("r1b1kbnr/pppppppp/2n5/8/8/2N5/PPPPPPPP/R1B1KBNR w KQkq - 0 1")
        self.assertEqual(classify_expert(b_queenless), "queenless")
        self.assertTrue(extract_metrics_from_board(b_queenless)["is_queenless"])

        # 3. Imbalance (> 14 pieces, queens > 0, material imbalance or queen/rook+minor asymmetry)
        b_imbalance = chess.Board("rnb1kbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")
        self.assertEqual(classify_expert(b_imbalance), "imbalance")
        self.assertTrue(extract_metrics_from_board(b_imbalance)["is_imbalance"])

        # 4. Attack (> 14 pieces, balanced, opposite-side castling with queens)
        # White king on c1 (file 2), Black king on g8 (file 6)
        b_attack = chess.Board("r1bq1rk1/pppppppp/2n2n2/8/8/2N2N2/PPPPPPPP/2KR1BQR w - - 0 1")
        self.assertEqual(classify_expert(b_attack), "attack")
        self.assertTrue(extract_metrics_from_board(b_attack)["opposite_castling"])

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

    def test_standard_nnue_512x2_64_architecture_and_export(self):
        from export import export_standard
        import struct

        model = StandardNNUE()
        self.assertEqual(model.hidden_dim, 512)
        self.assertEqual(model.l1_dim, 64)
        self.assertEqual(tuple(model.ft.weight.shape), (512, 768))
        self.assertEqual(tuple(model.l1.weight.shape), (64, 1024))
        self.assertEqual(tuple(model.output.weight.shape), (1, 64))

        nnue_path = os.path.join(self.temp_dir, "test_standard.nnue")
        export_standard(model, nnue_path)
        self.assertEqual(os.path.getsize(nnue_path), 460_052)

        with open(nnue_path, "rb") as f:
            hdr = f.read(16)
        magic, version, arch_id, hidden_dim, l1_dim, _ = struct.unpack("<IIHHH2s", hdr)
        self.assertEqual(magic, 0x4B414E41)
        self.assertEqual(version, 1)
        self.assertEqual(arch_id, 1)
        self.assertEqual(hidden_dim, 512)
        self.assertEqual(l1_dim, 64)


    def test_multi_file_sharded_binpack_loader(self):
        shard1 = os.path.join(self.temp_dir, "shard1.binpack")
        shard2 = os.path.join(self.temp_dir, "shard2.binpack")
        b = chess.Board()

        with BinpackWriter(shard1, model_tag="open") as w1:
            for _ in range(120):
                w1.add_board(b, target_eval=0.1, target_wdl=0.5, openness=0.5)

        with BinpackWriter(shard2, model_tag="open") as w2:
            for _ in range(80):
                w2.add_board(b, target_eval=0.2, target_wdl=0.6, openness=0.6)

        # Test loading via list
        loader = FastMemoryMappedBatchLoader([shard1, shard2], batch_size=50, shuffle=False)
        self.assertEqual(loader.total_records, 200)
        self.assertEqual(len(loader), 4)

        seen = 0
        for batch in loader:
            seen += batch["our_features"].shape[0]
        self.assertEqual(seen, 200)
        loader.close()

        # Test loading via glob pattern
        glob_pattern = os.path.join(self.temp_dir, "shard*.binpack")
        loader_glob = FastMemoryMappedBatchLoader(glob_pattern, batch_size=64, shuffle=True)
        self.assertEqual(loader_glob.total_records, 200)
        loader_glob.close()

    def test_validation_split_batch_loader(self):
        bp_path = os.path.join(self.temp_dir, "split_test.binpack")
        b = chess.Board()

        with BinpackWriter(bp_path, model_tag="open") as w:
            for _ in range(300):
                w.add_board(b, target_eval=0.1, target_wdl=0.5, openness=0.5)

        # 10% validation split: 270 train, 30 val
        train_loader = FastMemoryMappedBatchLoader(bp_path, batch_size=100, val_split=0.10, is_validation=False)
        val_loader = FastMemoryMappedBatchLoader(bp_path, batch_size=100, val_split=0.10, is_validation=True)

        self.assertEqual(train_loader.total_records, 270)
        self.assertEqual(val_loader.total_records, 30)

        train_samples = sum(batch["our_features"].shape[0] for batch in train_loader)
        val_samples = sum(batch["our_features"].shape[0] for batch in val_loader)

        self.assertEqual(train_samples, 270)
        self.assertEqual(val_samples, 30)

        train_loader.close()
        val_loader.close()

    def test_cross_domain_mixture_batch_loader(self):
        primary_path = os.path.join(self.temp_dir, "primary.binpack")
        mix_path = os.path.join(self.temp_dir, "mix.binpack")
        b = chess.Board()

        # Primary has target_eval = +1.0
        with BinpackWriter(primary_path, model_tag="open") as w:
            for _ in range(400):
                w.add_board(b, target_eval=1.0, target_wdl=0.7, openness=0.8)

        # Mix has target_eval = -1.0
        with BinpackWriter(mix_path, model_tag="closed") as w:
            for _ in range(200):
                w.add_board(b, target_eval=-1.0, target_wdl=0.3, openness=0.2)

        # 80/20 mixture: batch_size=100 -> 80 primary, 20 mix
        loader = FastMemoryMappedBatchLoader(
            primary_path,
            batch_size=100,
            shuffle=False,
            mix_sources=mix_path,
            mix_ratio=0.20,
        )
        self.assertEqual(loader.primary_batch_size, 80)
        self.assertEqual(loader.mix_batch_size, 20)

        batches = list(loader)
        self.assertGreaterEqual(len(batches), 1)
        batch0 = batches[0]
        # Total batch size is exactly 100
        self.assertEqual(batch0["our_features"].shape[0], 100)

        # Verify 80 primary (1.0) and 20 mix (-1.0)
        evals = batch0["target_eval"].cpu().numpy()
        count_primary = np.sum(np.isclose(evals, 1.0, atol=1e-3))
        count_mix = np.sum(np.isclose(evals, -1.0, atol=1e-3))
        self.assertEqual(count_primary, 80)
        self.assertEqual(count_mix, 20)
        loader.close()

    def test_resolve_binpack_files(self):
        f1 = os.path.join(self.temp_dir, "a.binpack")
        f2 = os.path.join(self.temp_dir, "b.binpack")
        open(f1, "w").close()
        open(f2, "w").close()

        # By directory
        resolved_dir = resolve_binpack_files(self.temp_dir)
        self.assertEqual(len(resolved_dir), 2)

        # By glob
        resolved_glob = resolve_binpack_files(os.path.join(self.temp_dir, "*.binpack"))
        self.assertEqual(len(resolved_glob), 2)

        # By comma-separated string
        resolved_comma = resolve_binpack_files(f"{f1},{f2}")
        self.assertEqual(len(resolved_comma), 2)


    def test_horizontal_flip_augmentation(self):
        bp_path = os.path.join(self.temp_dir, "flip_test.binpack")
        b = chess.Board()

        with BinpackWriter(bp_path, model_tag="open") as w:
            for _ in range(50):
                w.add_board(b, target_eval=0.5, target_wdl=0.7, openness=0.5)

        loader = FastMemoryMappedBatchLoader(bp_path, batch_size=50, shuffle=False, augment_flip=True)
        batches = list(loader)
        self.assertEqual(len(batches), 1)
        # Verify valid features scattered and shape
        features = batches[0]["our_features"]
        self.assertEqual(features.shape, (50, 768))
        self.assertTrue(torch.all(features >= 0.0))
        self.assertTrue(torch.all(features <= 1.0))
        loader.close()

    def test_stockfish_dataset_urls_catalog(self):
        from download_data import STOCKFISH_D9_URLS
        self.assertGreaterEqual(len(STOCKFISH_D9_URLS), 10)
        has_d11 = any("_d11" in u[0] for u in STOCKFISH_D9_URLS)
        has_d9 = any("_d9" in u[0] for u in STOCKFISH_D9_URLS)
        self.assertTrue(has_d11)
        self.assertTrue(has_d9)

    def test_perspective_antisymmetry(self):
        from model import enforce_perspective_antisymmetry, StandardNNUE
        from dataset import encode_board_features
        model = StandardNNUE()
        enforce_perspective_antisymmetry(model)
        model.eval()

        # Symmetric board (startpos)
        b = chess.Board()
        our_idx, their_idx = encode_board_features(b)
        our_t = torch.zeros((1, 768), dtype=torch.float32)
        their_t = torch.zeros((1, 768), dtype=torch.float32)
        our_t[0, our_idx] = 1.0
        their_t[0, their_idx] = 1.0

        with torch.no_grad():
            eval_normal = model(our_t, their_t).item()
            eval_reversed = model(their_t, our_t).item()

        # Due to tempo bonus, normal vs reversed differs only by tempo
        # Without tempo (bias removed), they must be exact opposites
        tempo = model.output.bias.item()
        self.assertAlmostEqual(eval_normal - tempo, -(eval_reversed - tempo), places=4)

    def test_hardware_profiler_and_auto_tune(self):
        from hardware import HardwareProfiler, auto_tune_hardware
        profiler = HardwareProfiler()
        self.assertGreaterEqual(profiler.cpu_count, 1)
        self.assertGreater(profiler.ram_gb, 0.0)

        cfg = auto_tune_hardware(verbose=False)
        self.assertIn("batch_size", cfg)
        self.assertGreaterEqual(cfg["batch_size"], 1024)
        self.assertIn("workers", cfg)
        self.assertGreaterEqual(cfg["workers"], 1)
        self.assertIn("prefetch_batches", cfg)
        self.assertGreaterEqual(cfg["prefetch_batches"], 1)
        self.assertIn("chunk_size", cfg)
        self.assertGreaterEqual(cfg["chunk_size"], 1024)

        # Micro vs Standard NNUE profile differentiation
        cfg_std = auto_tune_hardware(is_micro=False, verbose=False)
        cfg_micro = auto_tune_hardware(is_micro=True, verbose=False)
        self.assertGreaterEqual(cfg_micro["batch_size"], cfg_std["batch_size"])
        self.assertGreaterEqual(cfg_micro["lr"], cfg_std["lr"])
        self.assertLessEqual(cfg_micro["weight_decay"], cfg_std["weight_decay"])
        self.assertLessEqual(cfg_micro["recommended_epochs"], cfg_std["recommended_epochs"])

    def test_sparse_embedding_forward_parity(self):
        from model import StandardNNUE, MicroNNUE, evaluate_benchmark_positions
        from binpack import encode_features_to_indices

        device = torch.device("cpu")
        std_model = StandardNNUE().to(device)
        micro_model = MicroNNUE().to(device)

        b = chess.Board()
        our_idx, their_idx, _ = encode_features_to_indices(b)

        # Dense float tensors
        dense_our = torch.zeros((1, 768), dtype=torch.float32)
        dense_their = torch.zeros((1, 768), dtype=torch.float32)
        for idx in our_idx:
            if idx < 768:
                dense_our[0, idx] = 1.0
        for idx in their_idx:
            if idx < 768:
                dense_their[0, idx] = 1.0

        # Sparse int tensors
        sparse_our = torch.from_numpy(np.array([our_idx], dtype=np.int32))
        sparse_their = torch.from_numpy(np.array([their_idx], dtype=np.int32))

        with torch.inference_mode():
            std_dense = std_model(dense_our, dense_their).item()
            std_sparse = std_model(sparse_our, sparse_their).item()
            self.assertAlmostEqual(std_dense, std_sparse, places=4)

            micro_dense = micro_model(dense_our, dense_their).item()
            micro_sparse = micro_model(sparse_our, sparse_their).item()
            self.assertAlmostEqual(micro_dense, micro_sparse, places=4)

        # Test benchmark evaluator
        summary = evaluate_benchmark_positions(std_model, device)
        self.assertIn("StartPos", summary)
        self.assertIn("Tactical Refutation", summary)
        self.assertIn("KRvK Endgame", summary)
        self.assertIn("Symmetric Draw", summary)


if __name__ == "__main__":
    unittest.main()
