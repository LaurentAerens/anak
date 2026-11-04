# Anak Chess Engine — Failed Experiments & Revisit Archive

This document preserves the hypotheses, match results, root-cause analyses, and future revisit conditions for all optimization candidates, pruning ideas, and search heuristics that were experimentally tested and rejected.

---

## 📊 Summary of Rejected Experiments

| Step / Ref | Candidate / Hypothesis | Phase | Match Result / Games | Elo Delta | LOS | Status |
| :---: | :--- | :---: | :---: | :---: | :---: | :---: |
| **v1.0.12 — CPW 2.5** | Selective / Restricted Check Extensions | Search Extensions | 113.0 / 250 vs v1.0.12 | **-33.5 &plusmn; 36.1** | 3.3% | ❌ Reverted |
| **v1.0.12 — CPW 2.4** | Passed Pawn Push Extensions (+1 ply rank 6/7) | Search Extensions | 1492.0 / 3094 vs v1.0.12 | **-12.4 &plusmn; 10.8** | 0.8% | ❌ Reverted |
| **v1.0.12 — CPW 2.3** | One-Reply / Forced Move Extensions | Search Extensions | 110.5 / 250 vs v1.0.12 | **-40.5 &plusmn; 37.8** | 1.7% | ❌ Reverted |
| **v1.0.12 — CPW 2.2** | Recapture Extensions | Search Extensions | 117.0 / 250 vs v1.0.12 | **-22.3 &plusmn; 40.6** | 13.9% | ❌ Reverted |
| **v1.0.12 — CPW 2.1** | Margin-Based Double Singular Extensions | Search Extensions | 110.0 / 250 vs v1.0.12 | **-41.9 &plusmn; 35.2** | 0.9% | ❌ Reverted |
| **v1.0.12 — CPW 1.5** | Classical Multi-Cut Pruning (Björnsson 1998) | Search Pruning | 112.0 / 250 vs v1.0.12 | **-36.3 &plusmn; 39.0** | 3.3% | ❌ Reverted |
| **v1.0.12 — CPW 4b** | Gated Cut-Node Adaptive LMR | Search Pruning | 3996.0 / 8000 vs v1.0.12 | **-0.3 &plusmn; 6.5** | 45.8% | ❌ Reverted (Neutral) |
| **v1.0.12 — CPW 4** | Naive Cut-Node Adaptive LMR | Search Pruning | 3932.0 / 8000 vs v1.0.12 | **-5.9 &plusmn; 6.6** | 3.9% | ❌ Reverted |
| **v1.0.12 — CPW 3** | History-Directed Late Move Pruning | Search Pruning | 4002.0 / 8000 vs v1.0.12 | **+0.2 &plusmn; 6.6** | 52.1% | ❌ Reverted (Neutral) |
| **v1.0.12 — CPW 2** | ProbCut with Strict SEE Safeguards | Search Pruning | 106.5 / 250 vs v1.0.12 | **-51.8 &plusmn; 31.9** | 0.06% | ❌ Reverted |
| **v1.0.12 — CPW 1** | Classical Razoring at Depth <= 3 | Search Pruning | 117.0 / 250 vs v1.0.12 | **-22.3 &plusmn; 36.0** | 11.1% | ❌ Reverted |
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

### 0e. Gated Cut-Node Adaptive LMR (-0.3 Elo — Neutral Wash)
- **Hypothesis:** Gating Cut-Node Adaptive LMR with $\text{depth} \ge 6$, $\text{movesSearched} \ge 4$, and $\text{conthist} \le 0$ will eliminate the re-search cascades, shallow tactical blunders, and timeout surges seen in the naive version, unlocking true positive Elo.
- **Match Results:**
  - *Tier 1 Sanity (250 games at `1+0.01`):* 130.5 / 250 (52.20%, W: 118, L: 107, D: 25), **+15.3 &plusmn; 35.5 Elo**, LOS: 80.2%, Timeouts: Dev 32 vs Base 34. (Passed sanity gate).
  - *Tier 2 Validation SPRT (8,000 games at `1+0.01`):* 3996.0 / 8000 (49.95%, W: 3388, L: 3396, D: 1216), **-0.3 &plusmn; 6.5 Elo**, LOS: 45.8%, Timeouts: Dev 528 vs Base 558, LLR: -0.38 [-2.94, +2.94].
