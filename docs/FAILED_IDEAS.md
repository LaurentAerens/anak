# Anak Chess Engine — Failed Experiments & Revisit Archive

This document preserves the hypotheses, match results, root-cause analyses, and future revisit conditions for all optimization candidates, pruning ideas, and search heuristics that were experimentally tested and rejected.

---

## 📊 Summary of Rejected Experiments

| Step / Ref | Candidate / Hypothesis | Phase | Match Result / Games | Elo Delta | LOS | Status |
| :---: | :--- | :---: | :---: | :---: | :---: | :---: |
| **v1.0.x — 2.9** | 4-Ply Continuation History Table | Search Ordering | 115.0 / 250 vs v1.0.10 | **-27.9 &plusmn; 35.7** | 6.2% | ❌ Reverted |
| **v1.0.x — 2.10** | Fail-Soft Quiescence Search `bestScore` Tracking | Quiescence | 117.5 / 250 vs v1.0.6 | **-20.9 &plusmn; 38.0** | 13.9% | ❌ Reverted |
| **v1.0.x — 1.4** | Lazy NNUE Accumulator Evaluation on Stack | Evaluation | 236.0 / 500 vs v1.0.3 | **-19.5 &plusmn; 31.1** | 10.6% | ❌ Reverted |
| **v1.0.x — 2.3** | TT Move Ordering in Quiescence Search | Quiescence | 119.0 / 250 vs v1.0.4 | **-16.7 &plusmn; 34.1** | 16.7% | ❌ Reverted |
| **v1.0.x — 1.7** | Managed Array `ulong[]` Repetition Table | Core Board | 2418.0 / 4978 (Aborted) | **-9.9 &plusmn; 8.5** | <1% | ❌ Reverted |
| **v1.0.x — 1.4b** | Bulk Incremental Feature Updates in Accumulator | Evaluation | 243.0 / 498 vs v1.0.3 | **-8.4 &plusmn; 22.8** | 23.6% | ❌ Reverted |
| **v1.0.x — 1.9** | Dedicated Tactical Move Generator for QS | MoveGen | 505.0 / 1029 vs v1.0.3 | **-6.4 &plusmn; 21.7** | 15.3% | ❌ Reverted |
| **v1.0.x — 1.1** | Incremental `PawnKey` in `Board` | Core Board | 4961.5 / 10000 vs v1.0.0 | **-2.7 &plusmn; 6.1** | 19.6% | ❌ Reverted |
| **v1.0.x — 2.5** | Singular Extensions Multi-Cut & Negative Ext. | Search Pruning | 2668.0 / 5376 vs v1.0.5 | **-2.5 &plusmn; 7.9** | 26.5% | ❌ Reverted |
| **v1.0.x — 2.1** | Eager Quiet Move Scoring in `MovePicker` | Move Ordering | Isolated Bench Verification | Severe Cut-Slowdown | - | ❌ Reverted |
| **v1.0.x — 1.3** | SIMD Fused Copy-Add-Sub in Accumulator | Evaluation | Isolated Bench Verification | 0.0 NPS delta | - | ❌ Reverted |
| **v1.0.x — 1.5** | 4-Row Register Blocking in `ComputeHiddenLayer` | Evaluation | Isolated Bench Verification | -39% NPS | - | ❌ Reverted |
| **v1.0.x — 2.16**| Late Move Pruning (LMP) Improving Tightening | Pruning | Bench verification (+16% nodes)| Premature Fail-Lows | - | ❌ Reverted |
| **v0.0.5 — 1.6** | Naive ProbCut (QSearch probe when eval $\ge \beta + 200$) | Pruning | 48.0 / 250 vs Step 1.4 | **-249.6 &plusmn; 44.0** | 0.0% | ❌ Reverted |
| **v0.0.5 — 3.5** | Batch BCD Simultaneous 13-Parameter SPSA Tuning | Tuning | 100.0 / 250 vs Step 3.4 | **-70.4 &plusmn; 36.0** | 0.0% | ❌ Reverted |
| **v0.0.5 — 3.6** | Batch D Pruning Thresholds SPSA Tuning | Tuning | 115.0 / 250 vs Step 3.4 | **-27.8 &plusmn; 36.0** | 6.3% | ❌ Reverted |
| **v0.0.5 — 1.3** | Improving in NMP ($r{+}{+}$ when improving) | Pruning | 119.5 / 250 vs v0.0.4 | **-15.3 &plusmn; 37.4** | 21.0% | ❌ Reverted |
| **v0.0.5 — 2.2** | Node Dominance Early Breakout ($\ge 85\%$ node share) | Time Management| 120.5 / 250 vs Step 2.3 | **-12.5 &plusmn; 34.2** | 23.6% | ❌ Reverted |
| **v0.0.5 — 3.3** | Simultaneous Batch A Pruning Tightening | Pruning | 121.0 / 250 vs Step 3.2 | **-11.1 &plusmn; 34.5** | 26.3% | ❌ Reverted |
| **v0.0.5 — 1.5v1**| Naive Combined Butterfly + 1-ply + 2-ply Conthist | Move Ordering | 124.5 / 250 vs Step 1.4 | **-1.4 &plusmn; 36.0** | 47.0% | ❌ Reverted |

