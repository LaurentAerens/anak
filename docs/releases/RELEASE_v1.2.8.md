# Anak v1.2.8 Release Notes

**Release Date:** 2026-11-27  
**Category:** Search / Static Eval Correction (Patch Release)  
**Estimated Rating:** **3271 ± 5 Elo** (1T)

---

## Summary

`v1.2.8` upgrades `QuiescenceSearch` stand-pat evaluation and `SearchRoot` static evaluation to use the full 4-table `CorrectionHistory` (pawn, white/black non-pawn, and continuation) instead of the pawn-only table.

### Key Changes

1. **Full Multi-Table `CorrectionHistory` in `QuiescenceSearch` (`src/Anak.Search/Minimax/QuiescenceSearch.cs`)**
   - Added `prev1Piece` and `prev1To` parameters to `QuiescenceSearch.Search(...)` and propagated `(movedPiece, move.To)` across recursive QS plies.
   - Replaced pawn-only `GetAdjustedEval(board.SideToMove, board.PawnKey, rawEval)` with `GetAdjustedEval(in board, prev1Piece, prev1To, rawEval)`.

2. **Root & Leaf Threading (`src/Anak.Search/Minimax/AlphaBetaEngine.cs`)**
   - Updated `SearchRoot` static evaluation to call `_correctionHistory.GetAdjustedEval(in board, Piece.None, Square.None, rawEval)`.
   - Passed `ply - 1` move context (`MovedPiece`, `CurrentMove.To`) from `AlphaBeta` into `QuiescenceSearch.Search`.

---

## Validation

- **Unit Tests:** `dotnet test -c Release` — **419 / 419 passed** (0 failed, 0 skipped; `baseline_snapshots_64.json` regenerated for the updated QS/Root correction history).
- **Tier 1 Sanity (`v1.2.8` vs `v1.2.7`, 250 games at `1+0.01`):** `131.5 / 250` (`52.60%`, `88W / 75L / 87D`), `+18.08 ± 34.19` logistic Elo (`85.13%` LOS).
- **Tier 2 SPRT (`v1.2.8` vs `v1.2.7`, 8,000 games at `1+0.01`):** `4016.0 / 8000` (`50.20%`, `2588W / 2556L / 2856D`), **`+1.39 ± 5.81` logistic Elo** (`+1.82 ± 7.61` fastchess nElo, `68.03%` LOS, `Ptnml(0-2): [346, 911, 1476, 899, 368]`).
