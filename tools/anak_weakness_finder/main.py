"""
Anak Weakness Finder — Command Line Interface.
Unified CLI for the automated Elo ladder, DuckDB ingestion, failure analytics,
and remediation generation.
"""

import argparse
import sys
from pathlib import Path

if __package__ is None or __package__ == "":
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent.parent))
    from tools.anak_weakness_finder.db_schema import DEFAULT_DB_PATH
    from tools.anak_weakness_finder.ingest_pgn import ingest_pgn_file
    from tools.anak_weakness_finder.enrich_features import enrich_database_features
    from tools.anak_weakness_finder.report_generator import generate_reports
    from tools.anak_weakness_finder.export_training_fens import export_weakness_training_data
    from tools.anak_weakness_finder.test_case_generator import generate_python_repro_script
    from tools.anak_weakness_finder.run_weakness_ladder import run_ladder, DEFAULT_OPENINGS
    from tools.anak_weakness_finder.oracle_analyzer import run_oracle_audit
    from tools.anak_weakness_finder.campaign import run_full_campaign
else:
    from .db_schema import DEFAULT_DB_PATH
    from .ingest_pgn import ingest_pgn_file
    from .enrich_features import enrich_database_features
    from .report_generator import generate_reports
    from .export_training_fens import export_weakness_training_data
    from .test_case_generator import generate_python_repro_script
    from .run_weakness_ladder import run_ladder, DEFAULT_OPENINGS
    from .oracle_analyzer import run_oracle_audit
    from .campaign import run_full_campaign


