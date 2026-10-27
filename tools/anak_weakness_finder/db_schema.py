"""
DuckDB Schema and Database Manager for Anak Weakness Finder.
Provides columnar storage and vectorized analytical queries over match telemetry.
"""

from pathlib import Path
from typing import Optional
import duckdb


DEFAULT_DB_PATH = Path(__file__).resolve().parent / "weakness_finder.duckdb"


def get_connection(db_path: Optional[Path] = None) -> duckdb.DuckDBPyConnection:
    """Connects to the DuckDB database file, creating it if it doesn't exist."""
    path = db_path or DEFAULT_DB_PATH
    path.parent.mkdir(parents=True, exist_ok=True)
    conn = duckdb.connect(str(path))
    # Optimize DuckDB settings for analytics
    conn.execute("PRAGMA threads=4;")
    conn.execute("PRAGMA preserve_insertion_order=false;")
    return conn


def initialize_schema(conn: duckdb.DuckDBPyConnection):
    """Initializes tables, indexes, and views in DuckDB."""
    
    # 1. Matches Table
    conn.execute("""
        CREATE TABLE IF NOT EXISTS matches (
            match_id VARCHAR PRIMARY KEY,
            opponent_name VARCHAR,
            stockfish_elo INTEGER,
            time_control VARCHAR,
            total_games INTEGER,
            anak_wins INTEGER,
            anak_losses INTEGER,
            draws INTEGER,
            score FLOAT,
            pgn_file VARCHAR,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
    """)

    # 2. Games Table
    conn.execute("""
        CREATE TABLE IF NOT EXISTS games (
            game_id VARCHAR PRIMARY KEY,
            match_id VARCHAR,
            round_number INTEGER,
            opening_fen VARCHAR,
            opening_eco VARCHAR,
            opening_name VARCHAR,
            anak_color VARCHAR,
            result VARCHAR,
            anak_score FLOAT,
            termination VARCHAR,
            ply_count INTEGER,
            has_throw BOOLEAN DEFAULT FALSE,
            worst_eval_drop FLOAT DEFAULT 0.0,
            max_anak_eval FLOAT DEFAULT 0.0,
            loss_archetype VARCHAR DEFAULT 'none',
            FOREIGN KEY (match_id) REFERENCES matches(match_id)
        );
    """)

    # 3. Plies Table
    conn.execute("""
        CREATE TABLE IF NOT EXISTS plies (
            ply_id VARCHAR PRIMARY KEY,
            game_id VARCHAR,
            ply_number INTEGER,
            move_number INTEGER,
            side_to_move VARCHAR,
            is_anak_move BOOLEAN,
            move_san VARCHAR,
            move_uci VARCHAR,
            fen_before VARCHAR,
            anak_eval FLOAT,
            opponent_eval FLOAT,
            eval_delta FLOAT,
            eval_disagreement FLOAT DEFAULT 0.0,
            anak_depth INTEGER,
            opponent_depth INTEGER,
            anak_time_sec FLOAT,
            opponent_time_sec FLOAT,
            active_nnue_expert VARCHAR,
            openness FLOAT,
            is_throw BOOLEAN DEFAULT FALSE,
            is_blunder BOOLEAN DEFAULT FALSE,
            is_time_trouble BOOLEAN DEFAULT FALSE,
            FOREIGN KEY (game_id) REFERENCES games(game_id)
        );
    """)

    # 4. Full-Power Stockfish Oracle Evaluations Table
    conn.execute("""
        CREATE TABLE IF NOT EXISTS oracle_evaluations (
            fen VARCHAR PRIMARY KEY,
            game_id VARCHAR,
            ply_number INTEGER,
            played_move VARCHAR,
            anak_eval FLOAT,
            sf_depth INTEGER,
            sf_eval FLOAT,
            sf_best_move VARCHAR,
            eval_error FLOAT,
            is_objective_blunder BOOLEAN,
            analyzed_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
    """)

    # 5. Dedicated Weakness Training Data Table (Evaluated at Stockfish Depth 9)
    conn.execute("""
        CREATE TABLE IF NOT EXISTS weakness_training_data (
            fen VARCHAR PRIMARY KEY,
            game_id VARCHAR,
            ply_number INTEGER,
            active_nnue_expert VARCHAR,
            anak_eval FLOAT,
            sf9_eval_pawns FLOAT,
            sf9_eval_cp INTEGER,
            sf9_best_move VARCHAR,
            sf20_eval_pawns FLOAT,
            eval_error FLOAT,
            misidentification_type VARCHAR,
            created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        );
    """)

    # Automatic schema migration for existing databases
    _migrate_columns(conn)


