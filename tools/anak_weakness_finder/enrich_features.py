"""
Big-Data Feature Generation & Enrichment Engine for Anak Weakness Finder.
Transforms raw match games and plies into enriched analytical tables:
- Maps opening FENs to canonical Opening Families, ECO codes, and Pawn Structures.
- Classifies Loss Phases (Opening, Middlegame, Endgame).
- Computes NNUE Expert Attributions and Transitions.
- Runs targeted Stockfish Oracle evaluations (ST9 for training, ST20 for diagnostics)
  on critical positions from lost games to establish ground truth.
"""

import argparse
import concurrent.futures
import time
from pathlib import Path
from typing import Optional, List, Tuple
import chess
import duckdb
import pyarrow as pa

from .db_schema import get_connection, initialize_schema
from .opening_classifier import classify_opening
from .oracle_analyzer import StockfishOracle, StockfishOraclePool


def classify_endgame_piece_state(fen: str) -> str:
    """Classifies a board FEN into an endgame material archetype."""
    try:
        board_str = fen.split(" ")[0]
        w_q, b_q = board_str.count("Q"), board_str.count("q")
        w_r, b_r = board_str.count("R"), board_str.count("r")
        w_b, b_b = board_str.count("B"), board_str.count("b")
        w_n, b_n = board_str.count("N"), board_str.count("n")

        tot_q = w_q + b_q
        tot_r = w_r + b_r
        tot_b = w_b + b_b
        tot_n = w_n + b_n
        tot_minors = tot_b + tot_n

        if tot_q == 0 and tot_r == 0 and tot_minors == 0:
            return "Pure King & Pawn"
        if tot_q == 0 and tot_minors == 0 and tot_r > 0:
            return "Rook & Pawn"
        if tot_r == 0 and tot_minors == 0 and tot_q > 0:
            return "Queen & Pawn"
        if tot_q == 0 and tot_r == 0 and tot_minors > 0:
            if tot_b == 2 and w_b == 1 and b_b == 1 and tot_n == 0:
                board = chess.Board(fen)
                w_sq = list(board.pieces(chess.BISHOP, chess.WHITE))[0]
                b_sq = list(board.pieces(chess.BISHOP, chess.BLACK))[0]
                if ((chess.square_rank(w_sq) + chess.square_file(w_sq)) % 2) != (
                    (chess.square_rank(b_sq) + chess.square_file(b_sq)) % 2
                ):
                    return "Opposite-Colored Bishops"
                return "Same-Colored Bishops"
            if tot_b > 0 and tot_n == 0:
                return "Bishop Endgame"
            if tot_n > 0 and tot_b == 0:
                return "Knight Endgame"
            if (w_b > 0 and b_n > 0 and w_n == 0 and b_b == 0) or (
                b_b > 0 and w_n > 0 and b_n == 0 and w_b == 0
            ):
                return "Bishop vs Knight"
            return "Mixed Minor Pieces"
        if tot_q == 0 and tot_r > 0 and tot_minors > 0:
            return "Rook & Minor Pieces"
        if tot_q > 0 and (tot_r > 0 or tot_minors > 0):
            return "Queen & Pieces"
        return "Other Endgame"
    except Exception:
        return "Unclassified"


def classify_blunder_motif(fen: str, move_uci: str, side: str) -> str:
    """Classifies a tactical blunder move into a failure motif."""
    try:
        board = chess.Board(fen)
        move = chess.Move.from_uci(move_uci)
        if not board.is_legal(move):
            return "Positional / Tactical Slip"
        piece = board.piece_at(move.from_square)
        in_check = board.is_check()
        board.push(move)
        if board.is_checkmate():
            return "Checkmate Incurred"
        dest_sq = move.to_square
        piece_val = {
            chess.PAWN: 1,
            chess.KNIGHT: 3,
            chess.BISHOP: 3,
            chess.ROOK: 5,
            chess.QUEEN: 9,
            chess.KING: 0,
        }.get(piece.piece_type if piece else 0, 0)
        attackers = board.attackers(board.turn, dest_sq)
        defenders = board.attackers(not board.turn, dest_sq)
        if attackers:
            min_attacker_val = min(
                {
                    chess.PAWN: 1,
                    chess.KNIGHT: 3,
                    chess.BISHOP: 3,
                    chess.ROOK: 5,
                    chess.QUEEN: 9,
                    chess.KING: 100,
                }.get(board.piece_at(sq).piece_type, 100)
                for sq in attackers
                if board.piece_at(sq)
            )
            if not defenders and piece_val >= 3:
                return "Hanging Piece Drop"
            if piece_val > min_attacker_val and (piece_val - min_attacker_val >= 2):
                return "Bad Material Exchange"
        if board.is_check():
            return "King Safety / Check Danger"
        if in_check:
            return "Blunder Under Check"
        if piece and piece.piece_type == chess.PAWN:
            return "Flawed Pawn Advance"
        if piece and piece.piece_type in (chess.QUEEN, chess.ROOK):
            return "Heavy Piece Misplacement"
        return "Positional / Tactical Slip"
    except Exception:
        return "Positional / Tactical Slip"


