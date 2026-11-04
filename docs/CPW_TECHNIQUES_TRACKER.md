# Anak Engine — CPW Techniques & Classical Search Optimization Tracker

This document tracks search, pruning, movegen, and move-ordering techniques cataloged on [Chess Programming Wiki (chessprogramming.org)](https://chessprogramming.org/) to evaluate, benchmark, and progress across multiple development machines.

> **Note:** Cross-referenced against previous optimization history ([`RELEASE_NOTES.md`](../RELEASE_NOTES.md)) and the failed experiments archive ([`docs/FAILED_IDEAS.md`](FAILED_IDEAS.md)). Features previously tested and rejected are documented in [Section 7](#7-previously-tested--rejected-techniques-from-cpw-archive) to prevent duplicate work.

---

## 🚦 Active Untried Techniques

| # | Technique | Category | Status | Expected Impact | Primary File(s) |
|---|---|---|---|---|---|
| 1 | **Razoring** | Pruning | `[-] Tested & Rejected` | +3 to +8 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 2 | **ProbCut with Strict SEE Safeguards** | Pruning | `[-] Tested & Rejected` | +5 to +10 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 3 | **History-Directed Late Move Pruning** | Pruning | `[-] Tested & Rejected` | +3 to +6 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 4 | **Cut-Node Adaptive LMR** | Reductions | `[-] Tested & Rejected` | +5 to +10 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 5 | **Multi-Cut Pruning (Classical)** | Pruning | `[-] Tested & Rejected` | +0 to +4 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 6 | **Double Singular Extensions (Margin-Based)** | Extensions | `[-] Tested & Rejected` | +5 to +8 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 7 | **Recapture Extensions** | Extensions | `[-] Tested & Rejected` | +2 to +5 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 8 | **One-Reply / Forced Move Extensions** | Extensions | `[-] Tested & Rejected` | +2 to +4 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 9 | **Passed Pawn Push Extensions** | Extensions | `[-] Tested & Rejected` | +3 to +7 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 10 | **Selective Check Extensions** | Extensions | `[ ] Not Started` | Speed & Tree Stability | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 11 | **Checks in Quiescence Search** | Quiescence | `[ ] Not Started` | +4 to +8 Elo | `src/Anak.Search/Minimax/QuiescenceSearch.cs` |
| 12 | **Advanced Passed Pawn Pushes in QS** | Quiescence | `[ ] Not Started` | +3 to +6 Elo | `src/Anak.Search/Minimax/QuiescenceSearch.cs` |
| 13 | **Dedicated Countermove History Table (CMH)**| Move Ordering | `[ ] Not Started` | +5 to +8 Elo | `ContinuationHistory.cs`, `MovePicker.cs` |
| 14 | **Relative History Normalization** | Move Ordering | `[ ] Not Started` | +2 to +5 Elo | `HistoryTable.cs`, `AlphaBetaEngine.cs` |
| 15 | **Ordering Bad Captures by SEE** | Move Ordering | `[ ] Not Started` | +1 to +3 Elo | `src/Anak.Search/Minimax/MovePicker.cs` |
| 16 | **Fail-Low Soft-Time Extension** | Time Management | `[ ] Not Started` | +3 to +6 Elo (Clock) | `TimeManager.cs`, `AlphaBetaEngine.cs` |

Legend:
- `[ ] Not Started`
- `[/] In Progress / Under Testing`
- `[x] Implemented & Accepted (SPRT passed)`
- `[-] Tested & Rejected (Regression or Neutral)`

---

## 1. Search Pruning & Reductions

### 1.1 Razoring
- **CPW Reference:** [https://chessprogramming.org/Razoring](https://chessprogramming.org/Razoring)
- **Concept:** At low depths ($\text{depth} \le 3$), if `staticEval + razorMargin <= alpha`, the position is likely futile. Drop immediately into Quiescence Search with bound `alpha - razorMargin`. If QS fails low, prune the entire subtree and return `alpha`.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Implementation Blueprint:**
  ```csharp
  if (!isPv && !inCheck && depth <= 3 && _stack[ply].ExcludedMove.IsNull)
  {
      int razorMargin = 300 + 150 * depth;
      if (eval + razorMargin <= alpha)
      {
          int razorScore = _qsearch.Search(ref board, alpha - razorMargin, alpha - razorMargin + 1, ply);
          if (razorScore <= alpha - razorMargin)
          {
              return razorScore;
          }
      }
  }
  ```
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 117.0 / 250 (46.80%, W: 90, L: 106, D: 54), **-22.3 &plusmn; 36.0 Elo**, LOS 11.12%. **Hard Regression &rarr; Rejected & Reverted immediately**.
  - [ ] Tier 2 Validation: *(N/A — rejected at Tier 1)*

---

### 1.2 ProbCut with Strict SEE Safeguards
- **CPW Reference:** [https://chessprogramming.org/ProbCut](https://chessprogramming.org/ProbCut)
- **History in Anak:** A naive ProbCut in `v0.0.5` without SEE gating pruned defensive tactical lines and collapsed (-249.6 Elo).
- **Concept:** At depth $\ge 5$, perform a shallow search ($\text{depth} - 4$) with an elevated threshold (`beta + margin`) strictly on captures/promotions that pass Static Exchange Evaluation ($\text{SEE} \ge 0$). If the shallow tactical search fails high, with high statistical probability the full-depth search will also fail high. Return `beta`.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 106.5 / 250 (42.60%, W: 71, L: 108, D: 71), **-51.8 &plusmn; 31.9 Elo**, LOS 0.06%. **Hard Regression &rarr; Rejected & Reverted immediately**.
  - [ ] Tier 2 Validation: *(N/A — rejected at Tier 1)*

---

### 1.3 History-Directed Late Move Pruning (History Pruning)
- **CPW Reference:** [https://chessprogramming.org/History_Heuristic#History_Pruning](https://chessprogramming.org/History_Heuristic#History_Pruning)
- **Concept:** Rather than relying solely on move count for LMP ($N \ge 3 + 2d^2$), prune quiet moves when the combined butterfly + continuation history score falls below a negative depth-scaled threshold (e.g. `historyScore < -threshold * depth`).
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`, `src/Anak.Search/Minimax/Pruning/SearchHeuristics.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 125.0 / 250 (50.00%, W: 92, L: 92, D: 66), **-0.0 &plusmn; 32.8 Elo** (Neutral gate passed).
  - [x] SPSA Exhaustive Tuning: 8,000 games (80 steps $\times$ 100 games at `1+0.01`, 16 threads). Early stopped at step 80 upon full parameter convergence over 40 steps / 4,000 games. Optimal threshold converged to **3613** (from initial 4000).
  - [x] Tier 2 Validation: SPRT [0.0, 3.0] vs v1.0.12 (8,000 games played, LLR: -0.26): Score 4002.0 / 8000 (50.02%), **+0.2 &plusmn; 6.6 Elo**. **Neutral Wash (Failed to reach H1 +3.0) &rarr; Reverted**.

---

### 1.4 Cut-Node Adaptive LMR (Naive)
- **CPW Reference:** [https://chessprogramming.org/Node_Types](https://chessprogramming.org/Node_Types)
- **Concept:** Pass a boolean flag `cutNode` down PVS calls. Expected Cut-nodes receive $+1$ extra LMR reduction for quiet moves.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 127.0 / 250 (50.80%, W: 112, L: 108, D: 30), **+5.6 &plusmn; 38.5 Elo**.
  - [x] Tier 2 Validation: SPRT [0.0, 3.0] vs v1.0.12: Score 3932.0 / 8000 (49.15%, W: 3356, L: 3492, D: 1152), **-5.9 &plusmn; 6.6 Elo**, LLR: -1.66. **Failed &rarr; Rejected**. Root cause: Excessive re-search cascades and clock overruns on late moves at shallow depths.

---

### 1.4b Gated Cut-Node Adaptive LMR
- **CPW Reference:** [https://chessprogramming.org/Node_Types](https://chessprogramming.org/Node_Types)
- **Status:** [/] In Progress / Under Testing
- **Concept:** Add 3 strict safeguards to Cut-Node LMR:
  1. **Depth Gating:** Only apply at $\text{depth} \ge 6$ to protect shallow tactical trees.
  2. **Move Index Gating:** Only apply at $\text{movesSearched} \ge 4$ (protecting the top 3 quiet/killer alternatives).
  3. **History Gating:** Only apply if $\text{conthist} \le 0$ (never over-reduce quiet moves with demonstrated positive transition history).
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 130.5 / 250 (52.20%, W: 118, L: 107, D: 25), **+15.3 &plusmn; 35.5 Elo**, LOS: 80.19%. (Passed & graduated to Tier 2).
  - [x] Tier 2 Validation: SPRT [0.0, 3.0] vs v1.0.12 (8,000 games played, LLR: -0.38): Score 3996.0 / 8000 (49.95%, W: 3388, L: 3396, D: 1216), **-0.3 &plusmn; 6.5 Elo**, Timeouts: Dev 528 vs Base 558. **Neutral Wash (Failed to reach H1 +3.0) &rarr; Reverted**.

---

### 1.5 Multi-Cut Pruning (Classical)
- **CPW Reference:** [https://chessprogramming.org/Multi-Cut](https://chessprogramming.org/Multi-Cut)
- **Concept:** At expected Cut-nodes with depth $\ge 6$, search the first $M$ moves (e.g. 6 moves) with reduced depth ($\text{depth} - 1 - R$, where $R = 3$). If $C \ge 2$ moves fail high, cut immediately without full search.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 112.0 / 250 (44.80%, W: 95, L: 121, D: 34), **-36.3 &plusmn; 39.0 Elo**, LOS: 3.25%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**.
  - [ ] Tier 2 Validation: N/A (Failed Tier 1 Sanity Gate)

---

## 2. Search Extensions

### 2.1 Double Singular Extensions (Margin-Based Only)
- **CPW Reference:** [https://chessprogramming.org/Singular_Extensions](https://chessprogramming.org/Singular_Extensions)
- **History in Anak:** Negative singular extension (reducing non-singular moves) failed in Step 2.5 (-2.5 Elo). Double extension on wide margin remains open.
- **Concept:** If the singular search score is far below singular beta (`sScore < singularBeta - margin2`), the TT move is overwhelmingly superior; extend by $+2$ plies instead of $+1$.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 110.0 / 250 (44.00%, W: 93, L: 123, D: 34), **-41.9 &plusmn; 35.2 Elo**, LOS: 0.90%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**.
  - [ ] Tier 2 Validation: N/A (Failed Tier 1 Sanity Gate)

---

### 2.2 Recapture Extensions
- **CPW Reference:** [https://chessprogramming.org/Recapture_Extensions](https://chessprogramming.org/Recapture_Extensions)
- **Concept:** When the current move is a capture on the exact square of the opponent's previous capture (`move.To == prev1To`), extend by $+1$ ply to ensure full resolution of tactical trades.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 117.0 / 250 (46.80%, W: 99, L: 115, D: 36), **-22.3 &plusmn; 40.6 Elo**, LOS: 13.92%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**.
  - [ ] Tier 2 Validation: N/A (Failed Tier 1 Sanity Gate)

---

### 2.3 One-Reply / Forced Move Extensions
- **CPW Reference:** [https://chessprogramming.org/One_Reply_Extensions](https://chessprogramming.org/One_Reply_Extensions)
- **Concept:** When in check or responding to a threat where the position has **only 1 legal evasion**, extend search by $+1$ ply (branching factor is 1, so the cost in tree size is near zero).
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 110.5 / 250 (44.20%, W: 91, L: 120, D: 39), **-40.5 &plusmn; 37.8 Elo**, LOS: 1.68%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**. Root cause: Blanket check extensions already exist in Anak (`if (inCheck) depth++`). Adding single-reply extensions stacked to $+2$ plies on check evasions, and pushing forced replies by $+1$ ply dilated the search horizon for the opponent (whose subsequent branching factor is 30+), causing severe clock deficits (15 timeouts vs 6).
  - [ ] Tier 2 Validation: N/A (Failed Tier 1 Sanity Gate)

---

### 2.4 Passed Pawn Push Extensions
- **CPW Reference:** [https://chessprogramming.org/Passed_Pawn_Extensions](https://chessprogramming.org/Passed_Pawn_Extensions)
- **Concept:** Extend by $+1$ ply when an unblocked passed pawn advances to the 6th or 7th rank (`rank >= 6`), preventing horizon truncation in promotion races.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 126.5 / 250 (50.60%, W: 103, L: 100, D: 47), **+4.2 &plusmn; 38.2 Elo**, LOS: 58.51%. (Passed Tier 1 Sanity Gate &rarr; Graduated to Tier 2).
  - [x] Tier 2 Validation: SPRT [0.0, 3.0] vs v1.0.12 (3,094 games played): Score 1492.0 / 3094 (48.22%, W: 1287, L: 1397, D: 410), **-12.4 &plusmn; 10.8 Elo**, LLR: -1.24, LOS: 0.84%. **Failed (Regression) &rarr; Rejected & Reverted**. Root cause: Extending passed pawn pushes causes tree dilation at non-terminal plies. Opponents have numerous defensive resources that must all be searched deeper, increasing node consumption without yielding tactical breakthroughs in positions that aren't strictly winning pawn races. Modern NNUE evaluations already assign massive positional values to advanced passers without requiring search extensions.

---

### 2.5 Selective / Restricted Check Extensions
- **CPW Reference:** [https://chessprogramming.org/Check_Extensions](https://chessprogramming.org/Check_Extensions)
- **Concept:** Anak currently extends all checks unconditionally (`if (inCheck) depth++;`). Restrict check extensions to positions with positive SEE, non-PV nodes, or when evasion count is small, eliminating tactical search explosions.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12):
    - *Variant A (Complete Removal):* Score 121.0 / 250 (48.40%, W: 101, L: 109, D: 40), **-11.1 &plusmn; 38.9 Elo**, LOS: 28.65%.
    - *Variant B (PV-Only Check Extensions):* Score 113.0 / 250 (45.20%, W: 78, L: 102, D: 70), **-33.5 &plusmn; 36.1 Elo**, LOS: 3.34%. **Hard Regression (< 47.0%) &rarr; Rejected & Reverted**. Root cause: Blanket check extensions in alpha-beta ensure that both PV and scout searches reliably resolve check threats. Gating check extensions starves non-PV null-window searches of the necessary depth to refute threats, causing premature fail-highs, tactical blunders, and search instability.
  - [ ] Tier 2 Validation: N/A (Failed Tier 1 Sanity Gate)

---

## 3. Quiescence Search (QS)

### 3.1 Checks in Quiescence Search
- **CPW Reference:** [https://chessprogramming.org/Quiescence_Search#Checks](https://chessprogramming.org/Quiescence_Search#Checks)
- **Status:** [x] Validated & Released in `v1.0.13` (+15.1 Elo)
- **Concept:** At ply 0 (or plies 0–1) of QS, generate and search quiet giving-checks (with positive SEE or non-futile check masks) to avoid blundering to immediate forcing checks at the root of the qsearch tree.
- **Target Files:** `src/Anak.Search/Minimax/QuiescenceSearch.cs`
- **Testing Checklist:**
  - [x] Tier 1 Sanity: 250 games (`1+0.01` vs v1.0.12): Score 128.0 / 250 (51.20%, W: 97, L: 91, D: 62), **+8.3 &plusmn; 36.0 Elo**, LOS: 67.55%, PairsRatio: 1.08. (Passed sanity gate &rarr; Graduated to Tier 2).
  - [x] Tier 2 Validation: 6,008 games total across Tier 1 + Tier 2 vs v1.0.12 @ 1+0.01: Score 3,134.0 / 6,008 (52.16%, W: 2416, L: 2156, D: 1436), **+15.1 &plusmn; 7.2 Elo**, LOS: **>99.9%**, PairsRatio: 1.23. **Passed & Validated &rarr; Integrated into Release v1.0.13**.

---

### 3.2 Advanced Passed Pawn Pushes in QS
- **CPW Reference:** [https://chessprogramming.org/Quiescence_Search#Passed_Pawns](https://chessprogramming.org/Quiescence_Search#Passed_Pawns)
- **Concept:** Include pawn pushes to rank 7 (White) or rank 2 (Black) in QS candidate moves, resolving immediate promotion threats.
- **Target Files:** `src/Anak.Search/Minimax/QuiescenceSearch.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

## 4. Move Ordering & History Heuristics

### 4.1 Dedicated Countermove History Table (CMH Table)
- **CPW Reference:** [https://chessprogramming.org/Countermove_Heuristic](https://chessprogramming.org/Countermove_Heuristic)
- **Concept:** Anak currently tracks a single best countermove per `[prevPiece, prevTo]`. Implement a full score table `[16 piece][64 to][16 piece][64 to]` giving a scalar history score for any quiet move played in response to the opponent's previous move, blended with Continuation History.
- **Target Files:** `src/Anak.Search/Minimax/History/ContinuationHistory.cs`, `src/Anak.Search/Minimax/MovePicker.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 4.2 Relative History Heuristic (Normalization)
- **CPW Reference:** [https://chessprogramming.org/Relative_History_Heuristic](https://chessprogramming.org/Relative_History_Heuristic)
- **Concept:** Normalize butterfly history scores: $\text{score} = \frac{\text{HistoryScore}}{\text{SearchCount} + 1}$. Prevents frequently searched moves from dominating ordering over rarely searched but consistently failing-high moves.
- **Target Files:** `src/Anak.Search/Minimax/History/HistoryTable.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 4.3 Ordering Bad Captures by SEE
- **CPW Reference:** [https://chessprogramming.org/Move_Ordering#Captures](https://chessprogramming.org/Move_Ordering#Captures)
- **Concept:** In `MovePickerStage.BadCaptures`, pick moves in descending order of SEE (least negative first, e.g. $-10\text{ cp}$ pawn loss before $-900\text{ cp}$ queen loss).
- **Target Files:** `src/Anak.Search/Minimax/MovePicker.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

## 5. Time Management & Root Search

### 5.1 Fail-Low Soft-Time Extension
- **CPW Reference:** [https://chessprogramming.org/Time_Management#Failing_Low](https://chessprogramming.org/Time_Management#Failing_Low)
- **Concept:** When iterative deepening fails low at the root on depth $\ge 6$, the expected move has collapsed. Dynamically scale the soft time limit by $+30\%\text{--}50\%$ to allow the search to complete a re-search and find a sound alternative.
- **Target Files:** `src/Anak.Search/Time/TimeManager.cs`, `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

## 6. Multi-Machine Testing Log

| Date | Machine | Feature / Branch | Matches / Games | Time Control | Score / Result | Elo / LOS | Decision / Notes |
|---|---|---|---|---|---|---|---|
| 2026-11-02 | Main Dev (8T) | 1.1 Razoring (`depth <= 3`) | 250 games (Tier 1 Sanity) | 1+0.01 | W: 90, L: 106, D: 54 (117.0 / 250, 46.80%) | **-22.3 &plusmn; 36.0** (LOS 11.1%) | Hard Regression (< 47.0%). Pruning quiet defensive moves when static eval is low caused blunders. Reverted immediately. |
| 2026-11-02 | Main Dev (8T) | 1.2 ProbCut (`depth >= 5, SEE >= 0`) | 250 games (Tier 1 Sanity) | 1+0.01 | W: 71, L: 108, D: 71 (106.5 / 250, 42.60%) | **-51.8 &plusmn; 31.9** (LOS 0.06%) | Hard Regression (< 47.0%). Shallow verification search missed deeper counter-tactics, triggering premature beta cutoffs. Reverted immediately. |

---

## 7. Previously Tested & Rejected Techniques (From CPW Archive)

The following techniques were previously tested in Anak and definitively failed or caused measurable regressions. **Do NOT re-attempt these in their prior form:**

| Technique | Step in Logs | Match Result / Games | Elo Delta | LOS | Root Cause of Failure |
|---|:---:|:---:|:---:|:---:|---|
| **ProbCut (SEE-Gated Verification)** | Item 2 (v1.0.12) | 106.5 / 250 vs v1.0.12 | **-51.8 &plusmn; 31.9** | 0.06% | Shallow search (depth - 4) failed high on speculative captures where opponent's defensive refutation required 2-3 plies to detect. |
| **Razoring (Shallow QS Fallback)** | Item 1 (v1.0.12) | 117.0 / 250 vs v1.0.12 | **-22.3 &plusmn; 36.0** | 11.1% | Pruned quiet defensive moves, king evasions, and interpositions at depth <= 3 when static eval was behind by 450-750 cp. |
| **4-Ply Continuation History Table** | Step 2.9 (v1.0.x) | 115.0 / 250 vs v1.0.10 | **-27.9 &plusmn; 35.7** | 6.2% | Diluted 1-ply/2-ply move ordering signals; increased table pollution and cache misses. |
| **Negative Singular Extensions** | Step 2.5 (v1.0.x) | 2668.0 / 5376 vs v1.0.5 | **-2.5 &plusmn; 7.9** | 26.5% | Reducing non-singular moves by -1 caused tactical blind spots and triggered expensive re-searches. |
| **Dedicated Tactical Move Generator in QS** | Step 1.9 (v1.0.x) | 505.0 / 1029 vs v1.0.3 | **-6.4 &plusmn; 21.7** | 15.3% | Altered subtle move ordering tie-breakers among equal-scoring captures in QS selection sort. |
| **TT Move Ordering in Quiescence Search** | Step 2.3 (v1.0.x) | 119.0 / 250 vs v1.0.4 | **-16.7 &plusmn; 34.1** | 16.7% | Prioritizing TT moves and bypassing delta/SEE pruning in QS caused tactical inaccuracies when TT entries were upper bounds. |
| **Fail-Soft Quiescence Search** | Step 2.10 (v1.0.x) | 117.5 / 250 vs v1.0.6 | **-20.9 &plusmn; 38.0** | 13.9% | QS prunes losing captures without search; returning scores below alpha leaked false refutations into parent PVS and TT. |
| **LMP Improving Tightening** | Step 2.16 (v1.0.x) | Bench verification | +16% node bloat | - | Premature fail-lows on subtle positional quiet moves. |
| **Naive Node Dominance Breakout (Easy Move)**| Step 2.2 (v0.0.5) | 120.5 / 250 vs Step 2.3 | **-12.5 &plusmn; 34.2** | 23.6% | Aborting iterative deepening solely when move visits $\ge 85\%$ broke tactical verification of late refutations. |
| **Naive ProbCut (without SEE gating)** | Step 1.6 (v0.0.5) | 48.0 / 250 vs Step 1.4 | **-249.6 &plusmn; 44.0** | 0.0% | Elevated QS cutoff probe when static eval $\ge \beta + 200$ pruned essential defensive tactics. |
| **Improving in NMP (Dynamic Reduction)** | Step 1.3 (v0.0.5) | 119.5 / 250 vs v0.0.4 | **-15.3 &plusmn; 37.4** | 21.0% | Increasing NMP reduction when improving caused catastrophic tactical zugzwang escapes to be missed. |
