# Anak Chess Engine — Official Release Notes

This document provides a consolidated history of all official releases, validated optimizations, benchmark results, and architectural advancements in the Anak Chess Engine.

> **Current Active Version:** `v1.1.2` (Estimated Rating: **2969 Elo — Ordo CCRL-Anchored**)  
> **Benchmark Archive & Progression:** [`BENCHMARKS.md`](../BENCHMARKS.md)  
> **Benchmarking Protocol:** [`docs/BENCHMARKING_POLICY.md`](../BENCHMARKING_POLICY.md)  
> **Failed Experiments & Archive:** [`docs/FAILED_IDEAS.md`](../FAILED_IDEAS.md)

---

## 🌟 Releases & Optimization History

### `v1.1.2` (2026-11-19) — Blended Quiet Move Scoring (CPW 4.1 Revisit)
- **Category:** Move Ordering (Patch Release)
- **Estimated Elo:** **2969 &plusmn; 5** (`+4.07 ± 5.38` paired nElo / `+0.87 ± 6.21` logistic Elo over `v1.1.1` in 8,000 games at `1+0.01`: `2670W / 2650L / 2680D`, `50.12%`, `61.14% LOS`)
- **Highlights:**
  - **Blended Quiet Scoring in `MovePicker.ScoreQuiets()`:** Preserves the positive `ContinuationMoves` stage while blending `1-ply + 2-ply` continuation history with Butterfly history in `ScoreQuiets()`, properly demoting quiet moves with negative continuation history.

### `v1.1.1` (2026-11-18) — Bit-Identical Code & Math Simplification, NPS Gains & Book Retuning
- **Category:** Zero-Regression Throughput & Maintainability (Patch Release)
- **Estimated Elo:** **2965 &plusmn; 6** (`+4.04 ± 5.92` Elo over `v1.0.32` in 8,000 games at `1+0.01`: `2747W / 2654L / 2599D`, `50.58%`, `90.95% LOS`)
- **Highlights:**
  - **100% Bit-Identical NPS & Core Simplifications (`f1df032`, `7c39995`):** Branchless `Move.IsCapture` / `PromotionPieceType`, `BitOperations.Log2` in `Bitboard.Msb`, `Unsafe.Add` piece/color indexing and `(int)to ^ 8` EP victim in `Board`/`SEE`, `[SkipLocalsInit]` and `(opennessPct - low) << 4` in `MultiTierNnueEvaluator`, cached `OpenThresholdPct` / `AttackMinOpennessPct` in `ExpertRouting`, `SubSubAddAddFeature` for `Buckets == 1` castling, fast threshold swap loop in `SEE.HasPositiveOrEqualSee`, `knownInCheck: 0` passed to `QuiescenceSearch`, `GivesCheckCached` in `AlphaBeta` pre-move pruning, hoisted `ContinuationHistory.GetBaseOffset`, and `pvBoard.IsLegal` in `ExtractPv` (+2.17% `bench 12` NPS / +2.70% over 10k fixed-node positions with 0/10,000 mismatches).
  - **Code Cleanup (`7d63546`, `094bc3c`):** Precomputed `PassedPawnMasks` & `AdjacentFileMasks` in `DefaultEvaluator`, deduplicated `MovePicker.MarkSpecialMovesPicked` and `TTEntry.ClampEval`, and removed unused `SubFeature` / heuristic margin helpers.
  - **PolyGlot Book Fix (`2a7f9b2`):** Retuned `book.bin` from clean baseline without blunder-pruning draws.

### `v1.1.0` (2026-11-17) — Milestone Release (`v1.0.0` → `v1.0.33` Consolidation)
- **Category:** Major Milestone Release (Full Notes: [`RELEASE_v1.1.0.md`](RELEASE_v1.1.0.md))
- **Estimated Elo:** **2965 &plusmn; 21** (Ordo CCRL-anchored; **+497 Elo** cumulative chained self-play gain vs. `v1.0.0`)
- **Highlights:** Consolidates 33 patch releases since `v1.0.0`: Unified Shared-FT MoE NNUE & embedded NativeAOT binaries, Correction History overhaul (`+204.5 Elo`), 2.21× + 14.1% NPS throughput gains, QSearch checks/repetition/TT refinement, improving-aware LMP, deeper RFP, fail-low time extension, 59,400-game SPSA tuning, and `book.bin` calibration.

### `v1.0.33` (2026-11-17) — SPSA Tuning, Opening Book Calibration & Ordo Elo Re-Anchoring
- **Category:** Search Parameter Tuning, Opening Book Calibration & Rating Re-Anchoring (Patch Release)
- **Estimated Elo:** **2965 &plusmn; 21** (Ordo logistic rating anchored against 5 real CCRL engines over 1,105 non-timeout games at `5+0.05`)
  > **Note on Elo Re-Anchoring:** There is **no +211 Elo jump** in playing strength in this release. `v1.0.33` re-anchors Anak's rating baseline from chained self-play (`2754` in `v1.0.32`) to an external multi-opponent Ordo CCRL gauntlet (`Berserk-4.7.0`, `Laser-1.7`, `Weiss-2.0`, `Ethereal-13.00`, `Koivisto-8.0`). Relative to `v1.0.32`, the SPSA-tuned parameters measured **+4.00 Elo** (`50.58%`, `3,040W / 2,948L / 2,012D`, `89.21% LOS`) over 8,000 games at `1+0.01`.
