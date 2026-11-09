# Anak Engine — Search Bugfix Candidates & Architecture Defects

This document tracks identified bugs, state pollution issues, and mathematical convergence defects in Anak's search, evaluation, and move-ordering heuristics.

---

## 📌 Summary of Candidates

| Bug / Ref | Module | Severity | Description | Fix Status |
| :---: | :--- | :---: | :--- | :---: |
| **BUG-01** | `CorrectionHistory` | **High** | Mathematical convergence defect: updates against adjusted static eval decay learned corrections toward zero and halve steady-state table magnitudes. | Implemented (Tier 1 Cleared: 49.2%, 0 crashes) |
| **BUG-02** | `SearchStack` / Killers | **Medium-High** | Killer move bleed: killers from unrelated branches and earlier iterations persist indefinitely at child plies, distorting move ordering in subsequent searches. | Ready for Testing |
| **BUG-03** | `RunMicroBlunderWatchdog` | **Critical** | Root move hijacking & false refutations: 1-ply SEE on opponent reply flags sound queen trades, piece sacrifices, and recaptures as blunders, overriding deep search with blunders. | Disabled in v1.0.20 (+52.5 Elo at MTC); Redesign Plan Defined |

---

## 🔬 Candidate Bug Details

### BUG-01: Correction History Target Calculation (Mathematical Convergence Defect)

