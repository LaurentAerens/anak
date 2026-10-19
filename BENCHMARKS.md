# Anak Elo Benchmarks & Rating Progression

This document tracks the estimated Elo rating progression of Anak releases and provides instructions for running regression and progression benchmarks against previous versions or reference engines.

---

## 📊 Version Rating Progression

| Version | Release Date | Estimated Elo | 95% CI | Reference Opponent | Score | W / L / D | TC | PGN Archive |
| :--- | :---: | :---: | :---: | :--- | :---: | :---: | :---: | :--- |
| [**v0.0.1**](https://github.com/laerens/anak/releases/tag/v0.0.1) | 2026-10-19 | **1616** | &plusmn;215 | Stockfish 19 (1800 Elo) | 2.0 / 10 (20%) | 2 / 8 / 0 | 3+0.05 | [`anak_vs_sf1800_20261019_230409.pgn`](tools/fastchess/results/anak_vs_sf1800_20261019_230409.pgn) |

> **Machine-Readable Registry**: All rating metadata is also stored in [`tools/fastchess/ratings.json`](tools/fastchess/ratings.json).

---

## ⚔️ Self-Benchmarking (Candidate vs Baseline)

To measure improvements or prevent regressions, Anak benchmarks current development builds directly against archived baseline executables stored locally in `tools/fastchess/baselines/`.

### Run Self-Play Benchmark (Current vs Latest Baseline)
```cmd
# Standard full match (1000 games: 500 opening pairs with color reversal)
.\run_elo_bench.bat

# Standard match with SPRT early stopping (elo0=0, elo1=30)
.\run_elo_bench.bat --sprt

# Quick smoke test (50 games at 3+0.05)
.\run_elo_bench.bat --quick

# Custom game count and baseline
.\run_elo_bench.bat --baseline v0.0.1 --games 1000 --tc 3+0.05
```

### Calibrate against Stockfish
To re-calibrate against a fixed reference engine (Stockfish 19 with UCI limit strength):
```cmd
.\run_elo_bench.bat --opponent stockfish --elo 1800 --games 1000
```

---

## 📦 Archiving a New Baseline

When releasing a new version or significant milestone, archive the build as a baseline:
```cmd
# Build, store in tools/fastchess/baselines/<version>, and register in ratings.json & BENCHMARKS.md:
python tools\fastchess\run_elo_bench.py --archive-baseline v0.0.2
```
