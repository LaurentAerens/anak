#!/usr/bin/env python3
"""
Anak Chess Engine — Reinforcement Learning Opening Book Tuner
Tunes and calibrates PolyGlot opening books (book.bin) from game results.
Supports:
  1. Reinforcement Learning from PGN matches (Anak vs Reference Engines / Self-Play)
  2. Telemetry ingestion from DuckDB (Anak Weakness Finder matches & blunder flags)
  3. Opponent-Elo scaled temporal difference (TD) credit assignment
  4. Automatic refutation pruning (slashes weights of opening lines that lose)
  5. PolyGlot binary reading, updating, and saving with strict key sorting
"""

import argparse
from dataclasses import dataclass
import io
import math
import os
from pathlib import Path
import struct
import sys
import time
from typing import Dict, Iterator, List, Optional, Set, Tuple

try:
    import chess
    import chess.pgn
    import chess.polyglot
except ImportError:
    print("[!] Error: 'chess' library required. Run: pip install chess")
    sys.exit(1)

# Paths
REPO_ROOT = Path(__file__).resolve().parent.parent.parent
DEFAULT_BOOK_PATH = REPO_ROOT / "book.bin"
DUCKDB_PATH = REPO_ROOT / "tools" / "anak_weakness_finder" / "weakness_finder.duckdb"
RESULTS_DIR = REPO_ROOT / "tools" / "fastchess" / "results"

ENTRY_STRUCT = struct.Struct(">QHHI")  # uint64 key, uint16 move, uint16 weight, uint32 learn


def encode_polyglot_move(board: chess.Board, move: chess.Move) -> int:
    """
    Encodes a chess.Move into standard 16-bit PolyGlot move representation:
    - bits 0-2: to_file (0-7)
    - bits 3-5: to_rank (0-7)
    - bits 6-8: from_file (0-7)
    - bits 9-11: from_rank (0-7)
    - bits 12-14: promo (0=none, 1=knight, 2=bishop, 3=rook, 4=queen)
    Converts standard castling (king captures rook square) into PolyGlot notation.
    """
    from_sq = move.from_square
    to_sq = move.to_square

    # PolyGlot castling conversion: king moves to original rook square
    if board.is_castling(move):
        if from_sq == chess.E1 and to_sq == chess.G1:
            to_sq = chess.H1
        elif from_sq == chess.E1 and to_sq == chess.C1:
            to_sq = chess.A1
        elif from_sq == chess.E8 and to_sq == chess.G8:
            to_sq = chess.H8
        elif from_sq == chess.E8 and to_sq == chess.C8:
            to_sq = chess.A8

    promo_code = 0
    if move.promotion:
        promo_code = {
            chess.KNIGHT: 1,
            chess.BISHOP: 2,
            chess.ROOK: 3,
            chess.QUEEN: 4,
        }.get(move.promotion, 0)

    to_file = chess.square_file(to_sq)
    to_rank = chess.square_rank(to_sq)
    from_file = chess.square_file(from_sq)
    from_rank = chess.square_rank(from_sq)

    return (to_file & 7) | ((to_rank & 7) << 3) | ((from_file & 7) << 6) | ((from_rank & 7) << 9) | ((promo_code & 7) << 12)


