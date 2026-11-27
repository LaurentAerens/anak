# Anak v1.2.6 Release Notes

**Release Date:** 2026-11-27  
**Category:** Time Management / UCI Compliance (Patch Release)  
**Estimated Rating:** **3267 ± 5 Elo** (1T)

---

## Summary

`v1.2.6` fixes an unintended soft-limit contraction in bullet time controls (`remainingTime < 2000 ms`) and exposes the standard UCI `Move Overhead` option.

### Key Changes

1. **Bullet Soft-Limit Contraction Fix (`src/Anak.Search/Time/TimeManager.cs`)**
   - Previously, when `remainingTime < 2000`, `TimeManager.Init` used `hardMultiplier = 1.5f` and then clamped `_softLimitMs` against `_hardLimitMs * (TimeSoftHardRatio / 100.0f)` (`60%`), contracting `_softLimitMs` to `0.90 × allocated` before stability scaling.
   - `TimeManager.Init` now uses `0.80f` headroom capping for `remainingTime < 2000`, preserving full `allocated` soft limits while still guaranteeing 20% headroom below `_hardLimitMs`.

2. **Configurable `Move Overhead` UCI Option (`UciOption.cs`, `UciEngineAdapter.cs`, `AlphaBetaEngine.cs`, `TimeManager.cs`)**
   - Added `Move Overhead` (`spin`, default `40`, min `0`, max `5000` ms) wired through `AlphaBetaEngine.MoveOverheadMs` to `TimeManager.MoveOverheadMs`, used in both `movetime` and clock safety-lag calculations.

---

## Validation

- **Unit Tests:** `dotnet test -c Release` — **418 / 418 passed** (0 failed, 0 skipped).
- **Tier 1 Sanity (`v1.2.6` vs `v1.2.5`, 250 games at `1+0.01`):** `126.5 / 250` (`50.60%`, `78W / 75L / 97D`), `+4.17 ± 30.77` logistic Elo (`60.50%` LOS).
- **Tier 2 SPRT (`v1.2.6` vs `v1.2.5`, 8,000 games at `1+0.01`):** `4025.0 / 8000` (`50.31%`, `2603W / 2553L / 2844D`), **`+2.17 ± 5.62` logistic Elo** (`+2.94 ± 7.61` fastchess nElo, `77.57%` LOS, `Ptnml(0-2): [305, 884, 1620, 838, 353]`).
