# Anak Fast-Chess Elo Benchmark Runner (Windows)

An automated, one-command tool to measure Anak's Elo rating compared to calibrated levels of **Stockfish 19** using [`fast-chess`](https://github.com/Disservin/fastchess).

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
1. **Compiles Anak**: Publishes the latest Release build of `anak.exe`.
2. **Auto-Downloads Binaries**: Automatically fetches official Windows x64 builds of `fast-chess.exe` and `stockfish.exe` if not already present in `tools/fastchess/bin/`.
3. **Executes Balanced Match**: Plays a tournament match using the 50-opening suite in `openings.epd` (with color reversal for fair testing).
4. **Computes Elo Rating**: Analyzes game results and prints a final Elo Evaluation Report with 95% confidence intervals and writes all game PGNs to `tools/fastchess/results/`.

---

## Usage Examples

### 1. Fast Smoke Test (10 games at 3s + 0.05s)
```cmd
.\run_elo_bench.bat --quick
```

### 2. Match against Stockfish calibrated at 1600 Elo (20 games)
```cmd
.\run_elo_bench.bat --elo 1600 --rounds 10 --tc 10+0.1
```

### 3. Match against Stockfish calibrated at 2000 Elo (40 games)
```cmd
.\run_elo_bench.bat --elo 2000 --rounds 20 --tc 10+0.1
```

### 4. Adjust Parallelism / Concurrency
By default, games run concurrently across available CPU cores:
```cmd
.\run_elo_bench.bat --concurrency 4
```

---

## Command Line Options

| Option | Default | Description |
| :--- | :--- | :--- |
| `--elo <N>` | `1800` | Target Stockfish Elo rating (range: 1320–3190). |
| `--rounds <N>` | `10` | Number of opening pairs (each played twice with colors swapped, so `10` = 20 games). |
| `--tc <time>` | `10+0.1` | Time control in seconds + increment (e.g. `10+0.1`, `5+0.1`, `3+0.05`). |
| `--concurrency <N>` | *CPU Cores* | Number of concurrent games running in parallel. |
| `--quick` | `false` | Shortcut preset for a fast 10-game test (5 rounds at `3+0.05`). |
| `--hash <N>` | `64` | Transposition table hash size in MB per engine. |
| `--threads <N>` | `1` | Search threads per engine. |
| `--rebuild` | `false` | Force a rebuild of `anak.exe` before the match. |

---

## Output Report

At the conclusion of the match, an Elo report is displayed:

```text
======================================================================
                    ANAK ELO EVALUATION REPORT
======================================================================
  Reference Opponent:       Stockfish 19 (Calibrated Elo: 1800)
  Total Games Played:       20
  Score:                    11.5 / 20  (Wins: 9, Losses: 6, Draws: 5)
  Performance Percentage:   57.50%
  Elo Difference:           +52.4 +/- 48.1 (95% CI)
  ------------------------------------------------------------------
  >>> ESTIMATED ANAK ELO:   1852 +/- 48 <<<
  ------------------------------------------------------------------
  PGN Game Archive:         tools\fastchess\results\anak_vs_sf1800_20261019_145500.pgn
======================================================================
```