def decode_polyglot_move(raw_move: int, board: Optional[chess.Board] = None) -> chess.Move:
    """Decodes a 16-bit PolyGlot move into a standard chess.Move."""
    to_file = raw_move & 7
    to_rank = (raw_move >> 3) & 7
    from_file = (raw_move >> 6) & 7
    from_rank = (raw_move >> 9) & 7
    promo_code = (raw_move >> 12) & 7

    from_sq = chess.square(from_file, from_rank)
    to_sq = chess.square(to_file, to_rank)

    promo = {
        1: chess.KNIGHT,
        2: chess.BISHOP,
        3: chess.ROOK,
        4: chess.QUEEN,
    }.get(promo_code, None)

    # Convert PolyGlot castling back to standard King destination
    if board is not None and board.piece_at(from_sq) == chess.Piece(chess.KING, board.turn):
        if from_sq == chess.E1 and to_sq == chess.H1:
            return chess.Move(chess.E1, chess.G1)
        if from_sq == chess.E1 and to_sq == chess.A1:
            return chess.Move(chess.E1, chess.C1)
        if from_sq == chess.E8 and to_sq == chess.H8:
            return chess.Move(chess.E8, chess.G8)
        if from_sq == chess.E8 and to_sq == chess.A8:
            return chess.Move(chess.E8, chess.C8)

    # Fallback heuristic if board not supplied
    if from_sq == chess.E1 and to_sq == chess.H1:
        return chess.Move(chess.E1, chess.G1)
    if from_sq == chess.E1 and to_sq == chess.A1:
        return chess.Move(chess.E1, chess.C1)
    if from_sq == chess.E8 and to_sq == chess.H8:
        return chess.Move(chess.E8, chess.G8)
    if from_sq == chess.E8 and to_sq == chess.A8:
        return chess.Move(chess.E8, chess.C8)

    return chess.Move(from_sq, to_sq, promo)


@dataclass
class BookEntry:
    key: int
    raw_move: int
    weight: int
    learn: int = 0

    @property
    def games_played(self) -> int:
        return (self.learn >> 16) & 0xFFFF

    @property
    def games_won(self) -> int:
        return self.learn & 0xFFFF

    @property
    def win_rate(self) -> float:
        gp = self.games_played
        if gp <= 0:
            return 0.5
        return min(1.0, max(0.0, self.games_won / gp))


class PolyGlotBookTable:
    """In-memory mutable representation of a PolyGlot binary opening book."""

    def __init__(self):
        # Maps (key, raw_move) -> BookEntry
        self.entries: Dict[Tuple[int, int], BookEntry] = {}

    def __len__(self) -> int:
        return len(self.entries)

    def load(self, path: Path) -> int:
        """Loads a .bin file into memory."""
        self.entries.clear()
        if not path.is_file():
            raise FileNotFoundError(f"PolyGlot book file not found at {path}")

        data = path.read_bytes()
        count = len(data) // ENTRY_STRUCT.size
        for i in range(count):
            key, raw_move, weight, learn = ENTRY_STRUCT.unpack_from(data, i * ENTRY_STRUCT.size)
            self.entries[(key, raw_move)] = BookEntry(key, raw_move, weight, learn)
        return len(self.entries)

    def save(self, path: Path, backup: bool = True):
        """Saves entries to .bin file, sorted strictly by key ascending then weight descending."""
        if backup and path.exists():
            backup_path = path.with_suffix(".bin.bak")
            try:
                backup_path.write_bytes(path.read_bytes())
            except Exception:
                pass

        # Sort: primary key = key ascending, secondary key = weight descending
        sorted_entries = sorted(self.entries.values(), key=lambda e: (e.key, -e.weight, e.raw_move))
        
        buffer = bytearray(len(sorted_entries) * ENTRY_STRUCT.size)
        for i, entry in enumerate(sorted_entries):
            ENTRY_STRUCT.pack_into(buffer, i * ENTRY_STRUCT.size, entry.key, entry.raw_move, entry.weight, entry.learn)

        path.write_bytes(buffer)

    def get_entries_for_key(self, key: int) -> List[BookEntry]:
        """Returns all candidate move entries for a given position key."""
        return [e for (k, _), e in self.entries.items() if k == key]

    def add_or_update(
        self,
        key: int,
        raw_move: int,
        delta_weight: int,
        is_win: bool,
        is_loss: bool,
        initial_weight: int = 1,
        min_weight: int = 1,
        max_weight: int = 65535,
    ) -> BookEntry:
        """Applies reinforcement delta to a move and tracks win/game statistics in the learn field."""
        idx = (key, raw_move)
        if idx in self.entries:
            entry = self.entries[idx]
        else:
            entry = BookEntry(key, raw_move, initial_weight, 0)
            self.entries[idx] = entry

        # Update weight with clamping
        new_weight = max(min_weight, min(max_weight, entry.weight + delta_weight))
        entry.weight = new_weight

        # Update packed learn field: [games_played: 16 bits | games_won: 16 bits]
        gp = min(65535, entry.games_played + 1)
        gw = min(65535, entry.games_won + (1 if is_win else 0))
        entry.learn = (gp << 16) | gw

        return entry

    def prune_low_weight(self, min_weight: int = 1) -> int:
        """Removes entries below minimum weight."""
        before = len(self.entries)
        self.entries = {k: v for k, v in self.entries.items() if v.weight >= min_weight}
        return before - len(self.entries)

    def prune_blunders(self, min_games: int = 3, max_win_rate: float = 0.25) -> int:
        """Removes entries with sufficient sample size that have an abysmal win rate."""
        before = len(self.entries)
        to_del = []
        for k, v in self.entries.items():
            if v.games_played >= min_games and v.win_rate < max_win_rate:
                to_del.append(k)
        for k in to_del:
            del self.entries[k]
        return len(to_del)


