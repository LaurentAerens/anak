# Anak `v1.2.0` — Milestone Release Notes (`v1.1.0` → `v1.2.0`)

**Anak `v1.2.0`** is a major milestone release combining a ground-up **ArchV2 (`0x0004`) Pairwise-1024 INT16 NNUE** architecture and native CUDA training pipeline with **22 patch releases (`v1.1.1` → `v1.1.22`)** of search, move ordering, time management, SIMD throughput, and Lazy SMP improvements since `v1.1.0` (2026-11-17).

---

## Strength & Elo Progression (`v1.1.0` → `v1.2.0`)

- **Estimated Single-Thread Rating:** **3264 ± 18 Elo** (Ordo CCRL-anchored)
- **Total Cumulative Gain Since `v1.1.0` (`2965` Elo):** **+299 Elo (1T)** (+ **+10.33 Elo** additional Lazy SMP scaling at `Threads=4` in `v1.1.21`)
  - **`v1.1.0` → `v1.1.22` (22 patch releases):** **+211 Elo** (`2965` → `3176` Elo)
  - **`v1.1.22` → `v1.2.0-candidate` (`ArchV2` Pairwise-1024 INT16 NNUE + Finny Tables):** **`+81.69 ± 17.07` logistic Elo** (`+102.08 ± 19.05` fastchess nElo, `100.00% LOS`, `644W / 348L / 290D`, `61.54%`, `LLR = +2.95`, SPRT `[0.0, 3.0]` H1 accepted in `1,282` games at `1+0.01`)
  - **`v1.2.0-candidate` → `v1.2.0` (SPSA `CorrectionHistory` Retuning):** **`+5.91 ± 5.72` logistic Elo** (`+7.86 ± 7.61` fastchess nElo, `97.85% LOS`, `2,663W / 2,527L / 2,810D`, `50.85%` in `8,000` games at `1+0.01`)

---

## 1. Headline `v1.2.0` Breakthrough: `ArchV2` NNUE, Finny Tables & CUDA Trainer (`+87.6 Elo` vs `v1.1.22`)

### ArchV2 (`0x0004`) Pairwise-1024 INT16 NNUE (`+81.69 ± 17.07` Elo)
Replaced the legacy 6-expert INT8 MoE NNUE with a single, clean, high-capacity **Pairwise-1024 INT16** architecture (`networks/anak.nnue`), fixing six root-cause bottlenecks identified in the v1 architecture:
- **True Horizontal Mirroring (32 King Buckets):** Mirrors both king and piece squares (`sq ^ 7`) whenever the perspective king is on files E–H, eliminating asymmetric king-bucket feature collisions.
- **INT16 Feature Transformer (`QA = 255`, `QB = 64`, `V2OutputScale = 200`):** Eliminates the INT8 `WeightScale = 64` SCReLU dead-zone that previously zeroed small activations, calibrated directly to Anak's `100 cp = 1 pawn` search margin scale.
- **Pairwise Multiplication + 8 Material-Phase Output Buckets:** `(768 × 32 HM) -> 1024×2 -> Pairwise-Mul(1024) -> 8` material-phase output heads (`(popcount - 2) / 4`), replacing discontinuous 6-expert MoE routing cliffs.
- **Finny Tables (`NnueFinnyTable.cs`):** Per-worker `[2 × 32]` king-bucket accumulator cache that computes incremental deltas against the cached king-bucket state instead of full refreshes on king-bucket transitions.
- **Clean ArchV2-Only Codebase:** Completely removed all legacy v1 (`ArchStandard`/`ArchMoE`) inference code and deleted the 6 legacy `.nnue` expert files (`attack`, `closed`, `endgame`, `imbalance`, `open`, `queenless`) to keep the engine simple and maintainable.

### Native RTX 4090 CUDA Trainer & C++20 Binpack Pipeline
- **`training/cuda_v2/train_v2.cu`:** Custom CUDA trainer running at **2.34M positions/sec** on an RTX 4090 (~5× faster than PyTorch `EmbeddingBag`), featuring Factorized Feature Transformers folded at export, GPU EMA (`0.999`), pure WDL/sigmoid loss (removing double-blended WDL and Huber conflicts), and sparse FT updates without inactive-row AdamW decay.
- **`training/cpp_binpack/sf_binpack_to_v2.cpp`:** High-throughput C++20 binpack unpacker (`4.1M positions/sec`) with round-robin multi-archive interleaving, raw `0/1/2` WDL preservation, `ply >= 16` filtering, and clean capture/check/EP/promotion filtering.
- **Architecture Sizing Validated:** `HL = 1024` Pairwise beat `HL = 1536` Pairwise by `+64.7 Elo` at STC due to L3 cache / accumulator bandwidth efficiency.

