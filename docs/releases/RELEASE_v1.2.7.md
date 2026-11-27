# Anak v1.2.7 Release Notes

**Release Date:** 2026-11-27  
**Category:** Search Correctness / Endgame (Patch Release)  
**Estimated Rating:** **3270 ± 5 Elo** (1T)

---

## Summary

`v1.2.7` fixes Graph History Interaction (GHI) false TT cutoffs on 2nd-occurrence positions that already appeared once in prior game history.

### Key Changes

1. **Single-Pass Prior Occurrence Detection (`src/Anak.Core/Board/Board.cs`)**
   - Added `Board.IsRepetition(int searchPly, out bool hasPriorOccurrence)` which detects whether the current position appeared once before the search root (`count == 1`), signaling a 2nd occurrence where one more repetition completes a 3-fold draw.

2. **GHI TT-Cutoff Guard (`AlphaBetaEngine.cs`, `QuiescenceSearch.cs`)**
   - Disables direct TT score cutoffs when `hasPriorOccurrence` is true in both `AlphaBeta` (non-PV nodes) and `QuiescenceSearch`, while preserving `ttMove` for move ordering.

---

## Validation

- **Unit Tests:** `dotnet test -c Release` — **419 / 419 passed** (0 failed, 0 skipped), including `Board_IsRepetition_FlagsPriorGameHistoryOccurrenceForGhiGuard`.
- **Tier 1 Sanity (`v1.2.7` vs `v1.2.6`, 250 games at `1+0.01`):** `124.5 / 250` (`49.80%`, `83W / 84L / 83D`).
- **Tier 2 SPRT (`v1.2.7` vs `v1.2.6`, 8,000 games at `1+0.01`):** `4037.0 / 8000` (`50.46%`, `2605W / 2531L / 2864D`), **`+3.21 ± 5.65` logistic Elo** (`+4.33 ± 7.61` fastchess nElo, **`86.74%` LOS**, `Ptnml(0-2): [322, 859, 1586, 889, 344]`).