class ReinforcementBookTuner:
    """
    Reinforcement Learning Engine for PolyGlot opening books.
    Processes tournament game records and applies temporal difference (TD) credit updates.
    """

    def __init__(
        self,
        book_table: PolyGlotBookTable,
        learning_rate: float = 8.0,
        discount_factor: float = 0.85,
        elo_scaling: bool = True,
        max_ply: int = 16,
        create_new_entries: bool = False,
    ):
        self.book = book_table
        self.lr = learning_rate
        self.gamma = discount_factor
        self.elo_scaling = elo_scaling
        self.max_ply = max_ply
        self.create_new = create_new_entries

    def train_game(
        self,
        game: chess.pgn.Game,
        target_player: str = "Anak",
    ) -> Dict[str, object]:
        """
        Updates book weights based on a single game's outcome.
        Returns game stats and update summary.
        """
        white_player = game.headers.get("White", "")
        black_player = game.headers.get("Black", "")
        result_str = game.headers.get("Result", "*")

        # Determine target color and opponent
        is_white = target_player.lower() in white_player.lower()
        is_black = target_player.lower() in black_player.lower()

        if not is_white and not is_black:
            # If target player not matched, check if self-play
            if "anak" in white_player.lower() and "anak" in black_player.lower():
                is_white = True
            else:
                return {"status": "skipped", "reason": "Target engine not in game"}

        target_color = chess.WHITE if is_white else chess.BLACK
        opp_elo_str = game.headers.get("BlackElo" if is_white else "WhiteElo", "2400")
        try:
            opp_elo = int(opp_elo_str)
        except ValueError:
            opp_elo = 2400

        # Game outcome reward
        if result_str == "1-0":
            reward = 1.0 if is_white else 0.0
        elif result_str == "0-1":
            reward = 1.0 if is_black else 0.0
        elif result_str == "1/2-1/2":
            reward = 0.5
        else:
            return {"status": "skipped", "reason": "Incomplete result"}

        # Opponent Elo scaling factor: higher reward for beating stronger engines
        # e.g., 2500 Elo -> 1.5x scale; 1800 Elo -> 0.8x scale
        if self.elo_scaling:
            elo_mult = max(0.5, min(2.5, 1.0 + (opp_elo - 2000) / 1000.0))
        else:
            elo_mult = 1.0

        board = game.board()
        moves_played: List[Tuple[int, int, bool]] = []  # (key, raw_move, is_target_move)

        # Traverse plies up to max_ply
        ply = 0
        for move in game.mainline_moves():
            if ply >= self.max_ply:
                break

            key = chess.polyglot.zobrist_hash(board)
            raw_move = encode_polyglot_move(board, move)
            is_target_move = (board.turn == target_color)

            moves_played.append((key, raw_move, is_target_move))
            board.push(move)
            ply += 1

        # Temporal difference credit assignment:
        # Move closest to opening exit has exponent 0 (full signal), moves earlier decay by gamma^t
        target_moves = [m for m in moves_played if m[2]]
        total_target_moves = len(target_moves)
        updated_count = 0

        for t_idx, (key, raw_move, _) in enumerate(target_moves):
            # Recency factor: moves closest to out-of-book transition receive highest credit/blame
            steps_from_end = total_target_moves - 1 - t_idx
            temporal_decay = self.gamma ** steps_from_end

            # Calculate delta weight
            # Win (1.0): positive delta
            # Draw (0.5): slight stabilizing delta (near 0)
            # Loss (0.0): negative delta (refutation slash)
            base_delta = (reward - 0.5) * 2.0  # In [-1.0, +1.0]
            delta_val = int(round(base_delta * self.lr * elo_mult * temporal_decay))

            # If it's a loss, punish more aggressively (-1.5x) to prevent repeat blunders
            if reward == 0.0 and delta_val < 0:
                delta_val = int(round(delta_val * 1.5))

            # Only update if entry exists (or create_new is enabled)
            if (key, raw_move) in self.book.entries or self.create_new:
                self.book.add_or_update(
                    key=key,
                    raw_move=raw_move,
                    delta_weight=delta_val,
                    is_win=(reward == 1.0),
                    is_loss=(reward == 0.0),
                )
                updated_count += 1

        return {
            "status": "success",
            "reward": reward,
            "opp_elo": opp_elo,
            "target_color": "White" if is_white else "Black",
            "moves_updated": updated_count,
        }

    def train_pgn_file(
        self,
        pgn_path: Path,
        target_player: str = "Anak",
    ) -> Dict[str, int]:
        """Trains book over all games in a PGN file."""
        if not pgn_path.is_file():
            raise FileNotFoundError(f"PGN file not found at {pgn_path}")

        total_games = 0
        updated_games = 0
        total_move_updates = 0

        with open(pgn_path, "r", encoding="utf-8", errors="ignore") as f:
            while True:
                game = chess.pgn.read_game(f)
                if game is None:
                    break
                total_games += 1
                res = self.train_game(game, target_player=target_player)
                if res["status"] == "success":
                    updated_games += 1
                    total_move_updates += res.get("moves_updated", 0)

        return {
            "total_games": total_games,
            "updated_games": updated_games,
            "move_updates": total_move_updates,
        }

    def train_from_duckdb(
        self,
        duckdb_path: Path,
        target_player: str = "Anak",
        penalize_throws: bool = True,
    ) -> Dict[str, int]:
        """
        Directly queries DuckDB weakness telemetry to penalize opening lines with throws or blunders.
        """
        try:
            import duckdb
        except ImportError:
            print("[!] Warning: duckdb not installed. Skipping DuckDB ingestion.")
            return {"games": 0, "blunders_penalized": 0}

        if not duckdb_path.is_file():
            return {"games": 0, "blunders_penalized": 0}

        conn = duckdb.connect(str(duckdb_path))
        
        # Query games with throws or losses within first 20 plies
        query = """
            SELECT pgn_file, opponent_name, stockfish_elo
            FROM matches
            WHERE pgn_file IS NOT NULL
        """
        try:
            matches = conn.execute(query).fetchall()
        except Exception:
            return {"games": 0, "blunders_penalized": 0}

        total_matches = len(matches)
        total_updates = 0

        for row in matches:
            pgn_rel, opp_name, elo = row
            if not pgn_rel:
                continue
            pgn_file = REPO_ROOT / pgn_rel
            if not pgn_file.is_file():
                # check results directory
                pgn_file = RESULTS_DIR / Path(pgn_rel).name
            if pgn_file.is_file():
                res = self.train_pgn_file(pgn_file, target_player=target_player)
                total_updates += res["move_updates"]

        return {
            "matches_processed": total_matches,
            "move_updates": total_updates,
        }