### SPSA Post-NNUE Retuning (`+5.91 ± 5.72` Elo)
- Validated three SPSA batches (`core`, `pruning`, `corr`) via 8,000-game matches at `1+0.01` against `v1.2.0-candidate`:
  - **`--batch corr` (Accepted — `+5.91 ± 5.72` Elo, `97.85% LOS`):** `TunedCorrHistDivisor` `2028 -> 2031`, `TunedCorrHistMax` `110 -> 126` (`SearchHeuristics.cs`).
  - **`--batch core` & `--batch pruning` (Rejected & Reverted):** `-2.82 ± 5.86` Elo and `-0.48 ± 5.68` Elo over 8,000 games each.

---

## 2. All Search, Evaluation, Throughput & SMP Gains Since `v1.1.0` (`+211 Elo`)

### Move Ordering, History & Correction History (`+139.5 Elo`)
- **History Bonus Scaling Overhaul (`v1.1.8` — `+99.80 ± 8.88` Elo):** Fixed `HistoryTable.CalculateBonus(depth)` from `depth * depth` to `Math.Min(Math.Max(0, 300 * depth - 250), MaxBonus)` and fixed `_captureHistory.Update` to apply `-malus` on failed captures.
- **Multi-Component `CorrectionHistory` & Tactical Filtering (`v1.1.12` — `+38.81 ± 6.02` Elo):** Skipped `CorrectionHistory.Update` on capture/promotion cutoffs and added White/Black Non-Pawn Correction History plus 1-ply Continuation Correction History.
- **Blended Quiet Move Scoring (`v1.1.2` — `+4.07` paired nElo / `+0.87` logistic Elo):** Blended 1-ply + 2-ply continuation history with Butterfly history in `MovePicker.ScoreQuiets()`.

### Time Management (`+28.3 Elo`)
- **Best-Move Node-Fraction Soft-Limit Scaling (`v1.1.14` — `+22.79 ± 5.38` Elo):** Smoothly scales `targetTime` at `depth >= 6` based on root best-move node fraction (`Math.Clamp((1.5 - bestMoveFraction) * 1.35, 0.55, 1.65)`).
- **Single-Legal-Move Time Cap (`v1.1.13` — `+4.43 ± 5.09` Elo):** Caps forced single-move searches to `5ms` soft / `10ms` hard limit.
- **`TimeManager` Low-Time Invariant & Fail-Low Preservation (`v1.1.7` — `+1.09 ± 5.80` Elo):** Clamped `_softLimitMs < _hardLimitMs` everywhere and prevented move-stability contraction from erasing fail-low extensions.

### Search Pruning, Extensions, LMR & Evaluation (`+20.5 Elo`)
- **50-Move Rule (`HalfmoveClock`) Drawishness Scaling (`v1.1.19` — `+4.52 ± 5.77` Elo):** Linearly scales leaf NNUE eval by `(200 - HalfmoveClock) / 200` to dampen fortress shuffling.
- **Deeper / Shallower LMR Re-Search (`v1.1.10` — `+2.87 ± 5.96` Elo):** Adjusts LMR re-search depth by `±1` based on fail-high margin.
- **Aspiration Fail-High Move Preservation & Delta Optimization (`v1.1.11` — `+2.82 ± 5.70` Elo):** Preserves `iterBestMove` on root fail-high, widens directly to `InfiniteValue` past `delta > 500`, and reduces re-search depth by `failHighCount`.
- **Continuous History-Scaled LMR (`v1.1.9` — `+1.52 ± 5.91` Elo):** Replaced binary `±1` LMR thresholds with proportional `(hist + conthist1 + conthist2) / 8192` scaling.
- **ProbCut Tactical Pruning (`v1.1.22` — `+1.48 ± 5.39` Elo):** Prunes non-PV, non-check nodes at `depth >= 5` using SEE-filtered captures verified by QSearch and `depth - 4` search against `beta + 180`.
- **Core Search & SEE Bugfixes (`v1.1.3`–`v1.1.6` — `+21.2 Elo` combined):**
  - Root `StaticEval` initialization & `SearchStack.Reset()` sentinel fix (`v1.1.3` — `+9.25 ± 7.36` Elo).
  - SEE promotion attacker piece value fix (`v1.1.4` — `+5.86 ± 5.89` Elo).
  - Preserve deeper `TTEntry.BestMove` on shallow/QS TT updates (`v1.1.5` — `+3.74 ± 6.01` Elo).
  - Singular extensions mate-score guard, stop check, and ply cap (`v1.1.6` — `+2.35 ± 5.74` Elo).
  - `Board.IsLegal` validation hardening (`v1.1.8`, zero-regression correctness fix).

