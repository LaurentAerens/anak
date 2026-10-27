"""
Big-Data Analytical Engine for Anak Weakness Finder.
Executes vectorized DuckDB SQL queries to isolate weak openings, NNUE expert blind spots,
search horizon throws, and time management failures.
"""

from typing import Dict, Any, List, Optional
from pathlib import Path
import duckdb

from .db_schema import get_connection, initialize_schema


class WeaknessAnalyzer:
    def __init__(self, db_path: Optional[Path] = None):
        self.conn = get_connection(db_path)
        initialize_schema(self.conn)

    def get_peak_elo(self) -> int:
        """Determines the maximum Stockfish Elo played or where score was >= 50%."""
        res = self.conn.execute("""
            SELECT coalesce(max(stockfish_elo), 2000)
            FROM matches
        """).fetchone()
        return res[0] if res else 2000

    def get_match_overview(self) -> List[Dict[str, Any]]:
        """Returns summary of all played match tiers."""
        rows = self.conn.execute("""
            SELECT 
                match_id, opponent_name, stockfish_elo, time_control,
                total_games, anak_wins, anak_losses, draws,
                round(score * 100.0, 1) as perf_percent,
                created_at
            FROM matches
            ORDER BY stockfish_elo ASC, created_at ASC
        """).fetchall()

        cols = [
            "match_id", "opponent_name", "stockfish_elo", "time_control",
            "total_games", "wins", "losses", "draws", "perf_percent", "created_at"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_color_performance(self) -> Dict[str, Any]:
        """
        Calculates comprehensive White vs. Black performance metrics:
        Win/Loss/Draw rates, score delta, blunder rates, and average loss ply by color.
        """
        rows = self.conn.execute("""
            SELECT 
                lower(anak_color) as color,
                count(*) as total_games,
                sum(case when result = 'win' then 1 else 0 end) as wins,
                sum(case when result = 'loss' then 1 else 0 end) as losses,
                sum(case when result = 'draw' then 1 else 0 end) as draws,
                round(sum(anak_score) * 100.0 / nullif(count(*), 0), 1) as perf_percent,
                sum(case when has_throw then 1 else 0 end) as throw_games,
                round(avg(case when result = 'loss' then ply_count else null end), 1) as avg_loss_ply,
                round(avg(worst_eval_drop), 2) as avg_worst_eval_drop
            FROM games
            GROUP BY lower(anak_color);
        """).fetchall()

        stats = {
            "white": {"games": 0, "wins": 0, "losses": 0, "draws": 0, "perf": 0.0, "throws": 0, "avg_loss_ply": 0.0},
            "black": {"games": 0, "wins": 0, "losses": 0, "draws": 0, "perf": 0.0, "throws": 0, "avg_loss_ply": 0.0},
            "asymmetry_gap": 0.0
        }

        for r in rows:
            color = r[0]
            if color in ("white", "black"):
                stats[color] = {
                    "games": r[1],
                    "wins": r[2],
                    "losses": r[3],
                    "draws": r[4],
                    "perf": r[5] or 0.0,
                    "throws": r[6],
                    "avg_loss_ply": r[7] or 0.0,
                    "avg_eval_drop": r[8] or 0.0
                }

        w_perf = stats["white"]["perf"]
        b_perf = stats["black"]["perf"]
        stats["asymmetry_gap"] = round(w_perf - b_perf, 1)
        return stats

    def get_weakest_black_openings(self, min_games: int = 2, limit: int = 15) -> List[Dict[str, Any]]:
        """Identifies the openings where Anak suffered the highest loss/throw rates specifically as Black."""
        rows = self.conn.execute("""
            SELECT 
                coalesce(g.opening_name, 'Unknown Opening') as opening_name,
                coalesce(g.opening_eco, '---') as opening_eco,
                g.opening_fen,
                count(*) as black_games,
                sum(case when g.result = 'win' then 1 else 0 end) as wins,
                sum(case when g.result = 'loss' then 1 else 0 end) as losses,
                sum(case when g.result = 'draw' then 1 else 0 end) as draws,
                round(sum(g.anak_score) * 100.0 / nullif(count(*), 0), 1) as black_win_pct,
                sum(case when g.has_throw then 1 else 0 end) as throws,
                round(avg(case when g.result = 'loss' then g.ply_count else null end), 1) as avg_loss_ply
            FROM games g
            WHERE lower(g.anak_color) = 'black'
            GROUP BY g.opening_name, g.opening_eco, g.opening_fen
            HAVING count(*) >= ?
            ORDER BY black_win_pct ASC, losses DESC, throws DESC
            LIMIT ?;
        """, (min_games, limit)).fetchall()

        cols = ["opening_name", "eco", "fen", "games", "wins", "losses", "draws", "win_pct", "throws", "avg_loss_ply"]
        return [dict(zip(cols, r)) for r in rows]

    def get_nnue_by_color(self) -> List[Dict[str, Any]]:
        """Breaks down NNUE expert blunder rates when playing White vs Black."""
        rows = self.conn.execute("""
            SELECT 
                p.active_nnue_expert,
                sum(case when p.side_to_move = 'white' then 1 else 0 end) as white_plies,
                sum(case when p.side_to_move = 'white' and p.is_blunder then 1 else 0 end) as white_blunders,
                round(sum(case when p.side_to_move = 'white' and p.is_blunder then 1.0 else 0.0 end) * 100.0 / nullif(sum(case when p.side_to_move = 'white' then 1 else 0 end), 0), 2) as white_blunder_pct,
                sum(case when p.side_to_move = 'black' then 1 else 0 end) as black_plies,
                sum(case when p.side_to_move = 'black' and p.is_blunder then 1 else 0 end) as black_blunders,
                round(sum(case when p.side_to_move = 'black' and p.is_blunder then 1.0 else 0.0 end) * 100.0 / nullif(sum(case when p.side_to_move = 'black' then 1 else 0 end), 0), 2) as black_blunder_pct
            FROM plies p
            WHERE p.is_anak_move = TRUE
            GROUP BY p.active_nnue_expert
            ORDER BY black_blunder_pct DESC;
        """).fetchall()

        cols = [
            "expert", "white_plies", "white_blunders", "white_blunder_pct",
            "black_plies", "black_blunders", "black_blunder_pct"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_weakest_openings(self, min_games: int = 4, limit: int = 25) -> List[Dict[str, Any]]:
        """
        Calculates weakest openings dynamically weighted by relative distance from peak Elo.
        Losses against lower Elo tiers receive heavier penalty.
        """
        peak_elo = self.get_peak_elo()

        query = f"""
            SELECT 
                coalesce(g.opening_name, 'Unknown Opening') as opening_name,
                coalesce(g.opening_eco, '---') as opening_eco,
                g.opening_fen,
                count(*) as total_games,
                sum(case when g.result = 'win' then 1 else 0 end) as wins,
                sum(case when g.result = 'loss' then 1 else 0 end) as losses,
                sum(case when g.result = 'draw' then 1 else 0 end) as draws,
                round(sum(g.anak_score) * 100.0 / count(*), 1) as win_percent,
                sum(case when g.has_throw then 1 else 0 end) as throw_count,
                round(avg(case when g.result = 'loss' then g.ply_count else null end), 1) as avg_loss_ply,
                -- Dynamic relative severity score:
                round(sum(case when g.result = 'loss' then (1.0 + (greatest(0, {peak_elo} - m.stockfish_elo) / 200.0)) else 0 end), 2) as weighted_loss_score
            FROM games g
            JOIN matches m ON g.match_id = m.match_id
            GROUP BY g.opening_name, g.opening_eco, g.opening_fen
            HAVING count(*) >= ?
            ORDER BY win_percent ASC, weighted_loss_score DESC, throw_count DESC
            LIMIT ?;
        """
        rows = self.conn.execute(query, (min_games, limit)).fetchall()
        cols = [
            "opening_name", "eco", "fen", "games", "wins", "losses",
            "draws", "win_pct", "throws", "avg_loss_ply", "severity_score"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_loss_archetypes(self) -> List[Dict[str, Any]]:
        """
        Categorizes all lost games into distinct failure archetypes:
        - thrown_win: Anak had a winning lead (>= +1.5 cp) and threw it away.
        - cracked_defense: Position was drawn/equal (within -0.5 cp), but Anak failed to hold.
        - opening_squeeze: Anak was in deficit from the start and never equalized.
        """
        rows = self.conn.execute("""
            SELECT 
                loss_archetype,
                count(*) as total_losses,
                round(count(*) * 100.0 / nullif((SELECT count(*) FROM games WHERE result = 'loss'), 0), 1) as pct_of_losses,
                sum(case when lower(anak_color) = 'white' then 1 else 0 end) as white_losses,
                sum(case when lower(anak_color) = 'black' then 1 else 0 end) as black_losses,
                round(avg(ply_count), 1) as avg_ply,
                round(avg(worst_eval_drop), 2) as avg_worst_eval_drop
            FROM games
            WHERE result = 'loss'
            GROUP BY loss_archetype
            ORDER BY total_losses DESC;
        """).fetchall()

        cols = [
            "archetype", "total_losses", "pct_of_losses",
            "white_losses", "black_losses", "avg_ply", "avg_worst_eval_drop"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_high_eval_disagreements(self, min_disagreement: float = 2.0, limit: int = 25) -> List[Dict[str, Any]]:
        """
        Extracts positions where Anak and Opponent had the greatest evaluation disagreement
        in games where Anak lost (identifies positions where Anak was blind to danger).
        """
        rows = self.conn.execute("""
            SELECT 
                p.game_id,
                g.opening_name,
                p.move_number,
                p.side_to_move,
                p.move_san,
                p.fen_before,
                p.anak_eval,
                p.opponent_eval,
                p.eval_disagreement,
                p.active_nnue_expert
            FROM plies p
            JOIN games g ON p.game_id = g.game_id
            WHERE g.result = 'loss' 
              AND p.eval_disagreement >= ?
              AND p.is_anak_move = TRUE
            ORDER BY p.eval_disagreement DESC
            LIMIT ?;
        """, (min_disagreement, limit)).fetchall()

        cols = [
            "game_id", "opening", "move_num", "side", "move",
            "fen", "anak_eval", "opp_eval", "disagreement", "expert"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_oracle_evaluations(self, limit: int = 30) -> List[Dict[str, Any]]:
        """Returns deep ground-truth Stockfish evaluations from oracle_evaluations table."""
        rows = self.conn.execute("""
            SELECT 
                fen, game_id, ply_number, played_move, anak_eval,
                sf_depth, sf_eval, sf_best_move, eval_error, is_objective_blunder
            FROM oracle_evaluations
            ORDER BY abs(eval_error) DESC
            LIMIT ?;
        """, (limit,)).fetchall()

        cols = [
            "fen", "game_id", "ply", "played_move", "anak_eval",
            "sf_depth", "sf_eval", "sf_best_move", "eval_error", "is_objective_blunder"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_nnue_expert_breakdown(self) -> List[Dict[str, Any]]:
        """
        Returns breakdown of active NNUE experts across all plies,
        identifying blunder rates, throw rates, and average openness per expert.
        """
        rows = self.conn.execute("""
            SELECT 
                p.active_nnue_expert,
                count(*) as total_plies,
                sum(case when p.is_throw then 1 else 0 end) as throw_count,
                sum(case when p.is_blunder then 1 else 0 end) as blunder_count,
                round(sum(case when p.is_blunder then 1.0 else 0.0 end) * 100.0 / nullif(count(*), 0), 2) as blunder_rate_pct,
                round(avg(case when p.is_throw then p.eval_delta else null end), 2) as avg_throw_drop,
                round(avg(p.openness), 2) as avg_openness
            FROM plies p
            WHERE p.is_anak_move = TRUE
            GROUP BY p.active_nnue_expert
            ORDER BY blunder_rate_pct DESC, throw_count DESC;
        """).fetchall()

        cols = [
            "expert", "total_plies", "throws", "blunders",
            "blunder_rate_pct", "avg_throw_drop", "avg_openness"
        ]
        return [dict(zip(cols, r)) for r in rows]

    def get_depth_comparison(self, max_ply: int = 60) -> List[Dict[str, Any]]:
        """Returns Anak vs Opponent average and median depth across plies 1 to max_ply."""
        rows = self.conn.execute("""
            SELECT 
                p.ply_number,
                round(avg(p.anak_depth), 1) as avg_anak_depth,
                round(avg(p.opponent_depth), 1) as avg_opp_depth,
                round(avg(p.opponent_depth - p.anak_depth), 1) as depth_gap,
                round(avg(p.anak_time_sec), 3) as avg_anak_time,
                round(avg(p.opponent_time_sec), 3) as avg_opp_time,
                count(*) as count
            FROM plies p
            WHERE p.ply_number <= ?
            GROUP BY p.ply_number
            ORDER BY p.ply_number ASC;
        """, (max_ply,)).fetchall()

        cols = ["ply", "anak_depth", "opp_depth", "depth_gap", "anak_time", "opp_time", "samples"]
        return [dict(zip(cols, r)) for r in rows]

    def get_time_trouble_metrics(self) -> Dict[str, Any]:
        """Calculates blunder rates under clock pressure (< 0.5s or < 0.1s remaining)."""
        res = self.conn.execute("""
            SELECT 
                count(*) as total_anak_moves,
                sum(case when is_time_trouble then 1 else 0 end) as time_trouble_moves,
                sum(case when is_time_trouble and is_blunder then 1 else 0 end) as time_trouble_blunders,
                sum(case when not is_time_trouble and is_blunder then 1 else 0 end) as normal_blunders,
                sum(case when not is_time_trouble then 1 else 0 end) as normal_moves
            FROM plies
            WHERE is_anak_move = TRUE;
        """).fetchone()

        if not res or res[0] == 0:
            return {
                "total_moves": 0, "tt_moves": 0, "tt_blunder_pct": 0.0,
                "normal_blunder_pct": 0.0, "time_pressure_risk_factor": 1.0
            }

        tot, tt_moves, tt_blunders, norm_blunders, norm_moves = res
        tt_rate = (tt_blunders / tt_moves * 100.0) if tt_moves > 0 else 0.0
        norm_rate = (norm_blunders / norm_moves * 100.0) if norm_moves > 0 else 0.0
        risk_factor = (tt_rate / norm_rate) if norm_rate > 0 else 1.0

        return {
            "total_moves": tot,
            "tt_moves": tt_moves,
            "tt_blunder_pct": round(tt_rate, 2),
            "normal_blunder_pct": round(norm_rate, 2),
            "time_pressure_risk_factor": round(risk_factor, 2)
        }

    def get_worst_tactical_throws(self, limit: int = 30) -> List[Dict[str, Any]]:
        """
        Returns the most severe single-ply throws where Anak evaluated high advantage
        and collapsed to a losing score.
        """
        rows = self.conn.execute("""
            SELECT 
                p.game_id,
                g.opening_name,
                p.move_number,
                p.side_to_move,
                p.move_san,
                p.fen_before,
                p.anak_eval,
                p.eval_delta,
                p.anak_depth,
                p.active_nnue_expert
            FROM plies p
            JOIN games g ON p.game_id = g.game_id
            WHERE p.is_throw = TRUE
            ORDER BY p.eval_delta ASC
            LIMIT ?;
        """, (limit,)).fetchall()

        cols = [
            "game_id", "opening", "move_num", "side", "move",
            "fen", "eval_after", "eval_delta", "depth", "expert"
        ]
        return [dict(zip(cols, r)) for r in rows]
