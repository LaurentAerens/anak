"""
Full-Power Stockfish Oracle Analyzer for Anak Weakness Finder.
Runs uncapped, full-strength Stockfish at depth 9 (for NNUE training data)
and depth 20-26 (for deep data analysis) on misidentified FEN positions.
Stores training-ready evaluations in the weakness_training_data table.
Supports multi-threaded concurrent analysis over strictly unique FEN positions.
"""

import argparse
import concurrent.futures
import os
import queue
import re
import subprocess
import sys
import time
from pathlib import Path
from typing import Optional, Dict, Any, List
import chess

if __package__ is None or __package__ == "":
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))
    from tools.anak_weakness_finder.db_schema import get_connection, initialize_schema
else:
    from .db_schema import get_connection, initialize_schema

REPO_ROOT = Path(__file__).resolve().parent.parent.parent
STOCKFISH_EXE = REPO_ROOT / "tools" / "fastchess" / "bin" / ("stockfish.exe" if os.name == "nt" else "stockfish")


class StockfishOracle:
    def __init__(self, sf_path: Optional[Path] = None, threads: int = 1, hash_mb: int = 32):
        self.sf_path = sf_path or STOCKFISH_EXE
        if not self.sf_path.exists():
            raise FileNotFoundError(f"Stockfish executable not found at {self.sf_path}")
        
        self.process = subprocess.Popen(
            [str(self.sf_path)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1
        )
        self._send_cmd("uci")
        self._send_cmd(f"setoption name Threads value {threads}")
        self._send_cmd(f"setoption name Hash value {hash_mb}")
        # Strength limit disabled for true evaluations
        self._send_cmd("setoption name UCI_LimitStrength value false")
        self._send_cmd("isready")
        self._wait_for("readyok")

    def _send_cmd(self, cmd: str):
        if self.process.stdin:
            self.process.stdin.write(cmd + "\n")
            self.process.stdin.flush()

    def _wait_for(self, target: str) -> List[str]:
        lines = []
        while True:
            line = self.process.stdout.readline()
            if not line:
                break
            line_str = line.strip()
            lines.append(line_str)
            if target in line_str:
                break
        return lines

    def evaluate_fen(self, fen: str, depth: int = 9) -> Dict[str, Any]:
        """Runs search on FEN at specified depth and returns (eval_pawns, best_move, pv)."""
        self._send_cmd(f"position fen {fen}")
        self._send_cmd(f"go depth {depth}")

        best_move = None
        eval_score = 0.0
        pv_line = ""

        re_score = re.compile(r"score (cp|mate) (-?\d+)")
        re_pv = re.compile(r" pv (.+)")

        while True:
            line = self.process.stdout.readline()
            if not line:
                break
            line_str = line.strip()

            if line_str.startswith("info ") and f"depth {depth}" in line_str:
                m_score = re_score.search(line_str)
                if m_score:
                    score_type = m_score.group(1)
                    val = int(m_score.group(2))
                    if score_type == "cp":
                        eval_score = val / 100.0
                    else:
                        eval_score = 100.0 - abs(val) if val > 0 else -100.0 + abs(val)

                m_pv = re_pv.search(line_str)
                if m_pv:
                    pv_line = m_pv.group(1)

            if line_str.startswith("bestmove"):
                parts = line_str.split()
                if len(parts) >= 2:
                    best_move = parts[1]
                break

        return {
            "fen": fen,
            "depth": depth,
            "eval": eval_score,
            "best_move": best_move,
            "pv": pv_line
        }

    def close(self):
        try:
            self._send_cmd("quit")
            self.process.terminate()
        except Exception:
            pass


class StockfishOraclePool:
    """Pool of Stockfish worker processes for high-throughput concurrent evaluations."""
    def __init__(self, size: int = 8, sf_path: Optional[Path] = None, threads: int = 1, hash_mb: int = 32):
        self.size = max(1, size)
        self.sf_path = sf_path or STOCKFISH_EXE
        self.pool: queue.Queue = queue.Queue()
        self.oracles: List[StockfishOracle] = []
        for _ in range(self.size):
            oracle = StockfishOracle(sf_path=self.sf_path, threads=threads, hash_mb=hash_mb)
            self.oracles.append(oracle)
            self.pool.put(oracle)

    def evaluate_both_depths(self, fen: str, training_depth: int = 9, analysis_depth: int = 20):
        oracle = self.pool.get()
        try:
            t0 = time.time()
            sf9 = oracle.evaluate_fen(fen, depth=training_depth)
            sf20 = oracle.evaluate_fen(fen, depth=analysis_depth)
            dt = time.time() - t0
            return sf9, sf20, dt
        finally:
            self.pool.put(oracle)

    def close(self):
        for oracle in self.oracles:
            oracle.close()


def run_oracle_audit(
    analysis_depth: int = 20,
    training_depth: int = 9,
    delta_threshold: float = 2.0,
    only_throws: bool = False,
    limit: Optional[int] = None,
    concurrency: int = 16,
    db_path: Optional[Path] = None
) -> int:
    """
    Finds every position where Anak went significantly wrong in lost games.
    Evaluates at training_depth (default: 9) for NNUE training data,
    and analysis_depth (default: 20) for deep diagnostics using concurrent threads.
    Guarantees 100% strictly unique positions (no FEN analyzed twice).
    No hardcoded limits: every unique position reaching the delta threshold is evaluated.
    """
    conn = get_connection(db_path)
    initialize_schema(conn)

    cond = "p.is_throw = TRUE" if only_throws else f"(p.is_throw = TRUE OR p.eval_delta <= -abs({delta_threshold}))"
    limit_clause = f"LIMIT {limit}" if (limit is not None and limit > 0) else ""

    query = f"""
        SELECT 
            p.fen_before,
            arg_max(p.game_id, p.is_throw) as game_id,
            arg_max(p.ply_number, p.is_throw) as ply_number,
            arg_max(p.move_uci, p.is_throw) as move_uci,
            arg_max(coalesce(p.anak_eval, 0.0), p.is_throw) as anak_eval,
            arg_max(p.side_to_move, p.is_throw) as side_to_move,
            arg_max(p.active_nnue_expert, p.is_throw) as active_nnue_expert,
            arg_max(g.loss_archetype, p.is_throw) as loss_archetype,
            max(p.eval_disagreement) as eval_disagreement,
            bool_or(p.is_throw) as is_throw,
            min(coalesce(p.eval_delta, 0.0)) as eval_delta
        FROM plies p
        JOIN games g ON p.game_id = g.game_id
        LEFT JOIN weakness_training_data w ON p.fen_before = w.fen
        WHERE w.fen IS NULL 
          AND g.result = 'loss'
          AND p.is_anak_move = TRUE
          AND {cond}
        GROUP BY p.fen_before
        ORDER BY bool_or(p.is_throw) DESC, abs(min(coalesce(p.eval_delta, 0.0))) DESC
        {limit_clause};
    """

    rows = conn.execute(query).fetchall()

    if not rows:
        print("[*] No unanalyzed positions found reaching the error threshold.")
        return 0

    actual_workers = max(1, min(concurrency, len(rows)))
    mode_str = "Thrown Wins Only (eval >= +1.0 -> loss)" if only_throws else f"Severe Failure (Throw OR Eval Drop >= {abs(delta_threshold):.1f} pawns)"
    print("=" * 78)
    print(f"[*] RUNNING MULTI-THREADED STOCKFISH ORACLE ON ALL THRESHOLD POSITIONS")
    print(f"    Filter Mode: {mode_str}")
    print(f"    Total Unique Qualifying Positions: {len(rows):,} (No hardcoded limit)")
    print(f"    Workers: {actual_workers} concurrent threads | ST{training_depth} Training / ST{analysis_depth} Diagnostics")
    print("=" * 78)

    pool = StockfishOraclePool(size=actual_workers)
    evaluated = 0

    try:
        with concurrent.futures.ThreadPoolExecutor(max_workers=actual_workers) as executor:
            future_to_meta = {
                executor.submit(pool.evaluate_both_depths, r[0], training_depth, analysis_depth): r
                for r in rows
            }
            for fut in concurrent.futures.as_completed(future_to_meta):
                r = future_to_meta[fut]
                fen, game_id, ply_num, played_move, anak_eval, side, expert, archetype, disagreement, is_throw, eval_delta = r
                sf9, sf20, dt = fut.result()

                is_white = (side.lower() == "white")
                sf9_eval_white = sf9["eval"] if is_white else -sf9["eval"]
                sf9_cp = int(round(sf9_eval_white * 100))
                sf20_eval_white = sf20["eval"] if is_white else -sf20["eval"]

                eval_error = round((anak_eval or 0.0) - sf9["eval"], 2)
                is_objective_blunder = (played_move != sf20["best_move"] and abs(eval_error) >= 1.5)
                mis_type = "throw" if eval_error <= -2.0 else ("disagreement" if disagreement >= 1.5 else archetype)

                conn.execute("""
                    INSERT OR REPLACE INTO weakness_training_data (
                        fen, game_id, ply_number, active_nnue_expert, anak_eval,
                        sf9_eval_pawns, sf9_eval_cp, sf9_best_move, sf20_eval_pawns,
                        eval_error, misidentification_type
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """, (
                    fen, game_id, ply_num, expert, anak_eval or 0.0,
                    sf9_eval_white, sf9_cp, sf9["best_move"] or "", sf20_eval_white,
                    eval_error, mis_type
                ))

                conn.execute("""
                    INSERT OR REPLACE INTO oracle_evaluations (
                        fen, game_id, ply_number, played_move, anak_eval,
                        sf_depth, sf_eval, sf_best_move, eval_error, is_objective_blunder
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """, (
                    fen, game_id, ply_num, played_move or "", anak_eval or 0.0,
                    analysis_depth, sf20["eval"], sf20["best_move"] or "", eval_error, is_objective_blunder
                ))

                evaluated += 1
                status = "[BLUNDER]" if is_objective_blunder else "[OK]"
                print(f"[{evaluated}/{len(rows)}] {status} ({dt:.2f}s) ST9: {sf9['eval']:+.2f} ({sf9_cp} cp) | ST20: {sf20['eval']:+.2f} (Error: {eval_error:+.2f}) | Played: {played_move} vs Best: {sf20['best_move']}")

    finally:
        pool.close()

    print(f"\n[+] Evaluated & stored {evaluated:,} unique training positions in weakness_training_data.")
    return evaluated


def main():
    parser = argparse.ArgumentParser(description="Full-Power Stockfish Oracle Deep Evaluator & Training Data Generator")
    parser.add_argument("--analysis-depth", type=int, default=20, help="Deep search depth for analysis (default: 20)")
    parser.add_argument("--training-depth", type=int, default=9, help="Stockfish evaluation depth for training data (default: 9)")
    parser.add_argument("--delta-threshold", type=float, default=2.0, help="Eval drop threshold in pawns where engine went very wrong (default: 2.0)")
    parser.add_argument("--only-throws", action="store_true", default=False, help="Only evaluate blown leads (eval >= +1.0 to loss)")
    parser.add_argument("--limit", type=int, default=None, help="Optional max positions cap (default: None = unlimited, every position reaching threshold)")
    parser.add_argument("--concurrency", type=int, default=16, help="Concurrency for Oracle analysis (default: 16)")

    args = parser.parse_args()
    run_oracle_audit(
        analysis_depth=args.analysis_depth,
        training_depth=args.training_depth,
        delta_threshold=args.delta_threshold,
        only_throws=args.only_throws,
        limit=args.limit,
        concurrency=args.concurrency
    )


if __name__ == "__main__":
    main()

