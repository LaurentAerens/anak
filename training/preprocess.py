"""
High-Speed Multi-Threaded One-Time Preprocessor & Dataset Splitter for Anak NNUE.
Parses multi-source raw chess data (TCEC, Stockfish, Leela, Lichess, Generated),
calculates pawn openness and tactical metrics, and pre-splits positions into
compact memory-mapped binary packs (open.binpack, closed.binpack, micro.binpack).
Eliminates text PGN parsing during training epochs.
"""

import argparse
import json
import os
import random
import sys
import time
from typing import List, Dict, Any, Optional
import chess
import chess.pgn
from tqdm import tqdm

from dataset import find_pgn_files, parse_eval_from_comment, find_hanging_piece_blunder
from query import extract_metrics_from_board, PositionQuery, DatasetRecipe
from binpack import BinpackWriter, encode_features_to_indices


def determine_file_source(file_path: str) -> str:
    """Infers data source from filename conventions."""
    base = os.path.basename(file_path).lower()
    if "tcec" in base:
        return "tcec"
    elif "lichess" in base or "puzzle" in base:
        return "lichess"
    elif "stockfish" in base or "sf" in base or base.startswith("st_"):
        return "stockfish"
    elif "leela" in base or "lc0" in base:
        return "leela"
    elif "gen" in base or "generated" in base or "synthetic" in base:
        return "generated"
    return "general"


def process_pgn_stream(
    file_path: str,
    writers: Dict[str, BinpackWriter],
    recipe: Optional[DatasetRecipe] = None,
    custom_query: Optional[PositionQuery] = None,
    custom_writer: Optional[BinpackWriter] = None,
    skip_book_plies: int = 10,
    max_positions_per_file: Optional[int] = None,
    stats: Optional[Dict[str, Any]] = None
):
    """
    Parses a single PGN/GZ/ZIP file, routes positions into appropriate binpack writers,
    and updates metric statistics.
    """
    from dataset import TcecDataset
    source = determine_file_source(file_path)

    # Use TcecDataset's streaming file accessor
    dummy_ds = TcecDataset.__new__(TcecDataset)
    positions_processed = 0

    for handle in dummy_ds._stream_file(file_path):
        while True:
            try:
                game = chess.pgn.read_game(handle)
            except Exception:
                break
            if game is None:
                break

            result_str = game.headers.get("Result", "*")
            if result_str == "1-0":
                game_result = 1.0
            elif result_str == "0-1":
                game_result = 0.0
            elif result_str == "1/2-1/2":
                game_result = 0.5
            else:
                continue

            board = game.board()
            ply = 0
            prev_eval = 0.0

            for node in game.mainline():
                move = node.move
                is_capture = board.is_capture(move)
                board.push(move)
                ply += 1

                if ply <= skip_book_plies:
                    continue

                eval_val = parse_eval_from_comment(node.comment)
                if eval_val is None:
                    continue

                eval_relative = eval_val if board.turn == chess.WHITE else -eval_val
                wdl_relative = game_result if board.turn == chess.WHITE else (1.0 - game_result)
                eval_delta = abs(eval_val - prev_eval)
                prev_eval = eval_val

                # Extract rich metrics for queries & routing
                metrics = extract_metrics_from_board(
                    board,
                    eval_val=eval_relative,
                    eval_delta=eval_delta,
                    source=source
                )

                openness = metrics["openness"]
                is_blunder = metrics["is_blunder"]
                pieces = metrics["pieces"]
                has_queens = metrics["has_queens"]

                # 1. Custom Query Filter / Recipe Routing (if enabled)
                if custom_query and custom_writer:
                    if custom_query.matches(metrics):
                        custom_writer.add_board(board, eval_relative, wdl_relative, openness, is_blunder)
                        if stats:
                            stats["custom_count"] = stats.get("custom_count", 0) + 1

                if recipe:
                    routed_slice = recipe.route_sample(metrics)
                    if routed_slice and routed_slice in writers:
                        writers[routed_slice].add_board(board, eval_relative, wdl_relative, openness, is_blunder)
                        if stats:
                            stats["recipe_counts"][routed_slice] = stats["recipe_counts"].get(routed_slice, 0) + 1

                # 2. Standard 3-Way Architecture Pre-Splitting (Open, Closed, Micro)
                if "open" in writers or "closed" in writers or "micro" in writers:
                    is_secondary_10pct = random.random() < 0.10

                    # Route to Open NNUE
                    if "open" in writers:
                        if openness >= 0.50 or is_secondary_10pct:
                            writers["open"].add_board(board, eval_relative, wdl_relative, openness, is_blunder)
                            if stats:
                                stats["open_count"] += 1

                    # Route to Closed NNUE
                    if "closed" in writers:
                        if openness < 0.50 or is_secondary_10pct:
                            writers["closed"].add_board(board, eval_relative, wdl_relative, openness, is_blunder)
                            if stats:
                                stats["closed_count"] += 1

                    # Route to Micro / Minimal Blunder NNUE
                    if "micro" in writers:
                        # Synthetic blunder injection for quiet positions
                        if abs(eval_val) < 1.0 and random.random() < 0.30:
                            b_move, penalty = find_hanging_piece_blunder(board)
                            if b_move:
                                board.push(b_move)
                                b_openness = metrics["openness"]
                                writers["micro"].add_board(board, -penalty, 1.0, b_openness, is_blunder=True)
                                board.pop()
                                if stats:
                                    stats["micro_count"] += 1

                        # Blunder / Endgame filter
                        if is_blunder or is_capture or pieces <= 16 or is_secondary_10pct:
                            # Micro blunder deadzone
                            target_eval_micro = 0.0 if abs(eval_relative) < 2.0 else float(round(eval_relative))
                            writers["micro"].add_board(board, target_eval_micro, wdl_relative, openness, is_blunder)
                            if stats:
                                stats["micro_count"] += 1

                positions_processed += 1
                if stats:
                    stats["total_positions"] += 1
                    stats["sources"][source] = stats["sources"].get(source, 0) + 1
                    if not has_queens:
                        stats["queenless_count"] += 1
                    if pieces <= 16:
                        stats["endgame_count"] += 1

                if max_positions_per_file and positions_processed >= max_positions_per_file:
                    return