def main():
    parser = argparse.ArgumentParser(
        prog="anak-weakness-finder",
        description="Anak Weakness Finder: Automated Elo ladder & DuckDB big-data failure analytics"
    )
    subparsers = parser.add_subparsers(dest="command", help="Available subcommands")

    # 0. run-all (Full Chain Campaign)
    sub_all = subparsers.add_parser("run-all", help="Execute the complete end-to-end campaign (matches -> oracle -> reports -> datagen)")
    sub_all.add_argument("--start-elo", type=int, default=1600, help="Initial Stockfish Elo (default: 1600)")
    sub_all.add_argument("--step-elo", type=int, default=200, help="Elo increment per tier (default: 200)")
    sub_all.add_argument("--games", type=int, default=10000, help="Games per tier (default: 10000)")
    sub_all.add_argument("--stop-threshold", type=float, default=0.50, help="Stop score threshold (default: 0.50)")
    sub_all.add_argument("--tc", type=str, default="1+0.01", help="Time control (default: 1+0.01)")
    sub_all.add_argument("--concurrency", type=int, default=12, help="Concurrency (default: 12)")
    sub_all.add_argument("--hash", type=int, default=32, help="Hash table MB per engine (default: 32)")
    sub_all.add_argument("--threads", type=int, default=1, help="Threads per engine (default: 1)")
    sub_all.add_argument("--openings", type=str, default=str(DEFAULT_OPENINGS), help="Openings file")
    sub_all.add_argument("--max-elo", type=int, default=3400, help="Maximum Elo to test")
    sub_all.add_argument("--oracle-limit", type=lambda x: None if x.lower() in ("none", "0", "unlimited", "all") else int(x), default=None, help="Max unique positions to audit with Oracle (default: None = unlimited, every qualifying threshold position)")
    sub_all.add_argument("--delta-threshold", type=float, default=2.0, help="Eval drop threshold in pawns where engine went very wrong (default: 2.0)")
    sub_all.add_argument("--only-throws", action="store_true", default=False, help="Only evaluate blown leads (eval >= +1.0 to loss)")
    sub_all.add_argument("--oracle-training-depth", type=int, default=9, help="Oracle depth for training data (default: 9)")
    sub_all.add_argument("--oracle-analysis-depth", type=int, default=20, help="Oracle depth for diagnostics (default: 20)")
    sub_all.add_argument("--oracle-concurrency", type=int, default=16, help="Concurrency for Oracle analysis (default: 16)")
    sub_all.add_argument("--resume", action="store_true", default=True, help="Auto-resume from previous state (default: True)")
    sub_all.add_argument("--no-resume", action="store_false", dest="resume", help="Do not resume, start fresh")
    sub_all.add_argument("--reset", action="store_true", default=False, help="Wipe campaign state and start clean")
    sub_all.add_argument("--reprocess-only", "--analytics-only", action="store_true", default=False, dest="reprocess_only",
                         help="Skip playing match games and only reprocess existing data & regenerate reports")

    # 1. ladder
    sub_ladder = subparsers.add_parser("ladder", help="Run automated Stockfish Elo ladder match campaign")
    sub_ladder.add_argument("--start-elo", type=int, default=1600, help="Initial Stockfish Elo (default: 1600)")
    sub_ladder.add_argument("--step-elo", type=int, default=200, help="Elo increment per step (default: 200)")
    sub_ladder.add_argument("--games", type=int, default=10000, help="Games per tier (default: 10000)")
    sub_ladder.add_argument("--stop-threshold", type=float, default=0.50, help="Stop threshold (default: 0.50)")
    sub_ladder.add_argument("--tc", type=str, default="1+0.01", help="Time control (default: 1+0.01)")
    sub_ladder.add_argument("--concurrency", type=int, default=12, help="Concurrency (default: 12)")
    sub_ladder.add_argument("--openings", type=str, default=str(DEFAULT_OPENINGS), help="Openings file")

    # 2. ingest
    sub_ingest = subparsers.add_parser("ingest", help="Stream an existing PGN archive into DuckDB")
    sub_ingest.add_argument("pgn", type=str, help="Path to PGN file")
    sub_ingest.add_argument("--match-id", type=str, default=None, help="Custom match ID")
    sub_ingest.add_argument("--opponent", type=str, default=None, help="Opponent name")
    sub_ingest.add_argument("--elo", type=int, default=None, help="Stockfish calibrated Elo")
    sub_ingest.add_argument("--tc", type=str, default=None, help="Time control")
    sub_ingest.add_argument("--db", type=str, default=str(DEFAULT_DB_PATH), help="DuckDB database path")

    # 3. analyze (Two-Stage Analytics Engine)
    sub_analyze = subparsers.add_parser("analyze", help="Execute Two-Stage Analytics Engine (Stage 1 Enrichment -> Stage 2 Reporting)")
    sub_analyze.add_argument("--db", type=str, default=str(DEFAULT_DB_PATH), help="DuckDB database path")
    sub_analyze.add_argument("--out-md", type=str, default="weakness_report.md", help="Markdown output path")
    sub_analyze.add_argument("--out-html", type=str, default="weakness_report.html", help="HTML dashboard output path")
    sub_analyze.add_argument("--oracle-limit", type=lambda x: None if x.lower() in ("none", "0", "unlimited", "all") else int(x), default=0, help="Optional max cap for Oracle (default: 0 = skip oracle in quick analyze; set 'all' or a number to audit)")
    sub_analyze.add_argument("--delta-threshold", type=float, default=2.0, help="Eval drop threshold in pawns where engine went very wrong (default: 2.0)")
    sub_analyze.add_argument("--only-throws", action="store_true", default=False, help="Only evaluate blown leads (eval >= +1.0 to loss)")
    sub_analyze.add_argument("--oracle-training-depth", type=int, default=9, help="Oracle depth for training data (default: 9)")
    sub_analyze.add_argument("--oracle-analysis-depth", type=int, default=20, help="Oracle depth for diagnostics (default: 20)")
    sub_analyze.add_argument("--oracle-concurrency", type=int, default=16, help="Concurrency for Oracle analysis (default: 16)")
    sub_analyze.add_argument("--report-only", action="store_true", default=False, help="Skip feature re-enrichment and only re-render reports")

    # 4. export-fens
    sub_fens = subparsers.add_parser("export-fens", help="Export weak opening FENs for NNUE fine-tuning")
    sub_fens.add_argument("--out", type=str, default="training/data/weakness_training_st9.epd", help="Destination file")
    sub_fens.add_argument("--format", type=str, default="epd", choices=["epd", "csv", "plain"], help="Format (default: epd with ce tags)")
    sub_fens.add_argument("--expert", type=str, default=None, help="Filter by NNUE expert (Open, Closed, Imbalance...)")
    sub_fens.add_argument("--color", type=str, default=None, choices=["white", "black"], help="Filter by color (white or black)")
    sub_fens.add_argument("--type", type=str, default=None, choices=["throw", "cracked_defense", "disagreement"], help="Filter by misidentification type")
    sub_fens.add_argument("--max", type=int, default=50000, help="Max FENs to export")
    sub_fens.add_argument("--db", type=str, default=str(DEFAULT_DB_PATH), help="DuckDB database path")

    # 5. gen-repro
    sub_repro = subparsers.add_parser("gen-repro", help="Generate Alpha-Beta bug reproduction test suite")
    sub_repro.add_argument("--out", type=str, default="tools/anak_weakness_finder/repro_search_throws.py", help="Output script path")
    sub_repro.add_argument("--limit", type=int, default=25, help="Number of throws to extract")
    sub_repro.add_argument("--db", type=str, default=str(DEFAULT_DB_PATH), help="DuckDB database path")

    # 6. oracle
    sub_oracle = subparsers.add_parser("oracle", help="Run full-power Stockfish audit on all unique positions reaching failure threshold")
    sub_oracle.add_argument("--analysis-depth", type=int, default=20, help="Deep search depth for analysis (default: 20)")
    sub_oracle.add_argument("--training-depth", type=int, default=9, help="Stockfish evaluation depth for training data (default: 9)")
    sub_oracle.add_argument("--delta-threshold", type=float, default=2.0, help="Eval drop threshold in pawns where engine went very wrong (default: 2.0)")
    sub_oracle.add_argument("--only-throws", action="store_true", default=False, help="Only evaluate blown leads (eval >= +1.0 to loss)")
    sub_oracle.add_argument("--limit", type=lambda x: None if x.lower() in ("none", "0", "unlimited", "all") else int(x), default=None, help="Optional max positions cap (default: None = unlimited, every position reaching threshold)")
    sub_oracle.add_argument("--concurrency", type=int, default=16, help="Concurrency for Oracle analysis (default: 16)")
    sub_oracle.add_argument("--db", type=str, default=str(DEFAULT_DB_PATH), help="DuckDB database path")

    args = parser.parse_args()

    if args.command == "run-all":
        run_full_campaign(
            start_elo=args.start_elo,
            step_elo=args.step_elo,
            games_per_tier=args.games,
            stop_threshold=args.stop_threshold,
            tc=args.tc,
            concurrency=args.concurrency,
            hash_mb=args.hash,
            threads=args.threads,
            openings_path=Path(args.openings),
            max_elo=args.max_elo,
            oracle_limit=args.oracle_limit,
            oracle_delta_threshold=args.delta_threshold,
            oracle_only_throws=args.only_throws,
            oracle_training_depth=args.oracle_training_depth,
            oracle_analysis_depth=args.oracle_analysis_depth,
            oracle_concurrency=args.oracle_concurrency,
            resume=args.resume,
            reset=args.reset,
            reprocess_only=args.reprocess_only
        )
    elif args.command == "ladder":
        run_ladder(
            start_elo=args.start_elo,
            step_elo=args.step_elo,
            games_per_tier=args.games,
            stop_threshold=args.stop_threshold,
            tc=args.tc,
            concurrency=args.concurrency,
            openings_path=Path(args.openings)
        )
    elif args.command == "ingest":
        ingest_pgn_file(
            pgn_path=Path(args.pgn),
            match_id=args.match_id,
            opponent_name=args.opponent,
            stockfish_elo=args.elo,
            time_control=args.tc,
            db_path=Path(args.db)
        )
    elif args.command == "analyze":
        out_md = Path(args.out_md)
        out_html = Path(args.out_html)
        db_path = Path(args.db)

        if not args.report_only:
            print("\n>>> [STAGE 1/2] Running Feature Generation & Enrichment Engine...")
            enrich_database_features(
                db_path=db_path,
                run_oracle=(args.oracle_limit is None or args.oracle_limit > 0),
                oracle_limit=args.oracle_limit,
                oracle_delta_threshold=args.delta_threshold,
                oracle_only_throws=args.only_throws,
                oracle_training_depth=args.oracle_training_depth,
                oracle_analysis_depth=args.oracle_analysis_depth,
                oracle_concurrency=args.oracle_concurrency
            )
        else:
            print("\n>>> [STAGE 1/2] Skipped (Report-Only Mode)...")

        print("\n>>> [STAGE 2/2] Generating Pure Declarative Reports from DuckDB...")
        md_file, html_file = generate_reports(db_path=db_path, out_md=out_md, out_html=out_html)
        print("\n" + "=" * 76)
        print("           TWO-STAGE ANALYTICS PIPELINE COMPLETED SUCCESSFULLY!")
        print("=" * 76)
        print(f"  [+] Markdown Report:    file:///{md_file.resolve()}")
        print(f"  [+] HTML Dashboard:     file:///{html_file.resolve()}")
        print("=" * 76 + "\n")
    elif args.command == "export-fens":
        export_weakness_training_data(
            output_path=Path(args.out),
            export_format=args.format,
            expert_filter=args.expert,
            color_filter=args.color,
            type_filter=args.type,
            max_fens=args.max,
            db_path=Path(args.db)
        )
    elif args.command == "gen-repro":
        generate_python_repro_script(
            output_path=Path(args.out),
            limit=args.limit,
            db_path=Path(args.db)
        )
    elif args.command == "oracle":
        run_oracle_audit(
            analysis_depth=args.analysis_depth,
            training_depth=args.training_depth,
            delta_threshold=args.delta_threshold,
            only_throws=args.only_throws,
            limit=args.limit,
            concurrency=args.concurrency,
            db_path=Path(args.db)
        )
    else:
        parser.print_help()


if __name__ == "__main__":
    main()
