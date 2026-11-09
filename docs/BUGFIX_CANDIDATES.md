# Anak Engine — Search Bugfix Candidates & Architecture Defects

This document tracks identified bugs, state pollution issues, and mathematical convergence defects in Anak's search, evaluation, and move-ordering heuristics.

---

## 📌 Summary of Candidates

| Bug / Ref | Module | Severity | Description | Fix Status |
| :---: | :--- | :---: | :--- | :---: |
| **BUG-01** | `CorrectionHistory` | **High** | Mathematical convergence defect: updates against adjusted static eval decay learned corrections toward zero and halve steady-state table magnitudes. | Implemented (Tier 1 Cleared: 49.2%, 0 crashes) |
| **BUG-02** | `SearchStack` / Killers | **Medium-High** | Killer move bleed: killers from unrelated branches and earlier iterations persist indefinitely at child plies, distorting move ordering in subsequent searches. | Ready for Testing |

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