- **Highlights:**
  - **Wired & Tuned 6 New Pruning UCI Options (`AlphaBetaEngine.cs`, `SearchHeuristics.cs`, `UciOption.cs`, `spsa.py`):** Exposed `FutilityMarginD1D2`, `FutilityMarginD3`, `LmpBase`, `LmpMultiplier`, `SeeCaptureMargin`, and `SeeQuietMargin` as UCI `SpinOption`s and added `--batch pruning` to `tools/tuning/spsa.py`.
  - **59,400-Game SPSA Tuning (`0.5+0.005`):**
    - `--batch core` (29,700 games): `LmrDivision` `224 -> 228`, `RfpMargin` `77 -> 74`, `AspirationDelta` `20 -> 19`, `HistoryThreshold` `2130 -> 2139`.
    - `--batch pruning` (29,700 games): `FutilityMarginD3` `120 -> 124`, `LmpBase` `3 -> 4`, `SeeCaptureMargin` `60 -> 61`, `SeeQuietMargin` `35 -> 34`.
  - **PolyGlot Opening Book Calibration (`book.bin`):** Reinforced from 600 live `startpos` games (`5+0.05`, `OwnBook=true`, 4,800 move updates) and pruned 55,295 low-win-rate entries (`< 25%` over `20+` games), leaving 37,998 entries across 33,988 positions (`e4` 90%, `d4` 10% at root).

