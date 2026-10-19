"""
TCEC PGN & FEN Dataset Reader and Topology Filter for Anak NNUE Training.
Streams positions from TCEC PGN archives, parses engine eval comments,
calculates pawn openness, and encodes HalfKP features.
"""

import io
import os
import re
import gzip
import zipfile
import random
from typing import Union, List, Optional
import chess
import chess.pgn
import torch
from torch.utils.data import IterableDataset

FEATURE_COUNT = 768

# Regex to extract engine evaluation comments from PGNs:
# 1. Standard lichess/chessbase: {%eval 0.42/35} or {[%eval -1.25]} or [%eval #3]
# 2. TCEC White Value: {..., wv=0.89, ...}
# 3. UCI eval: {..., eval=0.89, ...}
EVAL_REGEXES = [
    re.compile(r"\[?%?eval\s*[:=]?\s*([+-]?\d+(?:\.\d+)?|#[+-]?\d+)", re.IGNORECASE),
    re.compile(r"\bwv=([+-]?\d+(?:\.\d+)?)", re.IGNORECASE),
    re.compile(r"\beval=([+-]?\d+(?:\.\d+)?)", re.IGNORECASE),
]


def parse_eval_from_comment(comment: str) -> Optional[float]:
    """Extracts numerical evaluation from PGN comment string, clamped to [-30.0, 30.0]."""
    if not comment:
        return None
    for rx in EVAL_REGEXES:
        m = rx.search(comment)
        if m:
            val_str = m.group(1)
            if val_str.startswith("#"):
                mate_in = int(val_str[1:])
                return 30.0 if mate_in > 0 else -30.0
            try:
                val = float(val_str)
                return max(-30.0, min(30.0, val))
            except ValueError:
                continue
    return None


def find_pgn_files(path_or_dir: Union[str, List[str]]) -> List[str]:
    """
    Recursively discovers all .pgn, .pgn.gz, and .zip files from a path or directory.
    If uncompressed .pgn exists for a .zip file, the .zip is omitted to avoid duplicate processing.
    """
    if isinstance(path_or_dir, (list, tuple)):
        collected = []
        for p in path_or_dir:
            collected.extend(find_pgn_files(p))
        return sorted(list(set(collected)))

    if not path_or_dir:
        return []

    p = os.path.abspath(path_or_dir)
    if not os.path.exists(p):
        return []

    # Guard against accidentally walking filesystem root or drive root
    drive, path_part = os.path.splitdrive(p)
    if path_part in ("", "/", "\\"):
        return []

    if os.path.isfile(p):
        if p.endswith((".pgn", ".pgn.gz", ".zip")):
            return [p]
        return []

    if os.path.isdir(p):
        pgn_files = []
        zip_files = []
        base_depth = p.count(os.sep)
        for root, dirs, files in os.walk(p):
            dirs[:] = [d for d in dirs if not d.startswith(".")]
            if root.count(os.sep) - base_depth > 2:
                dirs.clear()
            for file in sorted(files):
                full_path = os.path.join(root, file)
                if file.endswith((".pgn", ".pgn.gz")):
                    pgn_files.append(full_path)
                elif file.endswith(".zip"):
                    zip_files.append(full_path)

        # If .pgn already exists, avoid reprocessing identical .zip files
        pgn_basenames = {os.path.splitext(os.path.basename(f))[0] for f in pgn_files}
        for zf in zip_files:
            z_base = os.path.splitext(os.path.basename(zf))[0]
            if z_base.endswith(".pgn"):
                z_base = os.path.splitext(z_base)[0]
            if z_base not in pgn_basenames:
                pgn_files.append(zf)

        return sorted(pgn_files)

    return []


def calculate_openness(board: chess.Board) -> float:
    """
    Computes pawn topology openness score matching Anak's C# PawnTopology.CalculateOpenness.
    Returns float in range [0.0, 1.0].
    """
    white_pawns = board.pieces_mask(chess.PAWN, chess.WHITE)
    black_pawns = board.pieces_mask(chess.PAWN, chess.BLACK)
    all_pawns = white_pawns | black_pawns
    total_pawns = bin(all_pawns).count("1")

    # 1. Locked pawns (rams): white pawn blocked directly ahead by black pawn
    locked_rams = white_pawns & (black_pawns >> 8)
    locked_count = bin(locked_rams).count("1")

    # 2. Open and semi-open files
    open_files = 0
    semi_open_files = 0
    file_masks = [0x0101010101010101 << i for i in range(8)]

    for mask in file_masks:
        has_white = (white_pawns & mask) != 0
        has_black = (black_pawns & mask) != 0
        if not has_white and not has_black:
            open_files += 1
        elif not has_white or not has_black:
            semi_open_files += 1

    # 3. Center pawns (files c, d, e, f)
    center_mask = file_masks[2] | file_masks[3] | file_masks[4] | file_masks[5]
    center_pawns = bin(all_pawns & center_mask).count("1")

    openness = (
        0.35
        + (open_files * 0.12)
        + (semi_open_files * 0.05)
        - (locked_count * 0.10)
        + ((8 - center_pawns) * 0.03)
        + ((16 - total_pawns) * 0.02)
    )

    return max(0.0, min(1.0, openness))