def enrich_database_features(
    db_path: Optional[Path] = None,
    run_oracle: bool = True,
    oracle_limit: Optional[int] = None,
    oracle_delta_threshold: float = 2.0,
    oracle_only_throws: bool = False,
    oracle_training_depth: int = 9,
    oracle_analysis_depth: int = 20,
    oracle_concurrency: int = 16
):
    """
    Executes the complete data enrichment pipeline on the DuckDB database.
    Writes all computed features directly into database columns and tables.
    """
    conn = get_connection(db_path)
    initialize_schema(conn)

    print("=" * 75)
    print("      ANAK WEAKNESS FINDER — DATA ENRICHMENT & FEATURE PIPELINE")
    print("=" * 75)

    # 1. Update any matches with total_games if missing
    pass

    # 2. Add required enriched columns to games if not present
    games_cols = {c[1] for c in conn.execute("PRAGMA table_info(games)").fetchall()}
    if "pawn_structure" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN pawn_structure VARCHAR DEFAULT 'Unclassified';")
    if "opening_family" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN opening_family VARCHAR DEFAULT 'Unclassified';")
    if "loss_phase" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN loss_phase VARCHAR DEFAULT 'none';")
    if "middlegame_expert" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN middlegame_expert VARCHAR DEFAULT 'Unknown';")
    if "terminal_expert" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN terminal_expert VARCHAR DEFAULT 'Unknown';")
    if "blundering_expert" not in games_cols:
        conn.execute("ALTER TABLE games ADD COLUMN blundering_expert VARCHAR DEFAULT 'None';")

    # 3. Classify all unique opening FENs
    print("[*] Step 1/4: Classifying Opening Families, ECO codes & Pawn Structures...")
    t0 = time.time()
    distinct_fens = [r[0] for r in conn.execute("SELECT DISTINCT opening_fen FROM games").fetchall() if r[0]]
    
    fen_data = {
        "fen": [], "eco": [], "name": [], "family": [], "structure": []
    }
    for fen in distinct_fens:
        eco, name, struct = classify_opening(fen)
        family = name.split(":")[0].strip()
        fen_data["fen"].append(fen)
        fen_data["eco"].append(eco)
        fen_data["name"].append(name)
        fen_data["family"].append(family)
        fen_data["structure"].append(struct)

    # Bulk update games via temporary table join
    tbl_openings = pa.Table.from_pydict(fen_data)
    conn.execute("CREATE OR REPLACE TEMP TABLE tmp_opening_classifications AS SELECT * FROM tbl_openings;")
    conn.execute("""
        UPDATE games
        SET opening_eco = c.eco,
            opening_name = c.name,
            opening_family = c.family,
            pawn_structure = c.structure
        FROM tmp_opening_classifications c
        WHERE games.opening_fen = c.fen;
    """)
    print(f"    Classified {len(distinct_fens):,} unique openings in {time.time() - t0:.2f}s.")

    # 4. Classify Loss Phases
    print("[*] Step 2/4: Classifying Loss Phases (Opening vs. Middlegame vs. Endgame)...")
    conn.execute("""
        UPDATE games
        SET loss_phase = CASE 
            WHEN ply_count <= 30 THEN 'Early Knockout (Ply <= 30)'
            WHEN ply_count <= 60 THEN 'Middlegame Collapse (Ply 31-60)'
            ELSE 'Endgame Attrition (Ply > 60)'
        END
        WHERE result = 'loss';
    """)

    # 5. Determine active NNUE experts per game (Middlegame vs Terminal)
    print("[*] Step 3/4: Computing NNUE Expert Attributions & Game Transitions...")
    # Terminal expert (last ply of each game)
    conn.execute("""
        CREATE OR REPLACE TEMP TABLE tmp_terminal_experts AS
        WITH ranked_plies AS (
            SELECT game_id, active_nnue_expert,
                   row_number() OVER (PARTITION BY game_id ORDER BY ply_number DESC) as rn
            FROM plies
        )
        SELECT game_id, active_nnue_expert as terminal_expert
        FROM ranked_plies
        WHERE rn = 1;
    """)

    # Middlegame expert (most frequent expert between ply 18 and 36)
    conn.execute("""
        CREATE OR REPLACE TEMP TABLE tmp_mid_experts AS
        WITH mid_plies AS (
            SELECT game_id, active_nnue_expert, count(*) as cnt,
                   row_number() OVER (PARTITION BY game_id ORDER BY count(*) DESC) as rn
            FROM plies
            WHERE ply_number BETWEEN 18 AND 36
            GROUP BY game_id, active_nnue_expert
        )
        SELECT game_id, active_nnue_expert as middlegame_expert
        FROM mid_plies
        WHERE rn = 1;
    """)

    # Blundering expert (expert active on the single worst eval drop in the game)
    conn.execute("""
        CREATE OR REPLACE TEMP TABLE tmp_blund_experts AS
        WITH ranked_drops AS (
            SELECT game_id, active_nnue_expert,
                   row_number() OVER (PARTITION BY game_id ORDER BY eval_delta ASC) as rn
            FROM plies
            WHERE is_anak_move = TRUE AND eval_delta < -1.0
        )
        SELECT game_id, active_nnue_expert as blundering_expert
        FROM ranked_drops
        WHERE rn = 1;
    """)

    conn.execute("""
        UPDATE games
        SET terminal_expert = t.terminal_expert
        FROM tmp_terminal_experts t
        WHERE games.game_id = t.game_id;
    """)

    conn.execute("""
        UPDATE games
        SET middlegame_expert = m.middlegame_expert
        FROM tmp_mid_experts m
        WHERE games.game_id = m.game_id;
    """)

    conn.execute("""
        UPDATE games
        SET blundering_expert = b.blundering_expert
        FROM tmp_blund_experts b
        WHERE games.game_id = b.game_id;
    """)

    # 6. Classify Endgame Material Archetypes
    print("[*] Step 4/8: Classifying Endgame Material Archetypes...")
    t0 = time.time()
    terminal_rows = conn.execute("""
        WITH last_plies AS (
            SELECT game_id, fen_before, row_number() OVER (PARTITION BY game_id ORDER BY ply_number DESC) as rn 
            FROM plies
        ) 
        SELECT l.game_id, l.fen_before 
        FROM last_plies l 
        WHERE rn = 1;
    """).fetchall()

    eg_data = {"game_id": [], "endgame_archetype": []}
    for g_id, fen in terminal_rows:
        eg_data["game_id"].append(g_id)
        eg_data["endgame_archetype"].append(classify_endgame_piece_state(fen))

    tbl_eg = pa.Table.from_pydict(eg_data)
    conn.execute("CREATE OR REPLACE TEMP TABLE tmp_endgame_classifications AS SELECT * FROM tbl_eg;")
    conn.execute("""
        UPDATE games
        SET endgame_archetype = e.endgame_archetype
        FROM tmp_endgame_classifications e
        WHERE games.game_id = e.game_id;
    """)
    print(f"    Classified {len(terminal_rows):,} game endgame archetypes in {time.time() - t0:.2f}s.")

    # 7. Classify Tactical Blunder Motifs
    print("[*] Step 5/8: Classifying Tactical Blunder Motifs across Failure Plies...")
    t0 = time.time()
    blunder_rows = conn.execute("""
        SELECT ply_id, fen_before, move_uci, side_to_move 
        FROM plies 
        WHERE is_blunder = TRUE AND is_anak_move = TRUE;
    """).fetchall()

    bm_data = {"ply_id": [], "blunder_motif": []}
    for ply_id, fen, move_uci, side in blunder_rows:
        bm_data["ply_id"].append(ply_id)
        bm_data["blunder_motif"].append(classify_blunder_motif(fen, move_uci, side))

    tbl_bm = pa.Table.from_pydict(bm_data)
    conn.execute("CREATE OR REPLACE TEMP TABLE tmp_blunder_motifs AS SELECT * FROM tbl_bm;")
    conn.execute("""
        UPDATE plies
        SET blunder_motif = b.blunder_motif
        FROM tmp_blunder_motifs b
        WHERE plies.ply_id = b.ply_id;
    """)
    print(f"    Classified {len(blunder_rows):,} blunders into tactical motifs in {time.time() - t0:.2f}s.")

    # 8. Compute Lead Bleed Dynamics for Thrown Leads
    print("[*] Step 6/8: Computing Lead Bleed Dynamics for Blown Leads...")
    conn.execute("""
        UPDATE games
        SET lead_bleed_type = CASE 
            WHEN worst_eval_drop <= -2.5 THEN 'Catastrophic Blunder (Drop >= 2.5 cp)'
            WHEN worst_eval_drop <= -1.5 THEN 'Significant Tactical Slip (Drop 1.5 - 2.5 cp)'
            ELSE 'Gradual Positional Erosion'
        END
        WHERE loss_archetype = 'thrown_win';
    """)

    # 9. Compute NNUE Expert Transition Boundaries & Jumps
    print("[*] Step 7/8: Computing NNUE Expert Transition Boundaries & Jumps...")
    t0 = time.time()
    conn.execute("""
        CREATE OR REPLACE TEMP TABLE tmp_expert_transitions AS
        WITH ranked_transitions AS (
            SELECT 
                ply_id,
                lag(active_nnue_expert) OVER (PARTITION BY game_id ORDER BY ply_number ASC) as prev_expert,
                active_nnue_expert
            FROM plies
        )
        SELECT 
            ply_id,
            prev_expert,
            (prev_expert IS NOT NULL AND prev_expert != active_nnue_expert) as is_trans,
            case when prev_expert IS NOT NULL AND prev_expert != active_nnue_expert 
                 then prev_expert || ' -> ' || active_nnue_expert 
                 else null end as trans_pair
        FROM ranked_transitions;
    """)
    conn.execute("""
        UPDATE plies
        SET prev_expert = t.prev_expert,
            is_expert_transition = t.is_trans,
            expert_transition_pair = t.trans_pair
        FROM tmp_expert_transitions t
        WHERE plies.ply_id = t.ply_id;
    """)
    print(f"    Updated NNUE transitions in {time.time() - t0:.2f}s.")

    # 10. Create pre-computed analytical summary tables for the reporter
    print("[*] Step 8/8: Computing Analytical Summary Tables...")
    conn.execute("""
        CREATE OR REPLACE TABLE loss_archetype_summary AS
        SELECT 
            case 
                when loss_archetype = 'thrown_win' then 'Blown Lead (Eval >= +1.50 -> Loss)'
                when loss_archetype = 'cracked_defense' then 'Cracked Defense (Equal -> Loss)'
                when loss_archetype = 'opening_squeeze' then 'Opening Squeeze (Never Equal -> Loss)'
                when loss_archetype = 'missed_win' then 'Missed Win (Lead Blown to Draw)'
                else loss_archetype
            end as archetype_label,
            loss_archetype,
            count(*) as total_games,
            round(count(*) * 100.0 / nullif((SELECT count(*) FROM games WHERE result = 'loss'), 0), 1) as pct_of_losses,
            sum(case when lower(anak_color) = 'white' then 1 else 0 end) as white_count,
            sum(case when lower(anak_color) = 'black' then 1 else 0 end) as black_count,
            round(sum(case when lower(anak_color) = 'black' then 1 else 0 end) * 1.0 / nullif(sum(case when lower(anak_color) = 'white' then 1 else 0 end), 0), 2) as black_to_white_ratio,
            round(avg(ply_count), 1) as avg_ply,
            round(avg(worst_eval_drop), 2) as avg_worst_eval_drop
        FROM games
        WHERE result IN ('loss', 'draw') AND loss_archetype IN ('thrown_win', 'cracked_defense', 'opening_squeeze', 'missed_win')
        GROUP BY loss_archetype
        ORDER BY total_games DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE nnue_performance_summary AS
        SELECT 
            coalesce(middlegame_expert, 'Unknown') as expert,
            count(*) as total_games,
            sum(case when result = 'win' then 1 else 0 end) as wins,
            sum(case when result = 'loss' then 1 else 0 end) as losses,
            sum(case when result = 'draw' then 1 else 0 end) as draws,
            round(sum(anak_score) * 100.0 / nullif(count(*), 0), 1) as perf_percent,
            sum(case when lower(anak_color) = 'white' and result = 'loss' then 1 else 0 end) as white_losses,
            sum(case when lower(anak_color) = 'black' and result = 'loss' then 1 else 0 end) as black_losses,
            sum(case when loss_archetype = 'thrown_win' then 1 else 0 end) as thrown_wins,
            sum(case when loss_archetype = 'cracked_defense' then 1 else 0 end) as cracked_defenses
        FROM games
        WHERE middlegame_expert != 'Unknown'
        GROUP BY middlegame_expert
        ORDER BY perf_percent ASC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE color_performance_summary AS
        SELECT 
            lower(anak_color) as color,
            count(*) as total_games,
            sum(case when result = 'win' then 1 else 0 end) as wins,
            sum(case when result = 'loss' then 1 else 0 end) as losses,
            sum(case when result = 'draw' then 1 else 0 end) as draws,
            round(sum(anak_score) * 100.0 / nullif(count(*), 0), 1) as perf_percent,
            round(avg(case when result = 'loss' then ply_count else null end), 1) as avg_loss_ply,
            sum(case when loss_archetype = 'thrown_win' then 1 else 0 end) as thrown_wins,
            sum(case when loss_archetype = 'cracked_defense' then 1 else 0 end) as cracked_defenses,
            sum(case when loss_phase = 'Early Knockout (Ply <= 30)' then 1 else 0 end) as early_knockouts
        FROM games
        GROUP BY lower(anak_color)
        ORDER BY color DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE opening_family_summary AS
        SELECT 
            coalesce(opening_family, 'Unclassified') as opening_family,
            pawn_structure,
            count(*) as total_games,
            sum(case when result = 'win' then 1 else 0 end) as wins,
            sum(case when result = 'loss' then 1 else 0 end) as losses,
            sum(case when result = 'draw' then 1 else 0 end) as draws,
            round(sum(anak_score) * 100.0 / nullif(count(*), 0), 1) as perf_percent,
            sum(case when lower(anak_color) = 'black' and result = 'loss' then 1 else 0 end) as black_losses,
            round(avg(case when result = 'loss' then ply_count else null end), 1) as avg_loss_ply
        FROM games
        GROUP BY opening_family, pawn_structure
        HAVING count(*) >= 10
        ORDER BY perf_percent ASC, total_games DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE loss_phase_summary AS
        SELECT 
            loss_phase,
            count(*) as total_losses,
            round(count(*) * 100.0 / nullif((SELECT count(*) FROM games WHERE result = 'loss'), 0), 1) as pct_of_losses,
            sum(case when lower(anak_color) = 'white' then 1 else 0 end) as white_losses,
            sum(case when lower(anak_color) = 'black' then 1 else 0 end) as black_losses,
            round(avg(ply_count), 1) as avg_ply
        FROM games
        WHERE result = 'loss'
        GROUP BY loss_phase
        ORDER BY total_losses DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE endgame_performance_summary AS
        SELECT 
            endgame_archetype,
            count(*) as total_games,
            sum(case when result = 'win' then 1 else 0 end) as wins,
            sum(case when result = 'loss' then 1 else 0 end) as losses,
            sum(case when result = 'draw' then 1 else 0 end) as draws,
            round(sum(anak_score) * 100.0 / nullif(count(*), 0), 1) as perf_percent,
            sum(case when lower(anak_color) = 'white' and result = 'loss' then 1 else 0 end) as white_losses,
            sum(case when lower(anak_color) = 'black' and result = 'loss' then 1 else 0 end) as black_losses,
            sum(case when loss_archetype = 'thrown_win' then 1 else 0 end) as thrown_wins,
            sum(case when loss_archetype = 'cracked_defense' then 1 else 0 end) as cracked_defenses,
            round(avg(case when result = 'loss' then ply_count else null end), 1) as avg_loss_ply
        FROM games
        WHERE endgame_archetype != 'Unclassified'
        GROUP BY endgame_archetype
        ORDER BY total_games DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE blunder_motif_summary AS
        SELECT 
            coalesce(blunder_motif, 'Positional / Tactical Slip') as blunder_motif,
            count(*) as total_blunders,
            round(count(*) * 100.0 / nullif((SELECT count(*) FROM plies WHERE is_blunder = TRUE AND is_anak_move = TRUE), 0), 1) as pct_of_blunders,
            sum(case when side_to_move = 'white' then 1 else 0 end) as white_blunders,
            sum(case when side_to_move = 'black' then 1 else 0 end) as black_blunders,
            round(avg(eval_delta), 2) as avg_eval_drop,
            round(avg(ply_number), 1) as avg_ply
        FROM plies
        WHERE is_blunder = TRUE AND is_anak_move = TRUE AND blunder_motif != 'None'
        GROUP BY blunder_motif
        ORDER BY total_blunders DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE lead_bleed_summary AS
        SELECT 
            lead_bleed_type,
            count(*) as total_games,
            round(count(*) * 100.0 / nullif((SELECT count(*) FROM games WHERE loss_archetype = 'thrown_win'), 0), 1) as pct_of_thrown,
            round(avg(max_anak_eval), 2) as avg_peak_lead,
            sum(case when lower(anak_color) = 'white' then 1 else 0 end) as white_losses,
            sum(case when lower(anak_color) = 'black' then 1 else 0 end) as black_losses,
            round(avg(worst_eval_drop), 2) as avg_worst_drop,
            round(avg(ply_count), 1) as avg_ply
        FROM games
        WHERE loss_archetype = 'thrown_win'
        GROUP BY lead_bleed_type
        ORDER BY total_games DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE nnue_transition_summary AS
        SELECT 
            expert_transition_pair,
            prev_expert as from_expert,
            active_nnue_expert as to_expert,
            count(*) as transition_count,
            sum(case when is_blunder then 1 else 0 end) as blunder_count,
            round(sum(case when is_blunder then 1.0 else 0.0 end) * 100.0 / nullif(count(*), 0), 2) as blunder_rate_pct,
            round(avg(abs(eval_delta)), 2) as avg_abs_eval_swing,
            round(avg(openness), 2) as avg_openness
        FROM plies
        WHERE is_expert_transition = TRUE AND is_anak_move = TRUE
        GROUP BY expert_transition_pair, prev_expert, active_nnue_expert
        HAVING count(*) >= 100
        ORDER BY blunder_rate_pct DESC, transition_count DESC;
    """)

    conn.execute("""
        CREATE OR REPLACE TABLE search_deficit_summary AS
        WITH plies_with_opp AS (
            SELECT 
                game_id, ply_number, is_anak_move, anak_depth, anak_time_sec, is_blunder,
                coalesce(lag(opponent_depth) OVER (PARTITION BY game_id ORDER BY ply_number ASC),
                         lead(opponent_depth) OVER (PARTITION BY game_id ORDER BY ply_number ASC)) as opp_depth
            FROM plies
        )
        SELECT 
            case 
                when (opp_depth - anak_depth) <= -2 then 'Anak Outsearching (+2 or more plies)'
                when (opp_depth - anak_depth) BETWEEN -1 AND 1 then 'Parity (Within 1 ply)'
                when (opp_depth - anak_depth) BETWEEN 2 AND 4 then 'Moderate Deficit (2-4 plies behind)'
                when (opp_depth - anak_depth) >= 5 then 'Severe Search Deficit (5+ plies behind)'
                else 'Unknown'
            end as depth_tier,
            count(*) as total_plies,
            sum(case when is_blunder then 1 else 0 end) as blunder_count,
            round(sum(case when is_blunder then 1.0 else 0.0 end) * 100.0 / nullif(count(*), 0), 2) as blunder_rate_pct,
            round(avg(anak_depth), 1) as avg_anak_depth,
            round(avg(opp_depth), 1) as avg_opp_depth,
            round(avg(anak_time_sec), 3) as avg_time_sec
        FROM plies_with_opp
        WHERE is_anak_move = TRUE AND anak_depth IS NOT NULL AND opp_depth IS NOT NULL
        GROUP BY depth_tier
        ORDER BY min(opp_depth - anak_depth) ASC;
    """)

    print(f"    Features generated & stored in DuckDB successfully.")

    # 6. Targeted Stockfish Oracle Audit (Ground-Truth & ST9 Training Labels)
    if run_oracle and (oracle_limit is None or oracle_limit > 0):
        print(f"\n[*] Step 4/4: Running Full-Power Stockfish Oracle on Critical Failure Positions...")
        _run_oracle_enrichment(
            conn=conn,
            delta_threshold=oracle_delta_threshold,
            only_throws=oracle_only_throws,
            limit=oracle_limit,
            training_depth=oracle_training_depth,
            analysis_depth=oracle_analysis_depth,
            concurrency=oracle_concurrency
        )

    print("\n[+] Feature Enrichment Pipeline Complete! Database is fully populated.")


