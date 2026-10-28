# Anak Engine — Future Ideas & Improvement Backlog

This document maintains a living backlog of concrete architectural experiments, neural network enhancements, search optimizations, and parameter tuning initiatives for future Anak iterations.

---

## 🧠 Category 1: Neural Networks & Multi-Tier NNUE (Eval & Datagen)

### 1. In-Engine Self-Play Training Data Flywheel (Self-Generated Datagen)
- **Background & Context:**
  - In earlier versions, Anak's NNUE models were trained on external heterogeneous datasets (Stockfish evaluations, TCEC championship games, Leela Chess Zero self-play, and Lichess master archives).
  - While external data provided initial bootstrapping, it introduces severe **domain shift**: Anak's internal Alpha-Beta PVS search tree, move ordering heuristics, singular extensions, and dynamic time management explore positions and evaluate branches very differently from Stockfish or Leela.
  - In Phase 4 (`v0.0.5`), the native multi-threaded self-play engine (`anak datagen`, `DatagenRunner`, `StratifiedBinpackWriter`, `BinpackWriter`) was implemented in C#, capable of generating ~750–1,000 pos/sec per core with zero GC allocations and streaming directly into 144-byte binary pack format (`ANAKBINP`).
- **Implementation & Next Steps:**
  - **Closed-Loop Datagen Flywheel:**
    1. Run high-volume self-play datagen using Anak's latest executable (`anak datagen --threads 16 --games 500000 --nodes 15000 --out-dir ./training/data/selfplay`).
    2. Employ Syzygy 3-4-5-6 piece WDL/DTZ tablebase adjudication for rapid endgame resolution, score-drop resignation ($|\text{score}| \ge 1000\text{ cp}$ for 3 consecutive plies), and draw adjudication ($\le 10\text{ cp}$ after ply 40).
    3. Stratify the generated positions across the 80/20 domain streams (`open.binpack`, `closed.binpack`, `endgame.binpack`, `queenless.binpack`, `attack.binpack`, `imbalance.binpack`, `volatility.binpack`, `micro.binpack`) guided by `PawnTopology.ClassifyExpert`.
    4. Train updated NNUE networks using `training/train_all.py` on this engine-native distribution.
    5. Benchmark the newly trained nets against the previous baseline; deploy the winner to generate the next generation of games.

---

### 2. Targeted Fine-Tuning on Misevaluated Positions (Weakness Finder & Oracle Auditor)
- **Background & Context:**
  - The `tools/anak_weakness_finder` automated diagnostic pipeline runs extensive ladder matches against calibrated Stockfish tiers (1600–2400) and ingests game plies into an embedded **DuckDB** database.
  - In a 50,000-game audit, the tool identified systematic blind spots:
    - **Color Asymmetry Gap:** Black defense collapse rate is $4.64\times$ higher than White ("Cracked Defenses" represent 29.8% of losses, with 4,201 Black losses vs. 905 White losses).
    - **Vulnerable Endgames:** Bishop endgames (37.6% score), Mixed Minor pieces (46.7% score), and Rook & Pawn endgames (47.3% score) suffer disproportionate collapse.
    - **Tactical Failure Archetypes:** Hanging piece drops (15.1% of blunders, avg. drop $-13.99\text{ cp}$) and blunder under check (10.8% of blunders, avg. drop $-14.69\text{ cp}$).
  - The embedded Stockfish Oracle auditor (`oracle_analyzer.py`) audits extreme evaluation discrepancies ($|\Delta\text{eval}| \ge 1.5\text{ pawns}$ or blown winning leads $\ge +1.5\text{ pawns}$) by running full-strength Stockfish at depth 20–26 to establish objective ground-truth scores (`sf9_eval_cp`, `sf9_best_move`).