def find_hanging_piece_blunder(board: chess.Board):
    """
    Identifies if a legal move exists that hangs an undefended piece or walks
    into an attack by a lower-valued piece (e.g. piece attacked by pawn).
    Returns (blunder_move, penalty_eval) or (None, 0.0).
    """
    moving_color = board.turn
    enemy_color = not moving_color

    for move in board.legal_moves:
        p = board.piece_at(move.from_square)
        if not p or p.piece_type in (chess.KING, chess.PAWN):
            continue

        board.push(move)
        enemy_attackers = board.attackers(enemy_color, move.to_square)
        friendly_defenders = board.attackers(moving_color, move.to_square)
        board.pop()

        if not enemy_attackers:
            continue

        # 1. Undefended piece
        if not friendly_defenders:
            penalty = -9.0 if p.piece_type == chess.QUEEN else (-5.0 if p.piece_type == chess.ROOK else -3.0)
            return move, penalty

        # 2. Defended, but attacked by a lower-valued pawn
        pawn_attackers = enemy_attackers & board.pieces(chess.PAWN, enemy_color)
        if pawn_attackers and p.piece_type in (chess.KNIGHT, chess.BISHOP, chess.ROOK, chess.QUEEN):
            penalty = -8.0 if p.piece_type == chess.QUEEN else (-4.0 if p.piece_type == chess.ROOK else -2.0)
            return move, penalty

    return None, 0.0


def encode_board_features(board: chess.Board):
    """
    Encodes chess.Board into HalfKP sparse feature indices for our perspective and their perspective.
    """
    our_indices = []
    their_indices = []

    is_white_turn = board.turn == chess.WHITE

    for sq in chess.SQUARES:
        piece = board.piece_at(sq)
        if piece is None:
            continue

        p_idx = piece.piece_type - 1  # 0..5
        c_idx = 0 if piece.color == chess.WHITE else 1

        # White perspective:
        white_feat = ((c_idx * 6) + p_idx) * 64 + sq
        # Black perspective (mirrored rank, inverted color):
        black_feat = (((1 - c_idx) * 6) + p_idx) * 64 + (sq ^ 56)

        if is_white_turn:
            our_indices.append(white_feat)
            their_indices.append(black_feat)
        else:
            our_indices.append(black_feat)
            their_indices.append(white_feat)

    return our_indices, their_indices