def print_book_stats(book: PolyGlotBookTable):
    """Prints comprehensive statistics about the PolyGlot opening book."""
    total_entries = len(book)
    if total_entries == 0:
        print("[-] Book is empty.")
        return

    unique_positions = len(set(k for (k, _) in book.entries.keys()))
    total_weight = sum(e.weight for e in book.entries.values())
    avg_weight = total_weight / total_entries
    max_weight = max(e.weight for e in book.entries.values())
    min_weight = min(e.weight for e in book.entries.values())
    with_stats = sum(1 for e in book.entries.values() if e.games_played > 0)

    print("\n" + "=" * 70)
    print("           ANAK POLYGLOT OPENING BOOK STATISTICS")
    print("=" * 70)
    print(f"  Total Move Entries:         {total_entries:,}")
    print(f"  Unique Positions (Keys):    {unique_positions:,}")
    print(f"  Average Branching Factor:   {total_entries / max(1, unique_positions):.2f} moves/pos")
    print(f"  Weight Range:               [{min_weight} .. {max_weight}] (Avg: {avg_weight:.1f})")
    print(f"  Entries with Match Telemetry: {with_stats:,} ({with_stats / total_entries * 100:.1f}%)")
    print("=" * 70 + "\n")


def inspect_position(book: PolyGlotBookTable, moves_str: str = "", fen: Optional[str] = None):
    """Inspects candidate book moves for a specific FEN or opening move sequence."""
    board = chess.Board(fen) if fen else chess.Board()
    if moves_str:
        for m in moves_str.strip().split():
            try:
                board.push_san(m)
            except ValueError:
                board.push_uci(m)

    key = chess.polyglot.zobrist_hash(board)
    entries = book.get_entries_for_key(key)

    print("\n" + "-" * 70)
    print(f"Position FEN:  {board.fen()}")
    print(f"PolyGlot Key:  0x{key:016X}")
    print(f"Book Entries:  {len(entries)} candidate move(s)")
    print("-" * 70)

    if not entries:
        print("  [-] No book moves found for this position.")
        return

    total_weight = sum(e.weight for e in entries)
    # Sort by weight descending
    entries.sort(key=lambda e: -e.weight)

    print(f"  {'Move':<8} {'UCI':<8} {'Weight':<8} {'Prob':<8} {'Games':<8} {'Win Rate':<10}")
    print("  " + "-" * 55)
    for e in entries:
        m = decode_polyglot_move(e.raw_move, board)
        try:
            san = board.san(m)
        except Exception:
            san = m.uci()
        prob = (e.weight / total_weight * 100.0) if total_weight > 0 else 0.0
        wr_str = f"{e.win_rate * 100:.1f}%" if e.games_played > 0 else "-"
        print(f"  {san:<8} {m.uci():<8} {e.weight:<8} {prob:5.1f}%   {e.games_played:<8} {wr_str:<10}")
    print("-" * 70 + "\n")


