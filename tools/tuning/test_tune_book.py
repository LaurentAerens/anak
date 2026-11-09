#!/usr/bin/env python3
"""
Unit tests for Anak Reinforcement Learning Opening Book Tuner (tune_book.py).
"""

import io
from pathlib import Path
import tempfile
import unittest

import chess
import chess.pgn
import chess.polyglot

from .tune_book import (
    BookEntry,
    PolyGlotBookTable,
    ReinforcementBookTuner,
    decode_polyglot_move,
    encode_polyglot_move,
)


class TestPolyGlotMoveEncoding(unittest.TestCase):
    def test_standard_moves(self):
        board = chess.Board()
        e4 = chess.Move.from_uci("e2e4")
        raw = encode_polyglot_move(board, e4)
        # e2: file 4, rank 1; e4: file 4, rank 3
        # raw: (4) | (3 << 3) | (4 << 6) | (1 << 9) = 4 + 24 + 256 + 512 = 796 = 0x31C
        self.assertEqual(raw, 0x31C)
        decoded = decode_polyglot_move(raw, board)
        self.assertEqual(decoded, e4)

    def test_castling_polyglot_conversion(self):
        # White kingside castle: e1g1 -> encoded as e1h1 (0x107)
        board = chess.Board("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R w KQkq - 4 4")
        castle_white = chess.Move.from_uci("e1g1")
        raw = encode_polyglot_move(board, castle_white)
        self.assertEqual(raw, 0x107)
        decoded = decode_polyglot_move(raw, board)
        self.assertEqual(decoded, castle_white)

        # Black kingside castle: e8g8 -> encoded as e8h8
        board_b = chess.Board("r1bqk2r/pppp1ppp/2n2n2/2b1p3/2B1P3/2N2N2/PPPP1PPP/R1BQK2R b KQkq - 4 4")
        castle_black = chess.Move.from_uci("e8g8")
        raw_b = encode_polyglot_move(board_b, castle_black)
        decoded_b = decode_polyglot_move(raw_b, board_b)
        self.assertEqual(decoded_b, castle_black)

    def test_promotion_move(self):
        board = chess.Board("8/4P3/8/8/8/8/8/4K2k w - - 0 1")
        promo_q = chess.Move.from_uci("e7e8q")
        raw = encode_polyglot_move(board, promo_q)
        decoded = decode_polyglot_move(raw, board)
        self.assertEqual(decoded, promo_q)


class TestPolyGlotBookTable(unittest.TestCase):
    def test_round_trip_save_and_load(self):
        with tempfile.TemporaryDirectory() as tmpdir:
            book_path = Path(tmpdir) / "test_book.bin"
            table = PolyGlotBookTable()
            table.entries[(100, 0x31C)] = BookEntry(key=100, raw_move=0x31C, weight=50, learn=0)
            table.entries[(50, 0x2DB)] = BookEntry(key=50, raw_move=0x2DB, weight=100, learn=0)
            table.entries[(50, 0x29A)] = BookEntry(key=50, raw_move=0x29A, weight=30, learn=0)

            table.save(book_path, backup=False)
            self.assertTrue(book_path.exists())

            loaded = PolyGlotBookTable()
            count = loaded.load(book_path)
            self.assertEqual(count, 3)

            # Strict sort: key 50 must appear before key 100
            entries_50 = loaded.get_entries_for_key(50)
            self.assertEqual(len(entries_50), 2)
            self.assertEqual(entries_50[0].weight, 100)  # sorted by weight descending
            self.assertEqual(entries_50[1].weight, 30)

    def test_prune_entries(self):
        table = PolyGlotBookTable()
        table.entries[(1, 1)] = BookEntry(key=1, raw_move=1, weight=5, learn=(10 << 16) | 1)  # 1 win in 10 games (10%)
        table.entries[(1, 2)] = BookEntry(key=1, raw_move=2, weight=50, learn=(10 << 16) | 7) # 7 wins in 10 games (70%)
        table.entries[(2, 1)] = BookEntry(key=2, raw_move=1, weight=0, learn=0)

        # Prune weight < 1
        pruned_wt = table.prune_low_weight(min_weight=1)
        self.assertEqual(pruned_wt, 1)

        # Prune win rate < 20%
        pruned_wr = table.prune_blunders(min_games=5, max_win_rate=0.20)
        self.assertEqual(pruned_wr, 1)
        self.assertEqual(len(table), 1)


class TestReinforcementLearning(unittest.TestCase):
    def setUp(self):
        self.table = PolyGlotBookTable()
        self.board = chess.Board()
        self.e4_key = chess.polyglot.zobrist_hash(self.board)
        self.e4_raw = encode_polyglot_move(self.board, chess.Move.from_uci("e2e4"))
        self.d4_raw = encode_polyglot_move(self.board, chess.Move.from_uci("d2d4"))
        self.table.entries[(self.e4_key, self.e4_raw)] = BookEntry(self.e4_key, self.e4_raw, weight=100)
        self.table.entries[(self.e4_key, self.d4_raw)] = BookEntry(self.e4_key, self.d4_raw, weight=100)

        self.tuner = ReinforcementBookTuner(
            book_table=self.table,
            learning_rate=20.0,
            discount_factor=0.9,
            elo_scaling=True,
            max_ply=10,
        )

    def test_win_reinforcement(self):
        pgn_text = """[Event "Test Match"]
[White "Anak-Dev"]
[Black "FoxSEE-8.2"]
[Result "1-0"]
[BlackElo "2500"]

1. e4 e5 2. Nf3 Nc6 3. Bc4 1-0
"""
        game = chess.pgn.read_game(io.StringIO(pgn_text))
        res = self.tuner.train_game(game, target_player="Anak")
        self.assertEqual(res["status"], "success")
        self.assertEqual(res["reward"], 1.0)

        # e4 was played in a win -> weight should increase above 100
        e4_entry = self.table.entries[(self.e4_key, self.e4_raw)]
        self.assertGreater(e4_entry.weight, 100)
        self.assertEqual(e4_entry.games_played, 1)
        self.assertEqual(e4_entry.games_won, 1)

        # d4 was not played -> weight remains 100
        d4_entry = self.table.entries[(self.e4_key, self.d4_raw)]
        self.assertEqual(d4_entry.weight, 100)

    def test_loss_penalization(self):
        pgn_text = """[Event "Test Match"]
[White "Anak-Dev"]
[Black "Stockfish-3000"]
[Result "0-1"]
[BlackElo "3000"]

1. d4 Nf6 2. c4 e6 0-1
"""
        game = chess.pgn.read_game(io.StringIO(pgn_text))
        res = self.tuner.train_game(game, target_player="Anak")
        self.assertEqual(res["status"], "success")
        self.assertEqual(res["reward"], 0.0)

        # d4 was played in a loss -> weight should decrease below 100
        d4_entry = self.table.entries[(self.e4_key, self.d4_raw)]
        self.assertLess(d4_entry.weight, 100)
        self.assertEqual(d4_entry.games_played, 1)
        self.assertEqual(d4_entry.games_won, 0)


if __name__ == "__main__":
    unittest.main()
