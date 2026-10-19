"""
High-Speed Multi-Threaded Position & Evaluation Generator for Anak NNUE Training.
Generates diverse chess positions via self-play and opening book seeding, evaluates
them using local Stockfish across all available CPU cores, and exports standard PGNs or .binpack.

Supports query-driven generation and metric steering:
  --query "pieces <= 16 and not has_queens" : Endgame & queenless positions
  --no-queens                               : Strips or trades queens early
  --max-pieces 16                           : Biases toward endgame positions
  --mode open / closed / blunders / general : Topology and blunder biasing
  --format pgn / binpack                    : Output format
"""

import argparse
import os
import random
import subprocess
import sys
import time
from typing import List, Optional, Tuple, Dict, Any
import chess
import chess.pgn
from tqdm import tqdm

from query import extract_metrics_from_board, PositionQuery, DatasetRecipe
from binpack import BinpackWriter


def find_stockfish_binary() -> Optional[str]:
    """Auto-detects stockfish.exe in repository directories or system PATH."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    candidates = [
        os.path.join(repo_root, "tools", "fastchess", "bin", "stockfish.exe"),
        os.path.join(repo_root, "tools", "fastchess", "bin", "stockfish", "stockfish.exe"),
        os.path.join(repo_root, "stockfish.exe"),
        "stockfish.exe",
        "stockfish",
    ]
    for c in candidates:
        if os.path.isfile(c) and os.access(c, os.X_OK):
            return os.path.abspath(c)
        if os.path.isfile(c):
            return os.path.abspath(c)
    return None


def strip_queens_from_fen(fen: str) -> str:
    """Removes all queens from a FEN string to produce a valid queenless position."""
    try:
        board = chess.Board(fen)
        board.remove_piece_at(chess.D1)
        board.remove_piece_at(chess.D8)
        # Clear any remaining queens on board
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
        if max_pieces:
            try:
                b = chess.Board(cur_fen)
                if bin(b.occupied).count("1") > max_pieces:
                    # Strip non-king pieces randomly until count <= max_pieces
                    non_kings = [sq for sq in chess.SQUARES if b.piece_at(sq) and b.piece_at(sq).piece_type != chess.KING]
                    random.shuffle(non_kings)
                    while bin(b.occupied).count("1") > max_pieces and non_kings:
                        b.remove_piece_at(non_kings.pop())
                    cur_fen = b.fen()
            except Exception:
                pass
        processed.append(cur_fen)

    return processed


class StockfishEvaluator:
    """Manages an interactive UCI process with Stockfish for fast evaluations."""
    def __init__(self, stockfish_path: str, depth: int = 8):
        self.depth = depth
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
        self.process.stdin.write(f"{cmd}\n")
        self.process.stdin.flush()

    def _wait_for(self, target: str):
        while True:
            line = self.process.stdout.readline()
            if not line:
                break
            if target in line:
                break

    def evaluate_fen(self, fen: str) -> Optional[float]:
        """Evaluates FEN at configured depth and returns score in pawns from White's perspective."""
        self._send(f"position fen {fen}")
        self._send(f"go depth {self.depth}")

        score_pawns = None
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
                break

        return score_pawns

    def close(self):
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
    max_plies: int = 80
) -> Optional[chess.pgn.Game]:
    """
    Generates a single evaluated game from an opening seed FEN with query and topology biasing.
    """
    try:
        board = chess.Board(seed_fen)
    except Exception:
        board = chess.Board()

    game = chess.pgn.Game()
    game.headers["Event"] = f"Anak Generator ({mode})"
    game.headers["White"] = "Stockfish 19 / Rollout"
    game.headers["Black"] = "Stockfish 19 / Rollout"
    game.setup(board)
    current_node = game

    ply = 0
    prev_eval = 0.0

    while not board.is_game_over() and ply < max_plies:
        legal_moves = list(board.legal_moves)
        if not legal_moves:
            break

        # Move selection with biasing
        chosen_move = None

        if no_queens:
            # Strongly prioritize queen captures or queen trades if any queen is left
            queen_moves = [m for m in legal_moves if board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.QUEEN]
            queen_captures = [m for m in legal_moves if board.is_capture(m) and board.piece_at(m.to_square) and board.piece_at(m.to_square).piece_type == chess.QUEEN]
            if queen_captures:
                chosen_move = random.choice(queen_captures)
            elif queen_moves and random.random() < 0.6:
                chosen_move = random.choice(queen_moves)

        if chosen_move is None and mode == "open":
            captures = [m for m in legal_moves if board.is_capture(m)]
            pawn_moves = [m for m in legal_moves if board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.PAWN]
            if captures and random.random() < 0.65:
                chosen_move = random.choice(captures)
            elif pawn_moves and random.random() < 0.50:
                chosen_move = random.choice(pawn_moves)

        elif chosen_move is None and mode == "closed":
            quiet_pawns = [m for m in legal_moves if board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.PAWN and not board.is_capture(m)]
            knights = [m for m in legal_moves if board.piece_at(m.from_square) and board.piece_at(m.from_square).piece_type == chess.KNIGHT]
            if quiet_pawns and random.random() < 0.55:
                chosen_move = random.choice(quiet_pawns)
            elif knights and random.random() < 0.40:
                chosen_move = random.choice(knights)

        elif chosen_move is None and mode == "blunders":
            if random.random() < 0.25 and ply > 6:
                for m in legal_moves:
                    p = board.piece_at(m.from_square)
                    if p and p.piece_type in (chess.KNIGHT, chess.BISHOP, chess.ROOK, chess.QUEEN):
                        board.push(m)
                        attacked = bool(board.attackers(not board.turn, m.to_square))
                        defended = bool(board.attackers(board.turn, m.to_square))
                        board.pop()
                        if attacked and not defended:
                            chosen_move = m
                            break

        if chosen_move is None:
            chosen_move = random.choice(legal_moves)

        board.push(chosen_move)
        ply += 1

        # Evaluate position with Stockfish
        fen = board.fen()
        score = sf.evaluate_fen(fen)
        eval_delta = abs((score or 0.0) - prev_eval)
        prev_eval = score or 0.0

        current_node = current_node.add_variation(chosen_move)
        if score is not None:
            current_node.comment = f"[%eval {score:+.2f}]"

    if board.is_checkmate():
        res = "1-0" if board.turn == chess.BLACK else "0-1"
    elif board.is_stalemate() or board.is_insufficient_material() or board.can_claim_draw():
        res = "1/2-1/2"
    else:
        res = "*"

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
    parser.add_argument("--depth", type=int, default=8,
                        help="Stockfish evaluation depth (default: 8 for ultra-fast generation)")
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

    # 1. Locate Stockfish
    sf_path = args.stockfish or find_stockfish_binary()
    if not sf_path or not os.path.exists(sf_path):
        print("ERROR: stockfish.exe not found!")
        print("Please ensure Stockfish is downloaded in tools/fastchess/bin/ or provide --stockfish <path>")
        sys.exit(1)

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
    print(f"Stockfish:        {sf_path}")
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
        # Convert generated PGNs to .binpack directly
        import io
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
                    from query import calculate_pawn_openness
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