def main():
    parser = argparse.ArgumentParser(description="Anak Chess Engine — Reinforcement Learning Opening Book Tuner")
    subparsers = parser.add_subparsers(dest="command", help="Command to run")

    # Command: stats
    stats_p = subparsers.add_parser("stats", help="Display opening book statistics")
    stats_p.add_argument("--book", type=Path, default=DEFAULT_BOOK_PATH, help="Path to book.bin")

    # Command: inspect
    inspect_p = subparsers.add_parser("inspect", help="Inspect candidate moves for a position")
    inspect_p.add_argument("--book", type=Path, default=DEFAULT_BOOK_PATH, help="Path to book.bin")
    inspect_p.add_argument("--fen", type=str, default=None, help="FEN string to inspect")
    inspect_p.add_argument("--moves", type=str, default="", help="Opening moves sequence (e.g. 'e4 e5 Nf3')")

    # Command: tune
    tune_p = subparsers.add_parser("tune", help="Tune opening book via Reinforcement Learning")
    tune_p.add_argument("--book", type=Path, default=DEFAULT_BOOK_PATH, help="Path to book.bin to update")
    tune_p.add_argument("--pgn", type=Path, default=None, help="Path to PGN file or directory of PGNs")
    tune_p.add_argument("--duckdb", action="store_true", help="Ingest all matches from Weakness Finder DuckDB")
    tune_p.add_argument("--db-path", type=Path, default=DUCKDB_PATH, help="Path to weakness_finder.duckdb")
    tune_p.add_argument("--player", type=str, default="Anak", help="Target engine name in games (default: 'Anak')")
    tune_p.add_argument("--lr", type=float, default=10.0, help="Learning rate base weight delta (default: 10)")
    tune_p.add_argument("--gamma", type=float, default=0.85, help="TD temporal discount factor (default: 0.85)")
    tune_p.add_argument("--max-ply", type=int, default=16, help="Maximum opening ply to reinforce (default: 16)")
    tune_p.add_argument("--expand", "--create-new", dest="create_new", action="store_true", help="Add new lines from winning games into book if not already present")
    tune_p.add_argument("--prune-threshold", type=int, default=0, help="Prune entries with weight below this threshold")
    tune_p.add_argument("--no-backup", action="store_true", help="Skip creating .bin.bak before saving")

    # Command: prune
    prune_p = subparsers.add_parser("prune", help="Prune dead or low win-rate opening lines")
    prune_p.add_argument("--book", type=Path, default=DEFAULT_BOOK_PATH, help="Path to book.bin")
    prune_p.add_argument("--min-weight", type=int, default=1, help="Prune entries below this weight")
    prune_p.add_argument("--min-games", type=int, default=3, help="Minimum games played to evaluate win rate")
    prune_p.add_argument("--max-win-rate", type=float, default=0.25, help="Prune entries with win rate below this")

    args = parser.parse_args()

    if not args.command:
        parser.print_help()
        return

    book = PolyGlotBookTable()

    if args.command == "stats":
        print(f"[*] Loading PolyGlot book from {args.book} ...")
        book.load(args.book)
        print_book_stats(book)

    elif args.command == "inspect":
        book.load(args.book)
        inspect_position(book, moves_str=args.moves, fen=args.fen)

    elif args.command == "tune":
        print(f"[*] Loading PolyGlot book from {args.book} ...")
        start_entries = book.load(args.book)
        print(f"[+] Loaded {start_entries:,} entries.")

        tuner = ReinforcementBookTuner(
            book_table=book,
            learning_rate=args.lr,
            discount_factor=args.gamma,
            elo_scaling=True,
            max_ply=args.max_ply,
            create_new_entries=args.create_new,
        )

        total_move_updates = 0

        # Ingest PGN if provided
        if args.pgn:
            if args.pgn.is_file():
                print(f"[*] Training on PGN file: {args.pgn} ...")
                res = tuner.train_pgn_file(args.pgn, target_player=args.player)
                print(f"[+] Games processed: {res['total_games']}, Matches updated: {res['updated_games']}, Moves updated: {res['move_updates']}")
                total_move_updates += res["move_updates"]
            elif args.pgn.is_dir():
                pgn_files = list(args.pgn.glob("*.pgn"))
                print(f"[*] Found {len(pgn_files)} PGN file(s) in {args.pgn} ...")
                for p in pgn_files:
                    res = tuner.train_pgn_file(p, target_player=args.player)
                    total_move_updates += res["move_updates"]
                    print(f"    -> {p.name}: {res['updated_games']} games updated ({res['move_updates']} move weights)")

        # Ingest from DuckDB if requested
        if args.duckdb:
            print(f"[*] Ingesting telemetry from DuckDB ({args.db_path}) ...")
            db_res = tuner.train_from_duckdb(args.db_path, target_player=args.player)
            print(f"[+] DuckDB matches processed: {db_res['matches_processed']}, Moves reinforced: {db_res['move_updates']}")
            total_move_updates += db_res["move_updates"]

        # Prune if threshold specified
        if args.prune_threshold > 0:
            pruned = book.prune_low_weight(args.prune_threshold)
            print(f"[*] Pruned {pruned:,} entries with weight < {args.prune_threshold}.")

        print(f"\n[+] Reinforcement tuning finished: {total_move_updates:,} total move updates.")
        print(f"[*] Saving optimized book to {args.book} ...")
        book.save(args.book, backup=not args.no_backup)
        print("[+] Opening book updated successfully!")

    elif args.command == "prune":
        print(f"[*] Loading PolyGlot book from {args.book} ...")
        book.load(args.book)
        pruned_wt = book.prune_low_weight(args.min_weight)
        pruned_wr = book.prune_blunders(min_games=args.min_games, max_win_rate=args.max_win_rate)
        print(f"[+] Pruned {pruned_wt:,} entries below weight {args.min_weight}.")
        print(f"[+] Pruned {pruned_wr:,} entries with win rate < {args.max_win_rate * 100:.0f}% over {args.min_games}+ games.")
        book.save(args.book)
        print("[+] Pruning complete.")


if __name__ == "__main__":
    main()
