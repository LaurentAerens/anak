# Anak v1.2.5 — Root Syzygy WDL Draw Filter & Zero-HalfmoveClock In-Tree Probing

**Release Date:** 2026-11-27  
**Category:** Endgame / Syzygy Optimization (Patch Release)  
**Estimated Elo:** **3265 &plusmn; 5** (`-1.39 ± 5.55` logistic Elo / `-1.91 ± 7.61` fastchess nElo vs `v1.2.4` in 8,000 games — within error margin)

## Summary

Fixes the **7×–32× NPS collapse** on reversible `<= 5`-piece endgame plies (`HalfmoveClock > 0`) identified by the Weakness Finder (`1.83M` NPS vs `57k` NPS) and adds a Root Syzygy WDL Draw Filter that prunes losing root blunders in drawn tablebase endgames while keeping winning conversions unconstrained.

## Benchmark Validation

- **Tier 2 Validation (`Anak v1.2.5` vs `Anak v1.2.4`, `1+0.01`, `Threads=1`, 8,000 games):**
  - **Score:** `3984.0 / 8000` (`49.80%` — `2586W / 2618L / 2796D`, `34.9%` draw rate)
  - **Elo:** `-1.39 ± 5.55` logistic Elo (`-1.91 ± 7.61` fastchess nElo, `31.19%` LOS — **+0 Elo** within error margin)
  - **PGN Archive:** [`anak_dev_vs_v1.2.4_20261127_085624.pgn`](../../tools/fastchess/results/anak_dev_vs_v1.2.4_20261127_085624.pgn)
- **Tier 1 Sanity (`Anak v1.2.5` vs `Anak v1.2.4`, `1+0.01`, 250 games):**
  - **Score:** `129.5 / 250` (`51.80%` — `85W / 76L / 89D`), `+12.51 ± 35.93` logistic Elo, `75.35%` LOS.

## Highlights

- **Zero-HalfmoveClock In-Tree Syzygy Gating (`AlphaBetaEngine.cs`):** Gates in-tree WDL probing on `board.HalfmoveClock == 0` with register-first `PopCount <= 7` pre-filtering so 50-move WDL semantics are exact and non-zeroing shuffling plies never stall on disk `.rtbw` I/O.
- **Root Syzygy WDL Draw Filter (`AlphaBetaEngine.cs`):** When a `<= MaxPieces` root position is a tablebase draw or blessed loss (`bestWdl <= 0`), filters out root moves that drop WDL tier while letting continuous NNUE search choose the most active/testing move among WDL-optimal candidates.
- **Regression Tests (`AlphaBetaTests.cs`):** Added `AlphaBeta_RootSyzygyWdlFilter_PrunesLosingMovesInDrawnEndgame` and `AlphaBeta_InTreeSyzygy_OnlyProbesOnZeroHalfmoveClock`.
