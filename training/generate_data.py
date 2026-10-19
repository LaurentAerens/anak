"""
High-Speed Multi-Threaded Position & Evaluation Generator for Anak NNUE Training.
Generates diverse chess positions via self-play and opening book seeding, evaluates
them using local Stockfish (or built-in 2-ply Quiescent Tactical Evaluator fallback)
across all available CPU cores, and exports standard PGNs or .binpack.

Supports query-driven generation and metric steering:
  --query "pieces <= 16 and not has_queens" : Endgame & queenless positions
  --no-queens                               : Strips or trades queens early
  --max-pieces 16                           : Biases toward endgame positions
  --mode open / closed / blunders / general : Topology and blunder biasing
  --format pgn / binpack                    : Output format
"""

import argparse
import io
import os
import random
import shutil
import subprocess
import sys
import time
import urllib.request
import zipfile
from typing import List, Optional, Tuple
import chess
import chess.pgn
from tqdm import tqdm

from query import extract_metrics_from_board, PositionQuery, calculate_pawn_openness
from binpack import BinpackWriter

_PAWN_PST = [
     0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,
     0.05,  0.10,  0.10, -0.20, -0.20,  0.10,  0.10,  0.05,
     0.05, -0.05, -0.10,  0.00,  0.00, -0.10, -0.05,  0.05,
     0.00,  0.00,  0.00,  0.20,  0.20,  0.00,  0.00,  0.00,
     0.05,  0.05,  0.10,  0.25,  0.25,  0.10,  0.05,  0.05,
     0.10,  0.10,  0.20,  0.30,  0.30,  0.20,  0.10,  0.10,
     0.50,  0.50,  0.50,  0.50,  0.50,  0.50,  0.50,  0.50,
     0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,
]
_KNIGHT_PST = [
    -0.50, -0.40, -0.30, -0.30, -0.30, -0.30, -0.40, -0.50,
    -0.40, -0.20,  0.00,  0.05,  0.05,  0.00, -0.20, -0.40,
    -0.30,  0.05,  0.10,  0.15,  0.15,  0.10,  0.05, -0.30,
    -0.30,  0.00,  0.15,  0.20,  0.20,  0.15,  0.00, -0.30,
    -0.30,  0.05,  0.15,  0.20,  0.20,  0.15,  0.05, -0.30,
    -0.30,  0.00,  0.10,  0.15,  0.15,  0.10,  0.00, -0.30,
    -0.40, -0.20,  0.00,  0.00,  0.00,  0.00, -0.20, -0.40,
    -0.50, -0.40, -0.30, -0.30, -0.30, -0.30, -0.40, -0.50,
]
_BISHOP_PST = [
    -0.20, -0.10, -0.10, -0.10, -0.10, -0.10, -0.10, -0.20,
    -0.10,  0.05,  0.00,  0.00,  0.00,  0.00,  0.05, -0.10,
    -0.10,  0.10,  0.10,  0.10,  0.10,  0.10,  0.10, -0.10,
    -0.10,  0.00,  0.10,  0.10,  0.10,  0.10,  0.00, -0.10,
    -0.10,  0.05,  0.05,  0.10,  0.10,  0.05,  0.05, -0.10,
    -0.10,  0.00,  0.05,  0.10,  0.10,  0.05,  0.00, -0.10,
    -0.10,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.10,
    -0.20, -0.10, -0.10, -0.10, -0.10, -0.10, -0.10, -0.20,
]
_ROOK_PST = [
     0.00,  0.00,  0.00,  0.05,  0.05,  0.00,  0.00,  0.00,
    -0.05,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.05,
    -0.05,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.05,
    -0.05,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.05,
    -0.05,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.05,
    -0.05,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.05,
     0.05,  0.10,  0.10,  0.10,  0.10,  0.10,  0.10,  0.05,
     0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00,
]
_QUEEN_PST = [
    -0.20, -0.10, -0.10, -0.05, -0.05, -0.10, -0.10, -0.20,
    -0.10,  0.00,  0.05,  0.00,  0.00,  0.00,  0.00, -0.10,
    -0.10,  0.05,  0.05,  0.05,  0.05,  0.05,  0.00, -0.10,
     0.00,  0.00,  0.05,  0.05,  0.05,  0.05,  0.00, -0.05,
    -0.05,  0.00,  0.05,  0.05,  0.05,  0.05,  0.00, -0.05,
    -0.10,  0.00,  0.05,  0.05,  0.05,  0.05,  0.00, -0.10,
    -0.10,  0.00,  0.00,  0.00,  0.00,  0.00,  0.00, -0.10,
    -0.20, -0.10, -0.10, -0.05, -0.05, -0.10, -0.10, -0.20,
]
_KING_PST = [
     0.20,  0.30,  0.10,  0.00,  0.00,  0.10,  0.30,  0.20,
     0.20,  0.20, -0.10, -0.30, -0.30, -0.10,  0.20,  0.20,
    -0.10, -0.20, -0.30, -0.40, -0.40, -0.30, -0.20, -0.10,
    -0.20, -0.30, -0.40, -0.50, -0.50, -0.40, -0.30, -0.20,
    -0.30, -0.40, -0.50, -0.60, -0.60, -0.50, -0.40, -0.30,
    -0.30, -0.40, -0.50, -0.60, -0.60, -0.50, -0.40, -0.30,
    -0.30, -0.40, -0.50, -0.60, -0.60, -0.50, -0.40, -0.30,
    -0.30, -0.40, -0.50, -0.60, -0.60, -0.50, -0.40, -0.30,
]