- **Root Cause:** The gating completely fixed the timeout deficit and eliminated the tactical regression from naive Cut-Node LMR (improving score from $-5.9$ Elo to $-0.3$ Elo across 8,000 games). However, across the full 8,000 games, the $+1$ reduction on quiet moves at Cut-nodes provided no measurable net advantage: Anak already heavily reduces late quiet moves via its topology-adjusted logarithmic LMR table and continuation history modifiers. Adding another layer of reduction on top produced an exact wash.
- **Revisit Conditions:** Only revisit if integrated as a soft dynamic reduction factor (e.g. $+0.5$ in integer arithmetic or fractional LMR) or tied to opponent threat analysis.

---

### 0d. Naive Cut-Node Adaptive LMR (-5.9 Elo)
- **Hypothesis:** Passing a boolean `cutNode` down PVS calls and applying $+1$ extra LMR reduction to quiet moves on expected Cut-nodes will prune inferior branches faster where a beta cutoff is expected.
- **Match Results:**
  - *Tier 1 Sanity (250 games at `1+0.01`):* 127.0 / 250 (50.80%, W: 112, L: 108, D: 30), **+5.6 &plusmn; 38.5 Elo**, LOS: 61.2%.
  - *Tier 2 Validation SPRT (8,000 games at `1+0.01`):* 3932.0 / 8000 (49.15%, W: 3356, L: 3492, D: 1152), **-5.9 &plusmn; 6.6 Elo**, LOS: 3.89%, LLR: -1.66.
- **Root Cause:**
  1. *Re-search Cascades & Clock Bleed:* Applying $+1$ reduction to all quiet moves unconditionally caused moves that were actually strong to fail low at reduced depth, only to trigger full-depth re-searches. This caused excessive node re-search cascades and clock overruns in complex endgames (Dev suffered 109 timeouts in games 6,501–8,000 vs 35 for Base).
  2. *Shallow Tactical Blindness:* At shallow depths ($\text{depth} < 6$), reducing quiet moves by $+1$ pruned tactical defenses, converting winning positions into blunders.
- **Revisit Conditions (Attempted as 1.4b):** Gate with:
  1. $\text{depth} \ge 6$ (never reduce shallow trees).
  2. $\text{movesSearched} \ge 4$ (protect the first 3 alternatives to TT move).
  3. $\text{conthist} \le 0$ (do not reduce moves with strong positive continuation history).

---

### 0c. History-Directed Late Move Pruning (+0.2 Elo — Neutral Wash)
- **Hypothesis:** At shallow depths ($\text{depth} \le 4$), quiet moves with severely negative butterfly and continuation history scores ($\text{historyScore} < -\text{threshold} \times \text{depth}$) are virtually hopeless; pruning them outright will accelerate the search and allow deeper effective search depth.
- **Match Results:**
  - *Tier 1 Sanity (250 games at `1+0.01`):* 125.0 / 250 (50.00%, W: 92, L: 92, D: 66), **-0.0 &plusmn; 32.8 Elo**.
  - *SPSA Optimization (8,000 games across 80 steps):* Early stopped upon parameter stabilization. Threshold converged from initial guess 4000 to **3613**.
  - *Tier 2 Validation SPRT (8,000 games at `1+0.01`):* 4002.0 / 8000 (50.02%, W: 3434, L: 3430, D: 1136), **+0.2 &plusmn; 6.6 Elo**, LLR: -0.26 [-2.94, +2.94].
- **Root Cause:** In Anak, moves with deeply negative history scores are already ordered at the very tail end of the move list and subjected to aggressive Late Move Reductions (LMR) with history malus, which already reduces them to depth 1. Searching a quiet move at depth 1 takes virtually no nodes, but provides an essential tactical safety net that catches rare counter-intuitive quiet defenses or waiting moves. Completely skipping that 1-ply search yielded negligible node savings while occasionally causing blunders, producing an exact wash (+0.2 Elo across 8,000 games).
- **Revisit Conditions:** Only revisit if integrated directly into the LMR reduction formula (e.g. extending LMR reductions rather than hard pruning) or paired with threat-detection safeguards.

---

