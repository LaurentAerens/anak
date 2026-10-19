"""
End-to-End Test for Preprocessing, Binpack Splitting, and Query Recipe.
"""

import os
import shutil
import tempfile
import unittest
import chess
import chess.pgn

from preprocess import preprocess_data
from binpack import BinpackReader
from fast_loader import FastMemoryMappedBatchLoader


class TestPreprocessE2E(unittest.TestCase):
    def setUp(self):
        self.temp_dir = tempfile.mkdtemp()
        self.input_dir = os.path.join(self.temp_dir, "raw")
        self.output_dir = os.path.join(self.temp_dir, "preprocessed")
        os.makedirs(self.input_dir, exist_ok=True)

        # Generate a small PGN file with diverse positions and comments
        pgn_path = os.path.join(self.input_dir, "tcec_test.pgn")
        with open(pgn_path, "w", encoding="utf-8") as f:
            for game_idx in range(5):
                game = chess.pgn.Game()
                game.headers["Event"] = f"Test Game {game_idx}"
                game.headers["Result"] = "1-0" if game_idx % 2 == 0 else "1/2-1/2"
                b = game.board()
                node = game
                # Play 20 legal moves
                for ply in range(20):
                    legal = list(b.legal_moves)
                    if not legal:
                        break
                    m = legal[0]
                    b.push(m)
                    node = node.add_variation(m)
                    node.comment = f"[%eval {0.10 * ply:+.2f}]"
                f.write(str(game) + "\n\n")

    def tearDown(self):
        shutil.rmtree(self.temp_dir, ignore_errors=True)

    def test_e2e_preprocess_and_load(self):
        manifest = preprocess_data(
            input_paths=[self.input_dir],
            output_dir=self.output_dir
        )

        self.assertIn("open.binpack", manifest["generated_files"])
        self.assertIn("closed.binpack", manifest["generated_files"])
        self.assertIn("micro.binpack", manifest["generated_files"])

        open_path = os.path.join(self.output_dir, "open.binpack")
        self.assertTrue(os.path.isfile(open_path))

        reader = BinpackReader(open_path)
        self.assertGreater(len(reader), 0)
        reader.close()

        loader = FastMemoryMappedBatchLoader(open_path, batch_size=16)
        batches = list(loader)
        self.assertGreater(len(batches), 0)
        self.assertEqual(batches[0]["our_features"].shape[1], 768)
        loader.close()


if __name__ == "__main__":
    unittest.main()
