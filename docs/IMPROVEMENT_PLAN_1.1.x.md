# Anak Chess Engine (`v1.1.2`) â€” 50-Item Improvement & Bugfix Plan

> **Baseline:** `v1.1.2` (`4c17e81`, ~2969 Elo Ordo CCRL-anchored)  
> **Guardrail:** Every item below is verified against the current codebase and cross-checked against [`FAILED_IDEAS.md`](FAILED_IDEAS.md) â€” **zero** previously rejected experiments are repeated.  
> **Validation Protocol:** Per [`AGENTS.md`](../AGENTS.md), every change must pass `dotnet test -c Release` and follow the 3-Tier Testing Pyramid (100% node-equivalent NPS patches can use fixed-node equivalence + `bench 12` NPS verification; Elo/search/NNUE changes use Tier 1 Sanity -> Tier 2 SPRT `[0, 3]` / Tier 3 MTC).

---

## Executive Summary & Recommended Execution Waves

| Wave | Focus | Item IDs | Est. Cumulative Impact |
| :---: | :--- | :--- | :--- |
| **Wave 1** | **Immediate Bugfixes (Code-Only, High Elo ROI)** | `#1â€“#14` | **+25 to +55 Elo** + crash/corruption fixes |
| **Wave 2** | **Bit-Exact NPS Wins (100% Node-Equivalent)** | `#40â€“#45`, `#47â€“#50` | **+35% to +70% NPS** (+20 to +40 Elo in timed play) |
| **Wave 3** | **Search, Pruning, History, TT & Time** | `#15â€“#28`, `#38â€“#39`, `#46` | **+70 to +130 Elo** (single-thread) + **+15 to +30 Elo** (SMP) |
| **Wave 4** | **Next-Gen `anak.nnue` Architecture & Retrain** | `#29â€“#37` | **+80 to +150+ Elo** after retraining |

---

## 1. Critical Bugfixes & Correctness (`#1â€“#14`)

### 1. [x] Initialize `_stack[0].StaticEval` in `SearchRoot` (Fixes Broken `improving` at Ply 2) — **Released in `v1.1.3`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L911-L922), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1251-L1262), [`SearchStack.cs`](../src/Anak.Search/Minimax/SearchStack.cs#L63)
- **Bug:** `SearchStack.Reset()` sets `StaticEval = 0` instead of `-Value.InfiniteValue`, and `SearchRoot` never computes `_stack[0].StaticEval`. At every `ply == 2` node, `AlphaBeta` checks `_stack[ply - 2].StaticEval != -Value.InfiniteValue` (true because it is `0`) and computes `improving = staticEval > 0` instead of comparing against the root eval, misclassifying RFP, LMP, and LMR across the entire ply-2 frontier.
- **Fix:** Initialize `StaticEval = -Value.InfiniteValue` in `SearchStack.Reset()`, and compute `_stack[0].StaticEval` in `SearchRoot` when `!_stack[0].InCheck`.
- **Result (`v1.1.3` vs `v1.1.2`, `1+0.01`, 5,824 games):** **+9.25 ± 7.36 logistic Elo** (`+11.23 ± 8.92` nElo, `51.33%`, `99.32% LOS`).

### 2. Fix `QuiescenceSearch` TT Poisoning on Time-Up / Abort
- **Location:** [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L57-L60), [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L259-L295)
- **Bug:** (a) On `_isTimeUp()` mid-loop (L261), QS executes `break;` instead of `return 0;`, falling into L292â€“295 and writing an incomplete `Exact`/`UpperBound` entry into the TT. (b) When a recursive child returns `0` due to `_isTimeUp()`, the caller (L273) does not re-check `_isTimeUp()` and treats `score = 0` as a genuine result, storing fake TT entries that persist across moves.
- **Fix:** Return `0` immediately on `_isTimeUp()` both at L261 and immediately after the recursive call at L274, before any alpha/beta update or TT store.
- **Est. Impact:** **+3 to +8 Elo** (bullet/blitz) | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

