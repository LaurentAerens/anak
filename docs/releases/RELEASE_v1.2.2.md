# Anak v1.2.2 — Zero-Search-Impact NNUE, Core, QS & Search NPS Optimizations (+11.85% NPS)

**Release Date:** 2026-11-25  
**Category:** Performance / Throughput (Patch Release)  
**Estimated Elo:** **3264 &plusmn; 5** (+0 Elo vs `v1.2.0` — within error margin in 8,000 games)

## Summary

Delivers a **+11.85% NPS** throughput improvement across NNUE inference/accumulator updates, Core bitboard/SEE/Board operations, Quiescence Search, and Alpha-Beta move picking/TT probing without altering search behavior.

## Benchmark Validation

- **Match:** `Anak v1.2.2` vs `Anak v1.2.0` (`1+0.01`, 8,000 games, `openings.epd`, `concurrency=14`)
- **Score:** `4030.5 / 8000` (`50.38%` — `2675W / 2614L / 2711D`, `33.9%` draw rate)
- **Elo:** `+0.83 ± 5.38` nElo (`+2.65 ± 6.19` logistic Elo; within error margin → **+0 Elo** at `3264`)
- **PGN Archive:** [`anak_dev_vs_v1.2.0_20261124_225534.pgn`](../../tools/fastchess/results/anak_dev_vs_v1.2.0_20261124_225534.pgn)

## Highlights

- **NNUE Inference & Accumulators (`MultiTierNnueEvaluator.cs`, `NnueAccumulator.cs`, `NnueInference.cs`):** Zero-search-impact SIMD and accumulator hot-path optimizations.
- **Core (`Bitboard.cs`, `Board.cs`, `SEE.cs`):** Faster bitboard, board state, and Static Exchange Evaluation hot paths.
- **Search & QS (`AlphaBetaEngine.cs`, `MovePicker.cs`, `QuiescenceSearch.cs`, `TranspositionTable.cs`):** Lower overhead in move picking, quiescence search, and TT access.
