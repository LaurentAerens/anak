# Anak v1.2.4 — Quiescence Search Time-Up TT Corruption Fix & Exact QS Node Accounting

**Release Date:** 2026-11-27  
**Category:** Bugfix / Search Correctness (Patch Release)  
**Estimated Elo:** **3265 &plusmn; 5** (`+1.22 ± 5.69` logistic Elo / `+1.63 ± 7.61` fastchess nElo vs `v1.2.3` in 8,000 games)

## Summary

Fixes a Transposition Table corruption defect triggered when `QuiescenceSearch` expired its hard time limit mid-tree (previously responsible for `122` exact `0.00` evaluation blunders in the 90,000-game Weakness Finder campaign), eliminates aborted PVS re-searches, and fixes `2×` double-counting of `_qsNodes`.

## Benchmark Validation

- **Tier 2 Validation (`Anak v1.2.4` vs `Anak v1.2.3`, `1+0.01`, `Threads=1`, 8,000 games):**
  - **Score:** `4014.0 / 8000` (`50.18%` — `2585W / 2557L / 2858D`, `35.7%` draw rate)
  - **Elo:** **`+1.22 ± 5.69` logistic Elo** (`+1.63 ± 7.61` fastchess nElo / `+0.66 ± 5.38` `elo_kit` nElo, `66.24%` LOS, `Ptnml(0-2): [335, 868, 1578, 872, 347]`)
  - **PGN Archive:** [`anak_dev_vs_v1.2.3_20261126_225113.pgn`](../../tools/fastchess/results/anak_dev_vs_v1.2.3_20261126_225113.pgn)
- **Tier 1 Sanity (`Anak v1.2.4` vs `Anak v1.2.3`, `1+0.01`, 250 games):**
  - **Score:** `128.5 / 250` (`51.40%` — `86W / 79L / 85D`), `+9.73 ± 34.00` logistic Elo, `1` timeout vs `5`.

## Highlights

- **Latching `_stopped` on Hard Time Expiration (`AlphaBetaEngine.cs`):** `IsTimeUp()` now latches `_stopped = true` immediately when `TimeManager.IsHardTimeUp()` fires and skips redundant LMR/PVS re-search calls during unwind.
- **Prevent Poisoned `0.00` TT Writes on QS Abort (`QuiescenceSearch.cs`):** Replaced the candidate-loop `break;` on time expiration (which previously fell through to `_tt.Store`) and added `_aborted` propagation across recursive `QuiescenceSearch` frames so timed-out `0` scores can never trigger false `0 >= beta` `LowerBound` or partial `Exact`/`UpperBound` TT entries.
- **Exact Single-Counted QS Nodes (`QuiescenceSearch.cs`, `AlphaBetaTests.cs`):** Removed the duplicate entry-level `++_qsNodes` increment so each quiescence capture/evasion move is counted once at `MakeMove`, preserving 100% bit-identical fixed-depth `BestMove` and `ScoreCp` across the 64-position equivalence suite.
