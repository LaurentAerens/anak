# Anak Weakness Finder

The **Anak Weakness Finder** is an automated diagnostic and big-data failure analytics toolkit for the Anak Chess Engine. It identifies why Anak plays at ~2700+ strength from `startpos` but drops to ~2000 Elo when tested against opening book suites.

It automates matches against calibrated Stockfish, streams move evaluations into an embedded **DuckDB** columnar database, attributes failures to specific NNUE expert models or search bugs, and bridges findings into actionable fixes.

---

## Key Capabilities

1. **Failure Archetype Classification**:
   - **Thrown Wins**: Anak built a winning lead ($\ge +1.5$ cp) and threw it away.
   - **Cracked Defenses**: The position was equal/drawn, but Anak failed to hold and collapsed.
   - **Opening Squeezes**: Anak was put in deficit from the opening and never achieved equality.
2. **White vs. Black Color Disparity Engine**:
   - Quantifies the **Color Asymmetry Gap** ($\Delta_{\text{color}} = \text{Score}_{\text{White}} - \text{Score}_{\text{Black}}$).
   - Ranks the weakest Black defenses and flags NNUE models with elevated blunder rates when defending with Black.
3. **Full-Power Stockfish Oracle Deep Auditor**:
   - Detects positions where Anak and Opponent had massive evaluation disagreements.
   - Runs uncapped, full-strength Stockfish at depth 20–26 to establish objective ground truth and identify whether Anak's evaluation or move choice was flawed.

---

## Architecture Overview

```
tools/anak_weakness_finder/
├── main.py                  # Unified CLI entry point
├── run_weakness_ladder.py   # Automated Stockfish ladder (+200 Elo / 10k games default)
├── ingest_pgn.py            # Out-of-core streaming PGN ingestion engine
├── db_schema.py             # DuckDB schema, indexes, and analytical views
├── topology.py              # Bitboard topology classifier (Open, Closed, Endgame, etc.)
├── weakness_analyzer.py     # Loss archetypes, color metrics, relative severity & NNUE matrix
├── oracle_analyzer.py       # Full-Power Stockfish deep ground-truth auditor
├── report_generator.py      # ANSI console, GitHub Markdown, and interactive HTML dashboard
├── export_training_fens.py  # Bridge for targeted NNUE fine-tuning (Track 3)
└── test_case_generator.py   # Bridge for Alpha-Beta search regression testing (Track 1)
```

---

## The Single Command (Full-Chain Campaign with Auto-Resume)

To run the entire end-to-end pipeline in a single command:
1. Plays the stepped ladder matches (+200 Elo / 10k games per tier) until Anak scores $<50\%$.
2. Automatically streams all game PGNs into DuckDB.
3. Automatically triggers Full-Power Stockfish Oracle on candidate blind spots (evaluating at ST9 for training and ST20 for deep analysis).
4. Generates both Markdown and interactive HTML diagnostic dashboards.
5. Automatically exports `training/data/weakness_training_st9.epd` (ready for NNUE fine-tuning) and `repro_search_throws.py` (for Alpha-Beta bug verification).
6. **State Persistence**: If interrupted or crashed, simply running the exact same command resumes seamlessly from the current tier and unanalyzed positions.

```powershell
# Full campaign (Plays matches -> streams to DuckDB -> Oracle -> Reports -> Datagen):
.\.venv\Scripts\python -m tools.anak_weakness_finder.main run-all --start-elo 1600 --step-elo 200 --games 10000 --concurrency 12

# Reprocess existing data ONLY (Skips playing games; re-enriches features, runs Oracle, re-generates reports, and exports datagen):
.\.venv\Scripts\python -m tools.anak_weakness_finder.main run-all --reprocess-only
```

---

## Quick Start & Workflows

### 1. Ingest an Existing PGN Archive (Zero compute load)
```powershell
.\.venv\Scripts\python -m tools.anak_weakness_finder.main ingest "tools/fastchess/results/anak_vs_sf2000_20261025_162446.pgn" --elo 2000
```

### 2. Two-Stage Analytics Engine (Re-generate & Fine-tune Reports without replaying games)
Run the complete feature generation and pure declarative reporting pipeline in ~1 second directly on the database:
```powershell
# Full Stage 1 (Feature Enrichment) + Stage 2 (Declarative Reporting):
.\.venv\Scripts\python -m tools.anak_weakness_finder.main analyze

# Re-render HTML/Markdown reports in ~0.1s (tweaking report styling only):
.\.venv\Scripts\python -m tools.anak_weakness_finder.main analyze --report-only

# Run feature enrichment and audit 30 critical failure positions with Stockfish Oracle:
.\.venv\Scripts\python -m tools.anak_weakness_finder.main analyze --oracle-limit 30
```

### 3. Run Deep Ground-Truth Oracle on Blind Spots
Runs full-power Stockfish at depth 20 on the highest evaluation disagreements to see what the objective evaluation and best move really were:
```powershell
.\.venv\Scripts\python -m tools.anak_weakness_finder.main oracle --depth 20 --limit 30
```

### 4. Launch the Automated Stockfish Ladder (Run when Arena tournament finishes)
```powershell
.\.venv\Scripts\python -m tools.anak_weakness_finder.main ladder --start-elo 1600 --step-elo 200 --games 10000 --concurrency 12
```

### 5. Export Targeted Positions for Neural Network Retraining
Export positions specifically from Black defense losses or specific NNUE experts:
```powershell
.\.venv\Scripts\python -m tools.anak_weakness_finder.main export-fens --color black --expert Closed --out training/data/black_closed_weakness.epd
```
