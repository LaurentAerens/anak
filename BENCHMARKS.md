# Anak Elo Benchmarks & Rating Progression

This document tracks the estimated Elo rating progression of Anak releases and provides instructions for running regression and progression benchmarks against previous versions or reference engines.

---

## 📊 Version Rating Progression

| Version | Release Date | Estimated Elo | 95% CI | Reference Opponent | Score | W / L / D | TC | PGN Archive |
| :--- | :---: | :---: | :---: | :--- | :---: | :---: | :---: | :--- |
| [**v0.0.1**](https://github.com/laerens/anak/releases/tag/v0.0.1) | 2026-10-19 | **1616** | &plusmn;215 | Stockfish 19 (1800 Elo) | 2 / 10 (20.0%) | 2 / 8 / 0 | 3+0.05 | [`anak_vs_sf1800_20261019_230409.pgn`](tools/fastchess/results/anak_vs_sf1800_20261019_230409.pgn) |
| [**v0.0.2**](https://github.com/laerens/anak/releases/tag/v0.0.2) | 2026-10-20 | **1616** | - | Baseline v0.0.1 | - (50.0%) | 0 / 0 / 0 | 3+0.05 | - |
| [**v0.0.3**](https://github.com/laerens/anak/releases/tag/v0.0.3) | 2026-10-20 | **1659** | &plusmn;23 | Stockfish 19 (1800 Elo) | 307.5 / 1000 (30.75%) | 297 / 682 / 21 | 3+0.05 | [`anak_vs_sf1800_20261020_152301.pgn`](tools/fastchess/results/anak_vs_sf1800_20261020_152301.pgn) |
| [**v0.0.4**](https://github.com/laerens/anak/releases/tag/v0.0.4) | 2026-10-23 | **1678** | &plusmn;18 | Anak v0.0.3 (Known Elo: 1659) | 527 / 1000 (52.7%) | 366 / 312 / 322 | 3+0.05 | [`anak_dev_vs_v0.0.3_20261023_204146.pgn`](tools/fastchess/results/anak_dev_vs_v0.0.3_20261023_204146.pgn) |
| [**v0.0.5-phase5**](https://github.com/laerens/anak/releases/tag/v0.0.5-phase5) | 2026-10-24 | **1725** | &plusmn;19 | Anak v0.0.4 (Known Elo: 1678) | 567 / 1000 (56.7%) | 478 / 344 / 178 | 1+0.01 | [`anak_dev_vs_v0.0.4_20261024_230511.pgn`](tools/fastchess/results/anak_dev_vs_v0.0.4_20261024_230511.pgn) |
| [**current_dev**](https://github.com/laerens/anak/releases/tag/current_dev) | 2026-10-26 | **2230** | &plusmn;30 | Stockfish Ladder (SF-2200 50.8%) | 4661 / 10000 (50.8%) | 4661 / 4502 / 837 | 1+0.01 | - |
| [**v0.0.6-transition-warmup**](https://github.com/laerens/anak/releases/tag/v0.0.6-transition-warmup) | 2026-10-26 | **2262** | &plusmn;6 | Anak current_dev (Known Elo: 2230) | 5456 / 10000 (54.56%) | 4642 / 3730 / 1628 | 1+0.01 | [`anak_dev_vs_current_dev_20261026_234700.pgn`](tools/fastchess/results/anak_dev_vs_current_dev_20261026_234700.pgn) |
| [**v0.0.7-rfp-capture-see**](https://github.com/laerens/anak/releases/tag/v0.0.7-rfp-capture-see) | 2026-10-27 | **2262** | &plusmn;6 | Anak current_dev (Known Elo: 2230) | 5457 / 10000 (54.57%) | 4678 / 3764 / 1558 | 1+0.01 | [`anak_dev_vs_current_dev_20261027_090821.pgn`](tools/fastchess/results/anak_dev_vs_current_dev_20261027_090821.pgn) |
| [**v0.0.8-syzygy-search**](https://github.com/laerens/anak/releases/tag/v0.0.8-syzygy-search) | 2026-10-27 | **2263** | &plusmn;6 | Anak current_dev (Known Elo: 2230) | 5473.5 / 10000 (54.73%) | 4684 / 3737 / 1579 | 1+0.01 | [`anak_dev_vs_current_dev_20261027_100859.pgn`](tools/fastchess/results/anak_dev_vs_current_dev_20261027_100859.pgn) |
| [**v0.0.9-unified-tt-tagging**](https://github.com/laerens/anak/releases/tag/v0.0.9-unified-tt-tagging) | 2026-10-27 | **2261** | &plusmn;6 | Anak current_dev (Known Elo: 2230) | 5442.5 / 10000 (54.43%) | 4631 / 3746 / 1623 | 1+0.01 | [`anak_dev_vs_current_dev_20261027_135801.pgn`](tools/fastchess/results/anak_dev_vs_current_dev_20261027_135801.pgn) |
| [**v1.0.0**](https://github.com/laerens/anak/releases/tag/v1.0.0) | 2026-10-27 | **2261** | &plusmn;6 | Anak current_dev (Known Elo: 2230) | 5442.5 / 10000 (54.43%) | 4631 / 3746 / 1623 | 1+0.01 | [`anak_dev_vs_current_dev_20261027_135801.pgn`](tools/fastchess/results/anak_dev_vs_current_dev_20261027_135801.pgn) |
| [**v1.0.1**](https://github.com/laerens/anak/releases/tag/v1.0.1) | 2026-10-27 | **2264** | &plusmn;6 | Anak v1.0.0 (Known Elo: 2261) | 5044.5 / 10000 (50.45%) | 4213 / 4124 / 1663 | 1+0.01 | [`anak_dev_vs_v1.0.0_20261027_182542.pgn`](tools/fastchess/results/anak_dev_vs_v1.0.0_20261027_182542.pgn) |

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