---

## 🔬 Detailed Root Cause Analyses & Future Revisit Conditions

### 1. 4-Ply Continuation History Table (-27.9 Elo)
- **Hypothesis:** Tracking move response correlations 4 plies back (the current side's move 2 turns ago) would refine quiet move ordering.
- **Match Result:** 115.0 / 250 (46.0%) vs v1.0.10, **-27.9 &plusmn; 35.7 Elo** (6.2% LOS).
- **Root Cause:** In fast games, 4 plies in the search tree often span different board topologies and tactical sub-plans. Incorporating 4-ply history diluted the strong tactical correlation signals from 1-ply (immediate response to opponent) and 2-ply (follow-up to our immediate previous move), while bloating memory footprint and cache contention.
- **Revisit Conditions:** Only revisit if heavily scaled down (e.g. weighted at 10–15% of 1-ply score) or restricted exclusively to deep quiet endgame positions.

---

### 2. Fail-Soft Quiescence Search `bestScore` Tracking (-20.9 Elo)
- **Hypothesis:** Tracking `bestScore` in QS and returning fail-soft scores (and storing `bestScore` in TT) would improve score accuracy for `AlphaBeta` and `CorrectionHistory`.
- **Match Result:** 117.5 / 250 (47.0%) vs v1.0.6, **-20.9 &plusmn; 38.0 Elo** (13.9% LOS).
- **Root Cause:** Quiescence search is an asymmetric cut-off heuristic: captures with $\text{SEE} < 0$ and moves failing delta pruning are completely pruned without search, and quiet moves are omitted. Because unsearched moves are pruned, any score $< \alpha$ does NOT represent the true minimax value of the tactical position. Leaking incomplete, pruning-skewed floor values into parent PVS re-search windows and TT bounds caused severe search tree instability.
- **Revisit Conditions:** Requires strict safeguards:
  1. Never store `TTFlags.UpperBound` in QS if any candidate tactical moves were pruned by Delta or SEE.
  2. Clamp `bestScore` to `Math.Max(bestScore, standPat)` when not in check (the player always retains the option to stand pat).

---

### 3. TT Move Ordering in Quiescence Search (-16.7 Elo)
- **Hypothesis:** Probing the Transposition Table in QS and searching `entry.BestMove` first would increase cutoffs.
- **Match Result:** 119.0 / 250 (47.6%) vs v1.0.4, **-16.7 &plusmn; 34.1 Elo** (16.7% LOS).
- **Root Cause:** Although benchmark tree size dropped by -8.2% (719k to 660k nodes), prioritizing TT moves caused tactical inaccuracies when TT entries were upper bounds or stale from previous iterations, bypassing delta/SEE pruning in QS.
- **Revisit Conditions:** Only probe TT in QS if entry depth $\ge 1$ and flag is `Exact` or `LowerBound` with $\text{SEE} \ge 0$.

---

### 4. Naive ProbCut (-249.6 Elo)
- **Hypothesis:** At depth $\ge 5$, performing a shallow QSearch probe when static eval $\ge \beta + 200$ would detect early fail-highs.
- **Match Result:** 48.0 / 250 (19.2%) vs Step 1.4, **-249.6 &plusmn; 44.0 Elo** (0.0% LOS).
- **Root Cause:** Naive ProbCut without Static Exchange Evaluation (SEE) or material verification pruned essential defensive tactical lines where the opponent had sacrificial winning counter-attacks.
- **Revisit Conditions:** Re-investigate only with strict SEE verification ($\text{SEE} \ge 0$), non-pawn material checks, and running a reduced-depth search ($\text{depth} - 4$) rather than a raw QSearch probe.

---

### 5. Singular Extensions Multi-Cut & Negative Extension (-2.5 Elo)
- **Hypothesis:** Multi-Cut prunes search when an alternative move beats beta at reduced depth; negative extension (`singularExtension = -1`) searches non-singular moves shallower.
- **Match Result:** 2668.0 / 5376 (49.63%) vs v1.0.5, **-2.5 &plusmn; 7.9 Elo** (26.5% LOS).
- **Root Cause:** While fixed-depth bench nodes dropped by -28.5%, it was an illusion: searching non-singular moves 1 ply shallower caused tactical blind spots in complex positions. When the TT move failed low, the engine either played an inferior move or wasted clock researching.
- **Revisit Conditions:** Keep Singular Extension +1 / +2 (Double Extension on wide margin), but completely omit Negative Extension.

---

### 6. Dedicated Tactical Move Generator in QS (-6.4 Elo)
- **Hypothesis:** Generating only captures and promotions when `!inCheck` in `QuiescenceSearch` would eliminate generating and filtering discarded quiet moves.
- **Match Result:** 505.0 / 1029 (49.08%) vs v1.0.3, **-6.4 &plusmn; 21.7 Elo** (15.3% LOS).
- **Root Cause:** While it achieved a +5.4% NPS speedup in isolated benchmarks, in tournament play it altered subtle move-ordering tie-breakers among equal-scoring captures in QSearch selection sort.
- **Revisit Conditions:** Repurposed exclusively for the candidate blunder checker at the root, where move ordering tie-breakers against baseline search are irrelevant.

---

### 7. Incremental `PawnKey` in `Board` (-2.7 Elo)
- **Hypothesis:** Maintaining `ulong PawnKey` incrementally in `Board` during `MakeMove` and `UnmakeMove` would speed up `CorrectionHistory` probing.
- **Match Result:** 4961.5 / 10000 (49.62%) vs v1.0.0, **-2.7 &plusmn; 6.1 Elo** (19.6% LOS).
- **Root Cause:** `MakeMove` and `UnmakeMove` execute millions of times per node across the entire search tree. In contrast, `CorrectionHistory` is only probed at leaf evaluation or quiet move cutoffs. Copying and maintaining extra `PawnKey` fields on every move imposed a per-node tax that outweighed the small savings of lazily bit-scanning 8–12 pawns on demand.
- **Revisit Conditions:** Permanent reject unless the board representation is refactored into a pointer-based state stack.

---

### 8. Lazy NNUE Accumulator Evaluation on Stack (-19.5 Elo)
- **Hypothesis:** Deferring accumulator vector copying and incremental updates until `Evaluate()` is invoked would save compute on cut nodes.
- **Match Result:** 236.0 / 500 (47.2%) vs v1.0.3, **-19.5 &plusmn; 31.1 Elo** (10.6% LOS).
- **Root Cause:** Checking dirty stack flags and looking up stack metadata inside the critical leaf evaluation path adds branching overhead. Additionally, falling back to full `ComputeFull` when non-evaluating evasion plies precede a leaf evaluation introduces latency spikes during sharp tactical exchanges.
- **Revisit Conditions:** Eager incremental updates remain strictly superior for low-depth alpha-beta search.

---

### 9. Bulk Incremental Feature Updates in Accumulator (-8.4 Elo)
- **Hypothesis:** Fusing piece additions and subtractions into single-pass SIMD updates (`Update1Add1Sub` and `Update1Add2Sub`) would cut accumulator memory loads/stores by 50–67% and eliminate loop overhead.
- **Match Result:** 243.0 / 498 (48.8%) vs v1.0.3, **-8.4 &plusmn; 22.8 Elo** (23.6% LOS).
- **Root Cause:** Loading and widening 2–3 feature weight vectors concurrently inside the inner SIMD loop doubles register pressure (requiring 8–11 SIMD registers). On modern x86 hardware with 32–48 KB L1 cache, sequential single-feature updates with 128-bit SIMD (`SubFeature` then `AddFeature`) run with minimal register pressure (only 4–5 registers) and execute cleanly out of L1 cache. The increased register contention resulted in lower overall search throughput (256k vs 300k NPS).
- **Revisit Conditions:** Only reconsider on architectures with 32+ architectural vector registers (e.g. AVX-512 with 32 ZMM registers).

---

### 10. Node Dominance Early Breakout (-12.5 Elo)
- **Hypothesis:** Breaking iterative deepening early if the best move has accumulated $\ge 85\%$ of all node visits at root would bank clock time.
- **Match Result:** 120.5 / 250 (48.2%) vs Step 2.3, **-12.5 &plusmn; 34.2 Elo** (23.6% LOS).
- **Root Cause:** Aborting iterative deepening solely when move visits $\ge 85\%$ broke tactical verification of late refutations; in sharp positions, a failing-low blunder often appears only in the final 10% of deeper iterations.
- **Revisit Conditions:** Must require score lead $> 150\text{ cp}$ over runner-up AND multi-depth stability before early termination.

---

### 11. Managed Array `ulong[]` Repetition Table (-9.9 Elo)
- **Hypothesis:** Replacing inline struct `RepetitionTable` with `ulong[]` would reduce `Board` struct size.
- **Match Result:** 2418.0 / 4978 (Aborted), **-9.9 &plusmn; 8.5 Elo** (<1% LOS).
- **Root Cause:** In C#, `Board` is a struct copied by value. Making `_history` a managed `ulong[]` caused both root and search boards to share the exact same underlying array reference. Search plies overwrote the real game history, causing false threefold repetition claims during winning games and missed repetitions when losing.
- **Revisit Conditions:** **Permanently rejected.** Value-type inline arrays (`[InlineArray]`) or fixed buffers must always be used for board history state.

---

### 12. Improving in NMP (-15.3 Elo)
- **Hypothesis:** Adding $r{+}{+}$ during Null Move Pruning when the position is improving would speed up search in advantageous positions.
- **Match Result:** 119.5 / 250 (47.8%) vs v0.0.4, **-15.3 &plusmn; 37.4 Elo** (21.0% LOS).
- **Root Cause:** Increasing NMP reduction when improving caused critical tactical zugzwang defenses and counter-sacrifices to be missed, turning won positions into losses.
- **Revisit Conditions:** Keep NMP reduction strictly dependent on depth, static eval vs beta, and pawn topology openness.