### 0a. ProbCut with Strict SEE Safeguards (-51.8 Elo)
- **Hypothesis:** At depth $\ge 5$, performing a shallow verification search ($\text{depth} - 4$) with an elevated threshold ($\beta + 200$) strictly on winning/neutral tactical captures/promotions ($\text{SEE} \ge 0$) will safely prune branches that are virtually guaranteed to fail high.
- **Match Result:** 106.5 / 250 (42.60%, W: 71, L: 108, D: 71) vs v1.0.12 baseline, **-51.8 &plusmn; 31.9 Elo** (0.06% LOS). Hard regression (< 47.0%) triggered immediate revert.
- **Root Cause:** At shallow reduced depth ($\text{depth} - 4$, which equals depth 1 when node depth is 5), speculative tactical captures easily beat the elevated beta because the opponent's defensive refutation or counter-trap (e.g. counter-trapping the queen, counter-sacrifices, or Zwischenzugs) requires 2 or 3 plies to detect. Because the shallow search falsely claimed a fail-high, ProbCut triggered a premature beta cutoff on the entire parent subtree, converting winning or equal positions into tactical blunders.
- **Revisit Conditions:** Only revisit at much deeper starting depths ($\text{depth} \ge 8$ or $10$), with dynamic margins linked to opponent piece count and threat detection, or when `probCutDepth` is at least 3 or 4 plies deep.

---

### 0b. Classical Razoring at Depth <= 3 (-22.3 Elo)
- **Hypothesis:** At shallow depths ($\text{depth} \le 3$), if static eval is hopelessly below alpha ($\text{staticEval} + 300 + 150d \le \alpha$), dropping early into Quiescence Search with bound $\alpha - \text{margin}$ will prune fruitless subtrees early.
- **Match Result:** 117.0 / 250 (46.80%, W: 90, L: 106, D: 54) vs v1.0.12 baseline, **-22.3 &plusmn; 36.0 Elo** (11.1% LOS). Hard regression (< 47.0%) triggered immediate revert.
- **Root Cause:** In modern NNUE engines, positions where static evaluation is severely behind often hinge on critical quiet defensive resources (king escapes, interpositions, pawn shield maneuvers, counter-attacks, trapping an attacking piece). Because Quiescence Search strictly explores captures and promotions, falling back to QS when static evaluation was low completely pruned all candidate non-tactical moves, causing tactical blunders and dropped games in positions that were otherwise defensible.
- **Revisit Conditions:** Only reconsider if combined with threat detection (e.g. only if opponent has no queen/major piece threats) or restricted strictly to depth 1 where QS explores checking moves.

---

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

---

### 13. Classical Multi-Cut Pruning (-36.3 Elo)
- **Technique:** Section 1.5 in `docs/CPW_TECHNIQUES_TRACKER.md` (Björnsson & Marsland 1998, 2001).
- **Hypothesis:** At expected Cut-nodes with depth $\ge 6$, checking the top $M = 6$ moves at reduced depth ($\text{depth} - 1 - 3$) and pruning immediately when $C \ge 2$ moves fail high ($\ge \beta$) would save searching remaining moves and avoid full-depth re-searches.
- **Match Result:** Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 112.0 / 250 (44.80%, W: 95, L: 121, D: 34), **-36.3 &plusmn; 39.0 Elo**, LOS: 3.25%. Timeouts: Dev 15 vs Base 18.
- **Root Cause:**
  1. **Premature Speculative Cutoffs without Full Verification:** Unlike LMR (which verifies any fail-high with a full-depth re-search before cutting off), classical Multi-Cut returns $\beta$ immediately based on 2 reduced-depth searches at $d - 4$. In modern engines with deep tactics, two superficially active moves (sacrifices, aggressive checks) often fail high at shallow depths because the opponent's refutation only appears 3–4 plies later. Cutting off without verification caused severe tactical blunders.
  2. **Overhead on All-Nodes:** At All-nodes of depth $\ge 6$, searching up to 6 moves at $d - 4$ is pure overhead because no move beats $\beta$.
  3. **Redundancy with Modern LMR:** Modern engines already search late quiet moves at logarithmic depth reductions. Multi-Cut was developed in 1998 for engines that searched all non-tactical moves at full depth ($d - 1$). In the modern LMR era, unverified shallow cutoffs introduce severe search instability for zero net benefit.
- **Revisit Conditions:** Modern engines (e.g. Stockfish) have completely abandoned classical Multi-Cut in favor of Singular Extensions and History-directed LMR. Classical Multi-Cut is permanently retired.

---

