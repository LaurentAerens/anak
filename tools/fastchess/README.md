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

### 1. Standard Self-Play Benchmark (1000 Games)
```cmd
.\run_elo_bench.bat
```
Plays 500 opening rounds with color reversal (1,000 games total) using openings from `openings.epd` at `3+0.05` time control.

### 2. Fast-Chess SPRT Match (Early Stopping on Statistical Significance)
```cmd
.\run_elo_bench.bat --sprt
```
Automatically stops as soon as a patch is statistically proven better or worse than the baseline within [0, 30] Elo bounds.

### 3. Quick Smoke Test (50 games at 3+0.05)
```cmd
.\run_elo_bench.bat --quick
```

### 4. Custom Match (e.g. 1000 games at 5+0.05 against v0.0.1)
```cmd
.\run_elo_bench.bat --baseline v0.0.1 --games 1000 --tc 5+0.05
```

### 5. Archive Current Build as a Baseline
```cmd
python tools\fastchess\run_elo_bench.py --archive-baseline v0.0.2 --elo 1680 --notes "Added new NNUE architecture"
```

---

## Command Line Options

| Option | Default | Description |
| :--- | :--- | :--- |
| `--games <N>` | `1000` | Total number of games to play (sets rounds = `N // 2` with color reversal). |
| `--rounds <N>` | `500` | Number of opening pairs (each played twice with colors swapped, `500` = 1,000 games). |
| `--opponent <type>` | `baseline` | Opponent type: `baseline` (self-play vs archived version) or `stockfish`. |
| `--baseline <tag>` | `latest` | Baseline version to benchmark against (e.g. `v0.0.1`, `latest`). |
| `--elo <N>` | `1800` | Target Stockfish Elo rating (when `--opponent stockfish`). |
| `--tc <time>` | `3+0.05` | Time control in seconds + increment (e.g. `3+0.05`, `5+0.1`, `10+0.1`). |
| `--concurrency <N>` | *CPU Cores* | Number of concurrent games running in parallel. |
| `--sprt` | `false` | Enable SPRT (Sequential Probability Ratio Test) [0, 30] Elo for early stopping. |
| `--quick` | `false` | Quick smoke test preset (25 rounds = 50 games at `3+0.05`). |
| `--hash <N>` | `64` | Transposition table hash size in MB per engine. |
| `--threads <N>` | `1` | Search threads per engine. |
| `--rebuild` | `false` | Force a rebuild of candidate `anak.exe` before the match. |
| `--archive-baseline <TAG>`| `None` | Archive current build into `tools/fastchess/baselines/<TAG>` and exit. |
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
