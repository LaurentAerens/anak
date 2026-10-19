# Anak Fast-Chess Elo Benchmark Runner (Windows)

An automated tool to measure Anak's Elo rating compared to archived baseline versions of itself (self-benchmarking) or calibrated levels of **Stockfish 19** using [`fast-chess`](https://github.com/Disservin/fastchess).

---

## Quick Start (Windows)

Run the benchmark from the repository root using the pre-setup launcher:

```cmd
.\run_elo_bench.bat
```

Or run directly via Python:

```cmd
python tools\fastchess\run_elo_bench.py
```

### What Happens Automatically:
1. **Compiles Anak**: Publishes the latest Release build of `anak.exe` (Candidate / Dev build).
2. **Selects Opponent**:
   - By default, selects the latest archived baseline version in `tools/fastchess/baselines/` (e.g. `v0.0.1` at 1616 Elo).
   - If no baselines are archived, falls back to Stockfish 19 (1800 Elo).
3. **Executes Balanced Match**: Plays a tournament match using the 50-opening suite in `openings.epd` (with color reversal for fair testing).
4. **Computes Elo Rating**: Analyzes game results, prints a final Elo Evaluation Report with 95% confidence intervals, and writes game PGNs to `tools/fastchess/results/`.
5. **Tracks Version Progress**: Centralized tracking in [`BENCHMARKS.md`](../../BENCHMARKS.md) and [`tools/fastchess/ratings.json`](ratings.json).

---

## Usage Examples

### 1. Self-Play Smoke Test against Latest Baseline (10 games at 3s + 0.05s)
```cmd
.\run_elo_bench.bat --quick
```

### 2. Match against Specific Baseline Version (e.g. v0.0.1)
```cmd
.\run_elo_bench.bat --baseline v0.0.1 --rounds 10 --tc 10+0.1
```

### 3. Match against Stockfish Calibrated at 1800 Elo (20 games)
```cmd
.\run_elo_bench.bat --opponent stockfish --elo 1800 --rounds 10 --tc 10+0.1
```

### 4. Archive Current Build as a Baseline
```cmd
python tools\fastchess\run_elo_bench.py --archive-baseline v0.0.2 --elo 1680 --notes "Added new NNUE architecture"
```

### 5. Record Benchmark Match into Ratings Registry
```cmd
.\run_elo_bench.bat --quick --record v0.0.2 --notes "Eval search speedup test"
```

---

## Command Line Options

| Option | Default | Description |
| :--- | :--- | :--- |
| `--opponent <type>` | `baseline` | Opponent type: `baseline` (self-play vs archived version) or `stockfish`. |
| `--baseline <tag>` | `latest` | Baseline version to benchmark against (e.g. `v0.0.1`, `latest`). |
| `--elo <N>` | `1800` | Target Stockfish Elo rating (when `--opponent stockfish`). |
| `--rounds <N>` | `10` | Number of opening pairs (each played twice with colors swapped, so `10` = 20 games). |
| `--tc <time>` | `10+0.1` | Time control in seconds + increment (e.g. `10+0.1`, `5+0.1`, `3+0.05`). |
| `--concurrency <N>` | *CPU Cores* | Number of concurrent games running in parallel. |
| `--quick` | `false` | Shortcut preset for a fast 10-game test (5 rounds at `3+0.05`). |
| `--hash <N>` | `64` | Transposition table hash size in MB per engine. |
| `--threads <N>` | `1` | Search threads per engine. |
| `--rebuild` | `false` | Force a rebuild of candidate `anak.exe` before the match. |
| `--archive-baseline <TAG>`| `None` | Archive the current engine build into `tools/fastchess/baselines/<TAG>` and exit. |
| `--record <TAG>` | `None` | Record match results into `ratings.json` and `BENCHMARKS.md` under `<TAG>`. |
| `--notes <text>` | `""` | Notes when archiving or recording a version. |

---

## Output Report

At the conclusion of the match, an Elo report is displayed:

```text
======================================================================
                    ANAK ELO EVALUATION REPORT
======================================================================
  Reference Opponent:       Anak v0.0.1 (Known Elo: 1616)
  Total Games Played:       10
  Score:                    6 / 10  (Wins: 5, Losses: 3, Draws: 2)
  Performance Percentage:   60.00%
  Elo Difference:           +70.3 +/- 52.4 (95% CI)
  ------------------------------------------------------------------
  >>> ESTIMATED ANAK ELO:   1686 +/- 52 <<<
  ------------------------------------------------------------------
  PGN Game Archive:         tools\fastchess\results\anak_dev_vs_v0.0.1_20261019_231500.pgn
======================================================================
```