### 3. [x] Fix SEE Promotion Bug (Uses Pawn `100cp` Instead of Promoted Piece Value on Target Square) — **Released in `v1.1.4`**
- **Location:** [`SEE.cs`](../src/Anak.Core/Evaluation/SEE.cs#L42-L64), [`SEE.cs`](../src/Anak.Core/Evaluation/SEE.cs#L137-L179)
- **Bug:** Both `HasPositiveOrEqualSee` and `Evaluate` add `GetPieceValue(PromotionPieceType) - 100` to `victimVal`, but leave `attackerType = Pawn` (`100cp`) as the piece sitting on `to` that the opponent can recapture. On a losing promotion like `e7e8q` into a defended square, `Evaluate` returns **`+700cp`** instead of **`-100cp`**, and `HasPositiveOrEqualSee` immediately returns `true`.
- **Fix:** Set `PieceType attackerType = move.IsPromotion ? move.PromotionPieceType : movedPiece.Type();` in both methods.
- **Result (`v1.1.4` vs `v1.1.3`, `1+0.01`, 8,000 games):** **+5.86 ± 5.89 logistic Elo** (`+7.58 ± 7.61` nElo, `50.84%`, `97.45% LOS`).

### 4. Fix SEE Quiet-Move Fast Path Missing X-Ray Attacks
- **Location:** [`SEE.cs`](../src/Anak.Core/Evaluation/SEE.cs#L70-L73), [`SEE.cs`](../src/Anak.Core/Evaluation/SEE.cs#L152-L158)
- **Bug:** For quiet moves, both methods short-circuit with `if (!board.IsSquareAttacked(to, them))` using pre-move `board.Occupied` **before** removing `from`. If a piece retreats along an enemy slider's ray (e.g., `Qe2-e5` with Black Rook on `e1`), `e2` still blocks `e1` and SEE returns `0` instead of `-900`. When `to` *is* attacked, it pays for magic lookups twice.
- **Fix:** Remove L70â€“73 in `HasPositiveOrEqualSee` (the main loop exits on iteration 1 with updated `occupied`), and in `Evaluate` compute `occupied` and `attackers` first before checking `(attackers & themPieces) == 0`.
- **Est. Impact:** **+2 to +5 Elo** + small NPS gain | **Complexity:** Trivial | **Test:** `SEETests` + Tier 1 -> Tier 2 SPRT.

### 5. [x] Fix `TranspositionTable.UpdateMinimaxEntry` Overwriting Deep `BestMove` with Shallow `BestMove` — **Released in `v1.1.5`**
- **Location:** [`TranspositionTable.cs`](../src/Anak.Search/Transposition/TranspositionTable.cs#L248-L261)
- **Bug:** Even when the depth/flag/age guard at L248 preserves a deeper entry's `Score` and `Depth`, L258–261 unconditionally overwrite `existing.BestMove` with the shallow/QS `bestMove`, degrading move ordering and PV extraction.
- **Fix:** Only overwrite `existing.BestMove` when the entry itself is replaced (inside the L248 `if`) or when `existing.BestMove == Move.Null`.
- **Result (`v1.1.5` vs `v1.1.4`, `1+0.01`, 8,000 games):** **+3.74 ± 6.01 logistic Elo** (`+4.73 ± 7.61` fastchess nElo / `+5.76 ± 5.39` paired nElo, `50.54%`, `88.85% LOS`).

### 6. Fix `QuiescenceSearch` Taking TT Cutoffs on PV Nodes
- **Location:** [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L55-L84), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1122-L1125)
- **Bug:** `AlphaBeta` does not pass `isPv` into `_qsearch.Search(...)`, so QS takes `Exact`/`LowerBound`/`UpperBound` TT cutoffs on PV nodes, truncating PVs and returning non-PV bounds on the principal variation.
- **Fix:** Pass `bool isPv` into `QuiescenceSearch.Search` and guard L81â€“83 with `if (!isPv && ttHit && entry.ExpertId == ActiveExpertId)`.
- **Est. Impact:** **+2 to +5 Elo** | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

### 7. [x] Fix Singular Extensions Missing Mate-Score Guard, Stop-Check Guard & Ply Cap — **Released in `v1.1.6`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1323-L1338)
- **Bug:** (a) Missing `Math.Abs(ttScore) < TTEntry.MateBound` allows `singularBeta` to stay in mate territory or underflow `-Value.MateValue`. (b) Missing stop/cancellation check after L1331 lets an aborted singular search return `0 < singularBeta` and falsely extend. (c) No ply cap (`ply < 2 * rootDepth`) allows stacked `+2` singular + `+1` check extensions to explode deep forcing lines.
- **Fix:** Add `Math.Abs(ttScore) < TTEntry.MateBound`, abort check after L1331, and `ply < 2 * _rootDepth` / `depth < MaxSearchDepth - 2` cap.
- **Result (`v1.1.6` vs `v1.1.5`, `1+0.01`, 8,000 games):** **+2.35 ± 5.74 logistic Elo** (`+3.11 ± 7.61` fastchess nElo / `+1.42 ± 5.38` paired nElo, `50.34%`, `78.84% LOS`).

### 8. [x] Fix `TimeManager` Low-Time Invariant Violation & Fail-Low Extension Wipeout — **Released in `v1.1.7`**
- **Location:** [`TimeManager.cs`](../src/Anak.Search/Time/TimeManager.cs#L185-L249), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L729-L730)
- **Bug:** (a) `Math.Max(10, ...)` at L185/L215 can push `_softLimitMs` above `_hardLimitMs` when `_baseSoftLimitMs < 10ms`. (b) `UpdateMoveStability` (L215) resets `_softLimitMs` down to `0.75 * _baseSoftLimitMs` when `_stableMoveDepths >= 4`, wiping out `ExtendForFailLow` extensions.
- **Fix:** Clamp `_softLimitMs` against `_hardLimitMs - 1` and track a per-iteration `_hadFailLowThisIter` flag so stability contraction never erases fail-low extensions.
- **Result (`v1.1.7` vs `v1.1.6`, `1+0.01`, 8,000 games):** **+1.09 ± 5.80 logistic Elo** (`+1.43 ± 7.61` nElo, `50.16%`, `64.32% LOS`).

### 9. Fix Unchecked `IndexBuffer128` Stack Overflow in `MovePicker` & `QuiescenceSearch`
- **Location:** [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L92-L97), [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L315-L590), [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L170-L231)
- **Bug:** `_goodCaptureIndices`, `_quietIndices`, and `candidateIndices` are 128-element buffers while `MoveList` holds up to 256 moves, with no `< 128` bounds check. Multi-queen positions with >128 legal moves throw `IndexOutOfRangeException`. `_playedGoodCaptures` (`MoveBuffer32`) also silently drops captures past 32.
- **Fix:** Expand quiet/candidate index buffers to `IndexBuffer256` (256 bytes) and guard `_playedGoodCaptures` (or remove it once `#44` lands).
- **Est. Impact:** Eliminates crash/UB in extreme positions | **Complexity:** Low | **Test:** Unit test with >128 legal moves.

### 10. Fix `MoveGenerator.GenerateLegalCaptures` Ignoring Checks
- **Location:** [`MoveGenerator.cs`](../src/Anak.Core/MoveGen/MoveGenerator.cs#L674-L939)
- **Bug:** `GenerateLegalCaptures` never computes `checkers` and emits illegal captures that leave the king in check when called on an in-check position (violating `GenerateLegalMoves â‰¡ GenerateLegalCaptures âˆª GenerateLegalQuiets` and affecting callers like `ZapPolicy.cs:1619`).
- **Fix:** Handle `checkers` in `GenerateLegalCaptures` and expose an explicit `GenerateLegalCapturesNoCheck` overload for `MovePicker`/`QS` where `!inCheck` is already proven.
- **Est. Impact:** Correctness fix (0 NPS cost) | **Complexity:** Low | **Test:** `MoveGenTests` check-capture invariant test.

### 11. [x] Fix `Board.IsLegal` Accepting `DoublePawnPush` on Non-Pawns & Non-Capture Promotions on Captures — **Released in `v1.1.8` (0 Elo Fix)**
- **Location:** [`Board.cs`](../src/Anak.Core/Board/Board.cs#L787-L906)
- **Bug:** Non-pawn checks (`L865–906`) fail to reject `MoveType.DoublePawnPush` (which on a TT hash collision corrupts `EpSquare` and `ZobristKey` in `MakeMove:356`). Pawn capture-promotions (`L787–790, L847–850`) do not check `!move.IsCapture`.
- **Fix:** Use `if (move.Type != (isCaptureDest ? MoveType.Capture : MoveType.Quiet)) return false;` for non-pawns and add `|| !move.IsCapture` to capture-promotion checks.
- **Result (vs `v1.1.7`, `1+0.01`, 5,978 games):** **0.00 ± 5.76 Elo** (`1957W / 1957L / 2064D`, `50.00%`, `50.00% LOS`) — kept as a zero-regression correctness fix, bundled into `v1.1.8`.

### 12. Fix Mid-Search `limits.Nodes` Enforcement & Include QS Nodes in `SearchWorker.Nodes`
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L645-L647), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1141-L1150), [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L24-L57)
- **Bug:** `limits.Nodes` is only checked between iterative deepening iterations (overshooting `--nodes 50000` to 250k+), and `_qsNodes` is never added to `SearchWorker.Nodes`.
- **Fix:** Check `Nodes >= nodeLimit` inside the periodic `CheckMask` check and count QS nodes in `SearchWorker.Nodes`.
- **Est. Impact:** Accurate fixed-node benchmarking (`AGENTS.md` Â§4) & honest UCI `nodes`/`nps` reporting | **Complexity:** Low.

### 13. Fix 50-Move Draw Precedence Before Checkmate & `_gamePly == 1024` Overflow Asymmetry
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1104-L1119), [`Board.cs`](../src/Anak.Core/Board/Board.cs#L389-L411), [`Board.cs`](../src/Anak.Core/Board/Board.cs#L508-L539)
- **Bug:** (a) `board.IsFiftyMoveRule()` returns `0` before verifying checkmate (under FIDE rules, mate on move 50 wins). (b) `MakeMove`/`MakeNullMove` cap `_gamePly` at `1024` while `UnmakeMove`/`UnmakeNullMove` unconditionally decrement `_gamePly`, causing drift on >1024-ply games. (c) `RepetitionCount`/`IsRepetition` start at `_gamePly - 3` (2 plies ago, impossible to repeat) instead of `_gamePly - 5`.
- **Fix:** Verify at least one legal move when `IsFiftyMoveRule() && IsInCheck()`; keep `_gamePly` balanced while guarding `_history` writes at `< 1024`; start repetition scan at `_gamePly - 5`.
- **Est. Impact:** Endgame correctness + tiny NPS win | **Complexity:** Trivial | **Test:** `RepetitionRuleTests` + `AlphaBetaTests`.

### 14. Fix Syzygy `tbScore` Colliding with `TTEntry.MateBound` & Migrate to Root-Only DTZ/Blunder Filtering
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L758-L764), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1171-L1199), [`SearchStatistics.cs`](../src/Anak.Search/SearchStatistics.cs#L58)
- **Bug:** `tbScore = Value.MateValue - 100 - ply` (`29900 - ply`) exceeds `TTEntry.MateBound` (`29000`), falsely reporting forced `MateIn` to UCI and corrupting mate-distance TT logic; `TbHits` is never incremented.
- **Fix:** Use a TB win score below `MateBound` (`20000 - ply`), increment `TbHits`, and restrict Syzygy probing to root DTZ / root-move blunder filtering per [`FAILED_IDEAS.md`](FAILED_IDEAS.md#L59-L61).
- **Est. Impact:** Correctness + endgame conversion when Syzygy tables are loaded | **Complexity:** Low-Medium.

---

## 2. Search, Pruning & Extensions (`#15â€“#20`)

### 15. [x] Continuous History-Scaled LMR Replacing Binary `±1` Thresholds — **Released in `v1.1.9`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1509-L1515)
- **Improvement:** Replaced the binary `±1` threshold steps (`HistoryThreshold`, `ConthistReward`/`ConthistPenalty`) with continuous proportional scaling: `r -= (hist + conthist1 + conthist2) / 8192`, allowing `±2` to `±3` ply adjustments on extreme history moves.
- **Result (`v1.1.9` vs `v1.1.8`, `1+0.01`, 8,000 games):** **+1.52 ± 5.91 logistic Elo** (`+1.96 ± 7.61` nElo, `2731W / 2696L / 2573D`, `50.22%`, `69.29% LOS`).

### 16. Persist `ttPv` Flag in `TTEntry` for PV-Aware LMR & Pruning
- **Location:** [`TTEntry.cs`](../src/Anak.Search/Transposition/TTEntry.cs#L11-L18), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1202-L1284), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1474-L1503)
- **Improvement:** Use an unused bit in `TTEntry.Flags` (`0x08`) to record whether a node was ever a PV node (`bool ttPv = isPv || (ttHit && entry.IsPv)`). Reduce 1 ply less in LMR (`if (ttPv) r--;`) and guard aggressive pruning on former PV nodes across transpositions and aspiration re-searches.
- **Est. Impact:** **+8 to +15 Elo** | **Complexity:** Low | **Test:** Tier 1 -> Tier 2 SPRT.

### 17. `ttCapture` Extra LMR Reduction
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1474-L1503)
- **Improvement:** Track `bool ttCapture = !ttMove.IsNull && ttMove.IsCapture;`. When the TT move is a capture, later quiet moves are far less likely to be best â€” add `if (ttCapture) r++;` to quiet LMR reductions.
- **Est. Impact:** **+4 to +8 Elo** | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

### 18. [x] Deeper / Shallower LMR Re-Search (`doDeeperSearch` / `doShallowerSearch`) — **Released in `v1.1.10`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1530-L1539)
- **Improvement:** When a reduced LMR search fails high (`score > alpha && searchDepth < nextDepth`), applied `doDeeperSearch = score > bestScore + 50 + 2 * nextDepth` (`reSearchDepth++`) and `doShallowerSearch = score < bestScore + nextDepth` (`reSearchDepth--`).
- **Result (`v1.1.10` vs `v1.1.9`, `1+0.01`, 8,000 games):** **+2.87 ± 5.96 logistic Elo** (`+3.66 ± 7.61` nElo, `2689W / 2623L / 2688D`, `50.41%`, `82.71% LOS`).

### 19. Null Move Pruning Verification Search at High Depth & Mate-Beta Guard
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1289-L1319)
- **Improvement:** Guard NMP with `beta > -TTEntry.MateBound`, and when `depth >= 12` (or low non-pawn material) and `nullScore >= beta`, run a verification search at `nullDepth` with NMP disabled (`nmpMinPly = ply + 3 * nullDepth / 4`) to catch zugzwang without hurting shallow speed.
- **Est. Impact:** **+3 to +8 Elo** (stronger at MTC/endgames) | **Complexity:** Low-Medium | **Test:** Tier 1 -> Tier 3 MTC / Tier 2 SPRT.

### 20. [x] Preserve `iterBestMove` on Aspiration Fail-High Timeout & Optimize Delta Growth — **Released in `v1.1.11`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L669-L716)
- **Improvement:** Immediately saves `bestMove = iterBestMove` on a completed root fail-high (`score >= beta && !iterBestMove.IsNull`) so timeouts during fail-high re-search don't discard the better move; opens directly to `InfiniteValue` once `delta > 500`, and searches `searchDepth = Math.Max(1, currentDepth - failHighCount)` on fail-high.
- **Result (`v1.1.11` vs `v1.1.10`, `1+0.01`, 8,000 games):** **+2.82 ± 5.70 logistic Elo** (`+3.28 ± 5.38` paired nElo / `+3.77 ± 7.61` fastchess nElo, `2677W / 2612L / 2711D`, `50.41%`, `83.42% LOS`).

---

## 3. Move Ordering & History Heuristics (`#21â€“#24`)

### 21. [x] Fix Underscaled `HistoryTable.CalculateBonus(depth)` (~50× Too Small) & `-malus` in `CaptureHistory` — **Released in `v1.1.8`**
- **Location:** [`HistoryTable.cs`](../src/Anak.Search/Minimax/History/HistoryTable.cs#L69-L72), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1715)
- **Improvement:** `CalculateBonus(depth)` returned `depth * depth` (`1..36` for depths `1..6`) against `MaxHistory = 16384` and thresholds of `2139..3805`. Switched to `Math.Min(Math.Max(0, 300 * depth - 250), MaxBonus)`, and passed `-malus` instead of `-bonus` to `_captureHistory.Update`.
- **Result (`v1.1.8` vs `v1.1.7`, `1+0.01`, 4,120 games early-stopped):** **+99.80 ± 8.88 logistic Elo** (`+125.98 ± 10.61` nElo, `2142W / 990L / 988D`, `63.98%`, `100.00% LOS`).

### 22. Rebalance `CaptureHistory` vs. MVV-LVA Scaling & Halve `CaptureHistory` Table
- **Location:** [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L484-L489), [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L639-L657), [`CaptureHistory.cs`](../src/Anak.Search/Minimax/History/CaptureHistory.cs#L48-L64)
- **Improvement:** `GetMvvLva` spans `9,100..90,000` while `_captureHistory / 16` is bounded to `[-1024, +1024]`, drowning out capture history. Scale MVV-LVA to Stockfish proportions (`victimVal * 7 + captureHistory`) and index victim by `PieceType` (0..6) instead of `Piece` (0..12), cutting table size in half and eliminating the `Piece`/`PieceType` indexing hazard.
- **Est. Impact:** **+5 to +10 Elo** | **Complexity:** Low | **Test:** Tier 1 -> Tier 2 SPRT.

### 23. [x] Filter Tactical BestMoves Out of `CorrectionHistory` + Add Continuation & Non-Pawn Correction History — **Released in `v1.1.12`**
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1609-L1650), [`CorrectionHistory.cs`](../src/Anak.Search/Minimax/History/CorrectionHistory.cs), [`Board.cs`](../src/Anak.Core/Board/Board.cs), [`Zobrist.cs`](../src/Anak.Core/Zobrist/Zobrist.cs)
- **Improvement:** (a) Skips `CorrectionHistory.Update` when the cutoff/best move is a capture or promotion. (b) Added **Continuation Correction History** (`[2, 16, 64]` keyed by `[side, prev1Piece, prev1To]`) and **White/Black Non-Pawn Correction History** (incrementally maintained `WhiteNonPawnKey`/`BlackNonPawnKey` in `Board`) blended into `GetAdjustedEval`.
- **Result (`v1.1.12` vs `v1.1.11`, `1+0.01`, 8,000 games):** **+38.81 ± 6.02 logistic Elo** (`+49.54 ± 7.61` fastchess nElo, `3084W / 2194L / 2722D`, `55.56%`, `100.00% LOS`).

### 24. Order `BadCaptures` by Descending SEE in `MovePicker` (CPW 4.3)
- **Location:** [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L325-L345), [`CPW_TECHNIQUES_TRACKER.md`](CPW_TECHNIQUES_TRACKER.md#L221-L227)
- **Improvement:** `MovePicker` already caches `_badCaptureSee` (`v1.0.28`), but picks bad captures in generation order. Pick in descending order of SEE (e.g., `-10cp` before `-900cp`) so tactical sacrifices / near-equal exchanges are tried before queen hangs.
- **Est. Impact:** **+1 to +4 Elo** | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

---

## 4. Transposition Table, SMP & Time Management (`#25â€“#28`)

### 25. Compact 16-Byte (4-per-Cacheline) `TTEntry`, Lock-Free XOR Torn-Read Protection & Exact Hash Sizing (`BigMul`)
- **Location:** [`TranspositionTable.cs`](../src/Anak.Search/Transposition/TranspositionTable.cs#L11-L169), [`TTEntry.cs`](../src/Anak.Search/Transposition/TTEntry.cs#L24-L73)
- **Improvement:** (a) Store upper 32 bits of the Zobrist key (`uint`) to shrink `TTEntry` from 20 bytes (3/cacheline, 15.6% wasted) to **16 bytes (4/cacheline, +33% capacity)**. (b) Use a local struct + XOR key trick (`key ^ data`) to prevent Lazy SMP torn reads/writes. (c) Replace power-of-two `key & _mask` with `Math.BigMul(key, _clusterCount, out _)` to utilize 100% of requested `Hash` MB instead of wasting up to 49%.
- **Est. Impact:** **+5 to +12 Elo** + SMP safety | **Complexity:** Medium | **Test:** `TranspositionTableTests` + Tier 1 -> Tier 2 SPRT.

### 26. Lazy SMP Overhaul: Helper Depth-Skip Diversity, Aspiration Windows, Best-Thread Voting & Persistent Threads
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L456-L494), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L860-L878)
- **Improvement:** Give helper threads Stockfish-style depth-skip schedules and aspiration windows instead of full `[-inf, +inf]` windows with trivial `Id % 2`; implement best-thread voting across all workers at search end (instead of always taking Worker 0); replace per-move `Task.Run`/`Task.WaitAll` with persistent worker threads woken via `ManualResetEventSlim`.
- **Est. Impact:** **+15 to +30 Elo** at `Threads >= 4` | **Complexity:** Medium | **Test:** Multi-threaded SPRT (`--threads 4`).

### 27. Immediate / Fast Handling for Single Legal Move (`legalMoves.Count == 1`)
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L435-L453)
- **Improvement:** Currently Anak burns a full move's time allocation on forced single legal moves in timed games. When `legalMoves.Count == 1 && _timeManager.HasTimeLimit && !limits.Infinite`, cap `SoftLimitMs`/`HardLimitMs` to a tiny budget (e.g., 5â€“10 ms or depth 4) to bank clock time for real decisions.
- **Est. Impact:** **+5 to +10 Elo** (bullet/blitz) | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

### 28. Smooth Best-Move Node-Fraction Continuous Soft-Limit Time Scaling
- **Location:** [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L652-L659), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L951-L1063), [`TimeManager.cs`](../src/Anak.Search/Time/TimeManager.cs)
- **Improvement:** Track `rootMoveNodes` per root move and continuously modulate `SoftLimitMs` by `(1.5 - bestMoveFraction) * 1.35` (clamped `[0.55, 1.65]`) so contested positions get more time and easy positions spend less â€” *without* the hard early-breakout at `>= 85%` that failed in `FAILED_IDEAS.md` #10.
- **Est. Impact:** **+8 to +15 Elo** | **Complexity:** Low | **Test:** Tier 1 -> Tier 2 SPRT.

---

## 5. NNUE Architecture, Evaluation & Training Pipeline (`#29â€“#39`)

### 29. Fix Horizontally-Folded 16 King Buckets Missing Piece-Square Mirroring (`sq ^ 7`) â€” Major Architecture Bug
- **Location:** [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L20-L99), [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L390-L484), [`model.py`](../training/model.py#L39-L64), [`export.py`](../training/export.py#L80-L97)
- **Bug:** `GetKingBucket(sq)` folds files Eâ€“H onto Dâ€“A (`g1` and `b1` both map to bucket `1`), but piece squares are **never XORed with `7`** when `(kingSq & 7) >= 4`. Bucket `1` is forced to learn both `a2/b2/c2` AND `f2/g2/h2` as king pawn shields, defeating horizontal symmetry.
- **Fix:** Mirror all piece squares by `sq ^ 7` for a perspective whenever `(kingSq & 7) >= 4` in C#, Python `map_raw_indices_to_buckets`, and `export.py`; trigger accumulator refresh in `UpdateMove` when the king crosses the d/e file boundary (`((int)from & 4) != ((int)to & 4)`).
- **Est. Impact:** **+25 to +50 Elo** after retrain | **Complexity:** Medium | **Test:** `NnueTests` + retrain + Tier 2 SPRT.

### 30. Remove `enforce_perspective_antisymmetry()` Lock & `[0.15, 0.25]` Tempo Clamp
- **Location:** [`model.py`](../training/model.py#L67-L113), [`model.py`](../training/model.py#L240), [`train.py`](../training/train.py#L532), [`train_all.py`](../training/train_all.py#L430-L526)
- **Bug:** Post-epoch and EMA calls to `enforce_perspective_antisymmetry()` force `f(our, their) + f(their, our) == 2 * out_bias` identically for every position and hard-clamp `out_bias` to `[0.15, 0.25]`. This makes position-dependent tempo, initiative, and hanging-piece-on-turn evaluation mathematically impossible, and cuts effective L1 capacity from 64 to 32 neurons.
- **Fix:** Keep symmetric initialization at step 0 only; remove post-epoch and EMA antisymmetry projections and the `[0.15, 0.25]` bias clamp.
- **Est. Impact:** **+15 to +35 Elo** after retrain | **Complexity:** Trivial | **Test:** Retrain + Tier 2 SPRT.

### 31. Fix Double-Blending of WDL & Eval Targets (4Ã— Game-Outcome Dilution)
- **Location:** [`sf_binpack_to_anak.cpp`](../training/cpp_binpack/sf_binpack_to_anak.cpp#L329-L331), [`loss.py`](../training/loss.py#L24-L38)
- **Bug:** `sf_binpack_to_anak.cpp` pre-blends `wdl = 0.75 * eval_wdl + 0.25 * game_wdl`, and `ModernNNUELoss` blends again with `lambda_wdl = 0.35`, silently cutting true game-outcome weight to `8.75%` instead of `35%`.
- **Fix:** Store raw `game_wdl` (`0.0`, `0.5`, `1.0`) in `rec.target_wdl` so `ModernNNUELoss.lambda_wdl` is the single source of truth.
- **Est. Impact:** **+5 to +15 Elo** (endgame conversion) | **Complexity:** Low | **Test:** Rebuild binpacks + retrain.

### 32. Fix `train_all.py` Crashes (`ALL_8_MODELS`), Wrong `moe.nnue` Sync Name & Raw-vs-EMA Validation Mismatch
- **Location:** [`train_all.py`](../training/train_all.py#L74-L83), [`train_all.py`](../training/train_all.py#L434-L580), [`train_all.py`](../training/train_all.py#L875-L974), [`train.py`](../training/train.py#L536-L625)
- **Bug:** (a) Undefined `ALL_8_MODELS` causes `NameError` with `--models all`/`--experiment`. (b) Syncs `moe.nnue` instead of `anak.nnue` at L974. (c) Validation evaluates raw weights while checkpoints/exports save EMA weights.
- **Fix:** Use `ALL_EXPERT_MODELS`, sync `"anak.nnue" if m == "moe" else f"{m}.nnue"`, and wrap validation in `ema.store_and_apply`/`ema.restore`.
- **Est. Impact:** Unblocks automated training & fixes best-epoch selection | **Complexity:** Trivial.

### 33. Upgrade FT Quantization Scale (`QA: 64 -> 255`, `int16` FT Weights) to Eliminate 11% SCReLU Dead-Zone
- **Location:** [`NnueConstants.cs`](../src/Anak.Data/Nnue/NnueConstants.cs#L60-L67), [`NnueWeights.cs`](../src/Anak.Data/Nnue/NnueWeights.cs#L30-L183), [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L157-L296), [`NnueInference.cs`](../src/Anak.Data/Nnue/NnueInference.cs#L156-L163), [`export.py`](../training/export.py#L51-L115)
- **Improvement:** Under `QA = 64` (`sbyte` FT weights), SCReLU `(clamp(acc, 0, 64)^2) >> 6` truncates values `1..7` to `0` (killing the bottom 11% of the activation range) and clamps weights at `[-1.98, 1.98]`. Upgrading FT weights to `int16` with `QA = 255` and `(clamp(acc, 0, 255)^2) >> 9` gives 4Ã— resolution, still fits `vpmaddubsw` (`[0..127]`), and removes `sbyte -> short` sign-extension in `NnueAccumulator`.
- **Est. Impact:** **+15 to +30 Elo** after retrain | **Complexity:** Medium | **Test:** Format bump + `NnueInferenceTests` + retrain.

### 34. Widen Shared Feature Transformer (`512 -> 768` or `1024`) with Compact / Pairwise L1 Head
- **Location:** [`NnueConstants.cs`](../src/Anak.Data/Nnue/NnueConstants.cs#L52-L57), [`model.py`](../training/model.py#L18-L19), [`model.py`](../training/model.py#L315-L343)
- **Improvement:** Shift parameter budget into a wider FT (`768` or `1024` per perspective) paired with pairwise SCReLU (`1024 -> 512` per perspective before L1) or a compact L1 (`32`) to preserve leaf speed (prerequisite: `#42` dynamic native accumulator stacks).
- **Est. Impact:** **+20 to +45 Elo** after retrain | **Complexity:** Medium.

### 35. Add Incremental Linear PSQT Residual Bypass Alongside Shared FT
- **Location:** [`NnueWeights.cs`](../src/Anak.Data/Nnue/NnueWeights.cs#L18-L36), [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L73-L150), [`NnueInference.cs`](../src/Anak.Data/Nnue/NnueInference.cs#L32-L53), [`model.py`](../training/model.py#L137-L188)
- **Improvement:** Add a lightweight `int[6]` (or `int[8]` phase-bucketed) linear PSQT accumulator updated incrementally alongside the FT accumulator (6 adds/subs per move) and summed directly into the expert head output, freeing nonlinear FT/L1 neurons from material counting.
- **Est. Impact:** **+10 to +20 Elo** + faster training convergence | **Complexity:** Medium.

### 36. Eliminate Cross-Domain Head Contamination & Validation Wrap-Around in `SharedMoENNUE` Data Pipeline
- **Location:** [`sf_binpack_to_anak.cpp`](../training/cpp_binpack/sf_binpack_to_anak.cpp#L45), [`sf_binpack_to_anak.cpp`](../training/cpp_binpack/sf_binpack_to_anak.cpp#L437-L464), [`fast_loader.py`](../training/fast_loader.py#L584-L669)
- **Improvement:** Currently `MultiExpertBatchLoader` routes 20% cross-domain random samples to each expert head (training `Endgame` head on openings and `Open`/`Closed` heads on endgames) and wraps smaller binpacks repeatedly during validation. Store the true runtime `expert_id` in the unused 2-byte `AnakBinpackRecord.reserved` field, route `SharedMoENNUE` strictly by true `expert_id`, and validate once per expert without wrap-around.
- **Est. Impact:** **+10 to +25 Elo** for `anak.nnue` | **Complexity:** Low-Medium.

### 37. Replace `BCE + 0.25 * SmoothL1` with WDL-Space Power Loss (`|pred_wdl - target_wdl|^2.6`) & Lower Cosine LR Floor
- **Location:** [`loss.py`](../training/loss.py#L14-L44), [`train.py`](../training/train.py#L348-L357)
- **Improvement:** The `0.25 * SmoothL1` centipawn term overweights already-decided `Â±6..Â±15` pawn positions at the expense of the decisive `[-2.5, +2.5]` pawn band, and `eta_min = 0.05 * lr` stops LR decay too high. Switch to Stockfish/Bullet-style WDL power loss `mean(|sigmoid(pred / K) - target_prob|^2.6)` (`K ~ 1.60â€“2.00` pawns) and decay LR to `0.005 * lr`.
- **Est. Impact:** **+10 to +25 Elo** after retrain | **Complexity:** Low.

### 38. Add 50-Move Rule (`HalfmoveClock`) Drawishness Scaling in `MultiTierNnueEvaluator.Evaluate`
- **Location:** [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L512-L587)
- **Improvement:** Apply linear 50-move damping to NNUE leaf scores: `scoreCp = (scoreCp * (200 - board.HalfmoveClock)) / 200` (SPSA-tunable divisor `180..240`), preventing fortress shuffling and avoidable 50-move draws in closed/endgame positions. *(Works immediately with zero retraining.)*
- **Est. Impact:** **+5 to +15 Elo** | **Complexity:** Trivial | **Test:** Tier 1 -> Tier 2 SPRT.

### 39. Fast-Path Default `"EQIA"` Routing in `PawnTopology.ClassifyExpert` & Smooth `Endgame <-> Middlegame` Transition
- **Location:** [`PawnTopology.cs`](../src/Anak.Core/Topology/PawnTopology.cs#L248-L283), [`ExpertRouting.cs`](../src/Anak.Core/Topology/ExpertRouting.cs#L33-L38), [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L487-L510)
- **Improvement:** (a) Add a branch-direct inlined fast path when `routing.IsDefaultOrder` instead of looping `foreach (char rule in routing.Rules)` on every leaf eval, and fuse dual-head `pAct` loading in `EvaluateMoEBlended`. (b) Test smooth piece-count blending across `15..18` pieces between `Endgame` and middlegame heads to eliminate the discrete `16 <-> 17` piece blunder cliff.
- **Est. Impact:** **+2% to +4% NPS** + **+5 to +15 Elo** (transition smoothing) | **Complexity:** Low-Medium.

---

## 6. NNUE SIMD & Evaluation NPS (`#40â€“#43`)

### 40. Sparse `NNZ` (Non-Zero `uint` Chunk) Transposed L1 Matmul in `ComputeHiddenAndOutputLayers`
- **Location:** [`NnueInference.cs`](../src/Anak.Data/Nnue/NnueInference.cs#L166-L299), [`NnueWeights.cs`](../src/Anak.Data/Nnue/NnueWeights.cs#L233-L244)
- **Improvement:** Post-SCReLU `pAct` (`byte[1024]`) is 55â€“70% zeros, yet `ComputeHiddenAndOutputLayers` executes dense dot products over all 1024 bytes. Transpose `hiddenWeights` at load time to `(inputDim / 4, l1Dim, 4)` (no `.nnue` format change), collect non-zero 4-byte `uint` chunk indices via SIMD `MoveMask`, and accumulate only non-zero chunks across all 64 output accumulators.
- **Est. Impact:** **+12% to +25% NPS** (100% bit-exact) | **Complexity:** Medium | **Test:** `NnueInferenceTests` + `bench 12`.

### 41. Fix SIMD Dispatch Precedence (`AvxVnni` Shadows `Avx512BW`) & Add Missing `Avx512BW` / `Vector128` (ARM64 NEON) Paths
- **Location:** [`NnueInference.cs`](../src/Anak.Data/Nnue/NnueInference.cs#L131-L246), [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L206-L290), [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L430-L458)
- **Improvement:** `AvxVnni.IsSupported` (256-bit) is checked *before* `Avx512BW.IsSupported` (512-bit), downgrading Zen 4/5 and Sapphire Rapids CPUs; `ActivateFeatures`, `SubAddFeature`, `SubSubAddFeature`, `SubSubAddAddFeature`, and `CopyAccumulator` lack `Avx512BW` and `Vector128` (ARM64 NEON falls back to scalar). Reorder dispatch: 512-bit VNNI -> `Avx512BW` -> `AvxVnni` -> `Avx2` -> `Vector128` -> scalar.
- **Est. Impact:** **+8% to +15% NPS** on AVX-512; **3Ã—â€“5Ã— NPS** on ARM64 (100% bit-exact) | **Complexity:** Low-Medium.

### 42. Replace Hardcoded Managed `short[256 * 512]` Stacks with 64-Byte Aligned Native Accumulator Buffers
- **Location:** [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L33-L35), [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L361-L615)
- **Improvement:** Replace hardcoded managed `short[256 * 512]` arrays (which overflow heap memory if `HiddenDim > 512`, pin two arrays on every `PushMove`/`Evaluate`, and lack 64-byte alignment) with `NativeMemory.AlignedAlloc((nuint)(256 * maxHiddenDim * sizeof(short)), 64)` freed in `Dispose`.
- **Est. Impact:** **+2% to +5% NPS** + removes latent buffer overflow (100% bit-exact) | **Complexity:** Low.

### 43. Per-Worker Finny Table (King-Bucket Accumulator Cache) + Zero-Copy `PushNullMove` via Stack Slot Indirection
- **Location:** [`NnueAccumulator.cs`](../src/Anak.Data/Nnue/NnueAccumulator.cs#L105-L150), [`MultiTierNnueEvaluator.cs`](../src/Anak.Data/Nnue/MultiTierNnueEvaluator.cs#L404-L469)
- **Improvement:** (a) Maintain a `[2][16]` per-worker Finny Table (~32 KB) caching the last `short[HiddenDim]` accumulator + `ulong[12]` piece bitboards per king bucket so `RefreshWhite`/`RefreshBlack` only apply the 2â€“6 changed pieces via bitboard XOR (huge win once `#29` adds d/e-file mirror refreshes). (b) Use an `int[256] _stackSlot` indirection table so `PushNullMove` shares the parent slot instead of copying 2 KB.
- **Est. Impact:** **+5% to +10% NPS** (100% bit-exact) | **Complexity:** Medium.

---

## 7. Core Board, MoveGen, SEE & Search NPS (`#44â€“#50`)

### 44. Wire Dead `MoveGenerator.GenerateLegalQuiets` into `MovePicker`
- **Location:** [`MoveGenerator.cs`](../src/Anak.Core/MoveGen/MoveGenerator.cs#L412), [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L216-L229), [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L380-L414)
- **Improvement:** `GenerateLegalQuiets` currently has **zero callers**; `MovePicker.EnsureAllMovesGenerated()` wipes `_legalMoves`, regenerates all captures from scratch, and linearly scans `_playedGoodCaptures`. Append quiets via `GenerateLegalQuiets` when `_capturesGenerated == true` (and share a lightweight `MoveGenContext` for `kingSq`/`pinnedPieces`/`unsafeForKing`).
- **Est. Impact:** **+4% to +8% NPS** (100% node-equivalent) | **Complexity:** Low-Medium.

### 45. Pre-Filter Enemy Sliders by King Ray Masks Before Magic Lookups (+ Ray Guards & `blsi` in `SEE`)
- **Location:** [`MoveGenerator.cs`](../src/Anak.Core/MoveGen/MoveGenerator.cs#L41-L105), [`Board.cs`](../src/Anak.Core/Board/Board.cs#L133-L138), [`Bitboard.cs`](../src/Anak.Core/Bitboards/Bitboard.cs#L30-L45), [`SEE.cs`](../src/Anak.Core/Evaluation/SEE.cs#L96-L293)
- **Improvement:** Add precomputed `BishopRays[64]` and `RookRays[64]` in `Bitboard.cs` and guard checker/pinner/attack magic lookups with `(enemyBq & GetBishopRays(sq)) != 0` and `(enemyRq & GetRookRays(sq)) != 0` (skipping magic lookups on ~70â€“80% of nodes). In `SEE.GetLeastValuableAttacker`, return `ulong lsbBit = subset & (0UL - subset)` (`blsi`) directly instead of `tzcnt` + `1UL << sq`.
- **Est. Impact:** **+3% to +6% search NPS** (100% node-equivalent) | **Complexity:** Low.

### 46. Bitboard Bulk Pawn Move Generation (Eliminate Per-Pawn Loop & Branches)
- **Location:** [`MoveGenerator.cs`](../src/Anak.Core/MoveGen/MoveGenerator.cs#L219-L382), [`MoveGenerator.cs`](../src/Anak.Core/MoveGen/MoveGenerator.cs#L605-L912)
- **Improvement:** Split pawns into `unpinnedNot7`, `pawnsOn7`, and `pinnedNot7`. Compute all single pushes, double pushes, and left/right captures for `unpinnedNot7` in parallel via 3â€“4 bitboard shifts, then `PopLsb` only actual legal destinations (exits in ~4 cycles when pawns have no captures instead of looping over all 8 pawns).
- **Est. Impact:** **+8% to +15% movegen NPS** (+2% to +4% search NPS) | **Complexity:** Medium | **Test:** Perft + Tier 1/2 check.

### 47. `Board.GivesCheck` Fast-Path + Flip Short-Circuit Order in Quiet SEE Pruning
- **Location:** [`Board.cs`](../src/Anak.Core/Board/Board.cs#L159-L230), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1436-L1442)
- **Improvement:** (a) In `AlphaBetaEngine.cs:1438`, flip `!GivesCheckCached(...) && !HasPositiveOrEqualSee(...)` to `!HasPositiveOrEqualSee(...) && !GivesCheckCached(...)` so `GivesCheck` isn't computed on quiet moves that pass SEE. (b) In `Board.GivesCheck`, use `BishopRays`/`RookRays` + `Between` (zero magic lookups) for direct slider checks, and skip discovered-check magic lookups unless `Bitboard.Line[k, (int)from] != 0`.
- **Est. Impact:** **+5% to +12% search NPS** (100% node-equivalent) | **Complexity:** Low.

### 48. `MagicBitboards`: 16-Byte `PextEntry` with Direct Pinned Table Pointer & 32-Byte Aligned `MagicEntry`
- **Location:** [`MagicBitboards.cs`](../src/Anak.Core/Bitboards/MagicBitboards.cs#L11-L22), [`MagicBitboards.cs`](../src/Anak.Core/Bitboards/MagicBitboards.cs#L137-L174)
- **Improvement:** Pin `_bishopTable`/`_rookTable` (`GC.AllocateArray(..., pinned: true)` or `NativeMemory.AlignedAlloc`) and pack BMI2 mask + direct `ulong*` pointer into a single 16-byte `PextEntry` (1 aligned load + `pext` + pointer index with `(nuint)(byte)square`), and align non-BMI2 `MagicEntry` to 32 bytes (avoiding 24-byte cacheline splits).
- **Est. Impact:** **+2% to +4% search NPS** (100% node-equivalent) | **Complexity:** Low-Medium.

### 49. `Board.MakeMove`, `MoveList`, Castling & Hot Table Micro-Optimizations
- **Location:** [`Board.cs`](../src/Anak.Core/Board/Board.cs#L20-L394), [`Board.cs`](../src/Anak.Core/Board/Board.cs#L673-L721), [`MoveList.cs`](../src/Anak.Core/Moves/MoveList.cs#L25-L37), [`Bitboard.cs`](../src/Anak.Core/Bitboards/Bitboard.cs#L46-L49), [`Zobrist.cs`](../src/Anak.Core/Zobrist/Zobrist.cs#L10-L11), [`Piece.cs`](../src/Anak.Core/Pieces/Piece.cs#L23-L30)
- **Improvement:** Guard castling Zobrist updates with `if (Castling != CastlingRights.None)` + `if (newCastling != Castling)`; use `Unsafe.Add` on `_mailbox`, `MoveList._buffer`, `Bitboard` attack tables (unified `PawnAttacks[128]`), and `Zobrist` tables; add `[SkipLocalsInit]` to `Board`; reuse `unsafeForKing` for castling in `MoveGenerator` and check rights/occupancy before `IsInCheck()` in `Board.IsLegal`; branchless capture flag encoding in `MoveGenerator`; add `[AggressiveInlining]` to `Piece`/`Square`/`Color` extensions.
- **Est. Impact:** **+5% to +10% search NPS** (100% node-equivalent) | **Complexity:** Low.

### 50. `MovePicker`, `QuiescenceSearch` & History Table NPS Overhaul
- **Location:** [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L72-L204), [`MovePicker.cs`](../src/Anak.Search/Minimax/MovePicker.cs#L502-L614), [`QuiescenceSearch.cs`](../src/Anak.Search/Minimax/QuiescenceSearch.cs#L57-L273), [`AlphaBetaEngine.cs`](../src/Anak.Search/Minimax/AlphaBetaEngine.cs#L780-L808), [`HistoryTable.cs`](../src/Anak.Search/Minimax/History/HistoryTable.cs#L17)
- **Improvement:** (a) Pass `inCheck` into `MovePicker` (eliminating redundant `_board.IsInCheck()`). (b) Cache `ContinuationHistory` sum in `PickBestContinuationMove` so `ScoreQuiets` doesn't re-probe 2 MB tables. (c) Use swap-with-last compaction in `MovePicker` selection sorts and shrink `_scores` to `short` to cut the 2.8 KB stack frame. (d) Replace hardcoded `& 63` clock poll in `QuiescenceSearch` with `TimeManager.CheckMask` and propagate `knownInCheck: 1` on QS quiet checks. (e) Remove per-depth `Dictionary`/LINQ `TopMoves` allocations for single-PV UCI. (f) Shrink `HistoryTable` & `CaptureHistory` from `int[]` to `short[]` (96 KB -> 48 KB).
- **Est. Impact:** **+8% to +15% search NPS** + zero per-depth GC | **Complexity:** Low-Medium.