- **Implementation & Next Steps:**
  - **Export Pipeline:** Use `tools/anak_weakness_finder/export_training_fens.py` to dump critical misidentified positions:
    ```bash
    python -m tools.anak_weakness_finder.main export-fens --color black --expert Closed --out training/data/weakness_black_closed.epd
    python -m tools.anak_weakness_finder.main export-fens --type throw --out training/data/weakness_throws_st9.epd
    ```
  - **Curriculum / Active Learning Fine-Tuning:**
    - Incorporate exported weakness positions as a high-priority "booster set" (10–20% blend with the primary stratified binpack dataset) during training.
    - Use low learning rate fine-tuning ($\text{LR} = 10^{-4}$ with AdamW and cosine decay) to correct the specific blindspots without catastrophic forgetting on general chess positions.

---

### 3. Tactical Micro-Model: Tactical Puzzle Augmentation & Coarse Eval Quantization (Blunder Detector)
- **Background & Context:**
  - `micro.nnue` is an ultra-compact neural network ($768 \to 16\times 2 \to 1$) intended to run at 20M+ NPS as a rapid watchdog for leaf nodes, QSearch stand-pat evaluation, and pruning validation.
  - In earlier iterations, `micro.nnue` was trained on continuous floating-point centipawn values identical to standard networks. Because a 16-hidden-dim network lacks the parameter capacity to accurately grade subtle positional differences (e.g., $+15\text{ cp}$ vs. $+35\text{ cp}$), it became noisy and ineffective for pruning decisions.
- **Implementation & Next Steps:**
  - **Tactical Puzzle Dataset Ingestion:**
    - Augment the micro training pipeline (`micro.binpack`) with dedicated tactical blunder sets: Lichess tactical puzzles, mate-in-1/2/3 sequences, hanging piece refutations, and blunder swing positions ($\ge 150\text{ cp}$ delta between plies) extracted from `volatility.binpack`.
  - **Coarse Target Discretization (Blunder-Only Target Rounding):**
    - Modify `training/dataset.py` for target `micro` to discretize/round evaluations:
      - Positions within $[-100, +100]\text{ cp}$ (quiet positional play) are clamped or rounded to `0.0`.
      - Blunders, drop tactics, and tactical losses are snapped to extreme categorical values: $-1.0$ ($-500\text{ cp}$ or loss) and $+1.0$ ($+500\text{ cp}$ or win).
    - This forces the 16 hidden neurons to act purely as a sharp non-linear tactical classifier ("is there a tactical catastrophe / hanging piece on the board?") rather than wasting capacity fitting quiet positional noise.

---

### 4. Re-introducing Micro-NNUE Accumulators with a Specialized Watchdog Architecture
- **Background & Context:**
  - In `v1.0.3`, we removed the per-node incremental accumulator updates for the existing Micro-NNUE (`768 -> 16x2 -> 1`) during search.
  - In the earlier design, copying the micro accumulator at *every single node* throughout the minimax search tree introduced unnecessary memory bandwidth overhead, while the existing general-purpose micro model had negligible impact on pruning.
- **Architectural Enhancements for Re-introduction:**
  - **Specialized Tactical Training Integration:** Rather than training Micro-NNUE on standard game positions (where it is merely a low-precision clone of the full network), train a dedicated ultra-compact network on **tactical blunders, hanging piece traps, and forced tactical sequences** (as outlined in Item 3).
  - **Dual-Accumulator Architecture:** Only allocate, refresh, and incrementally update the micro accumulator when entering specific search subtrees (e.g., tactical plies, sharp branching, or Quiescence Search), completely bypassing it in deep quiet alpha-beta nodes.
  - **Wider / Modern Micro Architecture:** Consider expanding from `768 -> 16x2 -> 1` to `768 -> 32x2 -> 1` or `768 -> 64x2 -> 1`. With modern SIMD intrinsics, a 32- or 64-wide dot product fits in 1–2 AVX2 registers, giving significantly greater tactical discrimination with minimal execution time.
  - **Conditional In-Tree Querying:** Query this network specifically in Quiescence Search stand-pat evaluation or as a fast pre-filter for null-move / futility pruning.

---

