"""
Weakness Datagen Bridge: Exports critical FEN positions from lost games and misidentified positions
directly to EPD / CSV format for targeted NNUE fine-tuning via training/generate_data.py or train.py.
Supports exporting from the dedicated weakness_training_data table (with ST9 depth labels).
"""

import argparse
from pathlib import Path
from typing import Optional
from .db_schema import get_connection, initialize_schema


def export_weakness_training_data(
    output_path: Path,
    export_format: str = "epd",
    expert_filter: Optional[str] = None,
    color_filter: Optional[str] = None,
    type_filter: Optional[str] = None,
    max_fens: int = 50000,
    db_path: Optional[Path] = None
) -> int:
    """
    Exports positions from weakness_training_data (with ST9 evaluations)
    or falls back to raw plies if no ST9 evaluations have been run yet.
    """
    conn = get_connection(db_path)
    initialize_schema(conn)

    # Check if weakness_training_data has rows
    st9_count = conn.execute("SELECT count(*) FROM weakness_training_data").fetchone()[0]

    if st9_count > 0:
        query = """
            SELECT fen, sf9_eval_cp, sf9_eval_pawns, active_nnue_expert, sf9_best_move, misidentification_type
            FROM weakness_training_data
            WHERE 1=1
        """
        params = []
        if expert_filter:
            query += " AND active_nnue_expert = ?"
            params.append(expert_filter)
        if type_filter:
            query += " AND misidentification_type = ?"
            params.append(type_filter)

        query += " LIMIT ?"
        params.append(max_fens)
        rows = conn.execute(query, params).fetchall()

        output_path.parent.mkdir(parents=True, exist_ok=True)
        with open(output_path, "w", encoding="utf-8") as f:
            if export_format == "csv":
                f.write("fen,score_pawns,score_cp,expert,best_move,type\n")
                for fen, cp, pawns, exp, bm, mtype in rows:
                    f.write(f'"{fen}",{pawns},{cp},"{exp}","{bm}","{mtype}"\n')
            elif export_format == "epd":
                # Standard EPD with centipawn eval tag
                for fen, cp, pawns, exp, bm, mtype in rows:
                    bm_tag = f' bm {bm};' if bm else ''
                    f.write(f'{fen} ce {cp};{bm_tag}\n')
            else:
                for fen, _, _, _, _, _ in rows:
                    f.write(f"{fen}\n")

        print(f"[+] Exported {len(rows)} positions from weakness_training_data (evaluated at ST9 depth) to: {output_path}")
        return len(rows)

    else:
        # Fallback to extracting FENs directly from lost games/plies
        print("[!] Note: weakness_training_data table is empty. Extracting raw FENs from plies.")
        print("    Tip: Run 'python -m tools.anak_weakness_finder.main oracle' to evaluate positions with ST9 depth first.")
        query = """
            SELECT DISTINCT p.fen_before
            FROM plies p
            JOIN games g ON p.game_id = g.game_id
            WHERE (g.result = 'loss' OR p.is_throw = TRUE OR p.eval_delta <= -1.5)
        """
        params = []
        if expert_filter:
            query += " AND p.active_nnue_expert = ?"
            params.append(expert_filter)
        if color_filter:
            query += " AND lower(g.anak_color) = ?"
            params.append(color_filter.lower())

        query += " LIMIT ?"
        params.append(max_fens)
        rows = conn.execute(query, params).fetchall()

        output_path.parent.mkdir(parents=True, exist_ok=True)
        with open(output_path, "w", encoding="utf-8") as f:
            for (fen,) in rows:
                if fen:
                    f.write(f"{fen}\n")

        print(f"[+] Exported {len(rows)} raw candidate FENs to: {output_path}")
        return len(rows)


def main():
    parser = argparse.ArgumentParser(description="Export weakness training data for targeted NNUE fine-tuning")
    parser.add_argument("--output", type=str, default="training/data/weakness_training_st9.epd", help="Destination file path")
    parser.add_argument("--format", type=str, default="epd", choices=["epd", "csv", "plain"], help="Export format (default: epd with ce tags)")
    parser.add_argument("--expert", type=str, default=None, choices=["Open", "Closed", "Endgame", "Queenless", "Imbalance", "Attack"], help="Filter by NNUE expert")
    parser.add_argument("--color", type=str, default=None, choices=["white", "black"], help="Filter by player color (e.g. black)")
    parser.add_argument("--type", type=str, default=None, choices=["throw", "cracked_defense", "disagreement"], help="Filter by misidentification type")
    parser.add_argument("--max-fens", type=int, default=50000, help="Maximum FENs to export (default: 50000)")

    args = parser.parse_args()
    export_weakness_training_data(
        output_path=Path(args.output),
        export_format=args.format,
        expert_filter=args.expert,
        color_filter=args.color,
        type_filter=args.type,
        max_fens=args.max_fens
    )


if __name__ == "__main__":
    main()