- **Affected Files:**
  - [`src/Anak.Search/Minimax/AlphaBetaEngine.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1291-L1293)
  - [`src/Anak.Search/Minimax/History/CorrectionHistory.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/History/CorrectionHistory.cs#L109-L118)

#### Mechanism of the Defect
1. In `AlphaBetaEngine.cs`:
   ```csharp
   int rawEval = _evaluator.Evaluate(in board, ReadOnlySpan<Move>.Empty).Raw;
   staticEval = _correctionHistory.GetAdjustedEval(in board, rawEval);
   ```
   Here, `staticEval` is explicitly:
   $$\text{staticEval} = \text{rawEval} + \frac{\text{current}}{\text{ScaleFactor}}$$
2. When a beta cutoff occurs (line 1628) or when the node completes (line 1660), `Update` is called using `staticEval`:
   ```csharp
   _correctionHistory.Update(in board, depth, score, staticEval);
   ```
3. In `CorrectionHistory.cs`:
   ```csharp
   int diff = Math.Clamp(searchScore - staticEval, -MaxCorrection, MaxCorrection);
   int target = diff * ScaleFactor;
   int weight = Math.Clamp(depth * 32, 16, 512);

   int index = GetIndex(side, pawnKey);
   int current = _table[index];
   _table[index] = (short)(current + (target - current) * weight / WeightDivisor);
   ```

#### Mathematical Flaw & Consequences
- **Decay on Perfect Prediction:**
  Suppose the engine's adjusted static evaluation predicted the search score perfectly:
  $$\text{searchScore} == \text{staticEval}$$
  Then:
  $$\text{diff} = 0 \implies \text{target} = 0$$
  The update executes:
  $$\text{current} \leftarrow \text{current} + (0 - \text{current}) \times \frac{\text{weight}}{\text{WeightDivisor}}$$
  **Every time the position evaluation matches the search score, the correction is penalized and dragged back toward zero.**
- **Steady-State Halving:**
  At the convergence fixed point where $\text{target} == \text{current}$:
  $$(\text{searchScore} - \text{staticEval}) \times \text{ScaleFactor} = \text{current}$$
  Substituting $\text{staticEval} = \text{rawEval} + \frac{\text{current}}{\text{ScaleFactor}}$:
  $$\left(\text{searchScore} - \text{rawEval} - \frac{\text{current}}{\text{ScaleFactor}}\right) \times \text{ScaleFactor} = \text{current}$$
  $$(\text{searchScore} - \text{rawEval}) \times \text{ScaleFactor} - \text{current} = \text{current}$$
  $$2 \times \text{current} = (\text{searchScore} - \text{rawEval}) \times \text{ScaleFactor}$$
  $$\frac{\text{current}}{\text{ScaleFactor}} = \frac{1}{2}(\text{searchScore} - \text{rawEval})$$
  The learned correction is **artificially capped at half the actual NNUE prediction error**.

#### Proposed Fix
Pass `rawEval` (the unadjusted NNUE evaluation) to `_correctionHistory.Update(in board, depth, score, rawEval)`:
$$\text{diff} = \text{searchScore} - \text{rawEval}$$
$$\text{target} = \text{diff} \times \text{ScaleFactor}$$
When the learned correction matches the error ($\frac{\text{current}}{\text{ScaleFactor}} = \text{searchScore} - \text{rawEval}$), $\text{target} == \text{current}$, producing a stable, non-decaying fixed point that tracks the true neural network bias for each pawn structure.

---

### BUG-02: SearchStack Killer Move Bleed across Subtrees and Iterations

- **Affected Files:**
  - [`src/Anak.Search/Minimax/AlphaBetaEngine.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/AlphaBetaEngine.cs#L1160-L1215)
  - [`src/Anak.Search/Minimax/SearchStack.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/SearchStack.cs#L55-L66)
  - [`src/Anak.Search/Minimax/MovePicker.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/MovePicker.cs#L249-L278)

#### Mechanism of the Defect
1. In `AlphaBetaEngine.cs`, `_stack[i].Reset()` is called only inside `ResetForSearch()` at the very beginning of the entire search (before iterative deepening depth 1).
2. During iterative deepening (e.g. depth 3), quiet moves causing beta cutoffs at ply $N$ are stored in `_stack[N].Killer0` and `Killer1`.
3. In subsequent iterations (depth 4, 5, 6...) or when the search backtracks to the root and descends into a completely different branch of the game tree, **`_stack[ply].Killer0` and `Killer1` are never cleared for child plies**.
4. When a new, completely unrelated branch reaches ply $N$, [`MovePicker`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/MovePicker.cs#L249-L278) initializes with the stale killer moves from the old branch:
   ```csharp
   var picker = new MovePicker(
       in board,
       ttMove,
       _stack[ply].Killer0,
       _stack[ply].Killer1,
       ...);
   ```
5. If the stale killer move happens to be pseudo-legal in the new position (e.g. a general quiet developing move like `Nf3`, `e4`, `c4`, or `O-O`), `MovePicker` plays it in **Stage 2 (Killer0)**, prioritizing it ahead of:
   - Countermoves
   - Positive continuation history moves
   - High-ranking butterfly history quiet moves

#### Proposed Fix
Follow the standard chess engine convention (as in Stockfish): when entering `AlphaBeta` or descending a ply, clear killer moves for `ply + 1` and `ply + 2`:
```csharp
if (ply + 1 < MaxStackSize)
{
    _stack[ply + 1].Killer0 = Move.Null;
    _stack[ply + 1].Killer1 = Move.Null;
}
if (ply + 2 < MaxStackSize)
{
    _stack[ply + 2].Killer0 = Move.Null;
    _stack[ply + 2].Killer1 = Move.Null;
}
```
This guarantees that killer moves probed at ply $N$ were produced either by immediate sibling moves or within the active local subtree.

---

### BUG-03: Micro Blunder Watchdog Root Move Hijacking & False-Blunder Refutations

- **Affected Files:**
  - [`src/Anak.Search/Minimax/AlphaBetaEngine.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Search/Minimax/AlphaBetaEngine.cs#L779-L952)
  - [`src/Anak.Protocol/Uci/UciOption.cs`](file:///c:/Users/laurent%20aerens/source/repos/anak/src/Anak.Protocol/Uci/UciOption.cs#L329)

#### Mechanism of the Defect
At the end of `RunMainSearch`, when the engine has completed a 20+ ply iterative deepening search, `RunMicroBlunderWatchdog` was called to verify the candidate root move:
```csharp
if (multiTier is { HasMicroModel: true } && !bestMove.IsNull && !cancellationToken.IsCancellationRequested)
{
    bestMove = RunMicroBlunderWatchdog(in rootBoard, bestMove, bestScore, completedDepth, expectedDepth, multiTier, cancellationToken);
}
```
Inside the watchdog, the candidate move was made (`boardAfter.MakeMove(candidateMove)`), and opponent responses were checked:
```csharp
if (oppMove.IsCapture || oppMove.IsPromotion)
{
    int see = StaticExchangeEvaluator.Evaluate(in boardAfter, oppMove);
    if (see >= 200)
    {
        isRefuted = true;
        break;
    }
}
```

#### Why It Failed
1. **Exchange Blindness (Queen Trades):** If the candidate move was a Queen trade (e.g. `23. Qxc7`), White captured Black's queen on c7. From `boardAfter`, Black played `23... Bxc7` (Bishop captures Queen). Since White has no defender on c7, `SEE(Bxc7) = +900 >= 200`. The watchdog did not credit the +900 White had just captured on the same square, declaring the trade a catastrophic blunder!
2. **Piece Sacrifices (Reynolds Meran `12. Nxb5`):** In theoretical piece sacrifices, opponent can capture the sacrificed knight with a pawn (`12... axb5`, `SEE = +200`). The watchdog flagged the 21-ply GM line as refuted and substituted `12. Nxd4??`, hanging central pawns and losing on the spot.
3. **Piece Recaptures (`29. Nxe4`):** When opponent pawn takes back (`29... fxe4`, `SEE = +200`), the watchdog again aborted the main line and played an unsound alternative (`29. Nc4??`).
4. **Time Gate Discrepancy:** In bullet (`1+0.01`), average move time is $\sim 20\text{ ms} < 25\text{ ms}$ threshold, so the watchdog was 100% dormant in bullet test runs. At moderate time control (`5+0.05` or `300+2`), the watchdog ran on nearly every move, sabotaging tournament play.

#### Status & Benchmark
- **v1.0.20 Action:** Disabled by default (`BlunderWatchdog: false`) and properly wired to UCI option check.
- **MTC 100-Game Benchmark (`5+0.05`):** **+52.51 Elo** (98.6% LOS, 39 wins vs 24 losses against v1.0.19 baseline).

#### Roadmap for a Working Blunder Watchdog
To revive the blunder watchdog effectively without false-blunder hijacking:
1. **Net Material Accounting:** Account for material captured by `candidateMove` before comparing against opponent reply SEE ($\Delta \text{Material} = \text{OurGain} - \text{OpponentGain}$).
2. **Mini 2-Ply Quiescence Search:** Instead of raw 1-ply SEE + single-ply eval, run a fast mini-QS on the top 2 opponent replies.
3. **Confidence Gating:** Never override a root move if search depth $\ge 14$ and score confidence is high; only intervene if the position was considered sharp or volatile and search depth was truncated early due to time panic.

---

## 📋 Evaluation Plan (3-Tier Testing Pyramid)

1. **Automated Unit Tests:**
   Validate 100% test pass rate (`dotnet test -c Release`).
2. **Tier 1: Quick Sanity Gate (250 Games):**
   ```bash
   python tools/fastchess/run_elo_bench.py --baseline v1.0.16 --games 250 --tc 1+0.01 --concurrency 16
   ```
   - Auto-revert if Score $< 47.0\%$ ($< -20\text{ Elo}$).
   - Graduate to Tier 2 if Score $\ge 50.0\%$.
3. **Tier 2: Validation via SPRT:**
   ```bash
   python tools/fastchess/run_elo_bench.py --baseline v1.0.16 --sprt 0.0 3.0 --max-games 8000 --tc 1+0.01 --concurrency 16
   ```