def _run_oracle_enrichment(
    conn: duckdb.DuckDBPyConnection,
    delta_threshold: float = 2.0,
    only_throws: bool = False,
    limit: Optional[int] = None,
    training_depth: int = 9,
    analysis_depth: int = 20,
    concurrency: int = 16
):
    """
    Selects critical turning point positions from lost games (especially vs SF 1600/1800)
    and evaluates them concurrently with Stockfish at depth 9 (for training data) and depth 20 (for truth).
    Guarantees 100% strictly unique positions (no FEN analyzed twice).
    No hardcoded limit: every qualifying position meeting the error threshold is sent to the Oracle.
    """
    cond = "p.is_throw = TRUE" if only_throws else f"(p.is_throw = TRUE OR p.eval_delta <= -abs({delta_threshold}))"
    limit_clause = f"LIMIT {limit}" if (limit is not None and limit > 0) else ""

    query = f"""
        SELECT 
            p.fen_before,
            arg_max(p.game_id, p.is_throw) as game_id,
            arg_max(p.ply_number, p.is_throw) as ply_number,
            arg_max(p.move_uci, p.is_throw) as move_uci,
            arg_max(p.side_to_move, p.is_throw) as side_to_move,
            arg_max(p.active_nnue_expert, p.is_throw) as active_nnue_expert,
            min(m.stockfish_elo) as stockfish_elo,
            arg_max(coalesce(p.anak_eval, 0.0), p.is_throw) as anak_eval,
            bool_or(p.is_throw) as is_throw,
            bool_or(p.is_blunder) as is_blunder,
            min(p.eval_delta) as eval_delta
        FROM plies p
        JOIN games g ON p.game_id = g.game_id
        JOIN matches m ON g.match_id = m.match_id
        LEFT JOIN weakness_training_data w ON p.fen_before = w.fen
        WHERE w.fen IS NULL 
          AND g.result = 'loss'
          AND p.is_anak_move = TRUE
          AND {cond}
        GROUP BY p.fen_before
        ORDER BY 
          bool_or(p.is_throw) DESC, 
          abs(coalesce(min(p.eval_delta), 0.0)) DESC,
          min(m.stockfish_elo) ASC
        {limit_clause};
    """

    rows = conn.execute(query).fetchall()

    if not rows:
        print("    No unanalyzed candidate positions found for Oracle audit.")
        return

    actual_workers = max(1, min(concurrency, len(rows)))
    mode_str = "Thrown Wins (eval >= +1.0 -> loss)" if only_throws else f"Severe Failure (Throw OR Eval Drop >= {abs(delta_threshold):.1f} pawns)"
    print(f"    Selected {len(rows):,} strictly unique candidate positions across failure plies.")
    print(f"    Filter Mode: {mode_str} (No hardcoded limit)")
    print(f"    Running with {actual_workers} concurrent Stockfish threads (ST{training_depth} training / ST{analysis_depth} ground-truth)...")

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
                fen, game_id, ply_num, played_move, side, expert, elo, anak_eval, is_throw_flag, is_blunder_flag, eval_delta = r
                sf9, sf20, dt = fut.result()

                is_white = (side.lower() == "white")
                sf9_pawns_white = sf9["eval"] if is_white else -sf9["eval"]
                sf9_cp = int(round(sf9_pawns_white * 100))
                sf20_pawns_white = sf20["eval"] if is_white else -sf20["eval"]

                eval_err = round(abs(anak_eval - sf20_pawns_white), 2)
                is_blunder_move = (played_move != sf20["best_move"] and abs(sf20["eval"]) >= 1.5)

                if is_throw_flag:
                    mis_type = f"Lead Blown ({expert})"
                elif anak_eval >= 1.0 and sf20_pawns_white <= -1.0:
                    mis_type = f"False Optimism ({expert})"
                elif anak_eval <= -1.0 and sf20_pawns_white >= 0.5:
                    mis_type = f"Blind Panic ({expert})"
                elif abs(eval_delta or 0.0) >= 2.0:
                    mis_type = f"Blunder {eval_delta:+.1f} ({expert})"
                else:
                    mis_type = f"Loss vs SF {elo} ({expert})"

                # Insert into weakness_training_data (ST9 training data)
                conn.execute("""
                    INSERT OR REPLACE INTO weakness_training_data (
                        fen, game_id, ply_number, active_nnue_expert, anak_eval,
                        sf9_eval_pawns, sf9_eval_cp, sf9_best_move, sf20_eval_pawns,
                        eval_error, misidentification_type
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """, (
                    fen, game_id, ply_num, expert, round(anak_eval, 2),
                    sf9_pawns_white, sf9_cp, sf9["best_move"] or "", sf20_pawns_white,
                    eval_err, mis_type
                ))

                # Insert into oracle_evaluations
                conn.execute("""
                    INSERT OR REPLACE INTO oracle_evaluations (
                        fen, game_id, ply_number, played_move, anak_eval,
                        sf_depth, sf_eval, sf_best_move, eval_error, is_objective_blunder
                    ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?);
                """, (
                    fen, game_id, ply_num, played_move or "", round(anak_eval, 2),
                    analysis_depth, sf20["eval"], sf20["best_move"] or "", eval_err, is_blunder_move
                ))

                evaluated += 1
                status_tag = "[BLUNDER]" if is_blunder_move else "[OK]"
                print(f"    [{evaluated}/{len(rows)}] {status_tag} {dt:.2f}s | SF{elo} {expert} | ST9: {sf9['eval']:+.2f} ({sf9_cp} cp) | ST20: {sf20['eval']:+.2f} | Played: {played_move} vs Best: {sf20['best_move']}")

    finally:
        pool.close()

    print(f"    Evaluated & stored {evaluated} strictly unique positions in weakness_training_data and oracle_evaluations.")


def main():
    parser = argparse.ArgumentParser(description="Anak Weakness Finder — Feature Generation & Enrichment Pipeline")
    parser.add_argument("--db", type=str, default=str(Path(__file__).resolve().parent / "weakness_finder.duckdb"), help="DuckDB database path")
    parser.add_argument("--oracle-limit", type=int, default=30, help="Number of positions to audit with Stockfish Oracle (default: 30)")
    parser.add_argument("--training-depth", type=int, default=9, help="Stockfish depth for training data (default: 9)")
    parser.add_argument("--analysis-depth", type=int, default=20, help="Stockfish depth for deep diagnostics (default: 20)")
    parser.add_argument("--skip-oracle", action="store_true", default=False, help="Skip Stockfish oracle evaluation")

    args = parser.parse_args()
    enrich_database_features(
        db_path=Path(args.db),
        run_oracle=not args.skip_oracle,
        oracle_limit=args.oracle_limit,
        oracle_training_depth=args.training_depth,
        oracle_analysis_depth=args.analysis_depth
    )


if __name__ == "__main__":
    main()