### Lazy SMP Multi-Threading & Correctness (`v1.1.21` — `+10.33 ± 7.51` Elo at `Threads=4`)
- **Lazy SMP Overhaul:** Added Stockfish-style helper depth-skip diversity, per-helper aspiration windows, and `SelectBestThread` weighted voting across workers.
- **Bundled Correctness Fixes:** Enforced mid-search `_nodeLimit` + QS node accounting, fixed 50-move mate precedence & `_gamePly` balance past 1024 plies, and fixed Syzygy `tbScore` (`±(20000 - ply)`) + `TbHits` reporting.

### Engine Throughput (NPS) & SIMD (`v1.1.1`, `v1.1.15`–`v1.1.18`, `v1.1.20`)
- **Bitboard Bulk Pawn Move Generation (`v1.1.20` — `+3.13 ± 5.86` Elo, `+3%–5%` NPS):** Parallel bitboard shift generation for unpinned pawns.
- **AVX-512BW / NEON SIMD, Native Aligned Accumulator Stack & Zero-Copy Null Move (`v1.1.18`):** 512-bit AVX-512BW & 128-bit NEON SIMD paths, 64-byte aligned `NativeMemory` accumulator stack, and `_stackSlot` zero-copy `PushNullMove`.
- **Sparse `NNZ` Transposed NNUE L1 Matmul (`v1.1.17` — `+4.1%` NPS):** Non-zero chunk gathering via `Avx.MoveMask` (superseded by `ArchV2`'s Pairwise-1024 direct output layer in `v1.2.0`).
- **Core Movegen, `MagicBitboards`, SEE & History NPS (`v1.1.1`, `v1.1.15`, `v1.1.16` — `+4.04` Elo + `~3%` NPS):** 16-byte `PextEntry` / 32-byte `MagicEntry`, ray-mask pre-filtering, `blsi` SEE, zero-magic direct `GivesCheck`, incremental quiet generation in `MovePicker`, `short[]` history tables, and bit-identical core simplifications.

---

## Summary Table (`v1.1.0` → `v1.2.0`)

| Milestone / Patch Range | Key Changes | Elo Gain (1T) | Estimated Rating |
| :--- | :--- | :---: | :---: |
| **`v1.1.0`** | Baseline milestone (Ordo CCRL-anchored) | — | **2965 ± 21** |
| **`v1.1.1` – `v1.1.7`** | NPS simplifications, blended quiets, root `StaticEval`, SEE promo, TT `BestMove`, singular/TM fixes | +27 Elo | **2992 ± 6** |
| **`v1.1.8`** | History bonus scaling overhaul + `Board.IsLegal` fix | +100 Elo | **3092 ± 9** |
| **`v1.1.9` – `v1.1.14`** | Continuous/deeper LMR, aspiration fail-high, multi-table `CorrectionHistory`, node-fraction & single-move TM | +74 Elo | **3166 ± 5** |
| **`v1.1.15` – `v1.1.22`** | Movegen/Magic/SIMD NPS, 50-move scaling, bulk pawns, Lazy SMP (`+10.3 Elo` @ 4T), ProbCut | +10 Elo | **3176 ± 5** |
| **`v1.2.0`** | **`ArchV2` Pairwise-1024 INT16 NNUE + Finny Tables + CUDA Trainer + SPSA `corr`** | **+88 Elo** | **3264 ± 18** |
