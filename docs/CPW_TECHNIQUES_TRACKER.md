# Anak Engine — CPW Techniques & Classical Search Optimization Tracker

This document tracks search, pruning, movegen, and move-ordering techniques cataloged on [Chess Programming Wiki (chessprogramming.org)](https://chessprogramming.org/) to evaluate, benchmark, and progress across multiple development machines.

> **Note:** Cross-referenced against previous optimization history ([`RELEASE_NOTES.md`](../RELEASE_NOTES.md)) and the failed experiments archive ([`docs/FAILED_IDEAS.md`](FAILED_IDEAS.md)). Features previously tested and rejected are documented in [Section 7](#7-previously-tested--rejected-techniques-from-cpw-archive) to prevent duplicate work.

---

## 🚦 Active Untried Techniques

| # | Technique | Category | Status | Expected Impact | Primary File(s) |
|---|---|---|---|---|---|
| 1 | **Razoring** | Pruning | `[ ] Not Started` | +3 to +8 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 2 | **ProbCut with Strict SEE Safeguards** | Pruning | `[ ] Not Started` | +5 to +10 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 3 | **History-Directed Late Move Pruning** | Pruning | `[ ] Not Started` | +3 to +6 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 4 | **Cut-Node Adaptive LMR** | Reductions | `[ ] Not Started` | +5 to +10 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 5 | **Multi-Cut Pruning (Classical)** | Pruning | `[ ] Not Started` | +0 to +4 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 6 | **Double Singular Extensions (Margin-Based)** | Extensions | `[ ] Not Started` | +5 to +8 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 7 | **Recapture Extensions** | Extensions | `[ ] Not Started` | +2 to +5 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 8 | **One-Reply / Forced Move Extensions** | Extensions | `[ ] Not Started` | +2 to +4 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
| 9 | **Passed Pawn Push Extensions** | Extensions | `[ ] Not Started` | +3 to +7 Elo | `src/Anak.Search/Minimax/AlphaBetaEngine.cs` |
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
  - [ ] Tier 1 Sanity: 250 games (`1+0.01` or `nodes 50000`)
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0] vs active baseline

---

### 1.2 ProbCut with Strict SEE Safeguards
- **CPW Reference:** [https://chessprogramming.org/ProbCut](https://chessprogramming.org/ProbCut)
- **History in Anak:** A naive ProbCut in `v0.0.5` without SEE gating pruned defensive tactical lines and collapsed (-249.6 Elo).
- **Concept:** At depth $\ge 5$, perform a shallow search ($\text{depth} - 4$) with an elevated threshold (`beta + margin`) strictly on captures/promotions that pass Static Exchange Evaluation ($\text{SEE} \ge 0$). If the shallow tactical search fails high, with high statistical probability the full-depth search will also fail high. Return `beta`.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games (`1+0.01` or `nodes 50000`)
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0] vs active baseline

---

### 1.3 History-Directed Late Move Pruning (History Pruning)
- **CPW Reference:** [https://chessprogramming.org/History_Heuristic#History_Pruning](https://chessprogramming.org/History_Heuristic#History_Pruning)
- **Concept:** Rather than relying solely on move count for LMP ($N \ge 3 + 2d^2$), prune quiet moves when the combined butterfly + continuation history score falls below a negative depth-scaled threshold (e.g. `historyScore < -4000 * depth`).
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 1.4 Cut-Node Adaptive LMR
- **CPW Reference:** [https://chessprogramming.org/Node_Types](https://chessprogramming.org/Node_Types)
- **Concept:** Pass a boolean flag `cutNode` down PVS calls. Expected Cut-nodes (null-window searches where the parent expected a fail-high) receive $+1$ extra LMR reduction for quiet moves because a cutoff is expected on move 1 or 2. If all moves fail low, it transitions to an All-node.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 1.5 Multi-Cut Pruning (Classical)
- **CPW Reference:** [https://chessprogramming.org/Multi-Cut](https://chessprogramming.org/Multi-Cut)
- **Concept:** At expected Cut-nodes with depth $\ge 6$, search the first $M$ moves (e.g. 6–8 moves) with reduced depth ($\text{depth} - 1 - R$, where $R = 3$). If $C \ge 2$ or 3 moves fail high, cut immediately without full search.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

## 2. Search Extensions

### 2.1 Double Singular Extensions (Margin-Based Only)
- **CPW Reference:** [https://chessprogramming.org/Singular_Extensions](https://chessprogramming.org/Singular_Extensions)
- **History in Anak:** Negative singular extension (reducing non-singular moves) failed in Step 2.5 (-2.5 Elo). Double extension on wide margin remains open.
- **Concept:** If the singular search score is far below singular beta (`sScore < singularBeta - margin2`), the TT move is overwhelmingly superior; extend by $+2$ plies instead of $+1$.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 2.2 Recapture Extensions
- **CPW Reference:** [https://chessprogramming.org/Recapture_Extensions](https://chessprogramming.org/Recapture_Extensions)
- **Concept:** When the current move is a capture on the exact square of the opponent's previous capture (`move.To == prev1To`), extend by $+1$ ply to ensure full resolution of tactical trades.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 2.3 One-Reply / Forced Move Extensions
- **CPW Reference:** [https://chessprogramming.org/One_Reply_Extensions](https://chessprogramming.org/One_Reply_Extensions)
- **Concept:** When in check or responding to a threat where the position has **only 1 legal evasion**, extend search by $+1$ ply (branching factor is 1, so the cost in tree size is near zero).
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 2.4 Passed Pawn Push Extensions
- **CPW Reference:** [https://chessprogramming.org/Passed_Pawn_Extensions](https://chessprogramming.org/Passed_Pawn_Extensions)
- **Concept:** Extend by $+1$ ply when an unblocked passed pawn advances to the 6th or 7th rank (`rank >= 6`), preventing horizon truncation in promotion races.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

### 2.5 Selective / Restricted Check Extensions
- **CPW Reference:** [https://chessprogramming.org/Check_Extensions](https://chessprogramming.org/Check_Extensions)
- **Concept:** Anak currently extends all checks unconditionally (`if (inCheck) depth++;`). Restrict check extensions to positions with positive SEE, non-PV nodes, or when evasion count is small, eliminating tactical search explosions.
- **Target Files:** `src/Anak.Search/Minimax/AlphaBetaEngine.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

---

## 3. Quiescence Search (QS)

### 3.1 Checks in Quiescence Search
- **CPW Reference:** [https://chessprogramming.org/Quiescence_Search#Checks](https://chessprogramming.org/Quiescence_Search#Checks)
- **Concept:** At ply 0 (or plies 0–1) of QS, generate and search quiet giving-checks (with positive SEE or non-futile check masks) to avoid blundering to immediate forcing checks at the root of the qsearch tree.
- **Target Files:** `src/Anak.Search/Minimax/QuiescenceSearch.cs`
- **Testing Checklist:**
  - [ ] Tier 1 Sanity: 250 games
  - [ ] Tier 2 Validation: SPRT [0.0, 3.0]

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
| *(template)* | *Main PC (16T)* | *feat/razoring* | *8,000 max (SPRT)* | *1+0.01* | *W: 840, L: 710, D: 950* | *+18.5 Elo (H1 pass)* | *Accepted & merged* |

---

## 7. Previously Tested & Rejected Techniques (From CPW Archive)

The following techniques were previously tested in Anak and definitively failed or caused measurable regressions. **Do NOT re-attempt these in their prior form:**

| Technique | Step in Logs | Match Result / Games | Elo Delta | LOS | Root Cause of Failure |
|---|:---:|:---:|:---:|:---:|---|
| **4-Ply Continuation History Table** | Step 2.9 (v1.0.x) | 115.0 / 250 vs v1.0.10 | **-27.9 &plusmn; 35.7** | 6.2% | Diluted 1-ply/2-ply move ordering signals; increased table pollution and cache misses. |
| **Negative Singular Extensions** | Step 2.5 (v1.0.x) | 2668.0 / 5376 vs v1.0.5 | **-2.5 &plusmn; 7.9** | 26.5% | Reducing non-singular moves by -1 caused tactical blind spots and triggered expensive re-searches. |
| **Dedicated Tactical Move Generator in QS** | Step 1.9 (v1.0.x) | 505.0 / 1029 vs v1.0.3 | **-6.4 &plusmn; 21.7** | 15.3% | Altered subtle move ordering tie-breakers among equal-scoring captures in QS selection sort. |
| **TT Move Ordering in Quiescence Search** | Step 2.3 (v1.0.x) | 119.0 / 250 vs v1.0.4 | **-16.7 &plusmn; 34.1** | 16.7% | Prioritizing TT moves and bypassing delta/SEE pruning in QS caused tactical inaccuracies when TT entries were upper bounds. |
| **Fail-Soft Quiescence Search** | Step 2.10 (v1.0.x) | 117.5 / 250 vs v1.0.6 | **-20.9 &plusmn; 38.0** | 13.9% | QS prunes losing captures without search; returning scores below alpha leaked false refutations into parent PVS and TT. |
| **LMP Improving Tightening** | Step 2.16 (v1.0.x) | Bench verification | +16% node bloat | - | Premature fail-lows on subtle positional quiet moves. |
| **Naive Node Dominance Breakout (Easy Move)**| Step 2.2 (v0.0.5) | 120.5 / 250 vs Step 2.3 | **-12.5 &plusmn; 34.2** | 23.6% | Aborting iterative deepening solely when move visits $\ge 85\%$ broke tactical verification of late refutations. |
| **Naive ProbCut (without SEE gating)** | Step 1.6 (v0.0.5) | 48.0 / 250 vs Step 1.4 | **-249.6 &plusmn; 44.0** | 0.0% | Elevated QS cutoff probe when static eval $\ge \beta + 200$ pruned essential defensive tactics. |
| **Improving in NMP (Dynamic Reduction)** | Step 1.3 (v0.0.5) | 119.5 / 250 vs v0.0.4 | **-15.3 &plusmn; 37.4** | 21.0% | Increasing NMP reduction when improving caused catastrophic tactical zugzwang escapes to be missed. |
