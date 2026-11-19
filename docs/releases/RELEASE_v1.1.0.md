# Anak `v1.1.0` — Milestone Release Notes (`v1.0.0` → `v1.0.33`)

I am proud to announce **Anak `v1.1.0`**.

When I started this project, `1.0.0` was the dream and the goal: building my own engine and breaking past 2000 Elo. `1.1.0` goes far beyond what I ever imagined possible. As a long-time follower of TCEC and Stockfish, seeing Anak now hold its own against engines I used to look up to — and recently even besting **Andscacs 0.95** in a local arena game — is surreal. I would never have imagined I was capable of this.

`v1.1.0` consolidates **33 patch releases** of engine architecture, NNUE, search, throughput, time management, and book/parameter tuning since `v1.0.0` (2026-10-27).

---

## Strength & Elo Progression (`v1.0.0` → `v1.1.0`)

- **Cumulative Chained Self-Play Gain vs. `v1.0.0`:** **+497 Elo** (`2261` in `v1.0.0` → `2754` in `v1.0.32` → `+4.00 Elo` in `v1.0.33` over 8,000 games)
- **External Ordo CCRL-Anchored Rating:** **2965 ± 21 Elo** (measured in `v1.0.33` over 1,105 non-timeout games at `5+0.05` against `Berserk-4.7.0`, `Laser-1.7`, `Weiss-2.0`, `Ethereal-13.00`, and `Koivisto-8.0`)

> **Rating Scale Note:** `v1.0.0`–`v1.0.32` tracked ratings by chaining self-play diffs from `2261`. In `v1.0.33`, Anak was re-anchored against an external 5-engine Ordo CCRL gauntlet (`2965 ± 21`). Both numbers reflect the same **+497 Elo** of real playing-strength gains accumulated from `v1.0.0` to `v1.1.0`.

---

## Major Highlights Since `v1.0.0`

### 1. Unified MoE NNUE & Standalone Distribution
- **Unified Shared-FT MoE NNUE (`v1.0.22`):** Replaced separate per-expert feature transformers and legacy MCTS/Micro-NNUE code with a unified Shared Feature Transformer feeding 6 topology-routed expert heads, shrinking `TTEntry` to 20 bytes (`+6.56 nElo`).
- **Embedded NNUE Binaries (`v1.0.17`):** Bundled NNUE networks directly inside NativeAOT executables (`win-x64-avx512`, `win-x64-avx2`, `linux-x64-*`, `linux-arm64`, `osx-arm64`) for zero-config deployment.
- **Leaf Topology Caching (`v1.0.25`):** Eliminated duplicate pawn-topology scans during MoE leaf evaluation.

### 2. Search, Pruning & History Breakthroughs
- **Correction History Overhaul (`v1.0.5`, `v1.0.7`):** Fixed bound filtering, depth weighting, and `[SideToMove, PawnKey]` partitioning (**+204.5 Elo** in `v1.0.5` alone).
- **Quiescence Search & Draw Detection (`v1.0.9`, `v1.0.13`, `v1.0.16`, `v1.0.26`, `v1.0.30`):**
  - Added checks in Quiescence Search (`+15.1 Elo`).
  - Added in-tree/QSearch 2-fold repetition and FIDE-compliant en-passant repetition history (`+11.8 Elo`).
  - Prioritized TT moves in QSearch and refined `standPat` with matching-expert `TTFlags.LowerBound` (`+9.30 Elo`).
- **Pruning, Extensions & Reductions (`v1.0.6`–`v1.0.32`):**
  - **SEE & MovePicker:** Cached capture SEE + shallow SEE pruning (`+16.3 Elo`), staged index buffers + history malus capping (`+20.1 Elo`), deferred full move generation past killer/countermove stages (`v1.0.27`), and eliminated double SEE on bad captures (`v1.0.28`).
  - **LMR, LMP & RFP:** Added 2-ply continuation history to LMR (`+14.4 Elo`), exempted checking moves & mate threats from pre-move pruning (`+9.08 Elo`), fixed child killer clearing (`BUG-02`), added improving-aware LMP (`+15.56 Elo`), extended RFP to `depth <= 6` (`+9.08 Elo`), and reduced CounterMove LMR by 1 (`+6.38 Elo`).
- **Correctness & Reliability (`v1.0.18`, `v1.0.20`):** Fixed false mate-in-1 UCI reporting on high evals and disabled the blunder watchdog (`+52.5 Elo` at MTC).

### 3. Engine Throughput (NPS)
- **+121.3% NPS (2.21× throughput) in `v1.0.12`** via math, bitwise, and SIMD simplifications.
- **+14.1% search NPS in `v1.0.21`** via movegen throughput optimizations and hardware intrinsics (building on `v1.0.1` 1D `ContinuationHistory`, `v1.0.2` inline `Piece[64]` mailbox, and `v1.0.19` movegen improvements).

### 4. Time Management, SPSA Tuning & Opening Book
- **Dynamic Time Management (`v1.0.14`, `v1.0.15`):** Added fail-low soft-time extension (`+25.0 Elo`) and SPSA-tuned `TimeParams`.
- **59,400-Game SPSA Tuning (`v1.0.33`):** Exposed 6 new pruning UCI options (`FutilityMarginD1D2`, `FutilityMarginD3`, `LmpBase`, `LmpMultiplier`, `SeeCaptureMargin`, `SeeQuietMargin`) and tuned both `core` and `pruning` batches at `0.5+0.005` (`+4.00 Elo` over `v1.0.32` in 8,000 games).
- **PolyGlot Book Calibration (`v1.0.33`):** Reinforced `book.bin` over 600 live `startpos` games and pruned 55,295 low-win-rate entries (`37,998` entries across `33,988` positions).

---

## Thank You & Looking Ahead to `v1.2.0`

A huge thank you to the **Chess Programming Wiki (CPW)** and everyone in the computer chess community whose open-source engines, tools (`fastchess`, `Ordo`, `Stockfish`, `Laser`, `Weiss`, `Ethereal`, `Koivisto`, `Berserk`, `Andscacs`), and ideas inspired and tested Anak along the way.

Even at ~2965 Elo, I still believe there is a lot of performance left on the table — in both search and NNUE — and I'm excited to keep squeezing out every bit of strength on the road to **Anak `v1.2.0`**.
