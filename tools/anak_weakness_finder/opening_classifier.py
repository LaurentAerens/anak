"""
High-Performance Opening & Pawn Structure Classifier.
Maps arbitrary FEN board states into canonical chess opening families, ECO codes,
and tactical pawn structure archetypes.
"""

from typing import Tuple
import chess


def classify_opening(fen: str) -> Tuple[str, str, str]:
    """
    Classifies a board position FEN into (ECO, Opening Family, Pawn Structure).
    """
    try:
        board = chess.Board(fen)
    except Exception:
        return ("---", "Unknown", "Unclassified")

    w_pawns = {chess.square_name(s) for s in board.pieces(chess.PAWN, chess.WHITE)}
    b_pawns = {chess.square_name(s) for s in board.pieces(chess.PAWN, chess.BLACK)}

    # Locked rams (White pawn blocked directly by Black pawn on same file)
    locked_d = ('d4' in w_pawns and 'd5' in b_pawns) or ('d5' in w_pawns and 'd6' in b_pawns)
    locked_e = ('e4' in w_pawns and 'e5' in b_pawns) or ('e5' in w_pawns and 'e6' in b_pawns)

    # 1. Sicilian Defense (c5 played by Black, asymmetric center)
    if 'c5' in b_pawns or (chess.E4 in board.pieces(chess.PAWN, chess.WHITE) and 'c7' not in b_pawns and 'd6' in b_pawns and 'e5' not in b_pawns):
        structure = "Sicilian Asymmetry (c5 vs d4)"
        if 'g6' in b_pawns:
            return ("B70", "Sicilian Defense: Dragon", structure)
        if 'a6' in b_pawns and 'e6' in b_pawns:
            return ("B40", "Sicilian Defense: Paulsen / Kan", structure)
        if 'a6' in b_pawns:
            return ("B90", "Sicilian Defense: Najdorf", structure)
        if 'e6' in b_pawns:
            return ("B80", "Sicilian Defense: Scheveningen", structure)
        return ("B20", "Sicilian Defense", structure)

    # 2. French Defense (e6, d5 vs e4/e5/d4)
    if 'e6' in b_pawns and 'd5' in b_pawns and ('e5' in w_pawns or 'e4' in w_pawns or 'd4' in w_pawns):
        structure = "French Pawn Chain (d4-e5 vs d5-e6)"
        if 'c5' in b_pawns:
            return ("C02", "French Defense: Advance / Winawer", structure)
        return ("C00", "French Defense", structure)

    # 3. Caro-Kann Defense (c6, d5 vs e4/d4)
    if 'c6' in b_pawns and 'd5' in b_pawns:
        structure = "Caro-Kann Pawn Structure (c6-d5)"
        return ("B10", "Caro-Kann Defense", structure)

    # 4. King's Indian & Benoni (d5 ram vs e5 or c5)
    if 'd5' in w_pawns and ('e5' in b_pawns or 'c5' in b_pawns or 'd6' in b_pawns):
        structure = "Locked Center Ram (d5 vs e5/c5)"
        if 'g6' in b_pawns or 'g7' in b_pawns:
            return ("E60", "King's Indian Defense", structure)
        if 'c5' in b_pawns:
            return ("A60", "Benoni Defense", structure)
        return ("E60", "King's Indian Setup", structure)

    # 5. Dutch Defense (f5 vs d4)
    if 'f5' in b_pawns and ('d4' in w_pawns or 'd5' in w_pawns):
        structure = "Dutch Asymmetric Flank (f5 vs d4)"
        if 'c6' in b_pawns and 'e6' in b_pawns:
            return ("A85", "Dutch Defense: Stonewall", structure)
        if 'g6' in b_pawns:
            return ("A87", "Dutch Defense: Leningrad", structure)
        return ("A80", "Dutch Defense", structure)

    # 6. Queen's Gambit & Slav
    if 'd4' in w_pawns and 'c4' in w_pawns and 'd5' in b_pawns:
        structure = "Queen's Gambit Tension (c4-d4 vs d5)"
        if 'c6' in b_pawns and 'e6' in b_pawns:
            return ("D43", "Semi-Slav Defense", structure)
        if 'c6' in b_pawns:
            return ("D10", "Slav Defense", structure)
        if 'e6' in b_pawns:
            return ("D30", "Queen's Gambit Declined", structure)
        return ("D06", "Queen's Gambit", structure)

    # 7. Open Games (1.e4 e5)
    if 'e4' in w_pawns and 'e5' in b_pawns:
        structure = "Open Center (e4 vs e5)"
        w_bishops = board.pieces(chess.BISHOP, chess.WHITE)
        if chess.B5 in w_bishops:
            return ("C60", "Ruy Lopez", structure)
        if chess.C4 in w_bishops:
            return ("C50", "Italian Game", structure)
        if 'd4' in w_pawns:
            return ("C44", "Scotch Game / Center Game", structure)
        return ("C20", "Open Game (1.e4 e5)", structure)

    # 8. English Opening / Flank
    if 'c4' in w_pawns and 'd4' not in w_pawns:
        structure = "English Flank (c4 vs e5/c5)"
        if 'e5' in b_pawns:
            return ("A20", "English Opening: King's English", structure)
        return ("A10", "English Opening", structure)

    # 9. Queen's Pawn / Indian Defense
    if 'd4' in w_pawns:
        structure = "Queen's Pawn Structure"
        if 'g6' in b_pawns:
            return ("A40", "Modern / Pirc Setup", structure)
        if 'b6' in b_pawns:
            return ("E12", "Queen's Indian Defense", structure)
        return ("A40", "Queen's Pawn Game", structure)

    # Fallback
    structure = "Symmetrical / Flank" if (locked_d or locked_e) else "Fluid Center"
    return ("A00", "Flank / Uncommon Opening", structure)
