"""
Test Case Generator for Alpha-Beta Search Bugs.
Extracts single-ply tactical throws from DuckDB and generates reproducible
diagnostic scripts and C# unit tests.
"""

import argparse
from pathlib import Path
from typing import Optional
from .db_schema import get_connection, initialize_schema


def generate_python_repro_script(output_path: Path, limit: int = 20, db_path: Optional[Path] = None):
    """Generates an executable Python repro script to test Anak on the worst throw positions."""
    conn = get_connection(db_path)
    initialize_schema(conn)

    rows = conn.execute("""
        SELECT 
            p.game_id, g.opening_name, p.move_number, p.side_to_move,
            p.move_san, p.fen_before, p.anak_eval, p.eval_delta, p.active_nnue_expert
        FROM plies p
        JOIN games g ON p.game_id = g.game_id
        WHERE p.is_throw = TRUE
        ORDER BY p.eval_delta ASC
        LIMIT ?;
    """, (limit,)).fetchall()

    if not rows:
        print("[!] No tactical throws found in database to generate repro scripts.")
        return

    script_lines = [
        "#!/usr/bin/env python3",
        '"""',
        "Auto-generated Alpha-Beta Search Regression Repro Suite.",
        "Executes Anak micro-searches on critical blunder positions to diagnose pruning/horizon bugs.",
        '"""',
        "",
        "import subprocess",
        "import sys",
        "from pathlib import Path",
        "",
        "REPO_ROOT = Path(__file__).resolve().parent.parent.parent",
        'TEST_FEN_SCRIPT = REPO_ROOT / "tools" / "test_fen.py"',
        "",
        "CRITICAL_POSITIONS = ["
    ]

    for r in rows:
        game_id, opening, m_num, side, move, fen, eval_val, delta, expert = r
        script_lines.append(f"    {{")
        script_lines.append(f'        "game": "{game_id}",')
        script_lines.append(f'        "opening": "{opening}",')
        script_lines.append(f'        "move_num": {m_num},')
        script_lines.append(f'        "blunder_move": "{move}",')
        script_lines.append(f'        "eval_drop": {delta},')
        script_lines.append(f'        "expert": "{expert}",')
        script_lines.append(f'        "fen": "{fen}",')
        script_lines.append(f"    }},")

    script_lines.extend([
        "]",
        "",
        "def main():",
        '    print("=" * 70)',
        '    print("    RUNNING ANAK ALPHA-BETA SEARCH REGRESSION VERIFICATION")',
        '    print("=" * 70)',
        "    for i, pos in enumerate(CRITICAL_POSITIONS, 1):",
        '        print(f"\\n[{i}/{len(CRITICAL_POSITIONS)}] Testing {pos[\'opening\']} (Move {pos[\'move_num\']})")',
        '        print(f"    FEN: {pos[\'fen\']}")',
        '        print(f"    Blunder in Match: {pos[\'blunder_move\']} (Eval Drop: {pos[\'eval_drop\']} cp)")',
        "        if TEST_FEN_SCRIPT.exists():",
        '            cmd = [sys.executable, str(TEST_FEN_SCRIPT), "--fen", pos["fen"], "--depth", "12"]',
        "            subprocess.run(cmd)",
        "",
        'if __name__ == "__main__":',
        "    main()",
        ""
    ])

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text("\n".join(script_lines), encoding="utf-8")
    print(f"[+] Python search regression script generated: {output_path}")


def main():
    parser = argparse.ArgumentParser(description="Generate Alpha-Beta regression repro scripts")
    parser.add_argument("--output", type=str, default="tools/anak_weakness_finder/repro_search_throws.py", help="Output repro script path")
    parser.add_argument("--limit", type=int, default=25, help="Number of worst throws to include")

    args = parser.parse_args()
    generate_python_repro_script(Path(args.output), limit=args.limit)


if __name__ == "__main__":
    main()
