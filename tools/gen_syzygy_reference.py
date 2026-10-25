#!/usr/bin/env python3
"""
Generates tests/Anak.Tests/Data/syzygy_reference.txt: ground-truth WDL/DTZ values for random legal positions,
computed with python-chess (an independent Syzygy implementation) against the tables shipped in ./syzygy.

Line format:  <FEN> | <wdl> | <dtz>

Usage:  .venv\\Scripts\\python.exe tools\\gen_syzygy_reference.py
"""
import os
import random
from pathlib import Path

import chess
import chess.syzygy

ROOT = Path(__file__).resolve().parent.parent
TB_DIR = ROOT / "syzygy"
OUT = ROOT / "tests" / "Anak.Tests" / "Data" / "syzygy_reference.txt"

PIECE_TYPES = {"K": chess.KING, "Q": chess.QUEEN, "R": chess.ROOK, "B": chess.BISHOP, "N": chess.KNIGHT, "P": chess.PAWN}
PER_TABLE = 40


def random_position(name: str, rng: random.Random):
    white_part, black_part = name.split("v")
    for _ in range(200):
        board = chess.Board(None)
        squares = rng.sample(range(64), len(white_part) + len(black_part))
        ok = True
        i = 0
        for color, part in ((chess.WHITE, white_part), (chess.BLACK, black_part)):
            for ch in part:
                sq = squares[i]
                i += 1
                if ch == "P" and chess.square_rank(sq) in (0, 7):
                    ok = False
                    break
                board.set_piece_at(sq, chess.Piece(PIECE_TYPES[ch], color))
            if not ok:
                break
        if not ok:
            continue
        board.turn = rng.choice([chess.WHITE, chess.BLACK])
        board.castling_rights = 0
        if board.is_valid():
            return board
    return None


def en_passant_positions(rng: random.Random, count: int):
    out = []
    for _ in range(count * 20):
        if len(out) >= count:
            break
        board = chess.Board(None)
        file_w = rng.randrange(8)
        file_b = file_w + rng.choice([-1, 1])
        if not 0 <= file_b < 8:
            continue
        if rng.random() < 0.5:
            # White to move, black just played ...d7-d5 style.
            board.set_piece_at(chess.square(file_w, 4), chess.Piece(chess.PAWN, chess.WHITE))
            board.set_piece_at(chess.square(file_b, 4), chess.Piece(chess.PAWN, chess.BLACK))
            board.turn = chess.WHITE
            board.ep_square = chess.square(file_b, 5)
        else:
            board.set_piece_at(chess.square(file_w, 3), chess.Piece(chess.PAWN, chess.BLACK))
            board.set_piece_at(chess.square(file_b, 3), chess.Piece(chess.PAWN, chess.WHITE))
            board.turn = chess.BLACK
            board.ep_square = chess.square(file_b, 2)
        free = [s for s in range(64) if board.piece_at(s) is None]
        ks = rng.sample(free, 2)
        board.set_piece_at(ks[0], chess.Piece(chess.KING, chess.WHITE))
        board.set_piece_at(ks[1], chess.Piece(chess.KING, chess.BLACK))
        board.castling_rights = 0
        if board.is_valid() and any(board.generate_legal_ep()):
            out.append(board)
    return out


def main() -> None:
    rng = random.Random(20261025)
    names = sorted({p.stem for p in TB_DIR.glob("*.rtbw")})
    lines = []
    with chess.syzygy.open_tablebase(str(TB_DIR)) as tb:
        boards = []
        for name in names:
            got = 0
            while got < PER_TABLE:
                b = random_position(name, rng)
                if b is None:
                    break
                boards.append(b)
                got += 1
        boards.extend(en_passant_positions(rng, 60))

        for b in boards:
            try:
                wdl = tb.probe_wdl(b)
                dtz = tb.probe_dtz(b)
            except (KeyError, chess.syzygy.MissingTableError):
                continue
            lines.append(f"{b.fen()} | {wdl} | {dtz}")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"Wrote {len(lines)} reference positions across {len(names)} tables to {OUT}")


if __name__ == "__main__":
    main()