### 14. Margin-Based Double Singular Extensions (-41.9 Elo)
- **Technique:** Section 2.1 in `docs/CPW_TECHNIQUES_TRACKER.md`.
- **Hypothesis:** Restricting double singular extension (+2 plies) to cases where the singular verification search fails low by an additional safety margin below `singularBeta` (`sScore < singularBeta - (DoubleSingularMargin * depth)`) would prevent over-extending borderline singular moves and reduce search tree bloat.
- **Match Result:** Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 110.0 / 250 (44.00%, W: 93, L: 123, D: 34), **-41.9 &plusmn; 35.2 Elo**, LOS: 0.90%. Timeouts: Dev 31 vs Base 25.
- **Root Cause:**
  1. **Alpha-Bound Truncation in Verification Search:** The singular verification search is conducted with a narrow scout window `[singularBeta - 1, singularBeta]`. In alpha-beta search with pruning, failing moves frequently return scores bounded at or close to `alpha` (`singularBeta - 1`). Consequently, `sScore` almost never drops below `singularBeta - (6 * depth)` (which requires alternative moves to return $> 36\text{ cp}$ below `alpha`).
  2. **Complete Starvation of +2 Extensions:** Because the extra margin was virtually unattainable through the scout window, the double extension rate collapsed to near 0%. Almost all singular moves in non-PV fail-high positions that previously received $+2$ plies were demoted to $+1$ ply.
  3. **Tactical Truncation on Forcing Lines:** In Anak, the established $+2$ extension on Cut-nodes (`!isPv && ttScore >= beta`) is a vital tactical engine: it rapidly establishes conclusive refutations on critical forcing moves. Starving these nodes of their second extension resulted in tactical failures and blunders in sharp positions.
- **Revisit Conditions:** Retain the baseline singular extension formulation (`singularExtension = (!isPv && ttScore >= beta) ? 2 : 1`). Any multi-margin extension requires re-architecting the verification search window or using open fail-soft bounds.

---

### 15. Recapture Extensions (-22.3 Elo)
- **Technique:** Section 2.2 in `docs/CPW_TECHNIQUES_TRACKER.md`.
- **Hypothesis:** Extending by $+1$ ply when the current move is a recapture on the exact square of the opponent's previous capture (`move.To == prev1To` with non-negative SEE) would ensure tactical piece exchanges are fully resolved in the main tree, mitigating the horizon effect.
- **Match Result:** Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 117.0 / 250 (46.80%, W: 99, L: 115, D: 36), **-22.3 &plusmn; 40.6 Elo**, LOS: 13.92%. Timeouts: Dev 32 vs Base 25.
- **Root Cause:**
  1. **Redundancy with Quiescence Search (QS):** In modern engines like Anak, tactical piece trades and recaptures are already handled cleanly and completely by the Quiescence Search at leaf nodes (which evaluates all captures until a quiet position is reached).
  2. **Search Tree Dilation & Clock Pressure:** Adding an extension in the main tree on trade sequences inflates the branching factor and consumes node budget on forcing exchanges that QS resolves for free. This node dilation caused severe clock overruns (32 timeouts vs 25) and shallower overall iteration depth in non-tactical phases.
- **Revisit Conditions:** Modern engines have permanently retired manual capture/recapture extensions in favor of Quiescence Search and Singular Extensions. Permanently retired.

---

### 16. One-Reply / Forced Move Extensions (-40.5 Elo)
- **Technique:** Section 2.3 in `docs/CPW_TECHNIQUES_TRACKER.md`.
- **Hypothesis:** When the current position has exactly 1 legal move (`picker.HasOnlyOneLegalMove()`), extending search by $+1$ ply has a branching factor of 1 ($b = 1$) and should prevent horizon truncation on forced sequences at negligible node cost.
- **Match Result:** Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 110.5 / 250 (44.20%, W: 91, L: 120, D: 39), **-40.5 &plusmn; 37.8 Elo**, LOS: 1.68%. Timeouts: Dev 15 vs Base 6.
- **Root Cause:**
  1. **Interaction with Blanket Check Extensions:** In Anak, check extension already exists unconditionally: every check position receives `if (inCheck) depth++`. When black is in check and has only 1 evasion, adding a one-reply extension stacked with the check extension produced a $+2$ plies extension, rapidly expanding check-forcing subtrees far deeper than the nominal search horizon.
  2. **Explosion at the Successor Node:** Although the one-reply node itself has $b = 1$, the opponent's reply at the very next ply is typically NOT forced (branching factor $b \ge 30$). Searching all 30 of the opponent's replies at full extended depth multiplied the size of the subtree exponentially, consuming search time on forcing sequences and causing a 2.5x increase in timeout losses (15 timeouts vs 6).