def preprocess_data(
    input_paths: List[str],
    output_dir: str,
    recipe_path: Optional[str] = None,
    query_str: Optional[str] = None,
    source_filter: str = "all",
    max_positions: Optional[int] = None
) -> Dict[str, Any]:
    """
    Executes one-time preprocessing across all discovered data files.
    """
    os.makedirs(output_dir, exist_ok=True)
    all_files = find_pgn_files(input_paths)

    # Filter files by source if specified
    if source_filter != "all":
        allowed = [s.strip().lower() for s in source_filter.split(",")]
        all_files = [f for f in all_files if any(a in determine_file_source(f) or a in f.lower() for a in allowed)]

    if not all_files:
        raise FileNotFoundError(f"No matching data files found in: {input_paths}")

    # Initialize Writers
    writers: Dict[str, BinpackWriter] = {
        "open": BinpackWriter(os.path.join(output_dir, "open.binpack"), model_tag="open"),
        "closed": BinpackWriter(os.path.join(output_dir, "closed.binpack"), model_tag="closed"),
        "micro": BinpackWriter(os.path.join(output_dir, "micro.binpack"), model_tag="micro"),
    }

    custom_writer = None
    custom_query = None
    if query_str:
        print(f"Applying custom query filter: '{query_str}'")
        custom_query = PositionQuery(query_str)
        custom_writer = BinpackWriter(os.path.join(output_dir, "query_filtered.binpack"), model_tag="custom")

    recipe = None
    if recipe_path and os.path.isfile(recipe_path):
        with open(recipe_path, "r", encoding="utf-8") as rf:
            recipe_dict = json.load(rf)
        recipe = DatasetRecipe(recipe_dict)
        print(f"Loaded dataset recipe with {len(recipe.slices)} slices from: {recipe_path}")
        for s in recipe.slices:
            if s["name"] not in writers:
                writers[s["name"]] = BinpackWriter(os.path.join(output_dir, f"{s['name']}.binpack"), model_tag=s["name"])

    stats = {
        "total_positions": 0,
        "open_count": 0,
        "closed_count": 0,
        "micro_count": 0,
        "queenless_count": 0,
        "endgame_count": 0,
        "custom_count": 0,
        "recipe_counts": {},
        "sources": {},
        "files_processed": len(all_files),
    }

    start_time = time.time()
    print(f"\n--> Starting one-time preprocessing on {len(all_files)} file(s)...")

    for f in tqdm(all_files, desc="Preprocessing archives"):
        process_pgn_stream(
            f,
            writers=writers,
            recipe=recipe,
            custom_query=custom_query,
            custom_writer=custom_writer,
            stats=stats,
            max_positions_per_file=max_positions
        )
        if max_positions and stats["total_positions"] >= max_positions:
            break

    # Close and finalize all binary packs
    for w in writers.values():
        w.close()
    if custom_writer:
        custom_writer.close()

    elapsed = time.time() - start_time
    rate = stats["total_positions"] / max(0.001, elapsed)

    # Save manifest info
    manifest_path = os.path.join(output_dir, "manifest.json")
    manifest = {
        "timestamp": time.strftime("%Y-%m-%d %H:%M:%S"),
        "elapsed_seconds": round(elapsed, 2),
        "positions_per_sec": round(rate, 1),
        "stats": stats,
        "generated_files": [f for f in os.listdir(output_dir) if f.endswith(".binpack")],
    }
    with open(manifest_path, "w", encoding="utf-8") as mf:
        json.dump(manifest, mf, indent=2)

    print("\n" + "=" * 70)
    print("=== One-Time Preprocessing Complete! ===")
    print(f"Total Positions Parsed:  {stats['total_positions']:,}")
    print(f"Open Binpack:            {stats['open_count']:,} positions -> {os.path.join(output_dir, 'open.binpack')}")
    print(f"Closed Binpack:          {stats['closed_count']:,} positions -> {os.path.join(output_dir, 'closed.binpack')}")
    print(f"Micro/Minimal Binpack:   {stats['micro_count']:,} positions -> {os.path.join(output_dir, 'micro.binpack')}")
    print(f"Queenless Positions:     {stats['queenless_count']:,} ({stats['queenless_count'] / max(1, stats['total_positions']) * 100:.1f}%)")
    print(f"Endgame (<=16 pieces):   {stats['endgame_count']:,} ({stats['endgame_count'] / max(1, stats['total_positions']) * 100:.1f}%)")
    print(f"Processing Rate:         {rate:,.0f} pos/sec ({elapsed:.1f}s)")
    print(f"Dataset Manifest:        {manifest_path}")
    print("=" * 70)

    return manifest


def parse_args():
    parser = argparse.ArgumentParser(description="One-time Preprocessor & Dataset Splitter for Anak NNUE")
    parser.add_argument("--input", type=str, nargs="+", default=["./training/data", "./data"],
                        help="Input PGN files or data directories")
    parser.add_argument("--output", type=str, default="./training/data/preprocessed",
                        help="Output directory for binary packs (default: training/data/preprocessed/)")
    parser.add_argument("--source", type=str, default="all",
                        help="Filter input by data source: tcec, stockfish, leela, lichess, generated, all")
    parser.add_argument("--query", type=str, default=None,
                        help="Custom query string filter (e.g. 'pieces <= 16 and not has_queens')")
    parser.add_argument("--recipe", type=str, default=None,
                        help="Path to JSON dataset recipe with multi-query quotas ('how much of what')")
    parser.add_argument("--max-positions", type=int, default=None,
                        help="Limit total number of positions to process")
    return parser.parse_args()


def main():
    args = parse_args()
    preprocess_data(
        input_paths=args.input,
        output_dir=args.output,
        recipe_path=args.recipe,
        query_str=args.query,
        source_filter=args.source,
        max_positions=args.max_positions
    )


if __name__ == "__main__":
    main()