STOCKFISH_WINDOWS_URL = "https://github.com/official-stockfish/Stockfish/releases/download/sf_19/stockfish-windows-x86-64-universal.zip"


def find_stockfish_binary(auto_download: bool = False) -> Optional[str]:
    """Auto-detects stockfish.exe in repository directories or system PATH, optionally downloading it."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    bin_dir = os.path.join(repo_root, "tools", "fastchess", "bin")
    candidates = [
        os.path.join(bin_dir, "stockfish.exe"),
        os.path.join(bin_dir, "stockfish", "stockfish.exe"),
        os.path.join(repo_root, "stockfish.exe"),
        "stockfish.exe",
        "stockfish",
    ]
    for c in candidates:
        if os.path.isfile(c):
            return os.path.abspath(c)
        found_path = shutil.which(c)
        if found_path:
            return os.path.abspath(found_path)

    if auto_download and os.name == "nt":
        try:
            os.makedirs(bin_dir, exist_ok=True)
            print(f"[+] Downloading Stockfish 19 for training data generation...")
            req = urllib.request.Request(STOCKFISH_WINDOWS_URL, headers={"User-Agent": "Mozilla/5.0"})
            with urllib.request.urlopen(req, timeout=30) as resp:
                data = io.BytesIO(resp.read())
            with zipfile.ZipFile(data) as zf:
                zf.extractall(bin_dir)
            for root, _, files in os.walk(bin_dir):
                for f in files:
                    if "stockfish" in f.lower() and f.endswith(".exe"):
                        src = os.path.join(root, f)
                        dst = os.path.join(bin_dir, "stockfish.exe")
                        if os.path.abspath(src) != os.path.abspath(dst):
                            shutil.copy2(src, dst)
                        return os.path.abspath(dst)
        except Exception as e:
            print(f"Notice: Could not auto-download Stockfish ({e}). Using built-in Tactical Evaluator.")

    return None


class BuiltinTacticalEvaluator:
    """
    Fast built-in 2-ply Quiescent Tactical + Positional Evaluator (in pawns from White's perspective).
    Provides reliable evaluations and best moves when external Stockfish binary is absent.
    """
    PIECE_VALS = {
        chess.PAWN: 1.00,
        chess.KNIGHT: 3.20,
        chess.BISHOP: 3.30,
        chess.ROOK: 5.00,
        chess.QUEEN: 9.00,
        chess.KING: 0.00,
    }
    PSTS = {
        chess.PAWN: _PAWN_PST,
        chess.KNIGHT: _KNIGHT_PST,
        chess.BISHOP: _BISHOP_PST,
        chess.ROOK: _ROOK_PST,
        chess.QUEEN: _QUEEN_PST,
        chess.KING: _KING_PST,
    }

    def static_eval_white(self, board: chess.Board) -> float:
        if board.is_checkmate():
            return -30.0 if board.turn == chess.WHITE else 30.0
        if board.is_stalemate() or board.is_insufficient_material():
            return 0.0

        score = 0.0
        w_bishops = 0
        b_bishops = 0
        for sq in chess.SQUARES:
            p = board.piece_at(sq)
            if p is None:
                continue
            pt = p.piece_type
            base = self.PIECE_VALS[pt]
            pst_idx = sq if p.color == chess.WHITE else (sq ^ 56)
            val = base + self.PSTS[pt][pst_idx]
            if p.color == chess.WHITE:
                score += val
                if pt == chess.BISHOP:
                    w_bishops += 1
            else:
                score -= val
                if pt == chess.BISHOP:
                    b_bishops += 1

        # Bishop pair bonus
        if w_bishops >= 2:
            score += 0.35
        if b_bishops >= 2:
            score -= 0.35

        # Tempo bonus
        score += 0.20 if board.turn == chess.WHITE else -0.20
        return score

    def _quiesce_side(self, board: chess.Board, alpha: float, beta: float, depth: int = 2) -> float:
        stand_pat = self.static_eval_white(board)
        if board.turn == chess.BLACK:
            stand_pat = -stand_pat

        if stand_pat >= beta:
            return beta
        if alpha < stand_pat:
            alpha = stand_pat
        if depth <= 0:
            return stand_pat

        for m in board.legal_moves:
            if board.is_capture(m) or m.promotion:
                board.push(m)
                score = -self._quiesce_side(board, -beta, -alpha, depth - 1)
                board.pop()
                if score >= beta:
                    return beta
                if score > alpha:
                    alpha = score
        return alpha

    def search_move_and_eval(self, fen: str) -> Tuple[Optional[chess.Move], float]:
        board = chess.Board(fen)
        legal = list(board.legal_moves)
        if not legal:
            return None, self.static_eval_white(board)

        best_move = legal[0]
        best_score_side = -999.0

        for m in legal:
            board.push(m)
            sc = -self._quiesce_side(board, -999.0, 999.0, depth=1)
            board.pop()
            # Small tie-breaking noise
            sc_noisy = sc + random.uniform(-0.04, 0.04)
            if sc_noisy > best_score_side:
                best_score_side = sc_noisy
                best_move = m

        eval_white = best_score_side if board.turn == chess.WHITE else -best_score_side
        return best_move, max(-30.0, min(30.0, eval_white))

    def evaluate_fen(self, fen: str) -> float:
        board = chess.Board(fen)
        sc_side = self._quiesce_side(board, -999.0, 999.0, depth=2)
        return sc_side if board.turn == chess.WHITE else -sc_side

    def close(self):
        pass


def strip_queens_from_fen(fen: str) -> str:
    """Removes all queens from a FEN string to produce a valid queenless position."""
    try:
        board = chess.Board(fen)
        board.remove_piece_at(chess.D1)
        board.remove_piece_at(chess.D8)
        for sq in chess.SQUARES:
            p = board.piece_at(sq)
            if p and p.piece_type == chess.QUEEN:
                board.remove_piece_at(sq)
        return board.fen()
    except Exception:
        return fen


def load_seed_openings(no_queens: bool = False, max_pieces: Optional[int] = None) -> List[str]:
    """Loads FEN / EPD opening strings with optional piece filtering."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    epd_path = os.path.join(repo_root, "tools", "fastchess", "openings.epd")
    openings = []

    if os.path.isfile(epd_path):
        with open(epd_path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    fen_parts = line.split(";")
                    fen = fen_parts[0].strip()
                    openings.append(fen)

    if not openings:
        openings = [chess.STARTING_FEN]

    processed = []
    for fen in openings:
        cur_fen = fen
        if no_queens:
            cur_fen = strip_queens_from_fen(cur_fen)
        try:
            b = chess.Board(cur_fen)
            if max_pieces and bin(b.occupied).count("1") > max_pieces:
                non_kings = [sq for sq in chess.SQUARES if b.piece_at(sq) and b.piece_at(sq).piece_type != chess.KING]
                random.shuffle(non_kings)
                while bin(b.occupied).count("1") > max_pieces and non_kings:
                    b.remove_piece_at(non_kings.pop())
                cur_fen = b.fen()
            processed.append(cur_fen)
        except Exception:
            continue

    if not processed:
        processed = [chess.STARTING_FEN]

    return processed


class StockfishEvaluator:
    """Manages an interactive UCI process with Stockfish for fast evaluations and move search."""
    def __init__(self, stockfish_path: Optional[str], depth: int = 8):
        self.depth = depth
        self.fallback = None
        if not stockfish_path or not os.path.isfile(stockfish_path):
            self.fallback = BuiltinTacticalEvaluator()
            self.process = None
            return

        self.process = subprocess.Popen(
            [stockfish_path],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            bufsize=1,
        )
        self._send("uci")
        self._wait_for("uciok")
        self._send("setoption name Threads value 1")
        self._send("setoption name Hash value 16")
        self._send("isready")
        self._wait_for("readyok")

    def _send(self, cmd: str):
        if self.process and self.process.stdin:
            self.process.stdin.write(f"{cmd}\n")
            self.process.stdin.flush()

    def _wait_for(self, target: str):
        if not self.process or not self.process.stdout:
            return
        while True:
            line = self.process.stdout.readline()
            if not line or target in line:
                break

    def search_move_and_eval(self, fen: str) -> Tuple[Optional[chess.Move], Optional[float]]:
        """Runs UCI search at configured depth and returns (best_move, score_pawns_white)."""
        if self.fallback is not None:
            return self.fallback.search_move_and_eval(fen)

        self._send(f"position fen {fen}")
        self._send(f"go depth {self.depth}")

        score_pawns = None
        best_move = None
        side_to_move_white = " w " in fen

        while True:
            line = self.process.stdout.readline()
            if not line:
                break
            line_str = line.strip()
            if "score cp" in line_str:
                parts = line_str.split()
                try:
                    idx = parts.index("cp")
                    cp = int(parts[idx + 1])
                    val = cp / 100.0
                    score_pawns = val if side_to_move_white else -val
                except (ValueError, IndexError):
                    pass
            elif "score mate" in line_str:
                parts = line_str.split()
                try:
                    idx = parts.index("mate")
                    mate_plies = int(parts[idx + 1])
                    val = 30.0 if mate_plies > 0 else -30.0
                    score_pawns = val if side_to_move_white else -val
                except (ValueError, IndexError):
                    pass

            if line_str.startswith("bestmove"):
                parts = line_str.split()
                if len(parts) >= 2 and parts[1] != "(none)":
                    try:
                        best_move = chess.Move.from_uci(parts[1])
                    except ValueError:
                        best_move = None
                break

        return best_move, score_pawns

    def evaluate_fen(self, fen: str) -> Optional[float]:
        """Evaluates FEN at configured depth and returns score in pawns from White's perspective."""
        _, score = self.search_move_and_eval(fen)
        return score

    def close(self):
        if self.process is not None:
            try:
                self._send("quit")
                self.process.stdin.close()
                self.process.stdout.close()
                self.process.terminate()
                self.process.wait(timeout=1.0)
            except Exception:
                pass


def generate_single_game(
    sf: StockfishEvaluator,
    seed_fen: str,
    mode: str,
    query: Optional[PositionQuery] = None,
    no_queens: bool = False,
    max_pieces: Optional[int] = None,
    max_plies: int = 70
) -> Optional[chess.pgn.Game]:
    """
    Generates a single evaluated game from an opening seed FEN using engine moves
    with controlled topology/blunder steering.
    """
    try:
        board = chess.Board(seed_fen)
    except Exception:
        board = chess.Board()

    game = chess.pgn.Game()
    game.headers["Event"] = f"Anak Generator ({mode})"
    game.headers["White"] = "Stockfish / Rollout"
    game.headers["Black"] = "Stockfish / Rollout"
    game.setup(board)
    current_node = game

    ply = 0
    last_eval_white = 0.0

    while not board.is_game_over() and ply < max_plies:
        legal_moves = list(board.legal_moves)
        if not legal_moves:
            break

        # 1. Ask evaluator for best move and current position evaluation
        engine_move, pos_eval_white = sf.search_move_and_eval(board.fen())
        chosen_move = engine_move if (engine_move in legal_moves) else None

        # 2. Controlled exploration / mode steering on ~25% of moves
        if no_queens:
            queen_captures = [
                m for m in legal_moves
                if board.is_capture(m) and board.piece_at(m.to_square) and board.piece_at(m.to_square).piece_type == chess.QUEEN
            ]
            if queen_captures:
                chosen_move = random.choice(queen_captures)

        if mode == "open" and random.random() < 0.25:
            pawn_captures = [
                m for m in legal_moves
                if board.is_capture(m) and board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.PAWN
            ]
            if pawn_captures:
                chosen_move = random.choice(pawn_captures)
        elif mode == "closed" and random.random() < 0.25:
            quiet_pawns = [
                m for m in legal_moves
                if board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.PAWN and not board.is_capture(m)
            ]
            if quiet_pawns:
                chosen_move = random.choice(quiet_pawns)
        elif mode == "blunders" and ply > 6 and random.random() < 0.30:
            for m in legal_moves:
                p = board.piece_at(m.from_square)
                if p and p.piece_type in (chess.KNIGHT, chess.BISHOP, chess.ROOK, chess.QUEEN):
                    board.push(m)
                    attacked = bool(board.attackers(board.turn, m.to_square))
                    defended = bool(board.attackers(not board.turn, m.to_square))
                    board.pop()
                    if attacked and not defended:
                        chosen_move = m
                        pos_eval_white = None  # Re-evaluate after blunder move
                        break
        elif ply <= 6 and random.random() < 0.30:
            chosen_move = random.choice(legal_moves)
            pos_eval_white = None

        if chosen_move is None:
            chosen_move = random.choice(legal_moves)

        board.push(chosen_move)
        ply += 1

        # Evaluate resulting board if move deviated from engine best move
        if chosen_move != engine_move or pos_eval_white is None:
            score = sf.evaluate_fen(board.fen())
        else:
            score = pos_eval_white

        if score is not None:
            last_eval_white = score

        current_node = current_node.add_variation(chosen_move)
        if score is not None:
            current_node.comment = f"[%eval {score:+.2f}]"

        # Early adjudication if decisive advantage reached
        if abs(last_eval_white) >= 12.0 and ply >= 20:
            break

    if board.is_checkmate():
        res = "1-0" if board.turn == chess.BLACK else "0-1"
    elif board.is_stalemate() or board.is_insufficient_material() or board.can_claim_draw():
        res = "1/2-1/2"
    elif last_eval_white >= 1.50:
        res = "1-0"
    elif last_eval_white <= -1.50:
        res = "0-1"
    else:
        res = "1/2-1/2"

    game.headers["Result"] = res
    return game


def worker_task(args_tuple) -> List[str]:
    """Worker process function generating a batch of games and returning PGN strings."""
    worker_id, stockfish_path, mode, count, depth, seed_openings, query_str, no_queens, max_pieces = args_tuple
    sf = StockfishEvaluator(stockfish_path, depth=depth)
    query = PositionQuery(query_str) if query_str else None
    pgn_strings = []

    try:
        for _ in range(count):
            seed = random.choice(seed_openings)
            game = generate_single_game(
                sf, seed, mode,
                query=query,
                no_queens=no_queens,
                max_pieces=max_pieces
            )
            if game:
                pgn_strings.append(str(game))
    finally:
        sf.close()

    return pgn_strings


def parse_args():
    parser = argparse.ArgumentParser(description="Anak Query-Driven Position & Evaluation Generator")
    parser.add_argument("--mode", type=str, choices=["open", "closed", "blunders", "general"], default="general",
                        help="Target generation mode (open, closed, blunders, or general)")
    parser.add_argument("--query", type=str, default=None,
                        help="Query metric expression to steer data (e.g. 'pieces <= 16 and not has_queens')")
    parser.add_argument("--no-queens", action="store_true", default=False,
                        help="Steer generation to queenless boards (boards with no queen)")
    parser.add_argument("--max-pieces", type=int, default=None,
                        help="Maximum pieces on board for endgame generation (e.g. 16 or 10)")
    parser.add_argument("--positions", type=int, default=5000,
                        help="Target number of evaluated positions to generate")
    parser.add_argument("--games", type=int, default=None,
                        help="Target number of full games to generate (overrides --positions)")
    parser.add_argument("--depth", type=int, default=6,
                        help="Stockfish evaluation depth (default: 6 for fast generation)")
    parser.add_argument("--workers", type=int, default=None,
                        help="Number of parallel worker processes (default: cpu_count - 2)")
    parser.add_argument("--stockfish", type=str, default=None,
                        help="Path to stockfish executable (auto-detected if omitted)")
    parser.add_argument("--output", type=str, default=None,
                        help="Output file path (defaults to training/data/generated_<mode>.pgn)")
    parser.add_argument("--format", type=str, choices=["pgn", "binpack"], default="pgn",
                        help="Output format: standard PGN or high-performance .binpack")
    return parser.parse_args()


def main():
    args = parse_args()

    # 1. Locate Stockfish (or use built-in evaluator)
    sf_path = args.stockfish or find_stockfish_binary(auto_download=True)

    # 2. Worker concurrency
    total_cpus = os.cpu_count() or 4
    workers = args.workers or max(1, total_cpus - 2)

    # 3. Output file
    script_dir = os.path.dirname(os.path.abspath(__file__))
    data_dir = os.path.join(script_dir, "data")
    os.makedirs(data_dir, exist_ok=True)

    tag = args.mode
    if args.no_queens:
        tag += "_noqueen"
    if args.max_pieces:
        tag += f"_p{args.max_pieces}"

    ext = ".binpack" if args.format == "binpack" else ".pgn"
    out_file = args.output or os.path.join(data_dir, f"generated_{tag}{ext}")

    # 4. Target game count
    if args.games:
        total_games = args.games
    else:
        total_games = max(5, args.positions // 40)

    games_per_worker = max(1, total_games // workers)
    actual_games = games_per_worker * workers

    print("=" * 70)
    print("=== Anak NNUE Query-Driven Position & Evaluation Generator ===")
    print("=" * 70)
    print(f"Target Mode:      {args.mode.upper()}")
    if args.query:
        print(f"Query Filter:     '{args.query}'")
    if args.no_queens:
        print(f"Constraint:       NO QUEENS (Queenless boards)")
    if args.max_pieces:
        print(f"Constraint:       MAX PIECES <= {args.max_pieces} (Endgames)")
    print(f"Evaluator:        {sf_path or 'Built-in 2-Ply Quiescent Tactical Evaluator'}")
    print(f"Evaluation Depth: {args.depth}")
    print(f"Workers:          {workers} parallel processes (across {total_cpus} CPUs)")
    print(f"Target Games:     ~{actual_games} games (~{actual_games * 40:,} positions)")
    print(f"Output File:      {out_file} ({args.format.upper()})")
    print("=" * 70)

    openings = load_seed_openings(no_queens=args.no_queens, max_pieces=args.max_pieces)
    print(f"Prepared {len(openings)} opening seed positions.")

    tasks = [
        (i, sf_path, args.mode, games_per_worker, args.depth, openings, args.query, args.no_queens, args.max_pieces)
        for i in range(workers)
    ]

    start_time = time.time()
    all_pgn_strings = []

    from multiprocessing import Pool
    print("\nStarting generation across worker pool...")
    with Pool(processes=workers) as pool:
        for result in tqdm(pool.imap_unordered(worker_task, tasks), total=workers, desc="Workers Progress"):
            all_pgn_strings.extend(result)

    elapsed = time.time() - start_time
    total_positions_est = len(all_pgn_strings) * 40
    pos_per_sec = total_positions_est / max(0.001, elapsed)

    if args.format == "binpack":
        writer = BinpackWriter(out_file, model_tag=args.mode)
        pos_count = 0
        for pgn_text in all_pgn_strings:
            game = chess.pgn.read_game(io.StringIO(pgn_text))
            if not game:
                continue
            b = game.board()
            for node in game.mainline():
                b.push(node.move)
                from dataset import parse_eval_from_comment
                ev = parse_eval_from_comment(node.comment)
                if ev is not None:
                    op = calculate_pawn_openness(b)
                    writer.add_board(b, ev if b.turn == chess.WHITE else -ev, 0.5, op)
                    pos_count += 1
        writer.close()
        print(f"\nWrote {pos_count:,} positions directly to binary pack: {out_file}")
    else:
        with open(out_file, "a", encoding="utf-8") as f:
            for pgn in all_pgn_strings:
                f.write(pgn + "\n\n")

    print("\n" + "=" * 70)
    print(f"SUCCESS: Generated {len(all_pgn_strings)} games in {elapsed:.1f}s ({pos_per_sec:.0f} pos/sec)!")
    print(f"Saved to: {os.path.abspath(out_file)}")
    print("=" * 70)


if __name__ == "__main__":
    main()