- **Revisit Conditions:** Modern engines (like Stockfish) avoid hard-coded ply extensions for checks and one-replies, relying instead on Singular Extensions and Quiescence Search. Permanently retired.

---

### 17. Passed Pawn Push Extensions (-12.4 Elo)
- **Technique:** Section 2.4 in `docs/CPW_TECHNIQUES_TRACKER.md`.
- **Hypothesis:** Extending by $+1$ ply when an unblocked passed pawn advances to the 6th or 7th rank (`rank >= 6` or `rank <= 1`) would prevent horizon truncation in pawn promotion races and deep endgames.
- **Match Result:**
  - Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 126.5 / 250 (50.60%, W: 103, L: 100, D: 47), **+4.2 &plusmn; 38.2 Elo**, LOS: 58.51%. Graduated to Tier 2.
  - Tier 2 Validation (SPRT [0.0, 3.0] vs v1.0.12, 3,094 games): Score 1492.0 / 3094 (48.22%, W: 1287, L: 1397, D: 410), **-12.4 &plusmn; 10.8 Elo**, LLR: -1.24, LOS: 0.84%. **Failed (Regression) &rarr; Rejected & Reverted**.
- **Root Cause:**
  1. **Tree Dilation in Complex Positions:** While passed pawn extensions demonstrated clear node-count speedups in pure, isolated endgame positions (such as the canonical test suites), in real middlegames and complex endgames with multiple pieces, pushing a passed pawn to the 6th/7th rank opens dozens of tactical responses for the defending side. Extending the pusher by $+1$ ply forces the entire subtree of defensive resources to be searched deeper as well, bloating the tree and starving critical tactical variations elsewhere.
  2. **Redundancy with Multi-Tier Neural Networks:** Modern NNUE evaluation already heavily prioritizes advanced passed pawns and applies nonlinear bonuses to passers on the 6th and 7th ranks. The engine naturally targets and plays these pawn pushes without needing artificial search tree dilation.
- **Revisit Conditions:** Modern engines have moved away from manual geometric/piece-specific ply extensions in favor of singular extensions and time management extensions. Permanently retired.

---

### 18. Selective / Restricted Check Extensions (-33.5 Elo)
- **Technique:** Section 2.5 in `docs/CPW_TECHNIQUES_TRACKER.md`.
- **Hypothesis:** Restricting blanket $+1$ check extensions (`if (inCheck) depth++`) to PV-only nodes (`isPv && inCheck`) or completely removing them will eliminate search tree bloat and improve NPS/throughput without missing critical tactical lines.
- **Match Results:**
  - *Variant A (Complete Removal of Check Extensions):* Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 121.0 / 250 (48.40%, W: 101, L: 109, D: 40), **-11.1 &plusmn; 38.9 Elo**, LOS: 28.65%.
  - *Variant B (PV-Only Check Extensions):* Tier 1 Sanity (250 games at `1+0.01` vs v1.0.12): Score 113.0 / 250 (45.20%, W: 78, L: 102, D: 70), **-33.5 &plusmn; 36.1 Elo**, LOS: 3.34%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**.
- **Root Cause:**
  1. **Scout / Cut-Node Premature Cutoffs & Inaccuracy:** In modern PVS engines, over 90% of searches occur at non-PV nodes using null-window scout searches (`[alpha, alpha + 1]`). When non-PV check extensions are disabled, scout searches reach leaf/quiescence depth 1 ply earlier. If the opponent delivers a series of checks, the scout search fails to see the refutation (or erroneously evaluates an escape as winning), leading to either false cutoffs or expensive re-search storms when PV verification is forced.
  2. **Tactical Depth Deficit:** In complex mating or forcing check situations, 1 ply of depth is decisive. Blanket check extensions (`if (inCheck) depth++`) remain the gold standard in Anak for maintaining complete tactical vision across all branches.
- **Revisit Conditions:** Retain baseline blanket check extensions (`if (inCheck && depth < MaxSearchDepth) depth++;`). Only revisit if combined with comprehensive evasion tree searching and tactical check generation in Quiescence Search.
