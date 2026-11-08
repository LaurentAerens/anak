# Anak Chess Engine — Official Release Notes

This document provides a consolidated history of all official releases, validated optimizations, benchmark results, and architectural advancements in the Anak Chess Engine.

> **Current Active Version:** `v1.0.16` (Estimated Rating: **2611 Elo**)  
> **Benchmark Archive & Progression:** [`BENCHMARKS.md`](BENCHMARKS.md)  
> **Benchmarking Protocol:** [`docs/BENCHMARKING_POLICY.md`](docs/BENCHMARKING_POLICY.md)  
> **Failed Experiments & Archive:** [`docs/FAILED_IDEAS.md`](docs/FAILED_IDEAS.md)

---

## 🌟 Releases & Optimization History

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