### 5. Retrain Models Post-Selector / Topology Boundary Refactoring
- **Background & Context:**
  - In commit `64e10f4`, the position classification boundaries in `PawnTopology.ClassifyExpert` (C#) and `classify_expert` (`training/query.py`) were refined based on tournament analytics:
    - **Endgame piece count threshold:** Increased from $\le 14$ pieces to $\le 16$ pieces.
    - **Material imbalance threshold:** Tightened from $\ge 2$ pawns to $\ge 4$ pawns difference.
    - **Openness boundary:** Shifted from $0.50$ to $0.49$.
    - **Attack classification:** Now requires opposite-side castling *and* pawn openness $\ge 0.03$.
  - However, the neural network weights currently deployed in `networks/` were trained on datasets partitioned under the *previous* thresholds. This created a boundary mismatch between training data routing and runtime inference routing.
- **Implementation & Next Steps:**
  - Re-run dataset preparation (`training/preprocess.py`) using the updated `classify_expert` logic.
  - Retrain all 6 primary expert networks:
    - `open.nnue` & `closed.nnue` (updated openness threshold $0.49$)
    - `endgame.nnue` (now captures 15–16 piece transitions)
    - `imbalance.nnue` (now strictly captures major material imbalances $\ge 4$ pawns)
    - `attack.nnue` (filtered for open-file opposite-castling positions)
    - `queenless.nnue`
  - Re-export quantized weights via `training/export.py` to `networks/` and verify runtime inference consistency.

---

### 6. Multi-Net Specialization & Expert Fine-Tuning
- Further specialize sub-networks for high-frequency game phases:
  - **Sharp Tactical / King Attack Net:** Specialized for open king shelters, pawn storms, and heavy piece attacks.
  - **Endgame Specialist:** Dedicated sub-net for $\le 6$ piece positions operating directly alongside Syzygy tablebase probing.
  - **Pawn Endgame Net:** Dedicated micro net trained on pure pawn structures, breakthrough tactics, and king opposition geometry.

---

## ⚡ Category 2: Search Heuristics & Advanced Pruning Research

### 1. Refined ProbCut (Probability Cutoff) with SEE Safeguards
- **Context:** A naive ProbCut experiment was reverted in `v0.0.5` due to search instability when elevated QSearch cutoffs pruned critical defensive tactics.
- **Direction:** Re-investigate ProbCut gated by strict Static Exchange Evaluation ($\text{SEE} \ge 0$) and limited to non-quiet moves when static eval exceeds $\beta + 200\text{ cp}$.

### 2. Comprehensive SPSA Re-Tuning Post-Engine Changes
- **Context:** Multiple foundational search, data structure, and evaluation changes landed in Anak (`v1.0.1`, `v1.0.2`, `v1.0.3`, Syzygy probing, unified TT). Once all structural search features in the active plan are implemented, a comprehensive long-running SPSA re-tune across all pruning thresholds and time management parameters should be executed.

### 3. Dedicated Tactical Move Generator Scoped to Blunder Checking
- **Context:** An experimental dedicated `GenerateLegalTacticalMoves` (generating only captures and promotions) was evaluated in Quiescence Search (Step 1.9). While it provided +5.4% raw NPS speedup, in general QSearch it altered move ordering ties and yielded -6.4 Elo in match play against v1.0.3 across 1,029 games.
- **Direction:** Repurpose the dedicated tactical move generator specifically for the root/candidate **Blunder Checker** and shallow tactical watchdog screens. In blunder check routines, the engine only cares about immediate counter-tactics, hanging pieces, and tactical threats. Generating solely tactical moves there eliminates generating quiet non-threats without risking QSearch move-ordering side effects.

---

## 🚀 Category 3: Low-Level Hardware Research

### 1. Memory Prefetching in Transposition Table & History Probing
- **Context:** Probing the Transposition Table and butterfly history tables accounts for a notable fraction of L3 cache misses in deep searches.
- **Direction:** Issue hardware prefetch instructions (`_mm_prefetch` via C# `System.Runtime.Intrinsics.X86.Sse.Prefetch0`) for target TT cluster addresses during move generation and candidate move unpacking.