class TcecDataset(IterableDataset):
    """
    Streaming dataset reading TCEC PGN archives.
    Accepts single files, directories, or lists of files (.pgn, .pgn.gz, .zip).
    Filters positions according to the target network: 'open', 'closed', or 'micro'.
    """
    def __init__(
        self,
        pgn_path: Union[str, List[str]],
        target_model: str = "open",
        skip_book_plies: int = 10,
        max_samples: Optional[int] = None
    ):
        super().__init__()
        self.target_model = target_model.lower()  # 'open', 'closed', 'micro'
        self.skip_book_plies = skip_book_plies
        self.max_samples = max_samples

        if isinstance(pgn_path, list):
            self.pgn_files = pgn_path
        else:
            self.pgn_files = find_pgn_files(pgn_path)

        if not self.pgn_files:
            raise FileNotFoundError(
                f"No PGN archives (.pgn, .pgn.gz, .zip) found at '{pgn_path}'."
            )

    def _stream_file(self, file_path: str):
        """Yields text file handles for the given file or zip members."""
        if file_path.endswith(".gz"):
            with gzip.open(file_path, "rt", encoding="utf-8", errors="replace") as f:
                yield f
        elif file_path.endswith(".zip"):
            with zipfile.ZipFile(file_path, "r") as zf:
                for member in zf.namelist():
                    if member.endswith(".pgn"):
                        with zf.open(member, "r") as raw:
                            with io.TextIOWrapper(raw, encoding="utf-8", errors="replace") as f:
                                yield f
        else:
            with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                yield f

    def __iter__(self):
        sample_count = 0
        for file_path in self.pgn_files:
            for handle in self._stream_file(file_path):
                while True:
                    try:
                        game = chess.pgn.read_game(handle)
                    except Exception:
                        break
                    if game is None:
                        break

                    # Extract game result
                    result_str = game.headers.get("Result", "*")
                    if result_str == "1-0":
                        game_result = 1.0
                    elif result_str == "0-1":
                        game_result = 0.0
                    elif result_str == "1/2-1/2":
                        game_result = 0.5
                    else:
                        continue

                    board = game.board()
                    ply = 0
                    prev_eval = 0.0

                    for node in game.mainline():
                        move = node.move
                        is_move_capture = board.is_capture(move)
                        board.push(move)
                        ply += 1

                        if ply <= self.skip_book_plies:
                            continue

                        # Parse engine eval comment
                        eval_val = parse_eval_from_comment(node.comment)
                        if eval_val is None:
                            continue

                        # Eval relative to current side to move
                        eval_relative = eval_val if board.turn == chess.WHITE else -eval_val
                        wdl_relative = game_result if board.turn == chess.WHITE else (1.0 - game_result)

                        # Check topology filter
                        openness = calculate_openness(board)
                        eval_delta = abs(eval_val - prev_eval)
                        prev_eval = eval_val

                        # 10% sample of opposite topology for cross-regularization / baseline generalization
                        is_secondary_10pct = random.random() < 0.10

                        accept = False
                        if self.target_model == "open":
                            # 90% open positions (openness >= 0.50) + 10% closed positions for regularization
                            if openness >= 0.50 or is_secondary_10pct:
                                accept = True
                        elif self.target_model == "closed":
                            # 90% closed positions (openness < 0.50) + 10% open positions for regularization
                            if openness < 0.50 or is_secondary_10pct:
                                accept = True
                        elif self.target_model in ("micro", "small"):
                            # 1. Synthetic Blunder Injection:
                            # In quiet master positions (|eval| < 1.0), with 30% probability, generate a hanging-piece blunder.
                            # This converts flawless engine games into rich tactical blunder training data!
                            if abs(eval_val) < 1.0 and random.random() < 0.30:
                                b_move, penalty = find_hanging_piece_blunder(board)
                                if b_move:
                                    board.push(b_move)
                                    our_idx_b, their_idx_b = encode_board_features(board)
                                    board.pop()

                                    our_t = torch.zeros(FEATURE_COUNT, dtype=torch.float32)
                                    their_t = torch.zeros(FEATURE_COUNT, dtype=torch.float32)
                                    our_t[our_idx_b] = 1.0
                                    their_t[their_idx_b] = 1.0

                                    # Opponent's perspective right after blunder is made: heavily winning (+3, +5, +9)
                                    yield {
                                        "our_features": our_t,
                                        "their_features": their_t,
                                        "target_wdl": torch.tensor(1.0, dtype=torch.float32),
                                        "target_eval": torch.tensor(-penalty, dtype=torch.float32)
                                    }
                                    sample_count += 1
                                    if self.max_samples and sample_count >= self.max_samples:
                                        return

                            # Focus on major tactical swings (blunders, traps), positions after captures, and endgames
                            piece_count = bin(board.occupied).count("1")
                            is_blunder = eval_delta >= 1.5 or abs(eval_val) >= 2.0
                            is_capture = is_move_capture
                            # Also retain 10% quiet baseline positions as negative/neutral training samples
                            if is_blunder or is_capture or piece_count <= 16 or is_secondary_10pct:
                                accept = True

                        if not accept:
                            continue

                        # Target evaluation formatting:
                        # For Micro blunder detector:
                        # Deadzone small positional noise: anything below 2.0 pawns is treated as 0.0 (safe).
                        # Severe blunders (>= 2.0 pawns) are rounded to nearest integer (e.g. +2.8 -> +3.0, -4.1 -> -4.0)
                        # so the 16 neurons focus entirely on learning gross tactical imbalances.
                        if self.target_model in ("micro", "small"):
                            if abs(eval_relative) < 2.0:
                                target_eval_final = 0.0
                            else:
                                target_eval_final = float(round(eval_relative))
                        else:
                            target_eval_final = eval_relative

                        # Feature encoding
                        our_idx, their_idx = encode_board_features(board)

                        our_tensor = torch.zeros(FEATURE_COUNT, dtype=torch.float32)
                        their_tensor = torch.zeros(FEATURE_COUNT, dtype=torch.float32)

                        our_tensor[our_idx] = 1.0
                        their_tensor[their_idx] = 1.0

                        yield {
                            "our_features": our_tensor,
                            "their_features": their_tensor,
                            "target_wdl": torch.tensor(wdl_relative, dtype=torch.float32),
                            "target_eval": torch.tensor(target_eval_final, dtype=torch.float32)
                        }

                        sample_count += 1
                        if self.max_samples and sample_count >= self.max_samples:
                            return
