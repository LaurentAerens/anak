"""
Pawn Topology and NNUE Expert Classification.
Mirrors src/Anak.Core/Topology/PawnTopology.cs for offline analysis and ingestion.
High-performance bitboard implementation using native Python bitwise operations.
"""

from enum import Enum
import chess

# Precomputed 64-bit file masks
FILE_A = 0x0101010101010101
FILE_B = 0x0202020202020202
FILE_C = 0x0404040404040404
FILE_D = 0x0808080808080808
FILE_E = 0x1010101010101010
FILE_F = 0x2020202020202020
FILE_G = 0x4040404040404040
FILE_H = 0x8080808080808080

FILE_MASKS = [FILE_A, FILE_B, FILE_C, FILE_D, FILE_E, FILE_F, FILE_G, FILE_H]
CENTER_FILES = FILE_C | FILE_D | FILE_E | FILE_F
QUEEN_SIDE_FILES = FILE_A | FILE_B | FILE_C
KING_SIDE_FILES = FILE_F | FILE_G | FILE_H


class NnueExpertType(str, Enum):
    OPEN = "Open"
    CLOSED = "Closed"
    ENDGAME = "Endgame"
    QUEENLESS = "Queenless"
    IMBALANCE = "Imbalance"
    ATTACK = "Attack"


def calculate_openness(board: chess.Board) -> float:
    """
    Computes continuous openness score between 0.0 and 1.0.
    Direct bitboard implementation matching PawnTopology.CalculateOpenness.
    """
    white_mask = int(board.occupied_co[chess.WHITE])
    black_mask = int(board.occupied_co[chess.BLACK])
    pawns_mask = int(board.pawns)

    white_pawns = white_mask & pawns_mask
    black_pawns = black_mask & pawns_mask
    all_pawns = white_pawns | black_pawns
    total_pawns = all_pawns.bit_count()

    # 1. Locked pawns (rams): White pawn directly blocked head-to-head by Black pawn
    locked_rams = white_pawns & (black_pawns >> 8)
    locked_count = locked_rams.bit_count()

    # 2. Open and Semi-open files
    open_files = 0
    semi_open_files = 0
    for file_mask in FILE_MASKS:
        has_white = bool(white_pawns & file_mask)
        has_black = bool(black_pawns & file_mask)
        if not has_white and not has_black:
            open_files += 1
        elif not has_white or not has_black:
            semi_open_files += 1

    # 3. Pawn tension (pawns in mutual attack contact)
    white_attacks = ((white_pawns << 7) & ~FILE_H) | ((white_pawns << 9) & ~FILE_A)
    tension_count = (white_attacks & black_pawns).bit_count()

    # 4. Center pawn liquidation (c, d, e, f files)
    center_pawns = (all_pawns & CENTER_FILES).bit_count()

    openness = (
        0.35
        + (open_files * 0.12)
        + (semi_open_files * 0.05)
        - (locked_count * 0.10)
        + (tension_count * 0.04)
        + ((8 - center_pawns) * 0.03)
        + ((16 - total_pawns) * 0.02)
    )

    return max(0.0, min(1.0, openness))


def is_endgame(board: chess.Board, max_pieces: int = 14) -> bool:
    """Returns True if the total piece count is <= max_pieces."""
    return int(board.occupied).bit_count() <= max_pieces


def is_queenless(board: chess.Board) -> bool:
    """Returns True if neither side has a Queen on the board."""
    return int(board.queens) == 0


def has_material_imbalance(board: chess.Board, margin: int = 2) -> bool:
    """
    Returns True if the position has a material imbalance.
    """
    w_mask = int(board.occupied_co[chess.WHITE])
    b_mask = int(board.occupied_co[chess.BLACK])

    w_q = (int(board.queens) & w_mask).bit_count()
    b_q = (int(board.queens) & b_mask).bit_count()
    if w_q != b_q:
        return True

    w_r = (int(board.rooks) & w_mask).bit_count()
    b_r = (int(board.rooks) & b_mask).bit_count()

    minors_mask = int(board.knights | board.bishops)
    w_m = (minors_mask & w_mask).bit_count()
    b_m = (minors_mask & b_mask).bit_count()
    if w_r != b_r and w_m != b_m:
        return True

    pawns_mask = int(board.pawns)
    w_p = (pawns_mask & w_mask).bit_count()
    b_p = (pawns_mask & b_mask).bit_count()

    w_mat = (w_q * 9) + (w_r * 5) + (w_m * 3) + w_p
    b_mat = (b_q * 9) + (b_r * 5) + (b_m * 3) + b_p

    return abs(w_mat - b_mat) >= margin


def is_opposite_side_castling(board: chess.Board) -> bool:
    """
    Returns True if Queens are on board and Kings are on opposite flanks (files A-C vs F-H).
    """
    if int(board.queens) == 0:
        return False

    w_mask = int(board.occupied_co[chess.WHITE])
    b_mask = int(board.occupied_co[chess.BLACK])
    kings_mask = int(board.kings)

    w_king = kings_mask & w_mask
    b_king = kings_mask & b_mask
    if not w_king or not b_king:
        return False

    w_queen_side = bool(w_king & QUEEN_SIDE_FILES)
    w_king_side = bool(w_king & KING_SIDE_FILES)
    b_queen_side = bool(b_king & QUEEN_SIDE_FILES)
    b_king_side = bool(b_king & KING_SIDE_FILES)

    return (w_queen_side and b_king_side) or (w_king_side and b_queen_side)


def classify_expert(board: chess.Board,
                    open_threshold: float = 0.50,
                    endgame_max_pieces: int = 14,
                    imbalance_margin: int = 2) -> NnueExpertType:
    """
    Classifies board into one of the specialized NNUE experts using priority routing.
    """
    if is_endgame(board, endgame_max_pieces):
        return NnueExpertType.ENDGAME

    if is_queenless(board):
        return NnueExpertType.QUEENLESS

    if has_material_imbalance(board, imbalance_margin):
        return NnueExpertType.IMBALANCE

    if is_opposite_side_castling(board):
        return NnueExpertType.ATTACK

    openness = calculate_openness(board)
    return NnueExpertType.OPEN if openness >= open_threshold else NnueExpertType.CLOSED