### `v1.0.32` (2026-11-16) — Reduce LMR by 1 for CounterMoves
- **Category:** Search Extensions & Reductions (Patch Release)
- **Estimated Elo:** **2754 &plusmn; 7** (+6.38 Elo / +7.46 nElo over v1.0.31 in 8,000 games, 97.26% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,197 wins, 3,050 losses, 1,753 draws (50.92% score, 0 crashes).
- **Highlights:**
  - **CounterMove LMR Reduction (`AlphaBetaEngine.cs`):** Reduced Late Move Reduction by 1 ply when the quiet move is from `MovePickerStage.CounterMove`, preserving tactical refutation depth for strong countermoves.

### `v1.0.31` (2026-11-16) — Extend Reverse Futility Pruning (RFP) to Depth <= 6
- **Category:** Search Pruning Efficiency (Patch Release)
- **Estimated Elo:** **2748 &plusmn; 6** (+9.08 Elo / +10.65 nElo over v1.0.30 in 8,000 games, 99.70% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,242 wins, 3,033 losses, 1,725 draws (51.31% score, 0 crashes).
- **Highlights:**
  - **Deeper Reverse Futility Pruning (`AlphaBetaEngine.cs`):** Extended Reverse Futility Pruning (Static Null Move Pruning) from `depth <= 4` to `depth <= 6`, pruning clearly winning non-PV nodes 2 plies higher in the search tree.

### `v1.0.30` (2026-11-16) — Refine Quiescence Stand-Pat with TT LowerBound
- **Category:** Search Bounds & Quiescence Efficiency (Patch Release)
- **Estimated Elo:** **2739 &plusmn; 6** (+9.30 Elo / +11.07 nElo over v1.0.29 in 8,000 games, 99.78% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,284 wins, 3,070 losses, 1,646 draws (51.34% score, 0 crashes).
- **Highlights:**
  - **QS Stand-Pat TT Refinement (`QuiescenceSearch.cs`):** Refined `standPat` using `ttScore` when a matching-expert `TTFlags.LowerBound` entry exceeds `standPat`, tightening `alpha` and delta pruning bounds in quiescence search.

### `v1.0.29` (2026-11-16) — Improving-Aware Late Move Pruning (LMP)
- **Category:** Search Pruning Efficiency (Patch Release)
- **Estimated Elo:** **2730 &plusmn; 7** (+15.56 Elo / +17.69 nElo over v1.0.28 in 7,352 games, 100.00% LOS, SPRT `[0, 3]` H1 accepted)
- **Validation:** SPRT `[0.0, 3.0]` at STC (`1+0.01`): 3,103 wins, 2,774 losses, 1,475 draws (52.24% score, `LLR: 2.95`, 0 crashes).
- **Highlights:**
  - **Improving-Aware LMP (`AlphaBetaEngine.cs`):** Tightened Late Move Pruning quiet-move thresholds when the static evaluation is not improving (`improving ? 3 + (2 * depth * depth) : 3 + (depth * depth)`), cutting wasted quiet searches at deteriorating nodes.

### `v1.0.28` (2026-11-16) — Cache Bad Captures Directly in MovePicker (Eliminate Double SEE)
- **Category:** Move Ordering Speed & NPS (Patch Release)
- **Estimated Elo:** **2714 &plusmn; 6** (+6.38 Elo / +7.55 nElo over v1.0.27 in 8,000 games, 97.40% LOS, 100% node-count equivalence)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,201 wins, 3,054 losses, 1,745 draws (50.92% score, 0 crashes).
- **Highlights:**
  - **Single-Pass Capture SEE Scoring (`MovePicker.cs`):** Stored losing captures (`Move` + `SEE`) directly in stack buffers during `GoodCaptures` scoring and skipped already-picked `ttMove`, preventing `EnsureAllMovesGenerated()` from invalidating capture indices and eliminating redundant second-pass `StaticExchangeEvaluator.Evaluate` calls at `BadCaptures`.

### `v1.0.27` (2026-11-15) — Defer Full Move Generation Past Killer & CounterMove Stages
- **Category:** Move Ordering Speed & NPS (Patch Release)
- **Estimated Elo:** **2708 &plusmn; 7** (+4.43 Elo / +5.16 nElo over v1.0.26 in 8,000 games, 90.81% LOS, 100% node-count equivalence)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,169 wins, 3,067 losses, 1,764 draws (50.64% score, 0 crashes).
- **Highlights:**
  - **Lazy Quiet Move Generation (`MovePicker.cs`):** Validated `Killer0`, `Killer1`, and `CounterMove` via `_board.IsLegal(...)` before invoking `EnsureAllMovesGenerated()`, avoiding full legal move generation on every beta cutoff produced by killer or countermove stages.

### `v1.0.26` (2026-11-15) — Transposition Table Move Ordering in Quiescence Search
- **Category:** Search Move Ordering (Patch Release)
- **Estimated Elo:** **2704 &plusmn; 7** (+10.43 Elo / +12.19 nElo over v1.0.25 in 8,000 games, 99.92% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,271 wins, 3,031 losses, 1,698 draws (51.50% score, 0 crashes).
- **Highlights:**
  - **QS TT Move Prioritization (`QuiescenceSearch.cs`):** Scored `entry.BestMove` from TT hits at `1_000_000` ahead of MVV-LVA captures, checks, and check evasions in Quiescence Search, yielding immediate first-move beta cutoffs and cutting timeouts in half (`57` vs `121`).

### `v1.0.25` (2026-11-15) — Eliminate Duplicate Leaf Pawn Topology Scan in MoE Evaluation
- **Category:** Evaluation Speed & NPS (Patch Release)
- **Estimated Elo:** **2694 &plusmn; 7** (+0.91 Elo / +1.06 nElo over v1.0.24 in 8,000 games, 60.76% LOS, 100% node-count equivalence)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,130 wins, 3,109 losses, 1,761 draws (50.13% score, 0 crashes).
- **Highlights:**
  - **Single-Pass SWAR Topology Classification (`PawnTopology.cs`, `MultiTierNnueEvaluator.cs`):** Added `ClassifyExpert(..., out int opennessPct)` using integer SWAR `CalculateOpennessPercent` once inside classification and reused `opennessPct` for MoE Open/Closed blending, eliminating both the duplicate bitboard scan and floating-point arithmetic at every Open/Closed leaf node.

### `v1.0.24` (2026-11-15) — Clear Child Killer Moves in AlphaBeta (BUG-02)
- **Category:** Search Move Ordering (Patch Release)
- **Estimated Elo:** **2693 &plusmn; 7** (+6.86 Elo / +7.84 nElo over v1.0.23 in 8,000 games, 97.82% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,242 wins, 3,084 losses, 1,674 draws (50.99% score, 0 crashes).
- **Highlights:**
  - **Clear Child Killers (`AlphaBetaEngine.cs`):** Cleared `_stack[ply + 1].Killer0` and `_stack[ply + 1].Killer1` on entry to non-leaf `AlphaBeta` nodes so stale sibling killer moves no longer pollute move ordering across branches.

### `v1.0.23` (2026-11-15) — Exempt Checking Moves & Mate Threats from Pre-Move Pruning
- **Category:** Search Tactical Accuracy (Patch Release)
- **Estimated Elo:** **2686 &plusmn; 7** (+9.08 Elo / +10.30 nElo over v1.0.22 in 8,000 games, 99.60% LOS)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,345 wins, 3,136 losses, 1,519 draws (51.31% score, 0 crashes).
- **Highlights:**
  - **Check & Mate-Threat Exemption in Pre-Move Pruning (`AlphaBetaEngine.cs`):** Exempted moves that give check (`board.GivesCheck(move)`) and positions where a mate threat has been seen (`bestScore <= -TTEntry.MateBound`) from BadCaptures, Shallow Futility, Late Move Pruning (LMP), and SEE pruning, and fixed the depth-3 futility margin to scale monotonically (`120 * depth`).

---

### `v1.0.22` (2026-11-15) — Unified Shared-FT MoE NNUE, 20-Byte TTEntry & Legacy Cleanup
- **Category:** NNUE Architecture, Transposition Table Density & Cleanup
- **Estimated Elo:** **2677 &plusmn; 5** (+6.56 nElo / +5.95 Elo over v1.0.21 in 8,000 games)
- **Validation:** 8,000-game match at STC (`1+0.01`): 3,086 wins, 2,949 losses, 1,965 draws (50.86% score, 0 crashes, 1 timeout).
- **Highlights:**
  - **Unified Shared-FT MoE NNUE (`ArchMoE = 0x0003`, `networks/anak.nnue`):** Replaced multi-model switching with a shared `12288 -> 512x2` Feature Transformer feeding 6 specialized expert heads (`open`, `closed`, `endgame`, `queenless`, `imbalance`, `attack`), leaf-level expert routing, integer Open/Closed boundary blending, and unified search history/TT across phase transitions.
  - **20-Byte `TTEntry` (3 Entries / 64-Byte Cluster):** Shrunk `TTEntry` from 32 bytes to 20 bytes (+50% TT capacity).
  - **Removed Legacy MCTS & Unused Micro/Volatility Models:** Stripped `MCTSEngine`, `NodeArena`, `micro.nnue`, and `volatility.nnue` from engine, UCI options, tests, and Python training pipeline.

---

### `v1.0.21` (2026-11-10) — Move Generation Throughput & Hardware Intrinsic Optimizations (Patch Release)
- **Category:** Performance & Move Generation Throughput (Patch Release)
- **Estimated Elo:** **2670 &plusmn; 5** (+1.05 nElo / +1.30 Elo over v1.0.20 in 8,000 games)
- **Validation:** 8,000-game match at STC (`1+0.01`): 2,903 wins, 2,873 losses, 2,224 draws (50.19% score, 0 crashes, 1 timeout).
- **Highlights:**
  - **Hardware Intrinsic Pin Blocker Detection:** Replaced multi-branch pin checks with `BitOperations.IsPow2` hardware intrinsics across all move generators.
  - **Branchless Pawn Attack Lookups:** Replaced 4 file conditional branches per pawn with precomputed table lookups `Bitboard.WhitePawnAttacks` / `Bitboard.BlackPawnAttacks`.
  - **Pawn Push Bitwise Shift Register Reuse:** Reused bitwise shifts for single and double pushes.
  - **Ray-Directional Queen Validation:** Pre-checks orthogonality in `Board.IsLegal()` to execute only 1 slider query instead of 2.
  - **Dedicated `GenerateLegalQuiets` Method & Partition Invariant Tests:** Added dedicated legal quiet move generation validated with 8 comprehensive xUnit partition tests.
  - **Search NPS Gain:** Search throughput rose from ~305k NPS to ~348k–365k NPS (+14.1% in minimax search, +22–29% in raw movegen).

---

### `v1.0.20` (2026-11-09) — Disable Blunder Watchdog Pending Redesign (Critical Bugfix Release)
- **Category:** Search Move Integrity & Tactical Stability (Bugfix Release)
- **Estimated Elo:** **2669 &plusmn; 49** (+38.6 nElo / +52.5 Elo over v1.0.19 at MTC, 98.6% LOS)
- **Validation:** 100-game match at Moderate Time Control (`5+0.05`): 39 wins, 24 losses, 37 draws (57.50% score).
- **Highlights:**
  - **Eliminated Root Move Hijacking:** Disabled `RunMicroBlunderWatchdog` by default. Previously, the watchdog performed raw 1-ply SEE checks on opponent responses to candidate root moves without exchange awareness, causing it to misclassify Queen trades (e.g. `23. Qxc7`), standard piece sacrifices (e.g. Reynolds Meran `12. Nxb5`), and recaptures as blunders, overriding deep 20+ ply searches with severe tactical blunders.
  - **Wired UCI BlunderWatchdog Option:** Connected `BlunderWatchdog` UCI option to `IMultiTierEvaluator` and `AlphaBetaEngine` search worker, defaulting to `false`.
  - **Documented Defect & Redesign Roadmap:** Added comprehensive defect analysis and 3-step redesign plan in `docs/BUGFIX_CANDIDATES.md` (BUG-03) to re-introduce a net-exchange-aware watchdog in a future release.

---

### `v1.0.19` (2026-11-09) — Move Generation & Search NPS Throughput Optimizations
- **Category:** Performance & Move Ordering / Search Speedup (Patch Release)
- **Estimated Elo:** **2630 &plusmn; 9** (+19.2 nElo / +38.8 Elo over v1.0.16, 100.0% LOS)
- **SPRT Validation:** Tier 2 SPRT [0.0, 3.0] accepted $H_1$ at 2,683 games (1105 wins, 807 losses, 771 draws, 55.55% score, LLR 2.95)
- **Highlights:**
  - **Move Generation Throughput Optimizations:**
    - Streamlined legal move generation throughput and eliminated `stackalloc` pin ray allocations in `MoveGenerator`.
    - Accelerated pin and check resolution masks across king evasion and non-evasion pathways.
  - **Search & Quiescence NPS Speedups:**
    - Comprehensive zero-eval-impact hot-path optimizations across `AlphaBetaEngine`, `QuiescenceSearch`, and `MovePicker`.
    - Streamlined history tables (`ContinuationHistory`, `CorrectionHistory`, `CaptureHistory`) and transposition table entry access.
  - **Tooling & Benchmark Infrastructure:**
    - Integrated `elo_kit` pentanomial and Fishtest normalized Elo evaluation into fastchess benchmarking.
    - Added natural semver sorting and auto-detection for baseline opponents.

---

### `v1.0.18` (2026-11-08) — Fix False "Mate in 1" UCI Reporting on High Evaluations (Bugfix Release)
- **Category:** Protocol & Usability / Search Score Formatting (Patch Release)
- **Estimated Elo:** **2611 &plusmn; 6** (Zero regression, rating preserved)
- **Highlights:**
  - **Eliminated False "Mate in 1" UCI Output:** Removed the legacy heuristic in `UciEngineAdapter` and `AnalyzeRunner` that falsely emitted `score mate 1` / `score mate -1` whenever root win probability exceeded 98% ($\ge 950\text{ cp}$).
  - **Unified Score Formatting (`SearchScoreFormat`):** Standardized UCI output through `SearchScoreFormat.FormatUci`, ensuring crushing positions reliably report their actual centipawn evaluation (e.g. `score cp 1500`), while only positions with proven search checkmates announce `score mate N`.
  - **Unknown Mate Sentinel Alignment:** Updated `SearchScoreFormat.UnknownMateDistance` from 256 to 255 (`+M255` / `-M255`), adhering to standard chess engine convention for saturated NN win probability without a proven mate depth.

---

### `v1.0.17` (2026-11-08) — Self-Contained Binary with Embedded NNUE Weights (QoL Feature)
- **Category:** Architecture & Packaging / Usability (Patch Release)
- **Estimated Elo:** **2611 &plusmn; 6** (Zero regression, rating preserved)
- **Highlights:**
  - **Embedded NNUE Weights in Executables:** Compiled all 8 multi-expert and micro NNUE models (`open`, `closed`, `endgame`, `queenless`, `imbalance`, `attack`, `micro`, and `volatility`) directly into the executable as embedded resources. Standalone release binaries (e.g. `anak-windows-x64-avx512.exe`) now operate completely self-contained without requiring companion `./networks` folders.
  - **Fallback & Stream Deserialization:** Enhanced `NnueWeights` with `LoadFromStream` and `LoadFromSpan` methods. `MultiTierNnueEvaluator` loads external networks from disk if present (`./networks` or `NnuePath`), seamlessly falling back to embedded weights when running standalone outside the source tree.
  - **Dynamic Engine Versioning:** Dynamically resolved engine version from assembly informational attributes, ensuring UCI correctly announces `id name Anak 1.0.17`.

---

### `v1.0.16` (2026-11-08) — Leaf/QSearch Repetition Detection & Bitboard `GivesCheck` (+11.8 Elo)
- **Category:** Search / Repetition Accuracy & Quiescence Throughput (Patch Release)
- **Baseline Opponent:** Anak v1.0.15 (Known Elo: 2599)
- **Match Result:** 4,136.0 / 8,000 (51.70%) vs v1.0.15 @ 1+0.01 (2,948 W / 2,676 L / 2,376 D)
- **Elo Delta:** **+11.8 &plusmn; 5.8 Elo** (**+15.49 &plusmn; 7.61 nElo**, LOS: **100.0%**, Pairs Ratio: 1.17)
- **Estimated Elo:** **2611 &plusmn; 6** (First release crossing **2600 Elo**)
- **Highlights:**
  - **Leaf & QSearch Repetition / 50-Move Detection:** Moved repetition and 50-move draw detection (`HalfmoveClock >= 4`) before the `depth <= 0` Quiescence Search handoff in `AlphaBetaEngine` and added it to `QuiescenceSearch` (`qsDepth > 0`), preventing leaf moves from walking into 3-fold repetitions or 50-move draws while holding a winning evaluation.
  - **FIDE-Compliant En-Passant Repetition History:** Stripped `Zobrist.EpFile` from the repetition history table (`_history`) in `Board.MakeMove` when no enemy pawn attacks `EpSquare` after a double pawn push, eliminating missed 3-fold repetitions when returning to positions reached via double pawn pushes.
  - **Zero-MakeMove Bitboard `Board.GivesCheck`:** Replaced `MakeMove` + `IsInCheck` + `UnmakeMove` in Quiescence Search root check generation (`qsDepth == 0`) with a fast read-only bitboard direct and discovered-slider attack check (`Board.GivesCheck(Move)`).

---

### `v1.0.15` (2026-11-06) — SPSA-Tuned Dynamic Time Management (+2.5 Elo)
- **Category:** Search / Time Management (Patch Release)
- **Baseline Opponent:** Anak v1.0.14 (Known Elo: 2597)
- **Match Result:** 4,028.5 / 8,000 (50.36%) vs v1.0.14 @ 1+0.01 (3,001 W / 2,944 L / 2,055 D)
- **Elo Delta:** **+2.5 &plusmn; 6.0 Elo** (**+3.14 &plusmn; 7.61 nElo**, LOS: **79.1%**)
- **Estimated Elo:** **2599 &plusmn; 6**
- **Highlights:**
  - **Exposed & Tuned 9 Dynamic Time Management Parameters:** Wired core clock-allocation constants (`TimeMovesToGo`, `TimeIncFactor`, `TimeHardMultiplier`, `TimeSoftHardRatio`, `TimeFlipScale`, `TimeStableScale`, `TimeScoreDropScale`, `TimeFailLowScale`, `ScoreDropThreshold`) across `TimeManager`, `AlphaBetaEngine`, `UciOption`, and the SPSA tuning harness (`--batch time`).
  - **52,352-Game SPSA Convergence (818 Steps $\times$ 64 Games):** Converged on a tighter initial soft-to-hard ratio (`65%` &rarr; `60%`) and stronger stable-move time conservation (`80%` &rarr; `75%` after $\ge 4$ stable depths), paired with faster base pacing (`TimeMovesToGo: 40` &rarr; `38`) and higher increment utilization (`TimeIncFactor: 70%` &rarr; `73%`).

---

### `v1.0.14` (2026-11-04) — Fail-Low Soft-Time Extension (+25.0 Elo)
- **Category:** Search / Time Management (Patch Release)
- **Baseline Opponent:** Anak v1.0.13 (Known Elo: 2572)
- **Match Result:** 2,381.0 / 4,444 (53.58%) vs v1.0.13 @ 1+0.01 (1,795 W / 1,477 L / 1,172 D)
- **Elo Delta:** **+25.0 &plusmn; 8.0 Elo** (LOS: **>99.99%**, SPRT H1 Accepted: LLR 2.95 [0.00, 3.00])
- **Estimated Elo:** **2597 &plusmn; 8**
- **Highlights:**
  - **Dynamic Soft-Time Extension on Root Fail-Low:** When iterative deepening at root fails low on depth $\ge 6$ (`score <= alpha`), the expected PV move has collapsed. Dynamically scales the soft time limit by $1.4\times$ to allow the engine to complete the re-search and find a sound alternative move rather than rushing into an unverified blunder.
  - **Uncapped Headroom in Time Allocation:** Fixed an initialization bottleneck in `TimeManager` where baseline soft limits were prematurely constrained by hard limits, unlocking true dynamic scaling during critical tactical emergencies.
  - **Guaranteed Clock Safety:** Retains strict bounds against hard limits (`remainingTime - safetyLag`), ensuring zero clock forfeits in fast time controls while improving critical tactical resilience.

---

### `v1.0.13` (2026-11-04) — Checks in Quiescence Search (+15.1 Elo)
- **Category:** Search / Quiescence Optimization (Patch Release)
- **Baseline Opponent:** Anak v1.0.12 (Known Elo: 2557)
- **Match Result:** 3,134.0 / 6,008 (52.16%) vs v1.0.12 @ 1+0.01 (2,416 W / 2,156 L / 1,436 D)
- **Elo Delta:** **+15.1 &plusmn; 7.2 Elo** (LOS: **>99.9%**, Pairs Ratio: 1.23)
- **Estimated Elo:** **2572 &plusmn; 7**
- **Highlights:**
  - **Tactical Check Resolution at Leaf Nodes:** Generates and evaluates quiet giving-checks exclusively at the QS root (`qsDepth == 0`), preventing horizon-effect blunders on tactical check lines without suffering qsearch tree explosion.
  - **SEE Gating:** All quiet checks are filtered with non-negative Static Exchange Evaluation (`see >= 0`), immediately discarding sacrificing or losing checks.
  - **Tree Size Reduction:** Resolving tactical checks at the QS root cut main-tree bench nodes by **62.5%** on complex tactical suites while improving clock stability (4 timeouts vs 9 for baseline).

---

### `v1.0.12` (2026-11-02) — Math, Bitwise & SIMD Simplification (+121% NPS)
- **Category:** Pure Throughput Optimization (Patch Release)
- **Baseline Opponent:** Anak v1.0.11 (Known Elo: 2557)
- **Equivalence Verification:** 100% deterministic search equivalence verified across 64 positions (`bench 11`), **0 node difference**.
- **Performance:** 2.21x throughput speedup (from 166k to 368k NPS).
- **Highlights:**
  - Branchless bitwise operations for piece-type masking and square indexing.
  - SIMD vector optimizations across accumulator and dot-product routines.
  - Eliminated redundant safety checks and bounds checks in performance-critical paths.

---

### `v1.0.11` (2026-11-01) — Root LMR In-Check Bugfix & 2-Ply Conthist in LMR (+14.4 Elo)
- **Match Result:** 3962.5 / 7610 (52.07%) vs v1.0.10 @ 1+0.01 (3093 W / 2778 L / 1739 D)
- **Elo Delta:** **+14.4 &plusmn; 6.6 Elo** (99.9% LOS, SPRT H1 accepted)
- **Estimated Elo:** **2557 &plusmn; 7**
- **Highlights:**
  - Fixed a critical bug in `SearchRoot` where LMR was erroneously applied to check evasions (`!_stack[0].InCheck` guard).
  - Integrated 2-ply continuation history into `AlphaBeta` Late Move Reductions (LMR), creating a blended multi-ply move ordering signal.

---

### `v1.0.10` (2026-11-01) — Staged Index Buffers & History Malus Capping (+20.1 Elo)
- **Match Result:** 2729.0 / 5160 (52.89%) vs v1.0.9 @ 1+0.01 (2090 W / 1792 L / 1278 D)
- **Elo Delta:** **+20.1 &plusmn; 7.8 Elo** (99.9% LOS, SPRT H1 accepted)
- **Estimated Elo:** **2543 &plusmn; 8**
- **Highlights:**
  - Zero-allocation staged index buffers (`IndexBuffer128`, `ScoreBuffer`, `PickedBitmask`) in `MovePicker`, eliminating heap garbage collection and speeding up selection sort (+21% NPS).
  - Enforced gravity-based history malus capping (`TunedHistoryMaxMalus = 2000`), preventing over-penalizing occasionally failing quiet moves.

---

### `v1.0.9` (2026-10-31) — In-Tree 2-Fold Repetition Detection & Time Safety (+3.8 Elo)
- **Match Result:** 4027.0 / 7966 (50.55%) vs v1.0.8 @ 1+0.01 (3286 W / 3198 L / 1482 D)
- **Elo Delta:** **+3.8 &plusmn; 4.0 Elo** (92.2% LOS, SPRT H1 accepted)
- **Estimated Elo:** **2523 &plusmn; 4**
- **Highlights:**
  - Replaced strict 3-fold repetition checks inside the search tree with in-tree 2-fold repetition detection (`IsRepetition`), cutting 4+ wasted plies when defending against perpetual checks.
  - Dynamically masked time polling (`_timeManager.CheckMask`) to eliminate clock timeouts during extreme low-time conditions.

---

### `v1.0.8` (2026-10-30) — NativeAOT, Hardware CPU Targeting & Array Flattening (+15.0 Elo)
- **Match Result:** 3492.5 / 6696 (52.16%) vs v1.0.7 @ 1+0.01 (2731 W / 2442 L / 1523 D)
- **Elo Delta:** **+15.0 &plusmn; 6.0 Elo** (99.9% LOS, SPRT H1 accepted)
- **Estimated Elo:** **2519 &plusmn; 6**
- **Highlights:**
  - Ahead-of-Time compilation (`NativeAOT`) with hardware SIMD instruction targeting (`AVX-512`, `AVX2`, `BMI2`).
  - Flattened 3D history arrays (`CaptureHistory`, `HistoryTable`) into 1D power-of-two arrays with branchless bitwise addressing.
  - Applied `[SkipLocalsInit]` across hot search and movegen loops (+65% NPS throughput boost).

---

### `v1.0.7` (2026-10-30) — Correction History Side-to-Move Partitioning (+1.1 Elo)
- **Match Result:** 2006.5 / 4000 (50.16%) vs v1.0.6 @ 3+0.03 (1489 W / 1476 L / 1035 D)
- **Elo Delta:** **+1.1 &plusmn; 9.2 Elo** (59.5% LOS)
- **Estimated Elo:** **2504 &plusmn; 9**
- **Highlights:**
  - Expanded pawn structure correction history to `short[2 * 16384]`, partitioned by `[SideToMove, PawnKey]`.
  - Preserved Negamax mathematical symmetry by preventing White and Black nodes from overwriting each other's pawn adjustments with opposite signs.

---

### `v1.0.6` (2026-10-28) — Cached Capture SEE & Shallow SEE Pruning (+16.3 Elo)
- **Match Result:** 5235.0 / 10000 (52.35%) vs v1.0.5 @ 1+0.01 (4141 W / 3671 L / 2188 D)
- **Elo Delta:** **+16.3 &plusmn; 5.9 Elo** (100% LOS)
- **Estimated Elo:** **2503 &plusmn; 6** (Broke the 2500 Elo Milestone)
- **Highlights:**
  - Cached Static Exchange Evaluation in `MovePicker._seeScores` (`CurrentSee`), eliminating redundant calculations in LMR.
  - Shallow SEE pruning: pruned losing captures at `depth <= 5` (`SEE < -60 * depth`) and quiet moves blundering material at `depth <= 4` (`SEE < -35 * depth^2`).

---

### `v1.0.5` (2026-10-28) — Correction History Bound Filtering, Depth Weighting & Scaling (+204.5 Elo)
- **Match Result:** 7644.5 / 10000 (76.44%) vs v1.0.4 @ 1+0.01 (6969 W / 1680 L / 1351 D)
- **Elo Delta:** **+204.5 &plusmn; 7.1 Elo** (100% LOS)
- **Estimated Elo:** **2487 &plusmn; 7**
- **Highlights:**
  - Major breakthrough: fixed critical algorithmic flaws in pawn structure correction history.
  - Enforced strict bound filtering (LowerBound only updates when `score > staticEval`; UpperBound only when `bestScore < staticEval`).
  - Added depth-weighted EMA convergence and clamped corrections within `[-128, +128]` cp.
  - Slashed benchmark tree size by **-45.9%** (from 719k to 389k nodes).

---

### `v1.0.4` (2026-10-28) — Capture History Malus on Failed Captures (+8.2 Elo)
- **Match Result:** 5118.0 / 10000 (51.18%) vs v1.0.3 @ 1+0.01 (4274 W / 4038 L / 1688 D)
- **Elo Delta:** **+8.2 &plusmn; 5.4 Elo** (99.86% LOS)
- **Estimated Elo:** **2282 &plusmn; 5**
- **Highlights:**
  - Tracked searched captures at each node and applied gravity-damped negative history penalties to previously searched captures when a beta cutoff occurs.
  - Refined capture move ordering without overriding MVV-LVA or erroneously demoting good captures to bad captures.

---

### `v1.0.3` (2026-10-27) — Bypass Micro-NNUE Accumulator Updates in Search (+4.1 Elo)
- **Match Result:** 5059.0 / 10000 (50.59%) vs v1.0.2 @ 1+0.01 (4246 W / 4128 L / 1626 D)
- **Elo Delta:** **+4.1 &plusmn; 6.1 Elo** (90.7% LOS)
- **Estimated Elo:** **2274 &plusmn; 6**
- **Highlights:**
  - Removed vector copying and incremental updates for Micro-NNUE across every node in `AlphaBeta` and `QuiescenceSearch`.
  - Confined Micro-NNUE to the root candidate blunder watchdog using on-demand evaluation, saving significant memory bandwidth.

---

### `v1.0.2` (2026-10-27) — Inline Fixed Mailbox `Piece[64]` in Board (+6.3 Elo)
- **Match Result:** 5090.5 / 10000 (50.91%) vs v1.0.1 @ 1+0.01 (4293 W / 4112 L / 1595 D)
- **Elo Delta:** **+6.3 &plusmn; 6.1 Elo** (97.9% LOS)
- **Estimated Elo:** **2270 &plusmn; 6**
- **Highlights:**
  - Added an inline value buffer `[InlineArray(64)] Mailbox` inside `Board`.
  - Replaced the 7-branch bitboard chain in `Board.GetPieceAt` with an $O(1)$ direct array lookup, accelerating SEE and move ordering.

---

### `v1.0.1` (2026-10-27) — 1D Flattened `ContinuationHistory` Table (+3.1 Elo)
- **Match Result:** 5044.5 / 10000 (50.45%) vs v1.0.0 @ 1+0.01 (4213 W / 4124 L / 1663 D)
- **Elo Delta:** **+3.1 &plusmn; 5.9 Elo** (84.8% LOS)
- **Estimated Elo:** **2264 &plusmn; 6**
- **Highlights:**
  - Replaced multi-dimensional managed array `int[13, 64, 13, 64]` with flat 1D array `int[16 * 64 * 16 * 64]` and branchless bitwise indexing.
  - Achieved +5.8% NPS speedup by eliminating .NET runtime array bounds checks and improving CPU cache locality.

---

### `v1.0.0` (2026-10-27) — Official Release: Multi-Tier NNUE, Managed Syzygy & Unified TT (+30.8 Elo)
- **Match Result:** 5442.5 / 10000 (54.43%) vs current_dev @ 1+0.01 (4631 W / 3746 L / 1623 D)
- **Elo Delta:** **+30.8 &plusmn; 6.0 Elo** (100% LOS)
- **Estimated Elo:** **2261 &plusmn; 6**
- **Highlights:**
  - Multi-Tier NNUE architecture (Open, Closed, Endgame, Queenless, Imbalance, Attack + Micro Watchdog).
  - 100% pure managed C# Syzygy WDL/DTZ 3-4-5 piece tablebase decompressor without external C/C++ DLL dependencies.
  - Unified 64-byte aligned Transposition Table with `ExpertId` tagging, isolating neural score bounds across expert transitions while preserving multi-ply tree memory.
  - Full PVS search with Reverse Futility Pruning, Late Move Reductions, Singular Extensions, and Check Extensions.

---

### `v0.0.5` (2026-10-24) — 16 King-Bucketed NNUE & Stratified Datagen (+46.8 Elo)
- **Match Result:** 567.0 / 1000 (56.7%) vs v0.0.4 @ 1+0.01 (478 W / 344 L / 178 D)
- **Elo Delta:** **+46.8 &plusmn; 19.0 Elo** (100% LOS)
- **Estimated Elo:** **1725 &plusmn; 19**
- **Highlights:**
  - 16 King-Bucketed NNUE architecture with SCReLU activations and SIMD incremental updates.
  - Closed-loop topology-stratified self-play datagen flywheel (`anak datagen`) writing directly to binary pack format (`ANAKBINP`).
  - Dynamic time management with move stability scaling and tactical crisis score-drop panic (+13.9 Elo).
  - Pre-move capture SEE in LMR and RFP zugzwang/mate guards.

---

### `v0.0.4` (2026-10-23) — Multi-Expert NNUE & Syzygy Probing (+18.8 Elo)
- **Match Result:** 527.0 / 1000 (52.7%) vs v0.0.3 @ 3+0.05 (366 W / 312 L / 322 D)
- **Elo Delta:** **+18.8 &plusmn; 18.0 Elo**
- **Estimated Elo:** **1678 &plusmn; 18**
- **Highlights:**
  - 8 retrained multi-expert NNUE networks partitioned by pawn topology.
  - PolyGlot opening book integration and basic Syzygy tablebase probing.
  - Automated SPSA parameter tuning calibrating core search heuristics.

---

### `v0.0.3` (2026-10-20) — Transition to Alpha-Beta PVS (+20.5 Elo)
- **Match Result:** 529.5 / 1000 (53.0%) vs v0.0.2 @ 3+0.05
- **Elo Delta:** **+20.5 &plusmn; 23.0 Elo**
- **Estimated Elo:** **1659 &plusmn; 23**
- **Highlights:**
  - Transition from Monte Carlo Tree Search (MCTS) to Alpha-Beta (Negamax) with Principal Variation Search (PVS).
  - Iterative deepening with transposition table move ordering and quiescence search.

---

### `v0.0.1` & `v0.0.2` (2026-10-19 / 2026-10-20) — Initial Release (MCTS & NNUE Bootstrap)
- **Estimated Elo:** **1616 &plusmn; 215**
- **Highlights:**
  - Initial working engine bootstrap featuring MCTS with PUCT selection, initial Multi-Tier NNUE models, and bitboard move generation.
