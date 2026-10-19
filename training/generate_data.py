"""
High-Speed Multi-Threaded Position & Evaluation Generator for Anak NNUE Training.
Generates diverse chess positions via self-play and opening book seeding, evaluates
them using local Stockfish across all available CPU cores, and exports standard PGNs.

Supports targeted generation modes:
  --mode open     : Biases toward open center files, early pawn trades, and piece play.
  --mode closed   : Biases toward locked pawn wedges and maneuvering structures.
  --mode blunders : Generates tactical blunder refutations, forks, and hanging pieces for Micro-NNUE.
  --mode general  : Broad balanced self-play across diverse master openings.
"""

import argparse
import os
import random
import subprocess
import sys
import time
from typing import List, Optional, Tuple
import chess
import chess.pgn
from tqdm import tqdm


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


def load_seed_openings() -> List[str]:
    """Loads FEN / EPD opening strings from tools/fastchess/openings.epd if available."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    epd_path = os.path.join(repo_root, "tools", "fastchess", "openings.epd")
    openings = []

    if os.path.isfile(epd_path):
        with open(epd_path, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith("#"):
                    # EPD lines usually end with operations like 'ce 0; pm ...'
                    fen_parts = line.split(";")
                    fen = fen_parts[0].strip()
                    openings.append(fen)

    if not openings:
        openings = [chess.STARTING_FEN]

    return openings


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
                    # cp is relative to side to move in UCI
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


def calculate_openness(board: chess.Board) -> float:
    """Computes pawn topology openness score in range [0.0, 1.0]."""
    white_pawns = board.pieces_mask(chess.PAWN, chess.WHITE)
    black_pawns = board.pieces_mask(chess.PAWN, chess.BLACK)
    all_pawns = white_pawns | black_pawns
    total_pawns = bin(all_pawns).count("1")

    locked_rams = white_pawns & (black_pawns >> 8)
    locked_count = bin(locked_rams).count("1")

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


def generate_single_game(
    sf: StockfishEvaluator,
    seed_fen: str,
    mode: str,
    max_plies: int = 80
) -> Optional[chess.pgn.Game]:
    """
    Generates a single evaluated game from an opening seed FEN with topology biasing.
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
    while not board.is_game_over() and ply < max_plies:
        legal_moves = list(board.legal_moves)
        if not legal_moves:
            break

        # Move selection with mode biasing
        chosen_move = None

        if mode == "open":
            # Prefer captures, pawn trades, and open-file piece development
            captures = [m for m in legal_moves if board.is_capture(m)]
            pawn_moves = [m for m in legal_moves if board.piece_at(m.from_square).piece_type == chess.PAWN]
            if captures and random.random() < 0.65:
                chosen_move = random.choice(captures)
            elif pawn_moves and random.random() < 0.50:
                chosen_move = random.choice(pawn_moves)
            else:
                chosen_move = random.choice(legal_moves)

        elif mode == "closed":
            # Prefer non-capture pawn pushes and closed maneuvering
            quiet_pawns = [m for m in legal_moves if board.piece_at(m.from_square).piece_type == chess.PAWN and not board.is_capture(m)]
            knights = [m for m in legal_moves if board.piece_at(m.from_square).piece_type == chess.KNIGHT]
            if quiet_pawns and random.random() < 0.55:
                chosen_move = random.choice(quiet_pawns)
            elif knights and random.random() < 0.40:
                chosen_move = random.choice(knights)
            else:
                chosen_move = random.choice(legal_moves)

        elif mode == "blunders":
            # Normal play with intentional 25% tactical blunder injection
            if random.random() < 0.25 and ply > 6:
                # Find an undefended hanging piece move
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
        else:
            # General balanced play
            chosen_move = random.choice(legal_moves)

        board.push(chosen_move)
        ply += 1

        # Evaluate position with Stockfish
        fen = board.fen()
        score = sf.evaluate_fen(fen)

        current_node = current_node.add_variation(chosen_move)
        if score is not None:
            current_node.comment = f"[%eval {score:+.2f}]"

    # Set game result
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
    worker_id, stockfish_path, mode, count, depth, seed_openings = args_tuple
    sf = StockfishEvaluator(stockfish_path, depth=depth)
    pgn_strings = []

    try:
        for _ in range(count):
            seed = random.choice(seed_openings)
            game = generate_single_game(sf, seed, mode)
            if game:
                pgn_strings.append(str(game))
    finally:
        sf.close()

    return pgn_strings


def parse_args():
    parser = argparse.ArgumentParser(description="Anak High-Speed Multi-Core Training Position Generator")
    parser.add_argument("--mode", type=str, choices=["open", "closed", "blunders", "general"], default="general",
                        help="Target generation mode (open, closed, blunders, or general)")
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
                        help="Output PGN file path (defaults to training/data/generated_<mode>.pgn)")
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
    out_file = args.output or os.path.join(data_dir, f"generated_{args.mode}.pgn")

    # 4. Target game count
    if args.games:
        total_games = args.games
    else:
        # Average ~40 evaluated plies per game
        total_games = max(5, args.positions // 40)

    games_per_worker = max(1, total_games // workers)
    actual_games = games_per_worker * workers

    print("=" * 70)
    print("=== Anak NNUE Parallel Position & Evaluation Generator ===")
    print("=" * 70)
    print(f"Target Mode:      {args.mode.upper()}")
    print(f"Stockfish:        {sf_path}")
    print(f"Evaluation Depth: {args.depth}")
    print(f"Workers:          {workers} parallel processes (across {total_cpus} CPUs)")
    print(f"Target Games:     ~{actual_games} games (~{actual_games * 40:,} positions)")
    print(f"Output File:      {out_file}")
    print("=" * 70)

    openings = load_seed_openings()
    print(f"Loaded {len(openings)} opening seed positions from opening book.")

    tasks = [
        (i, sf_path, args.mode, games_per_worker, args.depth, openings)
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

    # Append to output PGN file
    with open(out_file, "a", encoding="utf-8") as f:
        for pgn in all_pgn_strings:
            f.write(pgn + "\n\n")

    print("\n" + "=" * 70)
    print(f"SUCCESS: Generated {len(all_pgn_strings)} games in {elapsed:.1f}s ({pos_per_sec:.0f} pos/sec)!")
    print(f"Saved to: {os.path.abspath(out_file)}")
    print("=" * 70)


if __name__ == "__main__":
    main()