def _migrate_columns(conn: duckdb.DuckDBPyConnection):
    """Automatically adds missing columns to existing tables for backwards compatibility."""
    try:
        games_cols = {c[1] for c in conn.execute("PRAGMA table_info(games)").fetchall()}
        if "max_anak_eval" not in games_cols:
            conn.execute("ALTER TABLE games ADD COLUMN max_anak_eval FLOAT DEFAULT 0.0;")
        if "loss_archetype" not in games_cols:
            conn.execute("ALTER TABLE games ADD COLUMN loss_archetype VARCHAR DEFAULT 'none';")
        if "endgame_archetype" not in games_cols:
            conn.execute("ALTER TABLE games ADD COLUMN endgame_archetype VARCHAR DEFAULT 'Unclassified';")
        if "lead_bleed_type" not in games_cols:
            conn.execute("ALTER TABLE games ADD COLUMN lead_bleed_type VARCHAR DEFAULT 'none';")
        if "lead_lost_ply" not in games_cols:
            conn.execute("ALTER TABLE games ADD COLUMN lead_lost_ply INTEGER DEFAULT 0;")

        plies_cols = {c[1] for c in conn.execute("PRAGMA table_info(plies)").fetchall()}
        if "eval_disagreement" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN eval_disagreement FLOAT DEFAULT 0.0;")
        if "blunder_motif" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN blunder_motif VARCHAR DEFAULT 'None';")
        if "depth_deficit" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN depth_deficit INTEGER DEFAULT 0;")
        if "is_depth_plunge" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN is_depth_plunge BOOLEAN DEFAULT FALSE;")
        if "prev_expert" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN prev_expert VARCHAR DEFAULT NULL;")
        if "is_expert_transition" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN is_expert_transition BOOLEAN DEFAULT FALSE;")
        if "expert_transition_pair" not in plies_cols:
            conn.execute("ALTER TABLE plies ADD COLUMN expert_transition_pair VARCHAR DEFAULT NULL;")
    except Exception as e:
        pass

    # 4. Analytical Views
    conn.execute("""
        CREATE OR REPLACE VIEW v_opening_performance AS
        SELECT 
            coalesce(g.opening_name, 'Unknown') as opening_name,
            coalesce(g.opening_eco, '---') as opening_eco,
            coalesce(g.opening_fen, 'startpos') as opening_fen,
            count(*) as total_games,
            sum(case when g.result = 'win' then 1 else 0 end) as wins,
            sum(case when g.result = 'loss' then 1 else 0 end) as losses,
            sum(case when g.result = 'draw' then 1 else 0 end) as draws,
            round(sum(g.anak_score) * 100.0 / count(*), 1) as win_percent,
            sum(case when g.has_throw then 1 else 0 end) as throw_games,
            round(avg(case when g.result = 'loss' then g.ply_count else null end), 1) as avg_loss_ply
        FROM games g
        GROUP BY g.opening_name, g.opening_eco, g.opening_fen;
    """)

    conn.execute("""
        CREATE OR REPLACE VIEW v_expert_attribution AS
        SELECT 
            p.active_nnue_expert,
            count(*) as total_plies_played,
            sum(case when p.is_throw then 1 else 0 end) as total_throws,
            sum(case when p.is_blunder then 1 else 0 end) as total_blunders,
            round(avg(case when p.is_throw then p.eval_delta else null end), 2) as avg_throw_drop,
            round(avg(p.openness), 2) as avg_openness
        FROM plies p
        WHERE p.is_anak_move = TRUE
        GROUP BY p.active_nnue_expert;
    """)

    conn.execute("""
        CREATE OR REPLACE VIEW v_depth_progression AS
        SELECT 
            p.ply_number,
            round(avg(p.anak_depth), 1) as avg_anak_depth,
            round(avg(p.opponent_depth), 1) as avg_opp_depth,
            round(avg(p.anak_time_sec), 3) as avg_anak_time_sec,
            round(avg(p.opponent_time_sec), 3) as avg_opp_time_sec,
            count(*) as sample_count
        FROM plies p
        WHERE p.ply_number <= 80
        GROUP BY p.ply_number
        ORDER BY p.ply_number ASC;
    """)
